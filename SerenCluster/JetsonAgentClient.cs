using SerenCluster.Configuration;
using SerenCluster.Models;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace SerenCluster;

/// <summary>
/// Typed HTTP client for the per-Jetson <c>seren-agent</c> API at
/// <c>/api/v1/...</c>. One instance per agent - see
/// <see cref="JetsonClusterClient"/> for routing across many.
/// </summary>
/// <remarks>
/// All methods (except <see cref="PingAsync"/> and <see cref="VersionAsync"/>)
/// require the bearer token configured at construction. If the token is null
/// or empty, the agent returns <c>X-Seren-Auth: disabled</c> and accepts the
/// request anyway - this lets fresh installs work before the token lands,
/// but in production every node should be auth-configured.
///
/// Methods do NOT throw on agent unreachability or HTTP errors - they
/// return null (for nullable returns), false (for bool returns), or empty
/// collections (for list returns), and log to stderr. The cluster client
/// uses these returns to decide on fall-through.
/// </remarks>
public sealed class JetsonAgentClient : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly HttpClient _http;
    private readonly string _nodeName;
    private readonly Action<string>? _log;

    /// <summary>The configured node name (for log/diagnostic context).</summary>
    public string NodeName => _nodeName;

    /// <summary>The configured base URL (for log/diagnostic context).</summary>
    public Uri BaseAddress => _http.BaseAddress ?? throw new InvalidOperationException("BaseAddress not set");

    /// <summary>
    /// Destination path on the node's filesystem for agent update packages.
    /// Empty string means this node has no update path configured and will
    /// be skipped during broadcast update operations.
    /// </summary>
    public string AgentUpdatePath { get; }

    /// <summary>
    /// True when this node is the one running the RuntimeHost itself.
    /// Sourced from <c>is_host: true</c> in seren-runtime.yaml.
    /// </summary>
    public bool IsHost { get; }

    /// <summary>
    /// Optional affectionate name for this node. Distinct from <see cref="NodeName"/>
    /// which is the routing identifier. Used in narrative output (the LLM's
    /// self-context, friendly UI labels, dashboard hover tips). Sourced from
    /// <c>nickname:</c> in seren-runtime.yaml. Empty string when unset.
    /// </summary>
    public string Nickname { get; }

    /// <summary>
    /// Construct a client for one agent. Pass <paramref name="log"/> if you
    /// want failures noted somewhere - defaults to <see cref="Console.Error"/>.
    /// </summary>
    public JetsonAgentClient(JetsonNodeOptions options, Action<string>? log = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(options.Name);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.AgentUrl);

        _nodeName = options.Name;
        _log = log ?? (msg => Console.Error.WriteLine(msg));

        AgentUpdatePath = options.AgentUpdatePath;
        IsHost = options.IsHost;
        Nickname = options.Nickname ?? string.Empty;

        _http = new HttpClient
        {
            BaseAddress = new Uri(options.AgentUrl.TrimEnd('/') + "/"),
            // Lifecycle ops can take time (start scripts run subprocesses);
            // the agent itself enforces a 30s ceiling, so 35s here covers
            // the common case without being so long that a hung node holds
            // up the dashboard forever.
            Timeout = TimeSpan.FromSeconds(35),
        };

        if (!string.IsNullOrWhiteSpace(options.AgentToken))
        {
            _http.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("Bearer", options.AgentToken);
        }
    }

    public void Dispose() => _http.Dispose();

    // -------------------------------------------------------------
    //  System endpoints
    // -------------------------------------------------------------

    /// <summary>
    /// Quick liveness probe. Public endpoint - works without auth.
    /// Returns null if the agent is unreachable.
    /// </summary>
    public Task<PingResponse?> PingAsync(CancellationToken ct = default) =>
        GetJsonAsync<PingResponse>("api/v1/system/ping", ct);

    public Task<VersionResponse?> VersionAsync(CancellationToken ct = default) =>
        GetJsonAsync<VersionResponse>("api/v1/system/version", ct);

    public Task<NodeResponse?> GetNodeAsync(CancellationToken ct = default) =>
        GetJsonAsync<NodeResponse>("api/v1/system/node", ct);

    /// <summary>
    /// Per-zone temperature readings from /sys/class/thermal. Returns
    /// <c>{available: false, zones: []}</c> on unsupported hardware/VMs
    /// rather than null - null means agent unreachable.
    /// </summary>
    public Task<ThermalResponse?> GetThermalAsync(CancellationToken ct = default) =>
        GetJsonAsync<ThermalResponse>("api/v1/system/thermal", ct);

    /// <summary>
    /// Full inventory of installed services + their current runtime status.
    /// This is the source of truth that drives <see cref="JetsonClusterClient"/>'s
    /// capability map - call this on every refresh.
    /// </summary>
    public Task<ServicesResponse?> GetServicesAsync(CancellationToken ct = default) =>
        GetJsonAsync<ServicesResponse>("api/v1/system/services", ct);

    public Task<HealthResponse?> GetHealthAsync(CancellationToken ct = default) =>
        GetJsonAsync<HealthResponse>("api/v1/system/health", ct);

    /// <summary>
    /// Stop idle services to free GPU memory. <paramref name="exclude"/> names
    /// services that should NOT be stopped (e.g. <c>llama</c> for the always-on
    /// model on the primary node).
    /// </summary>
    public Task<ReclaimResponse?> ReclaimAsync(IReadOnlyList<string>? exclude = null, CancellationToken ct = default)
    {
        var body = new { exclude = exclude ?? [] };
        return PostJsonAsync<object, ReclaimResponse>("api/v1/system/reclaim", body, ct);
    }

    /// <summary>
    /// Schedules a system reboot on the agent's host via <c>sudo shutdown -r +N</c>.
    /// Default delay is 1 minute, giving the response time to flush back AND
    /// providing a window where <see cref="RebootCancelAsync"/> can abort the
    /// reboot if the user fat-fingered the dashboard.
    ///
    /// Requires sudoers grant for <c>/sbin/shutdown -r *</c> on the target node.
    /// Existing pre-reboot-feature installs need the seren-sudoers-update.sh
    /// migration to add this grant.
    /// </summary>
    /// <param name="delayMinutes">Minutes from now to reboot. Clamped 0..60 by the agent.</param>
    public Task<RebootResponse?> RebootAsync(int delayMinutes = 1, CancellationToken ct = default)
    {
        var body = new { delay_minutes = delayMinutes };
        return PostJsonAsync<object, RebootResponse>("api/v1/system/reboot", body, ct);
    }

    /// <summary>
    /// Cancels a previously scheduled reboot. Maps to <c>sudo shutdown -c</c>.
    /// Safe to call even if no reboot is currently scheduled - the underlying
    /// command exits 0 either way (we forward that as <c>cancelled: true</c>).
    /// </summary>
    public Task<RebootCancelResponse?> RebootCancelAsync(CancellationToken ct = default) =>
        PostJsonAsync<object?, RebootCancelResponse>("api/v1/system/reboot/cancel", null, ct);

    /// <summary>
    /// Pushes a <c>seren-agent.tar.gz</c> package to this node's agent and
    /// triggers the <c>seren-agent-update</c> script. The agent fires the
    /// script in the background and returns immediately - the HTTP response
    /// will arrive before the update script completes (and potentially before
    /// the agent process restarts).
    /// </summary>
    /// <param name="packageStream">
    /// Readable stream of the tar.gz content. Ownership stays with the caller;
    /// this method does not dispose the stream.
    /// </param>
    /// <param name="filename">
    /// Filename sent in the multipart Content-Disposition header.
    /// Typically <c>seren-agent.tar.gz</c>.
    /// </param>
    /// <param name="destPath">
    /// Absolute path on the node where the agent should save the package.
    /// The <c>seren-agent-update.sh</c> script must live in the same directory.
    /// </param>
    /// <param name="ct">Caller cancellation token.</param>
    /// <remarks>
    /// Uses a 120 s local timeout (linked to <paramref name="ct"/>) to allow
    /// for file transfer time on slower Jetson hardware without holding the
    /// caller indefinitely if the node is unreachable.
    /// </remarks>
    public async Task<AgentUpdateResponse?> PushAgentUpdateAsync(
        Stream packageStream,
        string filename,
        string destPath,
        CancellationToken ct = default)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(120));

        try
        {
            var content = new MultipartFormDataContent();

            var streamContent = new StreamContent(packageStream);
            streamContent.Headers.ContentType =
                new System.Net.Http.Headers.MediaTypeHeaderValue("application/octet-stream");
            content.Add(streamContent, "package", filename);
            content.Add(new StringContent(destPath), "dest_path");

            using var resp = await _http.PostAsync("api/v1/system/agent-update", content, timeoutCts.Token);
            if (!resp.IsSuccessStatusCode)
            {
                _log?.Invoke($"[{_nodeName}] POST agent-update → HTTP {(int)resp.StatusCode}");
                return new AgentUpdateResponse(Ok: false, Message: null,
                    Error: $"HTTP {(int)resp.StatusCode}");
            }
            return await resp.Content.ReadFromJsonAsync<AgentUpdateResponse>(JsonOptions, timeoutCts.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            _log?.Invoke($"[{_nodeName}] POST agent-update → timeout");
            return new AgentUpdateResponse(Ok: false, Message: null, Error: "timeout");
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException)
        {
            _log?.Invoke($"[{_nodeName}] POST agent-update → {ex.GetType().Name}: {ex.Message}");
            return new AgentUpdateResponse(Ok: false, Message: null, Error: ex.Message);
        }
    }

    // -------------------------------------------------------------
    //  Per-service lifecycle
    //
    //  These match the universal verbs every service exposes. Service-
    //  specific endpoints (kokoro/voices, comfy/checkpoints) are exposed
    //  separately by the agent but the cluster client does NOT proxy them
    //  yet - RuntimeHost will add those when we build out the workflow
    //  endpoints.
    // -------------------------------------------------------------

    public Task<ServiceManifest?> GetServiceManifestAsync(string service, CancellationToken ct = default) =>
        GetJsonAsync<ServiceManifest>($"api/v1/service/{Uri.EscapeDataString(service)}/manifest", ct);

    public Task<ServiceStatus?> GetServiceStatusAsync(string service, CancellationToken ct = default) =>
        GetJsonAsync<ServiceStatus>($"api/v1/service/{Uri.EscapeDataString(service)}/status", ct);

    public Task<PortHealth?> GetServiceHealthAsync(string service, CancellationToken ct = default) =>
        GetJsonAsync<PortHealth>($"api/v1/service/{Uri.EscapeDataString(service)}/health", ct);

    public Task<ServiceLifecycleResponse?> StartServiceAsync(string service, CancellationToken ct = default) =>
        PostJsonAsync<object?, ServiceLifecycleResponse>($"api/v1/service/{Uri.EscapeDataString(service)}/start", null, ct);

    public Task<ServiceLifecycleResponse?> StopServiceAsync(string service, CancellationToken ct = default) =>
        PostJsonAsync<object?, ServiceLifecycleResponse>($"api/v1/service/{Uri.EscapeDataString(service)}/stop", null, ct);

    public Task<ServiceLifecycleResponse?> RestartServiceAsync(string service, CancellationToken ct = default) =>
        PostJsonAsync<object?, ServiceLifecycleResponse>($"api/v1/service/{Uri.EscapeDataString(service)}/restart", null, ct);

    public Task<LogsResponse?> GetServiceLogsAsync(string service, int lines = 100, CancellationToken ct = default) =>
        GetJsonAsync<LogsResponse>($"api/v1/service/{Uri.EscapeDataString(service)}/logs?lines={lines}", ct);

    // -------------------------------------------------------------
    //  Internal HTTP helpers
    // -------------------------------------------------------------

    private async Task<T?> GetJsonAsync<T>(string path, CancellationToken ct) where T : class
    {
        try
        {
            using var resp = await _http.GetAsync(path, ct);
            if (!resp.IsSuccessStatusCode)
            {
                _log?.Invoke($"[{_nodeName}] GET {path} → HTTP {(int)resp.StatusCode}");
                return null;
            }
            return await resp.Content.ReadFromJsonAsync<T>(JsonOptions, ct);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // Timeout (HttpClient throws OperationCanceledException, not TimeoutException)
            _log?.Invoke($"[{_nodeName}] GET {path} → timeout");
            return null;
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException)
        {
            _log?.Invoke($"[{_nodeName}] GET {path} → {ex.GetType().Name}: {ex.Message}");
            return null;
        }
    }

    private async Task<TResp?> PostJsonAsync<TReq, TResp>(string path, TReq? body, CancellationToken ct)
        where TResp : class
    {
        try
        {
            HttpResponseMessage resp;
            if (body is null)
            {
                resp = await _http.PostAsync(path, content: null, ct);
            }
            else
            {
                resp = await _http.PostAsJsonAsync(path, body, JsonOptions, ct);
            }

            using (resp)
            {
                if (!resp.IsSuccessStatusCode)
                {
                    _log?.Invoke($"[{_nodeName}] POST {path} → HTTP {(int)resp.StatusCode}");
                    return null;
                }
                return await resp.Content.ReadFromJsonAsync<TResp>(JsonOptions, ct);
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            _log?.Invoke($"[{_nodeName}] POST {path} → timeout");
            return null;
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException)
        {
            _log?.Invoke($"[{_nodeName}] POST {path} → {ex.GetType().Name}: {ex.Message}");
            return null;
        }
    }

    private async Task<JsonElement?> GetJsonForElementAsync(string path, CancellationToken ct)
    {
        try
        {
            using var resp = await _http.GetAsync(path, ct);
            if (!resp.IsSuccessStatusCode)
            {
                _log?.Invoke($"[{_nodeName}] GET {path} → HTTP {(int)resp.StatusCode}");
                return null;
            }
            using var doc = await resp.Content.ReadFromJsonAsync<JsonDocument>(JsonOptions, ct);
            return doc?.RootElement.Clone();
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            _log?.Invoke($"[{_nodeName}] GET {path} → timeout");
            return null;
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException)
        {
            _log?.Invoke($"[{_nodeName}] GET {path} → {ex.GetType().Name}: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Service-specific models endpoint. Only valid for services that
    /// implement it (llama, comfy, whisper). Returns a JsonElement so
    /// per-service response shapes can pass through unchanged - llama
    /// returns {models: [{name, size_mb}], models_dir}, whisper returns
    /// {models: [str], implementation, ...}, etc.
    /// </summary>
    public Task<JsonElement?> GetServiceModelsAsync(string service, CancellationToken ct = default) =>
        GetJsonForElementAsync($"api/v1/service/{Uri.EscapeDataString(service)}/models", ct);

    // POST with arbitrary body, return the response as a JsonElement so the
    // caller can pass it through verbatim. Used for dynamic-shape endpoints
    // (chroma item ops) where defining a typed DTO would add overhead with
    // no benefit - the data flows through RuntimeHost unchanged on its way
    // to the MCP server.
    private async Task<JsonElement?> PostJsonForElementAsync(
        string path, object body, CancellationToken ct)
    {
        try
        {
            using var resp = await _http.PostAsJsonAsync(path, body, JsonOptions, ct);
            if (!resp.IsSuccessStatusCode)
            {
                _log?.Invoke($"[{_nodeName}] POST {path} → HTTP {(int)resp.StatusCode}");
                return null;
            }
            // Read into a JsonDocument first so we can clone the root element -
            // returning the root directly would leave the document undisposed.
            using var doc = await resp.Content.ReadFromJsonAsync<JsonDocument>(JsonOptions, ct);
            return doc?.RootElement.Clone();
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            _log?.Invoke($"[{_nodeName}] POST {path} → timeout");
            return null;
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException)
        {
            _log?.Invoke($"[{_nodeName}] POST {path} → {ex.GetType().Name}: {ex.Message}");
            return null;
        }
    }

    private async Task<JsonElement?> DeleteForElementAsync(string path, CancellationToken ct)
    {
        try
        {
            using var resp = await _http.DeleteAsync(path, ct);
            if (!resp.IsSuccessStatusCode)
            {
                _log?.Invoke($"[{_nodeName}] DELETE {path} → HTTP {(int)resp.StatusCode}");
                return null;
            }
            using var doc = await resp.Content.ReadFromJsonAsync<JsonDocument>(JsonOptions, ct);
            return doc?.RootElement.Clone();
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            _log?.Invoke($"[{_nodeName}] DELETE {path} → timeout");
            return null;
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException)
        {
            _log?.Invoke($"[{_nodeName}] DELETE {path} → {ex.GetType().Name}: {ex.Message}");
            return null;
        }
    }
}
