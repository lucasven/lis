using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

using Lis.Core.Channel;
using Lis.Core.Util;

namespace Lis.Providers.OpenAi.Codex;

/// <summary>
/// Image generation through the ChatGPT subscription (Codex OAuth) — no API key.
/// Uses the direct Images routes, so the requested image model is honored and no chat model sits in between.
/// </summary>
public sealed class CodexImageClient(CodexTokenManager tokenManager, CodexOptions options, HttpClient httpClient)
	: IImageGenerationService {
	private const int MaxErrorBodyChars = 500;

	[Trace("CodexImageClient > GenerateAsync")]
	public async Task<IReadOnlyList<GeneratedImage>> GenerateAsync(ImageGenerationRequest request, CancellationToken ct = default) {
		if (string.IsNullOrWhiteSpace(request.Prompt)) throw new ArgumentException("Prompt is required.", nameof(request));

		bool   isEdit = request.References.Count > 0;
		string url    = ResolveImagesUrl(options.BaseUrl, isEdit ? "edits" : "generations");

		Activity.Current?.SetTag("image.model", options.ImageModel);
		Activity.Current?.SetTag("image.operation", isEdit ? "edit" : "generate");
		Activity.Current?.SetTag("image.references.count", request.References.Count);

		CodexImageRequest body  = BuildRequest(request, options.ImageModel);
		CodexTokenInfo    token = await tokenManager.GetValidTokenAsync(ct);

		using HttpRequestMessage httpRequest = new(HttpMethod.Post, url);
		foreach ((string key, string value) in CodexChatClient.BuildBaseHeaders(token))
			httpRequest.Headers.TryAddWithoutValidation(key, value);
		httpRequest.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
		httpRequest.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");

		using HttpResponseMessage response = await httpClient.SendAsync(httpRequest, ct);
		string responseBody = await response.Content.ReadAsStringAsync(ct);

		if (!response.IsSuccessStatusCode) throw BuildHttpException(response.StatusCode, responseBody);

		IReadOnlyList<GeneratedImage> images = ParseResponse(responseBody);
		Activity.Current?.SetTag("images.count", images.Count);
		return images;
	}

	internal static CodexImageRequest BuildRequest(ImageGenerationRequest request, string model) => new() {
		Prompt     = request.Prompt,
		Model      = model,
		Images     = request.References.Count > 0 ? request.References.Select(ToDataUrl).ToList() : null,
		N          = request.Count > 1 ? request.Count : null,
		Size       = NullIfEmpty(request.Size),
		Quality    = NullIfEmpty(request.Quality),
		Background = NullIfEmpty(request.Background)
	};

	internal static IReadOnlyList<GeneratedImage> ParseResponse(string json) {
		CodexImageResponse? parsed = JsonSerializer.Deserialize<CodexImageResponse>(json);
		if (parsed?.Data is not { Count: > 0 }) throw new InvalidOperationException("Image response contained no images.");

		List<GeneratedImage> images = [];
		foreach (CodexImageData item in parsed.Data) {
			if (item.B64Json is not { Length: > 0 }) continue;

			byte[] data = Convert.FromBase64String(item.B64Json);
			images.Add(new GeneratedImage(data, DetectMimeType(data, parsed.OutputFormat)));
		}

		if (images.Count == 0) throw new InvalidOperationException("Image response contained no base64 image data.");
		return images;
	}

	internal static string ResolveImagesUrl(string baseUrl, string route) {
		string trimmed = baseUrl.TrimEnd('/');

		// Accept base URLs pointing at the Responses endpoint or the codex root as well as the backend root
		if (trimmed.EndsWith("/codex/responses", StringComparison.Ordinal)) trimmed = trimmed[..^"/responses".Length];
		if (!trimmed.EndsWith("/codex", StringComparison.Ordinal)) trimmed += "/codex";

		return $"{trimmed}/images/{route}";
	}

	// Magic bytes win over the declared format — the backend's output_format is optional
	internal static string DetectMimeType(byte[] data, string? outputFormat) {
		if (data.Length >= 8 && data[0] == 0x89 && data[1] == 0x50 && data[2] == 0x4E && data[3] == 0x47) return "image/png";
		if (data.Length >= 3 && data[0] == 0xFF && data[1] == 0xD8 && data[2] == 0xFF) return "image/jpeg";
		if (data.Length >= 12 && data[0] == 0x52 && data[1] == 0x49 && data[2] == 0x46 && data[3] == 0x46
		    && data[8] == 0x57 && data[9] == 0x45 && data[10] == 0x42 && data[11] == 0x50) return "image/webp";

		return outputFormat?.ToLowerInvariant() switch {
			"jpeg" or "jpg" => "image/jpeg",
			"webp"          => "image/webp",
			_               => "image/png"
		};
	}

	private static CodexImageUrl ToDataUrl(MediaDownload media) =>
		new($"data:{media.MimeType};base64,{Convert.ToBase64String(media.Data)}");

	private static string? NullIfEmpty(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

	private static Exception BuildHttpException(HttpStatusCode status, string body) {
		string snippet = body.Length > MaxErrorBodyChars ? body[..MaxErrorBodyChars] + "…" : body;
		string message = $"Codex image request failed with {(int)status}: {snippet}";
		Activity.Current?.SetStatus(ActivityStatusCode.Error, message);

		if (status is HttpStatusCode.Unauthorized) return new CodexAuthException(message);
		return new InvalidOperationException(message);
	}
}
