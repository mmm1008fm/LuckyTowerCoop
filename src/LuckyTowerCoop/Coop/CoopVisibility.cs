using System.Collections.Generic;
using LuckyTower.CharacterSystem;
using UnityEngine;

namespace LuckyTowerCoop.Coop
{
    /// <summary>
    /// Спрятать/показать персонажа целиком (ожидание у двери). Выключить только объект аниматора мало:
    /// трусы, броня и эффекты рисуются отдельными объектами — гасим все включённые рендеры и потом включаем ровно их.
    /// </summary>
    internal static class CoopVisibility
    {
        private static readonly Dictionary<Character, List<Renderer>> s_hidden = new Dictionary<Character, List<Renderer>>();

        public static bool IsHidden(Character c)
        {
            if (c == null)
            {
                return false;
            }
            var gfx = Graphics(c);
            return s_hidden.ContainsKey(c) || (gfx != null && !gfx.activeSelf);
        }

        public static void SetHidden(Character c, bool hidden)
        {
            if (c == null)
            {
                return;
            }
            var gfx = Graphics(c);
            if (hidden)
            {
                if (!s_hidden.ContainsKey(c))
                {
                    Cleanup();
                    // рендеры внутри графики аниматора не трогаем: их включение ведут анимации, а при повторном включении
                    // аниматор запоминает текущее состояние как исходное — выключенные нами части остались бы выключенными навсегда
                    var list = new List<Renderer>();
                    foreach (var r in c.GetComponentsInChildren<Renderer>(false))
                    {
                        if (r.enabled && (gfx == null || !r.transform.IsChildOf(gfx.transform)))
                        {
                            r.enabled = false;
                            list.Add(r);
                        }
                    }
                    s_hidden[c] = list;
                }
                if (gfx != null && gfx.activeSelf)
                {
                    gfx.SetActive(false);
                }
                return;
            }
            if (s_hidden.TryGetValue(c, out var renderers))
            {
                foreach (var r in renderers)
                {
                    if (r != null)
                    {
                        r.enabled = true;
                    }
                }
                s_hidden.Remove(c);
            }
            if (gfx != null && !gfx.activeSelf)
            {
                gfx.SetActive(true);
            }
        }

        private static GameObject Graphics(Character c) =>
            c.Components != null && c.Components.Animator != null ? c.Components.Animator.gameObject : null;

        private static void Cleanup()
        {
            if (s_hidden.Count < 8)
            {
                return;
            }
            var dead = new List<Character>();
            foreach (var k in s_hidden.Keys)
            {
                if (k == null)
                {
                    dead.Add(k);
                }
            }
            foreach (var k in dead)
            {
                s_hidden.Remove(k);
            }
        }
    }
}
