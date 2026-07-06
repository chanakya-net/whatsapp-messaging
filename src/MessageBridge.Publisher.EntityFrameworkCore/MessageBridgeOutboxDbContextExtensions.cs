using MessageBridge.Publisher.EntityFrameworkCore.Outbox;
using Microsoft.EntityFrameworkCore;

namespace MessageBridge.Publisher.EntityFrameworkCore;

public static class MessageBridgeOutboxDbContextExtensions
{
    public static void ConfigureMessageBridgeOutbox(this ModelBuilder modelBuilder)
    {
        modelBuilder.ApplyConfiguration(new MessageBridgeOutboxMessageConfiguration());
    }
}
