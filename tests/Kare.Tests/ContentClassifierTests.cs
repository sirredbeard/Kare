using Kare.Service.Dashboard;
using Xunit;

namespace Kare.Tests;

public sealed class ContentClassifierTests
{
    [Fact]
    public void ExtractKeywordsReturnsOnlyFixedVocabularyTerms()
    {
        var keywords = ContentClassifier.ExtractKeywords(
            "Use Go and Docker for the deployment pipeline, then run tests.");

        Assert.Contains("go", keywords);
        Assert.Contains("docker", keywords);
        Assert.Contains("deploy", keywords);
        Assert.Contains("test", keywords);
        Assert.DoesNotContain("pipeline", keywords);
        Assert.DoesNotContain("then", keywords);
    }

    [Fact]
    public void ExtractKeywordsNormalizesSynonyms()
    {
        var keywords = ContentClassifier.ExtractKeywords("Configure golang and dotnet migration.");

        Assert.Contains("go", keywords);
        Assert.Contains("dotnet", keywords);
        Assert.Contains("migrate", keywords);
    }

    [Fact]
    public void ExtractKeywordsIncludesPrivacySafeTopics()
    {
        var keywords = ContentClassifier.ExtractKeywords(
            "Use the GitHub CLI and MCP skill for an Arduino weather service.");

        Assert.Contains("github", keywords);
        Assert.Contains("cli", keywords);
        Assert.Contains("mcp", keywords);
        Assert.Contains("skill", keywords);
        Assert.Contains("arduino", keywords);
        Assert.Contains("weather", keywords);
    }

    [Fact]
    public void ExtractKeywordsOnUnrelatedTextReturnsEmpty()
    {
        var keywords = ContentClassifier.ExtractKeywords("The quick brown fox jumps over the lazy dog.");

        Assert.Empty(keywords);
    }

    [Fact]
    public void PrimaryTaskClassIgnoresLanguageOnlyKeywords()
    {
        var languageOnly = ContentClassifier.ExtractKeywords("This project uses Go and Rust.");
        Assert.Null(ContentClassifier.PrimaryTaskClass(languageOnly));

        var withTask = ContentClassifier.Classify("Debug a Go service that fails under load.");
        Assert.Equal("debug", withTask.TaskClass);
        Assert.Contains("go", withTask.Keywords);
    }

    [Fact]
    public void ExtractHeadingsIsBoundedAndIgnoresLongLines()
    {
        var content = string.Join(
            '\n',
            "# Overview",
            "## Getting started",
            new string('#', 1) + " " + new string('x', 200),
            "not a heading",
            "### Configuration",
            "#### Routing",
            "##### Caching",
            "###### Skills",
            "####### MCP",
            "######## Sources");

        var headings = ContentClassifier.ExtractHeadings(content);

        Assert.True(headings.Count <= 8);
        Assert.Contains("Overview", headings);
        Assert.Contains("Getting started", headings);
        Assert.DoesNotContain(headings, heading => heading.Length > 80);
    }
}
