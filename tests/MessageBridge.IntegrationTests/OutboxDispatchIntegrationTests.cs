using FluentAssertions;
using MessageBridge.IntegrationTests.Fixtures;
using MessageBridge.Publisher.EntityFrameworkCore;
using MessageBridge.Publisher.EntityFrameworkCore.Outbox;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace MessageBridge.IntegrationTests;

[Collection(IntegrationTestCollection.Name)]
public sealed class OutboxDispatchIntegrationTests(IntegrationEnvironmentFixture fixture)
{
    [Fact]
    public async Task Committed_transaction_is_invisible_until_commit_then_dispatches_and_marks_published()
    {
        await using var scenario = await OutboxDispatchScenario.CreateAsync(fixture, failFirst: false);
        var marker = new OutboxBusinessMarker { Id = Guid.NewGuid(), Value = "committed" };
        var message = CreateMessage();

        await using var context = scenario.CreateDbContext();
        await using var transaction = await context.Database.BeginTransactionAsync();
        context.BusinessMarkers.Add(marker);
        await new MessageBridgeOutboxWriter(context).WriteAsync(message);
        await context.SaveChangesAsync();
        await scenario.StartDispatcherAsync();

        await scenario.AssertNoDispatchAttemptsAsync();
        await scenario.AssertRowsInvisibleAsync(marker.Id, message.Id);
        await scenario.Probe.AssertNoMessagesAsync(message.MessageId);
        scenario.Transport.Attempts.Should().Be(0);

        await transaction.CommitAsync();
        await scenario.WaitForRowsAsync(marker.Id, message.Id);
        await scenario.WaitForAttemptAsync(1);
        (await scenario.GetOutboxAsync(message.Id)).PublishedAtUtc.Should().BeNull();

        scenario.Transport.AllowPublication();
        await scenario.Transport.WaitForSuccessfulPublicationAsync();
        var envelope = await scenario.Probe.WaitForMessageAsync(message.MessageId);
        envelope.ExchangeName.Should().Be(message.ExchangeName);
        envelope.RoutingKey.Should().Be(message.RoutingKey);
        (await scenario.GetOutboxAsync(message.Id)).PublishedAtUtc.Should().BeNull();

        scenario.Transport.AllowDispatcherCompletion();
        (await scenario.WaitForPublishedAsync(message.Id)).PublishedAtUtc.Should().NotBeNull();
    }

    [Fact]
    public async Task Transient_publish_failure_retries_then_dispatches_the_row_once()
    {
        await using var scenario = await OutboxDispatchScenario.CreateAsync(fixture, failFirst: true);
        var marker = new OutboxBusinessMarker { Id = Guid.NewGuid(), Value = "retry" };
        var message = CreateMessage();

        await using (var context = scenario.CreateDbContext())
        {
            await using var transaction = await context.Database.BeginTransactionAsync();
            context.BusinessMarkers.Add(marker);
            await new MessageBridgeOutboxWriter(context).WriteAsync(message);
            await context.SaveChangesAsync();
            await transaction.CommitAsync();
        }

        await scenario.StartDispatcherAsync();
        await scenario.Transport.WaitForFirstFailureAsync();
        await scenario.WaitForAttemptAsync(2);
        (await scenario.GetOutboxAsync(message.Id)).PublishedAtUtc.Should().BeNull();
        await scenario.Probe.AssertNoMessagesAsync(message.MessageId);

        scenario.Transport.AllowPublication();
        await scenario.Transport.WaitForSuccessfulPublicationAsync();
        await scenario.Probe.WaitForMessageAsync(message.MessageId);
        (await scenario.GetOutboxAsync(message.Id)).PublishedAtUtc.Should().BeNull();

        scenario.Transport.AllowDispatcherCompletion();
        (await scenario.WaitForPublishedAsync(message.Id)).PublishedAtUtc.Should().NotBeNull();
        await scenario.AssertNoDuplicateDispatchAsync(message.MessageId, expectedAttempts: 2);
    }

    private static MessageBridgeOutboxMessage CreateMessage() => new()
    {
        Id = $"outbox-{Guid.NewGuid():N}",
        MessageId = $"message-{Guid.NewGuid():N}",
        CorrelationId = $"correlation-{Guid.NewGuid():N}",
        ExchangeName = "messagebridge.integration",
        RoutingKey = "outbox.dispatch",
        Headers = """{"content-type":"application/json"}""",
        Payload = [1, 2, 3, 4],
        CreatedAtUtc = DateTime.UtcNow,
    };
}

internal sealed class OutboxTestDbContext(DbContextOptions<OutboxTestDbContext> options)
    : DbContext(options)
{
    public DbSet<OutboxBusinessMarker> BusinessMarkers => Set<OutboxBusinessMarker>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<OutboxBusinessMarker>(builder =>
        {
            builder.ToTable("OutboxBusinessMarkers");
            builder.HasKey(marker => marker.Id);
            builder.Property(marker => marker.Value).IsRequired();
        });
        modelBuilder.ConfigureMessageBridgeOutbox();
    }
}

internal sealed class OutboxBusinessMarker
{
    public Guid Id { get; set; }
    public string Value { get; set; } = string.Empty;
}
