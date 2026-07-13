using System.Text.Json.Nodes;

using Anthropic.SDK.Messaging;

using Lis.Agent;
using Lis.Providers.Anthropic;

using Microsoft.Extensions.Logging.Abstractions;

namespace Lis.Tests.Agent;

public class ThinkingConfigTests {
	private static (JsonObject? Fields, int MinMaxTokens) Build(string model, string effort) =>
		ConversationService.BuildThinkingFields(model, effort, NullLogger<ConversationService>.Instance);

	// ── Adaptive thinking (Claude 4.6+) ─────────────────────────────

	[Theory]
	[InlineData("claude-opus-4-6")]
	[InlineData("claude-opus-4-7")]
	[InlineData("claude-opus-4-8")]
	[InlineData("claude-sonnet-4-6")]
	[InlineData("claude-fable-5")]
	public void Adaptive_models_get_adaptive_thinking_with_top_level_output_config(string model) {
		(JsonObject? fields, _) = Build(model, "high");

		Assert.NotNull(fields);
		Assert.Equal("adaptive",   fields["thinking"]?["type"]?.GetValue<string>());
		Assert.Equal("summarized", fields["thinking"]?["display"]?.GetValue<string>());
		Assert.Equal("high",       fields["output_config"]?["effort"]?.GetValue<string>());
		Assert.Null(fields["thinking"]?["budget_tokens"]);
	}

	[Theory]
	[InlineData("xhigh")]
	[InlineData("max")]
	public void Adaptive_xhigh_and_max_require_64k_max_tokens(string effort) {
		(_, int minMaxTokens) = Build("claude-opus-4-7", effort);
		Assert.Equal(64000, minMaxTokens);
	}

	[Fact]
	public void Adaptive_unknown_effort_falls_back_to_high() {
		(JsonObject? fields, _) = Build("claude-opus-4-8", "bogus");
		Assert.Equal("high", fields?["output_config"]?["effort"]?.GetValue<string>());
	}

	// ── Fixed-budget thinking (older models) ────────────────────────

	[Theory]
	[InlineData("low",    1024)]
	[InlineData("medium", 4096)]
	[InlineData("high",   16000)]
	[InlineData("12345",  12345)]
	public void Budget_models_get_enabled_thinking_with_budget_tokens(string effort, int expectedBudget) {
		(JsonObject? fields, int minMaxTokens) = Build("claude-sonnet-4-20250514", effort);

		Assert.NotNull(fields);
		Assert.Equal("enabled",      fields["thinking"]?["type"]?.GetValue<string>());
		Assert.Equal(expectedBudget, fields["thinking"]?["budget_tokens"]?.GetValue<int>());
		Assert.Null(fields["output_config"]);
		// API constraint: max_tokens must exceed budget_tokens
		Assert.True(minMaxTokens > expectedBudget);
	}

	[Fact]
	public void Budget_max_effort_is_model_dependent() {
		Assert.Equal(64000, Build("claude-opus-4-1",           "max").Fields?["thinking"]?["budget_tokens"]?.GetValue<int>());
		Assert.Equal(32000, Build("claude-sonnet-4-20250514",  "max").Fields?["thinking"]?["budget_tokens"]?.GetValue<int>());
		Assert.Equal(8192,  Build("claude-haiku-4-5-20251001", "max").Fields?["thinking"]?["budget_tokens"]?.GetValue<int>());
	}

	[Fact]
	public void Xhigh_on_non_adaptive_model_disables_thinking() {
		(JsonObject? fields, int minMaxTokens) = Build("claude-sonnet-4-20250514", "xhigh");
		Assert.Null(fields);
		Assert.Equal(0, minMaxTokens);
	}

	// ── Reasoning extraction ────────────────────────────────────────

	[Fact]
	public void Extractor_returns_thinking_delta_text() {
		AnthropicReasoningExtractor sut = new();
		MessageResponse chunk = new() { Delta = new Delta { Type = "thinking_delta", Thinking = "hmm" } };

		Assert.Equal("hmm", sut.ExtractReasoningDelta(chunk));
	}

	[Fact]
	public void Extractor_ignores_text_deltas_and_foreign_content() {
		AnthropicReasoningExtractor sut = new();

		Assert.Null(sut.ExtractReasoningDelta(new MessageResponse { Delta = new Delta { Type = "text_delta", Text = "hi" } }));
		Assert.Null(sut.ExtractReasoningDelta(new MessageResponse()));
		Assert.Null(sut.ExtractReasoningDelta("not a message response"));
		Assert.Null(sut.ExtractReasoningDelta(null));
	}
}
