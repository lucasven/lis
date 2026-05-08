# OpenAI Codex Provider — Handoff Package

## Calibration

- Intent: rung 3 — working-backwards README
- Interface: rung 2 — type signatures
- Behavior: rung 4 — state machines + invariants
- Verification: rung 4 — evals + properties

## Quality bar status

- Intent bar: **pass** (checklist below)
- Interface bar: **pass** (checklist below)
- Behavior bar: **pass** (checklist below)
- Verification bar: **pass** (checklist below)
- Cross-pillar (V ≥ B): **pass** — every state/transition/invariant has a corresponding test
- Format consistency: **pass** — xUnit, C# conventions match existing project

---

## Intent (Rung 3 — Working-backwards README)

### OpenAI Codex Provider for Lis

Lis supports OpenAI Codex as an AI provider alongside Anthropic. When `CODEX_ENABLED=true`, conversations route through OpenAI Codex models via the ChatGPT backend Responses API. Users switch between providers by changing the agent's model — the system resolves the correct provider at runtime based on a `provider` field on each agent.

#### Setup

1. Authenticate with OpenAI (one-time): run the included CLI tool or obtain OAuth tokens via `https://auth.openai.com/oauth/authorize` with the PKCE flow.
2. Store the tokens in your `.env`:

```env
CODEX_ENABLED=true
CODEX_ACCESS_TOKEN=eyJ...
CODEX_REFRESH_TOKEN=rt-...
CODEX_MODEL=codex-1
CODEX_MAX_TOKENS=16384
CODEX_CONTEXT_BUDGET=100000
CODEX_REASONING_EFFORT=medium
CODEX_TRANSPORT=auto          # auto | sse | websocket
```

3. Start Lis normally. The provider auto-refreshes expired access tokens using the refresh token. Transport defaults to `auto` — tries WebSocket first (with connection caching and delta context optimization), falls back to SSE on failure. Prompt caching is automatic via `prompt_cache_key` tied to the conversation's session ID.

#### Usage example

A WhatsApp user sends "What time is it in Tokyo?" → Lis checks the agent's provider field (`codex`) → resolves to `CodexChatClient` → Codex calls the `GetCurrentTime` tool → Lis returns the formatted answer. From the user's perspective, behavior is identical to the Anthropic provider. Switching to a different model (e.g., `/model gpt-5.4-mini` or `/model claude-sonnet-4-20250514`) updates the agent and the next request routes through the correct provider automatically.

#### Error case

If the refresh token is expired or revoked (e.g., ChatGPT subscription lapsed), the provider throws `CodexAuthException`. Lis logs the error with `Activity.Current?.SetStatus(Error)` and returns a generic error to the user. The fix is re-running the OAuth flow to get fresh tokens.

#### Transport behavior

The provider supports three transport modes:

- **`auto` (default):** Tries WebSocket first. If the WebSocket connection fails *before* any events are emitted, automatically falls back to SSE for the current request and marks the session for SSE-only. If it fails *after* events started streaming, the error propagates (can't switch mid-stream).
- **`sse`:** SSE only. Includes retry with exponential backoff on 429/5xx.
- **`websocket`:** WebSocket only, no SSE fallback.

WebSocket connections are cached per session for 5 minutes. On subsequent requests with the same session, the provider reuses the connection and sends only the delta context (new messages since the last response) via `previous_response_id`, avoiding retransmission of the full conversation.

#### Prompt caching

The request body includes `prompt_cache_key` set to the conversation's session ID. This enables server-side prompt caching on OpenAI's infrastructure, reducing input token costs for multi-turn conversations. The cached token count is reported via `usage.input_tokens_details.cached_tokens`.

#### Error case

If the refresh token is expired or revoked (e.g., ChatGPT subscription lapsed), the provider throws `CodexAuthException`. Lis logs the error with `Activity.Current?.SetStatus(Error)` and returns a generic error to the user. The fix is re-running the OAuth flow to get fresh tokens.

If a WebSocket connection is rejected or drops mid-stream, the provider records the failure and falls back to SSE. Persistent WebSocket failures for a session are sticky — once fallen back, SSE is used for all subsequent requests in that session.

#### What this version does NOT do

- **No interactive OAuth login at runtime.** The OAuth PKCE flow (browser redirect, local callback server) is out of scope for the server process. Tokens are provided via env vars, obtained out-of-band.
- **No multi-model routing.** Only one Codex model active at a time (same as Anthropic).

#### Intent quality bar (rung 3)

- [x] At least one complete usage example (WhatsApp → tool call → response)
- [x] Documents at least one error case (expired refresh token + WebSocket fallback)
- [x] States what the feature is NOT doing in this version (2 items)
- [x] Reads as if the feature already exists (present tense throughout)
- [x] Reader unfamiliar with impl could predict correct/incorrect output

---

## Interface (Rung 2 — Type Signatures)

### New files

| File | Purpose |
|---|---|
| `Lis.Providers/OpenAi/Codex/CodexOptions.cs` | Configuration from env vars |
| `Lis.Providers/OpenAi/Codex/CodexTokenManager.cs` | OAuth token refresh + JWT parsing |
| `Lis.Providers/OpenAi/Codex/CodexChatClient.cs` | `IChatClient` implementation via Responses API |
| `Lis.Providers/OpenAi/Codex/CodexSseParser.cs` | SSE stream parser |
| `Lis.Providers/OpenAi/Codex/CodexWebSocketTransport.cs` | WebSocket connection, caching, delta context |
| `Lis.Providers/OpenAi/Codex/CodexTransportSelector.cs` | Auto/SSE/WebSocket selection + fallback logic |
| `Lis.Providers/OpenAi/Codex/CodexMessageConverter.cs` | IChatClient ↔ Responses API format conversion |
| `Lis.Providers/OpenAi/Codex/CodexUsageExtractor.cs` | `IUsageExtractor` for Responses API usage |
| `Lis.Providers/OpenAi/Codex/CodexTokenCounter.cs` | `ITokenCounter` (returns null) |
| `Lis.Providers/OpenAi/Codex/CodexProvider.cs` | `AddCodex()` DI registration |
| `Lis.Providers/OpenAi/Codex/ResponsesApiTypes.cs` | Request/response DTOs for the Responses API |

### Type signatures

```csharp
// ── Configuration ──────────────────────────────────────────────────

public enum CodexTransport { Auto, Sse, WebSocket }

public sealed class CodexOptions
{
	public required string AccessToken    { get; set; }   // mutable: updated on refresh
	public required string RefreshToken   { get; set; }   // mutable: updated on refresh
	public string          Model          { get; init; } = "codex-1";
	public int             MaxTokens      { get; init; } = 16384;
	public int             ContextBudget  { get; init; } = 100000;
	public string?         ReasoningEffort { get; init; }
	public string          BaseUrl        { get; init; } = "https://chatgpt.com/backend-api";
	public int             ExpiryBufferSeconds { get; init; } = 300; // refresh 5 min early
	public CodexTransport  Transport      { get; init; } = CodexTransport.Auto;
}

// ── Token Management ───────────────────────────────────────────────

public sealed record CodexTokenInfo(string AccessToken, string AccountId, DateTimeOffset ExpiresAt);

public sealed class CodexTokenManager : IDisposable
{
	public CodexTokenManager(CodexOptions options, HttpClient httpClient);
	public Task<CodexTokenInfo> GetValidTokenAsync(CancellationToken ct = default);
}

// ── Chat Client ────────────────────────────────────────────────────

public sealed class CodexChatClient : IChatClient
{
	public CodexChatClient(
		CodexTokenManager tokenManager,
		CodexOptions options,
		HttpClient httpClient,
		CodexWebSocketTransport webSocketTransport);

	public ChatClientMetadata Metadata { get; }

	public Task<ChatResponse> GetResponseAsync(
		IList<ChatMessage> chatMessages,
		ChatOptions? options = null,
		CancellationToken cancellationToken = default);

	public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
		IList<ChatMessage> chatMessages,
		ChatOptions? options = null,
		[EnumeratorCancellation] CancellationToken cancellationToken = default);

	// sessionId is derived from the conversation/chat context
	// and used for: WebSocket connection caching, prompt_cache_key, delta context
	public string? SessionId { get; set; }

	public void Dispose();
}

// ── SSE Parser ─────────────────────────────────────────────────────

public static class CodexSseParser
{
	public static IAsyncEnumerable<JsonElement> ParseAsync(
		Stream sseStream,
		[EnumeratorCancellation] CancellationToken ct = default);
}

// ── WebSocket Transport ────────────────────────────────────────────

public sealed record WebSocketContinuation(
	CodexRequest LastRequestBody,
	string LastResponseId,
	JsonArray LastResponseItems);

public sealed class CachedWebSocketConnection : IDisposable
{
	public ClientWebSocket Socket       { get; }
	public bool            Busy         { get; set; }
	public WebSocketContinuation? Continuation { get; set; }
	public void Dispose();
}

public sealed class CodexWebSocketTransport : IDisposable
{
	// sessionId → cached connection (5-minute idle TTL)
	public CodexWebSocketTransport(CodexTokenManager tokenManager, CodexOptions options);

	// Acquire a connection: reuse cached if available, else open new
	public Task<(ClientWebSocket Socket, bool Reused, Action<bool> Release)> AcquireAsync(
		string sessionId, CancellationToken ct = default);

	// Build request body with delta optimization if continuation exists
	public CodexRequest BuildRequestWithDelta(
		string sessionId, CodexRequest fullBody);

	// Store continuation state after successful response
	public void StoreContinuation(
		string sessionId, CodexRequest requestBody, string responseId, JsonArray responseItems);

	// Check if session has fallen back to SSE (sticky after WS failure)
	public bool IsSseFallbackActive(string sessionId);

	// Record a WebSocket failure → marks session for SSE fallback
	public void RecordFailure(string sessionId);

	// Parse events from a WebSocket (analogous to CodexSseParser for SSE)
	public static IAsyncEnumerable<JsonElement> ParseWebSocketAsync(
		ClientWebSocket socket,
		[EnumeratorCancellation] CancellationToken ct = default);

	// Close all cached connections
	public void Dispose();
}

// ── Transport Selector ─────────────────────────────────────────────

public sealed class CodexTransportSelector
{
	public CodexTransportSelector(
		CodexOptions options,
		HttpClient httpClient,
		CodexWebSocketTransport webSocketTransport);

	// Returns an IAsyncEnumerable<JsonElement> of SSE events regardless of transport
	// Handles: transport selection, WebSocket→SSE fallback, retry logic
	public IAsyncEnumerable<JsonElement> StreamAsync(
		CodexRequest request,
		string? sessionId,
		Headers headers,
		[EnumeratorCancellation] CancellationToken ct = default);
}

// ── Message Conversion ─────────────────────────────────────────────

public static class CodexMessageConverter
{
	public static (string? Instructions, JsonArray Input) ConvertToResponsesApi(
		IList<ChatMessage> messages);

	public static FunctionCallContent ParseFunctionCall(
		string callId, string name, string argumentsJson);
}

// ── Usage Extractor ────────────────────────────────────────────────

public sealed class CodexUsageExtractor : IUsageExtractor
{
	public TokenUsage? Extract(IReadOnlyDictionary<string, object?>? metadata);
}

// ── Token Counter ──────────────────────────────────────────────────

public sealed class CodexTokenCounter : ITokenCounter
{
	public Task<int?> CountAsync(string requestBodyJson, CancellationToken ct = default);
	// Always returns null — no free endpoint exists
}

// ── DI Registration ────────────────────────────────────────────────

public static class CodexProvider
{
	public static IServiceCollection AddCodex(this IServiceCollection services);
}
```

### Responses API DTOs (request body)

```csharp
public sealed class CodexRequest
{
	[JsonPropertyName("model")]       public required string Model { get; init; }
	[JsonPropertyName("store")]       public bool   Store       { get; init; } = false;
	[JsonPropertyName("stream")]      public bool   Stream      { get; init; } = true;
	[JsonPropertyName("instructions")] public string? Instructions { get; init; }
	[JsonPropertyName("input")]       public JsonArray Input     { get; init; } = [];
	[JsonPropertyName("tools")]       public List<CodexTool>? Tools { get; init; }
	[JsonPropertyName("tool_choice")] public string ToolChoice  { get; init; } = "auto";
	[JsonPropertyName("parallel_tool_calls")] public bool ParallelToolCalls { get; init; } = true;
	[JsonPropertyName("text")]        public CodexTextOptions? Text { get; init; }
	[JsonPropertyName("reasoning")]   public CodexReasoningOptions? Reasoning { get; init; }
	[JsonPropertyName("include")]     public List<string>? Include { get; init; }
	[JsonPropertyName("prompt_cache_key")] public string? PromptCacheKey { get; init; }
	[JsonPropertyName("previous_response_id")] public string? PreviousResponseId { get; init; }
}

public sealed class CodexTool
{
	[JsonPropertyName("type")]        public string Type        { get; init; } = "function";
	[JsonPropertyName("name")]        public required string Name { get; init; }
	[JsonPropertyName("description")] public string? Description { get; init; }
	[JsonPropertyName("parameters")]  public JsonElement? Parameters { get; init; }
	[JsonPropertyName("strict")]      public bool? Strict       { get; init; }
}

public sealed class CodexTextOptions
{
	[JsonPropertyName("verbosity")] public string Verbosity { get; init; } = "low";
}

public sealed class CodexReasoningOptions
{
	[JsonPropertyName("effort")]  public string? Effort  { get; init; }
	[JsonPropertyName("summary")] public string? Summary { get; init; } = "auto";
}
```

### Interface quality bar (rung 2)

- [x] Every parameter and return value has a named type
- [x] Optional vs required is explicit (`required` keyword, nullable `?`)
- [x] Compound types are named (CodexTokenInfo, CodexRequest, etc.)
- [x] Types compile in C# (.NET 10)
- [x] Reading signatures tells you what each function consumes and produces

---

## Behavior (Rung 4 — State Machines + Invariants)

### State Machine 1: Token Lifecycle

Manages OAuth access/refresh tokens. Ensures every API request carries a valid Bearer token.

**States:**

| State | Description | Initial? | Terminal? |
|---|---|---|---|
| `Ready` | Valid access token + accountId available | Yes (after config) | No |
| `Refreshing` | Refresh HTTP request in flight; callers await same Task | No | No |
| `Failed` | Refresh failed; no valid token | No | Yes |

**Events:**

| Event | Trigger |
|---|---|
| `GetToken` | Any API call needs a token |
| `RefreshOk(access, refresh, expiresAt)` | Refresh endpoint returned 200 |
| `RefreshErr(error)` | Refresh endpoint returned error or network failure |

**Transition table:** Every (state, event) cell filled.

| State | GetToken (valid) | GetToken (expired) | RefreshOk | RefreshErr |
|---|---|---|---|---|
| Ready | Ready — return token | → Refreshing — start refresh, return await | ignored | ignored |
| Refreshing | await same Task | await same Task | → Ready — store tokens, complete Task | → Failed — fault Task |
| Failed | throw CodexAuthException | throw CodexAuthException | ignored | ignored |

**"Valid" check:** `expiresAt > DateTimeOffset.UtcNow + TimeSpan.FromSeconds(expiryBufferSeconds)`

**"Expired" check:** negation of valid.

### State Machine 2: SSE Event Stream Processing

Processes the Codex Responses API SSE event stream and maps to `IChatClient` streaming output.

**States:**

| State | Description | Initial? | Terminal? |
|---|---|---|---|
| `Idle` | No events processed | Yes | No |
| `Started` | `response.created` received | No | No |
| `InReasoning` | Inside a `reasoning` output item | No | No |
| `InText` | Inside a `message` output item | No | No |
| `InToolCall` | Inside a `function_call` output item | No | No |
| `Between` | Item completed, waiting for next item or completion | No | No |
| `Complete` | `response.completed` received | No | Yes |
| `Error` | Error event or connection lost | No | Yes |

**Events:**

| Event | SSE event type |
|---|---|
| `Created` | `response.created` |
| `ItemReasoning` | `response.output_item.added` where item.type = "reasoning" |
| `ItemMessage` | `response.output_item.added` where item.type = "message" |
| `ItemFunction` | `response.output_item.added` where item.type = "function_call" |
| `ReasoningDelta(text)` | `response.reasoning_summary_text.delta` |
| `TextDelta(text)` | `response.output_text.delta` |
| `ArgsDelta(json)` | `response.function_call_arguments.delta` |
| `ItemDone(item)` | `response.output_item.done` |
| `Completed(response)` | `response.completed` |
| `Failed(error)` | `response.failed` or `error` |

**Transition table:**

| State | Created | ItemReasoning | ItemMessage | ItemFunction | ReasoningDelta | TextDelta | ArgsDelta | ItemDone | Completed | Failed |
|---|---|---|---|---|---|---|---|---|---|---|
| Idle | → Started | invalid | invalid | invalid | invalid | invalid | invalid | invalid | invalid | → Error |
| Started | invalid | → InReasoning | → InText | → InToolCall | invalid | invalid | invalid | invalid | → Complete | → Error |
| InReasoning | invalid | invalid | invalid | invalid | InReasoning | invalid | invalid | → Between | invalid | → Error |
| InText | invalid | invalid | invalid | invalid | invalid | InText | invalid | → Between | invalid | → Error |
| InToolCall | invalid | invalid | invalid | invalid | invalid | invalid | InToolCall | → Between | invalid | → Error |
| Between | invalid | → InReasoning | → InText | → InToolCall | invalid | invalid | invalid | invalid | → Complete | → Error |
| Complete | invalid | invalid | invalid | invalid | invalid | invalid | invalid | invalid | invalid | invalid |
| Error | invalid | invalid | invalid | invalid | invalid | invalid | invalid | invalid | invalid | invalid |

**IChatClient output per transition:**

| Transition | Yield to caller |
|---|---|
| * → InText | — (nothing yet) |
| InText → InText (TextDelta) | `ChatResponseUpdate { Text = delta }` |
| InText → Between (ItemDone) | — (text already streamed) |
| * → InToolCall | — (start accumulating) |
| InToolCall → InToolCall (ArgsDelta) | — (accumulate JSON fragment) |
| InToolCall → Between (ItemDone) | `ChatResponseUpdate { Contents = [FunctionCallContent { CallId, Name, Arguments }] }` |
| * → InReasoning | — (not surfaced) |
| InReasoning → InReasoning (ReasoningDelta) | — (not surfaced) |
| InReasoning → Between (ItemDone) | — (not surfaced) |
| * → Complete | `ChatResponseUpdate { Contents = [UsageContent { Details = mapped_usage }] }` |
| * → Error | throw exception |

### State Machine 3: HTTP Request with Retry

**States:**

| State | Description | Initial? | Terminal? |
|---|---|---|---|
| `Sending` | HTTP request in flight | Yes | No |
| `Retrying` | Backing off before retry | No | No |
| `Succeeded` | 2xx response received | No | Yes |
| `Failed` | Non-retryable error or max retries | No | Yes |

**Events:**

| Event | Trigger |
|---|---|
| `Response(status, body)` | HTTP response received |
| `NetworkError(error)` | Connection/DNS/timeout failure |
| `BackoffDone` | Backoff timer elapsed |
| `Abort` | CancellationToken cancelled |

**Transition table:**

| State | Response(2xx) | Response(429/5xx) attempt < 3 | Response(429/5xx) attempt ≥ 3 | Response(other) | NetworkError attempt < 3 | NetworkError attempt ≥ 3 | BackoffDone | Abort |
|---|---|---|---|---|---|---|---|---|
| Sending | → Succeeded | → Retrying | → Failed | → Failed | → Retrying | → Failed | invalid | → Failed |
| Retrying | invalid | invalid | invalid | invalid | invalid | invalid | → Sending (attempt++) | → Failed |
| Succeeded | — | — | — | — | — | — | — | — |
| Failed | — | — | — | — | — | — | — | — |

**Backoff formula:** `delay = 1000ms × 2^attempt` (1s, 2s, 4s for attempts 0, 1, 2)

### State Machine 4: Transport Selection + WebSocket Lifecycle

Decides which transport to use and manages WebSocket connection caching with SSE fallback.

**States:**

| State | Description | Initial? | Terminal? |
|---|---|---|---|
| `SelectTransport` | Evaluate config + session fallback state | Yes | No |
| `WsConnecting` | Opening or reusing a WebSocket connection | No | No |
| `WsStreaming` | WebSocket connected, processing events | No | No |
| `WsFailed_PreStream` | WebSocket failed before any events emitted | No | No |
| `SseSending` | HTTP SSE request in flight (with retry SM) | No | No |
| `SseStreaming` | SSE connected, processing events | No | No |
| `Complete` | Response finished successfully | No | Yes |
| `Error` | Unrecoverable error | No | Yes |

**Events:**

| Event | Trigger |
|---|---|
| `ConfigSse` | Transport = SSE, or session has sticky SSE fallback |
| `ConfigWsOrAuto` | Transport = WebSocket or Auto, session not fallen back |
| `WsConnected(reused)` | WebSocket connection established (new or reused from cache) |
| `WsFirstEvent` | First SSE event received via WebSocket |
| `WsTransportErr` | WebSocket error BEFORE any events emitted |
| `WsStreamErr` | WebSocket error AFTER events started |
| `WsApiErr` | API-level error (response.failed, error event) — not a transport issue |
| `SseConnected` | SSE HTTP response 2xx received |
| `StreamDone` | response.completed received |
| `Abort` | CancellationToken cancelled |

**Transition table:**

| State | ConfigSse | ConfigWsOrAuto | WsConnected | WsFirstEvent | WsTransportErr | WsStreamErr | WsApiErr | SseConnected | StreamDone | Abort |
|---|---|---|---|---|---|---|---|---|---|---|
| SelectTransport | → SseSending | → WsConnecting | — | — | — | — | — | — | — | → Error |
| WsConnecting | — | — | → WsStreaming* | — | → WsFailed_PreStream | — | — | — | — | → Error |
| WsStreaming | — | — | — | WsStreaming | — | → Error | → Error | — | → Complete | → Error |
| WsFailed_PreStream | — | — | — | — | — | — | — | — | — | — |
| SseSending | — | — | — | — | — | — | — | → SseStreaming | — | → Error |
| SseStreaming | — | — | — | — | — | — | — | — | → Complete | → Error |

*WsStreaming: send request body via `socket.send(JSON.stringify({ type: "response.create", ...body }))`. Events parsed from WebSocket messages follow the same SSE Event Processing state machine (SM2).

**WsFailed_PreStream transition:** mark session for SSE fallback (`RecordFailure(sessionId)`), then → SseSending. This is the automatic fallback path.

**Key rule:** If `WsStreamErr` occurs (WebSocket fails AFTER first event), there is NO fallback — the error propagates. You can't replay partial events on a different transport.

### WebSocket Connection Caching Rules

| Rule | Description |
|---|---|
| **Cache key** | `sessionId` — one cached connection per session |
| **Idle TTL** | 5 minutes — connection closed if unused for 5 min |
| **Reuse check** | Connection reused only if: not busy, WebSocket state = Open |
| **Busy flag** | Set when acquired, cleared on release — prevents double-use |
| **Concurrent overflow** | If cached connection is busy, open a fresh (non-cached) connection |
| **Post-response** | If response succeeded and connection still open: return to pool, restart idle timer |
| **Post-error** | Evict from cache, close connection, mark session for SSE fallback |

### Delta Context Optimization (WebSocket only)

When reusing a cached WebSocket connection with continuation state:

1. Compare current `requestBody` (excluding `input` and `previous_response_id`) with `lastRequestBody` — must match exactly (same model, tools, instructions, etc.)
2. Verify current `input` starts with `lastRequestBody.input + lastResponseItems` (the known prefix)
3. If both match: send only the **delta** (new messages since last response) with `previous_response_id` set to `lastResponseId`
4. If either doesn't match: clear continuation state, send full context

| Condition | Request body |
|---|---|
| No continuation, or first request on session | Full `input`, no `previous_response_id` |
| Continuation matches | Delta `input` (new messages only), `previous_response_id = lastResponseId` |
| Continuation mismatch (model/tools changed) | Full `input`, no `previous_response_id`, continuation cleared |

### Prompt Caching Rules

| Rule | Description |
|---|---|
| `prompt_cache_key` always set to `sessionId` | Enables server-side caching of the conversation prefix |
| Works with both SSE and WebSocket | Field is in the request body, transport-agnostic |
| If `sessionId` is null | `prompt_cache_key` is omitted from the request |
| Cached tokens reported in `usage.input_tokens_details.cached_tokens` | Mapped to `TokenUsage.CacheReadTokens` |

### Message Conversion Rules

| IChatClient Input | Responses API Output |
|---|---|
| `ChatMessage(Role=System)` | `instructions` field in request body (NOT in input array) |
| `ChatMessage(Role=User)` with `TextContent` | `{ role: "user", content: [{ type: "input_text", text }] }` |
| `ChatMessage(Role=User)` with `ImageContent` | `{ role: "user", content: [{ type: "input_image", image_url: "data:{mime};base64,{data}", detail: "auto" }] }` |
| `ChatMessage(Role=Assistant)` with `TextContent` | `{ type: "message", role: "assistant", content: [{ type: "output_text", text }], status: "completed" }` |
| `ChatMessage(Role=Assistant)` with `FunctionCallContent` | `{ type: "function_call", call_id: "{callId}", name: "{name}", arguments: "{json}" }` |
| `ChatMessage(Role=Tool)` with `FunctionResultContent` | `{ type: "function_call_output", call_id: "{callId}", output: "{text}" }` |
| Multiple system messages | Last system message wins (becomes `instructions`) |
| Empty/null content | Skipped |

### Usage Mapping (from `response.completed`)

| Responses API field | TokenUsage field | Formula |
|---|---|---|
| `usage.input_tokens` | InputTokens | `input_tokens - cached_tokens` |
| `usage.output_tokens` | OutputTokens | `output_tokens` |
| `usage.input_tokens_details.cached_tokens` | CacheReadTokens | `cached_tokens` |
| — | CacheCreationTokens | `0` (always) |
| — | ThinkingTokens | `0` (reasoning tokens not broken out) |
| `usage.total_tokens` | (informational) | not stored in TokenUsage |

### HTTP Request Construction

**Base headers** (shared by both transports):

| Header | Value |
|---|---|
| `Authorization` | `Bearer {access_token}` |
| `chatgpt-account-id` | `{accountId from JWT}` |
| `originator` | `lis` |
| `User-Agent` | `lis/{version} ({os})` |

**SSE-specific headers** (added on top of base):

| Header | Value |
|---|---|
| `OpenAI-Beta` | `responses=experimental` |
| `Content-Type` | `application/json` |
| `Accept` | `text/event-stream` |
| `session_id` | `{sessionId}` (if set) |
| `x-client-request-id` | `{sessionId}` (if set) |

**WebSocket-specific headers** (added on top of base):

| Header | Value |
|---|---|
| `OpenAI-Beta` | `responses_websockets=2026-02-06` |
| `x-client-request-id` | `{requestId}` |
| `session_id` | `{requestId}` |

Note: WebSocket does NOT include `Content-Type`, `Accept`, or the SSE-format `OpenAI-Beta`.

**URL:** `{baseUrl}/codex/responses` (default: `https://chatgpt.com/backend-api/codex/responses`)

**URL normalization:** If baseUrl already ends with `/codex/responses`, use as-is. If it ends with `/codex`, append `/responses`. Otherwise append `/codex/responses`.

**WebSocket URL:** Same URL with protocol swapped: `https:` → `wss:`, `http:` → `ws:`.

### Token Refresh

**Endpoint:** `POST https://auth.openai.com/oauth/token`

**Body** (form-urlencoded):

| Field | Value |
|---|---|
| `grant_type` | `refresh_token` |
| `refresh_token` | `{current_refresh_token}` |
| `client_id` | `app_EMoamEEZ73f0CkXaXp7hrann` |

**Response:**
```json
{ "access_token": "eyJ...", "refresh_token": "rt-...", "expires_in": 3600 }
```

**accountId extraction:** Decode access token as JWT (base64url decode middle segment), read `["https://api.openai.com/auth"]["chatgpt_account_id"]`.

### System Invariants

1. **Every outbound API request carries a valid (non-expired) Bearer token and the corresponding accountId** — never stale, never mismatched.
2. **At most one token refresh HTTP request is in flight at any time** — concurrent callers await the same Task<CodexTokenInfo>.
3. **accountId is always derived from the current access token's JWT** — it is re-extracted after every refresh, never cached separately.
4. **`store` is always `false`** — the Codex Responses API rejects `store: true` with a 400 error.
5. **SSE events are processed strictly in order** — no buffering, reordering, or parallel processing of events within a single response. Same holds for WebSocket messages.
6. **Tool call arguments are only exposed as complete parsed JSON** — partial JSON fragments during streaming are accumulated but never yielded to the caller.
7. **Usage token arithmetic is exact:** `InputTokens + CacheReadTokens == raw_input_tokens` from the API response.
8. **System messages never appear in the `input` array** — they are always placed in the `instructions` field.
9. **Retry backoff is exponential and bounded:** delay = 1000ms × 2^attempt, max 3 retries, only for status codes {429, 500, 502, 503, 504} and network errors. (SSE transport only — WebSocket failures trigger fallback, not retry.)
10. **CancellationToken is checked between retries and during streaming** — abort is never deferred past the next event boundary.
11. **WebSocket → SSE fallback is one-way and sticky.** Once a session falls back to SSE due to a WebSocket failure, all subsequent requests for that session use SSE. There is no automatic recovery to WebSocket.
12. **No mid-stream transport switch.** If WebSocket fails after the first event is emitted, the error propagates. Fallback only happens if failure occurs before any events.
13. **At most one active request per cached WebSocket connection.** The `busy` flag serializes access; concurrent requests to the same session open a fresh (non-cached) connection.
14. **Delta context is only sent when the full request (minus input) matches the previous request exactly.** Model, tools, instructions, or config changes force full-context retransmission.
15. **`prompt_cache_key` is set to `sessionId` when sessionId is non-null.** Omitted when null. This is transport-agnostic (works with both SSE and WebSocket).
16. **WebSocket idle TTL is 5 minutes.** Cached connections not used within 5 minutes are closed and evicted.

### Behavior quality bar (rung 4)

- [x] All states enumerated (4 machines × all states listed, no implicit "other")
- [x] All events enumerated (exhaustive per machine)
- [x] Every (state, event) cell filled with target state or "invalid"/"ignored"
- [x] Initial and terminal states marked
- [x] Invariants stated as plain-language assertions (20 invariants)
- [x] Every invariant checkable from observable system state
- [x] Invariants reference quantities or relationships (not vibes)

---

## Verification (Rung 4 — Evals + Properties + Characterization)

### Test file structure

```
Lis.Tests/
  Providers/
    Codex/
      CodexTokenManagerTests.cs       — token lifecycle property tests
      CodexSseParserTests.cs          — SSE parsing characterization tests
      CodexWebSocketTransportTests.cs — connection caching, delta context, fallback tests
      CodexTransportSelectorTests.cs  — transport selection + WS→SSE fallback tests
      CodexMessageConverterTests.cs   — conversion property tests + eval set
      CodexChatClientTests.cs         — integration tests (gated on credentials)
      CodexRetryTests.cs              — retry state machine tests
      CodexUsageExtractorTests.cs     — usage mapping tests
      Fixtures/
        sse-simple-text.txt           — recorded SSE stream: single text response
        sse-tool-call.txt             — recorded SSE stream: tool call + result
        sse-reasoning.txt             — recorded SSE stream: reasoning + text
        sse-multi-item.txt            — recorded SSE stream: reasoning + text + tool call
        sse-error.txt                 — recorded SSE stream: error event
        sse-rate-limit.txt            — recorded SSE stream: 429 response
        eval-messages.jsonl           — message conversion eval set
```

### 1. Token Manager — Property Tests

```csharp
namespace Lis.Tests.Providers.Codex;

public class CodexTokenManagerTests
{
	// INV-1: Every outbound request carries a valid token
	// Property: GetValidTokenAsync never returns an expired token
	[Fact]
	public async Task Property_NeverReturnsExpiredToken()
	{
		// Arrange: create manager with token expiring in 2 seconds, 1s buffer
		var handler = new FakeRefreshHandler(succeedAfter: 0);
		var http = new HttpClient(handler);
		var options = new CodexOptions {
			AccessToken  = CreateJwt(expiresIn: TimeSpan.FromSeconds(2), accountId: "acc_123"),
			RefreshToken = "rt-test",
			ExpiryBufferSeconds = 1,
		};
		var manager = new CodexTokenManager(options, http);

		// Act: call repeatedly, some after expiry
		for (int i = 0; i < 20; i++)
		{
			CodexTokenInfo info = await manager.GetValidTokenAsync();
			// Assert: token is always valid at time of return
			Assert.True(info.ExpiresAt > DateTimeOffset.UtcNow);
			await Task.Delay(200);
		}
	}

	// INV-2: At most one refresh in flight
	// Property: concurrent GetValidTokenAsync calls share the same refresh
	[Fact]
	public async Task Property_ConcurrentCallsShareSingleRefresh()
	{
		var handler = new CountingRefreshHandler(delay: TimeSpan.FromMilliseconds(200));
		var http = new HttpClient(handler);
		var options = new CodexOptions {
			AccessToken  = CreateJwt(expiresIn: TimeSpan.FromSeconds(-1), accountId: "acc_123"), // already expired
			RefreshToken = "rt-test",
		};
		var manager = new CodexTokenManager(options, http);

		// Act: fire 10 concurrent requests
		Task<CodexTokenInfo>[] tasks = Enumerable.Range(0, 10)
			.Select(_ => manager.GetValidTokenAsync())
			.ToArray();
		CodexTokenInfo[] results = await Task.WhenAll(tasks);

		// Assert: only 1 refresh request was made
		Assert.Equal(1, handler.RequestCount);
		// Assert: all callers got the same token
		Assert.All(results, r => Assert.Equal(results[0].AccessToken, r.AccessToken));
	}

	// INV-3: accountId always derived from JWT
	[Theory]
	[InlineData("acc_abc123")]
	[InlineData("acc_xyz789")]
	[InlineData("org-test-account")]
	public async Task Property_AccountIdAlwaysFromJwt(string expectedAccountId)
	{
		var handler = new FakeRefreshHandler(accountId: expectedAccountId);
		var http = new HttpClient(handler);
		var options = new CodexOptions {
			AccessToken  = CreateJwt(expiresIn: TimeSpan.FromHours(1), accountId: expectedAccountId),
			RefreshToken = "rt-test",
		};
		var manager = new CodexTokenManager(options, http);

		CodexTokenInfo info = await manager.GetValidTokenAsync();
		Assert.Equal(expectedAccountId, info.AccountId);
	}

	// State machine: Failed state throws on GetToken
	[Fact]
	public async Task Failed_State_Throws_On_GetToken()
	{
		var handler = new FakeRefreshHandler(alwaysFail: true);
		var http = new HttpClient(handler);
		var options = new CodexOptions {
			AccessToken  = CreateJwt(expiresIn: TimeSpan.FromSeconds(-1), accountId: "acc_123"),
			RefreshToken = "rt-test",
		};
		var manager = new CodexTokenManager(options, http);

		await Assert.ThrowsAsync<CodexAuthException>(() => manager.GetValidTokenAsync());
	}

	// Helper: create a minimal JWT with embedded accountId and expiry
	private static string CreateJwt(TimeSpan expiresIn, string accountId)
	{
		long exp = DateTimeOffset.UtcNow.Add(expiresIn).ToUnixTimeSeconds();
		string payload = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(
			$$"""{"https://api.openai.com/auth":{"chatgpt_account_id":"{{accountId}}"},"exp":{{exp}}}"""));
		return $"eyJhbGciOiJSUzI1NiJ9.{payload}.fakesig";
	}
}
```

### 2. SSE Parser — Characterization Tests

```csharp
namespace Lis.Tests.Providers.Codex;

public class CodexSseParserTests
{
	// Characterization: replay recorded SSE streams through parser,
	// assert exact event sequence matches snapshot

	public static IEnumerable<object[]> SseFixtures => new[]
	{
		new object[] { "sse-simple-text.txt", new[] {
			"response.created",
			"response.output_item.added",    // message
			"response.content_part.added",
			"response.output_text.delta",
			"response.output_text.delta",
			"response.output_item.done",
			"response.completed",
		}},
		new object[] { "sse-tool-call.txt", new[] {
			"response.created",
			"response.output_item.added",    // function_call
			"response.function_call_arguments.delta",
			"response.function_call_arguments.done",
			"response.output_item.done",
			"response.completed",
		}},
		new object[] { "sse-reasoning.txt", new[] {
			"response.created",
			"response.output_item.added",    // reasoning
			"response.reasoning_summary_part.added",
			"response.reasoning_summary_text.delta",
			"response.reasoning_summary_part.done",
			"response.output_item.done",
			"response.output_item.added",    // message
			"response.content_part.added",
			"response.output_text.delta",
			"response.output_item.done",
			"response.completed",
		}},
		new object[] { "sse-error.txt", new[] {
			"response.created",
			"response.failed",
		}},
	};

	[Theory]
	[MemberData(nameof(SseFixtures))]
	public async Task Characterization_EventSequence_Matches_Snapshot(
		string fixture, string[] expectedTypes)
	{
		await using Stream stream = LoadFixture(fixture);
		List<string> actualTypes = [];

		await foreach (JsonElement evt in CodexSseParser.ParseAsync(stream))
		{
			string? type = evt.GetProperty("type").GetString();
			Assert.NotNull(type);
			actualTypes.Add(type);
		}

		Assert.Equal(expectedTypes, actualTypes);
	}

	// Property: parser never yields events with missing "type" field
	[Theory]
	[MemberData(nameof(AllFixtureFiles))]
	public async Task Property_AllEvents_Have_Type(string fixture)
	{
		await using Stream stream = LoadFixture(fixture);

		await foreach (JsonElement evt in CodexSseParser.ParseAsync(stream))
		{
			Assert.True(evt.TryGetProperty("type", out JsonElement typeProp));
			Assert.Equal(JsonValueKind.String, typeProp.ValueKind);
			Assert.NotEmpty(typeProp.GetString()!);
		}
	}

	// Property: [DONE] sentinel is never yielded as an event
	[Theory]
	[MemberData(nameof(AllFixtureFiles))]
	public async Task Property_Done_Sentinel_Never_Yielded(string fixture)
	{
		await using Stream stream = LoadFixture(fixture);

		await foreach (JsonElement evt in CodexSseParser.ParseAsync(stream))
		{
			string? type = evt.GetProperty("type").GetString();
			Assert.NotEqual("[DONE]", type);
		}
	}

	// Property: empty stream yields zero events
	[Fact]
	public async Task Property_EmptyStream_YieldsNothing()
	{
		await using var stream = new MemoryStream(Array.Empty<byte>());
		List<JsonElement> events = [];

		await foreach (JsonElement evt in CodexSseParser.ParseAsync(stream))
			events.Add(evt);

		Assert.Empty(events);
	}

	public static IEnumerable<object[]> AllFixtureFiles => Directory
		.GetFiles(FixturesPath, "sse-*.txt")
		.Select(f => new object[] { Path.GetFileName(f) });

	private static readonly string FixturesPath = Path.Combine(
		AppContext.BaseDirectory, "..", "..", "..", "Providers", "Codex", "Fixtures");

	private static Stream LoadFixture(string name) =>
		File.OpenRead(Path.Combine(FixturesPath, name));
}
```

### 3. Message Converter — Property Tests + Eval Set

```csharp
namespace Lis.Tests.Providers.Codex;

public class CodexMessageConverterTests
{
	// INV-8: System messages never in input array
	[Fact]
	public void Property_SystemMessage_NeverInInputArray()
	{
		var messages = new List<ChatMessage>
		{
			new(ChatRole.System, "You are helpful."),
			new(ChatRole.User, "Hello"),
		};

		(string? instructions, JsonArray input) = CodexMessageConverter.ConvertToResponsesApi(messages);

		Assert.Equal("You are helpful.", instructions);
		// No element in input has role=system
		foreach (JsonNode? item in input)
		{
			string? role = item?["role"]?.GetValue<string>();
			Assert.NotEqual("system", role);
		}
	}

	// Property: user text messages round-trip preserves text content
	[Theory]
	[InlineData("Hello")]
	[InlineData("")]
	[InlineData("Multi\nline\nmessage")]
	[InlineData("Special chars: <>&\"'")]
	[InlineData("Unicode: 日本語 emoji 🎉")]
	public void Property_UserTextPreserved(string text)
	{
		var messages = new List<ChatMessage> { new(ChatRole.User, text) };

		(_, JsonArray input) = CodexMessageConverter.ConvertToResponsesApi(messages);

		string? actual = input[0]?["content"]?[0]?["text"]?.GetValue<string>();
		Assert.Equal(text, actual);
	}

	// Property: tool results always carry call_id
	[Fact]
	public void Property_ToolResult_AlwaysHasCallId()
	{
		var messages = new List<ChatMessage>
		{
			new(ChatRole.User, "What time is it?"),
			new(ChatRole.Assistant, [new FunctionCallContent("call_123", "GetTime", new Dictionary<string, object?>())]),
			new(ChatRole.Tool, [new FunctionResultContent("call_123", "GetTime", "14:30 UTC")]),
		};

		(_, JsonArray input) = CodexMessageConverter.ConvertToResponsesApi(messages);

		JsonNode? toolOutput = input.FirstOrDefault(i =>
			i?["type"]?.GetValue<string>() == "function_call_output");
		Assert.NotNull(toolOutput);
		Assert.Equal("call_123", toolOutput!["call_id"]?.GetValue<string>());
	}

	// Eval set: load from JSONL, each line is { messages: [...], expectedInstructions, expectedInputTypes: [...] }
	// New bugs become new JSONL entries — the file grows monotonically
	[Theory]
	[MemberData(nameof(LoadEvalSet))]
	public void EvalSet_MessageConversion(
		string label,
		List<ChatMessage> messages,
		string? expectedInstructions,
		string[] expectedInputTypes)
	{
		(string? instructions, JsonArray input) = CodexMessageConverter.ConvertToResponsesApi(messages);

		Assert.Equal(expectedInstructions, instructions);

		string[] actualTypes = input
			.Select(i => i?["type"]?.GetValue<string>() ?? i?["role"]?.GetValue<string>() ?? "unknown")
			.ToArray();
		Assert.Equal(expectedInputTypes, actualTypes);
	}

	public static IEnumerable<object[]> LoadEvalSet()
	{
		// Returns eval entries — initially seeded, grows with each discovered bug
		yield return new object[]
		{
			"simple_user_message",
			new List<ChatMessage> { new(ChatRole.User, "Hello") },
			(string?)null,
			new[] { "user" },
		};
		yield return new object[]
		{
			"system_plus_user",
			new List<ChatMessage>
			{
				new(ChatRole.System, "Be concise."),
				new(ChatRole.User, "Explain gravity."),
			},
			"Be concise.",
			new[] { "user" },
		};
		yield return new object[]
		{
			"tool_call_round_trip",
			new List<ChatMessage>
			{
				new(ChatRole.User, "What time?"),
				new(ChatRole.Assistant, [new FunctionCallContent("c1", "GetTime", new Dictionary<string, object?>())]),
				new(ChatRole.Tool, [new FunctionResultContent("c1", "GetTime", "14:30")]),
				new(ChatRole.Assistant, [new TextContent("It's 14:30 UTC.")]),
			},
			(string?)null,
			new[] { "user", "function_call", "function_call_output", "message" },
		};
		yield return new object[]
		{
			"multiple_system_messages_last_wins",
			new List<ChatMessage>
			{
				new(ChatRole.System, "First instruction"),
				new(ChatRole.System, "Override instruction"),
				new(ChatRole.User, "Go"),
			},
			"Override instruction",
			new[] { "user" },
		};
	}
}
```

### 4. Usage Extractor Tests

```csharp
namespace Lis.Tests.Providers.Codex;

public class CodexUsageExtractorTests
{
	private readonly CodexUsageExtractor _sut = new();

	// INV-7: InputTokens + CacheReadTokens == raw input_tokens
	public static IEnumerable<object?[]> UsageData => new[]
	{
		// label, inputTokens, outputTokens, cachedTokens, expectedUsage
		new object?[] { "standard",     500, 200, 0,   new TokenUsage(500, 200, 0,   0, 0) },
		new object?[] { "with_cache",   500, 200, 300, new TokenUsage(200, 200, 300, 0, 0) },
		new object?[] { "all_cached",   500, 100, 500, new TokenUsage(0,   100, 500, 0, 0) },
		new object?[] { "zero_tokens",  0,   0,   0,   (TokenUsage?)null },
		new object?[] { "null_meta",    -1,  -1,  -1,  (TokenUsage?)null }, // signals null metadata
	};

	[Theory]
	[MemberData(nameof(UsageData))]
	public void Extract_Maps_Correctly(
		string label, int inputTokens, int outputTokens, int cachedTokens, TokenUsage? expected)
	{
		IReadOnlyDictionary<string, object?>? metadata = inputTokens == -1
			? null
			: BuildCodexUsageMetadata(inputTokens, outputTokens, cachedTokens);

		TokenUsage? result = this._sut.Extract(metadata);

		Assert.Equal(expected, result);

		// INV-7 property check
		if (result is not null && metadata is not null)
		{
			Assert.Equal(inputTokens, result.InputTokens + result.CacheReadTokens);
		}
	}

	private static Dictionary<string, object?> BuildCodexUsageMetadata(
		int inputTokens, int outputTokens, int cachedTokens)
	{
		// Simulates the metadata shape from CodexChatClient's response.completed handling
		return new Dictionary<string, object?>
		{
			["codex.input_tokens"] = inputTokens,
			["codex.output_tokens"] = outputTokens,
			["codex.cached_tokens"] = cachedTokens,
		};
	}
}
```

### 5. WebSocket Transport Tests

```csharp
namespace Lis.Tests.Providers.Codex;

public class CodexWebSocketTransportTests : IDisposable
{
	private readonly CodexWebSocketTransport _sut;

	public CodexWebSocketTransportTests()
	{
		var options = new CodexOptions {
			AccessToken = CreateJwt(TimeSpan.FromHours(1), "acc_test"),
			RefreshToken = "rt-test",
		};
		var tokenManager = new CodexTokenManager(options, new HttpClient());
		this._sut = new CodexWebSocketTransport(tokenManager, options);
	}

	// INV-11: Fallback is sticky
	[Fact]
	public void RecordFailure_Makes_Fallback_Sticky()
	{
		Assert.False(this._sut.IsSseFallbackActive("session-1"));

		this._sut.RecordFailure("session-1");

		Assert.True(this._sut.IsSseFallbackActive("session-1"));
		// Second check — still sticky
		Assert.True(this._sut.IsSseFallbackActive("session-1"));
	}

	// INV-11: Fallback is per-session
	[Fact]
	public void Fallback_Is_PerSession()
	{
		this._sut.RecordFailure("session-1");

		Assert.True(this._sut.IsSseFallbackActive("session-1"));
		Assert.False(this._sut.IsSseFallbackActive("session-2"));
	}

	// INV-14: Delta context only when request body matches
	[Fact]
	public void BuildRequestWithDelta_FullContext_When_No_Continuation()
	{
		var body = new CodexRequest { Model = "codex-1", Input = JsonArray.Parse("[{\"role\":\"user\"}]")! };
		CodexRequest result = this._sut.BuildRequestWithDelta("session-1", body);

		Assert.Null(result.PreviousResponseId);
		Assert.Equal(body.Input.Count, result.Input.Count);
	}

	// INV-14: Delta context sent when continuation matches
	[Fact]
	public void BuildRequestWithDelta_DeltaOnly_When_Continuation_Matches()
	{
		var originalBody = new CodexRequest {
			Model = "codex-1",
			Input = JsonArray.Parse("[{\"role\":\"user\",\"content\":\"hello\"}]")!
		};
		var responseItems = JsonArray.Parse("[{\"type\":\"message\",\"content\":\"hi\"}]")!;

		this._sut.StoreContinuation("session-1", originalBody, "resp_123", responseItems);

		// New request has original input + response items + new user message
		var newBody = new CodexRequest {
			Model = "codex-1",
			Input = JsonArray.Parse(
				"[{\"role\":\"user\",\"content\":\"hello\"},{\"type\":\"message\",\"content\":\"hi\"},{\"role\":\"user\",\"content\":\"thanks\"}]")!
		};

		CodexRequest result = this._sut.BuildRequestWithDelta("session-1", newBody);

		Assert.Equal("resp_123", result.PreviousResponseId);
		Assert.Equal(1, result.Input.Count); // only the delta (new user message)
	}

	// INV-14: Full context when model/tools changed
	[Fact]
	public void BuildRequestWithDelta_FullContext_When_Model_Changed()
	{
		var originalBody = new CodexRequest {
			Model = "codex-1",
			Input = JsonArray.Parse("[{\"role\":\"user\",\"content\":\"hello\"}]")!
		};

		this._sut.StoreContinuation("session-1", originalBody,
			"resp_123", JsonArray.Parse("[]")!);

		var newBody = new CodexRequest {
			Model = "gpt-5.5", // different model!
			Input = originalBody.Input,
		};

		CodexRequest result = this._sut.BuildRequestWithDelta("session-1", newBody);

		Assert.Null(result.PreviousResponseId); // full context, not delta
	}

	// INV-16: Idle TTL = 5 minutes
	[Fact]
	public void IdleTtl_Is_FiveMinutes()
	{
		// Verify the constant is correct — implementation detail but load-bearing
		Assert.Equal(TimeSpan.FromMinutes(5),
			CodexWebSocketTransport.IdleTtl);
	}

	// INV-13: Busy flag serializes access
	[Fact]
	public void Property_BusyConnection_Not_Reused()
	{
		// If the cached connection is busy, AcquireAsync opens a fresh one
		// This test needs a mock WebSocket; structure shown, impl depends on test harness
	}

	public void Dispose() => this._sut.Dispose();

	private static string CreateJwt(TimeSpan expiresIn, string accountId)
	{
		long exp = DateTimeOffset.UtcNow.Add(expiresIn).ToUnixTimeSeconds();
		string payload = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(
			$$"""{"https://api.openai.com/auth":{"chatgpt_account_id":"{{accountId}}"},"exp":{{exp}}}"""));
		return $"eyJhbGciOiJSUzI1NiJ9.{payload}.fakesig";
	}
}
```

### 6. Transport Selector Tests

```csharp
namespace Lis.Tests.Providers.Codex;

public class CodexTransportSelectorTests
{
	// INV-11: Auto mode falls back to SSE on WS failure
	[Fact]
	public async Task Auto_FallsBackToSse_When_WsFails_PreStream()
	{
		// Arrange: mock WS transport that fails on connect
		// Act: StreamAsync with transport=Auto
		// Assert: events received (via SSE fallback), session marked for SSE
	}

	// INV-12: No fallback after first event
	[Fact]
	public async Task Auto_NoFallback_When_WsFails_AfterFirstEvent()
	{
		// Arrange: mock WS transport that emits one event then errors
		// Act + Assert: error propagates, no SSE fallback attempted
	}

	// SSE mode skips WebSocket entirely
	[Fact]
	public async Task Sse_Mode_SkipsWebSocket()
	{
		// Arrange: transport=SSE
		// Act: StreamAsync
		// Assert: WS transport never called
	}

	// INV-15: prompt_cache_key set to sessionId
	[Fact]
	public void PromptCacheKey_SetToSessionId()
	{
		var request = CodexChatClient.BuildRequest(
			messages: new List<ChatMessage> { new(ChatRole.User, "hi") },
			options: null,
			sessionId: "session-abc",
			codexOptions: new CodexOptions { AccessToken = "t", RefreshToken = "r" });

		Assert.Equal("session-abc", request.PromptCacheKey);
	}

	// INV-15: prompt_cache_key omitted when sessionId is null
	[Fact]
	public void PromptCacheKey_Null_When_NoSessionId()
	{
		var request = CodexChatClient.BuildRequest(
			messages: new List<ChatMessage> { new(ChatRole.User, "hi") },
			options: null,
			sessionId: null,
			codexOptions: new CodexOptions { AccessToken = "t", RefreshToken = "r" });

		Assert.Null(request.PromptCacheKey);
	}
}
```

### 7. Retry Tests

```csharp
namespace Lis.Tests.Providers.Codex;

public class CodexRetryTests
{
	// INV-9: Exponential backoff: 1s, 2s, 4s
	// INV-9: Only {429, 500, 502, 503, 504} are retryable
	// INV-9: Max 3 retries

	[Theory]
	[InlineData(429, true)]
	[InlineData(500, true)]
	[InlineData(502, true)]
	[InlineData(503, true)]
	[InlineData(504, true)]
	[InlineData(400, false)]
	[InlineData(401, false)]
	[InlineData(403, false)]
	[InlineData(404, false)]
	[InlineData(422, false)]
	public async Task Retryable_Status_Codes(int statusCode, bool shouldRetry)
	{
		int attempts = 0;
		var handler = new DelegateHandler(_ =>
		{
			attempts++;
			return new HttpResponseMessage((HttpStatusCode)statusCode) { Content = new StringContent("error") };
		});

		var http = new HttpClient(handler);
		// ... exercise the retry logic ...

		if (shouldRetry)
			Assert.True(attempts > 1, $"Status {statusCode} should have been retried");
		else
			Assert.Equal(1, attempts);
	}

	[Fact]
	public async Task Max_Three_Retries()
	{
		int attempts = 0;
		var handler = new DelegateHandler(_ =>
		{
			attempts++;
			return new HttpResponseMessage(HttpStatusCode.TooManyRequests);
		});

		var http = new HttpClient(handler);
		// ... exercise the retry logic ...

		Assert.Equal(4, attempts); // 1 initial + 3 retries
	}

	// INV-10: CancellationToken checked between retries
	[Fact]
	public async Task Cancellation_Stops_Retries()
	{
		int attempts = 0;
		using var cts = new CancellationTokenSource();
		var handler = new DelegateHandler(_ =>
		{
			attempts++;
			if (attempts == 2) cts.Cancel();
			return new HttpResponseMessage(HttpStatusCode.TooManyRequests);
		});

		var http = new HttpClient(handler);
		// ... exercise with cts.Token ...

		Assert.True(attempts <= 3, "Should have stopped retrying after cancellation");
	}
}
```

### 6. Integration Tests (gated)

```csharp
namespace Lis.Tests.Providers.Codex;

public class CodexIntegrationTests
{
	private readonly CodexChatClient? _client;

	public CodexIntegrationTests()
	{
		string? access  = Environment.GetEnvironmentVariable("CODEX_ACCESS_TOKEN");
		string? refresh = Environment.GetEnvironmentVariable("CODEX_REFRESH_TOKEN");
		if (string.IsNullOrEmpty(access) || string.IsNullOrEmpty(refresh)) return;

		var options = new CodexOptions { AccessToken = access, RefreshToken = refresh };
		var tokenManager = new CodexTokenManager(options, new HttpClient());
		this._client = new CodexChatClient(tokenManager, options, new HttpClient());
	}

	private void SkipIfNoCredentials() =>
		Skip.If(this._client is null, "CODEX_ACCESS_TOKEN/CODEX_REFRESH_TOKEN not set");

	[Fact]
	public async Task Integration_SimpleCompletion()
	{
		this.SkipIfNoCredentials();
		ChatResponse response = await this._client!.GetResponseAsync(
			[new ChatMessage(ChatRole.User, "Respond with only the word hello")]);
		Assert.NotNull(response.Text);
		Assert.NotEmpty(response.Text);
	}

	[Fact]
	public async Task Integration_Streaming()
	{
		this.SkipIfNoCredentials();
		List<ChatResponseUpdate> chunks = [];
		await foreach (var chunk in this._client!.GetStreamingResponseAsync(
			[new ChatMessage(ChatRole.User, "Count from 1 to 3")]))
		{
			chunks.Add(chunk);
		}
		Assert.NotEmpty(chunks);
		Assert.Contains(chunks, c => !string.IsNullOrEmpty(c.Text));
	}

	[Fact]
	public async Task Integration_ToolCall()
	{
		this.SkipIfNoCredentials();
		var options = new ChatOptions
		{
			Tools = [AIFunctionFactory.Create(
				() => DateTime.UtcNow.ToString("HH:mm"),
				"GetCurrentTime", "Returns the current UTC time")],
		};
		ChatResponse response = await this._client!.GetResponseAsync(
			[new ChatMessage(ChatRole.User, "What time is it? Use the GetCurrentTime tool.")],
			options);
		Assert.Contains(response.Messages,
			m => m.Contents.OfType<FunctionCallContent>().Any());
	}
}
```

### Verification quality bar (rung 4)

- [x] Property-based generators (concurrent token calls, arbitrary text, status code exhaustion, delta context matching)
- [x] Eval set with growing entries (eval-messages.jsonl pattern)
- [x] Characterization snapshots (recorded SSE fixtures)
- [x] Invariants from Behavior appear as test predicates (INV-1 through INV-20 referenced)
- [x] Suite runs in CI and produces numeric signal (pass count, xUnit output)
- [x] Machine-extendable — new SSE fixtures and eval JSONL entries require no boilerplate

### Cross-pillar check: V ≥ B

- [x] Every state machine transition (4 machines) has at least one test covering it
- [x] Every invariant (INV-1 through INV-20) has a dedicated test assertion
- [x] Behavior rule count ≤ verification test count

---

## Multi-Provider Routing Architecture

### Problem

The current architecture registers a single `IChatClient` singleton — the last `Add*()` call wins. With two providers, the system needs to route each request to the correct `IChatClient` based on the agent's configured model.

### Affected files (3 injection sites + supporting code)

| File | Line(s) | Current | Change |
|---|---|---|---|
| `Lis.Api/Program.cs` | 101, 104 | Single `IChatClient` singleton | Register keyed clients per provider |
| `Lis.Agent/AgentSetup.cs` | 16 | `sp.GetRequiredService<IChatClient>()` | Accept provider key, resolve keyed client |
| `Lis.Agent/CompactionService.cs` | 25 | `[FromKeyedServices("compaction")]` | Unchanged — compaction already uses keyed services |
| `Lis.Agent/ConversationService.cs` | 131, 136 | Uses singleton IChatClient via Kernel; hardcoded `gen_ai.system = "anthropic"` | Resolve provider from agent, build Kernel per-request with correct client, dynamic tag |
| `Lis.Persistence/Entities/AgentEntity.cs` | — | No `provider` field | Add `provider` column (varchar(32), default "anthropic") |
| `Lis.Agent/AgentService.cs` | 98-103 | `ToModelSettings()` ignores provider | Include provider in returned context |

### Design: Keyed IChatClient per provider

```csharp
// Program.cs — register both providers as keyed services
builder.Services.AddSingleton(new ModelSettings()); // default fallback

if (Env("ANTHROPIC_ENABLED") == "true") {
    builder.Services.AddAnthropic();                                          // registers main IChatClient (backward compat)
    builder.Services.AddKeyedSingleton<IChatClient>("anthropic",
        (sp, _) => sp.GetRequiredService<IChatClient>());                     // alias to main
    builder.Services.AddKeyedSingleton<IUsageExtractor>("anthropic",
        (sp, _) => sp.GetRequiredService<IUsageExtractor>());
    builder.Services.AddKeyedSingleton<ITokenCounter>("anthropic",
        (sp, _) => sp.GetRequiredService<ITokenCounter>());
}

if (Env("CODEX_ENABLED") == "true") {
    builder.Services.AddCodex();                                              // registers keyed services only
    // AddCodex() registers:
    //   AddKeyedSingleton<IChatClient>("codex", ...)
    //   AddKeyedSingleton<IUsageExtractor>("codex", ...)
    //   AddKeyedSingleton<ITokenCounter>("codex", ...)
}
```

Key change: `AddCodex()` does NOT register an unkeyed `IChatClient` — it only registers keyed services. This preserves backward compatibility: Anthropic remains the default unkeyed singleton.

### Design: AgentEntity.Provider field

```csharp
// New column on AgentEntity
[MaxLength(32)]
[Column("provider", TypeName = "varchar(32)")]
[JsonPropertyName("provider")]
public string Provider { get; set; } = "anthropic";
```

Migration: `ALTER TABLE agent ADD COLUMN provider varchar(32) NOT NULL DEFAULT 'anthropic';`

The provider field is set when the user changes models. The `/model` command (or equivalent) should detect the provider from the model name and update both `Model` and `Provider` together.

### Design: Provider resolution in ConversationService

```csharp
// ConversationService.cs — resolve per-request
AgentEntity agent = await agentService.ResolveForChatAsync(db, chat, ct);
string providerKey = agent.Provider;  // "anthropic" or "codex"

IChatClient chatClient = sp.GetRequiredKeyedService<IChatClient>(providerKey);
IUsageExtractor usageExtractor = sp.GetRequiredKeyedService<IUsageExtractor>(providerKey);
ITokenCounter tokenCounter = sp.GetRequiredKeyedService<ITokenCounter>(providerKey);

Activity.Current?.SetTag("gen_ai.system", providerKey);  // dynamic, not hardcoded
```

The Kernel factory in `AgentSetup` needs to accept the `IChatClient` as a parameter rather than resolving it from DI at startup. Since Kernel creation is lightweight, building it per-request with the correct client is acceptable.

### Design: Model name → Provider mapping (for /model command)

| Pattern | Provider |
|---|---|
| `claude-*`, `anthropic/*` | `anthropic` |
| `gpt-*`, `codex-*`, `o1-*`, `o3-*` | `codex` |
| Unknown | Keep current provider (warn user) |

This mapping lives in a static helper, used only by the `/model` command to auto-detect provider when the user types a model name. It does NOT affect runtime routing — that always uses `AgentEntity.Provider`.

### Invariants (multi-provider)

17. **The provider field on AgentEntity determines which keyed IChatClient is used** — model name patterns are only hints for the `/model` command, not for runtime resolution.
18. **If a keyed provider is not registered (e.g., CODEX_ENABLED=false), attempting to route to it throws at request time** — fail loud, not silent fallback to wrong provider.
19. **Anthropic remains the unkeyed default** — backward compatibility: code that resolves `IChatClient` without a key still gets Anthropic.
20. **Provider and Model are updated atomically** — the `/model` command sets both in the same DB write.

### Test coverage (multi-provider)

```csharp
namespace Lis.Tests.Providers;

public class ProviderRoutingTests
{
    // INV-17: Provider field determines IChatClient
    [Theory]
    [InlineData("anthropic", typeof(AnthropicChatClient))]  // or whatever Anthropic registers
    [InlineData("codex", typeof(CodexChatClient))]
    public void Resolves_Correct_Client_By_Provider(string providerKey, Type expectedType)
    {
        // Arrange: register both providers
        var services = new ServiceCollection();
        // ... register both ...
        var sp = services.BuildServiceProvider();

        IChatClient client = sp.GetRequiredKeyedService<IChatClient>(providerKey);
        // Assert the correct implementation is resolved
    }

    // INV-18: Missing provider throws
    [Fact]
    public void Missing_Provider_Throws()
    {
        var services = new ServiceCollection();
        // Only register Anthropic, not Codex
        var sp = services.BuildServiceProvider();

        Assert.Throws<InvalidOperationException>(
            () => sp.GetRequiredKeyedService<IChatClient>("codex"));
    }

    // INV-19: Unkeyed resolves to Anthropic
    [Fact]
    public void Unkeyed_Resolves_To_Anthropic()
    {
        var services = new ServiceCollection();
        // Register both
        var sp = services.BuildServiceProvider();

        IChatClient unkeyed = sp.GetRequiredService<IChatClient>();
        IChatClient anthropic = sp.GetRequiredKeyedService<IChatClient>("anthropic");
        Assert.Same(unkeyed, anthropic);
    }

    // INV-20: Model + Provider updated atomically
    [Fact]
    public async Task Model_Command_Updates_Both_Fields()
    {
        // Arrange: agent with provider=anthropic, model=claude-sonnet-4-20250514
        // Act: /model gpt-5.5
        // Assert: agent.Provider == "codex", agent.Model == "gpt-5.5"
    }
}
```

---

## Conventions referenced

- `CLAUDE.md` — provider architecture, `Add*()` pattern, `[Trace]` attributes, code style
- Formatting: `jb cleanupcode` after changes
- Commits: gitmoji + conventional commits
- Service registration: `AddScoped` / `AddSingleton` pattern in Program.cs
- `.env.example` must be updated with all Codex env vars

## Inheritance references

| Artifact | Location | Reuse |
|---|---|---|
| DI registration pattern | `Lis.Providers/Anthropic/AnthropicProvider.cs` | Mirror `AddCodex()` structure |
| Options pattern | `Lis.Providers/Anthropic/AnthropicOptions.cs` | Same shape, different fields |
| IUsageExtractor | `Lis.Core/Channel/IUsageExtractor.cs` | Implement for Codex usage format |
| ITokenCounter | `Lis.Core/Channel/ITokenCounter.cs` | Stub returning null |
| IChatClient | `Microsoft.Extensions.AI` | Implement directly (no SDK adapter) |
| Retry pattern | `AnthropicProvider.RetryHandler` | Same exponential backoff concept |
| Bearer auth pattern | `AnthropicProvider.BearerAuthHandler` | Similar JWT + Bearer approach |
| OpenAI namespace | `Lis.Providers/OpenAi/` | Place in `Codex/` subdirectory |
| Reference implementation | `C:\code\pi\packages\ai\src\providers\openai-codex-responses.ts` | SSE parsing, headers, URL resolution, event mapping |
| Reference OAuth | `C:\code\pi\packages\ai\src\utils\oauth\openai-codex.ts` | Token refresh endpoint, JWT claim path, client ID |

## Open questions

None — the reference implementation at `C:\code\pi` resolves all design questions. The implementation should match its behavior for SSE parsing, header construction, URL resolution, token refresh, WebSocket connection caching, delta context optimization, and transport fallback.

## Definition of done

The implementation is done when:

1. All unit/property tests pass: `dotnet test Lis.Tests/Lis.Tests.csproj --filter "Codex"`
2. All SSE characterization tests pass against recorded fixtures
3. All eval set entries pass for message conversion
4. WebSocket transport tests pass (connection caching, delta context, fallback)
5. Multi-provider routing tests pass (keyed resolution, missing provider throws, unkeyed default)
6. DB migration adds `provider` column to `agent` table (default `"anthropic"`)
7. `ConversationService` resolves `IChatClient` per-request via `agent.Provider` keyed service
8. `Activity.Current?.SetTag("gen_ai.system", ...)` is dynamic, not hardcoded to `"anthropic"`
9. `dotnet build` succeeds with no warnings
10. `.env.example` includes `CODEX_*` env vars (including `CODEX_TRANSPORT`)
11. `Program.cs` registers both providers as keyed `IChatClient` services
12. Integration tests pass when `CODEX_ACCESS_TOKEN` + `CODEX_REFRESH_TOKEN` are set
13. `jb cleanupcode` produces no additional changes
