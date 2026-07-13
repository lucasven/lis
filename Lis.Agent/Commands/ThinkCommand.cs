using Lis.Core.Util;

using Microsoft.Extensions.Logging.Abstractions;

namespace Lis.Agent.Commands;

public sealed class ThinkCommand : IChatCommand {
	private const string Usage = "low | medium | high | xhigh | max | <token budget ≥ 1024> | off";

	private static readonly string[] Levels = ["low", "medium", "high", "xhigh", "max"];

	public string[] Triggers => ["/think"];
	public bool OwnerOnly => true;

	[Trace("ThinkCommand > ExecuteAsync")]
	public async Task<string> ExecuteAsync(CommandContext ctx, CancellationToken ct) {
		if (ctx.Args is null or { Length: 0 })
			return ctx.Agent.ThinkingEffort is { Length: > 0 } current
				? $"🧠 Thinking effort: {current}\nUse /think {Usage} to change."
				: $"🧠 Thinking is off.\nUse /think {Usage} to enable.";

		string arg = ctx.Args.Trim().ToLowerInvariant();

		if (arg is "off" or "none" or "disable") {
			await SaveAsync(ctx, null, ct);
			return "✅ Thinking disabled.";
		}

		bool isLevel  = Levels.Contains(arg);
		bool isBudget = int.TryParse(arg, out int tokens) && tokens >= 1024;
		if (!isLevel && !isBudget) return $"⚠️ Invalid effort '{ctx.Args.Trim()}'. Use /think {Usage}.";

		// xhigh only exists on adaptive-thinking models — BuildThinkingFields would silently disable it
		if (arg == "xhigh" && ConversationService.BuildThinkingFields(ctx.Agent.Model, "xhigh", NullLogger.Instance).Fields is null)
			return $"⚠️ xhigh requires an adaptive-thinking model (Claude 4.6+); '{ctx.Agent.Model}' doesn't support it.";

		await SaveAsync(ctx, arg, ct);
		return $"✅ Thinking effort set to '{arg}'.";
	}

	private static async Task SaveAsync(CommandContext ctx, string? effort, CancellationToken ct) {
		ctx.Agent.ThinkingEffort = effort;
		ctx.Agent.UpdatedAt      = DateTimeOffset.UtcNow;
		await ctx.Db.SaveChangesAsync(ct);
	}
}
