using System.Text.Json;
using Microsoft.Extensions.AI;

namespace Kare.Service.Dashboard;

public sealed partial class DashboardIntakeService
{
    private sealed record Classification(
        IntakeStatus Status,
        string? CanonicalName = null,
        string? CanonicalSource = null,
        string? InferredScope = null,
        string? TrustBasis = null,
        string? RefreshPolicy = null,
        bool RequiresAuth = false,
        string? RejectedExpansion = null,
        bool RequiresApproval = true,
        bool UsedLocalModel = false,
        string? ResolvedTarget = null,
        string? ResolvedDescription = null,
        string? Error = null,
        bool NeedsLocalModel = false);

    private static readonly string[] CommandPrefixes =
    [
        "/plugin install",
        "/plugin add",
        "claude plugin",
        "copilot plugin",
        "copilot skill",
        "gh extension install",
        "gh repo clone",
        "npm install",
        "npx ",
        "pip install",
        "pipx install",
    ];

    private static Classification ClassifySource(string input)
    {
        var hasWildcard = input.Contains('*', StringComparison.Ordinal);
        var parseable = hasWildcard
            ? input.Replace("*", "wildcard", StringComparison.Ordinal)
            : input;

        if (!Uri.TryCreate(parseable, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
        {
            return new Classification(
                IntakeStatus.Review,
                InferredScope: "unresolved",
                Error: "Not an absolute HTTPS URL.",
                NeedsLocalModel: true);
        }

        var segments = uri.AbsolutePath.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);

        if (hasWildcard)
        {
            return new Classification(
                IntakeStatus.Indexing,
                CanonicalSource: input,
                InferredScope: "explicit wildcard pattern",
                TrustBasis: "user-specified pattern",
                RefreshPolicy: "15 minute background refresh",
                RequiresApproval: false,
                ResolvedTarget: input);
        }

        if (string.Equals(uri.Host, "github.com", StringComparison.OrdinalIgnoreCase))
        {
            if (segments.Length <= 1)
            {
                return new Classification(
                    IntakeStatus.Review,
                    CanonicalSource: uri.GetLeftPart(UriPartial.Authority) + "/" + (segments.Length == 1 ? segments[0] : string.Empty),
                    InferredScope: "organization (broad)",
                    TrustBasis: "github organization",
                    RejectedExpansion: "Will not crawl every repository, issue, fork, or release in the organization.",
                    RequiresApproval: true);
            }

            var owner = segments[0];
            var repo = segments[1];
            var resolvedTarget = $"https://github.com/{owner}/{repo}/*";
            if (segments.Length == 2)
            {
                return new Classification(
                    IntakeStatus.Review,
                    CanonicalSource: $"https://github.com/{owner}/{repo}",
                    InferredScope: "repository (narrow scope needs confirmation)",
                    TrustBasis: "github repository",
                    RefreshPolicy: "15 minute background refresh",
                    RejectedExpansion:
                        "Proposed scope does not exclude issues, pull requests, releases, or forks by path; " +
                        "review before activating.",
                    RequiresApproval: true,
                    ResolvedTarget: resolvedTarget);
            }

            return new Classification(
                IntakeStatus.Indexing,
                CanonicalSource: input,
                InferredScope: "repository path",
                TrustBasis: "github repository",
                RefreshPolicy: "15 minute background refresh",
                RequiresApproval: false,
                ResolvedTarget: input);
        }

        if (segments.Length == 0)
        {
            return new Classification(
                IntakeStatus.Review,
                CanonicalSource: uri.GetLeftPart(UriPartial.Authority),
                InferredScope: "domain root (broad)",
                TrustBasis: "same-origin site",
                RejectedExpansion: "Will not crawl the entire domain from its root.",
                RequiresApproval: true);
        }

        return new Classification(
            IntakeStatus.Indexing,
            CanonicalSource: input,
            InferredScope: "single page or subtree",
            TrustBasis: "same-origin page",
            RefreshPolicy: "15 minute background refresh",
            RequiresApproval: false,
            ResolvedTarget: input);
    }

    private static Classification ClassifySkill(string input)
    {
        if (Path.IsPathRooted(input) && !input.Contains("://", StringComparison.Ordinal))
        {
            var name = Path.GetFileNameWithoutExtension(input);
            if (string.IsNullOrWhiteSpace(name))
            {
                name = "local-skill";
            }

            return new Classification(
                IntakeStatus.Indexing,
                CanonicalName: name,
                CanonicalSource: input,
                InferredScope: "explicit local path",
                TrustBasis: "local device file",
                RequiresApproval: false,
                ResolvedTarget: input,
                ResolvedDescription: $"Local skill at {input}.");
        }

        if (Uri.TryCreate(input, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps)
        {
            var path = uri.AbsolutePath;
            var looksLikeSkillFile = path.EndsWith("SKILL.md", StringComparison.OrdinalIgnoreCase);

            if (looksLikeSkillFile)
            {
                var resolved = ToRawIfGithubBlob(uri).AbsoluteUri;
                var parent = path.TrimEnd('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
                var name = parent.Length >= 2 ? parent[^2] : "remote-skill";
                return new Classification(
                    IntakeStatus.Fetching,
                    CanonicalName: name,
                    CanonicalSource: input,
                    InferredScope: "direct skill definition",
                    TrustBasis: "direct SKILL.md reference",
                    RefreshPolicy: "manual refresh",
                    RequiresApproval: false,
                    ResolvedTarget: resolved,
                    ResolvedDescription: $"Skill fetched from {input}.");
            }

            if (string.Equals(uri.Host, "github.com", StringComparison.OrdinalIgnoreCase))
            {
                var segments = path.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
                if (segments.Length == 2)
                {
                    var owner = segments[0];
                    var repo = segments[1];
                    var guess = $"https://raw.githubusercontent.com/{owner}/{repo}/HEAD/SKILL.md";
                    return new Classification(
                        IntakeStatus.Review,
                        CanonicalName: repo,
                        CanonicalSource: input,
                        InferredScope: "repository (guessed SKILL.md path)",
                        TrustBasis: "github repository",
                        RejectedExpansion: "The repository itself is not treated as skill content; only a guessed SKILL.md path was proposed.",
                        RequiresApproval: true,
                        ResolvedTarget: guess,
                        ResolvedDescription: $"Guessed skill location in {owner}/{repo}.");
                }

                return new Classification(
                    IntakeStatus.Review,
                    CanonicalSource: input,
                    InferredScope: "repository path (not a recognized skill file)",
                    TrustBasis: "github repository",
                    RequiresApproval: true,
                    Error: "Path inside the repository was not recognized as a skill definition.");
            }

            return new Classification(
                IntakeStatus.Review,
                CanonicalSource: input,
                InferredScope: "unresolved URL",
                RequiresApproval: true,
                Error: "URL does not point directly to a skill definition.");
        }

        if (TryParseCommand(input, out var commandName))
        {
            return new Classification(
                IntakeStatus.Review,
                CanonicalName: commandName,
                CanonicalSource: input,
                InferredScope: "install or plugin command (not executed)",
                TrustBasis: "unverified install command",
                RequiresApproval: true,
                Error: "Command was parsed as data only and was not executed. Provide a direct SKILL.md URL or absolute path to proceed.");
        }

        return new Classification(IntakeStatus.Review, InferredScope: "unresolved", NeedsLocalModel: true);
    }

    private static Classification ClassifyMcp(string input)
    {
        if (Uri.TryCreate(input, UriKind.Absolute, out var uri) &&
            (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
        {
            if (string.Equals(uri.Host, "github.com", StringComparison.OrdinalIgnoreCase))
            {
                return new Classification(
                    IntakeStatus.Review,
                    CanonicalSource: input,
                    InferredScope: "repository (documentation, not a resolved endpoint)",
                    TrustBasis: "github repository",
                    RequiresApproval: true,
                    Error: "The repository documents a server but does not itself provide a Streamable HTTP endpoint.");
            }

            var name = uri.Host.Split('.').FirstOrDefault(static part => part.Length > 0) ?? "mcp-server";
            return new Classification(
                IntakeStatus.Fetching,
                CanonicalName: name,
                CanonicalSource: EndpointRedaction.Redact(uri),
                InferredScope: "direct endpoint",
                TrustBasis: "user-specified endpoint",
                RequiresAuth: !string.IsNullOrEmpty(uri.Query),
                RefreshPolicy: "manual refresh; probed on add",
                RequiresApproval: false,
                ResolvedTarget: input);
        }

        if (TryParseCommand(input, out var commandName))
        {
            return new Classification(
                IntakeStatus.Review,
                CanonicalName: commandName,
                CanonicalSource: input,
                InferredScope: "install or plugin command (not executed)",
                TrustBasis: "unverified install command",
                RequiresApproval: true,
                Error: "Command was parsed as data only and was not executed. Provide a resolved Streamable HTTP endpoint to proceed.");
        }

        return new Classification(IntakeStatus.Review, InferredScope: "unresolved", NeedsLocalModel: true);
    }

    private static bool TryParseCommand(string input, out string? name)
    {
        foreach (var prefix in CommandPrefixes)
        {
            if (input.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                var rest = input[prefix.Length..].Trim();
                var token = rest.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
                name = string.IsNullOrWhiteSpace(token)
                    ? null
                    : token.Split('@')[0].Trim();
                return true;
            }
        }

        name = null;
        return false;
    }

    private static Uri ToRawIfGithubBlob(Uri uri)
    {
        if (!string.Equals(uri.Host, "github.com", StringComparison.OrdinalIgnoreCase))
        {
            return uri;
        }

        var segments = uri.AbsolutePath.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        var blobIndex = Array.IndexOf(segments, "blob");
        if (blobIndex < 0 || blobIndex + 1 >= segments.Length || segments.Length < 3)
        {
            return uri;
        }

        var owner = segments[0];
        var repo = segments[1];
        var tail = string.Join('/', segments[(blobIndex + 1)..]);
        return new Uri($"https://raw.githubusercontent.com/{owner}/{repo}/{tail}");
    }

    private async Task<Classification> InterpretWithLocalModelAsync(
        IntakeKind kind,
        string input,
        Classification fallback,
        CancellationToken cancellationToken)
    {
        if (_localModel is null)
        {
            return fallback with
            {
                Error = "No local model is available to interpret this input. Provide a direct URL, " +
                    "absolute path, or endpoint to proceed.",
            };
        }

        using var timeout = new CancellationTokenSource(LocalModelTimeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);

        var kindLabel = kind switch
        {
            IntakeKind.Source => "authoritative source",
            IntakeKind.Skill => "skill",
            IntakeKind.Mcp => "MCP server",
            _ => "intake value",
        };
        var prompt =
            $"""
            Kare is a bounded local gateway. A user pasted this value into the {kindLabel} intake
            field: {input}
            Return strict JSON only, no prose, matching exactly:
            """ + """
            {"canonicalName":"...","canonicalSource":"...","scope":"...","trustBasis":"...","note":"..."}
            """ + """
            Do not claim the value was fetched, installed, or activated. Propose an interpretation
            only; Kare validates and decides whether to apply it.
            """;

        try
        {
            var response = await _localModel.GetResponseAsync(
                [new ChatMessage(ChatRole.User, prompt)],
                new ChatOptions { MaxOutputTokens = 200 },
                linked.Token).ConfigureAwait(false);
            var text = response.Text ?? string.Empty;
            var start = text.IndexOf('{', StringComparison.Ordinal);
            var end = text.LastIndexOf('}');
            if (start < 0 || end < start)
            {
                return fallback with
                {
                    UsedLocalModel = true,
                    Error = "Local model did not return a structured proposal; marked for manual review.",
                };
            }

            using var document = JsonDocument.Parse(text[start..(end + 1)]);
            var root = document.RootElement;
            string? Read(string property) =>
                root.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
                    ? value.GetString()
                    : null;

            return new Classification(
                IntakeStatus.Review,
                CanonicalName: Read("canonicalName"),
                CanonicalSource: Read("canonicalSource") ?? input,
                InferredScope: Read("scope"),
                TrustBasis: Read("trustBasis"),
                RequiresApproval: true,
                UsedLocalModel: true,
                Error: Read("note"));
        }
        catch (Exception ex) when (
            ex is InvalidOperationException or OperationCanceledException or JsonException)
        {
            _logger.LogWarning(ex, "Local model interpretation failed for {Kind} intake.", kind);
            return fallback with
            {
                UsedLocalModel = true,
                Error = "Local model interpretation failed; marked for manual review.",
            };
        }
    }
}
