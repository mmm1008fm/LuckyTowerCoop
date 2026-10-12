using HarmonyLib;
using LuckyTowerCoop.Net;

namespace LuckyTowerCoop.Patches
{
    /// <summary>
    /// Взрыв башни бочкой с порохом (бочка-ловушка и бочка в руках): хост запускает ролик и сообщает клиенту,
    /// клиент показывает тот же ролик сам (свой DestroyTower у него не выполняется). Смерть придёт обычным путём.
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
