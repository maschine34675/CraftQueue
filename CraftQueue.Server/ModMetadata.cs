using CraftQueue.Shared;
using SPTarkov.Server.Core.Models.Spt.Mod;
using System.Collections.Generic;

namespace CraftQueue.Server
{
    public record ModMetadata : IModMetadata
    {
        public string ModGuid { get; init; } = CraftQueueProtocol.ModGuid;
        public string Name { get; init; } = CraftQueueProtocol.ModName;

        public string Author { get; init; } = "maschine";

        public List<string>? Contributors { get; init; } = [];

        public SemanticVersioning.Version Version { get; init; } = new(CraftQueueProtocol.Version);

        public SemanticVersioning.Range SptVersion { get; init; } = new($"~{CraftQueueProtocol.SptVersion}");

        public List<string>? Incompatibilities { get; init; } = [];

        public Dictionary<string, SemanticVersioning.Range>? ModDependencies { get; init; } = [];

        public string? Url { get; init; } = "https://github.com/maschine34675/CraftQueue";

        public bool HasPrepatcher { get; init; } = false;

        public string License { get; init; } = "MIT";
    }
}
