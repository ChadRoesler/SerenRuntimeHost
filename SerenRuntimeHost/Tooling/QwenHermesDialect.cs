using System.Text;
using System.Text.Json;

namespace SerenRuntimeHost.Tooling
{
    /// <summary>
    /// The Qwen / Hermes tool-calling dialect. This MIRRORS the format Seren's
    /// model was trained on, defined in SerenCoreLibrary:
    ///   - emission: ToolCallParser parses &lt;tool_call&gt;{json}&lt;/tool_call&gt;
    ///   - results:  JinjaTemplateExporter wraps tool results in
    ///               &lt;tool_response&gt;...&lt;/tool_response&gt; as a USER turn
    ///   - tools:    the # Tools system block + instruction text
    ///
    /// If you change the training-side format, change this in lockstep - the
    /// train/infer symmetry is the whole reason tool-calling works. (Filed as a
    /// honest coupling: these two files must agree. A test that round-trips a
    /// known tool call through both would be the right guard; not built yet.)
    /// </summary>
    public sealed class QwenHermesDialect : IToolDialect
    {
        private const string OpenTag = "<tool_call>";
        private const string CloseTag = "</tool_call>";

        public string Name => "qwen-hermes";

        public bool ContainsToolCall(string modelOutput)
        {
            // Lenient: an OPEN tag alone is enough to suspect a tool call. The
            // base uncensored Qwen tune often emits <tool_call>{...} and stops
            // before producing </tool_call> (likely because it wasn't trained
            // on the matched-pair discipline our SerenCoreLibrary training data
            // enforces). Postel's law for tool calls: be strict in what we
            // emit (training side), lenient in what we accept (inference side).
            //
            // The ACTUAL test for "is there a usable tool call" lives in
            // ParseToolCalls - it tries to find a balanced JSON object after
            // the open tag and only returns the call if it parses. So this
            // method is a CHEAP pre-check; the parser is the real validator.
            return modelOutput.Contains(OpenTag, StringComparison.Ordinal);
        }

        public IReadOnlyList<ParsedToolCall> ParseToolCalls(string modelOutput)
        {
            // Lifted from SerenCoreLibrary.Helpers.ToolCallParser but RELAXED:
            // the training-side parser requires matched <tool_call>...</tool_call>
            // pairs because the training data enforces that discipline. The
            // base model at inference may emit only the open tag + JSON (no
            // close), so this version finds the JSON object FIRST and treats
            // the closing tag as optional - if it's there we honor it, if not
            // we use the JSON's natural end.
            //
            // We keep arguments as a JsonElement (structured) rather than
            // flattening to Dictionary<string,string>, because MCP's tools/call
            // wants the real JSON - a tool taking a list or nested object would
            // be corrupted by the training-side string-flatten. (Fixes the
            // "Dictionary<string,string> flattens structure" disclosure from
            // the pipeline review.)
            var results = new List<ParsedToolCall>();
            var pos = 0;

            while (pos < modelOutput.Length)
            {
                var openIdx = modelOutput.IndexOf(OpenTag, pos, StringComparison.Ordinal);
                if (openIdx < 0) break;

                var jsonStart = openIdx + OpenTag.Length;
                // Skip whitespace/newlines after the open tag to find the {
                while (jsonStart < modelOutput.Length && char.IsWhiteSpace(modelOutput[jsonStart]))
                    jsonStart++;

                if (jsonStart >= modelOutput.Length || modelOutput[jsonStart] != '{')
                {
                    // No JSON object after the open tag - advance past this open
                    // and keep looking. Could be a stray tag in prose.
                    pos = openIdx + OpenTag.Length;
                    continue;
                }

                // Walk forward counting braces (respecting strings + escapes) to
                // find the matching close brace. This is what makes the parser
                // tolerant of missing </tool_call> - we don't NEED the close tag
                // to know where the call ends; the JSON tells us.
                var jsonEnd = FindJsonObjectEnd(modelOutput, jsonStart);
                if (jsonEnd < 0)
                {
                    // Truncated/malformed JSON. Stop trying to parse from here -
                    // anything later would be misaligned.
                    break;
                }

                var jsonText = modelOutput.Substring(jsonStart, jsonEnd - jsonStart + 1);
                try
                {
                    using var doc = JsonDocument.Parse(jsonText);
                    var root = doc.RootElement;
                    var name = root.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";

                    JsonElement args;
                    if (root.TryGetProperty("arguments", out var argsEl))
                    {
                        args = argsEl.Clone();
                    }
                    else
                    {
                        // No arguments → empty object, so tools/call still gets
                        // a valid (if empty) arguments field. Required for tools
                        // like get_current_time that take no parameters.
                        using var empty = JsonDocument.Parse("{}");
                        args = empty.RootElement.Clone();
                    }

                    if (!string.IsNullOrEmpty(name))
                        results.Add(new ParsedToolCall(name, args));
                }
                catch (JsonException)
                {
                    // Malformed - skip and continue past this call.
                }

                // Advance past this call. If a close tag follows the JSON
                // (training-style clean pair), skip over it too.
                var afterJson = jsonEnd + 1;
                var trySkipWs = afterJson;
                while (trySkipWs < modelOutput.Length && char.IsWhiteSpace(modelOutput[trySkipWs]))
                    trySkipWs++;
                if (trySkipWs < modelOutput.Length
                    && string.Compare(modelOutput, trySkipWs, CloseTag, 0, CloseTag.Length, StringComparison.Ordinal) == 0)
                {
                    pos = trySkipWs + CloseTag.Length;
                }
                else
                {
                    pos = afterJson;
                }
            }

            return results;
        }

        /// <summary>
        /// Walks forward from the position of an opening '{' and returns the
        /// index of the matching '}'. Respects JSON string literals (so a
        /// brace inside a string doesn't fool the counter) and backslash
        /// escapes inside strings. Returns -1 if the object never closes
        /// (truncated output) - the caller then bails on this parse.
        /// </summary>
        private static int FindJsonObjectEnd(string s, int openBracePos)
        {
            int depth = 0;
            bool inStr = false;
            bool esc = false;
            for (int i = openBracePos; i < s.Length; i++)
            {
                char c = s[i];
                if (inStr)
                {
                    if (esc) { esc = false; continue; }
                    if (c == '\\') { esc = true; continue; }
                    if (c == '"') inStr = false;
                    continue;
                }
                if (c == '"') { inStr = true; continue; }
                if (c == '{') depth++;
                else if (c == '}')
                {
                    depth--;
                    if (depth == 0) return i;
                }
            }
            return -1;
        }

        public string ExtractPreamble(string modelOutput)
        {
            var idx = modelOutput.IndexOf(OpenTag, StringComparison.Ordinal);
            return idx < 0 ? modelOutput : modelOutput[..idx].TrimEnd();
        }

        public (string Role, string Content) FormatToolResult(string toolName, string resultJson)
        {
            // Mirror JinjaTemplateExporter: tool results come back as a USER turn
            // wrapped in <tool_response> tags. The model was trained to read its
            // tool results in exactly this shape, so this is non-negotiable.
            // (The training exporter groups consecutive tool responses into one
            // user turn; here we emit one per result and let the loop append them
            // - functionally equivalent for the model, simpler for the loop.)
            var content = $"<tool_response>\n{resultJson}\n</tool_response>";
            return ("user", content);
        }

        public string? FormatToolsForSystemPrompt(IReadOnlyList<McpToolDefinition> tools)
        {
            if (tools.Count == 0) return null;

            // Mirror the # Tools block + instructions from JinjaTemplateExporter.
            // The model was trained to see tool definitions in this format in its
            // system prompt, then emit <tool_call> when it wants one.
            //
            // The DISCIPLINE language below is the belt to --jinja's suspenders.
            // Observed on the uncensored base tune: when a tool call was the
            // right move, the model would HALLUCINATE the call instead of
            // emitting one - italic "*Checking status...*" narration with no
            // real tool fire. The italicized fake-action pattern is the tell.
            // These instructions explicitly forbid that pattern, naming both
            // what TO do (emit the tag block) and what NOT to do (narrate
            // having called it). A well-trained Seren model won't need this,
            // but on a base model it's a meaningful nudge toward discipline.
            var sb = new StringBuilder();
            sb.AppendLine("# Tools");
            sb.AppendLine();
            sb.AppendLine("You have access to the following tools. Call one by emitting a");
            sb.AppendLine("JSON object inside <tool_call></tool_call> tags:");
            sb.AppendLine();
            sb.AppendLine("<tool_call>");
            sb.AppendLine("{\"name\": \"tool_name\", \"arguments\": {\"arg\": \"value\"}}");
            sb.AppendLine("</tool_call>");
            sb.AppendLine();
            sb.AppendLine("Rules for tool use:");
            sb.AppendLine("- To USE a tool, you MUST emit the <tool_call> block above. There");
            sb.AppendLine("  is no other way to invoke a tool.");
            sb.AppendLine("- DO NOT narrate or pretend to call tools. Phrases like");
            sb.AppendLine("  \"*checking status...*\" or \"I'll fetch that for you\" without an");
            sb.AppendLine("  actual <tool_call> block are LIES - the tool did NOT run.");
            sb.AppendLine("- If you don't know something (current time, cluster state, what's");
            sb.AppendLine("  in memory, what model you are), CALL the relevant tool. Do not");
            sb.AppendLine("  guess or invent plausible-sounding values.");
            sb.AppendLine("- After a tool returns, use its real result. Do not embellish or");
            sb.AppendLine("  replace tool output with what you think it \"should\" say.");
            sb.AppendLine();
            sb.AppendLine("Available tools:");
            foreach (var t in tools)
            {
                // Present each tool as its JSON-schema spec, which is what the MCP
                // tools/list gives us and what Qwen's training format expects.
                var spec = new
                {
                    name = t.Name,
                    description = t.Description ?? "",
                    parameters = t.InputSchema,
                };
                sb.AppendLine(JsonSerializer.Serialize(spec));
            }
            return sb.ToString();
        }
    }
}