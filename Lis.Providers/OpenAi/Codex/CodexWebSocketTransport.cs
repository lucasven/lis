using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Lis.Providers.OpenAi.Codex;

public sealed record WebSocketContinuation(
	CodexRequest LastRequestBody,
	string LastResponseId,
	JsonArray LastResponseItems);

public sealed class CachedWebSocketConnection : IDisposable {
	public ClientWebSocket Socket             { get; }
	public bool            Busy               { get; set; }
	public WebSocketContinuation? Continuation { get; set; }
	public DateTimeOffset  LastUsed           { get; set; }

	public CachedWebSocketConnection(ClientWebSocket socket) {
		this.Socket   = socket;
		this.LastUsed = DateTimeOffset.UtcNow;
	}

	public void Dispose() {
		try { this.Socket.Dispose(); } catch { /* best effort */ }
	}
}

public sealed class CodexWebSocketTransport : IDisposable {
	public static readonly TimeSpan IdleTtl = TimeSpan.FromMinutes(5);

	private readonly CodexTokenManager _tokenManager;
	private readonly CodexOptions      _options;

	private readonly ConcurrentDictionary<string, CachedWebSocketConnection> _pool = new();
	private readonly ConcurrentDictionary<string, WebSocketContinuation> _continuations = new();
	private readonly ConcurrentDictionary<string, bool> _sseFallback = new();
	private readonly Timer _cleanupTimer;

	public CodexWebSocketTransport(CodexTokenManager tokenManager, CodexOptions options) {
		this._tokenManager = tokenManager;
		this._options      = options;
		this._cleanupTimer = new Timer(_ => this.EvictExpired(), null, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1));
	}

	// INV-11: sticky SSE fallback per session
	public bool IsSseFallbackActive(string sessionId) =>
		this._sseFallback.ContainsKey(sessionId);

	public void RecordFailure(string sessionId) {
		this._sseFallback[sessionId] = true;

		// Evict cached connection if any
		if (this._pool.TryRemove(sessionId, out CachedWebSocketConnection? conn))
			conn.Dispose();
	}

	// INV-13: at most one active request per cached connection
	public async Task<(ClientWebSocket Socket, bool Reused, Action<bool> Release)> AcquireAsync(
		string sessionId, CancellationToken ct = default) {

		// Try to reuse a cached connection
		if (this._pool.TryGetValue(sessionId, out CachedWebSocketConnection? cached)
		    && !cached.Busy
		    && cached.Socket.State == WebSocketState.Open) {
			cached.Busy     = true;
			cached.LastUsed = DateTimeOffset.UtcNow;
			return (cached.Socket, true, success => this.Release(sessionId, cached, success));
		}

		// Open a fresh connection
		ClientWebSocket ws = new();
		CodexTokenInfo token = await this._tokenManager.GetValidTokenAsync(ct);

		string url = ResolveWebSocketUrl(this._options.BaseUrl);
		ws.Options.SetRequestHeader("Authorization", $"Bearer {token.AccessToken}");
		ws.Options.SetRequestHeader("chatgpt-account-id", token.AccountId);
		ws.Options.SetRequestHeader("originator", "lis");
		ws.Options.SetRequestHeader("OpenAI-Beta", "responses_websockets=2026-02-06");

		string requestId = Guid.NewGuid().ToString();
		ws.Options.SetRequestHeader("x-client-request-id", requestId);
		ws.Options.SetRequestHeader("session_id", requestId);

		await ws.ConnectAsync(new Uri(url), ct);

		CachedWebSocketConnection newConn = new(ws) { Busy = true };
		return (ws, false, success => this.Release(sessionId, newConn, success));
	}

	private void Release(string sessionId, CachedWebSocketConnection conn, bool success) {
		conn.Busy     = false;
		conn.LastUsed = DateTimeOffset.UtcNow;

		if (success && conn.Socket.State == WebSocketState.Open) {
			this._pool[sessionId] = conn;
		} else {
			this._pool.TryRemove(sessionId, out _);
			conn.Dispose();
			if (!success) this.RecordFailure(sessionId);
		}
	}

	// INV-14: delta context only when request body matches exactly
	public CodexRequest BuildRequestWithDelta(string sessionId, CodexRequest fullBody) {
		if (!this._continuations.TryGetValue(sessionId, out WebSocketContinuation? cont))
			return fullBody;

		// Check if everything except input and previous_response_id matches
		if (!RequestBodyMatchesExcludingInput(fullBody, cont.LastRequestBody)) {
			this._continuations.TryRemove(sessionId, out _);
			return fullBody;
		}

		// Check if the current input starts with the known prefix
		JsonArray expectedPrefix = BuildExpectedPrefix(cont.LastRequestBody.Input, cont.LastResponseItems);
		if (!InputStartsWith(fullBody.Input, expectedPrefix)) {
			this._continuations.TryRemove(sessionId, out _);
			return fullBody;
		}

		// Extract delta (new messages since last response)
		int prefixLen = expectedPrefix.Count;
		JsonArray delta = [];
		for (int i = prefixLen; i < fullBody.Input.Count; i++)
			delta.Add(fullBody.Input[i]!.DeepClone());

		return new CodexRequest {
			Model              = fullBody.Model,
			Store              = fullBody.Store,
			Stream             = fullBody.Stream,
			Instructions       = fullBody.Instructions,
			Input              = delta,
			Tools              = fullBody.Tools,
			ToolChoice         = fullBody.ToolChoice,
			ParallelToolCalls  = fullBody.ParallelToolCalls,
			Text               = fullBody.Text,
			Reasoning          = fullBody.Reasoning,
			Include            = fullBody.Include,
			PromptCacheKey     = fullBody.PromptCacheKey,
			PreviousResponseId = cont.LastResponseId
		};
	}

	public void StoreContinuation(
		string sessionId, CodexRequest requestBody, string responseId, JsonArray responseItems) {
		this._continuations[sessionId] = new WebSocketContinuation(requestBody, responseId, responseItems);
	}

	public static async IAsyncEnumerable<JsonElement> ParseWebSocketAsync(
		ClientWebSocket socket,
		[EnumeratorCancellation] CancellationToken ct = default) {

		byte[] buffer = new byte[64 * 1024];

		while (socket.State == WebSocketState.Open && !ct.IsCancellationRequested) {
			using MemoryStream ms = new();

			WebSocketReceiveResult result;
			do {
				result = await socket.ReceiveAsync(buffer, ct);
				if (result.MessageType == WebSocketMessageType.Close) yield break;
				await ms.WriteAsync(buffer.AsMemory(0, result.Count), ct);
			} while (!result.EndOfMessage);

			if (result.MessageType != WebSocketMessageType.Text) continue;

			string text = Encoding.UTF8.GetString(ms.GetBuffer(), 0, (int)ms.Length);

			JsonElement element;
			try {
				element = JsonDocument.Parse(text).RootElement.Clone();
			} catch (JsonException) {
				continue;
			}

			yield return element;
		}
	}

	private static bool RequestBodyMatchesExcludingInput(CodexRequest a, CodexRequest b) =>
		a.Model == b.Model
		&& a.Store == b.Store
		&& a.Stream == b.Stream
		&& a.Instructions == b.Instructions
		&& a.ToolChoice == b.ToolChoice
		&& a.ParallelToolCalls == b.ParallelToolCalls
		&& ToolsMatch(a.Tools, b.Tools);

	private static bool ToolsMatch(List<CodexTool>? a, List<CodexTool>? b) {
		if (a is null && b is null) return true;
		if (a is null || b is null) return false;
		if (a.Count != b.Count) return false;
		for (int i = 0; i < a.Count; i++)
			if (a[i].Name != b[i].Name) return false;
		return true;
	}

	private static JsonArray BuildExpectedPrefix(JsonArray lastInput, JsonArray lastResponseItems) {
		JsonArray prefix = [];
		foreach (JsonNode? item in lastInput)
			prefix.Add(item?.DeepClone());
		foreach (JsonNode? item in lastResponseItems)
			prefix.Add(item?.DeepClone());
		return prefix;
	}

	private static bool InputStartsWith(JsonArray current, JsonArray expectedPrefix) {
		if (current.Count < expectedPrefix.Count) return false;
		for (int i = 0; i < expectedPrefix.Count; i++) {
			string a = current[i]?.ToJsonString() ?? "";
			string b = expectedPrefix[i]?.ToJsonString() ?? "";
			if (a != b) return false;
		}
		return true;
	}

	private void EvictExpired() {
		DateTimeOffset cutoff = DateTimeOffset.UtcNow - IdleTtl;
		foreach ((string key, CachedWebSocketConnection conn) in this._pool) {
			if (conn.LastUsed < cutoff && !conn.Busy) {
				if (this._pool.TryRemove(key, out CachedWebSocketConnection? removed))
					removed.Dispose();
			}
		}
	}

	public static string ResolveWebSocketUrl(string baseUrl) {
		string httpUrl = ResolveSseUrl(baseUrl);
		return httpUrl
			.Replace("https://", "wss://")
			.Replace("http://", "ws://");
	}

	public static string ResolveSseUrl(string baseUrl) {
		if (baseUrl.EndsWith("/codex/responses", StringComparison.Ordinal))
			return baseUrl;
		if (baseUrl.EndsWith("/codex", StringComparison.Ordinal))
			return baseUrl + "/responses";
		return baseUrl.TrimEnd('/') + "/codex/responses";
	}

	public void Dispose() {
		this._cleanupTimer.Dispose();
		foreach ((_, CachedWebSocketConnection conn) in this._pool)
			conn.Dispose();
		this._pool.Clear();
	}
}
