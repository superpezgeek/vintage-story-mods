using System;
using HarmonyLib;
using Vintagestory.API.Common;

namespace Caveshrooms
{
    // Mirrors Alchemy's own EntityPlayerPatch (glow potion) approach - EntityPlayer.LightHsv
    // isn't virtual, so Harmony is the standard way mods make a player emit light.
    // Hue/saturation come from CaveshroomsModSystem.Config (defaults to our established teal,
    // hue 32) rather than being hardcoded here.
    [HarmonyPatch(typeof(EntityPlayer), "LightHsv", MethodType.Getter)]
    public static class EntityPlayerGlowPatch
    {
        public static void Postfix(EntityPlayer __instance, ref byte[] __result)
        {
            float glow = __instance.WatchedAttributes.GetFloat("caveshrooms:temporalGlow", 0f);
            if (glow <= 0f) return;

            CaveshroomsConfig config = CaveshroomsModSystem.Config;
            byte value = (byte)Math.Clamp((int)Math.Round(glow), 0, 22);
            __result = new byte[] { (byte)config.GlowHue, (byte)config.GlowSaturation, value };
        }
    }
}
