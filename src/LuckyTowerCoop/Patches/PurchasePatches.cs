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
    /// Магазины и NPC с диалогом (ломбард, парикмахер, трубочист, банкир): триггер диалога запоминает менеджер диалогов первого игрока,
    /// поэтому второй попадал в очередь первого, а оплата шла вне подмены «текущего игрока».
    ///  • геттер DialogueTrigger.DialogueManager отдаёт менеджер того, кто сейчас «игрок» (под подменой — менеджер второго);
    ///  • методы-«действия» магазинов (оплата, стрижка, события аниматора) выполняются от имени того, чей менеджер ведёт этот диалог;
    ///  • банк-копилка (DropAllCoinsDialogue) работает с тем игроком, который стоит в его зоне.
    /// На клиенте не работает: там диалоги ведёт хост.
    /// </summary>
    internal static class Buyers
    {
        /// <summary>Второй игрок, если именно его менеджер ведёт диалог с этим триггером.</summary>
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

        /// <summary>Касается ли триггер объекта хотя бы одного коллайдера игрока.</summary>
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
    /// Все методы триггеров диалога (магазины, NPC, меню): если действует второй игрок — менеджер диалогов триггера на это время его;
    /// «действия» магазинов (оплата, стрижка, события аниматора) идут от имени того, чей менеджер ведёт этот диалог.
    /// Поле m_dialogueManager меняем напрямую: геттер может быть встроен JIT-ом.
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
                    // действует главный игрок: триггер снова ведёт его менеджер
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

    /// <summary>Банк-копилка / банкомат: работают с тем, кто стоит в зоне (иначе всегда главный игрок).</summary>
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
            // Update / OpenMenu: второй игрок, если в зоне только он
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
