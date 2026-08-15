using System.Collections.Generic;
using Newtonsoft.Json;

namespace TheUnknowing
{
    // Loaded from VintagestoryData/ModConfig/TheUnknowing.json (created with these defaults on
    // first run if missing), mirroring Caveshrooms' config pattern - every tunable this mod
    // introduces belongs here, not as a hardcoded constant.
    public class UnknowingConfig
    {
        // How long an Unknowing Storm stays Active before it starts Collapsing.
        public double StormDurationHours { get; set; } = 48.0;

        // Real-time seconds between spawn attempts per storm - also the interval the spawn tick
        // listener itself is registered at (TheUnknowingModSystem), not just a threshold checked
        // against a stored timestamp, so it applies at server start and needs a restart to
        // re-tune, same as every other config value here.
        public double EnemySpawnIntervalSeconds { get; set; } = 30.0;

        // Per-storm cap - each spawn tick prunes dead/despawned entities from the storm's tracked
        // list first, so this reflects actually-alive enemies, not a lifetime spawn count.
        public int MaxConcurrentEnemies { get; set; } = 6;

        // Reuses the game's existing Drifter (the same creature vanilla Temporal Storms spawn)
        // rather than a custom entity - deliberately excludes the stronger tainted/corrupt/
        // nightmare/double-headed variants for now, this is meant to be a nuisance/threat over an
        // abandoned base, not something that needs to punch above a normal storm.
        //
        // Domain is "game", not "survival" - despite the JSON living under assets/survival/,
        // confirmed live that entity type codes for the built-in survival/creative content are
        // registered under the shared default "game" domain, not the folder they ship from.
        //
        // ObjectCreationHandling.Replace is required here - by default Newtonsoft.Json
        // deserializes a JSON array onto an already-populated List<T> property (this one has a
        // non-empty default) by appending to it, not replacing it. Without this, the list grows
        // by 2 duplicate entries on every single server restart (confirmed live - a real config
        // file reached 4 entries, 2 duplicated pairs, after one restart).
        [JsonProperty(ObjectCreationHandling = ObjectCreationHandling.Replace)]
        public List<string> EnemyEntityCodes { get; set; } = new() { "game:drifter-normal", "game:drifter-deep" };
    }
}
