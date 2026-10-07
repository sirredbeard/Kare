using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.ClientModel;
using System.ClientModel.Primitives;
using System.Text.Json;
using Kare.Abstractions;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;
using OpenAI;

namespace Kare.Inference.GenieX;

/// <summary>
/// GenieX QAIRT backend through its loopback OpenAI-compatible server.
/// </summary>
public sealed class GenieXBackend : ILocalInferenceBackend
{
    /// <summary>Chat option key for disabling model reasoning on a bounded request.</summary>
    public const string DisableThinkingOptionName = "kare.geniex.disable-thinking";

    private readonly GenieXOptions _options;
    private readonly HttpClient _probeClient;
    private readonly Lazy<IChatClient> _client;

    /// <summary>Creates the backend without contacting GenieX until it is probed or selected.</summary>
    public GenieXBackend(
        IOptions<GenieXOptions> options,
        IHttpClientFactory httpClientFactory)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(httpClientFactory);

        _options = options.Value;
        _probeClient = httpClientFactory.CreateClient(nameof(GenieXBackend));
        _client = new Lazy<IChatClient>(CreateClient, LazyThreadSafetyMode.ExecutionAndPublication);
    }

    /// <inheritdoc />
    public BackendKind Kind => BackendKind.GenieXQairt;

    /// <inheritdoc />
    public int Priority => _options.Priority;

    /// <inheritdoc />
    public async ValueTask<BackendProbeResult> ProbeAsync(CancellationToken cancellationToken = default)
    {
        if (!_options.Enabled)
        {
            return BackendProbeResult.Unavailable("GenieX is disabled.");
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(_options.ProbeTimeoutSeconds));

        try
        {
            var response = await _probeClient
                .GetFromJsonAsync(
                    new Uri($"{_options.Endpoint.ToString().TrimEnd('/')}/models"),
                    GenieXJsonContext.Default.GenieXModelList,
                    timeout.Token)
                .ConfigureAwait(false);

            var found = response?.Data.Any(model =>
                string.Equals(
                    RemovePrecision(model.Id),
                    _options.ModelId,
                    StringComparison.Ordinal)) == true;

            if (!found)
            {
                return BackendProbeResult.Unavailable(
                    $"GenieX responded, but model {_options.ModelId} is not loaded.");
            }

            using var readinessResponse = await _probeClient.PostAsJsonAsync(
                new Uri($"{_options.Endpoint.ToString().TrimEnd('/')}/chat/completions"),
                new GenieXReadinessRequest
                {
                    Model = _options.ModelId,
                    Messages =
                    [
                        new GenieXReadinessMessage
                        {
                            Content = $"Reply OK. Health probe {Guid.NewGuid():N}.",
                        },
                    ],
                    Temperature = 0,
                    Stream = false,
                },
                GenieXJsonContext.Default.GenieXReadinessRequest,
                timeout.Token).ConfigureAwait(false);
            readinessResponse.EnsureSuccessStatusCode();

            return BackendProbeResult.Available(
                $"GenieX model {_options.ModelId} completed a readiness inference at {_options.Endpoint}.");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return BackendProbeResult.Unavailable(
                $"GenieX probe timed out after {_options.ProbeTimeoutSeconds} seconds.");
        }
        catch (Exception ex) when (ex is HttpRequestException or ClientResultException)
        {
            return BackendProbeResult.Unavailable($"GenieX probe failed: {ex.Message}");
        }
    }

    /// <inheritdoc />
    public async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            if (options?.AdditionalProperties?.TryGetValue(
                    DisableThinkingOptionName,
                    out var disableThinking) == true &&
                disableThinking is true)
            {
                return await GetNonThinkingResponseAsync(
                    messages,
                    options,
                    cancellationToken).ConfigureAwait(false);
            }

            return await _client.Value
                .GetResponseAsync(messages, NormalizeOptions(options), cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or ClientResultException or JsonException)
        {
            throw BackendUnavailable(ex);
        }
    }

    /// <inheritdoc />
    public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default) =>
        StreamAsync(messages, NormalizeOptions(options), cancellationToken);

    /// <inheritdoc />
    public object? GetService(Type serviceType, object? serviceKey = null)
    {
        ArgumentNullException.ThrowIfNull(serviceType);

        if (serviceKey is null && serviceType.IsInstanceOfType(this))
        {
            return this;
        }

        return serviceType == typeof(ChatClientMetadata)
            ? new ChatClientMetadata("geniex-qairt", _options.Endpoint, _options.ModelId)
            : _client.Value.GetService(serviceType, serviceKey);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _probeClient.Dispose();
        if (_client.IsValueCreated)
        {
            _client.Value.Dispose();
        }
    }

    private IChatClient CreateClient()
    {
        var openAi = new OpenAIClient(
            new System.ClientModel.ApiKeyCredential("local-geniex"),
            new OpenAIClientOptions
            {
                Endpoint = _options.Endpoint,
                Transport = new HttpClientPipelineTransport(_probeClient),
            });

        return openAi.GetChatClient(_options.ModelId).AsIChatClient();
    }

    private ChatOptions NormalizeOptions(ChatOptions? options)
    {
        options ??= new ChatOptions();
        options.ModelId = _options.ModelId;
        options.Temperature ??= 0;
        return options;
    }

    private async Task<ChatResponse> GetNonThinkingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions options,
        CancellationToken cancellationToken)
    {
        var request = new GenieXCompletionRequest
        {
            Model = _options.ModelId,
            MaxTokens = options.MaxOutputTokens ?? 256,
            Temperature = options.Temperature ?? 0,
            Stream = false,
            EnableThink = false,
            Messages =
            [
                .. messages.Select(static message => new GenieXCompletionMessage
                {
                    Role = message.Role.Value,
                    Content = string.Join(
                        "\n",
                        message.Contents
                            .OfType<TextContent>()
                            .Select(static content => content.Text)),
                }),
            ],
        };

        using var httpResponse = await _probeClient.PostAsJsonAsync(
            new Uri($"{_options.Endpoint.ToString().TrimEnd('/')}/chat/completions"),
            request,
            GenieXJsonContext.Default.GenieXCompletionRequest,
            cancellationToken).ConfigureAwait(false);
        httpResponse.EnsureSuccessStatusCode();
        var response = await httpResponse.Content.ReadFromJsonAsync(
            GenieXJsonContext.Default.GenieXCompletionResponse,
            cancellationToken).ConfigureAwait(false);
        var choice = response?.Choices.FirstOrDefault() ??
            throw new HttpRequestException("GenieX returned no completion choice.");

        return new ChatResponse(new ChatMessage(ChatRole.Assistant, choice.Message.Content))
        {
            FinishReason = choice.FinishReason switch
            {
                "length" => ChatFinishReason.Length,
                "tool_calls" => ChatFinishReason.ToolCalls,
                _ => ChatFinishReason.Stop,
            },
            Usage = response.Usage is null
                ? null
                : new UsageDetails
                {
                    InputTokenCount = response.Usage.PromptTokens,
                    OutputTokenCount = response.Usage.CompletionTokens,
                    TotalTokenCount = response.Usage.TotalTokens,
                },
        };
    }

    private async IAsyncEnumerable<ChatResponseUpdate> StreamAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions options,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var stream = _client.Value.GetStreamingResponseAsync(messages, options, cancellationToken);
        await using var enumerator = stream.GetAsyncEnumerator(cancellationToken);
        while (await MoveNextAsync(enumerator).ConfigureAwait(false))
        {
            yield return enumerator.Current;
        }
    }

    private async ValueTask<bool> MoveNextAsync(
        IAsyncEnumerator<ChatResponseUpdate> enumerator)
    {
        try
        {
            return await enumerator.MoveNextAsync().ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or ClientResultException)
        {
            throw BackendUnavailable(ex);
        }
    }

    private LocalInferenceException BackendUnavailable(Exception exception) =>
        new($"GenieX request failed for model {_options.ModelId}: {exception.Message}", exception);

    private static string RemovePrecision(string modelId)
    {
        var separator = modelId.LastIndexOf(':');
        return separator < 0 ? modelId : modelId[..separator];
    }
}
