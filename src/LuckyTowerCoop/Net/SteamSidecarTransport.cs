using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using LiteNetLib;

namespace LuckyTowerCoop.Net
{
    /// <summary>
    /// Steam (Spacewar, AppID 480) via a separate CoopNet.exe process: in the game process the Steam API is already taken
    /// by the game's AppID, while Spacewar needs its own. The plugin is a LiteNetLib server on 127.0.0.1 that CoopNet connects to.
    /// IPC: [1][delivery?][data] - packets to/from the remote side; [2][text] - commands/events.
    /// </summary>
    public sealed class SteamSidecarTransport : INetTransport
    {
        private const byte IpcData = 1;
        private const byte IpcText = 2;
        private const string IpcKey = "LTCOOP-IPC";

        private readonly EventBasedNetListener m_listener = new EventBasedNetListener();
        private readonly NetManager m_ipc;
        private readonly bool m_isHost;
        private readonly ulong m_joinLobby;
        private NetPeer m_sidecar;
        private Process m_process;
        private bool m_remoteConnected;
        private string m_status = "starting CoopNet...";
        private byte[] m_buffer = new byte[2048];
        private int m_pingMs = -1;

        public event Action Connected;
        public event Action<string> Disconnected;
        public event Action<byte[], int, int> Received;

        /// <summary>Lobby (host: the one created; client: the one joined).</summary>
        public ulong LobbyId { get; private set; }

        public string MyName { get; private set; } = "";

        /// <summary>Friends with an open co-op lobby: (lobby, name).</summary>
        public List<(ulong lobby, string name)> FriendLobbies { get; } = new List<(ulong, string)>();

        /// <summary>Friends online available to invite: (steamId, name).</summary>
        public List<(ulong id, string name)> FriendsOnline { get; } = new List<(ulong, string)>();

        public bool IsConnected => m_remoteConnected && m_sidecar != null;

        public int PingMs => m_pingMs;

        public string Status => m_status;

        public static string SidecarPath => Path.Combine(Path.GetDirectoryName(typeof(SteamSidecarTransport).Assembly.Location) ?? ".", "CoopNet", "CoopNet.exe");

        private SteamSidecarTransport(bool isHost, ulong joinLobby)
        {
            m_isHost = isHost;
            m_joinLobby = joinLobby;
            m_ipc = new NetManager(m_listener) { UnsyncedEvents = false, AutoRecycle = true, DisconnectTimeout = 15000, UpdateTime = 1 }; // default is 15 ms - extra delay on every round trip
            m_listener.ConnectionRequestEvent += r =>
            {
                if (m_sidecar == null)
                {
                    r.AcceptIfKey(IpcKey);
                }
                else
                {
                    r.Reject();
                }
            };
            m_listener.PeerConnectedEvent += p =>
            {
                m_sidecar = p;
                m_status = "Steam: initializing...";
            };
            m_listener.PeerDisconnectedEvent += (p, info) =>
            {
                if (p != m_sidecar)
                {
                    return;
                }
                m_sidecar = null;
                m_status = "CoopNet disconnected";
                if (m_remoteConnected)
                {
                    m_remoteConnected = false;
                    Disconnected?.Invoke("CoopNet closed");
                }
            };
            m_listener.NetworkReceiveEvent += (p, reader, ch, method) => OnIpc(reader.RawData, reader.Position, reader.AvailableBytes, method == DeliveryMethod.ReliableOrdered);
        }

        /// <summary>Quality of the Steam leg (once a second from CoopNet).</summary>
        public struct SteamStats
        {
            public bool Valid;
            public int Ping;
            public float QualityLocal;   // fraction of packets that reached us (1 = no loss)
            public float QualityRemote;  // fraction of our packets that reached the friend
            public float OutBps;
            public float InBps;
            public int SendRateBps;      // current Steam send rate cap
            public int PendingUnreliable;
            public int PendingReliable;
            public int UnackedReliable;
            public float QueueMs;        // how long a packet waits in the send queue
            public bool Relayed;

            public string Short() => !Valid ? "" :
                $"loss: you {(1f - QualityLocal) * 100f:F0}% friend {(1f - QualityRemote) * 100f:F0}% - queue {QueueMs:F0} ms - limit {SendRateBps / 1024} KB/s{(Relayed ? " - via Valve relay" : " - direct")}";
        }

        public SteamStats Stats { get; private set; }

        private float m_nextStatsLog;

        private void ParseStats(string arg)
        {
            var p = arg.Split(' ');
            if (p.Length < 11)
            {
                return;
            }
            var inv = System.Globalization.CultureInfo.InvariantCulture;
            float F(int i) => float.TryParse(p[i], System.Globalization.NumberStyles.Float, inv, out var v) ? v : 0f;
            int I(int i) => int.TryParse(p[i], out var v) ? v : 0;
            var s = new SteamStats
            {
                Valid = true,
                Ping = I(0),
                QualityLocal = F(1),
                QualityRemote = F(2),
                OutBps = F(3),
                InBps = F(4),
                SendRateBps = I(5),
                PendingUnreliable = I(6),
                PendingReliable = I(7),
                UnackedReliable = I(8),
                QueueMs = F(9) / 1000f,
                Relayed = p[10] == "1",
            };
            Stats = s;
            // log trail: after playing with a friend you can see where time was lost (and a warning on a bad connection)
            bool bad = s.QueueMs > 50f || s.QualityLocal < 0.98f || s.QualityRemote < 0.98f;
            if (bad || UnityEngine.Time.unscaledTime >= m_nextStatsLog)
            {
                m_nextStatsLog = UnityEngine.Time.unscaledTime + 10f;
                string line = $"[Steam] ping={s.Ping} lossIn={(1f - s.QualityLocal) * 100f:F1}% lossOut={(1f - s.QualityRemote) * 100f:F1}% out={s.OutBps / 1024f:F1}kB/s in={s.InBps / 1024f:F1}kB/s rate={s.SendRateBps / 1024}kB/s pendU={s.PendingUnreliable} pendR={s.PendingReliable} unacked={s.UnackedReliable} queue={s.QueueMs:F1}ms relayed={s.Relayed}";
                if (bad)
                {
                    Plugin.Log.LogWarning(line);
                }
                else
                {
                    Plugin.Log.LogInfo(line);
                }
            }
        }

        public static SteamSidecarTransport Host() => Create(true, 0);

        public static SteamSidecarTransport Join(ulong lobby) => Create(false, lobby);

        private static SteamSidecarTransport Create(bool host, ulong lobby)
        {
            if (!File.Exists(SidecarPath))
            {
                throw new FileNotFoundException("CoopNet.exe not found (Steam transport)", SidecarPath);
            }
            var t = new SteamSidecarTransport(host, lobby);
            if (!t.m_ipc.Start(System.Net.IPAddress.Loopback, System.Net.IPAddress.IPv6Loopback, 0))
            {
                throw new InvalidOperationException("failed to open the IPC port");
            }
            var psi = new ProcessStartInfo(SidecarPath, $"--ipc {t.m_ipc.LocalPort} --parent {Process.GetCurrentProcess().Id}")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = Path.GetDirectoryName(SidecarPath),
            };
            t.m_process = Process.Start(psi);
            Plugin.Log.LogInfo($"[Net] CoopNet started pid={t.m_process?.Id} ipc={t.m_ipc.LocalPort}");
            return t;
        }

        // ---------- IPC ----------

        private readonly NetLinkSim m_sim = new NetLinkSim();
        private Action<byte[], int, int> m_deliver;

        private void OnIpc(byte[] data, int offset, int length, bool reliable)
        {
            if (length < 1)
            {
                return;
            }
            if (data[offset] == IpcData)
            {
                if (!m_sim.Intercept(data, offset + 1, length - 1, reliable)) // network lab on top of Steam
                {
                    Received?.Invoke(data, offset + 1, length - 1);
                }
                return;
            }
            if (data[offset] == IpcText)
            {
                OnEvent(Encoding.UTF8.GetString(data, offset + 1, length - 1));
            }
        }

        private void OnEvent(string text)
        {
            int sp = text.IndexOf(' ');
            string kind = sp < 0 ? text : text.Substring(0, sp);
            string arg = sp < 0 ? "" : text.Substring(sp + 1);
            switch (kind)
            {
                case "ready":
                    MyName = arg;
                    m_status = m_isHost ? "Steam: creating lobby..." : m_joinLobby != 0 ? $"Steam: joining lobby {m_joinLobby}..." : "Steam: pick a friend";
                    Command(m_isHost ? "host" : m_joinLobby != 0 ? $"join {m_joinLobby}" : "friends");
                    break;
                case "lobby":
                    LobbyId = ulong.TryParse(arg, out var id) ? id : 0;
                    m_status = m_isHost ? $"lobby {LobbyId} - waiting for a friend" : $"in lobby {LobbyId}, connecting to host...";
                    break;
                case "peer-connected":
                    m_remoteConnected = true;
                    m_status = $"connected via Steam: {arg}";
                    Connected?.Invoke();
                    break;
                case "peer-disconnected":
                    if (m_remoteConnected)
                    {
                        m_remoteConnected = false;
                        m_status = $"disconnected: {arg}";
                        Disconnected?.Invoke(arg);
                    }
                    break;
                case "ping":
                    int.TryParse(arg, out m_pingMs);
                    break;
                case "stats":
                    ParseStats(arg);
                    break;
                case "friends":
                    FriendLobbies.Clear();
                    foreach (var line in arg.Split('\n'))
                    {
                        var parts = line.Split('\t');
                        if (parts.Length == 2 && ulong.TryParse(parts[0], out var l))
                        {
                            FriendLobbies.Add((l, parts[1]));
                        }
                    }
                    break;
                case "online":
                    FriendsOnline.Clear();
                    foreach (var line in arg.Split('\n'))
                    {
                        var parts = line.Split('\t');
                        if (parts.Length == 2 && ulong.TryParse(parts[0], out var f))
                        {
                            FriendsOnline.Add((f, parts[1]));
                        }
                    }
                    break;
                case "error":
                    m_status = "Steam: " + arg;
                    Plugin.Log.LogWarning($"[Net] CoopNet error: {arg}");
                    break;
                case "log":
                    Plugin.Log.LogInfo($"[CoopNet] {arg}");
                    break;
            }
        }

        public void Command(string text)
        {
            if (m_sidecar == null)
            {
                return;
            }
            var bytes = Encoding.UTF8.GetBytes(text);
            var buf = new byte[bytes.Length + 1];
            buf[0] = IpcText;
            Buffer.BlockCopy(bytes, 0, buf, 1, bytes.Length);
            m_sidecar.Send(buf, 0, buf.Length, 0, DeliveryMethod.ReliableOrdered);
        }

        public void RefreshFriends() => Command("friends");

        public void Invite(ulong friend) => Command($"invite {friend}");

        /// <summary>Client: re-join the same lobby (reconnect).</summary>
        public void Rejoin()
        {
            if (!m_isHost && LobbyId != 0)
            {
                Command($"join {LobbyId}");
            }
            else if (!m_isHost && m_joinLobby != 0)
            {
                Command($"join {m_joinLobby}");
            }
        }

        // ---------- INetTransport ----------

        public void Poll()
        {
            m_ipc.PollEvents();
            m_sim.Pump(m_deliver ??= (d, o, l) => Received?.Invoke(d, o, l));
            if (m_process != null && m_process.HasExited && m_sidecar == null)
            {
                m_status = $"CoopNet exited (code {m_process.ExitCode}) - see the log";
            }
        }

        public void Send(byte[] data, int length, Delivery delivery)
        {
            if (m_sidecar == null || !m_remoteConnected)
            {
                return;
            }
            if (m_buffer.Length < length + 2)
            {
                m_buffer = new byte[length + 64];
            }
            m_buffer[0] = IpcData;
            m_buffer[1] = (byte)(delivery == Delivery.Reliable ? 1 : 0);
            Buffer.BlockCopy(data, 0, m_buffer, 2, length);
            // unreliable on the game-CoopNet leg too: a stale snapshot is better lost than delivered late
            var method = delivery == Delivery.Reliable || length + 2 > m_sidecar.GetMaxSinglePacketSize(DeliveryMethod.Unreliable)
                ? DeliveryMethod.ReliableOrdered // too large for a single unreliable packet - better reliable than an exception
                : DeliveryMethod.Unreliable;
            m_sidecar.Send(m_buffer, 0, length + 2, 0, method);
            m_ipc.TriggerUpdate();
        }

        public void Disconnect()
        {
            Command("leave");
            m_remoteConnected = false;
        }

        public void Dispose()
        {
            try
            {
                Command("quit");
                m_ipc.PollEvents();
                m_ipc.Stop();
            }
            catch (Exception)
            {
            }
            try
            {
                if (m_process != null && !m_process.HasExited && !m_process.WaitForExit(1500))
                {
                    m_process.Kill();
                }
            }
            catch (Exception)
            {
            }
        }
    }
}
