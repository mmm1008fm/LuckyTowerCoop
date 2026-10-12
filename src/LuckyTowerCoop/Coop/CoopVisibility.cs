using System.Collections.Generic;
using LuckyTower.CharacterSystem;
using UnityEngine;

namespace LuckyTowerCoop.Coop
{
    /// <summary>
    /// Hide/show a whole character (waiting at a door). Disabling just the animator object is not enough:
    /// shorts, armor and effects are drawn by separate objects - we turn off all enabled renderers and later turn exactly those back on.
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
                    // we do not touch renderers inside the animator graphics: their enabling is driven by animations, and on re-enabling
                    // the animator remembers the current state as the initial one - parts we turned off would stay off forever
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
