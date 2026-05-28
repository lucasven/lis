using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Lis.Core.Observability;

public sealed record OpikOptions {
	public required string BaseUrl   { get; init; }
	public string?         ApiKey    { get; init; }
	public string?         Workspace { get; init; }
	public string          Project   { get; init; } = "lis";
}

public sealed record OpikTrace {
	[JsonPropertyName("id")]           public required string  Id          { get; init; }
	[JsonPropertyName("name")]         public required string  Name        { get; init; }
	[JsonPropertyName("project_name")] public string?          ProjectName { get; init; }
	[JsonPropertyName("thread_id")]    public string?          ThreadId    { get; init; }
	[JsonPropertyName("start_time")]   public required string  StartTime   { get; init; }
	[JsonPropertyName("end_time")]     public string?          EndTime     { get; set; }
	[JsonPropertyName("input")]        public object?          Input       { get; init; }
	[JsonPropertyName("output")]       public object?          Output      { get; set; }
	[JsonPropertyName("metadata")]     public object?          Metadata    { get; init; }
	[JsonPropertyName("tags")]         public List<string>?    Tags        { get; init; }
	[JsonPropertyName("usage")]        public OpikUsage?       Usage       { get; set; }
	[JsonPropertyName("error_info")]   public OpikErrorInfo?   ErrorInfo   { get; set; }
}

public sealed record OpikSpan {
	[JsonPropertyName("id")]              public required string  Id           { get; init; }
	[JsonPropertyName("trace_id")]        public required string  TraceId      { get; init; }
	[JsonPropertyName("parent_span_id")]  public string?          ParentSpanId { get; init; }
	[JsonPropertyName("project_name")]    public string?          ProjectName  { get; init; }
	[JsonPropertyName("name")]            public required string  Name         { get; init; }
	[JsonPropertyName("type")]            public required string  Type         { get; init; }
	[JsonPropertyName("start_time")]      public required string  StartTime    { get; init; }
	[JsonPropertyName("end_time")]        public string?          EndTime      { get; set; }
	[JsonPropertyName("input")]           public object?          Input        { get; init; }
	[JsonPropertyName("output")]          public object?          Output       { get; set; }
	[JsonPropertyName("metadata")]        public object?          Metadata     { get; init; }
	[JsonPropertyName("model")]           public string?          Model        { get; init; }
	[JsonPropertyName("provider")]        public string?          Provider     { get; init; }
	[JsonPropertyName("usage")]           public OpikUsage?       Usage        { get; set; }
	[JsonPropertyName("error_info")]      public OpikErrorInfo?   ErrorInfo    { get; set; }
}

public sealed record OpikUsage {
	[JsonPropertyName("prompt_tokens")]     public int PromptTokens     { get; init; }
	[JsonPropertyName("completion_tokens")] public int CompletionTokens { get; init; }
	[JsonPropertyName("total_tokens")]      public int TotalTokens      { get; init; }
}

public sealed record OpikErrorInfo {
	[JsonPropertyName("error_type")]    public required string ErrorType    { get; init; }
	[JsonPropertyName("error_message")] public required string ErrorMessage { get; init; }
}

public sealed class OpikClient : IDisposable {
	private static readonly JsonSerializerOptions JsonOpts = new() {
		DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
	};

	private readonly HttpClient _http;

	public OpikClient(OpikOptions options) {
		this._http = new HttpClient { BaseAddress = new Uri(options.BaseUrl.TrimEnd('/') + "/") };

		if (options.ApiKey is { Length: > 0 })
			this._http.DefaultRequestHeaders.TryAddWithoutValidation("authorization", options.ApiKey);
		if (options.Workspace is { Length: > 0 })
			this._http.DefaultRequestHeaders.TryAddWithoutValidation("Comet-Workspace", options.Workspace);
	}

	public async Task SendTracesAsync(IReadOnlyList<OpikTrace> traces, CancellationToken ct = default) {
		if (traces.Count == 0) return;
		HttpResponseMessage resp = await this._http.PostAsJsonAsync("api/v1/private/traces/batch",
			new { traces }, JsonOpts, ct);
		await EnsureSuccessOrThrowWithBody(resp);
	}

	public async Task SendSpansAsync(IReadOnlyList<OpikSpan> spans, CancellationToken ct = default) {
		if (spans.Count == 0) return;
		HttpResponseMessage resp = await this._http.PostAsJsonAsync("api/v1/private/spans/batch",
			new { spans }, JsonOpts, ct);
		await EnsureSuccessOrThrowWithBody(resp);
	}

	private static async Task EnsureSuccessOrThrowWithBody(HttpResponseMessage resp) {
		if (resp.IsSuccessStatusCode) return;
		string body = await resp.Content.ReadAsStringAsync();
		throw new HttpRequestException($"Opik API {(int)resp.StatusCode}: {body}");
	}

	public void Dispose() => this._http.Dispose();
}
