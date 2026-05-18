using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using System.Text.Json.Serialization;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Lis.Persistence.Entities;

[Table("tool_digest")]
public sealed class ToolDigestEntity {
	[Key]
	[Column("id")]
	[JsonPropertyName("id")]
	public long Id { get; set; }

	[Column("session_id")]
	[JsonPropertyName("session_id")]
	public long SessionId { get; set; }

	[Column("message_id")]
	[JsonPropertyName("message_id")]
	public long MessageId { get; set; }

	[Required]
	[Column("digest")]
	[JsonPropertyName("digest")]
	public required string Digest { get; set; }

	[Column("created_at")]
	[JsonPropertyName("created_at")]
	public DateTimeOffset CreatedAt { get; set; }

	public SessionEntity Session { get; set; } = null!;
	public MessageEntity Message { get; set; } = null!;
}

public class ToolDigestEntityConfiguration : IEntityTypeConfiguration<ToolDigestEntity> {
	public void Configure(EntityTypeBuilder<ToolDigestEntity> builder) {
		builder.HasIndex(e => e.SessionId);

		builder.HasOne(e => e.Session)
			   .WithMany()
			   .HasForeignKey(e => e.SessionId)
			   .OnDelete(DeleteBehavior.Cascade);

		builder.HasOne(e => e.Message)
			   .WithMany()
			   .HasForeignKey(e => e.MessageId)
			   .OnDelete(DeleteBehavior.Cascade);
	}
}
