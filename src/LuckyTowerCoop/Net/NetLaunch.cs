using System;
using System.Runtime.InteropServices;
using Rewired;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace LuckyTowerCoop.Net
{
    /// <summary>
    /// Online on one PC: two game copies over localhost, launched with command-line parameters
    /// (tools\local-online.ps1):
    ///   --coop-host                      create a game right away (Direct)
    ///   --coop-join 127.0.0.1[:port]     connect right away (retries until the host is up)
    ///   --coop-window left|right         window in the left/right half of the primary monitor
    ///   --coop-device auto|keyboard|gamepad|all   which devices control this copy (auto: host - keyboard, client - gamepad if present)
    ///   --coop-devserver                 enable the debug command server without editing the config
    ///   --coop-steam-host                create a game via Steam right away (lobby for friends)
    ///   --coop-steam-join [lobby]        join via Steam right away: the given lobby or the first friend lobby that appears
    ///                                    (single-PC test: second copy with a second Steam account in a sandbox, tools\steam-two.ps1)
    /// In this mode the game doesn't freeze without focus, and sound plays only for the focused window.
    /// </summary>
    internal sealed class NetLaunch : MonoBehaviour
    {
        public enum DeviceMode
        {
            All,
            Auto,
            Keyboard,
            Gamepad,
        }

        public static bool Active { get; private set; }
        public static bool DevServer { get; private set; }
        public static DeviceMode Device { get; private set; } = DeviceMode.All;

        private static bool s_host;
        private static bool s_steamHost;
        private static bool s_steamJoin;
        private static ulong s_steamLobby;
        private float m_nextFriendPoll;
        private bool m_steamJoinSent;
        private static string s_joinIp;
        private static int s_joinPort;
        private static string s_window;

        private bool m_netStarted;
        private float m_nextCheck;
        private int m_moveInFrames = -1;
        private int m_targetW;
        private int m_targetH;

        public static void Parse()
        {
            var args = Environment.GetCommandLineArgs();
            for (int i = 0; i < args.Length; i++)
            {
                string a = args[i].ToLowerInvariant();
                string next = i + 1 < args.Length ? args[i + 1] : null;
                switch (a)
                {
                    case "--coop-host":
                        s_host = true;
                        Active = true;
                        break;
                    case "--coop-join":
                        Active = true;
                        s_joinIp = "127.0.0.1";
                        s_joinPort = CoopConfig.NetPort.Value;
                        if (next != null && !next.StartsWith("--"))
                        {
                            i++;
                            int colon = next.LastIndexOf(':');
                            if (colon > 0 && int.TryParse(next.Substring(colon + 1), out int port))
                            {
                                s_joinIp = next.Substring(0, colon);
                                s_joinPort = port;
                            }
                            else
                            {
                                s_joinIp = next;
                            }
                        }
                        break;
                    case "--coop-window":
                        Active = true;
                        s_window = next != null && !next.StartsWith("--") ? args[++i].ToLowerInvariant() : "left";
                        break;
                    case "--coop-device":
                        if (next != null && Enum.TryParse(next, true, out DeviceMode mode))
                        {
                            Device = mode;
                            i++;
                        }
                        break;
                    case "--coop-devserver":
                        DevServer = true;
                        break;
                    case "--coop-link":
                        if (next != null && !next.StartsWith("--"))
                        {
                            Plugin.Log.LogInfo("[Launch] link: " + NetLinkSim.Set(next));
                            i++;
                        }
                        break;
                    case "--coop-steam-host":
                        s_steamHost = true;
                        break;
                    case "--coop-steam-join":
                        s_steamJoin = true;
                        if (next != null && ulong.TryParse(next, out var lobby))
                        {
                            s_steamLobby = lobby;
                            i++;
                        }
                        break;
                }
            }
            if (s_steamHost || s_steamJoin)
            {
                Active = true;
            }
            if (Active)
            {
                string mode = s_steamHost ? "steam host" : s_steamJoin ? $"steam join {(s_steamLobby != 0 ? s_steamLobby.ToString() : "first friend lobby")}" : s_host ? "host" : s_joinIp != null ? $"join {s_joinIp}:{s_joinPort}" : "-";
                Plugin.Log.LogInfo($"[Launch] local online: {mode} window={s_window ?? "-"} device={Device}");
            }
        }

        /// <summary>Which devices to give your own player online (accounting for auto and gamepad availability).</summary>
        public static DeviceMode EffectiveDevice()
        {
            if (Device != DeviceMode.Auto)
            {
                return Device;
            }
            bool pads = ReInput.isReady && ReInput.controllers.joystickCount > 0;
            if (!pads)
            {
                return DeviceMode.All;
            }
            return NetSession.IsClient ? DeviceMode.Gamepad : DeviceMode.Keyboard;
        }

        private void Awake()
        {
            Application.runInBackground = true;
            SceneManager.activeSceneChanged += (_, __) => m_nextCheck = Time.realtimeSinceStartup + 1f;
            m_nextCheck = Time.realtimeSinceStartup + 1f;
        }

        private void OnApplicationFocus(bool focus)
        {
            // two copies on one PC: only the focused window is audible, the keyboard also goes to it
            AudioListener.volume = focus ? 1f : 0f;
            Coop.CoopInput.MarkDirty();
        }

        private void Update()
        {
            if (!m_netStarted && SceneManager.GetActiveScene().name == "MainMenu" && Time.realtimeSinceStartup > 3f && NetSession.Instance != null)
            {
                m_netStarted = true;
                if (s_steamHost)
                {
                    NetSession.Instance.StartHost(NetTransportKind.Steam);
                }
                else if (s_steamJoin)
                {
                    NetSession.Instance.StartJoinSteam(s_steamLobby);
                    m_steamJoinSent = s_steamLobby != 0;
                }
                else if (s_host)
                {
                    NetSession.Instance.StartHost(NetTransportKind.Direct);
                }
                else if (s_joinIp != null)
                {
                    NetSession.Instance.StartJoinDirect(s_joinIp, s_joinPort);
                }
            }

            if (s_steamJoin && !m_steamJoinSent && m_netStarted)
            {
                PollFriendLobby();
            }

            if (s_window != null)
            {
                UpdateWindow();
            }
        }

        /// <summary>Client without a lobby code: wait until a friend has a co-op lobby, then join the first one.</summary>
        private void PollFriendLobby()
        {
            var s = NetSession.Instance;
            if (s == null || !(s.Transport is SteamSidecarTransport steam) || Time.realtimeSinceStartup < m_nextFriendPoll)
            {
                return;
            }
            m_nextFriendPoll = Time.realtimeSinceStartup + 3f;
            if (steam.FriendLobbies.Count > 0)
            {
                var l = steam.FriendLobbies[0];
                m_steamJoinSent = true;
                Plugin.Log.LogInfo($"[Launch] steam: joining lobby {l.lobby} of {l.name}");
                steam.Command($"join {l.lobby}");
                return;
            }
            steam.RefreshFriends();
        }

        // ================= window =================

        private void UpdateWindow()
        {
            if (m_moveInFrames >= 0 && m_moveInFrames-- == 0)
            {
                MoveWindow();
            }
            if (Time.realtimeSinceStartup < m_nextCheck)
            {
                return;
            }
            m_nextCheck = Time.realtimeSinceStartup + 2f;
            if (m_title != null)
            {
                UpdateTitle(); // a gamepad may have been plugged in/out
            }
            int sw = Display.main.systemWidth;
            m_targetW = sw / 2 - 16; // window frame
            m_targetH = m_targetW * 9 / 16;
            if (Screen.fullScreenMode != FullScreenMode.Windowed || Screen.width != m_targetW || Screen.height != m_targetH)
            {
                Screen.SetResolution(m_targetW, m_targetH, FullScreenMode.Windowed);
                m_moveInFrames = 5; // the window doesn't resize immediately
            }
        }

        private void MoveWindow()
        {
            var hwnd = FindOwnWindow();
            if (hwnd == IntPtr.Zero)
            {
                return;
            }
            int sw = Display.main.systemWidth;
            int x = s_window == "right" ? sw / 2 : 0;
            SetWindowPos(hwnd, IntPtr.Zero, x, 0, 0, 0, SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE);
            m_title = null;
            UpdateTitle();
        }

        private string m_title;

        private void UpdateTitle()
        {
            string role = s_host ? "HOST" : "CLIENT";
            string dev = EffectiveDevice() switch
            {
                DeviceMode.Keyboard => "keyboard",
                DeviceMode.Gamepad => "gamepad",
                _ => "input in the focused window",
            };
            string title = $"Lucky Tower Ultimate - {role} - {dev}";
            if (title == m_title)
            {
                return;
            }
            var hwnd = FindOwnWindow();
            if (hwnd != IntPtr.Zero && SetWindowTextW(hwnd, title))
            {
                m_title = title;
            }
        }

        private static IntPtr FindOwnWindow()
        {
            uint pid = (uint)System.Diagnostics.Process.GetCurrentProcess().Id;
            var h = IntPtr.Zero;
            while ((h = FindWindowExW(IntPtr.Zero, h, "UnityWndClass", null)) != IntPtr.Zero)
            {
                GetWindowThreadProcessId(h, out uint owner);
                if (owner == pid)
                {
                    return h;
                }
            }
            return IntPtr.Zero;
        }

        private const uint SWP_NOSIZE = 0x0001;
        private const uint SWP_NOZORDER = 0x0004;
        private const uint SWP_NOACTIVATE = 0x0010;

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr FindWindowExW(IntPtr parent, IntPtr childAfter, string className, string windowName);

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);

        [DllImport("user32.dll")]
        private static extern bool SetWindowPos(IntPtr hwnd, IntPtr insertAfter, int x, int y, int cx, int cy, uint flags);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern bool SetWindowTextW(IntPtr hwnd, string text);
    }
}
