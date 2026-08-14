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
        // CollectibleObject.OnHeldInteractStop fires on ANY release, not just a completed eat,
        // despite what its own doc comment implies - vanilla's tryEatStop only actually grants
        // nutrition (and consumes the item) if secondsUsed >= 0.95f, discarding a too-quick
        // tap-and-release. Match that exact threshold here too, or a quick tap would drain
        // stability/add glow without ever actually eating the mushroom.
        private const float MinSecondsUsedToEat = 0.95f;

        public CollectibleBehaviorTemporalEffect(CollectibleObject collObj) : base(collObj)
        {
        }

        public override void OnHeldInteractStop(float secondsUsed, ItemSlot slot, EntityAgent byEntity, BlockSelection blockSel, EntitySelection entitySel, ref EnumHandling handling)
        {
            base.OnHeldInteractStop(secondsUsed, slot, byEntity, blockSel, entitySel, ref handling);

            // Leave `handling` as PassThrough - the base CollectibleObject still needs to run
            // its own tryEatStop() afterwards to apply satiety/health as normal.
            if (byEntity.World.Side != EnumAppSide.Server) return;
            if (secondsUsed < MinSecondsUsedToEat) return;

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

            // Lifetime count, doesn't decay (unlike temporalGlow above) - counts any genuine eat
            // of any of the four edible forms, since all of them carry this same behavior and a
            // positive Psychedelic value. No milestone/threshold behavior yet - just the count.
            int eatenCount = byEntity.WatchedAttributes.GetInt("caveshrooms:temporalMushroomsEaten", 0) + 1;
            byEntity.WatchedAttributes.SetInt("caveshrooms:temporalMushroomsEaten", eatenCount);
            byEntity.WatchedAttributes.MarkPathDirty("caveshrooms:temporalMushroomsEaten");
        }
    }
}
