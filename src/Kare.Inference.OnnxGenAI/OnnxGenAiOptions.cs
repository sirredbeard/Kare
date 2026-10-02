using System.ComponentModel.DataAnnotations;

namespace Kare.Inference.OnnxGenAI;

/// <summary>
/// Configuration for the ONNX Runtime GenAI backend.
/// Model path, provider name, and provider options are all explicit. Kare does not
/// probe the filesystem for models or guess an execution provider.
/// </summary>
public sealed class OnnxGenAiOptions
{
    /// <summary>Configuration section name.</summary>
    public const string SectionName = "Kare:Inference:OnnxGenAi";

    /// <summary>
    /// Directory holding the ONNX Runtime GenAI model. Must contain genai_config.json.
    /// On the VENTUNO Q this should live on NVMe, not eMMC.
    /// </summary>
    [Required(AllowEmptyStrings = false)]
    public string ModelPath { get; set; } = string.Empty;

    /// <summary>
    /// Model identifier reported in route records and metrics. This is a label for
    /// accounting. It does not select the model, <see cref="ModelPath"/> does.
    /// </summary>
    [Required(AllowEmptyStrings = false)]
    public string ModelId { get; set; } = "phi-4-mini-instruct-int4";

    /// <summary>
    /// Execution provider to append, such as "qnn". Leave empty to use whatever
    /// genai_config.json already specifies, which is the CPU path for the stock
    /// Microsoft.ML.OnnxRuntimeGenAI package.
    /// </summary>
    public string? ExecutionProvider { get; set; }

    /// <summary>
    /// Optional plugin execution provider library registered with ONNX Runtime GenAI
    /// before the model is loaded. QNN EP 2.x uses this path on Linux ARM64.
    /// </summary>
    public string? ExecutionProviderLibraryPath { get; set; }

    /// <summary>
    /// Process-wide handle used to register <see cref="ExecutionProviderLibraryPath"/>.
    /// This is not the provider name passed to <see cref="ExecutionProvider"/>.
    /// </summary>
    [Required(AllowEmptyStrings = false)]
    public string ExecutionProviderRegistrationName { get; set; } = "QNNExecutionProvider";

    /// <summary>
    /// Provider options passed through to ONNX Runtime, for example backend_path,
    /// soc_model, and htp_arch for QNN. Kare does not invent values for these.
    /// The correct QCS8275 values must be read from the installed QAIRT on the device.
    /// </summary>
    public Dictionary<string, string> ProviderOptions { get; } = [];

    /// <summary>
    /// Prevent ONNX Runtime from silently assigning unsupported nodes to CPU inside
    /// an explicitly configured accelerator session. Kare uses a separate CPU backend.
    /// </summary>
    public bool DisableCpuFallback { get; set; } = true;

    /// <summary>
    /// Native libraries that must be present before the backend reports available.
    /// For a QNN build this should list the provider and QAIRT libraries so a missing
    /// accelerator runtime demotes the backend instead of failing a request.
    /// </summary>
    public List<string> RequiredNativeLibraries { get; } = [];

    /// <summary>Backend preference. CPU stays low so it remains the fallback.</summary>
    [Range(0, 1000)]
    public int Priority { get; set; }
}
