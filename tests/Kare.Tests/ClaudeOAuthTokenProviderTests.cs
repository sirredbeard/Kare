using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Kare.Cloud.Copilot;
using Kare.Core;
using Xunit;

namespace Kare.Tests;

public sealed class ClaudeOAuthTokenProviderTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        $"kare-claude-oauth-{Guid.NewGuid():N}");

    public ClaudeOAuthTokenProviderTests()
    {
        Directory.CreateDirectory(_directory);
    }

    public void Dispose()
    {
        Directory.Delete(_directory, recursive: true);
    }

    [Fact]
    public async Task FreshStoredTokenIsReturnedWithoutCallingTheTokenEndpoint()
    {
        var path = WriteCredentials(
            "stored-access",
            "stored-refresh",
            DateTimeOffset.UtcNow.AddHours(1));
        var handler = new StubHandler(_ => throw new InvalidOperationException("No HTTP call expected."));
        var provider = new ClaudeOAuthTokenProvider(path, new HttpClient(handler));

        var token = await provider.GetTokenAsync(TestContext.Current.CancellationToken);

        Assert.Equal("stored-access", token);
    }

    [Fact]
    public async Task ExpiredStoredTokenIsRefreshedAndStoredBack()
    {
        var path = WriteCredentials(
            "expired-access",
            "stored-refresh",
            DateTimeOffset.UtcNow.AddMinutes(-5));
        var handler = new StubHandler(request =>
        {
            Assert.Equal("https://platform.claude.com/v1/oauth/token", request.RequestUri!.ToString());
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    """{"access_token":"fresh-access","expires_in":3600,"refresh_token":"rotated-refresh"}""",
                    Encoding.UTF8,
                    "application/json"),
            };
        });
        var provider = new ClaudeOAuthTokenProvider(path, new HttpClient(handler));

        var token = await provider.GetTokenAsync(TestContext.Current.CancellationToken);

        Assert.Equal("fresh-access", token);
        var stored = JsonNode.Parse(await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken))!["claudeAiOauth"]!;
        Assert.Equal("fresh-access", stored["accessToken"]!.GetValue<string>());
        Assert.Equal("rotated-refresh", stored["refreshToken"]!.GetValue<string>());
        Assert.True(
            DateTimeOffset.FromUnixTimeMilliseconds(stored["expiresAt"]!.GetValue<long>()) >
            DateTimeOffset.UtcNow.AddMinutes(30));
    }

    [Fact]
    public async Task RefreshFailureSurfacesAsCloudInferenceException()
    {
        var path = WriteCredentials(
            "expired-access",
            "stored-refresh",
            DateTimeOffset.UtcNow.AddMinutes(-5));
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.BadRequest));
        var provider = new ClaudeOAuthTokenProvider(path, new HttpClient(handler));

        await Assert.ThrowsAsync<CloudInferenceException>(() => provider.GetTokenAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task MissingCredentialsFileSurfacesAsCloudInferenceException()
    {
        var provider = new ClaudeOAuthTokenProvider(
            Path.Combine(_directory, "absent.json"),
            new HttpClient(new StubHandler(_ => throw new InvalidOperationException())));

        await Assert.ThrowsAsync<CloudInferenceException>(() => provider.GetTokenAsync(TestContext.Current.CancellationToken));
    }

    private string WriteCredentials(string accessToken, string refreshToken, DateTimeOffset expiresAt)
    {
        var path = Path.Combine(_directory, "credentials.json");
        var json = new JsonObject
        {
            ["claudeAiOauth"] = new JsonObject
            {
                ["accessToken"] = accessToken,
                ["refreshToken"] = refreshToken,
                ["expiresAt"] = expiresAt.ToUnixTimeMilliseconds(),
                ["scopes"] = new JsonArray("user:inference"),
            },
        };
        File.WriteAllText(path, json.ToJsonString());
        return path;
    }

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) => Task.FromResult(respond(request));
    }
}
