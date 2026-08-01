using CraftQueue.Server;
using SPTarkov.Server.Core.Callbacks;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Eft.Common;
using SPTarkov.Server.Core.Models.Eft.Hideout;
using SPTarkov.Server.Core.Models.Eft.Match;
using SPTarkov.Server.Core.Services;
using System.Reflection;
using Xunit;

namespace CraftQueue.Tests;

public sealed class LifecycleRegressionTests
{
    [Fact]
    public void StartConfirmationPatchTargetsTheVanillaProductionCallback()
    {
        MethodInfo resolver = typeof(HideoutStartConfirmationPatch).GetMethod(
            "GetTargetMethod",
            BindingFlags.Instance | BindingFlags.NonPublic)!;

        MethodBase target = (MethodBase)resolver.Invoke(new HideoutStartConfirmationPatch(), null)!;

        Assert.Equal(typeof(HideoutCallbacks), target.DeclaringType);
        Assert.Equal(nameof(HideoutCallbacks.SingleProductionStart), target.Name);
        Assert.Equal(
            [typeof(PmcData), typeof(HideoutSingleProductionStartRequestData), typeof(MongoId)],
            target.GetParameters().Select(parameter => parameter.ParameterType).ToArray());
    }

    [Fact]
    public void RaidStartPatchTargetsTheVanillaLifecycleService()
    {
        MethodInfo resolver = typeof(LocalRaidStartPatch).GetMethod(
            "GetTargetMethod",
            BindingFlags.Instance | BindingFlags.NonPublic)!;

        MethodBase target = (MethodBase)resolver.Invoke(new LocalRaidStartPatch(), null)!;

        Assert.Equal(typeof(LocationLifecycleService), target.DeclaringType);
        Assert.Equal(nameof(LocationLifecycleService.StartLocalRaid), target.Name);
        Assert.Equal(
            [typeof(MongoId), typeof(StartLocalRaidRequestData)],
            target.GetParameters().Select(parameter => parameter.ParameterType).ToArray());
    }

    [Fact]
    public void RaidEndPatchStopsBeforeTheVanillaProfileMerge()
    {
        MethodInfo resolver = typeof(LocalRaidEndPatch).GetMethod(
            "GetTargetMethod",
            BindingFlags.Instance | BindingFlags.NonPublic)!;

        MethodBase target = (MethodBase)resolver.Invoke(new LocalRaidEndPatch(), null)!;

        Assert.Equal(typeof(LocationLifecycleService), target.DeclaringType);
        Assert.Equal(nameof(LocationLifecycleService.EndLocalRaid), target.Name);
        Assert.Equal(
            [typeof(MongoId), typeof(EndLocalRaidRequestData)],
            target.GetParameters().Select(parameter => parameter.ParameterType).ToArray());
    }

    [Fact]
    public void WebUiReconcilesTheWaitingBannerWithTheClientCommand()
    {
        string html = WebUI.BuildHtml("test-token");

        Assert.Contains("pendingActionCommandId=result.success?(result.commandId||''):''", html);
        Assert.Contains("command.status!=='Completed'", html);
        Assert.Contains("Craft added to the queue.", html);
        Assert.Contains("state.raidAutomationActive", html);
        Assert.Contains("Craft validated and added by the raid executor.", html);
        Assert.Contains("Active crafts cannot be aborted safely during a raid.", html);
    }

    [Fact]
    public void WebUiKeepsShowingCraftsFromTheProfileWhileEftIsAway()
    {
        string html = WebUI.BuildHtml("test-token");

        Assert.Contains("state.profileView", html);
        Assert.Contains("estimated from the saved profile", html);
        Assert.Contains("EFT must be connected to abort a craft.", html);
        Assert.Contains("EFT must be connected to queue a craft.", html);
        Assert.Contains("data-rate=", html);
    }

    [Fact]
    public void WebUiGreysOutRecipesWithoutMaterials()
    {
        string html = WebUI.BuildHtml("test-token");

        Assert.Contains("state.materialAvailability", html);
        Assert.Contains("entry${craftable?'':' unavailable'}", html);
        Assert.Contains("Not enough materials in the stash.", html);
        Assert.Contains("if(!Array.isArray(have))return true;", html);
        Assert.Contains("${have[index]}/${req.count}", html);
    }

    [Fact]
    public void WebUiFailedCommandsCanBeDismissed()
    {
        string html = WebUI.BuildHtml("test-token");

        Assert.Contains("data-action=\"dismiss-command\"", html);
        Assert.Contains("/api/commands/dismiss", html);
        Assert.Contains("command.status==='Completed'?`<button", html);
    }

    [Fact]
    public void WebUiRecipeBrowserGroupsCollapseAndRegroup()
    {
        string html = WebUI.BuildHtml("test-token");

        Assert.Contains("id=\"groupMode\"", html);
        Assert.Contains("value=\"station\"", html);
        Assert.Contains("id=\"sortMode\"", html);
        Assert.Contains("data-toggle-group", html);
        Assert.Contains("searching||expandedGroups.has(name)", html);
        Assert.Contains("localStorage.getItem('craftqueue-expanded')", html);
    }

    [Fact]
    public void WebUiShowsAPausedCraftInsteadOfCountingItDown()
    {
        string html = WebUI.BuildHtml("test-token");

        Assert.Contains("'Paused'", html);
        Assert.Contains("paused, the generator is off", html);
        Assert.Contains("if(Number(element.dataset.rate)<=0)return;", html);
    }
}
