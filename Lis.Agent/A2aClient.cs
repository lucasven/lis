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

namespace Lis.Agent;

public sealed class A2aClient(
	IServiceScopeFactory scopeFactory,
	IServiceProvider     serviceProvider,
	PromptComposer       promptComposer,
	ILogger<A2aClient> logger) : IA2aClient {

	private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

	[Trace("A2aClient > SendMessageAsync")]
	public async Task<A2aTask> SendMessageAsync(string targetAgent, A2aMessage message, CancellationToken ct = default) {
		if (message.Parts is not { Count: > 0 })
			throw new InvalidOperationException("Message must contain at least one part.");

		using IServiceScope scope = scopeFactory.CreateScope();
		LisDbContext db    = scope.ServiceProvider.GetRequiredService<LisDbContext>();

		AgentEntity? agent = await db.Agents.FirstOrDefaultAsync(a => a.Name == targetAgent, ct);
		if (agent is null)
			throw new KeyNotFoundException($"Agent '{targetAgent}' not found.");

		string contextId = message.ContextId ?? Guid.NewGuid().ToString();
		string taskId    = Guid.NewGuid().ToString();

		// Create a dedicated OpikTracer for the target agent (if Opik is enabled)
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

			ChatHistory history = [];
			if (!string.IsNullOrWhiteSpace(systemPrompt))
				history.AddSystemMessage(systemPrompt);
			history.AddUserMessage(userContent);

			ModelSettings modelSettings = AgentService.ToModelSettings(agent);

			Dictionary<string, object> extensionData = new() { ["max_tokens"] = modelSettings.MaxTokens };
			if (modelSettings.ThinkingEffort is { Length: > 0 } effort)
				extensionData["thinking"] = new Dictionary<string, object> {
					["type"]    = "enabled",
					["budget_tokens"] = effort switch {
						"low"    => 1024,
						"medium" => 4096,
						"high"   => 16384,
						_        => int.TryParse(effort, out int t) ? t : 4096
					}
				};

			PromptExecutionSettings settings = new() {
				ModelId = modelSettings.Model,
				ExtensionData = extensionData,
			};

			tracer?.StartLlmSpan(modelSettings.Model, agent.Provider, history);

			IReadOnlyList<ChatMessageContent> results =
				await chatService.GetChatMessageContentsAsync(history, settings, kernel: null, ct);

			// Extract usage from response
			TokenUsage? usage = null;
			IUsageExtractor? usageExtractor = null;
			try { usageExtractor = serviceProvider.GetRequiredKeyedService<IUsageExtractor>(agent.Provider); }
			catch { /* provider has no usage extractor */ }

			foreach (ChatMessageContent result in results) {
				if (usageExtractor is not null)
					usage = usageExtractor.Extract(result.Metadata);

				tracer?.EndLlmSpan(result, usage);
			}

			List<Part> responseParts = [];
			foreach (ChatMessageContent result in results) {
				if (string.IsNullOrWhiteSpace(result.Content)) continue;

				responseParts.Add(new TextPart { Text = result.Content });

				if (TryParseJsonData(result.Content, out Dictionary<string, object>? data))
					responseParts.Add(new DataPart { Data = data });
			}

			if (responseParts.Count == 0)
				responseParts.Add(new TextPart { Text = "(no response)" });

			string responseText = string.Join("\n", responseParts.OfType<TextPart>().Select(p => p.Text));
			if (tracer is not null) await tracer.EndTraceAsync(responseText, usage);

			return new A2aTask {
				Id      = taskId,
				ContextId = contextId,
				Status = new A2aTaskStatus {
					State = A2aTaskState.Completed,
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
						Role= "agent",
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
