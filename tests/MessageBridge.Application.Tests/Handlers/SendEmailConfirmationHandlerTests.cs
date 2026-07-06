using ErrorOr;
using MessageBridge.Application.Abstractions;
using MessageBridge.Application.Handlers;
using MessageBridge.Application.Messages;
using MessageBridge.Application.Providers;
using Shouldly;
using Xunit;

namespace MessageBridge.Application.Tests.Handlers;

public sealed class SendEmailConfirmationHandlerTests
{
    [Fact]
    public async Task Handle_ShouldReturnSuccessResult_WhenProviderAcceptsEmail()
    {
        var command = new SendEmailConfirmation(
            MessageId: "msg-001",
            TenantId: "tenant-1",
            RecipientEmail: "user@example.com",
            RecipientName: "John",
            ConfirmationToken: "token-abc123",
            ExpiresAtUtc: DateTimeOffset.UtcNow.AddHours(24),
            CorrelationId: "corr-001",
            RequestedAtUtc: DateTimeOffset.UtcNow);

        var providerMock = new StubEmailConfirmationSender();
        var storeMock = new StubMessageProcessingStore();
        var tenantConfigMock = new StubTenantConfigurationProvider();
        var rateLimiterMock = new StubProviderRateLimiter();
        var handler = new SendEmailConfirmationHandler(providerMock, storeMock, tenantConfigMock, rateLimiterMock);

        var result = await handler.Handle(command);

        result.IsError.ShouldBeFalse();
        result.Value.ShouldBe(new Success());
        providerMock.SentEmails.ShouldContain(e =>
            e.RecipientEmailAddress == command.RecipientEmail);
    }

    [Fact]
    public async Task Handle_ShouldReturnErrorResult_WhenProviderRejectsEmail()
    {
        var command = new SendEmailConfirmation(
            MessageId: "msg-001",
            TenantId: "tenant-1",
            RecipientEmail: "user@example.com",
            RecipientName: null,
            ConfirmationToken: "token-abc123",
            ExpiresAtUtc: DateTimeOffset.UtcNow.AddHours(24),
            CorrelationId: null,
            RequestedAtUtc: DateTimeOffset.UtcNow);

        var providerMock = new StubEmailConfirmationSender { FailWith = Error.Failure("Provider.ServiceDown", "Email service unavailable") };
        var storeMock = new StubMessageProcessingStore();
        var tenantConfigMock = new StubTenantConfigurationProvider();
        var rateLimiterMock = new StubProviderRateLimiter();
        var handler = new SendEmailConfirmationHandler(providerMock, storeMock, tenantConfigMock, rateLimiterMock);

        var result = await handler.Handle(command);

        result.IsError.ShouldBeTrue();
        result.FirstError.Code.ShouldBe("Provider.ServiceDown");
    }

    [Fact]
    public async Task Handle_ShouldReturnErrorResult_WhenTenantConfigNotFound()
    {
        var command = new SendEmailConfirmation(
            MessageId: "msg-001",
            TenantId: "tenant-invalid",
            RecipientEmail: "user@example.com",
            RecipientName: "John",
            ConfirmationToken: "token-abc123",
            ExpiresAtUtc: DateTimeOffset.UtcNow.AddHours(24),
            CorrelationId: "corr-001",
            RequestedAtUtc: DateTimeOffset.UtcNow);

        var providerMock = new StubEmailConfirmationSender();
        var storeMock = new StubMessageProcessingStore();
        var tenantConfigMock = new StubTenantConfigurationProvider { FailWith = Error.NotFound("Tenant.NotFound", "Tenant does not exist") };
        var rateLimiterMock = new StubProviderRateLimiter();
        var handler = new SendEmailConfirmationHandler(providerMock, storeMock, tenantConfigMock, rateLimiterMock);

        var result = await handler.Handle(command);

        result.IsError.ShouldBeTrue();
        result.FirstError.Code.ShouldBe("Tenant.NotFound");
        providerMock.SentEmails.ShouldBeEmpty();
    }

    [Fact]
    public async Task Handle_ShouldReturnErrorResult_WhenRateLimitExceeded()
    {
        var command = new SendEmailConfirmation(
            MessageId: "msg-001",
            TenantId: "tenant-1",
            RecipientEmail: "user@example.com",
            RecipientName: "John",
            ConfirmationToken: "token-abc123",
            ExpiresAtUtc: DateTimeOffset.UtcNow.AddHours(24),
            CorrelationId: "corr-001",
            RequestedAtUtc: DateTimeOffset.UtcNow);

        var providerMock = new StubEmailConfirmationSender();
        var storeMock = new StubMessageProcessingStore();
        var tenantConfigMock = new StubTenantConfigurationProvider();
        var rateLimiterMock = new StubProviderRateLimiter { FailWith = Error.Validation("RateLimit.Exceeded", "Rate limit exceeded") };
        var handler = new SendEmailConfirmationHandler(providerMock, storeMock, tenantConfigMock, rateLimiterMock);

        var result = await handler.Handle(command);

        result.IsError.ShouldBeTrue();
        result.FirstError.Code.ShouldBe("RateLimit.Exceeded");
        providerMock.SentEmails.ShouldBeEmpty();
    }

    [Fact]
    public async Task Handle_ShouldReturnErrorResult_WhenStoreFailsToRecordMessage()
    {
        var command = new SendEmailConfirmation(
            MessageId: "msg-001",
            TenantId: "tenant-1",
            RecipientEmail: "user@example.com",
            RecipientName: "John",
            ConfirmationToken: "token-abc123",
            ExpiresAtUtc: DateTimeOffset.UtcNow.AddHours(24),
            CorrelationId: "corr-001",
            RequestedAtUtc: DateTimeOffset.UtcNow);

        var providerMock = new StubEmailConfirmationSender();
        var storeMock = new StubMessageProcessingStore { FailWith = Error.Failure("Store.WriteError", "Failed to record message") };
        var tenantConfigMock = new StubTenantConfigurationProvider();
        var rateLimiterMock = new StubProviderRateLimiter();
        var handler = new SendEmailConfirmationHandler(providerMock, storeMock, tenantConfigMock, rateLimiterMock);

        var result = await handler.Handle(command);

        result.IsError.ShouldBeTrue();
        result.FirstError.Code.ShouldBe("Store.WriteError");
    }

    private sealed class StubEmailConfirmationSender : IEmailConfirmationSender
    {
        public List<EmailConfirmation> SentEmails { get; } = [];
        public Error? FailWith { get; set; }

        public async Task<ErrorOr<Success>> SendAsync(EmailConfirmation email, string tenantId)
        {
            if (FailWith is not null)
                return FailWith.Value;

            SentEmails.Add(email);
            await Task.Delay(1);
            return new Success();
        }
    }

    private sealed class StubMessageProcessingStore : IMessageProcessingStore
    {
        public Error? FailWith { get; set; }

        public async Task<ErrorOr<Success>> RecordMessageSentAsync(string messageId, string tenantId)
        {
            if (FailWith is not null)
                return FailWith.Value;

            await Task.Delay(1);
            return new Success();
        }
    }

    private sealed class StubTenantConfigurationProvider : ITenantConfigurationProvider
    {
        public Error? FailWith { get; set; }

        public async Task<ErrorOr<TenantConfiguration>> GetTenantConfigAsync(string tenantId)
        {
            if (FailWith is not null)
                return FailWith.Value;

            await Task.Delay(1);
            return new TenantConfiguration(tenantId, IsActive: true);
        }
    }

    private sealed class StubProviderRateLimiter : IProviderRateLimiter
    {
        public Error? FailWith { get; set; }

        public async Task<ErrorOr<Success>> CheckRateLimitAsync(string tenantId, string providerType)
        {
            if (FailWith is not null)
                return FailWith.Value;

            await Task.Delay(1);
            return new Success();
        }
    }
}
