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
                if (m_simMs > 0)
                {
                    m_recvQ.Enqueue((UnityEngine.Time.realtimeSinceStartup + m_simMs / 1000f, Copy(reader.RawData, reader.Position, reader.AvailableBytes)));
                    return;
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
            if (m_simMs <= 0 && m_sendQ.Count == 0 && m_recvQ.Count == 0)
            {
                return;
            }
            float now = UnityEngine.Time.realtimeSinceStartup;
            while (m_sendQ.Count > 0 && m_sendQ.Peek().due <= now)
            {
                var p = m_sendQ.Dequeue();
                if (IsConnected)
                {
                    m_peer.Send(p.data, 0, p.data.Length, 0, p.delivery == Delivery.Reliable ? DeliveryMethod.ReliableOrdered : DeliveryMethod.Unreliable);
                }
            }
            while (m_recvQ.Count > 0 && m_recvQ.Peek().due <= now)
            {
                var p = m_recvQ.Dequeue();
                Received?.Invoke(p.data, 0, p.data.Length);
            }
        }

        // --- отладка: имитация задержки сети на приёме (одна сторона); «net sim N» на обеих копиях = пинг 2N.
        // Встроенная имитация LiteNetLib есть только в его DEBUG-сборке.
        private int m_simMs;
        private readonly Queue<(float due, byte[] data, Delivery delivery)> m_sendQ = new Queue<(float, byte[], Delivery)>();
        private readonly Queue<(float due, byte[] data)> m_recvQ = new Queue<(float, byte[])>();

        public void SimulateLatency(int oneWayMs)
        {
            m_simMs = oneWayMs;
            m_status = System.Text.RegularExpressions.Regex.Replace(m_status, @" \[имитация[^\]]*\]", "") + (oneWayMs > 0 ? $" [имитация +{oneWayMs} мс]" : "");
        }

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
