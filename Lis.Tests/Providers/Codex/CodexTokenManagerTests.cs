using System.Net;
using System.Text;
using System.Text.Json;

using Lis.Providers.OpenAi.Codex;

namespace Lis.Tests.Providers.Codex;

public class CodexTokenManagerTests {
	[Fact]
	public async Task Property_NeverReturnsExpiredToken() {
		FakeRefreshHandler handler = new();
		HttpClient http = new(handler);
		CodexOptions options = new() {
			AccessToken       = CreateJwt(TimeSpan.FromSeconds(2), "acc_123"),
			RefreshToken      = "rt-test",
			ExpiryBufferSeconds = 1
		};
		CodexTokenManager manager = new(options, http);

		for (int i = 0; i < 5; i++) {
			CodexTokenInfo info = await manager.GetValidTokenAsync();
			Assert.True(info.ExpiresAt > DateTimeOffset.UtcNow);
			await Task.Delay(100);
		}
	}

	[Fact]
	public async Task Property_ConcurrentCallsShareSingleRefresh() {
		CountingRefreshHandler handler = new(delay: TimeSpan.FromMilliseconds(200));
		HttpClient http = new(handler);
		CodexOptions options = new() {
			AccessToken  = CreateJwt(TimeSpan.FromSeconds(-1), "acc_123"),
			RefreshToken = "rt-test"
		};
		CodexTokenManager manager = new(options, http);

		Task<CodexTokenInfo>[] tasks = Enumerable.Range(0, 10)
			.Select(_ => manager.GetValidTokenAsync())
			.ToArray();
		CodexTokenInfo[] results = await Task.WhenAll(tasks);

		Assert.Equal(1, handler.RequestCount);
		Assert.All(results, r => Assert.Equal(results[0].AccessToken, r.AccessToken));
	}

	[Theory]
	[InlineData("acc_abc123")]
	[InlineData("acc_xyz789")]
	public async Task Property_AccountIdAlwaysFromJwt(string expectedAccountId) {
		CodexOptions options = new() {
			AccessToken  = CreateJwt(TimeSpan.FromHours(1), expectedAccountId),
			RefreshToken = "rt-test"
		};
		CodexTokenManager manager = new(options, new HttpClient());

		CodexTokenInfo info = await manager.GetValidTokenAsync();
		Assert.Equal(expectedAccountId, info.AccountId);
	}

	[Fact]
	public async Task Failed_State_Throws_On_GetToken() {
		FailingRefreshHandler handler = new();
		HttpClient http = new(handler);
		CodexOptions options = new() {
			AccessToken  = CreateJwt(TimeSpan.FromSeconds(-1), "acc_123"),
			RefreshToken = "rt-test"
		};
		CodexTokenManager manager = new(options, http);

		await Assert.ThrowsAsync<CodexAuthException>(() => manager.GetValidTokenAsync());
		await Assert.ThrowsAsync<CodexAuthException>(() => manager.GetValidTokenAsync());
	}

	[Fact]
	public void ParseJwt_ExtractsAccountIdAndExpiry() {
		string jwt = CreateJwt(TimeSpan.FromHours(1), "test_account");
		(string accountId, DateTimeOffset expiresAt) = CodexTokenManager.ParseJwt(jwt);

		Assert.Equal("test_account", accountId);
		Assert.True(expiresAt > DateTimeOffset.UtcNow);
		Assert.True(expiresAt < DateTimeOffset.UtcNow.AddHours(2));
	}

	internal static string CreateJwt(TimeSpan expiresIn, string accountId) {
		long exp = DateTimeOffset.UtcNow.Add(expiresIn).ToUnixTimeSeconds();
		string json = $$"""{"https://api.openai.com/auth":{"chatgpt_account_id":"{{accountId}}"},"exp":{{exp}}}""";
		string payload = Base64UrlEncode(Encoding.UTF8.GetBytes(json));
		return $"eyJhbGciOiJSUzI1NiJ9.{payload}.fakesig";
	}

	private static string Base64UrlEncode(byte[] input) {
		string s = Convert.ToBase64String(input);
		return s.TrimEnd('=').Replace('+', '-').Replace('/', '_');
	}

	private sealed class FakeRefreshHandler : HttpMessageHandler {
		protected override Task<HttpResponseMessage> SendAsync(
			HttpRequestMessage request, CancellationToken ct) {
			string newToken = CreateJwt(TimeSpan.FromHours(1), "acc_refreshed");
			string body = JsonSerializer.Serialize(new {
				access_token  = newToken,
				refresh_token = "rt-new",
				expires_in    = 3600
			});
			return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) {
				Content = new StringContent(body, Encoding.UTF8, "application/json")
			});
		}
	}

	private sealed class CountingRefreshHandler(TimeSpan delay) : HttpMessageHandler {
		public int RequestCount;

		protected override async Task<HttpResponseMessage> SendAsync(
			HttpRequestMessage request, CancellationToken ct) {
			Interlocked.Increment(ref this.RequestCount);
			await Task.Delay(delay, ct);
			string newToken = CreateJwt(TimeSpan.FromHours(1), "acc_refreshed");
			string body = JsonSerializer.Serialize(new {
				access_token  = newToken,
				refresh_token = "rt-new",
				expires_in    = 3600
			});
			return new HttpResponseMessage(HttpStatusCode.OK) {
				Content = new StringContent(body, Encoding.UTF8, "application/json")
			};
		}
	}

	private sealed class FailingRefreshHandler : HttpMessageHandler {
		protected override Task<HttpResponseMessage> SendAsync(
			HttpRequestMessage request, CancellationToken ct) =>
			Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized) {
				Content = new StringContent("{\"error\":\"invalid_grant\"}")
			});
	}
}
