using LuckyTowerCoop.Coop;
using UnityEngine;

namespace LuckyTowerCoop.Net
{
    /// <summary>Меню мода (F7): онлайн, настройки, игроки; оверлеи состояния связи.</summary>
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

        /// <summary>Отладка: открыть меню на вкладке (tab &lt; 0 — закрыть).</summary>
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
                    p1.isPlaying = false; // пока меню открыто, ввод не уходит в игру
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
                // размер шрифта зависит от высоты окна (окно могли сменить: полный экран → окно)
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
            string ping = (s.Transport != null && s.Transport.PingMs >= 0 ? $" · пинг {s.Transport.PingMs} мс" : "")
                + (NetSession.IsClient && s.Client != null && s.Client.InputLagMs > 0f ? $" · ввод→экран {s.Client.InputLagMs:F0} мс" : "")
                + (NetSession.PeerReady || NetSession.IsClient ? $" · ↑{s.KbOut:F1} ↓{s.KbIn:F1} кБ/с" : "")
                + (s.Transport is SteamSidecarTransport st && st.Stats.Valid ? " · " + st.Stats.Short() : "");
            Shadowed(new Rect(10, Screen.height - 34, Screen.width, 30), $"{(NetSession.IsHost ? "Хост" : "Клиент")}: {s.StatusText}{ping}   [{CoopConfig.NetMenuKey.Value} — меню]", m_small, new Color(0.9f, 0.9f, 0.9f));

            if (s.CurrentState == NetSession.State.PeerLost && NetSession.IsHost)
            {
                if (!s.AloneChosen)
                {
                    DrawPeerLostPanel(s);
                }
                else
                {
                    Shadowed(new Rect(0, Screen.height * 0.04f, Screen.width, 40), "Напарник отключён — может вернуться в любой момент", m_small, new Color(1f, 0.85f, 0.3f));
                }
            }
            else if (s.CurrentState == NetSession.State.Reconnecting)
            {
                Shadowed(new Rect(0, Screen.height * 0.35f, Screen.width, 200), "Связь с хостом потеряна.\nПереподключение…", m_big, new Color(1f, 0.85f, 0.3f));
            }

            if (NetSession.IsClient)
            {
                DrawHostUi();
                if (s.Client != null && Time.unscaledTime - s.Client.RestartRequestedAt < 8f)
                {
                    Shadowed(new Rect(0, Screen.height * 0.04f, Screen.width, 40), "Просьба начать заново отправлена хосту — решает он", m_small, new Color(1f, 0.85f, 0.3f));
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
            Shadowed(new Rect(r.x + 10, r.y + 10, r.width - 20, 60), $"{(string.IsNullOrEmpty(s.PeerName) ? "Напарник" : s.PeerName)} просит начать забег заново", BigSmall(), new Color(1f, 0.9f, 0.6f));
            float bw = (r.width - 30f) / 2f;
            if (GUI.Button(new Rect(r.x + 10, r.y + r.height - 54, bw, 44), "Начать заново"))
            {
                s.Host.RestartRun();
            }
            if (GUI.Button(new Rect(r.x + 20 + bw, r.y + r.height - 54, bw, 44), "Нет, играем дальше"))
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
                ? "Его персонаж погиб — вещи лежат на полу, их можно забрать.\nЕсли он вернётся, появится заново без вещей."
                : "Если он вернётся, продолжит с того же места.";
            float secs = Time.unscaledTime - s.LostAt;
            var label = new Rect(r.x + 10, r.y + 10, r.width - 20, 150);
            Shadowed(label, $"Напарник отключился ({Mathf.FloorToInt(secs / 60f)}:{Mathf.FloorToInt(secs % 60f):00} назад)\n{what}", m_small == null ? GUI.skin.label : BigSmall(), new Color(1f, 0.9f, 0.6f));
            float bw = (r.width - 40f) / 3f;
            float by = r.y + r.height - 60f;
            if (GUI.Button(new Rect(r.x + 10, by, bw, 44), s.PausedForPeer ? "Ждать (пауза)" : "Ждать на паузе"))
            {
                s.WaitForPeer();
            }
            if (GUI.Button(new Rect(r.x + 20 + bw, by, bw, 44), "Играть одному"))
            {
                s.ContinueAlone();
            }
            if (GUI.Button(new Rect(r.x + 30 + bw * 2, by, bw, 44), "Закрыть сессию"))
            {
                s.Stop("хост закрыл сессию");
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
                string text = $"Воскрешение: {NetClient.ReviveKills}/{NetClient.ReviveNeed} убийств" + (NetClient.ReviveOnFloor ? " или новый этаж" : "");
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

        private static readonly string[] s_tabs = { "Онлайн", "Настройки", "Игроки" };
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
                    GUILayout.Label($"Состояние: {(NetSession.Role == NetRole.None ? "не в сети" : s.StatusText)}");
                    if (s.Transport != null)
                    {
                        GUILayout.Label($"Транспорт: {s.Transport.Status}");
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
            if (GUILayout.Button("Закрыть"))
            {
                SetOpen(false);
            }
            GUI.DragWindow();
        }

        private void DrawStart(NetSession s)
        {
            GUILayout.BeginHorizontal();
            GUILayout.Label("Связь:", GUILayout.Width(60));
            bool steam = CoopConfig.NetTransport.Value == NetTransportKind.Steam;
            if (GUILayout.Toggle(steam, " Steam (Spacewar)", GUILayout.Width(170)) && !steam)
            {
                CoopConfig.NetTransport.Value = NetTransportKind.Steam;
            }
            if (GUILayout.Toggle(!steam, " Напрямую (IP)") && steam)
            {
                CoopConfig.NetTransport.Value = NetTransportKind.Direct;
            }
            GUILayout.EndHorizontal();
            GUILayout.Space(6);

            if (GUILayout.Button("Создать игру (я хост)", GUILayout.Height(32)))
            {
                s.StartHost(CoopConfig.NetTransport.Value);
            }
            GUILayout.Space(10);

            if (CoopConfig.NetTransport.Value == NetTransportKind.Steam)
            {
                GUILayout.Label("Подключиться к другу:");
                if (GUILayout.Button("Открыть список друзей (Steam)"))
                {
                    s.StartJoinSteam(0);
                }
                GUILayout.BeginHorizontal();
                GUILayout.Label("Код лобби:", GUILayout.Width(90));
                m_lobbyCode = GUILayout.TextField(m_lobbyCode, GUILayout.Width(220));
                if (GUILayout.Button("Войти") && ulong.TryParse(m_lobbyCode.Trim(), out var lobby))
                {
                    s.StartJoinSteam(lobby);
                }
                GUILayout.EndHorizontal();
            }
            else
            {
                GUILayout.Label($"Хост слушает UDP-порт {CoopConfig.NetPort.Value} (для интернета — Tailscale/проброс порта).");
                GUILayout.BeginHorizontal();
                GUILayout.Label("IP:", GUILayout.Width(30));
                m_ip = GUILayout.TextField(m_ip, GUILayout.Width(200));
                GUILayout.Label("порт:", GUILayout.Width(40));
                m_port = GUILayout.TextField(m_port, GUILayout.Width(70));
                if (GUILayout.Button("Подключиться") && int.TryParse(m_port, out var port))
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
                GUILayout.Label($"Код лобби: {steam.LobbyId}");
                if (GUILayout.Button("Копировать", GUILayout.Width(110)))
                {
                    GUIUtility.systemCopyBuffer = steam.LobbyId.ToString();
                }
                GUILayout.EndHorizontal();
                if (!NetSession.PeerReady)
                {
                    GUILayout.Label("Пригласить друга (придёт приглашение в Steam; другу проще открыть «список друзей» в этом меню):");
                    foreach (var f in steam.FriendsOnline)
                    {
                        GUILayout.BeginHorizontal();
                        GUILayout.Label(f.name);
                        if (GUILayout.Button("Пригласить", GUILayout.Width(110)))
                        {
                            steam.Invite(f.id);
                        }
                        GUILayout.EndHorizontal();
                    }
                }
            }

            if (NetSession.IsClient && steam != null && s.CurrentState == NetSession.State.Connecting)
            {
                GUILayout.Label("Друзья, у которых открыта игра:");
                if (steam.FriendLobbies.Count == 0)
                {
                    GUILayout.Label("  пока никого (список обновляется сам)");
                }
                foreach (var l in steam.FriendLobbies)
                {
                    GUILayout.BeginHorizontal();
                    GUILayout.Label(l.name);
                    if (GUILayout.Button("Войти", GUILayout.Width(90)))
                    {
                        steam.Command($"join {l.lobby}");
                    }
                    GUILayout.EndHorizontal();
                }
                GUILayout.BeginHorizontal();
                GUILayout.Label("Код лобби:", GUILayout.Width(90));
                m_lobbyCode = GUILayout.TextField(m_lobbyCode, GUILayout.Width(220));
                if (GUILayout.Button("Войти") && ulong.TryParse(m_lobbyCode.Trim(), out var lobby))
                {
                    steam.Command($"join {lobby}");
                }
                GUILayout.EndHorizontal();
            }

            if (NetSession.IsHost && s.CurrentState == NetSession.State.PeerLost)
            {
                GUILayout.Label("Напарник отключён — может вернуться, пока сессия открыта.");
                GUILayout.BeginHorizontal();
                if (GUILayout.Button("Ждать на паузе", GUILayout.Height(30)))
                {
                    s.WaitForPeer();
                }
                if (GUILayout.Button("Играть одному", GUILayout.Height(30)))
                {
                    s.ContinueAlone();
                }
                GUILayout.EndHorizontal();
            }

            if (s.CurrentState == NetSession.State.Connected)
            {
                GUILayout.Space(6);
                if (GUILayout.Button("Пересинхронизировать (если что-то разъехалось)", GUILayout.Height(28)))
                {
                    if (NetSession.IsHost)
                    {
                        s.Host?.ForceResync();
                    }
                    else
                    {
                        s.Client?.RequestResync("вручную");
                    }
                }
            }

            GUILayout.Space(10);
            if (GUILayout.Button(NetSession.IsHost ? "Закрыть сессию" : "Отключиться", GUILayout.Height(28)))
            {
                s.Stop("вышли из меню");
            }
        }
    }
}
