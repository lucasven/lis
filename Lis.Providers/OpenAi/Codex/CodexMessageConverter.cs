using System.Text.Json;
using System.Text.Json.Nodes;

using Microsoft.Extensions.AI;

namespace Lis.Providers.OpenAi.Codex;

public static class CodexMessageConverter {
	public static (string? Instructions, JsonArray Input) ConvertToResponsesApi(IList<ChatMessage> messages) {
		string? instructions = null;
		JsonArray input = [];

		foreach (ChatMessage msg in messages) {
			if (msg.Role == ChatRole.System) {
				// INV-8: system messages → instructions field, never in input array. Last wins.
				string? text = msg.Text;
				if (text is not null)
					instructions = text;
				continue;
			}

			if (msg.Role == ChatRole.User) {
				JsonArray contentParts = [];
				foreach (AIContent item in msg.Contents) {
					switch (item) {
						case TextContent tc:
							contentParts.Add(new JsonObject {
								["type"] = "input_text",
								["text"] = tc.Text
							});
							break;
						case DataContent dc when dc.Data.Length > 0:
							string mime = dc.MediaType ?? "image/png";
							string b64 = Convert.ToBase64String(dc.Data.ToArray());
							contentParts.Add(new JsonObject {
								["type"]      = "input_image",
								["image_url"] = $"data:{mime};base64,{b64}",
								["detail"]    = "auto"
							});
							break;
						case DataContent dc when dc.Uri is not null:
							contentParts.Add(new JsonObject {
								["type"]      = "input_image",
								["image_url"] = dc.Uri.ToString(),
								["detail"]    = "auto"
							});
							break;
					}
				}

				if (contentParts.Count == 0 && msg.Text is { Length: > 0 } userText) {
					contentParts.Add(new JsonObject {
						["type"] = "input_text",
						["text"] = userText
					});
				}

				if (contentParts.Count > 0) {
					input.Add(new JsonObject {
						["role"]    = "user",
						["content"] = contentParts
					});
				}

				continue;
			}

			if (msg.Role == ChatRole.Assistant) {
				foreach (AIContent item in msg.Contents) {
					switch (item) {
						case FunctionCallContent fc:
							input.Add(new JsonObject {
								["type"]      = "function_call",
								["call_id"]   = fc.CallId,
								["name"]      = fc.Name,
								["arguments"] = fc.Arguments is not null
									? JsonSerializer.Serialize(fc.Arguments)
									: "{}"
							});
							break;
						case TextContent tc when tc.Text is { Length: > 0 }:
							input.Add(new JsonObject {
								["type"]    = "message",
								["role"]    = "assistant",
								["content"] = new JsonArray(new JsonObject {
									["type"] = "output_text",
									["text"] = tc.Text
								}),
								["status"] = "completed"
							});
							break;
					}
				}

				// Fallback: if no content items but has text
				if (msg.Contents.Count == 0 && msg.Text is { Length: > 0 } assistantText) {
					input.Add(new JsonObject {
						["type"]    = "message",
						["role"]    = "assistant",
						["content"] = new JsonArray(new JsonObject {
							["type"] = "output_text",
							["text"] = assistantText
						}),
						["status"] = "completed"
					});
				}

				continue;
			}

			if (msg.Role == ChatRole.Tool) {
				foreach (AIContent item in msg.Contents) {
					if (item is FunctionResultContent fr) {
						string output = fr.Result?.ToString() ?? "";
						input.Add(new JsonObject {
							["type"]    = "function_call_output",
							["call_id"] = fr.CallId,
							["output"]  = output
						});
					}
				}
			}
		}

		return (instructions, input);
	}

	public static FunctionCallContent ParseFunctionCall(string callId, string name, string argumentsJson) {
		Dictionary<string, object?>? args = null;
		try {
			args = JsonSerializer.Deserialize<Dictionary<string, object?>>(argumentsJson);
		} catch {
			// Malformed JSON — pass raw string as single argument
			args = new Dictionary<string, object?> { ["raw"] = argumentsJson };
		}

		return new FunctionCallContent(callId, name, args);
	}
}
