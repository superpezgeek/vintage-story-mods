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

        // Full 20 glow fades to nothing over ~8 in-game hours if you stop eating (tracked via
        // the world calendar, not real time, so /time add and calendar speed changes both
        // affect it correctly). Unvalidated starting guess - tune if it feels too fast/slow.
        private const float GlowDecayPerInGameHour = 20f / 8f;

        private Harmony? harmony;
        private double lastCalendarTotalHours = double.NaN;

        public override void Start(ICoreAPI api)
        {
            base.Start(api);

            api.RegisterCollectibleBehaviorClass("CaveshroomsTemporalEffect", typeof(CollectibleBehaviorTemporalEffect));

            if (!Harmony.HasAnyPatches(HarmonyId))
            {
                harmony = new Harmony(HarmonyId);
                harmony.PatchAll(Assembly.GetExecutingAssembly());
            }
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

            float decayAmount = (float)(GlowDecayPerInGameHour * elapsedHours);

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
                        $"Temporal glow: {glow:0.0} / 20\n" +
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
