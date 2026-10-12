using System.Diagnostics;
using System.Linq;
using HarmonyLib;
using LuckyTower;
using LuckyTower.RoomGeneration;

namespace LuckyTowerCoop.Patches
{
    /// <summary>Диагностика: кто и откуда вызывает переходы между комнатами.</summary>
    [HarmonyPatch]
    internal static class TransitionLogPatches
    {
        private static bool Prepare() => CoopConfig.DevEnabled;

        private static string Caller()
        {
            var frames = new StackTrace(2, false).GetFrames();
            if (frames == null)
            {
                return "?";
            }
            return string.Join(" <- ", frames.Take(4).Select(f => f.GetMethod()).Where(m => m != null).Select(m => $"{m.DeclaringType?.Name}.{m.Name}"));
        }

        [HarmonyPatch(typeof(LuckyTower.CharacterSystem.CharacterStats), nameof(LuckyTower.CharacterSystem.CharacterStats.AllowMovement), MethodType.Setter)]
        [HarmonyPostfix]
        private static void AllowMovementSet(LuckyTower.CharacterSystem.CharacterStats __instance, bool value)
        {
            if (__instance != null && __instance.CharacterType == LuckyTower.CharacterSystem.CharacterTypes.Player)
            {
                Plugin.Log.LogInfo($"[Lock] {__instance.Character?.name} AllowMovement={value} by {Caller()}");
            }
        }

        [HarmonyPatch(typeof(GameManager), nameof(GameManager.EnterDoor))]
        [HarmonyPrefix]
        private static void EnterDoor(Door door) =>
            Plugin.Log.LogInfo($"[Transition] EnterDoor door={(door != null ? door.name : "null")} target={(door != null && door.TargetRoom != null ? door.TargetRoom.name : "-")} by {Caller()}");

        [HarmonyPatch(typeof(GameManager), nameof(GameManager.EnterDoorSide))]
        [HarmonyPrefix]
        private static void EnterDoorSide(Room targetRoom) =>
            Plugin.Log.LogInfo($"[Transition] EnterDoorSide target={(targetRoom != null ? targetRoom.name : "null")} by {Caller()}");

        [HarmonyPatch(typeof(GameManager), nameof(GameManager.EnterNextSegment))]
        [HarmonyPrefix]
        private static void EnterNextSegment(Room targetRoom) =>
            Plugin.Log.LogInfo($"[Transition] EnterNextSegment target={(targetRoom != null ? targetRoom.name : "null")} by {Caller()}");

        [HarmonyPatch(typeof(Room), nameof(Room.EnterRoom))]
        [HarmonyPrefix]
        private static void EnterRoom(Room __instance) =>
            Plugin.Log.LogInfo($"[Transition] Room.EnterRoom {__instance.name} by {Caller()}");

        [HarmonyPatch(typeof(CameraManager), nameof(CameraManager.SetConfinerCollider))]
        [HarmonyPrefix]
        private static void SetConfiner(UnityEngine.Collider2D __0) =>
            Plugin.Log.LogInfo($"[Transition] Camera.SetConfinerCollider {(__0 != null ? __0.transform.root.name + "/" + __0.name : "null")} by {Caller()}");
    }
}
