using MessageBridge.Publisher.EntityFrameworkCore.Outbox;
using Microsoft.EntityFrameworkCore;

namespace MessageBridge.Publisher.EntityFrameworkCore.Tests;

public class MessageBridgeOutboxStorageTests
{
    [Fact]
    public void OutboxMessage_EntityConfiguration_AppliesCorrectly()
    {
        var optionsBuilder = new DbContextOptionsBuilder<TestDbContext>()
            .UseInMemoryDatabase(nameof(OutboxMessage_EntityConfiguration_AppliesCorrectly));

        using var context = new TestDbContext(optionsBuilder.Options);

        var entityType = context.Model.FindEntityType(typeof(MessageBridgeOutboxMessage));
        entityType.ShouldNotBeNull();
        entityType.GetTableName().ShouldBe("MessageBridgeOutboxMessages");

        var keyProperty = entityType.FindProperty("Id");
        keyProperty.ShouldNotBeNull();
        keyProperty.IsKey().ShouldBeTrue();

        var messageIdProperty = entityType.FindProperty("MessageId");
        messageIdProperty.ShouldNotBeNull();

        var payloadProperty = entityType.FindProperty("Payload");
        payloadProperty.ShouldNotBeNull();

        var createdAtProperty = entityType.FindProperty("CreatedAtUtc");
        createdAtProperty.ShouldNotBeNull();
    }

    [Fact]
    public void OutboxMessage_CanBeCreatedAndSaved()
    {
        var optionsBuilder = new DbContextOptionsBuilder<TestDbContext>()
            .UseInMemoryDatabase(nameof(OutboxMessage_CanBeCreatedAndSaved));

        using (var context = new TestDbContext(optionsBuilder.Options))
        {
            var outboxMessage = new MessageBridgeOutboxMessage
            {
                Id = "out-1",
                MessageId = "msg-1",
                CorrelationId = "corr-1",
                ExchangeName = "test.exchange",
                RoutingKey = "test.routing.key",
                Headers = "{}",
                Payload = new byte[] { 1, 2, 3 },
                CreatedAtUtc = DateTime.UtcNow,
            };

            context.OutboxMessages.Add(outboxMessage);
            context.SaveChanges();
        }

        using (var context = new TestDbContext(optionsBuilder.Options))
        {
            var retrieved = context.OutboxMessages.FirstOrDefault(x => x.Id == "out-1");
            retrieved.ShouldNotBeNull();
            retrieved.MessageId.ShouldBe("msg-1");
            retrieved.CorrelationId.ShouldBe("corr-1");
            retrieved.ExchangeName.ShouldBe("test.exchange");
            retrieved.RoutingKey.ShouldBe("test.routing.key");
            retrieved.Payload.ShouldBe(new byte[] { 1, 2, 3 });
        }
    }

    [Fact]
    public void OutboxMessage_PublishedAtUtc_IsOptional()
    {
        var optionsBuilder = new DbContextOptionsBuilder<TestDbContext>()
            .UseInMemoryDatabase(nameof(OutboxMessage_PublishedAtUtc_IsOptional));

        using (var context = new TestDbContext(optionsBuilder.Options))
        {
            var outboxMessage = new MessageBridgeOutboxMessage
            {
                Id = "out-2",
                MessageId = "msg-2",
                CorrelationId = "corr-2",
                ExchangeName = "test.exchange",
                RoutingKey = "test.routing.key",
                Headers = "{}",
                Payload = new byte[] { 1, 2, 3 },
                CreatedAtUtc = DateTime.UtcNow,
                PublishedAtUtc = null,
            };

            context.OutboxMessages.Add(outboxMessage);
            context.SaveChanges();
        }

        using (var context = new TestDbContext(optionsBuilder.Options))
        {
            var retrieved = context.OutboxMessages.FirstOrDefault(x => x.Id == "out-2");
            retrieved.ShouldNotBeNull();
            retrieved.PublishedAtUtc.ShouldBeNull();
        }
    }

    [Fact]
    public async Task OutboxWriter_WritesMessageToDatabase()
    {
        var optionsBuilder = new DbContextOptionsBuilder<TestDbContext>()
            .UseInMemoryDatabase(nameof(OutboxWriter_WritesMessageToDatabase));

        using (var context = new TestDbContext(optionsBuilder.Options))
        {
            var writer = new MessageBridgeOutboxWriter(context);
            var outboxMessage = new MessageBridgeOutboxMessage
            {
                Id = "out-4",
                MessageId = "msg-4",
                CorrelationId = "corr-4",
                ExchangeName = "test.exchange",
                RoutingKey = "test.routing.key",
                Headers = @"{""content-type"":""application/json""}",
                Payload = new byte[] { 4, 5, 6 },
                CreatedAtUtc = DateTime.UtcNow,
            };

            await writer.WriteAsync(outboxMessage);
            await context.SaveChangesAsync();
        }

        using (var context = new TestDbContext(optionsBuilder.Options))
        {
            var retrieved = context.OutboxMessages.FirstOrDefault(x => x.Id == "out-4");
            retrieved.ShouldNotBeNull();
            retrieved.MessageId.ShouldBe("msg-4");
            retrieved.CorrelationId.ShouldBe("corr-4");
            retrieved.ExchangeName.ShouldBe("test.exchange");
            retrieved.RoutingKey.ShouldBe("test.routing.key");
            retrieved.Payload.ShouldBe(new byte[] { 4, 5, 6 });
            retrieved.PublishedAtUtc.ShouldBeNull();
        }
    }

    [Fact]
    public void DbContextExtension_ConfiguresOutboxTable()
    {
        var optionsBuilder = new DbContextOptionsBuilder<TestDbContextWithExtension>()
            .UseInMemoryDatabase(nameof(DbContextExtension_ConfiguresOutboxTable));

        using var context = new TestDbContextWithExtension(optionsBuilder.Options);

        var outboxMessage = new MessageBridgeOutboxMessage
        {
            Id = "out-3",
            MessageId = "msg-3",
            CorrelationId = "corr-3",
            ExchangeName = "test.exchange",
            RoutingKey = "test.routing.key",
            Headers = "{}",
            Payload = new byte[] { 1, 2, 3 },
            CreatedAtUtc = DateTime.UtcNow,
        };

        context.OutboxMessages.Add(outboxMessage);
        context.SaveChanges();

        var retrieved = context.OutboxMessages.FirstOrDefault(x => x.Id == "out-3");
        retrieved.ShouldNotBeNull();
        retrieved.MessageId.ShouldBe("msg-3");
    }

    private sealed class TestDbContext : DbContext
    {
        public DbSet<MessageBridgeOutboxMessage> OutboxMessages { get; set; } = null!;

        public TestDbContext(DbContextOptions options) : base(options)
        {
        }

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.ApplyConfiguration(new MessageBridgeOutboxMessageConfiguration());
        }
    }

    private sealed class TestDbContextWithExtension : DbContext
    {
        public DbSet<MessageBridgeOutboxMessage> OutboxMessages { get; set; } = null!;

        public TestDbContextWithExtension(DbContextOptions options) : base(options)
        {
        }

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.ConfigureMessageBridgeOutbox();
        }
    }
}
