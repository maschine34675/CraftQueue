using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CraftQueue.Server.Models
{
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public enum QueueEntryStatus
    {
        Queued,
        Claimed,
        Starting,
        NeedsReview
    }

    public record QueuedProductionRequest
    {
        [JsonPropertyName("queueId")]
        public string QueueId { get; set; } = Guid.NewGuid().ToString("N");

        [JsonPropertyName("recipeId")]
        public string RecipeId { get; set; } = "";

        [JsonPropertyName("sourceCommandId")]
        public string? SourceCommandId { get; set; }

        [JsonPropertyName("status")]
        public QueueEntryStatus Status { get; set; } = QueueEntryStatus.Queued;

        [JsonPropertyName("leaseUntil")]
        public long? LeaseUntil { get; set; }

        [JsonPropertyName("retryAt")]
        public long? RetryAt { get; set; }

        [JsonPropertyName("attemptCount")]
        public int AttemptCount { get; set; }
        [JsonPropertyName("deferralCount")]
        public int DeferralCount { get; set; }

        [JsonPropertyName("lastError")]
        public string? LastError { get; set; }

        [JsonPropertyName("createdAt")]
        public long CreatedAt { get; set; } = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        [JsonPropertyName("updatedAt")]
        public long UpdatedAt { get; set; } = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        [JsonPropertyName("requestData")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public LegacyStartRequestData? LegacyRequestData { get; set; }

        [JsonPropertyName("paymentItems")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public JsonElement? LegacyPaymentItems { get; set; }

        [JsonPropertyName("claimedUntil")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public long? LegacyClaimedUntil { get; set; }

        public bool NormalizeAfterLoad(HashSet<string> queueIds, long now)
        {
            bool changed = false;

            if (string.IsNullOrWhiteSpace(RecipeId) && !string.IsNullOrWhiteSpace(LegacyRequestData?.RecipeId))
            {
                RecipeId = LegacyRequestData.RecipeId;
                changed = true;
            }

            if (string.IsNullOrWhiteSpace(QueueId) || !queueIds.Add(QueueId))
            {
                QueueId = Guid.NewGuid().ToString("N");
                queueIds.Add(QueueId);
                changed = true;
            }

            if (CreatedAt <= 0)
            {
                CreatedAt = now;
                changed = true;
            }

            if (UpdatedAt <= 0)
            {
                UpdatedAt = CreatedAt;
                changed = true;
            }

            if (LegacyClaimedUntil.HasValue || Status is QueueEntryStatus.Claimed or QueueEntryStatus.Starting)
            {
                Status = QueueEntryStatus.NeedsReview;
                LeaseUntil = null;
                LastError = "The server stopped while this craft was being started. Verify it in game, then retry or remove it.";
                UpdatedAt = now;
                changed = true;
            }

            if (string.IsNullOrWhiteSpace(RecipeId))
            {
                Status = QueueEntryStatus.NeedsReview;
                LastError = "The saved queue entry does not contain a recipe id.";
                UpdatedAt = now;
                changed = true;
            }

            if (LegacyRequestData != null || LegacyPaymentItems.HasValue || LegacyClaimedUntil.HasValue)
            {
                LegacyRequestData = null;
                LegacyPaymentItems = null;
                LegacyClaimedUntil = null;
                changed = true;
            }

            return changed;
        }
    }

    public record LegacyStartRequestData
    {
        [JsonPropertyName("recipeId")]
        public string RecipeId { get; set; } = "";
    }
}
