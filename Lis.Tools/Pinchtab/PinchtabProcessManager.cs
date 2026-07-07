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

	private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

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

		await this.EnsureConfigFileAsync();

		TimeSpan backoff = TimeSpan.FromSeconds(2);
		DateTimeOffset lastStart;

		while (!stoppingToken.IsCancellationRequested) {
			lastStart = DateTimeOffset.UtcNow;

			try {
				Task exited = this.StartProcess(stoppingToken);
				await this.WaitHealthyAsync(stoppingToken);
				LogServerReady(logger, options.BaseUrl);

				Task restartDelay = Task.Delay(this.DelayUntilRestartHour(), stoppingToken);
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
		// Chrome inherits the sidecar's environment; without DISPLAY here, headed windows can't reach the screen
		string? display    = Environment.GetEnvironmentVariable("DISPLAY");
		string? wayland    = Environment.GetEnvironmentVariable("WAYLAND_DISPLAY");
		string? xauthority = Environment.GetEnvironmentVariable("XAUTHORITY");
		LogServerDisplayEnv(logger, display, wayland, xauthority);
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
		if (proc is null) return;
		if (proc.HasExited) { proc.Dispose(); return; }

		// Graceful shutdown first, then kill the process tree
		try {
			using CancellationTokenSource cts = new(TimeSpan.FromSeconds(5));
			await client.ShutdownAsync(cts.Token);
			await proc.WaitForExitAsync(cts.Token);
		} catch (Exception) {
			try { proc.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
		}

		proc.Dispose();
	}

	private async Task EnsureConfigFileAsync() {
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

		await File.WriteAllTextAsync(options.ConfigPath, JsonSerializer.Serialize(config, JsonOptions));
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

	[LoggerMessage(Level = LogLevel.Information, Message = "Pinchtab sidecar environment: DISPLAY={Display}, WAYLAND_DISPLAY={WaylandDisplay}, XAUTHORITY={Xauthority}")]
	private static partial void LogServerDisplayEnv(ILogger logger, string? display, string? waylandDisplay, string? xauthority);

	[LoggerMessage(Level = LogLevel.Information, Message = "[pinchtab] {Line}")]
	private static partial void LogServerStdout(ILogger logger, string line);

	[LoggerMessage(Level = LogLevel.Information, Message = "[pinchtab!] {Line}")]
	private static partial void LogServerStderr(ILogger logger, string line);
}
