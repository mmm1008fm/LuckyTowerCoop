using LuckyTowerCoop.Coop;
using UnityEngine;

namespace LuckyTowerCoop.Net
{
    /// <summary>Mod menu (F7): online, settings, players; connection status overlays.</summary>
    public class NetMenu : MonoBehaviour
    {
        private bool m_open;
        private Rect m_window = new Rect(40, 40, 600, 600);
        private Vector2 m_scroll;
        private string m_ip = "";
        private string m_port = "";
        private string m_lobbyCode = "";
        private bool m_prevCursorVisible;
        private CursorLockMode m_prevLock;
        private float m_nextFriendsRefresh;
        private GUIStyle m_big;
        private GUIStyle m_small;
        private int m_styleHeight;

        private NetSession S => NetSession.Instance;

        private void Start()
        {
            m_ip = CoopConfig.NetJoinAddress.Value;
            m_port = CoopConfig.NetPort.Value.ToString();
        }

        private void Update()
        {
            if (Input.GetKeyDown(CoopConfig.NetMenuKey.Value))
            {
                SetOpen(!m_open);
            }
            if (m_open && S?.Transport is SteamSidecarTransport steam && Time.unscaledTime >= m_nextFriendsRefresh)
            {
                m_nextFriendsRefresh = Time.unscaledTime + 4f;
                steam.RefreshFriends();
            }
        }

        /// <summary>Debug: open the menu on a tab (tab &lt; 0 closes it).</summary>
        internal void DevShow(int tab)
        {
            if (tab < 0)
            {
                SetOpen(false);
                return;
            }
            m_tab = tab;
            SetOpen(true);
        }

        private void SetOpen(bool open)
        {
            if (open == m_open)
            {
                return;
            }
            m_open = open;
            var p1 = CoopInput.P1Input;
            if (open)
            {
                m_prevCursorVisible = Cursor.visible;
                m_prevLock = Cursor.lockState;
                Cursor.visible = true;
                Cursor.lockState = CursorLockMode.None;
                if (p1 != null)
                {
                    p1.isPlaying = false; // while the menu is open, input doesn't reach the game
                }
            }
            else
            {
                Cursor.visible = m_prevCursorVisible;
                Cursor.lockState = m_prevLock;
                if (p1 != null)
                {
                    p1.isPlaying = true;
                }
            }
        }

        private void OnGUI()
        {
            if (m_big == null || m_styleHeight != Screen.height)
            {
                // font size depends on the window height (the window may have changed: fullscreen -> windowed)
                m_styleHeight = Screen.height;
                m_panelText = null;
                m_big = new GUIStyle(GUI.skin.label) { fontSize = Mathf.RoundToInt(Screen.height / 26f), fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleCenter, wordWrap = true };
                m_small = new GUIStyle(GUI.skin.label) { fontSize = Mathf.Max(12, Mathf.RoundToInt(Screen.height / 60f)) };
            }
            DrawOverlays();
            if (m_open)
            {
                GUI.skin.label.fontSize = 14;
                GUI.skin.button.fontSize = 14;
                GUI.skin.textField.fontSize = 14;
                m_window = GUILayout.Window(0x4C54, m_window, DrawWindow, $"Lucky Tower Coop {Plugin.Version}");
            }
        }

        private void DrawOverlays()
        {
            var s = S;
            if (s == null || NetSession.Role == NetRole.None)
            {
                return;
            }
            string ping = (s.Transport != null && s.Transport.PingMs >= 0 ? $" - ping {s.Transport.PingMs} ms" : "")
                + (NetSession.IsClient && s.Client != null && s.Client.InputLagMs > 0f ? $" - input->screen {s.Client.InputLagMs:F0} ms" : "")
                + (NetSession.PeerReady || NetSession.IsClient ? $" - out {s.KbOut:F1} / in {s.KbIn:F1} KB/s" : "")
                + (s.Transport is SteamSidecarTransport st && st.Stats.Valid ? " - " + st.Stats.Short() : "");
            Shadowed(new Rect(10, Screen.height - 34, Screen.width, 30), $"{(NetSession.IsHost ? "Host" : "Client")}: {s.StatusText}{ping}   [{CoopConfig.NetMenuKey.Value} - menu]", m_small, new Color(0.9f, 0.9f, 0.9f));

            if (s.CurrentState == NetSession.State.PeerLost && NetSession.IsHost)
            {
                if (!s.AloneChosen)
                {
                    DrawPeerLostPanel(s);
                }
                else
                {
                    Shadowed(new Rect(0, Screen.height * 0.04f, Screen.width, 40), "Partner disconnected - they can rejoin at any time", m_small, new Color(1f, 0.85f, 0.3f));
                }
            }
            else if (s.CurrentState == NetSession.State.Reconnecting)
            {
                Shadowed(new Rect(0, Screen.height * 0.35f, Screen.width, 200), "Connection to the host lost.\nReconnecting...", m_big, new Color(1f, 0.85f, 0.3f));
            }

            if (NetSession.IsClient)
            {
                DrawHostUi();
                if (s.Client != null && Time.unscaledTime - s.Client.RestartRequestedAt < 8f)
                {
                    Shadowed(new Rect(0, Screen.height * 0.04f, Screen.width, 40), "Restart request sent to the host - their call", m_small, new Color(1f, 0.85f, 0.3f));
                }
            }
            if (NetSession.IsHost && s.Host != null && Time.unscaledTime - s.Host.RestartAskedAt < 60f)
            {
                DrawRestartPanel(s);
            }
        }

        private void DrawRestartPanel(NetSession s)
        {
            Cursor.visible = true;
            Cursor.lockState = CursorLockMode.None;
            float w = Mathf.Min(560f, Screen.width - 40f);
            var r = new Rect((Screen.width - w) / 2f, Screen.height * 0.3f, w, 130f);
            GUI.Box(r, "");
            GUI.Box(r, "");
            Shadowed(new Rect(r.x + 10, r.y + 10, r.width - 20, 60), $"{(string.IsNullOrEmpty(s.PeerName) ? "Partner" : s.PeerName)} wants to restart the run", BigSmall(), new Color(1f, 0.9f, 0.6f));
            float bw = (r.width - 30f) / 2f;
            if (GUI.Button(new Rect(r.x + 10, r.y + r.height - 54, bw, 44), "Restart run"))
            {
                s.Host.RestartRun();
            }
            if (GUI.Button(new Rect(r.x + 20 + bw, r.y + r.height - 54, bw, 44), "No, keep playing"))
            {
                s.Host.RestartAskedAt = -100f;
            }
        }

        private void DrawPeerLostPanel(NetSession s)
        {
            Cursor.visible = true;
            Cursor.lockState = CursorLockMode.None;
            float w = Mathf.Min(720f, Screen.width - 40f);
            var r = new Rect((Screen.width - w) / 2f, Screen.height * 0.28f, w, 260f);
            GUI.Box(r, "");
            GUI.Box(r, "");
            string what = s.Host != null && s.Host.RemoteKilledByDisconnect
                ? "Their character died - their items are on the floor and can be picked up.\nIf they return, they respawn without items."
                : "If they return, they continue from where they left off.";
            float secs = Time.unscaledTime - s.LostAt;
            var label = new Rect(r.x + 10, r.y + 10, r.width - 20, 150);
            Shadowed(label, $"Partner disconnected ({Mathf.FloorToInt(secs / 60f)}:{Mathf.FloorToInt(secs % 60f):00} ago)\n{what}", m_small == null ? GUI.skin.label : BigSmall(), new Color(1f, 0.9f, 0.6f));
            float bw = (r.width - 40f) / 3f;
            float by = r.y + r.height - 60f;
            if (GUI.Button(new Rect(r.x + 10, by, bw, 44), s.PausedForPeer ? "Waiting (paused)" : "Wait (pause)"))
            {
                s.WaitForPeer();
            }
            if (GUI.Button(new Rect(r.x + 20 + bw, by, bw, 44), "Play solo"))
            {
                s.ContinueAlone();
            }
            if (GUI.Button(new Rect(r.x + 30 + bw * 2, by, bw, 44), "Close session"))
            {
                s.Stop("host closed the session");
            }
        }

        private GUIStyle m_panelText;

        private GUIStyle BigSmall()
        {
            if (m_panelText == null)
            {
                m_panelText = new GUIStyle(GUI.skin.label) { fontSize = Mathf.Max(16, Mathf.RoundToInt(Screen.height / 40f)), alignment = TextAnchor.UpperCenter, wordWrap = true, fontStyle = FontStyle.Bold };
            }
            return m_panelText;
        }

        private void DrawHostUi()
        {
            var cam = CoopCamera.CameraManagerSilent;
            if (cam != null && cam.MainCamera != null)
            {
                foreach (var l in NetClient.HostLabels)
                {
                    var sp = cam.MainCamera.WorldToScreenPoint(l.pos);
                    if (sp.z > 0f)
                    {
                        Shadowed(new Rect(sp.x - 100f, Screen.height - sp.y - 40f, 200f, 80f), l.text, m_big, new Color(1f, 0.85f, 0.3f));
                    }
                }
            }
            if (NetClient.ReviveKills >= 0)
            {
                string text = $"Revive: {NetClient.ReviveKills}/{NetClient.ReviveNeed} kills" + (NetClient.ReviveOnFloor ? " or a new floor" : "");
                Shadowed(new Rect(0f, Screen.height * 0.06f, Screen.width, 60f), text, m_big, new Color(1f, 0.85f, 0.3f));
            }
        }

        private static void Shadowed(Rect r, string text, GUIStyle style, Color color)
        {
            var old = style.normal.textColor;
            style.normal.textColor = Color.black;
            GUI.Label(new Rect(r.x + 2, r.y + 2, r.width, r.height), text, style);
            style.normal.textColor = color;
            GUI.Label(r, text, style);
            style.normal.textColor = old;
        }

        private static readonly string[] s_tabs = { "Online", "Settings", "Players" };
        private int m_tab;

        private void DrawWindow(int id)
        {
            var s = S;
            m_tab = GUILayout.Toolbar(m_tab, s_tabs, GUILayout.Height(28));
            GUILayout.Space(6);
            m_scroll = GUILayout.BeginScrollView(m_scroll);
            switch (m_tab)
            {
                case 1:
                    CoopMenuPages.DrawSettings();
                    break;
                case 2:
                    CoopMenuPages.DrawPlayers();
                    break;
                default:
                    GUILayout.Label($"Status: {(NetSession.Role == NetRole.None ? "offline" : s.StatusText)}");
                    if (s.Transport != null)
                    {
                        GUILayout.Label($"Transport: {s.Transport.Status}");
                    }
                    GUILayout.Space(8);
                    if (NetSession.Role == NetRole.None)
                    {
                        DrawStart(s);
                    }
                    else
                    {
                        DrawSession(s);
                    }
                    break;
            }
            GUILayout.EndScrollView();
            if (GUILayout.Button("Close"))
            {
                SetOpen(false);
            }
            GUI.DragWindow();
        }

        private void DrawStart(NetSession s)
        {
            GUILayout.BeginHorizontal();
            GUILayout.Label("Mode:", GUILayout.Width(60));
            bool steam = CoopConfig.NetTransport.Value == NetTransportKind.Steam;
            if (GUILayout.Toggle(steam, " Steam (Spacewar)", GUILayout.Width(170)) && !steam)
            {
                CoopConfig.NetTransport.Value = NetTransportKind.Steam;
            }
            if (GUILayout.Toggle(!steam, " Direct (IP)") && steam)
            {
                CoopConfig.NetTransport.Value = NetTransportKind.Direct;
            }
            GUILayout.EndHorizontal();
            GUILayout.Space(6);

            if (GUILayout.Button("Host a game", GUILayout.Height(32)))
            {
                s.StartHost(CoopConfig.NetTransport.Value);
            }
            GUILayout.Space(10);

            if (CoopConfig.NetTransport.Value == NetTransportKind.Steam)
            {
                GUILayout.Label("Join a friend:");
                if (GUILayout.Button("Open friends list (Steam)"))
                {
                    s.StartJoinSteam(0);
                }
                GUILayout.BeginHorizontal();
                GUILayout.Label("Lobby code:", GUILayout.Width(90));
                m_lobbyCode = GUILayout.TextField(m_lobbyCode, GUILayout.Width(220));
                if (GUILayout.Button("Join") && ulong.TryParse(m_lobbyCode.Trim(), out var lobby))
                {
                    s.StartJoinSteam(lobby);
                }
                GUILayout.EndHorizontal();
            }
            else
            {
                GUILayout.Label($"The host listens on UDP port {CoopConfig.NetPort.Value} (over the internet: Tailscale or port forwarding).");
                GUILayout.BeginHorizontal();
                GUILayout.Label("IP:", GUILayout.Width(30));
                m_ip = GUILayout.TextField(m_ip, GUILayout.Width(200));
                GUILayout.Label("Port:", GUILayout.Width(40));
                m_port = GUILayout.TextField(m_port, GUILayout.Width(70));
                if (GUILayout.Button("Connect") && int.TryParse(m_port, out var port))
                {
                    s.StartJoinDirect(m_ip.Trim(), port);
                }
                GUILayout.EndHorizontal();
            }
        }

        private void DrawSession(NetSession s)
        {
            var steam = s.Transport as SteamSidecarTransport;
            if (NetSession.IsHost && steam != null && steam.LobbyId != 0)
            {
                GUILayout.BeginHorizontal();
                GUILayout.Label($"Lobby code: {steam.LobbyId}");
                if (GUILayout.Button("Copy", GUILayout.Width(110)))
                {
                    GUIUtility.systemCopyBuffer = steam.LobbyId.ToString();
                }
                GUILayout.EndHorizontal();
                if (!NetSession.PeerReady)
                {
                    GUILayout.Label("Invite a friend (they get a Steam invite; it's easier for them to open the friends list in this menu):");
                    foreach (var f in steam.FriendsOnline)
                    {
                        GUILayout.BeginHorizontal();
                        GUILayout.Label(f.name);
                        if (GUILayout.Button("Invite", GUILayout.Width(110)))
                        {
                            steam.Invite(f.id);
                        }
                        GUILayout.EndHorizontal();
                    }
                }
            }

            if (NetSession.IsClient && steam != null && s.CurrentState == NetSession.State.Connecting)
            {
                GUILayout.Label("Friends with the game open:");
                if (steam.FriendLobbies.Count == 0)
                {
                    GUILayout.Label("  nobody yet (the list updates automatically)");
                }
                foreach (var l in steam.FriendLobbies)
                {
                    GUILayout.BeginHorizontal();
                    GUILayout.Label(l.name);
                    if (GUILayout.Button("Join", GUILayout.Width(90)))
                    {
                        steam.Command($"join {l.lobby}");
                    }
                    GUILayout.EndHorizontal();
                }
                GUILayout.BeginHorizontal();
                GUILayout.Label("Lobby code:", GUILayout.Width(90));
                m_lobbyCode = GUILayout.TextField(m_lobbyCode, GUILayout.Width(220));
                if (GUILayout.Button("Join") && ulong.TryParse(m_lobbyCode.Trim(), out var lobby))
                {
                    steam.Command($"join {lobby}");
                }
                GUILayout.EndHorizontal();
            }

            if (NetSession.IsHost && s.CurrentState == NetSession.State.PeerLost)
            {
                GUILayout.Label("Partner disconnected - they can rejoin while the session is open.");
                GUILayout.BeginHorizontal();
                if (GUILayout.Button("Wait (pause)", GUILayout.Height(30)))
                {
                    s.WaitForPeer();
                }
                if (GUILayout.Button("Play solo", GUILayout.Height(30)))
                {
                    s.ContinueAlone();
                }
                GUILayout.EndHorizontal();
            }

            if (s.CurrentState == NetSession.State.Connected)
            {
                GUILayout.Space(6);
                if (GUILayout.Button("Resync (if something got out of sync)", GUILayout.Height(28)))
                {
                    if (NetSession.IsHost)
                    {
                        s.Host?.ForceResync();
                    }
                    else
                    {
                        s.Client?.RequestResync("manual");
                    }
                }
            }

            GUILayout.Space(10);
            if (GUILayout.Button(NetSession.IsHost ? "Close session" : "Disconnect", GUILayout.Height(28)))
            {
                s.Stop("left via menu");
            }
        }
    }
}
