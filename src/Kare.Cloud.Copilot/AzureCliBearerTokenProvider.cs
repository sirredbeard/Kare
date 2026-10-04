using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Kare.Core;

namespace Kare.Cloud.Copilot;

internal sealed class AzureCliBearerTokenProvider
{
    private readonly TimeSpan _timeout;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<string, CachedToken> _tokens = new(StringComparer.Ordinal);

    public AzureCliBearerTokenProvider(TimeSpan timeout)
    {
        _timeout = timeout;
    }

    public async Task<string> GetTokenAsync(string scope)
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_tokens.TryGetValue(scope, out var cached) &&
                cached.ExpiresAt > DateTimeOffset.UtcNow.AddMinutes(2))
            {
                return cached.Value;
            }

            var token = await AcquireAsync(scope).ConfigureAwait(false);
            _tokens[scope] = token;
            return token.Value;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<CachedToken> AcquireAsync(string scope)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "az",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            },
        };
        process.StartInfo.ArgumentList.Add("account");
        process.StartInfo.ArgumentList.Add("get-access-token");
        process.StartInfo.ArgumentList.Add("--scope");
        process.StartInfo.ArgumentList.Add(scope);
        process.StartInfo.ArgumentList.Add("--output");
        process.StartInfo.ArgumentList.Add("json");
        process.StartInfo.ArgumentList.Add("--only-show-errors");

        try
        {
            if (!process.Start())
            {
                throw new CloudInferenceException("Azure CLI token acquisition could not start.");
            }
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            throw new CloudInferenceException("Azure CLI token acquisition is unavailable.", ex);
        }

        var standardOutput = process.StandardOutput.ReadToEndAsync();
        var standardError = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(_timeout);
        try
        {
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            throw new CloudInferenceException("Azure CLI token acquisition timed out.");
        }

        _ = await standardError.ConfigureAwait(false);
        var json = await standardOutput.ConfigureAwait(false);
        if (process.ExitCode != 0)
        {
            throw new CloudInferenceException(
                $"Azure CLI token acquisition failed with exit code {process.ExitCode}.");
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            var value = root.GetProperty("accessToken").GetString();
            if (string.IsNullOrWhiteSpace(value))
            {
                throw new CloudInferenceException("Azure CLI returned an empty access token.");
            }

            return new CachedToken(value, ReadExpiration(root));
        }
        catch (JsonException ex)
        {
            throw new CloudInferenceException("Azure CLI returned an invalid token response.", ex);
        }
    }

    private static DateTimeOffset ReadExpiration(JsonElement root)
    {
        if (root.TryGetProperty("expires_on", out var unixExpiration))
        {
            if (unixExpiration.ValueKind == JsonValueKind.Number && unixExpiration.TryGetInt64(out var seconds))
            {
                return DateTimeOffset.FromUnixTimeSeconds(seconds);
            }

            if (unixExpiration.ValueKind == JsonValueKind.String &&
                long.TryParse(unixExpiration.GetString(), NumberStyles.None, CultureInfo.InvariantCulture, out seconds))
            {
                return DateTimeOffset.FromUnixTimeSeconds(seconds);
            }
        }

        if (root.TryGetProperty("expiresOn", out var textExpiration) &&
            DateTimeOffset.TryParse(
                textExpiration.GetString(),
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeLocal,
                out var parsed))
        {
            return parsed;
        }

        return DateTimeOffset.UtcNow.AddMinutes(5);
    }

    private sealed record CachedToken(string Value, DateTimeOffset ExpiresAt);
}
