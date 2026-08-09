using Microsoft.Extensions.Logging;
using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.DI;
using SPTarkov.Server.Core.Models.Common;
using System;
using System.Collections.Concurrent;
using System.Threading.Tasks;
using System.Threading;

namespace CraftQueue.Server
{
    [Injectable(TypePriority = OnUpdateOrder.HideoutCallbacks + 100)]
    public sealed class RaidQueueExecutor(
        CraftQueueService craftQueueService,
        ILogger<RaidQueueExecutor> logger) : IOnUpdate
    {
        private const int RunIntervalSeconds = 5;
        private const int ErrorLogIntervalSeconds = 60;

        private readonly ConcurrentDictionary<string, long> nextErrorLog = new(StringComparer.Ordinal);

        public Task<bool> OnUpdateAsync(long secondsSinceLastRun, CancellationToken cancellationToken)
        {
            if (secondsSinceLastRun < RunIntervalSeconds)
                return Task.FromResult(false);

            long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            foreach (MongoId sessionId in craftQueueService.GetRaidSessions())
            {
                try
                {
                    _ = craftQueueService.ProcessRaidQueues(sessionId);
                    nextErrorLog.TryRemove(sessionId.ToString(), out _);
                }
                catch (Exception ex)
                {
                    string profileId = sessionId.ToString();
                    long retryAt = nextErrorLog.GetOrAdd(profileId, 0);
                    if (retryAt <= now)
                    {
                        nextErrorLog[profileId] = now + ErrorLogIntervalSeconds;
                        logger.LogError(
                            ex,
                            "[CraftQueue] Server-side raid queue processing failed for profile {ProfileId}; it will retry.",
                            profileId);
                    }
                }
            }

            return Task.FromResult(true);
        }
    }
}
