using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading.Channels;
using GitHub.Copilot;
using GitHub.Copilot.Rpc;
using Kare.Abstractions;
using Kare.Core;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Kare.Cloud.Copilot;

/// <summary>
/// Bounded cloud escalation through the GitHub Copilot SDK. Caller tool declarations
/// are registered as external tools and returned to the caller without executing them.
/// </summary>
public sealed class CopilotSdkBackend : ICloudInferenceBackend, ICloudModelCatalog, IAsyncDisposable
{
    private readonly CopilotSdkOptions _options;
    private readonly IReadOnlyList<CloudModelDescriptor> _models;
    private readonly ILogger<CopilotSdkBackend> _logger;
    private readonly AzureCliBearerTokenProvider _azureTokens;
    private readonly SemaphoreSlim _startGate = new(1, 1);
    private readonly SemaphoreSlim _modelCatalogGate = new(1, 1);
    private CopilotClient? _client;
    private HashSet<string>? _availableCopilotModels;

    /// <summary>Creates the cloud backend without starting the SDK runtime.</summary>
    public CopilotSdkBackend(
        IOptions<CopilotSdkOptions> options,
        ILogger<CopilotSdkBackend> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);
        _options = options.Value;
        _logger = logger;
        _azureTokens = new AzureCliBearerTokenProvider(
            TimeSpan.FromSeconds(_options.CredentialTimeoutSeconds));
        _models = _options.Models
            .OrderBy(static model => model.Tier)
            .ThenBy(static model => model.Priority)
            .Select(static model => new CloudModelDescriptor(
                model.Id,
                model.ModelId,
                model.Provider,
                model.Tier,
                model.Priority,
                model.SupportsTools,
                model.SupportsImages,
                model.AllowedModes.Aggregate(
                    (RouteMode)0,
                    static (modes, mode) => modes | mode)))
            .ToArray();
    }

    /// <inheritdoc />
    public IReadOnlyList<CloudModelDescriptor> Models => _models;

    /// <inheritdoc />
    public KareRoute Route => _options.Provider == CopilotCloudProvider.MicrosoftFoundry
        ? KareRoute.Foundry
        : KareRoute.CopilotHeavy;

    /// <inheritdoc />
    public string ModelId => _options.ModelId;

    /// <inheritdoc />
    public async ValueTask<BackendProbeResult> ProbeAsync(CancellationToken cancellationToken = default)
    {
        if (!_options.Enabled)
        {
            return BackendProbeResult.Unavailable("Copilot SDK escalation is disabled.");
        }

        var hasGitHubCredential = _options.UseLoggedInUser ||
            !string.IsNullOrWhiteSpace(
                Environment.GetEnvironmentVariable(_options.GitHubTokenEnvironmentVariable));
        var hasFoundryCredential = _options.Models.Any(model =>
            model.Provider == CloudModelProvider.MicrosoftFoundry &&
            (model.Authentication == CloudAuthentication.AzureCli ||
             (model.Authentication == CloudAuthentication.EnvironmentApiKey &&
              !string.IsNullOrWhiteSpace(model.ApiKeyEnvironmentVariable) &&
              !string.IsNullOrWhiteSpace(
                  Environment.GetEnvironmentVariable(model.ApiKeyEnvironmentVariable))))) ||
            (_options.Provider == CopilotCloudProvider.MicrosoftFoundry &&
             !string.IsNullOrWhiteSpace(
                 Environment.GetEnvironmentVariable(_options.FoundryApiKeyEnvironmentVariable)));

        if (!hasGitHubCredential && !hasFoundryCredential)
        {
            return BackendProbeResult.Unavailable(
                "No configured GitHub Copilot or Microsoft Foundry credential is available.");
        }

        try
        {
            CopilotClient client;
            try
            {
                client = await GetClientAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is InvalidOperationException or IOException)
            {
                throw new CloudInferenceException($"Copilot SDK startup failed: {ex.Message}");
            }
            return BackendProbeResult.Available(
                $"Copilot SDK routing is configured with {_models.Count} provider-neutral model route(s).");
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException)
        {
            return BackendProbeResult.Unavailable($"Copilot SDK startup failed: {ex.Message}");
        }
    }

    /// <inheritdoc />
    public async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var materialized = messages as IReadOnlyList<ChatMessage> ?? messages.ToArray();
        var route = ResolveRoute(options);
        ThrowIfImagesUnsupported(materialized, route);
        ThrowIfCredentialMissing(route);
        var client = await GetClientAsync(cancellationToken).ConfigureAwait(false);
        await EnsureRouteAvailableAsync(client, route, cancellationToken).ConfigureAwait(false);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(_options.TimeoutSeconds));

        var tools = options?.ToolMode == ChatToolMode.None
            ? []
            : options?.Tools?
                .OfType<AIFunctionDeclaration>()
                .ToArray() ?? [];
        var toolNames = tools
            .Select(static tool => tool.Name)
            .ToHashSet(StringComparer.Ordinal);
        var completion = new TaskCompletionSource<ChatResponse>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        string? lastMessage = null;
        long? inputTokens = null;
        long? outputTokens = null;

        var config = CreateSessionConfig(tools, route);
        CopilotSession session;
        try
        {
            session = await client
                .CreateSessionAsync(config, timeout.Token)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException)
        {
            throw new CloudInferenceException($"Copilot SDK session creation failed: {ex.Message}");
        }

        await using var sessionScope = session;

        using var subscription = session.On<SessionEvent>(evt =>
        {
            _logger.LogTrace(
                "Copilot SDK session event {EventClass} ({EventType}) for agent {AgentId}.",
                evt.GetType().Name,
                evt.Type,
                evt.AgentId);

            switch (evt)
            {
                case AssistantMessageEvent message when message.AgentId is null:
                    lastMessage = message.Data.Content;
                    break;

                case AssistantUsageEvent usage when usage.AgentId is null:
                    inputTokens = Add(inputTokens, usage.Data.InputTokens);
                    outputTokens = Add(outputTokens, usage.Data.OutputTokens);
                    break;

                case ExternalToolRequestedEvent tool:
                    completion.TrySetResult(CreateToolResponse(tool, inputTokens, outputTokens));
                    break;

                case ToolExecutionStartEvent tool when toolNames.Contains(tool.Data.ToolName):
                    completion.TrySetResult(CreateToolResponse(
                        tool.Data.ToolName,
                        tool.Data.ToolCallId,
                        tool.Data.Arguments,
                        inputTokens,
                        outputTokens));
                    break;

                case SessionErrorEvent error:
                    completion.TrySetException(new CloudInferenceException(
                        $"Copilot SDK {error.Data.ErrorType}: {error.Data.Message}"));
                    break;

                case SessionIdleEvent:
                    completion.TrySetResult(CreateTextResponse(lastMessage, inputTokens, outputTokens));
                    break;
            }
        });

        try
        {
            await session.SendAsync(
                CreateMessageOptions(materialized, options),
                timeout.Token).ConfigureAwait(false);
            return await completion.Task.WaitAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new CloudInferenceException(
                $"Copilot SDK request exceeded the configured {_options.TimeoutSeconds}-second timeout.");
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException)
        {
            throw new CloudInferenceException($"Copilot SDK request failed: {ex.Message}");
        }
        finally
        {
            try
            {
                await client.DeleteSessionAsync(session.SessionId, CancellationToken.None)
                    .ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is InvalidOperationException or IOException)
            {
                _logger.LogWarning(ex, "Failed to delete completed Copilot SDK session {SessionId}.", session.SessionId);
            }
        }
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var materialized = messages as IReadOnlyList<ChatMessage> ?? messages.ToArray();
        var route = ResolveRoute(options);
        ThrowIfImagesUnsupported(materialized, route);
        ThrowIfCredentialMissing(route);
        var client = await GetClientAsync(cancellationToken).ConfigureAwait(false);
        await EnsureRouteAvailableAsync(client, route, cancellationToken).ConfigureAwait(false);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(_options.TimeoutSeconds));

        var tools = options?.ToolMode == ChatToolMode.None
            ? []
            : options?.Tools?
                .OfType<AIFunctionDeclaration>()
                .ToArray() ?? [];
        var toolNames = tools
            .Select(static tool => tool.Name)
            .ToHashSet(StringComparer.Ordinal);
        var updates = Channel.CreateUnbounded<ChatResponseUpdate>(
            new UnboundedChannelOptions
            {
                SingleReader = true,
                SingleWriter = true,
            });
        long? inputTokens = null;
        long? outputTokens = null;

        CopilotSession session;
        try
        {
            session = await client
                .CreateSessionAsync(CreateSessionConfig(tools, route), timeout.Token)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException)
        {
            throw new CloudInferenceException($"Copilot SDK session creation failed: {ex.Message}");
        }

        await using var sessionScope = session;
        using var subscription = session.On<SessionEvent>(evt =>
        {
            switch (evt)
            {
                case AssistantMessageDeltaEvent delta
                    when delta.AgentId is null &&
                         !string.IsNullOrEmpty(delta.Data.DeltaContent):
                    updates.Writer.TryWrite(new ChatResponseUpdate(
                        ChatRole.Assistant,
                        delta.Data.DeltaContent));
                    break;

                case AssistantUsageEvent usage when usage.AgentId is null:
                    inputTokens = Add(inputTokens, usage.Data.InputTokens);
                    outputTokens = Add(outputTokens, usage.Data.OutputTokens);
                    break;

                case ExternalToolRequestedEvent tool:
                    WriteToolUpdate(
                        updates.Writer,
                        tool.Data.ToolName,
                        tool.Data.ToolCallId,
                        tool.Data.Arguments);
                    break;

                case ToolExecutionStartEvent tool when toolNames.Contains(tool.Data.ToolName):
                    WriteToolUpdate(
                        updates.Writer,
                        tool.Data.ToolName,
                        tool.Data.ToolCallId,
                        tool.Data.Arguments);
                    break;

                case SessionErrorEvent error:
                    updates.Writer.TryComplete(new CloudInferenceException(
                        $"Copilot SDK {error.Data.ErrorType}: {error.Data.Message}"));
                    break;

                case SessionIdleEvent:
                    updates.Writer.TryWrite(new ChatResponseUpdate
                    {
                        Role = ChatRole.Assistant,
                        FinishReason = ChatFinishReason.Stop,
                    });
                    var usageDetails = CreateUsage(inputTokens, outputTokens);
                    if (usageDetails is not null)
                    {
                        var usageUpdate = new ChatResponseUpdate();
                        usageUpdate.Contents.Add(new UsageContent(usageDetails));
                        updates.Writer.TryWrite(usageUpdate);
                    }

                    updates.Writer.TryComplete();
                    break;
            }
        });

        try
        {
            await session.SendAsync(
                CreateMessageOptions(materialized, options),
                timeout.Token).ConfigureAwait(false);

            await foreach (var update in updates.Reader
                .ReadAllAsync(timeout.Token)
                .ConfigureAwait(false))
            {
                yield return update;
            }
        }
        finally
        {
            try
            {
                await client.DeleteSessionAsync(session.SessionId, CancellationToken.None)
                    .ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is InvalidOperationException or IOException)
            {
                _logger.LogWarning(ex, "Failed to delete completed Copilot SDK session {SessionId}.", session.SessionId);
            }
        }
    }

    /// <inheritdoc />
    public object? GetService(Type serviceType, object? serviceKey = null)
    {
        ArgumentNullException.ThrowIfNull(serviceType);

        if (serviceKey is null && serviceType.IsInstanceOfType(this))
        {
            return this;
        }

        return serviceType == typeof(ChatClientMetadata)
            ? new ChatClientMetadata("github-copilot-sdk", defaultModelId: _options.ModelId)
            : null;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        DisposeAsync().AsTask().GetAwaiter().GetResult();
        GC.SuppressFinalize(this);
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_client is not null)
        {
            await _client.DisposeAsync().ConfigureAwait(false);
            _client = null;
        }

        _startGate.Dispose();
        _modelCatalogGate.Dispose();
        GC.SuppressFinalize(this);
    }

    private SessionConfig CreateSessionConfig(
        ICollection<AIFunctionDeclaration> tools,
        ResolvedCloudRoute route)
    {
        var availableTools = new ToolSet();
        foreach (var tool in tools)
        {
            availableTools.AddCustom(tool.Name);
        }

        var config = new SessionConfig
        {
            Model = route.ModelId,
            Streaming = true,
            Tools = tools,
            AvailableTools = availableTools,
            OnPermissionRequest = PermissionHandler.ApproveAll,
            EnableSessionStore = false,
            SkipEmbeddingRetrieval = true,
            EmbeddingCacheStorage = EmbeddingCacheStorageMode.InMemory,
            EnableSkills = false,
            EnableHostGitOperations = false,
            SkipCustomInstructions = true,
            EnableConfigDiscovery = false,
            SystemMessage = new SystemMessageConfig
            {
                Mode = SystemMessageMode.Append,
                Content =
                    "You are the cloud escalation backend for Kare. The caller owns the working directory and every external tool offered in this session. " +
                    "Use those tools when the request requires repository, GitHub, MCP, shell, SSH, or device access. " +
                    "Do not claim a tool is unavailable when it is offered, and do not claim it ran unless you requested it. " +
                    "Return a concise answer or request the required caller tool.",
            },
        };
        if (_options.MaxAiCreditsPerRequest is { } maxAiCredits)
        {
            config.SessionLimits = new SessionLimitsConfig
            {
                MaxAiCredits = maxAiCredits,
            };
        }

        if (route.Provider == CloudModelProvider.GitHubCopilot &&
            string.Equals(route.ModelId, "auto", StringComparison.Ordinal))
        {
            config.Capi = new GitHub.Copilot.CapiSessionOptions
            {
                AutoTier = new AutoTier(_options.AutoTier),
            };
        }
        else if (route.Provider == CloudModelProvider.MicrosoftFoundry)
        {
            var isAnthropic = string.Equals(route.WireApi, "anthropic", StringComparison.Ordinal);
            config.Provider = new GitHub.Copilot.ProviderConfig
            {
                Type = isAnthropic ? "anthropic" : "openai",
                BaseUrl = BuildProviderBaseUrl(route.BaseUrl!, isAnthropic),
                WireApi = isAnthropic ? null : "responses",
                ModelId = route.ModelId,
                WireModel = route.WireModel,
                MaxPromptTokens = route.MaxPromptTokens,
                MaxOutputTokens = route.MaxOutputTokens,
            };

            if (route.Authentication == CloudAuthentication.AzureCli)
            {
                config.Provider.BearerTokenProvider = _ => _azureTokens.GetTokenAsync(route.TokenScope);
            }
            else
            {
                config.Provider.ApiKey = Environment.GetEnvironmentVariable(
                    route.ApiKeyEnvironmentVariable!);
            }
        }

        return config;
    }

    private static string BuildProviderBaseUrl(string baseUrl, bool isAnthropic)
    {
        var normalized = baseUrl.TrimEnd('/');
        var suffix = isAnthropic ? "/anthropic" : "/openai/v1";
        return normalized.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)
            ? normalized
            : normalized + suffix;
    }

    private static string CreatePrompt(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options)
    {
        var prompt = CopilotPromptFormatter.Format(messages);
        if (options?.ToolMode is not RequiredChatToolMode required)
        {
            return prompt;
        }

        return required.RequiredFunctionName is { Length: > 0 } name
            ? $"{prompt}\nYou must request the external tool named {ToSdkToolName(name)} before answering."
            : $"{prompt}\nYou must request one of the offered external tools before answering.";
    }

    private static MessageOptions CreateMessageOptions(
        IReadOnlyList<ChatMessage> messages,
        ChatOptions? options)
    {
        var attachments = messages
            .SelectMany(static message => message.Contents)
            .OfType<DataContent>()
            .Where(static content => content.HasTopLevelMediaType("image"))
            .Select((content, index) => (Attachment)new AttachmentBlob
            {
                Data = content.Base64Data.ToString(),
                DisplayName = content.Name ?? $"image-{index + 1}",
                MimeType = content.MediaType,
            })
            .ToArray();

        return new MessageOptions
        {
            Prompt = CreatePrompt(messages, options),
            Attachments = attachments,
        };
    }

    private static void ThrowIfImagesUnsupported(
        IReadOnlyList<ChatMessage> messages,
        ResolvedCloudRoute route)
    {
        if (!route.SupportsImages &&
            messages.Any(static message =>
                message.Contents.OfType<DataContent>()
                    .Any(static content => content.HasTopLevelMediaType("image"))))
        {
            throw new UnsupportedBackendCapabilityException(
                "images",
                $"Cloud route {route.Id} does not support image attachments.");
        }
    }

    private async ValueTask<CopilotClient> GetClientAsync(CancellationToken cancellationToken)
    {
        if (_client is not null)
        {
            return _client;
        }

        await _startGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_client is not null)
            {
                return _client;
            }

            Directory.CreateDirectory(_options.StateDirectory);
            var client = new CopilotClient(new CopilotClientOptions
            {
                Mode = CopilotClientMode.Empty,
                BaseDirectory = _options.StateDirectory,
                UseLoggedInUser = _options.UseLoggedInUser,
                GitHubToken = Environment.GetEnvironmentVariable(
                    _options.GitHubTokenEnvironmentVariable),
                Logger = _logger,
            });

            await client.StartAsync(cancellationToken).ConfigureAwait(false);
            _client = client;
            return client;
        }
        finally
        {
            _startGate.Release();
        }
    }

    private ResolvedCloudRoute ResolveRoute(ChatOptions? options)
    {
        var routeId = options?.ModelId;
        var configured = _options.Models.FirstOrDefault(candidate =>
            string.Equals(candidate.Id, routeId, StringComparison.Ordinal));
        if (configured is not null)
        {
            return new ResolvedCloudRoute(
                configured.Id,
                configured.Provider,
                configured.ModelId,
                configured.WireModel,
                configured.BaseUrl,
                configured.Authentication,
                configured.ApiKeyEnvironmentVariable,
                configured.TokenScope,
                configured.WireApi,
                configured.MaxPromptTokens,
                configured.MaxOutputTokens,
                configured.SupportsImages);
        }

        var modelId = string.IsNullOrWhiteSpace(routeId) ? _options.ModelId : routeId;
        if (_options.Provider == CopilotCloudProvider.MicrosoftFoundry &&
            string.Equals(modelId, _options.ModelId, StringComparison.Ordinal))
        {
            return new ResolvedCloudRoute(
                "legacy-foundry",
                CloudModelProvider.MicrosoftFoundry,
                modelId,
                modelId,
                _options.FoundryBaseUrl,
                CloudAuthentication.EnvironmentApiKey,
                _options.FoundryApiKeyEnvironmentVariable,
                "https://cognitiveservices.azure.com/.default",
                _options.FoundryWireApi,
                null,
                null,
                SupportsImages: false);
        }

        return new ResolvedCloudRoute(
            "legacy-copilot",
            CloudModelProvider.GitHubCopilot,
            modelId,
            null,
            null,
            CloudAuthentication.Copilot,
            _options.GitHubTokenEnvironmentVariable,
            string.Empty,
            null,
            null,
            null,
            SupportsImages: false);
    }

    private async ValueTask EnsureRouteAvailableAsync(
        CopilotClient client,
        ResolvedCloudRoute route,
        CancellationToken cancellationToken)
    {
        if (route.Provider != CloudModelProvider.GitHubCopilot)
        {
            return;
        }

        if (_availableCopilotModels is null)
        {
            await _modelCatalogGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (_availableCopilotModels is null)
                {
                    var models = await client.ListModelsAsync(cancellationToken).ConfigureAwait(false);
                    _availableCopilotModels = models
                        .Select(static model => model.Id)
                        .ToHashSet(StringComparer.Ordinal);
                }
            }
            finally
            {
                _modelCatalogGate.Release();
            }
        }

        if (!_availableCopilotModels.Contains(route.ModelId))
        {
            throw new CloudInferenceException(
                $"Configured Copilot model {route.ModelId} for route {route.Id} is not available to this account.");
        }
    }

    private void ThrowIfCredentialMissing(ResolvedCloudRoute route)
    {
        if (route.Provider == CloudModelProvider.GitHubCopilot)
        {
            if (!_options.UseLoggedInUser &&
                string.IsNullOrWhiteSpace(
                    Environment.GetEnvironmentVariable(_options.GitHubTokenEnvironmentVariable)))
            {
                throw new CloudInferenceException(
                    $"GitHub token environment variable {_options.GitHubTokenEnvironmentVariable} is not set.");
            }

            return;
        }

        if (route.Authentication == CloudAuthentication.AzureCli)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(
            Environment.GetEnvironmentVariable(route.ApiKeyEnvironmentVariable!)))
        {
            throw new CloudInferenceException(
                $"Foundry key environment variable {route.ApiKeyEnvironmentVariable} is not set for model {route.ModelId}.");
        }
    }

    private sealed record ResolvedCloudRoute(
        string Id,
        CloudModelProvider Provider,
        string ModelId,
        string? WireModel,
        string? BaseUrl,
        CloudAuthentication Authentication,
        string? ApiKeyEnvironmentVariable,
        string TokenScope,
        string? WireApi,
        int? MaxPromptTokens,
        int? MaxOutputTokens,
        bool SupportsImages);

    private static ChatResponse CreateToolResponse(
        ExternalToolRequestedEvent tool,
        long? inputTokens,
        long? outputTokens)
        => CreateToolResponse(
            tool.Data.ToolName,
            tool.Data.ToolCallId,
            tool.Data.Arguments,
            inputTokens,
            outputTokens);

    private static ChatResponse CreateToolResponse(
        string toolName,
        string toolCallId,
        JsonElement? arguments,
        long? inputTokens,
        long? outputTokens)
    {
        var response = new ChatResponse(
            new ChatMessage(
                ChatRole.Assistant,
                [new FunctionCallContent(
                    toolCallId,
                    ToCallerToolName(toolName),
                    ToArguments(arguments))]))
        {
            FinishReason = ChatFinishReason.ToolCalls,
            Usage = CreateUsage(inputTokens, outputTokens),
        };
        return response;
    }

    private static ChatResponse CreateTextResponse(
        string? text,
        long? inputTokens,
        long? outputTokens) =>
        new(new ChatMessage(ChatRole.Assistant, text ?? string.Empty))
        {
            FinishReason = ChatFinishReason.Stop,
            Usage = CreateUsage(inputTokens, outputTokens),
        };

    private static void WriteToolUpdate(
        ChannelWriter<ChatResponseUpdate> writer,
        string toolName,
        string toolCallId,
        JsonElement? arguments)
    {
        var update = new ChatResponseUpdate
        {
            Role = ChatRole.Assistant,
            FinishReason = ChatFinishReason.ToolCalls,
        };
        update.Contents.Add(new FunctionCallContent(
            toolCallId,
            ToCallerToolName(toolName),
            ToArguments(arguments)));
        writer.TryWrite(update);
        writer.TryComplete();
    }

    private static string ToSdkToolName(string name) =>
        string.Equals(name, "bash", StringComparison.Ordinal) ? "kare_external_bash" : name;

    private static string ToCallerToolName(string name) =>
        string.Equals(name, "kare_external_bash", StringComparison.Ordinal) ? "bash" : name;

    private static UsageDetails? CreateUsage(long? inputTokens, long? outputTokens) =>
        inputTokens is null && outputTokens is null
            ? null
            : new UsageDetails
            {
                InputTokenCount = inputTokens,
                OutputTokenCount = outputTokens,
                TotalTokenCount = (inputTokens ?? 0) + (outputTokens ?? 0),
            };

    private static long? Add(long? current, long? value) =>
        value is null ? current : (current ?? 0) + value.Value;

    private static IDictionary<string, object?> ToArguments(JsonElement? arguments)
    {
        if (arguments is not { ValueKind: JsonValueKind.Object } element)
        {
            return new Dictionary<string, object?>();
        }

        var result = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var property in element.EnumerateObject())
        {
            result[property.Name] = ToValue(property.Value);
        }

        return result;
    }

    private static object? ToValue(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.String => value.GetString(),
        JsonValueKind.Number when value.TryGetInt64(out var integer) => integer,
        JsonValueKind.Number => value.GetDouble(),
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        JsonValueKind.Null => null,
        _ => value.Clone(),
    };
}
