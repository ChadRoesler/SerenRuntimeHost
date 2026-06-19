using System.Net.Http.Json;
using System.Text.Json;

namespace SerenRuntimeHost.Tooling
{
    /// <summary>
    /// A thin MCP client for the chat loop: lists tools and calls them over the
    /// MCP server's JSON-RPC-over-HTTP transport (Streamable HTTP, POST /).
    /// </summary>
    /// <remarks>
    /// This extends the fire-and-forget pattern the scheduler uses (which only
    /// checks HTTP status) - the loop NEEDS the result body to feed back to the
    /// model, so this reads and extracts the tools/call result content.
    ///
    /// Uses the "mcp" named HttpClient already registered in Program.cs (base
    /// address http://localhost:6362, 15s timeout). The MCP server is co-located
    /// on the NUC so this is a localhost hop.
    ///
    /// MCP is the SINGLE SOURCE OF TRUTH for what tools exist - this client just
    /// asks it. That's what makes plug-and-play tools work later: when the MCP
    /// server gains runtime-loaded tools, this code never changes because it only
    /// ever asks "what have you got?" and "run this one."
    /// </remarks>
    public sealed class McpToolClient
    {
        private readonly IHttpClientFactory _httpFactory;
        private static readonly JsonSerializerOptions JsonWeb = new(JsonSerializerDefaults.Web);

        public McpToolClient(IHttpClientFactory httpFactory)
        {
            _httpFactory = httpFactory;
        }

        /// <summary>
        /// Read an MCP response body into a JSON-RPC document, handling BOTH
        /// response shapes the Streamable HTTP transport can return:
        ///   - a plain application/json body, or
        ///   - a text/event-stream body framed as "event: …\ndata: {json}\n\n".
        /// Because we advertise Accept: application/json, text/event-stream, the
        /// server is free to pick either, so we must parse either. If it's SSE,
        /// we pull the JSON out of the last data: line. (Caught via test-mcp.sh —
        /// the transport rejects clients that don't accept both, and once you do
        /// accept both, you have to be ready for the stream form.)
        /// </summary>
        private static async Task<JsonDocument?> ReadRpcAsync(HttpResponseMessage resp, CancellationToken ct)
        {
            var body = await resp.Content.ReadAsStringAsync(ct);
            if (string.IsNullOrWhiteSpace(body)) return null;

            // SSE framing? Lines like "data: {…}". Take the last data: payload.
            if (body.Contains("data:", StringComparison.Ordinal)
                && !body.TrimStart().StartsWith('{'))
            {
                string? payload = null;
                foreach (var line in body.Split('\n'))
                {
                    var trimmed = line.TrimEnd('\r');
                    if (trimmed.StartsWith("data:", StringComparison.Ordinal))
                        payload = trimmed["data:".Length..].Trim();
                }
                if (string.IsNullOrWhiteSpace(payload)) return null;
                try { return JsonDocument.Parse(payload); }
                catch (JsonException) { return null; }
            }

            try { return JsonDocument.Parse(body); }
            catch (JsonException) { return null; }
        }

        /// <summary>
        /// List the tools the MCP server currently exposes. Returns empty on any
        /// failure (the loop then just proceeds with no tools - the model answers
        /// in prose, which is a graceful degrade, not an error).
        /// </summary>
        public async Task<IReadOnlyList<McpToolDefinition>> ListToolsAsync(CancellationToken ct)
        {
            try
            {
                var http = _httpFactory.CreateClient("mcp");
                var rpc = new { jsonrpc = "2.0", id = 1, method = "tools/list", @params = new { } };
                using var resp = await http.PostAsJsonAsync("/", rpc, ct);
                if (!resp.IsSuccessStatusCode) return [];

                using var doc = await ReadRpcAsync(resp, ct);
                if (doc is null) return [];

                // JSON-RPC envelope: { result: { tools: [ {name, description, inputSchema}, ... ] } }
                if (!doc.RootElement.TryGetProperty("result", out var result)) return [];
                if (!result.TryGetProperty("tools", out var toolsEl) || toolsEl.ValueKind != JsonValueKind.Array) return [];

                var tools = new List<McpToolDefinition>();
                foreach (var t in toolsEl.EnumerateArray())
                {
                    var name = t.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
                    if (string.IsNullOrEmpty(name)) continue;
                    var desc = t.TryGetProperty("description", out var d) ? d.GetString() : null;
                    JsonElement schema;
                    if (t.TryGetProperty("inputSchema", out var s))
                    {
                        schema = s.Clone();
                    }
                    else
                    {
                        using var empty = JsonDocument.Parse("{}");
                        schema = empty.RootElement.Clone();
                    }
                    tools.Add(new McpToolDefinition(name, desc, schema));
                }
                return tools;
            }
            catch
            {
                // Any failure → no tools. The loop degrades to a plain chat.
                return [];
            }
        }

        /// <summary>
        /// Call a tool by name with the given arguments. Returns the result as a
        /// JSON string to feed back to the model, or an error-shaped string the
        /// model can still read and recover from (we feed failures BACK rather
        /// than hard-failing the turn - a failed Recall shouldn't kill the chat,
        /// the model should just say "I couldn't pull that up").
        /// </summary>
        public async Task<string> CallToolAsync(string name, JsonElement arguments, CancellationToken ct)
        {
            try
            {
                var http = _httpFactory.CreateClient("mcp");
                var rpc = new
                {
                    jsonrpc = "2.0",
                    id = 1,
                    method = "tools/call",
                    @params = new { name, arguments },
                };
                using var resp = await http.PostAsJsonAsync("/", rpc, ct);
                if (!resp.IsSuccessStatusCode)
                {
                    return JsonSerializer.Serialize(new { error = $"tool '{name}' returned HTTP {(int)resp.StatusCode}" });
                }

                using var doc = await ReadRpcAsync(resp, ct);
                if (doc is null)
                {
                    return JsonSerializer.Serialize(new { error = $"tool '{name}' returned an unreadable response" });
                }

                // JSON-RPC: { result: { content: [ {type:"text", text:"..."} ], isError?: bool } }
                if (doc.RootElement.TryGetProperty("result", out var result))
                {
                    // Prefer the text content blocks (MCP standard result shape).
                    if (result.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.Array)
                    {
                        var texts = new List<string>();
                        foreach (var block in content.EnumerateArray())
                        {
                            if (block.TryGetProperty("text", out var txt) && txt.ValueKind == JsonValueKind.String)
                                texts.Add(txt.GetString() ?? "");
                        }
                        if (texts.Count > 0) return string.Join("\n", texts);
                    }
                    // Fall back to the raw result JSON if there's no text content.
                    return result.GetRawText();
                }

                // JSON-RPC error envelope.
                if (doc.RootElement.TryGetProperty("error", out var err))
                {
                    return JsonSerializer.Serialize(new { error = err.GetRawText() });
                }

                return JsonSerializer.Serialize(new { error = $"tool '{name}' returned no result" });
            }
            catch (Exception ex)
            {
                // Feed the failure back to the model as a readable result.
                return JsonSerializer.Serialize(new { error = $"tool '{name}' failed: {ex.Message}" });
            }
        }
    }
}