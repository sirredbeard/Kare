namespace Kare.Service.Dashboard;

/// <summary>
/// Classifies text against a small fixed vocabulary of language, framework, and task tags.
/// Used to build compact, privacy-safe descriptors for authoritative sources, skills, MCP
/// servers, and cache entries: the extracted tags are a bounded intersection with a known
/// allow-list, never an arbitrary excerpt of prompt or response text, so they are safe to
/// keep in process-local dashboard metadata even when the source text itself is not.
/// </summary>
public static class ContentClassifier
{
    private const int DefaultMaxKeywords = 8;
    private const int MaxHeadings = 8;
    private const int MaxHeadingLength = 80;

    private static readonly string[] LanguageTags =
    [
        "csharp", "dotnet", "go", "python", "java", "javascript", "typescript", "rust",
        "cpp", "c", "sql", "bash", "powershell", "ruby", "php", "kotlin", "swift",
        "yaml", "docker", "kubernetes",
    ];

    private static readonly string[] TaskTags =
    [
        "debug", "test", "refactor", "deploy", "review", "docs", "build", "configure",
        "security", "performance", "optimize", "migrate", "cache", "route", "auth",
        "triage", "monitor", "backup", "restore",
    ];

    private static readonly string[] TopicTags =
    [
        "api", "arduino", "cli", "dashboard", "github", "linux", "mcp", "repository",
        "service", "skill", "source", "weather",
    ];

    /// <summary>Maps common synonyms and tokenizer artifacts to one canonical tag.</summary>
    private static readonly IReadOnlyDictionary<string, string> Synonyms =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["net"] = "dotnet",
            ["aspnet"] = "dotnet",
            ["golang"] = "go",
            ["js"] = "javascript",
            ["ts"] = "typescript",
            ["py"] = "python",
            ["testing"] = "test",
            ["tests"] = "test",
            ["deployment"] = "deploy",
            ["deploying"] = "deploy",
            ["documentation"] = "docs",
            ["configuration"] = "configure",
            ["optimization"] = "optimize",
            ["optimizing"] = "optimize",
            ["migration"] = "migrate",
            ["migrating"] = "migrate",
            ["caching"] = "cache",
            ["routing"] = "route",
            ["authentication"] = "auth",
            ["authorization"] = "auth",
            ["monitoring"] = "monitor",
        };

    public static readonly IReadOnlyList<string> Vocabulary =
        [.. LanguageTags, .. TaskTags, .. TopicTags];

    /// <summary>Returns the bounded intersection of <paramref name="text"/> with the known
    /// language, framework, and task vocabulary, in vocabulary order.</summary>
    public static IReadOnlyList<string> ExtractKeywords(string? text, int maxKeywords = DefaultMaxKeywords)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return [];
        }

        var terms = Tokenize(text);
        if (terms.Count == 0)
        {
            return [];
        }

        return Vocabulary
            .Where(tag => terms.Contains(tag))
            .Take(Math.Max(0, maxKeywords))
            .ToArray();
    }

    /// <summary>Returns the first task tag found, or null when the text only matched
    /// language/framework tags or nothing at all.</summary>
    public static string? PrimaryTaskClass(IReadOnlyList<string> keywords) =>
        keywords.FirstOrDefault(tag => TaskTags.Contains(tag, StringComparer.Ordinal));

    /// <summary>Convenience overload combining keyword extraction and task classification for
    /// cache descriptors; the caller passes in-memory text that is discarded immediately after
    /// this call rather than persisted.</summary>
    public static (IReadOnlyList<string> Keywords, string? TaskClass) Classify(string? text)
    {
        var keywords = ExtractKeywords(text);
        return (keywords, PrimaryTaskClass(keywords));
    }

    /// <summary>Extracts up to <see cref="MaxHeadings"/> short heading-like lines (Markdown
    /// `#` headings or short title-like lines) to build a compact table of contents without
    /// retaining the full document.</summary>
    public static IReadOnlyList<string> ExtractHeadings(string? content)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            return [];
        }

        var headings = new List<string>();
        foreach (var rawLine in content.Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.Length == 0 || line.Length > MaxHeadingLength)
            {
                continue;
            }

            if (line.StartsWith('#'))
            {
                var text = line.TrimStart('#').Trim();
                if (text.Length > 0)
                {
                    headings.Add(text);
                }
            }

            if (headings.Count >= MaxHeadings)
            {
                break;
            }
        }

        return headings;
    }

    private static HashSet<string> Tokenize(string value)
    {
        var tokens = value
            .Split(
                [' ', '\t', '\r', '\n', '/', '\\', '.', ':', ',', ';', '(', ')', '[', ']', '{', '}', '-', '_', '#', '*'],
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(static term => term.ToLowerInvariant())
            .ToHashSet(StringComparer.Ordinal);

        foreach (var (synonym, canonical) in Synonyms)
        {
            if (tokens.Contains(synonym))
            {
                tokens.Add(canonical);
            }
        }

        return tokens;
    }
}
