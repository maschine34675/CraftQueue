using CraftQueue.Server.Models;
using CraftQueue.Server.Stores;
using Microsoft.Extensions.Logging;
using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.Controllers;
using SPTarkov.Server.Core.Helpers;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Eft.Common;
using SPTarkov.Server.Core.Models.Eft.Common.Tables;
using SPTarkov.Server.Core.Models.Eft.Hideout;
using SPTarkov.Server.Core.Models.Enums;
using SPTarkov.Server.Core.Models.Enums.Hideout;
using SPTarkov.Server.Core.Routers;
using SPTarkov.Server.Core.Servers;
using SPTarkov.Server.Core.Services;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;

namespace CraftQueue.Server
{
    [Injectable(InjectionType = InjectionType.Singleton)]
    public class CraftQueueService(
        CraftQueueStore craftQueueStore,
        DatabaseService databaseService,
        ItemHelper itemHelper,
        HideoutHelper hideoutHelper,
        HideoutController hideoutController,
        EventOutputHolder eventOutputHolder,
        SaveServer saveServer,
        ILogger<CraftQueueService> logger)
    {
        private const int ClaimLeaseSeconds = 30;
        private const int StartConfirmationSeconds = 120;
        private const int TelemetryFreshSeconds = 35;
        private const int AreaStartGraceSeconds = 15;
        private const int EnvironmentalRetrySeconds = 10;
        private const int MaximumFastDeferrals = 3;
        private const int RaidHandoverSeconds = 5;
        private const int RaidPollRecoverySeconds = 30;
        private const int MaximumRaidSeconds = 3 * 60 * 60;

        private const int CollectRetrySeconds = 60;
        private const int CommandRetentionSeconds = 300;
        private const int MaximumPendingCommands = 100;
        private const int MaximumQueueEntries = 1000;
        private const int MaximumTelemetryProductions = 64;
        private const int MaximumAvailableRecipes = 6000;

        private readonly ConcurrentDictionary<string, SessionRuntime> sessions = new(StringComparer.Ordinal);
        private readonly object recipeCacheLock = new();
        private Dictionary<string, List<RecipeInfo>>? cachedRecipes;
        private Dictionary<string, RecipeInfo>? cachedRecipeLookup;
        private Dictionary<string, HideoutProduction>? cachedProductionLookup;

        public QueueOperationResult CreateEnqueueCommand(MongoId sessionId, string recipeId)
        {
            if (string.IsNullOrWhiteSpace(recipeId) || GetRecipeById(recipeId) == null)
                return QueueOperationResult.Fail("Recipe not found.");

            SessionRuntime runtime = getRuntime(sessionId);
            lock (runtime.Sync)
            {
                long now = unixNow();
                cleanupRuntime(runtime, now);
                if (runtime.InRaid)
                    return enqueueDuringRaid(sessionId, runtime, recipeId, now);
                if (!hasFreshTelemetry(runtime, now))
                    return QueueOperationResult.Fail("The selected profile is not connected to the CraftQueue client.");
                if (!runtime.Telemetry!.AvailableRecipeIds.Contains(recipeId))
                    return QueueOperationResult.Fail("This recipe is not available for the selected profile.");
                if (countQueueEntries(craftQueueStore.Get(sessionId)) >= MaximumQueueEntries)
                    return QueueOperationResult.Fail($"The queue limit of {MaximumQueueEntries} entries has been reached.");

                return addCommand(runtime, ClientCommandKind.Enqueue, recipeId, null, now, 120);
            }
        }

        public QueueOperationResult CreateCancelActiveCommand(MongoId sessionId, string recipeId, string instanceId)
        {
            if (string.IsNullOrWhiteSpace(recipeId) || string.IsNullOrWhiteSpace(instanceId))
                return QueueOperationResult.Fail("The active production could not be identified.");

            SessionRuntime runtime = getRuntime(sessionId);
            lock (runtime.Sync)
            {
                long now = unixNow();
                cleanupRuntime(runtime, now);
                if (runtime.InRaid)
                    return QueueOperationResult.Fail("An active craft cannot be aborted safely while EFT is in a raid.");
                if (!hasFreshTelemetry(runtime, now))
                    return QueueOperationResult.Fail("The selected profile is not connected to the CraftQueue client.");

                bool exists = runtime.Telemetry!.Productions.Any(
                    production => production.RecipeId == recipeId && production.InstanceId == instanceId);
                if (!exists)
                    return QueueOperationResult.Fail("That production is no longer active.");

                ClientCommand? existing = runtime.Commands.Values.FirstOrDefault(command =>
                    command.Kind == ClientCommandKind.CancelActive
                    && command.Status != ClientCommandStatus.Completed
                    && command.InstanceId == instanceId);
                if (existing != null)
                    return QueueOperationResult.Ok(existing.CommandId);

                return addCommand(runtime, ClientCommandKind.CancelActive, recipeId, instanceId, now, 60);
            }
        }

        public QueueOperationResult CompleteClientCommand(MongoId sessionId, ClientCommandResultRequestData result)
        {
            if (string.IsNullOrWhiteSpace(result.CommandId))
                return QueueOperationResult.Fail("Missing command id.");

            SessionRuntime runtime = getRuntime(sessionId);
            lock (runtime.Sync)
            {
                long now = unixNow();
                cleanupRuntime(runtime, now);
                if (!runtime.Commands.TryGetValue(result.CommandId, out ClientCommand? command))
                    return QueueOperationResult.Fail("The command is unknown or has expired.");
                if (command.Status == ClientCommandStatus.Completed)
                    return command.Success == true
                        ? QueueOperationResult.Ok(command.CommandId)
                        : QueueOperationResult.Fail(command.Error ?? "The command failed.");

                if (result.Success && command.Kind == ClientCommandKind.Enqueue)
                {
                    RecipeInfo? recipe = GetRecipeById(command.RecipeId);
                    if (recipe == null)
                    {
                        completeCommand(command, false, "Recipe metadata is no longer available.", now);
                        return QueueOperationResult.Fail(command.Error ?? "Recipe metadata is no longer available.");
                    }

                    CraftQueueData data = craftQueueStore.Get(sessionId);
                    if (countQueueEntries(data) >= MaximumQueueEntries)
                    {
                        completeCommand(command, false, $"The queue limit of {MaximumQueueEntries} entries has been reached.", now);
                        return QueueOperationResult.Fail(command.Error ?? "The queue limit has been reached.");
                    }

                    if (appendQueueEntry(data, recipe, command.CommandId, now, out int queueCount))
                    {
                        craftQueueStore.Set(sessionId);
                        logger.LogInformation(
                            "[CraftQueue] Enqueued {RecipeId} in area {Area} (queue size: {Count}).",
                            command.RecipeId,
                            (HideoutAreas)recipe.AreaType,
                            queueCount);
                    }
                }

                completeCommand(command, result.Success, result.Success ? null : sanitizeError(result.Error), now);
                return result.Success
                    ? QueueOperationResult.Ok(command.CommandId)
                    : QueueOperationResult.Fail(command.Error ?? "The client rejected the command.");
            }
        }

        public ClientPollResponse Poll(MongoId sessionId, ClientPollRequestData request)
        {
            SessionRuntime runtime = getRuntime(sessionId);
            lock (runtime.Sync)
            {
                long now = unixNow();
                bool needRecipeIds = false;
                HashSet<string> availableRecipeIds;
                if (request.AvailableRecipeIds != null)
                {
                    availableRecipeIds = request.AvailableRecipeIds
                        .Where(id => !string.IsNullOrWhiteSpace(id))
                        .Distinct(StringComparer.Ordinal)
                        .Take(MaximumAvailableRecipes)
                        .ToHashSet(StringComparer.Ordinal);
                    runtime.RecipeIdsHash = request.RecipeIdsHash;
                }
                else if (!string.IsNullOrEmpty(request.RecipeIdsHash)
                    && request.RecipeIdsHash == runtime.RecipeIdsHash
                    && runtime.Telemetry != null)
                {
                    availableRecipeIds = runtime.Telemetry.AvailableRecipeIds;
                }
                else
                {
                    availableRecipeIds = runtime.Telemetry?.AvailableRecipeIds ?? new HashSet<string>(StringComparer.Ordinal);
                    needRecipeIds = true;
                }

                runtime.Telemetry = new ClientTelemetrySnapshot
                {
                    ReceivedAt = now,
                    Productions = sanitizeProductions(request.Productions),
                    AvailableRecipeIds = availableRecipeIds
                };

                cleanupRuntime(runtime, now);
                CraftQueueData data = craftQueueStore.Get(sessionId);
                ensureLedgerReconciled(sessionId, runtime, data);
                if (runtime.InRaid)
                {
                    if (now - runtime.RaidStateChangedAt < RaidPollRecoverySeconds)
                    {
                        return new ClientPollResponse
                        {
                            QueueEntries = createQueueView(data),
                            NeedRecipeIds = needRecipeIds
                        };
                    }

                    runtime.InRaid = false;
                    runtime.RaidStateChangedAt = now;
                    discardRaidOutput(sessionId);
                    logger.LogWarning(
                        "[CraftQueue] EFT resumed polling for profile {ProfileId} without a raid-end callback; the client executor has taken over.",
                        sessionId);
                }

                bool changed = expireAmbiguousEntries(sessionId, runtime, data, now);
                changed |= reconcileConfirmedProductions(data, runtime, now);

                List<ClientCommand> commands = getCommandsForDelivery(runtime, now);
                bool claimChanged;
                QueueClaim? claim = claimNext(data, runtime, runtime.Telemetry.Productions, now, out claimChanged);
                changed |= claimChanged;

                if (changed)
                    craftQueueStore.Set(sessionId);

                return new ClientPollResponse
                {
                    Commands = commands.Select(command => new ClientCommandDelivery
                    {
                        CommandId = command.CommandId,
                        Kind = command.Kind.ToString(),
                        RecipeId = command.RecipeId,
                        InstanceId = command.InstanceId
                    }).ToList(),
                    QueueEntries = createQueueView(data),
                    Claim = claim,
                    NeedRecipeIds = needRecipeIds
                };
            }
        }

        public bool AcknowledgeClaim(MongoId sessionId, QueueAckRequestData acknowledgement)
        {
            if (string.IsNullOrWhiteSpace(acknowledgement.QueueId))
                return false;

            SessionRuntime runtime = getRuntime(sessionId);
            lock (runtime.Sync)
            {
                long now = unixNow();
                cleanupRuntime(runtime, now);
                if (runtime.RecentlyConfirmedQueueIds.ContainsKey(acknowledgement.QueueId))
                    return true;

                CraftQueueData data = craftQueueStore.Get(sessionId);
                QueuedProductionRequest? entry = findQueueEntry(data, acknowledgement.QueueId);
                if (entry == null || entry.Status is not (QueueEntryStatus.Claimed or QueueEntryStatus.Starting))
                    return false;

                entry.UpdatedAt = now;
                if (acknowledgement.Success)
                {
                    entry.Status = QueueEntryStatus.Starting;
                    entry.LeaseUntil = now + StartConfirmationSeconds;
                    entry.RetryAt = null;
                    entry.LastError = null;
                    entry.DeferralCount = 0;
                    if (GetRecipeById(entry.RecipeId) is RecipeInfo startedRecipe)
                        runtime.AreaStartedAt[startedRecipe.AreaType] = now;
                }
                else if (string.Equals(acknowledgement.ErrorCode, "recipe_unavailable", StringComparison.Ordinal))
                {
                    entry.Status = QueueEntryStatus.NeedsReview;
                    entry.LeaseUntil = null;
                    entry.RetryAt = null;
                    entry.LastError = sanitizeError(acknowledgement.Error)
                        ?? "The recipe is no longer available for this profile.";
                }
                else
                {
                    entry.Status = QueueEntryStatus.Queued;
                    entry.LeaseUntil = null;
                    if (isEnvironmentalFailure(acknowledgement.ErrorCode) && entry.DeferralCount < MaximumFastDeferrals)
                    {
                        entry.DeferralCount++;
                        entry.RetryAt = now + EnvironmentalRetrySeconds;
                    }
                    else
                    {
                        entry.AttemptCount++;
                        entry.RetryAt = now + retryDelaySeconds(entry.AttemptCount);
                    }
                    entry.LastError = sanitizeError(acknowledgement.Error)
                        ?? "The craft could not be started yet; CraftQueue will retry it.";
                }

                craftQueueStore.Set(sessionId);
                return true;
            }
        }

        public bool ConfirmStarted(MongoId sessionId, string recipeId)
        {
            RecipeInfo? recipe = GetRecipeById(recipeId);
            if (recipe == null)
                return false;

            bool confirmed = confirmStartedEntry(sessionId, recipeId, recipe);
            if (confirmed)
            {
                SessionRuntime runtime = getRuntime(sessionId);
                lock (runtime.Sync)
                    requestProfileSave(sessionId, runtime);
            }

            return confirmed;
        }

        private bool confirmStartedEntry(MongoId sessionId, string recipeId, RecipeInfo recipe)
        {
            SessionRuntime runtime = getRuntime(sessionId);
            lock (runtime.Sync)
            {
                CraftQueueData data = craftQueueStore.Get(sessionId);
                if (!data.AreaQueues.TryGetValue((HideoutAreas)recipe.AreaType, out LinkedList<QueuedProductionRequest>? queue))
                    return false;

                QueuedProductionRequest? entry = queue.FirstOrDefault(
                    queued => queued.RecipeId == recipeId
                        && queued.Status is QueueEntryStatus.Claimed or QueueEntryStatus.Starting);
                if (entry == null)
                    return false;

                long now = unixNow();
                queue.Remove(entry);
                addStartLedger(data, runtime, entry, recipe.AreaType, readStartTimestamp(sessionId, recipeId));
                runtime.RecentlyConfirmedQueueIds[entry.QueueId] = now + CommandRetentionSeconds;
                runtime.AreaStartedAt[recipe.AreaType] = now;
                craftQueueStore.Set(sessionId);
                logger.LogInformation("[CraftQueue] Confirmed queued craft {RecipeId} and removed queue entry {QueueId}.", recipeId, entry.QueueId);
                return true;
            }
        }
        private static void addStartLedger(
            CraftQueueData data,
            SessionRuntime runtime,
            QueuedProductionRequest entry,
            int areaType,
            long startTimestamp)
        {
            data.PendingStartConfirmations.Add(new PendingStartConfirmation
            {
                QueueId = entry.QueueId,
                RecipeId = entry.RecipeId,
                AreaType = areaType,
                StartTimestamp = startTimestamp,
                Sequence = ++runtime.ConfirmSequence,
                ConfirmedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
            });
        }
        private long readStartTimestamp(MongoId sessionId, string recipeId)
        {
            try
            {
                Dictionary<MongoId, Production?>? productions = getPmcData(sessionId).Hideout?.Production;
                if (productions != null
                    && productions.TryGetValue(new MongoId(recipeId), out Production? production)
                    && production != null)
                    return production.StartTimestamp ?? 0;
            }
            catch
            {
            }
            return 0;
        }
        private void requestProfileSave(MongoId sessionId, SessionRuntime runtime)
        {
            if (runtime.ProfileSaveQueued)
                return;
            runtime.ProfileSaveQueued = true;
            Task previous = runtime.ProfileSaveTask;
            runtime.ProfileSaveTask = Task.Run(() => runProfileSave(previous, sessionId, runtime));
        }

        private async Task runProfileSave(Task previous, MongoId sessionId, SessionRuntime runtime)
        {
            try { await previous.ConfigureAwait(false); }
            catch { /* logged by its own run */ }

            long coveredThroughSequence;
            lock (runtime.Sync)
            {
                runtime.ProfileSaveQueued = false;
                coveredThroughSequence = runtime.ConfirmSequence;
            }

            var timer = Stopwatch.StartNew();
            try
            {
                await saveServer.SaveProfileAsync(sessionId).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                int attempt;
                lock (runtime.Sync)
                    attempt = ++runtime.ProfileSaveFailures;

                if (attempt <= 3)
                {
                    logger.LogWarning(ex,
                        "[CraftQueue] Deferred profile save failed for {ProfileId} (attempt {Attempt}); retrying.",
                        sessionId, attempt);
                    await Task.Delay(TimeSpan.FromSeconds(5 * attempt)).ConfigureAwait(false);
                    lock (runtime.Sync)
                        requestProfileSave(sessionId, runtime);
                }
                else
                {
                    logger.LogError(ex,
                        "[CraftQueue] Deferred profile save keeps failing for {ProfileId}; clearing the start ledger to avoid resurrecting finished crafts. SPT's own autosave remains responsible for the profile.",
                        sessionId);
                    lock (runtime.Sync)
                    {
                        runtime.ProfileSaveFailures = 0;
                        CraftQueueData stale = craftQueueStore.Get(sessionId);
                        if (stale.PendingStartConfirmations.RemoveAll(record => record.Sequence <= coveredThroughSequence) > 0)
                            craftQueueStore.Set(sessionId);
                    }
                }
                return;
            }
            timer.Stop();

            if (timer.ElapsedMilliseconds > 1000)
                logger.LogWarning(
                    "[CraftQueue] Profile save took {Ms} ms for {ProfileId} - a very large (modded) profile makes every save this expensive.",
                    timer.ElapsedMilliseconds,
                    sessionId);
            else
                logger.LogDebug("[CraftQueue] Profile save took {Ms} ms for {ProfileId}.", timer.ElapsedMilliseconds, sessionId);

            lock (runtime.Sync)
            {
                runtime.ProfileSaveFailures = 0;
                CraftQueueData data = craftQueueStore.Get(sessionId);
                int cleared = data.PendingStartConfirmations.RemoveAll(record => record.Sequence <= coveredThroughSequence);
                if (cleared > 0)
                    craftQueueStore.Set(sessionId);
            }
        }
        private void ensureLedgerReconciled(MongoId sessionId, SessionRuntime runtime, CraftQueueData data)
        {
            if (runtime.LedgerReconciled)
                return;
            runtime.LedgerReconciled = true;
            if (data.PendingStartConfirmations.Count == 0)
                return;

            Dictionary<MongoId, Production?>? productions = null;
            try
            {
                productions = getPmcData(sessionId).Hideout?.Production;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "[CraftQueue] Could not read the profile to reconcile the start ledger for {ProfileId}.", sessionId);
                return;
            }

            bool changed = false;
            foreach (PendingStartConfirmation record in data.PendingStartConfirmations.ToArray())
            {
                bool present = productions != null
                    && productions.TryGetValue(new MongoId(record.RecipeId), out Production? production)
                    && production != null;
                if (present && record.StartTimestamp > 0)
                {
                    productions!.TryGetValue(new MongoId(record.RecipeId), out Production? matched);
                    present = (matched?.StartTimestamp ?? 0) >= record.StartTimestamp;
                }

                if (!present)
                {
                    var restored = new QueuedProductionRequest
                    {
                        QueueId = record.QueueId,
                        RecipeId = record.RecipeId,
                        Status = QueueEntryStatus.Queued
                    };
                    LinkedList<QueuedProductionRequest> queue = getOrCreateQueue(data, (HideoutAreas)record.AreaType);
                    if (!queue.Any(entry => entry.QueueId == record.QueueId))
                    {
                        queue.AddFirst(restored);
                        logger.LogWarning(
                            "[CraftQueue] Restored craft {RecipeId}: the server stopped before the profile persisted its start.",
                            record.RecipeId);
                    }
                }

                data.PendingStartConfirmations.Remove(record);
                changed = true;
            }

            if (changed)
                craftQueueStore.Set(sessionId);
        }

        public void SetRaidState(MongoId sessionId, bool inRaid)
        {
            SessionRuntime runtime = getRuntime(sessionId);
            bool changed;
            lock (runtime.Sync)
            {
                if (runtime.InRaid == inRaid)
                {
                    if (inRaid)
                        runtime.RaidStateChangedAt = unixNow();
                    return;
                }

                long now = unixNow();
                runtime.InRaid = inRaid;
                runtime.RaidStateChangedAt = now;
                changed = false;

                if (inRaid)
                {
                    CraftQueueData data = craftQueueStore.Get(sessionId);
                    changed = completePendingCommandsForRaid(sessionId, runtime, data, now);
                    if (changed)
                        craftQueueStore.Set(sessionId);
                }
                else
                {
                    discardRaidOutput(sessionId);
                }
            }

            logger.LogInformation(
                inRaid
                    ? "[CraftQueue] Raid handover enabled; the server will process the validated queue for profile {ProfileId}."
                    : "[CraftQueue] Raid handover ended; EFT will resume queue execution for profile {ProfileId}.",
                sessionId);
        }
        private void discardRaidOutput(MongoId sessionId)
        {
            try
            {
                eventOutputHolder.ResetOutput(sessionId);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "[CraftQueue] Could not clear the raid item-event output for {ProfileId}.", sessionId);
            }
        }

        public IReadOnlyList<MongoId> GetRaidSessions()
        {
            var result = new List<MongoId>();
            long now = unixNow();
            foreach ((string profileId, SessionRuntime runtime) in sessions)
            {
                var sessionId = new MongoId(profileId);
                lock (runtime.Sync)
                {
                    if (!runtime.InRaid)
                        continue;
                    if (now - runtime.RaidStateChangedAt > MaximumRaidSeconds)
                    {
                        runtime.InRaid = false;
                        runtime.RaidStateChangedAt = now;
                        logger.LogInformation(
                            "[CraftQueue] Profile {ProfileId} never reported the end of its raid; the server handover was stopped.",
                            sessionId);
                        continue;
                    }

                    result.Add(sessionId);
                }
            }

            return result;
        }

        public bool ProcessRaidQueues(MongoId sessionId)
        {
            SessionRuntime runtime = getRuntime(sessionId);
            lock (runtime.Sync)
            {
                long now = unixNow();
                if (!runtime.InRaid || now - runtime.RaidStateChangedAt < RaidHandoverSeconds)
                    return false;

                cleanupRuntime(runtime, now);
                PmcData pmcData = getPmcData(sessionId);
                if (pmcData.Hideout?.Production == null || pmcData.Inventory?.Items == null)
                    return false;

                CraftQueueData data = craftQueueStore.Get(sessionId);
                ensureLedgerReconciled(sessionId, runtime, data);
                if (!hasRaidWork(data, pmcData))
                    return false;

                hideoutHelper.UpdatePlayerHideout(sessionId);

                bool changed = false;
                if (reconcileRaidHandover(sessionId, runtime, data, pmcData, now))
                {
                    craftQueueStore.Set(sessionId);
                    changed = true;
                }

                if (collectCompletedProductions(sessionId, runtime, pmcData, now))
                {
                    requestProfileSave(sessionId, runtime);
                    changed = true;
                }

                if (startQueuedProductions(sessionId, runtime, data, pmcData, now))
                    changed = true;

                return changed;
            }
        }
        private bool hasRaidWork(CraftQueueData data, PmcData pmcData)
        {
            if (data.PendingStartConfirmations.Count > 0)
                return true;
            if (data.AreaQueues.Values.Any(queue => queue.Count > 0))
                return true;

            Dictionary<MongoId, Production?>? productions = pmcData.Hideout?.Production;
            if (productions == null)
                return false;
            foreach ((MongoId productionId, Production? production) in productions)
            {
                if (production == null)
                    continue;
                HideoutProduction? recipe = getProductionRecipe(productionId.ToString());
                if (recipe != null && recipe.Continuous != true)
                    return true;
            }
            return false;
        }
        public QueueOperationResult DismissCommand(MongoId sessionId, string commandId)
        {
            if (string.IsNullOrWhiteSpace(commandId))
                return QueueOperationResult.Fail("Missing command id.");

            SessionRuntime runtime = getRuntime(sessionId);
            lock (runtime.Sync)
            {
                if (!runtime.Commands.TryGetValue(commandId, out ClientCommand? command))
                    return QueueOperationResult.Ok();
                if (command.Status != ClientCommandStatus.Completed)
                    return QueueOperationResult.Fail("This command is still being delivered to EFT.");

                runtime.Commands.Remove(commandId);
                return QueueOperationResult.Ok();
            }
        }

        public QueueOperationResult CancelQueued(MongoId sessionId, string queueId)
        {
            SessionRuntime runtime = getRuntime(sessionId);
            lock (runtime.Sync)
            {
                CraftQueueData data = craftQueueStore.Get(sessionId);
                foreach (LinkedList<QueuedProductionRequest> queue in data.AreaQueues.Values)
                {
                    QueuedProductionRequest? entry = queue.FirstOrDefault(item => item.QueueId == queueId);
                    if (entry == null)
                        continue;
                    if (entry.Status is QueueEntryStatus.Claimed or QueueEntryStatus.Starting)
                        return QueueOperationResult.Fail("This craft is already being started and can no longer be removed safely.");

                    queue.Remove(entry);
                    craftQueueStore.Set(sessionId);
                    return QueueOperationResult.Ok();
                }
            }

            return QueueOperationResult.Fail("Queue entry not found.");
        }

        public QueueOperationResult RetryQueued(MongoId sessionId, string queueId)
        {
            SessionRuntime runtime = getRuntime(sessionId);
            lock (runtime.Sync)
            {
                CraftQueueData data = craftQueueStore.Get(sessionId);
                QueuedProductionRequest? entry = findQueueEntry(data, queueId);
                if (entry == null)
                    return QueueOperationResult.Fail("Queue entry not found.");
                if (entry.Status != QueueEntryStatus.NeedsReview)
                    return QueueOperationResult.Fail("Only entries marked for review can be retried manually.");

                entry.Status = QueueEntryStatus.Queued;
                entry.LeaseUntil = null;
                entry.RetryAt = null;
                entry.LastError = null;
                entry.AttemptCount = 0;
                entry.DeferralCount = 0;
                entry.UpdatedAt = unixNow();
                craftQueueStore.Set(sessionId);
                return QueueOperationResult.Ok();
            }
        }

        public CraftQueueState GetState(MongoId sessionId)
        {
            SessionRuntime runtime = getRuntime(sessionId);
            CraftQueueState state;
            HashSet<string>? availabilityScope;
            lock (runtime.Sync)
            {
                long now = unixNow();
                cleanupRuntime(runtime, now);
                CraftQueueData data = craftQueueStore.Get(sessionId);
                ensureLedgerReconciled(sessionId, runtime, data);
                if (!runtime.InRaid && expireAmbiguousEntries(sessionId, runtime, data, now))
                    craftQueueStore.Set(sessionId);

                bool online = hasFreshTelemetry(runtime, now);
                bool recipesKnown = runtime.Telemetry?.AvailableRecipeIds.Count > 0;
                List<ActiveProductionView> active;
                bool profileView = false;
                if (online && !runtime.InRaid)
                {
                    active = runtime.Telemetry!.Productions.Select(production =>
                    {
                        RecipeInfo? recipe = GetRecipeById(production.RecipeId);
                        return new ActiveProductionView
                        {
                            RecipeId = production.RecipeId,
                            ProductName = recipe?.ProductName ?? production.RecipeId,
                            AreaType = production.AreaType,
                            TimeLeft = production.TimeLeft,
                            ProductionTime = production.ProductionTime,
                            ProgressRate = production.ProgressRate,
                            InstanceId = production.InstanceId
                        };
                    }).ToList();
                    if (active.Count == 0)
                    {
                        active = createServerActiveProductionView(sessionId, now);
                        profileView = active.Count > 0;
                    }
                }
                else
                {
                    active = createServerActiveProductionView(sessionId, now);
                    profileView = !runtime.InRaid;
                }
                List<string> availableRecipeIds = recipesKnown
                    ? runtime.Telemetry!.AvailableRecipeIds.Order(StringComparer.Ordinal).ToList()
                    : [];

                state = new CraftQueueState
                {
                    ClientOnline = online,
                    RaidAutomationActive = runtime.InRaid,
                    ProfileView = profileView,
                    RecipeListAvailable = recipesKnown,
                    ActiveProductions = active,
                    AvailableRecipeIds = availableRecipeIds,
                    QueueEntries = createQueueView(data),
                    Commands = runtime.Commands.Values
                        .Where(command => command.CreatedAt >= now - CommandRetentionSeconds)
                        .OrderByDescending(command => command.CreatedAt)
                        .Take(20)
                        .Select(copyCommand)
                        .ToList()
                };

                availabilityScope = recipesKnown
                    ? new HashSet<string>(runtime.Telemetry!.AvailableRecipeIds, StringComparer.Ordinal)
                    : null;
            }
            state.MaterialAvailability = getMaterialAvailabilityCached(sessionId, runtime, availabilityScope);
            return state;
        }

        private Dictionary<string, List<int>> getMaterialAvailabilityCached(
            MongoId sessionId,
            SessionRuntime runtime,
            HashSet<string>? scope)
        {
            const long CacheMilliseconds = 5000;
            string cacheKey = scope == null
                ? "*"
                : scope.Count + ":" + string.Join(",", scope.Order(StringComparer.Ordinal)).GetHashCode();
            long nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

            lock (runtime.Sync)
            {
                if (runtime.AvailabilityCache != null
                    && runtime.AvailabilityCacheKey == cacheKey
                    && nowMs - runtime.AvailabilityCacheAtMs < CacheMilliseconds)
                    return runtime.AvailabilityCache;
            }

            var timer = Stopwatch.StartNew();
            Dictionary<string, List<int>>? result = computeMaterialAvailability(sessionId, scope);
            timer.Stop();

            if (result == null)
            {
                lock (runtime.Sync)
                    return runtime.AvailabilityCache ?? [];
            }

            logger.LogDebug(
                "[CraftQueue] Material availability computed in {Ms} ms for {Count} recipes.",
                timer.ElapsedMilliseconds,
                result.Count);

            lock (runtime.Sync)
            {
                runtime.AvailabilityCache = result;
                runtime.AvailabilityCacheKey = cacheKey;
                runtime.AvailabilityCacheAtMs = nowMs;
            }

            return result;
        }
        public Dictionary<string, List<int>> GetMaterialAvailability(MongoId sessionId)
        {
            return computeMaterialAvailability(sessionId, null) ?? [];
        }
        private Dictionary<string, List<int>>? computeMaterialAvailability(MongoId sessionId, HashSet<string>? scope)
        {
            try
            {
                PmcData pmcData = getPmcData(sessionId);
                _ = GetRecipes();
                Dictionary<string, HideoutProduction> lookup;
                lock (recipeCacheLock)
                    lookup = cachedProductionLookup == null
                        ? new Dictionary<string, HideoutProduction>(StringComparer.Ordinal)
                        : new Dictionary<string, HideoutProduction>(cachedProductionLookup, StringComparer.Ordinal);

                ServerInventorySnapshot snapshot = createInventorySnapshot(pmcData);
                var result = new Dictionary<string, List<int>>(StringComparer.Ordinal);
                foreach ((string recipeId, HideoutProduction recipe) in lookup)
                {
                    if (scope != null && !scope.Contains(recipeId))
                        continue;
                    var counts = new List<int>();
                    foreach (Requirement requirement in recipe.Requirements ?? [])
                    {
                        if ((requirement.Type != "Item" && requirement.Type != "Tool") || !requirement.TemplateId.HasValue)
                            continue;
                        counts.Add(getSuitableItems(snapshot, requirement).Sum(stackCount));
                    }
                    result[recipeId] = counts;
                }

                return result;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "[CraftQueue] Material availability for {ProfileId} could not be computed.", sessionId);
                return null;
            }
        }

        public Dictionary<string, List<RecipeInfo>> GetRecipes()
        {
            lock (recipeCacheLock)
            {
                if (cachedRecipes != null)
                    return cachedRecipes;

                List<HideoutProduction>? recipes = databaseService.GetHideout().Production.Recipes;
                var result = new Dictionary<string, List<RecipeInfo>>(StringComparer.Ordinal);
                var lookup = new Dictionary<string, RecipeInfo>(StringComparer.Ordinal);
                var productionLookup = new Dictionary<string, HideoutProduction>(StringComparer.Ordinal);

                if (recipes != null)
                {
                    Dictionary<MongoId, TemplateItem> items = databaseService.GetItems();
                    var itemNames = new Dictionary<MongoId, string>();
                    string getItemName(MongoId templateId)
                    {
                        if (!itemNames.TryGetValue(templateId, out string? name))
                        {
                            name = itemHelper.GetItemName(templateId);
                            itemNames[templateId] = name;
                        }
                        return name;
                    }

                    foreach (HideoutProduction recipe in recipes)
                    {
                        if (recipe.Continuous == true || recipe.EndProduct == default || recipe.Id == default || !recipe.AreaType.HasValue)
                            continue;

                        string category = resolveCategory(recipe.EndProduct, items);
                        if (!result.TryGetValue(category, out List<RecipeInfo>? categoryRecipes))
                        {
                            categoryRecipes = [];
                            result[category] = categoryRecipes;
                        }

                        var requirements = new List<RecipeRequirement>();
                        if (recipe.Requirements != null)
                        {
                            foreach (Requirement requirement in recipe.Requirements)
                            {
                                if ((requirement.Type != "Item" && requirement.Type != "Tool") || !requirement.TemplateId.HasValue)
                                    continue;

                                requirements.Add(new RecipeRequirement
                                {
                                    TemplateId = requirement.TemplateId.Value.ToString(),
                                    Name = getItemName(requirement.TemplateId.Value),
                                    Count = Math.Max(1, requirement.Count ?? 1),
                                    IsTool = requirement.Type == "Tool"
                                });
                            }
                        }

                        var info = new RecipeInfo
                        {
                            RecipeId = recipe.Id.ToString(),
                            EndProduct = recipe.EndProduct.ToString(),
                            ProductName = getItemName(recipe.EndProduct),
                            AreaType = (int)recipe.AreaType.Value,
                            ProductionTime = (int)(recipe.ProductionTime ?? 0),
                            Requirements = requirements
                        };
                        categoryRecipes.Add(info);
                        lookup[info.RecipeId] = info;
                        productionLookup[info.RecipeId] = recipe;
                    }
                }

                foreach (List<RecipeInfo> list in result.Values)
                    list.Sort((left, right) => string.Compare(left.ProductName, right.ProductName, StringComparison.OrdinalIgnoreCase));

                cachedRecipeLookup = lookup;
                cachedProductionLookup = productionLookup;
                cachedRecipes = result;
                return result;
            }
        }

        public RecipeInfo? GetRecipeById(string recipeId)
        {
            _ = GetRecipes();
            lock (recipeCacheLock)
                return cachedRecipeLookup != null && cachedRecipeLookup.TryGetValue(recipeId, out RecipeInfo? recipe) ? recipe : null;
        }

        private QueueOperationResult enqueueDuringRaid(
            MongoId sessionId,
            SessionRuntime runtime,
            string recipeId,
            long now)
        {
            if (runtime.Telemetry?.AvailableRecipeIds.Contains(recipeId) != true)
                return QueueOperationResult.Fail("The recipe list for this profile was not loaded before the raid.");

            CraftQueueData data = craftQueueStore.Get(sessionId);
            if (countQueueEntries(data) >= MaximumQueueEntries)
                return QueueOperationResult.Fail($"The queue limit of {MaximumQueueEntries} entries has been reached.");

            RecipeInfo recipe = GetRecipeById(recipeId)!;
            PmcData pmcData;
            try
            {
                pmcData = getPmcData(sessionId);
            }
            catch (Exception ex)
            {
                return QueueOperationResult.Fail("The selected profile is unavailable: " + ex.Message);
            }

            if (!tryValidateProjectedRequirements(pmcData, data, recipeId, out string? error))
                return QueueOperationResult.Fail(error ?? "The required items are not available.");

            var command = new ClientCommand
            {
                Kind = ClientCommandKind.Enqueue,
                RecipeId = recipeId,
                CreatedAt = now,
                ExpiresAt = now + 120,
                NextDeliveryAt = now
            };
            runtime.Commands[command.CommandId] = command;

            if (!appendQueueEntry(data, recipe, command.CommandId, now, out int queueCount))
            {
                completeCommand(command, false, "The craft is already represented in the queue.", now);
                return QueueOperationResult.Fail(command.Error ?? "The craft is already represented in the queue.");
            }

            completeCommand(command, true, null, now);
            craftQueueStore.Set(sessionId);
            logger.LogInformation(
                "[CraftQueue] Enqueued {RecipeId} in area {Area} during a raid (queue size: {Count}).",
                recipeId,
                (HideoutAreas)recipe.AreaType,
                queueCount);
            return QueueOperationResult.Ok(command.CommandId, handledByServer: true);
        }

        private bool completePendingCommandsForRaid(
            MongoId sessionId,
            SessionRuntime runtime,
            CraftQueueData data,
            long now)
        {
            ClientCommand[] pendingCommands = runtime.Commands.Values
                .Where(command => command.Status != ClientCommandStatus.Completed)
                .OrderBy(command => command.CreatedAt)
                .ToArray();
            if (pendingCommands.Length == 0)
                return false;

            PmcData pmcData;
            try
            {
                pmcData = getPmcData(sessionId);
            }
            catch (Exception ex)
            {
                foreach (ClientCommand command in pendingCommands)
                    completeCommand(command, false, "The profile was unavailable during the raid handover: " + ex.Message, now);
                return false;
            }

            bool queueChanged = false;
            foreach (ClientCommand command in pendingCommands)
            {
                if (command.Kind != ClientCommandKind.Enqueue)
                {
                    completeCommand(command, false, "An active craft cannot be aborted safely while EFT is in a raid.", now);
                    continue;
                }

                RecipeInfo? recipe = GetRecipeById(command.RecipeId);
                if (recipe == null)
                {
                    completeCommand(command, false, "Recipe metadata is no longer available.", now);
                    continue;
                }
                if (countQueueEntries(data) >= MaximumQueueEntries)
                {
                    completeCommand(command, false, $"The queue limit of {MaximumQueueEntries} entries has been reached.", now);
                    continue;
                }
                if (!tryValidateProjectedRequirements(pmcData, data, command.RecipeId, out string? error))
                {
                    completeCommand(command, false, error ?? "The required items are not available.", now);
                    continue;
                }

                if (appendQueueEntry(data, recipe, command.CommandId, now, out int queueCount))
                {
                    queueChanged = true;
                    logger.LogInformation(
                        "[CraftQueue] Enqueued {RecipeId} in area {Area} during the raid handover (queue size: {Count}).",
                        command.RecipeId,
                        (HideoutAreas)recipe.AreaType,
                        queueCount);
                }
                completeCommand(command, true, null, now);
            }

            return queueChanged;
        }

        private bool reconcileRaidHandover(
            MongoId sessionId,
            SessionRuntime runtime,
            CraftQueueData data,
            PmcData pmcData,
            long now)
        {
            Dictionary<string, long> activeKeys = getActiveProductionKeys(pmcData);
            var confirmed = new List<(HideoutAreas Area, LinkedList<QueuedProductionRequest> Queue, QueuedProductionRequest Entry)>();
            var reset = new List<QueuedProductionRequest>();

            foreach ((HideoutAreas area, LinkedList<QueuedProductionRequest> queue) in data.AreaQueues)
            {
                foreach (QueuedProductionRequest entry in queue)
                {
                    if (entry.Status is not (QueueEntryStatus.Claimed or QueueEntryStatus.Starting))
                        continue;

                    if (activeKeys.ContainsKey(productionKey(area, entry.RecipeId)))
                        confirmed.Add((area, queue, entry));
                    else
                        reset.Add(entry);
                }
            }

            foreach ((HideoutAreas area, LinkedList<QueuedProductionRequest> queue, QueuedProductionRequest entry) in confirmed)
            {
                queue.Remove(entry);
                activeKeys.TryGetValue(productionKey(area, entry.RecipeId), out long startedAt);
                addStartLedger(data, runtime, entry, (int)area, startedAt);
                runtime.RecentlyConfirmedQueueIds[entry.QueueId] = now + CommandRetentionSeconds;
                logger.LogInformation(
                    "[CraftQueue] Reconciled production {RecipeId} during the raid handover and removed queue entry {QueueId}.",
                    entry.RecipeId,
                    entry.QueueId);
            }
            if (confirmed.Count > 0)
                requestProfileSave(sessionId, runtime);

            foreach (QueuedProductionRequest entry in reset)
            {
                entry.Status = QueueEntryStatus.Queued;
                entry.LeaseUntil = null;
                entry.RetryAt = null;
                entry.LastError = null;
                entry.UpdatedAt = now;
            }

            return confirmed.Count > 0 || reset.Count > 0;
        }

        private bool collectCompletedProductions(
            MongoId sessionId,
            SessionRuntime runtime,
            PmcData pmcData,
            long now)
        {
            bool changed = false;
            Dictionary<MongoId, Production> productions = pmcData.Hideout!.Production!;
            foreach ((MongoId productionId, Production? production) in productions.ToArray())
            {
                if (production == null)
                    continue;

                HideoutProduction? recipe = getProductionRecipe(productionId.ToString());
                if (recipe == null || recipe.Continuous == true)
                    continue;

                if (production.InProgress != true)
                {
                    if (production.SptIsComplete == true)
                    {
                        productions.Remove(productionId);
                        changed = true;
                    }
                    continue;
                }

                double progress = production.Progress ?? 0;
                double productionTime = production.ProductionTime ?? 0;
                if (progress < productionTime)
                    continue;

                string retryKey = productionId.ToString();
                if (runtime.ProductionRetryAt.TryGetValue(retryKey, out long retryAt) && retryAt > now)
                    continue;

                try
                {
                    eventOutputHolder.ResetOutput(sessionId);
                    _ = hideoutController.TakeProduction(
                        pmcData,
                        new HideoutTakeProductionRequestData
                        {
                            Action = "HideoutTakeProduction",
                            RecipeId = recipe.Id,
                            Timestamp = checked((int)Math.Min(int.MaxValue, now))
                        },
                        sessionId);

                    bool collected = !productions.TryGetValue(productionId, out Production? current)
                        || (current?.InProgress != true && current?.SptIsComplete == true);
                    if (!collected)
                    {
                        runtime.ProductionRetryAt[retryKey] = now + CollectRetrySeconds;
                        continue;
                    }

                    productions.Remove(productionId);
                    runtime.ProductionRetryAt.Remove(retryKey);
                    changed = true;
                    logger.LogInformation(
                        "[CraftQueue] Collected completed production {RecipeId} on the server during a raid.",
                        recipe.Id);
                }
                catch (Exception ex)
                {
                    runtime.ProductionRetryAt[retryKey] = now + CollectRetrySeconds;
                    logger.LogWarning(
                        ex,
                        "[CraftQueue] Collecting completed production {RecipeId} during a raid will retry later.",
                        recipe.Id);
                }
            }

            return changed;
        }

        private bool startQueuedProductions(
            MongoId sessionId,
            SessionRuntime runtime,
            CraftQueueData data,
            PmcData pmcData,
            long now)
        {
            bool changed = false;
            HashSet<HideoutAreas> activeAreas = getActiveAreas(pmcData);
            bool generatorOn = pmcData.Hideout?.Areas?
                .FirstOrDefault(area => area.Type == HideoutAreas.Generator)?.Active == true;

            foreach ((HideoutAreas area, LinkedList<QueuedProductionRequest> queue) in data.AreaQueues.OrderBy(pair => (int)pair.Key))
            {
                if (activeAreas.Contains(area) || queue.First?.Value is not QueuedProductionRequest entry)
                    continue;
                if (entry.Status != QueueEntryStatus.Queued || entry.RetryAt > now)
                    continue;

                HideoutProduction? recipe = getProductionRecipe(entry.RecipeId);
                if (recipe == null || recipe.AreaType != area)
                {
                    entry.Status = QueueEntryStatus.NeedsReview;
                    entry.LeaseUntil = null;
                    entry.RetryAt = null;
                    entry.LastError = recipe == null
                        ? "Recipe metadata is no longer available."
                        : "The saved queue area no longer matches the recipe.";
                    entry.UpdatedAt = now;
                    craftQueueStore.Set(sessionId);
                    changed = true;
                    continue;
                }

                if (recipe.NeedFuelForAllProductionTime == true && !generatorOn)
                {
                    deferRaidEntry(entry, now, "The generator must be running before this craft can start.");
                    craftQueueStore.Set(sessionId);
                    changed = true;
                    continue;
                }

                if (!trySelectServerRequirements(pmcData, recipe, out List<IdWithCount> items, out List<IdWithCount> tools, out string? error))
                {
                    deferRaidEntry(entry, now, error ?? "The required items are not currently available.");
                    craftQueueStore.Set(sessionId);
                    changed = true;
                    continue;
                }
                entry.Status = QueueEntryStatus.Starting;
                entry.LeaseUntil = now + StartConfirmationSeconds;
                entry.RetryAt = null;
                entry.LastError = null;
                entry.AttemptCount++;
                entry.UpdatedAt = now;
                craftQueueStore.Set(sessionId);
                changed = true;

                Exception? startError = null;
                try
                {
                    eventOutputHolder.ResetOutput(sessionId);
                    _ = hideoutController.SingleProductionStart(
                        pmcData,
                        new HideoutSingleProductionStartRequestData
                        {
                            Action = "HideoutSingleProductionStart",
                            RecipeId = recipe.Id,
                            Items = items,
                            Tools = tools,
                            Timestamp = now
                        },
                        sessionId);
                }
                catch (Exception ex)
                {
                    startError = ex;
                }

                Production? startedProduction = null;
                bool started = pmcData.Hideout?.Production?.TryGetValue(recipe.Id, out startedProduction) == true
                    && startedProduction?.InProgress == true;
                if (started)
                {
                    queue.Remove(entry);
                    addStartLedger(data, runtime, entry, (int)area, startedProduction?.StartTimestamp ?? 0);
                    runtime.RecentlyConfirmedQueueIds[entry.QueueId] = now + CommandRetentionSeconds;
                    craftQueueStore.Set(sessionId);
                    requestProfileSave(sessionId, runtime);
                    activeAreas.Add(area);
                    logger.LogInformation(
                        "[CraftQueue] Started queued production {RecipeId} in area {Area} on the server during a raid.",
                        recipe.Id,
                        area);
                    if (startError != null)
                    {
                        logger.LogWarning(
                            startError,
                            "[CraftQueue] Production {RecipeId} was registered despite an exception while starting it.",
                            recipe.Id);
                    }
                    continue;
                }

                deferRaidEntry(
                    entry,
                    now,
                    startError == null
                        ? "SPT did not register the production; CraftQueue will retry it."
                        : "SPT rejected the production: " + startError.Message,
                    incrementAttempt: false);
                craftQueueStore.Set(sessionId);
                if (startError != null)
                {
                    logger.LogWarning(
                        startError,
                        "[CraftQueue] Starting queued production {RecipeId} during a raid will retry later.",
                        recipe.Id);
                }
            }

            return changed;
        }

        private static void deferRaidEntry(
            QueuedProductionRequest entry,
            long now,
            string error,
            bool incrementAttempt = true)
        {
            if (incrementAttempt)
                entry.AttemptCount++;
            entry.Status = QueueEntryStatus.Queued;
            entry.LeaseUntil = null;
            entry.RetryAt = now + retryDelaySeconds(entry.AttemptCount);
            entry.LastError = sanitizeError(error);
            entry.UpdatedAt = now;
        }

        private List<ActiveProductionView> createServerActiveProductionView(MongoId sessionId, long now)
        {
            PmcData pmcData;
            try
            {
                pmcData = getPmcData(sessionId);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "[CraftQueue] Profile {ProfileId} is unavailable; active crafts cannot be shown.", sessionId);
                return [];
            }

            if (pmcData.Hideout?.Production == null)
                return [];

            var result = new List<ActiveProductionView>();
            foreach ((MongoId productionId, Production? production) in pmcData.Hideout.Production.ToArray())
            {
                if (production?.InProgress != true)
                    continue;

                RecipeInfo? recipe = GetRecipeById(productionId.ToString());
                if (recipe == null)
                    continue;

                double rate = progressRate(pmcData, productionId.ToString(), production);
                double total = Math.Max(0, production.ProductionTime ?? 0);
                double progress = Math.Max(0, production.Progress ?? 0) + pendingProgress(pmcData, rate, now);
                double remainingCraftSeconds = Math.Max(0, total - progress);
                result.Add(new ActiveProductionView
                {
                    RecipeId = productionId.ToString(),
                    ProductName = recipe.ProductName,
                    AreaType = recipe.AreaType,
                    TimeLeft = rate > 0 ? toSafeInt(remainingCraftSeconds / rate) : toSafeInt(remainingCraftSeconds),
                    ProductionTime = toSafeInt(total),
                    ProgressRate = rate,
                    InstanceId = productionId + ":" + (production.StartTimestamp ?? 0)
                });
            }

            return result.OrderBy(production => production.AreaType).ToList();
        }
        private double progressRate(PmcData pmcData, string recipeId, Production production)
        {
            if (production.Interrupted == true)
                return 0;

            HideoutProduction? recipe = getProductionRecipe(recipeId);
            if (recipe?.AreaType is not HideoutAreas areaType)
                return 1;

            bool generatorOn = pmcData.Hideout?.Areas?
                .FirstOrDefault(area => area.Type == HideoutAreas.Generator)?.Active == true;
            if (generatorOn)
                return 1;
            if (production.NeedFuelForAllProductionTime ?? recipe.NeedFuelForAllProductionTime ?? false)
                return 0;
            bool needsFuel = databaseService.GetHideout().Areas?
                .FirstOrDefault(area => area.Type == areaType)?.NeedsFuel == true;
            return needsFuel ? databaseService.GetHideout().Settings?.GeneratorSpeedWithoutFuel ?? 0 : 1;
        }
        private double pendingProgress(PmcData pmcData, double rate, long now)
        {
            long lastTick = pmcData.Hideout?.SptUpdateLastRunTimestamp ?? 0;
            if (rate <= 0 || lastTick <= 0 || now <= lastTick)
                return 0;
            return (now - lastTick) * rate;
        }

        private PmcData getPmcData(MongoId sessionId)
        {
            return saveServer.GetProfile(sessionId).CharacterData?.PmcData
                ?? throw new InvalidOperationException("The PMC profile is unavailable.");
        }

        private HideoutProduction? getProductionRecipe(string recipeId)
        {
            _ = GetRecipes();
            lock (recipeCacheLock)
            {
                return cachedProductionLookup != null
                    && cachedProductionLookup.TryGetValue(recipeId, out HideoutProduction? recipe)
                        ? recipe
                        : null;
            }
        }

        private HashSet<HideoutAreas> getActiveAreas(PmcData pmcData)
        {
            var result = new HashSet<HideoutAreas>();
            foreach ((MongoId productionId, Production? production) in pmcData.Hideout?.Production ?? [])
            {
                HideoutProduction? recipe = getProductionRecipe(productionId.ToString());
                if (production?.InProgress == true && recipe?.AreaType is HideoutAreas area)
                    result.Add(area);
            }
            return result;
        }
        private Dictionary<string, long> getActiveProductionKeys(PmcData pmcData)
        {
            var result = new Dictionary<string, long>(StringComparer.Ordinal);
            foreach ((MongoId productionId, Production? production) in pmcData.Hideout?.Production ?? [])
            {
                HideoutProduction? recipe = getProductionRecipe(productionId.ToString());
                if (production?.InProgress == true && recipe?.AreaType is HideoutAreas area)
                    result[productionKey(area, productionId.ToString())] = production.StartTimestamp ?? 0;
            }
            return result;
        }

        private static string productionKey(HideoutAreas area, string recipeId)
        {
            return ((int)area).ToString() + ":" + recipeId;
        }

        private static int toSafeInt(double value)
        {
            return checked((int)Math.Min(int.MaxValue, Math.Ceiling(Math.Max(0, value))));
        }

        private static LinkedList<QueuedProductionRequest> getOrCreateQueue(CraftQueueData data, HideoutAreas area)
        {
            if (!data.AreaQueues.TryGetValue(area, out LinkedList<QueuedProductionRequest>? queue))
            {
                queue = [];
                data.AreaQueues[area] = queue;
            }
            return queue;
        }

        private static bool appendQueueEntry(
            CraftQueueData data,
            RecipeInfo recipe,
            string sourceCommandId,
            long now,
            out int queueCount)
        {
            HideoutAreas area = (HideoutAreas)recipe.AreaType;
            LinkedList<QueuedProductionRequest> queue = getOrCreateQueue(data, area);

            if (data.AreaQueues.Values
                .SelectMany(existingQueue => existingQueue)
                .Any(entry => entry.SourceCommandId == sourceCommandId))
            {
                queueCount = queue.Count;
                return false;
            }

            queue.AddLast(new QueuedProductionRequest
            {
                RecipeId = recipe.RecipeId,
                SourceCommandId = sourceCommandId,
                CreatedAt = now,
                UpdatedAt = now
            });
            queueCount = queue.Count;
            return true;
        }

        private bool tryValidateProjectedRequirements(
            PmcData pmcData,
            CraftQueueData data,
            string candidateRecipeId,
            out string? error)
        {
            error = null;
            HideoutProduction? candidateRecipe = getProductionRecipe(candidateRecipeId);
            if (candidateRecipe?.AreaType is not HideoutAreas candidateArea)
            {
                error = "Recipe metadata is no longer available.";
                return false;
            }

            ServerInventorySnapshot snapshot = createInventorySnapshot(pmcData);
            List<ServerCraftPlan> activePlans = getActivePlans(pmcData);
            HashSet<string> activeKeys = activePlans
                .Select(plan => productionKey(plan.Area, plan.Recipe.Id.ToString()))
                .ToHashSet(StringComparer.Ordinal);

            var queuedPlans = new List<ServerCraftPlan>();
            foreach ((HideoutAreas area, LinkedList<QueuedProductionRequest> queue) in data.AreaQueues)
            {
                foreach (QueuedProductionRequest entry in queue)
                {
                    if (entry.Status == QueueEntryStatus.NeedsReview)
                        continue;
                    if (entry.Status is QueueEntryStatus.Claimed or QueueEntryStatus.Starting
                        && activeKeys.Contains(productionKey(area, entry.RecipeId)))
                        continue;

                    HideoutProduction? queuedRecipe = getProductionRecipe(entry.RecipeId);
                    if (queuedRecipe != null)
                        queuedPlans.Add(new ServerCraftPlan(area, queuedRecipe));
                }
            }
            queuedPlans.Add(new ServerCraftPlan(candidateArea, candidateRecipe));

            List<Requirement> consumableRequirements = queuedPlans
                .SelectMany(plan => plan.Recipe.Requirements ?? [])
                .Where(requirement => requirement.Type == "Item" && requirement.TemplateId.HasValue)
                .OrderBy(requirement => countSuitableItems(snapshot, requirement))
                .ToList();
            var remainingByItem = snapshot.StashItems.ToDictionary(
                item => item.Id,
                stackCount,
                EqualityComparer<MongoId>.Default);

            foreach (Requirement requirement in consumableRequirements)
            {
                int required = requirementCount(requirement);
                int remaining = required;
                int available = 0;
                foreach (Item item in getSuitableItems(snapshot, requirement))
                {
                    if (!remainingByItem.TryGetValue(item.Id, out int itemRemaining) || itemRemaining <= 0)
                        continue;

                    available += itemRemaining;
                    int take = Math.Min(remaining, itemRemaining);
                    remainingByItem[item.Id] = itemRemaining - take;
                    remaining -= take;
                    if (remaining <= 0)
                        break;
                }

                if (remaining > 0)
                {
                    error = "Not enough " + requirementName(requirement) + ": available " + available
                        + ", required " + required + " including queued crafts.";
                    return false;
                }
            }

            var allPlans = new List<ServerCraftPlan>(activePlans.Count + queuedPlans.Count);
            allPlans.AddRange(activePlans);
            allPlans.AddRange(queuedPlans);
            return validateProjectedTools(pmcData, snapshot, allPlans, out error);
        }

        private bool validateProjectedTools(
            PmcData pmcData,
            ServerInventorySnapshot snapshot,
            IReadOnlyCollection<ServerCraftPlan> plans,
            out string? error)
        {
            error = null;
            var requirementsByTemplate = plans
                .SelectMany(plan => plan.Recipe.Requirements ?? [])
                .Where(requirement => requirement.Type == "Tool" && requirement.TemplateId.HasValue)
                .GroupBy(requirement => requirement.TemplateId!.Value)
                .ToDictionary(group => group.Key, group => group.ToList());

            var requiredByArea = new Dictionary<HideoutAreas, Dictionary<MongoId, int>>();
            foreach (ServerCraftPlan plan in plans)
            {
                if (!requiredByArea.TryGetValue(plan.Area, out Dictionary<MongoId, int>? areaRequirements))
                {
                    areaRequirements = [];
                    requiredByArea[plan.Area] = areaRequirements;
                }

                foreach (IGrouping<MongoId, Requirement> group in (plan.Recipe.Requirements ?? [])
                    .Where(requirement => requirement.Type == "Tool" && requirement.TemplateId.HasValue)
                    .GroupBy(requirement => requirement.TemplateId!.Value))
                {
                    int count = group.Sum(requirementCount);
                    areaRequirements.TryGetValue(group.Key, out int existing);
                    areaRequirements[group.Key] = Math.Max(existing, count);
                }
            }

            var heldTools = (pmcData.Hideout?.Production ?? [])
                .Where(pair => pair.Value?.InProgress == true)
                .SelectMany(pair => pair.Value?.SptRequiredTools ?? [])
                .GroupBy(item => item.Template)
                .ToDictionary(group => group.Key, group => group.Sum(stackCount));

            foreach ((MongoId templateId, List<Requirement> requirements) in requirementsByTemplate)
            {
                int required = requiredByArea.Values.Sum(area =>
                    area.TryGetValue(templateId, out int count) ? count : 0);
                int available = snapshot.GetItems(templateId)
                    .Where(item => requirements.Any(requirement => isSuitableItem(snapshot, requirement, item)))
                    .Sum(stackCount);
                if (heldTools.TryGetValue(templateId, out int held))
                    available += held;

                if (available < required)
                {
                    error = "Missing tool " + requirementName(requirements[0]) + ": available " + available
                        + ", required " + required + " across queued areas.";
                    return false;
                }
            }

            return true;
        }

        private bool trySelectServerRequirements(
            PmcData pmcData,
            HideoutProduction recipe,
            out List<IdWithCount> items,
            out List<IdWithCount> tools,
            out string? error)
        {
            items = [];
            tools = [];
            error = null;

            ServerInventorySnapshot snapshot = createInventorySnapshot(pmcData);
            var usedCounts = new Dictionary<MongoId, int>();
            var selectedItems = new Dictionary<MongoId, int>();
            var selectedTools = new Dictionary<MongoId, int>();

            foreach (Requirement requirement in recipe.Requirements ?? [])
            {
                if ((requirement.Type != "Item" && requirement.Type != "Tool") || !requirement.TemplateId.HasValue)
                    continue;

                int remaining = requirementCount(requirement);
                foreach (Item item in getSuitableItems(snapshot, requirement))
                {
                    if (remaining <= 0)
                        break;

                    usedCounts.TryGetValue(item.Id, out int used);
                    int available = Math.Max(0, stackCount(item) - used);
                    int take = Math.Min(remaining, available);
                    if (take <= 0)
                        continue;

                    Dictionary<MongoId, int> destination = requirement.Type == "Tool" ? selectedTools : selectedItems;
                    destination.TryGetValue(item.Id, out int selected);
                    destination[item.Id] = selected + take;
                    usedCounts[item.Id] = used + take;
                    remaining -= take;
                }

                if (remaining > 0)
                {
                    error = "Not enough " + requirementName(requirement) + " to start this craft.";
                    return false;
                }
            }

            items = selectedItems.Select(pair => new IdWithCount { Id = pair.Key, Count = pair.Value }).ToList();
            tools = selectedTools.Select(pair => new IdWithCount { Id = pair.Key, Count = pair.Value }).ToList();
            return true;
        }

        private List<ServerCraftPlan> getActivePlans(PmcData pmcData)
        {
            var result = new List<ServerCraftPlan>();
            foreach ((MongoId productionId, Production? production) in pmcData.Hideout?.Production ?? [])
            {
                HideoutProduction? recipe = getProductionRecipe(productionId.ToString());
                if (production?.InProgress == true && recipe?.AreaType is HideoutAreas area)
                    result.Add(new ServerCraftPlan(area, recipe));
            }
            return result;
        }

        private ServerInventorySnapshot createInventorySnapshot(PmcData pmcData)
        {
            List<Item> allItems = pmcData.Inventory?.Items ?? [];
            string stashId = pmcData.Inventory?.Stash.ToString() ?? "";
            var itemsById = new Dictionary<string, Item>(StringComparer.Ordinal);
            foreach (Item item in allItems)
                itemsById[item.Id.ToString()] = item;

            var ancestryCache = new Dictionary<string, bool>(StringComparer.Ordinal);
            bool isInStash(Item candidate)
            {
                var path = new List<string>();
                var visited = new HashSet<string>(StringComparer.Ordinal);
                Item current = candidate;
                bool result;
                while (true)
                {
                    string currentId = current.Id.ToString();
                    if (ancestryCache.TryGetValue(currentId, out result))
                        break;
                    if (!visited.Add(currentId))
                    {
                        result = false;
                        break;
                    }

                    path.Add(currentId);
                    if (current.ParentId == stashId && current.SlotId == "hideout")
                    {
                        result = true;
                        break;
                    }
                    if (string.IsNullOrEmpty(current.ParentId)
                        || !itemsById.TryGetValue(current.ParentId, out Item? parent))
                    {
                        result = false;
                        break;
                    }
                    current = parent;
                }

                foreach (string itemId in path)
                    ancestryCache[itemId] = result;
                return result;
            }
            List<Item> stashItems = allItems.Where(isInStash).ToList();
            return new ServerInventorySnapshot(stashItems, allItems, databaseService.GetItems());
        }

        private IEnumerable<Item> getSuitableItems(ServerInventorySnapshot snapshot, Requirement requirement)
        {
            if (!requirement.TemplateId.HasValue)
                return [];
            return snapshot.GetItems(requirement.TemplateId.Value)
                .Where(item => isSuitableItem(snapshot, requirement, item));
        }

        private int countSuitableItems(ServerInventorySnapshot snapshot, Requirement requirement)
        {
            return getSuitableItems(snapshot, requirement).Count();
        }

        private bool isSuitableItem(ServerInventorySnapshot snapshot, Requirement requirement, Item item)
        {
            if (!requirement.TemplateId.HasValue || item.Template != requirement.TemplateId.Value)
                return false;
            if (item.Upd?.PinLockState == PinLockState.Locked)
                return false;
            if (requirement.IsSpawnedInSession == true && item.Upd?.SpawnedInSession != true)
                return false;
            if (!snapshot.IsEmptyRequirementItem(item))
                return false;
            if (requirement.IsFunctional == true
                && itemHelper.IsOfBaseclass(item.Template, BaseClasses.WEAPON)
                && !snapshot.HasRequiredParts(item))
                return false;

            if (snapshot.TryGetTemplate(item.Template, out TemplateItem? template)
                && template?.Properties?.IsEncoded.HasValue == true)
            {
                bool actual = item.Upd?.RecodableComponent?.IsEncoded
                    ?? template.Properties.IsEncoded
                    ?? false;
                if (actual != (requirement.IsEncoded ?? false))
                    return false;
            }

            return true;
        }

        private string requirementName(Requirement requirement)
        {
            return requirement.TemplateId.HasValue
                ? itemHelper.GetItemName(requirement.TemplateId.Value)
                : "item";
        }

        private static int requirementCount(Requirement requirement)
        {
            return Math.Max(1, requirement.Count ?? 1);
        }

        private static int stackCount(Item item)
        {
            return Math.Max(1, checked((int)Math.Min(int.MaxValue, Math.Floor(item.Upd?.StackObjectsCount ?? 1))));
        }

        private QueueOperationResult addCommand(
            SessionRuntime runtime,
            ClientCommandKind kind,
            string recipeId,
            string? instanceId,
            long now,
            int ttlSeconds)
        {
            int pendingCount = runtime.Commands.Values.Count(command => command.Status != ClientCommandStatus.Completed);
            if (pendingCount >= MaximumPendingCommands)
                return QueueOperationResult.Fail("Too many client commands are already pending.");

            var command = new ClientCommand
            {
                Kind = kind,
                RecipeId = recipeId,
                InstanceId = instanceId,
                CreatedAt = now,
                ExpiresAt = now + ttlSeconds,
                NextDeliveryAt = now
            };
            runtime.Commands[command.CommandId] = command;
            return QueueOperationResult.Ok(command.CommandId);
        }

        private static void completeCommand(ClientCommand command, bool success, string? error, long now)
        {
            command.Status = ClientCommandStatus.Completed;
            command.Success = success;
            command.Error = error;
            command.CompletedAt = now;
        }

        private static string? sanitizeError(string? error)
        {
            if (string.IsNullOrWhiteSpace(error))
                return null;
            string normalized = error.Replace('\r', ' ').Replace('\n', ' ').Trim();
            return normalized.Length <= 300 ? normalized : normalized[..300];
        }

        private static bool isEnvironmentalFailure(string? errorCode)
        {
            return errorCode is "area_busy" or "generator_unavailable" or "executor_changed";
        }

        private static int retryDelaySeconds(int attemptCount)
        {
            int exponent = Math.Clamp(attemptCount - 1, 0, 5);
            return Math.Min(300, 15 * (1 << exponent));
        }

        private static List<ActiveProductionTelemetry> sanitizeProductions(IEnumerable<ActiveProductionTelemetry>? productions)
        {
            return (productions ?? [])
                .Where(production => production != null
                    && !string.IsNullOrWhiteSpace(production.RecipeId)
                    && !string.IsNullOrWhiteSpace(production.InstanceId))
                .Take(MaximumTelemetryProductions)
                .Select(production => new ActiveProductionTelemetry
                {
                    RecipeId = production.RecipeId,
                    AreaType = production.AreaType,
                    TimeLeft = Math.Max(0, production.TimeLeft),
                    ProductionTime = Math.Max(0, production.ProductionTime),
                    ProgressRate = double.IsFinite(production.ProgressRate)
                        ? Math.Clamp(production.ProgressRate, 0, 1)
                        : 1,
                    InstanceId = production.InstanceId
                })
                .ToList();
        }

        private static List<ClientCommand> getCommandsForDelivery(SessionRuntime runtime, long now)
        {
            var result = new List<ClientCommand>();
            foreach (ClientCommand command in runtime.Commands.Values
                .Where(command => command.Status != ClientCommandStatus.Completed && command.NextDeliveryAt <= now)
                .OrderBy(command => command.CreatedAt))
            {
                command.Status = ClientCommandStatus.Delivered;
                command.NextDeliveryAt = now + 5;
                command.DeliveryCount++;
                result.Add(copyCommand(command));
            }

            return result;
        }

        private static ClientCommand copyCommand(ClientCommand command)
        {
            return command with { };
        }

        private QueueClaim? claimNext(
            CraftQueueData data,
            SessionRuntime runtime,
            IReadOnlyCollection<ActiveProductionTelemetry> activeProductions,
            long now,
            out bool changed)
        {
            changed = false;
            HashSet<int> activeAreas = activeProductions.Select(production => production.AreaType).ToHashSet();
            foreach ((HideoutAreas area, LinkedList<QueuedProductionRequest> queue) in data.AreaQueues.OrderBy(pair => (int)pair.Key))
            {
                if (activeAreas.Contains((int)area) || queue.First?.Value is not QueuedProductionRequest entry)
                    continue;
                if (runtime.AreaStartedAt.TryGetValue((int)area, out long startedAt)
                    && now - startedAt < AreaStartGraceSeconds)
                    continue;
                if (entry.Status != QueueEntryStatus.Queued || entry.RetryAt > now)
                    continue;
                if (GetRecipeById(entry.RecipeId) == null)
                {
                    entry.Status = QueueEntryStatus.NeedsReview;
                    entry.LastError = "Recipe metadata is no longer available.";
                    entry.UpdatedAt = now;
                    changed = true;
                    continue;
                }

                entry.Status = QueueEntryStatus.Claimed;
                entry.LeaseUntil = now + ClaimLeaseSeconds;
                entry.RetryAt = null;
                entry.UpdatedAt = now;
                changed = true;
                return new QueueClaim
                {
                    QueueId = entry.QueueId,
                    RecipeId = entry.RecipeId,
                    AreaType = (int)area
                };
            }

            return null;
        }
        private bool expireAmbiguousEntries(MongoId sessionId, SessionRuntime runtime, CraftQueueData data, long now)
        {
            var expired = new List<(LinkedList<QueuedProductionRequest> Queue, QueuedProductionRequest Entry, HideoutAreas Area)>();
            foreach ((HideoutAreas area, LinkedList<QueuedProductionRequest> queue) in data.AreaQueues)
            {
                foreach (QueuedProductionRequest entry in queue)
                {
                    if (entry.Status is (QueueEntryStatus.Claimed or QueueEntryStatus.Starting)
                        && entry.LeaseUntil.HasValue
                        && entry.LeaseUntil.Value <= now)
                        expired.Add((queue, entry, area));
                }
            }

            if (expired.Count == 0)
                return false;

            Dictionary<string, long>? activeKeys = null;
            try
            {
                activeKeys = getActiveProductionKeys(getPmcData(sessionId));
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "[CraftQueue] Could not read the profile while resolving expired claims for {ProfileId}.", sessionId);
            }

            foreach ((LinkedList<QueuedProductionRequest> queue, QueuedProductionRequest entry, HideoutAreas area) in expired)
            {
                if (activeKeys == null)
                {
                    entry.Status = QueueEntryStatus.NeedsReview;
                    entry.LeaseUntil = null;
                    entry.RetryAt = null;
                    entry.LastError = "The client did not confirm whether this craft started. Check the hideout before retrying.";
                    entry.UpdatedAt = now;
                    continue;
                }
                bool startedByThisClaim =
                    activeKeys.TryGetValue(productionKey(area, entry.RecipeId), out long startedAt)
                    && startedAt >= entry.UpdatedAt - ClaimLeaseSeconds;

                if (startedByThisClaim)
                {
                    queue.Remove(entry);
                    runtime.RecentlyConfirmedQueueIds[entry.QueueId] = now + CommandRetentionSeconds;
                    runtime.AreaStartedAt[(int)area] = now;
                    logger.LogInformation(
                        "[CraftQueue] Reconciled unconfirmed craft {RecipeId} against the profile and removed queue entry {QueueId}.",
                        entry.RecipeId,
                        entry.QueueId);
                    continue;
                }

                entry.AttemptCount++;
                entry.Status = QueueEntryStatus.Queued;
                entry.LeaseUntil = null;
                entry.RetryAt = now + retryDelaySeconds(entry.AttemptCount);
                entry.LastError = "EFT did not start this craft in time; CraftQueue will retry it.";
                entry.UpdatedAt = now;
            }

            return true;
        }

        private static bool reconcileConfirmedProductions(CraftQueueData data, SessionRuntime runtime, long now)
        {
            bool changed = false;
            foreach (ActiveProductionTelemetry production in runtime.Telemetry?.Productions ?? [])
            {
                if (!data.AreaQueues.TryGetValue((HideoutAreas)production.AreaType, out LinkedList<QueuedProductionRequest>? queue))
                    continue;

                QueuedProductionRequest? entry = queue.FirstOrDefault(
                    queued => queued.RecipeId == production.RecipeId
                        && queued.Status is QueueEntryStatus.Claimed or QueueEntryStatus.Starting);
                if (entry == null)
                    continue;

                queue.Remove(entry);
                runtime.RecentlyConfirmedQueueIds[entry.QueueId] = now + CommandRetentionSeconds;
                changed = true;
            }

            return changed;
        }

        private static List<QueueEntryView> createQueueView(CraftQueueData data)
        {
            var result = new List<QueueEntryView>();
            foreach ((HideoutAreas area, LinkedList<QueuedProductionRequest> queue) in data.AreaQueues.OrderBy(pair => (int)pair.Key))
            {
                result.AddRange(queue.Select(entry => new QueueEntryView
                {
                    QueueId = entry.QueueId,
                    RecipeId = entry.RecipeId,
                    AreaType = (int)area,
                    Status = entry.Status.ToString(),
                    LastError = entry.LastError,
                    RetryAt = entry.RetryAt,
                    AttemptCount = entry.AttemptCount,
                    CreatedAt = entry.CreatedAt
                }));
            }

            return result;
        }

        private static QueuedProductionRequest? findQueueEntry(CraftQueueData data, string queueId)
        {
            return data.AreaQueues.Values.SelectMany(queue => queue).FirstOrDefault(entry => entry.QueueId == queueId);
        }

        private static int countQueueEntries(CraftQueueData data)
        {
            return data.AreaQueues.Values.Sum(queue => queue.Count);
        }

        private static bool hasFreshTelemetry(SessionRuntime runtime, long now)
        {
            return runtime.Telemetry != null && now - runtime.Telemetry.ReceivedAt <= TelemetryFreshSeconds;
        }

        private static void cleanupRuntime(SessionRuntime runtime, long now)
        {
            foreach (ClientCommand command in runtime.Commands.Values)
            {
                if (command.Status != ClientCommandStatus.Completed && command.ExpiresAt <= now)
                    completeCommand(command, false, "The EFT client did not process this command before it expired.", now);
            }

            foreach (string commandId in runtime.Commands
                .Where(pair => pair.Value.CompletedAt.HasValue && pair.Value.CompletedAt.Value < now - CommandRetentionSeconds)
                .Select(pair => pair.Key)
                .ToArray())
            {
                runtime.Commands.Remove(commandId);
            }

            foreach (string queueId in runtime.RecentlyConfirmedQueueIds
                .Where(pair => pair.Value <= now)
                .Select(pair => pair.Key)
                .ToArray())
            {
                runtime.RecentlyConfirmedQueueIds.Remove(queueId);
            }
        }

        private SessionRuntime getRuntime(MongoId sessionId)
        {
            return sessions.GetOrAdd(sessionId.ToString(), _ => new SessionRuntime());
        }

        private static long unixNow()
        {
            return DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        }

        private string resolveCategory(MongoId endProductId, Dictionary<MongoId, TemplateItem> items)
        {
            if (!items.TryGetValue(endProductId, out TemplateItem? item) || item == null)
                return "Other";

            MongoId current = item.Parent;
            for (int depth = 0; depth < 5; depth++)
            {
                if (current == default)
                    break;
                string? name = getCategoryName(current.ToString());
                if (name != null)
                    return name;
                if (!items.TryGetValue(current, out TemplateItem? parentItem) || parentItem == null)
                    break;
                current = parentItem.Parent;
            }

            return "Other";
        }

        private static string? getCategoryName(string templateId)
        {
            return templateId switch
            {
                "5485a8684bdc2da71d8b4567" => "Ammo",
                "543be5cb4bdc2deb348b4568" => "Ammo containers",
                "5448e54d4bdc2dcc718b4568" => "Armor",
                "5448e53e4bdc2d60728b4567" => "Backpacks",
                "5448eb774bdc2d0a728b4567" => "Barter items",
                "543be5664bdc2dd4348b4569" => "Containers",
                "5448e8d64bdc2dce718b4568" => "Drinks",
                "5448f3a14bdc2d27728b4569" => "Drugs",
                "5422acb9af1c889c16000029" => "Equipment",
                "5448e8d04bdc2ddf718b4569" => "Food",
                "5447e1d04bdc2dff2f8b4567" => "Grenades",
                "5795f317245977243854e041" => "Keys",
                "543be5e94bdc2df1348b4568" => "Keys",
                "5448f39d4bdc2d0a728b4568" => "Medical",
                "5448f3ac4bdc2dce718b4569" => "Medical",
                "5448f3a64bdc2d60728b456a" => "Stimulants",
                "55802f4a4bdc2ddb688b4569" => "Provisions",
                "5a341c4086f77401f2541505" => "Headwear",
                "5447bedf4bdc2d87278b4568" => "Weapons",
                "5447bed64bdc2d97278b4568" => "Weapons",
                "5447b5cf4bdc2d65278b4567" => "Weapons",
                "5447b5e04bdc2d62278b4567" => "Weapons",
                "5447b5f14bdc2d61278b4567" => "Weapons",
                "5447b5fc4bdc2d87278b4567" => "Weapons",
                "5447b6094bdc2dc3278b4567" => "Weapons",
                "5447b6194bdc2d67278b4567" => "Weapons",
                "5447b6254bdc2dc3278b4568" => "Weapons",
                "617f1ef5e8b54b0998387733" => "Weapons",
                "5447bee84bdc2dc3278b4569" => "Weapon mods",
                _ => null
            };
        }
    }

    internal sealed class SessionRuntime
    {
        public object Sync { get; } = new();
        public bool InRaid { get; set; }
        public long RaidStateChangedAt { get; set; }
        public ClientTelemetrySnapshot? Telemetry { get; set; }
        public Dictionary<string, ClientCommand> Commands { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, long> RecentlyConfirmedQueueIds { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, long> ProductionRetryAt { get; } = new(StringComparer.Ordinal);
        public Dictionary<int, long> AreaStartedAt { get; } = [];
        public Task ProfileSaveTask { get; set; } = Task.CompletedTask;
        public bool ProfileSaveQueued { get; set; }
        public int ProfileSaveFailures { get; set; }
        public long ConfirmSequence { get; set; }
        public Dictionary<string, List<int>>? AvailabilityCache { get; set; }
        public long AvailabilityCacheAtMs { get; set; }
        public string? AvailabilityCacheKey { get; set; }
        public string? RecipeIdsHash { get; set; }
        public bool LedgerReconciled { get; set; }
    }

    internal sealed record ServerCraftPlan(
        HideoutAreas Area,
        HideoutProduction Recipe);

    internal sealed class ServerInventorySnapshot
    {
        private readonly Dictionary<MongoId, List<Item>> itemsByTemplate;
        private readonly Dictionary<string, List<Item>> childrenByParent;
        private readonly Dictionary<MongoId, TemplateItem> templates;
        private readonly Dictionary<MongoId, bool> emptyItemCache = [];
        private readonly Dictionary<MongoId, bool> functionalItemCache = [];

        public ServerInventorySnapshot(
            List<Item> stashItems,
            List<Item> allItems,
            Dictionary<MongoId, TemplateItem> templates)
        {
            StashItems = stashItems;
            this.templates = templates;
            itemsByTemplate = stashItems
                .GroupBy(item => item.Template)
                .ToDictionary(group => group.Key, group => group.ToList());
            childrenByParent = allItems
                .Where(item => !string.IsNullOrEmpty(item.ParentId))
                .GroupBy(item => item.ParentId!, StringComparer.Ordinal)
                .ToDictionary(group => group.Key, group => group.ToList(), StringComparer.Ordinal);
        }

        public List<Item> StashItems { get; }

        public IReadOnlyList<Item> GetItems(MongoId templateId)
        {
            return itemsByTemplate.TryGetValue(templateId, out List<Item>? items) ? items : [];
        }

        public bool TryGetTemplate(MongoId templateId, out TemplateItem? template)
        {
            bool found = templates.TryGetValue(templateId, out TemplateItem? value);
            template = value;
            return found;
        }

        public bool IsEmptyRequirementItem(Item item)
        {
            if (emptyItemCache.TryGetValue(item.Id, out bool cached))
                return cached;

            bool empty = !childrenByParent.TryGetValue(item.Id.ToString(), out List<Item>? children)
                || children.All(child => child.Location == null);

            emptyItemCache[item.Id] = empty;
            return empty;
        }

        public bool HasRequiredParts(Item item)
        {
            if (functionalItemCache.TryGetValue(item.Id, out bool cached))
                return cached;

            bool functional = true;
            var pending = new Stack<Item>();
            var visited = new HashSet<MongoId>();
            pending.Push(item);
            while (pending.Count > 0 && functional)
            {
                Item current = pending.Pop();
                if (!visited.Add(current.Id))
                    continue;
                if (!templates.TryGetValue(current.Template, out TemplateItem? template))
                {
                    functional = false;
                    break;
                }

                childrenByParent.TryGetValue(current.Id.ToString(), out List<Item>? children);
                foreach (var required in template.Properties?.Slots?.Where(slot => slot.Required == true) ?? [])
                {
                    if (children?.Any(child => child.Location == null && child.SlotId == required.Name) != true)
                    {
                        functional = false;
                        break;
                    }
                }

                foreach (Item child in children?.Where(child => child.Location == null) ?? [])
                    pending.Push(child);
            }

            functionalItemCache[item.Id] = functional;
            return functional;
        }
    }

    internal sealed class ClientTelemetrySnapshot
    {
        public long ReceivedAt { get; set; }
        public List<ActiveProductionTelemetry> Productions { get; set; } = [];
        public HashSet<string> AvailableRecipeIds { get; set; } = new(StringComparer.Ordinal);
    }

    public sealed class CraftQueueState
    {
        public bool ClientOnline { get; set; }
        public bool RaidAutomationActive { get; set; }
        public bool ProfileView { get; set; }
        public bool RecipeListAvailable { get; set; }
        public Dictionary<string, List<int>> MaterialAvailability { get; set; } = [];
        public List<ActiveProductionView> ActiveProductions { get; set; } = [];
        public List<string> AvailableRecipeIds { get; set; } = [];
        public List<QueueEntryView> QueueEntries { get; set; } = [];
        public List<ClientCommand> Commands { get; set; } = [];
    }

    public sealed class ActiveProductionView
    {
        public string RecipeId { get; set; } = "";
        public string ProductName { get; set; } = "";
        public int AreaType { get; set; }
        public int TimeLeft { get; set; }
        public int ProductionTime { get; set; }
        public double ProgressRate { get; set; } = 1;
        public string InstanceId { get; set; } = "";
    }

    public sealed class RecipeInfo
    {
        public string RecipeId { get; set; } = "";
        public string EndProduct { get; set; } = "";
        public string ProductName { get; set; } = "";
        public int AreaType { get; set; }
        public int ProductionTime { get; set; }
        public List<RecipeRequirement> Requirements { get; set; } = [];
    }

    public sealed class RecipeRequirement
    {
        public string TemplateId { get; set; } = "";
        public string Name { get; set; } = "";
        public int Count { get; set; }
        public bool IsTool { get; set; }
    }
}
