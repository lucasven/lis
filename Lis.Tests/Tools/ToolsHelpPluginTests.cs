using Lis.Tools;

namespace Lis.Tests.Tools;

public sealed class ToolsHelpPluginTests {

	[Fact]
	public void Get_NoGroup_ReturnsAvailableGroupList() {
		string result = ToolsHelpPlugin.Get();

		Assert.Contains("skills", result);
		Assert.Contains("browser", result);
		Assert.Contains("memory", result);
		Assert.Contains("web", result);
		Assert.Contains("a2a", result);
		Assert.Contains("cron", result);
		Assert.Contains("config", result);
		Assert.Contains("filesystem", result);
		Assert.Contains("prompt", result);
	}

	[Fact]
	public void Get_InvalidGroup_ReturnsErrorWithGroupList() {
		string result = ToolsHelpPlugin.Get("nonexistent");

		Assert.Contains("Unknown group", result);
		Assert.Contains("Available groups", result);
	}

	[Theory]
	[InlineData("skills")]
	[InlineData("a2a")]
	[InlineData("cron")]
	[InlineData("config")]
	[InlineData("browser")]
	[InlineData("filesystem")]
	[InlineData("memory")]
	[InlineData("web")]
	[InlineData("prompt")]
	public void Get_AllGroups_ReturnContent(string group) {
		string result = ToolsHelpPlugin.Get(group);

		Assert.NotEmpty(result);
		Assert.DoesNotContain("Unknown group", result);
	}

	[Fact]
	public void Get_GroupName_IsCaseInsensitive() {
		string lower = ToolsHelpPlugin.Get("skills");
		string upper = ToolsHelpPlugin.Get("SKILLS");
		string mixed = ToolsHelpPlugin.Get("Skills");

		Assert.Equal(lower, upper);
		Assert.Equal(lower, mixed);
	}
}
