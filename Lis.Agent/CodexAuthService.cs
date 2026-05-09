using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Security.Cryptography;
using System.Text;
using System.Web;

using Lis.Core.Util;

using Microsoft.Extensions.Logging;

namespace Lis.Agent;

public sealed record PendingAuth(string ChatId, long AgentId, string Verifier, DateTimeOffset ExpiresAt);

public sealed class CodexAuthService {
	private const string AuthFileName = "auth.json";
	private const string AuthorizeUrl = "https://auth.openai.com/oauth/authorize";
	private const string TokenUrl     = "https://auth.openai.com/oauth/token";
	private const string ClientId     = "app_EMoamEEZ73f0CkXaXp7hrann";
	private const string RedirectUri  = "http://localhost:1455/auth/callback";
	private const string OAuthScope   = "openid profile email offline_access";

	private readonly ConcurrentDictionary<string, PendingAuth> _pending = new();
	private readonly ILogger<CodexAuthService> _logger;
	private readonly string _authFilePath;

	/// <summary>
	/// Called after successful auth or token exchange to hot-reload into the running provider.
	/// Wired in Program.cs where both Agent and Provider layers are accessible.
	/// </summary>
	public Action<string, string>? OnTokensAcquired { get; set; }

	public CodexAuthService(ILogger<CodexAuthService> logger) {
		this._logger       = logger;
		this._authFilePath = Path.Combine(AppContext.BaseDirectory, AuthFileName);
	}

	[Trace("CodexAuthService > StartAuth")]
	public (string AuthUrl, string State) StartAuth(string chatId, long agentId) {
		(string verifier, string challenge) = GeneratePkce();
		string state = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();

		PendingAuth pending = new(chatId, agentId, verifier, DateTimeOffset.UtcNow.AddMinutes(10));
		this._pending[state] = pending;

		string authUrl = BuildAuthUrl(challenge, state);
		if (this._logger.IsEnabled(LogLevel.Debug))
			this._logger.LogDebug("Generated Codex auth URL: {Url}", authUrl);
		return (authUrl, state);
	}

	public bool IsCallbackUrl(string? messageBody) =>
		messageBody is not null && messageBody.Contains("localhost:1455/auth/callback", StringComparison.OrdinalIgnoreCase);

	[Trace("CodexAuthService > TryCompleteAuthAsync")]
	public async Task<string?> TryCompleteAuthAsync(string messageBody, CancellationToken ct) {
		var parsed = ParseCallbackUrl(messageBody);
		if (parsed is null)
			return null;

		(string code, string state) = parsed.Value;

		if (!this._pending.TryRemove(state, out PendingAuth? pending))
			return "❌ Auth state not found or expired. Start again with /auth codex.";

		if (pending.ExpiresAt < DateTimeOffset.UtcNow)
			return "❌ Auth session expired. Start again with /auth codex.";

		using HttpClient http = new();
		var result = await ExchangeCodeAsync(http, code, pending.Verifier, ct);
		if (result is null)
			return "❌ Failed to exchange authorization code. Start again with /auth codex.";

		(string accessToken, string refreshToken, int expiresIn) = result.Value;

		await this.PersistTokensAsync(accessToken, refreshToken, expiresIn);
		this.OnTokensAcquired?.Invoke(accessToken, refreshToken);

		if (this._logger.IsEnabled(LogLevel.Information))
			this._logger.LogInformation("Codex OAuth completed for chat {ChatId}", pending.ChatId);
		return "✅ Codex authenticated successfully.";
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

	public void CleanupExpired() {
		DateTimeOffset now = DateTimeOffset.UtcNow;
		foreach (var kvp in this._pending)
			if (kvp.Value.ExpiresAt < now)
				this._pending.TryRemove(kvp.Key, out _);
	}

	// ── OAuth helpers (self-contained — no Lis.Providers dependency) ──

	private static (string Verifier, string Challenge) GeneratePkce() {
		byte[] bytes = RandomNumberGenerator.GetBytes(32);
		string verifier = Base64UrlEncode(bytes);

		byte[] hash = SHA256.HashData(Encoding.ASCII.GetBytes(verifier));
		string challenge = Base64UrlEncode(hash);
		return (verifier, challenge);
	}

	private static string BuildAuthUrl(string challenge, string state) =>
		AuthorizeUrl
		+ "?response_type=code"
		+ "&client_id=" + ClientId
		+ "&redirect_uri=" + Uri.EscapeDataString(RedirectUri)
		+ "&scope=openid+profile+email+offline_access"
		+ "&code_challenge=" + challenge
		+ "&code_challenge_method=S256"
		+ "&state=" + state
		+ "&id_token_add_organizations=true"
		+ "&codex_cli_simplified_flow=true"
		+ "&originator=pi"
		+ "&prompt=login";

	private static (string Code, string State)? ParseCallbackUrl(string input) {
		input = input.Trim();

		try {
			if (input.StartsWith("http://") || input.StartsWith("https://")) {
				Uri uri = new(input);
				var query = HttpUtility.ParseQueryString(uri.Query);
				string? code = query["code"];
				string? state = query["state"];
				if (code is not null && state is not null)
					return (code, state);
			}
		} catch {
			// Not a valid URL
		}

		// code#state format
		int hashIdx = input.IndexOf('#');
		if (hashIdx > 0 && hashIdx < input.Length - 1)
			return (input[..hashIdx], input[(hashIdx + 1)..]);

		return null;
	}

	private static async Task<(string AccessToken, string RefreshToken, int ExpiresIn)?> ExchangeCodeAsync(
		HttpClient httpClient, string code, string verifier, CancellationToken ct) {

		Dictionary<string, string> form = new() {
			["grant_type"]    = "authorization_code",
			["client_id"]     = ClientId,
			["code"]          = code,
			["code_verifier"] = verifier,
			["redirect_uri"]  = RedirectUri
		};

		using FormUrlEncodedContent content = new(form);
		using HttpRequestMessage request = new(HttpMethod.Post, TokenUrl) { Content = content };

		using HttpResponseMessage response = await httpClient.SendAsync(request, ct);
		string body = await response.Content.ReadAsStringAsync(ct);

		if (!response.IsSuccessStatusCode)
			return null;

		JsonNode? json = JsonNode.Parse(body);
		string? accessToken  = json?["access_token"]?.GetValue<string>();
		string? refreshToken = json?["refresh_token"]?.GetValue<string>();
		int? expiresIn       = json?["expires_in"]?.GetValue<int>();

		if (accessToken is null || refreshToken is null || expiresIn is null)
			return null;

		return (accessToken, refreshToken, expiresIn.Value);
	}

	private static string Base64UrlEncode(byte[] bytes) =>
		Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
