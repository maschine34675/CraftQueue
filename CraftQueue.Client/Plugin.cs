using BepInEx;
using BepInEx.Configuration;
using Comfort.Common;
using CraftQueue.Shared;
using EFT;
using EFT.Hideout;
using EFT.InventoryLogic;
using Newtonsoft.Json;
using SPT.Common.Http;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

namespace CraftQueue
{
    [BepInPlugin(PluginGuid, PluginName, PluginVersion)]
    public class Plugin : BaseUnityPlugin
    {
        public const string PluginGuid = CraftQueueProtocol.ModGuid;
        public const string PluginName = CraftQueueProtocol.PluginName;
        public const string PluginVersion = CraftQueueProtocol.Version;

        private const float PollInterval = 2f;
        private const float WebRetryInterval = 30f;
        private const float CollectRetryInterval = 60f;
        private const int ClientApiTimeoutMs = 5000;
        private const int MaximumLocalResponseBytes = 1024 * 1024;

        private readonly Dictionary<string, float> collectRetryAt = new Dictionary<string, float>(StringComparer.Ordinal);
        private readonly Dictionary<string, CachedCommandResult> commandResults = new Dictionary<string, CachedCommandResult>(StringComparer.Ordinal);
        private readonly Dictionary<string, string> runIds = new Dictionary<string, string>(StringComparer.Ordinal);
        private List<string> clientApiHosts;
        private string preferredClientApiHost;
        private string lastRejectedToken;
        private ClientChannelInfo channelInfo;
        private float nextPoll;
        private float nextErrorLog;
        private float nextWebApiRetry;
        private float nextChannelInfoRetry;
        private bool usingBackendFallback;
        private bool processing;

        private ConfigEntry<KeyboardShortcut> openWebInterfaceKey;

        private void Awake()
        {
            this.clientApiHosts = getClientApiHosts();
            this.openWebInterfaceKey = this.Config.Bind(
                "Web Interface",
                "Open in browser",
                new KeyboardShortcut(KeyCode.F9),
                "Opens your personal CraftQueue web interface in the default browser.");
        }

        private void Update()
        {
            if (this.openWebInterfaceKey.Value.IsDown())
                this.openWebInterface();

            if (this.processing || Time.time < this.nextPoll)
                return;

            this.nextPoll = Time.time + PollInterval;
            HideoutClass hideout;
            if (!tryGetExecutorHideout(out hideout))
                return;

            _ = this.process();
        }

        private async Task process()
        {
            this.processing = true;
            try
            {
                HideoutClass hideout;
                if (!tryGetExecutorHideout(out hideout))
                    return;
                object executor = hideout.ProductionController;

                await this.collectCompletedProductions(hideout, executor);
                if (!isSameExecutor(hideout, executor))
                    return;

                ClientPollResponse poll = await this.poll(hideout);
                QueueClaim claim = poll == null ? null : poll.claim;
                if (!isSameExecutor(hideout, executor))
                {
                    await this.abandonClaim(claim);
                    return;
                }

                await this.processCommands(hideout, poll == null ? null : poll.commands, poll == null ? null : poll.queueEntries);
                if (!isSameExecutor(hideout, executor))
                {
                    await this.abandonClaim(claim);
                    return;
                }

                if (claim == null || string.IsNullOrEmpty(claim.queueId))
                    return;

                await this.startClaimedCraft(hideout, claim);
            }
            catch (Exception ex)
            {
                if (Time.realtimeSinceStartup >= this.nextErrorLog)
                {
                    this.nextErrorLog = Time.realtimeSinceStartup + 30f;
                    this.Logger.LogError("CraftQueue executor: " + ex);
                }
            }
            finally
            {
                this.processing = false;
            }
        }

        private static bool tryGetExecutorHideout(out HideoutClass hideout)
        {
            hideout = null;
#pragma warning disable CS0618
            if (GClass2340.InRaid)
                return false;
#pragma warning restore CS0618

            HideoutClass candidate = Singleton<HideoutClass>.Instance;
            if (candidate == null
                || candidate.ProductionController == null
                || candidate.ProductionController.Dictionary_0 == null
                || candidate.ISession == null
                || candidate.ISession.Profile == null
                || candidate.ISession.Profile.Inventory == null
                || candidate.ISession.Profile.Inventory.Stash == null)
                return false;
            if (candidate.InventoryController_0 == null
                || candidate.InventoryController_0.Inventory == null
                || candidate.InventoryController_0.Inventory.Stash == null
                || !ReferenceEquals(candidate.InventoryController_0.Inventory, candidate.ISession.Profile.Inventory))
                return false;

            hideout = candidate;
            return true;
        }

        private static bool isSameExecutor(HideoutClass expectedHideout, object expectedController)
        {
            HideoutClass current;
            return tryGetExecutorHideout(out current)
                && ReferenceEquals(current, expectedHideout)
                && ReferenceEquals(current.ProductionController, expectedController);
        }
        private async Task abandonClaim(QueueClaim claim)
        {
            if (claim == null || string.IsNullOrEmpty(claim.queueId))
                return;

            try
            {
                await this.ack(claim.queueId, false, "executor_changed", "The hideout executor changed before the craft could be started.");
            }
            catch (Exception ex)
            {
                this.Logger.LogWarning("CraftQueue: could not release the claim for " + claim.recipeId + " (" + ex.Message + ").");
            }
        }

        private async Task<ClientPollResponse> poll(HideoutClass hideout)
        {
            var payload = new
            {
                profileId = RequestHandler.SessionId,
                productions = buildTelemetry(hideout),
                availableRecipeIds = buildAvailableRecipeIds(hideout)
            };
            string response = await this.postWithFallback(
                "/api/client/poll",
                "/craftqueue/Poll",
                payload);
            return JsonConvert.DeserializeObject<ClientPollResponse>(response);
        }

        private async Task startClaimedCraft(HideoutClass hideout, QueueClaim claim)
        {
            GClass2431 producer;
            ProductionBuildAbstractClass scheme;
            if (!tryFindScheme(hideout, claim.recipeId, out producer, out scheme))
            {
                await this.ack(claim.queueId, false, "recipe_unavailable", "Recipe is not available in the local hideout.");
                return;
            }

            if (producer.ProducingItems.Count > 0
                || producer.Dictionary_2.Count > 0
                || producer.HasCompletedItems
                || !producer.CanStart
                || !producer.CanStartByProductionCount())
            {
                await this.ack(claim.queueId, false, "area_busy", "The hideout area is still occupied.");
                return;
            }

            if (!producer.CanStartScheme((GClass2440)scheme))
            {
                await this.ack(claim.queueId, false, "generator_unavailable", "The generator requirements are not currently met.");
                return;
            }

            List<GClass1433> items;
            List<GClass1433> tools;
            string selectionError;
            if (!trySelectItems(hideout, scheme, out items, out tools, out selectionError))
            {
                await this.ack(claim.queueId, false, "requirements_unavailable", selectionError);
                return;
            }

            try
            {
                hideout.ISession.StartSingleProduction(scheme._id, items, tools);
                producer.StartProducing(scheme);
            }
            catch (Exception ex)
            {
                await this.ack(claim.queueId, false, "start_exception", "The game rejected the start: " + ex.Message);
                return;
            }

            bool accepted = await this.ack(claim.queueId, true, null, null);
            if (!accepted)
                this.Logger.LogWarning("CraftQueue: the server did not accept the start acknowledgement for " + claim.recipeId + ".");
        }

        private async Task processCommands(
            HideoutClass hideout,
            IEnumerable<ClientCommand> commands,
            IList<QueueEntry> queueEntries)
        {
            cleanupCommandCache();
            foreach (ClientCommand command in commands ?? Enumerable.Empty<ClientCommand>())
            {
                if (string.IsNullOrEmpty(command.commandId))
                    continue;

                CachedCommandResult cached;
                if (!this.commandResults.TryGetValue(command.commandId, out cached))
                {
                    cached = await this.executeCommand(hideout, command, queueEntries ?? new List<QueueEntry>());
                    this.commandResults[command.commandId] = cached;
                }

                if (cached.success
                    && string.Equals(command.kind, "Enqueue", StringComparison.OrdinalIgnoreCase)
                    && queueEntries != null)
                {
                    GClass2431 producer;
                    ProductionBuildAbstractClass scheme;
                    if (tryFindScheme(hideout, command.recipeId, out producer, out scheme))
                    {
                        queueEntries.Add(new QueueEntry
                        {
                            recipeId = command.recipeId,
                            areaType = (int)producer.AreaType,
                            status = "Queued"
                        });
                    }
                }

                await this.sendCommandResult(cached);
            }
        }

        private async Task<CachedCommandResult> executeCommand(
            HideoutClass hideout,
            ClientCommand command,
            IList<QueueEntry> queueEntries)
        {
            var result = new CachedCommandResult
            {
                commandId = command.commandId,
                createdAt = Time.realtimeSinceStartup
            };

            try
            {
                if (matchesWireEnum(command.kind, "Enqueue", 0))
                {
                    GClass2431 producer;
                    ProductionBuildAbstractClass scheme;
                    if (!tryFindScheme(hideout, command.recipeId, out producer, out scheme) || scheme.locked)
                    {
                        result.success = false;
                        result.errorCode = "recipe_unavailable";
                        result.error = "This recipe is not available for the current profile.";
                        return result;
                    }

                    string error;
                    result.success = validateProjectedRequirements(hideout, queueEntries, producer, scheme, out error);
                    result.errorCode = result.success ? null : "requirements_unavailable";
                    result.error = result.success ? null : error;
                    return result;
                }

                if (matchesWireEnum(command.kind, "CancelActive", 1))
                {
                    GClass2431 producer;
                    ProductionBuildAbstractClass scheme;
                    if (!tryFindScheme(hideout, command.recipeId, out producer, out scheme)
                        || !isCurrentProduction(hideout, command.recipeId, command.instanceId))
                    {
                        result.success = false;
                        result.errorCode = "production_changed";
                        result.error = "The selected production is no longer active.";
                        return result;
                    }

                    IResult cancelResult = await hideout.CancelProduction(scheme);
                    result.success = cancelResult != null && cancelResult.Succeed;
                    result.errorCode = result.success ? null : "cancel_failed";
                    result.error = result.success
                        ? null
                        : "The game rejected the cancellation" + (cancelResult == null || cancelResult.Error == null ? "." : ": " + cancelResult.Error);
                    return result;
                }

                result.success = false;
                result.errorCode = "unknown_command";
                result.error = "Unsupported CraftQueue command kind '"
                    + (string.IsNullOrEmpty(command.kind) ? "<missing>" : command.kind)
                    + "'.";
                return result;
            }
            catch (Exception ex)
            {
                result.success = false;
                result.errorCode = "client_exception";
                result.error = ex.GetType().Name + ": " + ex.Message;
                return result;
            }
        }

        private async Task sendCommandResult(CachedCommandResult result)
        {
            var payload = new
            {
                profileId = RequestHandler.SessionId,
                commandId = result.commandId,
                success = result.success,
                errorCode = result.errorCode,
                error = result.error
            };
            await this.postWithFallback(
                "/api/client/command-result",
                "/craftqueue/CommandResult",
                payload);
        }

        private async Task<bool> ack(string queueId, bool success, string errorCode, string error)
        {
            var payload = new
            {
                profileId = RequestHandler.SessionId,
                queueId,
                success,
                errorCode,
                error
            };
            string response = await this.postWithFallback(
                "/api/client/ack",
                "/craftqueue/Ack",
                payload);
            return JsonConvert.DeserializeObject<bool>(response);
        }

        private async Task<string> postWithFallback(string webPath, string backendPath, object payload)
        {
            await this.ensureChannelInfo();

            if (this.channelInfo != null
                && (!this.usingBackendFallback || Time.realtimeSinceStartup >= this.nextWebApiRetry))
            {
                Exception lastError = null;
                foreach (string host in this.getOrderedClientApiHosts())
                {
                    try
                    {
                        string response = await postClientApi(host, this.channelInfo.port, this.channelInfo.token, webPath, payload);
                        this.preferredClientApiHost = host;
                        if (this.usingBackendFallback)
                            this.Logger.LogInfo("CraftQueue: local web channel restored.");
                        this.usingBackendFallback = false;
                        return response;
                    }
                    catch (UnauthorizedChannelException)
                    {
                        string rejected = this.channelInfo.token;
                        this.channelInfo = null;
                        if (string.Equals(rejected, this.lastRejectedToken, StringComparison.Ordinal))
                        {
                            this.nextChannelInfoRetry = Time.realtimeSinceStartup + WebRetryInterval;
                            lastError = new InvalidOperationException("The local CraftQueue channel rejected a freshly issued token.");
                            break;
                        }

                        this.lastRejectedToken = rejected;
                        this.nextChannelInfoRetry = 0f;
                        this.Logger.LogInfo("CraftQueue: local web channel token expired; requesting a new one.");
                        return await postSptApi(backendPath, payload);
                    }
                    catch (Exception ex)
                    {
                        lastError = ex;
                    }
                }

                bool firstFailure = !this.usingBackendFallback;
                this.usingBackendFallback = true;
                this.nextWebApiRetry = Time.realtimeSinceStartup + WebRetryInterval;
                if (firstFailure && lastError != null)
                    this.Logger.LogWarning(
                        "CraftQueue: local web channel unavailable (" + lastError.GetType().Name + ": " + lastError.Message + "); using the SPT backend fallback.");
            }

            return await postSptApi(backendPath, payload);
        }

        private IEnumerable<string> getOrderedClientApiHosts()
        {
            if (!string.IsNullOrEmpty(this.preferredClientApiHost)
                && this.clientApiHosts.Contains(this.preferredClientApiHost, StringComparer.OrdinalIgnoreCase))
                yield return this.preferredClientApiHost;

            foreach (string host in this.clientApiHosts)
            {
                if (!string.Equals(host, this.preferredClientApiHost, StringComparison.OrdinalIgnoreCase))
                    yield return host;
            }
        }

        private async Task ensureChannelInfo()
        {
            if (this.channelInfo != null || Time.realtimeSinceStartup < this.nextChannelInfoRetry)
                return;

            try
            {
                string response = await postSptApi("/craftqueue/ChannelInfo", new { });
                ClientChannelInfo info = JsonConvert.DeserializeObject<ClientChannelInfo>(response);
                if (info == null || info.port <= 0 || string.IsNullOrEmpty(info.token))
                    throw new InvalidDataException("CraftQueue channel information was incomplete.");
                this.channelInfo = info;
                this.Logger.LogInfo(
                    "CraftQueue: local web channel targets " + string.Join(", ", this.clientApiHosts.ToArray()) + " on port " + info.port + ".");
                this.Logger.LogInfo(
                    "CraftQueue: your personal web interface: http://" + getWebLinkHost() + ":" + info.port + "/?token=" + Uri.EscapeDataString(info.token));
            }
            catch (Exception ex)
            {
                this.nextChannelInfoRetry = Time.realtimeSinceStartup + WebRetryInterval;
                if (Time.realtimeSinceStartup >= this.nextErrorLog)
                {
                    this.nextErrorLog = Time.realtimeSinceStartup + 30f;
                    this.Logger.LogWarning("CraftQueue: could not obtain local channel information; backend fallback remains active (" + ex.Message + ").");
                }
            }
        }

        private async Task collectCompletedProductions(HideoutClass hideout, object executor)
        {
            var productionGroups = hideout.ProductionController.Dictionary_0;
            if (productionGroups == null)
                return;

            foreach (List<GClass2431> producers in productionGroups.Values.ToList())
            {
                if (producers == null)
                    continue;

                foreach (GClass2431 producer in producers.ToList())
                {
                    if (producer == null || producer.CompleteItemsStorage == null || producer.Schemes == null)
                        continue;

                    string areaRetryKey = "area:" + (int)producer.AreaType;
                    float areaRetryAt;
                    if (this.collectRetryAt.TryGetValue(areaRetryKey, out areaRetryAt)
                        && Time.realtimeSinceStartup < areaRetryAt)
                        continue;

                    try
                    {
                        string recipeId = producer.CompleteItemsStorage.FindCompleteItems().Item1;
                        if (string.IsNullOrEmpty(recipeId))
                            continue;
                        float retryAt;
                        if (this.collectRetryAt.TryGetValue(recipeId, out retryAt) && Time.realtimeSinceStartup < retryAt)
                            continue;

                        ProductionBuildAbstractClass scheme;
                        if (!producer.Schemes.TryGetValue(recipeId, out scheme) || scheme.continuous)
                            continue;
                        if (!isSameExecutor(hideout, executor))
                            return;

                        bool collected = await hideout.GetProducedItems(producer, recipeId, false);
                        if (collected)
                        {
                            producer.GetItems(recipeId);
                            this.collectRetryAt.Remove(recipeId);
                            this.collectRetryAt.Remove(areaRetryKey);
                        }
                        else
                        {
                            this.collectRetryAt[recipeId] = Time.realtimeSinceStartup + CollectRetryInterval;
                        }
                    }
                    catch (Exception ex)
                    {
                        this.collectRetryAt[areaRetryKey] = Time.realtimeSinceStartup + CollectRetryInterval;
                        this.Logger.LogWarning("CraftQueue: collecting a completed production will retry in 60 seconds (" + ex.Message + ").");
                    }
                }
            }
        }

        private List<object> buildTelemetry(HideoutClass hideout)
        {
            var productions = new List<object>();
            var seenRuns = new HashSet<string>(StringComparer.Ordinal);
            if (hideout == null
                || hideout.ProductionController == null
                || hideout.ProductionController.Dictionary_0 == null)
                return productions;
            Dictionary<string, HideoutProductionsDataClass> snapshot = getProfileProductionSnapshot(hideout);

            foreach (List<GClass2431> producers in hideout.ProductionController.Dictionary_0.Values)
            {
                if (producers == null)
                    continue;
                foreach (GClass2431 producer in producers)
                {
                    if (producer == null || producer.Schemes == null)
                        continue;

                    this.addRunningProductions(productions, seenRuns, producer, snapshot);
                    this.addPausedProductions(productions, seenRuns, producer, snapshot);
                }
            }

            this.forgetFinishedRuns(seenRuns);
            return productions;
        }

        private void addRunningProductions(
            List<object> productions,
            HashSet<string> seenRuns,
            GClass2431 producer,
            Dictionary<string, HideoutProductionsDataClass> snapshot)
        {
            foreach (KeyValuePair<string, GClass2438> pair in producer.ProducingItems)
            {
                GClass2438 live = pair.Value;
                if (live == null || !isQueueableScheme(producer, pair.Key))
                    continue;

                int left = Math.Max(0, live.EstimatedTimeInt);
                int total = live.Progress < 0.999
                    ? Math.Max(left, (int)Math.Ceiling(left / Math.Max(0.001, 1.0 - live.Progress)))
                    : left;
                productions.Add(this.describeProduction(seenRuns, producer, pair.Key, left, snapshotTotal(snapshot, pair.Key, total), 1.0));
            }
        }

        private void addPausedProductions(
            List<object> productions,
            HashSet<string> seenRuns,
            GClass2431 producer,
            Dictionary<string, HideoutProductionsDataClass> snapshot)
        {
            foreach (KeyValuePair<string, GClass2438> pair in producer.Dictionary_2)
            {
                if (pair.Value == null || !isQueueableScheme(producer, pair.Key))
                    continue;
                HideoutProductionsDataClass snapshotEntry = lookupSnapshot(snapshot, pair.Key);
                int total = snapshotEntry == null ? 0 : Math.Max(0, snapshotEntry.ProductionTime);
                int left = snapshotEntry == null ? 0 : Math.Max(0, (int)Math.Ceiling(total - snapshotEntry.Progress));
                productions.Add(this.describeProduction(seenRuns, producer, pair.Key, left, total, 0.0));
            }
        }

        private object describeProduction(
            HashSet<string> seenRuns,
            GClass2431 producer,
            string recipeId,
            int timeLeft,
            int productionTime,
            double progressRate)
        {
            string runKey = (int)producer.AreaType + ":" + recipeId;
            seenRuns.Add(runKey);
            return new
            {
                recipeId,
                areaType = (int)producer.AreaType,
                timeLeft,
                productionTime,
                progressRate,
                instanceId = this.resolveRunId(runKey, recipeId)
            };
        }
        private string resolveRunId(string runKey, string recipeId)
        {
            string runId;
            if (!this.runIds.TryGetValue(runKey, out runId))
            {
                runId = Guid.NewGuid().ToString("N").Substring(0, 8);
                this.runIds[runKey] = runId;
            }
            return recipeId + ":" + runId;
        }

        private void forgetFinishedRuns(HashSet<string> seenRuns)
        {
            foreach (string runKey in this.runIds.Keys.Where(key => !seenRuns.Contains(key)).ToList())
                this.runIds.Remove(runKey);
        }

        private static bool isQueueableScheme(GClass2431 producer, string recipeId)
        {
            ProductionBuildAbstractClass scheme;
            return producer.Schemes.TryGetValue(recipeId, out scheme)
                && scheme is GClass2440
                && !scheme.continuous;
        }

        private static HideoutProductionsDataClass lookupSnapshot(
            Dictionary<string, HideoutProductionsDataClass> snapshot,
            string recipeId)
        {
            HideoutProductionsDataClass entry = null;
            if (snapshot != null)
                snapshot.TryGetValue(recipeId, out entry);
            return entry;
        }

        private static int snapshotTotal(
            Dictionary<string, HideoutProductionsDataClass> snapshot,
            string recipeId,
            int liveTotal)
        {
            HideoutProductionsDataClass entry = lookupSnapshot(snapshot, recipeId);
            return entry == null ? liveTotal : Math.Max(liveTotal, Math.Max(0, entry.ProductionTime));
        }

        private static Dictionary<string, HideoutProductionsDataClass> getProfileProductionSnapshot(HideoutClass hideout)
        {
            if (hideout == null
                || hideout.ISession == null
                || hideout.ISession.Profile == null
                || hideout.ISession.Profile.Hideout == null)
                return null;
            return hideout.ISession.Profile.Hideout.Production;
        }

        private static List<string> buildAvailableRecipeIds(HideoutClass hideout)
        {
            var recipeIds = new HashSet<string>(StringComparer.Ordinal);
            if (hideout == null || hideout.ProductionController == null || hideout.ProductionController.Dictionary_0 == null)
                return recipeIds.ToList();

            foreach (List<GClass2431> producers in hideout.ProductionController.Dictionary_0.Values)
            {
                if (producers == null)
                    continue;
                foreach (GClass2431 producer in producers)
                {
                    if (producer == null || producer.Schemes == null)
                        continue;
                    foreach (ProductionBuildAbstractClass scheme in producer.Schemes.Values)
                    {
                        if (scheme is GClass2440 && !scheme.continuous && !scheme.locked)
                            recipeIds.Add(scheme._id);
                    }
                }
            }

            return recipeIds.ToList();
        }

        private bool isCurrentProduction(HideoutClass hideout, string recipeId, string instanceId)
        {
            GClass2431 producer;
            ProductionBuildAbstractClass scheme;
            if (!tryFindScheme(hideout, recipeId, out producer, out scheme))
                return false;
            if (!producer.ProducingItems.ContainsKey(recipeId) && !producer.Dictionary_2.ContainsKey(recipeId))
                return false;
            string runId;
            return this.runIds.TryGetValue((int)producer.AreaType + ":" + recipeId, out runId)
                && string.Equals(recipeId + ":" + runId, instanceId, StringComparison.Ordinal);
        }

        private static bool validateProjectedRequirements(
            HideoutClass hideout,
            IList<QueueEntry> queueEntries,
            GClass2431 candidateProducer,
            ProductionBuildAbstractClass candidateScheme,
            out string error)
        {
            error = null;
            var queuedPlans = new List<PlannedScheme>();
            var activePlans = getActivePlans(hideout);
            var activeKeys = new HashSet<string>(
                activePlans.Select(plan => plan.AreaType + ":" + plan.Scheme._id),
                StringComparer.Ordinal);

            foreach (QueueEntry entry in queueEntries)
            {
                if (entry == null || matchesWireEnum(entry.status, "NeedsReview", 3))
                    continue;
                if ((matchesWireEnum(entry.status, "Claimed", 1)
                    || matchesWireEnum(entry.status, "Starting", 2))
                    && activeKeys.Contains(entry.areaType + ":" + entry.recipeId))
                    continue;

                GClass2431 producer;
                ProductionBuildAbstractClass scheme;
                if (tryFindScheme(hideout, entry.recipeId, out producer, out scheme))
                    queuedPlans.Add(new PlannedScheme((int)producer.AreaType, scheme, false));
            }

            queuedPlans.Add(new PlannedScheme((int)candidateProducer.AreaType, candidateScheme, false));

            List<Item> stashItems = hideout.AllStashItems
                .Where(isUsableRequirementItem)
                .ToList();
            if (!validateConsumables(stashItems, queuedPlans, out error))
                return false;

            var allPlans = new List<PlannedScheme>(activePlans.Count + queuedPlans.Count);
            allPlans.AddRange(activePlans);
            allPlans.AddRange(queuedPlans);
            return validateTools(stashItems, allPlans, out error);
        }

        private static bool validateConsumables(
            List<Item> stashItems,
            List<PlannedScheme> queuedPlans,
            out string error)
        {
            error = null;
            var remainingByItem = stashItems.ToDictionary(item => item.Id, item => item.StackObjectsCount, StringComparer.Ordinal);
            List<ItemRequirement> requirements = queuedPlans
                .SelectMany(plan => plan.Scheme.RequiredItems)
                .Where(requirement => !(requirement is ToolRequirement))
                .OrderBy(requirement => stashItems.Count(requirement.IsSuitableItem))
                .ToList();

            foreach (ItemRequirement requirement in requirements)
            {
                int needed = Math.Max(1, requirement.IntCount);
                int availableBefore = 0;
                foreach (Item item in stashItems.Where(requirement.IsSuitableItem))
                {
                    int available;
                    if (!remainingByItem.TryGetValue(item.Id, out available) || available <= 0)
                        continue;
                    availableBefore += available;
                    int take = Math.Min(needed, available);
                    remainingByItem[item.Id] = available - take;
                    needed -= take;
                    if (needed <= 0)
                        break;
                }

                if (needed > 0)
                {
                    int required = Math.Max(1, requirement.IntCount);
                    error = "Not enough " + requirementName(requirement) + ": available " + availableBefore
                        + ", required " + required + " including queued crafts.";
                    return false;
                }
            }

            return true;
        }

        private static bool validateTools(
            List<Item> stashItems,
            List<PlannedScheme> plans,
            out string error)
        {
            error = null;
            var requirementsByTemplate = plans
                .SelectMany(plan => plan.Scheme.RequiredItems.OfType<ToolRequirement>())
                .GroupBy(requirement => requirement.TemplateId)
                .ToDictionary(group => group.Key, group => group.Cast<ItemRequirement>().ToList(), StringComparer.Ordinal);

            var requiredByArea = new Dictionary<int, Dictionary<string, int>>();
            var heldByActiveCrafts = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (PlannedScheme plan in plans)
            {
                Dictionary<string, int> areaRequirements;
                if (!requiredByArea.TryGetValue(plan.AreaType, out areaRequirements))
                {
                    areaRequirements = new Dictionary<string, int>(StringComparer.Ordinal);
                    requiredByArea[plan.AreaType] = areaRequirements;
                }

                foreach (ToolRequirement requirement in plan.Scheme.RequiredItems.OfType<ToolRequirement>())
                {
                    int count = Math.Max(1, requirement.IntCount);
                    int existing;
                    areaRequirements.TryGetValue(requirement.TemplateId, out existing);
                    areaRequirements[requirement.TemplateId] = Math.Max(existing, count);

                    if (plan.IsActive)
                    {
                        heldByActiveCrafts.TryGetValue(requirement.TemplateId, out existing);
                        heldByActiveCrafts[requirement.TemplateId] = existing + count;
                    }
                }
            }

            foreach (KeyValuePair<string, List<ItemRequirement>> pair in requirementsByTemplate)
            {
                int required = requiredByArea.Values.Sum(area =>
                {
                    int count;
                    return area.TryGetValue(pair.Key, out count) ? count : 0;
                });
                int available = stashItems
                    .Where(item => pair.Value.Any(requirement => requirement.IsSuitableItem(item)))
                    .Sum(item => Math.Max(1, item.StackObjectsCount));
                int held;
                if (heldByActiveCrafts.TryGetValue(pair.Key, out held))
                    available += held;

                if (available < required)
                {
                    error = "Missing tool " + requirementName(pair.Value[0]) + ": available " + available
                        + ", required " + required + " across queued areas.";
                    return false;
                }
            }

            return true;
        }

        private static List<PlannedScheme> getActivePlans(HideoutClass hideout)
        {
            var plans = new List<PlannedScheme>();
            if (hideout == null
                || hideout.ProductionController == null
                || hideout.ProductionController.Dictionary_0 == null)
                return plans;

            foreach (List<GClass2431> producers in hideout.ProductionController.Dictionary_0.Values)
            {
                if (producers == null)
                    continue;
                foreach (GClass2431 producer in producers)
                {
                    if (producer == null || producer.Schemes == null)
                        continue;
                    foreach (string recipeId in producer.ProducingItems.Keys.Concat(producer.Dictionary_2.Keys))
                    {
                        ProductionBuildAbstractClass scheme;
                        if (isQueueableScheme(producer, recipeId) && producer.Schemes.TryGetValue(recipeId, out scheme))
                            plans.Add(new PlannedScheme((int)producer.AreaType, scheme, true));
                    }
                }
            }

            return plans;
        }

        private static bool tryFindScheme(
            HideoutClass hideout,
            string recipeId,
            out GClass2431 producer,
            out ProductionBuildAbstractClass scheme)
        {
            if (hideout != null
                && hideout.ProductionController != null
                && hideout.ProductionController.Dictionary_0 != null)
            {
                foreach (List<GClass2431> producers in hideout.ProductionController.Dictionary_0.Values)
                {
                    if (producers == null)
                        continue;
                    foreach (GClass2431 candidate in producers)
                    {
                        ProductionBuildAbstractClass found;
                        if (candidate != null
                            && candidate.Schemes != null
                            && candidate.Schemes.TryGetValue(recipeId, out found)
                            && found is GClass2440
                            && !found.continuous)
                        {
                            producer = candidate;
                            scheme = found;
                            return true;
                        }
                    }
                }
            }

            producer = null;
            scheme = null;
            return false;
        }

        private static bool trySelectItems(
            HideoutClass hideout,
            ProductionBuildAbstractClass scheme,
            out List<GClass1433> items,
            out List<GClass1433> tools,
            out string error)
        {
            items = new List<GClass1433>();
            tools = new List<GClass1433>();
            error = null;

            List<Item> stashItems = hideout.AllStashItems.Where(isUsableRequirementItem).ToList();
            var usedCounts = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (ItemRequirement requirement in scheme.RequiredItems)
            {
                int remaining = Math.Max(1, requirement.IntCount);
                foreach (Item item in stashItems.Where(requirement.IsSuitableItem))
                {
                    if (remaining <= 0)
                        break;

                    int alreadyUsed;
                    usedCounts.TryGetValue(item.Id, out alreadyUsed);
                    int available = Math.Max(0, item.StackObjectsCount - alreadyUsed);
                    int take = Math.Min(remaining, available);
                    if (take <= 0)
                        continue;

                    var selected = new GClass1433(item, take);
                    if (requirement is ToolRequirement)
                        tools.Add(selected);
                    else
                        items.Add(selected);
                    usedCounts[item.Id] = alreadyUsed + take;
                    remaining -= take;
                }

                if (remaining > 0)
                {
                    error = "Not enough " + requirementName(requirement) + " to start this craft.";
                    return false;
                }
            }

            return true;
        }

        private static bool isUsableRequirementItem(Item item)
        {
            CompoundItem compound = item as CompoundItem;
            return compound == null || compound.IsEmpty;
        }

        private static string requirementName(ItemRequirement requirement)
        {
            try
            {
                string name = requirement.Item.LocalizedName();
                if (!string.IsNullOrWhiteSpace(name))
                    return name;
            }
            catch
            {
            }

            return string.IsNullOrWhiteSpace(requirement.ItemName) ? requirement.TemplateId : requirement.ItemName;
        }

        private void cleanupCommandCache()
        {
            float cutoff = Time.realtimeSinceStartup - 300f;
            foreach (string commandId in this.commandResults
                .Where(pair => pair.Value.createdAt < cutoff)
                .Select(pair => pair.Key)
                .ToList())
            {
                this.commandResults.Remove(commandId);
            }
        }

        private static bool matchesWireEnum(string value, string name, int legacyValue)
        {
            return string.Equals(value, name, StringComparison.OrdinalIgnoreCase)
                || string.Equals(value, legacyValue.ToString(), StringComparison.Ordinal);
        }

        private static async Task<string> postClientApi(
            string host,
            int port,
            string token,
            string path,
            object payload)
        {
            byte[] body = Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(payload));
            string hostHeader = host.Contains(":") ? "[" + host + "]" : host;
            byte[] headers = Encoding.ASCII.GetBytes(
                "POST " + path + " HTTP/1.1\r\n"
                + "Host: " + hostHeader + ":" + port + "\r\n"
                + "Content-Type: application/json\r\n"
                + "X-CraftQueue-Token: " + token + "\r\n"
                + "Content-Length: " + body.Length + "\r\n"
                + "Connection: close\r\n\r\n");

            using (var client = new TcpClient { NoDelay = true })
            using (var timeout = new CancellationTokenSource(ClientApiTimeoutMs))
            using (timeout.Token.Register(client.Close))
            {
                await client.ConnectAsync(host, port);
                timeout.Token.ThrowIfCancellationRequested();

                using (NetworkStream stream = client.GetStream())
                using (var responseBuffer = new MemoryStream())
                {
                    await stream.WriteAsync(headers, 0, headers.Length, timeout.Token);
                    await stream.WriteAsync(body, 0, body.Length, timeout.Token);
                    await stream.FlushAsync(timeout.Token);

                    var buffer = new byte[4096];
                    int read;
                    while ((read = await stream.ReadAsync(buffer, 0, buffer.Length, timeout.Token)) > 0)
                    {
                        responseBuffer.Write(buffer, 0, read);
                        if (responseBuffer.Length > MaximumLocalResponseBytes)
                            throw new InvalidDataException("Local CraftQueue response exceeded 1 MiB.");
                    }

                    string response = Encoding.UTF8.GetString(responseBuffer.ToArray());
                    int headerEnd = response.IndexOf("\r\n\r\n", StringComparison.Ordinal);
                    if (headerEnd < 0)
                        throw new InvalidDataException("Local CraftQueue response did not contain HTTP headers.");

                    int statusEnd = response.IndexOf("\r\n", StringComparison.Ordinal);
                    string statusLine = statusEnd >= 0 ? response.Substring(0, statusEnd) : response;
                    string[] statusParts = statusLine.Split(' ');
                    int statusCode;
                    if (statusParts.Length < 2 || !int.TryParse(statusParts[1], out statusCode))
                        throw new InvalidDataException("Local CraftQueue response had no status code.");
                    if (statusCode == 401 || statusCode == 403)
                        throw new UnauthorizedChannelException(statusLine);
                    if (statusCode < 200 || statusCode >= 300)
                        throw new InvalidOperationException("Local CraftQueue request returned " + statusLine + ".");

                    return response.Substring(headerEnd + 4);
                }
            }
        }

        private void openWebInterface()
        {
            ClientChannelInfo info = this.channelInfo;
            if (info == null)
            {
                this.nextChannelInfoRetry = 0f;
                _ = this.ensureChannelInfo();
                this.Logger.LogWarning("CraftQueue: the web link is not available yet; the server has not answered.");
                try
                {
                    NotificationManagerClass.DisplayMessageNotification(
                        "CraftQueue is still connecting to the server - try again in a moment.");
                }
                catch
                {
                }
                return;
            }

            string url = "http://" + getWebLinkHost() + ":" + info.port + "/?token=" + Uri.EscapeDataString(info.token);
            this.Logger.LogInfo("CraftQueue: opening " + url.Substring(0, url.IndexOf("?token=", StringComparison.Ordinal)) + " in the default browser.");
            Application.OpenURL(url);
        }
        private static string getWebLinkHost()
        {
            Uri backendUri;
            if (Uri.TryCreate(RequestHandler.Host, UriKind.Absolute, out backendUri)
                && !string.IsNullOrWhiteSpace(backendUri.Host))
                return backendUri.Host;
            return IPAddress.Loopback.ToString();
        }

        private static List<string> getClientApiHosts()
        {
            var result = new List<string> { IPAddress.Loopback.ToString() };
            Uri backendUri;
            if (Uri.TryCreate(RequestHandler.Host, UriKind.Absolute, out backendUri)
                && !string.IsNullOrWhiteSpace(backendUri.Host)
                && !result.Contains(backendUri.Host, StringComparer.OrdinalIgnoreCase))
                result.Add(backendUri.Host);
            return result;
        }

        private static async Task<string> postSptApi(string path, object payload)
        {
            byte[] request = Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(payload));
            byte[] response = await RequestHandler.HttpClient.PutAsync(path, request);
            return Encoding.UTF8.GetString(response);
        }

        private sealed class UnauthorizedChannelException : Exception
        {
            public UnauthorizedChannelException(string statusLine)
                : base("Local CraftQueue request was rejected: " + statusLine + ".")
            {
            }
        }

        private sealed class ClientChannelInfo
        {
            public int port { get; set; }
            public string token { get; set; }
        }

        private sealed class ClientPollResponse
        {
            public List<ClientCommand> commands { get; set; }
            public List<QueueEntry> queueEntries { get; set; }
            public QueueClaim claim { get; set; }
        }

        private sealed class ClientCommand
        {
            public string commandId { get; set; }
            public string kind { get; set; }
            public string recipeId { get; set; }
            public string instanceId { get; set; }
        }

        private sealed class QueueEntry
        {
            public string queueId { get; set; }
            public string recipeId { get; set; }
            public int areaType { get; set; }
            public string status { get; set; }
        }

        private sealed class QueueClaim
        {
            public string queueId { get; set; }
            public string recipeId { get; set; }
            public int areaType { get; set; }
        }

        private sealed class CachedCommandResult
        {
            public string commandId { get; set; }
            public bool success { get; set; }
            public string errorCode { get; set; }
            public string error { get; set; }
            public float createdAt { get; set; }
        }

        private sealed class PlannedScheme
        {
            public PlannedScheme(int areaType, ProductionBuildAbstractClass scheme, bool isActive)
            {
                this.AreaType = areaType;
                this.Scheme = scheme;
                this.IsActive = isActive;
            }

            public int AreaType { get; private set; }
            public ProductionBuildAbstractClass Scheme { get; private set; }
            public bool IsActive { get; private set; }
        }
    }
}
