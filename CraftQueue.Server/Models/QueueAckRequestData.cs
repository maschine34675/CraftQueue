using SPTarkov.Server.Core.Models.Utils;
using System.Text.Json.Serialization;

namespace CraftQueue.Server.Models
{
    public class QueueAckRequestData : IRequestData
    {
        [JsonPropertyName("profileId")]
        public string? ProfileId { get; set; }

        [JsonPropertyName("queueId")]
        public string QueueId { get; set; } = "";

        [JsonPropertyName("success")]
        public bool Success { get; set; }

        [JsonPropertyName("errorCode")]
        public string? ErrorCode { get; set; }

        [JsonPropertyName("error")]
        public string? Error { get; set; }
    }
}
