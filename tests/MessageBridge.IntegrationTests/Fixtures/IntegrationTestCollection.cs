using Xunit;

namespace MessageBridge.IntegrationTests.Fixtures;

[CollectionDefinition(Name)]
public sealed class IntegrationTestCollection : ICollectionFixture<IntegrationEnvironmentFixture>
{
    public const string Name = "IntegrationEnvironment";
}
