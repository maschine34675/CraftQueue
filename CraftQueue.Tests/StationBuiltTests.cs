using CraftQueue.Server;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Eft.Common.Tables;
using SPTarkov.Server.Core.Models.Eft.Hideout;
using SPTarkov.Server.Core.Models.Enums.Hideout;
using System.Reflection;
using Xunit;

namespace CraftQueue.Tests;
public sealed class StationBuiltTests
{
    private static int requiredLevel(HideoutProduction recipe)
    {
        MethodInfo method = typeof(CraftQueueService).GetMethod(
            "requiredStationLevel",
            BindingFlags.Static | BindingFlags.NonPublic)!;
        return (int)method.Invoke(null, [recipe])!;
    }

    private static string? block(List<BotHideoutArea>? stations, HideoutAreas area, int required)
    {
        MethodInfo method = typeof(CraftQueueService).GetMethod(
            "stationLevelBlock",
            BindingFlags.Static | BindingFlags.NonPublic)!;
        return (string?)method.Invoke(null, [stations, area, required]);
    }

    private static HideoutProduction recipe(HideoutAreas area, params Requirement[] requirements) => new()
    {
        Id = new MongoId("5d5c1fd4d582a500650132f0"),
        AreaType = area,
        Requirements = [.. requirements]
    };

    private static Requirement areaRequirement(HideoutAreas area, int level) => new()
    {
        Type = "Area",
        AreaType = (int)area,
        RequiredLevel = level
    };

    [Fact]
    public void ARecipeWithoutALevelOfItsOwnStillNeedsTheStationBuilt()
    {
        Assert.Equal(1, requiredLevel(recipe(
            HideoutAreas.BoozeGenerator,
            new Requirement { Type = "Item", TemplateId = new MongoId("59e3577886f774176a362503"), Count = 2 })));
        Assert.Equal(1, requiredLevel(new HideoutProduction { AreaType = HideoutAreas.ScavCase }));
    }

    [Fact]
    public void TheRecipesOwnAreaRequirementSetsTheLevel()
    {
        Assert.Equal(2, requiredLevel(recipe(HideoutAreas.Workbench, areaRequirement(HideoutAreas.Workbench, 2))));
        Assert.Equal(1, requiredLevel(recipe(HideoutAreas.Workbench, areaRequirement(HideoutAreas.Workbench, 0))));
        Assert.Equal(1, requiredLevel(recipe(HideoutAreas.Workbench, areaRequirement(HideoutAreas.Generator, 3))));
    }

    [Fact]
    public void AStationThatWasNeverBuiltHoldsItsEntries()
    {
        var stations = new List<BotHideoutArea>
        {
            new() { Type = HideoutAreas.ScavCase, Level = 0 },
            new() { Type = HideoutAreas.BoozeGenerator, Level = null },
            new() { Type = HideoutAreas.Workbench, Level = 1 }
        };

        const string notBuilt = "Waiting for construction: this station has not been built yet.";
        Assert.Equal(notBuilt, block(stations, HideoutAreas.ScavCase, 1));
        Assert.Equal(notBuilt, block(stations, HideoutAreas.BoozeGenerator, 1));
        Assert.Null(block(stations, HideoutAreas.Workbench, 1));
        Assert.Equal("Waiting for an upgrade: this recipe needs station level 2.", block(stations, HideoutAreas.Workbench, 2));
    }

    [Fact]
    public void AProfileWithoutAnyStationIsNotMistakenForADisconnectedGame()
    {
        string html = WebUI.BuildHtml("test-token");

        Assert.Contains("if(available.size===0)", html);
        Assert.Contains("No recipes yet: build a crafting station in the hideout first.", html);
        Assert.NotNull(typeof(CraftQueueService).Assembly
            .GetType("CraftQueue.Server.ClientTelemetrySnapshot")!
            .GetProperty("RecipeListKnown"));
    }

    [Fact]
    public void AnUnknownStationIsLeftToTheClient()
    {
        Assert.Null(block([new BotHideoutArea { Type = HideoutAreas.Workbench, Level = 3 }], HideoutAreas.ScavCase, 1));
        Assert.Null(block(null, HideoutAreas.ScavCase, 1));
    }
}
