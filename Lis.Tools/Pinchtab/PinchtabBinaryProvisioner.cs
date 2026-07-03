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
