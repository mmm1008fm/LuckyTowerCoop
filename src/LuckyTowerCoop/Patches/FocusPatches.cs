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
    /// CharacterUtilities.HandleInput (ходьба и все команды персонажа) работает, только пока окно игры в фокусе.
    /// Персонаж напарника у хоста управляется по сети — он должен двигаться, даже если хост свернул игру
    /// или играет во втором окне на этом же ПК. Остальным персонажам проверка фокуса остаётся.
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
                Plugin.Log.LogWarning("[Focus] Application.isFocused not found in HandleInput — partner may freeze when host window is unfocused");
            }
        }

        private static bool IsFocusedFor(CharacterUtilities utilities)
        {
            if (Application.isFocused || NetLaunch.DevServer)
            {
                return true; // автотесты двух копий: обе без фокуса
            }
            // персонаж напарника — тот, кем управляет его ввод (системный игрок Rewired); после смерти хоста он становится главным
            return NetSession.IsHost && s_character(utilities) is GamePlayer p && p.PlayerInput != null && p.PlayerInput == CoopInput.P2Input;
        }
    }
}
