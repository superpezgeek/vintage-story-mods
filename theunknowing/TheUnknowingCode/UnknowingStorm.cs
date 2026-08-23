using System.Collections.Generic;
using ProtoBuf;

namespace TheUnknowing
{
    // Explicitly numbered - protobuf-net serializes enums by ordinal, so a saved storm's Status
    // is really just this int. Renumbering or reordering these would silently reinterpret every
    // already-persisted storm as the wrong phase on next load; clear test storms
    // (/unknowing-storm-clear) before deploying any future change to this enum.
    public enum UnknowingStormStatus
    {
        // Storm has just been unleashed - baseline enemy pressure and ember density.
        GatheringStrength = 0,

        // Escalation phase - same mechanics as GatheringStrength, scaled up by
        // UnknowingConfig.EnteringIntensityMultiplier (enemy cap, ember density).
        EnteringReality = 1,

        Collapsing = 2,

        // Terminal state - reached once Collapsing has finished doing its work (stopping
        // spawns, running the wind-down VFX, triggering /wgen regen). Not acted on yet;
        // Collapsing currently has no exit.
        Done = 3
    }

    // Everything needed to persist an in-progress storm across a server restart, via
    // ISaveGame.StoreData/GetData (see UnknowingStormManager). That API is protobuf-net under
    // the hood, not Newtonsoft.Json (confirmed live - a first attempt without [ProtoContract]
    // threw "Type is not expected, and no contract can be inferred" from inside Persist(), after
    // the claim had already been deleted) - unlike LoadModConfig/StoreModConfig, which are JSON.
    // Explicit [ProtoMember] tags rather than ImplicitFields, so save data stays readable if
    // fields are added/removed later.
    [ProtoContract]
    public class UnknowingStorm
    {
        [ProtoMember(1)]
        public string TargetPlayerName { get; set; } = "";

        // Snapshotted from config at storm creation rather than read live from UnknowingConfig on
        // every tick, so a config change doesn't retroactively alter a storm already in progress.
        // Fields 2, 7, 8 (the old in-game-hours StartTotalHours/GatheringStrengthDurationHours/
        // EnteringRealityDurationHours) intentionally left unused rather than reused, per
        // protobuf-net convention - see UnknowingStormStatus. Replaced with real-world wall-clock
        // equivalents so phase timing means the same real-life wait regardless of a server's
        // calendar speed.
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

        // Entity IDs this storm has spawned and is still tracking (pruned of dead/despawned ones
        // on each spawn tick - see UnknowingStormManager). Basis for the MaxConcurrentEnemies cap
        // now, and for containment later (0.2's other remaining item).
        [ProtoMember(6)]
        public List<long> SpawnedEntityIds { get; set; } = new();
    }

    [ProtoContract]
    public class ChunkColumn
    {
        [ProtoMember(1)]
        public int ChunkX { get; set; }

        [ProtoMember(2)]
        public int ChunkZ { get; set; }

        // The landmark entity (theunknowing:theunknowing) spawned for this column - a static,
        // tall translucent column meant to be visible from far outside the storm (unlike the
        // particle effects, which only read up close). One per column rather than one per storm,
        // so the landmark is visible across the whole storm's footprint, not just its center.
        // Not subject to MaxConcurrentEnemies or containment despawn-on-wander - only
        // UnknowingStormManager.EnsureCloudsSpawned (self-healing, runs on OnGameTick) and
        // ClearAllStorms touch this. 0 = not yet spawned.
        [ProtoMember(3)]
        public long CloudEntityId { get; set; }

        public ChunkColumn() { }

        public ChunkColumn(int chunkX, int chunkZ)
        {
            ChunkX = chunkX;
            ChunkZ = chunkZ;
        }
    }
}
