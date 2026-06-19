// ========================================================================
//  SerenRuntimeHost - cluster head for the Seren stack
// ========================================================================
//
//  STATUS:  Greenfield rewrite. This replaces the previous Run-verb-based
//           RuntimeHost. Workflow endpoints (chat, tts, image, screen,
//           project) are NOT yet ported - they will land in subsequent
//           sessions on top of this foundation.
//
//  WHAT WORKS:
//    /api/v1/system/{ping,version,status,health,reclaim}
//    /api/v1/cluster/{refresh,refresh/{node},capabilities}
//    /api/v1/service/{name}/{manifest,status,health,start,stop,restart,logs}
//      where {name} is one of: llama, kokoro, comfy, chroma, whisper,
//      coral, agent
//
//  WHAT DOESN'T WORK YET:
//    /api/v1/chat            - needs llama/inference workers
//    /api/v1/tts             - needs kokoro proxy
//    /api/v1/image/*         - needs comfy proxy
//    /api/v1/screen/*        - needs vision worker
//    /api/v1/project/*       - needs file workers
//    Service-specific endpoints (kokoro/voices, comfy/checkpoints, etc.) -
//      callers can use the agent's per-service endpoints directly via
//      /api/v1/service/{name}/manifest to discover paths.
//
//  CONFIG:
//    Default path: ./seren-runtime.yaml (override with first CLI arg).
//    See seren-runtime.yaml.sample for the schema.
//
//  AUTH:
//    Inbound: Authorization: Bearer <runtime.bearer_token>
//    Outbound: per-Jetson bearer tokens stored in cluster.nodes[].agent_token
//    Public paths: /, /api/v1/system/ping, /api/v1/system/version
//
//  RUNNING:
//    dotnet run -- /path/to/seren-runtime.yaml
//
// ========================================================================

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

using SerenCluster;
using SerenCluster.Auth;
using SerenCluster.Logging;
using SerenRuntimeHost.Api;
using SerenRuntimeHost.Configuration;

var configPath = args.Length > 0 ? args[0] : "seren-runtime.yaml";
Console.WriteLine($"[runtime] config: {configPath}");

RuntimeHostOptions options;
try
{
    options = ConfigLoader.Load(configPath);
}
catch (Exception ex)
{
    Console.Error.WriteLine($"[runtime] config load failed: {ex.Message}");
    Environment.Exit(1);
    return;
}

Console.WriteLine($"[runtime] listen: {options.Runtime.Host}:{options.Runtime.Port}");
Console.WriteLine($"[runtime] inbound auth: " +
    (string.IsNullOrEmpty(options.Runtime.BearerToken) ? "DISABLED (no token)" : "enabled"));
Console.WriteLine($"[runtime] cluster: {options.Cluster.Nodes.Count} node(s) configured");
foreach (var n in options.Cluster.Nodes)
{
    var prefs = n.PreferredFor.Count > 0 ? string.Join(",", n.PreferredFor) : "-";
    Console.WriteLine($"           {n.Name,-12} {n.AgentUrl,-32} preferred:[{prefs}]");
}

// -- DI + service wireup --
var builder = WebApplication.CreateBuilder();
builder.WebHost.UseUrls($"http://{options.Runtime.Host}:{options.Runtime.Port}");

// Cluster client - singleton, owns HttpClient pool internally
var cluster = new JetsonClusterClient(
    options.Cluster,
    msg => Console.WriteLine($"[cluster] {msg}"));
builder.Services.AddSingleton(cluster);

// HttpClientFactory for outbound calls FROM RuntimeHost (currently just
// the scheduler talking back to MCP, but adding the factory now means
// future workers can grab named clients trivially).
builder.Services.AddHttpClient("mcp", c =>
{
    // MCP server lives next door on 6362. Scheduler fires tool calls
    // here. Tight timeout because tool calls should be fast; if one
    // blocks for >15s it's a degenerate tool that we'd rather time out
    // than hold the scheduler tick on.
    c.BaseAddress = new Uri("http://localhost:6362");
    c.Timeout = TimeSpan.FromSeconds(15);

    // CRITICAL: the MCP Streamable HTTP transport REQUIRES the client to
    // accept BOTH application/json AND text/event-stream — the server picks
    // which to return and rejects (HTTP 406, JSON-RPC -32000 "Not Acceptable")
    // any client that doesn't advertise both. Without this every tools/list
    // and tools/call silently 406s, which the loop would swallow as "no tools"
    // → tool_rounds always 0 with no obvious cause. Caught via test-mcp.sh.
    c.DefaultRequestHeaders.Accept.ParseAdd("application/json");
    c.DefaultRequestHeaders.Accept.ParseAdd("text/event-stream");
});

// Outbound client for proxying chat to a node's llama-server. No fixed
// BaseAddress - ChatEndpoints resolves the node URL per-request via the
// cluster router (the llama node can move), so it passes an absolute URL.
//
// Timeout is GENEROUS on purpose: a 9B Q8 on Volta generating a long
// response at ~modest tokens/sec can legitimately take a while, and the
// model also pays a one-time load cost on the first request after a
// restart. 120s covers cold-start + a long generation without holding a
// truly-wedged node forever (the catch in ChatEndpoints marks a timed-out
// node offline so routing avoids it next time). When streaming lands this
// matters less - tokens arrive incrementally - but the non-streaming path
// needs the headroom.
builder.Services.AddHttpClient("llama-upstream", c =>
{
    c.Timeout = TimeSpan.FromSeconds(120);
});

// The tool-call dialect. Singleton because it's stateless. QwenHermesDialect
// mirrors Seren's trained <tool_call> format. The day the backend model swaps
// to another family, change THIS line to the new dialect - the chat loop, the
// MCP client, and the tools all stay untouched. That's the agnostic seam.
builder.Services.AddSingleton<SerenRuntimeHost.Tooling.IToolDialect, SerenRuntimeHost.Tooling.QwenHermesDialect>();

// Scheduler - persists to ~/.seren/scheduled_tasks.json by default.
// Override path via env var SEREN_SCHEDULER_STATE if you want.
var schedulerStatePath = Environment.GetEnvironmentVariable("SEREN_SCHEDULER_STATE")
    ?? Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".seren",
        "scheduled_tasks.json");

builder.Services.AddSingleton<SerenRuntimeHost.Scheduling.SchedulerService>(sp =>
    new SerenRuntimeHost.Scheduling.SchedulerService(
        sp.GetRequiredService<IHttpClientFactory>(),
        schedulerStatePath,
        msg => Console.WriteLine($"[scheduler] {msg}")));
builder.Services.AddHostedService(sp =>
    sp.GetRequiredService<SerenRuntimeHost.Scheduling.SchedulerService>());

// -- CORS --
// Dashboard runs in the browser and may load from any origin (file://,
// http://localhost served from any port, http://nuc-ip from a desktop, etc).
// Wide-open CORS is acceptable here because (a) auth is enforced by bearer
// token on every authed endpoint, and (b) the threat model is "homelab on
// trusted LAN," not "public-internet API."
//
// AllowAnyOrigin + AllowCredentials would conflict (browser spec); we don't
// use cookies so credentials aren't needed.
builder.Services.AddCors(opts =>
{
    opts.AddDefaultPolicy(policy => policy
        .AllowAnyOrigin()
        .AllowAnyMethod()
        .AllowAnyHeader());
});

// Background discovery - eager refresh on startup, periodic after that
builder.Services.AddHostedService<JetsonDiscoveryService>(_ =>
    new JetsonDiscoveryService(
        cluster,
        options.Cluster,
        msg => Console.WriteLine($"[discovery] {msg}")));

// -- App pipeline --
var app = builder.Build();

// CORS BEFORE auth - preflight OPTIONS requests don't carry bearer tokens
// and would otherwise be 401'd, breaking every browser-issued request.
app.UseCors();

// Request logging FIRST (before auth) so we log 401s too - knowing
// auth is rejecting requests is half the debug story. Logger writes
// to BOTH stderr/stdout (journalctl) AND a file (~/seren-logs/runtime-host.log
// or /var/log/seren-runtime-host.log if writable) - read with cat/tail,
// no sudo needed.
app.UseSerenRequestLogging();

// Auth - bearer token check on everything except the public-paths list.
// Public path skip list lives in the middleware itself; "/" is in there
// so the dashboard loads without auth (the dashboard's JS then carries
// the bearer token for /api/v1/* calls).
app.UseSerenBearerAuth(options.Runtime.BearerToken);

// -- Dashboard at "/" --------------------------------------------------
// Serves the dashboard HTML from wwwroot/seren-dashboard.html. The dashboard
// is a single-file static asset that fetches /api/v1/* from the same origin.
//
// Token auto-injection: if options.Runtime.InjectBearerToken is true (default
// on the assumption of a trusted LAN), we read the HTML, swap a placeholder
// for the actual bearer token, and serve the result. New browsers hitting
// the dashboard then have auth pre-loaded - zero manual setup.
//
// SECURITY NOTE: if the dashboard URL is reachable outside your LAN, set
// InjectBearerToken=false in seren-runtime.yaml. Anyone who can load "/"
// would otherwise see your token in the served HTML.
{
    var wwwroot = Path.Combine(AppContext.BaseDirectory, "wwwroot");
    var dashboardPath = Path.Combine(wwwroot, "seren-dashboard.html");
    string dashboardHtml;
    try
    {
        dashboardHtml = File.ReadAllText(dashboardPath);
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"[runtime] could not load dashboard at {dashboardPath}: {ex.Message}");
        // Fall back to a tiny info page if the dashboard is missing - keep
        // the API surface working even if the static asset wasn't deployed.
        dashboardHtml = "<h1>SerenRuntimeHost</h1><p>Dashboard not found. See /api/v1/system/version.</p>";
    }

    // Inject the token once at startup. Cheap and means every request
    // serves an already-prepared string (no per-request file IO).
    if (options.Runtime.InjectBearerToken && !string.IsNullOrEmpty(options.Runtime.BearerToken))
    {
        // The placeholder lives in the HTML as a JS string literal so it's
        // also valid HTML/JS if NOT substituted (just empty token, settings
        // modal still works to enter it manually). See loadCfg() in the
        // dashboard JS.
        dashboardHtml = dashboardHtml.Replace(
            "__SEREN_BEARER_TOKEN__",
            EscapeForJsString(options.Runtime.BearerToken));
    }
    else
    {
        // No injection - leave the placeholder as an empty string so the
        // dashboard's "first-load needs settings" UX kicks in.
        dashboardHtml = dashboardHtml.Replace("__SEREN_BEARER_TOKEN__", "");
    }

    app.MapGet("/", () => Results.Content(dashboardHtml, "text/html; charset=utf-8"));

    // Also expose the raw file for direct linking (curl, dev work, embed in
    // an iframe, etc.). Same content, different URL. Both are public.
    app.UseStaticFiles();
}

SystemEndpoints.Map(app, cluster, options.Cluster.HealthStrictMode);
ClusterEndpoints.Map(app, cluster);
ServiceEndpoints.Map(app, cluster);
AgentUpdateEndpoints.Map(app, cluster, options.Runtime);
SchedulerEndpoints.Map(app, app.Services.GetRequiredService<SerenRuntimeHost.Scheduling.SchedulerService>());
ChatEndpoints.Map(app, cluster, app.Services.GetRequiredService<SerenRuntimeHost.Tooling.IToolDialect>());

Console.WriteLine("[runtime] ready");
app.Run();

// ---------------------------------------------------------------------
// Local helper - JS-string escape for tokens injected into served HTML.
// Tokens are typically alphanumeric (random hex / base64) so this rarely
// matters in practice, but better to escape than to ship a token that
// breaks the JS parser if someone uses a token with special characters.
// ---------------------------------------------------------------------
static string EscapeForJsString(string s)
{
    var sb = new System.Text.StringBuilder(s.Length);
    foreach (var c in s)
    {
        switch (c)
        {
            case '\\': sb.Append("\\\\"); break;
            case '\'': sb.Append("\\'"); break;
            case '"': sb.Append("\\\""); break;
            case '\n': sb.Append("\\n"); break;
            case '\r': sb.Append("\\r"); break;
            case '\t': sb.Append("\\t"); break;
            case '<': sb.Append("\\u003C"); break; // </script> defense
            case '>': sb.Append("\\u003E"); break;
            case '&': sb.Append("\\u0026"); break; // </script> defense we escape & because this string is rendered inside HTML before JS parses it.
            default:
                if (c < 0x20) sb.Append($"\\u{(int)c:X4}");
                else sb.Append(c);
                break;
        }
    }
    return sb.ToString();
}