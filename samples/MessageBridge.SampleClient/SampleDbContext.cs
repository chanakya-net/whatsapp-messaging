using MessageBridge.Publisher.EntityFrameworkCore;
using MessageBridge.Publisher.EntityFrameworkCore.Outbox;
using Microsoft.EntityFrameworkCore;

namespace MessageBridge.SampleClient;

public sealed class SampleDbContext : DbContext
{
    public DbSet<MessageBridgeOutboxMessage> MessageBridgeOutbox { get; set; } = null!;

    public SampleDbContext(DbContextOptions<SampleDbContext> options) : base(options)
    {
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        modelBuilder.ConfigureMessageBridgeOutbox();
    }
}
