using System;
using System.Collections.Generic;
using HarmonyLib;
using LuckyTower;
using LuckyTower.CharacterSystem;
using LuckyTower.Combat;
using LuckyTowerCoop.Coop;
using UnityEngine;
using GamePlayer = LuckyTower.CharacterSystem.Player;

namespace LuckyTowerCoop.Patches
{
    /// <summary>
    /// ИИ игры целится в «игрока» через Player.VonWansts[0] / GameManager.Player.
    /// На время апдейта ИИ подставляем туда ближайшего живого игрока и возвращаем обратно —
    /// одна точка вместо сотен мест в коде.
    /// </summary>
    internal static class TargetSwap
    {
        private static readonly AccessTools.FieldRef<GameManager, GamePlayer> s_gmPlayer =
            AccessTools.FieldRefAccess<GameManager, GamePlayer>("m_player");

        private static readonly Dictionary<int, GamePlayer> s_lastTarget = new Dictionary<int, GamePlayer>();

        internal struct State
        {
            public bool Swapped;
            public GamePlayer Slot0;
            public GamePlayer GmPlayer;
            public int Generation;
        }

        private static int s_depth;

        public static bool InSwap => s_depth > 0;

        public static State SwapTo(GamePlayer target)
        {
            var gm = GameManager.InstanceNoWarnings;
            if (target == null || gm == null || (GamePlayer.VonWansts[0] == target && s_gmPlayer(gm) == target))
            {
                return default;
            }
            var st = new State { Swapped = true, Slot0 = GamePlayer.VonWansts[0], GmPlayer = s_gmPlayer(gm), Generation = CoopPlayers.Generation };
            s_depth++;
            GamePlayer.VonWansts[0] = target;
            s_gmPlayer(gm) = target;
            return st;
        }

        public static void Restore(State st)
        {
            if (!st.Swapped)
            {
                return;
            }
            s_depth--;
            var gm = GameManager.InstanceNoWarnings;
            if (st.Generation != CoopPlayers.Generation)
            {
                // Во время подмены кто-то умер/повысился/появился: ставим актуального главного, а не сохранённое
                var main = CoopPlayers.P1;
                GamePlayer.VonWansts[0] = main;
                if (gm != null && main != null)
                {
                    s_gmPlayer(gm) = main;
                }
                return;
            }
            GamePlayer.VonWansts[0] = st.Slot0;
            if (gm != null)
            {
                s_gmPlayer(gm) = st.GmPlayer;
            }
        }

        public static GamePlayer NearestFor(Component ai)
        {
            int id = ai.GetInstanceID();
            s_lastTarget.TryGetValue(id, out var last);
            var t = CoopPlayers.NearestAlive(ai.transform.position, last);
            s_lastTarget[id] = t;
            return t;
        }

        public static bool Active => CoopPlayers.P2 != null && CoopPlayers.P1 != null;

        /// <summary>Клиент: кого игра считает «игроком» (камера, туман, свет) — вне подмен ИИ.</summary>
        public static void SetCurrent(GameManager gm, GamePlayer p)
        {
            if (gm == null || p == null || InSwap)
            {
                return;
            }
            s_gmPlayer(gm) = p;
            GamePlayer.VonWansts[0] = p;
        }

        public static GamePlayer CurrentOf(GameManager gm) => gm != null ? s_gmPlayer(gm) : null;
    }

    [HarmonyPatch]
    internal static class AiTargetPatches
    {
        [HarmonyPatch(typeof(Enemy), "Update")]
        [HarmonyPrefix]
        private static void EnemyUpdatePrefix(Enemy __instance, out TargetSwap.State __state)
        {
            __state = TargetSwap.Active ? TargetSwap.SwapTo(TargetSwap.NearestFor(__instance)) : default;
        }

        [HarmonyPatch(typeof(Enemy), "Update")]
        [HarmonyFinalizer]
        private static Exception EnemyUpdateFinalizer(TargetSwap.State __state, Exception __exception)
        {
            TargetSwap.Restore(__state);
            return __exception;
        }

        [HarmonyPatch(typeof(NPC), "Update")]
        [HarmonyPrefix]
        private static void NpcUpdatePrefix(NPC __instance, out TargetSwap.State __state)
        {
            __state = TargetSwap.Active && __instance.Stats.Evil ? TargetSwap.SwapTo(TargetSwap.NearestFor(__instance)) : default;
        }

        [HarmonyPatch(typeof(NPC), "Update")]
        [HarmonyFinalizer]
        private static Exception NpcUpdateFinalizer(TargetSwap.State __state, Exception __exception)
        {
            TargetSwap.Restore(__state);
            return __exception;
        }

        // Детектор врагов висит на каждом игроке: «игрок» для него — владелец детектора.
        [HarmonyPatch(typeof(EnemyDetectionRange), "OnTriggerEnter2D")]
        [HarmonyPrefix]
        private static void DetectEnterPrefix(EnemyDetectionRange __instance, out TargetSwap.State __state)
        {
            __state = default;
            if (TargetSwap.Active)
            {
                var owner = __instance.GetComponentInParent<GamePlayer>();
                if (owner != null && CoopPlayers.IsCoopPlayer(owner))
                {
                    __state = TargetSwap.SwapTo(owner);
                }
            }
        }

        [HarmonyPatch(typeof(EnemyDetectionRange), "OnTriggerEnter2D")]
        [HarmonyFinalizer]
        private static Exception DetectEnterFinalizer(TargetSwap.State __state, Exception __exception)
        {
            TargetSwap.Restore(__state);
            return __exception;
        }

        [HarmonyPatch(typeof(EnemyDetectionRange), "OnTriggerExit2D")]
        [HarmonyPrefix]
        private static void DetectExitPrefix(EnemyDetectionRange __instance, out TargetSwap.State __state)
        {
            __state = default;
            if (TargetSwap.Active)
            {
                var owner = __instance.GetComponentInParent<GamePlayer>();
                if (owner != null && CoopPlayers.IsCoopPlayer(owner))
                {
                    __state = TargetSwap.SwapTo(owner);
                }
            }
        }

        [HarmonyPatch(typeof(EnemyDetectionRange), "OnTriggerExit2D")]
        [HarmonyFinalizer]
        private static Exception DetectExitFinalizer(TargetSwap.State __state, Exception __exception)
        {
            TargetSwap.Restore(__state);
            // у каждого игрока свой список врагов, а у CombatManager общий — RemoveFurthestEnemy иногда зовётся на пустом
            return __exception is InvalidOperationException ? null : __exception;
        }

        // Летающие (летучие мыши, пчёлы): цель хранится в поле.
        private static readonly AccessTools.FieldRef<FlyingBehaviour, GamePlayer> s_flyingPlayer =
            AccessTools.FieldRefAccess<FlyingBehaviour, GamePlayer>("m_player");

        [HarmonyPatch(typeof(FlyingBehaviour), "Update")]
        [HarmonyPrefix]
        private static void FlyingUpdatePrefix(FlyingBehaviour __instance)
        {
            if (!TargetSwap.Active)
            {
                return;
            }
            var target = TargetSwap.NearestFor(__instance);
            if (target != null && s_flyingPlayer(__instance) != target)
            {
                s_flyingPlayer(__instance) = target;
            }
        }
    }
}
