using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using System.Text.Json.Serialization;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Lis.Persistence.Entities;

[Table("skill")]
public sealed class SkillEntity {
	[Key]
	[Column("id")]
	[JsonPropertyName("id")]
	public long Id { get; set; }

	[Required]
	[MaxLength(50)]
	[Column("name", TypeName = "varchar(50)")]
	[JsonPropertyName("name")]
	public required string Name { get; set; }

	[Required]
	[MaxLength(200)]
	[Column("description", TypeName = "varchar(200)")]
	[JsonPropertyName("description")]
	public required string Description { get; set; }

	[Required]
	[Column("content")]
	[JsonPropertyName("content")]
	public required string Content { get; set; }

	[Column("version")]
	[JsonPropertyName("version")]
	public int Version { get; set; } = 1;

	[MaxLength(2048)]
	[Column("source_url", TypeName = "varchar(2048)")]
	[JsonPropertyName("source_url")]
	public string? SourceUrl { get; set; }

	[Column("is_enabled")]
	[JsonPropertyName("is_enabled")]
	public bool IsEnabled { get; set; } = true;

	[Column("agent_id")]
	[JsonPropertyName("agent_id")]
	public long AgentId { get; set; }

	[Column("created_at")]
	[JsonPropertyName("created_at")]
	public DateTimeOffset CreatedAt { get; set; }

	[Column("updated_at")]
	[JsonPropertyName("updated_at")]
	public DateTimeOffset UpdatedAt { get; set; }

	public AgentEntity Agent { get; set; } = null!;
}

public class SkillEntityConfiguration : IEntityTypeConfiguration<SkillEntity> {
	public void Configure(EntityTypeBuilder<SkillEntity> builder) {
		builder.HasIndex(e => new { e.AgentId, e.Name }).IsUnique();

		builder.HasOne(e => e.Agent)
			   .WithMany(a => a.Skills)
			   .HasForeignKey(e => e.AgentId)
			   .OnDelete(DeleteBehavior.Cascade);
	}
}
