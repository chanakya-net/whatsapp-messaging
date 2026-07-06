namespace MessageBridge.Domain.Tests;

public class ProjectStructureTests
{
    [Fact]
    public void Domain_Should_Be_Valid_Assembly()
    {
        var domainType = typeof(MessageBridge.Domain.Class1);
        Assert.NotNull(domainType.Assembly);
        Assert.Equal("MessageBridge.Domain", domainType.Assembly.GetName().Name);
    }
}
