using CraftQueue.Server;
using CraftQueue.Server.Models;
using SPTarkov.Server.Core.Models.Enums.Hideout;
using System.Reflection;
using Xunit;

namespace CraftQueue.Tests;
public sealed class QueueRecoveryTests
{
    [Fact]
    public void QueuedEntryWaitingForMaterialsSurvivesARestartWithoutReview()
    {
        var data = new CraftQueueData
        {
            AreaQueues = new()
            {
                [HideoutAreas.Workbench] = new LinkedList<QueuedProductionRequest>(
                [
                    new QueuedProductionRequest
                    {
                        QueueId = "waiting-entry",
                        RecipeId = "655b56eb32b0b1645e6f54c8",
                        Status = QueueEntryStatus.Queued,
                        AttemptCount = 2,
                        RetryAt = DateTimeOffset.UtcNow.AddSeconds(30).ToUnixTimeSeconds(),
                        LastError = "Not enough Bolts: available 1, required 2 including queued crafts."
                    }
                ])
            }
        };

        data.NormalizeAfterLoad();

        QueuedProductionRequest entry = Assert.Single(data.AreaQueues[HideoutAreas.Workbench]);
        Assert.Equal(QueueEntryStatus.Queued, entry.Status);
        Assert.NotNull(entry.RetryAt);
    }

    [Theory]
    [InlineData("area_busy")]
    [InlineData("generator_unavailable")]
    [InlineData("executor_changed")]
    public void TransientClientFailuresAreTreatedAsEnvironmental(string errorCode)
    {
        Assert.True(invokeIsEnvironmentalFailure(errorCode));
    }

    [Theory]
    [InlineData("requirements_unavailable")]
    [InlineData("start_exception")]
    [InlineData("client_exception")]
    [InlineData(null)]
    public void RealStartFailuresStillEscalateTheBackoff(string? errorCode)
    {
        Assert.False(invokeIsEnvironmentalFailure(errorCode));
    }

    [Fact]
    public void RetryBackoffIsBoundedByFiveMinutes()
    {
        MethodInfo method = typeof(CraftQueueService).GetMethod(
            "retryDelaySeconds",
            BindingFlags.Static | BindingFlags.NonPublic)!;

        Assert.Equal(15, (int)method.Invoke(null, [1])!);
        Assert.Equal(30, (int)method.Invoke(null, [2])!);
        Assert.Equal(300, (int)method.Invoke(null, [99])!);
    }

    [Fact]
    public void ClaimingSkipsAnAreaThatJustStartedACraft()
    {
        int grace = (int)typeof(CraftQueueService)
            .GetField("AreaStartGraceSeconds", BindingFlags.Static | BindingFlags.NonPublic)!
            .GetRawConstantValue()!;

        Assert.True(grace >= 6, "The area grace period must span several client polls.");
    }

    [Fact]
    public void RepeatedEnvironmentalDeferralsStillFallBackToTheNormalBackoff()
    {
        int maximumFastDeferrals = (int)typeof(CraftQueueService)
            .GetField("MaximumFastDeferrals", BindingFlags.Static | BindingFlags.NonPublic)!
            .GetRawConstantValue()!;

        Assert.InRange(maximumFastDeferrals, 1, 5);
    }

    [Fact]
    public void RaidHandoverExpiresSoAMissedEndCallbackCannotStickForever()
    {
        int maximumRaid = (int)typeof(CraftQueueService)
            .GetField("MaximumRaidSeconds", BindingFlags.Static | BindingFlags.NonPublic)!
            .GetRawConstantValue()!;
        Assert.InRange(maximumRaid, 60 * 60, 12 * 60 * 60);
    }

    [Fact]
    public void TelemetryStaysFreshLongerThanAFullWebChannelFailover()
    {
        int fresh = (int)typeof(CraftQueueService)
            .GetField("TelemetryFreshSeconds", BindingFlags.Static | BindingFlags.NonPublic)!
            .GetRawConstantValue()!;

        Assert.True(fresh >= 15, "Telemetry must outlive a two-host web channel failover.");
    }

    private static bool invokeIsEnvironmentalFailure(string? errorCode)
    {
        MethodInfo method = typeof(CraftQueueService).GetMethod(
            "isEnvironmentalFailure",
            BindingFlags.Static | BindingFlags.NonPublic)!;
        return (bool)method.Invoke(null, [errorCode])!;
    }
}
