using FluentAssertions;
using MassTransit;
using MessageBridge.Application.Persistence;
using MessageBridge.Contracts.V1;
using MessageBridge.Domain.Processing;
using MessageBridge.Infrastructure.Persistence;
using MessageBridge.IntegrationTests.Fixtures;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MessageBridge.IntegrationTests;

[Collection(IntegrationTestCollection.Name)]
public sealed class ProcessingTrackingIntegrationTests(IntegrationEnvironmentFixture fixture)
{
    private readonly IntegrationEnvironmentFixture _fixture = fixture;

    [Fact]
    public async Task PublishMessage_CreatesProcessingRecord_ForOutboxTracking()
    {
        await using var scenario = await ProcessingTrackingScenario.CreateAsync(_fixture);
        var bus = scenario.Services.GetRequiredService<IBus>();
        var store = scenario.Services.GetRequiredService<IMessageProcessingStore>();

        var cmd = new SendWhatsAppMessageCommand
        {
            MessageId = $"outbox-msg-{Guid.NewGuid():N}",
            TenantId = "test-tenant",
            RecipientPhoneNumber = "+11234567890",
            TemplateName = "test",
            TemplateParameters = { ["body"] = "outbox test" }
        };

        var hash = GetPayloadHash(cmd);

        // Create outbox record
        var createReq = new CreateMessageProcessingRequest(
            cmd.MessageId,
            nameof(SendWhatsAppMessageCommand),
            hash,
            "masstransit",
            new Dictionary<string, string?> { ["outbox"] = "true", ["attempt"] = "1" });

        var createRes = await store.CreateAsync(createReq);
        createRes.Outcome.Should().Be(CreateMessageProcessingOutcome.Created);

        // Publish message
        await bus.Publish(cmd);

        // Verify record persisted (poll until found or timeout)
        await IntegrationEnvironmentFixture.PollUntilAssertedAsync(
            async () => await store.GetAsync(cmd.MessageId, nameof(SendWhatsAppMessageCommand)) != null,
            "Published outbox record was not persisted.");

        var record = await store.GetAsync(cmd.MessageId, nameof(SendWhatsAppMessageCommand));
        record.Should().NotBeNull();
        record!.Provider.Should().Be("masstransit");
        record.ProviderMetadata["outbox"].Should().Be("true");
    }

    [Fact]
    public async Task DuplicateOutboxEntry_ReturnsExistingRecord_PreventingDoublePublish()
    {
        await using var scenario = await ProcessingTrackingScenario.CreateAsync(_fixture);
        var store = scenario.Services.GetRequiredService<IMessageProcessingStore>();

        var msgId = $"dup-outbox-{Guid.NewGuid():N}";
        var hash = "hash-duplicate-outbox";

        var req = new CreateMessageProcessingRequest(
            msgId,
            "whatsapp.send",
            hash,
            "masstransit",
            new Dictionary<string, string?> { ["duplicate"] = "true" });

        var first = await store.CreateAsync(req);
        var second = await store.CreateAsync(req);

        first.Outcome.Should().Be(CreateMessageProcessingOutcome.Created);
        second.Outcome.Should().Be(CreateMessageProcessingOutcome.Duplicate);
        first.Record.Id.Should().Be(second.Record.Id);
    }

    [Fact]
    public async Task OutboxRecord_UpdatesStatusAfterDispatch()
    {
        await using var scenario = await ProcessingTrackingScenario.CreateAsync(_fixture);
        var store = scenario.Services.GetRequiredService<IMessageProcessingStore>();

        var msgId = $"status-outbox-{Guid.NewGuid():N}";
        var msgType = "email.confirm";

        var req = new CreateMessageProcessingRequest(
            msgId,
            msgType,
            "hash-status",
            "masstransit",
            new Dictionary<string, string?> { ["status_test"] = "true" });

        var created = await store.CreateAsync(req);
        created.Record.Status.Should().Be(ProcessingStatus.Received);

        // Simulate dispatch completion
        var updated = await store.UpdateStatusAsync(
            msgId,
            msgType,
            ProcessingStatus.Completed);

        updated.Status.Should().Be(ProcessingStatus.Completed);
        updated.ProcessedAt.Should().NotBeNull();

        // Verify persistence
        var final = await store.GetAsync(msgId, msgType);
        final!.Status.Should().Be(ProcessingStatus.Completed);
    }

    private static string GetPayloadHash<T>(T msg)
    {
        var json = System.Text.Json.JsonSerializer.Serialize(msg);
        var bytes = System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(json));
        return Convert.ToHexString(bytes);
    }
}

internal sealed class ProcessingTrackingScenario : IAsyncDisposable
{
    private readonly IntegrationEnvironmentFixture _fixture;
    private readonly MessageBridgeDbContext _dbContext;
    private readonly string _databaseName;
    private readonly ServiceProvider _serviceProvider;
    private readonly AsyncServiceScope _scope;
    private IBusControl? _bus;

    private ProcessingTrackingScenario(
        IntegrationEnvironmentFixture fixture,
        MessageBridgeDbContext dbContext,
        string databaseName,
        ServiceProvider serviceProvider,
        AsyncServiceScope scope)
    {
        _fixture = fixture;
        _dbContext = dbContext;
        _databaseName = databaseName;
        _serviceProvider = serviceProvider;
        _scope = scope;
    }

    public IServiceProvider Services => _scope.ServiceProvider;

    public static async Task<ProcessingTrackingScenario> CreateAsync(
        IntegrationEnvironmentFixture fixture)
    {
        var (dbContext, databaseName) = await fixture.CreateMigratedDatabaseAsync();
        var serviceProvider = BuildServices(fixture, dbContext);
        var scope = serviceProvider.CreateAsyncScope();
        var scenario = new ProcessingTrackingScenario(
            fixture,
            dbContext,
            databaseName,
            serviceProvider,
            scope);

        try
        {
            scenario._bus = scope.ServiceProvider.GetRequiredService<IBus>() as IBusControl;
            await scenario._bus!.StartAsync(IntegrationEnvironmentFixture.AssertionTimeout);
            return scenario;
        }
        catch
        {
            try
            {
                await scenario.DisposeAsync();
            }
            catch
            {
                // Preserve the bus startup failure.
            }

            throw;
        }
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            if (_bus is not null)
            {
                await _bus.StopAsync(IntegrationEnvironmentFixture.AssertionTimeout);
            }
        }
        finally
        {
            await DisposeResourcesAsync();
        }
    }

    private async Task DisposeResourcesAsync()
    {
        try
        {
            await _scope.DisposeAsync();
        }
        finally
        {
            try
            {
                await _serviceProvider.DisposeAsync();
            }
            finally
            {
                try
                {
                    await _dbContext.DisposeAsync();
                }
                finally
                {
                    await _fixture.DropDatabaseAsync(_databaseName);
                }
            }
        }
    }

    private static ServiceProvider BuildServices(
        IntegrationEnvironmentFixture fixture,
        MessageBridgeDbContext dbContext)
    {
        var connectionString = dbContext.Database.GetConnectionString()
            ?? dbContext.Database.GetDbConnection().ConnectionString;
        var options = new DbContextOptionsBuilder<MessageBridgeDbContext>()
            .UseNpgsql(connectionString)
            .Options;
        var services = new ServiceCollection();

        services.AddScoped(_ => new MessageBridgeDbContext(options));
        services.AddScoped<IMessageProcessingStore, MessageProcessingStore>();
        services.AddMassTransit(bus =>
        {
            bus.SetEndpointNameFormatter(new KebabCaseEndpointNameFormatter(
                IntegrationEnvironmentFixture.CreateUniqueTopologyPrefix(),
                includeNamespace: false));
            bus.UsingRabbitMq((_, cfg) => cfg.Host(fixture.GetRabbitMqConnectionString()));
        });

        return services.BuildServiceProvider();
    }
}
