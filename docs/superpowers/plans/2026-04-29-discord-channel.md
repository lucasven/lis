# Discord Channel Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

> **Prerequisite:** Complete [Multi-Channel Foundation](2026-04-29-multi-channel-foundation.md) and [Telegram Channel](2026-04-29-telegram-channel.md) (for pattern validation) first.

**Goal:** Add Discord as a messaging channel using Discord.Net, with a gateway-based bot that listens for messages and implements `IChannelClient`.

**Architecture:** Discord bots use a persistent WebSocket connection (gateway) rather than webhooks. Use `Discord.Net` library with a `BackgroundService` that connects to Discord and forwards messages to `IConversationService`. The `DiscordClient` implements `IChannelClient` for outbound messaging. A `DiscordFormatter` handles Markdown differences (Discord already uses standard Markdown, so this is minimal).

**Tech Stack:** Discord.Net 3.x NuGet, `IHostedService` for gateway connection, keyed `IChannelClient` registration

---

### Task 1: Add Discord.Net NuGet Package

**Files:**
- Modify: `Lis.Channels/Lis.Channels.csproj`

- [ ] **Step 1: Add package reference**

```xml
<PackageReference Include="Discord.Net" Version="3.*" />
```

- [ ] **Step 2: Restore and build**

Run: `dotnet restore && dotnet build`
Expected: SUCCESS

- [ ] **Step 3: Commit**

```bash
git add Lis.Channels/Lis.Channels.csproj
git commit -m "chore(channels): add Discord.Net NuGet package

Co-Authored-By: Claude Opus 4.6 <noreply@anthropic.com>"
```

---

### Task 2: Create DiscordOptions

**Files:**
- Create: `Lis.Channels/Discord/DiscordOptions.cs`

- [ ] **Step 1: Define options**

```csharp
namespace Lis.Channels.Discord;

public sealed class DiscordOptions {
	public required string BotToken { get; init; }
	public string? ApplicationId { get; init; }
}
```

- [ ] **Step 2: Commit**

```bash
git add Lis.Channels/Discord/DiscordOptions.cs
git commit -m "feat(discord): add DiscordOptions configuration

Co-Authored-By: Claude Opus 4.6 <noreply@anthropic.com>"
```

---

### Task 3: Create DiscordFormatter

**Files:**
- Create: `Lis.Channels/Discord/DiscordFormatter.cs`

- [ ] **Step 1: Implement formatter**

Discord uses standard Markdown, so the formatter is minimal — mostly passthrough:

```csharp
using Lis.Core.Channel;

namespace Lis.Channels.Discord;

public sealed class DiscordFormatter : IMessageFormatter {
	public string Format(string content) {
		// Discord already supports standard Markdown
		// Only limit message length to Discord's 2000-char limit
		return content.Length > 2000 ? content[..1997] + "..." : content;
	}
}
```

- [ ] **Step 2: Commit**

```bash
git add Lis.Channels/Discord/DiscordFormatter.cs
git commit -m "feat(discord): add DiscordFormatter

Co-Authored-By: Claude Opus 4.6 <noreply@anthropic.com>"
```

---

### Task 4: Implement DiscordClient (IChannelClient)

**Files:**
- Create: `Lis.Channels/Discord/DiscordClient.cs`

- [ ] **Step 1: Implement the channel client**

```csharp
using Discord.WebSocket;

using Lis.Core.Channel;
using Lis.Core.Util;

namespace Lis.Channels.Discord;

public sealed class DiscordClient(DiscordSocketClient bot, DiscordFormatter formatter) : IChannelClient {

	[Trace("DiscordClient > SendMessageAsync")]
	public async Task<string?> SendMessageAsync(
		string chatId, string message, string? replyToId = null, CancellationToken ct = default) {

		if (bot.GetChannel(ulong.Parse(chatId)) is not ISocketMessageChannel channel)
			return null;

		string formatted = formatter.Format(message);

		Discord.IUserMessage? sent;
		if (replyToId is not null && ulong.TryParse(replyToId, out ulong refId)) {
			var refMsg = await channel.GetMessageAsync(refId);
			sent = await channel.SendMessageAsync(
				text: formatted,
				messageReference: new Discord.MessageReference(refId));
		} else {
			sent = await channel.SendMessageAsync(formatted);
		}

		return sent?.Id.ToString();
	}

	[Trace("DiscordClient > SetTypingAsync")]
	public async Task SetTypingAsync(string chatId, CancellationToken ct = default) {
		if (bot.GetChannel(ulong.Parse(chatId)) is ISocketMessageChannel channel)
			await channel.TriggerTypingAsync();
	}

	[Trace("DiscordClient > StopTypingAsync")]
	public Task StopTypingAsync(string chatId, CancellationToken ct = default) {
		// Discord typing auto-clears after ~10s or when a message is sent
		return Task.CompletedTask;
	}

	[Trace("DiscordClient > MarkReadAsync")]
	public Task MarkReadAsync(string messageId, string chatId, CancellationToken ct = default) {
		// Discord Bot API doesn't support marking as read
		return Task.CompletedTask;
	}

	[Trace("DiscordClient > ReactAsync")]
	public async Task ReactAsync(string messageId, string chatId, string emoji, CancellationToken ct = default) {
		if (bot.GetChannel(ulong.Parse(chatId)) is not ISocketMessageChannel channel)
			return;

		var msg = await channel.GetMessageAsync(ulong.Parse(messageId));
		if (msg is Discord.IUserMessage userMsg)
			await userMsg.AddReactionAsync(new Discord.Emoji(emoji));
	}

	[Trace("DiscordClient > DownloadMediaAsync")]
	public async Task<MediaDownload?> DownloadMediaAsync(
		string messageId, string chatId, string? mediaPath = null, CancellationToken ct = default) {

		if (mediaPath is null) return null;

		using HttpClient http = new();
		byte[] data = await http.GetByteArrayAsync(mediaPath, ct);
		string mimeType = "application/octet-stream"; // Discord provides URL, not MIME
		return new MediaDownload(data, mimeType);
	}
}
```

- [ ] **Step 2: Build**

Run: `dotnet build`
Expected: SUCCESS

- [ ] **Step 3: Commit**

```bash
git add Lis.Channels/Discord/DiscordClient.cs
git commit -m "feat(discord): implement DiscordClient (IChannelClient)

Co-Authored-By: Claude Opus 4.6 <noreply@anthropic.com>"
```

---

### Task 5: Create Discord Gateway Service (BackgroundService)

**Files:**
- Create: `Lis.Channels/Discord/DiscordGatewayService.cs`

- [ ] **Step 1: Implement the hosted service**

```csharp
using Discord;
using Discord.WebSocket;

using Lis.Core.Channel;

using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Lis.Channels.Discord;

public sealed class DiscordGatewayService(
	DiscordSocketClient           bot,
	IConversationService          conversationService,
	ILogger<DiscordGatewayService> logger) : BackgroundService {

	protected override async Task ExecuteAsync(CancellationToken stoppingToken) {
		bot.MessageReceived += OnMessageReceived;
		bot.Log += OnLog;

		await bot.LoginAsync(TokenType.Bot, bot.TokenValidated is not null
			? null  // Already logged in
			: Environment.GetEnvironmentVariable("DISCORD_BOT_TOKEN"));
		await bot.StartAsync();

		// Keep alive until shutdown
		await Task.Delay(Timeout.Infinite, stoppingToken);
	}

	public override async Task StopAsync(CancellationToken cancellationToken) {
		await bot.StopAsync();
		await base.StopAsync(cancellationToken);
	}

	private Task OnMessageReceived(SocketMessage socketMsg) {
		// Ignore bot's own messages and system messages
		if (socketMsg.Author.IsBot) return Task.CompletedTask;
		if (socketMsg is not SocketUserMessage userMsg) return Task.CompletedTask;

		bool isGroup = socketMsg.Channel is SocketTextChannel;
		bool isBotMentioned = userMsg.MentionedUsers.Any(u => u.Id == bot.CurrentUser?.Id);

		// Extract media
		string? mediaType = null;
		string? mediaPath = null;
		if (userMsg.Attachments.FirstOrDefault() is { } attachment) {
			mediaType = attachment.ContentType?.Split('/')[0] ?? "file";
			mediaPath = attachment.Url;
		}

		IncomingMessage message = new() {
			ExternalId     = userMsg.Id.ToString(),
			ChatId         = userMsg.Channel.Id.ToString(),
			SenderId       = userMsg.Author.Id.ToString(),
			SenderName     = userMsg.Author.GlobalName ?? userMsg.Author.Username,
			Timestamp      = userMsg.Timestamp,
			IsFromMe       = false,
			IsGroup        = isGroup,
			Body           = userMsg.Content,
			RepliedId      = userMsg.ReferencedMessage?.Id.ToString(),
			RepliedContent = userMsg.ReferencedMessage?.Content,
			IsBotMentioned = isBotMentioned,
			ChatName       = isGroup ? (userMsg.Channel as SocketTextChannel)?.Name : null,
			ChatTopic      = isGroup ? (userMsg.Channel as SocketTextChannel)?.Topic : null,
			MediaType      = mediaType,
			MediaPath      = mediaPath,
			Channel        = "discord"
		};

		_ = Task.Run(async () => {
			try {
				await conversationService.HandleIncomingAsync(message, CancellationToken.None);
			} catch (Exception ex) {
				logger.LogError(ex, "Error processing Discord message {MessageId}", userMsg.Id);
			}
		});

		return Task.CompletedTask;
	}

	private Task OnLog(LogMessage log) {
		LogLevel level = log.Severity switch {
			LogSeverity.Critical => LogLevel.Critical,
			LogSeverity.Error    => LogLevel.Error,
			LogSeverity.Warning  => LogLevel.Warning,
			LogSeverity.Info     => LogLevel.Information,
			LogSeverity.Verbose  => LogLevel.Debug,
			LogSeverity.Debug    => LogLevel.Trace,
			_                    => LogLevel.Information
		};
		logger.Log(level, log.Exception, "Discord.Net: {Message}", log.Message);
		return Task.CompletedTask;
	}
}
```

- [ ] **Step 2: Build**

Run: `dotnet build`
Expected: SUCCESS

- [ ] **Step 3: Commit**

```bash
git add Lis.Channels/Discord/DiscordGatewayService.cs
git commit -m "feat(discord): add gateway BackgroundService for message listening

Co-Authored-By: Claude Opus 4.6 <noreply@anthropic.com>"
```

---

### Task 6: Create DiscordSetup Registration

**Files:**
- Create: `Lis.Channels/Discord/DiscordSetup.cs`

- [ ] **Step 1: Implement setup**

```csharp
using Discord;
using Discord.WebSocket;

using Lis.Core.Channel;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Lis.Channels.Discord;

public static class DiscordSetup {
	public static IServiceCollection AddDiscord(this IServiceCollection services) {
		DiscordOptions opts = new() {
			BotToken      = Env("DISCORD_BOT_TOKEN"),
			ApplicationId = Env("DISCORD_APPLICATION_ID"),
		};

		services.AddSingleton(Options.Create(opts));

		// Discord.Net socket client (singleton — maintains gateway connection)
		var config = new DiscordSocketConfig {
			GatewayIntents = GatewayIntents.Guilds
			               | GatewayIntents.GuildMessages
			               | GatewayIntents.DirectMessages
			               | GatewayIntents.MessageContent,
			LogLevel = LogSeverity.Info
		};

		var socketClient = new DiscordSocketClient(config);
		services.AddSingleton(socketClient);

		// Formatter and channel client
		services.AddSingleton<DiscordFormatter>();
		services.AddKeyedScoped<IChannelClient, DiscordClient>("discord");

		// Gateway background service
		services.AddHostedService<DiscordGatewayService>();

		return services;
	}

	private static string Env(string key) => Environment.GetEnvironmentVariable(key) ?? "";
}
```

- [ ] **Step 2: Build**

Run: `dotnet build`
Expected: SUCCESS

- [ ] **Step 3: Commit**

```bash
git add Lis.Channels/Discord/DiscordSetup.cs
git commit -m "feat(discord): add DI registration and gateway setup

Co-Authored-By: Claude Opus 4.6 <noreply@anthropic.com>"
```

---

### Task 7: Wire Discord into Program.cs

**Files:**
- Modify: `Lis.Api/Program.cs`

- [ ] **Step 1: Add registration**

Add `using Lis.Channels.Discord;` and after the channel registrations:
```csharp
if (Env("DISCORD_ENABLED") == "true") builder.Services.AddDiscord();
```

- [ ] **Step 2: Build, test, and commit**

Run: `dotnet build && dotnet test Lis.Tests/Lis.Tests.csproj`

```bash
git add Lis.Api/Program.cs
git commit -m "feat(discord): wire Discord channel into Program.cs

Co-Authored-By: Claude Opus 4.6 <noreply@anthropic.com>"
```

---

### Task 8: End-to-End Verification and Cleanup

- [ ] **Step 1: Build and test**
- [ ] **Step 2: Code cleanup with jb cleanupcode**
- [ ] **Step 3: Final commit**
