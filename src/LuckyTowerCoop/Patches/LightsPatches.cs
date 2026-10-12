using HarmonyLib;
using LuckyTowerCoop.Net;

namespace LuckyTowerCoop.Patches
{
    /// <summary>Ловушка «свет погас»: хост сообщает клиенту включение/выключение темноты; на клиенте локальные вызовы заглушены.</summary>
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