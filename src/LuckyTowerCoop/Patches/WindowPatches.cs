using HarmonyLib;
using LuckyTowerCoop.Net;
using UnityEngine;

namespace LuckyTowerCoop.Patches
{
    /// <summary>
    /// Окно выбора (InteractionWindow: диалоги, магазин, банк, джинн, компаньоны) персонажа клиента живёт у хоста —
    /// выбор делается по пересланному вводу. Клиенту хост пересылает «открылось/закрылось», тот показывает такое же окно у себя.
    /// У клиента локальные открытия заглушены.
    /// </summary>
    [HarmonyPatch(typeof(InteractionWindow))]
    internal static class WindowPatches
    {
        [HarmonyPatch(nameof(InteractionWindow.OpenWindow))]
        [HarmonyPrefix]
        private static bool OpenPrefix() => !NetSession.IsClient || NetClient.ApplyingWindow;

        [HarmonyPatch(nameof(InteractionWindow.OpenWindow))]
        [HarmonyPostfix]
        private static void OpenPostfix(InteractionWindow __instance, TalkState state, float delay, bool follow, bool canBeClosed, Transform target, Vector2 targetOffset,
            string animation1Override, string animation2Override, string animation3Override)
        {
            if (NetSession.IsHost)
            {
                Plugin.Log.LogInfo($"[Window] open {state} on {(OwnerContext.SecondOwnerOf(__instance) != null ? "partner" : "host")} window, isOpen={__instance.IsOpen}");
            }
            if (NetSession.IsHost && __instance.IsOpen && OwnerContext.SecondOwnerOf(__instance) != null)
            {
                NetSession.Instance?.Host?.SendWindow(true, state, canBeClosed, follow, delay, target, targetOffset, animation1Override, animation2Override, animation3Override);
            }
        }

        [HarmonyPatch(nameof(InteractionWindow.CloseWindow))]
        [HarmonyPrefix]
        private static void ClosePrefix(InteractionWindow __instance, out bool __state) => __state = __instance.IsOpen;

        [HarmonyPatch(nameof(InteractionWindow.CloseWindow))]
        [HarmonyPostfix]
        private static void ClosePostfix(InteractionWindow __instance, bool __state)
        {
            if (NetSession.IsHost && __state && OwnerContext.SecondOwnerOf(__instance) != null)
            {
                NetSession.Instance?.Host?.SendWindow(false, TalkState.None, false, false, 0f, null, Vector2.zero, null, null, null);
            }
        }
    }
}