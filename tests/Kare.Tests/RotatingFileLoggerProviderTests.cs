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
}
