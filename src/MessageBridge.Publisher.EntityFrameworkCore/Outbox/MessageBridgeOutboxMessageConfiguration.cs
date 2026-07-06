using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Microsoft.EntityFrameworkCore.Infrastructure;

namespace MessageBridge.Publisher.EntityFrameworkCore.Outbox;

public sealed class MessageBridgeOutboxMessageConfiguration : IEntityTypeConfiguration<MessageBridgeOutboxMessage>
{
    public void Configure(EntityTypeBuilder<MessageBridgeOutboxMessage> builder)
    {
        builder.ToTable("MessageBridgeOutboxMessages");

        builder.HasKey(x => x.Id);

        builder.Property(x => x.Id).IsRequired();
        builder.Property(x => x.MessageId).IsRequired();
        builder.Property(x => x.CorrelationId).IsRequired();
        builder.Property(x => x.ExchangeName).IsRequired();
        builder.Property(x => x.RoutingKey).IsRequired();
        builder.Property(x => x.Headers).IsRequired();
        builder.Property(x => x.Payload).IsRequired();
        builder.Property(x => x.CreatedAtUtc).IsRequired();
        builder.Property(x => x.PublishedAtUtc);

        builder.HasIndex(x => x.MessageId).IsUnique();
        builder.HasIndex(x => new { x.PublishedAtUtc, x.CreatedAtUtc });
        builder.HasIndex(x => x.CreatedAtUtc);
    }
}
