using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Lis.Core.Util;

namespace Lis.Tools.Pinchtab;

public sealed record PinchtabProfile(string Id, string Name);

public sealed record PinchtabInstance(
	string  Id,
	string? ProfileId,
	string? Port,
	string  Status,
	string? Mode           = null,
	string? Browser        = null,
	string? Error          = null,
	string? FallbackFrom   = null,
	string? FallbackReason = null);

public sealed record PinchtabTab(string TabId, string? Url, string? Title);

/// <summary>HTTP client for the PinchTab orchestrator API.</summary>
public interface IPinchtabClient {
	Task<bool> IsHealthyAsync(CancellationToken ct = default);
	Task<PinchtabProfile?> FindProfileAsync(string name, CancellationToken ct = default);
	Task<PinchtabProfile> CreateProfileAsync(string name, CancellationToken ct = default);
	Task StopProfileAsync(string profileId, CancellationToken ct = default);
	Task<PinchtabInstance> StartInstanceAsync(string profileId, bool headed, CancellationToken ct = default);
	Task<PinchtabInstance?> GetInstanceAsync(string instanceId, CancellationToken ct = default);
	Task<string> GetInstanceLogsAsync(string instanceId, CancellationToken ct = default);
	Task StopInstanceAsync(string instanceId, CancellationToken ct = default);
	Task<PinchtabTab> OpenTabAsync(string instanceId, string url, CancellationToken ct = default);
	Task<string> ListTabsAsync(string instanceId, CancellationToken ct = default);
	Task<string> NavigateAsync(string tabId, string url, CancellationToken ct = default);
	Task<string> SnapshotAsync(string tabId, string filter, int maxTokens, CancellationToken ct = default);
	Task<string> ActionAsync(string tabId, object payload, CancellationToken ct = default);
	Task<string> EvaluateAsync(string tabId, string expression, CancellationToken ct = default);
	Task<byte[]> ScreenshotAsync(string tabId, CancellationToken ct = default);
	Task ShutdownAsync(CancellationToken ct = default);
}

public sealed class PinchtabClient(
	IHttpClientFactory httpClientFactory,
	PinchtabOptions    options,
	PinchtabServerState serverState) : IPinchtabClient {

	private static readonly JsonSerializerOptions Json = new() {
		PropertyNamingPolicy   = JsonNamingPolicy.CamelCase,
		PropertyNameCaseInsensitive = true,
		DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
	};

	[Trace("PinchtabClient > IsHealthyAsync")]
	public async Task<bool> IsHealthyAsync(CancellationToken ct = default) {
		try {
			using HttpClient client = this.CreateClient();
			using HttpResponseMessage response = await client.GetAsync("/health", ct);
			return response.IsSuccessStatusCode;
		} catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException) {
			return false;
		}
	}

	[Trace("PinchtabClient > FindProfileAsync")]
	public async Task<PinchtabProfile?> FindProfileAsync(string name, CancellationToken ct = default) {
		using HttpClient client = this.CreateClient();
		using HttpResponseMessage response = await client.GetAsync("/profiles", ct);
		await EnsureSuccessAsync(response, ct);

		List<PinchtabProfile>? profiles = await response.Content.ReadFromJsonAsync<List<PinchtabProfile>>(Json, ct);
		return profiles?.FirstOrDefault(p => p.Name == name);
	}

	[Trace("PinchtabClient > CreateProfileAsync")]
	public async Task<PinchtabProfile> CreateProfileAsync(string name, CancellationToken ct = default) {
		using HttpClient client = this.CreateClient();
		using HttpResponseMessage response = await client.PostAsync("/profiles", JsonBody(new { name }), ct);
		await EnsureSuccessAsync(response, ct);

		return await response.Content.ReadFromJsonAsync<PinchtabProfile>(Json, ct)
			?? throw new InvalidOperationException("Empty response creating pinchtab profile.");
	}

	[Trace("PinchtabClient > StopProfileAsync")]
	public async Task StopProfileAsync(string profileId, CancellationToken ct = default) {
		using HttpClient client = this.CreateClient();
		using HttpResponseMessage response = await client.PostAsync($"/profiles/{profileId}/stop", content: null, ct);
		await EnsureSuccessAsync(response, ct);
	}

	[Trace("PinchtabClient > StartInstanceAsync")]
	public async Task<PinchtabInstance> StartInstanceAsync(string profileId, bool headed, CancellationToken ct = default) {
		var body = new {
			profileId,
			mode           = headed ? "headed" : "headless",
			securityPolicy = new { allowedDomains = new[] { "*" } },
		};

		using HttpClient client = this.CreateClient();
		using HttpResponseMessage response = await client.PostAsync("/instances/start", JsonBody(body), ct);
		await EnsureSuccessAsync(response, ct);

		return await response.Content.ReadFromJsonAsync<PinchtabInstance>(Json, ct)
			?? throw new InvalidOperationException("Empty response starting pinchtab instance.");
	}

	[Trace("PinchtabClient > GetInstanceAsync")]
	public async Task<PinchtabInstance?> GetInstanceAsync(string instanceId, CancellationToken ct = default) {
		using HttpClient client = this.CreateClient();
		using HttpResponseMessage response = await client.GetAsync($"/instances/{instanceId}", ct);
		if (!response.IsSuccessStatusCode) return null;

		return await response.Content.ReadFromJsonAsync<PinchtabInstance>(Json, ct);
	}

	[Trace("PinchtabClient > GetInstanceLogsAsync")]
	public async Task<string> GetInstanceLogsAsync(string instanceId, CancellationToken ct = default) {
		using HttpClient client = this.CreateClient();
		using HttpResponseMessage response = await client.GetAsync($"/instances/{instanceId}/logs", ct);
		await EnsureSuccessAsync(response, ct);

		return await response.Content.ReadAsStringAsync(ct);
	}

	[Trace("PinchtabClient > StopInstanceAsync")]
	public async Task StopInstanceAsync(string instanceId, CancellationToken ct = default) {
		using HttpClient client = this.CreateClient();
		using HttpResponseMessage response = await client.PostAsync($"/instances/{instanceId}/stop", content: null, ct);
		await EnsureSuccessAsync(response, ct);
	}

	[Trace("PinchtabClient > OpenTabAsync")]
	public async Task<PinchtabTab> OpenTabAsync(string instanceId, string url, CancellationToken ct = default) {
		using HttpClient client = this.CreateClient();
		using HttpResponseMessage response = await client.PostAsync($"/instances/{instanceId}/tabs/open", JsonBody(new { url }), ct);
		await EnsureSuccessAsync(response, ct);

		return await response.Content.ReadFromJsonAsync<PinchtabTab>(Json, ct)
			?? throw new InvalidOperationException("Empty response opening pinchtab tab.");
	}

	[Trace("PinchtabClient > ListTabsAsync")]
	public async Task<string> ListTabsAsync(string instanceId, CancellationToken ct = default) {
		using HttpClient client = this.CreateClient();
		using HttpResponseMessage response = await client.GetAsync($"/instances/{instanceId}/tabs", ct);
		await EnsureSuccessAsync(response, ct);

		return await response.Content.ReadAsStringAsync(ct);
	}

	[Trace("PinchtabClient > NavigateAsync")]
	public async Task<string> NavigateAsync(string tabId, string url, CancellationToken ct = default) {
		using HttpClient client = this.CreateClient();
		using HttpResponseMessage response = await client.PostAsync($"/tabs/{tabId}/navigate", JsonBody(new { url }), ct);
		await EnsureSuccessAsync(response, ct);

		return await response.Content.ReadAsStringAsync(ct);
	}

	[Trace("PinchtabClient > SnapshotAsync")]
	public async Task<string> SnapshotAsync(string tabId, string filter, int maxTokens, CancellationToken ct = default) {
		using HttpClient client = this.CreateClient();
		string query = $"/tabs/{tabId}/snapshot?filter={Uri.EscapeDataString(filter)}&format=compact&maxTokens={maxTokens}";
		using HttpResponseMessage response = await client.GetAsync(query, ct);
		await EnsureSuccessAsync(response, ct);

		return await response.Content.ReadAsStringAsync(ct);
	}

	[Trace("PinchtabClient > ActionAsync")]
	public async Task<string> ActionAsync(string tabId, object payload, CancellationToken ct = default) {
		using HttpClient client = this.CreateClient();
		using HttpResponseMessage response = await client.PostAsync($"/tabs/{tabId}/action", JsonBody(payload), ct);
		await EnsureSuccessAsync(response, ct);

		return await response.Content.ReadAsStringAsync(ct);
	}

	[Trace("PinchtabClient > EvaluateAsync")]
	public async Task<string> EvaluateAsync(string tabId, string expression, CancellationToken ct = default) {
		using HttpClient client = this.CreateClient();
		using HttpResponseMessage response = await client.PostAsync($"/tabs/{tabId}/evaluate", JsonBody(new { expression }), ct);
		await EnsureSuccessAsync(response, ct);

		return await response.Content.ReadAsStringAsync(ct);
	}

	[Trace("PinchtabClient > ScreenshotAsync")]
	public async Task<byte[]> ScreenshotAsync(string tabId, CancellationToken ct = default) {
		using HttpClient client = this.CreateClient();
		using HttpResponseMessage response = await client.GetAsync($"/tabs/{tabId}/screenshot?format=png&raw=true", ct);
		await EnsureSuccessAsync(response, ct);

		return await response.Content.ReadAsByteArrayAsync(ct);
	}

	[Trace("PinchtabClient > ShutdownAsync")]
	public async Task ShutdownAsync(CancellationToken ct = default) {
		using HttpClient client = this.CreateClient();
		using HttpResponseMessage response = await client.PostAsync("/shutdown", content: null, ct);
		await EnsureSuccessAsync(response, ct);
	}

	/// <summary>
	/// Buffered JSON content with an explicit Content-Length. PostAsJsonAsync streams with
	/// Transfer-Encoding: chunked, and pinchtab's orchestrator skips body parsing when
	/// ContentLength is not positive — silently dropping profileId/mode from instance starts.
	/// </summary>
	private static StringContent JsonBody(object body) =>
		new(JsonSerializer.Serialize(body, Json), Encoding.UTF8, "application/json");

	/// <summary>Like EnsureSuccessStatusCode, but includes the response body so pinchtab error codes (e.g. navigation_changed) surface.</summary>
	private static async Task EnsureSuccessAsync(HttpResponseMessage response, CancellationToken ct) {
		if (response.IsSuccessStatusCode) return;

		string body = await response.Content.ReadAsStringAsync(ct);
		throw new HttpRequestException(
			$"Pinchtab returned {(int)response.StatusCode} ({response.StatusCode}): {body}",
			inner: null, response.StatusCode);
	}

	private HttpClient CreateClient() {
		HttpClient client = httpClientFactory.CreateClient("pinchtab");
		client.BaseAddress = new Uri(options.BaseUrl);
		client.Timeout     = TimeSpan.FromSeconds(60);

		if (!string.IsNullOrEmpty(serverState.Token))
			client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", serverState.Token);

		return client;
	}
}
