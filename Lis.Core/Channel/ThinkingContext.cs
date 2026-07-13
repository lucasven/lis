using System.Text.Json.Nodes;

namespace Lis.Core.Channel;

/// <summary>
/// Per-request thinking config flowed via AsyncLocal so a DelegatingHandler in the
/// provider can inject fields into the HTTP request body without the agent layer
/// needing a provider reference. The SK→M.E.AI bridge does NOT forward
/// PromptExecutionSettings.ExtensionData["thinking"] to the wire, so this is the
/// cleanest provider-agnostic path.
///
/// Set <see cref="Pending"/>.Value to a <see cref="JsonObject"/> of top-level
/// Messages API fields (e.g. "thinking", "output_config") just before calling the
/// LLM. The provider's HTTP handler merges each field into the request body.
/// Null means "no thinking" — the handler is a no-op.
/// </summary>
public static class ThinkingContext {
	public static readonly AsyncLocal<JsonObject?> Pending = new();
}
