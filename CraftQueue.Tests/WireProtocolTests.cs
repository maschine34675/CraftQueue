using CraftQueue.Server.Models;
using SPTarkov.Server.Core.Utils;
using System.Text.Json;
using Xunit;

namespace CraftQueue.Tests;

public sealed class WireProtocolTests
{
    [Fact]
    public void SptFallbackSerializesClientWireValuesAsStrings()
    {
        var response = new ClientPollResponse
        {
            Commands =
            [
                new ClientCommandDelivery
                {
                    CommandId = "command",
                    Kind = ClientCommandKind.Enqueue.ToString(),
                    RecipeId = "recipe"
                }
            ],
            QueueEntries =
            [
                new QueueEntryView
                {
                    QueueId = "queue",
                    RecipeId = "recipe",
                    Status = QueueEntryStatus.Queued.ToString()
                }
            ]
        };

        string json = new JsonUtil([]).Serialize(response)!;
        using JsonDocument document = JsonDocument.Parse(json);

        Assert.Equal(
            JsonValueKind.String,
            document.RootElement.GetProperty("commands")[0].GetProperty("kind").ValueKind);
        Assert.Equal(
            JsonValueKind.String,
            document.RootElement.GetProperty("queueEntries")[0].GetProperty("status").ValueKind);
    }

    [Fact]
    public void TelemetryWithoutAProgressRateStillCountsDownAtFullSpeed()
    {
        const string json = """
        { "recipeId": "655b56eb32b0b1645e6f54c8", "areaType": 10, "timeLeft": 60, "productionTime": 120, "instanceId": "x:1" }
        """;

        ActiveProductionTelemetry? telemetry = JsonSerializer.Deserialize<ActiveProductionTelemetry>(
            json,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

        Assert.NotNull(telemetry);
        Assert.Equal(1, telemetry.ProgressRate);
    }

    [Fact]
    public void RaidHandledQueueResultIsExplicitOnTheWire()
    {
        QueueOperationResult result = QueueOperationResult.Ok("command", handledByServer: true);

        string json = new JsonUtil([]).Serialize(result)!;
        using JsonDocument document = JsonDocument.Parse(json);

        Assert.True(document.RootElement.GetProperty("handledByServer").GetBoolean());
        Assert.Equal("command", document.RootElement.GetProperty("commandId").GetString());
    }
}
