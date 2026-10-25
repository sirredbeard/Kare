using Kare.Service.Dashboard;
using Xunit;

namespace Kare.Tests;

public sealed class CrawlerPathPolicyTests
{
    private static readonly Uri Seed = new("https://example.com/docs/");

    [Theory]
    [InlineData("https://example.com/docs/*", "https://example.com/docs/guide", true)]
    [InlineData("https://example.com/docs/*", "https://example.com/docs/deep/page", true)]
    [InlineData("https://example.com/docs/*", "https://example.com/other/page", false)]
    [InlineData("https://example.com/docs/*", "https://example.com/docs/login", false)]
    [InlineData("https://example.com/docs/*", "https://example.com/docs/account/settings", false)]
    [InlineData("https://example.com/docs/*", "https://example.com/docs/.git/config", false)]
    [InlineData("https://example.com/docs/*", "https://other.example.com/docs/guide", false)]
    [InlineData("https://example.com/docs*", "https://example.com/docs", true)]
    [InlineData("https://example.com/docs*", "https://example.com/docs-v2", true)]
    [InlineData("https://example.com/docs*", "https://example.com/docs-v2/page", false)]
    [InlineData("https://example.com/docs*", "https://example.com/docs/page", false)]
    public void CrawlTargetStaysInsideTheRegisteredScope(string pattern, string linked, bool expected)
    {
        Assert.Equal(
            expected,
            DashboardKnowledgeService.IsAllowedCrawlTarget(Seed, pattern, new Uri(linked)));
    }
}
