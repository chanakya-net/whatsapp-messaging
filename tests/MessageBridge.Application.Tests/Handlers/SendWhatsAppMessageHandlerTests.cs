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
        var handler = new SendWhatsAppMessageHandler(providerMock, storeMock);

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
        var handler = new SendWhatsAppMessageHandler(providerMock, storeMock);

        var result = await handler.Handle(command);

        result.IsError.ShouldBeTrue();
        result.FirstError.Code.ShouldBe("Provider.RateLimited");
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
        public async Task<ErrorOr<Success>> RecordMessageSentAsync(string messageId, string tenantId)
        {
            await Task.Delay(1);
            return new Success();
        }
    }
}
