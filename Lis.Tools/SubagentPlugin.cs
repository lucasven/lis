using System.ComponentModel;
using System.Text;

using Lis.Core.Channel;
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

		await ToolContext.NotifyAsync($"🧠 *Subagent task:*\n{task}", ct);

		SubagentResult result = await runner.RunAsync(new SubagentRequest { Task = task, Model = model }, agentId, ct);

		string output = result.Status == SubagentStatus.Completed
			? result.Result ?? "(no response)"
			: $"[subagent error] {result.Error}";

		await ToolContext.NotifyAsync($"✅ *Subagent result:*\n{output}", ct);

		return output;
	}

	[KernelFunction("subagent_spawn_parallel")]
	[Description("Spawn multiple subagents in parallel. Each runs in an isolated context with full tool access. " +
	             "Results are streamed to the chat as each subagent finishes. " +
	             "Use when you have 2+ independent tasks that don't depend on each other.")]
	[ToolAuthorization(ToolAuthLevel.Open)]
	[ToolSummarization(SummarizationPolicy.Summarize)]
	[Trace("SubagentPlugin > SpawnParallelAsync")]
	public async Task<string> SpawnParallelAsync(
		[Description("List of task descriptions, one per subagent")] List<string> tasks,
		[Description("Optional model override applied to all subagents. " +
		             "Examples: 'claude-haiku-4-5-20251001', 'openai:gpt-4o'. Empty = inherit.")] string? model = null,
		CancellationToken ct = default) {

		if (ToolContext.Depth >= 3)
			return "Maximum subagent nesting depth (3) exceeded";

		if (tasks is not { Count: > 0 })
			return "[subagent error] At least one task is required";

		if (ToolContext.AgentId is not { } agentId)
			return "[subagent error] No agent context available";

		// Capture parent's channel context before subagents null it via AsyncLocal
		IChannelClient? channel = ToolContext.Channel;
		string?         chatId  = ToolContext.ChatId;
		int             total   = tasks.Count;

		// Send each task description to the chat before spawning
		for (int i = 0; i < total; i++)
			await ToolContext.NotifyAsync($"🧠 *Subagent {i + 1}/{total} task:*\n{tasks[i]}", ct);

		SubagentResult[] results = await Task.WhenAll(tasks.Select(async (task, index) => {
			SubagentResult result = await runner.RunAsync(new SubagentRequest { Task = task, Model = model }, agentId, ct);

			// Notify via captured parent channel — ToolContext.Channel is null inside subagent flow
			if (channel is not null && chatId is not null) {
				string emoji  = result.Status == SubagentStatus.Completed ? "✅" : "❌";
				string output = result.Status == SubagentStatus.Completed
					? result.Result ?? "(no response)"
					: $"[error] {result.Error}";
				await channel.SendMessageAsync(chatId, $"{emoji} *Subagent {index + 1}/{total}:*\n{output}", null, ct);
			}

			return result;
		}));

		// Build combined summary for the LLM's context
		StringBuilder sb = new();
		for (int i = 0; i < results.Length; i++) {
			SubagentResult r = results[i];
			sb.AppendLine($"--- Subagent {i + 1}/{total} [{r.Status}] ---");
			sb.AppendLine(r.Status == SubagentStatus.Completed
				? r.Result ?? "(no response)"
				: $"[error] {r.Error}");
			sb.AppendLine();
		}

		return sb.ToString().TrimEnd();
	}
}
