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
			.Include(a => a.Skills)
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
			.Include(a => a.Skills)
			.FirstOrDefault(a => a.Name == agentName);

		if (agent is null)
			throw new KeyNotFoundException($"Agent '{agentName}' not found.");

		return ToCard(agent);
	}

	private static AgentCard ToCard(AgentEntity agent) {
		List<AgentSkill> skills = agent.Skills
			.Where(s => s.IsEnabled)
			.Select(s => new AgentSkill {
				Id          = s.Name,
				Name        = s.Name,
				Description = s.Description,
			})
			.ToList();

		HashSet<string> skillNames = skills.Select(s => s.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);

		IEnumerable<AgentSkill> legacySkills = agent.PromptSections
			.Where(s => s.IsEnabled && s.Tags is not null && s.Tags.Contains("skill"))
			.Where(s => !skillNames.Contains(s.Name))
			.Select(s => new AgentSkill {
				Id          = s.Name.ToLowerInvariant().Replace(' ', '-'),
				Name        = s.Name,
				Description = s.Content.Length > 200 ? s.Content[..200] : s.Content,
				Tags        = s.Tags!.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries),
			});

		skills.AddRange(legacySkills);

		return new AgentCard {
			Name        = agent.Name,
			Description = agent.DisplayName ?? agent.Name,
			Skills      = skills,
		};
	}
}
