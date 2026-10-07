using Kare.Service.Cache;

namespace Kare.Service.Dashboard;

/// <summary>Maps the local Kare operations dashboard.</summary>
public static class DashboardEndpoints
{
    public static IEndpointRouteBuilder MapDashboard(this IEndpointRouteBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.MapGet(
            "/dashboard",
            static (HttpContext context) =>
            {
                context.Response.Headers.CacheControl = "no-store";
                context.Response.Headers.ContentSecurityPolicy =
                    "default-src 'self'; style-src 'unsafe-inline'; script-src 'unsafe-inline'; connect-src 'self'";
                context.Response.Headers.XContentTypeOptions = "nosniff";
                return Results.Content(Page, "text/html; charset=utf-8");
            });

        builder.MapGet(
            "/dashboard/api/snapshot",
            static (IDashboardMetricsCollector collector, IModelEndpointProvider endpointProvider) =>
        {
            var modelUsage = collector.GetModelUsage();
            var snapshot = new DashboardMetrics.Snapshot(
                DateTime.UtcNow,
                endpointProvider.GetEndpoints(modelUsage),
                modelUsage,
                collector.GetRequests(),
                collector.GetActivities(),
                collector.GetCacheEntries(),
                collector.GetWorkload(),
                collector.GetRoutingDecisionUrls(),
                collector.GetSkills(),
                collector.GetMcpServers());

            return Results.Json(snapshot, DashboardJsonContext.Default.Snapshot);
        });

        builder.MapDelete(
            "/dashboard/api/cache/{key}",
            static (string key, ResponseCache cache) =>
            {
                if (key.Length != 64 || key.Any(static character => !Uri.IsHexDigit(character)))
                {
                    return Results.BadRequest();
                }

                cache.Remove(key);
                return Results.NoContent();
            });

        builder.MapDelete(
            "/dashboard/api/cache",
            static (ResponseCache cache) =>
            {
                cache.Clear();
                return Results.NoContent();
            });

        builder.MapPost(
            "/dashboard/api/sources",
            static async Task<IResult> (
                CreateAuthoritativeSourceRequest request,
                IDashboardKnowledgeService knowledge,
                CancellationToken cancellationToken) =>
            {
                try
                {
                    var source = await knowledge
                        .AddSourceAsync(request, cancellationToken)
                        .ConfigureAwait(false);
                    return Results.Created($"/dashboard/api/sources/{source.Id}", source);
                }
                catch (ArgumentException)
                {
                    return Results.BadRequest();
                }
                catch (InvalidOperationException)
                {
                    return Results.Conflict();
                }
            });

        builder.MapDelete(
            "/dashboard/api/sources/{id}",
            static async Task<IResult> (
                string id,
                IDashboardKnowledgeService knowledge,
                CancellationToken cancellationToken) =>
                await knowledge.RemoveSourceAsync(id, cancellationToken).ConfigureAwait(false)
                    ? Results.NoContent()
                    : Results.NotFound());

        builder.MapPost(
            "/dashboard/api/skills",
            static async Task<IResult> (
                CreateDashboardSkillRequest request,
                IDashboardKnowledgeService knowledge,
                CancellationToken cancellationToken) =>
            {
                try
                {
                    var skill = await knowledge
                        .AddSkillAsync(request, cancellationToken)
                        .ConfigureAwait(false);
                    return Results.Created(
                        $"/dashboard/api/skills/{Uri.EscapeDataString(skill.Name)}",
                        skill);
                }
                catch (ArgumentException)
                {
                    return Results.BadRequest();
                }
                catch (InvalidOperationException)
                {
                    return Results.Conflict();
                }
            });

        builder.MapDelete(
            "/dashboard/api/skills/{name}",
            static async Task<IResult> (
                string name,
                IDashboardKnowledgeService knowledge,
                CancellationToken cancellationToken) =>
                await knowledge.RemoveSkillAsync(name, cancellationToken).ConfigureAwait(false)
                    ? Results.NoContent()
                    : Results.NotFound());

        builder.MapPost(
            "/dashboard/api/mcp-servers",
            static async Task<IResult> (
                CreateDashboardMcpServerRequest request,
                IDashboardKnowledgeService knowledge,
                CancellationToken cancellationToken) =>
            {
                try
                {
                    var server = await knowledge
                        .AddMcpServerAsync(request, cancellationToken)
                        .ConfigureAwait(false);
                    return Results.Created(
                        $"/dashboard/api/mcp-servers/{Uri.EscapeDataString(server.Name)}",
                        server);
                }
                catch (ArgumentException)
                {
                    return Results.BadRequest();
                }
                catch (InvalidOperationException)
                {
                    return Results.Conflict();
                }
            });

        builder.MapDelete(
            "/dashboard/api/mcp-servers/{name}",
            static async Task<IResult> (
                string name,
                IDashboardKnowledgeService knowledge,
                CancellationToken cancellationToken) =>
                await knowledge.RemoveMcpServerAsync(name, cancellationToken).ConfigureAwait(false)
                    ? Results.NoContent()
                    : Results.NotFound());

        builder.MapGet(
            "/dashboard/api/intake",
            static (IDashboardIntakeService intake) =>
                Results.Json(
                    (IReadOnlyList<IntakeProposal>)[.. intake.GetProposals().Select(Redacted)],
                    DashboardJsonContext.Default.ListIntakeProposal));

        builder.MapPost(
            "/dashboard/api/intake/{kind}",
            static async Task<IResult> (
                string kind,
                SubmitIntakeRequest request,
                IDashboardIntakeService intake,
                CancellationToken cancellationToken) =>
            {
                if (!TryParseKind(kind, out var intakeKind) ||
                    string.IsNullOrWhiteSpace(request.Input))
                {
                    return Results.BadRequest();
                }

                try
                {
                    var proposal = await intake
                        .SubmitAsync(intakeKind, request.Input, cancellationToken)
                        .ConfigureAwait(false);
                    return Results.Json(Redacted(proposal), DashboardJsonContext.Default.IntakeProposal);
                }
                catch (ArgumentException)
                {
                    return Results.BadRequest();
                }
                catch (InvalidOperationException)
                {
                    return Results.Conflict();
                }
            });

        builder.MapPost(
            "/dashboard/api/intake/{id}/approve",
            static async Task<IResult> (string id, IDashboardIntakeService intake, CancellationToken cancellationToken) =>
            {
                var proposal = await intake.ApproveAsync(id, cancellationToken).ConfigureAwait(false);
                return proposal is null ? Results.NotFound() : Results.Json(Redacted(proposal), DashboardJsonContext.Default.IntakeProposal);
            });

        builder.MapPost(
            "/dashboard/api/intake/{id}/retry",
            static async Task<IResult> (string id, IDashboardIntakeService intake, CancellationToken cancellationToken) =>
            {
                var proposal = await intake.RetryAsync(id, cancellationToken).ConfigureAwait(false);
                return proposal is null ? Results.NotFound() : Results.Json(Redacted(proposal), DashboardJsonContext.Default.IntakeProposal);
            });

        builder.MapPost(
            "/dashboard/api/intake/{id}/disable",
            static async Task<IResult> (string id, IDashboardIntakeService intake, CancellationToken cancellationToken) =>
            {
                var proposal = await intake.DisableAsync(id, cancellationToken).ConfigureAwait(false);
                return proposal is null ? Results.NotFound() : Results.Json(Redacted(proposal), DashboardJsonContext.Default.IntakeProposal);
            });

        builder.MapPost(
            "/dashboard/api/intake/{id}/refresh",
            static async Task<IResult> (string id, IDashboardIntakeService intake, CancellationToken cancellationToken) =>
            {
                var proposal = await intake.RefreshAsync(id, cancellationToken).ConfigureAwait(false);
                return proposal is null ? Results.NotFound() : Results.Json(Redacted(proposal), DashboardJsonContext.Default.IntakeProposal);
            });

        builder.MapDelete(
            "/dashboard/api/intake/{id}",
            static async Task<IResult> (string id, IDashboardIntakeService intake, CancellationToken cancellationToken) =>
                await intake.DeleteAsync(id, cancellationToken).ConfigureAwait(false)
                    ? Results.NoContent()
                    : Results.NotFound());

        return builder;
    }

    private static bool TryParseKind(string value, out IntakeKind kind)
    {
        switch (value)
        {
            case "source":
                kind = IntakeKind.Source;
                return true;
            case "skill":
                kind = IntakeKind.Skill;
                return true;
            case "mcp":
                kind = IntakeKind.Mcp;
                return true;
            default:
                kind = default;
                return false;
        }
    }

    /// <summary>Redacts query strings and fragments from any endpoint-shaped field before the
    /// proposal leaves the process boundary, in addition to the redaction already applied when
    /// MCP endpoints are classified and persisted.</summary>
    private static IntakeProposal Redacted(IntakeProposal proposal) =>
        proposal with
        {
            RawInput = proposal.Kind == IntakeKind.Mcp
                ? EndpointRedaction.RedactIfUri(proposal.RawInput)
                : proposal.RawInput,
            ResolvedTarget = proposal.Kind == IntakeKind.Mcp
                ? EndpointRedaction.RedactIfUri(proposal.ResolvedTarget)
                : proposal.ResolvedTarget,
        };

    private const string Page = """
        <!doctype html>
        <html lang="en">
        <head>
          <meta charset="utf-8">
          <meta name="viewport" content="width=device-width,initial-scale=1">
          <title>Kare dashboard</title>
          <style>
            :root {
              color-scheme: dark;
              --bg: #0b1014;
              --panel: #121a20;
              --panel-2: #172229;
              --line: #2a3942;
              --text: #e7eef2;
              --muted: #91a3ad;
              --green: #68d391;
              --amber: #f6c453;
              --red: #fc8181;
              --blue: #63b3ed;
            }
            * { box-sizing: border-box; }
            body {
              margin: 0;
              background: radial-gradient(circle at top right, #17313b 0, var(--bg) 34rem);
              color: var(--text);
              font: 14px/1.45 ui-monospace, SFMono-Regular, Menlo, Monaco, Consolas, monospace;
            }
            header, main { width: min(1440px, calc(100% - 32px)); margin: 0 auto; }
            header { padding: 28px 0 18px; display: flex; gap: 16px; align-items: end; justify-content: space-between; }
            h1 { margin: 0; font: 700 28px/1 system-ui, sans-serif; letter-spacing: -.03em; }
            h2 { margin: 0 0 14px; font: 650 16px/1.2 system-ui, sans-serif; }
            p { margin: 6px 0 0; color: var(--muted); }
            .section-head { display: flex; align-items: center; justify-content: space-between; gap: 12px; }
            .form-row { display: flex; gap: 8px; margin-bottom: 12px; }
            .form-row input { min-width: 0; flex: 1; }
            input, button {
              border: 1px solid var(--line);
              border-radius: 7px;
              background: var(--panel);
              color: var(--text);
              padding: 8px 10px;
              font: inherit;
            }
            button { cursor: pointer; }
            button:hover { border-color: var(--blue); }
            .status { display: inline-flex; align-items: center; gap: 7px; color: var(--muted); }
            .dot { width: 8px; height: 8px; border-radius: 50%; background: var(--amber); }
            .dot.ok { background: var(--green); }
            .dot.error { background: var(--red); }
            .grid { display: grid; grid-template-columns: repeat(4, minmax(0, 1fr)); gap: 12px; }
            .card {
              background: color-mix(in srgb, var(--panel) 94%, transparent);
              border: 1px solid var(--line);
              border-radius: 10px;
              padding: 16px;
              min-width: 0;
            }
            .metric { font: 700 26px/1 system-ui, sans-serif; margin-top: 9px; }
            .wide { grid-column: span 2; }
            .full { grid-column: 1 / -1; }
            .section { margin-top: 12px; }
            .table-wrap { overflow: auto; max-height: 390px; }
            table { width: 100%; border-collapse: collapse; white-space: nowrap; }
            th, td { padding: 9px 10px; border-bottom: 1px solid var(--line); text-align: left; }
            th { position: sticky; top: 0; background: var(--panel); color: var(--muted); font-weight: 500; }
            tr:last-child td { border-bottom: 0; }
            .tag { border-radius: 999px; padding: 2px 7px; background: var(--panel-2); }
            .good { color: var(--green); }
            .bad { color: var(--red); }
            .muted { color: var(--muted); }
            .empty { padding: 24px 10px; color: var(--muted); text-align: center; }
            .delete { padding: 4px 7px; color: var(--red); }
            .danger { color: var(--red); }
            .approve { color: var(--green); }
            .link-button { border: 0; background: transparent; color: var(--blue); padding: 0; text-align: left; }
            .preview { color: var(--muted); font-size: 12px; }
            .actions { display: flex; gap: 6px; flex-wrap: wrap; }
            .actions button { padding: 3px 7px; font-size: 12px; }
            dialog {
              width: min(720px, calc(100% - 32px));
              border: 1px solid var(--line);
              border-radius: 10px;
              background: var(--panel);
              color: var(--text);
              padding: 18px;
            }
            dialog::backdrop { background: rgb(0 0 0 / 65%); }
            .detail-grid { display: grid; grid-template-columns: 160px 1fr; gap: 8px 14px; }
            .detail-grid dt { color: var(--muted); }
            .detail-grid dd { margin: 0; overflow-wrap: anywhere; }
            footer { padding: 18px 0 32px; color: var(--muted); }
            @media (max-width: 900px) {
              .grid { grid-template-columns: repeat(2, minmax(0, 1fr)); }
              header { align-items: start; flex-direction: column; }
            }
            @media (max-width: 560px) {
              .grid { grid-template-columns: 1fr; }
              .wide { grid-column: span 1; }
            }
          </style>
        </head>
        <body>
          <header>
            <div>
              <h1>Kare</h1>
              <p><span class="status"><span id="dot" class="dot"></span><span id="connection">Connecting</span></span></p>
            </div>
            <button id="refresh" type="button">Refresh</button>
          </header>
          <main>
            <section class="grid">
              <article class="card"><div class="muted">Requests</div><div id="requests-total" class="metric">0</div></article>
              <article class="card"><div class="muted">Success rate</div><div id="success-rate" class="metric">n/a</div></article>
              <article class="card"><div class="muted">Average TTFT</div><div id="average-ttft" class="metric">n/a</div></article>
              <article class="card"><div class="muted">Decode rate</div><div id="decode-rate" class="metric">n/a</div></article>
              <article class="card"><div class="muted">Queue depth</div><div id="queue-depth" class="metric">0</div></article>
              <article class="card"><div class="muted">Active requests</div><div id="active-requests" class="metric">0</div></article>
              <article class="card"><div class="muted">Local routes</div><div id="local-routes" class="metric">0</div></article>
              <article class="card"><div class="muted">Billable routes</div><div id="billable-routes" class="metric">0</div></article>
            </section>

            <section class="grid section">
              <article class="card wide">
                <h2>Model call breakdown</h2>
                <div style="display:grid;grid-template-columns:220px 1fr;gap:16px;align-items:center;">
                  <svg id="model-route-chart" viewBox="0 0 200 200" width="200" height="200" role="img" aria-label="Model route chart"></svg>
                  <div id="model-route-legend"></div>
                </div>
              </article>
              <article class="card full">
                <h2>Model endpoints</h2>
                <div class="table-wrap"><table>
                  <thead><tr><th>ID</th><th>Provider</th><th>Model</th><th>Wire model</th><th>Endpoint</th><th>Tier</th><th>Tools</th><th>Requests</th><th>Input tokens</th><th>Output tokens</th><th>Last used</th></tr></thead>
                  <tbody id="model-endpoints"></tbody>
                </table></div>
              </article>
              <article class="card full">
                <h2>Usage by model</h2>
                <div class="table-wrap"><table>
                  <thead><tr><th>Model</th><th>Backend</th><th>Route</th><th>Requests</th><th>Success</th><th>Billable</th><th>Fallbacks</th><th>Input tokens</th><th>Output tokens</th><th>Avg TTFT</th><th>Avg total</th><th>Avg tok/s</th></tr></thead>
                  <tbody id="models"></tbody>
                </table></div>
              </article>
              <article class="card full">
                <h2>Recent requests</h2>
                <div class="table-wrap"><table>
                  <thead><tr><th>Time</th><th>Route</th><th>Model</th><th>Backend</th><th>Status</th><th>TTFT</th><th>Total</th><th>Tokens</th><th>tok/s</th></tr></thead>
                  <tbody id="requests"></tbody>
                </table></div>
              </article>
              <article class="card wide">
                <h2>Activity</h2>
                <div class="table-wrap"><table>
                  <thead><tr><th>Time</th><th>Type</th><th>Description</th><th>Status</th></tr></thead>
                  <tbody id="activities"></tbody>
                </table></div>
              </article>
              <article class="card wide">
                <div class="section-head">
                  <h2>Cache</h2>
                  <button id="clear-cache" class="danger" type="button">Delete all</button>
                </div>
                <div class="table-wrap"><table>
                  <thead><tr><th>Name</th><th>Type</th><th>Created</th><th>Last used</th><th>Expires</th><th>Size</th><th>Keywords</th><th></th></tr></thead>
                  <tbody id="cache"></tbody>
                </table></div>
              </article>
              <article class="card full">
                <h2>Authoritative sources</h2>
                <p>Paste a URL, repository, or documentation page. Kare proposes a bounded fetch scope before anything is crawled.</p>
                <div class="form-row">
                  <input id="source-paste" type="text" placeholder="https://docs.example.com/guide or https://github.com/owner/repo">
                  <button id="add-source" type="button">Add</button>
                </div>
                <div class="table-wrap"><table>
                  <thead><tr><th>Input</th><th>Status</th><th>Scope</th><th>Trust basis</th><th>Note</th><th></th></tr></thead>
                  <tbody id="intake-source"></tbody>
                </table></div>
                <div class="table-wrap"><table>
                  <thead><tr><th>Pattern</th><th>Status</th><th>Pages</th><th>Characters</th><th>Topics</th><th>Last crawled</th><th>Error</th><th></th></tr></thead>
                  <tbody id="routes"></tbody>
                </table></div>
              </article>
              <article class="card full">
                <h2>Skills</h2>
                <p>Paste a repository, direct SKILL.md URL, install/plugin command text, or an absolute device path. Commands are parsed, never executed.</p>
                <div class="form-row">
                  <input id="skill-paste" type="text" placeholder="https://example.com/SKILL.md, a GitHub repo, or /absolute/path/to/SKILL.md">
                  <button id="add-skill" type="button">Add</button>
                </div>
                <div class="table-wrap"><table>
                  <thead><tr><th>Input</th><th>Status</th><th>Scope</th><th>Trust basis</th><th>Note</th><th></th></tr></thead>
                  <tbody id="intake-skill"></tbody>
                </table></div>
                <div class="table-wrap"><table>
                  <thead><tr><th>Name</th><th>Description</th><th>File or URL</th><th>Status</th><th>Tags</th><th>Size</th><th>Modified</th><th></th></tr></thead>
                  <tbody id="skills"></tbody>
                </table></div>
              </article>
              <article class="card full">
                <h2>MCP servers</h2>
                <p>Paste an endpoint, package reference, repository, or install/plugin command. Query strings and fragments are redacted from this dashboard.</p>
                <div class="form-row">
                  <input id="mcp-paste" type="text" placeholder="https://mcp.example.com/mcp or an install command">
                  <button id="add-mcp" type="button">Add</button>
                </div>
                <div class="table-wrap"><table>
                  <thead><tr><th>Input</th><th>Status</th><th>Scope</th><th>Trust basis</th><th>Note</th><th></th></tr></thead>
                  <tbody id="intake-mcp"></tbody>
                </table></div>
                <div class="table-wrap"><table>
                  <thead><tr><th>Name</th><th>Endpoint</th><th>Capabilities</th><th>Keywords</th><th>Status</th><th>Last checked</th><th>Error</th><th></th></tr></thead>
                  <tbody id="mcp"></tbody>
                </table></div>
              </article>
            </section>
            <footer id="generated">No data loaded.</footer>
          </main>
          <dialog id="cache-detail">
            <div class="section-head">
              <h2 id="cache-detail-title">Cache entry</h2>
              <button id="close-cache-detail" type="button">Close</button>
            </div>
            <dl id="cache-detail-body" class="detail-grid"></dl>
          </dialog>
          <script>
            const escapeHtml = value => String(value ?? '')
              .replaceAll('&', '&amp;').replaceAll('<', '&lt;')
              .replaceAll('>', '&gt;').replaceAll('"', '&quot;')
              .replaceAll("'", '&#039;');
            const time = value => value ? new Date(value).toLocaleString() : 'n/a';
            const number = (value, suffix = '') => value == null ? 'n/a' : `${Number(value).toFixed(1)}${suffix}`;
            const empty = columns => `<tr><td class="empty" colspan="${columns}">No data yet.</td></tr>`;
            function renderModelBreakdown(snapshot) {
              const totals = (snapshot.modelEndpoints || []).map((item, index) => ({
                name: `${item.provider}: ${item.modelId}`,
                endpoint: item.endpoint,
                value: item.requestCount || 0,
                color: `hsl(${(hashString(item.id || String(index)) % 360)}, 68%, 58%)`
              })).filter(item => item.value > 0);

              const chart = document.querySelector('#model-route-chart');
              const legend = document.querySelector('#model-route-legend');
              if (!totals.length) {
                chart.innerHTML = '<circle cx="100" cy="100" r="80" fill="#172229" stroke="#2a3942" stroke-width="1" />';
                legend.innerHTML = '<div class="muted">No request data yet.</div>';
                return;
              }

              const total = totals.reduce((sum, item) => sum + item.value, 0);
              let start = -Math.PI / 2;
              const segments = totals.map(item => {
                const angle = (item.value / total) * Math.PI * 2;
                const end = start + angle;
                const largeArc = angle > Math.PI ? 1 : 0;
                const x1 = 100 + 80 * Math.cos(start);
                const y1 = 100 + 80 * Math.sin(start);
                const x2 = 100 + 80 * Math.cos(end);
                const y2 = 100 + 80 * Math.sin(end);
                const path = `M 100 100 L ${x1} ${y1} A 80 80 0 ${largeArc} 1 ${x2} ${y2} Z`;
                start = end;
                return { ...item, path, pct: item.value / total, full: item.value === total };
              });

              chart.innerHTML = segments.map(segment => segment.full
                ? `<circle cx="100" cy="100" r="80" fill="${segment.color}" stroke="#0b1014" stroke-width="1"></circle>`
                : `<path d="${segment.path}" fill="${segment.color}" stroke="#0b1014" stroke-width="1"></path>`).join('');
              legend.innerHTML = segments.map(segment => `
                <div style="display:flex;align-items:center;gap:8px;padding:4px 0;">
                  <span style="display:inline-block;width:12px;height:12px;border-radius:3px;background:${segment.color};"></span>
                  <span title="${escapeHtml(segment.endpoint)}">${escapeHtml(segment.name)}</span>
                  <span class="muted">${segment.value} (${((segment.pct) * 100).toFixed(1)}%)</span>
                </div>`).join('');
            }

            function hashString(value) {
              let hash = 0;
              for (let i = 0; i < value.length; i++) {
                hash = (hash * 31 + value.charCodeAt(i)) >>> 0;
              }
              return hash;
            }

            let cacheEntriesByKey = new Map();

            function render(snapshot) {
              const requests = snapshot.requests || [];
              const successful = requests.filter(item => item.succeeded).length;
              const rates = requests.map(item => item.decodeTokensPerSecond).filter(value => value != null);
              const local = requests.filter(item => !item.isBillable && item.route !== 'Cache').length;
              const billable = requests.filter(item => item.isBillable).length;
              renderModelBreakdown(snapshot);

              document.querySelector('#requests-total').textContent =
                snapshot.workload?.totalRequestsProcessed ?? requests.length;
              document.querySelector('#success-rate').textContent =
                requests.length ? `${(successful / requests.length * 100).toFixed(1)}%` : 'n/a';
              document.querySelector('#average-ttft').textContent =
                number(snapshot.workload?.averageTimeToFirstTokenMs, ' ms');
              document.querySelector('#decode-rate').textContent =
                rates.length ? number(rates.reduce((sum, value) => sum + value, 0) / rates.length, ' tok/s') : 'n/a';
              document.querySelector('#queue-depth').textContent = snapshot.workload?.queueDepth ?? 0;
              document.querySelector('#active-requests').textContent = snapshot.workload?.activeRequests ?? 0;
              document.querySelector('#local-routes').textContent = local;
              document.querySelector('#billable-routes').textContent = billable;

              const endpoints = snapshot.modelEndpoints || [];
              document.querySelector('#model-endpoints').innerHTML = endpoints.length ? endpoints.map(item => `
                <tr>
                  <td>${escapeHtml(item.id)}</td><td>${escapeHtml(item.provider)}</td>
                  <td>${escapeHtml(item.modelId)}</td><td>${escapeHtml(item.wireModel ?? 'n/a')}</td>
                  <td>${escapeHtml(item.endpoint)}</td><td>${escapeHtml(item.tier)}</td>
                  <td>${item.supportsTools ? 'yes' : 'no'}</td><td>${escapeHtml(item.requestCount)}</td>
                  <td>${escapeHtml(item.inputTokens)}</td><td>${escapeHtml(item.outputTokens)}</td>
                  <td>${escapeHtml(time(item.lastUsedAt))}</td>
                </tr>`).join('') : empty(11);

              const models = snapshot.modelUsage || [];
              document.querySelector('#models').innerHTML = models.length ? models.map(item => `
                <tr>
                  <td>${escapeHtml(item.modelId)}</td><td>${escapeHtml(item.backend)}</td>
                  <td><span class="tag">${escapeHtml(item.route)}</span></td>
                  <td>${escapeHtml(item.requestCount)}</td>
                  <td>${escapeHtml(item.successfulRequests)} / ${escapeHtml(item.failedRequests)} failed</td>
                  <td>${escapeHtml(item.billableRequests)}</td><td>${escapeHtml(item.fallbackRequests)}</td>
                  <td>${escapeHtml(item.inputTokens)}</td><td>${escapeHtml(item.outputTokens)}</td>
                  <td>${escapeHtml(number(item.averageTimeToFirstTokenMs, ' ms'))}</td>
                  <td>${escapeHtml(number(item.averageTotalDurationMs, ' ms'))}</td>
                  <td>${escapeHtml(number(item.averageDecodeTokensPerSecond))}</td>
                </tr>`).join('') : empty(12);

              document.querySelector('#requests').innerHTML = requests.length ? requests.map(item => `
                <tr>
                  <td>${escapeHtml(time(item.timestamp))}</td>
                  <td><span class="tag">${escapeHtml(item.route)}</span>${item.isFallback ? ' <span class="bad">fallback</span>' : ''}</td>
                  <td>${escapeHtml(item.modelId)}</td><td>${escapeHtml(item.backend)}</td>
                  <td class="${item.succeeded ? 'good' : 'bad'}">${item.succeeded ? 'succeeded' : 'failed'}</td>
                  <td>${escapeHtml(number(item.timeToFirstTokenMs, ' ms'))}</td>
                  <td>${escapeHtml(number(item.totalDurationMs, ' ms'))}</td>
                  <td>${escapeHtml(item.outputTokens ?? 'n/a')}</td>
                  <td>${escapeHtml(number(item.decodeTokensPerSecond))}</td>
                </tr>`).join('') : empty(9);

              const activities = snapshot.activities || [];
              document.querySelector('#activities').innerHTML = activities.length ? activities.map(item => `
                <tr><td>${escapeHtml(time(item.timestamp))}</td><td>${escapeHtml(item.type)}</td>
                <td title="${escapeHtml(item.description)}">${escapeHtml(item.description)}</td>
                <td class="${item.status === 'succeeded' ? 'good' : 'bad'}">${escapeHtml(item.status)}</td></tr>`).join('') : empty(4);

              const cache = snapshot.cacheEntries || [];
              cacheEntriesByKey = new Map(cache.map(item => [item.key, item]));
              document.querySelector('#cache').innerHTML = cache.length ? cache.map(item => `
                <tr><td><button class="link-button" data-cache-detail="${escapeHtml(item.key)}">${escapeHtml(item.name || 'Cached entry')}</button></td>
                <td>${escapeHtml(item.kind || 'response')}</td>
                <td>${escapeHtml(time(item.createdAt))}</td><td>${escapeHtml(time(item.lastAccessedAt))}</td>
                <td>${escapeHtml(time(item.expiresAt))}</td>
                <td>${escapeHtml(item.sizeBytes)} B</td>
                <td>${escapeHtml((item.keywords || []).join(', '))}</td>
                <td><button class="delete" data-cache-key="${escapeHtml(item.key)}">Delete</button></td></tr>`).join('') : empty(8);

              const routes = snapshot.routingDecisionUrls || [];
              document.querySelector('#routes').innerHTML = routes.length ? routes.map(item => `
                <tr><td>${escapeHtml(item.pattern)}</td>
                <td class="${item.status === 'ready' ? 'good' : item.status === 'failed' ? 'bad' : ''}">${escapeHtml(item.status)}</td>
                <td>${escapeHtml(item.pageCount)}</td><td>${escapeHtml(item.contentCharacters)}</td>
                <td title="${escapeHtml((item.headings || []).join(' / '))}">${escapeHtml((item.topics || []).join(', '))}</td>
                <td>${escapeHtml(time(item.lastCrawledAt))}</td><td>${escapeHtml(item.error ?? '')}</td>
                <td><button class="delete" data-source-id="${escapeHtml(item.id)}">Delete</button></td></tr>`).join('') : empty(8);

              const skills = snapshot.skills || [];
              document.querySelector('#skills').innerHTML = skills.length ? skills.map(item => `
                <tr><td>${escapeHtml(item.name)}</td><td>${escapeHtml(item.description)}</td>
                <td>${escapeHtml(item.path)}</td>
                <td class="${item.status === 'ready' ? 'good' : 'bad'}">${escapeHtml(item.status)}</td>
                <td>${escapeHtml((item.tags || []).join(', '))}</td>
                <td>${escapeHtml(item.sizeBytes)} B</td><td>${escapeHtml(time(item.lastModifiedAt))}</td>
                <td><button class="delete" data-skill-name="${escapeHtml(item.name)}">Delete</button></td></tr>`).join('') : empty(8);

              const servers = snapshot.mcpServers || [];
              document.querySelector('#mcp').innerHTML = servers.length ? servers.map(item => `
                <tr><td>${escapeHtml(item.name)}</td><td>${escapeHtml(item.endpoint)}</td>
                <td>${escapeHtml((item.capabilities || []).join(', '))}</td>
                <td>${escapeHtml((item.keywords || []).join(', '))}</td>
                <td class="${item.connected ? 'good' : 'bad'}">${item.connected ? 'connected' : 'disconnected'}</td>
                <td>${escapeHtml(time(item.lastCheckedAt))}</td><td>${escapeHtml(item.error ?? '')}</td>
                <td><button class="delete" data-mcp-name="${escapeHtml(item.name)}">Delete</button></td></tr>`).join('') : empty(8);

              document.querySelector('#generated').textContent = `Updated ${time(snapshot.generatedAt)}. Refreshes every 5 seconds.`;
            }

            const intakeActions = proposal => {
              const buttons = [];
              if (proposal.requiresApproval && proposal.status === 'Review') {
                buttons.push(`<button class="approve" data-intake-id="${escapeHtml(proposal.id)}" data-intake-action="approve">Approve</button>`);
              }
              if (proposal.status === 'Failed') {
                buttons.push(`<button data-intake-id="${escapeHtml(proposal.id)}" data-intake-action="retry">Retry</button>`);
              }
              if (proposal.status === 'Active') {
                buttons.push(`<button data-intake-id="${escapeHtml(proposal.id)}" data-intake-action="refresh">Refresh</button>`);
                buttons.push(`<button data-intake-id="${escapeHtml(proposal.id)}" data-intake-action="disable">Disable</button>`);
              }
              buttons.push(`<button class="delete" data-intake-id="${escapeHtml(proposal.id)}" data-intake-action="delete">Delete</button>`);
              return `<div class="actions">${buttons.join(' ')}</div>`;
            };

            const intakeStatusClass = status =>
              status === 'Active' ? 'good' : status === 'Failed' ? 'bad' : status === 'Review' ? '' : 'muted';

            function renderIntakeTable(elementId, proposals) {
              const rows = proposals.length ? proposals.map(item => `
                <tr><td title="${escapeHtml(item.rawInput)}">${escapeHtml((item.canonicalName || item.rawInput || '').slice(0, 60))}</td>
                <td class="${intakeStatusClass(item.status)}">${escapeHtml(item.status)}</td>
                <td>${escapeHtml(item.inferredScope ?? '')}</td>
                <td>${escapeHtml(item.trustBasis ?? '')}</td>
                <td title="${escapeHtml(item.error ?? '')}">${escapeHtml(item.resolvedDescription ?? item.error ?? '')}</td>
                <td>${intakeActions(item)}</td></tr>`).join('') : empty(6);
              document.querySelector(`#${elementId}`).innerHTML = rows;
            }

            function renderIntake(proposals) {
              const byKind = kind => (proposals || []).filter(item => item.kind === kind);
              renderIntakeTable('intake-source', byKind('Source'));
              renderIntakeTable('intake-skill', byKind('Skill'));
              renderIntakeTable('intake-mcp', byKind('Mcp'));
            }

            async function refreshIntake() {
              try {
                const response = await fetch('/dashboard/api/intake', { cache: 'no-store' });
                if (!response.ok) return;
                renderIntake(await response.json());
              } catch {
                // Intake polling failures do not interrupt the main dashboard refresh.
              }
            }

            async function refresh() {
              const dot = document.querySelector('#dot');
              const connection = document.querySelector('#connection');
              try {
                const response = await fetch('/dashboard/api/snapshot', { cache: 'no-store' });
                if (response.status === 403) throw new Error('Local network access required');
                if (!response.ok) throw new Error(`Dashboard API returned ${response.status}`);
                render(await response.json());
                dot.className = 'dot ok';
                connection.textContent = 'Connected';
              } catch (error) {
                dot.className = 'dot error';
                connection.textContent = error.message;
              }
              await refreshIntake();
            }

            document.querySelector('#refresh').addEventListener('click', refresh);
            document.querySelector('#cache').addEventListener('click', async event => {
              const detailKey = event.target.dataset.cacheDetail;
              if (detailKey) {
                showCacheDetail(cacheEntriesByKey.get(detailKey));
                return;
              }
              const key = event.target.dataset.cacheKey;
              if (!key) return;
              const response = await fetch(`/dashboard/api/cache/${encodeURIComponent(key)}`, {
                method: 'DELETE'
              });
              if (response.ok) refresh();
            });
            document.querySelector('#close-cache-detail').addEventListener('click', () =>
              document.querySelector('#cache-detail').close());
            document.querySelector('#clear-cache').addEventListener('click', async () => {
              if (!confirm('Delete every cached response?')) return;
              const response = await fetch('/dashboard/api/cache', { method: 'DELETE' });
              if (response.ok) refresh();
            });
            document.querySelector('#add-source').addEventListener('click', async () => {
              const input = document.querySelector('#source-paste');
              const response = await postJson('/dashboard/api/intake/source', {
                input: input.value.trim()
              });
              if (response.ok) {
                input.value = '';
                refresh();
              }
            });
            document.querySelector('#routes').addEventListener('click', async event => {
              const id = event.target.dataset.sourceId;
              if (!id) return;
              const response = await fetch(`/dashboard/api/sources/${encodeURIComponent(id)}`, {
                method: 'DELETE'
              });
              if (response.ok) refresh();
            });
            document.querySelector('#add-skill').addEventListener('click', async () => {
              const input = document.querySelector('#skill-paste');
              const response = await postJson('/dashboard/api/intake/skill', {
                input: input.value.trim()
              });
              if (response.ok) {
                input.value = '';
                refresh();
              }
            });
            document.querySelector('#skills').addEventListener('click', async event => {
              const name = event.target.dataset.skillName;
              if (!name) return;
              const response = await fetch(`/dashboard/api/skills/${encodeURIComponent(name)}`, {
                method: 'DELETE'
              });
              if (response.ok) refresh();
            });
            document.querySelector('#add-mcp').addEventListener('click', async () => {
              const input = document.querySelector('#mcp-paste');
              const response = await postJson('/dashboard/api/intake/mcp', {
                input: input.value.trim()
              });
              if (response.ok) {
                input.value = '';
                refresh();
              }
            });
            document.querySelector('#mcp').addEventListener('click', async event => {
              const name = event.target.dataset.mcpName;
              if (!name) return;
              const response = await fetch(`/dashboard/api/mcp-servers/${encodeURIComponent(name)}`, {
                method: 'DELETE'
              });
              if (response.ok) refresh();
            });
            async function handleIntakeAction(event) {
              const id = event.target.dataset.intakeId;
              const action = event.target.dataset.intakeAction;
              if (!id || !action) return;
              const response = action === 'delete'
                ? await fetch(`/dashboard/api/intake/${encodeURIComponent(id)}`, { method: 'DELETE' })
                : await fetch(`/dashboard/api/intake/${encodeURIComponent(id)}/${action}`, { method: 'POST' });
              if (response.ok) refresh();
            }
            document.querySelector('#intake-source').addEventListener('click', handleIntakeAction);
            document.querySelector('#intake-skill').addEventListener('click', handleIntakeAction);
            document.querySelector('#intake-mcp').addEventListener('click', handleIntakeAction);

            function postJson(url, value) {
              return fetch(url, {
                method: 'POST',
                headers: { 'Content-Type': 'application/json' },
                body: JSON.stringify(value)
              });
            }

            function showCacheDetail(entry) {
              if (!entry) return;
              const status = entry.expiresAt && new Date(entry.expiresAt) <= new Date()
                ? 'expired'
                : 'active';
              const values = [
                ['Type', entry.kind || 'response'],
                ['Status', status],
                ['Opaque key', entry.key],
                ['Created', time(entry.createdAt)],
                ['Last used', time(entry.lastAccessedAt)],
                ['Expires', time(entry.expiresAt)],
                ['Size', `${entry.sizeBytes} B`],
                ['Content type', entry.contentType],
                ['Model', entry.modelId || 'n/a'],
                ['Route', entry.route || 'n/a'],
                ['Backend', entry.backend || 'n/a'],
                ['Route target', entry.routeTarget || 'n/a'],
                ['Tokens', entry.totalTokens == null
                  ? 'n/a'
                  : `${entry.totalTokens} total (${entry.inputTokens ?? 'n/a'} in, ${entry.outputTokens ?? 'n/a'} out)`],
                ['Task', entry.taskClass || 'n/a'],
                ['Keywords', (entry.keywords || []).join(', ') || 'n/a'],
                ['Summary', entry.summary || 'No privacy-safe summary is available.'],
                ['Could match again', entry.reuseHint || 'Only an exact policy-safe fingerprint can reuse this entry.']
              ];
              document.querySelector('#cache-detail-title').textContent = entry.name || 'Cache entry';
              document.querySelector('#cache-detail-body').innerHTML = values
                .map(([label, value]) => `<dt>${escapeHtml(label)}</dt><dd>${escapeHtml(value)}</dd>`)
                .join('');
              document.querySelector('#cache-detail').showModal();
            }

            refresh();
            setInterval(refresh, 5000);
          </script>
        </body>
        </html>
        """;
}
