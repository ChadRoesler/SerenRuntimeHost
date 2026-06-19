using SerenCluster.Configuration;

namespace SerenRuntimeHost.Configuration
{
    /// <summary>
    /// Top-level RuntimeHost configuration loaded from <c>seren-runtime.yaml</c>.
    /// </summary>
    public sealed class RuntimeHostOptions
    {
        /// <summary>Listener config (port, bind address, the inbound bearer token).</summary>++
        /// 
        public RuntimeOptions Runtime { get; set; } = new();

        /// <summary>Cluster of Jetson nodes (or a single localhost) to manage.</summary>
        public ClusterOptions Cluster { get; set; } = new();
    }
}