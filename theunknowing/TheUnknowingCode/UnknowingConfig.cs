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

        // Real-time seconds between fog particle bursts - also the interval the fog tick listener
        // itself is registered at, same restart-to-retune caveat as EnemySpawnIntervalSeconds.
        //
        // Deliberately not reusing the game's own Temporal Stability system for this (an earlier
        // approach did, by pinning EntityBehaviorTemporalStabilityAffected.OwnStability down) -
        // confirmed live that got tangled in vanilla's own stability thresholds in ways that
        // matter for a real server: unavoidable player damage below 12%, and uncontrolled
        // tier-4 bowtorn spawns below 5%, neither of which was ever part of the intended design.
        // Spawning our own particles instead means zero coupling to any of that - entirely our
        // own responsibility to tune, and it can't hurt anyone by surprise.
        public double FogParticleIntervalSeconds { get; set; } = 2.0;

        // Crimson "ember" particles spawned per column, each burst - color contrast against the
        // stormcloud entity's void-black, meant to read as active danger rather than plain smoke.
        public float EmberParticlesPerColumn { get; set; } = 4f;

        // Real-time seconds between ambient dread audio cues - own tick, same restart-to-retune
        // caveat. Reuses the game's existing Rift sound (game:sounds/effect/rift.ogg - confirmed
        // live under the "game" domain, same shared-domain quirk as entity codes) rather than
        // needing new audio assets - it's the closest thematic match already in the game for
        // "localized temporal wrongness."
        public double AmbientAudioIntervalSeconds { get; set; } = 15.0;

        // How far (blocks) the ambient audio cue carries from the storm's center.
        public float AmbientAudioRange { get; set; } = 48f;

        // Real-time seconds for the in-storm fog AmbientModifier to fade fully in or out on
        // entering/leaving a storm's chunk bounds, rather than snapping on/off - see
        // TheUnknowingModSystem.OnFogFadeTick. Sent to the client per-transition via
        // InStormPacket, since the client has no config file of its own to read this from.
        public float FogFadeSeconds { get; set; } = 2.5f;
    }
}
