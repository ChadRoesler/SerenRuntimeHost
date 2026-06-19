using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace SerenRuntimeHost.Configuration
{

    /// <summary>
    /// Loads <c>seren-runtime.yaml</c> into a strongly-typed
    /// <see cref="RuntimeHostOptions"/>.
    /// </summary>
    /// <remarks>
    /// Naming convention: snake_case in YAML, PascalCase in C#. YamlDotNet's
    /// <see cref="UnderscoredNamingConvention"/> handles the mapping
    /// automatically. Unknown YAML keys are ignored (forward-compat) but
    /// unknown REQUIRED-but-missing fields fall back to the option type's
    /// default value.
    /// </remarks>
    public static class ConfigLoader
    {
        public static RuntimeHostOptions Load(string path)
        {
            if (!File.Exists(path))
            {
                throw new FileNotFoundException(
                    $"Runtime config not found: {path}. Copy seren-runtime.yaml.sample and edit.",
                    path);
            }

            var yaml = File.ReadAllText(path);
            var deserializer = new DeserializerBuilder()
                .WithNamingConvention(UnderscoredNamingConvention.Instance)
                .IgnoreUnmatchedProperties()
                .Build();

            var options = deserializer.Deserialize<RuntimeHostOptions>(yaml)
                ?? throw new InvalidOperationException(
                    $"Runtime config is empty or unparseable: {path}");

            ExpandPaths(options);
            Validate(options, path);
            return options;
        }

        /// <summary>
        /// Expand tilde (<c>~/</c>) prefixes in path-shaped fields. YamlDotNet
        /// passes strings through literally; the shell would normally expand
        /// <c>~</c> but our config never goes through a shell. Without this,
        /// File.Copy(...) etc. fail on the literal "~/foo" string.
        /// </summary>
        /// <remarks>
        /// Per-user expansion uses the CURRENT process's home dir
        /// (Environment.UserName + GetFolderPath). On the NUC where RuntimeHost
        /// runs, that's the service user's home. Per-node paths in the YAML
        /// are interpreted against the NUC's home, NOT against each Jetson's
        /// home, which is correct because RuntimeHost reads its local copy of
        /// seren-agent.tar.gz from those paths before scp-ing to the Jetsons.
        ///
        /// Honest disclosure: this means <c>agent_update_path: ~/serenSetup</c>
        /// in a node entry is "where on the NUC the tarball lives," not
        /// "where on the Jetson it'll land." That naming is mildly confusing
        /// but matches how the field has always been used in practice.
        /// </remarks>
        private static void ExpandPaths(RuntimeHostOptions opts)
        {
            opts.Runtime.AgentPackagePath = ExpandTilde(opts.Runtime.AgentPackagePath);
            foreach (var node in opts.Cluster.Nodes)
            {
                node.AgentUpdatePath = ExpandTilde(node.AgentUpdatePath);
            }
        }

        private static string ExpandTilde(string path)
        {
            if (string.IsNullOrEmpty(path)) return path;
            if (path == "~")
                return Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (path.StartsWith("~/") || path.StartsWith("~\\"))
            {
                var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                return Path.Combine(home, path[2..]);
            }
            return path;
        }

        private static void Validate(RuntimeHostOptions opts, string path)
        {
            if (opts.Cluster.Nodes.Count == 0)
            {
                throw new InvalidOperationException(
                    $"{path}: cluster.nodes is empty. Even local-only setups must " +
                    "configure a single node - see seren-runtime.yaml.sample.");
            }

            for (var i = 0; i < opts.Cluster.Nodes.Count; i++)
            {
                var n = opts.Cluster.Nodes[i];
                if (string.IsNullOrWhiteSpace(n.Name))
                    throw new InvalidOperationException(
                        $"{path}: cluster.nodes[{i}].name is empty");
                if (string.IsNullOrWhiteSpace(n.AgentUrl))
                    throw new InvalidOperationException(
                        $"{path}: cluster.nodes[{i}].agent_url is empty (node='{n.Name}')");
            }

            if (opts.Runtime.Port < 1 || opts.Runtime.Port > 65535)
                throw new InvalidOperationException(
                    $"{path}: runtime.port must be 1-65535 (got {opts.Runtime.Port})");
        }
    }
}