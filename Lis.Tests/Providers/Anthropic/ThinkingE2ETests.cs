using System.Net;
using System.Text;
using System.Text.Json.Nodes;

using Anthropic.SDK;

using Lis.Agent;
using Lis.Core.Channel;
using Lis.Core.Util;
using Lis.Providers.Anthropic;
using Lis.Tools;

using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;

namespace Lis.Tests.Providers;

/// <summary>
/// Wire-level E2E for extended thinking: drives the REAL Anthropic SDK client through the
/// production <see cref="AnthropicProvider.ThinkingInjectionHandler"/> into a fake HTTP handler
/// that captures the request body and replays a canned Anthropic SSE stream. Validates the full
/// pipeline — ThinkingContext → body injection → SDK SSE parsing → SK adapter → ToolRunner
/// thinking extraction + usage extraction — without network or credentials.
/// </summary>
public class ThinkingE2ETests {

	// ── Offline wire tests ──────────────────────────────────────────

	[Fact]
	public async Task Adaptive_thinking_is_injected_and_extracted_end_to_end() {
		SseReplayHandler sse = new(ThinkingSse);

		(List<ChatMessageContent> messages, JsonNode body) =
			await RunPipelineAsync(sse, model: "claude-opus-4-8", effort: "xhigh");

		// Request body: thinking + output_config injected, max_tokens bumped for xhigh
		Assert.Equal("adaptive",   body["thinking"]?["type"]?.GetValue<string>());
		Assert.Equal("summarized", body["thinking"]?["display"]?.GetValue<string>());
		Assert.Equal("xhigh",      body["output_config"]?["effort"]?.GetValue<string>());
		Assert.Equal(64000,        body["max_tokens"]?.GetValue<int>());

		// Response: thinking accumulated in metadata, kept out of the visible text
		ChatMessageContent assistant = Assert.Single(messages);
		Assert.Equal("Hello there!", assistant.Content);
		Assert.Equal("Let me think. Done thinking.", ToolRunner.GetThinking(assistant));

		TokenUsage? usage = ToolRunner.GetUsage(assistant);
		Assert.NotNull(usage);
		Assert.Equal(100, usage.InputTokens);
		Assert.Equal(42,  usage.OutputTokens);
		Assert.Equal(30,  usage.CacheReadTokens);
		Assert.Equal(10,  usage.CacheCreationTokens);
	}

	[Fact]
	public async Task Budget_thinking_is_injected_for_older_models() {
		SseReplayHandler sse = new(ThinkingSse);

		(_, JsonNode body) = await RunPipelineAsync(sse, model: "claude-sonnet-4-20250514", effort: "high");

		Assert.Equal("enabled", body["thinking"]?["type"]?.GetValue<string>());
		Assert.Equal(16000,     body["thinking"]?["budget_tokens"]?.GetValue<int>());
		Assert.Null(body["output_config"]);
		// max_tokens must exceed budget_tokens
		Assert.Equal(16000 + 4096, body["max_tokens"]?.GetValue<int>());
	}

	[Fact]
	public async Task Without_thinking_config_the_body_is_untouched() {
		SseReplayHandler sse = new(ThinkingSse);

		(_, JsonNode body) = await RunPipelineAsync(sse, model: "claude-opus-4-8", effort: null);

		Assert.Null(body["thinking"]);
		Assert.Null(body["output_config"]);
	}

	// ── Pipeline harness ────────────────────────────────────────────

	private static async Task<(List<ChatMessageContent> Messages, JsonNode RequestBody)> RunPipelineAsync(
		SseReplayHandler sse, string model, string? effort) {

		// Real SDK client through the real injection handler, terminating at the fake SSE handler
		AnthropicProvider.ThinkingInjectionHandler chain = new() { InnerHandler = sse };
		AnthropicClient anthropic = new("sk-ant-test", new HttpClient(chain));
		IChatCompletionService chatService = ((IChatClient)anthropic.Messages).AsChatCompletionService();

		int maxTokens = 4096;
		JsonObject? thinkingFields = null;
		if (effort is not null) {
			(thinkingFields, int minMaxTokens) =
				ConversationService.BuildThinkingFields(model, effort, NullLogger<ConversationService>.Instance);
			if (minMaxTokens > maxTokens) maxTokens = minMaxTokens;
		}
		ThinkingContext.Pending.Value = thinkingFields;

		Kernel kernel = Kernel.CreateBuilder().Build();
		ToolAuthRegistry authRegistry = new();
		authRegistry.Build(kernel);
		ToolRunner toolRunner = new(authRegistry, new FakeApprovalService(), NullLogger<ToolRunner>.Instance);

		ChatHistory history = [];
		history.AddUserMessage("hi");

		PromptExecutionSettings settings = new() {
			ModelId       = model,
			ExtensionData = new Dictionary<string, object> { ["max_tokens"] = maxTokens }
		};

		List<ChatMessageContent> messages = [];
		await foreach (ChatMessageContent msg in toolRunner.RunAsync(
			chatService, history, kernel, settings, new AnthropicUsageExtractor(), new AnthropicReasoningExtractor()))
			messages.Add(msg);

		Assert.NotNull(sse.CapturedRequestBody);
		return (messages, JsonNode.Parse(sse.CapturedRequestBody!)!);
	}

	/// <summary>Captures the (post-injection) request body and replays a canned SSE stream.</summary>
	private sealed class SseReplayHandler(string sseBody) : HttpMessageHandler {
		public string? CapturedRequestBody { get; private set; }

		protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) {
			this.CapturedRequestBody = request.Content is not null
				? await request.Content.ReadAsStringAsync(ct)
				: null;
			return new HttpResponseMessage(HttpStatusCode.OK) {
				Content = new StringContent(sseBody, Encoding.UTF8, "text/event-stream")
			};
		}
	}

	private const string ThinkingSse =
		"""
		event: message_start
		data: {"type":"message_start","message":{"id":"msg_test","type":"message","role":"assistant","model":"claude-opus-4-8","content":[],"stop_reason":null,"stop_sequence":null,"usage":{"input_tokens":100,"output_tokens":1}}}

		event: content_block_start
		data: {"type":"content_block_start","index":0,"content_block":{"type":"thinking","thinking":""}}

		event: content_block_delta
		data: {"type":"content_block_delta","index":0,"delta":{"type":"thinking_delta","thinking":"Let me think. "}}

		event: content_block_delta
		data: {"type":"content_block_delta","index":0,"delta":{"type":"thinking_delta","thinking":"Done thinking."}}

		event: content_block_stop
		data: {"type":"content_block_stop","index":0}

		event: content_block_start
		data: {"type":"content_block_start","index":1,"content_block":{"type":"text","text":""}}

		event: content_block_delta
		data: {"type":"content_block_delta","index":1,"delta":{"type":"text_delta","text":"Hello "}}

		event: content_block_delta
		data: {"type":"content_block_delta","index":1,"delta":{"type":"text_delta","text":"there!"}}

		event: content_block_stop
		data: {"type":"content_block_stop","index":1}

		event: message_delta
		data: {"type":"message_delta","delta":{"stop_reason":"end_turn","stop_sequence":null},"usage":{"input_tokens":100,"cache_read_input_tokens":30,"cache_creation_input_tokens":10,"output_tokens":42}}

		event: message_stop
		data: {"type":"message_stop"}

		""";

	private sealed class FakeApprovalService : IApprovalService {
		public Task<ApprovalResult> RequestApprovalAsync(ApprovalRequest request, CancellationToken ct) =>
			Task.FromResult(new ApprovalResult(ApprovalDecision.Once, "auto"));

		public Task<bool> ResolveAsync(string approvalId, ApprovalDecision decision, string senderJid) =>
			Task.FromResult(true);

		public Task<bool> ResolveByMessageAsync(string messageExternalId, ApprovalDecision decision, string senderJid) =>
			Task.FromResult(true);
	}
}

/// <summary>
/// Live tests against the real Anthropic API using the production AddAnthropic() wiring
/// (including OAuth bearer detection and the full handler chain). Opt-in only:
/// <c>LIS_E2E_ANTHROPIC=true dotnet test --filter FullyQualifiedName~ThinkingLiveE2ETests</c>
/// Credentials are read from the repo .env via DotEnv.Load().
/// </summary>
public class ThinkingLiveE2ETests {
	// Read the gate from the process env BEFORE DotEnv.Load so a stray .env entry
	// can't turn every local `dotnet test` into paid API calls.
	private static readonly bool Enabled =
		Environment.GetEnvironmentVariable("LIS_E2E_ANTHROPIC") == "true";

	[Fact]
	public async Task Live_thinking_request_streams_thinking_and_answer() {
		if (!Enabled) return;

		(IChatCompletionService chatService, ToolRunner toolRunner, Kernel kernel,
		 IUsageExtractor usageExtractor, IReasoningExtractor reasoningExtractor, string model) = BuildLiveStack();

		SetThinking(model, "high", maxTokensFloor: 4096, out PromptExecutionSettings settings);
		settings.ModelId = model;

		ChatHistory history = [];
		history.AddUserMessage("Think through this carefully, then answer in one word: what is the capital of France?");

		List<ChatMessageContent> messages = [];
		await foreach (ChatMessageContent msg in toolRunner.RunAsync(
			chatService, history, kernel, settings, usageExtractor, reasoningExtractor))
			messages.Add(msg);

		ChatMessageContent assistant = Assert.Single(messages);
		Assert.False(string.IsNullOrWhiteSpace(assistant.Content));
		Assert.False(string.IsNullOrWhiteSpace(ToolRunner.GetThinking(assistant)),
			"expected thinking deltas from the API — check thinking injection and the reasoning extractor");
		Assert.NotNull(ToolRunner.GetUsage(assistant));
	}

	[Fact]
	public async Task Live_thinking_with_tool_use_completes_the_loop() {
		if (!Enabled) return;

		(IChatCompletionService chatService, ToolRunner toolRunner, Kernel kernel,
		 IUsageExtractor usageExtractor, IReasoningExtractor reasoningExtractor, string model) = BuildLiveStack(withDateTimePlugin: true);

		SetThinking(model, "high", maxTokensFloor: 4096, out PromptExecutionSettings settings);
		settings.ModelId                = model;
		settings.FunctionChoiceBehavior = FunctionChoiceBehavior.Auto(autoInvoke: false);

		ChatHistory history = [];
		history.AddUserMessage("Use the available tool to get the current date and time, then tell me the year.");

		List<ChatMessageContent> messages = [];
		await foreach (ChatMessageContent msg in toolRunner.RunAsync(
			chatService, history, kernel, settings, usageExtractor, reasoningExtractor))
			messages.Add(msg);

		// Validates the thinking + tool_use continuation path: at least one tool round-trip
		// and a final assistant message, with no 400 from the API mid-loop.
		Assert.Contains(messages, m => m.Role == AuthorRole.Tool);
		ChatMessageContent final = messages.Last(m => m.Role == AuthorRole.Assistant);
		Assert.False(string.IsNullOrWhiteSpace(final.Content));
	}

	private static (IChatCompletionService, ToolRunner, Kernel, IUsageExtractor, IReasoningExtractor, string) BuildLiveStack(
		bool withDateTimePlugin = false) {
		DotEnv.Load();
		if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY")))
			throw new InvalidOperationException(
				"LIS_E2E_ANTHROPIC=true but ANTHROPIC_API_KEY is not set — export it or add it to the repo .env");

		// Production wiring: OAuth bearer detection, retry/cache/thinking handler chain
		ServiceCollection services = new();
		services.AddAnthropic();
		ServiceProvider sp = services.BuildServiceProvider();

		IChatCompletionService chatService = sp.GetRequiredService<IChatClient>().AsChatCompletionService();
		IUsageExtractor usageExtractor     = sp.GetRequiredService<IUsageExtractor>();
		IReasoningExtractor reasoningExtractor = sp.GetRequiredService<IReasoningExtractor>();
		string model = sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<AnthropicOptions>>().Value.Model;

		Kernel kernel = Kernel.CreateBuilder().Build();
		if (withDateTimePlugin) kernel.Plugins.AddFromType<DateTimePlugin>(pluginName: "dt");

		ToolAuthRegistry authRegistry = new();
		authRegistry.Build(kernel);
		ToolRunner toolRunner = new(authRegistry, new LiveFakeApprovalService(), NullLogger<ToolRunner>.Instance);

		return (chatService, toolRunner, kernel, usageExtractor, reasoningExtractor, model);
	}

	private static void SetThinking(string model, string effort, int maxTokensFloor, out PromptExecutionSettings settings) {
		(JsonObject? fields, int minMaxTokens) =
			ConversationService.BuildThinkingFields(model, effort, NullLogger<ConversationService>.Instance);
		ThinkingContext.Pending.Value = fields;

		settings = new PromptExecutionSettings {
			ExtensionData = new Dictionary<string, object> { ["max_tokens"] = Math.Max(maxTokensFloor, minMaxTokens) }
		};
	}

	private sealed class LiveFakeApprovalService : IApprovalService {
		public Task<ApprovalResult> RequestApprovalAsync(ApprovalRequest request, CancellationToken ct) =>
			Task.FromResult(new ApprovalResult(ApprovalDecision.Once, "auto"));

		public Task<bool> ResolveAsync(string approvalId, ApprovalDecision decision, string senderJid) =>
			Task.FromResult(true);

		public Task<bool> ResolveByMessageAsync(string messageExternalId, ApprovalDecision decision, string senderJid) =>
			Task.FromResult(true);
	}
}
