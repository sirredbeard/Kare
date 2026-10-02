namespace Kare.Abstractions;

/// <summary>
/// Which local execution path produced a response. Kare must never report an
/// accelerated backend it did not actually use.
/// </summary>
public enum BackendKind
{
    Unknown = 0,

    /// <summary>ONNX Runtime GenAI on CPU. The guaranteed fallback path.</summary>
    OnnxGenAiCpu = 1,

    /// <summary>ONNX Runtime GenAI with the QNN execution provider on the Hexagon NPU.</summary>
    OnnxGenAiQnn = 2,

    /// <summary>Qualcomm GenieX serving a QAIRT bundle.</summary>
    GenieXQairt = 3,

    /// <summary>Qualcomm GenieX serving a llama.cpp model.</summary>
    GenieXLlamaCpp = 4,

    /// <summary>A remote cloud provider. Not a local backend.</summary>
    Remote = 5,
}
