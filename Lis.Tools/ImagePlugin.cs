using System.ComponentModel;
using System.Diagnostics;
using System.Text.RegularExpressions;

using Lis.Core.Channel;
using Lis.Core.Util;
using Lis.Persistence;
using Lis.Persistence.Entities;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.SemanticKernel;

namespace Lis.Tools;

public sealed partial class ImagePlugin(
	IImageGenerationService imageService,
	IServiceScopeFactory    scopeFactory,
	ILogger<ImagePlugin>    logger) {
	private const string OutputDirectory    = "images";
	private const int    MaxCount           = 4;
	private const int    MaxReferences      = 5;
	private const long   MaxReferenceBytes  = 20 * 1024 * 1024;

	private static readonly HashSet<string> Qualities   = new(StringComparer.OrdinalIgnoreCase) { "low", "medium", "high", "auto" };
	private static readonly HashSet<string> Backgrounds = new(StringComparer.OrdinalIgnoreCase) { "transparent", "opaque", "auto" };

	[KernelFunction("generate")]
	[Description("Generate an image from a text prompt (or edit/compose from reference images in the workspace). Saves the result under 'images/' in the workspace and, by default, sends it to the current chat.")]
	[ToolSummarization(SummarizationPolicy.Prune)]
	[ToolAuthorization(ToolAuthLevel.OwnerOnly)]
	[Trace("ImagePlugin > GenerateAsync")]
	public async Task<string> GenerateAsync(
		[Description("Detailed description of the desired image (or of the edit to apply to the references)")] string prompt,
		[Description("Image size: '1024x1024', '1536x1024' (landscape), '1024x1536' (portrait) or 'auto'")] string? size = null,
		[Description("Quality: 'low', 'medium', 'high' or 'auto'")] string? quality = null,
		[Description("Background: 'transparent', 'opaque' or 'auto'")] string? background = null,
		[Description("Comma-separated workspace paths of reference images to edit/compose from (optional)")] string? referencePaths = null,
		[Description("Number of images to generate (1-4)")] int count = 1,
		[Description("Send the generated image(s) to the current chat")] bool send = true,
		[Description("Optional caption sent with the image")] string? caption = null) {

		string? validationError = Validate(prompt, size, quality, background, count);
		if (validationError is not null) return $"Error: {validationError}";

		string workspace = await this.ResolveWorkspacePathAsync();

		List<MediaDownload> references;
		try {
			references = await LoadReferencesAsync(workspace, referencePaths);
		} catch (Exception ex) when (ex is FileNotFoundException or UnauthorizedAccessException or InvalidOperationException) {
			return $"Error: {ex.Message}";
		}

		await ToolContext.NotifyAsync(references.Count > 0
			? $"🎨 Editing image ({references.Count} reference{(references.Count > 1 ? "s" : "")})"
			: "🎨 Generating image");

		ImageGenerationRequest request = new(prompt) {
			Size       = size,
			Quality    = quality,
			Background = background,
			Count      = count,
			References = references
		};

		IReadOnlyList<GeneratedImage> images;
		try {
			images = await imageService.GenerateAsync(request);
		} catch (Exception ex) {
			logger.LogWarning(ex, "Image generation failed");
			Activity.Current?.SetStatus(ActivityStatusCode.Error, ex.Message);
			return $"Image generation error: {ex.Message}";
		}

		List<string> savedPaths = await SaveImagesAsync(workspace, images);
		bool         sent       = send && await SendImagesAsync(images, savedPaths, caption);

		string paths = string.Join(", ", savedPaths);
		return sent
			? $"Generated and sent {images.Count} image(s): {paths}"
			: $"Generated {images.Count} image(s): {paths}";
	}

	internal static string? Validate(string prompt, string? size, string? quality, string? background, int count) {
		if (string.IsNullOrWhiteSpace(prompt)) return "prompt is required.";
		if (count is < 1 or > MaxCount) return $"count must be between 1 and {MaxCount}.";
		if (size is { Length: > 0 } && !size.Equals("auto", StringComparison.OrdinalIgnoreCase) && !SizeRegex().IsMatch(size))
			return "size must be 'auto' or WIDTHxHEIGHT (e.g. '1024x1024').";
		if (quality is { Length: > 0 } && !Qualities.Contains(quality)) return "quality must be one of: low, medium, high, auto.";
		if (background is { Length: > 0 } && !Backgrounds.Contains(background)) return "background must be one of: transparent, opaque, auto.";
		return null;
	}

	internal static List<string> ParseReferencePaths(string? referencePaths) =>
		referencePaths is { Length: > 0 }
			? referencePaths.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList()
			: [];

	private static async Task<List<MediaDownload>> LoadReferencesAsync(string workspace, string? referencePaths) {
		List<string> paths = ParseReferencePaths(referencePaths);
		if (paths.Count > MaxReferences) throw new InvalidOperationException($"at most {MaxReferences} reference images are supported.");

		List<MediaDownload> references = [];
		foreach (string path in paths) {
			string resolved = ResolveSafePath(workspace, path);
			if (!File.Exists(resolved)) throw new FileNotFoundException($"reference image not found: {path}");

			string mime = SendFilePlugin.ResolveMimeType(resolved, null);
			if (!mime.StartsWith("image/", StringComparison.Ordinal)) throw new InvalidOperationException($"reference is not an image: {path}");
			if (new FileInfo(resolved).Length > MaxReferenceBytes) throw new InvalidOperationException($"reference image too large (max 20 MB): {path}");

			references.Add(new MediaDownload(await File.ReadAllBytesAsync(resolved), mime));
		}

		return references;
	}

	private static async Task<List<string>> SaveImagesAsync(string workspace, IReadOnlyList<GeneratedImage> images) {
		string directory = Path.Combine(workspace, OutputDirectory);
		Directory.CreateDirectory(directory);

		string       stamp = DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmss");
		List<string> paths = [];
		for (int i = 0; i < images.Count; i++) {
			string extension = ExtensionFor(images[i].MimeType);
			string suffix    = images.Count > 1 ? $"-{i + 1}" : "";
			string fileName  = $"{stamp}-{Guid.NewGuid().ToString("N")[..6]}{suffix}.{extension}";

			await File.WriteAllBytesAsync(Path.Combine(directory, fileName), images[i].Data);
			paths.Add($"{OutputDirectory}/{fileName}");
		}

		return paths;
	}

	private static async Task<bool> SendImagesAsync(IReadOnlyList<GeneratedImage> images, List<string> paths, string? caption) {
		if (ToolContext.Channel is null || ToolContext.ChatId is null) return false;

		for (int i = 0; i < images.Count; i++) {
			MediaUpload media = new(images[i].Data, images[i].MimeType, Path.GetFileName(paths[i]));
			// Caption only on the first image so multi-image results don't repeat it
			await ToolContext.Channel.SendFileAsync(ToolContext.ChatId, media, i == 0 ? caption : null, ct: CancellationToken.None);
		}

		return true;
	}

	internal static string ExtensionFor(string mimeType) => mimeType switch {
		"image/jpeg" => "jpg",
		"image/webp" => "webp",
		_            => "png"
	};

	private async Task<string> ResolveWorkspacePathAsync() {
		long agentId = ToolContext.AgentId ?? throw new InvalidOperationException("No agent context");
		using IServiceScope scope = scopeFactory.CreateScope();
		LisDbContext db = scope.ServiceProvider.GetRequiredService<LisDbContext>();

		AgentEntity? agent = await db.Agents.FindAsync(agentId);
		return agent?.WorkspacePath ?? Directory.GetCurrentDirectory();
	}

	private static string ResolveSafePath(string workspace, string userPath) {
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

	[GeneratedRegex(@"^\d{2,5}x\d{2,5}$", RegexOptions.IgnoreCase, matchTimeoutMilliseconds: 100)]
	private static partial Regex SizeRegex();
}
