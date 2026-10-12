using HarmonyLib;
using LuckyTowerCoop.Coop;
using GamePlayer = LuckyTower.CharacterSystem.Player;

namespace LuckyTowerCoop.Patches
{
    /// <summary>
    /// Player.OnEnable занимает первый свободный слот VonWansts и всегда берёт Rewired player 0,
    /// а при активной бутылке возрождения насильно садится в слот 0. Для P2 исправляем после оригинала.
    /// </summary>
    [HarmonyPatch(typeof(GamePlayer), "OnEnable")]
    internal static class PlayerOnEnablePatch
    {
        private static void Postfix(GamePlayer __instance)
        {
            if (__instance.GetComponent<CoopPlayerTag>() == null)
            {
                return;
            }
            CoopPlayers.RegisterAsP2(__instance);
        }
    }

    /// <summary>Оси движения читаются у Player.CurrentPlayer — для P2 берём ввод владельца паттерна.</summary>
    [HarmonyPatch(typeof(CommandPattern), nameof(CommandPattern.GetHorizontalAxis))]
    internal static class HorizontalAxisPatch
    {
        private static bool Prefix(CommandPattern __instance, ref float __result)
        {
            if (!CoopPlayers.TryGetPatternOwner(__instance, out var owner) || owner.PlayerInput == null)
            {
                return true;
            }
            float axis = 0f;
            if (owner.PlayerInput.GetButton(2))
            {
                axis -= 1f;
            }
            if (owner.PlayerInput.GetButton(23))
            {
                axis += 1f;
            }
            __result = axis;
            return false;
        }
    }

    [HarmonyPatch(typeof(CommandPattern), nameof(CommandPattern.GetVerticalAxis))]
    internal static class VerticalAxisPatch
    {
        private static bool Prefix(CommandPattern __instance, ref float __result)
        {
            if (!CoopPlayers.TryGetPatternOwner(__instance, out var owner) || owner.PlayerInput == null)
            {
                return true;
            }
            float axis = 0f;
            if (owner.PlayerInput.GetButton(21))
            {
                axis += 1f;
            }
            if (owner.PlayerInput.GetButton(22))
            {
                axis -= 1f;
            }
            __result = axis;
            return false;
        }
    }
}
