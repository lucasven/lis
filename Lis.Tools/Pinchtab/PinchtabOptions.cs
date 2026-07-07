namespace Lis.Tools.Pinchtab;

/// <summary>Configuration for the PinchTab sidecar, read from flat env vars.</summary>
public sealed class PinchtabOptions {
	public bool    Enabled                { get; init; }
	public int     Port                  { get; init; } = 9867;
	public string  Version               { get; init; } = "v0.14.0";
	public string  DataDir               { get; init; } = "./data/pinchtab";
	public string? ChromePath            { get; init; }
	public int     RestartHour           { get; init; } = 4;
	public int     HandoffTimeoutMinutes { get; init; } = 15;

	public string BaseUrl         => $"http://localhost:{this.Port}";
	public string BinDir          => Path.Combine(this.DataDir, "bin");
	public string BinaryPath      => Path.Combine(this.BinDir, OperatingSystem.IsWindows() ? "pinchtab.exe" : "pinchtab");
	public string VersionFilePath => Path.Combine(this.BinDir, "VERSION");
	public string ConfigPath      => Path.Combine(this.DataDir, "config.json");

	public static PinchtabOptions FromEnvironment() {
		return new PinchtabOptions {
			Enabled                = string.Equals(Env("PINCHTAB_ENABLED"), "true", StringComparison.OrdinalIgnoreCase),
			Port                   = int.TryParse(Env("PINCHTAB_PORT"), out int port) ? port : 9867,
			Version                = Env("PINCHTAB_VERSION") ?? "v0.14.0",
			DataDir                = Env("PINCHTAB_DATA_DIR") ?? "./data/pinchtab",
			ChromePath             = Env("PINCHTAB_CHROME_PATH"),
			RestartHour            = int.TryParse(Env("PINCHTAB_RESTART_HOUR"), out int hour) ? hour : 4,
			HandoffTimeoutMinutes  = int.TryParse(Env("PINCHTAB_HANDOFF_TIMEOUT_MINUTES"), out int handoff) ? handoff : 15,
		};
	}

	private static string? Env(string key) {
		string? value = Environment.GetEnvironmentVariable(key);
		return string.IsNullOrWhiteSpace(value) ? null : value;
	}
}
