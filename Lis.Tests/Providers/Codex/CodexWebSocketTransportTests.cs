using System.Text.Json.Nodes;

using Lis.Providers.OpenAi.Codex;

namespace Lis.Tests.Providers.Codex;

public class CodexWebSocketTransportTests : IDisposable {
	private readonly CodexWebSocketTransport _sut;

	public CodexWebSocketTransportTests() {
		CodexOptions options = new() {
			AccessToken  = CodexTokenManagerTests.CreateJwt(TimeSpan.FromHours(1), "acc_test"),
			RefreshToken = "rt-test"
		};
		CodexTokenManager tokenManager = new(options, new HttpClient());
		this._sut = new CodexWebSocketTransport(tokenManager, options);
	}

	[Fact]
	public void RecordFailure_Makes_Fallback_Sticky() {
		Assert.False(this._sut.IsSseFallbackActive("session-1"));

		this._sut.RecordFailure("session-1");

		Assert.True(this._sut.IsSseFallbackActive("session-1"));
		Assert.True(this._sut.IsSseFallbackActive("session-1"));
	}

	[Fact]
	public void Fallback_Is_PerSession() {
		this._sut.RecordFailure("session-1");

		Assert.True(this._sut.IsSseFallbackActive("session-1"));
		Assert.False(this._sut.IsSseFallbackActive("session-2"));
	}

	[Fact]
	public void BuildRequestWithDelta_FullContext_When_No_Continuation() {
		CodexRequest body = new() {
			Model = "codex-1",
			Input = JsonNode.Parse("[{\"role\":\"user\"}]")!.AsArray()
		};

		CodexRequest result = this._sut.BuildRequestWithDelta("session-1", body);

		Assert.Null(result.PreviousResponseId);
		Assert.Equal(body.Input.Count, result.Input.Count);
	}

	[Fact]
	public void BuildRequestWithDelta_DeltaOnly_When_Continuation_Matches() {
		CodexRequest originalBody = new() {
			Model = "codex-1",
			Input = JsonNode.Parse("[{\"role\":\"user\",\"content\":\"hello\"}]")!.AsArray()
		};
		JsonArray responseItems = JsonNode.Parse("[{\"type\":\"message\",\"content\":\"hi\"}]")!.AsArray();

		this._sut.StoreContinuation("session-1", originalBody, "resp_123", responseItems);

		CodexRequest newBody = new() {
			Model = "codex-1",
			Input = JsonNode.Parse(
				"[{\"role\":\"user\",\"content\":\"hello\"},{\"type\":\"message\",\"content\":\"hi\"},{\"role\":\"user\",\"content\":\"thanks\"}]")!.AsArray()
		};

		CodexRequest result = this._sut.BuildRequestWithDelta("session-1", newBody);

		Assert.Equal("resp_123", result.PreviousResponseId);
		Assert.Single(result.Input);
	}

	[Fact]
	public void BuildRequestWithDelta_FullContext_When_Model_Changed() {
		CodexRequest originalBody = new() {
			Model = "codex-1",
			Input = JsonNode.Parse("[{\"role\":\"user\",\"content\":\"hello\"}]")!.AsArray()
		};

		this._sut.StoreContinuation("session-1", originalBody, "resp_123", JsonNode.Parse("[]")!.AsArray());

		CodexRequest newBody = new() {
			Model = "gpt-5.5",
			Input = originalBody.Input
		};

		CodexRequest result = this._sut.BuildRequestWithDelta("session-1", newBody);

		Assert.Null(result.PreviousResponseId);
	}

	[Fact]
	public void IdleTtl_Is_FiveMinutes() {
		Assert.Equal(TimeSpan.FromMinutes(5), CodexWebSocketTransport.IdleTtl);
	}

	[Fact]
	public void ResolveSseUrl_AppendsCorrectly() {
		Assert.Equal("https://chatgpt.com/backend-api/codex/responses",
			CodexWebSocketTransport.ResolveSseUrl("https://chatgpt.com/backend-api"));

		Assert.Equal("https://chatgpt.com/backend-api/codex/responses",
			CodexWebSocketTransport.ResolveSseUrl("https://chatgpt.com/backend-api/codex"));

		Assert.Equal("https://chatgpt.com/backend-api/codex/responses",
			CodexWebSocketTransport.ResolveSseUrl("https://chatgpt.com/backend-api/codex/responses"));
	}

	[Fact]
	public void ResolveWebSocketUrl_SwapsProtocol() {
		Assert.Equal("wss://chatgpt.com/backend-api/codex/responses",
			CodexWebSocketTransport.ResolveWebSocketUrl("https://chatgpt.com/backend-api"));

		Assert.Equal("ws://localhost:8080/codex/responses",
			CodexWebSocketTransport.ResolveWebSocketUrl("http://localhost:8080"));
	}

	public void Dispose() {
		this._sut.Dispose();
		GC.SuppressFinalize(this);
	}
}
