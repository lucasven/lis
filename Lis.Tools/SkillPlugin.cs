using System.ComponentModel;
using System.Diagnostics;
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
	[Description("Install a skill from a GitHub repo URL (github.com/user/repo), raw .md URL, or local file path. Expects a SKILL.md with name/description YAML frontmatter. Assets are copied to workspace. Re-installing an existing name updates it.")]
	[ToolAuthorization(ToolAuthLevel.OwnerOnly)]
	[ToolSummarization(SummarizationPolicy.Summarize)]
	[Trace("SkillPlugin > InstallAsync")]
	public async Task<string> InstallAsync(
		[Description("URL of the skill file")] string url) {
		await ToolContext.NotifyAsync($"📦 Installing skill from {url}");

		// Resolve workspace path for asset storage
		long agentId = ToolContext.AgentId ?? throw new InvalidOperationException("No agent context");
		using IServiceScope scope = scopeFactory.CreateScope();
		LisDbContext db         = scope.ServiceProvider.GetRequiredService<LisDbContext>();
		AgentEntity? agent         = await db.Agents.FindAsync(agentId);
		string workspacePath       = agent?.WorkspacePath ?? Directory.GetCurrentDirectory();

		// Determine source type and resolve skill content
		string body;
		string? sourceRef = url;
		string? assetsDir = null;

		if (IsGitHubRepoUrl(url)) {
			// GitHub repo URL — clone and find SKILL.md
			(body, assetsDir) = await CloneAndResolveSkillAsync(url);
		}
		else if (Uri.TryCreate(url, UriKind.Absolute, out Uri? uri)
				 && (uri.Scheme == "http" || uri.Scheme == "https")) {
			// Direct URL — fetch content
			try {
				using CancellationTokenSource cts    = new(TimeSpan.FromSeconds(10));
				using HttpClient            client = httpClientFactory.CreateClient();
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
		}
		else if (File.Exists(url)) {
			// Local file path
			body = await File.ReadAllTextAsync(url);
			string? parentDir = Path.GetDirectoryName(Path.GetFullPath(url));
			if (parentDir is not null && HasAssets(parentDir))
				assetsDir = parentDir;
		}
		else if (Directory.Exists(url)) {
			// Local directory — find SKILL.md inside
			string? skillFile = FindSkillFile(url);
			if (skillFile is null)
				return $"No SKILL.md found in '{url}' or its .claude/skills/ subdirectories.";
			body = await File.ReadAllTextAsync(skillFile);
			assetsDir = Path.GetDirectoryName(skillFile);
		}
		else {
			return "Source not found — provide a URL (HTTP/HTTPS/GitHub repo) or a local file/directory path.";
		}

		SkillParseResult result = SkillParser.TryParse(body);
		if (!result.IsSuccess)
			return result.Error!;

		ParsedSkill parsed = result.Skill!;

		// Copy assets to workspace if present
		string? installedAssetsPath = null;
		if (assetsDir is not null) {
			installedAssetsPath = Path.Combine(workspacePath, "skills", parsed.Name);
			CopyAssets(assetsDir, installedAssetsPath);
		}

		SkillEntity? existing = await db.Skills
			.FirstOrDefaultAsync(s => s.AgentId == agentId && s.Name == parsed.Name);

		if (existing is not null) {
			existing.Description = parsed.Description;
			existing.Content     = parsed.Content;
			existing.Version     = parsed.Version;
			existing.SourceUrl   = sourceRef;
			existing.AssetsPath  = installedAssetsPath;
			existing.UpdatedAt = DateTimeOffset.UtcNow;
			await db.SaveChangesAsync();
			return $"Skill '{parsed.Name}' updated."
				 + (installedAssetsPath is not null ? $" Assets at {installedAssetsPath}" : "");
		}

		db.Skills.Add(new SkillEntity {
			Name   = parsed.Name,
			Description = parsed.Description,
			Content     = parsed.Content,
			Version     = parsed.Version,
			SourceUrl   = sourceRef,
			AssetsPath  = installedAssetsPath,
			IsEnabled   = true,
			AgentId     = agentId,
			CreatedAt   = DateTimeOffset.UtcNow,
			UpdatedAt   = DateTimeOffset.UtcNow,
		});
		await db.SaveChangesAsync();
		return $"Skill '{parsed.Name}' installed."
			 + (installedAssetsPath is not null ? $" Assets at {installedAssetsPath}" : "");
	}

	[KernelFunction("list")]
	[Description("List installed skills with name, description, and enabled/disabled status. Omit agent to list current agent's skills. Specifying another agent's name requires owner permissions.")]
	[ToolAuthorization(ToolAuthLevel.Open)]
	[ToolSummarization(SummarizationPolicy.Prune)]
	[Trace("SkillPlugin > ListAsync")]
	public async Task<string> ListAsync(
		[Description("Agent name (owner only, defaults to current)")] string? agent = null) {
		using IServiceScope scope = scopeFactory.CreateScope();
		LisDbContext db  = scope.ServiceProvider.GetRequiredService<LisDbContext>();
		long agentId   = await ResolveAgentIdAsync(db, agent);

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
	[Description("Permanently remove an installed skill by name. Use skill-list to find exact names.")]
	[ToolAuthorization(ToolAuthLevel.OwnerOnly)]
	[ToolSummarization(SummarizationPolicy.Summarize)]
	[Trace("SkillPlugin > UninstallAsync")]
	public async Task<string> UninstallAsync(
		[Description("Skill name to remove")] string name) {
		using IServiceScope scope = scopeFactory.CreateScope();
		LisDbContext db    = scope.ServiceProvider.GetRequiredService<LisDbContext>();
		long agentId        = ToolContext.AgentId ?? throw new InvalidOperationException("No agent context");

		SkillEntity? skill = await db.Skills
			.FirstOrDefaultAsync(s => s.AgentId == agentId && s.Name == name);

		if (skill is null)
			return $"Skill '{name}' not found.";

		// Remove assets from workspace
		if (skill.AssetsPath is not null && Directory.Exists(skill.AssetsPath))
			Directory.Delete(skill.AssetsPath, true);

		db.Skills.Remove(skill);
		await db.SaveChangesAsync();
		return $"Skill '{name}' uninstalled.";
	}

	[KernelFunction("enable")]
	[Description("Enable a disabled skill by name. Enabled skills are loaded into the agent's system prompt on every message.")]
	[ToolAuthorization(ToolAuthLevel.OwnerOnly)]
	[ToolSummarization(SummarizationPolicy.Summarize)]
	[Trace("SkillPlugin > EnableAsync")]
	public async Task<string> EnableAsync(
		[Description("Skill name")] string name) {
		using IServiceScope scope = scopeFactory.CreateScope();
		LisDbContext db       = scope.ServiceProvider.GetRequiredService<LisDbContext>();
		long agentId      = ToolContext.AgentId ?? throw new InvalidOperationException("No agent context");

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
	[Description("Disable a skill without removing it. The skill stays installed but won't be loaded into the prompt until re-enabled.")]
	[ToolAuthorization(ToolAuthLevel.OwnerOnly)]
	[ToolSummarization(SummarizationPolicy.Summarize)]
	[Trace("SkillPlugin > DisableAsync")]
	public async Task<string> DisableAsync(
		[Description("Skill name")] string name) {
		using IServiceScope scope = scopeFactory.CreateScope();
		LisDbContext db   = scope.ServiceProvider.GetRequiredService<LisDbContext>();
		long agentId         = ToolContext.AgentId ?? throw new InvalidOperationException("No agent context");

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
	[Description("Get full details of an installed skill: content, metadata, enabled/disabled status, and asset paths.")]
	[ToolAuthorization(ToolAuthLevel.Open)]
	[ToolSummarization(SummarizationPolicy.Prune)]
	[Trace("SkillPlugin > GetAsync")]
	public async Task<string> GetAsync(
		[Description("Skill name")] string name) {
		using IServiceScope scope = scopeFactory.CreateScope();
		LisDbContext db         = scope.ServiceProvider.GetRequiredService<LisDbContext>();
		long agentId   = ToolContext.AgentId ?? throw new InvalidOperationException("No agent context");

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
		if (skill.AssetsPath is not null)
			sb.AppendLine($"Assets: {skill.AssetsPath}");
		sb.AppendLine($"Content length: {skill.Content.Length} chars");
		return sb.ToString().TrimEnd();
	}

	[KernelFunction("use")]
	[Description("Activate a skill for this conversation, loading its full instructions into context. The skill must be installed and enabled (see skill-install, skill-enable).")]
	[ToolAuthorization(ToolAuthLevel.Open)]
	[ToolSummarization(SummarizationPolicy.Summarize)]
	[Trace("SkillPlugin > UseAsync")]
	public async Task<string> UseAsync(
		[Description("Skill name to activate")] string name) {
		using IServiceScope scope = scopeFactory.CreateScope();
		LisDbContext db      = scope.ServiceProvider.GetRequiredService<LisDbContext>();
		long agentId       = ToolContext.AgentId ?? throw new InvalidOperationException("No agent context");

		SkillEntity? skill = await db.Skills
			.FirstOrDefaultAsync(s => s.AgentId == agentId && s.Name == name);

		if (skill is null)
			return $"Skill '{name}' not found.";

		if (!skill.IsEnabled)
			return $"Skill '{name}' is disabled. Enable it first.";

		return skill.Content;
	}

	// --- Private helpers ---

	private static bool IsGitHubRepoUrl(string url) =>
		url.StartsWith("https://github.com/", StringComparison.OrdinalIgnoreCase)
		&& !url.EndsWith(".md", StringComparison.OrdinalIgnoreCase)
		&& !url.Contains("/raw/", StringComparison.OrdinalIgnoreCase);

	private static async Task<(string Content, string? AssetsDir)> CloneAndResolveSkillAsync(string repoUrl) {
		string cleanUrl = repoUrl;
		int treeIdx = repoUrl.IndexOf("/tree/", StringComparison.OrdinalIgnoreCase);
		if (treeIdx > 0)
			cleanUrl = repoUrl[..treeIdx] + ".git";
		else if (!repoUrl.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
			cleanUrl = repoUrl + ".git";

		string tempDir = Path.Combine(Path.GetTempPath(), $"lis-skill-{Guid.NewGuid():N}");
		try {
			using Process git = new() {
				StartInfo = new ProcessStartInfo {
					FileName      = "git",
					Arguments   = $"clone --depth 1 {cleanUrl} {tempDir}",
					RedirectStandardOutput = true,
					RedirectStandardError  = true,
					UseShellExecute        = false,
				}
			};
			git.Start();
			await git.WaitForExitAsync();
			if (git.ExitCode != 0) {
				string stderr = await git.StandardError.ReadToEndAsync();
				throw new InvalidOperationException($"git clone failed: {stderr}");
			}

			string? skillFile = FindSkillFile(tempDir)
				?? throw new FileNotFoundException("No SKILL.md found in cloned repository.");

			string content   = await File.ReadAllTextAsync(skillFile);
			string? assetDir = Path.GetDirectoryName(skillFile);

			if (assetDir is not null)
				ResolveSymlinks(assetDir, tempDir);

			return (content, assetDir);
		}
		catch {
			if (Directory.Exists(tempDir))
				Directory.Delete(tempDir, true);
			throw;
		}
	}

	private static string? FindSkillFile(string rootDir) {
		string direct = Path.Combine(rootDir, "SKILL.md");
		if (File.Exists(direct))
			return direct;

		string claudeSkillsDir = Path.Combine(rootDir, ".claude", "skills");
		if (Directory.Exists(claudeSkillsDir)) {
			foreach (string dir in Directory.GetDirectories(claudeSkillsDir)) {
				string candidate = Path.Combine(dir, "SKILL.md");
				if (File.Exists(candidate))
					return candidate;
			}
		}

		return null;
	}

	private static bool HasAssets(string dir) {
		string[] entries = Directory.GetFileSystemEntries(dir);
		return entries.Length > 1
			   || (entries.Length == 1
				   && !entries[0].EndsWith("SKILL.md", StringComparison.OrdinalIgnoreCase));
	}

	private static void ResolveSymlinks(string dir, string repoRoot) {
		foreach (string entry in Directory.GetFileSystemEntries(dir)) {
			FileSystemInfo info = File.Exists(entry)
				? new FileInfo(entry)
				: new DirectoryInfo(entry);

			if (info.LinkTarget is null) continue;

			string targetPath = Path.GetFullPath(info.LinkTarget, dir);
			if (!targetPath.StartsWith(repoRoot)) continue;

			if (info is FileInfo) {
				File.Delete(entry);
				if (File.Exists(targetPath))
					File.Copy(targetPath, entry);
			}
			else {
				Directory.Delete(entry);
				if (Directory.Exists(targetPath))
					CopyDirectory(targetPath, entry);
			}
		}
	}

	private static void CopyAssets(string sourceDir, string targetDir) {
		if (Directory.Exists(targetDir))
			Directory.Delete(targetDir, true);
		CopyDirectory(sourceDir, targetDir);
	}

	private static void CopyDirectory(string source, string destination) {
		Directory.CreateDirectory(destination);
		foreach (string file in Directory.GetFiles(source))
			File.Copy(file, Path.Combine(destination, Path.GetFileName(file)), true);
		foreach (string dir in Directory.GetDirectories(source)) {
			string dirName = Path.GetFileName(dir);
			if (dirName is ".git") continue;
			CopyDirectory(dir, Path.Combine(destination, dirName));
		}
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
