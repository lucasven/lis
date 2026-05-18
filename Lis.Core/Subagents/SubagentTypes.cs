namespace Lis.Core.Subagents;

// -- Request / Result --------------------------------------------------------

public sealed record SubagentRequest {
	/// <summary>Task description sent as the user message to the subagent.</summary>
	public required string Task { get; init; }

	/// <summary>
	/// Optional model override. Three forms:
	///   - null/empty → inherit parent's model and provider
	///   - "model-name" → use this model with the parent's provider
	///   - "provider:model-name" → use this model with the specified provider's IChatClient
	/// </summary>
	public string? Model { get; init; }
}

public sealed record SubagentResult {
	public required SubagentStatus Status { get; init; }

	/// <summary>Final text produced by the subagent. Null when Status is Failed.</summary>
	public string? Result { get; init; }

	/// <summary>Error message when Status is Failed. Null on success.</summary>
	public string? Error { get; init; }

	/// <summary>Token usage from the subagent's execution. Tracked separately, not aggregated to parent.</summary>
	public SubagentTokenUsage? Usage { get; init; }
}

public sealed record SubagentTokenUsage {
	public int InputTokens  { get; init; }
	public int OutputTokens { get; init; }
}

public enum SubagentStatus {
	Completed,
	Failed,
}

// -- Errors ------------------------------------------------------------------

/// <summary>
/// Thrown internally by ISubagentRunner.RunAsync when nesting depth is exceeded.
/// The SubagentPlugin catches this and returns the error message as a tool result string.
/// Never propagated to the parent's ToolRunner — always caught at the plugin boundary.
/// </summary>
public sealed class SubagentDepthExceededException(int maxDepth)
	: InvalidOperationException($"Maximum subagent nesting depth ({maxDepth}) exceeded");

// -- Service interface -------------------------------------------------------

/// <summary>Runs an ephemeral subagent in an isolated context with the full tool loop.</summary>
public interface ISubagentRunner {
	/// <param name="request">Task description and optional model override.</param>
	/// <param name="agentId">ID of the calling agent (for resolving config, tools, prompt).</param>
	/// <param name="ct">Cancellation token.</param>
	/// <returns>Result containing the subagent's final output or error.</returns>
	Task<SubagentResult> RunAsync(SubagentRequest request, long agentId, CancellationToken ct = default);
}
