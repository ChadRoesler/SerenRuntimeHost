using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

using SerenCluster;
using SerenCluster.Constants;

namespace SerenRuntimeHost.Api
{

    /// <summary>
    /// <c>/api/v1/service/{name}/*</c> - universal lifecycle verbs for every
    /// service. Routes via <see cref="JetsonClusterClient"/> to whichever node
    /// is preferred for the capability.
    /// </summary>
    /// <remarks>
    /// Service-specific endpoints (kokoro/voices, comfy/checkpoints, chroma
    /// collections, etc.) are deliberately NOT mapped tonight. They each have
    /// nuances (multi-node aggregation? proxy uploads? write semantics?) that
    /// deserve focused sessions. For now, callers go via <c>/{name}/manifest</c>
    /// to discover what's installed and hit the agent directly if they need
    /// service-specifics.
    ///
    /// All endpoints under this prefix require auth (the public skip list
    /// only covers <c>/api/v1/system/{ping,version}</c>).
    /// </remarks>
    internal static class ServiceEndpoints
    {
        /// <summary>
        /// Names for which this RuntimeHost will mount routes. Determines
        /// what's reachable via <c>/api/v1/service/{name}/...</c>.
        /// Adding a new service = add the name here. Discovery still drives
        /// WHERE to route, this list just gates WHAT to route.
        /// </summary>
        private static readonly string[] KnownServices =
        [
            "llama", "kokoro", "comfy", "chroma", "whisper", "coral", "agent",
    ];

        public static void Map(IEndpointRouteBuilder app, JetsonClusterClient cluster)
        {
            foreach (var service in KnownServices)
            {
                MapForService(app, cluster, service);
            }

            // Per-node passthrough routes. Same verbs as MapForService, but
            // pinned to the node in the URL rather than cluster-routed by
            // preference. Phase 3 of the dashboard needs these - when you tap
            // a service row inside the node drill-down, you're asking about
            // THAT NODE's view of the service, not "whichever node the cluster
            // prefers." Cluster-routed paths are correct for "fire this work
            // somewhere"; per-node paths are correct for inspect-and-manage.
            //
            // This is the same bug pattern that bit us in Phase 2 with
            // /service/{name}/status - preferred-routing tossed the answer for
            // any non-preferred node. Per-node URLs sidestep it entirely.
            MapPerNode(app, cluster);
        }

        private static void MapForService(IEndpointRouteBuilder app, JetsonClusterClient cluster, string service)
        {
            // Manifest - proxies to chosen node's agent
            app.MapGet($"/api/{ResourceStrings.ApiVersion}/service/{service}/manifest", async (CancellationToken ct) =>
            {
                var (agent, err) = ResolveAgent(cluster, service);
                if (agent is null) return err!;

                var m = await agent.GetServiceManifestAsync(service, ct);
                if (m is null)
                {
                    cluster.MarkNodeOffline(agent.NodeName, $"manifest fetch failed for '{service}'");
                    return ServiceUnavailable($"agent on '{agent.NodeName}' did not respond");
                }
                return Results.Json(new { node = agent.NodeName, manifest = m });
            });

            // Status (PID, memory, port health)
            app.MapGet($"/api/{ResourceStrings.ApiVersion}/service/{service}/status", async (CancellationToken ct) =>
            {
                var (agent, err) = ResolveAgent(cluster, service);
                if (agent is null) return err!;

                var s = await agent.GetServiceStatusAsync(service, ct);
                if (s is null)
                {
                    cluster.MarkNodeOffline(agent.NodeName, $"status fetch failed for '{service}'");
                    return ServiceUnavailable($"agent on '{agent.NodeName}' did not respond");
                }
                return Results.Json(new { node = agent.NodeName, status = s });
            });

            // Health (port probe)
            app.MapGet($"/api/{ResourceStrings.ApiVersion}/service/{service}/health", async (CancellationToken ct) =>
            {
                var (agent, err) = ResolveAgent(cluster, service);
                if (agent is null) return err!;

                var h = await agent.GetServiceHealthAsync(service, ct);
                if (h is null)
                {
                    cluster.MarkNodeOffline(agent.NodeName, $"health fetch failed for '{service}'");
                    return ServiceUnavailable($"agent on '{agent.NodeName}' did not respond");
                }
                return Results.Json(new { node = agent.NodeName, health = h });
            });

            // Lifecycle
            app.MapPost($"/api/{ResourceStrings.ApiVersion}/service/{service}/start", async (CancellationToken ct) =>
            {
                var (agent, err) = ResolveAgent(cluster, service);
                if (agent is null) return err!;

                var r = await agent.StartServiceAsync(service, ct);
                if (r is null) return ServiceUnavailable($"agent on '{agent.NodeName}' did not respond");
                return Results.Json(new { node = agent.NodeName, result = r });
            });

            app.MapPost($"/api/{ResourceStrings.ApiVersion}/service/{service}/stop", async (CancellationToken ct) =>
            {
                var (agent, err) = ResolveAgent(cluster, service);
                if (agent is null) return err!;

                var r = await agent.StopServiceAsync(service, ct);
                if (r is null) return ServiceUnavailable($"agent on '{agent.NodeName}' did not respond");
                return Results.Json(new { node = agent.NodeName, result = r });
            });

            app.MapPost($"/api/{ResourceStrings.ApiVersion}/service/{service}/restart", async (CancellationToken ct) =>
            {
                var (agent, err) = ResolveAgent(cluster, service);
                if (agent is null) return err!;

                var r = await agent.RestartServiceAsync(service, ct);
                if (r is null) return ServiceUnavailable($"agent on '{agent.NodeName}' did not respond");
                return Results.Json(new { node = agent.NodeName, result = r });
            });

            // Logs
            app.MapGet($"/api/{ResourceStrings.ApiVersion}/service/{service}/logs", async (int? lines, CancellationToken ct) =>
            {
                var (agent, err) = ResolveAgent(cluster, service);
                if (agent is null) return err!;

                var n = Math.Clamp(lines ?? 100, 1, 10_000);
                var l = await agent.GetServiceLogsAsync(service, n, ct);
                if (l is null) return ServiceUnavailable($"agent on '{agent.NodeName}' did not respond");
                return Results.Json(new { node = agent.NodeName, logs = l });
            });

            // Models - service-specific (llama, comfy, whisper). Returns
            // the agent's /models payload as-is since per-service shapes differ.
            // Routed to the preferred node for the service. If a service doesn't
            // have a /models endpoint (e.g. kokoro), the agent returns 404 which
            // we surface as 502 (cluster-level unavailable).
            app.MapGet($"/api/{ResourceStrings.ApiVersion}/service/{service}/models", async (CancellationToken ct) =>
            {
                var (agent, err) = ResolveAgent(cluster, service);
                if (agent is null) return err!;

                var models = await agent.GetServiceModelsAsync(service, ct);
                if (models is null) return ServiceUnavailable($"agent on '{agent.NodeName}' did not respond");
                return Results.Json(new { node = agent.NodeName, models });
            });
        }

        /// <summary>
        /// Mount parameterized per-node routes:
        ///   GET  /api/v1/node/{node}/service/{svc}/manifest
        ///   GET  /api/v1/node/{node}/service/{svc}/status
        ///   GET  /api/v1/node/{node}/service/{svc}/health
        ///   GET  /api/v1/node/{node}/service/{svc}/logs?lines=N
        ///   GET  /api/v1/node/{node}/service/{svc}/models
        ///   POST /api/v1/node/{node}/service/{svc}/start
        ///   POST /api/v1/node/{node}/service/{svc}/stop
        ///   POST /api/v1/node/{node}/service/{svc}/restart
        ///
        /// One route per verb, parameterized - no Cartesian product of
        /// (node × service) registered at startup. Resolution happens at
        /// request time against the cluster's agent dictionary.
        ///
        /// Validates {svc} against KnownServices to avoid arbitrary route
        /// values being plumbed through to the agent (defense in depth).
        /// </summary>
        private static void MapPerNode(IEndpointRouteBuilder app, JetsonClusterClient cluster)
        {
            var basePath = $"/api/{ResourceStrings.ApiVersion}/node/{{node}}/service/{{svc}}";

            app.MapGet($"{basePath}/manifest", async (string node, string svc, CancellationToken ct) =>
            {
                var (agent, err) = ResolvePerNodeAgent(cluster, node, svc);
                if (agent is null) return err!;

                var m = await agent.GetServiceManifestAsync(svc, ct);
                if (m is null) return ServiceUnavailable($"agent on '{node}' did not respond");
                return Results.Json(new { node, manifest = m });
            });

            app.MapGet($"{basePath}/status", async (string node, string svc, CancellationToken ct) =>
            {
                var (agent, err) = ResolvePerNodeAgent(cluster, node, svc);
                if (agent is null) return err!;

                var s = await agent.GetServiceStatusAsync(svc, ct);
                if (s is null) return ServiceUnavailable($"agent on '{node}' did not respond");
                return Results.Json(new { node, status = s });
            });

            app.MapGet($"{basePath}/health", async (string node, string svc, CancellationToken ct) =>
            {
                var (agent, err) = ResolvePerNodeAgent(cluster, node, svc);
                if (agent is null) return err!;

                var h = await agent.GetServiceHealthAsync(svc, ct);
                if (h is null) return ServiceUnavailable($"agent on '{node}' did not respond");
                return Results.Json(new { node, health = h });
            });

            app.MapGet($"{basePath}/logs", async (string node, string svc, int? lines, CancellationToken ct) =>
            {
                var (agent, err) = ResolvePerNodeAgent(cluster, node, svc);
                if (agent is null) return err!;

                var n = Math.Clamp(lines ?? 100, 1, 10_000);
                var l = await agent.GetServiceLogsAsync(svc, n, ct);
                if (l is null) return ServiceUnavailable($"agent on '{node}' did not respond");
                return Results.Json(new { node, logs = l });
            });

            // Per-service models endpoint. Only implemented by services that
            // actually have models to enumerate (llama, comfy, whisper). Other
            // services return 404 from the agent, which we propagate as 502
            // here since "model list unavailable" is a different failure shape
            // than "node unreachable."
            //
            // Wired up specifically to support ModelTools.cs in the MCP server,
            // which the LLM uses to answer "what models do you have?" without
            // hallucinating capabilities.
            app.MapGet($"{basePath}/models", async (string node, string svc, CancellationToken ct) =>
            {
                var (agent, err) = ResolvePerNodeAgent(cluster, node, svc);
                if (agent is null) return err!;

                var models = await agent.GetServiceModelsAsync(svc, ct);
                if (models is null) return ServiceUnavailable($"agent on '{node}' did not respond");
                return Results.Json(new { node, models });
            });

            app.MapPost($"{basePath}/start", async (string node, string svc, CancellationToken ct) =>
            {
                var (agent, err) = ResolvePerNodeAgent(cluster, node, svc);
                if (agent is null) return err!;

                var r = await agent.StartServiceAsync(svc, ct);
                if (r is null) return ServiceUnavailable($"agent on '{node}' did not respond");
                return Results.Json(new { node, result = r });
            });

            app.MapPost($"{basePath}/stop", async (string node, string svc, CancellationToken ct) =>
            {
                var (agent, err) = ResolvePerNodeAgent(cluster, node, svc);
                if (agent is null) return err!;

                var r = await agent.StopServiceAsync(svc, ct);
                if (r is null) return ServiceUnavailable($"agent on '{node}' did not respond");
                return Results.Json(new { node, result = r });
            });

            app.MapPost($"{basePath}/restart", async (string node, string svc, CancellationToken ct) =>
            {
                var (agent, err) = ResolvePerNodeAgent(cluster, node, svc);
                if (agent is null) return err!;

                var r = await agent.RestartServiceAsync(svc, ct);
                if (r is null) return ServiceUnavailable($"agent on '{node}' did not respond");
                return Results.Json(new { node, result = r });
            });
        }

        /// <summary>
        /// Look up the agent for a specific (node, service) pair. Validates:
        ///   - Service is in <see cref="KnownServices"/> (defense in depth)
        ///   - Node exists in the cluster config
        ///   - We have an agent client for it
        /// Returns 404 for unknown nodes, 503 for unknown services.
        /// </summary>
        private static (JetsonAgentClient? agent, IResult? err) ResolvePerNodeAgent(
            JetsonClusterClient cluster, string node, string svc)
        {
            // Service whitelist - without this someone could probe
            // /api/v1/node/x/service/etc-passwd/start and we'd cheerfully
            // forward to the agent. Agent would reject (no manifest matches)
            // but we'd rather fail at the gateway.
            if (Array.IndexOf(KnownServices, svc) < 0)
            {
                return (null, Results.Json(
                    new { error = "unknown_service", detail = $"'{svc}' is not a known service" },
                    statusCode: StatusCodes.Status404NotFound));
            }

            var agent = cluster.GetAgent(node);
            if (agent is null)
            {
                return (null, Results.Json(
                    new { error = "unknown_node", detail = $"'{node}' is not in the cluster config" },
                    statusCode: StatusCodes.Status404NotFound));
            }

            return (agent, null);
        }

        /// <summary>
        /// Resolve the agent client for a service via the cluster's preferred
        /// node selection. Returns <c>(null, errorResult)</c> if no online node
        /// has this service.
        /// </summary>
        private static (JetsonAgentClient? agent, IResult? err) ResolveAgent(
            JetsonClusterClient cluster, string service)
        {
            var node = cluster.ChooseNodeFor(service);
            if (node is null)
                return (null, NotInstalledAnywhere(service));

            var agent = cluster.GetAgent(node.Name);
            if (agent is null)
                return (null, NotInstalledAnywhere(service)); // shouldn't happen
            return (agent, null);
        }

        private static IResult NotInstalledAnywhere(string service) =>
            Results.Json(
                new
                {
                    error = "service_unavailable",
                    detail = $"no online node has '{service}' installed",
                },
                statusCode: StatusCodes.Status503ServiceUnavailable);

        private static IResult ServiceUnavailable(string detail) =>
            Results.Json(
                new { error = "agent_unreachable", detail },
                statusCode: StatusCodes.Status503ServiceUnavailable);
    }
}