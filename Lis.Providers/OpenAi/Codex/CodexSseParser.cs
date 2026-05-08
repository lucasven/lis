using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;

namespace Lis.Providers.OpenAi.Codex;

public static class CodexSseParser {
	public static async IAsyncEnumerable<JsonElement> ParseAsync(
		Stream sseStream,
		[EnumeratorCancellation] CancellationToken ct = default) {

		using StreamReader reader = new(sseStream, Encoding.UTF8);

		while (!ct.IsCancellationRequested) {
			string? line = await reader.ReadLineAsync(ct);
			if (line is null) yield break;

			// SSE: skip empty lines and comments
			if (line.Length == 0 || line[0] == ':') continue;

			// Only process data: lines
			if (!line.StartsWith("data: ", StringComparison.Ordinal)) continue;

			string data = line[6..];

			// [DONE] sentinel — end of stream
			if (data == "[DONE]") yield break;

			JsonElement element;
			try {
				element = JsonDocument.Parse(data).RootElement.Clone();
			} catch (JsonException) {
				continue;
			}

			yield return element;
		}
	}
}
