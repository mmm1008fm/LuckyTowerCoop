using System;
using HarmonyLib;
using LuckyTowerCoop.Net;
using LuckyTower.RoomGeneration;
using UnityEngine;

namespace LuckyTowerCoop.Patches
{
    /// <summary>Game bugs that fire every frame in co-op/online and flood the log.</summary>
    [HarmonyPatch]
    internal static class GameFixPatches
    {
        private static readonly AccessTools.FieldRef<VisualIndicator, Transform> s_indicatorTarget =
            AccessTools.FieldRefAccess<VisualIndicator, Transform>("m_targetPos");

        /// <summary>Pointer to a powder keg: the keg is already destroyed (on the client - over the network), but the pointer touches it every frame.</summary>
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

        /// <summary>Particles created together with an object from the host look for a parent that the client does not have.</summary>
        [HarmonyPatch(typeof(PlayParticlesOnce), "Start")]
        [HarmonyFinalizer]
        private static Exception ParticlesStart(Exception __exception) => __exception is NullReferenceException ? null : __exception;

        /// <summary>A bat created over the network reaches into a combat manager on removal that the client does not have for it.</summary>
        [HarmonyPatch(typeof(BatBehaviour), "OnDisable")]
        [HarmonyFinalizer]
        private static Exception BatDisable(Exception __exception) => NetSession.IsClient && __exception is NullReferenceException ? null : __exception;

        /// <summary>Hanging decorations (chains, branches): their physics is off on the client, and the angle limiter fails every frame on an empty joint.</summary>
        [HarmonyPatch(typeof(AngleLimiter), "FixedUpdate")]
        [HarmonyPrefix]
        private static bool AngleLimiterFixedUpdate() => !NetSession.IsClient;

        /// <summary>The room projection sometimes touches a camera that is not ready yet on camera/scene change.</summary>
        [HarmonyPatch(typeof(Room), "UpdateProjection")]
        [HarmonyFinalizer]
        private static Exception RoomProjection(Exception __exception) => __exception is NullReferenceException ? null : __exception;
    }
}