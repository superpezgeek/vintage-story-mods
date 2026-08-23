using System.Reflection;
using ConfigLib;
using HarmonyLib;
using Newtonsoft.Json.Linq;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Datastructures;
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

            // Unlike TheUnknowing's equivalent hook, timing here isn't load-bearing: AssetsLoaded
            // (which bakes Harvest/GrowthGlow/Worldgen straight into JSON, see
            // CaveshroomsAssetTuning) already ran before StartServerSide, off the same static
            // Config that was loaded from Caveshrooms.json in Start() - this hook only pushes that
            // already-baked-from value into ConfigLib's display and subscribes for future live
            // edits, it never mutates Config on its own. Still gated the same way regardless: see
            // HookConfigLib's own comment for why every ConfigLib-typed call has to stay isolated
            // in its own method behind this same IsModEnabled check.
            if (api.ModLoader.IsModEnabled("configlib"))
            {
                HookConfigLib(api);
            }

            api.World.Logger.StoryEvent("Mushrooms glowing deep within caves...");

            api.Event.RegisterGameTickListener(_ => DecayGlow(api), 1000);

            // Admin-only equivalent of the client's own .temporalstatus, for checking another
            // player's stats - e.g. deciding when a milestone/achievement system (not built yet)
            // should trigger. Online only, since these values live on the live entity's
            // WatchedAttributes - there's nothing to read for an offline player.
            api.ChatCommands.Create("temporalstatusadmin")
                .WithDescription("Shows another online player's temporal glow, stability, and psychedelic/intoxication levels.")
                .RequiresPrivilege(Privilege.controlserver)
                .WithArgs(api.ChatCommands.Parsers.OnlinePlayer("player"))
                .HandleWith(args =>
                {
                    IPlayer target = (IPlayer)args[0];
                    Entity? entity = target.Entity;
                    if (entity == null) return TextCommandResult.Error($"{target.PlayerName} has no entity right now.");

                    return TextCommandResult.Success($"{target.PlayerName}:\n" + FormatTemporalStatus(entity));
                });
        }

        private static string FormatTemporalStatus(Entity entity)
        {
            float glow = entity.WatchedAttributes.GetFloat("caveshrooms:temporalGlow", 0f);
            float psychedelic = entity.WatchedAttributes.GetFloat("psychedelic", 0f);
            float intoxication = entity.WatchedAttributes.GetFloat("intoxication", 0f);
            int eatenCount = entity.WatchedAttributes.GetInt("caveshrooms:temporalMushroomsEaten", 0);
            EntityBehaviorTemporalStabilityAffected? stabilityBehavior = entity.GetBehavior<EntityBehaviorTemporalStabilityAffected>();

            string stabilityText = stabilityBehavior != null ? stabilityBehavior.OwnStability.ToString("0.000") : "n/a";

            return $"Temporal glow: {glow:0.0} / {Config.MaxGlow:0}\n" +
                   $"Temporal stability: {stabilityText}\n" +
                   $"Psychedelic level: {psychedelic:0.00}\n" +
                   $"Intoxication level: {intoxication:0.00}\n" +
                   $"Temporal mushrooms eaten: {eatenCount}";
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

                    return TextCommandResult.Success(FormatTemporalStatus(entity));
                });
        }

        public override void Dispose()
        {
            harmony?.UnpatchAll(HarmonyId);
            base.Dispose();
        }

        // Split out from StartServerSide deliberately - the JIT resolves every type referenced by
        // a method the moment that method first runs, not just the branches actually taken, so a
        // bare `if (IsModEnabled) { <ConfigLib calls inline> }` inside StartServerSide would still
        // try to load configlib.dll on a server that doesn't have it installed (ConfigLib is an
        // optional dependency - not in modinfo.json - specifically so it isn't required). Moving
        // every ConfigLib type reference into its own method (this one, and everything it calls)
        // means that method - and the assembly load it needs - only happens once the IsModEnabled
        // check in StartServerSide has already confirmed the mod is present.
        //
        // Caveshrooms.json (already loaded into the static Config by the time this runs) stays the
        // one file actually on disk; ConfigLib is treated purely as a live in-session editor for
        // it, not a second independent store - same model as TheUnknowing's own integration:
        // 1. Seed ConfigLib with Config's current values (SeedConfigLibFromConfig) - overwrites
        //    whatever ConfigLib parsed configlib-patches.json's own hardcoded "default"s into,
        //    since ConfigLib has no way to know what Caveshrooms.json already held. Without this,
        //    the very first time ConfigLib gets installed on a server with an already-tuned
        //    Caveshrooms.json, its GUI would show (and then apply) those hardcoded defaults
        //    instead of the real values.
        // 2. From then on, any GUI edit (SettingChanged) is pulled back into Config and written
        //    straight to Caveshrooms.json, so the file always reflects the latest value no matter
        //    which surface changed it last.
        // Editing Caveshrooms.json directly while the server is running still has no effect either
        // way - nothing re-reads it live, ConfigLib installed or not - same as before this
        // integration existed.
        //
        // Most settings here need a restart regardless of which surface edits them - Harvest,
        // GrowthGlow, and Worldgen are baked straight into shipped block/worldgen JSON once in
        // AssetsLoaded (see CaveshroomsAssetTuning), Perish and pie-filling nutrition are cached
        // once per item in ItemTemporalMushroom.OnLoaded, and GlowHue additionally feeds the
        // baked block glow on top of live player glow. Only the top-level stability/glow-per-eat
        // fields and the direct (non-pie) Nutrition fields are read fresh on every use and apply
        // without a restart. Each setting's own `comment` in configlib-patches.json says which.
        private static void HookConfigLib(ICoreServerAPI api)
        {
            var configLib = api.ModLoader.GetModSystem<ConfigLibModSystem>();
            IConfig? libConfig = configLib.GetConfig("caveshrooms");
            if (libConfig == null)
            {
                api.World.Logger.Warning("[Caveshrooms] ConfigLib is enabled but GetConfig(\"caveshrooms\") returned null - configlib-patches.json may have failed to load. See ConfigLib's own log lines above for the parse error.");
                return;
            }

            SeedConfigLibFromConfig(libConfig, Config);

            configLib.SettingChanged += (domain, _, _) =>
            {
                if (domain != "caveshrooms") return;
                PullConfigLibIntoConfig(libConfig, Config);
                api.StoreModConfig(Config, ConfigFilename);
            };
        }

        // Pushes every CaveshroomsConfig field's current value onto its matching ConfigLib
        // setting, then writes them out - the GUI/YAML immediately reflect real current values
        // instead of configlib-patches.json's defaults, even before an admin touches anything.
        // Called before HookConfigLib subscribes to configLib.SettingChanged above specifically so
        // this seeding pass can't immediately re-trigger our own handler (mirrors TheUnknowing's
        // SeedConfigLibFromConfig for the same reason).
        private static void SeedConfigLibFromConfig(IConfig libConfig, CaveshroomsConfig config)
        {
            SetIfPresent(libConfig, "STABILITY_LOSS_PER_PSYCHEDELIC", config.StabilityLossPerPsychedelic);
            SetIfPresent(libConfig, "GLOW_GAIN_PER_PSYCHEDELIC", config.GlowGainPerPsychedelic);
            SetIfPresent(libConfig, "MAX_GLOW", config.MaxGlow);
            SetIfPresent(libConfig, "GLOW_DECAY_PER_HOUR", config.GlowDecayPerInGameHour);
            SetIfPresent(libConfig, "GLOW_HUE", config.GlowHue);
            SetIfPresent(libConfig, "GLOW_SATURATION", config.GlowSaturation);

            NutritionTuning n = config.Nutrition;
            SetIfPresent(libConfig, "RAW_SATIETY", n.RawSatiety);
            SetIfPresent(libConfig, "RAW_HEALTH", n.RawHealth);
            SetIfPresent(libConfig, "RAW_PSYCHEDELIC", n.RawPsychedelic);
            SetIfPresent(libConfig, "PARTBAKED_SATIETY_MULT", n.PartBaked.SatietyMultiplier);
            SetIfPresent(libConfig, "PARTBAKED_HEALTH_MULT", n.PartBaked.HealthMultiplier);
            SetIfPresent(libConfig, "PARTBAKED_PSYCHEDELIC_MULT", n.PartBaked.PsychedelicMultiplier);
            SetIfPresent(libConfig, "PERFECT_SATIETY_MULT", n.Perfect.SatietyMultiplier);
            SetIfPresent(libConfig, "PERFECT_HEALTH_MULT", n.Perfect.HealthMultiplier);
            SetIfPresent(libConfig, "PERFECT_PSYCHEDELIC_MULT", n.Perfect.PsychedelicMultiplier);
            SetIfPresent(libConfig, "CHARRED_SATIETY_MULT", n.Charred.SatietyMultiplier);
            SetIfPresent(libConfig, "CHARRED_HEALTH_MULT", n.Charred.HealthMultiplier);
            SetIfPresent(libConfig, "CHARRED_PSYCHEDELIC_MULT", n.Charred.PsychedelicMultiplier);
            SetIfPresent(libConfig, "PIE_FILLING_SATIETY", n.PieFillingSatiety);

            PerishTuning p = config.Perish;
            SetIfPresent(libConfig, "RAW_FRESH_HOURS", p.RawFreshHours);
            SetIfPresent(libConfig, "RAW_TRANSITION_HOURS", p.RawTransitionHours);
            SetIfPresent(libConfig, "COOKED_FRESH_HOURS", p.CookedFreshHours);
            SetIfPresent(libConfig, "COOKED_TRANSITION_HOURS", p.CookedTransitionHours);
            SetIfPresent(libConfig, "CHARRED_FRESH_HOURS", p.CharredFreshHours);
            SetIfPresent(libConfig, "CHARRED_TRANSITION_HOURS", p.CharredTransitionHours);

            HarvestTuning h = config.Harvest;
            SetIfPresent(libConfig, "HARVEST_QUANTITY_AVG", h.QuantityAvg);
            SetIfPresent(libConfig, "HARVEST_QUANTITY_VAR", h.QuantityVar);
            SetIfPresent(libConfig, "SUBSTRATE_DROP_CHANCE", h.SubstrateDropChance);

            GrowthGlowTuning g = config.GrowthGlow;
            SetIfPresent(libConfig, "GLOW_EMPTY_SATURATION", g.Empty.Saturation);
            SetIfPresent(libConfig, "GLOW_EMPTY_VALUE", g.Empty.Value);
            SetIfPresent(libConfig, "GLOW_FLOWERING_SATURATION", g.Flowering.Saturation);
            SetIfPresent(libConfig, "GLOW_FLOWERING_VALUE", g.Flowering.Value);
            SetIfPresent(libConfig, "GLOW_RIPE_SATURATION", g.Ripe.Saturation);
            SetIfPresent(libConfig, "GLOW_RIPE_VALUE", g.Ripe.Value);

            WorldgenTuning w = config.Worldgen;
            SetIfPresent(libConfig, "WORLDGEN_CHANCE", w.Chance);
            SetIfPresent(libConfig, "WORLDGEN_QUANTITY_AVG", w.QuantityAvg);
            SetIfPresent(libConfig, "WORLDGEN_QUANTITY_VAR", w.QuantityVar);
            SetIfPresent(libConfig, "WORLDGEN_MAX_Y", w.MaxY);

            libConfig.WriteToFile();
        }

        private static void SetIfPresent(IConfig libConfig, string code, object value)
        {
            ISetting? setting = libConfig.GetSetting(code);
            if (setting == null) return;
            setting.Value = new JsonObject(JToken.FromObject(value));
        }

        // Reads each setting explicitly by code and expected type, rather than
        // IConfig.AssignSettingsValues' reflection-based field-name matching - that helper
        // assigns via Value.AsFloat()/AsInt() straight onto the target property via a raw
        // PropertyInfo.SetValue, which throws for StabilityLossPerPsychedelic (it's double, and
        // reflection won't auto-widen a boxed float into that) and can't reach any of the nested
        // Nutrition/Perish/Harvest/GrowthGlow/Worldgen properties at all. Falls back to each
        // field's current value if the specific setting isn't found (shouldn't happen once
        // SeedConfigLibFromConfig has run, but GetSetting is nullable).
        private static void PullConfigLibIntoConfig(IConfig libConfig, CaveshroomsConfig config)
        {
            config.StabilityLossPerPsychedelic = ReadDouble(libConfig, "STABILITY_LOSS_PER_PSYCHEDELIC", config.StabilityLossPerPsychedelic);
            config.GlowGainPerPsychedelic = ReadFloat(libConfig, "GLOW_GAIN_PER_PSYCHEDELIC", config.GlowGainPerPsychedelic);
            config.MaxGlow = ReadFloat(libConfig, "MAX_GLOW", config.MaxGlow);
            config.GlowDecayPerInGameHour = ReadFloat(libConfig, "GLOW_DECAY_PER_HOUR", config.GlowDecayPerInGameHour);
            config.GlowHue = ReadInt(libConfig, "GLOW_HUE", config.GlowHue);
            config.GlowSaturation = ReadInt(libConfig, "GLOW_SATURATION", config.GlowSaturation);

            NutritionTuning n = config.Nutrition;
            n.RawSatiety = ReadFloat(libConfig, "RAW_SATIETY", n.RawSatiety);
            n.RawHealth = ReadFloat(libConfig, "RAW_HEALTH", n.RawHealth);
            n.RawPsychedelic = ReadFloat(libConfig, "RAW_PSYCHEDELIC", n.RawPsychedelic);
            n.PartBaked.SatietyMultiplier = ReadFloat(libConfig, "PARTBAKED_SATIETY_MULT", n.PartBaked.SatietyMultiplier);
            n.PartBaked.HealthMultiplier = ReadFloat(libConfig, "PARTBAKED_HEALTH_MULT", n.PartBaked.HealthMultiplier);
            n.PartBaked.PsychedelicMultiplier = ReadFloat(libConfig, "PARTBAKED_PSYCHEDELIC_MULT", n.PartBaked.PsychedelicMultiplier);
            n.Perfect.SatietyMultiplier = ReadFloat(libConfig, "PERFECT_SATIETY_MULT", n.Perfect.SatietyMultiplier);
            n.Perfect.HealthMultiplier = ReadFloat(libConfig, "PERFECT_HEALTH_MULT", n.Perfect.HealthMultiplier);
            n.Perfect.PsychedelicMultiplier = ReadFloat(libConfig, "PERFECT_PSYCHEDELIC_MULT", n.Perfect.PsychedelicMultiplier);
            n.Charred.SatietyMultiplier = ReadFloat(libConfig, "CHARRED_SATIETY_MULT", n.Charred.SatietyMultiplier);
            n.Charred.HealthMultiplier = ReadFloat(libConfig, "CHARRED_HEALTH_MULT", n.Charred.HealthMultiplier);
            n.Charred.PsychedelicMultiplier = ReadFloat(libConfig, "CHARRED_PSYCHEDELIC_MULT", n.Charred.PsychedelicMultiplier);
            n.PieFillingSatiety = ReadFloat(libConfig, "PIE_FILLING_SATIETY", n.PieFillingSatiety);

            PerishTuning p = config.Perish;
            p.RawFreshHours = ReadFloat(libConfig, "RAW_FRESH_HOURS", p.RawFreshHours);
            p.RawTransitionHours = ReadFloat(libConfig, "RAW_TRANSITION_HOURS", p.RawTransitionHours);
            p.CookedFreshHours = ReadFloat(libConfig, "COOKED_FRESH_HOURS", p.CookedFreshHours);
            p.CookedTransitionHours = ReadFloat(libConfig, "COOKED_TRANSITION_HOURS", p.CookedTransitionHours);
            p.CharredFreshHours = ReadFloat(libConfig, "CHARRED_FRESH_HOURS", p.CharredFreshHours);
            p.CharredTransitionHours = ReadFloat(libConfig, "CHARRED_TRANSITION_HOURS", p.CharredTransitionHours);

            HarvestTuning h = config.Harvest;
            h.QuantityAvg = ReadFloat(libConfig, "HARVEST_QUANTITY_AVG", h.QuantityAvg);
            h.QuantityVar = ReadFloat(libConfig, "HARVEST_QUANTITY_VAR", h.QuantityVar);
            h.SubstrateDropChance = ReadFloat(libConfig, "SUBSTRATE_DROP_CHANCE", h.SubstrateDropChance);

            GrowthGlowTuning g = config.GrowthGlow;
            g.Empty.Saturation = ReadInt(libConfig, "GLOW_EMPTY_SATURATION", g.Empty.Saturation);
            g.Empty.Value = ReadInt(libConfig, "GLOW_EMPTY_VALUE", g.Empty.Value);
            g.Flowering.Saturation = ReadInt(libConfig, "GLOW_FLOWERING_SATURATION", g.Flowering.Saturation);
            g.Flowering.Value = ReadInt(libConfig, "GLOW_FLOWERING_VALUE", g.Flowering.Value);
            g.Ripe.Saturation = ReadInt(libConfig, "GLOW_RIPE_SATURATION", g.Ripe.Saturation);
            g.Ripe.Value = ReadInt(libConfig, "GLOW_RIPE_VALUE", g.Ripe.Value);

            WorldgenTuning w = config.Worldgen;
            w.Chance = ReadFloat(libConfig, "WORLDGEN_CHANCE", w.Chance);
            w.QuantityAvg = ReadFloat(libConfig, "WORLDGEN_QUANTITY_AVG", w.QuantityAvg);
            w.QuantityVar = ReadFloat(libConfig, "WORLDGEN_QUANTITY_VAR", w.QuantityVar);
            w.MaxY = ReadFloat(libConfig, "WORLDGEN_MAX_Y", w.MaxY);
        }

        private static double ReadDouble(IConfig libConfig, string code, double fallback) =>
            libConfig.GetSetting(code)?.Value.AsDouble(fallback) ?? fallback;

        private static float ReadFloat(IConfig libConfig, string code, float fallback) =>
            libConfig.GetSetting(code)?.Value.AsFloat(fallback) ?? fallback;

        private static int ReadInt(IConfig libConfig, string code, int fallback) =>
            libConfig.GetSetting(code)?.Value.AsInt(fallback) ?? fallback;
    }
}
