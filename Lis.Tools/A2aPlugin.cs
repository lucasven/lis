using System.ComponentModel;
using System.Text;

using Lis.Core.A2A;
using Lis.Core.Util;

using Microsoft.SemanticKernel;

namespace Lis.Tools;

public sealed class A2aPlugin(IAgentCardProvider cardProvider, IA2aClient client) {

	[KernelFunction("list_agents")]
	[Description("List all available agents with their name, description, and supported skills. Use this to discover which agent to delegate a task to before calling a2a-send.")]
	[ToolSummarization(SummarizationPolicy.Prune)]
	[ToolAuthorization(ToolAuthLevel.Open)]
	public Task<string> ListAgentsAsync() {
		IReadOnlyList<AgentCard> cards = cardProvider.ListCards();

		if (cards.Count == 0)
			return Task.FromResult("No other agents available.");

		StringBuilder sb = new();
		foreach (AgentCard card in cards) {
			sb.AppendLine($"**{card.Name}** — {card.Description}");
			foreach (AgentSkill skill in card.Skills)
				sb.AppendLine($"  - {skill.Name}: {skill.Description}");
		}

		return Task.FromResult(sb.ToString().TrimEnd());
	}

	[KernelFunction("get_agent")]
	[Description("Get detailed info about a specific agent by name, including its full skill list and capabilities. Use a2a-list_agents first to find agent names.")]
	[ToolSummarization(SummarizationPolicy.Prune)]
	[ToolAuthorization(ToolAuthLevel.Open)]
	public Task<string> GetAgentAsync(
		[Description("Name of the agent to look up")] string name) {
		try {
			AgentCard card = cardProvider.GetCard(name);

			StringBuilder sb = new();
			sb.AppendLine($"**{card.Name}** v{card.Version} (protocol {card.ProtocolVersion})");
			sb.AppendLine($"Description: {card.Description}");
			sb.AppendLine($"Input: {string.Join(", ", card.DefaultInputModes)}");
			sb.AppendLine($"Output: {string.Join(", ", card.DefaultOutputModes)}");

			if (card.Skills.Count > 0) {
				sb.AppendLine("Skills:");
				foreach (AgentSkill skill in card.Skills)
					sb.AppendLine($"  - {skill.Name}: {skill.Description}");
			}

			return Task.FromResult(sb.ToString().TrimEnd());
		} catch (KeyNotFoundException) {
			return Task.FromResult($"Agent '{name}' not found.");
		}
	}

	[KernelFunction("send")]
	[Description("Send a message to another agent by name and get their response. The target agent processes the request independently with its own tools and context. Use a2a-list_agents to discover available agents first.")]
	[ToolSummarization(SummarizationPolicy.Summarize)]
	[ToolAuthorization(ToolAuthLevel.Open)]
	public async Task<string> SendAsync(
		[Description("Name of the target agent")] string targetAgent,
		[Description("The message text to send")] string message,
		CancellationToken ct = default) {

		A2aMessage a2aMsg = new() {
			MessageId = Guid.NewGuid().ToString(),
			Role      = "user",
			Parts     = [new TextPart { Text = message }],
		};

		try {
			A2aTask task = await client.SendMessageAsync(targetAgent, a2aMsg, ct);

			if (task.Status.State == A2aTaskState.Failed) {
				string error = task.Status.Message?.Parts
					.OfType<TextPart>()
					.FirstOrDefault()?.Text ?? "Unknown error";
				return $"Agent '{targetAgent}' failed: {error}";
			}

			if (task.Artifacts is not { Count: > 0 })
				return "(no response from agent)";

			StringBuilder sb = new();
			foreach (A2aArtifact artifact in task.Artifacts)
				foreach (Part part in artifact.Parts)
					if (part is TextPart text)
						sb.AppendLine(text.Text);

			return sb.ToString().TrimEnd();
		} catch (KeyNotFoundException) {
			return $"Agent '{targetAgent}' not found. Use list_agents to see available agents.";
		}
	}
}
