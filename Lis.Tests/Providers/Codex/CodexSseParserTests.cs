using System.Text.Json;

using Lis.Providers.OpenAi.Codex;

namespace Lis.Tests.Providers.Codex;

public class CodexSseParserTests {
	public static IEnumerable<object[]> SseFixtures => new[] {
		new object[] {
			"sse-simple-text.txt", new[] {
				"response.created",
				"response.output_item.added",
				"response.content_part.added",
				"response.output_text.delta",
				"response.output_text.delta",
				"response.output_item.done",
				"response.completed"
			}
		},
		new object[] {
			"sse-tool-call.txt", new[] {
				"response.created",
				"response.output_item.added",
				"response.function_call_arguments.delta",
				"response.function_call_arguments.delta",
				"response.function_call_arguments.done",
				"response.output_item.done",
				"response.completed"
			}
		},
		new object[] {
			"sse-reasoning.txt", new[] {
				"response.created",
				"response.output_item.added",
				"response.reasoning_summary_part.added",
				"response.reasoning_summary_text.delta",
				"response.reasoning_summary_part.done",
				"response.output_item.done",
				"response.output_item.added",
				"response.content_part.added",
				"response.output_text.delta",
				"response.output_item.done",
				"response.completed"
			}
		},
		new object[] {
			"sse-error.txt", new[] {
				"response.created",
				"response.failed"
			}
		}
	};

	[Theory]
	[MemberData(nameof(SseFixtures))]
	public async Task Characterization_EventSequence_Matches_Snapshot(
		string fixture, string[] expectedTypes) {
		await using Stream stream = LoadFixture(fixture);
		List<string> actualTypes = [];

		await foreach (JsonElement evt in CodexSseParser.ParseAsync(stream)) {
			string? type = evt.GetProperty("type").GetString();
			Assert.NotNull(type);
			actualTypes.Add(type!);
		}

		Assert.Equal(expectedTypes, actualTypes);
	}

	[Theory]
	[MemberData(nameof(AllFixtureFiles))]
	public async Task Property_AllEvents_Have_Type(string fixture) {
		await using Stream stream = LoadFixture(fixture);

		await foreach (JsonElement evt in CodexSseParser.ParseAsync(stream)) {
			Assert.True(evt.TryGetProperty("type", out JsonElement typeProp));
			Assert.Equal(JsonValueKind.String, typeProp.ValueKind);
			Assert.NotEmpty(typeProp.GetString()!);
		}
	}

	[Theory]
	[MemberData(nameof(AllFixtureFiles))]
	public async Task Property_Done_Sentinel_Never_Yielded(string fixture) {
		await using Stream stream = LoadFixture(fixture);

		await foreach (JsonElement evt in CodexSseParser.ParseAsync(stream)) {
			string? type = evt.GetProperty("type").GetString();
			Assert.NotEqual("[DONE]", type);
		}
	}

	[Fact]
	public async Task Property_EmptyStream_YieldsNothing() {
		await using MemoryStream stream = new(Array.Empty<byte>());
		List<JsonElement> events = [];

		await foreach (JsonElement evt in CodexSseParser.ParseAsync(stream))
			events.Add(evt);

		Assert.Empty(events);
	}

	public static IEnumerable<object[]> AllFixtureFiles => Directory
		.GetFiles(FixturesPath, "sse-*.txt")
		.Select(f => new object[] { Path.GetFileName(f) });

	private static readonly string FixturesPath = Path.Combine(
		AppContext.BaseDirectory, "Providers", "Codex", "Fixtures");

	private static FileStream LoadFixture(string name) =>
		File.OpenRead(Path.Combine(FixturesPath, name));
}
