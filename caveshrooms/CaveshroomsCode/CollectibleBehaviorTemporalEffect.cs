using System;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Datastructures;
using Vintagestory.GameContent;

namespace Caveshrooms
{
    // Applied to every edible Temporal Mushroom item (raw, chopped, cooked, cooked-chopped).
    // Scales stability loss and glow gain off the eaten stack's own Psychedelic nutrition value,
    // so cooked/charred forms (which already carry a reduced Psychedelic value) automatically
    // cause a proportionally smaller effect without needing separate per-item config.
    public class CollectibleBehaviorTemporalEffect : CollectibleBehavior
    {
        private double stabilityLossPerPsychedelic;
        private float glowGainPerPsychedelic;
        private float maxGlow;

        public CollectibleBehaviorTemporalEffect(CollectibleObject collObj) : base(collObj)
        {
        }

        public override void Initialize(JsonObject properties)
        {
            base.Initialize(properties);
            stabilityLossPerPsychedelic = properties["stabilityLossPerPsychedelic"].AsDouble(0.05);
            glowGainPerPsychedelic = properties["glowGainPerPsychedelic"].AsFloat(2.5f);
            maxGlow = properties["maxGlow"].AsFloat(20f);
        }

        public override void OnHeldInteractStop(float secondsUsed, ItemSlot slot, EntityAgent byEntity, BlockSelection blockSel, EntitySelection entitySel, ref EnumHandling handling)
        {
            base.OnHeldInteractStop(secondsUsed, slot, byEntity, blockSel, entitySel, ref handling);

            // Leave `handling` as PassThrough - the base CollectibleObject still needs to run
            // its own tryEatStop() afterwards to apply satiety/health as normal.
            if (byEntity.World.Side != EnumAppSide.Server) return;

            ItemStack? stack = slot.Itemstack;
            if (stack == null) return;

            float psychedelic = stack.Collectible.GetNutritionProperties(byEntity.World, stack, byEntity)?.Psychedelic ?? 0f;
            if (psychedelic <= 0f) return;

            EntityBehaviorTemporalStabilityAffected? stabilityBehavior = byEntity.GetBehavior<EntityBehaviorTemporalStabilityAffected>();
            if (stabilityBehavior != null)
            {
                stabilityBehavior.OwnStability = Math.Max(0.0, stabilityBehavior.OwnStability - psychedelic * stabilityLossPerPsychedelic);
            }

            float currentGlow = byEntity.WatchedAttributes.GetFloat("caveshrooms:temporalGlow", 0f);
            float newGlow = Math.Min(maxGlow, currentGlow + psychedelic * glowGainPerPsychedelic);
            byEntity.WatchedAttributes.SetFloat("caveshrooms:temporalGlow", newGlow);
            byEntity.WatchedAttributes.MarkPathDirty("caveshrooms:temporalGlow");
        }
    }
}
