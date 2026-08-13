using System.Reflection;
using HarmonyLib;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Server;
using Vintagestory.GameContent;

namespace Caveshrooms
{
    public class CaveshroomsModSystem : ModSystem
    {
        private const string HarmonyId = "caveshrooms";
        private const string ConfigFilename = "Caveshrooms.json";

        // Loaded once in Start() and read from everywhere else, including the static Harmony
        // patch in EntityPlayerGlowPatch which has no other way to reach mod state.
        public static CaveshroomsConfig Config { get; private set; } = new CaveshroomsConfig();

        private Harmony? harmony;
        private double lastCalendarTotalHours = double.NaN;

        public override void Start(ICoreAPI api)
        {
            base.Start(api);

            Config = api.LoadModConfig<CaveshroomsConfig>(ConfigFilename) ?? new CaveshroomsConfig();
            api.StoreModConfig(Config, ConfigFilename);

            api.RegisterCollectibleBehaviorClass("CaveshroomsTemporalEffect", typeof(CollectibleBehaviorTemporalEffect));
            api.RegisterItemClass("Caveshrooms.ItemTemporalMushroom", typeof(ItemTemporalMushroom));

            if (!Harmony.HasAnyPatches(HarmonyId))
            {
                harmony = new Harmony(HarmonyId);
                harmony.PatchAll(Assembly.GetExecutingAssembly());
            }
        }

        // Runs after the engine's own jsonpatches loader (ExecuteOrder 0.05) but before the
        // block/item loader (0.2) parses itemtypes/blocktypes JSON - the documented window for
        // rewriting raw asset bytes before they're consumed. See CaveshroomsAssetTuning for why
        // (and how defensively) this rewrites two of our own JSON assets in place using Config.
        public override void AssetsLoaded(ICoreAPI api)
        {
            base.AssetsLoaded(api);

            CaveshroomsAssetTuning.RewriteBlocktypeAsset(api, Config);
            CaveshroomsAssetTuning.RewriteWorldgenAsset(api, Config);
        }

        public override void StartServerSide(ICoreServerAPI api)
        {
            base.StartServerSide(api);

            api.Event.RegisterGameTickListener(_ => DecayGlow(api), 1000);
        }

        private void DecayGlow(ICoreServerAPI api)
        {
            double totalHours = api.World.Calendar.TotalHours;
            if (double.IsNaN(lastCalendarTotalHours))
            {
                lastCalendarTotalHours = totalHours;
                return;
            }

            double elapsedHours = totalHours - lastCalendarTotalHours;
            lastCalendarTotalHours = totalHours;
            if (elapsedHours <= 0) return;

            float decayAmount = (float)(Config.GlowDecayPerInGameHour * elapsedHours);

            foreach (IPlayer player in api.World.AllOnlinePlayers)
            {
                Entity? entity = player.Entity;
                if (entity == null) continue;

                float glow = entity.WatchedAttributes.GetFloat("caveshrooms:temporalGlow", 0f);
                if (glow <= 0f) continue;

                float newGlow = System.Math.Max(0f, glow - decayAmount);
                entity.WatchedAttributes.SetFloat("caveshrooms:temporalGlow", newGlow);
                entity.WatchedAttributes.MarkPathDirty("caveshrooms:temporalGlow");
            }
        }

        public override void StartClientSide(ICoreClientAPI api)
        {
            base.StartClientSide(api);

            api.ChatCommands.Create("temporalstatus")
                .WithDescription("Shows your current temporal glow, stability, and psychedelic/intoxication levels.")
                .HandleWith(args =>
                {
                    Entity? entity = api.World.Player?.Entity;
                    if (entity == null) return TextCommandResult.Error("No player entity found.");

                    float glow = entity.WatchedAttributes.GetFloat("caveshrooms:temporalGlow", 0f);
                    float psychedelic = entity.WatchedAttributes.GetFloat("psychedelic", 0f);
                    float intoxication = entity.WatchedAttributes.GetFloat("intoxication", 0f);
                    EntityBehaviorTemporalStabilityAffected? stabilityBehavior = entity.GetBehavior<EntityBehaviorTemporalStabilityAffected>();

                    string stabilityText = stabilityBehavior != null ? stabilityBehavior.OwnStability.ToString("0.000") : "n/a";

                    return TextCommandResult.Success(
                        $"Temporal glow: {glow:0.0} / {Config.MaxGlow:0}\n" +
                        $"Temporal stability: {stabilityText}\n" +
                        $"Psychedelic level: {psychedelic:0.00}\n" +
                        $"Intoxication level: {intoxication:0.00}"
                    );
                });
        }

        public override void Dispose()
        {
            harmony?.UnpatchAll(HarmonyId);
            base.Dispose();
        }
    }
}
