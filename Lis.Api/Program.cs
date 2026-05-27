using Grafana.OpenTelemetry;

using Lis.Agent;
using Lis.Channels.Mattermost;
using Lis.Channels.Telegram;
using Lis.Channels.WhatsApp;
using Telegram.Bot;
using Telegram.Bot.Types.Enums;
using Lis.Core.Channel;
using Lis.Core.Configuration;
using Lis.Core.Observability;
using Lis.Core.Util;
using Lis.Persistence;
using Lis.Persistence.Entities;
using Lis.Providers.Anthropic;
using Lis.Providers.Embedding;
using Lis.Providers.OpenAi;
using Lis.Providers.OpenAi.Codex;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;
using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using System.Text.RegularExpressions;

DotEnv.Load();

// SK telemetry
AppContext.SetSwitch("Microsoft.SemanticKernel.Experimental.GenAI.EnableOTelDiagnostics",          true);
AppContext.SetSwitch("Microsoft.SemanticKernel.Experimental.GenAI.EnableOTelDiagnosticsSensitive", true);

WebApplicationBuilder builder = WebApplication.CreateBuilder(args);

// Controllers + JSON serialization
builder.Services.AddControllers()
	   .AddApplicationPart(typeof(GowaWebhookController).Assembly)
	   .AddJsonOptions(opt => JsonOpt.Configure(opt.JsonSerializerOptions));

// Error handling
builder.Services.AddProblemDetails();

// Observability
builder.Services.AddOpenTelemetry()
	   .ConfigureResource(rb => rb.AddService(serviceName: "lis", serviceNamespace: "lis"))
	   .WithMetrics(metrics => {
		   metrics.AddAspNetCoreInstrumentation();
		   metrics.AddHttpClientInstrumentation();
		   metrics.AddRuntimeInstrumentation();
		   metrics.AddMeter("Microsoft.SemanticKernel*");
	   })
	   .WithTracing(tracing => {
		   tracing.AddAspNetCoreInstrumentation();
		   tracing.AddHttpClientInstrumentation(o => {
		   o.RecordException = true;
		   o.EnrichWithHttpRequestMessage = (activity, request) => {
			   if (request.RequestUri?.Host == "api.telegram.org")
				   activity.SetTag("url.full", RedactTelegramToken(request.RequestUri));
		   };
	   });
		   tracing.AddSource("Microsoft.SemanticKernel*");
		   tracing.AddSource(TraceAspect.ActivitySource.Name);
	   })
	   .WithLogging(logging => logging.AddOtlpExporter())
	   .UseGrafana();
builder.Logging.AddOpenTelemetry(logging => {
	logging.IncludeFormattedMessage = true;
	logging.IncludeScopes           = true;
});

// Opik LLM observability (native REST client)
if (Env("OPIK_ENABLED") == "true") {
	OpikOptions opikOpts = new() {
		BaseUrl   = Env("OPIK_BASE_URL") is { Length: > 0 } u ? u : "https://www.comet.com/opik",
		ApiKey    = Env("OPIK_API_KEY") is { Length: > 0 } k ? k : null,
		Workspace = Env("OPIK_WORKSPACE") is { Length: > 0 } w ? w : null,
		Project   = Env("OPIK_PROJECT") is { Length: > 0 } proj ? proj : "lis"
	};
	builder.Services.AddSingleton(new OpikClient(opikOpts));
	builder.Services.AddScoped<OpikTracer>(sp =>
		new OpikTracer(sp.GetRequiredService<OpikClient>(), opikOpts.Project, sp.GetRequiredService<ILogger<OpikTracer>>()));
}
else {
	builder.Services.AddScoped<OpikTracer>(_ => null!);
}

// Configuration
builder.Services.AddSingleton(Options.Create(new LisOptions {
												 OwnerJid                = Env("LIS_OWNER_JID"),
												 Timezone                = Env("LIS_TIMEZONE") is { Length: > 0 } t ? t : "E. South America Standard Time",
												 MessageDebounceMs       = EnvInt("LIS_MESSAGE_DEBOUNCE_MS",       3000),
												 ToolNotifications       = Env("LIS_TOOL_NOTIFICATIONS") != "false",
												 KeepRecentTokens        = EnvInt("LIS_KEEP_RECENT_TOKENS",        4000),
												 ToolPruneThreshold      = EnvInt("LIS_TOOL_PRUNE_THRESHOLD",      8000),
												 ToolKeepThreshold       = EnvInt("LIS_TOOL_KEEP_THRESHOLD",       2000),
												 CompactionThreshold     = EnvInt("LIS_COMPACTION_THRESHOLD",      0),
												 CompactionNotify        = Env("LIS_COMPACTION_NOTIFY") != "false",
												 CompactionModel         = Env("LIS_COMPACTION_MODEL"),
												 ToolSummarizationPolicy = Env("LIS_TOOL_SUMMARIZATION_POLICY") is { Length: > 0 } p ? p : "auto",
												 ReactOnMessageQueued      = Env("LIS_REACT_ON_MESSAGE_QUEUED") == "true",
												 ReactOnMessageQueuedEmoji = Env("LIS_REACT_ON_MESSAGE_QUEUED_EMOJI") is { Length: > 0 } e ? e : "🕐",
												 ResumeTokenBudget       = EnvInt("LIS_RESUME_TOKEN_BUDGET",       0),
											 GroupContextMessages    = EnvInt("LIS_GROUP_CONTEXT_MESSAGES",    5),
											 NewSessionOnAgentSwitch = Env("LIS_NEW_SESSION_ON_AGENT_SWITCH") != "false"
											 }));

// Database
if (Env("DATABASE_URL") is { Length: > 0 } dbUrl)
	builder.Configuration["ConnectionStrings:lisdb"] = dbUrl;
builder.AddNpgsqlDbContext<LisDbContext>("lisdb",
										 configureDbContextOptions: options => options.UseNpgsql(o => o.UseVector()));

// Defaults (providers override these)
builder.Services.AddSingleton(new ModelSettings());

// AI Providers
if (Env("ANTHROPIC_ENABLED") == "true") {
	builder.Services.AddAnthropic();
	builder.Services.AddKeyedSingleton<IChatClient>("anthropic",
		(sp, _) => sp.GetRequiredService<IChatClient>());
	builder.Services.AddKeyedSingleton<IUsageExtractor>("anthropic",
		(sp, _) => sp.GetRequiredService<IUsageExtractor>());
	builder.Services.AddKeyedSingleton<ITokenCounter>("anthropic",
		(sp, _) => sp.GetRequiredService<ITokenCounter>());
}

if (Env("CODEX_ENABLED") == "true") builder.Services.AddCodex();

// Audio transcription (optional — audio messages fall back to placeholder without it)
if (Env("OPENAI_API_KEY") is { Length: > 0 }) builder.Services.AddOpenAiTranscription();

// Compaction client (keyed IChatClient for summarization — falls back to main)
if (Env("LIS_COMPACTION_PROVIDER") is { Length: > 0 } compProvider
    && compProvider.Equals("anthropic", StringComparison.OrdinalIgnoreCase)) {
	string compApiKey = Env("LIS_COMPACTION_API_KEY");
	Anthropic.SDK.AnthropicClient compClient = new(compApiKey);
	builder.Services.AddKeyedSingleton<IChatClient>("compaction", compClient.Messages);
} else {
	// Reuse main client for compaction
	builder.Services.AddKeyedSingleton<IChatClient>("compaction",
		(sp, _) => sp.GetRequiredService<IChatClient>());
}

// Embedding (optional — enables vector search for memories)
if (Env("MEMORIES_EMBEDDING_ENABLED") == "true") builder.Services.AddEmbedding();

// Channels
if (Env("GOWA_ENABLED") == "true") builder.Services.AddWhatsApp();
if (Env("TELEGRAM_ENABLED") == "true") builder.Services.AddTelegram();
if (Env("MATTERMOST_ENABLED") == "true") builder.Services.AddMattermost();

// Channel provider (resolves keyed IChannelClient by name)
builder.Services.AddScoped<IChannelClientProvider, ChannelClientProvider>();

// Application services
builder.Services.AddSingleton<ContextWindowBuilder>();
builder.Services.AddSingleton<PromptComposer>();
builder.Services.AddSingleton<ToolRunner>();

// Conversation + Agent require both AI and at least one Channel
bool hasChannel = Env("GOWA_ENABLED") == "true"
              || Env("TELEGRAM_ENABLED") == "true"
              || Env("DISCORD_ENABLED") == "true"
              || Env("MATTERMOST_ENABLED") == "true";

bool hasProvider = Env("ANTHROPIC_ENABLED") == "true" || Env("CODEX_ENABLED") == "true";

if (hasProvider && hasChannel) {
	builder.Services.AddScoped<ConversationService>();
	builder.Services.AddSingleton<IConversationService, MessageDebouncer>();
	builder.Services.AddLisAgent();
	builder.Services.AddHostedService<CronSchedulerService>();
}

WebApplication app = builder.Build();

// Apply migrations on startup + seed default agent
using (IServiceScope scope = app.Services.CreateScope()) {
	LisDbContext db = scope.ServiceProvider.GetRequiredService<LisDbContext>();
	await db.Database.MigrateAsync();

	AgentService agentService = scope.ServiceProvider.GetRequiredService<AgentService>();
	ModelSettings envModelSettings = scope.ServiceProvider.GetRequiredService<ModelSettings>();
	LisOptions lisOpts = scope.ServiceProvider.GetRequiredService<IOptions<LisOptions>>().Value;
	await agentService.SeedDefaultAsync(db, envModelSettings, lisOpts, CancellationToken.None);
}

// Wire Codex token persistence and hot-reload callbacks
if (Env("CODEX_ENABLED") == "true") {
	CodexTokenManager? tm = app.Services.GetKeyedService<CodexTokenManager>("codex");
	CodexAuthService? authSvc = app.Services.GetService<CodexAuthService>();
	if (tm is not null && authSvc is not null) {
		tm.OnTokensRefreshed = (access, refresh, expiresIn) =>
			authSvc.PersistTokensAsync(access, refresh, expiresIn);
		authSvc.OnTokensAcquired = (access, refresh) =>
			tm.UpdateTokens(access, refresh);
	}
}

// Map agent DB IDs → bot registry (for multi-bot Mattermost routing)
if (app.Services.GetService<MattermostBotRegistry>() is { } botRegistry) {
	using IServiceScope regScope = app.Services.CreateScope();
	LisDbContext regDb = regScope.ServiceProvider.GetRequiredService<LisDbContext>();
	foreach (AgentEntity a in await regDb.Agents.ToListAsync())
		botRegistry.MapAgentId(a.Id, a.Name);
}

// Flush queued messages from crash recovery
if (app.Services.GetService<IConversationService>() is MessageDebouncer debouncer)
	await debouncer.TriggerPendingResponsesAsync();

// Middleware
app.UseExceptionHandler();

// Endpoints
app.MapControllers();

// Register Telegram webhook
if (Env("TELEGRAM_ENABLED") == "true" && Env("TELEGRAM_WEBHOOK_URL") is { Length: > 0 } webhookUrl) {
	TelegramBotClient telegramBot = app.Services.GetRequiredService<TelegramBotClient>();
	TelegramOptions   telegramOpts = app.Services.GetRequiredService<IOptions<TelegramOptions>>().Value;
	await telegramBot.SetWebhook(
		url: webhookUrl,
		certificate: null,
		ipAddress: null,
		maxConnections: null,
		allowedUpdates: [UpdateType.Message],
		dropPendingUpdates: false,
		secretToken: telegramOpts.WebhookSecret);
}

await app.RunAsync();
return;

// Helpers
static string Env(string key) {
	return Environment.GetEnvironmentVariable(key) ?? "";
}

static int EnvInt(string key, int fallback) {
	return int.TryParse(Environment.GetEnvironmentVariable(key), out int v) ? v : fallback;
}

static string RedactTelegramToken(Uri uri) {
	return Regex.Replace(uri.ToString(), @"/bot[^/]+/", "/bot***/");
}
