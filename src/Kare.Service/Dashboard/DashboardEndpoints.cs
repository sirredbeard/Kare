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

        return builder;
    }

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
                  <thead><tr><th>Key</th><th>Created</th><th>Last used</th><th>Size</th><th></th></tr></thead>
                  <tbody id="cache"></tbody>
                </table></div>
              </article>
              <article class="card full">
                <h2>Authoritative sources</h2>
                <div class="form-row">
                  <input id="source-pattern" type="url" placeholder="https://docs.example.com/*">
                  <button id="add-source" type="button">Add and crawl</button>
                </div>
                <div class="table-wrap"><table>
                  <thead><tr><th>Pattern</th><th>Status</th><th>Pages</th><th>Characters</th><th>Last crawled</th><th>Error</th><th></th></tr></thead>
                  <tbody id="routes"></tbody>
                </table></div>
              </article>
              <article class="card full">
                <h2>Skills</h2>
                <div class="form-row">
                  <input id="skill-name" type="text" placeholder="Skill name">
                  <input id="skill-path" type="text" placeholder="/absolute/path/to/SKILL.md or https://example.com/SKILL.md">
                  <input id="skill-description" type="text" placeholder="Description">
                  <button id="add-skill" type="button">Add skill</button>
                </div>
                <div class="table-wrap"><table>
                  <thead><tr><th>Name</th><th>Description</th><th>File or URL</th><th>Status</th><th>Size</th><th>Modified</th><th></th></tr></thead>
                  <tbody id="skills"></tbody>
                </table></div>
              </article>
              <article class="card full">
                <h2>MCP servers</h2>
                <div class="form-row">
                  <input id="mcp-name" type="text" placeholder="Server name">
                  <input id="mcp-endpoint" type="url" placeholder="https://mcp.example.com/mcp">
                  <button id="add-mcp" type="button">Add and probe</button>
                </div>
                <div class="table-wrap"><table>
                  <thead><tr><th>Name</th><th>Endpoint</th><th>Capabilities</th><th>Status</th><th>Last checked</th><th>Error</th><th></th></tr></thead>
                  <tbody id="mcp"></tbody>
                </table></div>
              </article>
            </section>
            <footer id="generated">No data loaded.</footer>
          </main>
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
              document.querySelector('#cache').innerHTML = cache.length ? cache.map(item => `
                <tr><td title="${escapeHtml(item.key)}">${escapeHtml(item.key.slice(0, 12))}...</td>
                <td>${escapeHtml(time(item.createdAt))}</td><td>${escapeHtml(time(item.lastAccessedAt))}</td>
                <td>${escapeHtml(item.sizeBytes)} B</td>
                <td><button class="delete" data-cache-key="${escapeHtml(item.key)}">Delete</button></td></tr>`).join('') : empty(5);

              const routes = snapshot.routingDecisionUrls || [];
              document.querySelector('#routes').innerHTML = routes.length ? routes.map(item => `
                <tr><td>${escapeHtml(item.pattern)}</td>
                <td class="${item.status === 'ready' ? 'good' : item.status === 'failed' ? 'bad' : ''}">${escapeHtml(item.status)}</td>
                <td>${escapeHtml(item.pageCount)}</td><td>${escapeHtml(item.contentCharacters)}</td>
                <td>${escapeHtml(time(item.lastCrawledAt))}</td><td>${escapeHtml(item.error ?? '')}</td>
                <td><button class="delete" data-source-id="${escapeHtml(item.id)}">Delete</button></td></tr>`).join('') : empty(7);

              const skills = snapshot.skills || [];
              document.querySelector('#skills').innerHTML = skills.length ? skills.map(item => `
                <tr><td>${escapeHtml(item.name)}</td><td>${escapeHtml(item.description)}</td>
                <td>${escapeHtml(item.path)}</td>
                <td class="${item.status === 'ready' ? 'good' : 'bad'}">${escapeHtml(item.status)}</td>
                <td>${escapeHtml(item.sizeBytes)} B</td><td>${escapeHtml(time(item.lastModifiedAt))}</td>
                <td><button class="delete" data-skill-name="${escapeHtml(item.name)}">Delete</button></td></tr>`).join('') : empty(7);

              const servers = snapshot.mcpServers || [];
              document.querySelector('#mcp').innerHTML = servers.length ? servers.map(item => `
                <tr><td>${escapeHtml(item.name)}</td><td>${escapeHtml(item.endpoint)}</td>
                <td>${escapeHtml((item.capabilities || []).join(', '))}</td>
                <td class="${item.connected ? 'good' : 'bad'}">${item.connected ? 'connected' : 'disconnected'}</td>
                <td>${escapeHtml(time(item.lastCheckedAt))}</td><td>${escapeHtml(item.error ?? '')}</td>
                <td><button class="delete" data-mcp-name="${escapeHtml(item.name)}">Delete</button></td></tr>`).join('') : empty(7);

              document.querySelector('#generated').textContent = `Updated ${time(snapshot.generatedAt)}. Refreshes every 5 seconds.`;
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
            }

            document.querySelector('#refresh').addEventListener('click', refresh);
            document.querySelector('#cache').addEventListener('click', async event => {
              const key = event.target.dataset.cacheKey;
              if (!key) return;
              const response = await fetch(`/dashboard/api/cache/${encodeURIComponent(key)}`, {
                method: 'DELETE'
              });
              if (response.ok) refresh();
            });
            document.querySelector('#clear-cache').addEventListener('click', async () => {
              if (!confirm('Delete every cached response?')) return;
              const response = await fetch('/dashboard/api/cache', { method: 'DELETE' });
              if (response.ok) refresh();
            });
            document.querySelector('#add-source').addEventListener('click', async () => {
              const input = document.querySelector('#source-pattern');
              const response = await postJson('/dashboard/api/sources', {
                pattern: input.value.trim(), enabled: true
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
              const name = document.querySelector('#skill-name');
              const path = document.querySelector('#skill-path');
              const description = document.querySelector('#skill-description');
              const response = await postJson('/dashboard/api/skills', {
                name: name.value.trim(),
                path: path.value.trim(),
                description: description.value.trim(),
                enabled: true
              });
              if (response.ok) {
                name.value = '';
                path.value = '';
                description.value = '';
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
              const name = document.querySelector('#mcp-name');
              const endpoint = document.querySelector('#mcp-endpoint');
              const response = await postJson('/dashboard/api/mcp-servers', {
                name: name.value.trim(), endpoint: endpoint.value.trim()
              });
              if (response.ok) {
                name.value = '';
                endpoint.value = '';
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

            function postJson(url, value) {
              return fetch(url, {
                method: 'POST',
                headers: { 'Content-Type': 'application/json' },
                body: JSON.stringify(value)
              });
            }

            refresh();
            setInterval(refresh, 5000);
          </script>
        </body>
        </html>
        """;
}
