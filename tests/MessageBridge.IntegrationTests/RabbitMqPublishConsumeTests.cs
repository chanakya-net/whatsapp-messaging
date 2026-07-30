using System.Collections.Concurrent;
using ErrorOr;
using FluentAssertions;
using Google.Protobuf.WellKnownTypes;
using MassTransit;
using MessageBridge.Application.Abstractions;
using MessageBridge.Application.Handlers;
using MessageBridge.Application.Providers;
using MessageBridge.Contracts.V1;
using MessageBridge.Domain.Processing;
using MessageBridge.Infrastructure;
using MessageBridge.Infrastructure.Messaging;
using MessageBridge.Infrastructure.Persistence;
using MessageBridge.IntegrationTests.Fixtures;
using MessageBridge.IntegrationTests.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Wolverine;

namespace MessageBridge.IntegrationTests;

[Collection(IntegrationTestCollection.Name)]
public sealed class RabbitMqPublishConsumeTests(IntegrationEnvironmentFixture fixture)
{
    [Fact]
    public async Task PublishWhatsAppMessage_RoutesThroughProductionConsumerAndPersistsCompletedHistory()
    {
        await using var harness = await MessagingHarness.StartAsync(fixture);
        var requestedAt = DateTimeOffset.UtcNow;
        var command = new SendWhatsAppMessageCommand
        {
            MessageId = $"whatsapp-{Guid.NewGuid():N}",
            TenantId = "tenant-integration",
            RecipientPhoneNumber = "+14155552671",
            TemplateName = "welcome",
            TemplateLanguage = "en",
            TemplateParameters = { ["name"] = "Ada" },
            RequestedAtUtc = Timestamp.FromDateTimeOffset(requestedAt)
        };

        await harness.PublishAsync(command);
        var record = await harness.WaitForCompletedAsync(
            command.MessageId,
            nameof(SendWhatsAppMessageCommand));

        record.MessageId.Should().Be(command.MessageId);
        record.MessageType.Should().Be(nameof(SendWhatsAppMessageCommand));
        record.Status.Should().Be(ProcessingStatus.Completed);
        record.ProcessedAt.Should().NotBeNull();
        record.FailureReason.Should().BeNull();
        record.Provider.Should().Be("rabbitmq");
        record.ProviderMetadata.RootElement.GetProperty("transport").GetString()
            .Should().Be("masstransit");
        harness.WhatsAppMessages.Should().ContainSingle()
            .Which.MessageId.Should().Be(command.MessageId);
    }

    [Fact]
    public async Task PublishEmailConfirmation_RoutesThroughProductionConsumerAndPersistsCompletedHistory()
    {
        await using var harness = await MessagingHarness.StartAsync(fixture);
        var requestedAt = DateTimeOffset.UtcNow;
        var command = new SendEmailConfirmationCommand
        {
            MessageId = $"email-{Guid.NewGuid():N}",
            TenantId = "tenant-integration",
            RecipientEmail = "ada@example.com",
            RecipientName = "Ada",
            ConfirmationToken = "token-123",
            RequestedAtUtc = Timestamp.FromDateTimeOffset(requestedAt),
            ExpiresAtUtc = Timestamp.FromDateTimeOffset(requestedAt.AddHours(1))
        };

        await harness.PublishAsync(command);
        var record = await harness.WaitForCompletedAsync(
            command.MessageId,
            nameof(SendEmailConfirmationCommand));

        record.MessageId.Should().Be(command.MessageId);
        record.MessageType.Should().Be(nameof(SendEmailConfirmationCommand));
        record.Status.Should().Be(ProcessingStatus.Completed);
        record.ProcessedAt.Should().NotBeNull();
        record.FailureReason.Should().BeNull();
        record.Provider.Should().Be("rabbitmq");
        record.ProviderMetadata.RootElement.GetProperty("transport").GetString()
            .Should().Be("masstransit");
        harness.EmailConfirmations.Should().ContainSingle()
            .Which.MessageId.Should().Be(command.MessageId);
    }

    private sealed class MessagingHarness : IAsyncDisposable
    {
        private readonly IHost _host;
        private readonly MigratedDatabaseScenario _database;
        private readonly TrackingWhatsAppSender _whatsAppSender;
        private readonly TrackingEmailSender _emailSender;

        private MessagingHarness(
            IHost host,
            MigratedDatabaseScenario database,
            TrackingWhatsAppSender whatsAppSender,
            TrackingEmailSender emailSender)
        {
            _host = host;
            _database = database;
            _whatsAppSender = whatsAppSender;
            _emailSender = emailSender;
        }

        public IReadOnlyCollection<WhatsAppMessage> WhatsAppMessages =>
            _whatsAppSender.Messages;

        public IReadOnlyCollection<EmailConfirmation> EmailConfirmations =>
            _emailSender.Emails;

        public static async Task<MessagingHarness> StartAsync(
            IntegrationEnvironmentFixture fixture)
        {
            var database = await MigratedDatabaseScenario.CreateAsync(fixture);
            var connectionString = database.DbContext.Database.GetConnectionString()!;
            var settings = new Dictionary<string, string?>
            {
                ["ConnectionStrings:DefaultConnection"] = connectionString,
                ["MESSAGEBRIDGE_CONNECTION_STRING"] = connectionString,
                ["RabbitMq:ConnectionString"] = fixture.GetRabbitMqConnectionString(),
                ["MessageBridge:Topology:EnvironmentPrefix"] =
                    IntegrationEnvironmentFixture.CreateUniqueTopologyPrefix(),
                ["MessageBridge:ProcessingHistory:RecoveryEnabled"] = "false"
            };
            var whatsAppSender = new TrackingWhatsAppSender();
            var emailSender = new TrackingEmailSender();
            var builder = Host.CreateApplicationBuilder();
            builder.Configuration.AddInMemoryCollection(settings);
            ConfigureServices(
                builder.Services,
                builder.Configuration,
                whatsAppSender,
                emailSender);
            var host = builder.Build();

            try
            {
                await host.StartAsync();
                return new MessagingHarness(host, database, whatsAppSender, emailSender);
            }
            catch
            {
                host.Dispose();
                await database.DisposeAsync();
                throw;
            }
        }

        public Task PublishAsync<T>(T message)
            where T : class =>
            _host.Services.GetRequiredService<IPublishEndpoint>().Publish(message);

        public async Task<MessageProcessingRecord> WaitForCompletedAsync(
            string messageId,
            string messageType)
        {
            ProcessingStatus? lastStatus = null;

            try
            {
                return await IntegrationEnvironmentFixture.PollUntilAssertedAsync(
                    async () =>
                    {
                        await using var scope = _host.Services.CreateAsyncScope();
                        var dbContext = scope.ServiceProvider
                            .GetRequiredService<MessageBridgeDbContext>();
                        var record = await dbContext.MessageProcessingRecords
                            .AsNoTracking()
                            .SingleOrDefaultAsync(item =>
                                item.MessageId == messageId &&
                                item.MessageType == messageType);
                        lastStatus = record?.Status;
                        return record?.Status == ProcessingStatus.Completed ? record : null;
                    },
                    $"Expected {messageType}/{messageId} to complete; " +
                    $"last status={lastStatus?.ToString() ?? "not-persisted"}.");
            }
            catch (TimeoutException exception)
            {
                throw new TimeoutException(
                    $"Expected {messageType}/{messageId} to complete; " +
                    $"last status={lastStatus?.ToString() ?? "not-persisted"}.",
                    exception);
            }
        }

        public async ValueTask DisposeAsync()
        {
            await _host.StopAsync();
            _host.Dispose();
            await _database.DisposeAsync();
        }

        private static void ConfigureServices(
            IServiceCollection services,
            IConfiguration configuration,
            TrackingWhatsAppSender whatsAppSender,
            TrackingEmailSender emailSender)
        {
            services.AddOptions<MassTransitHostOptions>().Configure(options =>
            {
                options.WaitUntilStarted = true;
                options.StartTimeout = IntegrationEnvironmentFixture.AssertionTimeout;
                options.StopTimeout = IntegrationEnvironmentFixture.AssertionTimeout;
            });
            services.AddMessageBridgeMassTransit(configuration);
            services.AddMessageBridgeProcessingStore(configuration);
            services.AddSingleton<IWhatsAppMessageSender>(whatsAppSender);
            services.AddSingleton<IEmailConfirmationSender>(emailSender);
            services.AddSingleton<ITenantConfigurationProvider, ReadyTenantProvider>();
            services.AddSingleton<IProviderRateLimiter, ReadyRateLimiter>();
            services.AddWolverine(options =>
                options.Discovery.IncludeAssembly(typeof(SendWhatsAppMessageHandler).Assembly));
        }
    }

    private sealed class TrackingWhatsAppSender : IWhatsAppMessageSender
    {
        private readonly ConcurrentQueue<WhatsAppMessage> _messages = new();

        public IReadOnlyCollection<WhatsAppMessage> Messages => _messages.ToArray();

        public Task<ErrorOr<Success>> SendAsync(WhatsAppMessage message, string tenantId)
        {
            _messages.Enqueue(message);
            return Task.FromResult<ErrorOr<Success>>(new Success());
        }
    }

    private sealed class TrackingEmailSender : IEmailConfirmationSender
    {
        private readonly ConcurrentQueue<EmailConfirmation> _emails = new();

        public IReadOnlyCollection<EmailConfirmation> Emails => _emails.ToArray();

        public Task<ErrorOr<Success>> SendAsync(EmailConfirmation email, string tenantId)
        {
            _emails.Enqueue(email);
            return Task.FromResult<ErrorOr<Success>>(new Success());
        }
    }

    private sealed class ReadyTenantProvider : ITenantConfigurationProvider
    {
        public Task<ErrorOr<TenantConfiguration>> GetTenantConfigAsync(string tenantId) =>
            Task.FromResult<ErrorOr<TenantConfiguration>>(new TenantConfiguration(tenantId, true));
    }

    private sealed class ReadyRateLimiter : IProviderRateLimiter
    {
        public Task<ErrorOr<Success>> CheckRateLimitAsync(string tenantId, string providerType) =>
            Task.FromResult<ErrorOr<Success>>(new Success());
    }
}
