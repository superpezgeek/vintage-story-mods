using System;
using System.Text;
using Newtonsoft.Json.Linq;
using Vintagestory.API.Common;

namespace Caveshrooms
{
    // Rewrites the raw JSON bytes of two of our own assets in memory during ModSystem.AssetsLoaded,
    // before the engine's block/item loader (blocktypes/itemtypes, ExecuteOrder 0.2) or VSEssentials'
    // worldgen block-patch reader (which runs even later, lazily, at world-gen init) parse them - so
    // both end up reading our config-computed numbers instead of the shipped JSON defaults. This is
    // the same mechanism the engine's own JsonPatchLoader uses for jsonpatches (IAsset.Data +
    // IsPatched), just applied imperatively here instead of declaratively.
    //
    // Every mutation is wrapped in try/catch: on any failure (including a structural change to
    // either JSON file that breaks one of the lookups below), we log a warning and leave that asset
    // untouched, so a problem here can only lose that one piece of config-driven tuning - it can
    // never break asset loading for the mod as a whole again.
    public static class CaveshroomsAssetTuning
    {
        public static void RewriteBlocktypeAsset(ICoreAPI api, CaveshroomsConfig config)
        {
            AssetLocation loc = new AssetLocation("caveshrooms", "blocktypes/plant/caveshroom.json");
            try
            {
                IAsset? asset = api.Assets.TryGet(loc);
                if (asset == null)
                {
                    api.World.Logger.Warning("[Caveshrooms] Could not find asset {0}; harvest quantity and growth-stage glow will use shipped JSON defaults instead of config.", loc);
                    return;
                }

                JObject root = JObject.Parse(asset.ToText());

                JToken? harvestQuantity = root.SelectToken("behaviorsByType['*-ripe'][0].properties.harvestedStack.quantity");
                JToken? dropQuantity = root.SelectToken("dropsByType['*-ripe'][0].quantity");
                if (harvestQuantity == null || dropQuantity == null)
                {
                    api.World.Logger.Warning("[Caveshrooms] {0} does not have the expected harvestedStack/dropsByType structure; leaving harvest quantity as shipped.", loc);
                }
                else
                {
                    harvestQuantity.Replace(new JObject { ["avg"] = config.Harvest.QuantityAvg, ["var"] = config.Harvest.QuantityVar });
                    dropQuantity.Replace(new JObject { ["avg"] = config.Harvest.QuantityAvg, ["var"] = config.Harvest.QuantityVar });
                }

                // Substrate block-item drop chance on breaking - present on all three growth
                // stages. A fractional quantity avg (var 0) IS the drop-chance mechanism, per
                // BlockDropItemStack - see HarvestTuning.SubstrateDropChance.
                RewriteSubstrateDropChance(api, root, loc, "dropsByType['*-empty'][0].quantity", "empty", config.Harvest.SubstrateDropChance);
                RewriteSubstrateDropChance(api, root, loc, "dropsByType['*-flowering'][0].quantity", "flowering", config.Harvest.SubstrateDropChance);
                RewriteSubstrateDropChance(api, root, loc, "dropsByType['*-ripe'][1].quantity", "ripe", config.Harvest.SubstrateDropChance);

                RewriteGlowStage(api, root, loc, "lightHsvByType['*-empty']", "empty", config.GlowHue, config.GrowthGlow.Empty);
                RewriteGlowStage(api, root, loc, "lightHsvByType['*-flowering']", "flowering", config.GlowHue, config.GrowthGlow.Flowering);
                RewriteGlowStage(api, root, loc, "lightHsvByType['*-ripe']", "ripe", config.GlowHue, config.GrowthGlow.Ripe);

                asset.Data = Encoding.UTF8.GetBytes(root.ToString());
                asset.IsPatched = true;
            }
            catch (Exception e)
            {
                api.World.Logger.Warning("[Caveshrooms] Failed to apply config-driven harvest/glow tuning to {0}; falling back to shipped JSON defaults. {1}", loc, e);
            }
        }

        private static void RewriteGlowStage(ICoreAPI api, JObject root, AssetLocation loc, string path, string stageName, int hue, GlowStageValue stage)
        {
            JToken? token = root.SelectToken(path);
            if (token == null)
            {
                api.World.Logger.Warning("[Caveshrooms] {0} missing '{1}'; leaving the {2} growth stage's glow as shipped.", loc, path, stageName);
                return;
            }
            token.Replace(new JArray(hue, stage.Saturation, stage.Value));
        }

        private static void RewriteSubstrateDropChance(ICoreAPI api, JObject root, AssetLocation loc, string path, string stageName, float chance)
        {
            JToken? token = root.SelectToken(path);
            if (token == null)
            {
                api.World.Logger.Warning("[Caveshrooms] {0} missing '{1}'; leaving the {2} growth stage's substrate drop chance as shipped.", loc, path, stageName);
                return;
            }
            token.Replace(new JObject { ["avg"] = chance, ["var"] = 0 });
        }

        public static void RewriteWorldgenAsset(ICoreAPI api, CaveshroomsConfig config)
        {
            AssetLocation loc = new AssetLocation("caveshrooms", "worldgen/blockpatches/caveshrooms.json");
            try
            {
                IAsset? asset = api.Assets.TryGet(loc);
                if (asset == null)
                {
                    api.World.Logger.Warning("[Caveshrooms] Could not find asset {0}; worldgen spawn tuning will use shipped JSON defaults instead of config.", loc);
                    return;
                }

                JArray root = JArray.Parse(asset.ToText());
                if (root.Count == 0 || root[0] is not JObject patch)
                {
                    api.World.Logger.Warning("[Caveshrooms] {0} did not contain the expected block-patch array; leaving worldgen tuning as shipped.", loc);
                    return;
                }

                WorldgenTuning w = config.Worldgen;
                patch["chance"] = w.Chance;
                patch["quantity"] = new JObject { ["avg"] = w.QuantityAvg, ["var"] = w.QuantityVar };
                patch["maxY"] = w.MaxY;

                asset.Data = Encoding.UTF8.GetBytes(root.ToString());
                asset.IsPatched = true;
            }
            catch (Exception e)
            {
                api.World.Logger.Warning("[Caveshrooms] Failed to apply config-driven worldgen tuning to {0}; falling back to shipped JSON defaults. {1}", loc, e);
            }
        }
    }
}
