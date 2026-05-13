using System.ComponentModel;
using System.Text;

using Lis.Core.Util;
using Lis.Persistence;
using Lis.Persistence.Entities;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.SemanticKernel;

namespace Lis.Tools;

public sealed class SkillPlugin(IServiceScopeFactory scopeFactory, IHttpClientFactory httpClientFactory) {

	[KernelFunction("install")]
	[Description("Install a skill from a URL. Fetches, validates, and stores the skill.")]
	[ToolAuthorization(ToolAuthLevel.OwnerOnly)]
	[ToolSummarization(SummarizationPolicy.Summarize)]
	[Trace("SkillPlugin > InstallAsync")]
	public async Task<string> InstallAsync(
		[Description("URL of the skill file")] string url) {
		await ToolContext.NotifyAsync($"📦 Installing skill from {url}");

		if (!Uri.TryCreate(url, UriKind.Absolute, out Uri? uri)
			|| (uri.Scheme != "http" && uri.Scheme != "https"))
			return "Invalid URL — only HTTP and HTTPS URLs are supported.";

		string body;
		try {
			using CancellationTokenSource cts    = new(TimeSpan.FromSeconds(10));
			using HttpClient              client = httpClientFactory.CreateClient();
			using HttpResponseMessage     resp   = await client.GetAsync(uri, cts.Token);
			resp.EnsureSuccessStatusCode();
			body = await resp.Content.ReadAsStringAsync(cts.Token);
		}
		catch (TaskCanceledException) {
			return "Request timed out after 10 seconds.";
		}
		catch (HttpRequestException ex) {
			return $"Failed to fetch skill: {ex.Message}";
		}

		SkillParseResult result = SkillParser.TryParse(body);
		if (!result.IsSuccess)
			return result.Error!;

		ParsedSkill parsed = result.Skill!;

		using IServiceScope scope = scopeFactory.CreateScope();
		LisDbContext db            = scope.ServiceProvider.GetRequiredService<LisDbContext>();
		long agentId               = ToolContext.AgentId ?? throw new InvalidOperationException("No agent context");

		SkillEntity? existing = await db.Skills
			.FirstOrDefaultAsync(s => s.AgentId == agentId && s.Name == parsed.Name);

		if (existing is not null) {
			existing.Description = parsed.Description;
			existing.Content     = parsed.Content;
			existing.Version     = parsed.Version;
			existing.SourceUrl   = url;
			existing.UpdatedAt   = DateTimeOffset.UtcNow;
			await db.SaveChangesAsync();
			return $"Skill '{parsed.Name}' updated.";
		}

		db.Skills.Add(new SkillEntity {
			Name        = parsed.Name,
			Description = parsed.Description,
			Content     = parsed.Content,
			Version     = parsed.Version,
			SourceUrl   = url,
			IsEnabled   = true,
			AgentId     = agentId,
			CreatedAt   = DateTimeOffset.UtcNow,
			UpdatedAt   = DateTimeOffset.UtcNow,
		});
		await db.SaveChangesAsync();
		return $"Skill '{parsed.Name}' installed.";
	}

	[KernelFunction("list")]
	[Description("List installed skills. Optionally specify an agent name (owner only).")]
	[ToolAuthorization(ToolAuthLevel.Open)]
	[ToolSummarization(SummarizationPolicy.Prune)]
	[Trace("SkillPlugin > ListAsync")]
	public async Task<string> ListAsync(
		[Description("Agent name (owner only, defaults to current)")] string? agent = null) {
		using IServiceScope scope = scopeFactory.CreateScope();
		LisDbContext db            = scope.ServiceProvider.GetRequiredService<LisDbContext>();
		long agentId               = await ResolveAgentIdAsync(db, agent);

		List<SkillEntity> skills = await db.Skills
			.Where(s => s.AgentId == agentId)
			.OrderBy(s => s.Name)
			.ToListAsync();

		if (skills.Count == 0)
			return "No skills installed.";

		StringBuilder sb = new();
		foreach (SkillEntity skill in skills) {
			string status = skill.IsEnabled ? "enabled" : "disabled";
			sb.AppendLine($"- {skill.Name}: {skill.Description} [{status}]");
		}
		return sb.ToString().TrimEnd();
	}

	[KernelFunction("uninstall")]
	[Description("Uninstall a skill by name.")]
	[ToolAuthorization(ToolAuthLevel.OwnerOnly)]
	[ToolSummarization(SummarizationPolicy.Summarize)]
	[Trace("SkillPlugin > UninstallAsync")]
	public async Task<string> UninstallAsync(
		[Description("Skill name to remove")] string name) {
		using IServiceScope scope = scopeFactory.CreateScope();
		LisDbContext db            = scope.ServiceProvider.GetRequiredService<LisDbContext>();
		long agentId               = ToolContext.AgentId ?? throw new InvalidOperationException("No agent context");

		SkillEntity? skill = await db.Skills
			.FirstOrDefaultAsync(s => s.AgentId == agentId && s.Name == name);

		if (skill is null)
			return $"Skill '{name}' not found.";

		db.Skills.Remove(skill);
		await db.SaveChangesAsync();
		return $"Skill '{name}' uninstalled.";
	}

	[KernelFunction("enable")]
	[Description("Enable a disabled skill.")]
	[ToolAuthorization(ToolAuthLevel.OwnerOnly)]
	[ToolSummarization(SummarizationPolicy.Summarize)]
	[Trace("SkillPlugin > EnableAsync")]
	public async Task<string> EnableAsync(
		[Description("Skill name")] string name) {
		using IServiceScope scope = scopeFactory.CreateScope();
		LisDbContext db            = scope.ServiceProvider.GetRequiredService<LisDbContext>();
		long agentId               = ToolContext.AgentId ?? throw new InvalidOperationException("No agent context");

		SkillEntity? skill = await db.Skills
			.FirstOrDefaultAsync(s => s.AgentId == agentId && s.Name == name);

		if (skill is null)
			return $"Skill '{name}' not found.";

		skill.IsEnabled = true;
		skill.UpdatedAt = DateTimeOffset.UtcNow;
		await db.SaveChangesAsync();
		return $"Skill '{name}' enabled.";
	}

	[KernelFunction("disable")]
	[Description("Disable a skill without uninstalling it.")]
	[ToolAuthorization(ToolAuthLevel.OwnerOnly)]
	[ToolSummarization(SummarizationPolicy.Summarize)]
	[Trace("SkillPlugin > DisableAsync")]
	public async Task<string> DisableAsync(
		[Description("Skill name")] string name) {
		using IServiceScope scope = scopeFactory.CreateScope();
		LisDbContext db            = scope.ServiceProvider.GetRequiredService<LisDbContext>();
		long agentId               = ToolContext.AgentId ?? throw new InvalidOperationException("No agent context");

		SkillEntity? skill = await db.Skills
			.FirstOrDefaultAsync(s => s.AgentId == agentId && s.Name == name);

		if (skill is null)
			return $"Skill '{name}' not found.";

		skill.IsEnabled = false;
		skill.UpdatedAt = DateTimeOffset.UtcNow;
		await db.SaveChangesAsync();
		return $"Skill '{name}' disabled.";
	}

	[KernelFunction("get")]
	[Description("Get details of an installed skill.")]
	[ToolAuthorization(ToolAuthLevel.Open)]
	[ToolSummarization(SummarizationPolicy.Prune)]
	[Trace("SkillPlugin > GetAsync")]
	public async Task<string> GetAsync(
		[Description("Skill name")] string name) {
		using IServiceScope scope = scopeFactory.CreateScope();
		LisDbContext db            = scope.ServiceProvider.GetRequiredService<LisDbContext>();
		long agentId               = ToolContext.AgentId ?? throw new InvalidOperationException("No agent context");

		SkillEntity? skill = await db.Skills
			.FirstOrDefaultAsync(s => s.AgentId == agentId && s.Name == name);

		if (skill is null)
			return $"Skill '{name}' not found.";

		string status = skill.IsEnabled ? "enabled" : "disabled";
		StringBuilder sb = new();
		sb.AppendLine($"Name: {skill.Name}");
		sb.AppendLine($"Description: {skill.Description}");
		sb.AppendLine($"Version: {skill.Version}");
		sb.AppendLine($"Status: {status}");
		if (skill.SourceUrl is not null)
			sb.AppendLine($"Source: {skill.SourceUrl}");
		sb.AppendLine($"Content length: {skill.Content.Length} chars");
		return sb.ToString().TrimEnd();
	}

	[KernelFunction("use")]
	[Description("Activate a skill, loading its full instructions into the conversation.")]
	[ToolAuthorization(ToolAuthLevel.Open)]
	[ToolSummarization(SummarizationPolicy.Summarize)]
	[Trace("SkillPlugin > UseAsync")]
	public async Task<string> UseAsync(
		[Description("Skill name to activate")] string name) {
		using IServiceScope scope = scopeFactory.CreateScope();
		LisDbContext db            = scope.ServiceProvider.GetRequiredService<LisDbContext>();
		long agentId               = ToolContext.AgentId ?? throw new InvalidOperationException("No agent context");

		SkillEntity? skill = await db.Skills
			.FirstOrDefaultAsync(s => s.AgentId == agentId && s.Name == name);

		if (skill is null)
			return $"Skill '{name}' not found.";

		if (!skill.IsEnabled)
			return $"Skill '{name}' is disabled. Enable it first.";

		return skill.Content;
	}

	private static async Task<long> ResolveAgentIdAsync(LisDbContext db, string? agent) {
		if (agent is null or { Length: 0 })
			return ToolContext.AgentId ?? throw new InvalidOperationException("No agent context");

		if (!ToolContext.IsOwner)
			throw new UnauthorizedAccessException("Only the owner can access other agents' skills.");

		AgentEntity? target = await db.Agents.FirstOrDefaultAsync(a => a.Name == agent);
		if (target is null) throw new ArgumentException($"Agent '{agent}' not found.");
		return target.Id;
	}
}
