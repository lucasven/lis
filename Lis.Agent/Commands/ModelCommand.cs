using System.Text.RegularExpressions;

using Lis.Core.Util;

using Microsoft.Extensions.DependencyInjection;

namespace Lis.Agent.Commands;

public sealed class ModelCommand(IServiceScopeFactory scopeFactory, ErrorSuppressionService errorSuppression) : IChatCommand {
	// Kept for DI consistency; may be used for cross-agent model queries later.
	private readonly IServiceScopeFactory _scopeFactory = scopeFactory;

	public string[] Triggers => ["/model"];
	public bool OwnerOnly => true;

	[Trace("ModelCommand > ExecuteAsync")]
	public async Task<string> ExecuteAsync(CommandContext ctx, CancellationToken ct) {
		if (ctx.Args is null or { Length: 0 })
			return $"🧠 Current model: {ctx.Agent.Model} (provider: {ctx.Agent.Provider})";

		(string provider, string model) = ParseModel(ctx.Args.Trim(), ctx.Agent.Provider);

		ctx.Agent.Model     = model;
		ctx.Agent.Provider  = provider;
		ctx.Agent.UpdatedAt = DateTimeOffset.UtcNow;
		await ctx.Db.SaveChangesAsync(ct);

		errorSuppression.Clear(ctx.Agent.Id);

		return $"✅ Model updated to '{model}' (provider: {provider}).";
	}

	internal static (string Provider, string Model) ParseModel(string input, string currentProvider) {
		// Explicit provider/model format (e.g. "codex/gpt-5.4", "anthropic/claude-opus-4-6")
		int slash = input.IndexOf('/');
		if (slash > 0 && slash < input.Length - 1) {
			string prefix = input[..slash];
			string model  = input[(slash + 1)..];
			return (prefix, model);
		}

		// Infer provider from model name pattern
		string provider = DetectProvider(input) ?? currentProvider;
		return (provider, input);
	}

	internal static string? DetectProvider(string model) {
		if (Regex.IsMatch(model, @"^(claude-)", RegexOptions.IgnoreCase))
			return "anthropic";
		if (Regex.IsMatch(model, @"^(gpt-|codex-|o[13]-|o[13]p-|chatgpt-)", RegexOptions.IgnoreCase))
			return "codex";
		return null;
	}
}
