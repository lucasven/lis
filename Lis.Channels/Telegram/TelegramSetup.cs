using Lis.Core.Channel;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

using Telegram.Bot;

namespace Lis.Channels.Telegram;

public static class TelegramSetup {
	public static IServiceCollection AddTelegram(this IServiceCollection services) {
		TelegramOptions opts = new() {
			BotToken      = Env("TELEGRAM_BOT_TOKEN"),
			WebhookSecret = Env("TELEGRAM_WEBHOOK_SECRET"),
		};

		services.AddSingleton(Options.Create(opts));
		services.AddSingleton(new TelegramBotClient(opts.BotToken));
		services.AddSingleton<TelegramFormatter>();
		services.AddKeyedScoped<IChannelClient, TelegramClient>("telegram");

		return services;
	}

	private static string Env(string key) => Environment.GetEnvironmentVariable(key) ?? "";
}
