using HarmonyLib;
using LuckyTower.RoomGeneration;
using LuckyTowerCoop.Coop;

namespace LuckyTowerCoop.Patches
{
    [HarmonyPatch(typeof(Room), nameof(Room.EnterRoom))]
    internal static class RoomEnterDifficultyPatch
    {
        private static void Postfix(Room __instance) => CoopDifficulty.OnRoomEntered(__instance);
    }
}
