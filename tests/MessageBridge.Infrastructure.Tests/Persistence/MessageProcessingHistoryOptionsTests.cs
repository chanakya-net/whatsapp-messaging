using MessageBridge.Infrastructure.Persistence;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Shouldly;
using Xunit;

namespace MessageBridge.Infrastructure.Tests.Persistence;

[Trait("Category", "Unit")]
public sealed class MessageProcessingHistoryOptionsTests
{
    [Fact]
    public void Default_Values_Are_Set()
    {
        var options = new MessageProcessingHistoryOptions();

        options.RecoveryEnabled.ShouldBeTrue();
        options.StaleThresholdMinutes.ShouldBe(30);
        options.CleanupEnabled.ShouldBeFalse();
        options.CleanupRetentionHours.ShouldBe(24);
        options.CleanupBatchSize.ShouldBe(500);
        options.CleanupIntervalMilliseconds.ShouldBe(1_000);
    }

    [Fact]
    public void Options_Bind_From_Configuration()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["MessageBridge:ProcessingHistory:RecoveryEnabled"] = "false",
                ["MessageBridge:ProcessingHistory:StaleThresholdMinutes"] = "45",
                ["MessageBridge:ProcessingHistory:CleanupEnabled"] = "true",
                ["MessageBridge:ProcessingHistory:CleanupRetentionHours"] = "48",
                ["MessageBridge:ProcessingHistory:CleanupBatchSize"] = "1000"
            })
            .Build();

        var options = new MessageProcessingHistoryOptions();
        config.GetSection(MessageProcessingHistoryOptions.SectionName).Bind(options);

        options.RecoveryEnabled.ShouldBeFalse();
        options.StaleThresholdMinutes.ShouldBe(45);
        options.CleanupEnabled.ShouldBeTrue();
        options.CleanupRetentionHours.ShouldBe(48);
        options.CleanupBatchSize.ShouldBe(1000);
    }

    [Fact]
    public void StaleThresholdMinutes_Minimum_Is_1()
    {
        var services = new ServiceCollection();
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["MessageBridge:ProcessingHistory:StaleThresholdMinutes"] = "1"
            })
            .Build();

        services.AddOptions<MessageProcessingHistoryOptions>()
            .Bind(config.GetSection(MessageProcessingHistoryOptions.SectionName))
            .ValidateDataAnnotations();

        var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<IOptionsMonitor<MessageProcessingHistoryOptions>>().CurrentValue;

        options.StaleThresholdMinutes.ShouldBe(1);
    }

    [Fact]
    public void StaleThresholdMinutes_Maximum_Is_5000()
    {
        var services = new ServiceCollection();
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["MessageBridge:ProcessingHistory:StaleThresholdMinutes"] = "5000"
            })
            .Build();

        services.AddOptions<MessageProcessingHistoryOptions>()
            .Bind(config.GetSection(MessageProcessingHistoryOptions.SectionName))
            .ValidateDataAnnotations();

        var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<IOptionsMonitor<MessageProcessingHistoryOptions>>().CurrentValue;

        options.StaleThresholdMinutes.ShouldBe(5000);
    }

    [Fact]
    public void CleanupRetentionHours_Accepts_Wide_Range()
    {
        var options = new MessageProcessingHistoryOptions
        {
            CleanupRetentionHours = 730
        };

        options.CleanupRetentionHours.ShouldBe(730);
    }

    [Fact]
    public void CleanupBatchSize_Accepts_Large_Values()
    {
        var options = new MessageProcessingHistoryOptions
        {
            CleanupBatchSize = 5000
        };

        options.CleanupBatchSize.ShouldBe(5000);
    }

    [Fact]
    public void CleanupIntervalMilliseconds_Accepts_Long_Duration()
    {
        var options = new MessageProcessingHistoryOptions
        {
            CleanupIntervalMilliseconds = 3_600_000
        };

        options.CleanupIntervalMilliseconds.ShouldBe(3_600_000);
    }
}
