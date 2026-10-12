using System;
using System.Reflection;
using HarmonyLib;
using LuckyTower;
using LuckyTower.CharacterSystem;
using LuckyTower.Items;
using LuckyTowerCoop.Coop;
using GamePlayer = LuckyTower.CharacterSystem.Player;

namespace LuckyTowerCoop.Patches
{
    /// <summary>
    /// Game code for items/actions refers to the "current player" (Player.CurrentPlayer, GameManager.Player).
    /// While the second player is acting (their update, input, damage, death, their pouch) we substitute them.
    /// </summary>
    [HarmonyPatch]
    internal static class ActingAsPatches
    {
        private static readonly AccessTools.FieldRef<CharacterAction, Character> s_actionCharacter =
            AccessTools.FieldRefAccess<CharacterAction, Character>("m_character");

        private static TargetSwap.State SwapIfP2(Character c)
        {
            var p2 = CoopPlayers.P2;
            return p2 != null && c == p2 ? TargetSwap.SwapTo(p2) : default;
        }

        // --- second player update (input, commands, pickup, throws) ---

        [HarmonyPatch(typeof(GamePlayer), "Update")]
        [HarmonyPrefix]
        private static void PlayerUpdatePrefix(GamePlayer __instance, out TargetSwap.State __state) => __state = SwapIfP2(__instance);

        [HarmonyPatch(typeof(GamePlayer), "Update")]
        [HarmonyFinalizer]
        private static Exception PlayerUpdateFinalizer(TargetSwap.State __state, Exception __exception)
        {
            TargetSwap.Restore(__state);
            return __exception;
        }

        [HarmonyPatch(typeof(GamePlayer), "FixedUpdate")]
        [HarmonyPrefix]
        private static void PlayerFixedUpdatePrefix(GamePlayer __instance, out TargetSwap.State __state) => __state = SwapIfP2(__instance);

        [HarmonyPatch(typeof(GamePlayer), "FixedUpdate")]
        [HarmonyFinalizer]
        private static Exception PlayerFixedUpdateFinalizer(TargetSwap.State __state, Exception __exception)
        {
            TargetSwap.Restore(__state);
            return __exception;
        }

        // --- second player damage and death ---

        [HarmonyPatch(typeof(CharacterAction), nameof(CharacterAction.GetDamaged))]
        [HarmonyPrefix]
        private static void GetDamagedPrefix(CharacterAction __instance, out TargetSwap.State __state) => __state = SwapIfP2(s_actionCharacter(__instance));

        [HarmonyPatch(typeof(CharacterAction), nameof(CharacterAction.GetDamaged))]
        [HarmonyFinalizer]
        private static Exception GetDamagedFinalizer(TargetSwap.State __state, Exception __exception)
        {
            TargetSwap.Restore(__state);
            return __exception;
        }

        // --- transformation (monster potion, genie, minion): the game replaces the "current player" object with a new body ---

        [HarmonyPatch(typeof(CharacterAction), nameof(CharacterAction.TransformAction))]
        [HarmonyPrefix]
        private static void TransformPrefix(CharacterAction __instance, out TargetSwap.State __state) => __state = SwapIfP2(s_actionCharacter(__instance));

        [HarmonyPatch(typeof(CharacterAction), nameof(CharacterAction.TransformAction))]
        [HarmonyFinalizer]
        private static Exception TransformFinalizer(TargetSwap.State __state, Exception __exception)
        {
            if (__state.Swapped)
            {
                // the game stored the second player's new body in GameManager.Player - make it the second player and restore the main one
                var gm = GameManager.InstanceNoWarnings;
                var body = TargetSwap.CurrentOf(gm);
                var p2 = CoopPlayers.P2;
                if (body != null && body != p2 && body != CoopPlayers.P1)
                {
                    CoopPlayers.AdoptAsSecond(body);
                    Plugin.Log.LogInfo($"[Coop] second player transformed into {body.name}");
                }
            }
            TargetSwap.Restore(__state);
            return __exception;
        }

        // --- weapon hit: player-only objects (levers) compare the owner with the main player ---

        [HarmonyPatch(typeof(Weapon), "GetPotentialHitTargets")]
        [HarmonyPrefix]
        private static void WeaponHitsPrefix(Weapon __instance, out TargetSwap.State __state) => __state = SwapIfP2(__instance.Owner);

        [HarmonyPatch(typeof(Weapon), "GetPotentialHitTargets")]
        [HarmonyFinalizer]
        private static Exception WeaponHitsFinalizer(TargetSwap.State __state, Exception __exception)
        {
            TargetSwap.Restore(__state);
            return __exception;
        }

        // --- weapon damage: levers and slot machines take coins from whoever hit them; the kill is credited to them ---

        [HarmonyPatch(typeof(Weapon), "ApplyDamage")]
        [HarmonyPrefix]
        private static void WeaponApplyDamagePrefix(Weapon __instance, out TargetSwap.State __state) => __state = SwapIfP2(__instance.Owner);

        [HarmonyPatch(typeof(Weapon), "ApplyDamage")]
        [HarmonyFinalizer]
        private static Exception WeaponApplyDamageFinalizer(TargetSwap.State __state, Exception __exception)
        {
            TargetSwap.Restore(__state);
            return __exception;
        }

        // --- pouch: substitute its owner ---

        private static GamePlayer OwnerOf(GoldSack sack)
        {
            var p2 = CoopPlayers.P2;
            if (p2 == null || sack == null)
            {
                return null;
            }
            var s = p2.Stats;
            if (s.CurrentGoldSack == sack
                || (s.CurrentWeapon != null && s.CurrentWeapon.GetComponentInChildren<GoldSack>() == sack)
                || (s.ItemInSecondHand != null && s.ItemInSecondHand.GetComponentInChildren<GoldSack>() == sack))
            {
                return p2;
            }
            return null;
        }

        private static MethodBase[] GoldSackMethods() => new MethodBase[]
        {
            AccessTools.Method(typeof(GoldSack), nameof(GoldSack.AddCoin)),
            AccessTools.Method(typeof(GoldSack), nameof(GoldSack.DropCoin), new[] { typeof(int) }),
            AccessTools.Method(typeof(GoldSack), "DropCoin", new[] { typeof(bool) }),
            AccessTools.Method(typeof(GoldSack), nameof(GoldSack.Hit)),
            AccessTools.Method(typeof(GoldSack), nameof(GoldSack.Loot)),
        };

        [HarmonyPatch]
        private static class GoldSackPatch
        {
            private static System.Collections.Generic.IEnumerable<MethodBase> TargetMethods() => GoldSackMethods();

            private static void Prefix(GoldSack __instance, out TargetSwap.State __state)
            {
                var owner = OwnerOf(__instance);
                __state = owner != null ? TargetSwap.SwapTo(owner) : default;
            }

            private static Exception Finalizer(TargetSwap.State __state, Exception __exception)
            {
                TargetSwap.Restore(__state);
                return __exception;
            }
        }

        // --- coin pile: equipped by whoever picks it up ---

        [HarmonyPatch]
        private static class CoinEquipPatch
        {
            private static MethodBase TargetMethod() => AccessTools.FirstMethod(typeof(CoinSpecialItem), m => m.Name.EndsWith("ISpecialEquip.Equip"));

            private static bool Prepare() => TargetMethod() != null;

            private static void Prefix(Character c, out TargetSwap.State __state) => __state = SwapIfP2(c);

            private static Exception Finalizer(TargetSwap.State __state, Exception __exception)
            {
                TargetSwap.Restore(__state);
                return __exception;
            }
        }
    }
}
