using HarmonyLib;
using LuckyTowerCoop.Coop;
using GamePlayer = LuckyTower.CharacterSystem.Player;

namespace LuckyTowerCoop.Patches
{
    /// <summary>
    /// Player.OnEnable takes the first free VonWansts slot and always uses Rewired player 0,
    /// and with an active revival bottle it forcibly takes slot 0. For P2 we fix this after the original.
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

    /// <summary>Movement axes are read from Player.CurrentPlayer - for P2 we take the input of the pattern's owner.</summary>
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
