using System.Runtime.InteropServices;
using Kare.Abstractions;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.ML.OnnxRuntimeGenAI;

namespace Kare.Inference.OnnxGenAI;

/// <summary>
/// ONNX Runtime GenAI backend. Serves the CPU reference path, and the QNN path when a
/// QNN capable native runtime and the QAIRT libraries are present on the device.
/// </summary>
/// <remarks>
/// The stock Microsoft.ML.OnnxRuntimeGenAI package ships linux-arm64 natives but does not
/// ship libonnxruntime_providers_qnn.so. Configuring an execution provider here is
/// therefore necessary but not sufficient for NPU execution. The probe checks for the
/// native libraries so a board without them demotes to CPU instead of failing requests.
/// </remarks>
public sealed class OnnxGenAiBackend : ILocalInferenceBackend
{
    private readonly OnnxGenAiOptions _options;
    private readonly ILogger<OnnxGenAiBackend> _logger;
    private readonly Lazy<OnnxRuntimeGenAIChatClient> _client;

    /// <summary>Creates the backend. The model is not loaded until first use.</summary>
    public OnnxGenAiBackend(IOptions<OnnxGenAiOptions> options, ILogger<OnnxGenAiBackend> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);
        _options = options.Value;
        _logger = logger;
        _client = new Lazy<OnnxRuntimeGenAIChatClient>(CreateClient, LazyThreadSafetyMode.ExecutionAndPublication);
    }

    /// <inheritdoc />
    public BackendKind Kind => IsQnnConfigured ? BackendKind.OnnxGenAiQnn : BackendKind.OnnxGenAiCpu;

    /// <inheritdoc />
    public int Priority => _options.Priority;

    private bool IsQnnConfigured =>
        string.Equals(_options.ExecutionProvider, "qnn", StringComparison.OrdinalIgnoreCase);

    /// <inheritdoc />
    public ValueTask<BackendProbeResult> ProbeAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (string.IsNullOrWhiteSpace(_options.ModelPath))
        {
            return ValueTask.FromResult(BackendProbeResult.Unavailable("No model path is configured."));
        }

        if (!Directory.Exists(_options.ModelPath))
        {
            return ValueTask.FromResult(
                BackendProbeResult.Unavailable($"Model directory does not exist: {_options.ModelPath}"));
        }

        var configPath = Path.Combine(_options.ModelPath, "genai_config.json");
        if (!File.Exists(configPath))
        {
            return ValueTask.FromResult(
                BackendProbeResult.Unavailable($"genai_config.json is missing from {_options.ModelPath}"));
        }

        foreach (var library in _options.RequiredNativeLibraries)
        {
            if (!NativeLibrary.TryLoad(library, out var handle))
            {
                // Expected on a board without the accelerator runtime installed.
                return ValueTask.FromResult(
                    BackendProbeResult.Unavailable($"Required native library could not be loaded: {library}"));
            }

            NativeLibrary.Free(handle);
        }

        // Building the Config proves the native GenAI library loads and that the model
        // config parses. It does not load model weights.
        try
        {
            using var config = BuildConfig();
        }
        catch (DllNotFoundException ex)
        {
            return ValueTask.FromResult(
                BackendProbeResult.Unavailable($"ONNX Runtime GenAI native library did not load: {ex.Message}"));
        }
        catch (OnnxRuntimeGenAIException ex)
        {
            return ValueTask.FromResult(
                BackendProbeResult.Unavailable($"ONNX Runtime GenAI rejected the configuration: {ex.Message}"));
        }

        var provider = IsQnnConfigured ? "qnn" : "the provider declared in genai_config.json";
        return ValueTask.FromResult(
            BackendProbeResult.Available($"Model {_options.ModelId} at {_options.ModelPath} using {provider}."));
    }

    /// <inheritdoc />
    public Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default) =>
        _client.Value.GetResponseAsync(messages, options, cancellationToken);

    /// <inheritdoc />
    public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default) =>
        _client.Value.GetStreamingResponseAsync(messages, options, cancellationToken);

    /// <inheritdoc />
    public object? GetService(Type serviceType, object? serviceKey = null)
    {
        ArgumentNullException.ThrowIfNull(serviceType);

        if (serviceKey is null && serviceType.IsInstanceOfType(this))
        {
            return this;
        }

        return serviceType == typeof(ChatClientMetadata)
            ? new ChatClientMetadata("onnxruntime-genai", defaultModelId: _options.ModelId)
            : ((IChatClient)_client.Value).GetService(serviceType, serviceKey);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_client.IsValueCreated)
        {
            _client.Value.Dispose();
        }
    }

    private Config BuildConfig()
    {
        var config = new Config(_options.ModelPath);
        try
        {
            if (!string.IsNullOrWhiteSpace(_options.ExecutionProvider))
            {
                // Replace rather than add. Leaving the config declared provider in place
                // would let ONNX Runtime silently fall back to it, and Kare would then
                // report an accelerated backend it did not actually use.
                config.ClearProviders();
                config.AppendProvider(_options.ExecutionProvider);

                foreach (var (option, value) in _options.ProviderOptions)
                {
                    config.SetProviderOption(_options.ExecutionProvider, option, value);
                }
            }

            return config;
        }
        catch
        {
            config.Dispose();
            throw;
        }
    }

    private OnnxRuntimeGenAIChatClient CreateClient()
    {
        _logger.LogInformation(
            "Loading ONNX Runtime GenAI model {ModelId} from {ModelPath} with provider {Provider}.",
            _options.ModelId,
            _options.ModelPath,
            _options.ExecutionProvider ?? "genai_config.json default");

        var config = BuildConfig();
        return new OnnxRuntimeGenAIChatClient(config, ownsConfig: true, new OnnxRuntimeGenAIChatClientOptions());
    }
}
