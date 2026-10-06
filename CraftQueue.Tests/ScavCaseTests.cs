using CraftQueue.Server;
using SPTarkov.Server.Core.Callbacks;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Eft.Common;
using SPTarkov.Server.Core.Models.Eft.Hideout;
using SPTarkov.Server.Core.Models.Eft.ItemEvent;
using System.Reflection;
using Xunit;

namespace CraftQueue.Tests;
public sealed class ScavCaseTests
{
    private static readonly MongoId Roubles = new("5449016a4bdc2d6f028b456f");
    private static readonly MongoId Moonshine = new("5d1b376e86f774252519444e");

    private static int cost(MongoId template, int count, double discount)
    {
        MethodInfo method = typeof(CraftQueueService).GetMethod(
            "scavCaseCost",
            BindingFlags.Static | BindingFlags.NonPublic)!;
        var requirement = new Requirement { Type = "Item", TemplateId = template, Count = count };
        return (int)method.Invoke(null, [requirement, discount])!;
    }

    [Fact]
    public void EliteCharismaTakesItsShareOffMoneyOnly()
    {
        Assert.Equal(2250, cost(Roubles, 2500, 0.1));
        Assert.Equal(13500, cost(Roubles, 15000, 0.1));
        Assert.Equal(85500, cost(Roubles, 95000, 0.1));
        Assert.Equal(1, cost(Moonshine, 1, 0.1));
        Assert.Equal(95000, cost(Roubles, 95000, 0));
        Assert.Equal(1, cost(Roubles, 1, 0.5));
    }

    [Fact]
    public void ARunIsNamedAfterWhatItCosts()
    {
        MethodInfo name = typeof(CraftQueueService).GetMethod(
            "scavCaseName",
            BindingFlags.Static | BindingFlags.NonPublic)!;

        Assert.Equal("Scav case: 95,000 Roubles",
            name.Invoke(null, [new RecipeRequirement { Name = "Roubles", Count = 95000 }]));
        Assert.Equal("Scav case: Intelligence folder",
            name.Invoke(null, [new RecipeRequirement { Name = "Intelligence folder", Count = 1 }]));
    }

    [Fact]
    public void AnEditedScavRecipeRebuildsTheCache()
    {
        MethodInfo fingerprint = typeof(CraftQueueService).GetMethod(
            "scavRecipeFingerprint",
            BindingFlags.Static | BindingFlags.NonPublic)!;
        long hash(Action<ScavRecipe> edit)
        {
            var recipe = new ScavRecipe
            {
                Id = new MongoId("62710a69adfbd4354d79c58e"),
                ProductionTime = 8100,
                Requirements = [new Requirement { Type = "Item", TemplateId = Roubles, Count = 95000 }]
            };
            edit(recipe);
            return (long)fingerprint.Invoke(null, [new List<ScavRecipe> { recipe }])!;
        }

        long baseline = hash(_ => { });
        Assert.Equal(baseline, hash(_ => { }));
        Assert.NotEqual(baseline, hash(recipe => recipe.ProductionTime = 4000));
        Assert.NotEqual(baseline, hash(recipe => recipe.Requirements![0].Count = 50000));
        Assert.NotEqual(baseline, hash(recipe => recipe.Requirements![0].TemplateId = Moonshine));
        Assert.Equal(0L, (long)fingerprint.Invoke(null, [null])!);
    }

    [Fact]
    public void ScavCaseStartsAreConfirmedThroughTheirOwnCallback()
    {
        MethodInfo resolver = typeof(ScavCaseStartConfirmationPatch).GetMethod(
            "GetTargetMethod",
            BindingFlags.Instance | BindingFlags.NonPublic)!;

        MethodBase target = (MethodBase)resolver.Invoke(new ScavCaseStartConfirmationPatch(), null)!;

        Assert.Equal(typeof(HideoutCallbacks), target.DeclaringType);
        Assert.Equal(nameof(HideoutCallbacks.ScavCaseProductionStart), target.Name);
        Assert.Equal(typeof(ValueTask<ItemEventRouterResponse>), ((MethodInfo)target).ReturnType);
        Assert.Equal(
            [typeof(PmcData), typeof(HideoutScavCaseStartRequestData), typeof(MongoId)],
            target.GetParameters().Select(parameter => parameter.ParameterType).ToArray());
    }

    [Fact]
    public void ThePanelPricesARunAndNeverOffersToAbortOne()
    {
        string html = WebUI.BuildHtml("test-token");

        Assert.Contains("if(recipe.isScavCase)return", html);
        Assert.Contains("random loot", html);
        Assert.Contains("A running scav case cannot be aborted.", html);
        Assert.Contains("const needs=state.requirementNeeds?.[recipe.recipeId];", html);
        Assert.Contains("(have[index]??0)>=needOf(recipe,index)", html);
    }
}
