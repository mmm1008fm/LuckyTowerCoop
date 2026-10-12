using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using LuckyTower;
using LuckyTower.CharacterSystem;
using LuckyTowerCoop.Coop;
using LuckyTowerCoop.Net;
using UnityEngine;
using GamePlayer = LuckyTower.CharacterSystem.Player;

namespace LuckyTowerCoop.Patches
{
    /// <summary>
    /// "Who is the player right now" for the second character, systemically. Game code reads Player.CurrentPlayer / GameManager.Player;
    /// the swap (TargetSwap) used to apply only to the second character's Update. Here it is extended to:
    ///  - coroutines started on its object (end of a banana slide, action delays);
    ///  - physics callbacks of any objects its collider entered (traps, shops, genie, mirror);
    ///  - Update/FixedUpdate/LateUpdate of components on its object (dialogue window, held weapon, color, animation).
    /// Does not work on the client: there the second character is a host puppet.
    /// </summary>
    internal static class OwnerContext
    {
        private static readonly Dictionary<long, bool> s_isSecond = new Dictionary<long, bool>();

        /// <summary>Whether the component belongs to the second (non-main) character. Cached per instance and player generation.</summary>
        public static GamePlayer SecondOwnerOf(Component c)
        {
            var p2 = CoopPlayers.P2;
            if (c == null || p2 == null || !p2 || NetSession.IsClient)
            {
                return null;
            }
            try
            {
                // when opened, the choice window goes under the NPC "icon" point - it cannot be recognized by hierarchy, so compare with the second player's window
                if (c is InteractionWindow win && p2.Components != null && p2.Components.InteractionWindow == win)
                {
                    return p2;
                }
                long key = ((long)c.GetInstanceID() << 16) ^ (uint)CoopPlayers.Generation;
                if (!s_isSecond.TryGetValue(key, out bool second))
                {
                    if (s_isSecond.Count > 8000)
                    {
                        s_isSecond.Clear();
                    }
                    var t = c.transform;
                    second = t != null && (t == p2.transform || t.IsChildOf(p2.transform));
                    s_isSecond[key] = second;
                }
                return second ? p2 : null;
            }
            catch (Exception)
            {
                return null; // object is being destroyed (scene change) - no swap needed
            }
        }
    }

    /// <summary>Coroutines of the second character run under a swap of the "current player" to them.</summary>
    [HarmonyPatch(typeof(MonoBehaviour), nameof(MonoBehaviour.StartCoroutine), typeof(IEnumerator))]
    internal static class CoroutineContextPatch
    {
        private static void Prefix(MonoBehaviour __instance, ref IEnumerator routine)
        {
            if (routine == null || routine is SwappedRoutine)
            {
                return;
            }
            var owner = OwnerContext.SecondOwnerOf(__instance);
            // a coroutine on a foreign object (banana, trap, shop) started while the "player" was swapped to the second one inherits the swap
            if (owner == null && TargetSwap.InSwap && !NetSession.IsClient && CoopPlayers.P2 != null && GamePlayer.VonWansts[0] == CoopPlayers.P2)
            {
                owner = CoopPlayers.P2;
            }
            if (owner != null)
            {
                routine = new SwappedRoutine(routine, owner);
            }
        }
    }

    internal sealed class SwappedRoutine : IEnumerator
    {
        private readonly IEnumerator m_inner;
        private readonly GamePlayer m_owner;

        public SwappedRoutine(IEnumerator inner, GamePlayer owner)
        {
            m_inner = inner;
            m_owner = owner;
        }

        public object Current { get; private set; }

        public bool MoveNext()
        {
            // the owner became the main player (partner died) - the swap is no longer needed
            if (m_owner == null || m_owner != CoopPlayers.P2)
            {
                bool r0 = m_inner.MoveNext();
                Current = m_inner.Current;
                return r0;
            }
            var st = TargetSwap.SwapTo(m_owner);
            try
            {
                bool r = m_inner.MoveNext();
                var cur = m_inner.Current;
                Current = cur is IEnumerator nested && !(cur is SwappedRoutine) ? new SwappedRoutine(nested, m_owner) : cur;
                return r;
            }
            finally
            {
                TargetSwap.Restore(st);
            }
        }

        public void Reset() => m_inner.Reset();
    }

    /// <summary>Physics callbacks: if the second character's collider entered, the "player" = them for the duration of the callback.</summary>
    [HarmonyPatch]
    internal static class TriggerContextPatches
    {
        private static readonly string[] s_names =
        {
            "OnTriggerEnter2D", "OnTriggerExit2D", "OnTriggerStay2D", "OnCollisionEnter2D", "OnCollisionExit2D", "OnCollisionStay2D",
        };

        private static IEnumerable<MethodBase> TargetMethods()
        {
            int n = 0;
            foreach (var t in typeof(GameManager).Assembly.GetTypes())
            {
                if (!typeof(MonoBehaviour).IsAssignableFrom(t))
                {
                    continue;
                }
                foreach (var name in s_names)
                {
                    var m = t.GetMethod(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
                    if (m != null && m.GetMethodBody() != null && m.GetParameters().Length == 1)
                    {
                        n++;
                        yield return m;
                    }
                }
            }
            Plugin.Log.LogInfo($"[Context] physics callbacks patched: {n}");
        }

        private static void Prefix(object[] __args, out TargetSwap.State __state)
        {
            __state = default;
            var p2 = CoopPlayers.P2;
            if (p2 == null || NetSession.IsClient || __args == null || __args.Length == 0)
            {
                return;
            }
            var col = __args[0] as Collider2D ?? (__args[0] as Collision2D)?.collider;
            if (col != null && CoopPlayers.IsP2Collider(col))
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

    /// <summary>Components on the second character's object (dialogue window, held weapon, recoloring, etc.): their Update runs under the swap.</summary>
    [HarmonyPatch]
    internal static class ComponentContextPatches
    {
        private static readonly string[] s_names = { "Update", "FixedUpdate", "LateUpdate" };

        private static readonly HashSet<Type> s_skip = new HashSet<Type>
        {
            typeof(GamePlayer), typeof(Enemy), typeof(NPC), typeof(Character), typeof(FlyingBehaviour),
        };

        private static IEnumerable<MethodBase> TargetMethods()
        {
            int n = 0;
            foreach (var t in typeof(GameManager).Assembly.GetTypes())
            {
                if (!typeof(MonoBehaviour).IsAssignableFrom(t) || s_skip.Contains(t) || typeof(Trap).IsAssignableFrom(t) && !typeof(LuckyTower.Items.SpecialItem).IsAssignableFrom(t))
                {
                    continue; // traps are not tied to the player; trap items (lamp, potions) are
                }
                foreach (var name in s_names)
                {
                    var m = t.GetMethod(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
                    if (m != null && m.GetMethodBody() != null && m.GetParameters().Length == 0)
                    {
                        n++;
                        yield return m;
                    }
                }
            }
            Plugin.Log.LogInfo($"[Context] component updates patched: {n}");
        }

        private static void Prefix(MonoBehaviour __instance, out TargetSwap.State __state)
        {
            var owner = OwnerContext.SecondOwnerOf(__instance);
            __state = owner != null ? TargetSwap.SwapTo(owner) : default;
        }

        private static Exception Finalizer(TargetSwap.State __state, Exception __exception)
        {
            TargetSwap.Restore(__state);
            return __exception;
        }
    }
}
