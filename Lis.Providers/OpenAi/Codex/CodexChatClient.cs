using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

using Lis.Core.Channel;

using Microsoft.Extensions.AI;

namespace Lis.Providers.OpenAi.Codex;

public sealed class CodexChatClient : IChatClient, ISessionAware {
	private readonly CodexTokenManager       _tokenManager;
	private readonly CodexOptions            _options;
	private readonly CodexTransportSelector  _transportSelector;

	public string? SessionId { get; set; }

	public ChatClientMetadata Metadata { get; }

	public CodexChatClient(
		CodexTokenManager tokenManager,
		CodexOptions options,
		HttpClient httpClient,
		CodexWebSocketTransport? webSocketTransport = null) {
		this._tokenManager = tokenManager;
		this._options      = options;

		webSocketTransport ??= new CodexWebSocketTransport(tokenManager, options);
		this._transportSelector = new CodexTransportSelector(options, httpClient, webSocketTransport);

		this.Metadata = new ChatClientMetadata("codex", null, options.Model);
	}

	public async Task<ChatResponse> GetResponseAsync(
		IEnumerable<ChatMessage> chatMessages,
		ChatOptions? options = null,
		CancellationToken cancellationToken = default) {

		StringBuilder text = new();
		List<AIContent> contents = [];
		Dictionary<string, object?>? usageMetadata = null;

		await foreach (ChatResponseUpdate update in
			this.GetStreamingResponseAsync(chatMessages, options, cancellationToken)) {
			if (update.Text is not null)
				text.Append(update.Text);

			if (update.Contents is not null) {
				foreach (AIContent content in update.Contents) {
					if (content is UsageContent uc) {
						usageMetadata = new Dictionary<string, object?>();
						if (uc.Details?.InputTokenCount is { } inp)
							usageMetadata["codex.input_tokens"] = (int)inp;
						if (uc.Details?.OutputTokenCount is { } outp)
							usageMetadata["codex.output_tokens"] = (int)outp;
						if (uc.Details?.AdditionalCounts?.TryGetValue("CachedTokens", out long cached) == true)
							usageMetadata["codex.cached_tokens"] = (int)cached;
					} else {
						contents.Add(content);
					}
				}
			}
		}

		string? textContent = text.Length > 0 ? text.ToString() : null;
		ChatMessage responseMessage = new(ChatRole.Assistant, textContent);
		foreach (AIContent content in contents)
			responseMessage.Contents.Add(content);

		return new ChatResponse(responseMessage) {
			ModelId = options?.ModelId ?? this._options.Model
		};
	}

	public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
		IEnumerable<ChatMessage> chatMessages,
		ChatOptions? options = null,
		[EnumeratorCancellation] CancellationToken cancellationToken = default) {

		IList<ChatMessage> messageList = chatMessages as IList<ChatMessage> ?? chatMessages.ToList();
		CodexRequest request = BuildRequest(messageList, options, this.SessionId, this._options);
		CodexTokenInfo token = await this._tokenManager.GetValidTokenAsync(cancellationToken);
		Dictionary<string, string> headers = BuildBaseHeaders(token);

		if (this.SessionId is not null) {
			headers["session_id"]            = this.SessionId;
			headers["x-client-request-id"]   = this.SessionId;
		}

		// SSE Event Stream Processing state machine (SM2)
		string? currentItemType = null;
		StringBuilder argsAccumulator = new();
		string? functionCallId   = null;
		string? functionCallName = null;

		await foreach (JsonElement evt in this._transportSelector.StreamAsync(
			request, this.SessionId, headers, cancellationToken)) {

			string? type = evt.TryGetProperty("type", out JsonElement typeProp) ? typeProp.GetString() : null;
			if (type is null) continue;

			switch (type) {
				case "response.output_item.added": {
					JsonElement item = evt.GetProperty("item");
					string? itemType = item.TryGetProperty("type", out JsonElement it) ? it.GetString() : null;
					currentItemType = itemType;

					if (itemType == "function_call") {
						functionCallId   = item.TryGetProperty("call_id", out JsonElement cid) ? cid.GetString() : null;
						functionCallName = item.TryGetProperty("name", out JsonElement nm) ? nm.GetString() : null;
						argsAccumulator.Clear();
					}

					break;
				}

				case "response.output_text.delta": {
					if (currentItemType != "message") break;
					string? delta = evt.TryGetProperty("delta", out JsonElement d) ? d.GetString() : null;
					if (delta is not null)
						yield return new ChatResponseUpdate {
							Role     = ChatRole.Assistant,
							Contents = [new TextContent(delta)]
						};
					break;
				}

				case "response.function_call_arguments.delta": {
					if (currentItemType != "function_call") break;
					string? delta = evt.TryGetProperty("delta", out JsonElement d) ? d.GetString() : null;
					if (delta is not null)
						argsAccumulator.Append(delta);
					break;
				}

				case "response.output_item.done": {
					// INV-6: tool call arguments only exposed as complete JSON
					if (currentItemType == "function_call" && functionCallId is not null && functionCallName is not null) {
						FunctionCallContent fc = CodexMessageConverter.ParseFunctionCall(
							functionCallId, functionCallName, argsAccumulator.ToString());
						yield return new ChatResponseUpdate {
							Role     = ChatRole.Assistant,
							Contents = [fc]
						};
					}

					currentItemType  = null;
					functionCallId   = null;
					functionCallName = null;
					argsAccumulator.Clear();
					break;
				}

				case "response.completed": {
					// Extract usage from response.completed
					if (evt.TryGetProperty("response", out JsonElement response)
					    && response.TryGetProperty("usage", out JsonElement usage)) {
						UsageDetails details = ExtractUsageDetails(usage);
						yield return new ChatResponseUpdate {
							Contents = [new UsageContent(details)]
						};
					}

					break;
				}

				case "response.failed": {
					string errorMsg = "Codex response failed";
					string? errorCode = null;
					if (evt.TryGetProperty("response", out JsonElement resp)
					    && resp.TryGetProperty("error", out JsonElement err)) {
						if (err.TryGetProperty("message", out JsonElement msg))
							errorMsg = msg.GetString() ?? errorMsg;
						if (err.TryGetProperty("code", out JsonElement codeProp))
							errorCode = codeProp.GetString();
					}

					throw BuildStreamException(errorCode, errorMsg, evt);
				}

				case "error": {
					string errorMsg = "Codex stream error";
					string? errorCode = null;
					if (evt.TryGetProperty("message", out JsonElement msg))
						errorMsg = msg.GetString() ?? errorMsg;
					if (evt.TryGetProperty("code", out JsonElement codeProp))
						errorCode = codeProp.GetString();

					throw BuildStreamException(errorCode, errorMsg, evt);
				}

				// reasoning events — not surfaced to caller
				case "response.reasoning_summary_text.delta":
				case "response.reasoning_summary_part.added":
				case "response.reasoning_summary_part.done":
					break;
			}
		}
	}

	public static CodexRequest BuildRequest(
		IList<ChatMessage> messages,
		ChatOptions? options,
		string? sessionId,
		CodexOptions codexOptions) {

		(string? instructions, JsonArray input) = CodexMessageConverter.ConvertToResponsesApi(messages);

		List<CodexTool>? tools = null;
		if (options?.Tools is { Count: > 0 }) {
			tools = [];
			foreach (AITool tool in options.Tools) {
				if (tool is AIFunction func) {
					JsonElement? parameters = func.JsonSchema is { } schema
						? JsonDocument.Parse(JsonSerializer.Serialize(schema)).RootElement.Clone()
						: null;
					tools.Add(new CodexTool {
						Name        = func.Name,
						Description = func.Description,
						Parameters  = parameters
					});
				}
			}
		}

		CodexReasoningOptions? reasoning = codexOptions.ReasoningEffort is not null
			? new CodexReasoningOptions { Effort = codexOptions.ReasoningEffort }
			: null;

		return new CodexRequest {
			Model              = options?.ModelId ?? codexOptions.Model,
			Instructions       = instructions ?? "",
			Input              = input,
			Tools              = tools,
			Reasoning          = reasoning,
			Text               = new CodexTextOptions(),
			PromptCacheKey     = sessionId,
			PreviousResponseId = null,
			Include            = ["reasoning.encrypted_content"]
		};
	}

	private static Dictionary<string, string> BuildBaseHeaders(CodexTokenInfo token) => new() {
		["Authorization"]       = $"Bearer {token.AccessToken}",
		["chatgpt-account-id"]  = token.AccountId,
		["originator"]          = "lis",
		["User-Agent"]          = $"lis ({Environment.OSVersion.Platform})"
	};

	private static Exception BuildStreamException(string? code, string message, JsonElement rawEvent) {
		string raw = rawEvent.GetRawText();
		string fullMsg = raw.Length > 2 && raw != "{}" ? $"{message} | raw: {raw}" : message;

		if (IsAuthError(code, message))
			return new CodexAuthException(fullMsg);
		return new InvalidOperationException(fullMsg);
	}

	private static bool IsAuthError(string? code, string message) =>
		code is "authentication_error" or "invalid_api_key" or "token_expired"
		|| message.Contains("auth", StringComparison.OrdinalIgnoreCase)
		|| message.Contains("unauthorized", StringComparison.OrdinalIgnoreCase)
		|| message.Contains("token", StringComparison.OrdinalIgnoreCase)
		   && message.Contains("expired", StringComparison.OrdinalIgnoreCase);

	private static UsageDetails ExtractUsageDetails(JsonElement usage) {
		int inputTokens  = usage.TryGetProperty("input_tokens", out JsonElement inp) ? inp.GetInt32() : 0;
		int outputTokens = usage.TryGetProperty("output_tokens", out JsonElement outp) ? outp.GetInt32() : 0;
		int cachedTokens = 0;

		if (usage.TryGetProperty("input_tokens_details", out JsonElement tokenDetails)
		    && tokenDetails.TryGetProperty("cached_tokens", out JsonElement cachedProp))
			cachedTokens = cachedProp.GetInt32();

		UsageDetails result = new() {
			InputTokenCount  = inputTokens,
			OutputTokenCount = outputTokens
		};

		if (cachedTokens > 0)
			result.AdditionalCounts = new AdditionalPropertiesDictionary<long> { ["CachedTokens"] = cachedTokens };

		return result;
	}

	public object? GetService(Type serviceType, object? serviceKey = null) {
		if (serviceType == typeof(ChatClientMetadata))
			return this.Metadata;
		return null;
	}

	public void Dispose() { }
}
