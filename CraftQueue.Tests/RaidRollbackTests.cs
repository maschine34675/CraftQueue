using CraftQueue.Server;
using CraftQueue.Server.Models;
using SPTarkov.Server.Core.Models.Eft.Common.Tables;
using SPTarkov.Server.Core.Utils;
using System.Reflection;
using Xunit;

namespace CraftQueue.Tests;
public sealed class RaidRollbackTests
{
    private const long Start = 1_790_000_000;

    private static string classify(PendingStartConfirmation record, Production? production, long now)
    {
        MethodInfo method = typeof(CraftQueueService).GetMethod(
            "classifyRaidStart",
            BindingFlags.Static | BindingFlags.NonPublic)!;
        return method.Invoke(null, [record, production, now])!.ToString()!;
    }

    private static PendingStartConfirmation raidStart(long startTimestamp = Start, double seconds = 1800)
    {
        return new PendingStartConfirmation
        {
            QueueId = "q1",
            RecipeId = "magnet",
            AreaType = 10,
            StartTimestamp = startTimestamp,
            ProductionSeconds = seconds,
            RaidStart = true
        };
    }

    [Fact]
    public void ACraftStillInTheProfileIsKept()
    {
        Assert.Equal("Kept", classify(raidStart(), new Production { StartTimestamp = Start }, Start + 60));
        Assert.Equal("Kept", classify(raidStart(), new Production { StartTimestamp = Start + 5 }, Start + 60));
        Assert.Equal("Kept", classify(raidStart(startTimestamp: 0), new Production { StartTimestamp = 42 }, Start));
    }

    [Fact]
    public void ACraftGoneBeforeItCouldFinishWasRolledBack()
    {
        Assert.Equal("RolledBack", classify(raidStart(), null, Start + 600));
        Assert.Equal("RolledBack", classify(raidStart(), new Production { StartTimestamp = Start - 900 }, Start + 600));
    }

    [Fact]
    public void ACraftThatMayHaveFinishedIsNeverQueuedAgainOnAGuess()
    {
        Assert.Equal("Unclear", classify(raidStart(), null, Start + 1800));
        Assert.Equal("Unclear", classify(raidStart(), null, Start + 86_400));
        Assert.Equal("Unclear", classify(raidStart(seconds: 0), null, Start + 1));
        Assert.Equal("Unclear", classify(raidStart(startTimestamp: 0), null, Start + 1));
    }

    [Fact]
    public void AProfileSaveNoLongerSettlesARaidStart()
    {
        MethodInfo settled = typeof(CraftQueueService).GetMethod(
            "isSettledBySave",
            BindingFlags.Static | BindingFlags.NonPublic)!;
        bool run(PendingStartConfirmation record, long covered) => (bool)settled.Invoke(null, [record, covered])!;

        var clientStart = new PendingStartConfirmation { Sequence = 3 };
        Assert.True(run(clientStart, 3));
        Assert.False(run(clientStart, 2));
        Assert.False(run(new PendingStartConfirmation { Sequence = 3, RaidStart = true }, 10));
    }

    [Fact]
    public void CollectingACraftInTheRaidSettlesOnlyThatStart()
    {
        MethodInfo settle = typeof(CraftQueueService).GetMethod(
            "settleCollectedRaidStarts",
            BindingFlags.Static | BindingFlags.NonPublic)!;

        var collected = raidStart();
        var laterSameRecipe = raidStart(startTimestamp: Start + 2000) with { QueueId = "q2" };
        var otherRecipe = raidStart() with { QueueId = "q3", RecipeId = "wires" };
        var clientStart = raidStart() with { QueueId = "q4", RaidStart = false };
        var data = new CraftQueueData { PendingStartConfirmations = [collected, laterSameRecipe, otherRecipe, clientStart] };

        Assert.True((bool)settle.Invoke(null, [data, "magnet", Start])!);
        Assert.Equal(["q2", "q3", "q4"], data.PendingStartConfirmations.Select(record => record.QueueId));
        Assert.False((bool)settle.Invoke(null, [data, "magnet", Start])!);
    }

    [Fact]
    public void AWaitingReasonIsOnlyWrittenWhenItChanges()
    {
        MethodInfo note = typeof(CraftQueueService).GetMethod(
            "noteWaiting",
            BindingFlags.Static | BindingFlags.NonPublic)!;
        var entry = new QueuedProductionRequest { RecipeId = "magnet", AttemptCount = 2 };

        Assert.True((bool)note.Invoke(null, [entry, "Waiting for power.", Start])!);
        Assert.Equal("Waiting for power.", entry.LastError);
        Assert.False((bool)note.Invoke(null, [entry, "Waiting for power.", Start + 5])!);
        Assert.Equal(2, entry.AttemptCount);
        Assert.Null(entry.RetryAt);
    }

    [Fact]
    public void OlderQueueFilesAndClientsStillParse()
    {
        PendingStartConfirmation old = new JsonUtil([]).Deserialize<PendingStartConfirmation>(
            """{"queueId":"q","recipeId":"r","areaType":10,"startTimestamp":5,"sequence":1,"confirmedAt":0}""")!;
        Assert.False(old.RaidStart);
        Assert.Equal(0, old.ProductionSeconds);
        ClientPollRequestData request = new JsonUtil([]).Deserialize<ClientPollRequestData>(
            """{"areaSlots":[{"areaType":10,"capacity":2,"occupied":0},{"areaType":7,"capacity":1,"occupied":0,"canStart":false}]}""")!;
        Assert.True(request.AreaSlots![0].CanStart);
        Assert.False(request.AreaSlots![1].CanStart);

        MethodInfo sanitize = typeof(CraftQueueService).GetMethod(
            "sanitizeAreaSlots",
            BindingFlags.Static | BindingFlags.NonPublic)!;
        var slots = (Dictionary<int, AreaSlotTelemetry>)sanitize.Invoke(null, [request.AreaSlots])!;
        Assert.False(slots[7].CanStart);
    }
}
