using System.Text;
using Microsoft.Extensions.Logging;

namespace Kare.Service.Logging;

internal sealed class RotatingFileLoggerProvider : ILoggerProvider
{
    private readonly Lock _lock = new();
    private readonly string _directory;
    private readonly long _fileSizeLimitBytes;
    private readonly long _totalSizeLimitBytes;
    private StreamWriter _writer;
    private long _currentLength;
    private long _archiveSequence;

    public RotatingFileLoggerProvider(
        string directory,
        long fileSizeLimitBytes,
        long totalSizeLimitBytes)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        if (!Path.IsPathRooted(directory))
        {
            throw new ArgumentException("The Kare log directory must be an absolute path.", nameof(directory));
        }

        if (fileSizeLimitBytes <= 0 || totalSizeLimitBytes < fileSizeLimitBytes)
        {
            throw new ArgumentOutOfRangeException(
                nameof(fileSizeLimitBytes),
                "The file limit must be positive and no larger than the total log limit.");
        }

        _directory = directory;
        _fileSizeLimitBytes = fileSizeLimitBytes;
        _totalSizeLimitBytes = totalSizeLimitBytes;

        Directory.CreateDirectory(_directory);
        SetPrivateDirectoryMode(_directory);
        PrepareExistingFiles();
        (_writer, _currentLength) = OpenCurrentFile();
    }

    public ILogger CreateLogger(string categoryName) => new RotatingFileLogger(this, categoryName);

    public void Dispose()
    {
        lock (_lock)
        {
            _writer.Dispose();
        }
    }

    private void Write(
        string category,
        LogLevel level,
        EventId eventId,
        string message,
        Exception? exception)
    {
        var line = string.Create(
            System.Globalization.CultureInfo.InvariantCulture,
            $"{DateTimeOffset.UtcNow:O} [{level}] {category} [{eventId.Id}] {message}");
        if (exception is not null)
        {
            line = $"{line}{Environment.NewLine}{exception}";
        }

        line = BoundEntry(line);
        var bytes = Encoding.UTF8.GetByteCount(line) + Encoding.UTF8.GetByteCount(Environment.NewLine);

        lock (_lock)
        {
            if (_currentLength > 0 && _currentLength + bytes > _fileSizeLimitBytes)
            {
                Rotate();
            }

            _writer.WriteLine(line);
            _writer.Flush();
            _currentLength += bytes;
        }
    }

    private string BoundEntry(string line)
    {
        var maxEntryBytes = checked((int)Math.Min(
            int.MaxValue,
            _fileSizeLimitBytes - Encoding.UTF8.GetByteCount(Environment.NewLine)));
        if (maxEntryBytes <= 0 || Encoding.UTF8.GetByteCount(line) <= maxEntryBytes)
        {
            return maxEntryBytes <= 0 ? string.Empty : line;
        }

        var buffer = new byte[maxEntryBytes];
        Encoding.UTF8.GetEncoder().Convert(
            line.AsSpan(),
            buffer.AsSpan(),
            flush: true,
            out _,
            out var bytesUsed,
            out _);
        return Encoding.UTF8.GetString(buffer.AsSpan(0, bytesUsed));
    }

    private void PrepareExistingFiles()
    {
        var currentPath = GetCurrentPath();
        if (File.Exists(currentPath) && new FileInfo(currentPath).Length > _fileSizeLimitBytes)
        {
            ArchiveCurrentFile(currentPath);
        }

        DeleteOldestArchives();
    }

    private void Rotate()
    {
        _writer.Dispose();

        var currentPath = GetCurrentPath();
        if (File.Exists(currentPath))
        {
            ArchiveCurrentFile(currentPath);
        }

        DeleteOldestArchives();
        (_writer, _currentLength) = OpenCurrentFile();
    }

    private void ArchiveCurrentFile(string currentPath)
    {
        var archivePath = Path.Combine(
            _directory,
            $"kare-{DateTimeOffset.UtcNow:yyyyMMdd-HHmmssfff}-{++_archiveSequence:D6}.log");
        File.Move(currentPath, archivePath);
        SetPrivateFileMode(archivePath);
    }

    private void DeleteOldestArchives()
    {
        var archives = new DirectoryInfo(_directory)
            .EnumerateFiles("kare-*.log", SearchOption.TopDirectoryOnly)
            .OrderByDescending(file => file.LastWriteTimeUtc)
            .ToArray();
        long retainedBytes = 0;

        foreach (var archive in archives)
        {
            retainedBytes += archive.Length;
            if (retainedBytes > _totalSizeLimitBytes - _fileSizeLimitBytes)
            {
                archive.Delete();
            }
        }
    }

    private (StreamWriter Writer, long Length) OpenCurrentFile()
    {
        var path = GetCurrentPath();
        var stream = new FileStream(
            path,
            FileMode.Append,
            FileAccess.Write,
            FileShare.Read,
            bufferSize: 16 * 1024,
            FileOptions.SequentialScan);
        SetPrivateFileMode(path);
        return (new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)), stream.Length);
    }

    private string GetCurrentPath() => Path.Combine(_directory, "kare.log");

    private static void SetPrivateDirectoryMode(string path)
    {
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(
                path,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    private static void SetPrivateFileMode(string path)
    {
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
    }

    private sealed class RotatingFileLogger(RotatingFileLoggerProvider provider, string categoryName)
        : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (IsEnabled(logLevel))
            {
                provider.Write(categoryName, logLevel, eventId, formatter(state, exception), exception);
            }
        }
    }
}
