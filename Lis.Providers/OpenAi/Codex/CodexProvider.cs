using Lis.Core.Channel;
using Lis.Core.Configuration;

using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;

namespace Lis.Providers.OpenAi.Codex;

public static class CodexProvider {
	public static IServiceCollection AddCodex(this IServiceCollection services) {
		CodexOptions opts = new() {
			AccessToken     = Env("CODEX_ACCESS_TOKEN"),
			RefreshToken    = Env("CODEX_REFRESH_TOKEN"),
			Model           = Env("CODEX_MODEL") is { Length: > 0 } m ? m : "codex-1",
			MaxTokens       = EnvInt("CODEX_MAX_TOKENS", 16384),
			ContextBudget   = EnvInt("CODEX_CONTEXT_BUDGET", 100000),
			ReasoningEffort = Env("CODEX_REASONING_EFFORT") is { Length: > 0 } re ? re : null,
			BaseUrl         = Env("CODEX_BASE_URL") is { Length: > 0 } url ? url : "https://chatgpt.com/backend-api",
			Transport       = Env("CODEX_TRANSPORT") switch {
				"sse"       => CodexTransport.Sse,
				"websocket" => CodexTransport.WebSocket,
				_           => CodexTransport.Auto
			}
		};

		HttpClient httpClient = new();
		CodexTokenManager tokenManager = new(opts, new HttpClient());
		CodexWebSocketTransport wsTransport = new(tokenManager, opts);
		CodexChatClient chatClient = new(tokenManager, opts, httpClient, wsTransport);

		// Register as keyed services only — Anthropic remains the unkeyed default (INV-19)
		services.AddKeyedSingleton<IChatClient>("codex", chatClient);
		services.AddKeyedSingleton<IUsageExtractor>("codex", new CodexUsageExtractor());
		services.AddKeyedSingleton<ITokenCounter>("codex", new CodexTokenCounter());
		services.AddKeyedSingleton("codex", new ModelSettings {
			Model = opts.Model, MaxTokens = opts.MaxTokens,
			ContextBudget = opts.ContextBudget, ThinkingEffort = opts.ReasoningEffort
		});

		return services;
	}

	private static string Env(string key) => Environment.GetEnvironmentVariable(key) ?? "";

	private static int EnvInt(string key, int fallback) =>
		int.TryParse(Environment.GetEnvironmentVariable(key), out int v) ? v : fallback;
}
