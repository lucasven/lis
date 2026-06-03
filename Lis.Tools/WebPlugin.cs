using System.ComponentModel;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Web;

using Lis.Core.Util;

using Microsoft.Extensions.Logging;
using Microsoft.SemanticKernel;

namespace Lis.Tools;

public sealed partial class WebPlugin(IHttpClientFactory httpClientFactory, ILogger<WebPlugin> logger) {

	[KernelFunction("search")]
	[Description("Search the web via Brave Search. Returns titles, URLs, and text snippets for matching results. Use web-fetch to read full page content from a result URL.")]
	[ToolSummarization(SummarizationPolicy.Summarize)]
	[ToolAuthorization(ToolAuthLevel.Open)]
	public async Task<string> SearchAsync(
		[Description("Search query")] string query,
		[Description("Maximum number of results (1-10)")] int maxResults = 5) {
		await ToolContext.NotifyAsync($"🔍 Searching: {query}");

		maxResults = Math.Clamp(maxResults, 1, 10);

		string? apiKey  = Environment.GetEnvironmentVariable("LIS_WEB_SEARCH_API_KEY");
		string? enabled = Environment.GetEnvironmentVariable("LIS_WEB_SEARCH_ENABLED");

		if (!string.Equals(enabled, "true", StringComparison.OrdinalIgnoreCase) || string.IsNullOrWhiteSpace(apiKey))
			return "Web search is not configured.";

		try {
			string encodedQuery = HttpUtility.UrlEncode(query);
			string requestUrl   = $"https://api.search.brave.com/res/v1/web/search?q={encodedQuery}&count={maxResults}";

			using HttpRequestMessage request = new(HttpMethod.Get, requestUrl);
			request.Headers.Add("X-Subscription-Token", apiKey);
			request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

			using HttpClient client = httpClientFactory.CreateClient();
			using HttpResponseMessage response = await client.SendAsync(request);
			response.EnsureSuccessStatusCode();

			string json = await response.Content.ReadAsStringAsync();

			using JsonDocument doc = JsonDocument.Parse(json);
			JsonElement root = doc.RootElement;

			if (!root.TryGetProperty("web", out JsonElement web) ||
				!web.TryGetProperty("results", out JsonElement results))
				return "No results found.";

			StringBuilder sb = new();
			int index = 1;

			foreach (JsonElement result in results.EnumerateArray()) {
				string title       = result.GetProperty("title").GetString() ?? "";
				string url         = result.GetProperty("url").GetString() ?? "";
				string description = result.GetProperty("description").GetString() ?? "";

				sb.AppendLine($"{index}. {title}");
				sb.AppendLine($"   {url}");
				sb.AppendLine($"   {description}");
				index++;
			}

			string output = sb.ToString().TrimEnd();
			return output.Length > 0 ? output : "No results found.";
		} catch (Exception ex) {
			logger.LogWarning(ex, "Web search failed for query '{Query}'", query);
			return $"Search error: {ex.Message}";
		}
	}

	[KernelFunction("fetch")]
	[Description("Fetch a URL and return its content as cleaned plain text (HTML tags stripped). Works for articles, docs, APIs, and any public URL. For interactive pages, use browser tools instead.")]
	[ToolSummarization(SummarizationPolicy.Summarize)]
	[ToolAuthorization(ToolAuthLevel.Open)]
	public async Task<string> FetchAsync(
		[Description("URL to fetch")] string url,
		[Description("Maximum content length (1000-50000)")] int maxLength = 10000) {
		await ToolContext.NotifyAsync($"🌐 Fetching: {url}");

		maxLength = Math.Clamp(maxLength, 1000, 50000);

		try {
			using HttpRequestMessage request = new(HttpMethod.Get, url);
			request.Headers.UserAgent.ParseAdd("Lis/1.0");

			using CancellationTokenSource cts = new(TimeSpan.FromSeconds(10));
			using HttpClient client = httpClientFactory.CreateClient();
			using HttpResponseMessage response = await client.SendAsync(request, cts.Token);
			response.EnsureSuccessStatusCode();

			string html = await response.Content.ReadAsStringAsync(cts.Token);

			// Basic HTML-to-text: strip tags, decode entities, collapse whitespace
			string text = StripHtmlTags().Replace(html, " ");
			text = HttpUtility.HtmlDecode(text);
			text = CollapseWhitespace().Replace(text, " ").Trim();

			if (text.Length > maxLength)
				text = text[..maxLength] + " [...truncated]";

			return text;
		} catch (Exception ex) {
			logger.LogWarning(ex, "Web fetch failed for URL '{Url}'", url);
			return $"Fetch error: {ex.Message}";
		}
	}

	[GeneratedRegex("<[^>]+>", RegexOptions.NonBacktracking)]
	private static partial Regex StripHtmlTags();

	[GeneratedRegex(@"\s+", RegexOptions.NonBacktracking)]
	private static partial Regex CollapseWhitespace();
}
