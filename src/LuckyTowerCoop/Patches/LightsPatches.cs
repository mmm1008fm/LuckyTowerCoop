using HarmonyLib;
using LuckyTowerCoop.Net;

namespace LuckyTowerCoop.Patches
{
    /// <summary>"Lights out" trap: the host tells the client when darkness turns on/off; local calls are suppressed on the client.</summary>
    [HarmonyPatch(typeof(LuckyTower.Traps.TrapLightsOut))]
    internal static class LightsPatches
    {
        [HarmonyPatch("LightsOut")]
        [HarmonyPrefix]
        private static bool LightsOutPrefix() => !NetSession.IsClient || NetClient.ApplyingLights;

        [HarmonyPatch("LightsOut")]
        [HarmonyPostfix]
        private static void LightsOutPostfix(LuckyTower.Traps.TrapLightsOut __instance)
        {
            if (NetSession.IsHost)
            {
                NetSession.Instance?.Host?.SendLights(__instance, true, false);
            }
        }

        [HarmonyPatch("OnPlayerExits")]
        [HarmonyPrefix]
        private static bool LightsOnPrefix() => !NetSession.IsClient || NetClient.ApplyingLights;

        [HarmonyPatch("OnPlayerExits")]
        [HarmonyPostfix]
        private static void LightsOnPostfix(LuckyTower.Traps.TrapLightsOut __instance, bool animateRelightHandlers)
        {
            if (NetSession.IsHost)
            {
                NetSession.Instance?.Host?.SendLights(__instance, false, !animateRelightHandlers);
            }
        }
    }
}