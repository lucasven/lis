using System.Text;
using System.Text.Json;

using Lis.Core.A2A;
using Lis.Core.Channel;
using Lis.Core.Configuration;
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

public sealed class A2aClient(
	IServiceScopeFactory scopeFactory,
	IServiceProvider     serviceProvider,
	Kernel               kernel,
	ToolRunner           toolRunner,
	ToolPolicyService    toolPolicyService,
	PromptComposer       promptComposer,
	ILogger<A2aClient>   logger) : IA2aClient {

	private const int MaxDepth = 3;
	private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

	[Trace("A2aClient > SendMessageAsync")]
	public async Task<A2aTask> SendMessageAsync(string targetAgent, A2aMessage message, CancellationToken ct = default) {
		if (message.Parts is not { Count: > 0 })
			throw new InvalidOperationException("Message must contain at least one part.");

		int currentDepth = ToolContext.Depth;
		if (currentDepth >= MaxDepth)
			throw new InvalidOperationException($"Maximum A2A nesting depth ({MaxDepth}) exceeded");

		using IServiceScope scope = scopeFactory.CreateScope();
		LisDbContext db = scope.ServiceProvider.GetRequiredService<LisDbContext>();

		AgentEntity? agent = await db.Agents.FirstOrDefaultAsync(a => a.Name == targetAgent, ct);
		if (agent is null)
			throw new KeyNotFoundException($"Agent '{targetAgent}' not found.");

		string contextId = message.ContextId ?? Guid.NewGuid().ToString();
		string taskId    = Guid.NewGuid().ToString();

		OpikTracer? tracer = scope.ServiceProvider.GetService<OpikTracer>();

		string userContent = BuildUserContent(message);
		IncomingMessage fakeMsg = new() {
			ExternalId = taskId,
			ChatId     = $"a2a:{targetAgent}",
			SenderId   = "a2a",
			Body       = userContent,
			SenderName = "a2a",
			Channel    = "a2a",
		};
		tracer?.StartTrace(fakeMsg.ChatId, targetAgent, agent.Provider,
			agent.Model ?? "unknown", 0, fakeMsg);

		try {
			string systemPrompt = await promptComposer.BuildAsync(db, agent.Id, ct);

			IChatClient chatClient = serviceProvider.GetRequiredKeyedService<IChatClient>(agent.Provider);
			IChatCompletionService chatService = chatClient.AsChatCompletionService();
			IUsageExtractor usageExtractor = serviceProvider.GetRequiredKeyedService<IUsageExtractor>(agent.Provider);

			ChatHistory history = [];
			if (!string.IsNullOrWhiteSpace(systemPrompt))
				history.AddSystemMessage(systemPrompt);
			history.AddUserMessage(userContent);

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

			// Clone kernel and filter plugins based on target agent's tool policy
			Kernel agentKernel = kernel.Clone();
			HashSet<string> allowedPlugins = toolPolicyService.GetAllowedPluginNames(agent);
			foreach (KernelPlugin plugin in agentKernel.Plugins.ToList())
				if (!allowedPlugins.Contains(plugin.Name))
					agentKernel.Plugins.Remove(plugin);

			PromptExecutionSettings settings = new() {
				ModelId                = modelSettings.Model,
				FunctionChoiceBehavior = FunctionChoiceBehavior.Auto(autoInvoke: false),
				ExtensionData          = extensionData,
			};

			// ToolContext isolation for the target agent
			ToolContext.Channel              = null;
			ToolContext.NotificationsEnabled = false;
			ToolContext.MessageExternalId    = null;
			ToolContext.CacheBreakIndex      = -1;
			ToolContext.AgentId              = agent.Id;
			ToolContext.Depth                = currentDepth + 1;

			tracer?.StartLlmSpan(modelSettings.Model, agent.Provider, history);

			string?     lastAssistantContent = null;
			TokenUsage? lastUsage            = null;

			await foreach (ChatMessageContent msg in toolRunner.RunAsync(
				chatService, history, agentKernel, settings, usageExtractor, ct)) {

				TokenUsage? msgUsage = ToolRunner.GetUsage(msg);

				if (msg.Role == AuthorRole.Assistant) {
					if (!string.IsNullOrWhiteSpace(msg.Content))
						lastAssistantContent = msg.Content;

					if (msgUsage is not null) {
						lastUsage = msgUsage;
						tracer?.EndLlmSpan(msg, msgUsage);
					} else {
						tracer?.StartLlmSpan(modelSettings.Model, agent.Provider, history);
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
			}

			List<Part> responseParts = [];
			if (!string.IsNullOrWhiteSpace(lastAssistantContent)) {
				responseParts.Add(new TextPart { Text = lastAssistantContent });

				if (TryParseJsonData(lastAssistantContent, out Dictionary<string, object>? data))
					responseParts.Add(new DataPart { Data = data });
			}

			if (responseParts.Count == 0)
				responseParts.Add(new TextPart { Text = "(no response)" });

			string responseText = string.Join("\n", responseParts.OfType<TextPart>().Select(p => p.Text));
			if (tracer is not null) await tracer.EndTraceAsync(responseText, lastUsage);

			return new A2aTask {
				Id        = taskId,
				ContextId = contextId,
				Status = new A2aTaskStatus {
					State     = A2aTaskState.Completed,
					Timestamp = DateTimeOffset.UtcNow,
				},
				Artifacts = [
					new A2aArtifact {
						ArtifactId = Guid.NewGuid().ToString(),
						Parts      = responseParts,
					}
				],
			};
		} catch (Exception ex) when (ex is not OperationCanceledException and not KeyNotFoundException and not InvalidOperationException) {
			logger.LogError(ex, "A2A call to agent '{Agent}' failed", targetAgent);

			if (tracer is not null) await tracer.EndTraceAsync(null, null, ex);

			return new A2aTask {
				Id        = taskId,
				ContextId = contextId,
				Status = new A2aTaskStatus {
					State = A2aTaskState.Failed,
					Message = new A2aMessage {
						MessageId = Guid.NewGuid().ToString(),
						Role      = "agent",
						Parts     = [new TextPart { Text = ex.Message }],
					},
					Timestamp = DateTimeOffset.UtcNow,
				},
			};
		}
	}

	private static bool TryParseJsonData(string content, out Dictionary<string, object> data) {
		data = null!;
		string trimmed = content.Trim();
		if (!trimmed.StartsWith('{') || !trimmed.EndsWith('}')) return false;

		try {
			data = JsonSerializer.Deserialize<Dictionary<string, object>>(trimmed, JsonOptions)!;
			return data is not null;
		} catch {
			return false;
		}
	}

	private static string BuildUserContent(A2aMessage message) {
		StringBuilder sb = new();

		foreach (Part part in message.Parts) {
			if (sb.Length > 0) sb.Append("\n\n");

			switch (part) {
				case TextPart text:
					sb.Append(text.Text);
					break;
				case DataPart data:
					sb.Append(JsonSerializer.Serialize(data.Data, JsonOptions));
					break;
			}
		}

		return sb.ToString();
	}
}
