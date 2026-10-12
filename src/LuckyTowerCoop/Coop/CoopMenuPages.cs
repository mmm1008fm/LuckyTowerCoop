using System;
using BepInEx.Configuration;
using LuckyTowerCoop.Net;
using UnityEngine;
using GamePlayer = LuckyTower.CharacterSystem.Player;

namespace LuckyTowerCoop.Coop
{
    /// <summary>
    /// The Settings and Players tabs of the F7 menu. Values are written to the config immediately (BepInEx saves the file itself).
    /// Rules (difficulty, revive, pulling a lagging player) apply on the host / in local play - read-only for the client.
    /// </summary>
    internal static class CoopMenuPages
    {
        private static GUIStyle s_header;

        public static void DrawSettings()
        {
            bool client = NetSession.IsClient;
            bool enabled = GUI.enabled;

            Header("Two-player difficulty");
            if (client)
            {
                Note("Difficulty and rules are set by the host - they have no effect here.");
            }
            GUI.enabled = enabled && !client;
            Set(CoopConfig.EnemyHealthMultiplier, Slider("Enemy health", CoopConfig.EnemyHealthMultiplier.Value, 0.25f, 5f, 0.25f, v => $"x{v:0.##}"));
            Set(CoopConfig.ExtraEnemies, Slider("Extra monsters", CoopConfig.ExtraEnemies.Value, 0f, 3f, 0.25f, v => v <= 0f ? "none" : $"+{v * 100f:0}%"));
            Note("Applies to rooms you haven't entered yet.");

            Header("Partner revive");
            Set(CoopConfig.KillsToRevive, Mathf.RoundToInt(Slider("Kills to revive", CoopConfig.KillsToRevive.Value, 1f, 20f, 1f, v => $"{v:0}")));
            Set(CoopConfig.ReviveOnNewFloor, GUILayout.Toggle(CoopConfig.ReviveOnNewFloor.Value, " Revive on reaching a new floor"));

            Header("Camera");
            Set(CoopConfig.LeashSeconds, Slider("Pull lagging player after", CoopConfig.LeashSeconds.Value, 0f, 10f, 0.5f, v => v <= 0f ? "never" : $"{v:0.#} s"));
            GUI.enabled = enabled;
            Set(CoopConfig.ShowNames, GUILayout.Toggle(CoopConfig.ShowNames.Value, " Name tags above characters"));

            if (NetSession.Role == NetRole.None)
            {
                Header("Second player on this PC");
                bool kb = CoopConfig.P2Device.Value == P2DeviceMode.Keyboard;
                GUILayout.BeginHorizontal();
                GUILayout.Label("Controls:", GUILayout.Width(110));
                if (GUILayout.Toggle(!kb, " Gamepad", GUILayout.Width(110)) && kb)
                {
                    CoopConfig.P2Device.Value = P2DeviceMode.Gamepad;
                    CoopInput.MarkDirty();
                }
                if (GUILayout.Toggle(kb, " Keyboard (both players on keyboard)") && !kb)
                {
                    CoopConfig.P2Device.Value = P2DeviceMode.Keyboard;
                    CoopInput.MarkDirty();
                }
                GUILayout.EndHorizontal();
                if (kb)
                {
                    Note("Player 2: arrow keys, K jump, J attack, L pick up, U throw, I use, O swap hands, P drop, 7/8/9 answers. Player 1 uses the keyboard + mouse scheme.");
                }
                Set(CoopConfig.AutoSpawnP2, GUILayout.Toggle(CoopConfig.AutoSpawnP2.Value, " Second player spawns automatically"));
                Set(CoopConfig.SpawnInVillage, GUILayout.Toggle(CoopConfig.SpawnInVillage.Value, " ...also in the village"));
            }

            Header("Network");
            Set(CoopConfig.NetPauseOnDisconnect, GUILayout.Toggle(CoopConfig.NetPauseOnDisconnect.Value, " Pause until a disconnected partner returns"));
            Set(CoopConfig.NetPredict, GUILayout.Toggle(CoopConfig.NetPredict.Value, " Client-side movement prediction (move instantly on key press)"));
            Set(CoopConfig.NetInterp, GUILayout.Toggle(CoopConfig.NetInterp.Value, " Smooth world display on the client (interpolation buffer)"));
            Set(CoopConfig.NetReconnectSeconds, Slider("Continue without them after", CoopConfig.NetReconnectSeconds.Value, 0f, 600f, 30f, v => v <= 0f ? "I decide" : $"{v / 60f:0.#} min"));

            GUILayout.Space(8);
            Note($"Menu key: {CoopConfig.NetMenuKey.Value}. Changes are saved immediately to BepInEx\\config\\{Plugin.Guid}.cfg");
        }

        public static void DrawPlayers()
        {
            bool client = NetSession.IsClient;
            Header("Characters");
            DrawPlayerLine(CoopPlayers.P1, NetSession.Role == NetRole.None ? "Player 1" : "You", CoopGold.SeatOf(true));
            DrawPlayerLine(CoopPlayers.P2, NetSession.Role == NetRole.None ? "Player 2" : Partner(), CoopGold.SeatOf(false));

            DrawCoins();

            if (client)
            {
                GUILayout.Space(6);
                Note("Character actions are available to the host. If something got out of sync, use Resync on the Online tab.");
                return;
            }

            Header("Actions");
            if (CoopLife.SomeoneDead)
            {
                GUILayout.Label($"Partner died: {CoopLife.Kills}/{CoopConfig.KillsToRevive.Value} kills" + (CoopConfig.ReviveOnNewFloor.Value ? " or a new floor" : ""));
                if (GUILayout.Button("Revive now", GUILayout.Height(28)))
                {
                    CoopLife.ReviveNow();
                }
            }
            GUI.enabled = CoopPlayers.P1 != null && CoopPlayers.P2 != null;
            if (GUILayout.Button("Pull partner to me (if stuck)", GUILayout.Height(28)))
            {
                CoopPlayers.TeleportP2ToP1("menu");
            }
            GUI.enabled = true;

            if (NetSession.Role == NetRole.None)
            {
                var p1 = CoopPlayers.P1;
                if (CoopPlayers.P2 == null && !CoopLife.SomeoneDead)
                {
                    GUI.enabled = p1 != null && !p1.Stats.Dead;
                    if (GUILayout.Button("Add second player", GUILayout.Height(28)))
                    {
                        CoopPlayers.SpawnP2(p1.transform.position + Vector3.right * 0.8f);
                    }
                    GUI.enabled = true;
                }
                else if (CoopPlayers.P2 != null && GUILayout.Button("Remove second player", GUILayout.Height(28)))
                {
                    CoopPlayers.Despawn();
                }
            }
        }

        private static void DrawCoins()
        {
            Header("Gold");
            bool online = NetSession.Role != NetRole.None;
            var p1 = CoopPlayers.P1;
            var p2 = CoopPlayers.P2;
            bool both = p1 != null && p2 != null;
            if (!both)
            {
                Note("Gold can be given when two players are in the game.");
                return;
            }
            // online: only your own character (the main one: P1 on the host, the client's own on the client); local: each gives to the other
            DrawGiveRow(online ? "You" : "Player 1", p1, CoopGold.SeatOf(true));
            if (!online)
            {
                DrawGiveRow("Player 2", p2, CoopGold.SeatOf(false));
            }
        }

        private static void DrawGiveRow(string who, GamePlayer mine, Seat seat)
        {
            int have = CoopGold.TotalOf(mine, seat);
            bool enabled = GUI.enabled;
            GUILayout.BeginHorizontal();
            GUILayout.Label($"{who}: {have}", GUILayout.Width(110));
            GUI.enabled = enabled && have > 0 && !mine.Stats.Dead;
            if (GUILayout.Button("Give 1", GUILayout.Width(110)))
            {
                CoopGold.GiveToPartner(mine, 1);
            }
            if (GUILayout.Button("Give 10", GUILayout.Width(110)))
            {
                CoopGold.GiveToPartner(mine, 10);
            }
            if (GUILayout.Button("Give all", GUILayout.Width(110)))
            {
                CoopGold.GiveToPartner(mine, 1000);
            }
            GUI.enabled = enabled;
            GUILayout.EndHorizontal();
        }

        private static string Partner()
        {
            string name = NetSession.Instance != null ? NetSession.Instance.PeerName : null;
            return string.IsNullOrEmpty(name) ? "Partner" : name;
        }

        private static void DrawPlayerLine(GamePlayer p, string who, Seat seat)
        {
            if (p == null)
            {
                GUILayout.Label($"{who}: -");
                return;
            }
            var s = p.Stats;
            string hands = (s.HasWeapon ? s.WeaponItem.ItemName : "empty") + (s.ItemInSecondHand != null ? " + " + s.ItemInSecondHand.ItemName : "");
            GUILayout.Label($"{who}: {(s.Dead ? "dead" : "alive")} - in hands: {hands} - gold: {CoopGold.TotalOf(p, seat)}");
        }

        // ---------------- widgets ----------------

        private static void Header(string text)
        {
            if (s_header == null)
            {
                s_header = new GUIStyle(GUI.skin.label) { fontStyle = FontStyle.Bold, fontSize = 15 };
            }
            GUILayout.Space(6);
            GUILayout.Label(text, s_header);
        }

        private static void Note(string text)
        {
            var c = GUI.contentColor;
            GUI.contentColor = new Color(0.8f, 0.8f, 0.8f);
            GUILayout.Label(text);
            GUI.contentColor = c;
        }

        private static float Slider(string label, float value, float min, float max, float step, Func<float, string> format)
        {
            GUILayout.BeginHorizontal();
            GUILayout.Label(label, GUILayout.Width(220));
            bool changedBefore = GUI.changed;
            GUI.changed = false;
            float v = GUILayout.HorizontalSlider(value, min, max, GUILayout.Width(200));
            bool moved = GUI.changed;
            GUI.changed |= changedBefore;
            // leave a config value outside the slider range untouched until the slider is moved
            v = moved ? Mathf.Clamp(Mathf.Round(v / step) * step, min, max) : value;
            GUILayout.Label(format(v), GUILayout.Width(90));
            GUILayout.EndHorizontal();
            return v;
        }

        private static void Set(ConfigEntry<float> e, float v)
        {
            if (!Mathf.Approximately(e.Value, v))
            {
                e.Value = v;
            }
        }

        private static void Set(ConfigEntry<int> e, int v)
        {
            if (e.Value != v)
            {
                e.Value = v;
            }
        }

        private static void Set(ConfigEntry<bool> e, bool v)
        {
            if (e.Value != v)
            {
                e.Value = v;
            }
        }
    }
}
