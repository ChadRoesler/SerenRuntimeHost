namespace SerenCluster.Configuration
{

    /// <summary>
    /// Top-level cluster configuration loaded from YAML.
    /// </summary>
    /// <remarks>
    /// An empty <see cref="Nodes"/> list is invalid - even local-only setups
    /// configure a single node pointing at <c>127.0.0.1:7777</c>. The "no
    /// cluster" path was rejected during design in favor of "cluster of one"
    /// because it eliminates an entire if-else branch from the data plane.
    /// </remarks>
    public sealed class ClusterOptions
    {
        /// <summary>
        /// All Jetson (or local) agents this RuntimeHost knows about.
        /// </summary>
        public List<JetsonNodeOptions> Nodes { get; set; } = [];

        /// <summary>
        /// How often the discovery service re-queries every agent's
        /// <c>/api/v1/system/services</c> to refresh the capability map.
        /// Defaults to 30 minutes - failure-driven invalidation handles
        /// "node went DOWN" instantly, this only matters for "node came UP"
        /// or "node added a service" cases.
        /// </summary>
        public TimeSpan RefreshInterval { get; set; } = TimeSpan.FromMinutes(30);

        /// <summary>
        /// Per-node timeout for the eager refresh probe. Short by design -
        /// startup shouldn't block on a node that's powered off.
        /// </summary>
        public TimeSpan DiscoveryTimeout { get; set; } = TimeSpan.FromSeconds(2);

        /// <summary>
        /// If true, <c>/api/v1/system/health</c> returns 503 when ANY configured
        /// node is unreachable. If false (default), returns 200 with a degraded
        /// status payload listing which nodes are down.
        /// </summary>
        public bool HealthStrictMode { get; set; }
    }
}
