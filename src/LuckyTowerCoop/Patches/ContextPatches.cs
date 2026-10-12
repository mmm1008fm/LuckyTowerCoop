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
    /// «Кто сейчас игрок» для второго персонажа — системно. Код игры читает Player.CurrentPlayer / GameManager.Player;
    /// подмена (TargetSwap) раньше стояла только на Update второго персонажа. Здесь она расширяется на:
    ///  • корутины, запущенные на его объекте (конец скольжения на банане, задержки действий);
    ///  • физические колбэки любых объектов, в которые вошёл его коллайдер (ловушки, магазины, джинн, зеркало);
    ///  • Update/FixedUpdate/LateUpdate компонентов на его объекте (окно диалога, оружие в руке, цвет, анимация).
    /// На клиенте не работает: там второй персонаж — марионетка хоста.
    /// </summary>
    internal static class OwnerContext
    {
        private static readonly Dictionary<long, bool> s_isSecond = new Dictionary<long, bool>();

        /// <summary>Принадлежит ли компонент второму (неглавному) персонажу. Кэш по экземпляру и поколению игроков.</summary>
        public static GamePlayer SecondOwnerOf(Component c)
        {
            var p2 = CoopPlayers.P2;
            if (c == null || p2 == null || !p2 || NetSession.IsClient)
            {
                return null;
            }
            try
            {
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
                return null; // объект уничтожается (смена сцены) — подмена не нужна
            }
        }
    }

    /// <summary>Корутины второго персонажа выполняются под подменой «текущего игрока» на него.</summary>
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
            // корутина на чужом объекте (банан, ловушка, магазин), запущенная пока «игрок» подменён на второго, — наследует подмену
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
            // владелец стал главным (напарник погиб) — подмена больше не нужна
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

    /// <summary>Физические колбэки: если вошёл коллайдер второго персонажа — на время колбэка «игрок» = он.</summary>
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

    /// <summary>Компоненты на объекте второго персонажа (окно диалога, оружие в руке, перекраска и т. п.): их Update — под подменой.</summary>
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
                    continue; // ловушки к игроку не привязаны; предметы-ловушки (лампа, зелья) — да
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
