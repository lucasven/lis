using System.Text.Json;

using Lis.Providers.OpenAi.Codex;

using Microsoft.Extensions.AI;

namespace Lis.Tests.Providers.Codex;

public class CodexTransportSelectorTests : IDisposable {
	private readonly CodexWebSocketTransport _wsTransport;
	private readonly CodexOptions            _options;

	public CodexTransportSelectorTests() {
		this._options = new CodexOptions {
			AccessToken  = CodexTokenManagerTests.CreateJwt(TimeSpan.FromHours(1), "acc_test"),
			RefreshToken = "rt-test",
			Transport    = CodexTransport.Auto
		};
		CodexTokenManager tokenManager = new(this._options, new HttpClient());
		this._wsTransport = new CodexWebSocketTransport(tokenManager, this._options);
	}

	// INV-11: Auto mode with sticky SSE fallback routes to SSE
	[Fact]
	public async Task Auto_UseSse_When_SessionFallenBack() {
		this._wsTransport.RecordFailure("session-fallback");

		CodexOptions sseOptions = new() {
			AccessToken  = this._options.AccessToken,
			RefreshToken = this._options.RefreshToken,
			Transport    = CodexTransport.Auto
		};

		SseOkHandler handler = new();
		HttpClient http = new(handler);
		CodexTransportSelector selector = new(sseOptions, http, this._wsTransport);

		CodexRequest request = new() { Model = "codex-1" };
		Dictionary<string, string> headers = new() { ["Authorization"] = "Bearer test" };

		List<JsonElement> events = [];
		await foreach (JsonElement evt in selector.StreamAsync(request, "session-fallback", headers))
			events.Add(evt);

		// Should have received events via SSE (the handler returned a valid SSE stream)
		Assert.NotEmpty(events);
		Assert.True(handler.WasCalled, "SSE handler should have been called");
	}

	// INV-11: Fallback is sticky — still SSE on subsequent calls
	[Fact]
	public async Task Auto_FallbackSticky_SubsequentCallsUseSse() {
		this._wsTransport.RecordFailure("session-sticky");

		SseOkHandler handler = new();
		HttpClient http = new(handler);

		CodexOptions sseOptions = new() {
			AccessToken  = this._options.AccessToken,
			RefreshToken = this._options.RefreshToken,
			Transport    = CodexTransport.Auto
		};

		CodexTransportSelector selector = new(sseOptions, http, this._wsTransport);
		CodexRequest request = new() { Model = "codex-1" };
		Dictionary<string, string> headers = new() { ["Authorization"] = "Bearer test" };

		// First call
		await foreach (JsonElement _ in selector.StreamAsync(request, "session-sticky", headers))
		{ }

		// Reset handler
		handler.Reset();

		// Second call — still SSE
		await foreach (JsonElement _ in selector.StreamAsync(request, "session-sticky", headers))
		{ }

		Assert.True(handler.WasCalled, "SSE handler should have been called on second request too");
	}

	// No session ID → SSE (WebSocket needs session for caching)
	[Fact]
	public async Task NullSessionId_ForcesSse() {
		SseOkHandler handler = new();
		HttpClient http = new(handler);

		CodexOptions autoOptions = new() {
			AccessToken  = this._options.AccessToken,
			RefreshToken = this._options.RefreshToken,
			Transport    = CodexTransport.Auto
		};

		CodexTransportSelector selector = new(autoOptions, http, this._wsTransport);
		CodexRequest request = new() { Model = "codex-1" };
		Dictionary<string, string> headers = new() { ["Authorization"] = "Bearer test" };

		List<JsonElement> events = [];
		await foreach (JsonElement evt in selector.StreamAsync(request, null, headers))
			events.Add(evt);

		Assert.NotEmpty(events);
		Assert.True(handler.WasCalled);
	}

	// SSE mode skips WebSocket entirely
	[Fact]
	public async Task Sse_Mode_SkipsWebSocket() {
		SseOkHandler handler = new();
		HttpClient http = new(handler);

		CodexOptions sseOnly = new() {
			AccessToken  = this._options.AccessToken,
			RefreshToken = this._options.RefreshToken,
			Transport    = CodexTransport.Sse
		};

		CodexTransportSelector selector = new(sseOnly, http, this._wsTransport);
		CodexRequest request = new() { Model = "codex-1" };
		Dictionary<string, string> headers = new() { ["Authorization"] = "Bearer test" };

		List<JsonElement> events = [];
		await foreach (JsonElement evt in selector.StreamAsync(request, "session-sse", headers))
			events.Add(evt);

		Assert.NotEmpty(events);
		Assert.True(handler.WasCalled);
		// Session should not be marked as fallen back (SSE is the configured mode, not a fallback)
		Assert.False(this._wsTransport.IsSseFallbackActive("session-sse"));
	}

	// INV-15: prompt_cache_key set to sessionId
	[Fact]
	public void PromptCacheKey_SetToSessionId() {
		CodexOptions opts = new() { AccessToken = "t", RefreshToken = "r" };

		CodexRequest request = CodexChatClient.BuildRequest(
			messages: [new ChatMessage(ChatRole.User, "hi")],
			options: null,
			sessionId: "session-abc",
			codexOptions: opts);

		Assert.Equal("session-abc", request.PromptCacheKey);
	}

	// INV-15: prompt_cache_key omitted when sessionId is null
	[Fact]
	public void PromptCacheKey_Null_When_NoSessionId() {
		CodexOptions opts = new() { AccessToken = "t", RefreshToken = "r" };

		CodexRequest request = CodexChatClient.BuildRequest(
			messages: [new ChatMessage(ChatRole.User, "hi")],
			options: null,
			sessionId: null,
			codexOptions: opts);

		Assert.Null(request.PromptCacheKey);
	}

	public void Dispose() {
		this._wsTransport.Dispose();
		GC.SuppressFinalize(this);
	}

	private sealed class SseOkHandler : HttpMessageHandler {
		public bool WasCalled { get; private set; }

		public void Reset() => this.WasCalled = false;

		protected override Task<HttpResponseMessage> SendAsync(
			HttpRequestMessage request, CancellationToken ct) {
			this.WasCalled = true;

			string sseBody = """
				data: {"type":"response.created","response":{"id":"resp_sse","status":"in_progress"}}

				data: {"type":"response.completed","response":{"id":"resp_sse","status":"completed","output":[],"usage":{"input_tokens":5,"output_tokens":3,"total_tokens":8,"input_tokens_details":{"cached_tokens":0}}}}

				data: [DONE]

				""";

			return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK) {
				Content = new System.Net.Http.StringContent(sseBody, System.Text.Encoding.UTF8, "text/event-stream")
			});
		}
	}
}
