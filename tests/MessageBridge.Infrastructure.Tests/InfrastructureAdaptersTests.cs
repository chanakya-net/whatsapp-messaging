using ErrorOr;
using MessageBridge.Application.Persistence;
using MessageBridge.Domain.Processing;
using MessageBridge.Infrastructure;
using MessageBridge.Infrastructure.Messaging.Processing;
using MessageBridge.Infrastructure.Messaging;
using MessageBridge.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Npgsql;
using Shouldly;

namespace MessageBridge.Infrastructure.Tests;

[Trait("Category", "Unit")]
public sealed class InfrastructureAdaptersTests
{
    [Fact]
    public async Task ProcessingCoordinator_completes_new_message_after_dispatch()
    {
        var store = new RecordingProcessingStore();
        var coordinator = new MessageProcessingCoordinator(store);
        var cancellation = new CancellationTokenSource().Token;
        var dispatched = false;

        var result = await coordinator.ProcessAsync(
            "message-001", "SendWhatsAppMessageCommand", "hash", "placeholder",
            new Dictionary<string, string?> { ["tenant"] = "tenant-1" },
            token =>
            {
                token.ShouldBe(cancellation);
                dispatched = true;
                return Task.CompletedTask;
            }, cancellation);

        result.ShouldBeTrue();
        dispatched.ShouldBeTrue();
        store.Statuses.ShouldBe([ProcessingStatus.Processing, ProcessingStatus.Completed]);
    }

    [Fact]
    public async Task ProcessingCoordinator_skips_completed_duplicate()
    {
        var store = new RecordingProcessingStore
        {
            CreateResult = DuplicateResult(ProcessingStatus.Completed)
        };
        var coordinator = new MessageProcessingCoordinator(store);
        var dispatched = false;

        var result = await coordinator.ProcessAsync(
            "message-duplicate", "SendEmailConfirmationCommand", "hash", "placeholder",
            new Dictionary<string, string?>(),
            _ =>
            {
                dispatched = true;
                return Task.CompletedTask;
            });

        result.ShouldBeFalse();
        dispatched.ShouldBeFalse();
        store.Statuses.ShouldBeEmpty();
    }

    [Fact]
    public async Task ProcessingCoordinator_marks_dispatch_failure_with_sanitized_reason()
    {
        var store = new RecordingProcessingStore();
        var coordinator = new MessageProcessingCoordinator(store);

        var exception = await Should.ThrowAsync<InvalidOperationException>(() =>
            coordinator.ProcessAsync(
                "message-failure", "SendEmailConfirmationCommand", "hash", "placeholder",
                new Dictionary<string, string?>(),
                _ => throw new InvalidOperationException(
                    "token=super_secret password=hunter2 phone=+1 (415) 555-2671")));

        exception.Message.ShouldContain("super_secret");
        store.Statuses.ShouldBe([ProcessingStatus.Processing, ProcessingStatus.Failed]);
        (store.FailureReason ?? string.Empty).ShouldNotContain("hunter2");
        (store.FailureReason ?? string.Empty).ShouldContain("*******2671");
    }

    [Fact]
    public void DbContext_model_contains_history_table_and_indexes()
    {
        using var context = CreateContext();
        var entity = context.Model.FindEntityType(typeof(MessageProcessingRecord));

        entity.ShouldNotBeNull();
        entity!.GetTableName().ShouldBe("message_processing_history");
        entity.FindProperty(nameof(MessageProcessingRecord.ProviderMetadata))!
            .GetColumnType().ShouldBe("jsonb");
        entity.GetIndexes().ShouldContain(index => index.IsUnique &&
            index.Properties.Select(property => property.Name).SequenceEqual(
                new[] { nameof(MessageProcessingRecord.MessageId), nameof(MessageProcessingRecord.MessageType) }));
    }

    [Fact]
    public void DbContextFactory_uses_Database_contract_without_database_access()
    {
        var settings = new Dictionary<string, string?>
        {
            ["Database__Host"] = "unreachable.invalid",
            ["Database__Port"] = "5544",
            ["Database__Database"] = "bridge",
            ["Database__Username"] = "user",
            ["Database__Password"] = "p;a=s\"word",
            ["Database__UseEntraAuth"] = "false",
            ["Database__MaxPoolSize"] = "19"
        };
        var previous = settings.Keys.ToDictionary(
            key => key,
            Environment.GetEnvironmentVariable);
        try
        {
            foreach (var setting in settings)
                Environment.SetEnvironmentVariable(setting.Key, setting.Value);

            using var context = new MessageBridgeDbContextFactory().CreateDbContext([]);
            var builder = new NpgsqlConnectionStringBuilder(
                context.Database.GetDbConnection().ConnectionString);

            (context.Database.ProviderName ?? string.Empty).ShouldContain("Npgsql");
            builder.Host.ShouldBe("unreachable.invalid");
            builder.Port.ShouldBe(5544);
            builder.Password.ShouldBeNull();
            builder.MaxPoolSize.ShouldBe(19);
        }
        finally
        {
            foreach (var setting in previous)
                Environment.SetEnvironmentVariable(setting.Key, setting.Value);
        }
    }

    [Fact]
    public async Task LegacyStoreAdapter_is_registered_and_returns_success()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Database:Host"] = "unit-test",
                ["Database:Database"] = "bridge",
                ["Database:Username"] = "user",
                ["Database:Password"] = "password",
                ["RabbitMq:ConnectionString"] = "amqp://guest:guest@localhost"
            })
            .Build();
        var services = new ServiceCollection();
        services.AddMessageBridgeProcessingStore(configuration);
        services.AddMessageBridgeMassTransit(configuration);

        await using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();
        var adapter = scope.ServiceProvider
            .GetRequiredService<MessageBridge.Application.Abstractions.IMessageProcessingStore>();

        (await adapter.RecordMessageSentAsync("message-001", "tenant-1"))
            .IsError.ShouldBeFalse();
    }

    [Fact]
    public void ProviderOptionsValidator_reports_each_missing_provider_name()
    {
        var result = new MessageBridge.Infrastructure.Providers.ProviderOptionsValidator()
            .Validate(Options.DefaultName, new MessageBridge.Infrastructure.Providers.ProviderOptions
            {
                WhatsAppProviderName = " ",
                EmailProviderName = ""
            });

        result.Failed.ShouldBeTrue();
        result.Failures.Count().ShouldBe(2);
    }

    [Fact]
    public void ProcessingRecord_exposes_all_persisted_fields()
    {
        var now = DateTimeOffset.UtcNow;
        using var metadata = System.Text.Json.JsonDocument.Parse("{\"tenant\":\"tenant-1\"}");
        var record = new MessageProcessingRecord
        {
            Id = Guid.NewGuid(),
            MessageId = "message-001",
            MessageType = "SendWhatsAppMessageCommand",
            Status = ProcessingStatus.Completed,
            PayloadHash = "hash",
            Provider = "placeholder",
            ProviderMetadata = metadata,
            FailureReason = null,
            AttemptCount = 1,
            CreatedAt = now,
            UpdatedAt = now,
            ProcessedAt = now
        };

        record.MessageId.ShouldBe("message-001");
        record.Status.ShouldBe(ProcessingStatus.Completed);
        record.ProviderMetadata.RootElement.GetProperty("tenant").GetString()
            .ShouldBe("tenant-1");
        record.ProcessedAt.ShouldBe(now);
    }

    private static MessageBridgeDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<MessageBridgeDbContext>()
            .UseNpgsql("Host=unit-test;Database=bridge")
            .Options;
        return new MessageBridgeDbContext(options);
    }

    private static CreateMessageProcessingResult DuplicateResult(ProcessingStatus status)
    {
        var now = DateTimeOffset.UtcNow;
        return new CreateMessageProcessingResult(
            CreateMessageProcessingOutcome.Duplicate,
            new MessageProcessingSnapshot(
                Guid.NewGuid(), "message-duplicate", "message", status,
                "hash", "provider", new Dictionary<string, string?>(), null, 1,
                now, now, now));
    }

    private sealed class RecordingProcessingStore : IMessageProcessingStore
    {
        public CreateMessageProcessingResult? CreateResult { get; init; }
        public List<ProcessingStatus> Statuses { get; } = [];
        public string? FailureReason { get; private set; }

        public Task<CreateMessageProcessingResult> CreateAsync(
            CreateMessageProcessingRequest request,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(CreateResult ?? new CreateMessageProcessingResult(
                CreateMessageProcessingOutcome.Created,
                new MessageProcessingSnapshot(
                    Guid.NewGuid(), request.MessageId, request.MessageType,
                    ProcessingStatus.Received, request.PayloadHash, request.Provider,
                    new Dictionary<string, string?>(request.ProviderMetadata), null, 1,
                    DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, null)));

        public Task<MessageProcessingSnapshot?> GetAsync(
            string messageId,
            string messageType,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<MessageProcessingSnapshot?>(null);

        public Task<MessageProcessingSnapshot> UpdateStatusAsync(
            string messageId,
            string messageType,
            ProcessingStatus status,
            string? failureReason = null,
            CancellationToken cancellationToken = default)
        {
            Statuses.Add(status);
            FailureReason = failureReason;
            var now = DateTimeOffset.UtcNow;
            return Task.FromResult(new MessageProcessingSnapshot(
                Guid.NewGuid(), messageId, messageType, status, string.Empty, string.Empty,
                new Dictionary<string, string?>(), failureReason, 1, now, now, now));
        }
    }
}
