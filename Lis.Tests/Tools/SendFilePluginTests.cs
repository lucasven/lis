using Lis.Tools;

namespace Lis.Tests.Tools;

public class SendFilePluginTests {
	[Fact]
	public void ResolveMimeType_From_Extension() {
		Assert.Equal("image/png", SendFilePlugin.ResolveMimeType("screenshot.png", null));
		Assert.Equal("image/jpeg", SendFilePlugin.ResolveMimeType("photo.jpg", null));
		Assert.Equal("application/pdf", SendFilePlugin.ResolveMimeType("doc.pdf", null));
		Assert.Equal("text/html", SendFilePlugin.ResolveMimeType("page.html", null));
		Assert.Equal("text/markdown", SendFilePlugin.ResolveMimeType("readme.md", null));
	}

	[Fact]
	public void ResolveMimeType_Override_Takes_Precedence() {
		Assert.Equal("image/webp", SendFilePlugin.ResolveMimeType("file.png", "image/webp"));
	}

	[Fact]
	public void ResolveMimeType_Unknown_Extension_Falls_Back() {
		Assert.Equal("application/octet-stream", SendFilePlugin.ResolveMimeType("file.xyz123", null));
	}
}
