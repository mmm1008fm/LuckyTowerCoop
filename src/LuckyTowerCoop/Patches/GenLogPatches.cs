using System.Collections.Generic;
using HarmonyLib;
using LuckyTower;
using LuckyTower.RoomGeneration;
using UnityEngine;

namespace LuckyTowerCoop.Patches
{
    /// <summary>Diagnostics (dev): segment generation fingerprint - input (RNG, player state) and output (room list) for host/client comparison.</summary>
    [HarmonyPatch(typeof(RoomGenerator), nameof(RoomGenerator.GenerateSegmentFromConfig))]
    internal static class GenLogPatches
    {
        private static bool Prepare() => CoopConfig.DevEnabled;

        private static void Prefix(SegmentConfiguration segmentConfig, int seed)
        {
            var gm = GameManager.InstanceNoWarnings;
            var p = gm != null ? gm.Player : null;
            var st = UnityEngine.Random.state;
            var parts = new List<string>();
            foreach (var f in typeof(UnityEngine.Random.State).GetFields(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public))
            {
                parts.Add(f.GetValue(st)?.ToString());
            }
            string unity = string.Join("/", parts);
            var dm = DataManager.Instance;
            Plugin.Log.LogInfo($"[Gen] segment {segmentConfig?.Segment} seed={seed} unity={unity} player={(p != null ? p.name : "-")} segId={(p != null ? p.Stats.SegmentID : -1)} " +
                               $"teeth={(p != null ? p.Stats.TeethCount : -1)} companions={(p != null ? p.Stats.GetFollowingCompanions().Count : -1)} " +
                               $"difficulty={dm?.GameData?.Difficulty} endgame={dm?.EndgameMode} companionIdx={SegmentManager.Instance?.CompanionIndices?.Count}");
        }

        private static void Postfix(SegmentConfiguration segmentConfig, Transform __result)
        {
            if (__result == null)
            {
                Plugin.Log.LogInfo("[Gen] result null");
                return;
            }
            var names = new List<string>();
            foreach (var r in __result.GetComponentsInChildren<Room>(true))
            {
                names.Add($"{r.name}({r.RoomType})");
            }
            Plugin.Log.LogInfo($"[Gen] segment {segmentConfig?.Segment} rooms={names.Count}: {string.Join(", ", names)}");
        }
    }
}