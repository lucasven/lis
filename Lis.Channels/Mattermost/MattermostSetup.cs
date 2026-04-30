using System.Net.Http.Headers;

using Lis.Core.Channel;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Lis.Channels.Mattermost;

public static class MattermostSetup {
	public static IServiceCollection AddMattermost(this IServiceCollection services) {
		MattermostOptions opts = new() {
			BaseUrl       = Env("MATTERMOST_URL"),
			BotToken      = Env("MATTERMOST_BOT_TOKEN"),
			WebhookSecret = Env("MATTERMOST_WEBHOOK_SECRET"),
			BotUserId     = Env("MATTERMOST_BOT_USER_ID"),
		};

		services.AddSingleton(Options.Create(opts));

		services.AddHttpClient<MattermostApiClient>((sp, client) => {
			client.BaseAddress = new Uri(opts.BaseUrl);
			client.DefaultRequestHeaders.Authorization =
				new AuthenticationHeaderValue("Bearer", opts.BotToken);
		});

		services.AddSingleton<MattermostFormatter>();
		services.AddKeyedScoped<IChannelClient, MattermostClient>("mattermost");

		return services;
	}

	private static string Env(string key) => Environment.GetEnvironmentVariable(key) ?? "";
}
