using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using SerenCluster.Constants;

namespace SerenCluster.Auth
{

    /// <summary>
    /// Constant-time bearer-token middleware for RuntimeHost. Different concern
    /// from per-Jetson tokens (those are SerenCluster's outbound auth; this is
    /// SerenRuntimeHost's inbound auth for the chat-app and dashboard).
    /// </summary>
    /// <remarks>
    /// Skipped paths: <c>/</c>, <c>/api/v1/system/ping</c>, <c>/api/v1/system/version</c>.
    /// Same skip list as the agent for consistency.
    ///
    /// If the configured token is null/empty, auth is DISABLED - every request
    /// goes through with an <c>X-Seren-Auth: disabled</c> response header. This
    /// is the dev-mode escape hatch; production configs should always set a
    /// token.
    /// </remarks>
    public static class BearerAuthMiddleware
    {
        private static readonly HashSet<PathString> PublicPaths =
        [
            new("/"),
            new($"/api/{ResourceStrings.ApiVersion}/system/ping"),
            new($"/api/{ResourceStrings.ApiVersion}/system/version"),
        ];

        public static IApplicationBuilder UseSerenBearerAuth(
            this IApplicationBuilder app,
            string? expectedToken)
        {
            return app.Use(async (ctx, next) =>
            {
                // Dev mode: no token configured, accept everything but flag it.
                if (string.IsNullOrWhiteSpace(expectedToken))
                {
                    ctx.Response.Headers["X-Seren-Auth"] = "disabled-no-token-configured";
                    await next();
                    return;
                }

                if (PublicPaths.Contains(ctx.Request.Path))
                {
                    await next();
                    return;
                }

                var auth = ctx.Request.Headers.Authorization.ToString();
                if (string.IsNullOrEmpty(auth) || !auth.StartsWith("Bearer ", StringComparison.Ordinal))
                {
                    await UnauthorizedAsync(ctx, "missing bearer token");
                    return;
                }

                var provided = auth[7..].Trim();
                if (!ConstantTimeEquals(provided, expectedToken))
                {
                    await UnauthorizedAsync(ctx, "invalid token");
                    return;
                }

                await next();
            });
        }

        private static async Task UnauthorizedAsync(HttpContext ctx, string detail)
        {
            ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
            ctx.Response.ContentType = "application/json";
            await ctx.Response.WriteAsync($"{{\"error\":\"unauthorized\",\"detail\":\"{detail}\"}}");
        }

        /// <summary>
        /// Compare two strings in O(max(len)) time to avoid timing-leak attacks
        /// on the prefix. Length mismatch leaks length but not contents - that's
        /// the standard trade-off, fine for our threat model.
        /// </summary>
        private static bool ConstantTimeEquals(string a, string b)
        {
            if (a.Length != b.Length) return false;
            var diff = 0;
            for (var i = 0; i < a.Length; i++)
                diff |= a[i] ^ b[i];
            return diff == 0;
        }
    }
}