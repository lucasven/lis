using Lis.Tools;

namespace Lis.Tests.Tools;

public class ImagePluginTests {
	[Theory]
	[InlineData(null, null, null, 1)]
	[InlineData("1024x1024", "high", "transparent", 1)]
	[InlineData("auto", "AUTO", "Opaque", 4)]
	public void Validate_AcceptsValidInput(string? size, string? quality, string? background, int count) {
		Assert.Null(ImagePlugin.Validate("a cat", size, quality, background, count));
	}

	[Theory]
	[InlineData("", null, null, null, 1, "prompt")]
	[InlineData("a cat", "big", null, null, 1, "size")]
	[InlineData("a cat", null, "ultra", null, 1, "quality")]
	[InlineData("a cat", null, null, "green", 1, "background")]
	[InlineData("a cat", null, null, null, 0, "count")]
	[InlineData("a cat", null, null, null, 5, "count")]
	public void Validate_RejectsInvalidInput(string prompt, string? size, string? quality, string? background, int count, string field) {
		string? error = ImagePlugin.Validate(prompt, size, quality, background, count);

		Assert.NotNull(error);
		Assert.StartsWith(field, error);
	}

	[Fact]
	public void ParseReferencePaths_SplitsAndTrims() {
		Assert.Equal(["a.png", "images/b.jpg"], ImagePlugin.ParseReferencePaths(" a.png , images/b.jpg ,"));
		Assert.Empty(ImagePlugin.ParseReferencePaths(null));
	}

	[Theory]
	[InlineData("image/png", "png")]
	[InlineData("image/jpeg", "jpg")]
	[InlineData("image/webp", "webp")]
	public void ExtensionFor_MapsMime(string mime, string expected) {
		Assert.Equal(expected, ImagePlugin.ExtensionFor(mime));
	}
}
