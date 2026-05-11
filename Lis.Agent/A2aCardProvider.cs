using Lis.Core.A2A;
using Lis.Core.Util;
using Lis.Persistence;
using Lis.Persistence.Entities;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Lis.Agent;

public sealed class A2aCardProvider(IServiceScopeFactory scopeFactory) : IAgentCardProvider {

	public IReadOnlyList<AgentCard> ListCards() {
		long? callerAgentId = ToolContext.AgentId;

		using IServiceScope scope = scopeFactory.CreateScope();
		LisDbContext db            = scope.ServiceProvider.GetRequiredService<LisDbContext>();

		List<AgentEntity> agents = db.Agents
			.Include(a => a.PromptSections)
			.Where(a => callerAgentId == null || a.Id != callerAgentId)
			.OrderBy(a => a.Name)
			.ToList();

		return agents.Select(ToCard).ToList();
	}

	public AgentCard GetCard(string agentName) {
		using IServiceScope scope = scopeFactory.CreateScope();
		LisDbContext db            = scope.ServiceProvider.GetRequiredService<LisDbContext>();

		AgentEntity? agent = db.Agents
			.Include(a => a.PromptSections)
			.FirstOrDefault(a => a.Name == agentName);

		if (agent is null)
			throw new KeyNotFoundException($"Agent '{agentName}' not found.");

		return ToCard(agent);
	}

	private static AgentCard ToCard(AgentEntity agent) {
		List<AgentSkill> skills = agent.PromptSections
			.Where(s => s.IsEnabled && s.Tags is not null && s.Tags.Contains("skill"))
			.Select(s => new AgentSkill {
				Id          = s.Name.ToLowerInvariant().Replace(' ', '-'),
				Name        = s.Name,
				Description = s.Content.Length > 200 ? s.Content[..200] : s.Content,
				Tags        = s.Tags!.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries),
			})
			.ToList();

		return new AgentCard {
			Name        = agent.Name,
			Description = agent.DisplayName ?? agent.Name,
			Skills      = skills,
		};
	}
}
