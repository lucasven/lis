using System.Text.Json.Serialization;

namespace Lis.Providers.OpenAi.Codex;

/// <summary>
/// Body for POST {base}/codex/images/generations and /codex/images/edits.
/// Mirrors the typed Images client in openai/codex (codex-api/src/images.rs).
/// </summary>
public sealed class CodexImageRequest {
	[JsonPropertyName("prompt")]
	public required string Prompt { get; init; }

	[JsonPropertyName("model")]
	public required string Model { get; init; }

	// Only sent on /images/edits
	[JsonPropertyName("images")]
	[JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
	public List<CodexImageUrl>? Images { get; init; }

	[JsonPropertyName("n")]
	[JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
	public int? N { get; init; }

	[JsonPropertyName("size")]
	[JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
	public string? Size { get; init; }

	[JsonPropertyName("quality")]
	[JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
	public string? Quality { get; init; }

	[JsonPropertyName("background")]
	[JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
	public string? Background { get; init; }
}

public sealed record CodexImageUrl([property: JsonPropertyName("image_url")] string ImageUrl);

public sealed class CodexImageResponse {
	[JsonPropertyName("created")]
	public long Created { get; init; }

	[JsonPropertyName("data")]
	public List<CodexImageData>? Data { get; init; }

	[JsonPropertyName("output_format")]
	public string? OutputFormat { get; init; }
}

public sealed class CodexImageData {
	[JsonPropertyName("b64_json")]
	public string? B64Json { get; init; }
}
