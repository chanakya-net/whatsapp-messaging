using MessageBridge.Publisher.EntityFrameworkCore;
using MessageBridge.Publisher.EntityFrameworkCore.Outbox;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace MessageBridge.Publisher.EntityFrameworkCore.Tests;

[Trait("Category", "Unit")]
public sealed class OutboxDependencyInjectionTests
{
    [Fact]
    public void AddMessageBridgeOutboxPublisher_RegistersWriterDispatcherAndCleanup()
    {
        var services = new ServiceCollection();

        services.AddMessageBridgeOutboxPublisher<TestDbContext>(_ => { });

        AssertRegistrations(services, 2);
        services.ShouldContain(descriptor => descriptor.ServiceType == typeof(IMessageBridgeOutboxWriter));
    }

    [Fact]
    public void AddMessageBridgeOutboxDispatcher_RegistersOnlyDispatcher()
    {
        var services = new ServiceCollection();

        services.AddMessageBridgeOutboxDispatcher<TestDbContext>(_ => { });

        AssertRegistrations(services, 1);
        services.ShouldContain(descriptor => descriptor.ImplementationType == typeof(MessageBridgeOutboxDispatcherHostedService<TestDbContext>));
    }

    [Fact]
    public void AddMessageBridgeOutboxCleanup_RegistersOnlyCleanup()
    {
        var services = new ServiceCollection();

        services.AddMessageBridgeOutboxCleanup<TestDbContext>(_ => { });

        AssertRegistrations(services, 1);
        services.ShouldContain(descriptor => descriptor.ImplementationType == typeof(MessageBridgeOutboxCleanupHostedService<TestDbContext>));
    }

    [Fact]
    public void AddMessageBridgeOutboxDispatcher_InvalidOptionsFailOnAccess()
    {
        var services = new ServiceCollection();
        services.AddMessageBridgeOutboxDispatcher<TestDbContext>(options => options.BatchSize = 0);
        using var provider = services.BuildServiceProvider();

        var action = () => provider.GetRequiredService<IOptions<MessageBridgeOutboxOptions>>().Value;

        action.ShouldThrow<OptionsValidationException>();
    }

    private static void AssertRegistrations(IServiceCollection services, int hostedServiceCount)
    {
        services.Count(descriptor => descriptor.ServiceType == typeof(IHostedService)).ShouldBe(hostedServiceCount);
        services.ShouldContain(descriptor => descriptor.ServiceType == typeof(IMessageBridgeOutboxWriter));
    }

    private sealed class TestDbContext(DbContextOptions<TestDbContext> options) : DbContext(options);
}
