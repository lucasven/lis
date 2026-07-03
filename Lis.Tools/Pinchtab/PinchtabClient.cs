using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Lis.Tools.Pinchtab;

public sealed record PinchtabProfile(string Id, string Name);

public sealed record PinchtabInstance(string Id, string? ProfileId, string? Port, string Status);

public sealed record PinchtabTab(string TabId, string? Url, string? Title);

/// <summary>HTTP client for the PinchTab orchestrator API.</summary>
public interface IPinchtabClient {
	Task<bool> IsHealthyAsync(CancellationToken ct = default);
	Task<PinchtabProfile?> FindProfileAsync(string name, CancellationToken ct = default);
	Task<PinchtabProfile> CreateProfileAsync(string name, CancellationToken ct = default);
	Task StopProfileAsync(string profileId, CancellationToken ct = default);
	Task<PinchtabInstance> StartInstanceAsync(string profileId, bool headed, CancellationToken ct = default);
	Task<PinchtabInstance?> GetInstanceAsync(string instanceId, CancellationToken ct = default);
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

	public async Task<bool> IsHealthyAsync(CancellationToken ct = default) {
		try {
			using HttpClient client = this.CreateClient();
			using HttpResponseMessage response = await client.GetAsync("/health", ct);
			return response.IsSuccessStatusCode;
		} catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException) {
			return false;
		}
	}

	public async Task<PinchtabProfile?> FindProfileAsync(string name, CancellationToken ct = default) {
		using HttpClient client = this.CreateClient();
		using HttpResponseMessage response = await client.GetAsync("/profiles", ct);
		response.EnsureSuccessStatusCode();

		List<PinchtabProfile>? profiles = await response.Content.ReadFromJsonAsync<List<PinchtabProfile>>(Json, ct);
		return profiles?.FirstOrDefault(p => p.Name == name);
	}

	public async Task<PinchtabProfile> CreateProfileAsync(string name, CancellationToken ct = default) {
		using HttpClient client = this.CreateClient();
		using HttpResponseMessage response = await client.PostAsJsonAsync("/profiles", new { name }, Json, ct);
		response.EnsureSuccessStatusCode();

		return await response.Content.ReadFromJsonAsync<PinchtabProfile>(Json, ct)
			?? throw new InvalidOperationException("Empty response creating pinchtab profile.");
	}

	public async Task StopProfileAsync(string profileId, CancellationToken ct = default) {
		using HttpClient client = this.CreateClient();
		using HttpResponseMessage response = await client.PostAsync($"/profiles/{profileId}/stop", content: null, ct);
		response.EnsureSuccessStatusCode();
	}

	public async Task<PinchtabInstance> StartInstanceAsync(string profileId, bool headed, CancellationToken ct = default) {
		var body = new {
			profileId,
			mode           = headed ? "headed" : "headless",
			securityPolicy = new { allowedDomains = new[] { "*" } },
		};

		using HttpClient client = this.CreateClient();
		using HttpResponseMessage response = await client.PostAsJsonAsync("/instances/start", body, Json, ct);
		response.EnsureSuccessStatusCode();

		return await response.Content.ReadFromJsonAsync<PinchtabInstance>(Json, ct)
			?? throw new InvalidOperationException("Empty response starting pinchtab instance.");
	}

	public async Task<PinchtabInstance?> GetInstanceAsync(string instanceId, CancellationToken ct = default) {
		using HttpClient client = this.CreateClient();
		using HttpResponseMessage response = await client.GetAsync($"/instances/{instanceId}", ct);
		if (!response.IsSuccessStatusCode) return null;

		return await response.Content.ReadFromJsonAsync<PinchtabInstance>(Json, ct);
	}

	public async Task StopInstanceAsync(string instanceId, CancellationToken ct = default) {
		using HttpClient client = this.CreateClient();
		using HttpResponseMessage response = await client.PostAsync($"/instances/{instanceId}/stop", content: null, ct);
		response.EnsureSuccessStatusCode();
	}

	public async Task<PinchtabTab> OpenTabAsync(string instanceId, string url, CancellationToken ct = default) {
		using HttpClient client = this.CreateClient();
		using HttpResponseMessage response = await client.PostAsJsonAsync($"/instances/{instanceId}/tabs/open", new { url }, Json, ct);
		response.EnsureSuccessStatusCode();

		return await response.Content.ReadFromJsonAsync<PinchtabTab>(Json, ct)
			?? throw new InvalidOperationException("Empty response opening pinchtab tab.");
	}

	public async Task<string> ListTabsAsync(string instanceId, CancellationToken ct = default) {
		using HttpClient client = this.CreateClient();
		using HttpResponseMessage response = await client.GetAsync($"/instances/{instanceId}/tabs", ct);
		response.EnsureSuccessStatusCode();

		return await response.Content.ReadAsStringAsync(ct);
	}

	public async Task<string> NavigateAsync(string tabId, string url, CancellationToken ct = default) {
		using HttpClient client = this.CreateClient();
		using HttpResponseMessage response = await client.PostAsJsonAsync($"/tabs/{tabId}/navigate", new { url }, Json, ct);
		response.EnsureSuccessStatusCode();

		return await response.Content.ReadAsStringAsync(ct);
	}

	public async Task<string> SnapshotAsync(string tabId, string filter, int maxTokens, CancellationToken ct = default) {
		using HttpClient client = this.CreateClient();
		string query = $"/tabs/{tabId}/snapshot?filter={Uri.EscapeDataString(filter)}&format=compact&maxTokens={maxTokens}";
		using HttpResponseMessage response = await client.GetAsync(query, ct);
		response.EnsureSuccessStatusCode();

		return await response.Content.ReadAsStringAsync(ct);
	}

	public async Task<string> ActionAsync(string tabId, object payload, CancellationToken ct = default) {
		using HttpClient client = this.CreateClient();
		using HttpResponseMessage response = await client.PostAsJsonAsync($"/tabs/{tabId}/action", payload, Json, ct);
		response.EnsureSuccessStatusCode();

		return await response.Content.ReadAsStringAsync(ct);
	}

	public async Task<string> EvaluateAsync(string tabId, string expression, CancellationToken ct = default) {
		using HttpClient client = this.CreateClient();
		using HttpResponseMessage response = await client.PostAsJsonAsync($"/tabs/{tabId}/evaluate", new { expression }, Json, ct);
		response.EnsureSuccessStatusCode();

		return await response.Content.ReadAsStringAsync(ct);
	}

	public async Task<byte[]> ScreenshotAsync(string tabId, CancellationToken ct = default) {
		using HttpClient client = this.CreateClient();
		using HttpResponseMessage response = await client.GetAsync($"/tabs/{tabId}/screenshot?format=png&raw=true", ct);
		response.EnsureSuccessStatusCode();

		return await response.Content.ReadAsByteArrayAsync(ct);
	}

	public async Task ShutdownAsync(CancellationToken ct = default) {
		using HttpClient client = this.CreateClient();
		using HttpResponseMessage response = await client.PostAsync("/shutdown", content: null, ct);
		response.EnsureSuccessStatusCode();
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
