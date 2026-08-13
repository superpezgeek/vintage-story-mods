using System;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.GameContent;

namespace Caveshrooms
{
    // Applied to every edible Temporal Mushroom item (raw, chopped, cooked, cooked-chopped).
    // Scales stability loss and glow gain off the eaten stack's own Psychedelic nutrition value,
    // so cooked/charred forms (which already carry a reduced Psychedelic value) automatically
    // cause a proportionally smaller effect. Strength values come from CaveshroomsModSystem.Config
    // (one shared config, not per-item JSON) so a server admin can retune eating all four forms
    // at once without a rebuild.
    public class CollectibleBehaviorTemporalEffect : CollectibleBehavior
    {
        public CollectibleBehaviorTemporalEffect(CollectibleObject collObj) : base(collObj)
        {
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

            CaveshroomsConfig config = CaveshroomsModSystem.Config;

            EntityBehaviorTemporalStabilityAffected? stabilityBehavior = byEntity.GetBehavior<EntityBehaviorTemporalStabilityAffected>();
            if (stabilityBehavior != null)
            {
                stabilityBehavior.OwnStability = Math.Max(0.0, stabilityBehavior.OwnStability - psychedelic * config.StabilityLossPerPsychedelic);
            }

            float currentGlow = byEntity.WatchedAttributes.GetFloat("caveshrooms:temporalGlow", 0f);
            float newGlow = Math.Min(config.MaxGlow, currentGlow + psychedelic * config.GlowGainPerPsychedelic);
            byEntity.WatchedAttributes.SetFloat("caveshrooms:temporalGlow", newGlow);
            byEntity.WatchedAttributes.MarkPathDirty("caveshrooms:temporalGlow");
        }
    }
}
