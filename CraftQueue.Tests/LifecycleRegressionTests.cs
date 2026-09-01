using CraftQueue.Server;
using SPTarkov.Server.Core.Callbacks;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Eft.Common;
using SPTarkov.Server.Core.Models.Eft.Common.Tables;
using SPTarkov.Server.Core.Models.Eft.Hideout;
using SPTarkov.Server.Core.Models.Eft.ItemEvent;
using SPTarkov.Server.Core.Models.Eft.Match;
using SPTarkov.Server.Core.Services.InRaid;
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
        Assert.Equal(typeof(ValueTask<ItemEventRouterResponse>), ((MethodInfo)target).ReturnType);
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
        Assert.Equal(nameof(LocationLifecycleService.StartLocalRaidAsync), target.Name);
        Assert.Equal(
            [typeof(MongoId), typeof(StartLocalRaidRequestData), typeof(CancellationToken)],
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
        Assert.Equal(nameof(LocationLifecycleService.EndLocalRaidAsync), target.Name);
        Assert.Equal(
            [typeof(MongoId), typeof(EndLocalRaidRequestData), typeof(CancellationToken)],
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
    public void CraftEconomicsIgnoreToolsAndScaleProfitToAnHour()
    {
        MethodInfo apply = typeof(CraftQueueService).GetMethod(
            "applyEconomics",
            BindingFlags.Static | BindingFlags.NonPublic)!;

        var material = new MongoId("5449016a4bdc2d6f028b456f");
        var tool = new MongoId("544fb5454bdc2df8738b456a");
        var product = new MongoId("59faff1d86f7746c51718c9c");

        var info = new RecipeInfo
        {
            ProductionTime = 1800,
            ProductCount = 2,
            Requirements =
            [
                new RecipeRequirement { TemplateId = material.ToString(), Count = 3 },
                new RecipeRequirement { TemplateId = tool.ToString(), Count = 1, IsTool = true }
            ]
        };

        Func<MongoId, int> price = id =>
            id == material ? 1000 : id == tool ? 500000 : id == product ? 4000 : 0;

        apply.Invoke(null, [info, product, price]);

        Assert.Equal(3000, info.MaterialCost);
        Assert.Equal(8000, info.ProductValue);
        Assert.Equal(5000, info.Profit);
        Assert.Equal(10000, info.ProfitPerHour);
        var instant = new RecipeInfo { ProductionTime = 0, ProductCount = 1 };
        apply.Invoke(null, [instant, product, price]);
        Assert.Equal(0, instant.ProfitPerHour);
        Assert.Equal(4000, instant.Profit);
        var unpriced = new RecipeInfo
        {
            ProductionTime = 3600,
            ProductCount = 1,
            Requirements = [new RecipeRequirement { TemplateId = material.ToString(), Count = 2 }]
        };
        apply.Invoke(null, [unpriced, new MongoId("5449016a4bdc2d6f028b4570"), price]);
        Assert.Equal(2000, unpriced.MaterialCost);
        Assert.Equal(0, unpriced.ProductValue);
        Assert.Equal(0, unpriced.Profit);
        Assert.Equal(0, unpriced.ProfitPerHour);
    }

    [Fact]
    public void CraftsSpendPlainMaterialsBeforeFoundInRaidOnes()
    {
        MethodInfo sortKey = typeof(CraftQueueService).GetMethod(
            "preferNonFoundInRaid",
            BindingFlags.Static | BindingFlags.NonPublic)!;

        var foundInRaid = new Item { Id = new MongoId(), Upd = new Upd { SpawnedInSession = true } };
        var plain = new Item { Id = new MongoId(), Upd = new Upd { SpawnedInSession = false } };
        var noUpd = new Item { Id = new MongoId() };

        int firKey = (int)sortKey.Invoke(null, [foundInRaid])!;
        int plainKey = (int)sortKey.Invoke(null, [plain])!;
        int noUpdKey = (int)sortKey.Invoke(null, [noUpd])!;

        Assert.True(plainKey < firKey, "A plain item must be spent before a found-in-raid one.");
        Assert.Equal(plainKey, noUpdKey);
        Item[] stash = [foundInRaid, plain, noUpd];
        Item[] ordered = [.. stash.OrderBy(item => (int)sortKey.Invoke(null, [item])!)];
        Assert.Equal([plain.Id, noUpd.Id, foundInRaid.Id], ordered.Select(item => item.Id));
    }

    [Fact]
    public void ToolsBorrowedByRunningCraftsCountAsAvailable()
    {
        MethodInfo held = typeof(CraftQueueService).GetMethod(
            "getToolsHeldByRunningCrafts",
            BindingFlags.Static | BindingFlags.NonPublic)!;

        var leatherman = new MongoId("544fb5454bdc2df8738b456a");
        var pmcData = new PmcData
        {
            Hideout = new SPTarkov.Server.Core.Models.Eft.Common.Tables.Hideout
            {
                Production = new Dictionary<MongoId, Production?>
                {
                    [new MongoId("655b56eb32b0b1645e6f54c8")] = new Production
                    {
                        InProgress = true,
                        SptRequiredTools = [new Item { Id = new MongoId(), Template = leatherman }]
                    },
                    [new MongoId("5d5589c1f934db006d2d5b32")] = new Production
                    {
                        InProgress = false,
                        SptRequiredTools = [new Item { Id = new MongoId(), Template = leatherman }]
                    }
                }
            }
        };

        var result = (Dictionary<MongoId, int>)held.Invoke(null, [pmcData])!;

        Assert.Equal(1, result[leatherman]);
    }

    [Fact]
    public void WebUiShowsHowManyItemsACraftYields()
    {
        string html = WebUI.BuildHtml("test-token");

        Assert.Contains("function yieldTag(recipe){", html);
        Assert.Contains("×${count}</span>`:'';", html);
        Assert.Contains("<span class=\"name\">${esc(recipe.productName)}</span>${yieldTag(recipe)}", html);
        Assert.Contains("<span class=\"name\">${esc(production.productName)}</span>${yieldTag(recipe)}", html);
        Assert.Contains(".entry-name > .name { overflow:hidden;", html);
        Assert.Contains(".entry-name > .badge,.entry-yield { flex:0 0 auto; }", html);
        Assert.DoesNotContain("${money(value)}${count}", html);
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
