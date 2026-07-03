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

	public void Dispose() {
		Directory.Delete(this.tempDir, recursive: true);
		GC.SuppressFinalize(this);
	}

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
