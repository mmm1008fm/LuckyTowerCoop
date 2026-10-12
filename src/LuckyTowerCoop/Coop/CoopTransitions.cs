using System;
using System.Collections.Generic;
using LuckyTower;
using LuckyTower.LevelLayers;
using LuckyTower.RoomGeneration;
using UnityEngine;
using UnityEngine.SceneManagement;
using GamePlayer = LuckyTower.CharacterSystem.Player;
using Object = UnityEngine.Object;

namespace LuckyTowerCoop.Coop
{
    /// <summary>
    /// Voting on transitions (doors, side passages, hatch to the next segment):
    /// the first player "enters" and waits hidden (with "1/2" above the entrance), the transition runs
    /// when the second one enters the same place. The transition is always executed by P1 (GameManager.Player) using the game's normal code,
    /// P2 is moved to them after the room change.
    /// </summary>
    public class CoopTransitions : MonoBehaviour
    {
        private sealed class Wait
        {
            public GamePlayer Player;
            public Object Key;
            public Vector3 LabelPos;
            public bool CanCancel;
            public Vector3 CancelPush;
            /// <summary>How to perform the transition for P1 (P1 is no longer hidden).</summary>
            public Action CommitAsP1;
        }

        private static readonly System.Reflection.MethodInfo s_doorDownAction = HarmonyLib.AccessTools.Method(typeof(EnterDoorCommand), "DoorDownAction");

        private static readonly List<Wait> s_waits = new List<Wait>();
        private static bool s_bypass;

        // Deferred move of P2 after a transition executed by P1
        private static GamePlayer s_follower;
        private static Room s_roomAtCommit;
        private static bool s_sawInEnterDoor;
        private static bool s_teleported;
        private static float s_commitTime;

        private GUIStyle m_labelStyle;

        public static bool IsWaiting(GamePlayer p)
        {
            if (p == null)
            {
                return false;
            }
            if (p == s_follower)
            {
                return true;
            }
            foreach (var w in s_waits)
            {
                if (w.Player == p)
                {
                    return true;
                }
            }
            return false;
        }

        private void Start()
        {
            SceneManager.activeSceneChanged += (_, __) => ClearAll();
        }

        // ---------------- entry points from patches ----------------

        /// <summary>The player pressed "up" at a door. true - run the original code.</summary>
        public static bool OnDoorPressed(EnterDoorCommand cmd, GamePlayer player, Door door)
        {
            if (s_bypass || door == null || !door.enabled)
            {
                return true;
            }
            var gm = GameManager.InstanceNoWarnings;
            if (gm == null || gm.InEnterDoor)
            {
                return true;
            }
            return Arrive(player, door, door.DoorCenter.position + Vector3.up * 2.2f, canCancel: true, cancelPush: Vector3.zero,
                commitAsP1: () =>
                {
                    // P1 is standing at the door: repeat their action (the command's m_results still contains this door)
                    var p1Cmd = FindEnterDoorCommand(CoopPlayers.P1);
                    if (p1Cmd != null)
                    {
                        Bypass(() => s_doorDownAction.Invoke(p1Cmd, null));
                    }
                    if (!gm.InEnterDoor && door.TargetDoor != null && !door.BubbleDoor)
                    {
                        Plugin.Log.LogWarning("[Transition] P1 replay did not start door, forcing GameManager.EnterDoor");
                        gm.EnterDoor(door);
                    }
                });
        }

        /// <summary>The player entered a transition trigger (hatch/side passage). true - run the original code.</summary>
        public static bool OnTriggerTransition(GamePlayer player, Object key, Vector3 labelPos, bool canCancel, Vector3 cancelPush, Action commitAsP1)
        {
            if (s_bypass)
            {
                return true;
            }
            return Arrive(player, key, labelPos, canCancel, cancelPush, commitAsP1);
        }

        // ---------------- logic ----------------

        private static bool CoopActive(out GamePlayer p1, out GamePlayer p2)
        {
            p1 = CoopPlayers.P1;
            p2 = CoopPlayers.P2;
            return p1 != null && p2 != null && !p1.Stats.Dead && !p2.Stats.Dead && p2.isActiveAndEnabled && s_follower == null;
        }

        private static bool Arrive(GamePlayer player, Object key, Vector3 labelPos, bool canCancel, Vector3 cancelPush, Action commitAsP1)
        {
            if (!CoopActive(out var p1, out var p2) || (player != p1 && player != p2))
            {
                return true;
            }
            if (IsWaiting(player))
            {
                return false;
            }

            var other = player == p1 ? p2 : p1;
            var otherWait = s_waits.Find(w => w.Player == other);
            if (otherWait != null && otherWait.Key == key)
            {
                s_waits.Remove(otherWait);
                Plugin.Log.LogInfo($"[Transition] 2/2 at {key.name} - commit (second: P{(player == p1 ? 1 : 2)})");
                if (player == p1)
                {
                    // P2 is already hidden and waiting, P1 goes through with the normal code
                    BeginFollow(p2);
                    return true;
                }

                // The second is P2: hide them, P1 leaves the wait and performs the transition
                Hide(p2);
                Unhide(p1);
                BeginFollow(p2);
                // We may be inside P2's update (where the "current player" is swapped to P2) - the transition must see P1
                var swap = Patches.TargetSwap.SwapTo(p1);
                try
                {
                    commitAsP1();
                }
                catch (Exception e)
                {
                    Plugin.Log.LogError($"[Transition] commit failed: {e}");
                }
                finally
                {
                    Patches.TargetSwap.Restore(swap);
                }
                return false;
            }

            // The first is at the entrance - wait for the second
            Hide(player);
            s_waits.Add(new Wait { Player = player, Key = key, LabelPos = labelPos, CanCancel = canCancel, CancelPush = cancelPush, CommitAsP1 = commitAsP1 });
            Plugin.Log.LogInfo($"[Transition] 1/2 P{(player == p1 ? 1 : 2)} waits at {key.name}");
            return false;
        }

        private static void BeginFollow(GamePlayer follower)
        {
            s_follower = follower;
            s_roomAtCommit = GameManager.InstanceNoWarnings?.CurrentRoom;
            s_sawInEnterDoor = false;
            s_teleported = false;
            s_commitTime = Time.time;
        }

        private void Update()
        {
            var gm = GameManager.InstanceNoWarnings;

            // Wait cancelled / the second player is gone
            for (int i = s_waits.Count - 1; i >= 0; i--)
            {
                var w = s_waits[i];
                if (w.Player == null || w.Key == null)
                {
                    s_waits.RemoveAt(i);
                    continue;
                }
                bool otherGone = !CoopActive(out _, out _);
                var input = w.Player == CoopPlayers.P2 ? CoopInput.P2Input : w.Player.PlayerInput;
                bool cancel = w.CanCancel && input != null && (input.GetButtonDown(22) || input.GetButtonDown(3) || input.GetButtonDown(1));
                if (cancel || otherGone)
                {
                    s_waits.RemoveAt(i);
                    Unhide(w.Player);
                    if (w.CancelPush != Vector3.zero)
                    {
                        w.Player.transform.position += w.CancelPush;
                    }
                    Plugin.Log.LogInfo($"[Transition] wait cancelled ({(cancel ? "input" : "partner unavailable")}) at {w.Key.name}");
                }
            }

            UpdateFollower(gm);
        }

        private static void UpdateFollower(GameManager gm)
        {
            if (s_follower == null)
            {
                return;
            }
            var p1 = CoopPlayers.P1;
            if (gm == null || p1 == null)
            {
                return;
            }
            if (gm.InEnterDoor)
            {
                s_sawInEnterDoor = true;
            }

            bool roomChanged = gm.CurrentRoom != s_roomAtCommit;
            if (!s_teleported && roomChanged)
            {
                // P1 is moved in the same iteration as the room change - move P2 right after (while hidden)
                s_teleported = true;
                TeleportBehind(s_follower, p1);
            }

            bool finished = s_sawInEnterDoor && !gm.InEnterDoor;
            bool timeout = Time.time - s_commitTime > 20f;
            if (finished || timeout)
            {
                if (!s_teleported || timeout)
                {
                    TeleportBehind(s_follower, p1);
                }
                var f = s_follower;
                s_follower = null;
                Unhide(f);
                Plugin.Log.LogInfo($"[Transition] follower released ({(timeout ? "timeout" : "done")}) at {f.transform.position}");
            }
        }

        private static void TeleportBehind(GamePlayer follower, GamePlayer leader)
        {
            float side = leader.transform.localScale.x >= 0f ? -0.7f : 0.7f;
            follower.transform.position = leader.transform.position + new Vector3(side, 0.05f, 0f);
            follower.Components.Rigidbody.linearVelocity = Vector2.zero;
            if (LayerManager.LayersReady && LayerManager.CurrentPlayerLayer >= 0)
            {
                LayerManager.MoveToLayer(follower.gameObject, LayerManager.CurrentPlayerLayer);
            }
        }

        private static void Hide(GamePlayer p)
        {
            p.Utilities.Freeze();
            p.Stats.InputAllowed = false;
            CoopVisibility.SetHidden(p, true);
        }

        private static void Unhide(GamePlayer p)
        {
            if (p == null)
            {
                return;
            }
            CoopVisibility.SetHidden(p, false);
            p.Utilities.UnFreeze();
            p.Stats.InputAllowed = true;
        }

        /// <summary>"1/2" markers above entrances (to send to the client).</summary>
        public static void GetLabels(List<(Vector3 pos, string text)> into)
        {
            into.Clear();
            foreach (var w in s_waits)
            {
                into.Add((w.LabelPos, "1/2"));
            }
        }

        /// <summary>Show the character if left hidden (e.g. a scene change during the wait).</summary>
        public static void ForceUnhide(GamePlayer p)
        {
            s_waits.RemoveAll(w => w.Player == p);
            if (s_follower == p)
            {
                s_follower = null;
            }
            if (p != null && CoopVisibility.IsHidden(p))
            {
                Unhide(p);
            }
            else if (p != null)
            {
                p.Utilities.UnFreeze();
            }
        }

        private static void Bypass(Action a)
        {
            s_bypass = true;
            try
            {
                a();
            }
            finally
            {
                s_bypass = false;
            }
        }

        private static EnterDoorCommand FindEnterDoorCommand(GamePlayer p)
        {
            if (p == null)
            {
                return null;
            }
            foreach (var c in p.Stats.Commands)
            {
                if (c is EnterDoorCommand edc)
                {
                    return edc;
                }
            }
            return null;
        }

        private static void ClearAll()
        {
            s_waits.Clear();
            s_follower = null;
        }

        // ---------------- "1/2" above the entrance ----------------

        private void OnGUI()
        {
            if (s_waits.Count == 0)
            {
                return;
            }
            var cam = CoopCamera.CameraManagerSilent;
            if (cam == null || cam.MainCamera == null)
            {
                return;
            }
            if (m_labelStyle == null)
            {
                m_labelStyle = new GUIStyle(GUI.skin.label) { fontSize = Mathf.RoundToInt(Screen.height / 18f), fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleCenter };
            }
            foreach (var w in s_waits)
            {
                var sp = cam.MainCamera.WorldToScreenPoint(w.LabelPos);
                if (sp.z < 0f)
                {
                    continue;
                }
                var rect = new Rect(sp.x - 100f, Screen.height - sp.y - 40f, 200f, 80f);
                m_labelStyle.normal.textColor = Color.black;
                GUI.Label(new Rect(rect.x + 3f, rect.y + 3f, rect.width, rect.height), "1/2", m_labelStyle);
                m_labelStyle.normal.textColor = new Color(1f, 0.85f, 0.3f);
                GUI.Label(rect, "1/2", m_labelStyle);
            }
        }
    }
}
