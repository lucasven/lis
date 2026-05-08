using Lis.Core.Channel;

using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;

namespace Lis.Tests.Providers.Codex;

public class ProviderRoutingTests {
	// INV-17: Provider field determines IChatClient
	[Fact]
	public void Resolves_Correct_Client_By_ProviderKey() {
		ServiceCollection services = new();

		FakeChatClient anthropicClient = new("anthropic");
		FakeChatClient codexClient = new("codex");

		services.AddSingleton<IChatClient>(anthropicClient);
		services.AddKeyedSingleton<IChatClient>("anthropic", anthropicClient);
		services.AddKeyedSingleton<IChatClient>("codex", codexClient);

		ServiceProvider sp = services.BuildServiceProvider();

		IChatClient resolvedAnthropic = sp.GetRequiredKeyedService<IChatClient>("anthropic");
		IChatClient resolvedCodex = sp.GetRequiredKeyedService<IChatClient>("codex");

		Assert.Same(anthropicClient, resolvedAnthropic);
		Assert.Same(codexClient, resolvedCodex);
		Assert.NotSame(resolvedAnthropic, resolvedCodex);
	}

	// INV-18: Missing provider throws
	[Fact]
	public void Missing_Provider_Throws() {
		ServiceCollection services = new();

		FakeChatClient anthropicClient = new("anthropic");
		services.AddSingleton<IChatClient>(anthropicClient);
		services.AddKeyedSingleton<IChatClient>("anthropic", anthropicClient);
		// Codex NOT registered

		ServiceProvider sp = services.BuildServiceProvider();

		Assert.Throws<InvalidOperationException>(
			() => sp.GetRequiredKeyedService<IChatClient>("codex"));
	}

	// INV-19: Unkeyed resolves to Anthropic (backward compatibility)
	[Fact]
	public void Unkeyed_Resolves_To_Anthropic() {
		ServiceCollection services = new();

		FakeChatClient anthropicClient = new("anthropic");
		FakeChatClient codexClient = new("codex");

		// Anthropic registered as unkeyed default + keyed alias (mirrors Program.cs)
		services.AddSingleton<IChatClient>(anthropicClient);
		services.AddKeyedSingleton<IChatClient>("anthropic",
			(_, _) => anthropicClient);
		services.AddKeyedSingleton<IChatClient>("codex", codexClient);

		ServiceProvider sp = services.BuildServiceProvider();

		IChatClient unkeyed = sp.GetRequiredService<IChatClient>();
		IChatClient keyedAnthropic = sp.GetRequiredKeyedService<IChatClient>("anthropic");

		Assert.Same(anthropicClient, unkeyed);
		Assert.Same(anthropicClient, keyedAnthropic);
	}

	// INV-17: IUsageExtractor also keyed per provider
	[Fact]
	public void UsageExtractor_Resolved_By_ProviderKey() {
		ServiceCollection services = new();

		FakeUsageExtractor anthropicExtractor = new("anthropic");
		FakeUsageExtractor codexExtractor = new("codex");

		services.AddSingleton<IUsageExtractor>(anthropicExtractor);
		services.AddKeyedSingleton<IUsageExtractor>("anthropic", anthropicExtractor);
		services.AddKeyedSingleton<IUsageExtractor>("codex", codexExtractor);

		ServiceProvider sp = services.BuildServiceProvider();

		IUsageExtractor resolvedAnthropic = sp.GetRequiredKeyedService<IUsageExtractor>("anthropic");
		IUsageExtractor resolvedCodex = sp.GetRequiredKeyedService<IUsageExtractor>("codex");

		Assert.Same(anthropicExtractor, resolvedAnthropic);
		Assert.Same(codexExtractor, resolvedCodex);
	}

	// INV-17: ITokenCounter also keyed per provider
	[Fact]
	public void TokenCounter_Resolved_By_ProviderKey() {
		ServiceCollection services = new();

		FakeTokenCounter anthropicCounter = new("anthropic");
		FakeTokenCounter codexCounter = new("codex");

		services.AddSingleton<ITokenCounter>(anthropicCounter);
		services.AddKeyedSingleton<ITokenCounter>("anthropic", anthropicCounter);
		services.AddKeyedSingleton<ITokenCounter>("codex", codexCounter);

		ServiceProvider sp = services.BuildServiceProvider();

		ITokenCounter resolvedAnthropic = sp.GetRequiredKeyedService<ITokenCounter>("anthropic");
		ITokenCounter resolvedCodex = sp.GetRequiredKeyedService<ITokenCounter>("codex");

		Assert.Same(anthropicCounter, resolvedAnthropic);
		Assert.Same(codexCounter, resolvedCodex);
	}

	// INV-18: All three services throw when provider not registered
	[Theory]
	[InlineData("codex")]
	[InlineData("nonexistent")]
	public void All_Services_Throw_For_Missing_Provider(string missingKey) {
		ServiceCollection services = new();
		ServiceProvider sp = services.BuildServiceProvider();

		Assert.Throws<InvalidOperationException>(
			() => sp.GetRequiredKeyedService<IChatClient>(missingKey));
		Assert.Throws<InvalidOperationException>(
			() => sp.GetRequiredKeyedService<IUsageExtractor>(missingKey));
		Assert.Throws<InvalidOperationException>(
			() => sp.GetRequiredKeyedService<ITokenCounter>(missingKey));
	}

	private sealed class FakeChatClient(string provider) : IChatClient {
		public string Provider => provider;

		public Task<ChatResponse> GetResponseAsync(
			IEnumerable<ChatMessage> chatMessages,
			ChatOptions? options = null,
			CancellationToken cancellationToken = default) =>
			Task.FromResult(new ChatResponse([]));

		public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
			IEnumerable<ChatMessage> chatMessages,
			ChatOptions? options = null,
			CancellationToken cancellationToken = default) =>
			AsyncEnumerable.Empty<ChatResponseUpdate>();

		public ChatClientMetadata Metadata => new(provider);
		public object? GetService(Type serviceType, object? serviceKey = null) => null;
		public void Dispose() { }
	}

	private sealed class FakeUsageExtractor(string provider) : IUsageExtractor {
		public string Provider => provider;
		public TokenUsage? Extract(IReadOnlyDictionary<string, object?>? metadata) => null;
	}

	private sealed class FakeTokenCounter(string provider) : ITokenCounter {
		public string Provider => provider;
		public Task<int?> CountAsync(string requestBodyJson, CancellationToken ct = default) =>
			Task.FromResult<int?>(null);
	}
}
