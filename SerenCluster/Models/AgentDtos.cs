using System.Text.Json.Serialization;

namespace SerenCluster.Models
{

    /// <summary>
    /// Response from <c>GET /api/v1/system/ping</c> - public, no auth required.
    /// Used for liveness probes and quick "is this thing alive" checks.
    /// </summary>
    public sealed record PingResponse(
        [property: JsonPropertyName("ok")] bool Ok,
        [property: JsonPropertyName("ts")] long Timestamp);

    /// <summary>
    /// Response from <c>GET /api/v1/system/version</c> - public, no auth required.
    /// </summary>
    public sealed record VersionResponse(
        [property: JsonPropertyName("agent_version")] string AgentVersion,
        [property: JsonPropertyName("manifest_schema")] int ManifestSchema);

    /// <summary>
    /// Response from <c>GET /api/v1/system/node</c> - node identity + runtime stats.
    /// </summary>
    public sealed record NodeResponse(
        [property: JsonPropertyName("manifest")] NodeManifest? Manifest,
        [property: JsonPropertyName("runtime")] NodeRuntime? Runtime);

    public sealed record NodeManifest(
        [property: JsonPropertyName("hostname")] string? Hostname,
        [property: JsonPropertyName("ip_addresses")] List<string>? IpAddresses,
        [property: JsonPropertyName("platform")] string? Platform,
        [property: JsonPropertyName("jetpack_release")] string? JetpackRelease,
        [property: JsonPropertyName("cuda_arch")] string? CudaArch,
        [property: JsonPropertyName("cuda_version")] string? CudaVersion,
        [property: JsonPropertyName("unified_memory_gb")] int? UnifiedMemoryGb,
        [property: JsonPropertyName("cpu_cores")] int? CpuCores,
        [property: JsonPropertyName("installed_at")] string? InstalledAt,
        [property: JsonPropertyName("schema_version")] int SchemaVersion);

    public sealed record NodeRuntime(
        [property: JsonPropertyName("load_avg")] List<double>? LoadAvg,
        [property: JsonPropertyName("memory_mb_total")] long? MemoryMbTotal,
        [property: JsonPropertyName("memory_mb_available")] long? MemoryMbAvailable,
        [property: JsonPropertyName("memory_pct_used")] double? MemoryPctUsed,
        [property: JsonPropertyName("uptime_seconds")] long? UptimeSeconds);

    /// <summary>
    /// Response from <c>GET /api/v1/system/thermal</c>. Each zone is one
    /// <c>/sys/class/thermal/thermal_zone*</c> entry. Jetsons typically
    /// expose 5-8 zones (CPU, GPU, AUX, AO, etc.); NUCs typically 1-2
    /// (CPU package). On VMs / unsupported hardware <see cref="Available"/>
    /// is false and <see cref="Zones"/> is empty.
    /// </summary>
    public sealed record ThermalResponse(
        [property: JsonPropertyName("available")] bool Available,
        [property: JsonPropertyName("zones")] List<ThermalZone>? Zones,
        [property: JsonPropertyName("max_temp_c")] double? MaxTempC);

    public sealed record ThermalZone(
        [property: JsonPropertyName("zone")] string Zone,
        [property: JsonPropertyName("type")] string Type,
        [property: JsonPropertyName("temp_c")] double TempC);

    /// <summary>
    /// Response from <c>GET /api/v1/system/services</c> - full picture of what's
    /// installed on a node and what's currently running.
    /// </summary>
    public sealed record ServicesResponse(
        [property: JsonPropertyName("count")] int Count,
        [property: JsonPropertyName("services")] Dictionary<string, ServiceEntry> Services);

    public sealed record ServiceEntry(
        [property: JsonPropertyName("manifest")] ServiceManifest Manifest,
        [property: JsonPropertyName("status")] ServiceStatus? Status);

    public sealed record ServiceManifest(
        [property: JsonPropertyName("service")] string Service,
        [property: JsonPropertyName("implementation")] string? Implementation,
        [property: JsonPropertyName("port")] int Port,
        [property: JsonPropertyName("endpoint")] string? Endpoint,
        [property: JsonPropertyName("start_script")] string? StartScript,
        [property: JsonPropertyName("stop_script")] string? StopScript,
        [property: JsonPropertyName("pid_path")] string? PidPath,
        [property: JsonPropertyName("log_path")] string? LogPath,
        [property: JsonPropertyName("venv_path")] string? VenvPath,
        [property: JsonPropertyName("repo_path")] string? RepoPath,
        [property: JsonPropertyName("persistence_dir")] string? PersistenceDir,
        [property: JsonPropertyName("installed_at")] string? InstalledAt,
        [property: JsonPropertyName("schema_version")] int SchemaVersion,
        [property: JsonPropertyName("serviceSpecific")] Dictionary<string, object>? ServiceSpecific);

    public sealed record ServiceStatus(
        [property: JsonPropertyName("service")] string? Service,
        [property: JsonPropertyName("service_type")] string? ServiceType,
        [property: JsonPropertyName("running")] bool Running,
        [property: JsonPropertyName("pid")] int? Pid,
        [property: JsonPropertyName("memory_mb")] long? MemoryMb,
        [property: JsonPropertyName("cpu_percent")] double? CpuPercent,
        [property: JsonPropertyName("uptime_seconds")] long? UptimeSeconds,
        [property: JsonPropertyName("library_mode")] bool? LibraryMode,
        [property: JsonPropertyName("port_health")] PortHealth? PortHealth);

    public sealed record PortHealth(
        [property: JsonPropertyName("ok")] bool Ok,
        [property: JsonPropertyName("status_code")] int? StatusCode,
        [property: JsonPropertyName("latency_ms")] int? LatencyMs,
        [property: JsonPropertyName("probed_path")] string? ProbedPath,
        [property: JsonPropertyName("error")] string? Error,
        [property: JsonPropertyName("reason")] string? Reason);

    /// <summary>
    /// Response from <c>GET /api/v1/system/health</c> - rollup health.
    /// </summary>
    public sealed record HealthResponse(
        [property: JsonPropertyName("ok")] bool Ok,
        [property: JsonPropertyName("total")] int Total,
        [property: JsonPropertyName("healthy")] int Healthy,
        [property: JsonPropertyName("degraded")] List<string> Degraded,
        [property: JsonPropertyName("not_running")] List<string> NotRunning);

    /// <summary>
    /// Response from <c>POST /api/v1/system/reclaim</c>.
    /// </summary>
    public sealed record ReclaimResponse(
        [property: JsonPropertyName("stopped")] List<string> Stopped,
        [property: JsonPropertyName("kept")] List<string> Kept,
        [property: JsonPropertyName("failed")] List<ReclaimFailure>? Failed);

    public sealed record ReclaimFailure(
        [property: JsonPropertyName("service")] string Service,
        [property: JsonPropertyName("error")] string? Error);

    /// <summary>
    /// Generic envelope for service lifecycle responses (start, stop, restart).
    /// The agent returns a wider variety of fields depending on action; this
    /// captures the common ones, callers parse extras as needed.
    /// </summary>
    public sealed record ServiceLifecycleResponse(
        [property: JsonPropertyName("ok")] bool Ok,
        [property: JsonPropertyName("pid")] int? Pid,
        [property: JsonPropertyName("already_running")] bool? AlreadyRunning,
        [property: JsonPropertyName("was_running")] bool? WasRunning,
        [property: JsonPropertyName("exit_code")] int? ExitCode,
        [property: JsonPropertyName("stdout")] string? Stdout,
        [property: JsonPropertyName("stderr")] string? Stderr,
        [property: JsonPropertyName("error")] string? Error);

    /// <summary>
    /// Response from <c>GET /api/v1/service/{name}/logs</c>.
    /// </summary>
    public sealed record LogsResponse(
        [property: JsonPropertyName("ok")] bool Ok,
        [property: JsonPropertyName("lines")] List<string>? Lines,
        [property: JsonPropertyName("log_path")] string? LogPath,
        [property: JsonPropertyName("note")] string? Note,
        [property: JsonPropertyName("error")] string? Error);

    /// <summary>
    /// Response from <c>POST /api/v1/system/reboot</c>.
    /// Schedules the agent's host to reboot via <c>sudo shutdown -r +N</c>.
    /// </summary>
    public sealed record RebootResponse(
        [property: JsonPropertyName("scheduled")] bool Scheduled,
        [property: JsonPropertyName("scheduled_at")] string? ScheduledAt,
        [property: JsonPropertyName("delay_minutes")] int? DelayMinutes,
        [property: JsonPropertyName("method")] string? Method,
        [property: JsonPropertyName("error")] string? Error,
        [property: JsonPropertyName("hint")] string? Hint);

    /// <summary>
    /// Response from <c>POST /api/v1/system/reboot/cancel</c>.
    /// Cancels a previously scheduled reboot via <c>sudo shutdown -c</c>.
    /// </summary>
    public sealed record RebootCancelResponse(
        [property: JsonPropertyName("cancelled")] bool Cancelled,
        [property: JsonPropertyName("error")] string? Error);

    /// <summary>
    /// Response from <c>POST /api/v1/system/agent-update</c>.
    /// The agent saves the uploaded package and fires <c>seren-agent-update</c>
    /// in the background before returning - the update script may restart the
    /// agent process, so the HTTP response is sent first.
    /// </summary>
    public sealed record AgentUpdateResponse(
        [property: JsonPropertyName("ok")] bool Ok,
        [property: JsonPropertyName("message")] string? Message,
        [property: JsonPropertyName("error")] string? Error);

    /// <summary>
    /// Per-node result entry inside a broadcast agent-update response.
    /// </summary>
    public sealed record AgentUpdateNodeResult(
        [property: JsonPropertyName("ok")] bool Ok,
        [property: JsonPropertyName("node")] string Node,
        [property: JsonPropertyName("message")] string? Message,
        [property: JsonPropertyName("error")] string? Error);
}