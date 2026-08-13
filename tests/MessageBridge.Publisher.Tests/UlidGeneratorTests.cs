using FluentAssertions;
using MessageBridge.Publisher.Internal;

namespace MessageBridge.Publisher.Tests;

[Trait("Category", "Unit")]
public sealed class UlidGeneratorTests
{
    [Fact]
    public void New_ReturnsUniqueCrockfordUlids()
    {
        var ids = Enumerable.Range(0, 20).Select(_ => UlidGenerator.New()).ToArray();

        ids.Should().OnlyHaveUniqueItems();
        ids.Should().OnlyContain(id => id.Length == 26 && id.All("0123456789ABCDEFGHJKMNPQRSTVWXYZ".Contains));
    }

    [Fact]
    public void New_OrdersTimestampPrefixesAcrossMilliseconds()
    {
        var first = UlidGenerator.New();
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        SpinWait.SpinUntil(
            () => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() > timestamp,
            TimeSpan.FromSeconds(1)).Should().BeTrue();
        var second = UlidGenerator.New();

        first[..10].CompareTo(second[..10]).Should().BeLessThanOrEqualTo(0);
    }
}
