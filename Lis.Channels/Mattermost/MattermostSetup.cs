using System.Net.Http.Headers;
using System.Text.Json;

using Lis.Core.Channel;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Lis.Channels.Mattermost;

public static class MattermostSetup {
	public static IServiceCollection AddMattermost(this IServiceCollection services) {
		string baseUrl = Env("MATTERMOST_URL");
		List<MattermostBotConfig> bots = ParseBots();

		MattermostOptions opts = new() { BaseUrl = baseUrl, Bots = bots };
		services.AddSingleton(Options.Create(opts));

		// Register a named HttpClient per bot (each with its own Bearer token)
		foreach (MattermostBotConfig bot in bots) {
			services.AddHttpClient($"mattermost-{bot.AgentName}", client => {
				client.BaseAddress = new Uri(baseUrl);
				client.DefaultRequestHeaders.Authorization =
					new AuthenticationHeaderValue("Bearer", bot.Token);
			}).AddStandardResilienceHandler(o => {
				o.Retry.MaxRetryAttempts         = 1;
				o.Retry.ShouldHandle             = _ => ValueTask.FromResult(false);
				o.CircuitBreaker.SamplingDuration = TimeSpan.FromSeconds(120);
				o.AttemptTimeout.Timeout          = TimeSpan.FromSeconds(60);
				o.TotalRequestTimeout.Timeout     = TimeSpan.FromSeconds(120);
			});
		}

		services.AddSingleton<MattermostBotRegistry>();
		services.AddSingleton<MattermostFormatter>();
		services.AddSingleton<MattermostWebSocketService>();
		services.AddHostedService(sp => sp.GetRequiredService<MattermostWebSocketService>());
		services.AddKeyedScoped<IChannelClient, MattermostClient>("mattermost");

		return services;
	}

	private static List<MattermostBotConfig> ParseBots() {
		string botsJson = Env("MATTERMOST_BOTS");

		if (botsJson is { Length: > 0 })
			return JsonSerializer.Deserialize<List<MattermostBotConfig>>(botsJson, JsonOpts) ?? [];

		// Backward compat: single bot from legacy env vars
		string token  = Env("MATTERMOST_BOT_TOKEN");
		string userId = Env("MATTERMOST_BOT_USER_ID");
		if (token is { Length: > 0 })
			return [new MattermostBotConfig { AgentName = "default", Token = token, UserId = userId }];

		return [];
	}

	private static string Env(string key) => Environment.GetEnvironmentVariable(key) ?? "";

	private static readonly JsonSerializerOptions JsonOpts = new() {
		PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
	};
}
