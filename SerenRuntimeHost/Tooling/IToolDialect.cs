using System.Text.Json;

namespace SerenRuntimeHost.Tooling
{
    /// <summary>
    /// A model-family-specific tool-calling dialect. This is the seam that makes
    /// the tool loop backend-agnostic: the loop never knows whether it's talking
    /// to Qwen, Llama, or GPT - it asks the dialect to parse tool calls out of
    /// model output and to format tool results + tool definitions back in the
    /// shape that model family expects.
    /// </summary>
    /// <remarks>
    /// WHY this exists (the load-bearing reason): the model emits tool calls in
    /// whatever format it was TRAINED on, and that format is baked in at training
    /// time - it can't be changed at inference. Seren's trained model speaks
    /// Qwen/Hermes <c>&lt;tool_call&gt;</c> tags (see SerenCoreLibrary's
    /// ToolCallParser / JinjaTemplateExporter - this dialect mirrors that exact
    /// format so train and infer can NEVER drift). The day the backend model is
    /// swapped to a different family, you write a new IToolDialect and change one
    /// line of wiring; the MCP server, the tool registry, and the loop itself
    /// stay untouched.
    ///
    /// WHAT this is NOT: this is not the tool registry. It does not know what
    /// tools exist - that's the MCP server's job (tools/list is the single source
    /// of truth). This only knows how to TRANSLATE between the model's text and
    /// structured tool calls/results. Keeping "what tools exist" (MCP) separate
    /// from "how tool calls are spoken" (dialect) is what makes plug-and-play
    /// tools possible later: a new tool needs zero dialect changes.
    /// </remarks>
    public interface IToolDialect
    {
        /// <summary>
        /// Family name, for logging/diagnostics (e.g. "qwen-hermes").
        /// </summary>
        string Name { get; }

        /// <summary>
        /// Does this chunk of model output contain at least one tool call?
        /// Cheap pre-check so the loop can short-circuit when the model just
        /// answered in prose (the common case).
        /// </summary>
        bool ContainsToolCall(string modelOutput);

        /// <summary>
        /// Extract all tool calls from model output. Returns empty when none are
        /// present or parsing fails (a malformed call is skipped, not thrown - a
        /// model occasionally emits broken JSON and the turn shouldn't die for it).
        /// </summary>
        IReadOnlyList<ParsedToolCall> ParseToolCalls(string modelOutput);

        /// <summary>
        /// The text the model produced BEFORE its first tool call - its "thinking
        /// out loud" preamble, if any. Used so a model that says "let me check
        /// that..." then calls a tool doesn't lose the preamble.
        /// </summary>
        string ExtractPreamble(string modelOutput);

        /// <summary>
        /// Format a tool's result back into a message the model expects to see.
        /// Returns the (role, content) pair to append to the conversation.
        /// For Qwen this is a "user"-role turn wrapped in &lt;tool_response&gt;
        /// tags (matching how JinjaTemplateExporter renders tool results at
        /// training time - the model was taught to read results in exactly this
        /// shape).
        /// </summary>
        (string Role, string Content) FormatToolResult(string toolName, string resultJson);

        /// <summary>
        /// Format the available tool definitions into the system-prompt block the
        /// model expects, so it knows what's callable. For Qwen this is the
        /// <c># Tools</c> section + the tool-call instruction block (mirroring
        /// JinjaTemplateExporter's system-message construction).
        ///
        /// WHY the dialect owns this rather than relying on llama-server's --jinja
        /// template: an uncensored community fine-tune may ship a chat template
        /// that doesn't handle the tools block correctly, and we don't want tool
        /// support to depend on the GGUF template being right. Owning it here is
        /// self-contained and matches how the training pipeline already works.
        /// Returns null/empty when there are no tools (nothing to inject).
        /// </summary>
        string? FormatToolsForSystemPrompt(IReadOnlyList<McpToolDefinition> tools);
    }

    /// <summary>
    /// A tool call parsed out of model output: the tool name and its arguments
    /// as a raw JSON object (kept as JsonElement so structured/nested args
    /// survive - we don't flatten to string here, unlike the training-side
    /// parser, because MCP tools/call wants the real JSON).
    /// </summary>
    public sealed record ParsedToolCall(string Name, JsonElement Arguments);

    /// <summary>
    /// A tool definition as returned by the MCP server's tools/list. Minimal
    /// shape - name, description, and the JSON-schema input definition. The
    /// dialect turns these into whatever system-prompt format the model wants.
    /// </summary>
    public sealed record McpToolDefinition(string Name, string? Description, JsonElement InputSchema);
}