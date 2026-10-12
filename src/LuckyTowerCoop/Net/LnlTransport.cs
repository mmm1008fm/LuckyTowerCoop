using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using LiteNetLib;

namespace LuckyTowerCoop.Net
{
    /// <summary>Прямое UDP-соединение на LiteNetLib: хост слушает порт, клиент подключается по IP.</summary>
    public sealed class LnlTransport : INetTransport
    {
        private readonly EventBasedNetListener m_listener = new EventBasedNetListener();
        private readonly NetManager m_manager;
        private readonly bool m_isHost;
        private NetPeer m_peer;
        private string m_status;

        public event Action Connected;
        public event Action<string> Disconnected;
        public event Action<byte[], int, int> Received;

        public bool IsConnected => m_peer != null && m_peer.ConnectionState == ConnectionState.Connected;

        public int PingMs => m_peer != null ? m_peer.Ping : -1;

        public string Status => m_status;

        private LnlTransport(bool isHost)
        {
            m_isHost = isHost;
            m_manager = new NetManager(m_listener)
            {
                DisconnectTimeout = (int)(NetConst.TimeoutSeconds * 1000),
                UnsyncedEvents = false,
                AutoRecycle = true,
                IPv6Enabled = false,
                UpdateTime = 1, // поток отправки: по умолчанию 15 мс
            };

            m_listener.ConnectionRequestEvent += request =>
            {
                if (!m_isHost || IsConnected)
                {
                    request.Reject();
                    return;
                }
                request.AcceptIfKey(NetConst.ConnectKey);
            };
            m_listener.PeerConnectedEvent += peer =>
            {
                m_peer = peer;
                m_status = $"соединено с {peer.Address}:{peer.Port}";
                Connected?.Invoke();
            };
            m_listener.PeerDisconnectedEvent += (peer, info) =>
            {
                if (peer != m_peer)
                {
                    return;
                }
                m_peer = null;
                m_status = $"разрыв: {info.Reason}";
                Disconnected?.Invoke(info.Reason.ToString());
            };
            m_listener.NetworkReceiveEvent += (peer, reader, channel, method) =>
            {
                if (m_sim.Intercept(reader.RawData, reader.Position, reader.AvailableBytes, method == DeliveryMethod.ReliableOrdered))
                {
                    return; // сетевая лаборатория: пакет придёт позже (или потеряется)
                }
                Received?.Invoke(reader.RawData, reader.Position, reader.AvailableBytes);
            };
            m_listener.NetworkErrorEvent += (endPoint, error) => m_status = $"ошибка сети: {error}";
        }

        public static LnlTransport Host(int port)
        {
            var t = new LnlTransport(true);
            if (!t.m_manager.Start(port))
            {
                t.Dispose();
                throw new InvalidOperationException($"порт {port} занят");
            }
            t.m_status = $"ожидание подключения на порту {port}";
            return t;
        }

        public static LnlTransport Join(string address, int port)
        {
            var t = new LnlTransport(false);
            t.m_manager.Start();
            t.m_status = $"подключение к {address}:{port}…";
            t.m_manager.Connect(address, port, NetConst.ConnectKey);
            return t;
        }

        public void Poll()
        {
            m_manager.PollEvents();
            m_sim.Pump(m_deliver ??= (d, o, l) => Received?.Invoke(d, o, l));
        }

        // --- сетевая лаборатория (NetLinkSim): задержка/джиттер/потери/ширина на приёме; профиль общий для процесса
        private readonly NetLinkSim m_sim = new NetLinkSim();
        private Action<byte[], int, int> m_deliver;

        /// <summary>Старая команда «net sim N»: только задержка N мс в одну сторону.</summary>
        public void SimulateLatency(int oneWayMs) => NetLinkSim.Set(oneWayMs > 0 ? $"lat={oneWayMs}" : "off");

        private static byte[] Copy(byte[] data, int offset, int length)
        {
            var b = new byte[length];
            Buffer.BlockCopy(data, offset, b, 0, length);
            return b;
        }

        public void Send(byte[] data, int length, Delivery delivery)
        {
            if (!IsConnected)
            {
                return;
            }
            try
            {
                m_peer.Send(data, 0, length, 0, delivery == Delivery.Reliable ? DeliveryMethod.ReliableOrdered : DeliveryMethod.Unreliable);
            }
            catch (Exception e)
            {
                Plugin.Log.LogError($"[Net] send failed ({length} bytes, {delivery}): {e.Message}");
            }
        }

        public void Disconnect()
        {
            m_peer?.Disconnect();
            m_peer = null;
        }

        public void Dispose()
        {
            try
            {
                m_manager.Stop();
            }
            catch (Exception)
            {
            }
        }

        /// <summary>Свободен ли UDP-порт (для выбора порта хоста).</summary>
        public static bool IsPortFree(int port)
        {
            try
            {
                using (var u = new UdpClient(new IPEndPoint(IPAddress.Any, port)))
                {
                    return true;
                }
            }
            catch (SocketException)
            {
                return false;
            }
        }
    }
}
