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

        // Single source of truth for every storm-music fade (fresh entry, cancel-a-fade-out-in-
        // flight, and leaving all read this rather than their own literal).
        private const float StormMusicFadeSeconds = 3.0f;

        // Client-side fade state for the in-storm fog effect - see StartClientSide/OnFogFadeTick.
        private AmbientModifier? fogModifier;
        private float fogWeight;
        private bool inStorm;
        private float fogFadeSeconds = 2.5f;

        // Client-side "you are in a storm" music - see StartClientSide/OnInStormPacket and the
        // Start/StopStormMusic region below. One pre-concatenated ~2:52 file (4 source tracks in
        // a fixed order, authored to sound correct back-to-back), looped continuously with a
        // single fade-in on first entering the storm - a runtime-shuffled version of 4 separate
        // tracks read as a noticeable seam at every switch even with per-track fade-ins. Resolves
        // to assets/theunknowing/music/unknowing-storm.ogg (MusicTrack.Initialize prefixes any
        // non-"sounds"-rooted path with "music/" and appends ".ogg").
        private static readonly AssetLocation StormMusicLocation = new AssetLocation("theunknowing", "unknowing-storm");
        private ICoreClientAPI? capi;
        private MusicTrack? stormTrack;
        private bool stormTrackFadingOut;
        private long stormTrackStartLoadingMs;
        private long stormTrackStartHandlerId;

        // Runs on both sides before Entities/Blocks/Items are loaded from JSON - custom entity
        // behavior classes referenced by code need to be registered here, not in
        // StartServerSide/StartClientSide (those run after).
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

            api.ChatCommands.Create("unknowing-storm-status")
                .WithDescription("Debug/testing only: lists every tracked storm's target, status, chunk column count, and spawned entity count - use to check for stale/overlapping storms left over from earlier testing.")
                .RequiresPrivilege(Privilege.controlserver)
                .HandleWith(_ => stormManager.DumpStormStatus());

            api.ChatCommands.Create("unknowing-demo")
                .WithDescription("RP/event tool: '/unknowing-demo <player>' spawns the storm cloud + ember particles on the online player's current chunk (no claim touched, no state progression, no enemy spawns) and it stays until '/unknowing-demo kill' ends it.")
                .RequiresPrivilege(Privilege.controlserver)
                .WithArgs(api.ChatCommands.Parsers.Word("playerNameOrKill"))
                .HandleWith(args => HandleDemoCommand(stormManager, (string)args[0]));
        }

        private static TextCommandResult HandleDemoCommand(UnknowingStormManager stormManager, string arg)
        {
            return string.Equals(arg, "kill", StringComparison.OrdinalIgnoreCase)
                ? stormManager.StopDemo()
                : stormManager.StartDemo(arg);
        }

        // First client-side code this mod has - real fog (IAmbientManager's blended
        // AmbientModifier stack) only means anything on the client; the server only knows
        // whether a player is inside a storm's bounds (see UnknowingStormManager.OnGameTick).
        public override void StartClientSide(ICoreClientAPI api)
        {
            base.StartClientSide(api);
            capi = api;

            api.RegisterEntityRendererClass("theunknowing:theunknowing", typeof(TheUnknowingRenderer));

            // FogDensity values run roughly 7-20+ even for ordinary weather (see
            // assets/game/config/weatherpatterns/haze.json) - a 0-1-normalized guess here would
            // be effectively zero. Registered once and never removed; only Weight moves
            // afterward (see OnFogFadeTick) - removing/re-adding the modifier on entering/leaving
            // is what caused an earlier "flips on/off like a switch" bug, since removal snapped
            // the blend straight back to base with nothing to fade through.
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
            bool wasInStorm = inStorm;
            inStorm = packet.InStorm;
            if (packet.FogFadeSeconds > 0) fogFadeSeconds = packet.FogFadeSeconds;

            // InStormPacket only arrives on an actual transition (see
            // UnknowingStormManager.OnMembershipTick), so this edge check is mostly
            // belt-and-suspenders rather than load-bearing deduplication.
            if (inStorm && !wasInStorm) StartStormMusic();
            else if (!inStorm && wasInStorm) StopStormMusic();
        }

        // Mirrors vssurvivalmod's BlockEntityMusicTrigger rather than a raw looping PlaySound -
        // StartTrack/ForceActive/Priority plug into the real music engine so ambient music fades
        // out properly instead of layering under this. Priority 99 matches the game's own
        // boss-fight/lore-area tracks, high enough to always win over incidental ambient music.
        private void StartStormMusic()
        {
            if (capi == null) return;
            if (stormTrackFadingOut && stormTrack?.Sound != null)
            {
                stormTrack.Sound.FadeIn(StormMusicFadeSeconds, null); // cancels the FadeOut already in flight
                stormTrackFadingOut = false;
                return;
            }
            if (stormTrack?.loading == true || stormTrack?.Sound?.IsPlaying == true) return;

            stormTrackFadingOut = false;
            stormTrackStartLoadingMs = capi.World.ElapsedMilliseconds;
            stormTrack = capi.StartTrack(StormMusicLocation, 99f, EnumSoundType.Music, OnStormTrackLoaded);
            stormTrack.ForceActive = true;
        }

        private void StopStormMusic()
        {
            if (capi == null) return;

            if (stormTrackStartHandlerId != 0)
            {
                capi.Event.UnregisterCallback(stormTrackStartHandlerId);
                return;
            }

            if (stormTrack?.Sound != null) stormTrackFadingOut = true;
            stormTrack?.FadeOut(StormMusicFadeSeconds, () =>
            {
                stormTrackFadingOut = false;
                // This callback lands on a later main-thread tick, by which time stormTrack could
                // already be null.
                MusicTrack? trackTmp = stormTrack;
                if (trackTmp != null) trackTmp.ForceActive = false;
                stormTrack = null;
            });
        }

        private void OnStormTrackLoaded(ILoadedSound sound)
        {
            if (stormTrack == null)
            {
                sound?.Dispose();
                return;
            }
            if (sound == null) return;

            stormTrack.Sound = sound;
            stormTrack.Sound.SetLooping(true);

            // Needed so the music engine doesn't dispose the sound out from under ForceActive.
            stormTrack.ManualDispose = true;

            long msPassed = capi!.World.ElapsedMilliseconds - stormTrackStartLoadingMs;
            stormTrackStartHandlerId = capi.Event.RegisterCallback((dt) =>
            {
                if (sound.IsDisposed) return;
                // Start() begins playback at full volume immediately - FadeIn only ramps
                // audibly from a nonzero starting volume, so it's a no-op on a fresh Start()
                // unless the volume is explicitly zeroed first.
                sound.SetVolume(0f);
                sound.Start();
                sound.FadeIn(StormMusicFadeSeconds, null);
                stormTrack!.loading = false;
                stormTrackStartHandlerId = 0;
            }, (int)Math.Max(0, 500 - msPassed), true);
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

        // Split out from StartServerSide deliberately - the JIT resolves every type a method
        // references the moment that method first runs, so a bare `if (IsModEnabled) { <ConfigLib
        // calls inline> }` would still try to load configlib.dll on a server without it installed
        // (an optional dependency, deliberately not in modinfo.json). Isolating every ConfigLib
        // type reference in its own method means that load only happens once IsModEnabled has
        // already confirmed the mod is present.
        //
        // TheUnknowing.json stays the one file on disk; ConfigLib is purely a live in-session
        // editor for it, one direction at a time: SeedConfigLibFromConfig pushes config's current
        // values onto every ConfigLib setting (so a server with an already-tuned config doesn't
        // get silently stomped by configlib-patches.json's own hardcoded defaults the first time
        // ConfigLib is installed), and from then on every GUI edit is pulled back into config and
        // written straight to TheUnknowing.json. See NOTES.local.md for the full pattern and the
        // two ConfigLib bugs (YAML corruption, stale schema version) worked around along the way.
        private static void HookConfigLib(ICoreServerAPI api, UnknowingConfig config)
        {
            var configLib = api.ModLoader.GetModSystem<ConfigLibModSystem>();
            IConfig? libConfig = configLib.GetConfig("theunknowing");
            if (libConfig == null) return;

            SeedConfigLibFromConfig(libConfig, config);

            // Every *IntervalSeconds field only sets a tick listener's fixed interval once, at
            // StartServerSide's registration call above, so those still need a server restart to
            // re-tune even via a live GUI edit (see each setting's own comment in
            // configlib-patches.json).
            configLib.SettingChanged += (domain, _, _) =>
            {
                if (domain != "theunknowing") return;
                PullConfigLibIntoConfig(libConfig, config);
                api.StoreModConfig(config, ConfigFilename);
            };
        }

        // Pushes every UnknowingConfig field onto its matching ConfigLib setting and writes them
        // out, so the GUI/YAML reflect real current values immediately, even before an admin
        // touches anything. Called before HookConfigLib subscribes to SettingChanged so this
        // seeding pass can't immediately re-trigger its own handler.
        //
        // The enemy-code lists are joined as comma-separated strings, matching their "string"
        // setting type in configlib-patches.json - not "other" (arbitrary JSON), which corrupts
        // ConfigLib's generated YAML for any array/object-default setting with no range/values/
        // mapping validation (a real ConfigLib bug - JsonObject.ToString() on a JArray comes back
        // multi-line, spliced into what must be a single-line YAML comment). A plain string's
        // ToString() is always single-line, so this sidesteps it entirely.
        private static void SeedConfigLibFromConfig(IConfig libConfig, UnknowingConfig config)
        {
            SetIfPresent(libConfig, "GATHERING_MINUTES", config.GatheringStrengthDurationMinutes);
            SetIfPresent(libConfig, "ENTERING_MINUTES", config.EnteringRealityDurationMinutes);
            SetIfPresent(libConfig, "ENTERING_INTENSITY_MULTIPLIER", config.EnteringIntensityMultiplier);
            SetIfPresent(libConfig, "ENEMY_SPAWN_INTERVAL_SECONDS", config.EnemySpawnIntervalSeconds);
            SetIfPresent(libConfig, "MAX_CONCURRENT_ENEMIES", config.MaxConcurrentEnemies);
            SetIfPresent(libConfig, "FOG_PARTICLE_INTERVAL_SECONDS", config.FogParticleIntervalSeconds);
            SetIfPresent(libConfig, "EMBER_PARTICLES_PER_COLUMN", config.EmberParticlesPerColumn);
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

        // Reads each setting explicitly rather than via IConfig.AssignSettingsValues - that
        // reflection-based helper assigns via Value.AsFloat()/AsInt() straight onto the property,
        // which throws for our double *Minutes/*Seconds fields (no auto-widening from a boxed
        // float). Each falls back to config's current value if the setting isn't found.
        private static void PullConfigLibIntoConfig(IConfig libConfig, UnknowingConfig config)
        {
            config.GatheringStrengthDurationMinutes = libConfig.GetSetting("GATHERING_MINUTES")?.Value.AsDouble(config.GatheringStrengthDurationMinutes) ?? config.GatheringStrengthDurationMinutes;
            config.EnteringRealityDurationMinutes = libConfig.GetSetting("ENTERING_MINUTES")?.Value.AsDouble(config.EnteringRealityDurationMinutes) ?? config.EnteringRealityDurationMinutes;
            config.EnteringIntensityMultiplier = (float)(libConfig.GetSetting("ENTERING_INTENSITY_MULTIPLIER")?.Value.AsDouble(config.EnteringIntensityMultiplier) ?? config.EnteringIntensityMultiplier);
            config.EnemySpawnIntervalSeconds = libConfig.GetSetting("ENEMY_SPAWN_INTERVAL_SECONDS")?.Value.AsDouble(config.EnemySpawnIntervalSeconds) ?? config.EnemySpawnIntervalSeconds;
            config.MaxConcurrentEnemies = libConfig.GetSetting("MAX_CONCURRENT_ENEMIES")?.Value.AsInt(config.MaxConcurrentEnemies) ?? config.MaxConcurrentEnemies;
            config.FogParticleIntervalSeconds = libConfig.GetSetting("FOG_PARTICLE_INTERVAL_SECONDS")?.Value.AsDouble(config.FogParticleIntervalSeconds) ?? config.FogParticleIntervalSeconds;
            config.EmberParticlesPerColumn = (float)(libConfig.GetSetting("EMBER_PARTICLES_PER_COLUMN")?.Value.AsDouble(config.EmberParticlesPerColumn) ?? config.EmberParticlesPerColumn);
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
