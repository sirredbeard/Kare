using System.Net.Http.Json;
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

            return found
                ? BackendProbeResult.Available(
                    $"GenieX model {_options.ModelId} is available at {_options.Endpoint}.")
                : BackendProbeResult.Unavailable(
                    $"GenieX responded, but model {_options.ModelId} is not loaded.");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return BackendProbeResult.Unavailable(
                $"GenieX probe timed out after {_options.ProbeTimeoutSeconds} seconds.");
        }
        catch (HttpRequestException ex)
        {
            return BackendProbeResult.Unavailable($"GenieX probe failed: {ex.Message}");
        }
    }

    /// <inheritdoc />
    public Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default) =>
        _client.Value.GetResponseAsync(messages, NormalizeOptions(options), cancellationToken);

    /// <inheritdoc />
    public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default) =>
        _client.Value.GetStreamingResponseAsync(messages, NormalizeOptions(options), cancellationToken);

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
            new OpenAIClientOptions { Endpoint = _options.Endpoint });

        return openAi.GetChatClient(_options.ModelId).AsIChatClient();
    }

    private ChatOptions NormalizeOptions(ChatOptions? options)
    {
        options ??= new ChatOptions();
        options.ModelId = _options.ModelId;
        options.Temperature ??= 0;
        return options;
    }

    private static string RemovePrecision(string modelId)
    {
        var separator = modelId.LastIndexOf(':');
        return separator < 0 ? modelId : modelId[..separator];
    }
}
