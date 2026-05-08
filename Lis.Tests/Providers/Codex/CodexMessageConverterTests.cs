using System.Text.Json.Nodes;

using Lis.Providers.OpenAi.Codex;

using Microsoft.Extensions.AI;

namespace Lis.Tests.Providers.Codex;

public class CodexMessageConverterTests {
	[Fact]
	public void Property_SystemMessage_NeverInInputArray() {
		List<ChatMessage> messages = [
			new(ChatRole.System, "You are helpful."),
			new(ChatRole.User, "Hello")
		];

		(string? instructions, JsonArray input) = CodexMessageConverter.ConvertToResponsesApi(messages);

		Assert.Equal("You are helpful.", instructions);
		foreach (JsonNode? item in input) {
			string? role = item?["role"]?.GetValue<string>();
			Assert.NotEqual("system", role);
		}
	}

	[Theory]
	[InlineData("Hello")]
	[InlineData("")]
	[InlineData("Multi\nline\nmessage")]
	[InlineData("Special chars: <>&\"'")]
	public void Property_UserTextPreserved(string text) {
		List<ChatMessage> messages = [new(ChatRole.User, text)];

		(_, JsonArray input) = CodexMessageConverter.ConvertToResponsesApi(messages);

		string? actual = input[0]?["content"]?[0]?["text"]?.GetValue<string>();
		Assert.Equal(text, actual);
	}

	[Fact]
	public void Property_ToolResult_AlwaysHasCallId() {
		List<ChatMessage> messages = [
			new(ChatRole.User, "What time is it?"),
			new(ChatRole.Assistant, [new FunctionCallContent("call_123", "GetTime", new Dictionary<string, object?>())]),
			new(ChatRole.Tool, [new FunctionResultContent("call_123", "14:30 UTC")])
		];

		(_, JsonArray input) = CodexMessageConverter.ConvertToResponsesApi(messages);

		JsonNode? toolOutput = input.FirstOrDefault(i =>
			i?["type"]?.GetValue<string>() == "function_call_output");
		Assert.NotNull(toolOutput);
		Assert.Equal("call_123", toolOutput!["call_id"]?.GetValue<string>());
	}

	[Fact]
	public void MultipleSystemMessages_LastWins() {
		List<ChatMessage> messages = [
			new(ChatRole.System, "First instruction"),
			new(ChatRole.System, "Override instruction"),
			new(ChatRole.User, "Go")
		];

		(string? instructions, JsonArray input) = CodexMessageConverter.ConvertToResponsesApi(messages);

		Assert.Equal("Override instruction", instructions);
		Assert.Single(input);
	}

	[Fact]
	public void AssistantTextMessage_ConvertedCorrectly() {
		List<ChatMessage> messages = [
			new(ChatRole.User, "Hi"),
			new(ChatRole.Assistant, "Hello!")
		];

		(_, JsonArray input) = CodexMessageConverter.ConvertToResponsesApi(messages);

		Assert.Equal(2, input.Count);
		Assert.Equal("message", input[1]?["type"]?.GetValue<string>());
		Assert.Equal("assistant", input[1]?["role"]?.GetValue<string>());
		Assert.Equal("completed", input[1]?["status"]?.GetValue<string>());
	}

	[Fact]
	public void FunctionCall_ConvertedCorrectly() {
		List<ChatMessage> messages = [
			new(ChatRole.Assistant, [new FunctionCallContent("c1", "GetTime", new Dictionary<string, object?> { ["tz"] = "UTC" })])
		];

		(_, JsonArray input) = CodexMessageConverter.ConvertToResponsesApi(messages);

		Assert.Single(input);
		Assert.Equal("function_call", input[0]?["type"]?.GetValue<string>());
		Assert.Equal("c1", input[0]?["call_id"]?.GetValue<string>());
		Assert.Equal("GetTime", input[0]?["name"]?.GetValue<string>());
	}

	[Fact]
	public void ParseFunctionCall_ValidJson() {
		FunctionCallContent fc = CodexMessageConverter.ParseFunctionCall(
			"call_1", "GetTime", """{"timezone":"UTC"}""");

		Assert.Equal("call_1", fc.CallId);
		Assert.Equal("GetTime", fc.Name);
		Assert.NotNull(fc.Arguments);
	}

	[Fact]
	public void ParseFunctionCall_InvalidJson_FallsBack() {
		FunctionCallContent fc = CodexMessageConverter.ParseFunctionCall(
			"call_2", "DoThing", "not-json");

		Assert.Equal("call_2", fc.CallId);
		Assert.NotNull(fc.Arguments);
		Assert.True(fc.Arguments!.ContainsKey("raw"));
	}
}
