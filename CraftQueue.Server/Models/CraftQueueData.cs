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
        [JsonPropertyName("pendingStartConfirmations")]
        public List<PendingStartConfirmation> PendingStartConfirmations { get; set; } = [];

        public bool NormalizeAfterLoad()
        {
            bool changed = false;
            long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            var queueIds = new HashSet<string>(StringComparer.Ordinal);

            AreaQueues ??= [];
            PendingStartConfirmations ??= [];
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

    public record PendingStartConfirmation
    {
        [JsonPropertyName("queueId")]
        public string QueueId { get; set; } = "";

        [JsonPropertyName("recipeId")]
        public string RecipeId { get; set; } = "";

        [JsonPropertyName("areaType")]
        public int AreaType { get; set; }
        [JsonPropertyName("startTimestamp")]
        public long StartTimestamp { get; set; }
        [JsonPropertyName("sequence")]
        public long Sequence { get; set; }
        [JsonPropertyName("confirmedAt")]
        public long ConfirmedAt { get; set; }
    }
}
