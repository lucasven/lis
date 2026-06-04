using Lis.Core.Channel;
using Lis.Core.Configuration;
using Lis.Core.Subagents;
using Lis.Core.Util;
using Lis.Persistence;
using Lis.Persistence.Entities;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;

using FunctionCallContent = Microsoft.SemanticKernel.FunctionCallContent;
using FunctionResultContent = Microsoft.SemanticKernel.FunctionResultContent;

namespace Lis.Agent;

public sealed class SubagentRunner(
	IServiceScopeFactory scopeFactory,
	IServiceProvider     serviceProvider,
	ToolRunner           toolRunner,
	ToolPolicyService    toolPolicyService,
	PromptComposer       promptComposer,
	ILogger<SubagentRunner> logger) : ISubagentRunner {

	private const int MaxDepth = 3;

	[Trace("SubagentRunner > RunAsync")]
	public async Task<SubagentResult> RunAsync(SubagentRequest request, long agentId, CancellationToken ct = default) {
		if (string.IsNullOrWhiteSpace(request.Task))
			return new SubagentResult { Status = SubagentStatus.Failed, Error = "Task description is required" };

		int currentDepth = ToolContext.Depth;
		if (currentDepth >= MaxDepth)
			return new SubagentResult {
				Status = SubagentStatus.Failed,
				Error  = $"Maximum subagent nesting depth ({MaxDepth}) exceeded"
			};

		try {
			return await this.ExecuteAsync(request, agentId, currentDepth, ct);
		} catch (OperationCanceledException) {
			throw;
		} catch (Exception ex) {
			logger.LogError(ex, "Subagent execution failed for agent {AgentId}, task: {Task}, model: {Model}, depth: {Depth}",
					agentId, request.Task?[..Math.Min(request.Task.Length, 200)], request.Model ?? "(default)", currentDepth);
			return new SubagentResult { Status = SubagentStatus.Failed, Error = ex.Message };
		}
	}

	[Trace("SubagentRunner > ExecuteAsync")]
	private async Task<SubagentResult> ExecuteAsync(
		SubagentRequest request, long agentId, int parentDepth, CancellationToken ct) {

		using IServiceScope scope = scopeFactory.CreateScope();
		LisDbContext db            = scope.ServiceProvider.GetRequiredService<LisDbContext>();
		OpikTracer? tracer         = scope.ServiceProvider.GetService<OpikTracer>();

		AgentEntity agent = await db.Agents.AsNoTracking().FirstAsync(a => a.Id == agentId, ct);

		(string provider, string modelId) = ResolveModel(request.Model, agent);

		IChatClient? chatClient = serviceProvider.GetKeyedService<IChatClient>(provider);
		if (chatClient is null)
			return new SubagentResult {
				Status = SubagentStatus.Failed,
				Error  = $"Provider '{provider}' is not configured"
			};

		IUsageExtractor usageExtractor = serviceProvider.GetRequiredKeyedService<IUsageExtractor>(provider);
		IChatCompletionService chatService = chatClient.AsChatCompletionService();

		string systemPrompt = await promptComposer.BuildAsync(db, agent.Id, ct);

		ChatHistory history = [];
		if (!string.IsNullOrWhiteSpace(systemPrompt))
			history.AddSystemMessage(systemPrompt);
		history.AddUserMessage(request.Task);

		Kernel agentKernel = serviceProvider.GetRequiredService<Kernel>().Clone();
		HashSet<string> allowedPlugins = toolPolicyService.GetAllowedPluginNames(agent);
		foreach (KernelPlugin plugin in agentKernel.Plugins.ToList())
			if (!allowedPlugins.Contains(plugin.Name))
				agentKernel.Plugins.Remove(plugin);

		ModelSettings modelSettings = AgentService.ToModelSettings(agent);

		Dictionary<string, object> extensionData = new() { ["max_tokens"] = modelSettings.MaxTokens };
		if (modelSettings.ThinkingEffort is { Length: > 0 } effort)
			extensionData["thinking"] = new Dictionary<string, object> {
				["type"]          = "enabled",
				["budget_tokens"] = effort switch {
					"low"    => 1024,
					"medium" => 4096,
					"high"   => 16384,
					_        => int.TryParse(effort, out int t) ? t : 4096
				}
			};

		PromptExecutionSettings settings = new() {
			ModelId                = modelId,
			FunctionChoiceBehavior = FunctionChoiceBehavior.Auto(autoInvoke: false),
			ExtensionData          = extensionData
		};

		// Set ToolContext isolation — explicit writes for fields that must NOT inherit
		ToolContext.Channel              = null;
		ToolContext.NotificationsEnabled = false;
		ToolContext.MessageExternalId    = null;
		ToolContext.CacheBreakIndex      = -1;
		ToolContext.Depth                = parentDepth + 1;

		IncomingMessage fakeMsg = new() {
			ExternalId = Guid.NewGuid().ToString(),
			ChatId     = ToolContext.ChatId ?? $"sub:{agentId}",
			SenderId   = "subagent",
			Body       = request.Task,
			SenderName = "subagent",
			Channel    = "subagent",
		};
		tracer?.StartTrace(fakeMsg.ChatId, agent.Name, provider, modelId, agentId, fakeMsg);
		tracer?.StartLlmSpan(modelId, provider, history);

		string?     lastAssistantContent = null;
		TokenUsage? lastUsage            = null;
		int         totalInput           = 0;
		int         totalOutput          = 0;

		try {
			await foreach (ChatMessageContent msg in toolRunner.RunAsync(chatService, history, agentKernel, settings, usageExtractor, ct)) {
				TokenUsage? msgUsage = ToolRunner.GetUsage(msg);

				if (msg.Role == AuthorRole.Assistant) {
					if (!string.IsNullOrWhiteSpace(msg.Content))
						lastAssistantContent = msg.Content;

					if (msgUsage is not null) {
						lastUsage = msgUsage;
						tracer?.EndLlmSpan(msg, msgUsage);
					} else {
						tracer?.StartLlmSpan(modelId, provider, history);
					}
				}

				if (msg.Role == AuthorRole.Tool) {
					foreach (KernelContent item in msg.Items) {
						if (item is FunctionResultContent fr) {
							FunctionCallContent? matchingCall = null;
							foreach (ChatMessageContent prev in history.AsEnumerable().Reverse()) {
								matchingCall = prev.Items.OfType<FunctionCallContent>()
									.FirstOrDefault(c => c.Id == fr.CallId);
								if (matchingCall is not null) break;
							}
							if (matchingCall is not null) tracer?.RecordToolSpan(matchingCall, fr);
						}
					}
				}

				if (msgUsage is not null) {
					totalInput  += msgUsage.TotalInputTokens;
					totalOutput += msgUsage.OutputTokens;
				}
			}

			if (tracer is not null) await tracer.EndTraceAsync(lastAssistantContent ?? "(no response)", lastUsage);
		} catch (Exception ex) when (ex is not OperationCanceledException) {
			if (tracer is not null) await tracer.EndTraceAsync(null, lastUsage, ex);
			throw;
		}

		return new SubagentResult {
			Status = SubagentStatus.Completed,
			Result = lastAssistantContent ?? "(no response)",
			Usage  = new SubagentTokenUsage { InputTokens = totalInput, OutputTokens = totalOutput }
		};
	}

	private static (string provider, string modelId) ResolveModel(string? model, AgentEntity agent) {
		if (string.IsNullOrWhiteSpace(model))
			return (agent.Provider, agent.Model);

		int colonIndex = model.IndexOf(':');
		if (colonIndex > 0)
			return (model[..colonIndex], model[(colonIndex + 1)..]);

		return (agent.Provider, model);
	}
}
