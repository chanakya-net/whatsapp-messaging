using Microsoft.EntityFrameworkCore;

namespace MessageBridge.Infrastructure.Persistence;

public sealed class MessageBridgeDbContext(DbContextOptions<MessageBridgeDbContext> options) : DbContext(options)
{
    public DbSet<MessageProcessingRecord> MessageProcessingRecords => Set<MessageProcessingRecord>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.ApplyConfiguration(new MessageProcessingRecordConfiguration());
    }
}
