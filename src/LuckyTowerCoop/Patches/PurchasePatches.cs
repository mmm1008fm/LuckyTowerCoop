using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using LuckyTower;
using LuckyTowerCoop.Coop;
using LuckyTowerCoop.Net;
using UnityEngine;
using GamePlayer = LuckyTower.CharacterSystem.Player;

namespace LuckyTowerCoop.Patches
{
    /// <summary>
    /// Shops and NPCs with dialogue (pawnshop, barber, chimney sweep, banker): a dialogue trigger remembers the first player's dialogue manager,
    /// so the second player ended up in the first one's queue, and payment happened outside the "current player" swap.
    ///  - the DialogueTrigger.DialogueManager getter returns the manager of whoever is the "player" now (under a swap - the second one's manager);
    ///  - shop "action" methods (payment, haircut, animator events) run on behalf of whoever's manager drives this dialogue;
    ///  - the piggy bank (DropAllCoinsDialogue) works with the player standing in its zone.
    /// Does not work on the client: the host drives dialogues there.
    /// </summary>
    internal static class Buyers
    {
        /// <summary>The second player, if their manager is the one driving the dialogue with this trigger.</summary>
        public static GamePlayer SecondTalkingTo(DialogueTrigger t)
        {
            var p2 = CoopPlayers.P2;
            if (t == null || p2 == null || !p2 || NetSession.IsClient || p2.Components == null)
            {
                return null;
            }
            var dm = p2.Components.DialogueManager;
            if (dm == null)
            {
                return null;
            }
            if (dm.CurrentDialogueTrigger == t)
            {
                return p2;
            }
            var p1 = CoopPlayers.P1;
            var dm1 = p1 != null && p1.Components != null ? p1.Components.DialogueManager : null;
            if ((dm1 == null || dm1.CurrentDialogueTrigger != t) && dm.OverlappingTriggers.Contains(t))
            {
                return p2;
            }
            return null;
        }

        /// <summary>Whether the trigger touches at least one player collider.</summary>
        public static bool Touching(Component shop, GamePlayer p)
        {
            if (shop == null || p == null || !p)
            {
                return false;
            }
            var mine = p.GetComponentsInChildren<Collider2D>();
            foreach (var c in shop.GetComponents<Collider2D>())
            {
                if (c == null || !c.enabled)
                {
                    continue;
                }
                foreach (var pc in mine)
                {
                    if (pc != null && pc.enabled && c.IsTouching(pc))
                    {
                        return true;
                    }
                }
            }
            return false;
        }
    }

    /// <summary>
    /// All dialogue trigger methods (shops, NPCs, menus): if the second player is acting, the trigger's dialogue manager is theirs for that time;
    /// shop "actions" (payment, haircut, animator events) run on behalf of whoever's manager drives this dialogue.
    /// We change the m_dialogueManager field directly: the getter may be inlined by the JIT.
    /// </summary>
    [HarmonyPatch]
    internal static class DialogueTriggerContextPatches
    {
        private static readonly HashSet<string> s_lifecycle = new HashSet<string>
        {
            "Awake", "Start", "Update", "FixedUpdate", "LateUpdate", "OnEnable", "OnDisable", "OnDestroy",
            "OnTriggerEnter2D", "OnTriggerExit2D", "OnTriggerStay2D", "OnCollisionEnter2D", "OnCollisionExit2D", "OnCollisionStay2D",
        };

        private static readonly AccessTools.FieldRef<DialogueTrigger, DialogueManager> s_dm =
            AccessTools.FieldRefAccess<DialogueTrigger, DialogueManager>("m_dialogueManager");

        internal struct State
        {
            public TargetSwap.State Swap;
        }

        private static IEnumerable<MethodBase> TargetMethods()
        {
            int n = 0;
            foreach (var t in typeof(GameManager).Assembly.GetTypes())
            {
                if (!typeof(DialogueTrigger).IsAssignableFrom(t))
                {
                    continue;
                }
                foreach (var m in t.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                {
                    if (m.IsAbstract || m.IsSpecialName || m.IsGenericMethod || m.Name == "Awake" || m.Name == "Start" || m.GetMethodBody() == null
                        || m.ReturnType == typeof(System.Collections.IEnumerator) || m.Name.Contains("<"))
                    {
                        continue;
                    }
                    n++;
                    yield return m;
                }
            }
            Plugin.Log.LogInfo($"[Context] dialogue trigger methods patched: {n}");
        }

        private static void Prefix(DialogueTrigger __instance, MethodBase __originalMethod, object[] __args, out State __state)
        {
            __state = default;
            var p2 = CoopPlayers.P2;
            if (p2 == null || !p2 || NetSession.IsClient || p2.Components == null || p2.Components.DialogueManager == null)
            {
                return;
            }
            string name = __originalMethod.Name;
            bool acting = TargetSwap.InSwap && TargetSwap.CurrentOf(GameManager.InstanceNoWarnings) == p2;
            if (!acting)
            {
                GamePlayer who = null;
                if (name.StartsWith("OnTrigger") || name.StartsWith("OnCollision"))
                {
                    var col = __args != null && __args.Length == 1 ? (__args[0] as Collider2D ?? (__args[0] as Collision2D)?.collider) : null;
                    who = col != null && CoopPlayers.IsP2Collider(col) ? p2 : null;
                }
                else if (!s_lifecycle.Contains(name))
                {
                    who = Buyers.SecondTalkingTo(__instance);
                }
                if (who == null)
                {
                    // the main player is acting: the trigger is driven by their manager again
                    var p1 = CoopPlayers.P1;
                    var dm1 = p1 != null && p1.Components != null ? p1.Components.DialogueManager : null;
                    if (dm1 != null && s_dm(__instance) != dm1 && (dm1.CurrentDialogueTrigger == __instance || dm1.OverlappingTriggers.Contains(__instance) || name.StartsWith("OnTrigger")))
                    {
                        var colp = __args != null && __args.Length == 1 ? (__args[0] as Collider2D ?? (__args[0] as Collision2D)?.collider) : null;
                        if (!name.StartsWith("OnTrigger") || (colp != null && colp.GetComponentInParent<GamePlayer>() == p1))
                        {
                            s_dm(__instance) = dm1;
                        }
                    }
                    return;
                }
                __state.Swap = TargetSwap.SwapTo(who);
                acting = true;
            }
            if (acting)
            {
                s_dm(__instance) = p2.Components.DialogueManager;
            }
        }

        private static Exception Finalizer(DialogueTrigger __instance, State __state, Exception __exception)
        {
            TargetSwap.Restore(__state.Swap);
            return __exception;
        }
    }

    /// <summary>Piggy bank / ATM: work with whoever stands in the zone (otherwise always the main player).</summary>
    [HarmonyPatch]
    internal static class BankAreaPatches
    {
        private static IEnumerable<MethodBase> TargetMethods()
        {
            var t = typeof(GameManager).Assembly.GetType("DropAllCoinsDialogue");
            if (t == null)
            {
                yield break;
            }
            foreach (var name in new[] { "OnTriggerEnter2D", "OnTriggerExit2D", "Update", "OpenMenu" })
            {
                var m = t.GetMethod(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
                if (m != null)
                {
                    yield return m;
                }
            }
        }

        private static void Prefix(MonoBehaviour __instance, MethodBase __originalMethod, object[] __args, out TargetSwap.State __state)
        {
            __state = default;
            var p2 = CoopPlayers.P2;
            if (p2 == null || !p2 || NetSession.IsClient)
            {
                return;
            }
            if (__args != null && __args.Length == 1)
            {
                var col = __args[0] as Collider2D;
                if (col != null && CoopPlayers.IsP2Collider(col))
                {
                    __state = TargetSwap.SwapTo(p2);
                }
                return;
            }
            // Update / OpenMenu: the second player, if only they are in the zone
            if (Buyers.Touching(__instance, p2) && !Buyers.Touching(__instance, CoopPlayers.P1))
            {
                __state = TargetSwap.SwapTo(p2);
            }
        }

        private static Exception Finalizer(TargetSwap.State __state, Exception __exception)
        {
            TargetSwap.Restore(__state);
            return __exception;
        }
    }
}
