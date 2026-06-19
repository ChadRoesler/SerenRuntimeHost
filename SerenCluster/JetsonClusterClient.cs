using SerenCluster.Configuration;
using SerenCluster.Models;
using System.Collections.Concurrent;

namespace SerenCluster;

/// <summary>
/// Cluster-wide capability router. Owns one <see cref="JetsonAgentClient"/>
/// per configured node and a capability map (<c>kokoro → [xavier32gb, ...]</c>).
/// Workers ask <see cref="GetServiceUrlAsync"/> "where does X live right now"
/// and connect directly to the resolved URL.
/// </summary>
/// <remarks>
/// Routing policy (current): preferred-list order. For each capability,
/// candidates are ranked by:
///   1. nodes whose <c>preferred_for</c> includes the capability,
///      in declaration order
///   2. nodes that have the service installed but didn't declare
///      preference, in declaration order
/// The first candidate that's currently online wins. Failure-driven
/// invalidation marks a node offline immediately on connection failure;
/// the next candidate gets the next request.
///
/// Round-robin and stickiness are deliberately NOT implemented yet -
/// they're optimizations on top of this primitive, easy to add later
/// without changing the public API.
/// </remarks>
public sealed class JetsonClusterClient : IDisposable
{
    private readonly ClusterOptions _options;
    private readonly Dictionary<string, JetsonAgentClient> _agents;
    private readonly Action<string> _log;

    /// <summary>
    /// Per-node capability snapshot. Updated by <see cref="RefreshAsync"/>
    /// (eager + scheduled) and <see cref="MarkNodeOfflineAsync"/>
    /// (failure-driven).
    /// </summary>
    private readonly ConcurrentDictionary<string, NodeSnapshot> _snapshots = new();

    public JetsonClusterClient(ClusterOptions options, Action<string>? log = null)
    {
        _options = options;
        _log = log ?? (msg => Console.WriteLine($"[cluster] {msg}"));

        if (options.Nodes.Count == 0)
        {
            throw new InvalidOperationException(
                "ClusterOptions.Nodes is empty. Even local-only setups must configure " +
                "a single node pointing at 127.0.0.1 - see seren-runtime.yaml.sample.");
        }

        // Sanity: node names must be unique. Routing relies on this.
        var dupes = options.Nodes.GroupBy(n => n.Name)
            .Where(g => g.Count() > 1).Select(g => g.Key).ToList();
        if (dupes.Count > 0)
        {
            throw new InvalidOperationException(
                $"Duplicate node name(s) in cluster config: {string.Join(", ", dupes)}");
        }

        _agents = options.Nodes.ToDictionary(
            n => n.Name,
            n => new JetsonAgentClient(n, msg => _log(msg)));
    }

    public void Dispose()
    {
        foreach (var agent in _agents.Values)
            agent.Dispose();
    }

    /// <summary>The node names this cluster knows about (config order).</summary>
    public IReadOnlyList<string> NodeNames =>
        _options.Nodes.Select(n => n.Name).ToList();

    /// <summary>
    /// Returns the agent client for a specific node, or null if no such
    /// node is configured. Used by RuntimeHost endpoints that need to
    /// call agent methods directly (e.g. <c>POST /api/v1/cluster/refresh/{node}</c>).
    /// </summary>
    public JetsonAgentClient? GetAgent(string nodeName) =>
        _agents.GetValueOrDefault(nodeName);

    /// <summary>
    /// All configured agents. Used by RuntimeHost endpoints that aggregate
    /// across the cluster (e.g. <c>GET /api/v1/system/health</c>).
    /// </summary>
    public IReadOnlyDictionary<string, JetsonAgentClient> Agents => _agents;

    // -------------------------------------------------------------
    //  Discovery
    // -------------------------------------------------------------

    /// <summary>
    /// Re-queries every configured node's <c>/api/v1/system/services</c> in
    /// parallel and rebuilds the capability map. Called at startup, by the
    /// background discovery service on the configured interval, and on
    /// demand via <c>POST /api/v1/cluster/refresh</c>.
    /// </summary>
    public async Task<RefreshSummary> RefreshAsync(CancellationToken ct = default)
    {
        var tasks = _agents.Select(async kv =>
            (Name: kv.Key, Snapshot: await ProbeNodeAsync(kv.Value, ct)));

        var results = await Task.WhenAll(tasks);
        foreach (var (name, snapshot) in results)
        {
            _snapshots[name] = snapshot;
        }

        var online = results.Count(r => r.Snapshot.Online);
        _log($"refresh: {online}/{results.Length} nodes online");
        return new RefreshSummary(
            TotalNodes: results.Length,
            OnlineNodes: online,
            PerNode: results.ToDictionary(r => r.Name, r => r.Snapshot));
    }

    /// <summary>
    /// Refresh just one node - used after a failure to re-validate that
    /// node's state. Cheaper than full refresh.
    /// </summary>
    public async Task<NodeSnapshot?> RefreshNodeAsync(string nodeName, CancellationToken ct = default)
    {
        if (!_agents.TryGetValue(nodeName, out var agent))
            return null;

        var snapshot = await ProbeNodeAsync(agent, ct);
        _snapshots[nodeName] = snapshot;
        _log($"refresh-node {nodeName}: online={snapshot.Online} services={snapshot.InstalledServices.Count}");
        return snapshot;
    }

    /// <summary>
    /// Mark a node offline without probing. Called by failure-driven
    /// invalidation when a worker's data-plane call fails.
    /// </summary>
    public void MarkNodeOffline(string nodeName, string reason)
    {
        if (_snapshots.TryGetValue(nodeName, out var prev))
        {
            _snapshots[nodeName] = prev with { Online = false, LastError = reason };
            _log($"marked offline: {nodeName} ({reason})");
        }
    }

    private async Task<NodeSnapshot> ProbeNodeAsync(JetsonAgentClient agent, CancellationToken ct)
    {
        // Honor the per-node discovery timeout. The agent client's own 35s
        // timeout is for lifecycle ops; for discovery we want fail-fast.
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(_options.DiscoveryTimeout);

        try
        {
            var services = await agent.GetServicesAsync(timeoutCts.Token);
            if (services is null)
            {
                return new NodeSnapshot(
                    Online: false,
                    InstalledServices: [],
                    Status: new Dictionary<string, ServiceStatus?>(),
                    LastError: "agent returned null (unreachable or auth failed)",
                    LastProbed: DateTimeOffset.UtcNow);
            }

            return new NodeSnapshot(
                Online: true,
                InstalledServices: services.Services.Keys.ToList(),
                Status: services.Services.ToDictionary(kv => kv.Key, kv => kv.Value.Status),
                LastError: null,
                LastProbed: DateTimeOffset.UtcNow);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return new NodeSnapshot(
                Online: false,
                InstalledServices: [],
                Status: new Dictionary<string, ServiceStatus?>(),
                LastError: $"discovery timed out after {_options.DiscoveryTimeout.TotalSeconds}s",
                LastProbed: DateTimeOffset.UtcNow);
        }
    }

    // -------------------------------------------------------------
    //  Routing
    // -------------------------------------------------------------

    /// <summary>
    /// Resolves "where is service X right now?" - returns the URL of the
    /// chosen node's service port (e.g. <c>http://192.168.0.102:8090</c>
    /// for llama on xavier32gb). Returns null if no online node has the
    /// service installed.
    /// </summary>
    /// <remarks>
    /// Workers should call this for every request, NOT cache the URL -
    /// failure-driven invalidation may have moved the route since last
    /// time. The cluster client caches internally; this is cheap.
    /// </remarks>
    public async Task<RoutedService?> GetServiceUrlAsync(string capability, CancellationToken ct = default)
    {
        var node = ChooseNodeFor(capability);
        if (node is null)
        {
            _log($"no online node available for capability '{capability}'");
            return null;
        }

        // We have the node's name and the capability. We need the agent's
        // ServiceManifest to know the service's port and base URL. Use the
        // cached snapshot's status if available; otherwise fetch fresh.
        var snapshot = _snapshots.GetValueOrDefault(node.Name);
        if (snapshot is null || !snapshot.InstalledServices.Contains(capability))
        {
            // Cold path - node not in snapshot yet. Force a refresh.
            snapshot = await RefreshNodeAsync(node.Name, ct);
            if (snapshot is null || !snapshot.Online || !snapshot.InstalledServices.Contains(capability))
                return null;
        }

        var manifest = await GetAgent(node.Name)!.GetServiceManifestAsync(capability, ct);
        if (manifest is null) return null;

        // Library-mode services (port=0) have no HTTP endpoint to route to.
        // Caller (worker) must use a different access path (e.g. chroma is
        // accessed via the agent's per-service endpoints, not directly).
        if (manifest.Port <= 0)
        {
            return new RoutedService(
                NodeName: node.Name,
                Capability: capability,
                BaseUrl: null,
                Manifest: manifest,
                LibraryMode: true);
        }

        // Build the URL from the agent's host + the service's port. We
        // assume the service binds 0.0.0.0 (which is how every install
        // script we ship configures them).
        var agentBase = GetAgent(node.Name)!.BaseAddress;
        var serviceUrl = $"{agentBase.Scheme}://{agentBase.Host}:{manifest.Port}";

        return new RoutedService(
            NodeName: node.Name,
            Capability: capability,
            BaseUrl: serviceUrl,
            Manifest: manifest,
            LibraryMode: false);
    }

    /// <summary>
    /// Pick the best node for a capability. Considers preferred_for first,
    /// then falls through to any other node that has the service installed.
    /// Returns null if no online node has it.
    /// </summary>
    public JetsonNodeOptions? ChooseNodeFor(string capability)
    {
        // First tier: nodes whose preferred_for includes this capability,
        // in config-declaration order.
        foreach (var node in _options.Nodes.Where(n => n.PreferredFor.Contains(capability)))
        {
            if (IsOnlineWith(node.Name, capability))
            {
                _log($"routing '{capability}' → '{node.Name}' (preferred)");
                return node;
            }
        }

        // Second tier: nodes that have it installed but didn't declare
        // preference. Same config-declaration order.
        foreach (var node in _options.Nodes.Where(n => !n.PreferredFor.Contains(capability)))
        {
            if (IsOnlineWith(node.Name, capability))
            {
                _log($"routing '{capability}' → '{node.Name}' (fallback - no preferred node online)");
                return node;
            }
        }

        _log($"routing '{capability}' → unavailable (no online node has it installed)");
        return null;
    }

    private bool IsOnlineWith(string nodeName, string capability)
    {
        if (!_snapshots.TryGetValue(nodeName, out var snap))
            return false;
        return snap.Online && snap.InstalledServices.Contains(capability);
    }

    /// <summary>
    /// Read-only view of the current capability map. Useful for diagnostics
    /// (the <c>/api/v1/cluster/refresh</c> endpoint includes this in its
    /// response).
    /// </summary>
    public IReadOnlyDictionary<string, NodeSnapshot> GetSnapshots() =>
        _snapshots.ToDictionary(kv => kv.Key, kv => kv.Value);
}

/// <summary>
/// Per-node state cached by <see cref="JetsonClusterClient"/>.
/// </summary>
public sealed record NodeSnapshot(
    bool Online,
    List<string> InstalledServices,
    Dictionary<string, ServiceStatus?> Status,
    string? LastError,
    DateTimeOffset LastProbed);

/// <summary>
/// Result of <see cref="JetsonClusterClient.GetServiceUrlAsync"/>.
/// <see cref="BaseUrl"/> is null for library-mode services like chroma -
/// callers should detect this and access via the agent's endpoints instead.
/// </summary>
public sealed record RoutedService(
    string NodeName,
    string Capability,
    string? BaseUrl,
    ServiceManifest Manifest,
    bool LibraryMode);

public sealed record RefreshSummary(
    int TotalNodes,
    int OnlineNodes,
    Dictionary<string, NodeSnapshot> PerNode);