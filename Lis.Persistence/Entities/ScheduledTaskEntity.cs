using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using System.Text.Json.Serialization;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Lis.Persistence.Entities;

[Table("scheduled_task")]
public sealed class ScheduledTaskEntity {
	[Key]
	[Column("id")]
	[JsonPropertyName("id")]
	public long Id { get; set; }

	[Required]
	[MaxLength(128)]
	[Column("name", TypeName = "varchar(128)")]
	[JsonPropertyName("name")]
	public required string Name { get; set; }

	/// <summary>Cron expression (5-field standard).</summary>
	[Required]
	[MaxLength(64)]
	[Column("cron_expression", TypeName = "varchar(64)")]
	[JsonPropertyName("cron_expression")]
	public required string CronExpression { get; set; }

	/// <summary>IANA timezone for cron evaluation (e.g. "America/Sao_Paulo").</summary>
	[MaxLength(64)]
	[Column("timezone", TypeName = "varchar(64)")]
	[JsonPropertyName("timezone")]
	public string? Timezone { get; set; }

	[Required]
	[MaxLength(128)]
	[Column("chat_id", TypeName = "varchar(128)")]
	[JsonPropertyName("chat_id")]
	public required string ChatId { get; set; }

	/// <summary>Channel name (whatsapp, telegram, discord, mattermost).</summary>
	[Required]
	[MaxLength(32)]
	[Column("channel", TypeName = "varchar(32)")]
	[JsonPropertyName("channel")]
	public required string Channel { get; set; }

	[Column("agent_id")]
	[JsonPropertyName("agent_id")]
	public long? AgentId { get; set; }

	/// <summary>
	/// If Type is "prompt", sent as a user message to the AI.
	/// If Type is "message", sent directly to the chat.
	/// </summary>
	[Required]
	[Column("payload")]
	[JsonPropertyName("payload")]
	public required string Payload { get; set; }

	/// <summary>"prompt" (AI processes it) or "message" (sent directly).</summary>
	[Required]
	[MaxLength(16)]
	[Column("type", TypeName = "varchar(16)")]
	[JsonPropertyName("type")]
	public string Type { get; set; } = "prompt";

	[Column("enabled")]
	[JsonPropertyName("enabled")]
	public bool Enabled { get; set; } = true;

	[Column("next_run_at")]
	[JsonPropertyName("next_run_at")]
	public DateTimeOffset? NextRunAt { get; set; }

	[Column("last_run_at")]
	[JsonPropertyName("last_run_at")]
	public DateTimeOffset? LastRunAt { get; set; }

	[Column("created_at")]
	[JsonPropertyName("created_at")]
	public DateTimeOffset CreatedAt { get; set; }

	[Column("updated_at")]
	[JsonPropertyName("updated_at")]
	public DateTimeOffset UpdatedAt { get; set; }
}

public class ScheduledTaskEntityConfiguration : IEntityTypeConfiguration<ScheduledTaskEntity> {
	public void Configure(EntityTypeBuilder<ScheduledTaskEntity> builder) {
		builder.HasIndex(e => e.Enabled);
		builder.HasIndex(e => e.NextRunAt);
	}
}
