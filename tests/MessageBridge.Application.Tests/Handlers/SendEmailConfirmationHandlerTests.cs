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
        var handler = new SendEmailConfirmationHandler(providerMock, storeMock);

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
        var handler = new SendEmailConfirmationHandler(providerMock, storeMock);

        var result = await handler.Handle(command);

        result.IsError.ShouldBeTrue();
        result.FirstError.Code.ShouldBe("Provider.ServiceDown");
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
        public async Task<ErrorOr<Success>> RecordMessageSentAsync(string messageId, string tenantId)
        {
            await Task.Delay(1);
            return new Success();
        }
    }
}
