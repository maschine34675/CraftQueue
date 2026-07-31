using System.Text.Json.Serialization;

namespace CraftQueue.Server
{
    public sealed class WebServerConfig
    {
        [JsonPropertyName("allowLan")]
        public bool AllowLan { get; set; } = true;

        [JsonPropertyName("bindAddress")]
        public string BindAddress { get; set; } = "0.0.0.0";
    }
}
