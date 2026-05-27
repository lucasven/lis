using Lis.Core.Channel;

namespace Lis.Tests.Channel;

public class MediaUploadTests {
	[Fact]
	public void Inherits_MediaDownload() {
		MediaUpload upload = new([1, 2, 3], "image/png");
		MediaDownload download = upload;

		Assert.Equal([1, 2, 3], download.Data);
		Assert.Equal("image/png", download.MimeType);
	}

	[Fact]
	public void Filename_Defaults_To_Null() {
		MediaUpload upload = new([1], "image/png");
		Assert.Null(upload.Filename);
	}

	[Fact]
	public void Filename_Can_Be_Set() {
		MediaUpload upload = new([1], "image/png", "screenshot.png");
		Assert.Equal("screenshot.png", upload.Filename);
	}

	[Fact]
	public void ResolveFilename_Uses_Explicit_Filename() {
		MediaUpload upload = new([1], "image/png", "my-chart.png");
		Assert.Equal("my-chart.png", upload.ResolveFilename());
	}

	[Fact]
	public void ResolveFilename_Derives_From_MimeType() {
		MediaUpload upload = new([1], "image/jpeg");
		Assert.Equal("file.jpeg", upload.ResolveFilename());
	}

	[Fact]
	public void ResolveFilename_Falls_Back_To_Bin() {
		MediaUpload upload = new([1], "application/x-unknown-type-xyz");
		Assert.Equal("file.bin", upload.ResolveFilename());
	}
}
