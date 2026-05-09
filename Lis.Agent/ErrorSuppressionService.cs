using System.Collections.Concurrent;

namespace Lis.Agent;

public sealed class ErrorSuppressionService {
	private readonly ConcurrentDictionary<long, string> _suppressed = new();

	public bool ShouldNotify(long agentId, string errorKey) =>
		!this._suppressed.TryGetValue(agentId, out string? current) || current != errorKey;

	public void Suppress(long agentId, string errorKey) =>
		this._suppressed[agentId] = errorKey;

	public void Clear(long agentId) =>
		this._suppressed.TryRemove(agentId, out _);
}
