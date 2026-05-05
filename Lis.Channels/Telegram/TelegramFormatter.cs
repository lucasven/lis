using System.Text;
using System.Text.RegularExpressions;

using Lis.Core.Channel;

namespace Lis.Channels.Telegram;

/// <summary>
/// Converts standard markdown (as produced by LLMs) to Telegram MarkdownV2 format.
/// Uses placeholder tokens during conversion to avoid regex passes interfering with each other.
/// </summary>
public sealed partial class TelegramFormatter : IMessageFormatter {

	private const string MustEscape = @"_[]()~`>#+-=|{}.!\";

	// Placeholder tokens (non-printable sequences that won't appear in text)
	// NOTE: must use \u0002 not \x02 — C# \x escape is greedy and consumes hex chars (A-F)
	// that follow, e.g. \x02B becomes U+002B (+) instead of STX + 'B'
	private const string PH_BOLD_OPEN    = "\u0002BO\u0002";
	private const string PH_BOLD_CLOSE   = "\u0002BC\u0002";
	private const string PH_ITALIC_OPEN  = "\u0002IO\u0002";
	private const string PH_ITALIC_CLOSE = "\u0002IC\u0002";
	private const string PH_STRIKE_OPEN  = "\u0002SO\u0002";
	private const string PH_STRIKE_CLOSE = "\u0002SC\u0002";
	private const string PH_UNDER_OPEN   = "\u0002UO\u0002";
	private const string PH_UNDER_CLOSE  = "\u0002UC\u0002";
	private const string PH_QUOTE        = "\u0002BQ\u0002";

	[GeneratedRegex(@"```([\s\S]*?)```", RegexOptions.None)]
	private static partial Regex FencedCodeRegex();

	[GeneratedRegex(@"`([^`\n]+)`", RegexOptions.None)]
	private static partial Regex InlineCodeRegex();

	[GeneratedRegex(@"\[([^\]]+)\]\(([^)]+)\)", RegexOptions.None)]
	private static partial Regex LinkRegex();

	[GeneratedRegex(@"\*\*\*(.+?)\*\*\*", RegexOptions.Singleline)]
	private static partial Regex BoldItalicRegex();

	[GeneratedRegex(@"\*\*(.+?)\*\*", RegexOptions.Singleline)]
	private static partial Regex BoldRegex();

	[GeneratedRegex(@"(?<!\*)\*(?!\*)(.+?)(?<!\*)\*(?!\*)", RegexOptions.Singleline)]
	private static partial Regex ItalicRegex();

	[GeneratedRegex(@"~~(.+?)~~", RegexOptions.Singleline)]
	private static partial Regex StrikethroughRegex();

	[GeneratedRegex(@"^---+\s*$", RegexOptions.Multiline)]
	private static partial Regex HorizontalRuleRegex();

	[GeneratedRegex(@"((?:^\|.+\|[ \t]*\n?)+)", RegexOptions.Multiline)]
	private static partial Regex TableRegex();

	[GeneratedRegex(@"^>\s?(.*)$", RegexOptions.Multiline)]
	private static partial Regex BlockquoteRegex();

	public string Format(string content) {
		if (string.IsNullOrWhiteSpace(content)) return string.Empty;

		List<(string placeholder, string replacement)> preserved = [];
		int counter = 0;

		// Phase 1: extract code blocks and inline code (preserve verbatim)
		content = FencedCodeRegex().Replace(content, m => {
			string ph    = $"\x01{counter++}\x01";
			string inner = EscapeCodeContent(m.Groups[1].Value);
			preserved.Add((ph, $"```{inner}```"));
			return ph;
		});

		content = InlineCodeRegex().Replace(content, m => {
			string ph    = $"\x01{counter++}\x01";
			string inner = EscapeCodeContent(m.Groups[1].Value);
			preserved.Add((ph, $"`{inner}`"));
			return ph;
		});

		// Phase 2: convert markdown tables to monospace code blocks
		content = TableRegex().Replace(content, m => {
			string ph    = $"\x01{counter++}\x01";
			string table = FormatTableAsMonospace(m.Groups[1].Value);
			string inner = EscapeCodeContent(table);
			preserved.Add((ph, $"```\n{inner}```"));
			return ph;
		});

		// Phase 3: extract links
		content = LinkRegex().Replace(content, m => {
			string ph   = $"\x01{counter++}\x01";
			string text = Escape(m.Groups[1].Value);
			string url  = m.Groups[2].Value.Replace(@"\", @"\\").Replace(")", @"\)");
			preserved.Add((ph, $"[{text}]({url})"));
			return ph;
		});

		// Phase 4: line-level formatting
		content = HorizontalRuleRegex().Replace(content, "");
		content = BlockquoteRegex().Replace(content, m => PH_QUOTE + m.Groups[1].Value);

		// Phase 5: inline formatting → placeholders (order: bold-italic > bold > italic > strike)
		content = BoldItalicRegex().Replace(content, m =>
			PH_BOLD_OPEN + PH_ITALIC_OPEN + m.Groups[1].Value + PH_ITALIC_CLOSE + PH_BOLD_CLOSE);
		content = BoldRegex().Replace(content, m =>
			PH_BOLD_OPEN + m.Groups[1].Value + PH_BOLD_CLOSE);
		content = ItalicRegex().Replace(content, m =>
			PH_ITALIC_OPEN + m.Groups[1].Value + PH_ITALIC_CLOSE);
		content = StrikethroughRegex().Replace(content, m =>
			PH_STRIKE_OPEN + m.Groups[1].Value + PH_STRIKE_CLOSE);

		// Phase 6: escape all remaining plain text
		content = EscapeRemaining(content);

		// Phase 7: replace placeholders with actual Telegram MarkdownV2 markers
		content = content
			.Replace(PH_BOLD_OPEN, "*").Replace(PH_BOLD_CLOSE, "*")
			.Replace(PH_ITALIC_OPEN, "_").Replace(PH_ITALIC_CLOSE, "_")
			.Replace(PH_STRIKE_OPEN, "~").Replace(PH_STRIKE_CLOSE, "~")
			.Replace(PH_UNDER_OPEN, "__").Replace(PH_UNDER_CLOSE, "__")
			.Replace(PH_QUOTE, ">");

		// Phase 8: restore preserved blocks
		foreach ((string placeholder, string replacement) in preserved)
			content = content.Replace(placeholder, replacement);

		return content.TrimEnd();
	}

	private static string Escape(string text) {
		StringBuilder sb = new(text.Length + 16);
		foreach (char c in text) {
			if (MustEscape.Contains(c))
				sb.Append('\\');
			sb.Append(c);
		}
		return sb.ToString();
	}

	private static string EscapeCodeContent(string text) {
		return text.Replace(@"\", @"\\").Replace("`", @"\`");
	}

	private static string FormatTableAsMonospace(string table) {
		string[] lines = table.Split('\n', StringSplitOptions.RemoveEmptyEntries);

		// Parse rows into cells, skip separator rows
		List<string[]> rows = [];
		foreach (string line in lines) {
			string trimmed = line.Trim();
			// Separator row: each cell contains only dashes/colons (at least 3 dashes)
			if (Regex.IsMatch(trimmed, @"^\|(\s*:?-{3,}:?\s*\|)+\s*$")) continue;
			string[] cells = trimmed.Split('|', StringSplitOptions.None)
				.Where((_, i) => i > 0) // skip empty before first |
				.ToArray();
			if (cells.Length > 0 && string.IsNullOrWhiteSpace(cells[^1]))
				cells = cells[..^1]; // skip empty after last |
			rows.Add(cells.Select(c => c.Trim()).ToArray());
		}

		if (rows.Count == 0) return table;

		// Calculate max width per column
		int cols = rows.Max(r => r.Length);
		int[] widths = new int[cols];
		foreach (string[] row in rows) {
			for (int i = 0; i < row.Length; i++) {
				if (row[i].Length > widths[i])
					widths[i] = row[i].Length;
			}
		}

		// Render aligned
		StringBuilder sb = new();
		foreach (string[] row in rows) {
			for (int i = 0; i < cols; i++) {
				string cell = i < row.Length ? row[i] : "";
				sb.Append(cell.PadRight(widths[i]));
				if (i < cols - 1) sb.Append(" | ");
			}
			sb.Append('\n');
		}

		return sb.ToString().TrimEnd('\n');
	}

	private static string EscapeRemaining(string content) {
		StringBuilder sb = new(content.Length + 64);
		for (int i = 0; i < content.Length; i++) {
			char c = content[i];

			// Placeholder markers — pass through untouched
			if (c is '\x01' or '\x02') {
				sb.Append(c);
				continue;
			}

			if (MustEscape.Contains(c))
				sb.Append('\\');
			sb.Append(c);
		}
		return sb.ToString();
	}
}
