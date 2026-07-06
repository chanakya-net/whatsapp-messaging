using MassTransit;
using MessageBridge.Publisher;
using MessageBridge.Publisher.EntityFrameworkCore;
using MessageBridge.Publisher.Requests;
using MessageBridge.SampleClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

var services = new ServiceCollection();
services.AddLogging(cfg => cfg.AddConsole());
services.AddMassTransit(bus => bus.UsingInMemory());
services.AddMessageBridgePublisher(opts =>
{
    opts.DefaultTenantId = "sample-tenant";
    opts.ExchangeName = "messagebridge.commands";
    opts.WhatsAppRoutingKey = "whatsapp.send";
    opts.EmailRoutingKey = "email.confirmation";
});

var provider = services.BuildServiceProvider();
var publisher = provider.GetRequiredService<IMessageBridgePublisher>();

var whatsappRequest = new SendWhatsAppMessageRequest
{
    TenantId = "sample-tenant",
    PhoneNumber = "+1234567890",
    TemplateId = "welcome",
    Body = "Welcome to MessageBridge!",
    LanguageCode = "en-US",
};

Console.WriteLine("Publishing WhatsApp message...");
var whatsappResult = await publisher.PublishWhatsAppMessageAsync(whatsappRequest);
if (whatsappResult.IsSuccess)
{
    Console.WriteLine($"✓ WhatsApp message published: MessageId={whatsappResult.Value.MessageId}");
}
else
{
    Console.WriteLine($"✗ WhatsApp publish failed: {string.Join(", ", whatsappResult.Errors)}");
}

var emailRequest = new SendEmailConfirmationRequest
{
    TenantId = "sample-tenant",
    Email = "user@example.com",
    ConfirmationCode = "123456",
};

Console.WriteLine("Publishing email confirmation...");
var emailResult = await publisher.PublishEmailConfirmationAsync(emailRequest);
if (emailResult.IsSuccess)
{
    Console.WriteLine($"✓ Email confirmation published: MessageId={emailResult.Value.MessageId}");
}
else
{
    Console.WriteLine($"✗ Email publish failed: {string.Join(", ", emailResult.Errors)}");
}

Console.WriteLine("\n--- Outbox Sample ---");
var outboxServices = new ServiceCollection();
outboxServices.AddLogging(cfg => cfg.AddConsole());
outboxServices.AddMassTransit(bus => bus.UsingInMemory());
outboxServices.AddDbContext<SampleDbContext>(opts => opts.UseInMemoryDatabase("samples"));
outboxServices.AddMessageBridgePublisher(opts =>
{
    opts.DefaultTenantId = "sample-tenant";
    opts.ExchangeName = "messagebridge.commands";
    opts.WhatsAppRoutingKey = "whatsapp.send";
    opts.EmailRoutingKey = "email.confirmation";
});
outboxServices.AddMessageBridgeOutboxPublisher<SampleDbContext>(opts =>
{
    opts.BatchSize = 100;
    opts.PollIntervalMilliseconds = 5000;
    opts.CleanupEnabled = true;
});

var outboxProvider = outboxServices.BuildServiceProvider();

Console.WriteLine("✓ Outbox publisher configured (uses sample DbContext with in-memory database)");
Console.WriteLine("  - Batch size: 100");
Console.WriteLine("  - Poll interval: 5000ms");
Console.WriteLine("  - Cleanup enabled: true");
