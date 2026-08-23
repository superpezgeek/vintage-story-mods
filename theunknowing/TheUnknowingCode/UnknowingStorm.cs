using System.Collections.Generic;
using ProtoBuf;

namespace TheUnknowing
{
    // Explicitly numbered - protobuf-net serializes enums by ordinal, so reordering these would
    // silently reinterpret already-persisted storms as the wrong phase. Clear test storms
    // (/unknowing-storm-clear) before deploying any future change to this enum.
    public enum UnknowingStormStatus
    {
        GatheringStrength = 0,
        EnteringReality = 1,
        Collapsing = 2,
        Done = 3
    }

    // Persisted across server restarts via ISaveGame.StoreData/GetData, which is protobuf-net
    // under the hood (not Newtonsoft.Json like LoadModConfig/StoreModConfig) - needs explicit
    // [ProtoMember] tags rather than ImplicitFields so fields can be added/removed later.
    [ProtoContract]
    public class UnknowingStorm
    {
        [ProtoMember(1)]
        public string TargetPlayerName { get; set; } = "";

        // Snapshotted from config at creation, not read live, so a config change never
        // retroactively alters a storm already in progress. Fields 2/7/8 intentionally left
        // unused (old in-game-hours fields, replaced by real-world wall-clock equivalents).
        [ProtoMember(9)]
        public long StartUnixMillis { get; set; }

        [ProtoMember(10)]
        public double GatheringStrengthDurationMinutes { get; set; }

        [ProtoMember(11)]
        public double EnteringRealityDurationMinutes { get; set; }

        [ProtoMember(4)]
        public UnknowingStormStatus Status { get; set; } = UnknowingStormStatus.GatheringStrength;

        [ProtoMember(5)]
        public List<ChunkColumn> ChunkColumns { get; set; } = new();

        [ProtoMember(6)]
        public List<long> SpawnedEntityIds { get; set; } = new();

        // Set only by /unknowing-demo - no claim was ever removed, so this never progresses past
        // GatheringStrength and never spawns enemies (see UnknowingStormManager.OnGameTick/
        // OnSpawnTick). Torn down directly by /unknowing-demo kill, not through the normal
        // Collapsing/regen pipeline.
        [ProtoMember(12)]
        public bool IsDemo { get; set; }
    }

    [ProtoContract]
    public class ChunkColumn
    {
        [ProtoMember(1)]
        public int ChunkX { get; set; }

        [ProtoMember(2)]
        public int ChunkZ { get; set; }

        // The landmark entity (theunknowing:theunknowing) spawned for this column. 0 = not yet
        // spawned; self-healed by UnknowingStormManager.EnsureCloudsSpawned every tick.
        [ProtoMember(3)]
        public long CloudEntityId { get; set; }

        // The Y span of whichever claim area(s) originally touched this column, so mobs spawn at
        // the claim's own depth (e.g. an underground base) rather than at the surface.
        [ProtoMember(4)]
        public int ClaimMinY { get; set; }

        [ProtoMember(5)]
        public int ClaimMaxY { get; set; }

        public ChunkColumn() { }

        public ChunkColumn(int chunkX, int chunkZ, int claimMinY, int claimMaxY)
        {
            ChunkX = chunkX;
            ChunkZ = chunkZ;
            ClaimMinY = claimMinY;
            ClaimMaxY = claimMaxY;
        }
    }
}
