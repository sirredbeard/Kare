using Kare.Service.Cache;

namespace Kare.Service.Dashboard;

/// <summary>Maps the local Kare operations dashboard.</summary>
public static class DashboardEndpoints
{
    public static IEndpointRouteBuilder MapDashboard(this IEndpointRouteBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.MapGet("/dashboard", static (HttpContext context) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            context.Response.Headers.ContentSecurityPolicy =
                "default-src 'self'; style-src 'unsafe-inline'; script-src 'unsafe-inline'; connect-src 'self'";
            context.Response.Headers.XContentTypeOptions = "nosniff";
            return Results.Content(Page, "text/html; charset=utf-8");
        });

        builder.MapGet("/dashboard/api/snapshot", static (IDashboardMetricsCollector collector) =>
        {
            var snapshot = new DashboardMetrics.Snapshot(
                DateTime.UtcNow,
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
            .auth { display: flex; gap: 8px; }
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
            footer { padding: 18px 0 32px; color: var(--muted); }
            @media (max-width: 900px) {
              .grid { grid-template-columns: repeat(2, minmax(0, 1fr)); }
              header { align-items: start; flex-direction: column; }
            }
            @media (max-width: 560px) {
              .grid { grid-template-columns: 1fr; }
              .wide { grid-column: span 1; }
              .auth { width: 100%; }
              .auth input { min-width: 0; flex: 1; }
            }
          </style>
        </head>
        <body>
          <header>
            <div>
              <h1>Kare</h1>
              <p><span class="status"><span id="dot" class="dot"></span><span id="connection">Connecting</span></span></p>
            </div>
            <div class="auth">
              <input id="token" type="password" autocomplete="off" placeholder="API key, if configured">
              <button id="save-token" type="button">Apply</button>
              <button id="refresh" type="button">Refresh</button>
            </div>
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
                <h2>Cache</h2>
                <div class="table-wrap"><table>
                  <thead><tr><th>Key</th><th>Created</th><th>Last used</th><th>Size</th><th></th></tr></thead>
                  <tbody id="cache"></tbody>
                </table></div>
              </article>
              <article class="card wide">
                <h2>Routing decision URLs</h2>
                <div class="table-wrap"><table>
                  <thead><tr><th>URL</th><th>Preference</th><th>Modified</th></tr></thead>
                  <tbody id="routes"></tbody>
                </table></div>
              </article>
              <article class="card wide">
                <h2>Local skills</h2>
                <div class="table-wrap"><table>
                  <thead><tr><th>Name</th><th>Description</th><th>Path</th><th>Modified</th></tr></thead>
                  <tbody id="skills"></tbody>
                </table></div>
              </article>
              <article class="card full">
                <h2>MCP servers</h2>
                <div class="table-wrap"><table>
                  <thead><tr><th>Name</th><th>Endpoint</th><th>Capabilities</th><th>Status</th><th>Last connected</th></tr></thead>
                  <tbody id="mcp"></tbody>
                </table></div>
              </article>
            </section>
            <footer id="generated">No data loaded.</footer>
          </main>
          <script>
            const tokenInput = document.querySelector('#token');
            const savedToken = sessionStorage.getItem('kare-dashboard-token') || '';
            tokenInput.value = savedToken;

            const escapeHtml = value => String(value ?? '')
              .replaceAll('&', '&amp;').replaceAll('<', '&lt;')
              .replaceAll('>', '&gt;').replaceAll('"', '&quot;')
              .replaceAll("'", '&#039;');
            const time = value => value ? new Date(value).toLocaleString() : 'n/a';
            const number = (value, suffix = '') => value == null ? 'n/a' : `${Number(value).toFixed(1)}${suffix}`;
            const empty = columns => `<tr><td class="empty" colspan="${columns}">No data yet.</td></tr>`;
            const headers = () => {
              const token = sessionStorage.getItem('kare-dashboard-token');
              return token ? { Authorization: `Bearer ${token}` } : {};
            };

            function render(snapshot) {
              const requests = snapshot.requests || [];
              const successful = requests.filter(item => item.succeeded).length;
              const rates = requests.map(item => item.decodeTokensPerSecond).filter(value => value != null);
              const local = requests.filter(item => !item.isBillable && item.route !== 'Cache').length;
              const billable = requests.filter(item => item.isBillable).length;

              document.querySelector('#requests-total').textContent = requests.length;
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
                <tr><td>${escapeHtml(item.url)}</td><td>${item.preferLocal ? 'local' : 'cloud'}</td>
                <td>${escapeHtml(time(item.lastModifiedAt))}</td></tr>`).join('') : empty(3);

              const skills = snapshot.skills || [];
              document.querySelector('#skills').innerHTML = skills.length ? skills.map(item => `
                <tr><td>${escapeHtml(item.name)}</td><td>${escapeHtml(item.description)}</td>
                <td>${escapeHtml(item.path)}</td><td>${escapeHtml(time(item.lastModifiedAt))}</td></tr>`).join('') : empty(4);

              const servers = snapshot.mcpServers || [];
              document.querySelector('#mcp').innerHTML = servers.length ? servers.map(item => `
                <tr><td>${escapeHtml(item.name)}</td><td>${escapeHtml(item.endpoint)}</td>
                <td>${escapeHtml((item.capabilities || []).join(', '))}</td>
                <td class="${item.connected ? 'good' : 'bad'}">${item.connected ? 'connected' : 'disconnected'}</td>
                <td>${escapeHtml(time(item.lastConnectedAt))}</td></tr>`).join('') : empty(5);

              document.querySelector('#generated').textContent = `Updated ${time(snapshot.generatedAt)}. Refreshes every 5 seconds.`;
            }

            async function refresh() {
              const dot = document.querySelector('#dot');
              const connection = document.querySelector('#connection');
              try {
                const response = await fetch('/dashboard/api/snapshot', { headers: headers(), cache: 'no-store' });
                if (response.status === 401) throw new Error('API key required');
                if (!response.ok) throw new Error(`Dashboard API returned ${response.status}`);
                render(await response.json());
                dot.className = 'dot ok';
                connection.textContent = 'Connected';
              } catch (error) {
                dot.className = 'dot error';
                connection.textContent = error.message;
              }
            }

            document.querySelector('#save-token').addEventListener('click', () => {
              const token = tokenInput.value.trim();
              if (token) sessionStorage.setItem('kare-dashboard-token', token);
              else sessionStorage.removeItem('kare-dashboard-token');
              refresh();
            });
            document.querySelector('#refresh').addEventListener('click', refresh);
            document.querySelector('#cache').addEventListener('click', async event => {
              const key = event.target.dataset.cacheKey;
              if (!key) return;
              const response = await fetch(`/dashboard/api/cache/${encodeURIComponent(key)}`, {
                method: 'DELETE', headers: headers()
              });
              if (response.ok) refresh();
            });

            refresh();
            setInterval(refresh, 5000);
          </script>
        </body>
        </html>
        """;
}
