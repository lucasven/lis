using Lis.Core.Channel;

namespace Lis.Providers.OpenAi.Codex;

public sealed class CodexTokenCounter : ITokenCounter {
	public Task<int?> CountAsync(string requestBodyJson, CancellationToken ct = default) =>
		Task.FromResult<int?>(null);
}
