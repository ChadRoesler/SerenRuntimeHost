using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

using SerenCluster;
using SerenCluster.Constants;
using SerenRuntimeHost.Tooling;

namespace SerenRuntimeHost.Api
{

    /// <summary>
    /// <c>/api/v1/chat</c> - the inference entrypoint. This is the seam the
    /// chat client (and eventually the CLI, and eventually Seren-on-the-rack
    /// talking to herself) hits to actually get a model response.
    /// </summary>
    /// <remarks>
    /// ARCHITECTURE (Option A - RuntimeHost owns the loop):
    ///   client → POST /api/v1/chat → resolve llama node via the cluster
    ///   router → proxy to that node's llama-server /v1/chat/completions →
    ///   shape the response back to the client.
    ///
    /// This is deliberately the BASE happy-path. What's here:
    ///   - cluster routing (GetServiceUrlAsync resolves where llama lives NOW)
    ///   - failure-driven invalidation (connection failure marks the node
    ///     offline so the next request re-routes)
    ///   - OpenAI-compatible upstream call (stock llama-server exposes
    ///     /v1/chat/completions; --reasoning off is set in the launch script)
    ///   - system-prompt + sampling param passthrough
    ///
    /// What's NOT here yet (next beats, built on this seam):
    ///   - streaming WITH tools (/api/v1/chat/stream is non-tool today; the tool
    ///     loop lives in the non-streaming path first because tools resolve before
    ///     the visible answer streams - adding mid-stream "calling Recall…" status
    ///     events is the next iteration).
    ///
    /// What IS here now:
    ///   - the MCP tool-call loop (RuntimeHost as MCP client → :6362), via a
    ///     pluggable IToolDialect so it's backend-model-agnostic. tool_rounds in
    ///     the response is real.
    ///   - tool-driven memory: tool DEFINITIONS are injected so the model knows it
    ///     can Recall/Remember, but memory content is never auto-injected - the
    ///     model calls Recall when IT wants. "Your memories are yours."
    ///
    /// WHY proxy instead of letting the client hit llama directly: one brain.
    /// The client must not know which node llama lives on - that knowledge is
    /// the orchestrator's job, and centralizing it here is what makes the
    /// nano-only "I just have one box" story collapse into "a cluster of one"
    /// with zero special-casing.
    /// </remarks>
    internal static class ChatEndpoints
    {
        // llama-server is stock llama.cpp's server: it speaks the OpenAI
        // chat-completions dialect at /v1/chat/completions. We talk that
        // dialect rather than the native /completion endpoint because it
        // handles the chat template (system/user/assistant turns) for us -
        // the model's chat template is baked into the GGUF, and llama-server
        // applies it. Native /completion would make US responsible for
        // formatting turns, which is a per-model footgun we don't want.
        private const string LlamaCapability = "llama";
        private const string LlamaChatPath = "/v1/chat/completions";

        // Default sampling. These are only used when the client doesn't
        // override. Kept conservative - the model's own defaults are fine
        // for a base chat; the point tonight is "it speaks," not tuning.
        private const int DefaultMaxTokens = 1024;

        // Tool-loop round cap. Generous enough for multi-step reasoning ("recall
        // something, then search to confirm it") but low enough that a confused
        // model can't loop forever burning tokens. Hard-stop with an honest note
        // when hit. 5 is the agreed ceiling.
        private const int MaxToolRounds = 5;

        private static readonly JsonSerializerOptions JsonWeb = new(JsonSerializerDefaults.Web);

        // -- Chat activity tracking (for TimeSinceLastMessage MCP tool) --
        // Last unix-second timestamp at which a real /chat or /chat/stream
        // request arrived (i.e. the user said something). 0 = never seen.
        //
        // Long writes/reads aren't atomic at the C# language level even on
        // x64, so we go through Interlocked - tiny cost, correct semantics.
        // In-memory only: a process restart resets to 0 and the MCP tool
        // reports posture="unknown" until the first chat request, which is
        // the right honest answer.
        private static long _lastUserAtUnix = 0;

        private static void RecordChatActivity()
            => Interlocked.Exchange(ref _lastUserAtUnix,
                DateTimeOffset.UtcNow.ToUnixTimeSeconds());

        private static long ReadLastUserAt()
            => Interlocked.Read(ref _lastUserAtUnix);

        public static void Map(IEndpointRouteBuilder app, JetsonClusterClient cluster, IToolDialect dialect)
        {
            app.MapPost($"/api/{ResourceStrings.ApiVersion}/chat",
                async (ChatRequest req, IHttpClientFactory httpFactory, CancellationToken ct) =>
                {
                    if (string.IsNullOrWhiteSpace(req.Prompt))
                    {
                        return Results.BadRequest(new { error = "prompt is required" });
                    }

                    // Record a real chat arrival - AFTER the empty-prompt
                    // reject so a 400 doesn't count as "the user spoke."
                    RecordChatActivity();

                    // -- 1. Where does llama live right now? --
                    // GetServiceUrlAsync re-resolves every call (cheap, cached
                    // internally) so a node that moved/died since last request
                    // gets routed around. Returns null if no online node has
                    // llama installed.
                    var routed = await cluster.GetServiceUrlAsync(LlamaCapability, ct);
                    if (routed is null || routed.BaseUrl is null)
                    {
                        // No online llama node. This is the honest "the rack
                        // isn't ready" response - distinct from a model error.
                        return Results.Json(new
                        {
                            error = "no online node is serving llama",
                            hint = "start the llama service on a node (dashboard or "
                                 + "~/start_llama.sh), then retry. Check /api/v1/system/status.",
                        }, statusCode: StatusCodes.Status503ServiceUnavailable);
                    }

                    // -- 2. Build the base message list --
                    // Shared with the streaming endpoint via BuildMessages so the two
                    // paths can NEVER drift in how they assemble a request.
                    var messages = BuildMessages(req);

                    // -- 2b. Discover tools + inject them into the system prompt --
                    // MCP is the single source of truth for what tools exist. We ask
                    // it once per chat request (not cached across requests - the tool
                    // set can change on redeploy; not re-fetched each round - it can't
                    // change mid-turn). The dialect formats them into the model's
                    // expected # Tools system block.
                    //
                    // Tool-driven memory by design: we inject the tool DEFINITIONS so
                    // the model KNOWS it can Recall/Remember, but we never auto-inject
                    // memory content - the model calls Recall when IT wants to. "Your
                    // memories are yours" carried all the way into the loop.
                    var mcp = new McpToolClient(httpFactory);
                    var tools = await mcp.ListToolsAsync(ct);
                    var toolsBlock = dialect.FormatToolsForSystemPrompt(tools);
                    if (!string.IsNullOrEmpty(toolsBlock))
                    {
                        // Prepend the tools block as/into the system message. If there's
                        // already a system message (from req.SystemPrompt), we put tools
                        // FIRST then the persona prompt, matching the training exporter's
                        // ordering (# Tools block, then system prompt).
                        if (messages.Count > 0 && messages[0].Role == "system")
                        {
                            messages[0] = new OpenAiMessage("system", toolsBlock + "\n\n" + messages[0].Content);
                        }
                        else
                        {
                            messages.Insert(0, new OpenAiMessage("system", toolsBlock));
                        }
                    }

                    // -- 3. The tool loop --
                    // call llama → if it emitted tool calls, execute them via MCP,
                    // append results, loop → until it returns prose or we hit the cap.
                    var http = httpFactory.CreateClient("llama-upstream");
                    var url = routed.BaseUrl.TrimEnd('/') + LlamaChatPath;
                    int toolRounds = 0;
                    string finalText = "";
                    string? finalModel = null;
                    OpenAiUsage? lastUsage = null;

                    try
                    {
                        while (true)
                        {
                            var upstream = BuildUpstreamRequest(req, messages, stream: false);
                            using var resp = await http.PostAsJsonAsync(url, upstream, JsonWeb, ct);
                            if (!resp.IsSuccessStatusCode)
                            {
                                var bodyText = await resp.Content.ReadAsStringAsync(ct);
                                return Results.Json(new
                                {
                                    error = $"llama-server returned HTTP {(int)resp.StatusCode}",
                                    node = routed.NodeName,
                                    detail = bodyText.Length > 500 ? bodyText[..500] + "…" : bodyText,
                                }, statusCode: StatusCodes.Status502BadGateway);
                            }

                            var completion = await resp.Content.ReadFromJsonAsync<OpenAiChatResponse>(JsonWeb, ct);
                            var text = completion?.Choices?.FirstOrDefault()?.Message?.Content ?? "";
                            finalModel = completion?.Model;
                            lastUsage = completion?.Usage;

                            // No tool calls → the model answered. Done.
                            if (!dialect.ContainsToolCall(text))
                            {
                                finalText = text;
                                break;
                            }

                            // Hit the round cap with the model still wanting tools →
                            // stop and return what we have plus an honest note. Better
                            // than looping forever burning tokens on a confused model.
                            if (toolRounds >= MaxToolRounds)
                            {
                                var preamble = dialect.ExtractPreamble(text);
                                finalText = string.IsNullOrWhiteSpace(preamble)
                                    ? "(I got stuck calling tools and couldn't finish that - try rephrasing?)"
                                    : preamble + "\n\n(I ran out of tool-call attempts finishing that.)";
                                break;
                            }

                            // Execute each tool call, append the assistant's tool-call
                            // turn + each result, then loop for the model's next move.
                            var calls = dialect.ParseToolCalls(text);

                            // The assistant's turn that CONTAINED the tool calls must be
                            // in history so the model sees its own call alongside the
                            // result (matches the training round-trip structure).
                            messages.Add(new OpenAiMessage("assistant", text));

                            foreach (var call in calls)
                            {
                                var resultJson = await mcp.CallToolAsync(call.Name, call.Arguments, ct);
                                var (role, content) = dialect.FormatToolResult(call.Name, resultJson);
                                messages.Add(new OpenAiMessage(role, content));
                            }

                            // Trim oversized tool results before the next round so a
                            // single fat tool result can't blow the model's ctx.
                            EnforceTokenBudget(messages);

                            toolRounds++;
                        }

                        // -- 4. Shape the response. tool_rounds is now REAL. --
                        return Results.Json(new ChatResponse(
                            Response: finalText,
                            Model: finalModel ?? "seren",
                            Node: routed.NodeName,
                            ToolRounds: toolRounds,
                            Usage: lastUsage));
                    }
                    catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                    {
                        // Upstream timeout. The model is taking too long OR the node
                        // is wedged. Mark offline so the next request re-routes - if
                        // it was just a slow generation the next discovery sweep
                        // brings it back; if it's truly wedged, routing avoids it.
                        cluster.MarkNodeOffline(routed.NodeName, "llama-server chat call timed out");
                        return Results.Json(new
                        {
                            error = "llama-server timed out",
                            node = routed.NodeName,
                            hint = "the model may be loading or generating a long response. Retry.",
                        }, statusCode: StatusCodes.Status504GatewayTimeout);
                    }
                    catch (HttpRequestException ex)
                    {
                        // Connection-level failure = the node is unreachable. This is
                        // exactly the failure-driven-invalidation case the cluster
                        // client documents: mark it offline, next request re-routes.
                        cluster.MarkNodeOffline(routed.NodeName, $"llama-server unreachable: {ex.Message}");
                        return Results.Json(new
                        {
                            error = "llama-server unreachable",
                            node = routed.NodeName,
                            detail = ex.Message,
                        }, statusCode: StatusCodes.Status502BadGateway);
                    }
                });

            // Lightweight health/readiness for the chat path specifically.
            // The client's "Health" button can hit this to learn "is there a
            // model I can talk to RIGHT NOW" - distinct from /system/health
            // which is about node reachability, not inference readiness.
            app.MapGet($"/api/{ResourceStrings.ApiVersion}/chat/health",
                async (CancellationToken ct) =>
                {
                    var routed = await cluster.GetServiceUrlAsync(LlamaCapability, ct);
                    if (routed is null || routed.BaseUrl is null)
                    {
                        return Results.Json(new
                        {
                            ok = false,
                            inference_backend = "llama.cpp",
                            model = (string?)null,
                            reason = "no online node serving llama",
                        });
                    }

                    return Results.Json(new
                    {
                        ok = true,
                        inference_backend = "llama.cpp",
                        node = routed.NodeName,
                        base_url = routed.BaseUrl,
                    });
                });

            // -- GET /api/v1/chat/last_user_at --
            // Returns the most recent timestamp at which a /chat or
            // /chat/stream request arrived. Backs the MCP TimeSinceLastMessage
            // tool. null when no chat has happened since process start - the
            // tool side handles that and reports posture="unknown".
            app.MapGet($"/api/{ResourceStrings.ApiVersion}/chat/last_user_at",
                () =>
                {
                    var ts = ReadLastUserAt();
                    return Results.Json(new
                    {
                        last_user_at_unix = ts > 0 ? (long?)ts : null,
                    });
                });

            // -- POST /api/v1/chat/inspect ---------------------------------
            //
            // DIAGNOSTIC: runs the front half of the chat loop ONLY — discovers
            // tools via MCP, formats them into the system prompt block via the
            // dialect, builds the full message list — and returns the result
            // WITHOUT calling llama. Lets us answer in one curl: "what does the
            // model actually see?" Without this, debugging "model didn't reach
            // for a tool" is a guess: did tools/list fail? did injection fail?
            // did llama get the block but ignore it? This isolates the FIRST
            // two questions definitively, leaving only "model behavior" for
            // when the diagnostic shows everything plumbed correctly.
            //
            // Same request body as /chat, plus a tools_count + has_tools_block
            // for at-a-glance checks. Cheap. No model in the loop.
            app.MapPost($"/api/{ResourceStrings.ApiVersion}/chat/inspect",
                async (ChatRequest req, IHttpClientFactory httpFactory, CancellationToken ct) =>
                {
                    var mcpDiag = new McpToolClient(httpFactory);
                    var tools = await mcpDiag.ListToolsAsync(ct);
                    var toolsBlock = dialect.FormatToolsForSystemPrompt(tools);

                    var messages = BuildMessages(req);
                    if (!string.IsNullOrEmpty(toolsBlock))
                    {
                        if (messages.Count > 0 && messages[0].Role == "system")
                            messages[0] = new OpenAiMessage("system", toolsBlock + "\n\n" + messages[0].Content);
                        else
                            messages.Insert(0, new OpenAiMessage("system", toolsBlock));
                    }

                    return Results.Json(new
                    {
                        dialect = dialect.Name,
                        tools_count = tools.Count,
                        tool_names = tools.Select(t => t.Name).ToArray(),
                        has_tools_block = !string.IsNullOrEmpty(toolsBlock),
                        tools_block_chars = toolsBlock?.Length ?? 0,
                        messages = messages.Select(m => new { role = m.Role, content = m.Content }).ToArray(),
                    });
                });

            // -- POST /api/v1/chat/stream ----------------------------------
            //
            // Streaming sibling of /api/v1/chat. Same routing, same request
            // shape - the ONLY difference is the response is pushed token-by-
            // token instead of all at once.
            //
            // PROTOCOL (matches the client's reader EXACTLY - real bytes, see
            // SerenChat app.js streaming branch):
            //   The client reads NEWLINE-DELIMITED JSON (NDJSON), one object
            //   per line. NOT Server-Sent Events. Three event types:
            //     {"type":"chunk","content":"<token text>"}   - incremental
            //     {"type":"done","response":"<full text>"}     - terminal OK
            //     {"type":"error","error":"<message>"}         - terminal fail
            //   So we write compact JSON + '\n' per event and flush.
            //
            // WHY a translation pump: llama-server's OWN stream is OpenAI-style
            // SSE ("data: {delta}\n\n" lines, terminated by "data: [DONE]").
            // The client doesn't speak that - it speaks our NDJSON. So this
            // endpoint reads llama's SSE, pulls choices[0].delta.content out of
            // each frame, and re-emits it as our {type:chunk}. We accumulate
            // the full text alongside so the final {type:done} carries it (the
            // client uses that as the authoritative final, overwriting the
            // streamed-together version - lets us correct any chunk-boundary
            // weirdness in one place).
            //
            // WHY not Results.Stream / IResult: we need raw control over the
            // response body to write-and-flush per token with no buffering.
            // Writing straight to HttpContext.Response.Body is the clean way.
            app.MapPost($"/api/{ResourceStrings.ApiVersion}/chat/stream",
                async (ChatRequest req, HttpContext http, IHttpClientFactory httpFactory, CancellationToken ct) =>
                {
                    // Content-Type: application/x-ndjson is the honest label for
                    // newline-delimited JSON. The client doesn't check it (it just
                    // reads the body stream), but a curious curl / proxy should see
                    // the truth. Disable response buffering so each flush actually
                    // hits the wire immediately.
                    http.Response.ContentType = "application/x-ndjson";
                    http.Response.Headers["Cache-Control"] = "no-cache";
                    http.Response.Headers["X-Accel-Buffering"] = "no"; // defeat nginx buffering if proxied

                    // Helper: write one NDJSON event line and flush. Local function
                    // so it closes over the response stream without ceremony.
                    async Task WriteEventAsync(object evt)
                    {
                        var line = JsonSerializer.Serialize(evt, JsonWeb) + "\n";
                        var bytes = System.Text.Encoding.UTF8.GetBytes(line);
                        await http.Response.Body.WriteAsync(bytes, ct);
                        await http.Response.Body.FlushAsync(ct);
                    }

                    if (string.IsNullOrWhiteSpace(req.Prompt))
                    {
                        // Can't use BadRequest here - we've already committed to a
                        // 200 streaming response by setting ContentType. Emit an
                        // error event instead; the client renders it inline.
                        await WriteEventAsync(new { type = "error", error = "prompt is required" });
                        return;
                    }


                    // Record a real chat arrival - AFTER the empty-prompt
                    // reject so a 400 doesn't count as "the user spoke."
                    RecordChatActivity();

                    // -- 1. Where does llama live right now? --

                    var routed = await cluster.GetServiceUrlAsync(LlamaCapability, ct);
                    if (routed is null || routed.BaseUrl is null)
                    {
                        await WriteEventAsync(new { type = "error", error = "no online node is serving llama" });
                        return;
                    }

                    var messages = BuildMessages(req);

                    // -- Tool injection + pre-loop (NON-streamed) --
                    // The streaming endpoint reaches PARITY with the non-streaming
                    // one by running the tool loop FIRST, non-streamed, and only
                    // streaming the model's FINAL prose answer. Rationale: you can't
                    // know whether a turn will call a tool until you see the output,
                    // and streaming a half-answer then yanking it back to call a tool
                    // is jarring. So: resolve all tool rounds quietly (emitting
                    // tool_status events so the user sees "🔧 calling X…"), THEN
                    // stream the answer that has no more tool calls in it.
                    //
                    // This injects the SAME # Tools block as the non-streaming path -
                    // without it the model doesn't know it has tools and confabulates
                    // ("I can't access a real-time clock"), which is exactly the
                    // streaming-hallucination bug this fixes.
                    var mcp = new McpToolClient(httpFactory);
                    var tools = await mcp.ListToolsAsync(ct);
                    var toolsBlock = dialect.FormatToolsForSystemPrompt(tools);
                    if (!string.IsNullOrEmpty(toolsBlock))
                    {
                        if (messages.Count > 0 && messages[0].Role == "system")
                            messages[0] = new OpenAiMessage("system", toolsBlock + "\n\n" + messages[0].Content);
                        else
                            messages.Insert(0, new OpenAiMessage("system", toolsBlock));
                    }

                    var client = httpFactory.CreateClient("llama-upstream");
                    var url = routed.BaseUrl.TrimEnd('/') + LlamaChatPath;
                    int streamToolRounds = 0;
                    // Capture the model name as soon as we see it on the wire. The
                    // probe path sees it in the parsed completion; the SSE pump
                    // sees it in each chunk's "model" field. Either way we end up
                    // with the real upstream name instead of the "seren" placeholder.
                    string? streamFinalModel = null;

                    // Resolve tool rounds non-streamed. Each iteration: ask llama
                    // (non-stream), if it emitted tool calls, execute them, append,
                    // loop. Break when the model produces a tool-call-free answer
                    // (which we DON'T use here - we re-ask with stream:true so the
                    // final answer streams token-by-token) or we hit the cap.
                    try
                    {
                        while (streamToolRounds < MaxToolRounds)
                        {
                            var probe = BuildUpstreamRequest(req, messages, stream: false);
                            using var probeResp = await client.PostAsJsonAsync(url, probe, JsonWeb, ct);
                            if (!probeResp.IsSuccessStatusCode)
                            {
                                var body = await probeResp.Content.ReadAsStringAsync(ct);
                                await WriteEventAsync(new
                                {
                                    type = "error",
                                    error = $"llama-server returned HTTP {(int)probeResp.StatusCode}: "
                                          + (body.Length > 300 ? body[..300] + "…" : body),
                                });
                                return;
                            }

                            var probeCompletion = await probeResp.Content.ReadFromJsonAsync<OpenAiChatResponse>(JsonWeb, ct);
                            var probeText = probeCompletion?.Choices?.FirstOrDefault()?.Message?.Content ?? "";
                            if (!string.IsNullOrEmpty(probeCompletion?.Model)) streamFinalModel = probeCompletion.Model;

                            if (!dialect.ContainsToolCall(probeText))
                            {
                                // No (more) tool calls. The model's ready to give its
                                // real answer. Break and let the streaming pump below
                                // generate it fresh with stream:true.
                                break;
                            }

                            // Execute the tool calls, surfacing each as a status event
                            // so the user watches her reach for tools in real time.
                            var calls = dialect.ParseToolCalls(probeText);
                            messages.Add(new OpenAiMessage("assistant", probeText));
                            foreach (var call in calls)
                            {
                                await WriteEventAsync(new { type = "tool_status", tool = call.Name });
                                var resultJson = await mcp.CallToolAsync(call.Name, call.Arguments, ct);
                                var (role, content) = dialect.FormatToolResult(call.Name, resultJson);
                                messages.Add(new OpenAiMessage(role, content));
                            }

                            // Trim oversized tool results before the next round so a
                            // single fat tool result can't blow the model's ctx.
                            EnforceTokenBudget(messages);

                            streamToolRounds++;
                        }
                    }
                    catch (HttpRequestException ex)
                    {
                        cluster.MarkNodeOffline(routed.NodeName, $"llama-server unreachable during tool pre-loop: {ex.Message}");
                        try { await WriteEventAsync(new { type = "error", error = $"llama-server unreachable: {ex.Message}" }); }
                        catch { /* client gone */ }
                        return;
                    }

                    // Now stream the FINAL answer. messages already contains all the
                    // tool exchanges, so this generation is the model's prose reply
                    // with no more tool calls expected.
                    var upstream = BuildUpstreamRequest(req, messages, stream: true);

                    // We must use HttpCompletionOption.ResponseHeadersRead so we get
                    // the response as soon as headers arrive and can read the body
                    // INCREMENTALLY. The default (ResponseContentRead) buffers the
                    // whole body first, which would defeat streaming entirely.
                    var sb = new System.Text.StringBuilder();
                    try
                    {
                        using var upstreamReq = new HttpRequestMessage(HttpMethod.Post, url)
                        {
                            Content = JsonContent.Create(upstream, options: JsonWeb),
                        };

                        using var resp = await client.SendAsync(
                            upstreamReq, HttpCompletionOption.ResponseHeadersRead, ct);

                        if (!resp.IsSuccessStatusCode)
                        {
                            var bodyText = await resp.Content.ReadAsStringAsync(ct);
                            await WriteEventAsync(new
                            {
                                type = "error",
                                error = $"llama-server returned HTTP {(int)resp.StatusCode}: "
                                      + (bodyText.Length > 300 ? bodyText[..300] + "…" : bodyText),
                            });
                            return;
                        }

                        using var stream = await resp.Content.ReadAsStreamAsync(ct);
                        using var reader = new StreamReader(stream);

                        // llama-server emits OpenAI SSE: lines like
                        //   data: {"choices":[{"delta":{"content":"Hel"}}]}
                        //   data: {"choices":[{"delta":{"content":"lo"}}]}
                        //   data: [DONE]
                        // Blank lines separate events. We read line-by-line, strip
                        // the "data: " prefix, parse the delta, emit a chunk.
                        string? line;
                        while ((line = await reader.ReadLineAsync(ct)) is not null)
                        {
                            if (line.Length == 0) continue;
                            if (!line.StartsWith("data:", StringComparison.Ordinal)) continue;

                            var payload = line["data:".Length..].Trim();
                            if (payload == "[DONE]") break;

                            // Parse just enough to pull the delta content. A malformed
                            // frame (rare) is skipped rather than killing the stream.
                            // We also grab the "model" field once (every chunk repeats
                            // it; first one is enough) so the done event reports the
                            // REAL upstream model name, not the "seren" placeholder.
                            string? delta = null;
                            try
                            {
                                using var doc = JsonDocument.Parse(payload);
                                if (streamFinalModel is null
                                    && doc.RootElement.TryGetProperty("model", out var modelEl)
                                    && modelEl.ValueKind == JsonValueKind.String)
                                {
                                    streamFinalModel = modelEl.GetString();
                                }
                                if (doc.RootElement.TryGetProperty("choices", out var choices)
                                    && choices.ValueKind == JsonValueKind.Array
                                    && choices.GetArrayLength() > 0)
                                {
                                    var first = choices[0];
                                    if (first.TryGetProperty("delta", out var deltaEl)
                                        && deltaEl.TryGetProperty("content", out var contentEl)
                                        && contentEl.ValueKind == JsonValueKind.String)
                                    {
                                        delta = contentEl.GetString();
                                    }
                                }
                            }
                            catch (JsonException)
                            {
                                continue;
                            }

                            if (!string.IsNullOrEmpty(delta))
                            {
                                sb.Append(delta);
                                await WriteEventAsync(new { type = "chunk", content = delta });
                            }
                        }

                        // Terminal: send the accumulated full text as the
                        // authoritative final, with model + the REAL tool_rounds
                        // resolved by the pre-loop above. Now at full parity with
                        // the non-streaming path: tools fire, tool_rounds is honest,
                        // model name is carried.
                        await WriteEventAsync(new
                        {
                            type = "done",
                            response = sb.ToString(),
                            // Real upstream model name when we got it (probe or SSE
                            // chunk); fall back to override or "seren" placeholder.
                            model = streamFinalModel
                                    ?? (string.IsNullOrWhiteSpace(req.ModelOverride) ? "seren" : req.ModelOverride),
                            tool_rounds = streamToolRounds,
                        });
                    }
                    catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                    {
                        cluster.MarkNodeOffline(routed.NodeName, "llama-server stream timed out");
                        // Best-effort error event. If the socket's already gone this
                        // throws and the outer catch swallows it - nothing to do.
                        try { await WriteEventAsync(new { type = "error", error = "llama-server timed out" }); }
                        catch { /* client already disconnected */ }
                    }
                    catch (OperationCanceledException)
                    {
                        // ct itself was cancelled = the CLIENT disconnected (closed
                        // the tab / hit stop). Not an error, not the node's fault.
                        // Don't mark offline, don't try to write (socket's gone).
                    }
                    catch (HttpRequestException ex)
                    {
                        cluster.MarkNodeOffline(routed.NodeName, $"llama-server unreachable: {ex.Message}");
                        try { await WriteEventAsync(new { type = "error", error = $"llama-server unreachable: {ex.Message}" }); }
                        catch { /* client already disconnected */ }
                    }
                });
        }

        // -------------------------------------------------------------
        //  Shared request-building helpers (used by both /chat and
        //  /chat/stream so the two paths can't drift)
        // -------------------------------------------------------------

        /// <summary>
        /// Assemble the OpenAI-style message list from a client request:
        /// optional system prompt, then history turns (malformed ones skipped),
        /// then the new user turn. llama-server applies the model's baked-in
        /// chat template to this list.
        /// </summary>
        private static List<OpenAiMessage> BuildMessages(ChatRequest req)
        {
            var messages = new List<OpenAiMessage>();
            if (!string.IsNullOrWhiteSpace(req.SystemPrompt))
            {
                messages.Add(new OpenAiMessage("system", req.SystemPrompt));
            }
            if (req.History is { Count: > 0 })
            {
                foreach (var m in req.History)
                {
                    // Defensive: skip malformed history entries rather than
                    // letting one bad turn 500 the whole request. The client
                    // builds this from localStorage, which can carry cruft.
                    if (string.IsNullOrWhiteSpace(m.Role) || m.Content is null) continue;
                    messages.Add(new OpenAiMessage(m.Role, m.Content));
                }
            }
            messages.Add(new OpenAiMessage("user", req.Prompt));
            return messages;
        }

        /// <summary>
        /// Build the upstream llama-server request body. <paramref name="stream"/>
        /// is the only thing that differs between the two endpoints.
        /// </summary>
        private static OpenAiChatRequest BuildUpstreamRequest(
            ChatRequest req, List<OpenAiMessage> messages, bool stream)
        {
            return new OpenAiChatRequest
            {
                // model: llama-server ignores this for single-model loads (it
                // serves whatever GGUF it was launched with), but the OpenAI
                // dialect wants the field present. Pass the override through if
                // the client sent one, else a sentinel.
                Model = string.IsNullOrWhiteSpace(req.ModelOverride) ? "seren" : req.ModelOverride,
                Messages = messages,
                MaxTokens = req.MaxTokens.HasValue ? (req.MaxTokens > 0 ? req.MaxTokens.Value : DefaultMaxTokens) : DefaultMaxTokens,
                Temperature = req.Temperature,
                // llama.cpp accepts repeat_penalty in the OpenAI body as an
                // extension. Null is omitted by the serializer (see options).
                RepeatPenalty = req.RepeatPenalty,
                Stream = stream,
                // Stop at the tool-call close tag so the model halts cleanly
                // after emitting a call instead of riffing past it. See the
                // Stop property's comment for the full reasoning.
                Stop = new List<string> { "</tool_call>" },
            };
        }

        // -- Context budget management --
        //
        // SEREN_CTX_BUDGET is the soft cap (in approximate tokens) for the
        // total message-list size we send to llama. Default 6000 - safe for
        // an 8K-ctx Nano with ~2K headroom for the model's response.
        // Override via env (`SEREN_CTX_BUDGET=12000 dotnet run` etc.) on
        // beefier setups where you want larger working context.
        //
        // WHY 3 chars/token: real ratio is 3-4 for English prose, ~2-3 for
        // code/HTML/JSON. We err safe (overestimate token count) so we trim
        // *before* hitting the actual ctx limit rather than after.
        private static readonly int CtxBudgetTokens =
            int.TryParse(Environment.GetEnvironmentVariable("SEREN_CTX_BUDGET"), out var b) && b > 0
                ? b : 6000;
        private const int CharsPerTokenEstimate = 3;
        private const string ToolResponseOpen = "<tool_response>";
        private const string ToolResponseClose = "</tool_response>";

        /// <summary>
        /// Trim the message list to fit within the token budget. Strategy:
        /// walk OLDEST → NEWEST, finding tool_response messages (the most
        /// trimmable thing - they're verbose, and only the most recent ones
        /// usually matter for the current reasoning). Replace their content
        /// with a short "[truncated, original was N tokens]" marker until the
        /// list fits. We never trim user messages, the system prompt, or
        /// assistant turns - those are load-bearing for conversational coherence.
        ///
        /// WHY this exists: a single big tool result (fetch_url on a fat
        /// webpage, get_recent_logs with lines=200) can blow the ctx window
        /// in ONE call. Per-tool caps help, but the loop needs to be the
        /// final guardrail so every future tool is automatically safe -
        /// "make Seren work on a $250 Nano" is the design north star, and
        /// that means no tool can structurally exceed available context.
        /// </summary>
        private static void EnforceTokenBudget(List<OpenAiMessage> messages)
        {
            int EstimateTokens() =>
                messages.Sum(m => (m.Content?.Length ?? 0) / CharsPerTokenEstimate);

            if (EstimateTokens() <= CtxBudgetTokens) return;

            // Pass 1: truncate oversized tool_response contents to a compact
            // marker. We do this OLDEST → NEWEST so the most recent tool
            // results (most relevant to current reasoning) survive longest.
            for (int i = 0; i < messages.Count && EstimateTokens() > CtxBudgetTokens; i++)
            {
                var m = messages[i];
                if (m.Role != "user") continue;
                if (!m.Content.Contains(ToolResponseOpen, StringComparison.Ordinal)) continue;

                var originalTokens = (m.Content.Length) / CharsPerTokenEstimate;
                // Replace the inner content of the tool_response wrapper with a
                // marker the model can read - it learns "the result existed but
                // was trimmed for space" rather than seeing nothing.
                var marker = $"{ToolResponseOpen}\n[truncated to fit context, original was ~{originalTokens} tokens]\n{ToolResponseClose}";
                messages[i] = new OpenAiMessage(m.Role, marker);
            }
            // If we're STILL over budget after truncating all tool_responses,
            // we've done what we can without nuking conversational history.
            // The next round may overflow llama - the existing 502 error path
            // will surface it honestly, which is better than truncating user
            // turns silently.
        }

        // -------------------------------------------------------------
        //  Client-facing contract (request in, response out)
        // -------------------------------------------------------------

        /// <summary>
        /// Inbound chat request from the client. Field names are camelCase
        /// to match the JS client's JSON.stringify output (the Web defaults
        /// on the deserializer handle the casing).
        /// </summary>
        private sealed record ChatRequest(
            string Prompt,
            List<ChatHistoryEntry>? History,
            string? SystemPrompt,
            string? ModelOverride,
            double? Temperature,
            double? RepeatPenalty,
            int? MaxTokens);

        private sealed record ChatHistoryEntry(string Role, string Content);

        /// <summary>
        /// Outbound response to the client. snake_case via JsonPropertyName
        /// to match the rest of the Seren API surface (the client reads
        /// json.response / json.model / json.toolRounds - note the client
        /// currently expects camelCase 'toolRounds'; we serialize 'tool_rounds'
        /// and the client edit aligns to it. See the client patch.)
        /// </summary>
        private sealed record ChatResponse(
            [property: JsonPropertyName("response")] string Response,
            [property: JsonPropertyName("model")] string Model,
            [property: JsonPropertyName("node")] string Node,
            [property: JsonPropertyName("tool_rounds")] int ToolRounds,
            [property: JsonPropertyName("usage")] OpenAiUsage? Usage);

        // -------------------------------------------------------------
        //  Upstream contract (llama-server OpenAI dialect)
        //
        //  Minimal DTOs - only the fields we send/read. llama-server returns
        //  more (timings, etc.) but we don't need them at this tier.
        // -------------------------------------------------------------

        private sealed class OpenAiChatRequest
        {
            [JsonPropertyName("model")] public string Model { get; set; } = "seren";
            [JsonPropertyName("messages")] public List<OpenAiMessage> Messages { get; set; } = [];
            [JsonPropertyName("max_tokens")] public int MaxTokens { get; set; }
            // Stop sequences. Including "</tool_call>" makes well-trained models
            // emit a clean matched pair AND tells less-trained ones to halt right
            // after the close tag instead of generating prose after the call
            // (which then would never get processed - the loop wants to see the
            // call FIRST). The lenient parser handles a missing close too, so
            // this is a "make the model behave well" knob, not a correctness
            // requirement. Omitted from wire when null/empty.
            [JsonPropertyName("stop")]
            [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
            public List<string>? Stop { get; set; }
            // Nullable + the Web serializer's default-ignore means these are
            // omitted from the wire when the client didn't set them, letting
            // llama-server fall back to its own defaults.
            [JsonPropertyName("temperature")]
            [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
            public double? Temperature { get; set; }
            [JsonPropertyName("repeat_penalty")]
            [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
            public double? RepeatPenalty { get; set; }
            [JsonPropertyName("stream")] public bool Stream { get; set; }
        }

        private sealed record OpenAiMessage(
            [property: JsonPropertyName("role")] string Role,
            [property: JsonPropertyName("content")] string Content);

        private sealed record OpenAiChatResponse(
            [property: JsonPropertyName("model")] string? Model,
            [property: JsonPropertyName("choices")] List<OpenAiChoice>? Choices,
            [property: JsonPropertyName("usage")] OpenAiUsage? Usage);

        private sealed record OpenAiChoice(
            [property: JsonPropertyName("index")] int Index,
            [property: JsonPropertyName("message")] OpenAiMessage? Message,
            [property: JsonPropertyName("finish_reason")] string? FinishReason);

        private sealed record OpenAiUsage(
            [property: JsonPropertyName("prompt_tokens")] int? PromptTokens,
            [property: JsonPropertyName("completion_tokens")] int? CompletionTokens,
            [property: JsonPropertyName("total_tokens")] int? TotalTokens);
    }
}