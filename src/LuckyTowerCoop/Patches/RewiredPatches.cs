using HarmonyLib;
using LuckyTowerCoop.Coop;
using Rewired;

namespace LuckyTowerCoop.Patches
{
    /// <summary>Подмешивание скриптового/сетевого ввода к конкретному Rewired-игроку (OR с устройством).</summary>
    [HarmonyPatch(typeof(Player), nameof(Player.GetButton), typeof(int))]
    internal static class RewiredGetButtonPatch
    {
        private static void Postfix(Player __instance, int actionId, ref bool __result)
        {
            if (!__result && InputOverrides.Any) { __result = InputOverrides.GetButton(__instance.id, actionId); }
        }
    }

    [HarmonyPatch(typeof(Player), nameof(Player.GetButtonDown), typeof(int))]
    internal static class RewiredGetButtonDownPatch
    {
        private static void Postfix(Player __instance, int actionId, ref bool __result)
        {
            if (!__result && InputOverrides.Any) { __result = InputOverrides.GetButtonDown(__instance.id, actionId); }
        }
    }

    [HarmonyPatch(typeof(Player), nameof(Player.GetButtonUp), typeof(int))]
    internal static class RewiredGetButtonUpPatch
    {
        private static void Postfix(Player __instance, int actionId, ref bool __result)
        {
            if (!__result && InputOverrides.Any) { __result = InputOverrides.GetButtonUp(__instance.id, actionId); }
        }
    }
}
