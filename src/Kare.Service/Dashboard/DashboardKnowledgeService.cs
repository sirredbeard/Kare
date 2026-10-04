using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.AI;

namespace Kare.Service.Dashboard;

/// <summary>Manages authoritative sources, local skills, and Streamable HTTP MCP servers.</summary>
public interface IDashboardKnowledgeService
{
    string ContextVersion { get; }

    Task<DashboardMetrics.RoutingDecisionUrl> AddSourceAsync(
        CreateAuthoritativeSourceRequest request,
        CancellationToken cancellationToken);

    Task<bool> RemoveSourceAsync(string id, CancellationToken cancellationToken);

    Task<DashboardMetrics.SkillInfo> AddSkillAsync(
        CreateDashboardSkillRequest request,
        CancellationToken cancellationToken);

    Task<bool> RemoveSkillAsync(string name, CancellationToken cancellationToken);

    Task<DashboardMetrics.McpServerInfo> AddMcpServerAsync(
        CreateDashboardMcpServerRequest request,
        CancellationToken cancellationToken);

    Task<bool> RemoveMcpServerAsync(string name, CancellationToken cancellationToken);

    Task<IReadOnlyList<ChatMessage>> AddLocalContextAsync(
        IReadOnlyList<ChatMessage> messages,
        CancellationToken cancellationToken);
}

/// <summary>Payload for registering an authoritative URL pattern.</summary>
public sealed record CreateAuthoritativeSourceRequest(string Pattern, bool Enabled = true);

/// <summary>Payload for registering an explicit local skill file or public HTTPS URL.</summary>
public sealed record CreateDashboardSkillRequest(
    string Name,
    string Path,
    string Description,
    bool Enabled = true);

/// <summary>Payload for registering a Streamable HTTP MCP endpoint.</summary>
public sealed record CreateDashboardMcpServerRequest(string Name, string Endpoint);

/// <summary>Bounded process-local content registry with protected configuration persistence.</summary>
public sealed partial class DashboardKnowledgeService : BackgroundService, IDashboardKnowledgeService
{
    private const int MaxSources = 50;
    private const int MaxSkills = 100;
    private const int MaxMcpServers = 50;
    private const int MaxPagesPerSource = 20;
    private const int MaxPageBytes = 512 * 1024;
    private const int MaxSourceCharacters = 256 * 1024;
    private const int MaxSkillBytes = 256 * 1024;
    private const int MaxInjectedCharacters = 12_000;
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromMinutes(15);

    private readonly Lock _sync = new();
    private readonly SemaphoreSlim _persistGate = new(1, 1);
    private readonly IDashboardMetricsCollector _collector;
    private readonly HttpClient _httpClient;
    private readonly ILogger<DashboardKnowledgeService> _logger;
    private readonly string _statePath;
    private readonly Dictionary<string, SourceRegistration> _sources = new(StringComparer.Ordinal);
    private readonly Dictionary<string, SkillRegistration> _skills = new(StringComparer.Ordinal);
    private readonly Dictionary<string, McpRegistration> _mcpServers = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _sourceContent = new(StringComparer.Ordinal);
    private readonly Dictionary<string, RemoteSkillContent> _remoteSkillContent = new(StringComparer.Ordinal);
    private long _contextVersion;

    public string ContextVersion =>
        Volatile.Read(ref _contextVersion).ToString(System.Globalization.CultureInfo.InvariantCulture);

    public DashboardKnowledgeService(
        IDashboardMetricsCollector collector,
        IHttpClientFactory httpClientFactory,
        ILogger<DashboardKnowledgeService> logger)
        : this(
            collector,
            httpClientFactory,
            logger,
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "kare",
                "dashboard-registry.json"))
    {
    }

    internal DashboardKnowledgeService(
        IDashboardMetricsCollector collector,
        IHttpClientFactory httpClientFactory,
        ILogger<DashboardKnowledgeService> logger,
        string statePath)
    {
        ArgumentNullException.ThrowIfNull(collector);
        ArgumentNullException.ThrowIfNull(httpClientFactory);
        ArgumentNullException.ThrowIfNull(logger);
        _collector = collector;
        _httpClient = httpClientFactory.CreateClient(nameof(DashboardKnowledgeService));
        _logger = logger;
        _statePath = statePath;
    }

    public async Task<DashboardMetrics.RoutingDecisionUrl> AddSourceAsync(
        CreateAuthoritativeSourceRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ValidateSourcePattern(request.Pattern);

        var now = DateTime.UtcNow;
        var registration = new SourceRegistration(
            Guid.NewGuid().ToString("N"),
            request.Pattern.Trim(),
            request.Enabled,
            now,
            now);

        lock (_sync)
        {
            if (_sources.Count >= MaxSources)
            {
                throw new InvalidOperationException($"At most {MaxSources} authoritative sources may be registered.");
            }

            _sources[registration.Id] = registration;
        }
        Interlocked.Increment(ref _contextVersion);

        var metric = ToPendingMetric(registration);
        _collector.RegisterRoutingDecisionUrl(metric);
        await PersistAsync(cancellationToken).ConfigureAwait(false);

        if (registration.Enabled)
        {
            await CrawlSourceAsync(registration, cancellationToken).ConfigureAwait(false);
            return _collector.GetRoutingDecisionUrls().Single(item => item.Id == registration.Id);
        }

        return metric;
    }

    public async Task<bool> RemoveSourceAsync(string id, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        bool removed;
        lock (_sync)
        {
            removed = _sources.Remove(id);
            _sourceContent.Remove(id);
        }

        if (removed)
        {
            Interlocked.Increment(ref _contextVersion);
            _collector.RemoveRoutingDecisionUrl(id);
            await PersistAsync(cancellationToken).ConfigureAwait(false);
        }

        return removed;
    }

    public async Task<DashboardMetrics.SkillInfo> AddSkillAsync(
        CreateDashboardSkillRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.Name) || string.IsNullOrWhiteSpace(request.Path))
        {
            throw new ArgumentException("Skill name and file path or URL are required.");
        }

        var location = request.Path.Trim();
        var isRemote = TryGetRemoteSkillUri(location, out var remoteUri);
        if (isRemote)
        {
            location = remoteUri.AbsoluteUri;
        }
        else if (Path.IsPathRooted(location))
        {
            location = Path.GetFullPath(location);
        }
        else
        {
            throw new ArgumentException("Skill location must be an absolute file path or public HTTPS URL.");
        }

        var registration = new SkillRegistration(
            request.Name.Trim(),
            location,
            request.Description.Trim(),
            request.Enabled);

        lock (_sync)
        {
            if (!_skills.ContainsKey(registration.Name) && _skills.Count >= MaxSkills)
            {
                throw new InvalidOperationException($"At most {MaxSkills} skills may be registered.");
            }

            _skills[registration.Name] = registration;
        }
        Interlocked.Increment(ref _contextVersion);

        await PersistAsync(cancellationToken).ConfigureAwait(false);

        if (isRemote && registration.Enabled)
        {
            return await RefreshRemoteSkillAsync(registration, remoteUri, cancellationToken)
                .ConfigureAwait(false);
        }

        var metric = InspectSkill(registration);
        _collector.RegisterSkill(metric);
        return metric;
    }

    public async Task<bool> RemoveSkillAsync(string name, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        bool removed;
        lock (_sync)
        {
            removed = _skills.Remove(name);
            _remoteSkillContent.Remove(name);
        }

        if (removed)
        {
            Interlocked.Increment(ref _contextVersion);
            _collector.RemoveSkill(name);
            await PersistAsync(cancellationToken).ConfigureAwait(false);
        }

        return removed;
    }

    public async Task<DashboardMetrics.McpServerInfo> AddMcpServerAsync(
        CreateDashboardMcpServerRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.Name) ||
            !Uri.TryCreate(request.Endpoint, UriKind.Absolute, out var endpoint) ||
            (endpoint.Scheme != Uri.UriSchemeHttp && endpoint.Scheme != Uri.UriSchemeHttps))
        {
            throw new ArgumentException("MCP name and an absolute HTTP or HTTPS endpoint are required.");
        }

        var registration = new McpRegistration(request.Name.Trim(), endpoint);
        lock (_sync)
        {
            if (!_mcpServers.ContainsKey(registration.Name) && _mcpServers.Count >= MaxMcpServers)
            {
                throw new InvalidOperationException($"At most {MaxMcpServers} MCP servers may be registered.");
            }

            _mcpServers[registration.Name] = registration;
        }

        await PersistAsync(cancellationToken).ConfigureAwait(false);
        return await ProbeMcpServerAsync(registration, cancellationToken).ConfigureAwait(false);
    }

    public async Task<bool> RemoveMcpServerAsync(string name, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        bool removed;
        lock (_sync)
        {
            removed = _mcpServers.Remove(name);
        }

        if (removed)
        {
            _collector.RemoveMcpServer(name);
            await PersistAsync(cancellationToken).ConfigureAwait(false);
        }

        return removed;
    }

    public async Task<IReadOnlyList<ChatMessage>> AddLocalContextAsync(
        IReadOnlyList<ChatMessage> messages,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(messages);
        var contentBuffer = new StringBuilder(MaxInjectedCharacters);

        SourceRegistration[] sources;
        SkillRegistration[] skills;
        lock (_sync)
        {
            sources = [.. _sources.Values.Where(static item => item.Enabled)];
            skills = [.. _skills.Values.Where(static item => item.Enabled)];
        }

        foreach (var source in sources)
        {
            string? sourceContent;
            lock (_sync)
            {
                _sourceContent.TryGetValue(source.Id, out sourceContent);
            }

            AppendBounded(contentBuffer, sourceContent, MaxInjectedCharacters);
        }

        foreach (var skill in skills)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var metric = InspectSkill(skill);
            _collector.RegisterSkill(metric);
            if (metric.Status != "ready")
            {
                continue;
            }

            string skillContent;
            if (TryGetRemoteSkillUri(skill.Path, out _))
            {
                lock (_sync)
                {
                    skillContent = _remoteSkillContent[skill.Name].Content;
                }
            }
            else
            {
                skillContent = await ReadBoundedFileAsync(
                    skill.Path,
                    MaxSkillBytes,
                    cancellationToken).ConfigureAwait(false);
            }

            AppendBounded(
                contentBuffer,
                $"\nSkill: {skill.Name}\nLocation: {skill.Path}\n{skillContent}\n",
                MaxInjectedCharacters);
        }

        if (contentBuffer.Length == 0)
        {
            return messages;
        }

        var context = new StringBuilder(MaxInjectedCharacters);
        context.AppendLine(
            "The following Kare-managed sources and skills are authoritative for this request. " +
            "Prefer them over model memory and cite source URLs when applicable.");
        AppendBounded(context, contentBuffer.ToString(), MaxInjectedCharacters);

        return
        [
            new ChatMessage(ChatRole.System, context.ToString()),
            .. messages,
        ];
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await LoadAsync(stoppingToken).ConfigureAwait(false);

        while (!stoppingToken.IsCancellationRequested)
        {
            await RefreshAsync(stoppingToken).ConfigureAwait(false);
            await Task.Delay(RefreshInterval, stoppingToken).ConfigureAwait(false);
        }
    }

    private async Task RefreshAsync(CancellationToken cancellationToken)
    {
        SourceRegistration[] sources;
        SkillRegistration[] skills;
        McpRegistration[] mcpServers;
        lock (_sync)
        {
            sources = [.. _sources.Values.Where(static item => item.Enabled)];
            skills = [.. _skills.Values];
            mcpServers = [.. _mcpServers.Values];
        }

        foreach (var source in sources)
        {
            await CrawlSourceAsync(source, cancellationToken).ConfigureAwait(false);
        }

        foreach (var skill in skills)
        {
            if (skill.Enabled && TryGetRemoteSkillUri(skill.Path, out var remoteUri))
            {
                await RefreshRemoteSkillAsync(skill, remoteUri, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                _collector.RegisterSkill(InspectSkill(skill));
            }
        }
        if (skills.Length > 0)
        {
            Interlocked.Increment(ref _contextVersion);
        }

        foreach (var server in mcpServers)
        {
            await ProbeMcpServerAsync(server, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task CrawlSourceAsync(
        SourceRegistration source,
        CancellationToken cancellationToken)
    {
        var visited = new HashSet<string>(StringComparer.Ordinal);
        var pending = new Queue<Uri>();
        var seed = CreateSeedUri(source.Pattern);
        pending.Enqueue(seed);
        var content = new StringBuilder();

        try
        {
            while (pending.Count > 0 &&
                   visited.Count < MaxPagesPerSource &&
                   content.Length < MaxSourceCharacters)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var uri = pending.Dequeue();
                if (!visited.Add(uri.AbsoluteUri))
                {
                    continue;
                }

                await ValidatePublicAddressAsync(uri, cancellationToken).ConfigureAwait(false);
                using var response = await _httpClient
                    .GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                    .ConfigureAwait(false);
                response.EnsureSuccessStatusCode();

                var mediaType = response.Content.Headers.ContentType?.MediaType ?? string.Empty;
                if (!mediaType.StartsWith("text/", StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(mediaType, "application/json", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var body = await ReadBoundedAsync(
                    response.Content,
                    MaxPageBytes,
                    cancellationToken).ConfigureAwait(false);
                content.AppendLine($"\nSource URL: {uri}");
                content.AppendLine(ToPlainText(body));

                if (!mediaType.Equals("text/html", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                foreach (Match match in LinkRegex().Matches(body))
                {
                    if (!Uri.TryCreate(uri, match.Groups["url"].Value, out var linked) ||
                        !SameOrigin(seed, linked) ||
                        !MatchesPattern(source.Pattern, linked.AbsoluteUri))
                    {
                        continue;
                    }

                    pending.Enqueue(linked);
                }
            }

            var value = content.Length > MaxSourceCharacters
                ? content.ToString(0, MaxSourceCharacters)
                : content.ToString();
            lock (_sync)
            {
                _sourceContent[source.Id] = value;
            }
            Interlocked.Increment(ref _contextVersion);

            _collector.RegisterRoutingDecisionUrl(new DashboardMetrics.RoutingDecisionUrl(
                source.Id,
                source.Pattern,
                source.Enabled,
                source.CreatedAt,
                source.LastModifiedAt,
                DateTime.UtcNow,
                visited.Count,
                value.Length,
                "ready",
                Error: null));
        }
        catch (Exception ex) when (
            !cancellationToken.IsCancellationRequested &&
            ex is HttpRequestException or IOException or InvalidOperationException or
                System.Net.Sockets.SocketException)
        {
            _logger.LogWarning(ex, "Authoritative source crawl failed for source {SourceId}.", source.Id);
            _collector.RegisterRoutingDecisionUrl(new DashboardMetrics.RoutingDecisionUrl(
                source.Id,
                source.Pattern,
                source.Enabled,
                source.CreatedAt,
                source.LastModifiedAt,
                DateTime.UtcNow,
                visited.Count,
                content.Length,
                "failed",
                ex.Message));
        }
    }

    private async Task<DashboardMetrics.McpServerInfo> ProbeMcpServerAsync(
        McpRegistration server,
        CancellationToken cancellationToken)
    {
        var now = DateTime.UtcNow;
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, server.Endpoint)
            {
                Content = new StringContent(
                    """
                    {"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-06-18","capabilities":{},"clientInfo":{"name":"Kare","version":"1"}}}
                    """,
                    Encoding.UTF8,
                    "application/json"),
            };
            request.Headers.Accept.ParseAdd("application/json");
            request.Headers.Accept.ParseAdd("text/event-stream");

            using var response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            var body = await ReadBoundedAsync(
                response.Content,
                MaxPageBytes,
                cancellationToken).ConfigureAwait(false);
            if (string.Equals(
                    response.Content.Headers.ContentType?.MediaType,
                    "text/event-stream",
                    StringComparison.OrdinalIgnoreCase))
            {
                var dataLine = body
                    .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .FirstOrDefault(static line => line.StartsWith("data:", StringComparison.Ordinal));
                if (dataLine is null)
                {
                    throw new JsonException("MCP server returned no JSON data event.");
                }

                body = dataLine["data:".Length..].Trim();
            }

            using var document = JsonDocument.Parse(body);

            var capabilities = document.RootElement.TryGetProperty("result", out var result) &&
                result.TryGetProperty("capabilities", out var capabilityElement)
                    ? capabilityElement.EnumerateObject().Select(static item => item.Name).ToArray()
                    : [];

            var metric = new DashboardMetrics.McpServerInfo(
                server.Name,
                server.Endpoint.ToString(),
                capabilities,
                Connected: true,
                LastConnectedAt: now,
                LastCheckedAt: now,
                Error: null);
            _collector.RegisterMcpServer(metric);
            return metric;
        }
        catch (Exception ex) when (
            !cancellationToken.IsCancellationRequested &&
            ex is HttpRequestException or IOException or JsonException or TaskCanceledException)
        {
            _logger.LogWarning(ex, "MCP probe failed for server {ServerName}.", server.Name);
            var metric = new DashboardMetrics.McpServerInfo(
                server.Name,
                server.Endpoint.ToString(),
                [],
                Connected: false,
                LastConnectedAt: null,
                LastCheckedAt: now,
                ex.Message);
            _collector.RegisterMcpServer(metric);
            return metric;
        }
    }

    private async Task LoadAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_statePath))
        {
            return;
        }

        await using var stream = File.OpenRead(_statePath);
        var state = await JsonSerializer.DeserializeAsync(
            stream,
            DashboardJsonContext.Default.DashboardRegistryState,
            cancellationToken).ConfigureAwait(false);
        if (state is null)
        {
            return;
        }

        lock (_sync)
        {
            foreach (var source in state.Sources)
            {
                _sources[source.Id] = source;
                _collector.RegisterRoutingDecisionUrl(ToPendingMetric(source));
            }

            foreach (var skill in state.Skills)
            {
                _skills[skill.Name] = skill;
                _collector.RegisterSkill(InspectSkill(skill));
            }

            foreach (var server in state.McpServers)
            {
                _mcpServers[server.Name] = server;
                _collector.RegisterMcpServer(new DashboardMetrics.McpServerInfo(
                    server.Name,
                    server.Endpoint.ToString(),
                    [],
                    Connected: false,
                    LastConnectedAt: null,
                    LastCheckedAt: null,
                    Error: "Not probed yet."));
            }
        }
    }

    private async Task PersistAsync(CancellationToken cancellationToken)
    {
        await _persistGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            DashboardRegistryState state;
            lock (_sync)
            {
                state = new DashboardRegistryState(
                    [.. _sources.Values],
                    [.. _skills.Values],
                    [.. _mcpServers.Values]);
            }

            var directory = Path.GetDirectoryName(_statePath)!;
            Directory.CreateDirectory(directory);
            var temporary = _statePath + ".tmp";
            await using (var stream = File.Create(temporary))
            {
                await JsonSerializer.SerializeAsync(
                    stream,
                    state,
                    DashboardJsonContext.Default.DashboardRegistryState,
                    cancellationToken).ConfigureAwait(false);
            }

            File.Move(temporary, _statePath, overwrite: true);
        }
        finally
        {
            _persistGate.Release();
        }
    }

    private static DashboardMetrics.RoutingDecisionUrl ToPendingMetric(SourceRegistration source) =>
        new(
            source.Id,
            source.Pattern,
            source.Enabled,
            source.CreatedAt,
            source.LastModifiedAt,
            LastCrawledAt: null,
            PageCount: 0,
            ContentCharacters: 0,
            source.Enabled ? "pending" : "disabled",
            Error: null);

    private DashboardMetrics.SkillInfo InspectSkill(SkillRegistration skill)
    {
        if (TryGetRemoteSkillUri(skill.Path, out _))
        {
            RemoteSkillContent? content;
            lock (_sync)
            {
                _remoteSkillContent.TryGetValue(skill.Name, out content);
            }

            return new DashboardMetrics.SkillInfo(
                skill.Name,
                skill.Path,
                skill.Description,
                skill.Enabled,
                content?.SizeBytes ?? 0,
                content?.FetchedAt ?? DateTime.MinValue,
                !skill.Enabled ? "disabled" : content is null ? "pending" : "ready");
        }

        var file = new FileInfo(skill.Path);
        return new DashboardMetrics.SkillInfo(
            skill.Name,
            skill.Path,
            skill.Description,
            skill.Enabled,
            file.Exists ? file.Length : 0,
            file.Exists ? file.LastWriteTimeUtc : DateTime.MinValue,
            !file.Exists
                ? "missing"
                : file.Length > MaxSkillBytes
                    ? "too-large"
                    : "ready");
    }

    private async Task<DashboardMetrics.SkillInfo> RefreshRemoteSkillAsync(
        SkillRegistration skill,
        Uri uri,
        CancellationToken cancellationToken)
    {
        try
        {
            await ValidatePublicAddressAsync(uri, cancellationToken).ConfigureAwait(false);
            using var response = await _httpClient
                .GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);
            response.EnsureSuccessStatusCode();

            var mediaType = response.Content.Headers.ContentType?.MediaType ?? string.Empty;
            if (!mediaType.StartsWith("text/", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(mediaType, "application/json", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("Remote skills must return text or JSON content.");
            }

            var body = await ReadBoundedAsync(
                response.Content,
                MaxSkillBytes,
                cancellationToken).ConfigureAwait(false);
            var content = string.Equals(mediaType, "text/html", StringComparison.OrdinalIgnoreCase)
                ? ToPlainText(body)
                : body;
            var fetched = new RemoteSkillContent(
                content,
                Encoding.UTF8.GetByteCount(content),
                DateTime.UtcNow);

            bool changed;
            lock (_sync)
            {
                changed = !_remoteSkillContent.TryGetValue(skill.Name, out var previous) ||
                    !string.Equals(previous.Content, fetched.Content, StringComparison.Ordinal);
                _remoteSkillContent[skill.Name] = fetched;
            }

            if (changed)
            {
                Interlocked.Increment(ref _contextVersion);
            }

            var metric = InspectSkill(skill);
            _collector.RegisterSkill(metric);
            return metric;
        }
        catch (Exception ex) when (
            !cancellationToken.IsCancellationRequested &&
            ex is HttpRequestException or IOException or InvalidOperationException or
                System.Net.Sockets.SocketException)
        {
            _logger.LogWarning(ex, "Remote skill refresh failed for skill {SkillName}.", skill.Name);
            var current = InspectSkill(skill);
            var metric = current with { Status = "failed" };
            _collector.RegisterSkill(metric);
            return metric;
        }
    }

    private static void ValidateSourcePattern(string pattern)
    {
        if (string.IsNullOrWhiteSpace(pattern) ||
            !Uri.TryCreate(pattern.Replace("*", "wildcard", StringComparison.Ordinal), UriKind.Absolute, out var uri) ||
            uri.Scheme != Uri.UriSchemeHttps ||
            uri.Host.Contains('*', StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "Authoritative source patterns must be absolute HTTPS URLs with wildcards only in the path or query.");
        }
    }

    private static Uri CreateSeedUri(string pattern)
    {
        var wildcard = pattern.IndexOf('*', StringComparison.Ordinal);
        if (wildcard < 0)
        {
            return new Uri(pattern);
        }

        var prefix = pattern[..wildcard];
        var slash = prefix.LastIndexOf('/');
        return new Uri(prefix[..(slash + 1)]);
    }

    private static async Task ValidatePublicAddressAsync(Uri uri, CancellationToken cancellationToken)
    {
        var addresses = await Dns.GetHostAddressesAsync(uri.Host, cancellationToken).ConfigureAwait(false);
        if (addresses.Length == 0 || addresses.Any(IsPrivateAddress))
        {
            throw new InvalidOperationException("Authoritative sources must resolve only to public addresses.");
        }
    }

    private static bool IsPrivateAddress(IPAddress address)
    {
        if (IPAddress.IsLoopback(address) || address.IsIPv6LinkLocal || address.IsIPv6SiteLocal)
        {
            return true;
        }

        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }

        if (address.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork)
        {
            return false;
        }

        var bytes = address.GetAddressBytes();
        return bytes[0] == 10 ||
            bytes[0] == 127 ||
            (bytes[0] == 169 && bytes[1] == 254) ||
            (bytes[0] == 172 && bytes[1] is >= 16 and <= 31) ||
            (bytes[0] == 192 && bytes[1] == 168);
    }

    private static async Task<string> ReadBoundedAsync(
        HttpContent content,
        int maximumBytes,
        CancellationToken cancellationToken)
    {
        if (content.Headers.ContentLength is > 0 &&
            content.Headers.ContentLength > maximumBytes)
        {
            throw new InvalidOperationException($"Content exceeds {maximumBytes} bytes.");
        }

        await using var stream = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        return await ReadBoundedStreamAsync(stream, maximumBytes, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<string> ReadBoundedFileAsync(
        string path,
        int maximumBytes,
        CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 16 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        return await ReadBoundedStreamAsync(stream, maximumBytes, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<string> ReadBoundedStreamAsync(
        Stream stream,
        int maximumBytes,
        CancellationToken cancellationToken)
    {
        using var memory = new MemoryStream();
        var buffer = new byte[16 * 1024];
        while (memory.Length <= maximumBytes)
        {
            var read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                return Encoding.UTF8.GetString(memory.GetBuffer(), 0, (int)memory.Length);
            }

            await memory.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
        }

        throw new InvalidOperationException($"Content exceeds {maximumBytes} bytes.");
    }

    private static bool TryGetRemoteSkillUri(string value, out Uri uri)
    {
        if (Uri.TryCreate(value, UriKind.Absolute, out var candidate) &&
            candidate.Scheme == Uri.UriSchemeHttps &&
            string.IsNullOrEmpty(candidate.UserInfo))
        {
            uri = candidate;
            return true;
        }

        uri = null!;
        return false;
    }

    private static string ToPlainText(string value)
    {
        var withoutScripts = ScriptRegex().Replace(value, " ");
        var withoutTags = TagRegex().Replace(withoutScripts, " ");
        return WhitespaceRegex().Replace(WebUtility.HtmlDecode(withoutTags), " ").Trim();
    }

    private static bool MatchesPattern(string pattern, string value)
    {
        var expression = "^" + Regex.Escape(pattern).Replace("\\*", ".*", StringComparison.Ordinal) + "$";
        return Regex.IsMatch(value, expression, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }

    private static bool SameOrigin(Uri left, Uri right) =>
        string.Equals(left.Scheme, right.Scheme, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(left.Host, right.Host, StringComparison.OrdinalIgnoreCase) &&
        left.Port == right.Port;

    private static void AppendBounded(StringBuilder destination, string? value, int maximum)
    {
        if (string.IsNullOrWhiteSpace(value) || destination.Length >= maximum)
        {
            return;
        }

        var remaining = maximum - destination.Length;
        destination.Append(value.AsSpan(0, Math.Min(value.Length, remaining)));
    }

    [GeneratedRegex("""href\s*=\s*["'](?<url>[^"'#]+)""", RegexOptions.IgnoreCase)]
    private static partial Regex LinkRegex();

    [GeneratedRegex("""<(script|style)\b[^>]*>.*?</\1>""", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex ScriptRegex();

    [GeneratedRegex("""<[^>]+>""")]
    private static partial Regex TagRegex();

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRegex();

    private sealed record RemoteSkillContent(string Content, long SizeBytes, DateTime FetchedAt);
}

/// <summary>Persisted dashboard registry.</summary>
public sealed record DashboardRegistryState(
    List<SourceRegistration> Sources,
    List<SkillRegistration> Skills,
    List<McpRegistration> McpServers);

/// <summary>Persisted authoritative source registration.</summary>
public sealed record SourceRegistration(
    string Id,
    string Pattern,
    bool Enabled,
    DateTime CreatedAt,
    DateTime LastModifiedAt);

/// <summary>Persisted skill registration.</summary>
public sealed record SkillRegistration(
    string Name,
    string Path,
    string Description,
    bool Enabled);

/// <summary>Persisted MCP server registration.</summary>
public sealed record McpRegistration(string Name, Uri Endpoint);
