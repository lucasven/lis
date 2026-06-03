using System.ComponentModel;

using Lis.Core.Channel;
using Lis.Core.Util;
using Lis.Persistence;
using Lis.Persistence.Entities;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.SemanticKernel;

namespace Lis.Tools;

public sealed class SendFilePlugin(IServiceScopeFactory scopeFactory) {

	[KernelFunction("send_file")]
	[Description("Send a file from the workspace to the current chat. Path is relative to workspace (e.g. 'screenshots/page.png') or absolute within the workspace directory.")]
	[ToolSummarization(SummarizationPolicy.Prune)]
	[ToolAuthorization(ToolAuthLevel.OwnerOnly)]
	public async Task<string> SendFileAsync(
		[Description("File path relative to workspace")] string path,
		[Description("Optional message to send with the file")] string? caption = null,
		[Description("MIME type override (auto-detected from extension if omitted)")] string? mimeType = null) {

		if (ToolContext.Channel is null || ToolContext.ChatId is null)
			return "Error: no channel context available.";

		string resolved = await this.ResolveSafePathAsync(path);
		string relativePath = Path.GetRelativePath(await this.ResolveWorkspacePathAsync(), resolved);
		await ToolContext.NotifyAsync($"📎 Sending file\n{relativePath}");

		if (!File.Exists(resolved))
			return $"Error: file not found: {relativePath}";

		byte[] data = await File.ReadAllBytesAsync(resolved);
		string resolvedMime = ResolveMimeType(resolved, mimeType);

		MediaUpload media = new(data, resolvedMime, Path.GetFileName(resolved));

		string? messageId = await ToolContext.Channel.SendFileAsync(
			ToolContext.ChatId, media, caption, ct: CancellationToken.None);

		return messageId is not null
			? $"File sent: {relativePath} ({FormatSize(data.Length)})"
			: $"File sent: {relativePath} (no message ID returned)";
	}

	internal static string ResolveMimeType(string filePath, string? mimeTypeOverride) {
		if (mimeTypeOverride is { Length: > 0 }) return mimeTypeOverride;

		if (MimeTypes.TryGetMimeType(filePath, out string? detected))
			return detected;

		return "application/octet-stream";
	}

	private async Task<string> ResolveWorkspacePathAsync() {
		long agentId = ToolContext.AgentId ?? throw new InvalidOperationException("No agent context");
		using IServiceScope scope = scopeFactory.CreateScope();
		LisDbContext db = scope.ServiceProvider.GetRequiredService<LisDbContext>();

		AgentEntity? agent = await db.Agents.FindAsync(agentId);
		return agent?.WorkspacePath ?? Directory.GetCurrentDirectory();
	}

	private async Task<string> ResolveSafePathAsync(string userPath) {
		string workspace = await this.ResolveWorkspacePathAsync();
		string resolved = Path.GetFullPath(userPath, workspace);
		if (!resolved.StartsWith(workspace, StringComparison.OrdinalIgnoreCase))
			throw new UnauthorizedAccessException($"Path outside workspace: {userPath}");
		if (File.Exists(resolved)) {
			string? linkTarget = File.ResolveLinkTarget(resolved, true)?.FullName;
			if (linkTarget is not null && !linkTarget.StartsWith(workspace, StringComparison.OrdinalIgnoreCase))
				throw new UnauthorizedAccessException("Symlink target outside workspace");
		}
		return resolved;
	}

	private static string FormatSize(long bytes) => bytes switch {
		>= 1_048_576 => $"{bytes / 1_048_576.0:F1} MB",
		>= 1_024     => $"{bytes / 1_024.0:F1} KB",
		_             => $"{bytes} B",
	};
}
