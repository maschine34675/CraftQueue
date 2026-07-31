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
        public List<string> AvailableRecipeIds { get; set; } = [];
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
