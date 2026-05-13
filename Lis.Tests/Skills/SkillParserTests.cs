using Lis.Tools;

namespace Lis.Tests.Skills;

public class SkillParserTests {

	[Fact]
	public void Parse_ValidSkillFile_ExtractsFieldsAndContent() {
		string input = "---\nname: translator\ndescription: Translates messages between languages\nversion: 2\n---\n\nWhen the user asks you to translate text, provide accurate translations.\nAlways specify the source and target languages.";

		ParsedSkill result = SkillParser.Parse(input);

		Assert.Equal("translator", result.Name);
		Assert.Equal("Translates messages between languages", result.Description);
		Assert.Equal(2, result.Version);
		Assert.StartsWith("When the user asks you to translate", result.Content);
	}

	[Fact]
	public void Parse_NoFrontMatterDelimiters_ReturnsError() {
		string input = "name: translator\ndescription: Does stuff\n\nSome instructions here.";

		SkillParseResult result = SkillParser.TryParse(input);

		Assert.False(result.IsSuccess);
		Assert.Contains("front matter", result.Error, StringComparison.OrdinalIgnoreCase);
	}

	[Fact]
	public void Parse_MissingName_ReturnsError() {
		string input = "---\ndescription: Translates things\n---\n\nInstructions here.";

		SkillParseResult result = SkillParser.TryParse(input);

		Assert.False(result.IsSuccess);
		Assert.Contains("name", result.Error, StringComparison.OrdinalIgnoreCase);
	}

	[Fact]
	public void Parse_MissingDescription_ReturnsError() {
		string input = "---\nname: translator\n---\n\nInstructions here.";

		SkillParseResult result = SkillParser.TryParse(input);

		Assert.False(result.IsSuccess);
		Assert.Contains("description", result.Error, StringComparison.OrdinalIgnoreCase);
	}

	[Theory]
	[InlineData("My-Skill", "invalid")]
	[InlineData("-bad-name", "invalid")]
	[InlineData("x", "invalid")]
	public void Parse_InvalidNameFormat_ReturnsError(string name, string expectedErrorFragment) {
		string input = $"---\nname: {name}\ndescription: Does stuff\n---\n\nInstructions.";

		SkillParseResult result = SkillParser.TryParse(input);

		Assert.False(result.IsSuccess);
		Assert.Contains(expectedErrorFragment, result.Error, StringComparison.OrdinalIgnoreCase);
	}

	[Theory]
	[InlineData("ab")]
	[InlineData("my-skill")]
	[InlineData("code-review-v2")]
	public void Parse_ValidNameFormat_Succeeds(string name) {
		string input = $"---\nname: {name}\ndescription: Does stuff\n---\n\nInstructions.";

		SkillParseResult result = SkillParser.TryParse(input);

		Assert.True(result.IsSuccess);
		Assert.Equal(name, result.Skill!.Name);
	}

	[Fact]
	public void Parse_EmptyContent_ReturnsError() {
		string input = "---\nname: empty-skill\ndescription: Has no body\n---\n";

		SkillParseResult result = SkillParser.TryParse(input);

		Assert.False(result.IsSuccess);
		Assert.Contains("content", result.Error, StringComparison.OrdinalIgnoreCase);
	}

	[Fact]
	public void Parse_OversizedContent_ReturnsError() {
		string content = new('x', 100_001);
		string input   = $"---\nname: big-skill\ndescription: Too large\n---\n\n{content}";

		SkillParseResult result = SkillParser.TryParse(input);

		Assert.False(result.IsSuccess);
		Assert.Contains("100,000", result.Error);
	}
}
