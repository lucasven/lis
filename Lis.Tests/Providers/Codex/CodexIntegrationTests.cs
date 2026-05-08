using Lis.Core.Util;
using Lis.Providers.OpenAi.Codex;

using Microsoft.Extensions.AI;

namespace Lis.Tests.Providers.Codex;

public class CodexIntegrationTests : IDisposable {
	private readonly CodexChatClient? _client;

	public CodexIntegrationTests() {
		DotEnv.Load();
		string? access  = Environment.GetEnvironmentVariable("CODEX_ACCESS_TOKEN");
		string? refresh = Environment.GetEnvironmentVariable("CODEX_REFRESH_TOKEN");
		if (string.IsNullOrEmpty(access) || string.IsNullOrEmpty(refresh)) return;

		string model = Environment.GetEnvironmentVariable("CODEX_MODEL") is { Length: > 0 } m ? m : "codex-1";
		CodexOptions options = new() {
			AccessToken  = access,
			RefreshToken = refresh,
			Model        = model
		};
		CodexTokenManager tokenManager = new(options, new HttpClient());
		this._client = new CodexChatClient(tokenManager, options, new HttpClient());
	}

	private bool SkipIfNoCredentials() => this._client is null;

	[Fact]
	public async Task Integration_SimpleCompletion() {
		if (this.SkipIfNoCredentials()) return;

ChatResponse response = await this._client!.GetResponseAsync(
			[new ChatMessage(ChatRole.User, "Respond with only the word hello")]);

		Assert.NotNull(response.Text);
		Assert.NotEmpty(response.Text);
	}

	[Fact]
	public async Task Integration_Streaming() {
		if (this.SkipIfNoCredentials()) return;

List<ChatResponseUpdate> chunks = [];
		await foreach (ChatResponseUpdate chunk in this._client!.GetStreamingResponseAsync(
			[new ChatMessage(ChatRole.User, "Count from 1 to 3")])) {
			chunks.Add(chunk);
		}

		Assert.NotEmpty(chunks);
		Assert.Contains(chunks, c => c.Text is { Length: > 0 });
	}

	[Fact]
	public async Task Integration_ToolCall() {
		if (this.SkipIfNoCredentials()) return;

ChatOptions options = new() {
			Tools = [AIFunctionFactory.Create(
				() => DateTime.UtcNow.ToString("HH:mm"),
				"GetCurrentTime", "Returns the current UTC time")]
		};

		ChatResponse response = await this._client!.GetResponseAsync(
			[new ChatMessage(ChatRole.User, "What time is it? Use the GetCurrentTime tool.")],
			options);

		Assert.Contains(response.Messages,
			m => m.Contents.OfType<FunctionCallContent>().Any());
	}

	[Fact]
	public async Task Integration_TokenRefresh() {
		if (this.SkipIfNoCredentials()) return;

ChatResponse first = await this._client!.GetResponseAsync(
			[new ChatMessage(ChatRole.User, "Say hi")]);
		Assert.NotNull(first.Text);

		ChatResponse second = await this._client!.GetResponseAsync(
			[new ChatMessage(ChatRole.User, "Say bye")]);
		Assert.NotNull(second.Text);
	}

	[Fact]
	public async Task Integration_UsageReported() {
		if (this.SkipIfNoCredentials()) return;

List<ChatResponseUpdate> chunks = [];
		await foreach (ChatResponseUpdate chunk in this._client!.GetStreamingResponseAsync(
			[new ChatMessage(ChatRole.User, "Say ok")])) {
			chunks.Add(chunk);
		}

		Assert.Contains(chunks,
			c => c.Contents.OfType<UsageContent>().Any());
	}

	public void Dispose() {
		this._client?.Dispose();
		GC.SuppressFinalize(this);
	}
}
