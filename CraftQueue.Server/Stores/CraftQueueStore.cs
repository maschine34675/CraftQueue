using CraftQueue.Server.Models;
using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.Models.Common;
using System.Collections.Concurrent;

namespace CraftQueue.Server.Stores
{
    [Injectable(InjectionType = InjectionType.Singleton)]
    public class CraftQueueStore(JsonStore<CraftQueueData> jsonStore)
    {
        private const string filename = "craftqueue.json";

        private readonly ConcurrentDictionary<MongoId, CraftQueueData> craftQueueData = [];

        public CraftQueueData Get(MongoId profileId)
        {
            return craftQueueData.GetOrAdd(profileId, id =>
            {
                CraftQueueData data = jsonStore.Load(id, filename) ?? new CraftQueueData();
                if (data.NormalizeAfterLoad())
                    jsonStore.Save(data, id, filename);
                return data;
            });
        }

        public void Set(MongoId profileId)
        {
            if (craftQueueData.TryGetValue(profileId, out CraftQueueData? data))
                jsonStore.Save(data, profileId, filename);
        }
    }
}
