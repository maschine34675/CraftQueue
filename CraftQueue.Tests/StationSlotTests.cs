using CraftQueue.Server;
using CraftQueue.Server.Models;
using SPTarkov.Server.Core.Utils;
using System.Reflection;
using Xunit;

namespace CraftQueue.Tests;
public sealed class StationSlotTests
{
    private const long Now = 1_000_000;

    private static readonly Func<QueuedProductionRequest, bool> ToolsAlwaysFree = _ => true;

    private static QueuedProductionRequest? pick(
        IEnumerable<QueuedProductionRequest> queue,
        int capacity,
        int occupied,
        IEnumerable<string>? busy = null,
        Func<QueuedProductionRequest, bool>? toolsFree = null)
    {
        MethodInfo method = typeof(CraftQueueService).GetMethod(
            "pickNextForStation",
            BindingFlags.Static | BindingFlags.NonPublic)!;
        var busyRecipes = new HashSet<string>(busy ?? [], StringComparer.Ordinal);
        return (QueuedProductionRequest?)method.Invoke(
            null,
            [queue, capacity, occupied, busyRecipes, Now, toolsFree ?? ToolsAlwaysFree]);
    }

    private static QueuedProductionRequest entry(string recipeId, QueueEntryStatus status = QueueEntryStatus.Queued, long? retryAt = null)
    {
        return new QueuedProductionRequest { RecipeId = recipeId, Status = status, RetryAt = retryAt };
    }

    [Fact]
    public void SingleSlotStationsKeepTheOldHeadOnlyRule()
    {
        QueuedProductionRequest head = entry("bolts");
        QueuedProductionRequest next = entry("nuts");

        Assert.Same(head, pick([head, next], capacity: 1, occupied: 0));
        Assert.Null(pick([head, next], capacity: 1, occupied: 1));
        Assert.Null(pick([entry("bolts", QueueEntryStatus.Claimed), next], capacity: 1, occupied: 0));
        Assert.Null(pick([entry("bolts", QueueEntryStatus.Starting), next], capacity: 1, occupied: 0));
        Assert.Null(pick([entry("bolts", QueueEntryStatus.NeedsReview), next], capacity: 1, occupied: 0));
        Assert.Null(pick([entry("bolts", retryAt: Now + 10), next], capacity: 1, occupied: 0));
        Assert.Same(head, pick([head], capacity: 1, occupied: 0, toolsFree: _ => false));
    }

    [Fact]
    public void EliteCraftingFillsTheSecondSlot()
    {
        QueuedProductionRequest nuts = entry("nuts");

        Assert.Same(nuts, pick([nuts], capacity: 2, occupied: 1));
        Assert.Null(pick([nuts], capacity: 2, occupied: 2));
        Assert.Same(nuts, pick([entry("bolts", QueueEntryStatus.Claimed), nuts], capacity: 2, occupied: 0));
        Assert.Null(pick([entry("bolts", QueueEntryStatus.Claimed), nuts], capacity: 2, occupied: 1));
    }

    [Fact]
    public void TheSameRecipeIsSteppedOverRatherThanStartedTwice()
    {
        QueuedProductionRequest bolts2 = entry("bolts");
        QueuedProductionRequest bolts3 = entry("bolts");
        QueuedProductionRequest nuts = entry("nuts");

        Assert.Same(nuts, pick([bolts2, bolts3, nuts], capacity: 2, occupied: 1, busy: ["bolts"]));
        Assert.Null(pick([bolts2, bolts3], capacity: 2, occupied: 1, busy: ["bolts"]));
        Assert.Same(nuts, pick([bolts2, nuts], capacity: 1, occupied: 0, busy: ["bolts"]));
    }

    [Fact]
    public void AnExtraSlotSkipsAnEntryWhoseToolIsInTheRunningCraft()
    {
        QueuedProductionRequest needsScrewdriver = entry("gunpowder");
        QueuedProductionRequest noTool = entry("bandage");
        Func<QueuedProductionRequest, bool> screwdriverTaken = candidate => candidate.RecipeId != "gunpowder";

        Assert.Same(noTool, pick([needsScrewdriver, noTool], capacity: 2, occupied: 1, toolsFree: screwdriverTaken));
        Assert.Null(pick([needsScrewdriver], capacity: 2, occupied: 1, toolsFree: screwdriverTaken));
    }

    [Fact]
    public void AFailedToolCheckIsNotRepeatedOnEveryPoll()
    {
        MethodInfo method = typeof(CraftQueueService).GetMethod(
            "checkToolsWithBackoff",
            BindingFlags.Static | BindingFlags.NonPublic)!;
        var waits = new Dictionary<string, long>(StringComparer.Ordinal);
        QueuedProductionRequest candidate = entry("gunpowder");
        int checks = 0;
        bool check(bool result) { checks++; return result; }
        bool run(long at, bool result) => (bool)method.Invoke(null, [waits, candidate, at, (Func<bool>)(() => check(result))])!;

        Assert.False(run(Now, false));
        Assert.Equal(1, checks);
        Assert.False(run(Now + 2, true));
        Assert.False(run(Now + 29, true));
        Assert.Equal(1, checks);
        Assert.True(run(Now + 30, true));
        Assert.Equal(2, checks);
        Assert.True(run(Now + 31, true));
        Assert.Equal(3, checks);
        Assert.Empty(waits);
    }

    [Fact]
    public void AStuckEntryStillHoldsItsStationWithTwoSlots()
    {
        QueuedProductionRequest nuts = entry("nuts");

        Assert.Null(pick([entry("bolts", QueueEntryStatus.NeedsReview), nuts], capacity: 2, occupied: 1));
        Assert.Null(pick([entry("bolts", retryAt: Now + 30), nuts], capacity: 2, occupied: 1));
    }

    [Fact]
    public void SlotReportsFromTheClientAreClamped()
    {
        MethodInfo sanitize = typeof(CraftQueueService).GetMethod(
            "sanitizeAreaSlots",
            BindingFlags.Static | BindingFlags.NonPublic)!;

        var result = (Dictionary<int, AreaSlotTelemetry>)sanitize.Invoke(null,
        [
            new List<AreaSlotTelemetry>
            {
                new() { AreaType = 10, Capacity = 0, Occupied = -3, CompletedRecipeIds = ["", "bolts"] },
                new() { AreaType = 11, Capacity = 500, Occupied = 1 }
            }
        ])!;

        Assert.Equal(1, result[10].Capacity);
        Assert.Equal(0, result[10].Occupied);
        Assert.Equal(["bolts"], result[10].CompletedRecipeIds);
        Assert.Equal(8, result[11].Capacity);

        Assert.Empty((Dictionary<int, AreaSlotTelemetry>)sanitize.Invoke(null, [null])!);
    }

    [Fact]
    public void TheClientsSlotReportDeserializes()
    {
        const string json = """
            {"profileId":"p","productions":[],"areaSlots":[
              {"areaType":10,"capacity":2,"occupied":1,"completedRecipeIds":["bolts"]}]}
            """;

        ClientPollRequestData request = new JsonUtil([]).Deserialize<ClientPollRequestData>(json)!;

        AreaSlotTelemetry slot = Assert.Single(request.AreaSlots!);
        Assert.Equal(10, slot.AreaType);
        Assert.Equal(2, slot.Capacity);
        Assert.Equal(1, slot.Occupied);
        Assert.Equal(["bolts"], slot.CompletedRecipeIds);
        Assert.Null(new JsonUtil([]).Deserialize<ClientPollRequestData>("""{"productions":[]}""")!.AreaSlots);
    }
}
