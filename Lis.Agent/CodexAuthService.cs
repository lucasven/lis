using System.Text.Json;
using System.Text.Json.Nodes;

using Lis.Core.Util;

using Microsoft.Extensions.Logging;

namespace Lis.Agent;

/// <summary>
/// Manages Codex token persistence to auth.json.
/// Tokens are obtained via the CLI script (scripts/codex-auth.csx)
/// and persisted here after automatic refresh.
/// </summary>
public sealed class CodexAuthService {
	private const string AuthFileName = "auth.json";

	private readonly ILogger<CodexAuthService> _logger;
	private readonly string _authFilePath;

	/// <summary>
	/// Called after successful OAuth or token exchange to hot-reload into the running provider.
	/// Wired in Program.cs where both Agent and Provider layers are accessible.
	/// </summary>
	public Action<string, string>? OnTokensAcquired { get; set; }

	public CodexAuthService(ILogger<CodexAuthService> logger) {
		this._logger       = logger;
		this._authFilePath = Path.Combine(AppContext.BaseDirectory, AuthFileName);
	}

	public (string? AccessToken, string? RefreshToken) LoadPersistedTokens() {
		try {
			if (!File.Exists(this._authFilePath)) return (null, null);

			string json = File.ReadAllText(this._authFilePath);
			JsonNode? root = JsonNode.Parse(json);
			JsonNode? codex = root?["codex"];
			if (codex is null) return (null, null);

			string? access  = codex["access_token"]?.GetValue<string>();
			string? refresh = codex["refresh_token"]?.GetValue<string>();
			return (access, refresh);
		} catch (Exception ex) {
			this._logger.LogWarning(ex, "Failed to load persisted Codex tokens");
			return (null, null);
		}
	}

	[Trace("CodexAuthService > PersistTokensAsync")]
	public async Task PersistTokensAsync(string accessToken, string refreshToken, int? expiresIn = null) {
		try {
			JsonObject root;
			if (File.Exists(this._authFilePath)) {
				string existing = await File.ReadAllTextAsync(this._authFilePath);
				root = JsonNode.Parse(existing)?.AsObject() ?? new JsonObject();
			} else {
				root = new JsonObject();
			}

			root["codex"] = new JsonObject {
				["access_token"]  = accessToken,
				["refresh_token"] = refreshToken,
				["persisted_at"]  = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
			};

			string json = root.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
			await File.WriteAllTextAsync(this._authFilePath, json);
		} catch (Exception ex) {
			this._logger.LogWarning(ex, "Failed to persist Codex tokens");
		}
	}
}
