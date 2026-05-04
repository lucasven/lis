using System.Buffers;
using System.Net.WebSockets;
using System.Text.Json;
using System.Text.Json.Nodes;

using Lis.Core.Util;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Lis.Channels.Mattermost;

public sealed class MattermostWebSocketConnection(
	IOptions<MattermostOptions>               options,
	ILogger<MattermostWebSocketConnection>    logger) : IAsyncDisposable {

	private readonly SemaphoreSlim _writeLock = new(1, 1);

	private ClientWebSocket? _socket;
	private int              _seq;

	public bool IsConnected => this._socket?.State == WebSocketState.Open;

	[Trace("MattermostWebSocketConnection > ConnectAsync")]
	public async Task ConnectAsync(CancellationToken ct) {
		await this.DisconnectAsync();

		this._seq    = 0;
		this._socket = new ClientWebSocket();

		Uri wsUri = BuildWebSocketUri(options.Value.BaseUrl);

		if (logger.IsEnabled(LogLevel.Information))
			logger.LogInformation("Connecting to Mattermost WebSocket at {Uri}", wsUri);

		await this._socket.ConnectAsync(wsUri, ct);
		await this.AuthenticateAsync(ct);
	}

	[Trace("MattermostWebSocketConnection > ReceiveAsync")]
	public async Task<JsonDocument?> ReceiveAsync(CancellationToken ct) {
		if (this._socket is null || this._socket.State != WebSocketState.Open)
			return null;

		ArrayBufferWriter<byte> buffer = new(4096);

		ValueWebSocketReceiveResult result;
		do {
			Memory<byte> memory = buffer.GetMemory(4096);
			result = await this._socket.ReceiveAsync(memory, ct);

			if (result.MessageType == WebSocketMessageType.Close)
				return null;

			buffer.Advance(result.Count);
		} while (!result.EndOfMessage);

		return JsonDocument.Parse(buffer.WrittenMemory);
	}

	[Trace("MattermostWebSocketConnection > SendActionAsync")]
	public async Task SendActionAsync(string action, JsonObject data, CancellationToken ct) {
		if (this._socket is null || this._socket.State != WebSocketState.Open)
			return;

		int seq = this.NextSeq();

		JsonObject payload = new() {
			["action"] = action,
			["seq"]    = seq,
			["data"]   = data
		};

		byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(payload);

		await this._writeLock.WaitAsync(ct);
		try {
			await this._socket.SendAsync(bytes, WebSocketMessageType.Text, true, ct);
		} finally {
			this._writeLock.Release();
		}
	}

	public async Task DisconnectAsync() {
		if (this._socket is null) return;

		try {
			if (this._socket.State == WebSocketState.Open)
				await this._socket.CloseAsync(WebSocketCloseStatus.NormalClosure, null, CancellationToken.None);
		} catch {
			// best-effort close
		} finally {
			this._socket.Dispose();
			this._socket = null;
		}
	}

	public async ValueTask DisposeAsync() {
		await this.DisconnectAsync();
		this._writeLock.Dispose();
	}

	private int NextSeq() => Interlocked.Increment(ref this._seq);

	private async Task AuthenticateAsync(CancellationToken ct) {
		JsonObject challenge = new() {
			["seq"]    = this.NextSeq(),
			["action"] = "authentication_challenge",
			["data"]   = new JsonObject { ["token"] = options.Value.BotToken }
		};

		byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(challenge);

		await this._writeLock.WaitAsync(ct);
		try {
			await this._socket!.SendAsync(bytes, WebSocketMessageType.Text, true, ct);
		} finally {
			this._writeLock.Release();
		}

		using JsonDocument? response = await this.ReceiveAsync(ct);
		string? status = response?.RootElement.TryGetProperty("status", out JsonElement s) == true
			? s.GetString()
			: null;

		if (status != "OK")
			throw new InvalidOperationException($"WebSocket authentication failed: {response?.RootElement}");

		logger.LogInformation("WebSocket authenticated successfully");
	}

	private static Uri BuildWebSocketUri(string baseUrl) {
		UriBuilder ub = new(baseUrl) { Path = "/api/v4/websocket" };
		ub.Scheme = ub.Scheme == "https" ? "wss" : "ws";
		return ub.Uri;
	}
}
