using System.Collections.Generic;
using UnityEngine;

namespace LuckyTowerCoop.Coop
{
    /// <summary>Источник «нажатий», который подмешивается к Rewired-игроку (скрипт для тестов, сеть).</summary>
    public interface IButtonSource
    {
        bool GetButton(int actionId);
        bool GetButtonDown(int actionId);
        bool GetButtonUp(int actionId);
    }

    /// <summary>
    /// Реестр подмешиваемых источников по id Rewired-игрока.
    /// Патчи Rewired.Player.GetButton* делают OR с реальным устройством и всеми источниками.
    /// </summary>
    internal static class InputOverrides
    {
        private static readonly Dictionary<int, List<IButtonSource>> s_sources = new Dictionary<int, List<IButtonSource>>();
        private static int s_count;

        public static bool Any => s_count > 0;

        public static void Add(int rewiredPlayerId, IButtonSource source)
        {
            if (!s_sources.TryGetValue(rewiredPlayerId, out var list))
            {
                s_sources[rewiredPlayerId] = list = new List<IButtonSource>();
            }
            if (!list.Contains(source))
            {
                list.Add(source);
                s_count++;
            }
        }

        public static void Remove(IButtonSource source)
        {
            foreach (var list in s_sources.Values)
            {
                if (list.Remove(source))
                {
                    s_count--;
                }
            }
        }

        public static bool GetButton(int rewiredPlayerId, int actionId)
        {
            if (!s_sources.TryGetValue(rewiredPlayerId, out var list))
            {
                return false;
            }
            for (int i = 0; i < list.Count; i++)
            {
                if (list[i].GetButton(actionId))
                {
                    return true;
                }
            }
            return false;
        }

        public static bool GetButtonDown(int rewiredPlayerId, int actionId)
        {
            if (!s_sources.TryGetValue(rewiredPlayerId, out var list))
            {
                return false;
            }
            for (int i = 0; i < list.Count; i++)
            {
                if (list[i].GetButtonDown(actionId))
                {
                    return true;
                }
            }
            return false;
        }

        public static bool GetButtonUp(int rewiredPlayerId, int actionId)
        {
            if (!s_sources.TryGetValue(rewiredPlayerId, out var list))
            {
                return false;
            }
            for (int i = 0; i < list.Count; i++)
            {
                if (list[i].GetButtonUp(actionId))
                {
                    return true;
                }
            }
            return false;
        }
    }

    /// <summary>Скриптуемый ввод для отладки: удержание действий на время, края Down/Up считаются по кадрам.</summary>
    public class ScriptedButtonSource : IButtonSource
    {
        private readonly Dictionary<int, float> m_holdUntil = new Dictionary<int, float>();
        private HashSet<int> m_current = new HashSet<int>();
        private HashSet<int> m_previous = new HashSet<int>();
        private int m_lastFrame = -1;

        public void Hold(int actionId, float seconds)
        {
            m_holdUntil[actionId] = Time.realtimeSinceStartup + seconds;
        }

        public void Release(int actionId) => m_holdUntil.Remove(actionId);

        public void ReleaseAll() => m_holdUntil.Clear();

        private void Tick()
        {
            if (Time.frameCount == m_lastFrame)
            {
                return;
            }
            m_lastFrame = Time.frameCount;

            var swap = m_previous;
            m_previous = m_current;
            m_current = swap;
            m_current.Clear();

            float now = Time.realtimeSinceStartup;
            List<int> expired = null;
            foreach (var kv in m_holdUntil)
            {
                if (kv.Value > now)
                {
                    m_current.Add(kv.Key);
                }
                else
                {
                    (expired ??= new List<int>()).Add(kv.Key);
                }
            }
            if (expired != null)
            {
                foreach (int id in expired)
                {
                    m_holdUntil.Remove(id);
                }
            }
        }

        public bool GetButton(int actionId)
        {
            Tick();
            return m_current.Contains(actionId);
        }

        public bool GetButtonDown(int actionId)
        {
            Tick();
            return m_current.Contains(actionId) && !m_previous.Contains(actionId);
        }

        public bool GetButtonUp(int actionId)
        {
            Tick();
            return !m_current.Contains(actionId) && m_previous.Contains(actionId);
        }
    }

    /// <summary>Имена действий Rewired (RewiredConsts.InputActions) для отладочных команд.</summary>
    internal static class ActionNames
    {
        public static readonly Dictionary<string, int> Map = new Dictionary<string, int>
        {
            { "submit", 0 }, { "cancel", 1 },
            { "left", 2 }, { "right", 23 }, { "up", 21 }, { "down", 22 },
            { "jump", 3 }, { "attack", 6 }, { "throw", 7 }, { "pickup", 8 },
            { "use", 9 }, { "swap", 10 }, { "talk", 11 },
            { "opt1", 13 }, { "opt2", 14 }, { "opt3", 15 },
            { "menu", 16 }, { "drop", 26 },
        };
    }
}
