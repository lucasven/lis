namespace Lis.Tools.Pinchtab;

/// <summary>Detects whether a graphical display is available for headed browser mode.</summary>
public static class DisplayProbe {
	public static bool IsAvailable() {
		if (!OperatingSystem.IsLinux()) return true;

		return !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("DISPLAY"))
			|| !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("WAYLAND_DISPLAY"));
	}
}
