namespace Lis.Core.Channel;

/// <summary>
/// Provider-agnostic image generation. When <see cref="ImageGenerationRequest.References"/> is non-empty,
/// implementations edit/compose from those images instead of generating from scratch.
/// </summary>
public interface IImageGenerationService {
	Task<IReadOnlyList<GeneratedImage>> GenerateAsync(ImageGenerationRequest request, CancellationToken ct = default);
}

public sealed record ImageGenerationRequest(string Prompt) {
	/// <summary>e.g. "1024x1024", "1536x1024", "1024x1536" or "auto". Null lets the provider decide.</summary>
	public string? Size { get; init; }

	/// <summary>"low", "medium", "high" or "auto".</summary>
	public string? Quality { get; init; }

	/// <summary>"transparent", "opaque" or "auto".</summary>
	public string? Background { get; init; }

	public int Count { get; init; } = 1;

	public IReadOnlyList<MediaDownload> References { get; init; } = [];
}

public sealed record GeneratedImage(byte[] Data, string MimeType);
