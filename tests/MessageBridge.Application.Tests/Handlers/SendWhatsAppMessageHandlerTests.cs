using ErrorOr;
using MessageBridge.Application.Abstractions;
using MessageBridge.Application.Handlers;
using MessageBridge.Application.Messages;
using MessageBridge.Application.Providers;
using Shouldly;
using Xunit;

namespace MessageBridge.Application.Tests.Handlers;

public sealed class SendWhatsAppMessageHandlerTests
{
    [Fact]
    public async Task Handle_ShouldReturnSuccessResult_WhenProviderAcceptsMessage()
    {
        var command = new SendWhatsAppMessage(
            MessageId: "msg-001",
            TenantId: "tenant-1",
            RecipientPhoneNumber: "+15551234567",
            TemplateName: "welcome",
            TemplateLanguage: "en-US",
            TemplateParameters: new Dictionary<string, string> { ["firstName"] = "Ada" },
            CorrelationId: "corr-001",
            RequestedAtUtc: DateTimeOffset.UtcNow);

        var providerMock = new StubWhatsAppMessageSender();
        var storeMock = new StubMessageProcessingStore();
        var tenantConfigMock = new StubTenantConfigurationProvider();
        var rateLimiterMock = new StubProviderRateLimiter();
        var handler = new SendWhatsAppMessageHandler(providerMock, storeMock, tenantConfigMock, rateLimiterMock);

        var result = await handler.Handle(command);

        result.IsError.ShouldBeFalse();
        result.Value.ShouldBe(new Success());
        providerMock.SentMessages.ShouldContain(m =>
            m.RecipientPhoneNumber == command.RecipientPhoneNumber);
    }

    [Fact]
    public async Task Handle_ShouldReturnErrorResult_WhenProviderRejectsMessage()
    {
        var command = new SendWhatsAppMessage(
            MessageId: "msg-001",
            TenantId: "tenant-1",
            RecipientPhoneNumber: "+15551234567",
            TemplateName: "welcome",
            TemplateLanguage: "en-US",
            TemplateParameters: null,
            CorrelationId: null,
            RequestedAtUtc: DateTimeOffset.UtcNow);

        var providerMock = new StubWhatsAppMessageSender { FailWith = Error.Validation("Provider.RateLimited", "Too many requests") };
        var storeMock = new StubMessageProcessingStore();
        var tenantConfigMock = new StubTenantConfigurationProvider();
        var rateLimiterMock = new StubProviderRateLimiter();
        var handler = new SendWhatsAppMessageHandler(providerMock, storeMock, tenantConfigMock, rateLimiterMock);

        var result = await handler.Handle(command);

        result.IsError.ShouldBeTrue();
        result.FirstError.Code.ShouldBe("Provider.RateLimited");
    }

    [Fact]
    public async Task Handle_ShouldReturnErrorResult_WhenTenantConfigNotFound()
    {
        var command = new SendWhatsAppMessage(
            MessageId: "msg-001",
            TenantId: "tenant-invalid",
            RecipientPhoneNumber: "+15551234567",
            TemplateName: "welcome",
            TemplateLanguage: "en-US",
            TemplateParameters: new Dictionary<string, string>(),
            CorrelationId: "corr-001",
            RequestedAtUtc: DateTimeOffset.UtcNow);

        var providerMock = new StubWhatsAppMessageSender();
        var storeMock = new StubMessageProcessingStore();
        var tenantConfigMock = new StubTenantConfigurationProvider { FailWith = Error.NotFound("Tenant.NotFound", "Tenant does not exist") };
        var rateLimiterMock = new StubProviderRateLimiter();
        var handler = new SendWhatsAppMessageHandler(providerMock, storeMock, tenantConfigMock, rateLimiterMock);

        var result = await handler.Handle(command);

        result.IsError.ShouldBeTrue();
        result.FirstError.Code.ShouldBe("Tenant.NotFound");
        providerMock.SentMessages.ShouldBeEmpty();
    }

    [Fact]
    public async Task Handle_ShouldReturnErrorResult_WhenRateLimitExceeded()
    {
        var command = new SendWhatsAppMessage(
            MessageId: "msg-001",
            TenantId: "tenant-1",
            RecipientPhoneNumber: "+15551234567",
            TemplateName: "welcome",
            TemplateLanguage: "en-US",
            TemplateParameters: new Dictionary<string, string>(),
            CorrelationId: "corr-001",
            RequestedAtUtc: DateTimeOffset.UtcNow);

        var providerMock = new StubWhatsAppMessageSender();
        var storeMock = new StubMessageProcessingStore();
        var tenantConfigMock = new StubTenantConfigurationProvider();
        var rateLimiterMock = new StubProviderRateLimiter { FailWith = Error.Validation("RateLimit.Exceeded", "Rate limit exceeded") };
        var handler = new SendWhatsAppMessageHandler(providerMock, storeMock, tenantConfigMock, rateLimiterMock);

        var result = await handler.Handle(command);

        result.IsError.ShouldBeTrue();
        result.FirstError.Code.ShouldBe("RateLimit.Exceeded");
        providerMock.SentMessages.ShouldBeEmpty();
    }

    [Fact]
    public async Task Handle_ShouldReturnErrorResult_WhenStoreFailsToRecordMessage()
    {
        var command = new SendWhatsAppMessage(
            MessageId: "msg-001",
            TenantId: "tenant-1",
            RecipientPhoneNumber: "+15551234567",
            TemplateName: "welcome",
            TemplateLanguage: "en-US",
            TemplateParameters: new Dictionary<string, string>(),
            CorrelationId: "corr-001",
            RequestedAtUtc: DateTimeOffset.UtcNow);

        var providerMock = new StubWhatsAppMessageSender();
        var storeMock = new StubMessageProcessingStore { FailWith = Error.Failure("Store.WriteError", "Failed to record message") };
        var tenantConfigMock = new StubTenantConfigurationProvider();
        var rateLimiterMock = new StubProviderRateLimiter();
        var handler = new SendWhatsAppMessageHandler(providerMock, storeMock, tenantConfigMock, rateLimiterMock);

        var result = await handler.Handle(command);

        result.IsError.ShouldBeTrue();
        result.FirstError.Code.ShouldBe("Store.WriteError");
    }

    private sealed class StubWhatsAppMessageSender : IWhatsAppMessageSender
    {
        public List<WhatsAppMessage> SentMessages { get; } = [];
        public Error? FailWith { get; set; }

        public async Task<ErrorOr<Success>> SendAsync(WhatsAppMessage message, string tenantId)
        {
            if (FailWith is not null)
                return FailWith.Value;

            SentMessages.Add(message);
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
