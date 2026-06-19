using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

using SerenCluster;
using SerenCluster.Constants;

namespace SerenRuntimeHost.Api
{

/// <summary>
/// <c>/api/v1/system/*</c> - RuntimeHost-level endpoints.
/// </summary>
/// <remarks>
/// These are about the RuntimeHost itself + cluster aggregation. Compare
/// with the per-Jetson agent's <c>/api/v1/system/*</c> which is about a
/// single node.
/// </remarks>
    internal static class SystemEndpoints
    {
        public static void Map(IEndpointRouteBuilder app, JetsonClusterClient cluster, bool healthStrictMode)
        {
            // Public - no auth required (matches agent contract for ping/version).
            app.MapGet($"/api/{ResourceStrings.ApiVersion}/system/ping", () =>
                Results.Json(new { ok = true, ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds() }));

            app.MapGet($"/api/{ResourceStrings.ApiVersion}/system/version", () =>
                Results.Json(new
                {
                    runtime_version = ThisAssemblyVersion(),
                    api_version = ResourceStrings.ApiVersion,
                }));

            // Authed - full cluster status. Aggregates from every configured node.
            //
            // Per-node fan-out: node info + thermal + services list, all in parallel,
            // swallowing errors (a node that's slow to respond shouldn't block the
            // rest of the rollup).
            //
            // services_detail is the per-node {svc: {manifest, status}} block from
            // the agent's /api/v1/system/services endpoint. The dashboard uses this
            // to render per-service memory/PID/health WITHOUT going through cluster
            // routing - each node's agent is the truth source for that node's
            // services. (The previous design had the dashboard call /api/v1/service/
            // {name}/status per service, but that goes through preferred-node routing,
            // which means a service installed on multiple nodes only returns data for
            // the preferred one. Bug: every node except the preferred one showed
            // empty.)
            app.MapGet($"/api/{ResourceStrings.ApiVersion}/system/status", async (CancellationToken ct) =>
            {
                var snapshots = cluster.GetSnapshots();
                var perNode = await Task.WhenAll(cluster.Agents.Select(async kv =>
                {
                    var snap = snapshots.GetValueOrDefault(kv.Key);

                    // Three concurrent fetches per node - node info, thermal, and
                    // the full services list with statuses. Total wall time is
                    // max(slowest), not sum, so this is fast.
                    var nodeTask = SafeGet(() => kv.Value.GetNodeAsync(ct));
                    var thermalTask = SafeGet(() => kv.Value.GetThermalAsync(ct));
                    var servicesTask = SafeGet(() => kv.Value.GetServicesAsync(ct));
                    await Task.WhenAll(nodeTask, thermalTask, servicesTask);

                    // -- Online-flag reconciliation --
                    //
                    // Snapshot's `online` is updated by the periodic discovery sweep.
                    // If discovery hasn't run since the node went down, snapshot may
                    // say `online: true` while every SafeGet returned null (node is
                    // actually down, we just haven't caught up yet).
                    //
                    // Trust the live SafeGets over the cached snapshot. If all three
                    // returned null, the node IS effectively unreachable right now -
                    // mark it offline in the response AND poke the cluster client to
                    // update its snapshot so subsequent calls converge to truth.
                    //
                    // The other way (snap says down but SafeGets succeeded) doesn't
                    // happen in practice - if the gets succeeded, the agent is up,
                    // and snapshot will be refreshed on next discovery tick. We use
                    // snap as the floor: if snap is offline, we report offline even
                    // if a one-off SafeGet succeeded during a flap.
                    var allGetsFailed = nodeTask.Result is null
                                     && thermalTask.Result is null
                                     && servicesTask.Result is null;

                    var online = (snap?.Online ?? false) && !allGetsFailed;

                    if (allGetsFailed && (snap?.Online ?? false))
                    {
                        // Snapshot lies about being up - tell the cluster client so
                        // the next /system/status converges. Cheap; just updates the
                        // in-memory snapshot.
                        cluster.MarkNodeOffline(kv.Key, "all status fetches failed during aggregate query");
                    }

                    return new
                    {
                        name = kv.Key,
                        nickname = kv.Value.Nickname,
                        is_host = kv.Value.IsHost,
                        online,
                        last_probed = snap?.LastProbed.ToString("o"),
                        last_error = snap?.LastError,
                        installed_services = snap?.InstalledServices ?? [],
                        agent_node = nodeTask.Result,
                        thermal = thermalTask.Result,
                        // Per-service detail straight from this node's agent.
                        // Shape: {<svc_name>: {manifest, status}, ...}
                        services_detail = servicesTask.Result?.Services,
                    };
                }));

                return Results.Json(new
                {
                    nodes = perNode,
                    node_count = perNode.Length,
                    online_count = perNode.Count(n => n.online),
                });
            });

            // Rollup health. Returns 200 with degraded payload OR 503 depending
            // on strict mode.
            app.MapGet($"/api/{ResourceStrings.ApiVersion}/system/health", async (CancellationToken ct) =>
            {
                var perNode = await Task.WhenAll(cluster.Agents.Select(async kv =>
                {
                    var ping = await kv.Value.PingAsync(ct);
                    return (Name: kv.Key, Reachable: ping is { Ok: true });
                }));

                var unreachable = perNode.Where(n => !n.Reachable).Select(n => n.Name).ToList();
                var allOk = unreachable.Count == 0;

                var payload = new
                {
                    ok = allOk,
                    status = allOk ? "ok" : "degraded",
                    total = perNode.Length,
                    reachable = perNode.Count(n => n.Reachable),
                    unreachable,
                };

                if (!allOk && healthStrictMode)
                    return Results.Json(payload, statusCode: StatusCodes.Status503ServiceUnavailable);

                return Results.Json(payload);
            });

            // Reclaim memory across the cluster - tells every agent to stop
            // non-excluded services. Useful for "free up memory" dashboard button.
            app.MapPost($"/api/{ResourceStrings.ApiVersion}/system/reclaim", async (HttpContext http, CancellationToken ct) =>
            {
                // Optional body: { "exclude": ["llama"], "nodes": ["xavier32gb"] }
                ReclaimRequest? body = null;
                if (http.Request.ContentLength is > 0)
                {
                    try { body = await http.Request.ReadFromJsonAsync<ReclaimRequest>(ct); }
                    catch { /* keep default */ }
                }
                body ??= new ReclaimRequest(null, null);

                var targets = body.Nodes is { Count: > 0 }
                    ? cluster.Agents.Where(kv => body.Nodes.Contains(kv.Key)).ToList()
                    : cluster.Agents.ToList();

                var perNode = await Task.WhenAll(targets.Select(async kv =>
                {
                    var resp = await kv.Value.ReclaimAsync(body.Exclude, ct);
                    return new
                    {
                        node = kv.Key,
                        ok = resp is not null,
                        stopped = resp?.Stopped ?? [],
                        kept = resp?.Kept ?? [],
                        failed = resp?.Failed ?? [],
                    };
                }));

                return Results.Json(new
                {
                    ok = perNode.All(p => p.ok),
                    nodes = perNode,
                });
            });

            // -- Per-node reboot -------------------------------------------
            // Cluster-wide reboot is intentionally NOT exposed - too easy a
            // foot-cannon, and the dashboard's UX never wants to do it.
            // If you ever need it, ssh and `for h in ...; do ssh $h sudo
            // shutdown -r +1; done` is the right tool.
            app.MapPost($"/api/{ResourceStrings.ApiVersion}/system/reboot/{{node}}",
                async (string node, HttpContext http, CancellationToken ct) =>
            {
                if (!cluster.Agents.TryGetValue(node, out var agent))
                {
                    return Results.NotFound(new { error = $"unknown node '{node}'" });
                }

                // Optional body: { "delay_minutes": 1 }. Default 1 minute -
                // enough for the response to flush AND for the user to hit
                // the Cancel button if they panic.
                int delay = 1;
                if (http.Request.ContentLength is > 0)
                {
                    try
                    {
                        var body = await http.Request.ReadFromJsonAsync<RebootRequest>(ct);
                        if (body?.DelayMinutes is int d) delay = d;
                    }
                    catch { /* keep default */ }
                }

                var resp = await agent.RebootAsync(delay, ct);
                if (resp is null)
                {
                    return Results.Json(new
                    {
                        node,
                        scheduled = false,
                        error = "agent unreachable or returned no body",
                    }, statusCode: 502);
                }

                return Results.Json(new
                {
                    node,
                    resp.Scheduled,
                    resp.ScheduledAt,
                    resp.DelayMinutes,
                    resp.Method,
                    resp.Error,
                    resp.Hint,
                });
            });

            app.MapPost($"/api/{ResourceStrings.ApiVersion}/system/reboot/{{node}}/cancel",
                async (string node, CancellationToken ct) =>
            {
                if (!cluster.Agents.TryGetValue(node, out var agent))
                {
                    return Results.NotFound(new { error = $"unknown node '{node}'" });
                }

                var resp = await agent.RebootCancelAsync(ct);
                if (resp is null)
                {
                    return Results.Json(new
                    {
                        node,
                        cancelled = false,
                        error = "agent unreachable or returned no body",
                    }, statusCode: 502);
                }

                return Results.Json(new { node, resp.Cancelled, resp.Error });
            });
        }

        private static string ThisAssemblyVersion() =>
            typeof(SystemEndpoints).Assembly.GetName().Version?.ToString() ?? "unknown";

        /// <summary>
        /// Wrap an agent client call so any thrown exception becomes null.
        /// The status rollup must NOT throw if one node misbehaves - partial
        /// data is better than a 500 response that breaks the dashboard.
        /// </summary>
        private static async Task<T?> SafeGet<T>(Func<Task<T?>> op) where T : class
        {
            try { return await op(); }
            catch { return null; }
        }

        private sealed record ReclaimRequest(List<string>? Exclude, List<string>? Nodes);
        private sealed record RebootRequest(int? DelayMinutes);
    }
}