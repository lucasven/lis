using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

using Lis.Core.Configuration;
using Lis.Core.Util;
using Lis.Persistence;
using Lis.Persistence.Entities;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Lis.Agent;

public sealed partial class DigestService(
	[FromKeyedServices("compaction")] IChatClient compactionClient,
	IServiceScopeFactory                          scopeFactory,
	IOptions<LisOptions>                          lisOptions,
	ILogger<DigestService>                        logger) {

	private const int MaxConversationMessages = 10;

	[Trace("DigestService > GenerateDigestsAsync")]
	public async Task GenerateDigestsAsync(long sessionId, long pruneBoundaryId, CancellationToken ct) {
		using IServiceScope scope = scopeFactory.CreateScope();
		LisDbContext        db    = scope.ServiceProvider.GetRequiredService<LisDbContext>();

		SessionEntity? session = await db.Sessions.FindAsync([sessionId], ct);
		if (session is null) return;

		// Load the last N non-tool conversation messages for context
		List<MessageEntity> conversationMessages = await db.Messages
			.Where(m => m.SessionId == session.Id && !m.Queued && m.Role != "tool")
			.OrderByDescending(m => m.Id)
			.Take(MaxConversationMessages)
			.ToListAsync(ct);
		conversationMessages.Reverse();

		// Load tool messages (assistant+tool pairs) that are being pruned
		List<MessageEntity> toolMessages = await db.Messages
			.Where(m => m.SessionId == session.Id && !m.Queued
			         && m.Id <= pruneBoundaryId
			         && (m.Role == "tool" || m.SkContent != null))
			.OrderBy(m => m.Id)
			.ToListAsync(ct);

		// Filter to only tool-related messages (assistant with FunctionCallContent + tool results)
		toolMessages = toolMessages
			.Where(m => m.Role == "tool" || HasFunctionCallContent(m))
			.ToList();

		if (toolMessages.Count == 0) return;

		string prompt = BuildDigestPrompt(conversationMessages, toolMessages);

		try {
			string model = lisOptions.Value.CompactionModel is { Length: > 0 } m ? m : "claude-haiku-4-5-20251001";
			ChatOptions options = new() { ModelId = model };
			ChatResponse result = await compactionClient.GetResponseAsync(prompt, options, ct);
			string response = result.Text ?? "";

			List<MessageEntity> toolResultMessages = toolMessages.Where(m => m.Role == "tool").ToList();
			List<(long MessageId, string Digest)> digests = ParseDigestResponse(response, toolResultMessages);

			if (digests.Count > 0) {
				foreach ((long messageId, string digest) in digests) {
					db.ToolDigests.Add(new ToolDigestEntity {
						SessionId = session.Id,
						MessageId = messageId,
						Digest    = digest,
						CreatedAt = DateTimeOffset.UtcNow
					});
				}
				await db.SaveChangesAsync(ct);
			}

			if (logger.IsEnabled(LogLevel.Information))
				logger.LogInformation(
					"Generated {Count} tool digests for session #{SessionId}",
					digests.Count, session.Id);
		} catch (Exception ex) {
			logger.LogWarning(ex, "Failed to generate tool digests for session #{SessionId}", session.Id);
		}
	}

	public static string BuildDigestPrompt(
		IReadOnlyList<MessageEntity> conversationMessages, IReadOnlyList<MessageEntity> toolMessages) {

		StringBuilder sb = new();
		sb.AppendLine("""
			You are reviewing tool call results that are about to be removed from a conversation's context window.
			Your job: identify which tool results contain facts, decisions, or data the conversation depends on.

			For each relevant tool call, write exactly one line:
			<function_name> (call-<id>, msg <message_id>): <one-line summary of the relevant fact>

			If a tool result is purely mechanical (e.g., "message sent", "typing indicator set", "ok") or redundant with the conversation, skip it entirely.

			If none are relevant, respond with exactly: NONE
			""");

		sb.AppendLine("\n## Recent conversation (for context):\n");

		// Take only last N conversation messages
		IReadOnlyList<MessageEntity> recentConversation = conversationMessages.Count > MaxConversationMessages
			? conversationMessages.Skip(conversationMessages.Count - MaxConversationMessages).ToList()
			: conversationMessages;

		foreach (MessageEntity msg in recentConversation) {
			string role = msg.IsFromMe ? "Assistant" : "User";
			sb.AppendLine($"{role}: {msg.Body ?? "[media]"}");
		}

		sb.AppendLine("\n## Tool calls to evaluate:\n");

		foreach (MessageEntity msg in toolMessages) {
			if (msg.Role == "tool" && msg.SkContent is not null) {
				(string? funcName, string? result) = ExtractToolResult(msg);
				sb.AppendLine($"[msg {msg.Id}] {funcName ?? "unknown"}: {result ?? "[no result]"}");
			} else if (HasFunctionCallContent(msg) && msg.SkContent is not null) {
				string? callInfo = ExtractFunctionCall(msg);
				if (callInfo is not null) sb.AppendLine($"[assistant] {callInfo}");
			}
		}

		return sb.ToString();
	}

	public static List<(long MessageId, string Digest)> ParseDigestResponse(
		string response, IReadOnlyList<MessageEntity> toolMessages) {

		List<(long MessageId, string Digest)> digests = [];

		if (response.Trim().Equals("NONE", StringComparison.OrdinalIgnoreCase)) return digests;

		HashSet<long> toolMessageIds = toolMessages.Select(m => m.Id).ToHashSet();

		foreach (string line in response.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)) {
			// Try to match "msg <id>" pattern in the line
			Match match = DigestMsgIdRegex().Match(line);

			if (match.Success && long.TryParse(match.Groups[1].Value, out long msgId) && toolMessageIds.Contains(msgId))
				digests.Add((msgId, line));
		}

		return digests;
	}

	private static bool HasFunctionCallContent(MessageEntity msg) {
		if (msg.SkContent is null || msg.Role != "assistant") return false;
		return msg.SkContent.Contains("FunctionCallContent", StringComparison.Ordinal);
	}

	private static (string? FuncName, string? Result) ExtractToolResult(MessageEntity msg) {
		if (msg.SkContent is null) return (null, null);
		try {
			using JsonDocument doc = JsonDocument.Parse(msg.SkContent);
			JsonElement root = doc.RootElement;
			if (root.TryGetProperty("Items", out JsonElement items)) {
				foreach (JsonElement item in items.EnumerateArray()) {
					if (item.TryGetProperty("FunctionName", out JsonElement fn)
					    && item.TryGetProperty("Result", out JsonElement res)) {
						return (fn.GetString(), res.GetString());
					}
				}
			}
		} catch { /* best effort */ }
		return (null, null);
	}

	private static string? ExtractFunctionCall(MessageEntity msg) {
		if (msg.SkContent is null) return null;
		try {
			using JsonDocument doc = JsonDocument.Parse(msg.SkContent);
			JsonElement root = doc.RootElement;
			if (root.TryGetProperty("Items", out JsonElement items)) {
				foreach (JsonElement item in items.EnumerateArray()) {
					if (item.TryGetProperty("FunctionName", out JsonElement fn)
					    && item.TryGetProperty("$type", out JsonElement type)
					    && type.GetString()?.Contains("FunctionCall") == true) {
						return $"Called {fn.GetString()}";
					}
				}
			}
		} catch { /* best effort */ }
		return null;
	}

	[GeneratedRegex(@"msg\s+(\d+)", RegexOptions.NonBacktracking)]
	private static partial Regex DigestMsgIdRegex();
}
