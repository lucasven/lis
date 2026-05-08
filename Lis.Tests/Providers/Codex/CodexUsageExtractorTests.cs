using Lis.Core.Channel;
using Lis.Providers.OpenAi.Codex;

namespace Lis.Tests.Providers.Codex;

public class CodexUsageExtractorTests {
	private readonly CodexUsageExtractor _sut = new();

	[Theory]
	[InlineData(500, 200, 0,   500, 200, 0)]
	[InlineData(500, 200, 300, 200, 200, 300)]
	[InlineData(500, 100, 500, 0,   100, 500)]
	public void Extract_Maps_Correctly(
		int inputTokens, int outputTokens, int cachedTokens,
		int expectedInput, int expectedOutput, int expectedCached) {
		Dictionary<string, object?> metadata = new() {
			["codex.input_tokens"]  = inputTokens,
			["codex.output_tokens"] = outputTokens,
			["codex.cached_tokens"] = cachedTokens
		};

		TokenUsage? result = this._sut.Extract(metadata);

		Assert.NotNull(result);
		Assert.Equal(expectedInput, result!.InputTokens);
		Assert.Equal(expectedOutput, result.OutputTokens);
		Assert.Equal(expectedCached, result.CacheReadTokens);
		Assert.Equal(0, result.CacheCreationTokens);
		Assert.Equal(0, result.ThinkingTokens);

		// INV-7: InputTokens + CacheReadTokens == raw input_tokens
		Assert.Equal(inputTokens, result.InputTokens + result.CacheReadTokens);
	}

	[Fact]
	public void Extract_NullMetadata_ReturnsNull() {
		TokenUsage? result = this._sut.Extract(null);
		Assert.Null(result);
	}

	[Fact]
	public void Extract_ZeroTokens_ReturnsNull() {
		Dictionary<string, object?> metadata = new() {
			["codex.input_tokens"]  = 0,
			["codex.output_tokens"] = 0,
			["codex.cached_tokens"] = 0
		};

		TokenUsage? result = this._sut.Extract(metadata);
		Assert.Null(result);
	}

	[Fact]
	public void Extract_MissingKeys_ReturnsNull() {
		Dictionary<string, object?> metadata = new() { ["unrelated"] = 42 };
		TokenUsage? result = this._sut.Extract(metadata);
		Assert.Null(result);
	}
}
