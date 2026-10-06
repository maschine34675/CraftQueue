using SPTarkov.Server.Core.Models.Utils;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace CraftQueue.Server.Models
{
    public class ClientPollRequestData : IRequestData
    {
        [JsonPropertyName("profileId")]
        public string? ProfileId { get; set; }

        [JsonPropertyName("productions")]
        public List<ActiveProductionTelemetry> Productions { get; set; } = [];
        [JsonPropertyName("availableRecipeIds")]
        public List<string>? AvailableRecipeIds { get; set; }

        [JsonPropertyName("recipeIdsHash")]
        public string? RecipeIdsHash { get; set; }
        [JsonPropertyName("areaSlots")]
        public List<AreaSlotTelemetry>? AreaSlots { get; set; }
    }

    public class AreaSlotTelemetry
    {
        [JsonPropertyName("areaType")]
        public int AreaType { get; set; }
        [JsonPropertyName("capacity")]
        public int Capacity { get; set; } = 1;
        [JsonPropertyName("occupied")]
        public int Occupied { get; set; }
        [JsonPropertyName("canStart")]
        public bool CanStart { get; set; } = true;
        [JsonPropertyName("completedRecipeIds")]
        public List<string> CompletedRecipeIds { get; set; } = [];
    }

    public class ActiveProductionTelemetry
    {
        [JsonPropertyName("recipeId")]
        public string RecipeId { get; set; } = "";

        [JsonPropertyName("areaType")]
        public int AreaType { get; set; }

        [JsonPropertyName("timeLeft")]
        public int TimeLeft { get; set; }

        [JsonPropertyName("productionTime")]
        public int ProductionTime { get; set; }
        [JsonPropertyName("progressRate")]
        public double ProgressRate { get; set; } = 1;

        [JsonPropertyName("instanceId")]
        public string InstanceId { get; set; } = "";
    }
}
