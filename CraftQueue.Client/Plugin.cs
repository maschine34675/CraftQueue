using BepInEx;
using BepInEx.Configuration;
using Comfort.Common;
using CraftQueue.Shared;
using EFT.Communications;
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
using JsonType;

namespace CraftQueue
{
    [BepInPlugin(PluginGuid, PluginName, PluginVersion)]
    [BepInDependency(CraftQueue.UI.WebOverlayGate.LibraryGuid, BepInDependency.DependencyFlags.SoftDependency)]
    public class Plugin : BaseUnityPlugin
    {
        public const string PluginGuid = CraftQueueProtocol.ModGuid;
        public const string PluginName = CraftQueueProtocol.PluginName;
        public const string PluginVersion = CraftQueueProtocol.Version;

        private const float PollInterval = 2f;
        private const float IdlePollInterval = 8f;
        private const float IdleAfterSeconds = 30f;
        private const float WebRetryInterval = 30f;
        private const float CollectRetryInterval = 60f;
        private const int ClientApiTimeoutMs = 15000;
        private const int ConnectTimeoutMs = 3000;
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
        private bool overlayHintLogged;
        private float lastActivityAt;
        private string lastSentRecipeIdsHash;
        private bool resendRecipeIds = true;
        private bool serverSpeaksRecipeHash;
        private float currentPollInterval()
        {
            return Time.time - this.lastActivityAt <= IdleAfterSeconds ? PollInterval : IdlePollInterval;
        }

        private ConfigEntry<KeyboardShortcut> openWebInterfaceKey;
        private ConfigEntry<KeyboardShortcut> openInBrowserKey;
        private ConfigEntry<bool> useOverlay;
        private ConfigEntry<bool> showOverlayFrame;
        private ConfigEntry<bool> showTaskBarButton;

        internal static Plugin Instance { get; private set; }

        internal bool ShowTaskBarButton => this.showTaskBarButton.Value;
        internal void OpenWebInterfaceFromTaskBar()
        {
            this.openWebInterface(false);
        }

        internal void LogTaskBarButtonFailure(Exception ex)
        {
            this.Logger.LogWarning("the task bar button could not be added ("
                + ex.GetType().Name + ": " + ex.Message + ").");
        }

        internal void LogTaskBarButtonDiagnostic(string message)
        {
            this.Logger.LogDebug("task bar button: " + message);
        }
        internal void RunDelayed(float seconds, Action action)
        {
            this.StartCoroutine(runDelayed(seconds, action));
        }

        private static System.Collections.IEnumerator runDelayed(float seconds, Action action)
        {
            yield return new WaitForSeconds(seconds);
            try
            {
                action();
            }
            catch (Exception ex)
            {
                CraftQueue.Plugin.Instance?.Logger.LogWarning("a delayed UI action failed ("
                    + ex.GetType().Name + ": " + ex.Message + ").");
            }
        }

        private void Awake()
        {
            Instance = this;
            this.clientApiHosts = getClientApiHosts();
            this.openWebInterfaceKey = this.Config.Bind(
                "Web Interface",
                "Open in browser",
                new KeyboardShortcut(KeyCode.F9),
                "Opens your personal CraftQueue web interface.");
            this.openInBrowserKey = this.Config.Bind(
                "Web Interface",
                "Open in external browser",
                new KeyboardShortcut(KeyCode.F9, KeyCode.LeftShift),
                "Opens the web interface in the external browser even when the overlay is enabled.");
            this.useOverlay = this.Config.Bind(
                "Web Interface",
                "Show as in-game overlay",
                true,
                "Show the web interface in a window over the game instead of the external browser. Needs the WebView2 runtime, which current Windows installs ship with; without it the browser is used.");
            this.showOverlayFrame = this.Config.Bind(
                "Web Interface",
                "Show window title bar",
                true,
                "Draw a title bar on the overlay window. Without it the window cannot be moved or resized and closes only via F9 or Escape. Takes effect when the overlay is next created (usually the next game start).");
            this.showTaskBarButton = this.Config.Bind(
                "Web Interface",
                "Show menu bar button",
                true,
                "Add a CRAFT QUEUE button to the bottom menu bar, next to HIDEOUT. Takes effect when the menu is next rebuilt (usually the next game start).");

            try
            {
                new CraftQueue.Patches.TaskBarButtonPatch().Enable();
            }
            catch (Exception ex)
            {
                this.Logger.LogWarning("the task bar button patch could not be enabled ("
                    + ex.GetType().Name + "). The hotkey still works.");
            }
        }

        private void Update()
        {
            if (this.openInBrowserKey.Value.IsDown())
                this.openWebInterface(true);
            else if (this.openWebInterfaceKey.Value.IsDown())
                this.openWebInterface(false);

            if (this.processing || Time.time < this.nextPoll)
                return;

            this.nextPoll = Time.time + currentPollInterval();
            HideoutRepresentation hideout;
            if (!tryGetExecutorHideout(out hideout))
                return;

            _ = this.process();
        }

        private async Task process()
        {
            this.processing = true;
            try
            {
                HideoutRepresentation hideout;
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
                    this.Logger.LogError("executor: " + ex);
                }
            }
            finally
            {
                this.processing = false;
            }
        }

        private static bool tryGetExecutorHideout(out HideoutRepresentation hideout)
        {
            hideout = null;
#pragma warning disable CS0618
            if (InGameStatus.InRaid)
                return false;
#pragma warning restore CS0618

            HideoutRepresentation candidate = Singleton<HideoutRepresentation>.Instance;
            if (candidate == null
                || candidate.ProductionController == null
                || candidate.ProductionController._areaProducers == null
                || candidate._session == null
                || candidate._session.Profile == null
                || candidate._session.Profile.Inventory == null
                || candidate._session.Profile.Inventory.Stash == null)
                return false;
            if (candidate._inventoryController == null
                || candidate._inventoryController.Inventory == null
                || candidate._inventoryController.Inventory.Stash == null
                || !ReferenceEquals(candidate._inventoryController.Inventory, candidate._session.Profile.Inventory))
                return false;

            hideout = candidate;
            return true;
        }

        private static bool isSameExecutor(HideoutRepresentation expectedHideout, object expectedController)
        {
            HideoutRepresentation current;
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
                this.Logger.LogWarning("could not release the claim for " + claim.recipeId + " (" + ex.Message + ").");
            }
        }

        private async Task<ClientPollResponse> poll(HideoutRepresentation hideout)
        {
            List<string> availableRecipeIds = buildAvailableRecipeIds(hideout);
            string recipeIdsHash = computeRecipeIdsHash(availableRecipeIds);
            bool sendList = !this.serverSpeaksRecipeHash
                || this.resendRecipeIds
                || recipeIdsHash != this.lastSentRecipeIdsHash;

            var payload = new
            {
                profileId = RequestHandler.SessionId,
                productions = buildTelemetry(hideout),
                availableRecipeIds = sendList ? availableRecipeIds : null,
                recipeIdsHash
            };

            ClientPollResponse parsed;
            try
            {
                string response = await this.postWithFallback(
                    "/api/client/poll",
                    "/craftqueue/Poll",
                    payload);
                parsed = JsonConvert.DeserializeObject<ClientPollResponse>(response);
            }
            catch
            {
                this.resendRecipeIds = true;
                throw;
            }

            if (parsed == null)
            {
                this.resendRecipeIds = true;
                return null;
            }

            if (parsed.needRecipeIds.HasValue)
                this.serverSpeaksRecipeHash = true;
            if (sendList)
            {
                this.lastSentRecipeIdsHash = recipeIdsHash;
                this.resendRecipeIds = false;
            }
            if (parsed.needRecipeIds == true)
            {
                this.resendRecipeIds = true;
                this.nextPoll = 0f;
            }
            if (parsed.claim != null
                || (parsed.commands != null && parsed.commands.Count > 0)
                || (parsed.queueEntries != null && parsed.queueEntries.Count > 0))
                this.lastActivityAt = Time.time;

            return parsed;
        }

        private static string computeRecipeIdsHash(List<string> recipeIds)
        {
            unchecked
            {
                ulong hash = 14695981039346656037UL;
                foreach (string recipeId in recipeIds.OrderBy(id => id, StringComparer.Ordinal))
                {
                    foreach (char character in recipeId)
                    {
                        hash ^= character;
                        hash *= 1099511628211UL;
                    }
                    hash ^= '\n';
                    hash *= 1099511628211UL;
                }
                return hash.ToString("x16");
            }
        }

        private async Task startClaimedCraft(HideoutRepresentation hideout, QueueClaim claim)
        {
            ItemsProducerBase producer;
            BaseHideoutScheme scheme;
            if (!tryFindScheme(hideout, claim.recipeId, out producer, out scheme))
            {
                await this.ack(claim.queueId, false, "recipe_unavailable", "Recipe is not available in the local hideout.");
                return;
            }

            if (producer.ProducingItems.Count > 0
                || producer._errorItems.Count > 0
                || producer.HasCompletedItems
                || !producer.CanStart
                || !producer.CanStartByProductionCount())
            {
                await this.ack(claim.queueId, false, "area_busy", "The hideout area is still occupied.");
                return;
            }

            if (!producer.CanStartScheme((ProductionScheme)scheme))
            {
                await this.ack(claim.queueId, false, "generator_unavailable", "The generator requirements are not currently met.");
                return;
            }

            List<ItemToHandover> items;
            List<ItemToHandover> tools;
            string selectionError;
            if (!trySelectItems(hideout, scheme, out items, out tools, out selectionError))
            {
                await this.ack(claim.queueId, false, "requirements_unavailable", selectionError);
                return;
            }

            try
            {
                hideout._session.StartSingleProduction(scheme._id, items, tools);
                producer.StartProducing(scheme);
            }
            catch (Exception ex)
            {
                await this.ack(claim.queueId, false, "start_exception", "The game rejected the start: " + ex.Message);
                return;
            }

            bool accepted = await this.ack(claim.queueId, true, null, null);
            if (!accepted)
                this.Logger.LogWarning("the server did not accept the start acknowledgement for " + claim.recipeId + ".");
        }

        private async Task processCommands(
            HideoutRepresentation hideout,
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
                    ItemsProducerBase producer;
                    BaseHideoutScheme scheme;
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
            HideoutRepresentation hideout,
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
                    ItemsProducerBase producer;
                    BaseHideoutScheme scheme;
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
                    ItemsProducerBase producer;
                    BaseHideoutScheme scheme;
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
                            this.Logger.LogDebug("local web channel restored.");
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
                        this.Logger.LogDebug("local web channel token expired; requesting a new one.");
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
                        "local web channel unavailable (" + lastError.GetType().Name + ": " + lastError.Message + "); using the SPT backend fallback.");
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
                this.Logger.LogDebug(
                    "local web channel targets " + string.Join(", ", this.clientApiHosts.ToArray()) + " on port " + info.port + ".");
                this.Logger.LogInfo(
                    "your personal web interface: http://" + getWebLinkHost() + ":" + info.port + "/?token=" + Uri.EscapeDataString(info.token));
            }
            catch (Exception ex)
            {
                this.nextChannelInfoRetry = Time.realtimeSinceStartup + WebRetryInterval;
                if (Time.realtimeSinceStartup >= this.nextErrorLog)
                {
                    this.nextErrorLog = Time.realtimeSinceStartup + 30f;
                    this.Logger.LogWarning("could not obtain local channel information; backend fallback remains active (" + ex.Message + ").");
                }
            }
        }

        private async Task collectCompletedProductions(HideoutRepresentation hideout, object executor)
        {
            var productionGroups = hideout.ProductionController._areaProducers;
            if (productionGroups == null)
                return;

            foreach (List<ItemsProducerBase> producers in productionGroups.Values.ToList())
            {
                if (producers == null)
                    continue;

                foreach (ItemsProducerBase producer in producers.ToList())
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

                        BaseHideoutScheme scheme;
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
                        this.Logger.LogWarning("collecting a completed production will retry in 60 seconds (" + ex.Message + ").");
                    }
                }
            }
        }

        private List<object> buildTelemetry(HideoutRepresentation hideout)
        {
            var productions = new List<object>();
            var seenRuns = new HashSet<string>(StringComparer.Ordinal);
            if (hideout == null
                || hideout.ProductionController == null
                || hideout.ProductionController._areaProducers == null)
                return productions;
            Dictionary<string, ProductionData> snapshot = getProfileProductionSnapshot(hideout);

            foreach (List<ItemsProducerBase> producers in hideout.ProductionController._areaProducers.Values)
            {
                if (producers == null)
                    continue;
                foreach (ItemsProducerBase producer in producers)
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
            ItemsProducerBase producer,
            Dictionary<string, ProductionData> snapshot)
        {
            foreach (KeyValuePair<string, ProducingItemController> pair in producer.ProducingItems)
            {
                ProducingItemController live = pair.Value;
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
            ItemsProducerBase producer,
            Dictionary<string, ProductionData> snapshot)
        {
            foreach (KeyValuePair<string, ProducingItemController> pair in producer._errorItems)
            {
                if (pair.Value == null || !isQueueableScheme(producer, pair.Key))
                    continue;
                ProductionData snapshotEntry = lookupSnapshot(snapshot, pair.Key);
                int total = snapshotEntry == null ? 0 : Math.Max(0, snapshotEntry.ProductionTime);
                int left = snapshotEntry == null ? 0 : Math.Max(0, (int)Math.Ceiling(total - snapshotEntry.Progress));
                productions.Add(this.describeProduction(seenRuns, producer, pair.Key, left, total, 0.0));
            }
        }

        private object describeProduction(
            HashSet<string> seenRuns,
            ItemsProducerBase producer,
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

        private static bool isQueueableScheme(ItemsProducerBase producer, string recipeId)
        {
            BaseHideoutScheme scheme;
            return producer.Schemes.TryGetValue(recipeId, out scheme)
                && scheme is ProductionScheme
                && !scheme.continuous;
        }

        private static ProductionData lookupSnapshot(
            Dictionary<string, ProductionData> snapshot,
            string recipeId)
        {
            ProductionData entry = null;
            if (snapshot != null)
                snapshot.TryGetValue(recipeId, out entry);
            return entry;
        }

        private static int snapshotTotal(
            Dictionary<string, ProductionData> snapshot,
            string recipeId,
            int liveTotal)
        {
            ProductionData entry = lookupSnapshot(snapshot, recipeId);
            return entry == null ? liveTotal : Math.Max(liveTotal, Math.Max(0, entry.ProductionTime));
        }

        private static Dictionary<string, ProductionData> getProfileProductionSnapshot(HideoutRepresentation hideout)
        {
            if (hideout == null
                || hideout._session == null
                || hideout._session.Profile == null
                || hideout._session.Profile.Hideout == null)
                return null;
            return hideout._session.Profile.Hideout.Production;
        }

        private static List<string> buildAvailableRecipeIds(HideoutRepresentation hideout)
        {
            var recipeIds = new HashSet<string>(StringComparer.Ordinal);
            if (hideout == null || hideout.ProductionController == null || hideout.ProductionController._areaProducers == null)
                return recipeIds.ToList();

            foreach (List<ItemsProducerBase> producers in hideout.ProductionController._areaProducers.Values)
            {
                if (producers == null)
                    continue;
                foreach (ItemsProducerBase producer in producers)
                {
                    if (producer == null || producer.Schemes == null)
                        continue;
                    foreach (BaseHideoutScheme scheme in producer.Schemes.Values)
                    {
                        if (scheme is ProductionScheme && !scheme.continuous && !scheme.locked)
                            recipeIds.Add(scheme._id);
                    }
                }
            }

            return recipeIds.ToList();
        }

        private bool isCurrentProduction(HideoutRepresentation hideout, string recipeId, string instanceId)
        {
            ItemsProducerBase producer;
            BaseHideoutScheme scheme;
            if (!tryFindScheme(hideout, recipeId, out producer, out scheme))
                return false;
            if (!producer.ProducingItems.ContainsKey(recipeId) && !producer._errorItems.ContainsKey(recipeId))
                return false;
            string runId;
            return this.runIds.TryGetValue((int)producer.AreaType + ":" + recipeId, out runId)
                && string.Equals(recipeId + ":" + runId, instanceId, StringComparison.Ordinal);
        }

        private static bool validateProjectedRequirements(
            HideoutRepresentation hideout,
            IList<QueueEntry> queueEntries,
            ItemsProducerBase candidateProducer,
            BaseHideoutScheme candidateScheme,
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

                ItemsProducerBase producer;
                BaseHideoutScheme scheme;
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

        private static List<PlannedScheme> getActivePlans(HideoutRepresentation hideout)
        {
            var plans = new List<PlannedScheme>();
            if (hideout == null
                || hideout.ProductionController == null
                || hideout.ProductionController._areaProducers == null)
                return plans;

            foreach (List<ItemsProducerBase> producers in hideout.ProductionController._areaProducers.Values)
            {
                if (producers == null)
                    continue;
                foreach (ItemsProducerBase producer in producers)
                {
                    if (producer == null || producer.Schemes == null)
                        continue;
                    foreach (string recipeId in producer.ProducingItems.Keys.Concat(producer._errorItems.Keys))
                    {
                        BaseHideoutScheme scheme;
                        if (isQueueableScheme(producer, recipeId) && producer.Schemes.TryGetValue(recipeId, out scheme))
                            plans.Add(new PlannedScheme((int)producer.AreaType, scheme, true));
                    }
                }
            }

            return plans;
        }

        private static bool tryFindScheme(
            HideoutRepresentation hideout,
            string recipeId,
            out ItemsProducerBase producer,
            out BaseHideoutScheme scheme)
        {
            if (hideout != null
                && hideout.ProductionController != null
                && hideout.ProductionController._areaProducers != null)
            {
                foreach (List<ItemsProducerBase> producers in hideout.ProductionController._areaProducers.Values)
                {
                    if (producers == null)
                        continue;
                    foreach (ItemsProducerBase candidate in producers)
                    {
                        BaseHideoutScheme found;
                        if (candidate != null
                            && candidate.Schemes != null
                            && candidate.Schemes.TryGetValue(recipeId, out found)
                            && found is ProductionScheme
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
            HideoutRepresentation hideout,
            BaseHideoutScheme scheme,
            out List<ItemToHandover> items,
            out List<ItemToHandover> tools,
            out string error)
        {
            items = new List<ItemToHandover>();
            tools = new List<ItemToHandover>();
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

                    var selected = new ItemToHandover(item, take);
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
                using (var connectTimeout = new CancellationTokenSource(ConnectTimeoutMs))
                using (connectTimeout.Token.Register(client.Close))
                {
                    await client.ConnectAsync(host, port);
                    connectTimeout.Token.ThrowIfCancellationRequested();
                }
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

        private void openWebInterface(bool forceBrowser)
        {
            this.lastActivityAt = Time.time;
            ClientChannelInfo info = this.channelInfo;
            if (info == null)
            {
                this.nextChannelInfoRetry = 0f;
                _ = this.ensureChannelInfo();
                this.Logger.LogWarning("the web link is not available yet; the server has not answered.");
                try
                {
                    NotificationManager.DisplayMessageNotification(
                        "CraftQueue is still connecting to the server - try again in a moment.");
                }
                catch
                {
                }
                return;
            }

            string url = "http://" + getWebLinkHost() + ":" + info.port + "/?token=" + Uri.EscapeDataString(info.token);
            string safeUrl = url.Substring(0, url.IndexOf("?token=", StringComparison.Ordinal));

            if (!forceBrowser && this.tryToggleOverlay(url))
            {
                this.Logger.LogDebug("toggled the in-game overlay for " + safeUrl + ".");
                return;
            }

            this.Logger.LogDebug("opening " + safeUrl + " in the default browser.");
            Application.OpenURL(url);
        }
        private bool tryToggleOverlay(string url)
        {
            if (!this.useOverlay.Value)
                return false;

            if (Screen.fullScreenMode == FullScreenMode.ExclusiveFullScreen)
            {
                this.Logger.LogInfo("exclusive fullscreen cannot host the overlay; using the browser.");
                return false;
            }

            if (!CraftQueue.UI.WebOverlayGate.IsLoaded)
            {
                if (!this.overlayHintLogged)
                {
                    this.overlayHintLogged = true;
                    this.Logger.LogInfo("install the Anvil-WebOverlay library for the in-game overlay; using the browser.");
                }
                return false;
            }

            bool toggled = CraftQueue.UI.WebOverlayGate.Toggle(
                url, this.showOverlayFrame.Value, closeKeysForOverlay(), this.Logger.LogWarning, out bool starting);
            if (starting)
            {
                try
                {
                    NotificationManager.DisplayMessageNotification(
                        "the overlay is starting - one moment...");
                }
                catch
                {
                }
            }

            return toggled;
        }
        private int[] closeKeysForOverlay()
        {
            const int Escape = 0x1B;
            int mapped = toVirtualKey(this.openWebInterfaceKey.Value.MainKey);
            return mapped == 0 || mapped == Escape ? new[] { Escape } : new[] { Escape, mapped };
        }

        private static int toVirtualKey(KeyCode key)
        {
            if (key >= KeyCode.F1 && key <= KeyCode.F15)
                return 0x70 + (key - KeyCode.F1);
            if (key >= KeyCode.A && key <= KeyCode.Z)
                return 'A' + (key - KeyCode.A);
            if (key >= KeyCode.Alpha0 && key <= KeyCode.Alpha9)
                return '0' + (key - KeyCode.Alpha0);
            switch (key)
            {
                case KeyCode.Escape: return 0x1B;
                case KeyCode.Tab: return 0x09;
                case KeyCode.Home: return 0x24;
                case KeyCode.End: return 0x23;
                case KeyCode.Insert: return 0x2D;
                case KeyCode.Delete: return 0x2E;
                case KeyCode.PageUp: return 0x21;
                case KeyCode.PageDown: return 0x22;
                case KeyCode.Pause: return 0x13;
                default: return 0;
            }
        }

        private void OnDestroy()
        {
            if (CraftQueue.UI.WebOverlayGate.IsLoaded)
                CraftQueue.UI.WebOverlayGate.Shutdown();
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
            public bool? needRecipeIds { get; set; }
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
            public PlannedScheme(int areaType, BaseHideoutScheme scheme, bool isActive)
            {
                this.AreaType = areaType;
                this.Scheme = scheme;
                this.IsActive = isActive;
            }

            public int AreaType { get; private set; }
            public BaseHideoutScheme Scheme { get; private set; }
            public bool IsActive { get; private set; }
        }
    }
}
