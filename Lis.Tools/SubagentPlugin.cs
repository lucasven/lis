using System.ComponentModel;

using Lis.Core.Subagents;
using Lis.Core.Util;

using Microsoft.SemanticKernel;

namespace Lis.Tools;

public sealed class SubagentPlugin(ISubagentRunner runner) {

	[KernelFunction("subagent_spawn")]
	[Description("Spawn an ephemeral subagent to execute a focused task in an isolated context. " +
	             "The subagent has full tool access but cannot message the user. " +
	             "Returns only the final result. Use for delegation and parallel subtasks.")]
	[ToolAuthorization(ToolAuthLevel.Open)]
	[ToolSummarization(SummarizationPolicy.Summarize)]
	public async Task<string> SpawnAsync(
		[Description("Task description for the subagent")] string task,
		[Description("Optional model override. Examples: 'claude-haiku-4-5-20251001' (same provider), " +
		             "'openai:gpt-4o' (cross-provider). Empty = inherit parent's model.")] string? model = null,
		CancellationToken ct = default) {

		if (ToolContext.Depth >= 3)
			return "Maximum subagent nesting depth (3) exceeded";

		if (string.IsNullOrWhiteSpace(task))
			return "[subagent error] Task description is required";

		if (ToolContext.AgentId is not { } agentId)
			return "[subagent error] No agent context available";

		SubagentResult result = await runner.RunAsync(new SubagentRequest { Task = task, Model = model }, agentId, ct);

		return result.Status == SubagentStatus.Completed
			? result.Result ?? "(no response)"
			: $"[subagent error] {result.Error}";
	}
}
