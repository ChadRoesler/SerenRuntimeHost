using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

using SerenCluster;
using SerenCluster.Constants;

namespace SerenRuntimeHost.Api
{

    /// <summary>
    /// <c>/api/v1/cluster/*</c> - cluster topology + manual rediscovery.
    /// </summary>
    internal static class ClusterEndpoints
    {
        public static void Map(IEndpointRouteBuilder app, JetsonClusterClient cluster)
        {
            // Force eager rediscovery of all nodes. Useful when you've shut a
            // Jetson down and want the cluster client to know NOW.
            app.MapPost($"/api/{ResourceStrings.ApiVersion}/cluster/refresh", async (CancellationToken ct) =>
            {
                var summary = await cluster.RefreshAsync(ct);
                return Results.Json(new
                {
                    ok = true,
                    total = summary.TotalNodes,
                    online = summary.OnlineNodes,
                    nodes = summary.PerNode.ToDictionary(
                        kv => kv.Key,
                        kv => new
                        {
                            online = kv.Value.Online,
                            installed_services = kv.Value.InstalledServices,
                            last_error = kv.Value.LastError,
                            last_probed = kv.Value.LastProbed.ToString("o"),
                        }),
                });
            });

            // Refresh just one node - cheaper, used after a known state change.
            app.MapPost($"/api/{ResourceStrings.ApiVersion}/cluster/refresh/{{node}}", async (string node, CancellationToken ct) =>
            {
                var snap = await cluster.RefreshNodeAsync(node, ct);
                if (snap is null)
                    return Results.Json(new { ok = false, error = $"unknown node: {node}" },
                        statusCode: StatusCodes.Status404NotFound);

                return Results.Json(new
                {
                    ok = true,
                    node,
                    online = snap.Online,
                    installed_services = snap.InstalledServices,
                    last_error = snap.LastError,
                    last_probed = snap.LastProbed.ToString("o"),
                });
            });

            // Capability map - "where is X right now?"
            app.MapGet($"/api/{ResourceStrings.ApiVersion}/cluster/capabilities", () =>
            {
                var snapshots = cluster.GetSnapshots();
                // Build inverse: capability → [nodes that have it]
                var cap = new Dictionary<string, List<string>>();
                foreach (var (nodeName, snap) in snapshots)
                {
                    if (!snap.Online) continue;
                    foreach (var svc in snap.InstalledServices)
                    {
                        if (!cap.TryGetValue(svc, out var list))
                            cap[svc] = list = [];
                        list.Add(nodeName);
                    }
                }
                return Results.Json(new { capabilities = cap });
            });
        }
    }
}