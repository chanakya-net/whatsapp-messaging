using FluentAssertions;
using MessageBridge.Publisher;
using MessageBridge.Publisher.Internal;
using Microsoft.Extensions.DependencyInjection;

namespace MessageBridge.Publisher.Tests;

[Trait("Category", "Unit")]
public sealed class PublisherDependencyInjectionTests
{
    [Fact]
    public void AddMessageBridgePublisher_RegistersPublisherAndDependencies()
    {
        var services = new ServiceCollection();
        services.AddMessageBridgePublisher(options => options.DefaultTenantId = "tenant");

        services.Should().Contain(descriptor =>
            descriptor.ServiceType == typeof(IMessageBridgePublisher) &&
            descriptor.ImplementationType == typeof(DirectMessageBridgePublisher));
        services.Should().Contain(descriptor =>
            descriptor.ServiceType == typeof(IMessageBridgePublisherTransport) &&
            descriptor.ImplementationType == typeof(MassTransitMessageBridgeTransport));
    }

    [Fact]
    public void AddMessageBridgePublisher_WithNullServices_Throws()
    {
        IServiceCollection? services = null;

        var action = () => services!.AddMessageBridgePublisher(_ => { });

        action.Should().Throw<ArgumentNullException>();
    }
}
