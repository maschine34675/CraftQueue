using SPTarkov.Server.Core.Models.Utils;
using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace CraftQueue.Server.Models
{
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public enum ClientCommandKind
    {
        Enqueue,
        CancelActive
    }

    [JsonConverter(typeof(JsonStringEnumConverter))]
    public enum ClientCommandStatus
    {
        Pending,
        Delivered,
        Completed
    }

    public sealed record ClientCommand
    {
        [JsonPropertyName("commandId")]
        public string CommandId { get; init; } = Guid.NewGuid().ToString("N");

        [JsonPropertyName("kind")]
        public ClientCommandKind Kind { get; init; }

        [JsonPropertyName("recipeId")]
        public string RecipeId { get; init; } = "";

        [JsonPropertyName("instanceId")]
        public string? InstanceId { get; init; }

        [JsonPropertyName("status")]
        public ClientCommandStatus Status { get; set; } = ClientCommandStatus.Pending;

        [JsonPropertyName("createdAt")]
        public long CreatedAt { get; init; }

        [JsonPropertyName("expiresAt")]
        public long ExpiresAt { get; init; }

        [JsonPropertyName("nextDeliveryAt")]
        public long NextDeliveryAt { get; set; }

        [JsonPropertyName("deliveryCount")]
        public int DeliveryCount { get; set; }

        [JsonPropertyName("success")]
        public bool? Success { get; set; }

        [JsonPropertyName("error")]
        public string? Error { get; set; }

        [JsonPropertyName("completedAt")]
        public long? CompletedAt { get; set; }
    }

    public sealed class ClientCommandResultRequestData : IRequestData
    {
        [JsonPropertyName("profileId")]
        public string? ProfileId { get; set; }

        [JsonPropertyName("commandId")]
        public string CommandId { get; set; } = "";

        [JsonPropertyName("success")]
        public bool Success { get; set; }

        [JsonPropertyName("errorCode")]
        public string? ErrorCode { get; set; }

        [JsonPropertyName("error")]
        public string? Error { get; set; }
    }

    public sealed class QueueClaim
    {
        [JsonPropertyName("queueId")]
        public string QueueId { get; set; } = "";

        [JsonPropertyName("recipeId")]
        public string RecipeId { get; set; } = "";

        [JsonPropertyName("areaType")]
        public int AreaType { get; set; }
    }

    public sealed class ClientPollResponse
    {
        [JsonPropertyName("commands")]
        public List<ClientCommandDelivery> Commands { get; set; } = [];

        [JsonPropertyName("queueEntries")]
        public List<QueueEntryView> QueueEntries { get; set; } = [];

        [JsonPropertyName("claim")]
        public QueueClaim? Claim { get; set; }
    }

    public sealed class ClientCommandDelivery
    {
        [JsonPropertyName("commandId")]
        public string CommandId { get; set; } = "";
        [JsonPropertyName("kind")]
        public string Kind { get; set; } = "";

        [JsonPropertyName("recipeId")]
        public string RecipeId { get; set; } = "";

        [JsonPropertyName("instanceId")]
        public string? InstanceId { get; set; }
    }

    public sealed class ClientChannelInfo
    {
        [JsonPropertyName("port")]
        public int Port { get; set; }

        [JsonPropertyName("token")]
        public string Token { get; set; } = "";
    }

    public sealed class QueueEntryView
    {
        [JsonPropertyName("queueId")]
        public string QueueId { get; set; } = "";

        [JsonPropertyName("recipeId")]
        public string RecipeId { get; set; } = "";

        [JsonPropertyName("areaType")]
        public int AreaType { get; set; }

        [JsonPropertyName("status")]
        public string Status { get; set; } = "";

        [JsonPropertyName("lastError")]
        public string? LastError { get; set; }

        [JsonPropertyName("retryAt")]
        public long? RetryAt { get; set; }

        [JsonPropertyName("attemptCount")]
        public int AttemptCount { get; set; }

        [JsonPropertyName("createdAt")]
        public long CreatedAt { get; set; }
    }

    public sealed class QueueOperationResult
    {
        [JsonPropertyName("success")]
        public bool Success { get; set; }

        [JsonPropertyName("error")]
        public string? Error { get; set; }

        [JsonPropertyName("commandId")]
        public string? CommandId { get; set; }

        [JsonPropertyName("handledByServer")]
        public bool HandledByServer { get; set; }

        public static QueueOperationResult Ok(string? commandId = null, bool handledByServer = false) =>
            new() { Success = true, CommandId = commandId, HandledByServer = handledByServer };

        public static QueueOperationResult Fail(string error) => new() { Success = false, Error = error };
    }
}
