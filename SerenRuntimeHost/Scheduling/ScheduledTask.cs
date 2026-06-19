namespace SerenRuntimeHost.Scheduling
{
    public sealed class ScheduledTask
    {
        public required string Name { get; init; }
        public required string Description { get; init; }
        public required string ToolName { get; init; }

        // Tool arguments as a JSON object - passed verbatim to the MCP tool.
        // Stored as string for easy JSON round-tripping; deserialized at fire time.
        public string ToolArgsJson { get; init; } = "{}";

        // Schedule shape - "cron" or "relative".
        public required string ScheduleType { get; init; }

        // For cron: the cron expression. For relative: empty.
        public string CronExpression { get; init; } = "";

        // For both: the next time this should fire. Updated after each fire
        // (advanced by cron, or removed if relative one-shot).
        public DateTimeOffset NextFireAt { get; set; }

        // True for cron (re-arms). False for relative (deleted after fire).
        public bool Recurring { get; init; }

        // Audit
        public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
        public DateTimeOffset? LastFiredAt { get; set; }
        public int FireCount { get; set; }
        public string? LastError { get; set; }
        public bool Paused { get; set; }
    }
}
