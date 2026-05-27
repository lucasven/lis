using System.Diagnostics;
using System.Net.WebSockets;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Lis.Providers.OpenAi.Codex;

public sealed class CodexTransportSelector {
	private const int MaxRetries  = 3;
	private const int BaseDelayMs = 1000;

	private static readonly HashSet<int> RetryableStatusCodes = [429, 500, 502, 503, 504];

	private readonly CodexOptions             _options;
	private readonly HttpClient               _httpClient;
	private readonly CodexWebSocketTransport   _wsTransport;

	public CodexTransportSelector(
		CodexOptions options,
		HttpClient httpClient,
		CodexWebSocketTransport wsTransport) {
		this._options     = options;
		this._httpClient  = httpClient;
		this._wsTransport = wsTransport;
	}

	public IAsyncEnumerable<JsonElement> StreamAsync(
		CodexRequest request,
		string? sessionId,
		Dictionary<string, string> headers,
		CancellationToken ct = default) {

		CodexTransport effective = this._options.Transport;

		// Auto: try WS unless session has fallen back
		if (effective == CodexTransport.Auto && sessionId is not null && this._wsTransport.IsSseFallbackActive(sessionId))
			effective = CodexTransport.Sse;

		// No session ID → SSE (WebSocket needs session for caching)
		if (effective != CodexTransport.Sse && sessionId is null)
			effective = CodexTransport.Sse;

		Activity.Current?.SetTag("codex.transport", effective.ToString());
		Activity.Current?.SetTag("codex.request_model", request.Model ?? "(null)");

		return effective switch {
			CodexTransport.WebSocket => this.StreamWebSocketAsync(request, sessionId!, headers, ct),
			CodexTransport.Sse      => this.StreamSseWithRetryAsync(request, headers, ct),
			_                       => this.StreamAutoAsync(request, sessionId!, headers, ct),
		};
	}

	private async IAsyncEnumerable<JsonElement> StreamAutoAsync(
		CodexRequest request, string sessionId,
		Dictionary<string, string> headers,
		[EnumeratorCancellation] CancellationToken ct) {

		bool firstEventEmitted = false;

		IAsyncEnumerator<JsonElement>? wsEnumerator = null;
		try {
			wsEnumerator = this.StreamWebSocketAsync(request, sessionId, headers, ct).GetAsyncEnumerator(ct);

			while (true) {
				bool moved;
				try {
					moved = await wsEnumerator.MoveNextAsync();
				} catch (Exception ex) when (!firstEventEmitted && IsTransportError(ex)) {
					// WsFailed_PreStream → fallback to SSE
					this._wsTransport.RecordFailure(sessionId);
					await wsEnumerator.DisposeAsync();
					wsEnumerator = null;
					break;
				}

				if (!moved) yield break;
				firstEventEmitted = true;
				yield return wsEnumerator.Current;
			}
		} finally {
			if (wsEnumerator is not null)
				await wsEnumerator.DisposeAsync();
		}

		// Fallback to SSE
		if (!firstEventEmitted) {
			await foreach (JsonElement evt in this.StreamSseWithRetryAsync(request, headers, ct))
				yield return evt;
		}
	}

	private async IAsyncEnumerable<JsonElement> StreamWebSocketAsync(
		CodexRequest request, string sessionId,
		Dictionary<string, string> headers,
		[EnumeratorCancellation] CancellationToken ct) {

		CodexRequest effective = this._wsTransport.BuildRequestWithDelta(sessionId, request);
		Activity.Current?.SetTag("codex.ws_delta_model", effective.Model ?? "(null)");
		Activity.Current?.SetTag("codex.ws_has_prev_response", effective.PreviousResponseId is not null);

		(ClientWebSocket socket, bool reused, Action<bool> release) =
			await this._wsTransport.AcquireAsync(sessionId, ct);
		Activity.Current?.SetTag("codex.ws_reused", reused);

		bool success = false;
		try {
			// Send the request via WebSocket
			JsonObject wsMessage = new() {
				["type"]     = "response.create",
				["response"] = JsonSerializer.SerializeToNode(effective)
			};

			Activity.Current?.SetTag("codex.ws_payload_model",
				wsMessage["response"]?["model"]?.GetValue<string>() ?? "(missing)");

			byte[] payload = Encoding.UTF8.GetBytes(wsMessage.ToJsonString());
			await socket.SendAsync(payload, WebSocketMessageType.Text, true, ct);

			string? responseId = null;
			JsonArray responseItems = [];

			await foreach (JsonElement evt in CodexWebSocketTransport.ParseWebSocketAsync(socket, ct)) {
				string? type = evt.TryGetProperty("type", out JsonElement typeProp) ? typeProp.GetString() : null;

				if (type == "response.completed" && evt.TryGetProperty("response", out JsonElement resp)) {
					if (resp.TryGetProperty("id", out JsonElement idProp))
						responseId = idProp.GetString();
					if (resp.TryGetProperty("output", out JsonElement outputProp)) {
						responseItems = JsonSerializer.Deserialize<JsonArray>(outputProp.GetRawText()) ?? [];
					}
				}

				yield return evt;
			}

			// Store continuation for delta optimization
			if (responseId is not null)
				this._wsTransport.StoreContinuation(sessionId, request, responseId, responseItems);

			success = true;
		} finally {
			release(success);
		}
	}

	private async IAsyncEnumerable<JsonElement> StreamSseWithRetryAsync(
		CodexRequest request,
		Dictionary<string, string> headers,
		[EnumeratorCancellation] CancellationToken ct) {

		string url = CodexWebSocketTransport.ResolveSseUrl(this._options.BaseUrl);
		string body = JsonSerializer.Serialize(request);

		for (int attempt = 0; attempt <= MaxRetries; attempt++) {
			ct.ThrowIfCancellationRequested();

			if (attempt > 0) {
				int delay = BaseDelayMs * (1 << (attempt - 1));
				await Task.Delay(delay, ct);
			}

			Stream? sseStream = null;
			HttpResponseMessage? response = null;
			string? fatalError = null;
			try {
				HttpRequestMessage req = new(HttpMethod.Post, url);
				req.Content = new StringContent(body, Encoding.UTF8, "application/json");

				foreach ((string key, string value) in headers)
					req.Headers.TryAddWithoutValidation(key, value);

				req.Headers.TryAddWithoutValidation("OpenAI-Beta", "responses=experimental");
				req.Headers.Accept.ParseAdd("text/event-stream");

				response = await this._httpClient.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);

				if (!response.IsSuccessStatusCode) {
					int status = (int)response.StatusCode;
					if (attempt < MaxRetries && RetryableStatusCodes.Contains(status)) {
						response.Dispose();
						continue;
					}

					fatalError = $"Codex API returned {status}: {await response.Content.ReadAsStringAsync(ct)}";
					response.Dispose();
				} else {
					sseStream = await response.Content.ReadAsStreamAsync(ct);
				}
			} catch (HttpRequestException) when (attempt < MaxRetries) {
				response?.Dispose();
				continue;
			} catch (IOException) when (attempt < MaxRetries) {
				response?.Dispose();
				continue;
			}

			if (fatalError is not null)
				throw new HttpRequestException(fatalError);

			await foreach (JsonElement evt in CodexSseParser.ParseAsync(sseStream!, ct))
				yield return evt;

			yield break;
		}
	}

	private static bool IsTransportError(Exception ex) =>
		ex is WebSocketException or IOException or HttpRequestException;
}
