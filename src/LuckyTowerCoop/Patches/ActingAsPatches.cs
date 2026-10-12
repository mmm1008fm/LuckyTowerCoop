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
    /// Код предметов/действий игры обращается к «текущему игроку» (Player.CurrentPlayer, GameManager.Player).
    /// Пока действует второй игрок (его апдейт, ввод, урон, смерть, его кошель) — подставляем его.
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

        // --- апдейт второго игрока (ввод, команды, подбор, броски) ---

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

        // --- урон и смерть второго игрока ---

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

        // --- превращение (зелье-монстр, джинн, прислужник): игра заменяет объект «текущего игрока» новым телом ---

        [HarmonyPatch(typeof(CharacterAction), nameof(CharacterAction.TransformAction))]
        [HarmonyPrefix]
        private static void TransformPrefix(CharacterAction __instance, out TargetSwap.State __state) => __state = SwapIfP2(s_actionCharacter(__instance));

        [HarmonyPatch(typeof(CharacterAction), nameof(CharacterAction.TransformAction))]
        [HarmonyFinalizer]
        private static Exception TransformFinalizer(TargetSwap.State __state, Exception __exception)
        {
            if (__state.Swapped)
            {
                // новое тело второго игрока игра записала в GameManager.Player — делаем его вторым, главного возвращаем
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

        // --- удар оружием: объекты «только для игрока» (рычаги) сверяют владельца с главным игроком ---

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

        // --- применение урона оружием: рычаги и автоматы платят монетами того, кто ударил; убийство засчитывается ему ---

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

        // --- кошель: подставляем его владельца ---

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

        // --- кучка монет: экипирует тот, кто подбирает ---

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
