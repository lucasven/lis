using Lis.Channels.Telegram;

namespace Lis.Tests.Channel;

public class TelegramFormatterTests {
	private readonly TelegramFormatter _sut = new();

	// ── Guard Clauses ───────────────────────────────────────────────

	[Fact]
	public void Format_Null_ReturnsEmpty() {
		string result = this._sut.Format(null!);
		Assert.Equal(string.Empty, result);
	}

	[Fact]
	public void Format_Empty_ReturnsEmpty() {
		string result = this._sut.Format("");
		Assert.Equal(string.Empty, result);
	}

	[Fact]
	public void Format_Whitespace_ReturnsEmpty() {
		string result = this._sut.Format("   ");
		Assert.Equal(string.Empty, result);
	}

	// ── Special Character Escaping ──────────────────────────────────

	[Fact]
	public void Format_PlainText_EscapesSpecialChars() {
		string result = this._sut.Format("hello.world");
		Assert.Equal("hello\\.world", result);
	}

	[Fact]
	public void Format_MultipleDots_AllEscaped() {
		string result = this._sut.Format("a.b.c");
		Assert.Equal("a\\.b\\.c", result);
	}

	// ── Bold ────────────────────────────────────────────────────────

	[Fact]
	public void Format_Bold_ConvertsTelegramBold() {
		string result = this._sut.Format("**bold text**");
		Assert.Equal("*bold text*", result);
	}

	// ── Strikethrough ───────────────────────────────────────────────

	[Fact]
	public void Format_Strikethrough_ConvertsTilde() {
		string result = this._sut.Format("~~deleted~~");
		Assert.Equal("~deleted~", result);
	}

	// ── Code Blocks ─────────────────────────────────────────────────

	[Fact]
	public void Format_CodeBlock_PreservesContent() {
		string result = this._sut.Format("```csharp\nvar x = 1;\n```");
		Assert.Equal("```csharp\nvar x = 1;\n```", result);
	}

	[Fact]
	public void Format_InlineCode_PreservesContent() {
		string result = this._sut.Format("`hello.world`");
		Assert.Equal("`hello.world`", result);
	}

	// ── Tables ──────────────────────────────────────────────────────

	[Fact]
	public void Format_TableWithInlineCode_PreservesTextNotNumbers() {
		string input =
			"| Método | Endpoint | Descrição |\n" +
			"|---|---|---|\n" +
			"| `GET` | `/tasks` | Listar todas as tasks |\n" +
			"| `POST` | `/tasks` | Criar task |";

		string result = this._sut.Format(input);

		Assert.Contains("GET", result);
		Assert.Contains("/tasks", result);
		Assert.Contains("POST", result);
		Assert.Contains("Listar todas as tasks", result);
		Assert.StartsWith("```", result);
	}

	// ── Mixed Content ───────────────────────────────────────────────

	[Fact]
	public void Format_MixedBoldAndText_FormatsCorrectly() {
		string result = this._sut.Format("This is **important**.");
		Assert.Equal("This is *important*\\.", result);
	}
}
