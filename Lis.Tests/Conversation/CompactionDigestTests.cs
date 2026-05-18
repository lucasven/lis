using Lis.Agent;
using Lis.Persistence.Entities;

namespace Lis.Tests.Conversation;

public sealed class CompactionDigestTests {
	[Fact]
	public void BuildConversationText_InjectsDigests() {
		List<MessageEntity> messages = [
			new() { Id = 1, Body = "Check the weather", IsFromMe = false, SenderId = "user" },
			new() { Id = 2, Body = "Sure, let me check.", IsFromMe = true, SenderId = "me", Role = "assistant" },
			new() { Id = 4, Body = "[media/tool]", IsFromMe = true, SenderId = "me", Role = "tool" },
			new() { Id = 5, Body = "The weather is sunny!", IsFromMe = true, SenderId = "me", Role = "assistant" },
		];

		List<ToolDigestEntity> digests = [
			new() { Id = 1, SessionId = 1, MessageId = 4, Digest = "get_weather (call-1, msg 4): Weather API returned sunny, 25°C in São Paulo" },
		];

		string result = CompactionService.BuildConversationText(messages, null, digests);

		Assert.Contains("Tool context (from pruned tool calls)", result);
		Assert.Contains("get_weather", result);
		Assert.Contains("sunny, 25°C", result);
	}

	[Fact]
	public void BuildConversationText_WorksWithoutDigests() {
		List<MessageEntity> messages = [
			new() { Id = 1, Body = "Hello", IsFromMe = false, SenderId = "user" },
			new() { Id = 2, Body = "Hi!", IsFromMe = true, SenderId = "me", Role = "assistant" },
		];

		string result = CompactionService.BuildConversationText(messages, null, []);

		Assert.Contains("User: Hello", result);
		Assert.Contains("Assistant: Hi!", result);
		Assert.DoesNotContain("Tool context", result);
	}

	[Fact]
	public void BuildConversationText_IncludesPreviousSummary() {
		List<MessageEntity> messages = [
			new() { Id = 1, Body = "Hello", IsFromMe = false, SenderId = "user" },
		];

		string result = CompactionService.BuildConversationText(messages, "Previous context here.", []);

		Assert.Contains("Previous summary:", result);
		Assert.Contains("Previous context here.", result);
	}
}
