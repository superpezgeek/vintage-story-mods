using System;
using System.Collections.Generic;
using System.Linq;
using ConfigLib;
using Newtonsoft.Json.Linq;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;

namespace TheUnknowing
{
    public class TheUnknowingModSystem : ModSystem
    {
        private const string ConfigFilename = "TheUnknowing.json";
        private const string NetworkChannelName = "theunknowing";
        private const string FogAmbientModifierKey = "theunknowing:storm-fog";

        // Client-side fade state for the in-storm fog effect - see StartClientSide/OnFogFadeTick.
        private AmbientModifier? fogModifier;
        private float fogWeight;
        private bool inStorm;
        private float fogFadeSeconds = 2.5f;

        // Runs on both sides before Entities/Blocks/Items are loaded from JSON - custom entity
        // behavior classes referenced by code (e.g. stormcloud.json's "theunknowing:infotext")
        // need to be registered here, not in StartServerSide/StartClientSide (those run after).
        public override void Start(ICoreAPI api)
        {
            base.Start(api);
            api.RegisterEntityBehaviorClass("theunknowing:infotext", typeof(EntityBehaviorInfoText));
        }

        public override void StartServerSide(ICoreServerAPI api)
        {
            base.StartServerSide(api);

            api.World.Logger.StoryEvent("Unknowing what was forgotten...");

            UnknowingConfig config = api.LoadModConfig<UnknowingConfig>(ConfigFilename) ?? new UnknowingConfig();
            api.StoreModConfig(config, ConfigFilename);

            // Must run before the tick listeners below are registered - several of their
            // intervals are read once here, not on every tick, so a ConfigLib override needs to
            // land before this point to affect them at all (see HookConfigLib's comment).
            if (api.ModLoader.IsModEnabled("configlib"))
            {
                HookConfigLib(api, config);
            }

            IServerNetworkChannel channel = api.Network.RegisterChannel(NetworkChannelName)
                .RegisterMessageType<InStormPacket>();

            var stormManager = new UnknowingStormManager(api, config, channel);
            api.Event.RegisterGameTickListener(_ => stormManager.OnGameTick(), 10000);
            api.Event.RegisterGameTickListener(_ => stormManager.OnSpawnTick(), (int)(config.EnemySpawnIntervalSeconds * 1000));
            api.Event.RegisterGameTickListener(_ => stormManager.OnFogTick(), (int)(config.FogParticleIntervalSeconds * 1000));
            api.Event.RegisterGameTickListener(_ => stormManager.OnAmbientAudioTick(), (int)(config.AmbientAudioIntervalSeconds * 1000));
            api.Event.RegisterGameTickListener(_ => stormManager.OnMembershipTick(), (int)(config.StormMembershipIntervalSeconds * 1000));

            api.ChatCommands.Create("unknowing-storm")
                .WithAlias(new[] { "unknowingstorm" })
                .WithDescription("Summons an Unknowing Storm over every land claim owned by the given player (offline is fine) - erases the claim(s) and starts the storm.")
                .RequiresPrivilege(Privilege.controlserver)
                .WithArgs(api.ChatCommands.Parsers.Word("playerName"))
                .HandleWith(args => stormManager.StartStorm((string)args[0]));

            api.ChatCommands.Create("unknowing-storm-clear")
                .WithDescription("Debug/testing only: despawns every enemy every tracked storm owns and clears all storm state. Nothing in the real lifecycle calls this.")
                .RequiresPrivilege(Privilege.controlserver)
                .HandleWith(_ => stormManager.ClearAllStorms());

            api.ChatCommands.Create("unknowing-storm-respawn-clouds")
                .WithDescription("Debug/testing only: despawns every storm's cloud entities and lets them respawn fresh within one game tick - use after changing the stormcloud shape/texture to avoid testing against stale entity instances.")
                .RequiresPrivilege(Privilege.controlserver)
                .HandleWith(_ => stormManager.RespawnClouds());

            api.ChatCommands.Create("unknowing-storm-status")
                .WithDescription("Debug/testing only: lists every tracked storm's target, status, chunk column count, and spawned entity count - use to check for stale/overlapping storms left over from earlier testing.")
                .RequiresPrivilege(Privilege.controlserver)
                .HandleWith(_ => stormManager.DumpStormStatus());

            api.ChatCommands.Create("unknowing-storm-kill-nearby")
                .WithDescription("Debug/testing only: despawns any stormcloud entity within range of the caller that isn't currently owned by a tracked storm - for cleaning up orphaned clouds (e.g. left behind by a chunk that wasn't loaded when /unknowing-storm-clear or /unknowing-storm-respawn-clouds ran).")
                .RequiresPrivilege(Privilege.controlserver)
                .WithArgs(api.ChatCommands.Parsers.OptionalDouble("range", 64))
                .HandleWith(args => stormManager.KillNearbyClouds(args.Caller.Pos, (double)args[0]));
        }

        // First client-side code this mod has - everything else is server-authoritative. Needed
        // specifically because real fog (IAmbientManager's blended AmbientModifier stack - the
        // same general system caves/underwater/etc. use, not tied to Temporal Stability) only
        // means anything on the client; the server has no fog to push, only the knowledge of
        // whether a player is inside a storm's bounds (see UnknowingStormManager.OnGameTick).
        public override void StartClientSide(ICoreClientAPI api)
        {
            base.StartClientSide(api);

            // Color/density/brightness are a first-pass guess, expect these to need retuning once
            // seen live (same as every other numeric value in this mod so far).
            //
            // FogDensity's scale was a real bug the first time: guessed 0.03 (a 0-1-normalized
            // fraction), which was effectively zero - confirmed against the game's own shipped
            // weather patterns (assets/game/config/weatherpatterns/haze.json) that real FogDensity
            // values run roughly 7-20+ even for ordinary haze, not a 0-1 range. Went stronger than
            // vanilla's own haze here on purpose - this is meant to read as an active corruption
            // effect, not ambient weather.
            //
            // Registered once here and never removed - only its Value/Weight fields move
            // afterward (see OnFogFadeTick). Weight starts at 0 (no contribution to the blended
            // ambient) rather than being added/removed from CurrentModifiers on
            // entering/leaving, which is what caused the original "flips on/off like a switch"
            // effect: removing the modifier snaps the blend straight back to the base outdoor
            // values with nothing to fade through.
            fogModifier = new AmbientModifier
            {
                FogDensity = new WeightedFloat(25f, 0f),
                FogColor = new WeightedFloatArray(new[] { 0.03f, 0.02f, 0.04f }, 0f),
                FogBrightness = new WeightedFloat(0.15f, 0f),
            }.EnsurePopulated();
            api.Ambient.CurrentModifiers[FogAmbientModifierKey] = fogModifier;

            api.Event.RegisterGameTickListener(OnFogFadeTick, 50);

            api.Network.RegisterChannel(NetworkChannelName)
                .RegisterMessageType<InStormPacket>()
                .SetMessageHandler<InStormPacket>(OnInStormPacket);
        }

        private void OnInStormPacket(InStormPacket packet)
        {
            inStorm = packet.InStorm;
            if (packet.FogFadeSeconds > 0) fogFadeSeconds = packet.FogFadeSeconds;
        }

        // Ticks fogModifier's Weight fields toward 1 (in storm) or 0 (out of storm) over
        // fogFadeSeconds real-time seconds instead of snapping - see the comment in
        // StartClientSide for why the modifier itself stays registered permanently.
        private void OnFogFadeTick(float dt)
        {
            if (fogModifier == null) return;

            float target = inStorm ? 1f : 0f;
            float maxStep = fogFadeSeconds > 0 ? dt / fogFadeSeconds : 1f;

            if (fogWeight < target) fogWeight = Math.Min(target, fogWeight + maxStep);
            else if (fogWeight > target) fogWeight = Math.Max(target, fogWeight - maxStep);

            fogModifier.FogDensity.Weight = fogWeight;
            fogModifier.FogColor.Weight = fogWeight;
            fogModifier.FogBrightness.Weight = fogWeight;
        }

        // Split out from StartServerSide deliberately - the JIT resolves every type referenced
        // by a method the moment that method first runs, not just the branches actually taken, so
        // a bare `if (IsModEnabled) { <ConfigLib calls inline> }` inside StartServerSide would
        // still try to load configlib.dll on a server that doesn't have it installed (ConfigLib is
        // an optional dependency - not in modinfo.json - specifically so it isn't required). Moving
        // every ConfigLib type reference into its own method means that method - and the assembly
        // load it needs - only happens once the IsModEnabled check above already confirmed the mod
        // is present.
        //
        // TheUnknowing.json (already loaded into `config` by the time this runs) stays the one
        // file actually on disk; ConfigLib is treated purely as a live in-session editor for it,
        // not a second independent store:
        // 1. Seed ConfigLib with `config`'s current values (SeedConfigLibFromConfig) - overwrites
        //    whatever ConfigLib parsed configlib-patches.json's own hardcoded "default"s into,
        //    since ConfigLib has no way to know what TheUnknowing.json already held. Without this,
        //    the very first time ConfigLib gets installed on a server with an already-tuned
        //    TheUnknowing.json, its GUI would show (and then apply) those hardcoded defaults
        //    instead of the real values.
        // 2. From then on, any GUI edit (SettingChanged) is pulled back into `config` and written
        //    straight to TheUnknowing.json, so the file always reflects the latest value no matter
        //    which surface changed it last.
        // Editing TheUnknowing.json directly while the server is running still has no effect
        // either way - nothing re-reads it live, ConfigLib installed or not - same as before this
        // integration existed.
        private static void HookConfigLib(ICoreServerAPI api, UnknowingConfig config)
        {
            var configLib = api.ModLoader.GetModSystem<ConfigLibModSystem>();
            IConfig? libConfig = configLib.GetConfig("theunknowing");
            if (libConfig == null) return;

            SeedConfigLibFromConfig(libConfig, config);

            // GUI edits happen live, but only the fields OnGameTick/OnSpawnTick/etc. read fresh
            // every tick actually pick this up mid-session - every *IntervalSeconds field only sets
            // a tick listener's fixed interval once, at the registration call in StartServerSide
            // above, so those still need a server restart to actually retune (see each setting's
            // own comment in configlib-patches.json).
            configLib.SettingChanged += (domain, _, _) =>
            {
                if (domain != "theunknowing") return;
                PullConfigLibIntoConfig(libConfig, config);
                api.StoreModConfig(config, ConfigFilename);
            };
        }

        // Pushes every UnknowingConfig field's current value onto its matching ConfigLib setting,
        // then writes them out - the GUI/YAML immediately reflect real current values instead of
        // configlib-patches.json's defaults, even before an admin touches anything. Setting
        // ISetting.Value fires ConfigSetting's own SettingChanged if the value actually differs
        // (see ConfigSetting.cs in configlib's source) - called before HookConfigLib subscribes to
        // configLib.SettingChanged above specifically so this seeding pass can't immediately
        // re-trigger our own handler.
        //
        // The two enemy-code lists are joined into a comma-separated string here, matching their
        // "string" setting type in configlib-patches.json - NOT the "other" (arbitrary JSON) type
        // a List<string> would suggest. Confirmed live: "other" corrupts the generated YAML for any
        // setting whose default is a JArray/JObject and that has no range/values/mapping validation
        // (neither applies to a free-form list) - ConfigSetting.AddComments always appends
        // ` (default: {DefaultValue}) ` unless that validation exists, and JsonObject.ToString() on
        // a JArray comes back multi-line (Newtonsoft's default Formatting.Indented), splicing a
        // multi-line blob into what must be a single-line YAML comment. A real bug in ConfigLib
        // itself, not fixable from our side of the JSON schema - worked around by never handing it
        // an array/object default: a plain string's ToString() is always single-line, so the
        // corrupting code path never triggers, and a comma list is arguably nicer to edit in
        // ConfigLib's GUI than raw JSON syntax would have been anyway.
        private static void SeedConfigLibFromConfig(IConfig libConfig, UnknowingConfig config)
        {
            SetIfPresent(libConfig, "GATHERING_HOURS", config.GatheringStrengthDurationHours);
            SetIfPresent(libConfig, "ENTERING_HOURS", config.EnteringRealityDurationHours);
            SetIfPresent(libConfig, "ENTERING_INTENSITY_MULTIPLIER", config.EnteringIntensityMultiplier);
            SetIfPresent(libConfig, "ENEMY_SPAWN_INTERVAL_SECONDS", config.EnemySpawnIntervalSeconds);
            SetIfPresent(libConfig, "MAX_CONCURRENT_ENEMIES", config.MaxConcurrentEnemies);
            SetIfPresent(libConfig, "FOG_PARTICLE_INTERVAL_SECONDS", config.FogParticleIntervalSeconds);
            SetIfPresent(libConfig, "EMBER_PARTICLES_PER_COLUMN", config.EmberParticlesPerColumn);
            SetIfPresent(libConfig, "AMBIENT_AUDIO_INTERVAL_SECONDS", config.AmbientAudioIntervalSeconds);
            SetIfPresent(libConfig, "AMBIENT_AUDIO_RANGE", config.AmbientAudioRange);
            SetIfPresent(libConfig, "FOG_FADE_SECONDS", config.FogFadeSeconds);
            SetIfPresent(libConfig, "STORM_MEMBERSHIP_INTERVAL_SECONDS", config.StormMembershipIntervalSeconds);
            SetIfPresent(libConfig, "ENEMY_ENTITY_CODES", string.Join(", ", config.EnemyEntityCodes));
            SetIfPresent(libConfig, "ENTERING_ENEMY_ENTITY_CODES", string.Join(", ", config.EnteringEnemyEntityCodes));

            libConfig.WriteToFile();
        }

        private static void SetIfPresent(IConfig libConfig, string code, object value)
        {
            ISetting? setting = libConfig.GetSetting(code);
            if (setting == null) return;
            setting.Value = new JsonObject(JToken.FromObject(value));
        }

        // Reads each setting explicitly (code, expected type) rather than IConfig.
        // AssignSettingsValues' reflection-based field-name matching - that helper assigns via
        // Value.AsFloat()/AsInt() straight onto the target property, which throws for any of our
        // *Hours/*Seconds fields below (they're double, and PropertyInfo.SetValue doesn't
        // auto-widen a boxed float into a double). Explicit AsDouble/AsInt per field sidesteps that
        // entirely, and each falls back to `config`'s current value if the specific setting isn't
        // found (shouldn't happen once SeedConfigLibFromConfig has run, but GetSetting is nullable).
        private static void PullConfigLibIntoConfig(IConfig libConfig, UnknowingConfig config)
        {
            config.GatheringStrengthDurationHours = libConfig.GetSetting("GATHERING_HOURS")?.Value.AsDouble(config.GatheringStrengthDurationHours) ?? config.GatheringStrengthDurationHours;
            config.EnteringRealityDurationHours = libConfig.GetSetting("ENTERING_HOURS")?.Value.AsDouble(config.EnteringRealityDurationHours) ?? config.EnteringRealityDurationHours;
            config.EnteringIntensityMultiplier = (float)(libConfig.GetSetting("ENTERING_INTENSITY_MULTIPLIER")?.Value.AsDouble(config.EnteringIntensityMultiplier) ?? config.EnteringIntensityMultiplier);
            config.EnemySpawnIntervalSeconds = libConfig.GetSetting("ENEMY_SPAWN_INTERVAL_SECONDS")?.Value.AsDouble(config.EnemySpawnIntervalSeconds) ?? config.EnemySpawnIntervalSeconds;
            config.MaxConcurrentEnemies = libConfig.GetSetting("MAX_CONCURRENT_ENEMIES")?.Value.AsInt(config.MaxConcurrentEnemies) ?? config.MaxConcurrentEnemies;
            config.FogParticleIntervalSeconds = libConfig.GetSetting("FOG_PARTICLE_INTERVAL_SECONDS")?.Value.AsDouble(config.FogParticleIntervalSeconds) ?? config.FogParticleIntervalSeconds;
            config.EmberParticlesPerColumn = (float)(libConfig.GetSetting("EMBER_PARTICLES_PER_COLUMN")?.Value.AsDouble(config.EmberParticlesPerColumn) ?? config.EmberParticlesPerColumn);
            config.AmbientAudioIntervalSeconds = libConfig.GetSetting("AMBIENT_AUDIO_INTERVAL_SECONDS")?.Value.AsDouble(config.AmbientAudioIntervalSeconds) ?? config.AmbientAudioIntervalSeconds;
            config.AmbientAudioRange = (float)(libConfig.GetSetting("AMBIENT_AUDIO_RANGE")?.Value.AsDouble(config.AmbientAudioRange) ?? config.AmbientAudioRange);
            config.FogFadeSeconds = (float)(libConfig.GetSetting("FOG_FADE_SECONDS")?.Value.AsDouble(config.FogFadeSeconds) ?? config.FogFadeSeconds);
            config.StormMembershipIntervalSeconds = libConfig.GetSetting("STORM_MEMBERSHIP_INTERVAL_SECONDS")?.Value.AsDouble(config.StormMembershipIntervalSeconds) ?? config.StormMembershipIntervalSeconds;
            config.EnemyEntityCodes = SplitEntityCodes(libConfig.GetSetting("ENEMY_ENTITY_CODES")?.Value.AsString(), config.EnemyEntityCodes);
            config.EnteringEnemyEntityCodes = SplitEntityCodes(libConfig.GetSetting("ENTERING_ENEMY_ENTITY_CODES")?.Value.AsString(), config.EnteringEnemyEntityCodes);
        }

        // Mirrors SeedConfigLibFromConfig's string.Join(", ", ...) on the way back in.
        private static List<string> SplitEntityCodes(string? raw, List<string> fallback)
        {
            if (string.IsNullOrWhiteSpace(raw)) return fallback;
            return raw.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).ToList();
        }
    }
}
