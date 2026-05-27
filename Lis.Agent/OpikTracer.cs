using Lis.Core.Channel;
using Lis.Core.Observability;

using Microsoft.Extensions.Logging;
using Microsoft.SemanticKernel;

namespace Lis.Agent;

public sealed class OpikTracer(OpikClient client, string project, ILogger<OpikTracer> logger) {
	private OpikTrace?         _trace;
	private OpikSpan?          _currentLlmSpan;
	private readonly List<OpikSpan> _spans = [];

	private static string Now() => DateTimeOffset.UtcNow.ToString("O");

	public void StartTrace(string chatId, string agentName, string provider, string model, long sessionId, IncomingMessage message) {
		this._trace = new OpikTrace {
			Id          = Guid.NewGuid().ToString(),
			Name        = agentName,
			ProjectName = project,
			ThreadId    = chatId,
			StartTime   = Now(),
			Input       = new { message = message.Body, sender = message.SenderName },
			Metadata    = new { agent = agentName, provider, model, session_id = sessionId },
			Tags        = [agentName, provider]
		};
	}

	public void StartLlmSpan(string model, string provider) {
		if (this._trace is null) return;

		this._currentLlmSpan = new OpikSpan {
			Id          = Guid.NewGuid().ToString(),
			TraceId     = this._trace.Id,
			ProjectName = project,
			Name        = $"chat {model}",
			Type        = "llm",
			StartTime   = Now(),
			Model       = model,
			Provider    = provider
		};
	}

	public void EndLlmSpan(ChatMessageContent msg, TokenUsage? usage) {
		if (this._currentLlmSpan is null) return;

		this._currentLlmSpan.EndTime = Now();
		this._currentLlmSpan.Output  = new { message = msg.Content };

		if (usage is not null) {
			this._currentLlmSpan.Usage = new OpikUsage {
				PromptTokens     = usage.TotalInputTokens,
				CompletionTokens = usage.OutputTokens,
				TotalTokens      = usage.TotalInputTokens + usage.OutputTokens
			};
		}

		this._spans.Add(this._currentLlmSpan);
		this._currentLlmSpan = null;
	}

	public void RecordToolSpan(Microsoft.Extensions.AI.FunctionCallContent call, Microsoft.Extensions.AI.FunctionResultContent result) {
		if (this._trace is null) return;

		string? parentId = this._spans.Count > 0 ? this._spans[^1].Id : null;

		this._spans.Add(new OpikSpan {
			Id           = Guid.NewGuid().ToString(),
			TraceId      = this._trace.Id,
			ParentSpanId = parentId,
			ProjectName  = project,
			Name         = $"tool {call.Name}",
			Type         = "tool",
			StartTime    = Now(),
			EndTime      = Now(),
			Input        = new { name = call.Name, arguments = call.Arguments },
			Output       = new { result = result.Result?.ToString() }
		});
	}

	public void EndTrace(string? finalResponse, TokenUsage? totalUsage, Exception? error = null) {
		if (this._trace is null) return;

		this._trace.EndTime = Now();
		if (finalResponse is not null)
			this._trace.Output = new { response = finalResponse };

		if (totalUsage is not null) {
			this._trace.Usage = new OpikUsage {
				PromptTokens     = totalUsage.TotalInputTokens,
				CompletionTokens = totalUsage.OutputTokens,
				TotalTokens      = totalUsage.TotalInputTokens + totalUsage.OutputTokens
			};
		}

		if (error is not null) {
			this._trace.ErrorInfo = new OpikErrorInfo {
				ErrorType    = error.GetType().Name,
				ErrorMessage = error.Message
			};
		}

		// Fire-and-forget flush
		OpikTrace trace = this._trace;
		List<OpikSpan> spans = [.. this._spans];
		_ = Task.Run(async () => {
			try {
				await client.SendTracesAsync([trace]);
				if (spans.Count > 0)
					await client.SendSpansAsync(spans);
			} catch (Exception ex) {
				if (logger.IsEnabled(LogLevel.Debug))
					logger.LogDebug(ex, "Failed to send Opik trace {TraceId}", trace.Id);
			}
		});
	}
}
