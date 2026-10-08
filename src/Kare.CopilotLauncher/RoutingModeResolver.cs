using System.Diagnostics;
using System.ComponentModel;
using Kare.Abstractions;

namespace Kare.CopilotLauncher;

internal sealed record RoutingModeSelection(RouteMode Mode, string Reason);

internal static class RoutingModeResolver
{
    private static readonly HashSet<string> DefaultWorkOrganizations =
        new(["neverenginsupport", "dotnet"], StringComparer.OrdinalIgnoreCase);

    public static RoutingModeSelection Resolve(
        bool explicitWork,
        IReadOnlyDictionary<string, string> configValues,
        IReadOnlyList<GitRemote> remotes)
    {
        if (explicitWork)
        {
            return new(RouteMode.Work, "explicit_work");
        }

        var workOrganizations = ParseList(
            GetSetting(configValues, "KARE_WORK_ORGANIZATIONS"))
            .Concat(DefaultWorkOrganizations)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var workRepositories = ParseList(
            GetSetting(configValues, "KARE_WORK_REPOSITORIES"))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        if (remotes.Any(remote =>
                !remote.IsUpstream &&
                (workRepositories.Contains(remote.Repository) ||
                 workOrganizations.Contains(remote.Owner))))
        {
            return new(
                RouteMode.Work,
                remotes.Any(remote =>
                    !remote.IsUpstream &&
                    workRepositories.Contains(remote.Repository))
                    ? "protected_mapping"
                    : "org_match");
        }

        if (remotes.Any(remote =>
                remote.IsUpstream &&
                (workRepositories.Contains(remote.Repository) ||
                 workOrganizations.Contains(remote.Owner))))
        {
            return new(RouteMode.Work, "upstream_match");
        }

        return new(RouteMode.Personal, "default_personal");
    }

    public static IReadOnlyList<GitRemote> ReadGitRemotes()
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "git",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                CreateNoWindow = true,
            },
        };
        process.StartInfo.ArgumentList.Add("config");
        process.StartInfo.ArgumentList.Add("--get-regexp");
        process.StartInfo.ArgumentList.Add(@"^remote\..*\.url$");

        try
        {
            if (!process.Start())
            {
                return [];
            }

            var output = process.StandardOutput.ReadToEnd();
            process.WaitForExit();
            if (process.ExitCode != 0)
            {
                return [];
            }

            return output
                .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
                .Select(ParseRemote)
                .Where(static remote => remote is not null)
                .Cast<GitRemote>()
                .ToArray();
        }
        catch (Win32Exception)
        {
            return [];
        }
    }

    private static GitRemote? ParseRemote(string line)
    {
        var separator = line.IndexOf('\t');
        if (separator <= 0 || separator == line.Length - 1)
        {
            return null;
        }

        var name = line[..separator];
        var url = line[(separator + 1)..].Trim();
        var repository = NormalizeRepository(url);
        if (repository is null)
        {
            return null;
        }

        var parts = repository.Split('/', 2);
        return new GitRemote(
            parts[0],
            parts[1],
            name.StartsWith("remote.upstream.", StringComparison.OrdinalIgnoreCase));
    }

    private static string? NormalizeRepository(string value)
    {
        var text = value.Trim();
        if (text.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
        {
            text = text[..^4];
        }

        string? path;
        if (Uri.TryCreate(text, UriKind.Absolute, out var uri) &&
            string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(uri.Host, "github.com", StringComparison.OrdinalIgnoreCase))
        {
            path = uri.AbsolutePath;
        }
        else
        {
            var separator = text.IndexOf(':');
            if (separator <= 0 ||
                !text[..separator].EndsWith("@github.com", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            path = text[(separator + 1)..];
        }

        var parts = path.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length >= 2
            ? $"{parts[0].ToLowerInvariant()}/{parts[1].ToLowerInvariant()}"
            : null;
    }

    private static IReadOnlyList<string> ParseList(string? value) =>
        string.IsNullOrWhiteSpace(value)
            ? []
            : value.Split([',', ';', '\n'], StringSplitOptions.RemoveEmptyEntries)
                .Select(static item => item.Trim().TrimEnd('/').ToLowerInvariant())
                .Where(static item => item.Length > 0)
                .ToArray();

    private static string? GetSetting(
        IReadOnlyDictionary<string, string> configValues,
        string key)
    {
        var environmentValue = Environment.GetEnvironmentVariable(key);
        return !string.IsNullOrWhiteSpace(environmentValue)
            ? environmentValue
            : configValues.TryGetValue(key, out var value) ? value : null;
    }
}

internal sealed record GitRemote(string Owner, string RepositoryName, bool IsUpstream)
{
    public string Repository => $"{Owner}/{RepositoryName}";
}
