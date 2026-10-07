using Kare.Service.Logging;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Kare.Tests;

public sealed class RotatingFileLoggerProviderTests
{
    [Fact]
    public void RotatesAndBoundsLogFiles()
    {
        var directory = Path.Combine(Path.GetTempPath(), "kare-logs-" + Guid.NewGuid().ToString("N"));

        try
        {
            using (var provider = new RotatingFileLoggerProvider(directory, 256, 768))
            {
                var logger = provider.CreateLogger("test");
                for (var index = 0; index < 40; index++)
                {
                    logger.LogInformation("Message {Index}: {Payload}", index, new string('x', 80));
                }
            }

            var files = new DirectoryInfo(directory).EnumerateFiles("kare*.log").ToArray();
            Assert.NotEmpty(files);
            Assert.True(files.Sum(file => file.Length) <= 768);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void BoundsOversizedEntries()
    {
        var directory = Path.Combine(Path.GetTempPath(), "kare-logs-" + Guid.NewGuid().ToString("N"));

        try
        {
            using (var provider = new RotatingFileLoggerProvider(directory, 256, 768))
            {
                provider.CreateLogger("test").LogInformation("{Payload}", new string('é', 1_000));
            }

            var files = new DirectoryInfo(directory).EnumerateFiles("kare*.log").ToArray();
            Assert.NotEmpty(files);
            Assert.All(files, file => Assert.True(file.Length <= 256));
            Assert.True(files.Sum(file => file.Length) <= 768);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void AppliesRetentionLimitsDuringInitialization()
    {
        var directory = Path.Combine(Path.GetTempPath(), "kare-logs-" + Guid.NewGuid().ToString("N"));

        try
        {
            Directory.CreateDirectory(directory);
            File.WriteAllText(Path.Combine(directory, "kare.log"), new string('x', 400));
            File.WriteAllText(Path.Combine(directory, "kare-old.log"), new string('x', 400));

            using (new RotatingFileLoggerProvider(directory, 256, 512))
            {
            }

            var files = new DirectoryInfo(directory).EnumerateFiles("kare*.log").ToArray();
            Assert.True(files.Sum(file => file.Length) <= 512);
            Assert.True(new FileInfo(Path.Combine(directory, "kare.log")).Length <= 256);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
