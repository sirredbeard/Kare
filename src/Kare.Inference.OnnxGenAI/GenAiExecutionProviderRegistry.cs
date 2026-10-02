using System.Runtime.InteropServices;

namespace Kare.Inference.OnnxGenAI;

internal static partial class GenAiExecutionProviderRegistry
{
    private const string BridgeLibrary = "kare_onnxruntime_genai_bridge";
    private static readonly Lock Sync = new();
    private static readonly Dictionary<string, string> Registrations = new(StringComparer.Ordinal);

    public static void EnsureRegistered(string registrationName, string libraryPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(registrationName);
        ArgumentException.ThrowIfNullOrWhiteSpace(libraryPath);

        var fullPath = Path.GetFullPath(libraryPath);
        lock (Sync)
        {
            if (Registrations.TryGetValue(registrationName, out var registeredPath))
            {
                if (!string.Equals(registeredPath, fullPath, StringComparison.Ordinal))
                {
                    throw new InvalidOperationException(
                        $"Execution provider registration '{registrationName}' already uses {registeredPath}, not {fullPath}.");
                }

                return;
            }

            var error = RegisterExecutionProviderLibrary(registrationName, fullPath);
            if (error != IntPtr.Zero)
            {
                throw new InvalidOperationException(
                    Marshal.PtrToStringUTF8(error) ?? "Execution provider registration failed without an error message.");
            }

            Registrations.Add(registrationName, fullPath);
        }
    }

    [LibraryImport(
        BridgeLibrary,
        EntryPoint = "KareRegisterExecutionProviderLibrary",
        StringMarshalling = StringMarshalling.Utf8)]
    private static partial IntPtr RegisterExecutionProviderLibrary(string registrationName, string libraryPath);
}
