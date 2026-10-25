using System.Text.Json;
using System.Text.Json.Nodes;
using Kare.Core;

namespace Kare.Cloud.Copilot;

/// <summary>
/// Reads the OAuth token from a Claude Code credentials file and refreshes it in
/// place when it is about to expire. The file keeps the Claude Code shape
/// (<c>claudeAiOauth</c> with an access token, a refresh token, and a millisecond
/// expiry), so Claude Code and Kare share one credential store. Refresh replaces
/// the stored tokens atomically and keeps the file owner-only. Token values never
/// appear in exception messages or logs.
/// </summary>
public sealed class ClaudeOAuthTokenProvider
{
    private const string DefaultTokenEndpoint = "https://platform.claude.com/v1/oauth/token";
    private const string ClaudeCodeClientId = "9d1c250a-e61b-44d9-88ed-5944d1962f5e";
    private static readonly TimeSpan RefreshMargin = TimeSpan.FromMinutes(2);

    private readonly string _credentialsPath;
    private readonly HttpClient _http;
    private readonly string _tokenEndpoint;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private string? _cachedToken;
    private DateTimeOffset _cachedExpiresAt;

    public ClaudeOAuthTokenProvider(
        string credentialsPath,
        HttpClient? httpClient = null,
        string? tokenEndpoint = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(credentialsPath);
        _credentialsPath = credentialsPath;
        _http = httpClient ?? new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        _tokenEndpoint = string.IsNullOrWhiteSpace(tokenEndpoint)
            ? DefaultTokenEndpoint
            : tokenEndpoint;
    }

    /// <summary>Returns a current access token, refreshing and storing it first when needed.</summary>
    public async Task<string> GetTokenAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_cachedToken is not null &&
                _cachedExpiresAt > DateTimeOffset.UtcNow.Add(RefreshMargin))
            {
                return _cachedToken;
            }

            var credentials = await ReadCredentialsAsync(cancellationToken).ConfigureAwait(false);
            if (credentials.ExpiresAt > DateTimeOffset.UtcNow.Add(RefreshMargin))
            {
                _cachedToken = credentials.AccessToken;
                _cachedExpiresAt = credentials.ExpiresAt;
                return credentials.AccessToken;
            }

            var refreshed = await RefreshAsync(credentials.RefreshToken, cancellationToken)
                .ConfigureAwait(false);
            await WriteCredentialsAsync(credentials.Root, refreshed, cancellationToken)
                .ConfigureAwait(false);
            _cachedToken = refreshed.AccessToken;
            _cachedExpiresAt = refreshed.ExpiresAt;
            return refreshed.AccessToken;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<StoredCredentials> ReadCredentialsAsync(CancellationToken cancellationToken)
    {
        string json;
        try
        {
            json = await File.ReadAllTextAsync(_credentialsPath, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new CloudInferenceException(
                "Claude OAuth credentials file could not be read. Sign in with Claude Code on this device first.",
                ex);
        }

        JsonObject root;
        try
        {
            root = JsonNode.Parse(json) as JsonObject
                ?? throw new JsonException("Credentials root is not a JSON object.");
        }
        catch (JsonException ex)
        {
            throw new CloudInferenceException("Claude OAuth credentials file is not valid JSON.", ex);
        }

        if (root["claudeAiOauth"] is not JsonObject oauth ||
            oauth["accessToken"]?.GetValue<string>() is not { Length: > 0 } accessToken ||
            oauth["refreshToken"]?.GetValue<string>() is not { Length: > 0 } refreshToken)
        {
            throw new CloudInferenceException(
                "Claude OAuth credentials file has no claudeAiOauth token pair. Sign in with Claude Code on this device first.");
        }

        var expiresAt = oauth["expiresAt"]?.GetValue<long>() ?? 0;
        return new StoredCredentials(
            root,
            accessToken,
            refreshToken,
            DateTimeOffset.FromUnixTimeMilliseconds(expiresAt));
    }

    private async Task<RefreshedToken> RefreshAsync(
        string refreshToken,
        CancellationToken cancellationToken)
    {
        using var content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = refreshToken,
            ["client_id"] = ClaudeCodeClientId,
        });

        HttpResponseMessage response;
        try
        {
            response = await _http.PostAsync(_tokenEndpoint, content, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            throw new CloudInferenceException("Claude OAuth token refresh request failed.", ex);
        }

        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                throw new CloudInferenceException(
                    $"Claude OAuth token refresh failed with status {(int)response.StatusCode}. Sign in with Claude Code on this device to restore it.");
            }

            var json = await response.Content.ReadAsStringAsync(cancellationToken)
                .ConfigureAwait(false);
            try
            {
                using var document = JsonDocument.Parse(json);
                var root = document.RootElement;
                var accessToken = root.GetProperty("access_token").GetString();
                if (string.IsNullOrWhiteSpace(accessToken))
                {
                    throw new CloudInferenceException("Claude OAuth token refresh returned an empty access token.");
                }

                var expiresInSeconds = root.TryGetProperty("expires_in", out var expiresIn) &&
                    expiresIn.TryGetInt64(out var seconds)
                        ? seconds
                        : 3600;
                var rotatedRefreshToken = root.TryGetProperty("refresh_token", out var rotated) &&
                    rotated.GetString() is { Length: > 0 } rotatedValue
                        ? rotatedValue
                        : null;
                return new RefreshedToken(
                    accessToken,
                    DateTimeOffset.UtcNow.AddSeconds(expiresInSeconds),
                    rotatedRefreshToken);
            }
            catch (JsonException ex)
            {
                throw new CloudInferenceException("Claude OAuth token refresh returned an invalid response.", ex);
            }
        }
    }

    private async Task WriteCredentialsAsync(
        JsonObject root,
        RefreshedToken refreshed,
        CancellationToken cancellationToken)
    {
        var oauth = (JsonObject)root["claudeAiOauth"]!;
        oauth["accessToken"] = refreshed.AccessToken;
        oauth["expiresAt"] = refreshed.ExpiresAt.ToUnixTimeMilliseconds();
        if (refreshed.RefreshToken is not null)
        {
            oauth["refreshToken"] = refreshed.RefreshToken;
        }

        var directory = Path.GetDirectoryName(Path.GetFullPath(_credentialsPath))!;
        var temporaryPath = Path.Combine(
            directory,
            $".{Path.GetFileName(_credentialsPath)}.tmp-{Guid.NewGuid():N}");
        try
        {
            await File.WriteAllTextAsync(
                temporaryPath,
                root.ToJsonString(),
                cancellationToken).ConfigureAwait(false);
            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(
                    temporaryPath,
                    UnixFileMode.UserRead | UnixFileMode.UserWrite);
            }

            File.Move(temporaryPath, _credentialsPath, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new CloudInferenceException(
                "Refreshed Claude OAuth credentials could not be stored back to the credentials file.",
                ex);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    private sealed record StoredCredentials(
        JsonObject Root,
        string AccessToken,
        string RefreshToken,
        DateTimeOffset ExpiresAt);

    private sealed record RefreshedToken(
        string AccessToken,
        DateTimeOffset ExpiresAt,
        string? RefreshToken);
}
