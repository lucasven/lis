using Lis.Core.Util;

namespace Lis.Agent.Commands;

public sealed class AuthCommand(CodexAuthService codexAuthService, ErrorSuppressionService errorSuppression) : IChatCommand {
	public string[] Triggers => ["/auth"];
	public bool OwnerOnly => true;

	[Trace("AuthCommand > ExecuteAsync")]
	public Task<string> ExecuteAsync(CommandContext ctx, CancellationToken ct) {
		string provider = ctx.Args?.Trim().ToLowerInvariant() ?? "";

		if (provider is not "codex")
			return Task.FromResult("Usage: /auth codex — authenticate with OpenAI Codex via OAuth.");

		(string authUrl, _) = codexAuthService.StartAuth(ctx.Message.ChatId, ctx.Agent.Id);
		errorSuppression.Clear(ctx.Agent.Id);

		string response = $"🔐 Open this link to authenticate with Codex:\n\n{authUrl}\n\nAfter logging in, paste the redirect URL back here.";
		return Task.FromResult(response);
	}
}
