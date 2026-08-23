using System.Collections.Generic;
using Newtonsoft.Json;

namespace TheUnknowing
{
    // Loaded from VintagestoryData/ModConfig/TheUnknowing.json (created with these defaults on
    // first run if missing). Every tunable this mod introduces belongs here, not as a hardcoded
    // constant.
    public class UnknowingConfig
    {
        // Real-life minutes, not in-game hours - a server's calendar speed would otherwise make
        // the same value mean a wildly different real-world wait on different servers.
        public double GatheringStrengthDurationMinutes { get; set; } = 30.0;
        public double EnteringRealityDurationMinutes { get; set; } = 30.0;

        // Multiplies MaxConcurrentEnemies and EmberParticlesPerColumn during EnteringReality.
        public float EnteringIntensityMultiplier { get; set; } = 2f;

        // Also the fixed interval the spawn tick listener is registered at - needs a server
        // restart to re-tune, same as every other interval field below.
        public double EnemySpawnIntervalSeconds { get; set; } = 30.0;

        // Per-storm cap - dead/despawned entities are pruned from tracking first, so this
        // reflects actually-alive enemies, not a lifetime spawn count.
        public int MaxConcurrentEnemies { get; set; } = 6;

        // Entity type codes for built-in survival/creative content live under the shared "game"
        // domain regardless of which assets/<folder>/ they ship from.
        //
        // ObjectCreationHandling.Replace is required - Newtonsoft otherwise appends a
        // deserialized JSON array onto this List<T>'s non-empty default instead of replacing it,
        // duplicating every entry on each server restart.
        [JsonProperty(ObjectCreationHandling = ObjectCreationHandling.Replace)]
        public List<string> EnemyEntityCodes { get; set; } = new() { "game:drifter-normal", "game:drifter-deep" };

        // Replaces (not adds to) EnemyEntityCodes once a storm reaches EnteringReality.
        [JsonProperty(ObjectCreationHandling = ObjectCreationHandling.Replace)]
        public List<string> EnteringEnemyEntityCodes { get; set; } = new()
        {
            "game:drifter-tainted", "game:drifter-corrupt", "game:drifter-nightmare", "game:drifter-double-headed"
        };

        // Also the fixed interval the fog tick listener is registered at.
        public double FogParticleIntervalSeconds { get; set; } = 2.0;

        // Purple "ember" particles spawned per column, each burst.
        public float EmberParticlesPerColumn { get; set; } = 4f;

        // Real-time seconds for the client's in-storm fog to fade fully in/out on crossing a
        // storm boundary, rather than snapping. Sent to the client via InStormPacket, since it
        // has no config file of its own.
        public float FogFadeSeconds { get; set; } = 2.5f;

        // Its own fast tick (not the shared 10s OnGameTick) so InStormPacket - and the client fog
        // fade - starts promptly on the real boundary crossing. Cheap enough (a chunk-column
        // lookup per online player, no state mutation) to run this often.
        public double StormMembershipIntervalSeconds { get; set; } = 1.0;
    }
}
