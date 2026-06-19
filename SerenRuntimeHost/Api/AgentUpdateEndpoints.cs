using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

using SerenCluster;
using SerenCluster.Constants;
using SerenCluster.Models;

using SerenRuntimeHost.Configuration;

namespace SerenRuntimeHost.Api;

/// <summary>
/// <c>/api/v1/system/agent-update</c> and
/// <c>/api/v1/node/{node}/agent-update</c> - push a new seren-agent package
/// to all nodes (broadcast) or a single named node.
/// </summary>
/// <remarks>
/// The RuntimeHost reads <c>seren-agent.tar.gz</c> from the local path
/// configured in <c>runtime.agent_package_path</c> in seren-runtime.yaml,
/// then streams it to each target node's agent as multipart/form-data.
/// The agent saves the file and fires <c>seren-agent-update</c> in the
/// background before returning - the response arrives before the update
/// script completes. All endpoints require auth.
/// </remarks>
internal static class AgentUpdateEndpoints
{
    public static void Map(IEndpointRouteBuilder app, JetsonClusterClient cluster, RuntimeOptions runtime)
    {
        // -- Broadcast: push to all nodes in parallel ----------------------
        app.MapPost($"/api/{ResourceStrings.ApiVersion}/system/agent-update",
            async (CancellationToken ct) =>
        {
            var (packagePath, guardResult) = ResolvePackagePath(runtime);
            if (guardResult is not null) return guardResult;

            var agents = cluster.Agents;
            var tasks = agents.Select(async kv =>
            {
                var (nodeName, agent) = (kv.Key, kv.Value);

                if (string.IsNullOrWhiteSpace(agent.AgentUpdatePath))
                {
                    return new AgentUpdateNodeResult(Ok: false, Node: nodeName, Message: null,
                        Error: "agent_update_path not configured for this node");
                }

                // Each node gets its own stream - streams can't be shared
                // across concurrent readers.
                await using var stream = File.OpenRead(packagePath!);
                var result = await agent.PushAgentUpdateAsync(
                    stream, "seren-agent.tar.gz", agent.AgentUpdatePath, ct);
                return result is not null
                    ? new AgentUpdateNodeResult(result.Ok, nodeName, result.Message, result.Error)
                    : new AgentUpdateNodeResult(Ok: false, Node: nodeName, Message: null,
                        Error: "agent did not respond");
            });

            var results = await Task.WhenAll(tasks);
            var anyOk = results.Any(r => r.Ok);

            return Results.Json(new
            {
                ok = anyOk,
                total = results.Length,
                succeeded = results.Count(r => r.Ok),
                results = results.ToDictionary(r => r.Node, r => new
                {
                    ok = r.Ok,
                    message = r.Message,
                    error = r.Error,
                }),
            });
        });

        // -- Per-node: push to one named node ------------------------------
        app.MapPost($"/api/{ResourceStrings.ApiVersion}/node/{{node}}/agent-update",
            async (string node, CancellationToken ct) =>
        {
            var (packagePath, guardResult) = ResolvePackagePath(runtime);
            if (guardResult is not null) return guardResult;

            var agent = cluster.GetAgent(node);
            if (agent is null)
            {
                return Results.Json(
                    new { ok = false, error = "unknown_node", detail = $"'{node}' is not in the cluster config" },
                    statusCode: StatusCodes.Status404NotFound);
            }

            if (string.IsNullOrWhiteSpace(agent.AgentUpdatePath))
            {
                return Results.Json(
                    new { ok = false, error = "not_configured", detail = $"agent_update_path is not set for node '{node}' in seren-runtime.yaml" },
                    statusCode: StatusCodes.Status409Conflict);
            }

            await using var stream = File.OpenRead(packagePath!);
            var result = await agent.PushAgentUpdateAsync(
                stream, "seren-agent.tar.gz", agent.AgentUpdatePath, ct);

            if (result is null)
            {
                return Results.Json(
                    new { ok = false, node, error = "agent did not respond" },
                    statusCode: StatusCodes.Status503ServiceUnavailable);
            }

            return Results.Json(new { ok = result.Ok, node, message = result.Message, error = result.Error });
        });
    }

    /// <summary>
    /// Validates that <see cref="RuntimeOptions.AgentPackagePath"/> is
    /// configured and the file currently exists on the host filesystem.
    /// Returns <c>(resolvedPath, null)</c> on success, or
    /// <c>(null, errorResult)</c> when a guard fails.
    /// </summary>
    private static (string? path, IResult? error) ResolvePackagePath(RuntimeOptions runtime)
    {
        if (string.IsNullOrWhiteSpace(runtime.AgentPackagePath))
        {
            return (null, Results.Json(
                new
                {
                    ok = false,
                    error = "not_configured",
                    detail = "runtime.agent_package_path is not set in seren-runtime.yaml",
                },
                statusCode: StatusCodes.Status409Conflict));
        }

        var expanded = runtime.AgentPackagePath.StartsWith("~/")
            ? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                runtime.AgentPackagePath[2..])
            : runtime.AgentPackagePath;

        var resolved = Path.GetFullPath(expanded);
        if (!File.Exists(resolved))
        {
            return (null, Results.Json(
                new
                {
                    ok = false,
                    error = "package_not_found",
                    detail = $"seren-agent package not found at: {resolved}",
                },
                statusCode: StatusCodes.Status409Conflict));
        }

        return (resolved, null);
    }
}
