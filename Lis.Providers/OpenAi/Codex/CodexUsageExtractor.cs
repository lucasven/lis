using Lis.Core.Channel;

namespace Lis.Providers.OpenAi.Codex;

public sealed class CodexUsageExtractor : IUsageExtractor {
	public TokenUsage? Extract(IReadOnlyDictionary<string, object?>? metadata) {
		if (metadata is null) return null;

		if (!metadata.TryGetValue("codex.input_tokens", out object? inputObj)
		    || !metadata.TryGetValue("codex.output_tokens", out object? outputObj))
			return null;

		int inputTokens  = Convert.ToInt32(inputObj);
		int outputTokens = Convert.ToInt32(outputObj);

		if (inputTokens == 0 && outputTokens == 0) return null;

		int cachedTokens = metadata.TryGetValue("codex.cached_tokens", out object? cacheObj)
			? Convert.ToInt32(cacheObj)
			: 0;

		// INV-7: InputTokens + CacheReadTokens == raw input_tokens
		return new TokenUsage(
			InputTokens: inputTokens - cachedTokens,
			OutputTokens: outputTokens,
			CacheReadTokens: cachedTokens,
			CacheCreationTokens: 0,
			ThinkingTokens: 0);
	}
}
