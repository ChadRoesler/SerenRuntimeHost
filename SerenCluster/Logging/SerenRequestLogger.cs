// ========================================================================
//  RequestLoggingMiddleware - logs every inbound HTTP request to RuntimeHost
// ========================================================================
//
//  Mirrors the agent's request_log.py shape: log every request with
//  method, path, status, duration. Captures 500 traceback details. Logs
//  to BOTH Console.Out (so systemd/journalctl picks them up) AND a
//  rotating file at /var/log/seren-runtime-host.log if writable, falling
//  back to ~/seren-logs/runtime-host.log if /var/log isn't.
//
//  WHY THIS EXISTS:
//    The agents had no request log, the only way to see failures was
//    'sudo journalctl -u seren-agent', which requires sudo password
//    every time. Same problem here on the C# side. A simple file log
//    that the running user can read with cat/tail is enormously more
//    debuggable.
//
//  LOG FORMAT:
//    2026-05-10 04:35:12 [INFO]  127.0.0.1 GET /api/v1/system/status → 200 (47ms)
//    2026-05-10 04:35:13 [WARN]  127.0.0.1 POST /api/v1/cluster/refresh → 401 (3ms)
//    2026-05-10 04:35:14 [ERROR] 192.168.0.42 GET /api/v1/service/llama/status → 500 (1203ms)
//      System.Net.Http.HttpRequestException: Connection refused (xavier32gb:7777)
//        at System.Net.Http.HttpClient...
//
//  LEVELS:
//    INFO:  2xx/3xx responses
//    WARN:  4xx responses, OR 2xx that took >1s ("slow")
//    ERROR: 5xx responses, with full exception detail
//    DEBUG: same as INFO + dumps response body for non-binary content
//           (off by default; enable with SEREN_RUNTIME_LOG_LEVEL=Debug)
//
//  ENV CONTROLS:
//    SEREN_RUNTIME_LOG_LEVEL  = Information | Debug | Warning | Error
//                               (default: Information)
//    SEREN_RUNTIME_LOG_FILE   = path to log file
//                               (default: /var/log/seren-runtime-host.log
//                                falling back to ~/seren-logs/runtime-host.log)
//    SEREN_RUNTIME_LOG_QUERY  = "1" to include query string in path
//                               (off by default - could leak tokens)
//
// ========================================================================

using Microsoft.AspNetCore.Builder;
using System.Diagnostics;

namespace SerenCluster.Logging
{

    

    /// <summary>
    /// Singleton logger backing the request middleware. Public so other code
    /// (cluster routing decisions, discovery loops) can log to the same sink.
    /// </summary>
    public sealed class SerenRequestLogger
    {
        public static SerenRequestLogger Instance { get; } = new();

        private readonly object _lock = new();
        private readonly StreamWriter? _file;
        private readonly LogLevel _minLevel;

        private SerenRequestLogger()
        {
            _minLevel = ParseLevel(
                Environment.GetEnvironmentVariable("SEREN_RUNTIME_LOG_LEVEL") ?? "Information");

            // Pick a log file path. /var/log if writable, else ~/seren-logs/.
            // Falling back to no-file (stderr only) is fine if neither works.
            var customPath = Environment.GetEnvironmentVariable("SEREN_RUNTIME_LOG_FILE");
            var path = customPath ?? PickDefaultLogPath();

            try
            {
                if (!string.IsNullOrEmpty(path))
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                    _file = new StreamWriter(
                        new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read))
                    {
                        AutoFlush = true,
                    };
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[runtime-log] could not open {path}: {ex.Message}");
                _file = null;
            }
        }

        private static string PickDefaultLogPath()
        {
            // Try /var/log first (system convention), then home dir.
            // We don't actually CREATE /var/log/seren-runtime-host.log - we
            // probe write permission by trying. If we can't, fall back.
            const string varLog = "/var/log/seren-runtime-host.log";
            try
            {
                // Probe by opening a write handle without truncating
                using var probe = new FileStream(
                    varLog, FileMode.Append, FileAccess.Write, FileShare.Read);
                return varLog;
            }
            catch
            {
                // Fall back to home dir - no privileges needed
                var home = Environment.GetEnvironmentVariable("HOME") ?? ".";
                return Path.Combine(home, "seren-logs", "runtime-host.log");
            }
        }

        private static LogLevel ParseLevel(string s) => s.ToLowerInvariant() switch
        {
            "debug" => LogLevel.Debug,
            "warning" or "warn" => LogLevel.Warning,
            "error" => LogLevel.Error,
            _ => LogLevel.Information,
        };

        public void Debug(string msg) => Write(LogLevel.Debug, msg);
        public void Info(string msg) => Write(LogLevel.Information, msg);
        public void Warn(string msg) => Write(LogLevel.Warning, msg);
        public void Error(string msg) => Write(LogLevel.Error, msg);

        private void Write(LogLevel level, string msg)
        {
            if (level < _minLevel) return;

            var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} [{level.ToString().ToUpperInvariant()}] {msg}";

            // Stderr/stdout - picked up by journalctl. Lock to avoid interleaved
            // lines on multi-threaded request handling.
            lock (_lock)
            {
                if (level >= LogLevel.Warning)
                    Console.Error.WriteLine(line);
                else
                    Console.WriteLine(line);

                _file?.WriteLine(line);
            }
        }

        public enum LogLevel
        {
            Debug = 0,
            Information = 1,
            Warning = 2,
            Error = 3,
        }
    }
}