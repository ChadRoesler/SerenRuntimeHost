// ========================================================================
//  SchedulerService - the cluster's cron, with a twist.
//
//  Schedules MCP tool calls. NOT shell commands, NOT processes - tool calls.
//  Means every scheduled action goes through the same audit/security layer
//  as direct LLM invocations, and the same toolset is available.
//
//  ----------------------------------------------------------------------
//  WHY THIS LIVES IN RuntimeHost AND NOT MCP:
//
//  MCP is stateless by design (every tool call is independent). The scheduler
//  needs to:
//    1. Persist tasks across restarts
//    2. Fire on time even when no LLM conversation is active
//    3. Coordinate so two MCP instances don't both fire the same task
//
//  RuntimeHost is the long-lived process with config-backed state. Natural
//  home. MCP tools become the front-door for schedule MANAGEMENT, but the
//  actual ticking happens here.
//
//  ----------------------------------------------------------------------
//  PERSISTENCE
//
//  Tasks live in ~/.seren/scheduled_tasks.json. Atomic writes (tmpfile +
//  rename) so crashes can't corrupt the file. Loaded on startup.
//
//  ----------------------------------------------------------------------
//  SCHEDULE SHAPES (v1)
//
//  CRON-ISH: "0 3 * * *" = every day at 3am. Standard cron syntax, parsed
//  with NCrontab.
//
//  RELATIVE: "in 2h", "in 30m", "in 5d" - one-shot relative offsets.
//
//  Future: event-triggered ("when llama crashes", "when CPU > 80% for 5min").
//  Out of scope for v1.
// ========================================================================

using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Hosting;

namespace SerenRuntimeHost.Scheduling
{
    public sealed class SchedulerService : BackgroundService
    {
        private readonly IHttpClientFactory _httpFactory;
        private readonly string _stateFilePath;
        private readonly Action<string> _log;
        private readonly TimeSpan _tickInterval = TimeSpan.FromSeconds(30);
        private readonly SemaphoreSlim _stateLock = new(1, 1);

        // In-memory copy of tasks, mutated under _stateLock. Persisted on
        // every mutation. List, not dict, because order matters for "fire
        // oldest-due first" if multiple are simultaneously due.
        private List<ScheduledTask> _tasks = new();

        private static readonly JsonSerializerOptions JsonOpts = new()
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
            // ScheduledTask uses init-only setters; need to allow them.
            PropertyNameCaseInsensitive = true,
        };

        public SchedulerService(IHttpClientFactory httpFactory, string stateFilePath, Action<string>? log = null)
        {
            _httpFactory = httpFactory;
            _stateFilePath = stateFilePath;
            _log = log ?? (msg => Console.WriteLine($"[scheduler] {msg}"));
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            await LoadStateAsync();
            _log($"loaded {_tasks.Count} scheduled tasks from {_stateFilePath}");

            // First tick after a short warmup so other startup tasks settle.
            await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);

            using var timer = new PeriodicTimer(_tickInterval);
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    await TickAsync(stoppingToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _log($"tick threw: {ex.GetType().Name}: {ex.Message}");
                }

                try { await timer.WaitForNextTickAsync(stoppingToken); }
                catch (OperationCanceledException) { break; }
            }

            _log("shutting down, final state flush");
            await SaveStateAsync();
        }

        // -- Public API (called from MCP tool endpoints) --

        public async Task<List<ScheduledTask>> ListAsync()
        {
            await _stateLock.WaitAsync();
            try { return _tasks.Select(Clone).ToList(); }
            finally { _stateLock.Release(); }
        }

        public async Task<ScheduledTask> AddAsync(ScheduledTask task)
        {
            await _stateLock.WaitAsync();
            try
            {
                if (_tasks.Any(t => t.Name == task.Name))
                    throw new InvalidOperationException($"Task '{task.Name}' already exists");
                _tasks.Add(task);
                await SaveStateUnsafeAsync();
                _log($"added task '{task.Name}' (type={task.ScheduleType}, next={task.NextFireAt:o})");
                return Clone(task);
            }
            finally { _stateLock.Release(); }
        }

        public async Task<bool> RemoveAsync(string name)
        {
            await _stateLock.WaitAsync();
            try
            {
                var removed = _tasks.RemoveAll(t => t.Name == name);
                if (removed > 0)
                {
                    await SaveStateUnsafeAsync();
                    _log($"removed task '{name}'");
                    return true;
                }
                return false;
            }
            finally { _stateLock.Release(); }
        }

        public async Task<bool> SetPausedAsync(string name, bool paused)
        {
            await _stateLock.WaitAsync();
            try
            {
                var task = _tasks.FirstOrDefault(t => t.Name == name);
                if (task is null) return false;
                task.Paused = paused;
                await SaveStateUnsafeAsync();
                _log($"task '{name}' paused={paused}");
                return true;
            }
            finally { _stateLock.Release(); }
        }

        // -- Ticking --

        private async Task TickAsync(CancellationToken ct)
        {
            var now = DateTimeOffset.UtcNow;
            List<ScheduledTask> due;

            await _stateLock.WaitAsync(ct);
            try
            {
                due = _tasks.Where(t => !t.Paused && t.NextFireAt <= now).ToList();
            }
            finally { _stateLock.Release(); }

            foreach (var task in due)
            {
                if (ct.IsCancellationRequested) break;
                await FireAsync(task, ct);
            }
        }

        private async Task FireAsync(ScheduledTask task, CancellationToken ct)
        {
            _log($"firing '{task.Name}' → tool='{task.ToolName}'");

            string? error = null;
            try
            {
                var http = _httpFactory.CreateClient("mcp");
                // MCP's HTTP transport accepts JSON-RPC at POST /. We're calling
                // a tool by name, so the request shape is:
                //   { "jsonrpc": "2.0", "id": 1, "method": "tools/call",
                //     "params": { "name": "<tool>", "arguments": {...} } }
                var args = JsonSerializer.Deserialize<JsonElement>(task.ToolArgsJson);
                var rpc = new
                {
                    jsonrpc = "2.0",
                    id = 1,
                    method = "tools/call",
                    @params = new
                    {
                        name = task.ToolName,
                        arguments = args,
                    },
                };
                using var resp = await http.PostAsJsonAsync("/", rpc, ct);
                if (!resp.IsSuccessStatusCode)
                {
                    error = $"MCP returned HTTP {(int)resp.StatusCode}";
                }
                else
                {
                    // The MCP server commonly returns SSE-framed responses
                    // ("event: message\ndata: {...}\n\n") even for unary calls,
                    // because we advertise text/event-stream in Accept. A naive
                    // "HTTP 200 = success" check misses JSON-RPC ERROR envelopes
                    // inside that body - which is exactly how scheduled tasks
                    // can appear to fire forever while quietly never doing the
                    // thing they're supposed to do. Read the body, unwrap SSE
                    // if present, and check for an error envelope.
                    var body = await resp.Content.ReadAsStringAsync(ct);
                    var payload = body;
                    if (body.Contains("data:", StringComparison.Ordinal)
                        && !body.TrimStart().StartsWith('{'))
                    {
                        // SSE framing - take the last data: line as the payload.
                        string? extracted = null;
                        foreach (var rawLine in body.Split('\n'))
                        {
                            var trimmed = rawLine.TrimEnd('\r');
                            if (trimmed.StartsWith("data:", StringComparison.Ordinal))
                                extracted = trimmed["data:".Length..].Trim();
                        }
                        payload = extracted ?? body;
                    }

                    try
                    {
                        using var doc = JsonDocument.Parse(payload);
                        if (doc.RootElement.TryGetProperty("error", out var errEl))
                        {
                            // JSON-RPC error envelope - surface it as the
                            // task's LastError so you can see WHY it didn't
                            // actually do anything despite a 200 OK.
                            var msg = errEl.TryGetProperty("message", out var m) && m.ValueKind == JsonValueKind.String
                                ? m.GetString() : errEl.GetRawText();
                            error = $"MCP JSON-RPC error: {msg}";
                        }
                    }
                    catch (JsonException)
                    {
                        // Body wasn't JSON we could parse. Leave error null -
                        // the call did get a 200, and we don't want to invent
                        // a failure just because we couldn't parse the body.
                    }
                }
            }
            catch (Exception ex)
            {
                error = $"{ex.GetType().Name}: {ex.Message}";
            }

            if (error is not null)
            {
                _log($"  '{task.Name}' fire reported error: {error}");
            }

            await _stateLock.WaitAsync(ct);
            try
            {
                task.LastFiredAt = DateTimeOffset.UtcNow;
                task.FireCount++;
                task.LastError = error;

                if (task.Recurring && task.ScheduleType == "cron")
                {
                    // Re-arm. Use NCrontab to compute next occurrence.
                    try
                    {
                        var cron = NCrontab.CrontabSchedule.Parse(task.CronExpression);
                        var next = cron.GetNextOccurrence(DateTime.UtcNow);
                        task.NextFireAt = new DateTimeOffset(next, TimeSpan.Zero);
                        _log($"  '{task.Name}' re-armed for {task.NextFireAt:o}");
                    }
                    catch (Exception ex)
                    {
                        task.LastError = $"cron reparse failed: {ex.Message}";
                        task.Paused = true;
                        _log($"  '{task.Name}' broken cron, paused: {ex.Message}");
                    }
                }
                else
                {
                    // One-shot - remove from the list.
                    _tasks.Remove(task);
                    _log($"  '{task.Name}' was one-shot, removed");
                }

                await SaveStateUnsafeAsync();
            }
            finally { _stateLock.Release(); }
        }

        // -- Persistence --

        private async Task LoadStateAsync()
        {
            if (!File.Exists(_stateFilePath))
            {
                _tasks = new();
                return;
            }
            try
            {
                var json = await File.ReadAllTextAsync(_stateFilePath);
                _tasks = JsonSerializer.Deserialize<List<ScheduledTask>>(json, JsonOpts) ?? new();
            }
            catch (Exception ex)
            {
                _log($"could not load state from {_stateFilePath}: {ex.Message}; starting fresh");
                _tasks = new();
            }
        }

        private async Task SaveStateAsync()
        {
            await _stateLock.WaitAsync();
            try { await SaveStateUnsafeAsync(); }
            finally { _stateLock.Release(); }
        }

        // Caller MUST hold _stateLock. Atomic write via tmpfile+rename so a
        // crash mid-write doesn't corrupt the file.
        private async Task SaveStateUnsafeAsync()
        {
            var dir = Path.GetDirectoryName(_stateFilePath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            var tmp = _stateFilePath + ".tmp";
            var json = JsonSerializer.Serialize(_tasks, JsonOpts);
            await File.WriteAllTextAsync(tmp, json);
            File.Move(tmp, _stateFilePath, overwrite: true);
        }

        // Shallow clone for read-only callers.
        private static ScheduledTask Clone(ScheduledTask t) => new()
        {
            Name = t.Name,
            Description = t.Description,
            ToolName = t.ToolName,
            ToolArgsJson = t.ToolArgsJson,
            ScheduleType = t.ScheduleType,
            CronExpression = t.CronExpression,
            NextFireAt = t.NextFireAt,
            Recurring = t.Recurring,
            CreatedAt = t.CreatedAt,
            LastFiredAt = t.LastFiredAt,
            FireCount = t.FireCount,
            LastError = t.LastError,
            Paused = t.Paused,
        };
    }
}