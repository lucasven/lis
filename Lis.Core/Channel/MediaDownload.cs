namespace Lis.Core.Channel;

/// <summary>Raw media downloaded from the messaging platform.</summary>
public record MediaDownload(byte[] Data, string MimeType);

/// <summary>Media to upload to a messaging platform.</summary>
public sealed record MediaUpload(byte[] Data, string MimeType, string? Filename = null)
	: MediaDownload(Data, MimeType) {

	/// <summary>
	/// Returns the explicit filename or derives a default from MimeType.
	/// </summary>
	public string ResolveFilename() {
		if (this.Filename is { Length: > 0 }) return this.Filename;

		string[] extensions = MimeTypes.GetMimeTypeExtensions(this.MimeType).ToArray();
		if (extensions.Length > 0) {
			// Prefer the extension that matches the MIME subtype (e.g. "jpeg" for "image/jpeg")
			string subtype = this.MimeType.Contains('/') ? this.MimeType.Split('/')[1] : this.MimeType;
			string? preferred = extensions.FirstOrDefault(e => e == subtype)
				?? extensions.OrderByDescending(e => e.Length).First();

			return $"file.{preferred}";
		}

		return "file.bin";
	}
}
