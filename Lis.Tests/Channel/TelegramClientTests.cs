using Lis.Channels.Telegram;

namespace Lis.Tests.Channel;

public class TelegramClientTests {

	// ── Short Messages ──────────────────────────────────────────────

	[Fact]
	public void SplitMessage_ShortText_ReturnsSingleChunk() {
		List<string> result = TelegramClient.SplitMessage("Hello world");
		Assert.Single(result);
		Assert.Equal("Hello world", result[0]);
	}

	[Fact]
	public void SplitMessage_ExactlyAtLimit_ReturnsSingleChunk() {
		string text = new('a', 4096);
		List<string> result = TelegramClient.SplitMessage(text);
		Assert.Single(result);
	}

	// ── Basic Splitting ─────────────────────────────────────────────

	[Fact]
	public void SplitMessage_ExceedsLimit_SplitsOnNewlines() {
		string line = new('a', 2000);
		string text = $"{line}\n{line}\n{line}";

		List<string> result = TelegramClient.SplitMessage(text);

		Assert.Equal(2, result.Count);
		Assert.True(result[0].Length <= 4096);
		Assert.True(result[1].Length <= 4096);
		Assert.Equal($"{line}\n{line}", result[0]);
		Assert.Equal(line, result[1]);
	}

	// ── Code Block Continuity ───────────────────────────────────────

	[Fact]
	public void SplitMessage_CodeBlock_ClosesAndReopens() {
		string codeLine = new('x', 2000);
		string text = $"```\n{codeLine}\n{codeLine}\n{codeLine}\n```";

		List<string> result = TelegramClient.SplitMessage(text);

		Assert.True(result.Count >= 2);
		Assert.EndsWith("```", result[0]);
		Assert.StartsWith("```", result[1]);
	}

	[Fact]
	public void SplitMessage_CodeBlockSplit_AllChunksHaveMatchedDelimiters() {
		string codeLine = new('x', 1500);
		string text = $"```\n{codeLine}\n{codeLine}\n{codeLine}\n```";

		List<string> result = TelegramClient.SplitMessage(text);

		foreach (string chunk in result) {
			int count = 0;
			int idx   = 0;
			while ((idx = chunk.IndexOf("```", idx, StringComparison.Ordinal)) != -1) {
				count++;
				idx += 3;
			}
			Assert.True(count % 2 == 0, $"Chunk has unmatched code block delimiters: {chunk[..Math.Min(80, chunk.Length)]}...");
		}
	}

	// ── Content Preservation ────────────────────────────────────────

	[Fact]
	public void SplitMessage_RejoiningChunks_PreservesAllContent() {
		string line = new('a', 1000);
		string text = string.Join("\n", Enumerable.Repeat(line, 10));

		List<string> result  = TelegramClient.SplitMessage(text);
		string       rejoined = string.Join("\n", result);

		Assert.Equal(text, rejoined);
	}

	[Fact]
	public void SplitMessage_MixedContent_PreservesOrder() {
		string para   = new('p', 1500);
		string code   = new('c', 1500);
		string text   = $"{para}\n```\n{code}\n```\n{para}";

		List<string> result = TelegramClient.SplitMessage(text);

		Assert.True(result.Count >= 2);
		Assert.Contains(para, result[0]);
	}
}
