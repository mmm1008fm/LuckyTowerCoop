using HarmonyLib;
using LuckyTowerCoop.Coop;
using UnityEngine;

namespace LuckyTowerCoop.Patches
{
    /// <summary>Camera switch zones (and their one-shot events) react only to P1.</summary>
    [HarmonyPatch(typeof(SnapCameraToCollider))]
    internal static class SnapCameraToColliderPatches
    {
        [HarmonyPatch("OnTriggerEnter2D")]
        [HarmonyPrefix]
        private static bool Enter(Collider2D collision) => !CoopPlayers.IsP2Collider(collision);

        [HarmonyPatch("OnTriggerExit2D")]
        [HarmonyPrefix]
        private static bool Exit(Collider2D collision) => !CoopPlayers.IsP2Collider(collision);
    }
}
