using HarmonyLib;
using LuckyTowerCoop.Net;

namespace LuckyTowerCoop.Patches
{
    /// <summary>
    /// Tower explosion by a powder keg (trap keg and a held keg): the host plays the cutscene and tells the client,
    /// the client plays the same cutscene itself (its own DestroyTower does not run). Death arrives the usual way.
    /// </summary>
    [HarmonyPatch]
    internal static class PowderKegPatches
    {
        [HarmonyPatch(typeof(LuckyTower.Traps.TrapPowderKeg), "DestroyTower")]
        [HarmonyPrefix]
        private static bool TrapKegDestroyTower() => OnDestroyTower();

        [HarmonyPatch(typeof(LuckyTower.Items.SpecialItemPowderKeg), "DestroyTower")]
        [HarmonyPrefix]
        private static bool ItemKegDestroyTower() => OnDestroyTower();

        private static bool OnDestroyTower()
        {
            if (NetSession.IsClient)
            {
                return false;
            }
            if (NetSession.IsHost)
            {
                NetSession.Instance?.Host?.SendTowerExplosion();
            }
            return true;
        }
    }
}
