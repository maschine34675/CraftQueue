using SPTarkov.Server.Core.Controllers;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Eft.Common;
using SPTarkov.Server.Core.Models.Eft.Common.Tables;
using SPTarkov.Server.Core.Models.Eft.Hideout;
using SPTarkov.Server.Core.Models.Eft.ItemEvent;
using System.Reflection;
using Xunit;

namespace CraftQueue.Tests;
public class CancelProductionFixTests
{
    [Fact]
    public void CancelProductionKeepsTheSignatureThePatchBindsTo()
    {
        MethodInfo? target = typeof(HideoutController).GetMethod(
            nameof(HideoutController.CancelProduction),
            [
                typeof(MongoId),
                typeof(PmcData),
                typeof(HideoutCancelProductionRequestData)
            ]);

        Assert.NotNull(target);
        Assert.Equal(typeof(ItemEventRouterResponse), target.ReturnType);
    }

    [Fact]
    public void ProductionStaysADictionarySoTheMissingCraftStillThrows()
    {
        PropertyInfo production = typeof(SPTarkov.Server.Core.Models.Eft.Common.Tables.Hideout).GetProperty("Production")!;

        Assert.True(
            production.PropertyType.IsGenericType
            && production.PropertyType.GetGenericTypeDefinition() == typeof(Dictionary<,>),
            $"HideoutController indexes Production[recipeId]; it is now {production.PropertyType.Name}.");
    }

    [Fact]
    public void CancelProductionStillDoesNotHandBackTheTools()
    {
        Assert.NotNull(typeof(Production).GetProperty(nameof(Production.SptRequiredTools)));
    }
}
