using System;
using System.Runtime.InteropServices;
using Rewired;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace LuckyTowerCoop.Net
{
    /// <summary>
    /// Онлайн на одном ПК: две копии игры через localhost, запуск с параметрами командной строки
    /// (tools\local-online.ps1):
    ///   --coop-host                      сразу создать игру (Direct)
    ///   --coop-join 127.0.0.1[:порт]     сразу подключиться (повторяет попытки, пока хост не поднимется)
    ///   --coop-window left|right         окно в левой/правой половине основного монитора
    ///   --coop-device auto|keyboard|gamepad|all   чем управляется эта копия (auto: хост — клавиатура, клиент — геймпад, если он есть)
    ///   --coop-devserver                 включить отладочный сервер команд без правки конфига
    ///   --coop-steam-host                сразу создать игру через Steam (лобби для друзей)
    ///   --coop-steam-join [лобби]        сразу войти через Steam: в указанное лобби или в первое лобби друга, которое появится
    ///                                    (тест на одном ПК: вторая копия со вторым аккаунтом Steam в песочнице, tools\steam-two.ps1)
    /// В этом режиме игра не замирает без фокуса, а звук играет только у окна в фокусе.
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

        /// <summary>Какие устройства отдать своему игроку в онлайне (с учётом auto и наличия геймпадов).</summary>
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
            // две копии на одном ПК: слышно только окно в фокусе, клавиатура тоже у него
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

        /// <summary>Клиент без кода лобби: ждём, пока у друга появится лобби коопа, и входим в первое.</summary>
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

        // ================= окно =================

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
                UpdateTitle(); // геймпад могли подключить/отключить
            }
            int sw = Display.main.systemWidth;
            m_targetW = sw / 2 - 16; // рамка окна
            m_targetH = m_targetW * 9 / 16;
            if (Screen.fullScreenMode != FullScreenMode.Windowed || Screen.width != m_targetW || Screen.height != m_targetH)
            {
                Screen.SetResolution(m_targetW, m_targetH, FullScreenMode.Windowed);
                m_moveInFrames = 5; // окно меняет размер не сразу
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
            string role = s_host ? "ХОСТ" : "КЛИЕНТ";
            string dev = EffectiveDevice() switch
            {
                DeviceMode.Keyboard => "клавиатура",
                DeviceMode.Gamepad => "геймпад",
                _ => "ввод в окне с фокусом",
            };
            string title = $"Lucky Tower Ultimate — {role} · {dev}";
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
