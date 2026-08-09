using HarmonyLib;
using Microsoft.Extensions.Logging;
using SPTarkov.Reflection.Patching;
using SPTarkov.Server.Core.Controllers;
using SPTarkov.Server.Core.Helpers;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Eft.Common;
using SPTarkov.Server.Core.Models.Eft.Common.Request;
using SPTarkov.Server.Core.Models.Eft.Common.Tables;
using SPTarkov.Server.Core.Models.Eft.Hideout;
using SPTarkov.Server.Core.Models.Eft.Inventory;
using SPTarkov.Server.Core.Models.Eft.ItemEvent;
using SPTarkov.Server.Core.Models.Enums;
using SPTarkov.Server.Core.Routers;
using SPTarkov.Server.Core.Services;
using SPTarkov.Server.Core.Utils;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using SPTarkov.Server.Core.Helpers.Profile;
using SPTarkov.Server.Core.Services.Locales;

namespace CraftQueue.Server.Patches
{
    public sealed class CancelProductionMissingCraftPatch : AbstractPatch
    {
        private static EventOutputHolder? eventOutputHolder;
        private static HttpResponseUtil? httpResponseUtil;
        private static InventoryHelper? inventoryHelper;
        private static ServerLocalisationService? serverLocalisationService;
        private static ILogger? logger;

        public static void Configure(
            EventOutputHolder outputHolder,
            HttpResponseUtil responseUtil,
            InventoryHelper inventory,
            ServerLocalisationService localisation,
            ILogger patchLogger)
        {
            eventOutputHolder = outputHolder;
            httpResponseUtil = responseUtil;
            inventoryHelper = inventory;
            serverLocalisationService = localisation;
            logger = patchLogger;
        }

        protected override MethodBase GetTargetMethod()
        {
            return AccessTools.Method(typeof(HideoutController), nameof(HideoutController.CancelProduction))
                ?? throw new InvalidOperationException(
                    "HideoutController.CancelProduction was not found; the CraftQueue cancel fix needs updating for this SPT version.");
        }

        [PatchPrefix]
        public static bool Prefix(MongoId sessionId, PmcData pmcData, HideoutCancelProductionRequestData request, ref ItemEventRouterResponse __result)
        {
            if (eventOutputHolder == null || httpResponseUtil == null)
                return true;

            if (request == null
                || pmcData.Hideout?.Production == null
                || !pmcData.Hideout.Production.TryGetValue(request.RecipeId, out Production? craft)
                || craft == null)
            {
                string recipeId = request == null ? "<missing>" : request.RecipeId.ToString();
                logger?.LogWarning("[CraftQueue] Ignored a cancel request for craft {RecipeId}, which is no longer running.", recipeId);
                __result = httpResponseUtil.AppendErrorToOutput(
                    eventOutputHolder.GetOutput(sessionId),
                    $"Unable to find craft {recipeId} to cancel");
                return false;
            }

            return returnToolsBeforeCancel(sessionId, pmcData, craft, request.RecipeId, ref __result);
        }
        private static bool returnToolsBeforeCancel(
            MongoId sessionId,
            PmcData pmcData,
            Production craft,
            MongoId recipeId,
            ref ItemEventRouterResponse __result)
        {
            if (inventoryHelper == null || craft.SptRequiredTools is not { Count: > 0 } tools)
                return true;

            List<List<Item>> toolGroups = tools.Select(tool => new List<Item> { tool }).ToList();
            ItemEventRouterResponse output = eventOutputHolder!.GetOutput(sessionId);

            if (!inventoryHelper.CanPlaceItemsInInventory(sessionId, toolGroups))
            {
                logger?.LogWarning(
                    "[CraftQueue] Refused to cancel craft {RecipeId}: the stash has no space for its {Count} tool(s).",
                    recipeId,
                    toolGroups.Count);
                __result = httpResponseUtil!.AppendErrorToOutput(
                    output,
                    serverLocalisationService?.GetText("inventory-no_stash_space") ?? "Not enough stash space to return the craft's tools.",
                    BackendErrorCodes.NotEnoughSpace);
                return false;
            }

            foreach (List<Item> toolGroup in toolGroups)
            {
                inventoryHelper.AddItemsToStash(
                    sessionId,
                    new AddItemsDirectRequest
                    {
                        ItemsWithModsToAdd = [toolGroup],
                        FoundInRaid = toolGroup.FirstOrDefault()?.Upd?.SpawnedInSession ?? false,
                        UseSortingTable = false,
                        Callback = null,
                    },
                    pmcData,
                    output);
                if (output.Warnings?.Count > 0)
                {
                    logger?.LogWarning(
                        "[CraftQueue] Aborted the cancel of craft {RecipeId}: returning a tool failed unexpectedly.",
                        recipeId);
                    __result = output;
                    return false;
                }
            }

            logger?.LogInformation(
                "[CraftQueue] Returned {Count} tool(s) to the stash before cancelling craft {RecipeId}.",
                toolGroups.Count,
                recipeId);
            return true;
        }
    }
}
