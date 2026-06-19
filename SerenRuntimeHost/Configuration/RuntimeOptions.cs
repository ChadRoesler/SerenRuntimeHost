namespace SerenRuntimeHost.Configuration
{
    public sealed class RuntimeOptions
    {
        /// <summary>Bind address. Default 0.0.0.0 for LAN access.</summary>
        public string Host { get; set; } = "0.0.0.0";

        /// <summary>Listen port. Different from agent port (7777) and chat-app
        /// expectations to keep the namespaces clean.</summary>
        public int Port { get; set; } = 6361;

        /// <summary>
        /// Bearer token chat-app and dashboard must present. Leave empty for
        /// dev mode (auth disabled, header set on every response). In production
        /// this should be a 32+ char random string from a secure source.
        /// </summary>
        public string BearerToken { get; set; } = string.Empty;

        /// <summary>
        /// When true (default), the dashboard HTML served at "/" has the
        /// bearer token substituted into it server-side. This means a fresh
        /// browser load gets a working dashboard with zero manual setup.
        ///
        /// Set to FALSE when the dashboard URL is reachable beyond a trusted
        /// LAN - otherwise anyone who can GET / sees the token in the HTML
        /// source. Disabled = users must paste the token into the dashboard's
        /// Settings modal on first load.
        ///
        /// Default true is "homelab convenience"; flip for production-grade
        /// security postures.
        /// </summary>
        public bool InjectBearerToken { get; set; } = true;

        /// <summary>
        /// Absolute or relative path to the <c>seren-agent.tar.gz</c> package
        /// on the host filesystem. When set, the RuntimeHost reads this file
        /// and pushes it to target nodes via
        /// <c>POST /api/v1/system/agent-update</c> and
        /// <c>POST /api/v1/node/{node}/agent-update</c>.
        ///
        /// Leave empty to disable the agent-update endpoints (they return 409).
        /// Example: <c>./updates/seren-agent.tar.gz</c>
        /// </summary>
        public string AgentPackagePath { get; set; } = string.Empty;
    }
}