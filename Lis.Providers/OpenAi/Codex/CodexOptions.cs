namespace Lis.Providers.OpenAi.Codex;

public enum CodexTransport { Auto, Sse, WebSocket }

public sealed class CodexOptions {
	public required string AccessToken      { get; set; }
	public required string RefreshToken     { get; set; }
	public string          Model            { get; init; } = "codex-1";
	public int             MaxTokens        { get; init; } = 16384;
	public int             ContextBudget    { get; init; } = 100000;
	public string?         ReasoningEffort  { get; init; }
	public string          BaseUrl          { get; init; } = "https://chatgpt.com/backend-api";
	public int             ExpiryBufferSeconds { get; init; } = 300;
	public CodexTransport  Transport        { get; init; } = CodexTransport.Auto;
}
