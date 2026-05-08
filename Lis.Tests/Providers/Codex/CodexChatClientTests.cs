using Microsoft.Extensions.AI;

using Lis.Providers.OpenAi.Codex;

namespace Lis.Tests.Providers.Codex;

public class CodexChatClientTests {
	[Fact]
	public void BuildRequest_SetsPromptCacheKey_ToSessionId() {
		CodexOptions options = new() { AccessToken = "t", RefreshToken = "r" };
		List<ChatMessage> messages = [new(ChatRole.User, "hi")];

		CodexRequest request = CodexChatClient.BuildRequest(messages, null, "session-abc", options);

		Assert.Equal("session-abc", request.PromptCacheKey);
	}

	[Fact]
	public void BuildRequest_OmitsPromptCacheKey_WhenNoSessionId() {
		CodexOptions options = new() { AccessToken = "t", RefreshToken = "r" };
		List<ChatMessage> messages = [new(ChatRole.User, "hi")];

		CodexRequest request = CodexChatClient.BuildRequest(messages, null, null, options);

		Assert.Null(request.PromptCacheKey);
	}

	[Fact]
	public void BuildRequest_StoreIsFalse() {
		CodexOptions options = new() { AccessToken = "t", RefreshToken = "r" };
		List<ChatMessage> messages = [new(ChatRole.User, "test")];

		CodexRequest request = CodexChatClient.BuildRequest(messages, null, null, options);

		Assert.False(request.Store);
	}

	[Fact]
	public void BuildRequest_StreamIsTrue() {
		CodexOptions options = new() { AccessToken = "t", RefreshToken = "r" };
		List<ChatMessage> messages = [new(ChatRole.User, "test")];

		CodexRequest request = CodexChatClient.BuildRequest(messages, null, null, options);

		Assert.True(request.Stream);
	}

	[Fact]
	public void BuildRequest_SystemMessage_GoesToInstructions() {
		CodexOptions options = new() { AccessToken = "t", RefreshToken = "r" };
		List<ChatMessage> messages = [
			new(ChatRole.System, "Be concise."),
			new(ChatRole.User, "Hello")
		];

		CodexRequest request = CodexChatClient.BuildRequest(messages, null, null, options);

		Assert.Equal("Be concise.", request.Instructions);
		Assert.Single(request.Input);
	}

	[Fact]
	public void BuildRequest_WithReasoningEffort() {
		CodexOptions options = new() { AccessToken = "t", RefreshToken = "r", ReasoningEffort = "medium" };
		List<ChatMessage> messages = [new(ChatRole.User, "test")];

		CodexRequest request = CodexChatClient.BuildRequest(messages, null, null, options);

		Assert.NotNull(request.Reasoning);
		Assert.Equal("medium", request.Reasoning!.Effort);
	}

	[Fact]
	public void BuildRequest_UsesModelFromOptions() {
		CodexOptions options = new() { AccessToken = "t", RefreshToken = "r", Model = "codex-1" };
		List<ChatMessage> messages = [new(ChatRole.User, "test")];

		CodexRequest request = CodexChatClient.BuildRequest(messages, null, null, options);

		Assert.Equal("codex-1", request.Model);
	}

	[Fact]
	public void BuildRequest_ModelOverrideFromChatOptions() {
		CodexOptions options = new() { AccessToken = "t", RefreshToken = "r", Model = "codex-1" };
		ChatOptions chatOpts = new() { ModelId = "gpt-5.4-mini" };
		List<ChatMessage> messages = [new(ChatRole.User, "test")];

		CodexRequest request = CodexChatClient.BuildRequest(messages, chatOpts, null, options);

		Assert.Equal("gpt-5.4-mini", request.Model);
	}
}
