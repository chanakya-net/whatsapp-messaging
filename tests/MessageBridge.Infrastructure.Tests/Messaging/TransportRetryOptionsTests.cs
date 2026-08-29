using MessageBridge.Infrastructure.Messaging.Options;
using Microsoft.Extensions.Configuration;
using Shouldly;
using Xunit;

namespace MessageBridge.Infrastructure.Tests.Messaging;

[Trait("Category", "Unit")]
public sealed class TransportRetryOptionsTests
{
    [Fact]
    public void Default_Immediate_Retry_Count_Is_3()
    {
        var options = new TransportRetryOptions();

        options.ImmediateRetryCount.ShouldBe(3);
    }

    [Fact]
    public void Default_Delayed_Redelivery_Intervals_Is_Empty()
    {
        var options = new TransportRetryOptions();

        options.DelayedRedeliveryIntervals.ShouldBeEmpty();
    }

    [Fact]
    public void DefaultDelayedRedeliveryIntervals_Contains_3_Intervals()
    {
        TransportRetryOptions.DefaultDelayedRedeliveryIntervals.Length.ShouldBe(3);
        TransportRetryOptions.DefaultDelayedRedeliveryIntervals[0].ShouldBe(TimeSpan.FromMinutes(5));
        TransportRetryOptions.DefaultDelayedRedeliveryIntervals[1].ShouldBe(TimeSpan.FromMinutes(15));
        TransportRetryOptions.DefaultDelayedRedeliveryIntervals[2].ShouldBe(TimeSpan.FromHours(1));
    }

    [Fact]
    public void EffectiveDelayedRedeliveryIntervals_UsesApprovedSchedule_WhenUnset()
    {
        var options = new TransportRetryOptions();

        options.EffectiveDelayedRedeliveryIntervals.ShouldBe(
            TransportRetryOptions.DefaultDelayedRedeliveryIntervals);
        options.ImmediateRetryCount.ShouldBe(3);
    }

    [Fact]
    public void EffectiveDelayedRedeliveryIntervals_UsesConfiguredSchedule()
    {
        var intervals = new[] { TimeSpan.FromMinutes(2), TimeSpan.FromMinutes(20) };
        var options = new TransportRetryOptions { DelayedRedeliveryIntervals = intervals };

        options.EffectiveDelayedRedeliveryIntervals.ShouldBe(intervals);
    }

    [Fact]
    public void Options_Bind_ImmediateRetryCount_From_Configuration()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["MessageBridge:TransportRetry:ImmediateRetryCount"] = "5"
            })
            .Build();

        var options = new TransportRetryOptions();
        config.GetSection(TransportRetryOptions.SectionName).Bind(options);

        options.ImmediateRetryCount.ShouldBe(5);
    }

    [Fact]
    public void Options_Accept_Custom_Delayed_Intervals()
    {
        var intervals = new[]
        {
            TimeSpan.FromMinutes(1),
            TimeSpan.FromMinutes(10),
            TimeSpan.FromHours(2)
        };
        var options = new TransportRetryOptions
        {
            DelayedRedeliveryIntervals = intervals
        };

        options.DelayedRedeliveryIntervals.ShouldBe(intervals);
        options.DelayedRedeliveryIntervals.Length.ShouldBe(3);
    }

    [Fact]
    public void Options_Preserves_Zero_ImmediateRetryCount()
    {
        var options = new TransportRetryOptions
        {
            ImmediateRetryCount = 0
        };

        options.ImmediateRetryCount.ShouldBe(0);
    }

    [Fact]
    public void Options_Preserves_Large_ImmediateRetryCount()
    {
        var options = new TransportRetryOptions
        {
            ImmediateRetryCount = 100
        };

        options.ImmediateRetryCount.ShouldBe(100);
    }

    [Fact]
    public void Options_Accepts_Custom_Single_Interval()
    {
        var intervals = new[] { TimeSpan.FromHours(1) };
        var options = new TransportRetryOptions
        {
            DelayedRedeliveryIntervals = intervals
        };

        options.DelayedRedeliveryIntervals.Length.ShouldBe(1);
        options.DelayedRedeliveryIntervals[0].ShouldBe(TimeSpan.FromHours(1));
    }
}
