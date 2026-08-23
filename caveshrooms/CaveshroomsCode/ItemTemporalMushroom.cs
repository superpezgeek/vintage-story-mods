using Newtonsoft.Json.Linq;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.MathTools;

namespace Caveshrooms
{
    // Custom Item class for all four edible Temporal Mushroom itemtypes (raw whole, raw chopped,
    // cooked whole, cooked chopped) - assigned via "class": "Caveshrooms.ItemTemporalMushroom" in
    // each itemtype JSON, which no longer sets nutritionProps/transitionableProps itself. Overrides
    // the two CollectibleObject virtuals the vanilla eating/perish/handbook code already calls
    // (GetNutritionProperties, GetTransitionableProperties) so both compute from
    // CaveshroomsModSystem.Config instead. Independent of, and unaffected by, the
    // CaveshroomsTemporalEffect behavior - behaviors run alongside the base class, not through it.
    public class ItemTemporalMushroom : Item
    {
        private TransitionableProperties[]? cachedTransitionProps;

        public override void OnLoaded(ICoreAPI api)
        {
            base.OnLoaded(api);

            cachedTransitionProps = BuildTransitionableProperties(api.World);

            // Only choppedtemporalmushroom-item declares inPieProperties in JSON - that's the
            // gate for patching its nutritionPropsWhenInMeal, since pie filling nutrition is read
            // directly off Attributes by vanilla's pie code rather than through a virtual method.
            if (Attributes?.Token is JObject attributesObj && attributesObj["inPieProperties"] != null)
            {
                NutritionTuning n = CaveshroomsModSystem.Config.Nutrition;
                attributesObj["nutritionPropsWhenInMeal"] = JObject.FromObject(new
                {
                    satiety = n.PieFillingSatiety,
                    foodcategory = "Vegetable",
                    health = n.RawHealth,
                    psychedelic = n.RawPsychedelic
                });
            }
        }

        public override FoodNutritionProperties GetNutritionProperties(IWorldAccessor world, ItemStack itemstack, Entity forEntity)
        {
            NutritionTuning n = CaveshroomsModSystem.Config.Nutrition;
            // Use this.Variant rather than itemstack.Collectible.Variant - ACulinaryArtillery's
            // pie-filling cache (BlockPiePatches.OnLoadedPrefix) probes nutrition props during mod
            // load with itemstacks whose Collectible isn't resolved, which NREs on that path.
            CookStageMultiplier? mul = CookMultiplierFor(Variant["state"], n);

            return new FoodNutritionProperties
            {
                FoodCategory = EnumFoodCategory.Vegetable,
                Satiety = n.RawSatiety * (mul?.SatietyMultiplier ?? 1f),
                Health = n.RawHealth * (mul?.HealthMultiplier ?? 1f),
                Psychedelic = n.RawPsychedelic * (mul?.PsychedelicMultiplier ?? 1f)
            };
        }

        public override TransitionableProperties[] GetTransitionableProperties(IWorldAccessor world, ItemStack itemstack, Entity forEntity)
        {
            return cachedTransitionProps ?? base.GetTransitionableProperties(world, itemstack, forEntity);
        }

        private static CookStageMultiplier? CookMultiplierFor(string? state, NutritionTuning n) => state switch
        {
            "partbaked" => n.PartBaked,
            "perfect" => n.Perfect,
            "charred" => n.Charred,
            _ => null // raw whole/chopped - no "state" variantgroup
        };

        private TransitionableProperties[] BuildTransitionableProperties(IWorldAccessor world)
        {
            PerishTuning p = CaveshroomsModSystem.Config.Perish;
            string? state = Variant["state"];
            (float fresh, float transition) = state switch
            {
                "charred" => (p.CharredFreshHours, p.CharredTransitionHours),
                "partbaked" or "perfect" => (p.CookedFreshHours, p.CookedTransitionHours),
                _ => (p.RawFreshHours, p.RawTransitionHours)
            };

            TransitionableProperties props = new TransitionableProperties
            {
                Type = EnumTransitionType.Perish,
                FreshHours = NatFloat.createUniform(fresh, 0f),
                TransitionHours = NatFloat.createUniform(transition, 0f),
                TransitionedStack = new JsonItemStack { Type = EnumItemClass.Item, Code = new AssetLocation("game", "rot") }
            };
            props.TransitionedStack.Resolve(world, $"Caveshrooms perish transition ({Code})");

            return new[] { props };
        }
    }
}
