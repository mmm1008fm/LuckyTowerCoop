using System.Collections.Generic;
using HarmonyLib;
using LuckyTower;
using LuckyTower.CharacterSystem;
using LuckyTower.RoomGeneration;
using LuckyTowerCoop.Coop;
using UnityEngine;
using GamePlayer = LuckyTower.CharacterSystem.Player;

namespace LuckyTowerCoop.Patches
{
    /// <summary>Doors: pressing "up" at a door goes through a vote.</summary>
    [HarmonyPatch(typeof(EnterDoorCommand), "DoorDownAction")]
    internal static class DoorDownActionPatch
    {
        private static readonly AccessTools.FieldRef<EnterDoorCommand, Character> s_character =
            AccessTools.FieldRefAccess<EnterDoorCommand, Character>("m_character");

        private static readonly AccessTools.FieldRef<EnterDoorCommand, List<Collider2D>> s_results =
            AccessTools.FieldRefAccess<EnterDoorCommand, List<Collider2D>>("m_results");

        private static bool Prefix(EnterDoorCommand __instance)
        {
            if (!(s_character(__instance) is GamePlayer player))
            {
                return true;
            }
            var results = s_results(__instance);
            if (results == null || results.Count == 0 || results[0] == null)
            {
                return true;
            }
            // Door choice as in the original (bubble door takes priority)
            var door = results[0].GetComponent<Door>();
            foreach (var r in results)
            {
                if (r != null && r.TryGetComponent<Door>(out var d) && d.BubbleDoor)
                {
                    door = d;
                    break;
                }
            }
            return CoopTransitions.OnDoorPressed(__instance, player, door);
        }
    }

    /// <summary>Hatch to the next segment.</summary>
    [HarmonyPatch(typeof(StartNewSegmentTrigger), "OnTriggerEnter2D")]
    internal static class StartNewSegmentTriggerPatch
    {
        private static readonly AccessTools.FieldRef<StartNewSegmentTrigger, List<GameObject>> s_dropped =
            AccessTools.FieldRefAccess<StartNewSegmentTrigger, List<GameObject>>("m_droppedObjects");

        private static bool Prefix(StartNewSegmentTrigger __instance, Collider2D collision)
        {
            if (!collision.CompareTag("Player") || collision.gameObject.layer != LayerMask.NameToLayer("Player"))
            {
                return true;
            }
            var player = collision.GetComponent<GamePlayer>();
            if (player == null)
            {
                return true;
            }
            var trigger = __instance;
            return CoopTransitions.OnTriggerTransition(player, trigger, trigger.transform.position + Vector3.up * 1.5f, canCancel: false, cancelPush: Vector3.zero,
                commitAsP1: () => GameManager.Instance.EnterNextSegment(trigger.TargetRoom, s_dropped(trigger)));
        }
    }

    /// <summary>Side passage to the adjacent room.</summary>
    [HarmonyPatch(typeof(EnterDoorSideTrigger), "OnTriggerEnter2D")]
    internal static class EnterDoorSideTriggerPatch
    {
        private static bool Prefix(EnterDoorSideTrigger __instance, Collider2D collision)
        {
            if (!collision.CompareTag("Player"))
            {
                return true;
            }
            var player = collision.GetComponent<GamePlayer>();
            if (player == null)
            {
                return true;
            }
            var trigger = __instance;
            var room = GameManager.InstanceNoWarnings?.CurrentRoom;
            // Cancel - a step back, toward the center of the current room
            var push = room != null ? new Vector3(Mathf.Sign(room.transform.position.x - trigger.transform.position.x) * 1.2f, 0f, 0f) : Vector3.zero;
            return CoopTransitions.OnTriggerTransition(player, trigger, trigger.transform.position + Vector3.up * 2.5f, canCancel: true, cancelPush: push,
                commitAsP1: () => GameManager.Instance.EnterDoorSide(trigger.TargetRoom, trigger.TargetPosition));
        }
    }
}
