using MessageBridge.Publisher.EntityFrameworkCore.Outbox;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace MessageBridge.Publisher.EntityFrameworkCore.Tests;

[Trait("Category", "Unit")]
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
    public async Task WriteAsync_StagesMessageUntilCallerSavesChanges()
    {
        var options = CreateOptions(nameof(WriteAsync_StagesMessageUntilCallerSavesChanges));
        await using var context = new TestDbContext(options);
        var writer = new MessageBridgeOutboxWriter(context);
        var message = CreateMessage("id-1", "message-1");

        await writer.WriteAsync(message);

        context.Entry(message).State.ShouldBe(EntityState.Added);
        await using var verifyContext = new TestDbContext(options);
        (await verifyContext.OutboxMessages.CountAsync()).ShouldBe(0);
    }

    [Fact]
    public async Task WriteAsync_PreservesCallerTransaction()
    {
        var options = CreateOptions(nameof(WriteAsync_PreservesCallerTransaction));
        await using (var context = new TestDbContext(options))
        {
            await using var transaction = await context.Database.BeginTransactionAsync();
            await new MessageBridgeOutboxWriter(context).WriteAsync(CreateMessage("id-2", "message-2"));
            await context.SaveChangesAsync();
            await transaction.CommitAsync();
        }

        await using var verifyContext = new TestDbContext(options);
        (await verifyContext.OutboxMessages.SingleAsync()).MessageId.ShouldBe("message-2");
    }

    [Fact]
    public void OutboxConfiguration_RequiresUniqueMessageId()
    {
        using var context = new TestDbContext(CreateOptions(nameof(OutboxConfiguration_RequiresUniqueMessageId)));

        var messageIdIndex = context.Model.FindEntityType(typeof(MessageBridgeOutboxMessage))!
            .GetIndexes().Single(index =>
                index.Properties.Count == 1 &&
                index.Properties[0].Name == nameof(MessageBridgeOutboxMessage.MessageId));

        messageIdIndex.IsUnique.ShouldBeTrue();
    }

    [Fact]
    public async Task SaveChangesAsync_WithDuplicatePrimaryKey_Throws()
    {
        var options = CreateOptions(nameof(SaveChangesAsync_WithDuplicatePrimaryKey_Throws));
        await using (var context = new TestDbContext(options))
        {
            context.OutboxMessages.Add(CreateMessage("id-3", "message-3"));
            await context.SaveChangesAsync();
        }

        await using var duplicateContext = new TestDbContext(options);
        duplicateContext.OutboxMessages.Add(CreateMessage("id-3", "message-4"));

        await Should.ThrowAsync<ArgumentException>(() => duplicateContext.SaveChangesAsync());
    }

    [Fact]
    public async Task SaveChangesAsync_WithCancelledToken_ThrowsAndDoesNotPersist()
    {
        var options = CreateOptions(nameof(SaveChangesAsync_WithCancelledToken_ThrowsAndDoesNotPersist));
        await using var context = new TestDbContext(options);
        context.OutboxMessages.Add(CreateMessage("id-4", "message-4"));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Should.ThrowAsync<OperationCanceledException>(() => context.SaveChangesAsync(cancellation.Token));

        await using var verifyContext = new TestDbContext(options);
        (await verifyContext.OutboxMessages.CountAsync()).ShouldBe(0);
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

    private static DbContextOptions<TestDbContext> CreateOptions(string name) =>
        new DbContextOptionsBuilder<TestDbContext>()
            .UseInMemoryDatabase(name)
            .ConfigureWarnings(warnings => warnings.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options;

    private static MessageBridgeOutboxMessage CreateMessage(string id, string messageId) => new()
    {
        Id = id,
        MessageId = messageId,
        CorrelationId = "correlation",
        ExchangeName = "exchange",
        RoutingKey = "routing",
        Headers = "{}",
        Payload = [1],
        CreatedAtUtc = DateTime.UtcNow,
    };

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
