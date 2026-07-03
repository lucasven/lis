namespace Lis.Tools.Pinchtab;

/// <summary>
/// Shared mutable state between PinchtabProcessManager (writer)
/// and PinchtabClient (reader). The process manager sets the auth
/// token before spawning the server; the client uses it for Bearer auth.
/// </summary>
public sealed class PinchtabServerState {
	public string Token { get; set; } = "";
}
