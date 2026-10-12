using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using LuckyTower.CharacterSystem;
using LuckyTowerCoop.Coop;
using LuckyTowerCoop.Net;
using UnityEngine;
using GamePlayer = LuckyTower.CharacterSystem.Player;

namespace LuckyTowerCoop.Patches
{
    /// <summary>
    /// CharacterUtilities.HandleInput (walking and all character commands) works only while the game window is focused.
    /// The partner character on the host is driven over the network - it must move even if the host minimized the game
    /// or plays in a second window on the same PC. The focus check remains for other characters.
    /// </summary>
    [HarmonyPatch(typeof(CharacterUtilities), nameof(CharacterUtilities.HandleInput))]
    internal static class HandleInputFocusPatch
    {
        private static readonly MethodInfo s_isFocused = AccessTools.PropertyGetter(typeof(Application), nameof(Application.isFocused));
        private static readonly MethodInfo s_replacement = AccessTools.Method(typeof(HandleInputFocusPatch), nameof(IsFocusedFor));
        private static readonly AccessTools.FieldRef<CharacterUtilities, Character> s_character =
            AccessTools.FieldRefAccess<CharacterUtilities, Character>("m_character");

        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            int replaced = 0;
            foreach (var ci in instructions)
            {
                if (ci.Calls(s_isFocused))
                {
                    var load = new CodeInstruction(OpCodes.Ldarg_0);
                    ci.MoveLabelsTo(load);
                    ci.MoveBlocksTo(load);
                    yield return load;
                    yield return new CodeInstruction(OpCodes.Call, s_replacement);
                    replaced++;
                    continue;
                }
                yield return ci;
            }
            if (replaced == 0)
            {
                Plugin.Log.LogWarning("[Focus] Application.isFocused not found in HandleInput - partner may freeze when host window is unfocused");
            }
        }

        private static bool IsFocusedFor(CharacterUtilities utilities)
        {
            if (Application.isFocused || NetLaunch.DevServer)
            {
                return true; // two-copy autotests: both without focus
            }
            // the partner character is the one driven by its input (the system Rewired player); after the host dies it becomes the main one
            return NetSession.IsHost && s_character(utilities) is GamePlayer p && p.PlayerInput != null && p.PlayerInput == CoopInput.P2Input;
        }
    }
}
