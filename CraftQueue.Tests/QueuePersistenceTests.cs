using CraftQueue.Server;
using CraftQueue.Server.Models;
using CraftQueue.Server.Stores;
using CraftQueue.Shared;
using Microsoft.Extensions.Logging.Abstractions;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Enums.Hideout;
using System.Text.Json;
using System.Text.Json.Serialization;
using Xunit;

namespace CraftQueue.Tests;

public sealed class QueuePersistenceTests
{
    private static readonly JsonSerializerOptions jsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() }
    };

    [Fact]
    public void LegacyQueueJsonIsLoadedAndMigrated()
    {
        const string json = """
        {
          "areaQueues": {
            "Workbench": [
              {
                "queueId": "legacy-entry",
                "requestData": { "recipeId": "655b56eb32b0b1645e6f54c8" },
                "paymentItems": [],
                "claimedUntil": null
              }
            ]
          }
        }
        """;

        CraftQueueData? data = JsonSerializer.Deserialize<CraftQueueData>(json, jsonOptions);

        Assert.NotNull(data);
        Assert.True(data.NormalizeAfterLoad());
        QueuedProductionRequest entry = Assert.Single(data.AreaQueues[HideoutAreas.Workbench]);
        Assert.Equal("655b56eb32b0b1645e6f54c8", entry.RecipeId);
        Assert.Equal(QueueEntryStatus.Queued, entry.Status);
        Assert.Null(entry.LegacyRequestData);
        Assert.Null(entry.LegacyPaymentItems);

        string migrated = JsonSerializer.Serialize(data, jsonOptions);
        Assert.DoesNotContain("\"requestData\"", migrated);
        Assert.DoesNotContain("\"paymentItems\"", migrated);
        Assert.DoesNotContain("\"claimedUntil\"", migrated);
    }

    [Fact]
    public void AmbiguousStartBecomesNeedsReviewAfterRestart()
    {
        var data = new CraftQueueData
        {
            AreaQueues = new()
            {
                [HideoutAreas.Workbench] = new LinkedList<QueuedProductionRequest>(
                [
                    new QueuedProductionRequest
                    {
                        QueueId = "starting-entry",
                        RecipeId = "655b56eb32b0b1645e6f54c8",
                        Status = QueueEntryStatus.Starting,
                        LeaseUntil = DateTimeOffset.UtcNow.AddMinutes(1).ToUnixTimeSeconds()
                    }
                ])
            }
        };

        Assert.True(data.NormalizeAfterLoad());
        QueuedProductionRequest entry = Assert.Single(data.AreaQueues[HideoutAreas.Workbench]);
        Assert.Equal(QueueEntryStatus.NeedsReview, entry.Status);
        Assert.Null(entry.LeaseUntil);
        Assert.Contains("Verify it in game", entry.LastError);
    }

    [Fact]
    public void DuplicateQueueIdsAreRegenerated()
    {
        var first = new QueuedProductionRequest { QueueId = "duplicate", RecipeId = "recipe-a" };
        var second = new QueuedProductionRequest { QueueId = "duplicate", RecipeId = "recipe-b" };
        var data = new CraftQueueData
        {
            AreaQueues = new()
            {
                [HideoutAreas.Workbench] = new LinkedList<QueuedProductionRequest>([first, second])
            }
        };

        Assert.True(data.NormalizeAfterLoad());
        Assert.NotEqual(first.QueueId, second.QueueId);
    }

    [Fact]
    public void CorruptPrimaryFileRecoversFromAtomicBackup()
    {
        string originalDirectory = Directory.GetCurrentDirectory();
        string testDirectory = Path.Combine(Path.GetTempPath(), "CraftQueue.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(testDirectory);

        try
        {
            Directory.SetCurrentDirectory(testDirectory);
            MongoId profileId = "0123456789abcdef01234567";
            var store = new JsonStore<CraftQueueData>(NullLogger<JsonStore<CraftQueueData>>.Instance);

            store.Save(createData("first"), profileId, "craftqueue.json");
            store.Save(createData("second"), profileId, "craftqueue.json");

            string queuePath = Path.Combine(testDirectory, "user", "profileData", profileId.ToString(), "craftqueue.json");
            File.WriteAllText(queuePath, "{ definitely not valid json");

            CraftQueueData? recovered = store.Load(profileId, "craftqueue.json");

            Assert.NotNull(recovered);
            Assert.Equal("first", Assert.Single(recovered.AreaQueues[HideoutAreas.Workbench]).RecipeId);
            Assert.NotNull(JsonSerializer.Deserialize<CraftQueueData>(File.ReadAllText(queuePath), jsonOptions));
            Assert.Single(Directory.GetFiles(Path.GetDirectoryName(queuePath)!, "craftqueue.json.corrupt-*"));
        }
        finally
        {
            Directory.SetCurrentDirectory(originalDirectory);
            Directory.Delete(testDirectory, true);
        }
    }

    [Fact]
    public void StartLedgerSurvivesARoundTripAndOlderFilesLoadWithoutIt()
    {
        var data = createData("recipe");
        data.PendingStartConfirmations.Add(new PendingStartConfirmation
        {
            QueueId = "queue-1",
            RecipeId = "recipe",
            AreaType = (int)HideoutAreas.Workbench,
            StartTimestamp = 1700000000,
            Sequence = 7,
            ConfirmedAt = 1700000000123
        });

        string json = JsonSerializer.Serialize(data, jsonOptions);
        CraftQueueData? restored = JsonSerializer.Deserialize<CraftQueueData>(json, jsonOptions);

        Assert.NotNull(restored);
        PendingStartConfirmation record = Assert.Single(restored.PendingStartConfirmations);
        Assert.Equal("queue-1", record.QueueId);
        Assert.Equal(1700000000, record.StartTimestamp);
        Assert.Equal(7, record.Sequence);

        CraftQueueData? legacy = JsonSerializer.Deserialize<CraftQueueData>(
            """{ "areaQueues": {} }""", jsonOptions);
        Assert.NotNull(legacy);
        legacy.NormalizeAfterLoad();
        Assert.Empty(legacy.PendingStartConfirmations);
    }

    [Fact]
    public void ConfigWithoutWebPortKeepsTheDefaultPort()
    {
        var legacy = JsonSerializer.Deserialize<WebServerConfig>(
            """{ "allowLan": true, "bindAddress": "0.0.0.0" }""", jsonOptions);
        Assert.NotNull(legacy);
        Assert.Equal(CraftQueueProtocol.WebPort, legacy.WebPort);

        var custom = JsonSerializer.Deserialize<WebServerConfig>(
            """{ "allowLan": false, "bindAddress": "127.0.0.1", "webPort": 41234 }""", jsonOptions);
        Assert.NotNull(custom);
        Assert.Equal(41234, custom.WebPort);
    }

    private static CraftQueueData createData(string recipeId)
    {
        return new CraftQueueData
        {
            AreaQueues = new()
            {
                [HideoutAreas.Workbench] = new LinkedList<QueuedProductionRequest>(
                    [new QueuedProductionRequest { RecipeId = recipeId }])
            }
        };
    }
}
