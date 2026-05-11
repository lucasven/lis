using System.Text.Json.Serialization;

namespace Lis.Core.A2A;

// -- Agent Card (discovery) --------------------------------------------------

public sealed record AgentCard {
	[JsonPropertyName("name")]
	public required string Name { get; init; }

	[JsonPropertyName("description")]
	public required string Description { get; init; }

	[JsonPropertyName("version")]
	public string Version { get; init; } = "1.0.0";

	[JsonPropertyName("protocolVersion")]
	public string ProtocolVersion { get; init; } = "1.0";

	[JsonPropertyName("defaultInputModes")]
	public IReadOnlyList<string> DefaultInputModes { get; init; } = ["text/plain"];

	[JsonPropertyName("defaultOutputModes")]
	public IReadOnlyList<string> DefaultOutputModes { get; init; } = ["text/plain"];

	[JsonPropertyName("capabilities")]
	public AgentCapabilities Capabilities { get; init; } = new();

	[JsonPropertyName("skills")]
	public IReadOnlyList<AgentSkill> Skills { get; init; } = [];
}

public sealed record AgentSkill {
	[JsonPropertyName("id")]
	public required string Id { get; init; }

	[JsonPropertyName("name")]
	public required string Name { get; init; }

	[JsonPropertyName("description")]
	public required string Description { get; init; }

	[JsonPropertyName("tags")]
	public IReadOnlyList<string> Tags { get; init; } = [];

	[JsonPropertyName("examples")]
	public IReadOnlyList<string>? Examples { get; init; }
}

public sealed record AgentCapabilities {
	[JsonPropertyName("streaming")]
	public bool Streaming { get; init; }

	[JsonPropertyName("pushNotifications")]
	public bool PushNotifications { get; init; }

	[JsonPropertyName("stateTransitionHistory")]
	public bool StateTransitionHistory { get; init; }
}

// -- Message -----------------------------------------------------------------

public sealed record A2aMessage {
	[JsonPropertyName("messageId")]
	public required string MessageId { get; init; }

	[JsonPropertyName("role")]
	public required string Role { get; init; }

	[JsonPropertyName("parts")]
	public required IReadOnlyList<Part> Parts { get; init; }

	[JsonPropertyName("contextId")]
	public string? ContextId { get; init; }

	[JsonPropertyName("taskId")]
	public string? TaskId { get; init; }

	[JsonPropertyName("metadata")]
	public IDictionary<string, object>? Metadata { get; init; }
}

// -- Parts (discriminated union) ---------------------------------------------

[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]
[JsonDerivedType(typeof(TextPart), "text")]
[JsonDerivedType(typeof(DataPart), "data")]
public abstract record Part;

public sealed record TextPart : Part {
	[JsonPropertyName("text")]
	public required string Text { get; init; }
}

public sealed record DataPart : Part {
	[JsonPropertyName("data")]
	public required Dictionary<string, object> Data { get; init; }
}

// -- Task (lifecycle) --------------------------------------------------------

public sealed record A2aTask {
	[JsonPropertyName("id")]
	public required string Id { get; init; }

	[JsonPropertyName("contextId")]
	public required string ContextId { get; init; }

	[JsonPropertyName("status")]
	public required A2aTaskStatus Status { get; init; }

	[JsonPropertyName("artifacts")]
	public IReadOnlyList<A2aArtifact>? Artifacts { get; init; }

	[JsonPropertyName("history")]
	public IReadOnlyList<A2aMessage>? History { get; init; }
}

public sealed record A2aTaskStatus {
	[JsonPropertyName("state")]
	public required A2aTaskState State { get; init; }

	[JsonPropertyName("message")]
	public A2aMessage? Message { get; init; }

	[JsonPropertyName("timestamp")]
	public DateTimeOffset? Timestamp { get; init; }
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum A2aTaskState {
	Submitted,
	Working,
	Completed,
	Failed,
	Canceled
}

public sealed record A2aArtifact {
	[JsonPropertyName("artifactId")]
	public required string ArtifactId { get; init; }

	[JsonPropertyName("parts")]
	public required IReadOnlyList<Part> Parts { get; init; }

	[JsonPropertyName("name")]
	public string? Name { get; init; }

	[JsonPropertyName("description")]
	public string? Description { get; init; }
}

// -- Service interfaces (internal transport) ---------------------------------

public interface IAgentCardProvider {
	AgentCard GetCard(string agentName);
	IReadOnlyList<AgentCard> ListCards();
}

public interface IA2aClient {
	Task<A2aTask> SendMessageAsync(string targetAgent, A2aMessage message, CancellationToken ct = default);
}
