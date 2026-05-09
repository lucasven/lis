using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;

namespace Lis.Providers.OpenAi.Codex;

public sealed record CodexTokenInfo(string AccessToken, string AccountId, DateTimeOffset ExpiresAt);

public sealed class CodexAuthException(string message, Exception? inner = null) : Exception(message, inner);

public sealed class CodexTokenManager : IDisposable {
	private const string TokenEndpoint = "https://auth.openai.com/oauth/token";
	private const string ClientId      = "app_EMoamEEZ73f0CkXaXp7hrann";
	private const string JwtClaimPath  = "https://api.openai.com/auth";

	private readonly CodexOptions _options;
	private readonly HttpClient   _httpClient;
	private readonly SemaphoreSlim _gate = new(1, 1);

	private CodexTokenInfo? _cached;
	private Task<CodexTokenInfo>? _refreshTask;
	private bool _failed;

	public Func<string, string, int, Task>? OnTokensRefreshed { get; set; }

	public CodexTokenManager(CodexOptions options, HttpClient httpClient) {
		this._options    = options;
		this._httpClient = httpClient;

		// Parse initial token (skip if empty — awaiting OAuth)
		if (options.AccessToken is { Length: > 0 }) {
			try {
				(string accountId, DateTimeOffset expiresAt) = ParseJwt(options.AccessToken);
				this._cached = new CodexTokenInfo(options.AccessToken, accountId, expiresAt);
			} catch {
				// Invalid initial token — will need refresh or OAuth
			}
		}
	}

	public void UpdateTokens(string accessToken, string refreshToken) {
		this._options.AccessToken  = accessToken;
		this._options.RefreshToken = refreshToken;
		this._failed = false;

		try {
			(string accountId, DateTimeOffset expiresAt) = ParseJwt(accessToken);
			this._cached = new CodexTokenInfo(accessToken, accountId, expiresAt);
		} catch {
			this._cached = null;
		}
	}

	public async Task<CodexTokenInfo> GetValidTokenAsync(CancellationToken ct = default) {
		if (this._failed)
			throw new CodexAuthException("Codex token refresh previously failed. Use /auth codex to re-authenticate.");

		if (this._cached is null && this._options.RefreshToken is not { Length: > 0 })
			throw new CodexAuthException("Codex is not authenticated. Use /auth codex to log in.");

		CodexTokenInfo? cached = this._cached;
		if (cached is not null && this.IsValid(cached))
			return cached;

		return await this.RefreshAsync(ct);
	}

	private async Task<CodexTokenInfo> RefreshAsync(CancellationToken ct) {
		Task<CodexTokenInfo>? existing;

		await this._gate.WaitAsync(ct);
		try {
			if (this._failed)
				throw new CodexAuthException("Codex token refresh previously failed.");

			// Double-check after acquiring lock
			if (this._cached is not null && this.IsValid(this._cached))
				return this._cached;

			// If a refresh is already in flight, share it
			if (this._refreshTask is not null) {
				existing = this._refreshTask;
			} else {
				this._refreshTask = this.DoRefreshAsync(ct);
				existing = this._refreshTask;
			}
		} finally {
			this._gate.Release();
		}

		return await existing;
	}

	private async Task<CodexTokenInfo> DoRefreshAsync(CancellationToken ct) {
		try {
			Dictionary<string, string> form = new() {
				["grant_type"]    = "refresh_token",
				["refresh_token"] = this._options.RefreshToken,
				["client_id"]     = ClientId
			};

			using FormUrlEncodedContent content = new(form);
			using HttpRequestMessage request = new(HttpMethod.Post, TokenEndpoint) { Content = content };
			request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

			using HttpResponseMessage response = await this._httpClient.SendAsync(request, ct);
			string body = await response.Content.ReadAsStringAsync(ct);

			if (!response.IsSuccessStatusCode) {
				this._failed = true;
				throw new CodexAuthException($"Token refresh failed with {(int)response.StatusCode}: {body}");
			}

			JsonNode? json = JsonNode.Parse(body);
			string? accessToken  = json?["access_token"]?.GetValue<string>();
			string? refreshToken = json?["refresh_token"]?.GetValue<string>();
			int? expiresIn       = json?["expires_in"]?.GetValue<int>();

			if (accessToken is null || refreshToken is null || expiresIn is null) {
				this._failed = true;
				throw new CodexAuthException("Token refresh response missing required fields");
			}

			// Update mutable options so subsequent refreshes use the new refresh token
			this._options.AccessToken  = accessToken;
			this._options.RefreshToken = refreshToken;

			(string accountId, DateTimeOffset expiresAt) = ParseJwt(accessToken);
			CodexTokenInfo info = new(accessToken, accountId, expiresAt);
			this._cached = info;

			if (this.OnTokensRefreshed is not null)
				_ = Task.Run(() => this.OnTokensRefreshed(accessToken, refreshToken, expiresIn.Value), CancellationToken.None);

			return info;
		} catch (CodexAuthException) {
			throw;
		} catch (Exception ex) {
			this._failed = true;
			throw new CodexAuthException("Token refresh failed", ex);
		} finally {
			await this._gate.WaitAsync(CancellationToken.None);
			try { this._refreshTask = null; } finally { this._gate.Release(); }
		}
	}

	private bool IsValid(CodexTokenInfo info) =>
		info.ExpiresAt > DateTimeOffset.UtcNow + TimeSpan.FromSeconds(this._options.ExpiryBufferSeconds);

	public static (string AccountId, DateTimeOffset ExpiresAt) ParseJwt(string jwt) {
		string[] parts = jwt.Split('.');
		if (parts.Length < 2)
			throw new CodexAuthException("Invalid JWT: missing payload segment");

		string payload = parts[1];
		// Fix base64url padding
		payload = payload.Replace('-', '+').Replace('_', '/');
		switch (payload.Length % 4) {
			case 2: payload += "=="; break;
			case 3: payload += "="; break;
		}

		byte[] bytes = Convert.FromBase64String(payload);
		JsonNode? json = JsonNode.Parse(Encoding.UTF8.GetString(bytes));

		string? accountId = json?[JwtClaimPath]?["chatgpt_account_id"]?.GetValue<string>();
		long? exp = json?["exp"]?.GetValue<long>();

		if (accountId is null)
			throw new CodexAuthException("JWT missing chatgpt_account_id claim");
		if (exp is null)
			throw new CodexAuthException("JWT missing exp claim");

		DateTimeOffset expiresAt = DateTimeOffset.FromUnixTimeSeconds(exp.Value);
		return (accountId, expiresAt);
	}

	public void Dispose() => this._gate.Dispose();
}
