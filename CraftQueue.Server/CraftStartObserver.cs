using Microsoft.Extensions.Logging;
using SPTarkov.DI.Annotations;
using SPTarkov.Reflection.Patching;
using SPTarkov.Server.Core.Callbacks;
using SPTarkov.Server.Core.DI;
using SPTarkov.Server.Core.Helpers.Profile;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Eft.Hideout;
using SPTarkov.Server.Core.Models.Eft.ItemEvent;
using SPTarkov.Server.Core.Models.Eft.Match;
using SPTarkov.Server.Core.Routers;
using SPTarkov.Server.Core.Services.InRaid;
using SPTarkov.Server.Core.Services.Locales;
using SPTarkov.Server.Core.Utils;
using System;
using System.Reflection;
using System.Threading.Tasks;
using System.Threading;

namespace CraftQueue.Server
{
    [Injectable(TypePriority = OnLoadOrder.PostLoad)]
    public sealed class CraftStartObserver(
        CraftQueueService craftQueueService,
        EventOutputHolder eventOutputHolder,
        HttpResponseUtil httpResponseUtil,
        InventoryHelper inventoryHelper,
        ServerLocalisationService serverLocalisationService,
        ILogger<CraftStartObserver> logger) : IOnLoad
    {
        public Task OnLoadAsync(CancellationToken cancellationToken)
        {
            HideoutStartConfirmationPatch.Configure(craftQueueService, logger);
            ScavCaseStartConfirmationPatch.Configure(craftQueueService, logger);
            LocalRaidStartPatch.Configure(craftQueueService, logger);
            LocalRaidEndPatch.Configure(craftQueueService, logger);
            new HideoutStartConfirmationPatch().Enable();
            new ScavCaseStartConfirmationPatch().Enable();
            new LocalRaidStartPatch().Enable();
            new LocalRaidEndPatch().Enable();
            logger.LogInformation("[CraftQueue] Installed production confirmation and raid handover hooks.");
            try
            {
                Patches.CancelProductionMissingCraftPatch.Configure(eventOutputHolder, httpResponseUtil, inventoryHelper, serverLocalisationService, logger);
                new Patches.CancelProductionMissingCraftPatch().Enable();
                logger.LogInformation("[CraftQueue] Installed the craft cancel fix for SPT's missing-craft indexer.");
            }
            catch (Exception ex)
            {
                logger.LogError(
                    ex,
                    "[CraftQueue] Could not install the craft cancel fix; cancelling a craft may fail inside SPT until this is updated.");
            }

            return Task.CompletedTask;
        }
    }

    public sealed class HideoutStartConfirmationPatch : AbstractPatch
    {
        private static CraftQueueService? craftQueueService;
        private static ILogger<CraftStartObserver>? logger;

        public static void Configure(
            CraftQueueService service,
            ILogger<CraftStartObserver> observerLogger)
        {
            craftQueueService = service;
            logger = observerLogger;
        }

        protected override MethodBase GetTargetMethod()
        {
            return typeof(HideoutCallbacks).GetMethod(
                nameof(HideoutCallbacks.SingleProductionStart),
                [
                    typeof(SPTarkov.Server.Core.Models.Eft.Common.PmcData),
                    typeof(HideoutSingleProductionStartRequestData),
                    typeof(MongoId)
                ])!;
        }

        [PatchPostfix]
        public static void Postfix(
            HideoutSingleProductionStartRequestData request,
            MongoId sessionID,
            ValueTask<ItemEventRouterResponse> __result)
        {
            if (__result.Result?.Warnings?.Count > 0)
                return;

            try
            {
                craftQueueService?.ConfirmStarted(sessionID, request.RecipeId.ToString());
            }
            catch (Exception ex)
            {
                logger?.LogError(ex, "[CraftQueue] Failed to reconcile a successful hideout start request.");
            }
        }
    }
    public sealed class ScavCaseStartConfirmationPatch : AbstractPatch
    {
        private static CraftQueueService? craftQueueService;
        private static ILogger<CraftStartObserver>? logger;

        public static void Configure(
            CraftQueueService service,
            ILogger<CraftStartObserver> observerLogger)
        {
            craftQueueService = service;
            logger = observerLogger;
        }

        protected override MethodBase GetTargetMethod()
        {
            return typeof(HideoutCallbacks).GetMethod(
                nameof(HideoutCallbacks.ScavCaseProductionStart),
                [
                    typeof(SPTarkov.Server.Core.Models.Eft.Common.PmcData),
                    typeof(HideoutScavCaseStartRequestData),
                    typeof(MongoId)
                ])!;
        }

        [PatchPostfix]
        public static void Postfix(
            HideoutScavCaseStartRequestData request,
            MongoId sessionID,
            ValueTask<ItemEventRouterResponse> __result)
        {
            if (__result.Result?.Warnings?.Count > 0)
                return;

            try
            {
                craftQueueService?.ConfirmStarted(sessionID, request.RecipeId.ToString());
            }
            catch (Exception ex)
            {
                logger?.LogError(ex, "[CraftQueue] Failed to reconcile a successful scav case start request.");
            }
        }
    }

    public sealed class LocalRaidStartPatch : AbstractPatch
    {
        private static CraftQueueService? craftQueueService;
        private static ILogger<CraftStartObserver>? logger;

        public static void Configure(
            CraftQueueService service,
            ILogger<CraftStartObserver> observerLogger)
        {
            craftQueueService = service;
            logger = observerLogger;
        }

        protected override MethodBase GetTargetMethod()
        {
            return typeof(LocationLifecycleService).GetMethod(
                nameof(LocationLifecycleService.StartLocalRaidAsync),
                [
                    typeof(MongoId),
                    typeof(StartLocalRaidRequestData),
                    typeof(CancellationToken)
                ])!;
        }

        [PatchPostfix]
        public static void Postfix(MongoId sessionId)
        {
            try
            {
                craftQueueService?.SetRaidState(sessionId, true);
            }
            catch (Exception ex)
            {
                logger?.LogError(ex, "[CraftQueue] Failed to enable the server raid handover.");
            }
        }
    }

    public sealed class LocalRaidEndPatch : AbstractPatch
    {
        private static CraftQueueService? craftQueueService;
        private static ILogger<CraftStartObserver>? logger;

        public static void Configure(
            CraftQueueService service,
            ILogger<CraftStartObserver> observerLogger)
        {
            craftQueueService = service;
            logger = observerLogger;
        }

        protected override MethodBase GetTargetMethod()
        {
            return typeof(LocationLifecycleService).GetMethod(
                nameof(LocationLifecycleService.EndLocalRaidAsync),
                [
                    typeof(MongoId),
                    typeof(EndLocalRaidRequestData),
                    typeof(CancellationToken)
                ])!;
        }

        [PatchPrefix]
        public static void Prefix(MongoId sessionId)
        {
            try
            {
                craftQueueService?.SetRaidState(sessionId, false);
            }
            catch (Exception ex)
            {
                logger?.LogError(ex, "[CraftQueue] Failed to stop the server raid handover before profile reconciliation.");
            }
        }
    }
}
