using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace MessageBridge.Infrastructure.Persistence;

public sealed class MessageProcessingRecordConfiguration : IEntityTypeConfiguration<MessageProcessingRecord>
{
    public void Configure(EntityTypeBuilder<MessageProcessingRecord> builder)
    {
        builder.ToTable("message_processing_history");

        builder.HasKey(record => record.Id);

        builder.Property(record => record.Id)
            .HasColumnName("id")
            .ValueGeneratedNever();

        builder.Property(record => record.MessageId)
            .HasColumnName("message_id")
            .IsRequired();

        builder.Property(record => record.MessageType)
            .HasColumnName("message_type")
            .IsRequired();

        builder.Property(record => record.Status)
            .HasColumnName("status")
            .HasConversion<string>()
            .IsRequired();

        builder.Property(record => record.PayloadHash)
            .HasColumnName("payload_hash")
            .IsRequired();

        builder.Property(record => record.Provider)
            .HasColumnName("provider")
            .IsRequired();

        builder.Property(record => record.ProviderMetadata)
            .HasColumnName("provider_metadata")
            .HasColumnType("jsonb")
            .IsRequired();

        builder.Property(record => record.FailureReason)
            .HasColumnName("failure_reason");

        builder.Property(record => record.AttemptCount)
            .HasColumnName("attempt_count")
            .IsRequired();

        builder.Property(record => record.CreatedAt)
            .HasColumnName("created_at")
            .IsRequired();

        builder.Property(record => record.UpdatedAt)
            .HasColumnName("updated_at")
            .IsRequired();

        builder.Property(record => record.ProcessedAt)
            .HasColumnName("processed_at");

        builder.HasIndex(record => new { record.MessageId, record.MessageType })
            .IsUnique();

        builder.HasIndex(record => record.Status);
        builder.HasIndex(record => record.CreatedAt);
    }
}
