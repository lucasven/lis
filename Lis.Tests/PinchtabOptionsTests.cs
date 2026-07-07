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
		Environment.SetEnvironmentVariable("PINCHTAB_HANDOFF_TIMEOUT_MINUTES", null);

		PinchtabOptions options = PinchtabOptions.FromEnvironment();

		Assert.False(options.Enabled);
		Assert.Equal(9867, options.Port);
		Assert.Equal("v0.14.0", options.Version);
		Assert.Equal("./data/pinchtab", options.DataDir);
		Assert.Null(options.ChromePath);
		Assert.Equal(4, options.RestartHour);
		Assert.Equal(15, options.HandoffTimeoutMinutes);
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
		Environment.SetEnvironmentVariable("PINCHTAB_HANDOFF_TIMEOUT_MINUTES", "30");

		PinchtabOptions options = PinchtabOptions.FromEnvironment();

		Assert.True(options.Enabled);
		Assert.Equal(9900, options.Port);
		Assert.Equal("v0.15.0", options.Version);
		Assert.Equal("/opt/pinchtab", options.DataDir);
		Assert.Equal("/usr/bin/chromium", options.ChromePath);
		Assert.Equal(2, options.RestartHour);
		Assert.Equal(30, options.HandoffTimeoutMinutes);
		Assert.Equal("http://localhost:9900", options.BaseUrl);

		Environment.SetEnvironmentVariable("PINCHTAB_ENABLED", null);
		Environment.SetEnvironmentVariable("PINCHTAB_PORT", null);
		Environment.SetEnvironmentVariable("PINCHTAB_VERSION", null);
		Environment.SetEnvironmentVariable("PINCHTAB_DATA_DIR", null);
		Environment.SetEnvironmentVariable("PINCHTAB_CHROME_PATH", null);
		Environment.SetEnvironmentVariable("PINCHTAB_RESTART_HOUR", null);
		Environment.SetEnvironmentVariable("PINCHTAB_HANDOFF_TIMEOUT_MINUTES", null);
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
