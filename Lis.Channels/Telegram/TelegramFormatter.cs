using System.Text;
using System.Text.RegularExpressions;

using Lis.Core.Channel;

namespace Lis.Channels.Telegram;

/// <summary>
/// Converts standard markdown to Telegram MarkdownV2 format.
/// Code blocks are preserved as-is — only non-code segments are escaped/formatted.
/// </summary>
public sealed partial class TelegramFormatter : IMessageFormatter {

	// Telegram MarkdownV2 special characters that need escaping (outside formatting markers)
	private static readonly char[] SpecialChars = ['_', '[', ']', '(', ')', '~', '>', '#', '+', '-', '=', '|', '{', '}', '.', '!'];

	[GeneratedRegex(@"```[\s\S]*?```|`[^`\n]+`", RegexOptions.None)]
	private static partial Regex CodeBlockRegex();

	[GeneratedRegex(@"\*\*(.+?)\*\*", RegexOptions.Singleline)]
	private static partial Regex BoldRegex();

	[GeneratedRegex(@"__(.+?)__", RegexOptions.Singleline)]
	private static partial Regex UnderlineRegex();

	[GeneratedRegex(@"~~(.+?)~~", RegexOptions.Singleline)]
	private static partial Regex StrikethroughRegex();

	public string Format(string content) {
		if (string.IsNullOrWhiteSpace(content)) return string.Empty;

		// Split into code and non-code segments, only format non-code parts
		StringBuilder   sb      = new();
		MatchCollection matches = CodeBlockRegex().Matches(content);
		int             cursor  = 0;

		foreach (Match match in matches) {
			if (match.Index > cursor)
				sb.Append(FormatSegment(content[cursor..match.Index]));

			sb.Append(match.Value);
			cursor = match.Index + match.Length;
		}

		if (cursor < content.Length)
			sb.Append(FormatSegment(content[cursor..]));

		return sb.ToString().TrimEnd();
	}

	private static string FormatSegment(string text) {
		// Convert markdown formatting to Telegram MarkdownV2 equivalents
		text = BoldRegex().Replace(text, "*$1*");
		text = UnderlineRegex().Replace(text, "__$1__");
		text = StrikethroughRegex().Replace(text, "~$1~");

		// Escape special characters (but not the formatting markers we just inserted)
		text = EscapeSpecialChars(text);

		return text;
	}

	private static string EscapeSpecialChars(string text) {
		StringBuilder sb = new(text.Length);

		for (int i = 0; i < text.Length; i++) {
			char c = text[i];

			// Don't escape formatting markers (* ~ __)
			if (c == '*' || c == '~') {
				sb.Append(c);
				continue;
			}

			if (Array.IndexOf(SpecialChars, c) >= 0)
				sb.Append('\\').Append(c);
			else
				sb.Append(c);
		}

		return sb.ToString();
	}
}
