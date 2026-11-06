using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

using Lis.Core.Channel;
using Lis.Providers.OpenAi.Codex;

namespace Lis.Tests.Providers.Codex;

public class CodexImageClientTests {
	private static readonly byte[] PngBytes  = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x00];
	private static readonly byte[] JpegBytes = [0xFF, 0xD8, 0xFF, 0xE0, 0x00];

	[Theory]
	[InlineData("https://chatgpt.com/backend-api", "https://chatgpt.com/backend-api/codex/images/generations")]
	[InlineData("https://chatgpt.com/backend-api/", "https://chatgpt.com/backend-api/codex/images/generations")]
	[InlineData("https://chatgpt.com/backend-api/codex", "https://chatgpt.com/backend-api/codex/images/generations")]
	[InlineData("https://chatgpt.com/backend-api/codex/responses", "https://chatgpt.com/backend-api/codex/images/generations")]
	public void ResolveImagesUrl_NormalizesBaseUrl(string baseUrl, string expected) {
		Assert.Equal(expected, CodexImageClient.ResolveImagesUrl(baseUrl, "generations"));
	}

	[Fact]
	public void BuildRequest_Generation_OmitsUnsetFields() {
		CodexImageRequest body = CodexImageClient.BuildRequest(new ImageGenerationRequest("a red fox"), "gpt-image-1.5");

		JsonNode json = JsonNode.Parse(JsonSerializer.Serialize(body))!;

		Assert.Equal("a red fox", json["prompt"]!.GetValue<string>());
		Assert.Equal("gpt-image-1.5", json["model"]!.GetValue<string>());
		Assert.Null(json["images"]);
		Assert.Null(json["n"]);
		Assert.Null(json["size"]);
		Assert.Null(json["quality"]);
		Assert.Null(json["background"]);
	}

	[Fact]
	public void BuildRequest_Edit_SendsReferencesAsDataUrls() {
		ImageGenerationRequest request = new("add a red hat") {
			Size       = "1024x1536",
			Quality    = "medium",
			Background = "opaque",
			Count      = 2,
			References = [new MediaDownload("foo"u8.ToArray(), "image/png")]
		};

		JsonNode json = JsonNode.Parse(JsonSerializer.Serialize(CodexImageClient.BuildRequest(request, "gpt-image-1.5")))!;

		Assert.Equal("data:image/png;base64,Zm9v", json["images"]![0]!["image_url"]!.GetValue<string>());
		Assert.Equal(2, json["n"]!.GetValue<int>());
		Assert.Equal("1024x1536", json["size"]!.GetValue<string>());
		Assert.Equal("medium", json["quality"]!.GetValue<string>());
		Assert.Equal("opaque", json["background"]!.GetValue<string>());
	}

	[Fact]
	public void ParseResponse_DecodesImagesAndDetectsMime() {
		string json = $$"""{"created":1,"data":[{"b64_json":"{{Convert.ToBase64String(PngBytes)}}"},{"b64_json":"{{Convert.ToBase64String(JpegBytes)}}"}],"output_format":"png"}""";

		IReadOnlyList<GeneratedImage> images = CodexImageClient.ParseResponse(json);

		Assert.Equal(2, images.Count);
		Assert.Equal(PngBytes, images[0].Data);
		Assert.Equal("image/png", images[0].MimeType);
		Assert.Equal("image/jpeg", images[1].MimeType);
	}

	[Fact]
	public void ParseResponse_EmptyData_Throws() {
		Assert.Throws<InvalidOperationException>(() => CodexImageClient.ParseResponse("""{"created":1,"data":[]}"""));
	}

	[Fact]
	public void DetectMimeType_FallsBackToOutputFormat() {
		Assert.Equal("image/webp", CodexImageClient.DetectMimeType([0x00, 0x01], "webp"));
		Assert.Equal("image/png", CodexImageClient.DetectMimeType([0x00, 0x01], null));
	}

	[Fact]
	public async Task GenerateAsync_PostsToGenerations_WithCodexAuthHeaders() {
		CapturingHandler handler = new(HttpStatusCode.OK,
			$$"""{"created":1,"data":[{"b64_json":"{{Convert.ToBase64String(PngBytes)}}"}]}""");
		CodexImageClient client = CreateClient(handler);

		IReadOnlyList<GeneratedImage> images = await client.GenerateAsync(new ImageGenerationRequest("a lighthouse"));

		Assert.Single(images);
		Assert.Equal(HttpMethod.Post, handler.Request!.Method);
		Assert.Equal("https://chatgpt.com/backend-api/codex/images/generations", handler.Request.RequestUri!.ToString());
		Assert.StartsWith("Bearer ", handler.Request.Headers.Authorization!.ToString());
		Assert.Equal("acc_img", handler.Request.Headers.GetValues("chatgpt-account-id").Single());
		Assert.Equal("gpt-image-1.5", JsonNode.Parse(handler.Body!)!["model"]!.GetValue<string>());
	}

	[Fact]
	public async Task GenerateAsync_WithReferences_PostsToEdits() {
		CapturingHandler handler = new(HttpStatusCode.OK,
			$$"""{"created":1,"data":[{"b64_json":"{{Convert.ToBase64String(PngBytes)}}"}]}""");
		CodexImageClient client = CreateClient(handler);

		await client.GenerateAsync(new ImageGenerationRequest("make it blue") {
			References = [new MediaDownload(PngBytes, "image/png")]
		});

		Assert.EndsWith("/codex/images/edits", handler.Request!.RequestUri!.ToString());
	}

	[Fact]
	public async Task GenerateAsync_Unauthorized_ThrowsAuthException() {
		CodexImageClient client = CreateClient(new CapturingHandler(HttpStatusCode.Unauthorized, """{"error":"nope"}"""));

		await Assert.ThrowsAsync<CodexAuthException>(() => client.GenerateAsync(new ImageGenerationRequest("x")));
	}

	[Fact]
	public async Task GenerateAsync_ServerError_ThrowsWithStatus() {
		CodexImageClient client = CreateClient(new CapturingHandler(HttpStatusCode.TooManyRequests, """{"error":"quota"}"""));

		InvalidOperationException ex = await Assert.ThrowsAsync<InvalidOperationException>(
			() => client.GenerateAsync(new ImageGenerationRequest("x")));
		Assert.Contains("429", ex.Message);
	}

	private static CodexImageClient CreateClient(HttpMessageHandler handler) {
		CodexOptions options = new() {
			AccessToken  = CodexTokenManagerTests.CreateJwt(TimeSpan.FromHours(1), "acc_img"),
			RefreshToken = "rt-test"
		};
		CodexTokenManager tokenManager = new(options, new HttpClient());
		return new CodexImageClient(tokenManager, options, new HttpClient(handler));
	}

	private sealed class CapturingHandler(HttpStatusCode status, string responseBody) : HttpMessageHandler {
		public HttpRequestMessage? Request { get; private set; }
		public string?             Body    { get; private set; }

		protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) {
			this.Request = request;
			this.Body    = request.Content is null ? null : await request.Content.ReadAsStringAsync(ct);
			return new HttpResponseMessage(status) { Content = new StringContent(responseBody, Encoding.UTF8, "application/json") };
		}
	}
}
