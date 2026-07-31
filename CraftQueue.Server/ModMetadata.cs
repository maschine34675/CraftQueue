using CraftQueue.Shared;
using SPTarkov.Server.Core.Models.Spt.Mod;
using System.Collections.Generic;

namespace CraftQueue.Server
{
    public record ModMetadata : AbstractModMetadata
    {
        public override string ModGuid { get; init; } = CraftQueueProtocol.ModGuid;
        public override string Name { get; init; } = CraftQueueProtocol.ModName;

        public override string Author { get; init; } = "maschine";

        public override List<string>? Contributors { get; init; } = [];

        public override SemanticVersioning.Version Version { get; init; } = new(CraftQueueProtocol.Version);

        public override SemanticVersioning.Range SptVersion { get; init; } = new($"~{CraftQueueProtocol.SptVersion}");

        public override List<string>? Incompatibilities { get; init; } = [];

        public override Dictionary<string, SemanticVersioning.Range>? ModDependencies { get; init; } = [];

        public override string? Url { get; init; } = "https://github.com/maschine34675/CraftQueue";

        public override bool? IsBundleMod { get; init; } = false;

        public override string License { get; init; } = "MIT";
    }
}
