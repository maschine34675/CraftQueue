using CraftQueue.Server.Patches;
using SPTarkov.Server.Core.Models.Eft.Common.Request;
using SPTarkov.Server.Core.Models.Eft.Hideout;
using SPTarkov.Server.Core.Models.Enums;
using SPTarkov.Server.Core.Utils;
using SPTarkov.Server.Core.Utils.Json;
using SPTarkov.Server.Core.Utils.Json.Converters;
using System.Text.Json.Serialization;
using System.Reflection;
using System.Text.Json;
using Xunit;

namespace CraftQueue.Tests;
public sealed class CancelProductionPatchTests
{
    private const string CancelBody = """
    {"Action":"HideoutCancelProductionCommand","recipeId":"655b4bbe4343a16d2e047668","timestamp":1785000000}
    """;
    private static readonly JsonSerializerOptions jsonOptions = new()
    {
        Converters = { new StringToMongoIdConverter() }
    };

    private static JsonSerializerOptions options() => jsonOptions;

    [Fact]
    public void SptStillDeserializesACancelBodyIntoTheWrongType()
    {
        BaseInteractionRequestData? converted = convertLikeSpt(CancelBody);

        Assert.NotNull(converted);
        Assert.IsNotType<HideoutCancelProductionRequestData>(converted);
    }

    [Fact]
    public void PatchRestoresTheCancelRequestWithItsRecipeId()
    {
        BaseInteractionRequestData? converted = convertLikeSpt(CancelBody);

        CancelProductionRequestPatch.Postfix(
            HideoutEventActions.HIDEOUT_CANCEL_PRODUCTION_COMMAND,
            CancelBody,
            options(),
            ref converted);

        HideoutCancelProductionRequestData request = Assert.IsType<HideoutCancelProductionRequestData>(converted);
        Assert.Equal("655b4bbe4343a16d2e047668", request.RecipeId.ToString());
    }

    [Fact]
    public void PatchLeavesOtherActionsUntouched()
    {
        BaseInteractionRequestData? converted = convertLikeSpt(CancelBody);
        BaseInteractionRequestData? original = converted;

        CancelProductionRequestPatch.Postfix(
            HideoutEventActions.HIDEOUT_IMPROVE_AREA,
            CancelBody,
            options(),
            ref converted);

        Assert.Same(original, converted);
    }

    [Fact]
    public void PatchIsIdempotentOnAnAlreadyCorrectRequest()
    {
        BaseInteractionRequestData? converted =
            JsonSerializer.Deserialize<HideoutCancelProductionRequestData>(CancelBody, options());
        BaseInteractionRequestData? original = converted;

        CancelProductionRequestPatch.Postfix(
            HideoutEventActions.HIDEOUT_CANCEL_PRODUCTION_COMMAND,
            CancelBody,
            options(),
            ref converted);

        Assert.Same(original, converted);
    }

    [Fact]
    public void EnablingThePatchActuallyRepairsSptsOwnDeserialization()
    {
        var options = new JsonSerializerOptions();
        foreach (JsonConverter converter in new SptJsonConverterRegistrator().GetJsonConverters())
            options.Converters.Add(converter);

        var patch = new CancelProductionRequestPatch();
        patch.Enable();
        try
        {
            BaseInteractionRequestData? parsed =
                JsonSerializer.Deserialize<BaseInteractionRequestData>(CancelBody, options);

            HideoutCancelProductionRequestData request =
                Assert.IsType<HideoutCancelProductionRequestData>(parsed);
            Assert.Equal("655b4bbe4343a16d2e047668", request.RecipeId.ToString());
        }
        finally
        {
            patch.Disable();
        }
    }

    [Fact]
    public void PatchTargetsTheRequestConverter()
    {
        MethodInfo resolver = typeof(CancelProductionRequestPatch).GetMethod(
            "GetTargetMethod",
            BindingFlags.Instance | BindingFlags.NonPublic)!;

        MethodBase target = (MethodBase)resolver.Invoke(new CancelProductionRequestPatch(), null)!;

        Assert.Equal(typeof(BaseInteractionRequestDataConverter), target.DeclaringType);
        Assert.Equal("ConvertToCorrectType", target.Name);
    }

    [Fact]
    public void MissingCraftPatchTargetsTheController()
    {
        MethodInfo resolver = typeof(CancelProductionMissingCraftPatch).GetMethod(
            "GetTargetMethod",
            BindingFlags.Instance | BindingFlags.NonPublic)!;

        MethodBase target = (MethodBase)resolver.Invoke(new CancelProductionMissingCraftPatch(), null)!;

        Assert.Equal(typeof(SPTarkov.Server.Core.Controllers.HideoutController), target.DeclaringType);
        Assert.Equal("CancelProduction", target.Name);
    }

    [Fact]
    public void UnconfiguredPrefixNeverBlocksTheOriginal()
    {
        var result = default(SPTarkov.Server.Core.Models.Eft.ItemEvent.ItemEventRouterResponse);

        bool runOriginal = CancelProductionMissingCraftPatch.Prefix(
            default,
            new SPTarkov.Server.Core.Models.Eft.Common.PmcData(),
            new HideoutCancelProductionRequestData(),
            ref result!);

        Assert.True(runOriginal);
    }

    private static BaseInteractionRequestData? convertLikeSpt(string json)
    {
        MethodInfo convert = typeof(BaseInteractionRequestDataConverter).GetMethod(
            "ConvertToCorrectType",
            BindingFlags.Static | BindingFlags.NonPublic)!;

        using JsonDocument document = JsonDocument.Parse(json);
        return (BaseInteractionRequestData?)convert.Invoke(
            null,
            [HideoutEventActions.HIDEOUT_CANCEL_PRODUCTION_COMMAND, document.RootElement, json, options()]);
    }
}
