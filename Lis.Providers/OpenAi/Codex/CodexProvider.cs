using Lis.Core.Channel;
using Lis.Core.Configuration;

using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;

namespace Lis.Providers.OpenAi.Codex;

public static class CodexProvider {
	public static IServiceCollection AddCodex(this IServiceCollection services) {
		string envAccess  = Env("CODEX_ACCESS_TOKEN");
		string envRefresh = Env("CODEX_REFRESH_TOKEN");

		// Try loading persisted tokens (from previous OAuth or refresh)
		(string? persistedAccess, string? persistedRefresh) = LoadPersistedTokens();

		CodexOptions opts = new() {
			AccessToken     = persistedAccess ?? envAccess,
			RefreshToken    = persistedRefresh ?? envRefresh,
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
		services.AddKeyedSingleton("codex", tokenManager);
		services.AddKeyedSingleton("codex", new ModelSettings {
			Model = opts.Model, MaxTokens = opts.MaxTokens,
			ContextBudget = opts.ContextBudget, ThinkingEffort = opts.ReasoningEffort
		});

		return services;
	}

	private static (string? Access, string? Refresh) LoadPersistedTokens() {
		try {
			string path = Path.Combine(AppContext.BaseDirectory, "auth.json");
			if (!File.Exists(path)) return (null, null);

			string json = File.ReadAllText(path);
			var root = System.Text.Json.Nodes.JsonNode.Parse(json);
			var codex = root?["codex"];
			if (codex is null) return (null, null);

			string? access  = codex["access_token"]?.GetValue<string>();
			string? refresh = codex["refresh_token"]?.GetValue<string>();
			if (access is { Length: > 0 } && refresh is { Length: > 0 })
				return (access, refresh);
			return (null, null);
		} catch {
			return (null, null);
		}
	}

	private static string Env(string key) => Environment.GetEnvironmentVariable(key) ?? "";

	private static int EnvInt(string key, int fallback) =>
		int.TryParse(Environment.GetEnvironmentVariable(key), out int v) ? v : fallback;
}
