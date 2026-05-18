using System.Text.Json;

using Lis.Agent;
using Lis.Persistence.Entities;

using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;

namespace Lis.Tests.Conversation;

public sealed class DigestServiceTests {
	[Fact]
	public void BuildDigestPrompt_IncludesConversationAndToolCalls() {
		List<MessageEntity> conversationMessages = [
			new() { Id = 1, Body = "What time is it?", IsFromMe = false, SenderId = "user" },
			new() { Id = 2, Body = "Let me check.", IsFromMe = true, SenderId = "me", Role = "assistant" },
		];

		(MessageEntity assistant, MessageEntity tool) = CreateToolCallPair(
			id: 3, callId: "call-1", funcName: "get_current_time",
			result: "2026-05-18T14:30:00-03:00");

		List<MessageEntity> toolMessages = [assistant, tool];

		string prompt = DigestService.BuildDigestPrompt(conversationMessages, toolMessages);

		Assert.Contains("What time is it?", prompt);
		Assert.Contains("get_current_time", prompt);
		Assert.Contains("2026-05-18T14:30:00-03:00", prompt);
	}

	[Fact]
	public void BuildDigestPrompt_LimitsConversationMessages() {
		List<MessageEntity> conversationMessages = [];
		for (int i = 1; i <= 20; i++)
			conversationMessages.Add(new() {
				Id = i, Body = $"Message {i}", IsFromMe = i % 2 == 0,
				SenderId = i % 2 == 0 ? "me" : "user",
				Role = i % 2 == 0 ? "assistant" : null
			});

		(MessageEntity assistant, MessageEntity tool) = CreateToolCallPair(
			id: 30, callId: "call-1", funcName: "test_tool", result: "result");

		string prompt = DigestService.BuildDigestPrompt(conversationMessages, [assistant, tool]);

		// Should only include the last 10 conversation messages (11-20)
		Assert.DoesNotContain("Message 1:", prompt);
		Assert.Contains("Message 11", prompt);
		Assert.Contains("Message 20", prompt);
	}

	[Fact]
	public void ParseDigestResponse_ExtractsDigests() {
		string response = """
			get_current_time (call-1, msg 3): Returned current time 2026-05-18T14:30:00-03:00
			search_web (call-2, msg 5): Found 3 results about Semantic Kernel plugins
			""";

		List<MessageEntity> toolMessages = [
			new() { Id = 3, Role = "tool", SenderId = "me" },
			new() { Id = 5, Role = "tool", SenderId = "me" },
		];

		List<(long MessageId, string Digest)> digests = DigestService.ParseDigestResponse(response, toolMessages);

		Assert.Equal(2, digests.Count);
		Assert.Contains(digests, d => d.MessageId == 3);
		Assert.Contains(digests, d => d.MessageId == 5);
	}

	[Fact]
	public void ParseDigestResponse_HandlesNoRelevantResponse() {
		string response = "None of these tool calls contain relevant information.";

		List<MessageEntity> toolMessages = [
			new() { Id = 3, Role = "tool", SenderId = "me" },
		];

		List<(long MessageId, string Digest)> digests = DigestService.ParseDigestResponse(response, toolMessages);

		Assert.Empty(digests);
	}

	private static (MessageEntity Assistant, MessageEntity Tool) CreateToolCallPair(
		long id, string callId, string funcName, string result) {

		ChatMessageContent assistantMsg = new(AuthorRole.Assistant, content: (string?)null);
		assistantMsg.Items.Add(new FunctionCallContent(callId, funcName));

		ChatMessageContent toolMsg = new(AuthorRole.Tool, content: (string?)null);
		toolMsg.Items.Add(new FunctionResultContent(funcName, null, callId, result));

		MessageEntity assistant = new() {
			Id = id, SenderId = "me", IsFromMe = true, Role = "assistant",
			SkContent = JsonSerializer.Serialize(assistantMsg),
			Timestamp = DateTimeOffset.UtcNow.AddSeconds(id),
		};

		MessageEntity tool = new() {
			Id = id + 1, SenderId = "me", IsFromMe = true, Role = "tool",
			SkContent = JsonSerializer.Serialize(toolMsg),
			Timestamp = DateTimeOffset.UtcNow.AddSeconds(id + 1),
		};

		return (assistant, tool);
	}
}
