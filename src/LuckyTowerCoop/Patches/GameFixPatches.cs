using System;
using HarmonyLib;
using LuckyTowerCoop.Net;
using LuckyTower.RoomGeneration;
using UnityEngine;

namespace LuckyTowerCoop.Patches
{
    /// <summary>Ошибки игры, которые в коопе/онлайне срабатывают каждый кадр и забивают лог.</summary>
    [HarmonyPatch]
    internal static class GameFixPatches
    {
        private static readonly AccessTools.FieldRef<VisualIndicator, Transform> s_indicatorTarget =
            AccessTools.FieldRefAccess<VisualIndicator, Transform>("m_targetPos");

        /// <summary>Указатель на бочку с порохом: бочка уже уничтожена (у клиента — по сети), а указатель обращается к ней каждый кадр.</summary>
        [HarmonyPatch(typeof(VisualIndicator), "Update")]
        [HarmonyPrefix]
        private static bool VisualIndicatorUpdate(VisualIndicator __instance)
        {
            if (s_indicatorTarget(__instance) != null)
            {
                return true;
            }
            __instance.gameObject.SetActive(false);
            return false;
        }

        /// <summary>Частицы, созданные вместе с объектом от хоста, ищут родителя, которого у клиента нет.</summary>
        [HarmonyPatch(typeof(PlayParticlesOnce), "Start")]
        [HarmonyFinalizer]
        private static Exception ParticlesStart(Exception __exception) => __exception is NullReferenceException ? null : __exception;

        /// <summary>Летучая мышь, созданная по сети, при удалении лезет в менеджер боя, которого у клиента для неё нет.</summary>
        [HarmonyPatch(typeof(BatBehaviour), "OnDisable")]
        [HarmonyFinalizer]
        private static Exception BatDisable(Exception __exception) => NetSession.IsClient && __exception is NullReferenceException ? null : __exception;

        /// <summary>Подвесные декорации (цепи, ветки): у клиента их физика выключена, ограничитель угла каждый кадр падает на пустой связке.</summary>
        [HarmonyPatch(typeof(AngleLimiter), "FixedUpdate")]
        [HarmonyPrefix]
        private static bool AngleLimiterFixedUpdate() => !NetSession.IsClient;

        /// <summary>Проекция комнаты при смене камеры/сцены иногда обращается к ещё не готовой камере.</summary>
        [HarmonyPatch(typeof(Room), "UpdateProjection")]
        [HarmonyFinalizer]
        private static Exception RoomProjection(Exception __exception) => __exception is NullReferenceException ? null : __exception;
    }
}