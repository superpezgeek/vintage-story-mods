namespace Caveshrooms
{
    // Loaded from VintagestoryData/ModConfig/Caveshrooms.json (created with these defaults on
    // first run if missing) - lets server admins retune the mod without rebuilding it. Single
    // source of truth for values that used to be duplicated across multiple JSON asset files
    // (or, for the temporal-effect fields, hardcoded C# constants). Every default below is
    // copied verbatim from what shipped in the JSON/constants it replaced, so a fresh config
    // reproduces the exact same balance until an admin edits it.
    public class CaveshroomsConfig
    {
        public double StabilityLossPerPsychedelic { get; set; } = 0.05;
        public float GlowGainPerPsychedelic { get; set; } = 2.5f;
        public float MaxGlow { get; set; } = 20f;

        // Full MaxGlow fades to nothing over this many in-game hours if the player stops eating.
        public float GlowDecayPerInGameHour { get; set; } = 20f / 8f;

        // Kept at hue 32 by default to match the game's own Temporal Gear. Real usable
        // saturation range is 0-7. Shared by player glow and, via GrowthGlow below, block glow -
        // one value keeps both in sync.
        public int GlowHue { get; set; } = 32;
        public int GlowSaturation { get; set; } = 6;

        public NutritionTuning Nutrition { get; set; } = new NutritionTuning();
        public PerishTuning Perish { get; set; } = new PerishTuning();
        public HarvestTuning Harvest { get; set; } = new HarvestTuning();
        public GrowthGlowTuning GrowthGlow { get; set; } = new GrowthGlowTuning();
        public WorldgenTuning Worldgen { get; set; } = new WorldgenTuning();
    }

    public class NutritionTuning
    {
        // Raw base values, shared by raw whole and raw chopped mushroom.
        public float RawSatiety { get; set; } = 80f;
        public float RawHealth { get; set; } = -1f;
        public float RawPsychedelic { get; set; } = 0.8f;

        // Per-cook-stage multipliers applied to the raw values above. Not one shared "falloff %" -
        // satiety actually rises while cooking (80 -> 90/110/100) even as health/psychedelic fall
        // (75%/50%/25%), so each stage needs its own three independent multipliers.
        public CookStageMultiplier PartBaked { get; set; } = new CookStageMultiplier(90f / 80f, 0.75f, 0.75f);
        public CookStageMultiplier Perfect { get; set; } = new CookStageMultiplier(110f / 80f, 0.5f, 0.5f);
        public CookStageMultiplier Charred { get; set; } = new CookStageMultiplier(100f / 80f, 0.25f, 0.25f);

        // Pie-filling satiety (raw chopped mushroom only). Doesn't derive from RawSatiety - pie
        // satiety (150) has never matched direct-eating satiety (80). Health/psychedelic reuse
        // the raw values in pie filling by design (kept at full strength - see README).
        public float PieFillingSatiety { get; set; } = 150f;
    }

    public class CookStageMultiplier
    {
        public float SatietyMultiplier { get; set; }
        public float HealthMultiplier { get; set; }
        public float PsychedelicMultiplier { get; set; }

        public CookStageMultiplier() { }

        public CookStageMultiplier(float satietyMultiplier, float healthMultiplier, float psychedelicMultiplier)
        {
            SatietyMultiplier = satietyMultiplier;
            HealthMultiplier = healthMultiplier;
            PsychedelicMultiplier = psychedelicMultiplier;
        }
    }

    public class PerishTuning
    {
        public float RawFreshHours { get; set; } = 96f;
        public float RawTransitionHours { get; set; } = 24f;

        // Shared by partbaked + perfect.
        public float CookedFreshHours { get; set; } = 432f;
        public float CookedTransitionHours { get; set; } = 72f;

        public float CharredFreshHours { get; set; } = 1512f;
        public float CharredTransitionHours { get; set; } = 120f;
    }

    public class HarvestTuning
    {
        // Written to both the Harvestable behavior's harvestedStack and the block's own
        // dropsByType, so right-click-harvesting and breaking always agree.
        public float QuantityAvg { get; set; } = 2.5f;
        public float QuantityVar { get; set; } = 0.5f;

        // Chance (0-1) that breaking the block (any growth stage) also drops a relocatable
        // Unstable Substrate block-item, on top of any mushroom item yield above. Only applies
        // to breaking - right-click harvesting a ripe mushroom always leaves the substrate
        // planted in place via exchangeBlock, which isn't a "drop" at all. Expressed to the
        // engine as a NatFloat with this as its avg and 0 variance - VS has no separate
        // drop-chance field, a fractional quantity average IS the chance mechanism (see
        // BlockDropItemStack.GetNextItemStack -> GameMath.RoundRandom).
        public float SubstrateDropChance { get; set; } = 0.5f;
    }

    public class GrowthGlowTuning
    {
        // Hue intentionally not duplicated here - reuses CaveshroomsConfig.GlowHue.
        public GlowStageValue Empty { get; set; } = new GlowStageValue(3, 3);
        public GlowStageValue Flowering { get; set; } = new GlowStageValue(5, 7);
        public GlowStageValue Ripe { get; set; } = new GlowStageValue(7, 12);
    }

    public class GlowStageValue
    {
        public int Saturation { get; set; }
        public int Value { get; set; }

        public GlowStageValue() { }

        public GlowStageValue(int saturation, int value)
        {
            Saturation = saturation;
            Value = value;
        }
    }

    public class WorldgenTuning
    {
        public float Chance { get; set; } = 12f;
        public float QuantityAvg { get; set; } = 2f;
        public float QuantityVar { get; set; } = 1f;
        public float MaxY { get; set; } = 0.35f;
    }
}
