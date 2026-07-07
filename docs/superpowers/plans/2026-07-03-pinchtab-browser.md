# PinchTab Browser Integration Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Replace the Playwright-based `BrowserPlugin` + `BrowserSessionManager` with PinchTab (a Go binary driving Chrome via HTTP API), managed as a sidecar entirely by Lis, and remove `WebPlugin`'s `fetch` tool.

**Architecture:** A `PinchtabProcessManager` (BackgroundService) downloads the pinned pinchtab binary, writes its config, spawns/supervises the server (restart on crash + daily scheduled restart). A `PinchtabInstanceManager` lazily starts one browser instance per agent (profile `agent-{agentId}`) on first browser tool call, and `ConversationService` closes it when the conversation turn ends. `BrowserPlugin` is rewritten against `IPinchtabClient` (thin HTTP wrapper on the orchestrator API).

**Tech Stack:** .NET 10, ASP.NET Core BackgroundService, Semantic Kernel plugins, PinchTab v0.14.0 HTTP API, xunit 2.9.3.

**Spec:** `docs/superpowers/specs/2026-07-03-pinchtab-browser-design.md`

## Global Constraints

- Tabs for indentation, LF line endings, `this.` prefix for instance members, `is null`/`is not null`, aligned `=` in assignment blocks, same-line `if (x) return y;`.
- Interface + implementation in the **same .cs file**.
- `[Trace("ClassName > MethodName")]` on public async methods (from `Lis.Core.Util`).
- Kernel tools return error strings (`return $"Error: {ex.Message}"`), never throw to the model.
- Pinned pinchtab version: `v0.14.0`. Release asset URL pattern: `https://github.com/pinchtab/pinchtab/releases/download/v0.14.0/pinchtab-{linux|darwin|windows}-{amd64|arm64}[.exe]`.
- Env vars (all read in `PinchtabOptions.FromEnvironment()`): `PINCHTAB_ENABLED` (default false), `PINCHTAB_PORT` (9867), `PINCHTAB_VERSION` (v0.14.0), `PINCHTAB_DATA_DIR` (./data/pinchtab), `PINCHTAB_CHROME_PATH` (empty = pinchtab auto-detect), `PINCHTAB_RESTART_HOUR` (4), `PINCHTAB_TOKEN` (empty = generate per boot).
- Commits: gitmoji + conventional commits, co-author `Co-Authored-By: Claude Opus 4.6 <noreply@anthropic.com>`.
- Never use `dotnet format`. Final cleanup uses `jb cleanupcode` (Task 10 only).
- Build check: `dotnet build` from repo root. Tests: `dotnet test Lis.Tests/Lis.Tests.csproj`.

## PinchTab API contract (verified from v0.14.0 docs)

All calls go to the orchestrator at `http://localhost:{port}` with header `Authorization: Bearer {token}`.

| Call | Request | Response (relevant fields) |
|---|---|---|
| `GET /health` | — | `{"status":"ok"}` |
| `GET /profiles` | — | `[{"id":"...","name":"..."}]` |
| `POST /profiles` | `{"name":"agent-3"}` | `{"id":"...","name":"agent-3"}` |
| `POST /profiles/{id}/stop` | — | stops any instance bound to profile |
| `POST /instances/start` | `{"profileId":"...","mode":"headless"\|"headed","securityPolicy":{"allowedDomains":["*"]}}` | `{"id":"inst_x","profileId":"...","port":"9999","status":"starting"}` |
| `GET /instances/{id}` | — | `{"id":"...","status":"running"\|"starting"\|"stopped",...}` |
| `POST /instances/{id}/stop` | — | `{"id":"...","status":"stopped"}` |
| `POST /instances/{id}/tabs/open` | `{"url":"https://..."}` | `{"tabId":"...","url":"...","title":"..."}` |
| `GET /instances/{id}/tabs` | — | JSON array of tabs |
| `POST /tabs/{tabId}/navigate` | `{"url":"..."}` | JSON result |
| `GET /tabs/{tabId}/snapshot?filter=interactive&format=compact&maxTokens=N` | — | text aria-tree with refs `e0`, `e1`, … |
| `POST /tabs/{tabId}/action` | `{"kind":"click","ref":"e1"}` / `{"kind":"fill","ref":"e2","value":"..."}` / `{"kind":"press","key":"Enter"}` / `{"kind":"scroll","direction":"down"}` | JSON result |
| `POST /tabs/{tabId}/evaluate` | `{"expression":"document.title"}` | JSON result (requires `security.allowEvaluate: true` in config) |
| `GET /tabs/{tabId}/screenshot?format=png&raw=true` | — | raw PNG bytes |
| `POST /shutdown` | — | graceful server shutdown |

Server child-process env: `PINCHTAB_CONFIG` (config.json path), `PINCHTAB_TOKEN` (auth token). Command: `pinchtab server`.

---

### Task 1: Options, server state, display probe

**Files:**
- Create: `Lis.Tools/Pinchtab/PinchtabOptions.cs`
- Create: `Lis.Tools/Pinchtab/PinchtabServerState.cs`
- Create: `Lis.Tools/Pinchtab/DisplayProbe.cs`
- Test: `Lis.Tests/PinchtabOptionsTests.cs`

**Interfaces:**
- Produces: `PinchtabOptions` (properties `Enabled:bool`, `Port:int`, `Version:string`, `DataDir:string`, `ChromePath:string?`, `RestartHour:int`; computed `BaseUrl:string`, `BinDir:string`, `BinaryPath:string`, `VersionFilePath:string`, `ConfigPath:string`; static `FromEnvironment():PinchtabOptions`), `PinchtabServerState` (`Token:string` get/set), `DisplayProbe.IsAvailable():bool`.

- [ ] **Step 1: Write the failing test**

Create `Lis.Tests/PinchtabOptionsTests.cs`:

```csharp
using Lis.Tools.Pinchtab;

namespace Lis.Tests;

public class PinchtabOptionsTests {

	[Fact]
	public void Defaults_are_applied_when_env_is_empty() {
		Environment.SetEnvironmentVariable("PINCHTAB_ENABLED", null);
		Environment.SetEnvironmentVariable("PINCHTAB_PORT", null);
		Environment.SetEnvironmentVariable("PINCHTAB_VERSION", null);
		Environment.SetEnvironmentVariable("PINCHTAB_DATA_DIR", null);
		Environment.SetEnvironmentVariable("PINCHTAB_CHROME_PATH", null);
		Environment.SetEnvironmentVariable("PINCHTAB_RESTART_HOUR", null);

		PinchtabOptions options = PinchtabOptions.FromEnvironment();

		Assert.False(options.Enabled);
		Assert.Equal(9867, options.Port);
		Assert.Equal("v0.14.0", options.Version);
		Assert.Equal("./data/pinchtab", options.DataDir);
		Assert.Null(options.ChromePath);
		Assert.Equal(4, options.RestartHour);
		Assert.Equal("http://localhost:9867", options.BaseUrl);
	}

	[Fact]
	public void Env_values_override_defaults() {
		Environment.SetEnvironmentVariable("PINCHTAB_ENABLED", "true");
		Environment.SetEnvironmentVariable("PINCHTAB_PORT", "9900");
		Environment.SetEnvironmentVariable("PINCHTAB_VERSION", "v0.15.0");
		Environment.SetEnvironmentVariable("PINCHTAB_DATA_DIR", "/opt/pinchtab");
		Environment.SetEnvironmentVariable("PINCHTAB_CHROME_PATH", "/usr/bin/chromium");
		Environment.SetEnvironmentVariable("PINCHTAB_RESTART_HOUR", "2");

		PinchtabOptions options = PinchtabOptions.FromEnvironment();

		Assert.True(options.Enabled);
		Assert.Equal(9900, options.Port);
		Assert.Equal("v0.15.0", options.Version);
		Assert.Equal("/opt/pinchtab", options.DataDir);
		Assert.Equal("/usr/bin/chromium", options.ChromePath);
		Assert.Equal(2, options.RestartHour);
		Assert.Equal("http://localhost:9900", options.BaseUrl);

		Environment.SetEnvironmentVariable("PINCHTAB_ENABLED", null);
		Environment.SetEnvironmentVariable("PINCHTAB_PORT", null);
		Environment.SetEnvironmentVariable("PINCHTAB_VERSION", null);
		Environment.SetEnvironmentVariable("PINCHTAB_DATA_DIR", null);
		Environment.SetEnvironmentVariable("PINCHTAB_CHROME_PATH", null);
		Environment.SetEnvironmentVariable("PINCHTAB_RESTART_HOUR", null);
	}

	[Fact]
	public void Computed_paths_derive_from_data_dir() {
		PinchtabOptions options = new() { DataDir = "/opt/pinchtab" };

		Assert.Equal(Path.Combine("/opt/pinchtab", "bin"), options.BinDir);
		Assert.Equal(Path.Combine("/opt/pinchtab", "bin", OperatingSystem.IsWindows() ? "pinchtab.exe" : "pinchtab"), options.BinaryPath);
		Assert.Equal(Path.Combine("/opt/pinchtab", "bin", "VERSION"), options.VersionFilePath);
		Assert.Equal(Path.Combine("/opt/pinchtab", "config.json"), options.ConfigPath);
	}
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test Lis.Tests/Lis.Tests.csproj --filter PinchtabOptionsTests`
Expected: FAIL to compile — `Lis.Tools.Pinchtab` namespace does not exist.

- [ ] **Step 3: Write the implementation**

Create `Lis.Tools/Pinchtab/PinchtabOptions.cs`:

```csharp
namespace Lis.Tools.Pinchtab;

/// <summary>Configuration for the PinchTab sidecar, read from flat env vars.</summary>
public sealed class PinchtabOptions {
	public bool    Enabled     { get; init; }
	public int     Port        { get; init; } = 9867;
	public string  Version     { get; init; } = "v0.14.0";
	public string  DataDir     { get; init; } = "./data/pinchtab";
	public string? ChromePath  { get; init; }
	public int     RestartHour { get; init; } = 4;

	public string BaseUrl         => $"http://localhost:{this.Port}";
	public string BinDir          => Path.Combine(this.DataDir, "bin");
	public string BinaryPath      => Path.Combine(this.BinDir, OperatingSystem.IsWindows() ? "pinchtab.exe" : "pinchtab");
	public string VersionFilePath => Path.Combine(this.BinDir, "VERSION");
	public string ConfigPath      => Path.Combine(this.DataDir, "config.json");

	public static PinchtabOptions FromEnvironment() {
		return new PinchtabOptions {
			Enabled     = string.Equals(Env("PINCHTAB_ENABLED"), "true", StringComparison.OrdinalIgnoreCase),
			Port        = int.TryParse(Env("PINCHTAB_PORT"), out int port) ? port : 9867,
			Version     = Env("PINCHTAB_VERSION") ?? "v0.14.0",
			DataDir     = Env("PINCHTAB_DATA_DIR") ?? "./data/pinchtab",
			ChromePath  = Env("PINCHTAB_CHROME_PATH"),
			RestartHour = int.TryParse(Env("PINCHTAB_RESTART_HOUR"), out int hour) ? hour : 4,
		};
	}

	private static string? Env(string key) {
		string? value = Environment.GetEnvironmentVariable(key);
		return string.IsNullOrWhiteSpace(value) ? null : value;
	}
}
```

Create `Lis.Tools/Pinchtab/PinchtabServerState.cs`:

```csharp
namespace Lis.Tools.Pinchtab;

/// <summary>
/// Shared mutable state between <see cref="PinchtabProcessManager" /> (writer)
/// and <see cref="PinchtabClient" /> (reader). The process manager sets the auth
/// token before spawning the server; the client uses it for Bearer auth.
/// </summary>
public sealed class PinchtabServerState {
	public string Token { get; set; } = "";
}
```

Create `Lis.Tools/Pinchtab/DisplayProbe.cs`:

```csharp
namespace Lis.Tools.Pinchtab;

/// <summary>Detects whether a graphical display is available for headed browser mode.</summary>
public static class DisplayProbe {
	public static bool IsAvailable() {
		if (!OperatingSystem.IsLinux()) return true;

		return !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("DISPLAY"))
			|| !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("WAYLAND_DISPLAY"));
	}
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test Lis.Tests/Lis.Tests.csproj --filter PinchtabOptionsTests`
Expected: PASS (3 tests).

- [ ] **Step 5: Commit**

```bash
git add Lis.Tools/Pinchtab/PinchtabOptions.cs Lis.Tools/Pinchtab/PinchtabServerState.cs Lis.Tools/Pinchtab/DisplayProbe.cs Lis.Tests/PinchtabOptionsTests.cs
git commit -m "✨ feat(pinchtab): add options, server state, and display probe"
```

---

### Task 2: PinchtabClient (HTTP wrapper)

**Files:**
- Create: `Lis.Tools/Pinchtab/PinchtabClient.cs`

**Interfaces:**
- Consumes: `PinchtabOptions.BaseUrl`, `PinchtabServerState.Token` (Task 1).
- Produces: `IPinchtabClient` with methods listed below plus records `PinchtabProfile(string Id, string Name)`, `PinchtabInstance(string Id, string? ProfileId, string? Port, string Status)`, `PinchtabTab(string TabId, string? Url, string? Title)`. Later tasks (InstanceManager, ProcessManager, BrowserPlugin) depend on these exact signatures.

No unit test for this class — it is a thin HTTP mapping layer; behavior is covered via `IPinchtabClient` fakes in Task 4. Verify with `dotnet build`.

- [ ] **Step 1: Write the implementation**

Create `Lis.Tools/Pinchtab/PinchtabClient.cs`:

```csharp
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
```

- [ ] **Step 2: Verify build**

Run: `dotnet build`
Expected: Build succeeded, 0 errors. (Note: `System.Net.Http.Json` is available via the `Microsoft.Extensions.Http` package already referenced by Lis.Tools.)

- [ ] **Step 3: Commit**

```bash
git add Lis.Tools/Pinchtab/PinchtabClient.cs
git commit -m "✨ feat(pinchtab): add HTTP client for orchestrator API"
```

---

### Task 3: PinchtabBinaryProvisioner (TDD)

**Files:**
- Create: `Lis.Tools/Pinchtab/PinchtabBinaryProvisioner.cs`
- Test: `Lis.Tests/PinchtabBinaryProvisionerTests.cs`

**Interfaces:**
- Consumes: `PinchtabOptions` (Task 1), `IHttpClientFactory`.
- Produces: `PinchtabBinaryProvisioner` with `static string GetDownloadUrl(string version)`, `bool NeedsProvisioning()`, `Task EnsureBinaryAsync(CancellationToken ct)`. Task 5's ProcessManager calls `EnsureBinaryAsync` at startup.

- [ ] **Step 1: Write the failing tests**

Create `Lis.Tests/PinchtabBinaryProvisionerTests.cs`:

```csharp
using System.Runtime.InteropServices;

using Lis.Tools.Pinchtab;

using Microsoft.Extensions.Logging.Abstractions;

namespace Lis.Tests;

public class PinchtabBinaryProvisionerTests : IDisposable {
	private readonly string tempDir;

	public PinchtabBinaryProvisionerTests() {
		this.tempDir = Path.Combine(Path.GetTempPath(), $"pinchtab-test-{Guid.NewGuid():N}");
		Directory.CreateDirectory(this.tempDir);
	}

	public void Dispose() => Directory.Delete(this.tempDir, recursive: true);

	private PinchtabBinaryProvisioner CreateProvisioner(out PinchtabOptions options) {
		options = new PinchtabOptions { DataDir = this.tempDir };
		return new PinchtabBinaryProvisioner(options, httpClientFactory: null!, NullLogger<PinchtabBinaryProvisioner>.Instance);
	}

	[Fact]
	public void Download_url_matches_release_asset_pattern() {
		string url = PinchtabBinaryProvisioner.GetDownloadUrl("v0.14.0");

		string os = OperatingSystem.IsWindows() ? "windows" : OperatingSystem.IsMacOS() ? "darwin" : "linux";
		string arch = RuntimeInformation.OSArchitecture == Architecture.Arm64 ? "arm64" : "amd64";
		string ext = OperatingSystem.IsWindows() ? ".exe" : "";

		Assert.Equal($"https://github.com/pinchtab/pinchtab/releases/download/v0.14.0/pinchtab-{os}-{arch}{ext}", url);
	}

	[Fact]
	public void Needs_provisioning_when_binary_missing() {
		PinchtabBinaryProvisioner provisioner = this.CreateProvisioner(out _);

		Assert.True(provisioner.NeedsProvisioning());
	}

	[Fact]
	public void Skips_provisioning_when_binary_and_version_match() {
		PinchtabBinaryProvisioner provisioner = this.CreateProvisioner(out PinchtabOptions options);

		Directory.CreateDirectory(options.BinDir);
		File.WriteAllText(options.BinaryPath, "fake-binary");
		File.WriteAllText(options.VersionFilePath, "v0.14.0\n");

		Assert.False(provisioner.NeedsProvisioning());
	}

	[Fact]
	public void Needs_provisioning_when_version_differs() {
		PinchtabBinaryProvisioner provisioner = this.CreateProvisioner(out PinchtabOptions options);

		Directory.CreateDirectory(options.BinDir);
		File.WriteAllText(options.BinaryPath, "fake-binary");
		File.WriteAllText(options.VersionFilePath, "v0.13.0");

		Assert.True(provisioner.NeedsProvisioning());
	}
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test Lis.Tests/Lis.Tests.csproj --filter PinchtabBinaryProvisionerTests`
Expected: FAIL to compile — `PinchtabBinaryProvisioner` does not exist.

- [ ] **Step 3: Write the implementation**

Create `Lis.Tools/Pinchtab/PinchtabBinaryProvisioner.cs`:

```csharp
using System.Runtime.InteropServices;

using Lis.Core.Util;

using Microsoft.Extensions.Logging;

namespace Lis.Tools.Pinchtab;

/// <summary>Downloads and pins the pinchtab binary under {DataDir}/bin.</summary>
public sealed partial class PinchtabBinaryProvisioner(
	PinchtabOptions                    options,
	IHttpClientFactory                 httpClientFactory,
	ILogger<PinchtabBinaryProvisioner> logger) {

	public static string GetDownloadUrl(string version) {
		string os   = OperatingSystem.IsWindows() ? "windows" : OperatingSystem.IsMacOS() ? "darwin" : "linux";
		string arch = RuntimeInformation.OSArchitecture == Architecture.Arm64 ? "arm64" : "amd64";
		string ext  = OperatingSystem.IsWindows() ? ".exe" : "";

		return $"https://github.com/pinchtab/pinchtab/releases/download/{version}/pinchtab-{os}-{arch}{ext}";
	}

	public bool NeedsProvisioning() {
		if (!File.Exists(options.BinaryPath)) return true;
		if (!File.Exists(options.VersionFilePath)) return true;

		return File.ReadAllText(options.VersionFilePath).Trim() != options.Version;
	}

	[Trace("PinchtabBinaryProvisioner > EnsureBinaryAsync")]
	public async Task EnsureBinaryAsync(CancellationToken ct) {
		if (!this.NeedsProvisioning()) {
			LogBinaryPresent(logger, options.BinaryPath, options.Version);
			return;
		}

		string url = GetDownloadUrl(options.Version);
		LogDownloading(logger, url);

		Directory.CreateDirectory(options.BinDir);

		using HttpClient client = httpClientFactory.CreateClient("pinchtab-download");
		client.Timeout = TimeSpan.FromMinutes(5);

		// Download to a temp file first so a failed download never leaves a broken binary
		string tempPath = options.BinaryPath + ".tmp";
		await using (Stream download = await client.GetStreamAsync(url, ct))
		await using (FileStream file = File.Create(tempPath)) {
			await download.CopyToAsync(file, ct);
		}

		File.Move(tempPath, options.BinaryPath, overwrite: true);

		if (!OperatingSystem.IsWindows())
			File.SetUnixFileMode(options.BinaryPath,
				UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
				UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
				UnixFileMode.OtherRead | UnixFileMode.OtherExecute);

		await File.WriteAllTextAsync(options.VersionFilePath, options.Version, ct);
		LogDownloaded(logger, options.BinaryPath, options.Version);
	}

	[LoggerMessage(Level = LogLevel.Debug, Message = "Pinchtab binary present at {Path} ({Version})")]
	private static partial void LogBinaryPresent(ILogger logger, string path, string version);

	[LoggerMessage(Level = LogLevel.Information, Message = "Downloading pinchtab from {Url}")]
	private static partial void LogDownloading(ILogger logger, string url);

	[LoggerMessage(Level = LogLevel.Information, Message = "Pinchtab binary ready at {Path} ({Version})")]
	private static partial void LogDownloaded(ILogger logger, string path, string version);
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test Lis.Tests/Lis.Tests.csproj --filter PinchtabBinaryProvisionerTests`
Expected: PASS (4 tests).

- [ ] **Step 5: Commit**

```bash
git add Lis.Tools/Pinchtab/PinchtabBinaryProvisioner.cs Lis.Tests/PinchtabBinaryProvisionerTests.cs
git commit -m "✨ feat(pinchtab): add binary provisioner with version pinning"
```

---

### Task 4: PinchtabInstanceManager (TDD)

**Files:**
- Create: `Lis.Tools/Pinchtab/PinchtabInstanceManager.cs`
- Test: `Lis.Tests/PinchtabInstanceManagerTests.cs`

**Interfaces:**
- Consumes: `IPinchtabClient` (Task 2).
- Produces: `PinchtabInstanceManager` with `Task<AgentBrowserSession> EnsureSessionAsync(long agentId, bool headed, CancellationToken ct)`, `AgentBrowserSession? GetSession(long agentId)`, `Task CloseForAgentAsync(long agentId)`; class `AgentBrowserSession { string InstanceId; bool Headed; string? TabId; }`. Task 6 (BrowserPlugin) and Task 7 (ConversationService) depend on these exact members.

- [ ] **Step 1: Write the failing tests**

Create `Lis.Tests/PinchtabInstanceManagerTests.cs`:

```csharp
using Lis.Tools.Pinchtab;

using Microsoft.Extensions.Logging.Abstractions;

namespace Lis.Tests;

public class PinchtabInstanceManagerTests {

	private sealed class FakePinchtabClient : IPinchtabClient {
		public List<PinchtabProfile>  Profiles         = new();
		public List<string>           StoppedProfiles  = new();
		public List<string>           StoppedInstances = new();
		public List<(string ProfileId, bool Headed)> Started = new();
		public HashSet<string>        DeadInstances    = new();
		private int nextId = 1;

		public Task<bool> IsHealthyAsync(CancellationToken ct = default) => Task.FromResult(true);

		public Task<PinchtabProfile?> FindProfileAsync(string name, CancellationToken ct = default) =>
			Task.FromResult(this.Profiles.FirstOrDefault(p => p.Name == name));

		public Task<PinchtabProfile> CreateProfileAsync(string name, CancellationToken ct = default) {
			PinchtabProfile profile = new($"prof_{this.nextId++}", name);
			this.Profiles.Add(profile);
			return Task.FromResult(profile);
		}

		public Task StopProfileAsync(string profileId, CancellationToken ct = default) {
			this.StoppedProfiles.Add(profileId);
			return Task.CompletedTask;
		}

		public Task<PinchtabInstance> StartInstanceAsync(string profileId, bool headed, CancellationToken ct = default) {
			this.Started.Add((profileId, headed));
			return Task.FromResult(new PinchtabInstance($"inst_{this.nextId++}", profileId, "9999", "running"));
		}

		public Task<PinchtabInstance?> GetInstanceAsync(string instanceId, CancellationToken ct = default) =>
			Task.FromResult(this.DeadInstances.Contains(instanceId)
				? (PinchtabInstance?)null
				: new PinchtabInstance(instanceId, null, "9999", "running"));

		public Task StopInstanceAsync(string instanceId, CancellationToken ct = default) {
			this.StoppedInstances.Add(instanceId);
			return Task.CompletedTask;
		}

		public Task<PinchtabTab> OpenTabAsync(string instanceId, string url, CancellationToken ct = default) =>
			Task.FromResult(new PinchtabTab("tab_1", url, "Title"));

		public Task<string> ListTabsAsync(string instanceId, CancellationToken ct = default) => Task.FromResult("[]");
		public Task<string> NavigateAsync(string tabId, string url, CancellationToken ct = default) => Task.FromResult("{}");
		public Task<string> SnapshotAsync(string tabId, string filter, int maxTokens, CancellationToken ct = default) => Task.FromResult("");
		public Task<string> ActionAsync(string tabId, object payload, CancellationToken ct = default) => Task.FromResult("{}");
		public Task<string> EvaluateAsync(string tabId, string expression, CancellationToken ct = default) => Task.FromResult("{}");
		public Task<byte[]> ScreenshotAsync(string tabId, CancellationToken ct = default) => Task.FromResult(Array.Empty<byte>());
		public Task ShutdownAsync(CancellationToken ct = default) => Task.CompletedTask;
	}

	private static PinchtabInstanceManager CreateManager(FakePinchtabClient client) =>
		new(client, NullLogger<PinchtabInstanceManager>.Instance);

	[Fact]
	public async Task Creates_profile_and_starts_instance_on_first_call() {
		FakePinchtabClient client = new();
		PinchtabInstanceManager manager = CreateManager(client);

		AgentBrowserSession session = await manager.EnsureSessionAsync(agentId: 3, headed: false, CancellationToken.None);

		Assert.Single(client.Profiles);
		Assert.Equal("agent-3", client.Profiles[0].Name);
		Assert.Single(client.Started);
		Assert.False(client.Started[0].Headed);
		Assert.NotNull(session.InstanceId);
		Assert.False(session.Headed);
	}

	[Fact]
	public async Task Reuses_running_instance_on_second_call() {
		FakePinchtabClient client = new();
		PinchtabInstanceManager manager = CreateManager(client);

		AgentBrowserSession first  = await manager.EnsureSessionAsync(3, headed: false, CancellationToken.None);
		AgentBrowserSession second = await manager.EnsureSessionAsync(3, headed: false, CancellationToken.None);

		Assert.Same(first, second);
		Assert.Single(client.Started);
	}

	[Fact]
	public async Task Reuses_existing_profile() {
		FakePinchtabClient client = new();
		client.Profiles.Add(new PinchtabProfile("prof_existing", "agent-3"));
		PinchtabInstanceManager manager = CreateManager(client);

		await manager.EnsureSessionAsync(3, headed: false, CancellationToken.None);

		Assert.Single(client.Profiles);
		Assert.Equal("prof_existing", client.Started[0].ProfileId);
	}

	[Fact]
	public async Task Restarts_when_tracked_instance_is_dead() {
		FakePinchtabClient client = new();
		PinchtabInstanceManager manager = CreateManager(client);

		AgentBrowserSession first = await manager.EnsureSessionAsync(3, headed: false, CancellationToken.None);
		client.DeadInstances.Add(first.InstanceId);

		AgentBrowserSession second = await manager.EnsureSessionAsync(3, headed: false, CancellationToken.None);

		Assert.NotSame(first, second);
		Assert.Equal(2, client.Started.Count);
	}

	[Fact]
	public async Task Restarts_instance_when_mode_changes() {
		FakePinchtabClient client = new();
		PinchtabInstanceManager manager = CreateManager(client);

		AgentBrowserSession headless = await manager.EnsureSessionAsync(3, headed: false, CancellationToken.None);
		AgentBrowserSession headed   = await manager.EnsureSessionAsync(3, headed: true, CancellationToken.None);

		Assert.NotSame(headless, headed);
		Assert.Contains(headless.InstanceId, client.StoppedInstances);
		Assert.True(headed.Headed);
		Assert.Equal(2, client.Started.Count);
	}

	[Fact]
	public async Task Close_stops_instance_and_forgets_session() {
		FakePinchtabClient client = new();
		PinchtabInstanceManager manager = CreateManager(client);

		AgentBrowserSession session = await manager.EnsureSessionAsync(3, headed: false, CancellationToken.None);
		await manager.CloseForAgentAsync(3);

		Assert.Contains(session.InstanceId, client.StoppedInstances);
		Assert.Null(manager.GetSession(3));
	}

	[Fact]
	public async Task Close_is_noop_without_session() {
		FakePinchtabClient client = new();
		PinchtabInstanceManager manager = CreateManager(client);

		await manager.CloseForAgentAsync(42);

		Assert.Empty(client.StoppedInstances);
	}
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test Lis.Tests/Lis.Tests.csproj --filter PinchtabInstanceManagerTests`
Expected: FAIL to compile — `PinchtabInstanceManager` does not exist.

- [ ] **Step 3: Write the implementation**

Create `Lis.Tools/Pinchtab/PinchtabInstanceManager.cs`:

```csharp
using System.Collections.Concurrent;

using Lis.Core.Util;

using Microsoft.Extensions.Logging;

namespace Lis.Tools.Pinchtab;

/// <summary>A live browser instance owned by one agent.</summary>
public sealed class AgentBrowserSession {
	public required string InstanceId { get; init; }
	public required bool   Headed     { get; init; }
	public string?         TabId      { get; set; }
}

/// <summary>
/// Tracks one pinchtab browser instance per agent (profile "agent-{agentId}").
/// Instances start lazily on first browser tool call and close at conversation turn end.
/// </summary>
public sealed partial class PinchtabInstanceManager(
	IPinchtabClient                  client,
	ILogger<PinchtabInstanceManager> logger) {

	private readonly ConcurrentDictionary<long, AgentBrowserSession> sessions = new();
	private readonly SemaphoreSlim gate = new(1, 1);

	[Trace("PinchtabInstanceManager > EnsureSessionAsync")]
	public async Task<AgentBrowserSession> EnsureSessionAsync(long agentId, bool headed, CancellationToken ct) {
		await this.gate.WaitAsync(ct);
		try {
			// Reuse a live instance with matching mode; drop dead or mode-mismatched ones
			if (this.sessions.TryGetValue(agentId, out AgentBrowserSession? existing)) {
				PinchtabInstance? live = await client.GetInstanceAsync(existing.InstanceId, ct);

				if (live is { Status: "running" } && existing.Headed == headed) return existing;

				if (live is { Status: "running" }) {
					LogModeChange(logger, agentId, headed);
					await client.StopInstanceAsync(existing.InstanceId, ct);
				}
				this.sessions.TryRemove(agentId, out _);
			}

			string profileId = await this.EnsureProfileAsync(agentId, ct);

			// A profile can only have one active instance; stop any stale one (ignore errors)
			try { await client.StopProfileAsync(profileId, ct); } catch (HttpRequestException) { }

			PinchtabInstance instance = await client.StartInstanceAsync(profileId, headed, ct);
			instance = await this.WaitRunningAsync(instance, ct);

			AgentBrowserSession session = new() { InstanceId = instance.Id, Headed = headed };
			this.sessions[agentId] = session;
			LogStarted(logger, agentId, instance.Id, headed);
			return session;
		} finally {
			this.gate.Release();
		}
	}

	public AgentBrowserSession? GetSession(long agentId) =>
		this.sessions.TryGetValue(agentId, out AgentBrowserSession? session) ? session : null;

	[Trace("PinchtabInstanceManager > CloseForAgentAsync")]
	public async Task CloseForAgentAsync(long agentId) {
		if (!this.sessions.TryRemove(agentId, out AgentBrowserSession? session)) return;

		try {
			await client.StopInstanceAsync(session.InstanceId);
			LogClosed(logger, agentId, session.InstanceId);
		} catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException) {
			LogCloseFailed(logger, ex, agentId, session.InstanceId);
		}
	}

	private async Task<string> EnsureProfileAsync(long agentId, CancellationToken ct) {
		string name = $"agent-{agentId}";

		PinchtabProfile? profile = await client.FindProfileAsync(name, ct);
		profile ??= await client.CreateProfileAsync(name, ct);

		return profile.Id;
	}

	private async Task<PinchtabInstance> WaitRunningAsync(PinchtabInstance instance, CancellationToken ct) {
		if (instance.Status == "running") return instance;

		using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
		timeout.CancelAfter(TimeSpan.FromSeconds(30));

		while (true) {
			await Task.Delay(250, timeout.Token);
			PinchtabInstance? current = await client.GetInstanceAsync(instance.Id, timeout.Token);

			if (current is { Status: "running" }) return current;
			if (current is null or { Status: "stopped" })
				throw new InvalidOperationException($"Pinchtab instance {instance.Id} failed to start.");
		}
	}

	[LoggerMessage(Level = LogLevel.Information, Message = "Started pinchtab instance {InstanceId} for agent {AgentId} (headed: {Headed})")]
	private static partial void LogStarted(ILogger logger, long agentId, string instanceId, bool headed);

	[LoggerMessage(Level = LogLevel.Information, Message = "Restarting pinchtab instance for agent {AgentId} due to mode change (headed: {Headed})")]
	private static partial void LogModeChange(ILogger logger, long agentId, bool headed);

	[LoggerMessage(Level = LogLevel.Information, Message = "Closed pinchtab instance {InstanceId} for agent {AgentId}")]
	private static partial void LogClosed(ILogger logger, long agentId, string instanceId);

	[LoggerMessage(Level = LogLevel.Warning, Message = "Failed to close pinchtab instance {InstanceId} for agent {AgentId}")]
	private static partial void LogCloseFailed(ILogger logger, Exception ex, long agentId, string instanceId);
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test Lis.Tests/Lis.Tests.csproj --filter PinchtabInstanceManagerTests`
Expected: PASS (7 tests).

- [ ] **Step 5: Commit**

```bash
git add Lis.Tools/Pinchtab/PinchtabInstanceManager.cs Lis.Tests/PinchtabInstanceManagerTests.cs
git commit -m "✨ feat(pinchtab): add per-agent instance manager"
```

---

### Task 5: PinchtabProcessManager (sidecar supervisor)

**Files:**
- Create: `Lis.Tools/Pinchtab/PinchtabProcessManager.cs`

**Interfaces:**
- Consumes: `PinchtabOptions`, `PinchtabServerState` (Task 1), `PinchtabBinaryProvisioner.EnsureBinaryAsync` (Task 3), `IPinchtabClient.IsHealthyAsync`/`ShutdownAsync` (Task 2).
- Produces: `PinchtabProcessManager : BackgroundService`, registered via `AddHostedService` in Task 7.

No unit test — process supervision is integration-level; verified manually in Task 10. Verify with `dotnet build`.

- [ ] **Step 1: Write the implementation**

Create `Lis.Tools/Pinchtab/PinchtabProcessManager.cs`:

```csharp
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;

using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Lis.Tools.Pinchtab;

/// <summary>
/// Supervises the pinchtab server as a sidecar: provisions the binary, writes config,
/// spawns the process, restarts on crash with backoff, and restarts daily at RestartHour.
/// </summary>
public sealed partial class PinchtabProcessManager(
	PinchtabOptions                  options,
	PinchtabServerState              serverState,
	PinchtabBinaryProvisioner        provisioner,
	IPinchtabClient                  client,
	ILogger<PinchtabProcessManager>  logger) : BackgroundService {

	private Process? process;

	protected override async Task ExecuteAsync(CancellationToken stoppingToken) {
		try {
			await provisioner.EnsureBinaryAsync(stoppingToken);
		} catch (Exception ex) when (ex is not OperationCanceledException) {
			LogProvisionFailed(logger, ex);
			return;
		}

		serverState.Token = Environment.GetEnvironmentVariable("PINCHTAB_TOKEN") is { Length: > 0 } fixedToken
			? fixedToken
			: Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(24));

		this.EnsureConfigFile();

		TimeSpan backoff = TimeSpan.FromSeconds(2);
		DateTimeOffset lastStart;

		while (!stoppingToken.IsCancellationRequested) {
			lastStart = DateTimeOffset.UtcNow;

			try {
				Task exited = this.StartProcess(stoppingToken);
				await this.WaitHealthyAsync(stoppingToken);
				LogServerReady(logger, options.BaseUrl);

				Task restartDelay = Task.Delay(DelayUntilRestartHour(), stoppingToken);
				Task finished     = await Task.WhenAny(exited, restartDelay);

				if (finished == restartDelay) {
					LogScheduledRestart(logger, options.RestartHour);
					await this.StopProcessAsync();
				} else {
					LogServerExited(logger, this.process?.ExitCode ?? -1);
				}
			} catch (OperationCanceledException) {
				break;
			} catch (Exception ex) {
				LogServerCrashed(logger, ex);
				await this.StopProcessAsync();
			}

			if (stoppingToken.IsCancellationRequested) break;

			// Reset backoff after 5 stable minutes; otherwise back off exponentially up to 60s
			if (DateTimeOffset.UtcNow - lastStart > TimeSpan.FromMinutes(5)) backoff = TimeSpan.FromSeconds(2);

			await Task.Delay(backoff, stoppingToken);
			backoff = TimeSpan.FromSeconds(Math.Min(backoff.TotalSeconds * 2, 60));
		}
	}

	public override async Task StopAsync(CancellationToken cancellationToken) {
		await base.StopAsync(cancellationToken);
		await this.StopProcessAsync();
	}

	private Task StartProcess(CancellationToken ct) {
		ProcessStartInfo startInfo = new() {
			FileName               = options.BinaryPath,
			Arguments              = "server",
			UseShellExecute        = false,
			RedirectStandardOutput = true,
			RedirectStandardError  = true,
		};
		startInfo.Environment["PINCHTAB_CONFIG"] = Path.GetFullPath(options.ConfigPath);
		startInfo.Environment["PINCHTAB_TOKEN"]  = serverState.Token;

		Process proc = new() { StartInfo = startInfo, EnableRaisingEvents = true };
		proc.OutputDataReceived += (_, e) => { if (e.Data is not null) LogServerStdout(logger, e.Data); };
		proc.ErrorDataReceived  += (_, e) => { if (e.Data is not null) LogServerStderr(logger, e.Data); };

		TaskCompletionSource exitedTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
		proc.Exited += (_, _) => exitedTcs.TrySetResult();

		proc.Start();
		proc.BeginOutputReadLine();
		proc.BeginErrorReadLine();

		this.process = proc;
		LogServerStarted(logger, proc.Id, options.BinaryPath);
		return exitedTcs.Task;
	}

	private async Task WaitHealthyAsync(CancellationToken ct) {
		using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
		timeout.CancelAfter(TimeSpan.FromSeconds(30));

		while (true) {
			if (await client.IsHealthyAsync(timeout.Token)) return;
			if (this.process is { HasExited: true })
				throw new InvalidOperationException($"Pinchtab server exited during startup (code {this.process.ExitCode}).");

			await Task.Delay(250, timeout.Token);
		}
	}

	private async Task StopProcessAsync() {
		Process? proc = this.process;
		this.process = null;
		if (proc is null || proc.HasExited) return;

		// Graceful shutdown first, then kill the process tree
		try {
			using CancellationTokenSource cts = new(TimeSpan.FromSeconds(5));
			await client.ShutdownAsync(cts.Token);
			await proc.WaitForExitAsync(cts.Token);
		} catch (Exception) {
			try { proc.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
		}
	}

	private void EnsureConfigFile() {
		if (File.Exists(options.ConfigPath)) return;

		Directory.CreateDirectory(options.DataDir);

		Dictionary<string, object> config = new() {
			["server"]   = new Dictionary<string, object> {
				["port"]     = options.Port.ToString(),
				["stateDir"] = Path.GetFullPath(Path.Combine(options.DataDir, "state")),
			},
			["security"] = new Dictionary<string, object> {
				["allowEvaluate"]  = true,
				["allowedDomains"] = new[] { "*" },
			},
			["profiles"] = new Dictionary<string, object> {
				["baseDir"] = Path.GetFullPath(Path.Combine(options.DataDir, "profiles")),
			},
			["multiInstance"]    = new Dictionary<string, object> { ["strategy"] = "explicit" },
			["instanceDefaults"] = new Dictionary<string, object> { ["mode"] = "headless" },
		};

		if (options.ChromePath is not null)
			config["browser"] = new Dictionary<string, object> { ["binary"] = options.ChromePath };

		File.WriteAllText(options.ConfigPath, JsonSerializer.Serialize(config, new JsonSerializerOptions { WriteIndented = true }));
		LogConfigWritten(logger, options.ConfigPath);
	}

	private TimeSpan DelayUntilRestartHour() {
		DateTimeOffset now  = DateTimeOffset.Now;
		DateTimeOffset next = new(now.Year, now.Month, now.Day, options.RestartHour, 0, 0, now.Offset);
		if (next <= now) next = next.AddDays(1);

		return next - now;
	}

	[LoggerMessage(Level = LogLevel.Error, Message = "Failed to provision pinchtab binary; browser tools unavailable")]
	private static partial void LogProvisionFailed(ILogger logger, Exception ex);

	[LoggerMessage(Level = LogLevel.Information, Message = "Pinchtab server started (pid {Pid}) from {Path}")]
	private static partial void LogServerStarted(ILogger logger, int pid, string path);

	[LoggerMessage(Level = LogLevel.Information, Message = "Pinchtab server healthy at {BaseUrl}")]
	private static partial void LogServerReady(ILogger logger, string baseUrl);

	[LoggerMessage(Level = LogLevel.Information, Message = "Performing scheduled daily pinchtab restart (hour {Hour})")]
	private static partial void LogScheduledRestart(ILogger logger, int hour);

	[LoggerMessage(Level = LogLevel.Warning, Message = "Pinchtab server exited unexpectedly (code {ExitCode}); restarting")]
	private static partial void LogServerExited(ILogger logger, int exitCode);

	[LoggerMessage(Level = LogLevel.Error, Message = "Pinchtab server crashed; restarting with backoff")]
	private static partial void LogServerCrashed(ILogger logger, Exception ex);

	[LoggerMessage(Level = LogLevel.Information, Message = "Wrote pinchtab config to {Path}")]
	private static partial void LogConfigWritten(ILogger logger, string path);

	[LoggerMessage(Level = LogLevel.Debug, Message = "[pinchtab] {Line}")]
	private static partial void LogServerStdout(ILogger logger, string line);

	[LoggerMessage(Level = LogLevel.Debug, Message = "[pinchtab!] {Line}")]
	private static partial void LogServerStderr(ILogger logger, string line);
}
```

Note: `Lis.Tools` needs the `Microsoft.Extensions.Hosting.Abstractions` package for `BackgroundService`. Add to `Lis.Tools/Lis.Tools.csproj` in the `<ItemGroup>` with other `PackageReference`s:

```xml
<PackageReference Include="Microsoft.Extensions.Hosting.Abstractions" Version="10.0.3" />
```

- [ ] **Step 2: Verify build**

Run: `dotnet build`
Expected: Build succeeded, 0 errors.

- [ ] **Step 3: Commit**

```bash
git add Lis.Tools/Pinchtab/PinchtabProcessManager.cs Lis.Tools/Lis.Tools.csproj
git commit -m "✨ feat(pinchtab): add sidecar process manager with crash and daily restart"
```

---

### Task 6: Rewrite BrowserPlugin against pinchtab

**Files:**
- Rewrite: `Lis.Tools/BrowserPlugin.cs` (full replacement)
- Delete: `Lis.Tools/Browser/BrowserSessionManager.cs` (and the now-empty `Lis.Tools/Browser/` directory)
- Modify: `Lis.Tools/Lis.Tools.csproj` (remove Playwright)

**Interfaces:**
- Consumes: `PinchtabInstanceManager.EnsureSessionAsync/GetSession` and `AgentBrowserSession.TabId` (Task 4), `IPinchtabClient` tab methods (Task 2), `DisplayProbe.IsAvailable()` (Task 1), `PinchtabOptions.Enabled`.
- Produces: kernel plugin "browser" with tools: `navigate(url, headed?)`, `snapshot(filter?)`, `click(ref)`, `type(ref, text)`, `press(key, ref?)`, `scroll(direction)`, `screenshot()`, `eval(code)`, `tabs()`.

- [ ] **Step 1: Replace `Lis.Tools/BrowserPlugin.cs` entirely with:**

```csharp
using System.ComponentModel;

using Lis.Core.Util;
using Lis.Tools.Pinchtab;

using Microsoft.SemanticKernel;

namespace Lis.Tools;

/// <summary>
/// Browser automation via the PinchTab sidecar. Each agent gets its own browser
/// instance (profile "agent-{agentId}"), lazily started on first navigate and
/// closed automatically when the conversation turn ends.
/// </summary>
public sealed class BrowserPlugin(PinchtabInstanceManager instances, IPinchtabClient client) {

	[KernelFunction("navigate")]
	[Description("Open a URL in the agent's browser. Starts the browser automatically on first use. Use headed=true to show a visible browser window (only works when a display is available).")]
	[ToolSummarization(SummarizationPolicy.Prune)]
	[ToolAuthorization(ToolAuthLevel.OwnerOnly)]
	public async Task<string> NavigateAsync(
		[Description("URL to navigate to")] string url,
		[Description("Show a visible browser window (default: false, headless)")] bool headed = false,
		CancellationToken ct = default) {
		await ToolContext.NotifyAsync($"🌐 Opening: {url}", ct);

		if (headed && !DisplayProbe.IsAvailable())
			return "Headed mode is not available: no display detected on this system (e.g. running in Docker). Retry with headed=false.";

		try {
			long agentId = ToolContext.AgentId ?? throw new InvalidOperationException("No agent context");
			AgentBrowserSession session = await instances.EnsureSessionAsync(agentId, headed, ct);

			if (session.TabId is null) {
				PinchtabTab tab = await client.OpenTabAsync(session.InstanceId, url, ct);
				session.TabId = tab.TabId;
				return $"Navigated to {url} (title: {tab.Title}). Use browser-snapshot to read the page.";
			}

			await client.NavigateAsync(session.TabId, url, ct);
			return $"Navigated to {url}. Use browser-snapshot to read the page.";
		} catch (Exception ex) {
			return $"Navigation error: {ex.Message}";
		}
	}

	[KernelFunction("snapshot")]
	[Description("Read the current page as an accessibility tree with element refs (e0, e1, ...). Refs are used by browser-click, browser-type, and browser-press. Filter 'interactive' returns only actionable elements; 'full' returns all text content.")]
	[ToolSummarization(SummarizationPolicy.Summarize)]
	[ToolAuthorization(ToolAuthLevel.OwnerOnly)]
	public async Task<string> SnapshotAsync(
		[Description("Filter: 'interactive' (default, actionable elements only) or 'full' (all content)")] string filter = "interactive",
		CancellationToken ct = default) {
		try {
			(_, string tabId) = await this.GetTabOrThrowAsync();
			return await client.SnapshotAsync(tabId, filter == "full" ? "none" : "interactive", maxTokens: 4000, ct);
		} catch (InvalidOperationException ex) {
			return ex.Message;
		} catch (Exception ex) {
			return $"Snapshot error: {ex.Message}";
		}
	}

	[KernelFunction("click")]
	[Description("Click an element by its ref from browser-snapshot (e.g. 'e5').")]
	[ToolSummarization(SummarizationPolicy.Prune)]
	[ToolAuthorization(ToolAuthLevel.OwnerOnly)]
	public async Task<string> ClickAsync(
		[Description("Element ref from browser-snapshot (e.g. 'e5')")] string @ref,
		CancellationToken ct = default) {
		try {
			(_, string tabId) = await this.GetTabOrThrowAsync();
			await client.ActionAsync(tabId, new { kind = "click", @ref }, ct);
			return $"Clicked {@ref}. Page may have changed — use browser-snapshot to see the result.";
		} catch (InvalidOperationException ex) {
			return ex.Message;
		} catch (Exception ex) {
			return $"Click error: {ex.Message}";
		}
	}

	[KernelFunction("type")]
	[Description("Fill a form field by its ref from browser-snapshot. Replaces existing content.")]
	[ToolSummarization(SummarizationPolicy.Prune)]
	[ToolAuthorization(ToolAuthLevel.OwnerOnly)]
	public async Task<string> TypeAsync(
		[Description("Element ref from browser-snapshot (e.g. 'e3')")] string @ref,
		[Description("Text to fill into the field")] string text,
		CancellationToken ct = default) {
		try {
			(_, string tabId) = await this.GetTabOrThrowAsync();
			await client.ActionAsync(tabId, new { kind = "fill", @ref, value = text }, ct);
			return $"Typed into {@ref}.";
		} catch (InvalidOperationException ex) {
			return ex.Message;
		} catch (Exception ex) {
			return $"Type error: {ex.Message}";
		}
	}

	[KernelFunction("press")]
	[Description("Press a keyboard key (e.g. 'Enter', 'Tab', 'Escape'), optionally on a specific element.")]
	[ToolSummarization(SummarizationPolicy.Prune)]
	[ToolAuthorization(ToolAuthLevel.OwnerOnly)]
	public async Task<string> PressAsync(
		[Description("Key to press (e.g. 'Enter', 'Tab', 'Escape', 'ArrowDown')")] string key,
		[Description("Element ref to focus first (optional)")] string? @ref = null,
		CancellationToken ct = default) {
		try {
			(_, string tabId) = await this.GetTabOrThrowAsync();
			object payload = @ref is null ? new { kind = "press", key } : new { kind = "press", key, @ref };
			await client.ActionAsync(tabId, payload, ct);
			return $"Pressed {key}.";
		} catch (InvalidOperationException ex) {
			return ex.Message;
		} catch (Exception ex) {
			return $"Press error: {ex.Message}";
		}
	}

	[KernelFunction("scroll")]
	[Description("Scroll the page up or down.")]
	[ToolSummarization(SummarizationPolicy.Prune)]
	[ToolAuthorization(ToolAuthLevel.OwnerOnly)]
	public async Task<string> ScrollAsync(
		[Description("Direction: 'down' or 'up'")] string direction = "down",
		CancellationToken ct = default) {
		try {
			(_, string tabId) = await this.GetTabOrThrowAsync();
			await client.ActionAsync(tabId, new { kind = "scroll", direction }, ct);
			return $"Scrolled {direction}.";
		} catch (InvalidOperationException ex) {
			return ex.Message;
		} catch (Exception ex) {
			return $"Scroll error: {ex.Message}";
		}
	}

	[KernelFunction("screenshot")]
	[Description("Take a visual screenshot of the current page. Use browser-snapshot for text content instead — screenshots cost more tokens.")]
	[ToolSummarization(SummarizationPolicy.Prune)]
	[ToolAuthorization(ToolAuthLevel.OwnerOnly)]
	public async Task<string> ScreenshotAsync(CancellationToken ct = default) {
		try {
			(_, string tabId) = await this.GetTabOrThrowAsync();
			byte[] bytes = await client.ScreenshotAsync(tabId, ct);
			return $"data:image/png;base64,{Convert.ToBase64String(bytes)}";
		} catch (InvalidOperationException ex) {
			return ex.Message;
		} catch (Exception ex) {
			return $"Screenshot error: {ex.Message}";
		}
	}

	[KernelFunction("eval")]
	[Description("Execute JavaScript in the page context and return the result. Useful for extracting data or interacting with page APIs.")]
	[ToolSummarization(SummarizationPolicy.Summarize)]
	[ToolAuthorization(ToolAuthLevel.OwnerOnly)]
	public async Task<string> EvalAsync(
		[Description("JavaScript expression to execute")] string code,
		CancellationToken ct = default) {
		await ToolContext.NotifyAsync("🔧 Evaluating JS", ct);

		try {
			(_, string tabId) = await this.GetTabOrThrowAsync();
			return await client.EvaluateAsync(tabId, code, ct);
		} catch (InvalidOperationException ex) {
			return ex.Message;
		} catch (Exception ex) {
			return $"Evaluate error: {ex.Message}";
		}
	}

	[KernelFunction("tabs")]
	[Description("List all open browser tabs with their titles and URLs.")]
	[ToolSummarization(SummarizationPolicy.Prune)]
	[ToolAuthorization(ToolAuthLevel.OwnerOnly)]
	public async Task<string> TabsAsync(CancellationToken ct = default) {
		try {
			(AgentBrowserSession session, _) = await this.GetTabOrThrowAsync();
			return await client.ListTabsAsync(session.InstanceId, ct);
		} catch (InvalidOperationException ex) {
			return ex.Message;
		} catch (Exception ex) {
			return $"Tabs error: {ex.Message}";
		}
	}

	private Task<(AgentBrowserSession Session, string TabId)> GetTabOrThrowAsync() {
		long agentId = ToolContext.AgentId ?? throw new InvalidOperationException("No agent context");

		AgentBrowserSession? session = instances.GetSession(agentId);
		if (session?.TabId is null)
			throw new InvalidOperationException("No page open. Use browser-navigate first.");

		return Task.FromResult((session, session.TabId));
	}
}
```

- [ ] **Step 2: Delete the Playwright session manager**

```bash
git rm Lis.Tools/Browser/BrowserSessionManager.cs
```

- [ ] **Step 3: Remove the Playwright package**

In `Lis.Tools/Lis.Tools.csproj`, delete the line:

```xml
<PackageReference Include="Microsoft.Playwright" Version="1.*" />
```

- [ ] **Step 4: Verify build fails only at known integration points**

Run: `dotnet build`
Expected: errors ONLY in `Lis.Agent/AgentSetup.cs` (missing `Lis.Tools.Browser` using + `BrowserSessionManager` registration). These are fixed in Task 7. If errors appear elsewhere, investigate before proceeding.

To keep commits green, do Task 7 Step 1 (AgentSetup changes) in this same commit — see note in Task 7.

- [ ] **Step 5: Commit** (after Task 7 Step 1 makes the build green)

Deferred: this task commits together with Task 7.

---

### Task 7: Wire-up (AgentSetup + ConversationService turn-end close)

**Files:**
- Modify: `Lis.Agent/AgentSetup.cs`
- Modify: `Lis.Agent/ConversationService.cs`

**Interfaces:**
- Consumes: `PinchtabOptions.FromEnvironment()`, `PinchtabServerState`, `PinchtabBinaryProvisioner`, `IPinchtabClient`/`PinchtabClient`, `PinchtabInstanceManager.CloseForAgentAsync(long)`, `PinchtabProcessManager` (Tasks 1-5).

- [ ] **Step 1: Update `Lis.Agent/AgentSetup.cs`**

Replace the using `using Lis.Tools.Browser;` with `using Lis.Tools.Pinchtab;`.

Replace line 39 (`kernel.Plugins.AddFromType<BrowserPlugin>(...)`) with:

```csharp
			if (sp.GetRequiredService<PinchtabOptions>().Enabled)
				kernel.Plugins.AddFromType<BrowserPlugin>(pluginName: "browser", serviceProvider: sp);
```

Replace line 63 (`services.AddSingleton<BrowserSessionManager>();`) with:

```csharp
		// Browser (PinchTab sidecar)
		PinchtabOptions pinchtabOptions = PinchtabOptions.FromEnvironment();
		services.AddSingleton(pinchtabOptions);
		services.AddSingleton<PinchtabServerState>();
		services.AddSingleton<PinchtabBinaryProvisioner>();
		services.AddSingleton<IPinchtabClient, PinchtabClient>();
		services.AddSingleton<PinchtabInstanceManager>();
		if (pinchtabOptions.Enabled) services.AddHostedService<PinchtabProcessManager>();
```

Note: `AddHostedService` lives in `Microsoft.Extensions.Hosting.Abstractions` (added to Lis.Tools in Task 5, transitively available here). If the compiler still cannot resolve it, add the same `<PackageReference Include="Microsoft.Extensions.Hosting.Abstractions" Version="10.0.3" />` to `Lis.Agent/Lis.Agent.csproj`.

- [ ] **Step 2: Verify build and commit Tasks 6+7 plugin work**

Run: `dotnet build`
Expected: Build succeeded, 0 errors.

```bash
git add Lis.Tools/BrowserPlugin.cs Lis.Tools/Lis.Tools.csproj Lis.Agent/AgentSetup.cs
git commit -m "✨ feat(browser): rewrite BrowserPlugin on pinchtab, drop Playwright"
```

- [ ] **Step 3: Add turn-end instance close in `Lis.Agent/ConversationService.cs`**

Add `using Lis.Tools.Pinchtab;` to the usings.

Add a constructor parameter to the primary constructor (after `errorSuppression`, keeping alignment):

```csharp
	ErrorSuppressionService      errorSuppression,
	PinchtabInstanceManager      pinchtabInstances,
	OpikTracer?                  opikTracer,
```

Append a `finally` block to the `try { ... } catch (Exception ex) ... } catch (OperationCanceledException) { ... }` structure around the tool loop (the `try` at ~line 293 in `RespondAsync`; the last `catch (OperationCanceledException)` ends at ~line 367):

```csharp
		} catch (OperationCanceledException) {
			if (opikTracer is not null) await opikTracer.EndTraceAsync(null, lastUsage);
			throw;
		} finally {
			// Close the agent's pinchtab browser instance at the end of the conversation turn
			try { await pinchtabInstances.CloseForAgentAsync(agent.Id); }
			catch (Exception ex) { logger.LogWarning(ex, "Failed to close pinchtab instance for agent {AgentId}", agent.Id); }
		}
```

- [ ] **Step 4: Verify build and run all tests**

Run: `dotnet build && dotnet test Lis.Tests/Lis.Tests.csproj`
Expected: Build succeeded; all tests pass.

- [ ] **Step 5: Commit**

```bash
git add Lis.Agent/ConversationService.cs
git commit -m "✨ feat(browser): close agent browser instance at conversation turn end"
```

---

### Task 8: Remove web-fetch + update docs and help files

**Files:**
- Modify: `Lis.Tools/WebPlugin.cs`
- Rewrite: `Lis.Tools/Help/browser.md`
- Rewrite: `Lis.Tools/Help/web.md`
- Modify: `docs/TOOLS.md:98-121`
- Rewrite: `docs/BROWSER.md`

- [ ] **Step 1: Remove fetch from `Lis.Tools/WebPlugin.cs`**

Delete the entire `FetchAsync` method (lines 77-112), the `StripHtmlTags()` and `CollapseWhitespace()` generated regexes (lines 114-118), and the now-unused usings `System.Text.RegularExpressions`. Keep `System.Web` (used by `HttpUtility.UrlEncode` in search). Change the class from `partial` to non-partial (no generated regexes remain):

```csharp
public sealed class WebPlugin(IHttpClientFactory httpClientFactory, ILogger<WebPlugin> logger) {
```

Also update the `search` tool description (it references web-fetch):

```csharp
	[Description("Search the web via Brave Search. Returns titles, URLs, and text snippets for matching results. Use browser-navigate to read full page content from a result URL.")]
```

- [ ] **Step 2: Replace `Lis.Tools/Help/browser.md` entirely with:**

```markdown
# Browser

Browser automation via PinchTab. Each agent gets its own persistent browser profile. The browser starts automatically on first navigate and closes when the conversation turn ends (cookies and logins persist across turns).

## Tools

### browser-navigate(url, headed?)
Open a URL. Starts the browser automatically on first use.
- `url` (string): Full URL including protocol (e.g. `https://example.com`).
- `headed` (bool, optional): Show a visible browser window (default false = headless). Not available without a display (e.g. Docker).

### browser-snapshot(filter?)
Read the page as an accessibility tree with element refs (`e0`, `e1`, ...).
- `filter` (string, optional): `interactive` (default, actionable elements only) or `full` (all content).
- Refs are used by browser-click, browser-type, and browser-press.
- Cheaper than screenshot for reading content.

### browser-click(ref)
Click an element by its ref.
- `ref` (string): Element ref from browser-snapshot (e.g. `e5`).

### browser-type(ref, text)
Fill a form field by its ref. Replaces existing content.
- `ref` (string): Element ref of the input field.
- `text` (string): Text to fill.

### browser-press(key, ref?)
Press a keyboard key, optionally on a specific element.
- `key` (string): e.g. `Enter`, `Tab`, `Escape`, `ArrowDown`.
- `ref` (string, optional): Element to focus first.

### browser-scroll(direction?)
Scroll the page.
- `direction` (string): `down` (default) or `up`.

### browser-screenshot()
Take a visual screenshot. More expensive in tokens than snapshot — prefer snapshot for text.

### browser-eval(code)
Execute JavaScript in the page context.
- `code` (string): JavaScript expression. Result returned as JSON.

### browser-tabs()
List all open tabs with title and URL.

## Workflow

**Read a page:**
1. `browser-navigate(url="https://example.com")` — browser starts automatically
2. `browser-snapshot(filter="full")` — read content

**Fill a form:**
1. `browser-navigate(url="https://example.com/form")`
2. `browser-snapshot()` — find refs of the inputs
3. `browser-type(ref="e3", text="user@example.com")`
4. `browser-type(ref="e4", text="secret")`
5. `browser-click(ref="e5")`
6. `browser-snapshot()` — verify result

## Common Errors

- **"No page open"**: Call `browser-navigate` first.
- **Ref not found**: The page changed. Re-run `browser-snapshot()` to get fresh refs.
- **"Headed mode is not available"**: No display on this system (e.g. Docker). Use headless (default).
- **Page didn't load**: Use `browser-snapshot()` or `browser-screenshot()` to inspect the current state.
```

- [ ] **Step 3: Replace `Lis.Tools/Help/web.md` entirely with:**

```markdown
# Web

Search the web.

## Tools

### web-search(query)
Search the web via Brave Search.
- `query` (string): Search query text.
- Returns: list of results with title, URL, and text snippet.

## Workflow

**Research a topic:**
1. `web-search(query="semantic kernel plugins tutorial")` — find relevant pages
2. `browser-navigate(url="https://example.com/article")` + `browser-snapshot(filter="full")` — read a specific result

## Common Errors

- **No search results**: Try rephrasing the query or using different keywords.
- **"Web search is not configured"**: Requires `LIS_WEB_SEARCH_ENABLED=true` and `LIS_WEB_SEARCH_API_KEY`.
```

- [ ] **Step 4: Update `docs/TOOLS.md`**

Replace the `### WebPlugin (`web`)` and `### BrowserPlugin (`browser`)` sections (lines 98-121) with:

```markdown
### WebPlugin (`web`)

| Function | Description | Auth |
|----------|-------------|------|
| `search` | Web search via Brave Search API (1-10 results) | Open |

Requires `LIS_WEB_SEARCH_ENABLED=true` and `LIS_WEB_SEARCH_API_KEY`. To read page content, use the browser tools.

### BrowserPlugin (`browser`)

| Function | Description | Auth |
|----------|-------------|------|
| `navigate` | Open a URL; browser starts automatically on first use (`headed` opt-in) | OwnerOnly |
| `snapshot` | Accessibility tree with element refs (`e0`, `e1`, ...); `interactive` or `full` filter | OwnerOnly |
| `click` | Click element by ref | OwnerOnly |
| `type` | Fill form field by ref | OwnerOnly |
| `press` | Press a keyboard key, optionally on an element | OwnerOnly |
| `scroll` | Scroll the page up or down | OwnerOnly |
| `screenshot` | PNG screenshot as base64 data URI | OwnerOnly |
| `eval` | Execute JavaScript, return JSON result | OwnerOnly |
| `tabs` | List open tabs | OwnerOnly |

Powered by the PinchTab sidecar (`PINCHTAB_ENABLED=true`). Each agent has its own browser profile (`agent-{agentId}`); instances start lazily and close at conversation turn end. See [BROWSER](BROWSER.md) for setup.
```

- [ ] **Step 5: Replace `docs/BROWSER.md` entirely with:**

```markdown
# Browser (PinchTab)

Lis drives Chrome through [PinchTab](https://pinchtab.com) — a single Go binary managed entirely by Lis as a sidecar process.

## How it works

- On startup (when `PINCHTAB_ENABLED=true`), Lis downloads the pinned pinchtab binary to `{PINCHTAB_DATA_DIR}/bin` (skipped if already present at the right version), writes `config.json`, and spawns `pinchtab server`.
- The sidecar is supervised: restarted on crash (exponential backoff) and restarted daily at `PINCHTAB_RESTART_HOUR`.
- Each agent gets a persistent browser profile named `agent-{agentId}`. A browser instance starts lazily on the agent's first `browser-navigate` and stops when the conversation turn ends. Profile state (cookies, logins) persists on disk.
- Headless by default. `browser-navigate(headed=true)` opens a visible window when a display is available; in Docker it returns a graceful "not available" message.

## Configuration

| Env var | Default | Description |
|---------|---------|-------------|
| `PINCHTAB_ENABLED` | `false` | Enable the browser plugin + sidecar |
| `PINCHTAB_PORT` | `9867` | Orchestrator HTTP port |
| `PINCHTAB_VERSION` | `v0.14.0` | Pinned release version |
| `PINCHTAB_DATA_DIR` | `./data/pinchtab` | Binary, config, profiles, and state |
| `PINCHTAB_CHROME_PATH` | (auto-detect) | Chrome/Chromium binary path |
| `PINCHTAB_RESTART_HOUR` | `4` | Local hour for the daily scheduled restart |
| `PINCHTAB_TOKEN` | (generated) | API auth token; random per boot when empty |

## Bare metal

Chrome or Chromium must be installed. PinchTab auto-detects common locations; set `PINCHTAB_CHROME_PATH` to override. The pinchtab binary is downloaded automatically on first start.

## Docker

The image bakes in both Chromium (`apt`) and the pinchtab binary (release download), so no runtime downloads happen:

- `PINCHTAB_DATA_DIR=/opt/pinchtab`
- `PINCHTAB_CHROME_PATH=/usr/bin/chromium`

Headed mode is unavailable in Docker (no display).

## Troubleshooting

- **Browser tools missing**: `PINCHTAB_ENABLED` is not `true`.
- **"Headed mode is not available"**: no `DISPLAY`/`WAYLAND_DISPLAY` (Linux). Use headless.
- **Sidecar won't start**: check logs for `pinchtab` lines; verify the binary is executable and Chrome is found.
- **Reset all browser state**: stop Lis and delete `{PINCHTAB_DATA_DIR}/profiles`.
```

- [ ] **Step 6: Verify build + tests**

Run: `dotnet build && dotnet test Lis.Tests/Lis.Tests.csproj`
Expected: Build succeeded; all tests pass.

- [ ] **Step 7: Commit**

```bash
git add Lis.Tools/WebPlugin.cs Lis.Tools/Help/browser.md Lis.Tools/Help/web.md docs/TOOLS.md docs/BROWSER.md
git commit -m "🔥 feat(web): drop web-fetch in favor of pinchtab browser, update docs"
```

---

### Task 9: Dockerfile + .env.example

**Files:**
- Modify: `Lis.Api/Dockerfile`
- Modify: `.env.example`

- [ ] **Step 1: Update `Lis.Api/Dockerfile`**

Remove the Playwright download from the build stage (line 29-30):

```dockerfile
# Download Playwright's bundled Chromium (SDK has pwsh)
RUN pwsh /app/playwright.ps1 install chromium
```

Replace the runtime apt block (lines 41-50, the Chromium shared-library list) with a `chromium` package install:

```dockerfile
# Globalization, timezones, and Chromium for pinchtab browser automation
ENV DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=false \
    LC_ALL=pt_BR.UTF-8 \
    LANG=pt_BR.UTF-8
RUN apt-get update && apt-get install -y --no-install-recommends \
    locales tzdata bash chromium fonts-liberation \
    && sed -i '/pt_BR.UTF-8/s/^# //g' /etc/locale.gen \
    && locale-gen \
    && rm -rf /var/lib/apt/lists/*
```

Remove the Playwright cache copy (lines 52-53):

```dockerfile
# Copy Playwright browsers from build stage
COPY --from=build /root/.cache/ms-playwright /root/.cache/ms-playwright
```

Add the pinchtab binary bake-in before the final `COPY --from=build /app .` (requires `ARG TARGETARCH` after the runtime `FROM`):

```dockerfile
# PinchTab binary (pinned) — skips runtime download
ARG TARGETARCH
ADD --chmod=755 https://github.com/pinchtab/pinchtab/releases/download/v0.14.0/pinchtab-linux-${TARGETARCH} /opt/pinchtab/bin/pinchtab
RUN printf 'v0.14.0' > /opt/pinchtab/bin/VERSION
ENV PINCHTAB_DATA_DIR=/opt/pinchtab \
    PINCHTAB_CHROME_PATH=/usr/bin/chromium
```

Note: docker `TARGETARCH` values are `amd64`/`arm64`, which match pinchtab's asset names exactly.

- [ ] **Step 2: Add pinchtab block to `.env.example`**

After the `# Lis` block (after line 67 `LIS_MAX_TOOL_ITERATIONS=10`), insert:

```bash
# Browser (PinchTab sidecar) — optional
PINCHTAB_ENABLED=false
PINCHTAB_PORT=9867
PINCHTAB_VERSION=v0.14.0
PINCHTAB_DATA_DIR=./data/pinchtab
PINCHTAB_CHROME_PATH=                       # empty = auto-detect Chrome/Chromium
PINCHTAB_RESTART_HOUR=4                     # daily scheduled sidecar restart (local hour)
PINCHTAB_TOKEN=                             # empty = random token per boot
```

- [ ] **Step 3: Verify Dockerfile builds (optional but recommended if docker available)**

Run: `docker build -f Lis.Api/Dockerfile -t lis-test . 2>&1 | tail -5`
Expected: `naming to docker.io/library/lis-test` (success). If docker is unavailable, skip.

- [ ] **Step 4: Commit**

```bash
git add Lis.Api/Dockerfile .env.example
git commit -m "🐳 chore(docker): bake in chromium + pinchtab, drop Playwright"
```

---

### Task 10: Final verification, cleanup, smoke test

- [ ] **Step 1: Full build and test suite**

Run: `dotnet build && dotnet test Lis.Tests/Lis.Tests.csproj`
Expected: Build succeeded, all tests pass.

- [ ] **Step 2: Grep for leftovers**

Run: `grep -ri "playwright" --include="*.cs" --include="*.csproj" --include="*.md" --include="Dockerfile" . | grep -v docs/superpowers | grep -v bin/ | grep -v obj/`
Expected: no matches outside spec/plan docs. Also check `grep -rn "web-fetch\|BrowserSessionManager" --include="*.cs" --include="*.md" . | grep -v docs/superpowers | grep -v bin/ | grep -v obj/` — no matches.

- [ ] **Step 3: ReSharper cleanup**

Run (from repo root): `jb cleanupcode Lis.Api/Lis.Api.csproj --profile="Built-in: Full Cleanup" --settings=Lis.sln.DotSettings`
Then: `git diff --stat` — verify only intended files changed; revert collateral damage if any, re-run build + tests.

- [ ] **Step 4: Manual smoke test (requires network + Chrome installed)**

```bash
# In .env: set PINCHTAB_ENABLED=true, then:
cd Lis.Api && dotnet run
```

Expected log lines: "Downloading pinchtab from ..." (first run), "Pinchtab server started (pid ...)", "Pinchtab server healthy at http://localhost:9867".
Then via chat, ask the agent to browse a page (e.g. "open example.com and tell me what it says") and verify navigate + snapshot work, and that the instance closes at turn end (log: "Closed pinchtab instance ... for agent ...").

- [ ] **Step 5: Final commit**

```bash
git add -u
git commit -m "🎨 style: cleanup after pinchtab browser integration"
```

(Skip if cleanupcode produced no changes.)
