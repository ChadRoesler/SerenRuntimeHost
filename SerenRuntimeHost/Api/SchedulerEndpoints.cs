using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using SerenCluster.Constants;
using SerenRuntimeHost.Scheduling;

namespace SerenRuntimeHost.Api
{
    /// <summary>
    /// <c>/api/v1/scheduler/*</c> - CRUD for scheduled tasks. The MCP server's
    /// scheduler tools call these endpoints; chat app could too if a future
    /// dashboard panel wants to surface "what's queued."
    /// </summary>
    /// <remarks>
    /// All endpoints require auth (BearerAuthMiddleware applies). The
    /// scheduler firing actually-runs-tools is high-trust - we wouldn't want
    /// an unauthed caller scheduling restart-everything-now.
    /// </remarks>
    internal static class SchedulerEndpoints
    {
        public static void Map(IEndpointRouteBuilder app, SchedulerService scheduler)
        {
            // List all scheduled tasks
            app.MapGet($"/api/{ResourceStrings.ApiVersion}/scheduler/tasks",
                async () =>
                {
                    var tasks = await scheduler.ListAsync();
                    return Results.Json(new { tasks });
                });

            // Add a task
            app.MapPost($"/api/{ResourceStrings.ApiVersion}/scheduler/tasks",
                async (HttpContext http, CancellationToken ct) =>
                {
                    AddTaskRequest? body;
                    try { body = await http.Request.ReadFromJsonAsync<AddTaskRequest>(ct); }
                    catch (Exception ex)
                    {
                        return Results.BadRequest(new { error = $"malformed body: {ex.Message}" });
                    }
                    if (body is null)
                        return Results.BadRequest(new { error = "empty body" });
                    if (string.IsNullOrWhiteSpace(body.Name))
                        return Results.BadRequest(new { error = "'name' required" });
                    if (string.IsNullOrWhiteSpace(body.ToolName))
                        return Results.BadRequest(new { error = "'tool_name' required" });
                    if (string.IsNullOrWhiteSpace(body.ScheduleType))
                        return Results.BadRequest(new { error = "'schedule_type' required (cron|relative)" });

                    // Compute next fire time based on schedule type
                    DateTimeOffset nextFire;
                    bool recurring;
                    string cronExpr = "";

                    if (string.Equals(body.ScheduleType, "cron", StringComparison.OrdinalIgnoreCase))
                    {
                        if (string.IsNullOrWhiteSpace(body.CronExpression))
                            return Results.BadRequest(new { error = "cron schedule requires 'cron_expression'" });
                        try
                        {
                            var cron = NCrontab.CrontabSchedule.Parse(body.CronExpression);
                            nextFire = new DateTimeOffset(cron.GetNextOccurrence(DateTime.UtcNow), TimeSpan.Zero);
                            cronExpr = body.CronExpression;
                            recurring = true;
                        }
                        catch (Exception ex)
                        {
                            return Results.BadRequest(new { error = $"invalid cron: {ex.Message}" });
                        }
                    }
                    else if (string.Equals(body.ScheduleType, "relative", StringComparison.OrdinalIgnoreCase))
                    {
                        if (string.IsNullOrWhiteSpace(body.RelativeOffset))
                            return Results.BadRequest(new { error = "relative schedule requires 'relative_offset' like '2h', '30m', '5d'" });
                        if (!TryParseOffset(body.RelativeOffset, out var ts))
                            return Results.BadRequest(new { error = $"can't parse offset '{body.RelativeOffset}'; use Nh, Nm, Nd" });
                        nextFire = DateTimeOffset.UtcNow.Add(ts);
                        recurring = false;
                    }
                    else
                    {
                        return Results.BadRequest(new { error = $"unknown schedule_type '{body.ScheduleType}'; use 'cron' or 'relative'" });
                    }

                    var task = new ScheduledTask
                    {
                        Name = body.Name,
                        Description = body.Description ?? "",
                        ToolName = body.ToolName,
                        ToolArgsJson = body.ToolArgsJson ?? "{}",
                        ScheduleType = body.ScheduleType.ToLowerInvariant(),
                        CronExpression = cronExpr,
                        NextFireAt = nextFire,
                        Recurring = recurring,
                    };

                    try
                    {
                        var created = await scheduler.AddAsync(task);
                        return Results.Json(new { task = created });
                    }
                    catch (InvalidOperationException ex)
                    {
                        return Results.Conflict(new { error = ex.Message });
                    }
                });

            // Delete a task
            app.MapDelete($"/api/{ResourceStrings.ApiVersion}/scheduler/tasks/{{name}}",
                async (string name) =>
                {
                    var removed = await scheduler.RemoveAsync(name);
                    if (!removed)
                        return Results.NotFound(new { error = $"no task named '{name}'" });
                    return Results.Json(new { removed = name });
                });

            // Pause/resume
            app.MapPost($"/api/{ResourceStrings.ApiVersion}/scheduler/tasks/{{name}}/pause",
                async (string name) =>
                {
                    var ok = await scheduler.SetPausedAsync(name, true);
                    if (!ok) return Results.NotFound(new { error = $"no task named '{name}'" });
                    return Results.Json(new { paused = name });
                });

            app.MapPost($"/api/{ResourceStrings.ApiVersion}/scheduler/tasks/{{name}}/resume",
                async (string name) =>
                {
                    var ok = await scheduler.SetPausedAsync(name, false);
                    if (!ok) return Results.NotFound(new { error = $"no task named '{name}'" });
                    return Results.Json(new { resumed = name });
                });
        }

        // Parse "2h", "30m", "5d", "90s" → TimeSpan
        private static bool TryParseOffset(string s, out TimeSpan result)
        {
            result = TimeSpan.Zero;
            if (string.IsNullOrWhiteSpace(s) || s.Length < 2) return false;
            var unit = s[^1];
            if (!int.TryParse(s.AsSpan(0, s.Length - 1), out var n) || n < 0) return false;
            result = unit switch
            {
                's' => TimeSpan.FromSeconds(n),
                'm' => TimeSpan.FromMinutes(n),
                'h' => TimeSpan.FromHours(n),
                'd' => TimeSpan.FromDays(n),
                _ => TimeSpan.Zero,
            };
            return result != TimeSpan.Zero || n == 0;
        }

        // Bind from snake_case JSON since the MCP tool POSTs with that
        // shape (matches the rest of the API surface). ASP.NET defaults to
        // camelCase so we declare each name explicitly. Worth filing as
        // tech debt: configure global snake_case naming on RuntimeHost's
        // JsonOptions so this kind of friction stops happening per-endpoint.
        private sealed record AddTaskRequest(
            [property: System.Text.Json.Serialization.JsonPropertyName("name")] string? Name,
            [property: System.Text.Json.Serialization.JsonPropertyName("description")] string? Description,
            [property: System.Text.Json.Serialization.JsonPropertyName("tool_name")] string? ToolName,
            [property: System.Text.Json.Serialization.JsonPropertyName("tool_args_json")] string? ToolArgsJson,
            [property: System.Text.Json.Serialization.JsonPropertyName("schedule_type")] string? ScheduleType,
            [property: System.Text.Json.Serialization.JsonPropertyName("cron_expression")] string? CronExpression,
            [property: System.Text.Json.Serialization.JsonPropertyName("relative_offset")] string? RelativeOffset);
    }
}
