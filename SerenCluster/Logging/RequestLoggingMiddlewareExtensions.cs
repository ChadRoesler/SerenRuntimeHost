using Microsoft.AspNetCore.Builder;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;

namespace SerenCluster.Logging
{
    public static class RequestLoggingMiddlewareExtensions
    {
        /// <summary>
        /// Adds the Seren request logger to the pipeline. Must be added BEFORE
        /// auth so 401-rejected requests are also logged (knowing your auth is
        /// failing is half the debug battle).
        /// </summary>
        public static IApplicationBuilder UseSerenRequestLogging(this IApplicationBuilder app)
        {
            var logger = SerenRequestLogger.Instance;
            return app.Use(async (ctx, next) =>
            {
                var sw = Stopwatch.StartNew();
                var method = ctx.Request.Method;
                var path = ctx.Request.Path.Value ?? "/";

                // Optionally include query string. Off by default to avoid
                // accidentally logging bearer tokens that someone passed via
                // ?token=... (we don't accept that, but defense in depth).
                if (Environment.GetEnvironmentVariable("SEREN_RUNTIME_LOG_QUERY") == "1"
                    && ctx.Request.QueryString.HasValue)
                {
                    path += ctx.Request.QueryString.Value;
                }

                var client = ctx.Connection.RemoteIpAddress?.ToString() ?? "?";

                try
                {
                    await next();
                    sw.Stop();
                    var status = ctx.Response.StatusCode;
                    var ms = sw.ElapsedMilliseconds;
                    var line = $"{client} {method} {path} → {status} ({ms}ms)";

                    // Pick level by status. Slow-request warning at >1s for 2xx.
                    if (status >= 500)
                        logger.Error(line);
                    else if (status >= 400)
                        logger.Warn(line);
                    else if (ms > 1000)
                        logger.Warn($"{line} [slow]");
                    else
                        logger.Info(line);
                }
                catch (Exception ex)
                {
                    // Unhandled exception escaped a route. Log full detail
                    // before re-raising - the framework's exception handler
                    // will turn it into a 500 for the client.
                    sw.Stop();
                    var ms = sw.ElapsedMilliseconds;
                    logger.Error(
                        $"{client} {method} {path} → 500 EXCEPTION ({ms}ms)\n" +
                        $"  {ex.GetType().Name}: {ex.Message}\n{ex}");
                    throw;
                }
            });
        }
    }
}
