using System;
using LiteNetLib.Utils;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace LuckyTowerCoop.Net
{
    /// <summary>
    /// Онлайн-сессия: транспорт, рукопожатие, heartbeat, обрыв и переподключение.
    /// Хост-авторитет: мир считает хост (NetHost), клиент — зеркало (NetClient).
    /// </summary>
    public class NetSession : MonoBehaviour
    {
        public enum State
        {
            Off,
            /// <summary>Хост ждёт первого подключения.</summary>
            Listening,
            /// <summary>Клиент устанавливает соединение.</summary>
            Connecting,
            /// <summary>Транспорт соединён, идёт Hello/Welcome.</summary>
            Handshaking,
            Connected,
            /// <summary>Хост: напарник пропал, место держится.</summary>
            PeerLost,
            /// <summary>Клиент: связь потеряна, переподключаемся.</summary>
            Reconnecting,
        }

        public static NetSession Instance { get; private set; }

        public static NetRole Role => Instance != null ? Instance.m_role : NetRole.None;

        public static bool IsHost => Role == NetRole.Host;

        /// <summary>Клиент-зеркало: пока сессия клиента активна (в том числе во время переподключения), мир у себя не симулируем.</summary>
        public static bool IsClient => Role == NetRole.Client;

        /// <summary>Хост: клиент подключён и прошёл рукопожатие.</summary>
        public static bool PeerReady => Instance != null && Instance.m_state == State.Connected;

        public State CurrentState => m_state;

        public INetTransport Transport => m_transport;

        public string StatusText { get; private set; } = "";

        public string PeerName { get; private set; } = "";

        public float LostAt { get; private set; }

        public NetHost Host { get; private set; }

        public NetClient Client { get; private set; }

        private INetTransport m_transport;
        private NetRole m_role;
        private State m_state;
        private string m_token = "";
        private float m_lastRecv;
        private float m_nextHeartbeat;
        private float m_nextReconnect;
        private bool m_pausedByUs;
        private float m_timeScaleBeforePause = 1f;
        private NetTransportKind m_kind;
        private string m_joinAddress;
        private int m_joinPort;
        private ulong m_joinLobby;

        private readonly NetDataWriter m_writer = new NetDataWriter(true, 1024);
        private readonly NetDataReader m_reader = new NetDataReader();

        private void Awake()
        {
            Instance = this;
        }

        // ================= запуск / остановка =================

        public void StartHost(NetTransportKind kind)
        {
            Stop("restart");
            try
            {
                m_kind = kind;
                m_transport = kind == NetTransportKind.Steam ? (INetTransport)SteamSidecarTransport.Host() : LnlTransport.Host(CoopConfig.NetPort.Value);
            }
            catch (Exception e)
            {
                StatusText = "не удалось запустить хост: " + e.Message;
                Plugin.Log.LogError($"[Net] host start failed: {e}");
                return;
            }
            m_role = NetRole.Host;
            m_token = Guid.NewGuid().ToString("N");
            Host = new NetHost(this);
            Hook();
            SetState(State.Listening);
            Plugin.Log.LogInfo($"[Net] hosting via {kind}");
        }

        public void StartJoinDirect(string address, int port)
        {
            Stop("restart");
            m_kind = NetTransportKind.Direct;
            m_joinAddress = address;
            m_joinPort = port;
            CoopConfig.NetJoinAddress.Value = address;
            BeginClient();
        }

        public void StartJoinSteam(ulong lobby)
        {
            Stop("restart");
            m_kind = NetTransportKind.Steam;
            m_joinLobby = lobby;
            BeginClient();
        }

        private void BeginClient()
        {
            try
            {
                m_transport = m_kind == NetTransportKind.Steam ? (INetTransport)SteamSidecarTransport.Join(m_joinLobby) : LnlTransport.Join(m_joinAddress, m_joinPort);
            }
            catch (Exception e)
            {
                StatusText = "не удалось подключиться: " + e.Message;
                Plugin.Log.LogError($"[Net] join failed: {e}");
                return;
            }
            m_role = NetRole.Client;
            Client ??= new NetClient(this);
            Hook();
            SetState(State.Connecting);
            Plugin.Log.LogInfo($"[Net] joining via {m_kind}");
        }

        public void Stop(string reason)
        {
            if (m_transport != null)
            {
                try
                {
                    if (m_transport.IsConnected)
                    {
                        var w = Begin(Msg.Bye);
                        w.Put(reason ?? "");
                        Send(w, Delivery.Reliable);
                        m_transport.Poll();
                    }
                }
                catch (Exception)
                {
                }
                Unhook();
                m_transport.Dispose();
                m_transport = null;
            }
            if (m_role == NetRole.Host)
            {
                Host?.OnSessionEnded();
            }
            else if (m_role == NetRole.Client)
            {
                Client?.OnSessionEnded();
            }
            Host = null;
            Client = null;
            m_role = NetRole.None;
            m_token = "";
            Unpause();
            SetState(State.Off);
            if (reason != "restart")
            {
                Plugin.Log.LogInfo($"[Net] session stopped: {reason}");
            }
        }

        /// <summary>Хост выбрал «играть одному»: пауза снята, но напарник по-прежнему может вернуться.</summary>
        public bool AloneChosen { get; private set; }

        /// <summary>Хост: продолжить без отключившегося напарника (он может вернуться, пока сессия открыта).</summary>
        public void ContinueAlone()
        {
            AloneChosen = true;
            Unpause();
        }

        /// <summary>Хост: снова ждать напарника на паузе.</summary>
        public void WaitForPeer()
        {
            AloneChosen = false;
            if (m_state == State.PeerLost)
            {
                Pause();
            }
        }

        private bool m_bgSaved;
        private bool m_bgPrev;

        private void Hook()
        {
            m_transport.Connected += OnTransportConnected;
            m_transport.Disconnected += OnTransportDisconnected;
            m_transport.Received += OnReceived;
            // свернул окно — игра не должна вставать: иначе через 6 с связь рвётся, а персонаж напарника у хоста погибает
            if (!m_bgSaved)
            {
                m_bgPrev = Application.runInBackground;
                m_bgSaved = true;
                // точный таймер на время сессии: поток LiteNetLib (UpdateTime=1) иначе может тикать раз в 15,6 мс
                try { timeBeginPeriod(1); } catch { }
            }
            Application.runInBackground = true;
        }

        [System.Runtime.InteropServices.DllImport("winmm.dll")]
        private static extern uint timeBeginPeriod(uint ms);

        [System.Runtime.InteropServices.DllImport("winmm.dll")]
        private static extern uint timeEndPeriod(uint ms);

        private void Unhook()
        {
            m_transport.Connected -= OnTransportConnected;
            m_transport.Disconnected -= OnTransportDisconnected;
            m_transport.Received -= OnReceived;
            if (m_bgSaved)
            {
                m_bgSaved = false;
                Application.runInBackground = m_bgPrev || NetLaunch.Active || CoopConfig.DevRunInBackground.Value;
                try { timeEndPeriod(1); } catch { }
            }
        }

        private void OnDestroy()
        {
            Stop("shutdown");
        }

        private void OnApplicationQuit()
        {
            Stop("quit");
        }

        // ================= отправка =================

        public NetDataWriter Begin(Msg type)
        {
            m_writer.Reset();
            m_writer.Put((byte)type);
            return m_writer;
        }

        public void Send(NetDataWriter w, Delivery delivery)
        {
            if (m_transport == null || !m_transport.IsConnected)
            {
                return;
            }
            m_transport.Send(w.Data, w.Length, delivery);
            m_bytesOut += w.Length;
        }

        private long m_bytesOut;
        private long m_bytesIn;
        private float m_rateTime;

        /// <summary>Трафик за последнюю секунду, кБ/с.</summary>
        public float KbOut { get; private set; }

        public float KbIn { get; private set; }

        // ================= события транспорта =================

        private void OnTransportConnected()
        {
            m_lastRecv = Time.unscaledTime;
            SetState(State.Handshaking);
            if (m_role == NetRole.Client)
            {
                var w = Begin(Msg.Hello);
                w.Put(NetConst.ProtocolVersion);
                w.Put(Plugin.Version);
                w.Put(m_token);
                w.Put(LocalName());
                Client.WriteHelloState(w);
                Send(w, Delivery.Reliable);
            }
        }

        private void OnTransportDisconnected(string reason)
        {
            OnPeerLost("транспорт: " + reason);
        }

        private void OnReceived(byte[] data, int offset, int length)
        {
            if (length < 1)
            {
                return;
            }
            m_lastRecv = Time.unscaledTime;
            m_bytesIn += length;
            m_reader.SetSource(data, offset, offset + length);
            var type = (Msg)m_reader.GetByte();
            try
            {
                switch (type)
                {
                    case Msg.Hello:
                        OnHello(m_reader);
                        return;
                    case Msg.Welcome:
                        OnWelcome(m_reader);
                        return;
                    case Msg.Reject:
                        StatusText = "хост отклонил подключение: " + m_reader.GetString();
                        Plugin.Log.LogWarning($"[Net] {StatusText}");
                        Stop("rejected");
                        return;
                    case Msg.Heartbeat:
                        return;
                    case Msg.Bye:
                        OnPeerLost("напарник вышел: " + m_reader.GetString());
                        return;
                }
                if (m_state != State.Connected)
                {
                    return;
                }
                if (m_role == NetRole.Host)
                {
                    Host?.OnMessage(type, m_reader);
                }
                else
                {
                    Client?.OnMessage(type, m_reader);
                }
            }
            catch (Exception e)
            {
                Plugin.Log.LogError($"[Net] error handling {type}: {e}");
            }
        }

        private void OnHello(NetDataReader r)
        {
            if (m_role != NetRole.Host)
            {
                return;
            }
            int proto = r.GetInt();
            string modVersion = r.GetString();
            string token = r.GetString();
            string name = r.GetString();
            if (proto != NetConst.ProtocolVersion)
            {
                var w = Begin(Msg.Reject);
                w.Put($"разные версии мода (хост {Plugin.Version}, у вас {modVersion})");
                Send(w, Delivery.Reliable);
                return;
            }
            bool rejoin = token == m_token;
            PeerName = name;
            var welcome = Begin(Msg.Welcome);
            welcome.Put(m_token);
            welcome.Put(LocalName());
            Send(welcome, Delivery.Reliable);
            SetState(State.Connected);
            Unpause();
            LostAt = -1f;
            AloneChosen = false;
            Plugin.Log.LogInfo($"[Net] client '{name}' {(rejoin ? "reconnected" : "joined")} (mod {modVersion})");
            Host.OnClientReady(r, rejoin);
        }

        private void OnWelcome(NetDataReader r)
        {
            if (m_role != NetRole.Client)
            {
                return;
            }
            m_token = r.GetString();
            PeerName = r.GetString();
            SetState(State.Connected);
            Unpause();
            Plugin.Log.LogInfo($"[Net] joined host '{PeerName}'");
            Client.OnWelcome();
        }

        private void OnPeerLost(string reason)
        {
            if (m_role == NetRole.None || m_state == State.PeerLost || m_state == State.Reconnecting)
            {
                return;
            }
            bool wasConnected = m_state == State.Connected || m_state == State.Handshaking;
            Plugin.Log.LogWarning($"[Net] peer lost: {reason}");
            StatusText = reason;
            if (m_role == NetRole.Host)
            {
                if (!wasConnected)
                {
                    return;
                }
                SetState(State.PeerLost);
                LostAt = Time.unscaledTime;
                AloneChosen = false;
                Host.OnPeerLost();
                if (CoopConfig.NetPauseOnDisconnect.Value)
                {
                    Pause();
                }
            }
            else
            {
                SetState(State.Reconnecting);
                LostAt = Time.unscaledTime;
                m_nextReconnect = Time.unscaledTime + 2f;
                Client.OnPeerLost();
            }
        }

        // ================= кадр =================

        private void Update()
        {
            if (m_transport == null)
            {
                return;
            }
            try
            {
                m_transport.Poll();
            }
            catch (Exception e)
            {
                Plugin.Log.LogError($"[Net] transport poll: {e.Message}");
            }

            float now = Time.unscaledTime;
            if (now - m_rateTime >= 1f)
            {
                KbOut = m_bytesOut / 1024f / (now - m_rateTime);
                KbIn = m_bytesIn / 1024f / (now - m_rateTime);
                m_bytesOut = m_bytesIn = 0;
                m_rateTime = now;
            }
            if (m_transport.IsConnected && now >= m_nextHeartbeat)
            {
                m_nextHeartbeat = now + NetConst.HeartbeatInterval;
                Send(Begin(Msg.Heartbeat), Delivery.Unreliable);
            }
            if ((m_state == State.Connected || m_state == State.Handshaking) && now - m_lastRecv > NetConst.TimeoutSeconds)
            {
                m_transport.Disconnect();
                OnPeerLost("нет ответа");
            }

            switch (m_state)
            {
                case State.PeerLost:
                    // ждём без срока; если задан ReconnectSeconds — по истечении просто снимаем паузу
                    float limit = CoopConfig.NetReconnectSeconds.Value;
                    if (limit > 0f && !AloneChosen && now - LostAt > limit)
                    {
                        Plugin.Log.LogInfo("[Net] wait limit reached — continuing alone (partner can still return)");
                        ContinueAlone();
                    }
                    break;
                case State.Reconnecting:
                    if (now >= m_nextReconnect)
                    {
                        m_nextReconnect = now + 3f;
                        TryReconnect();
                    }
                    break;
            }

            if (m_role == NetRole.Host && m_state == State.Connected)
            {
                Host?.Tick();
            }
            else if (m_role == NetRole.Client)
            {
                Client?.Tick(m_state == State.Connected);
            }
        }

        private void TryReconnect()
        {
            if (m_kind == NetTransportKind.Steam && m_transport is SteamSidecarTransport steam)
            {
                steam.Rejoin();
                return;
            }
            // Direct: новый транспорт с тем же адресом, токен сохраняется
            Unhook();
            m_transport.Dispose();
            try
            {
                m_transport = LnlTransport.Join(m_joinAddress, m_joinPort);
                Hook();
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"[Net] reconnect attempt failed: {e.Message}");
            }
        }

        private void SetState(State s)
        {
            m_state = s;
            StatusText = s switch
            {
                State.Off => "не в сети",
                State.Listening => "ожидание напарника",
                State.Connecting => "подключение…",
                State.Handshaking => "рукопожатие…",
                State.Connected => $"в игре с {PeerName}",
                State.PeerLost => "напарник отключился — ждём переподключения",
                State.Reconnecting => "связь потеряна — переподключение…",
                _ => StatusText,
            };
        }

        private void Pause()
        {
            if (m_pausedByUs)
            {
                return;
            }
            m_pausedByUs = true;
            m_timeScaleBeforePause = Time.timeScale > 0f ? Time.timeScale : 1f;
            Time.timeScale = 0f;
        }

        private void Unpause()
        {
            if (!m_pausedByUs)
            {
                return;
            }
            m_pausedByUs = false;
            Time.timeScale = m_timeScaleBeforePause;
        }

        public bool PausedForPeer => m_pausedByUs;

        public static string LocalName()
        {
            if (Instance?.m_transport is SteamSidecarTransport s && !string.IsNullOrEmpty(s.MyName))
            {
                return s.MyName;
            }
            return Environment.UserName;
        }

        public static string SceneName => SceneManager.GetActiveScene().name;
    }
}
