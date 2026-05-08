namespace Lis.Core.Channel;

/// <summary>
/// Implemented by AI provider clients that support session-scoped optimizations
/// (prompt caching, connection reuse, delta context).
/// </summary>
public interface ISessionAware {
	string? SessionId { get; set; }
}
