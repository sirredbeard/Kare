namespace Kare.Service.Storage;

internal static class ProtectedStateFile
{
    public static void WriteAtomic(string path, ReadOnlySpan<byte> content, int backupCount)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (!Path.IsPathRooted(path))
        {
            throw new ArgumentException("Persistent state paths must be absolute.", nameof(path));
        }

        var directory = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(directory);
        SetPrivateDirectoryMode(directory);

        for (var index = backupCount; index >= 2; index--)
        {
            var previous = $"{path}.bak{index - 1}";
            var current = $"{path}.bak{index}";
            if (File.Exists(previous))
            {
                File.Move(previous, current, overwrite: true);
                SetPrivateFileMode(current);
            }
        }

        if (backupCount > 0 && File.Exists(path))
        {
            File.Copy(path, $"{path}.bak1", overwrite: true);
            SetPrivateFileMode($"{path}.bak1");
        }

        var temporary = path + ".tmp";
        File.WriteAllBytes(temporary, content);
        SetPrivateFileMode(temporary);
        File.Move(temporary, path, overwrite: true);
        SetPrivateFileMode(path);
    }

    public static void SetPrivateFileMode(string path)
    {
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
    }

    private static void SetPrivateDirectoryMode(string path)
    {
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(
                path,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }
}
