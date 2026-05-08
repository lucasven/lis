using System.Net;
using System.Text;
using System.Text.Json;

using Lis.Providers.OpenAi.Codex;

namespace Lis.Tests.Providers.Codex;

public class CodexRetryTests : IDisposable {
	private readonly CodexWebSocketTransport _wsTransport;

	public CodexRetryTests() {
		CodexOptions options = new() {
			AccessToken  = CodexTokenManagerTests.CreateJwt(TimeSpan.FromHours(1), "acc_test"),
			RefreshToken = "rt-test"
		};
		CodexTokenManager tokenManager = new(options, new HttpClient());
		this._wsTransport = new CodexWebSocketTransport(tokenManager, options);
	}

	// INV-9: Only {429, 500, 502, 503, 504} are retryable
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
	public async Task Retryable_Status_Codes(int statusCode, bool shouldRetry) {
		int attempts = 0;
		StatusCodeHandler handler = new((HttpStatusCode)statusCode, () => attempts++);
		HttpClient http = new(handler);

		CodexOptions options = new() {
			AccessToken  = CodexTokenManagerTests.CreateJwt(TimeSpan.FromHours(1), "acc_test"),
			RefreshToken = "rt-test",
			Transport    = CodexTransport.Sse
		};

		CodexTransportSelector selector = new(options, http, this._wsTransport);

		CodexRequest request = new() { Model = "codex-1" };
		Dictionary<string, string> headers = new() {
			["Authorization"] = "Bearer test"
		};

		if (shouldRetry) {
			// Retryable status → should attempt multiple times then throw
			await Assert.ThrowsAsync<HttpRequestException>(async () => {
				await foreach (JsonElement _ in selector.StreamAsync(request, null, headers))
				{ }
			});
			Assert.Equal(4, attempts); // 1 initial + 3 retries
		} else {
			// Non-retryable → single attempt then throw
			await Assert.ThrowsAsync<HttpRequestException>(async () => {
				await foreach (JsonElement _ in selector.StreamAsync(request, null, headers))
				{ }
			});
			Assert.Equal(1, attempts);
		}
	}

	// INV-9: Max 3 retries (4 total attempts)
	[Fact]
	public async Task Max_Three_Retries() {
		int attempts = 0;
		StatusCodeHandler handler = new(HttpStatusCode.TooManyRequests, () => attempts++);
		HttpClient http = new(handler);

		CodexOptions options = new() {
			AccessToken  = CodexTokenManagerTests.CreateJwt(TimeSpan.FromHours(1), "acc_test"),
			RefreshToken = "rt-test",
			Transport    = CodexTransport.Sse
		};

		CodexTransportSelector selector = new(options, http, this._wsTransport);

		CodexRequest request = new() { Model = "codex-1" };
		Dictionary<string, string> headers = new() {
			["Authorization"] = "Bearer test"
		};

		await Assert.ThrowsAsync<HttpRequestException>(async () => {
			await foreach (JsonElement _ in selector.StreamAsync(request, null, headers))
			{ }
		});

		Assert.Equal(4, attempts); // 1 initial + 3 retries
	}

	// INV-9: Successful response on retry stops retrying
	[Fact]
	public async Task Succeeds_On_Retry() {
		int attempts = 0;
		FailThenSucceedHandler handler = new(failCount: 2, () => attempts++);
		HttpClient http = new(handler);

		CodexOptions options = new() {
			AccessToken  = CodexTokenManagerTests.CreateJwt(TimeSpan.FromHours(1), "acc_test"),
			RefreshToken = "rt-test",
			Transport    = CodexTransport.Sse
		};

		CodexTransportSelector selector = new(options, http, this._wsTransport);

		CodexRequest request = new() { Model = "codex-1" };
		Dictionary<string, string> headers = new() {
			["Authorization"] = "Bearer test"
		};

		List<JsonElement> events = [];
		await foreach (JsonElement evt in selector.StreamAsync(request, null, headers))
			events.Add(evt);

		Assert.Equal(3, attempts); // 2 failures + 1 success
		Assert.NotEmpty(events);
	}

	// INV-10: CancellationToken checked between retries
	[Fact]
	public async Task Cancellation_Stops_Retries() {
		int attempts = 0;
		using CancellationTokenSource cts = new();
		StatusCodeHandler handler = new(HttpStatusCode.TooManyRequests, () => {
			attempts++;
			if (attempts == 2) cts.Cancel();
		});
		HttpClient http = new(handler);

		CodexOptions options = new() {
			AccessToken  = CodexTokenManagerTests.CreateJwt(TimeSpan.FromHours(1), "acc_test"),
			RefreshToken = "rt-test",
			Transport    = CodexTransport.Sse
		};

		CodexTransportSelector selector = new(options, http, this._wsTransport);

		CodexRequest request = new() { Model = "codex-1" };
		Dictionary<string, string> headers = new() {
			["Authorization"] = "Bearer test"
		};

		await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => {
			await foreach (JsonElement _ in selector.StreamAsync(request, null, headers, cts.Token))
			{ }
		});

		Assert.True(attempts <= 3, "Should have stopped retrying after cancellation");
	}

	// INV-9: Network errors (HttpRequestException) are retried
	[Fact]
	public async Task Network_Error_Is_Retried() {
		int attempts = 0;
		NetworkErrorHandler handler = new(() => attempts++);
		HttpClient http = new(handler);

		CodexOptions options = new() {
			AccessToken  = CodexTokenManagerTests.CreateJwt(TimeSpan.FromHours(1), "acc_test"),
			RefreshToken = "rt-test",
			Transport    = CodexTransport.Sse
		};

		CodexTransportSelector selector = new(options, http, this._wsTransport);

		CodexRequest request = new() { Model = "codex-1" };
		Dictionary<string, string> headers = new() {
			["Authorization"] = "Bearer test"
		};

		await Assert.ThrowsAsync<HttpRequestException>(async () => {
			await foreach (JsonElement _ in selector.StreamAsync(request, null, headers))
			{ }
		});

		Assert.Equal(4, attempts); // 1 initial + 3 retries
	}

	public void Dispose() {
		this._wsTransport.Dispose();
		GC.SuppressFinalize(this);
	}

	private sealed class StatusCodeHandler(HttpStatusCode statusCode, Action onRequest) : HttpMessageHandler {
		protected override Task<HttpResponseMessage> SendAsync(
			HttpRequestMessage request, CancellationToken ct) {
			onRequest();
			return Task.FromResult(new HttpResponseMessage(statusCode) {
				Content = new StringContent("{\"error\":\"test\"}", Encoding.UTF8, "application/json")
			});
		}
	}

	private sealed class NetworkErrorHandler(Action onRequest) : HttpMessageHandler {
		protected override Task<HttpResponseMessage> SendAsync(
			HttpRequestMessage request, CancellationToken ct) {
			onRequest();
			throw new HttpRequestException("Connection refused");
		}
	}

	private sealed class FailThenSucceedHandler(int failCount, Action onRequest) : HttpMessageHandler {
		private int _attempt;

		protected override Task<HttpResponseMessage> SendAsync(
			HttpRequestMessage request, CancellationToken ct) {
			onRequest();
			int current = Interlocked.Increment(ref this._attempt);

			if (current <= failCount) {
				return Task.FromResult(new HttpResponseMessage(HttpStatusCode.TooManyRequests) {
					Content = new StringContent("{\"error\":\"rate_limited\"}", Encoding.UTF8, "application/json")
				});
			}

			string sseBody = """
				data: {"type":"response.created","response":{"id":"resp_retry","status":"in_progress"}}

				data: {"type":"response.completed","response":{"id":"resp_retry","status":"completed","output":[],"usage":{"input_tokens":10,"output_tokens":5,"total_tokens":15,"input_tokens_details":{"cached_tokens":0}}}}

				data: [DONE]

				""";

			return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) {
				Content = new StringContent(sseBody, Encoding.UTF8, "text/event-stream")
			});
		}
	}
}
