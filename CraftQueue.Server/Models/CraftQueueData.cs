using SPTarkov.Server.Core.Models.Enums.Hideout;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Serialization;

namespace CraftQueue.Server.Models
{
    public record CraftQueueData
    {
        [JsonPropertyName("areaQueues")]
        public Dictionary<HideoutAreas, LinkedList<QueuedProductionRequest>> AreaQueues { get; set; } = [];

        public bool NormalizeAfterLoad()
        {
            bool changed = false;
            long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            var queueIds = new HashSet<string>(StringComparer.Ordinal);

            AreaQueues ??= [];
            foreach (HideoutAreas area in AreaQueues.Keys.ToArray())
            {
                LinkedList<QueuedProductionRequest>? queue = AreaQueues[area];
                if (queue == null)
                {
                    AreaQueues[area] = [];
                    changed = true;
                    continue;
                }

                foreach (QueuedProductionRequest entry in queue)
                    changed |= entry.NormalizeAfterLoad(queueIds, now);
            }

            return changed;
        }
    }
}
