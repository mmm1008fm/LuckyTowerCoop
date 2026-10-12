using HarmonyLib;
using LuckyTower.Evelius;
using LuckyTowerCoop.Net;

namespace LuckyTowerCoop.Patches
{
    /// <summary>
    /// Эвелиус (злой маг в хабах) — решает хост: появление, настроение, охота, сундуки. У клиента его логика не выполняется
    /// (Update/триггеры глушатся списком WorldSuppressPatches), а Start не даёт ему самоуничтожиться по положению марионетки игрока.
    /// Позиция/анимация приходят как у любого объекта мира, реплики — через Talk/Window.
    /// </summary>
    [HarmonyPatch(typeof(EveliusController), "Start")]
    internal static class EveliusStartPatch
    {
        private static bool Prefix() => !NetSession.IsClient;
    }
}