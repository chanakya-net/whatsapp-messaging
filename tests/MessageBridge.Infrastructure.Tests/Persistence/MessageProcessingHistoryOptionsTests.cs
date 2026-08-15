using MessageBridge.Domain.Processing;
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

    [Fact]
    public void DevelopmentRetentionHours_Defaults_To_24()
    {
        var options = new MessageProcessingHistoryOptions();
        options.DevelopmentRetentionHours.ShouldBe(24);
    }

    [Fact]
    public void ProductionRetentionHours_Defaults_To_168()
    {
        var options = new MessageProcessingHistoryOptions();
        options.ProductionRetentionHours.ShouldBe(168);
    }

    [Fact]
    public void EligibleStatusesForCleanup_Defaults_To_CompletedAndAbandoned()
    {
        var options = new MessageProcessingHistoryOptions();
        options.EligibleStatusesForCleanup.ShouldNotBeNull();
        options.EligibleStatusesForCleanup.ShouldContain(ProcessingStatus.Completed);
        options.EligibleStatusesForCleanup.ShouldContain(ProcessingStatus.Abandoned);
        options.EligibleStatusesForCleanup.Length.ShouldBe(2);
    }

    [Fact]
    public void EligibleStatusesForCleanup_Never_Includes_FailedOrRejected()
    {
        var options = new MessageProcessingHistoryOptions
        {
            EligibleStatusesForCleanup = [ProcessingStatus.Failed, ProcessingStatus.Rejected]
        };

        options.EligibleStatusesForCleanup.ShouldNotContain(ProcessingStatus.Failed);
        options.EligibleStatusesForCleanup.ShouldNotContain(ProcessingStatus.Rejected);
        options.EligibleStatusesForCleanup.Length.ShouldBe(0);
    }

    [Fact]
    public void EligibleStatusesForCleanup_Never_Includes_NonTerminalStatuses()
    {
        var options = new MessageProcessingHistoryOptions
        {
            EligibleStatusesForCleanup =
            [
                ProcessingStatus.Received,
                ProcessingStatus.Processing,
                ProcessingStatus.Completed,
                ProcessingStatus.Abandoned
            ]
        };

        options.EligibleStatusesForCleanup.ShouldBe(
            [ProcessingStatus.Completed, ProcessingStatus.Abandoned]);
    }

    [Fact]
    public void DevelopmentRetentionHours_Binds_From_Configuration()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["MessageBridge:ProcessingHistory:DevelopmentRetentionHours"] = "48"
            })
            .Build();

        var options = new MessageProcessingHistoryOptions();
        config.GetSection(MessageProcessingHistoryOptions.SectionName).Bind(options);

        options.DevelopmentRetentionHours.ShouldBe(48);
    }

    [Fact]
    public void ProductionRetentionHours_Binds_From_Configuration()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["MessageBridge:ProcessingHistory:ProductionRetentionHours"] = "240"
            })
            .Build();

        var options = new MessageProcessingHistoryOptions();
        config.GetSection(MessageProcessingHistoryOptions.SectionName).Bind(options);

        options.ProductionRetentionHours.ShouldBe(240);
    }
}
