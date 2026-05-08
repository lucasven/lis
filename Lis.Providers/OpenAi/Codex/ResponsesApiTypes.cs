using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace Lis.Providers.OpenAi.Codex;

public sealed class CodexRequest {
	[JsonPropertyName("model")]
	public required string Model { get; init; }

	[JsonPropertyName("store")]
	public bool Store { get; init; } = false;

	[JsonPropertyName("stream")]
	public bool Stream { get; init; } = true;

	[JsonPropertyName("instructions")]
	[JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
	public string? Instructions { get; init; }

	[JsonPropertyName("input")]
	public JsonArray Input { get; init; } = [];

	[JsonPropertyName("tools")]
	[JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
	public List<CodexTool>? Tools { get; init; }

	[JsonPropertyName("tool_choice")]
	public string ToolChoice { get; init; } = "auto";

	[JsonPropertyName("parallel_tool_calls")]
	public bool ParallelToolCalls { get; init; } = true;

	[JsonPropertyName("text")]
	[JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
	public CodexTextOptions? Text { get; init; }

	[JsonPropertyName("reasoning")]
	[JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
	public CodexReasoningOptions? Reasoning { get; init; }

	[JsonPropertyName("include")]
	[JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
	public List<string>? Include { get; init; }

	[JsonPropertyName("prompt_cache_key")]
	[JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
	public string? PromptCacheKey { get; init; }

	[JsonPropertyName("previous_response_id")]
	[JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
	public string? PreviousResponseId { get; init; }
}

public sealed class CodexTool {
	[JsonPropertyName("type")]
	public string Type { get; init; } = "function";

	[JsonPropertyName("name")]
	public required string Name { get; init; }

	[JsonPropertyName("description")]
	[JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
	public string? Description { get; init; }

	[JsonPropertyName("parameters")]
	[JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
	public JsonElement? Parameters { get; init; }

	[JsonPropertyName("strict")]
	[JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
	public bool? Strict { get; init; }
}

public sealed class CodexTextOptions {
	[JsonPropertyName("verbosity")]
	public string Verbosity { get; init; } = "low";
}

public sealed class CodexReasoningOptions {
	[JsonPropertyName("effort")]
	[JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
	public string? Effort { get; init; }

	[JsonPropertyName("summary")]
	[JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
	public string? Summary { get; init; } = "auto";
}
