using System;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
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

        public override void StartServerSide(ICoreServerAPI api)
        {
            base.StartServerSide(api);

            UnknowingConfig config = api.LoadModConfig<UnknowingConfig>(ConfigFilename) ?? new UnknowingConfig();
            api.StoreModConfig(config, ConfigFilename);

            IServerNetworkChannel channel = api.Network.RegisterChannel(NetworkChannelName)
                .RegisterMessageType<InStormPacket>();

            var stormManager = new UnknowingStormManager(api, config, channel);
            api.Event.RegisterGameTickListener(_ => stormManager.OnGameTick(), 10000);
            api.Event.RegisterGameTickListener(_ => stormManager.OnSpawnTick(), (int)(config.EnemySpawnIntervalSeconds * 1000));
            api.Event.RegisterGameTickListener(_ => stormManager.OnFogTick(), (int)(config.FogParticleIntervalSeconds * 1000));
            api.Event.RegisterGameTickListener(_ => stormManager.OnAmbientAudioTick(), (int)(config.AmbientAudioIntervalSeconds * 1000));

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
    }
}
