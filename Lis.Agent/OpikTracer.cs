using System.Security.Cryptography;

using Lis.Core.Channel;
using Lis.Core.Observability;

using Microsoft.Extensions.Logging;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;

namespace Lis.Agent;

public sealed class OpikTracer(OpikClient client, string project, ILogger<OpikTracer> logger) {
	private OpikTrace?_trace;
	private OpikSpan?   _currentLlmSpan;
	private string?            _lastLlmSpanId;
	private DateTimeOffset _toolBatchStart;
	private readonly List<OpikSpan> _spans = [];

	private const string Iso = "yyyy-MM-dd'T'HH:mm:ss.fff'Z'";
	private static string Now() => DateTimeOffset.UtcNow.ToString(Iso);

	/// <summary>Generate a UUID v7 (time-ordered) as required by Opik API.</summary>
	private static string NewUuidV7() {
		Span<byte> bytes = stackalloc byte[16];

		// First 6 bytes: Unix timestamp in milliseconds (big-endian)
		long ms = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
		bytes[0] = (byte)(ms >> 40);
		bytes[1] = (byte)(ms >> 32);
		bytes[2] = (byte)(ms >> 24);
		bytes[3] = (byte)(ms >> 16);
		bytes[4] = (byte)(ms >> 8);
		bytes[5] = (byte)ms;

		// Remaining 10 bytes: random
		RandomNumberGenerator.Fill(bytes[6..]);

		// Set version (0111) and variant (10xx) bits
		bytes[6] = (byte)((bytes[6] & 0x0F) | 0x70); // version 7
		bytes[8] = (byte)((bytes[8] & 0x3F) | 0x80); // variant 1

		return new Guid(bytes, bigEndian: true).ToString();
	}

	public void StartTrace(string chatId, string agentName, string provider, string model, long sessionId, IncomingMessage message) {
		this._trace = new OpikTrace {
			Id       = NewUuidV7(),
			Name    = agentName,
			ProjectName = project,
			ThreadId    = chatId,
			StartTime   = Now(),
			Input       = new { message = message.Body, sender = message.SenderName },
			Metadata    = new { agent = agentName, provider, model, session_id = sessionId },
			Tags        = [agentName, provider]
		};
	}

	public void StartLlmSpan(string model, string provider, ChatHistory? history = null) {
		if (this._trace is null) return;

		object? input = null;
		if (history is not null) {
			input = new {
				messages = history.Select(m => new {
					role    = m.Role.Label,
					content = m.Content is { } c ? c[..Math.Min(c.Length, 2000)] : null
				}).ToList()
			};
		}

		this._currentLlmSpan = new OpikSpan {
			Id     = NewUuidV7(),
			TraceId     = this._trace.Id,
			ProjectName = project,
			Name  = $"chat {model}",
			Type   = "llm",
			StartTime   = Now(),
			Input       = input,
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
				PromptTokens = usage.TotalInputTokens,
				CompletionTokens = usage.OutputTokens,
				TotalTokens      = usage.TotalInputTokens + usage.OutputTokens
			};
		}

		this._spans.Add(this._currentLlmSpan);
		this._lastLlmSpanId = this._currentLlmSpan.Id;
		this._toolBatchStart = DateTimeOffset.UtcNow;
		this._currentLlmSpan = null;
	}

	public void RecordToolSpan(FunctionCallContent call, FunctionResultContent result) {
		if (this._trace is null) return;

		string? parentId = this._lastLlmSpanId;

		this._spans.Add(new OpikSpan {
			Id      = NewUuidV7(),
			TraceId      = this._trace.Id,
			ParentSpanId = parentId,
			ProjectName  = project,
			Name      = $"tool {call.FunctionName}",
			Type         = "tool",
			StartTime    = this._toolBatchStart.ToString(Iso),
			EndTime      = Now(),
			Input   = new { name = call.FunctionName, plugin = call.PluginName, arguments = call.Arguments },
			Output    = new { result = result.Result?.ToString() }
		});
	}

	public async Task EndTraceAsync(string? finalResponse, TokenUsage? totalUsage, Exception? error = null) {
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

		try {
			await client.SendTracesAsync([this._trace]);
			if (this._spans.Count > 0)
				await client.SendSpansAsync(this._spans);
		} catch (Exception ex) {
			logger.LogWarning(ex, "Failed to send Opik trace {TraceId}", this._trace.Id);
		}
	}
}
