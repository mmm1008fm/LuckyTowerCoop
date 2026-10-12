using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using LiteNetLib;

namespace LuckyTowerCoop.Net
{
    /// <summary>Direct UDP connection over LiteNetLib: the host listens on a port, the client connects by IP.</summary>
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
                UpdateTime = 1, // send thread: 15 ms by default
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
                m_status = $"connected to {peer.Address}:{peer.Port}";
                Connected?.Invoke();
            };
            m_listener.PeerDisconnectedEvent += (peer, info) =>
            {
                if (peer != m_peer)
                {
                    return;
                }
                m_peer = null;
                m_status = $"disconnected: {info.Reason}";
                Disconnected?.Invoke(info.Reason.ToString());
            };
            m_listener.NetworkReceiveEvent += (peer, reader, channel, method) =>
            {
                if (m_sim.Intercept(reader.RawData, reader.Position, reader.AvailableBytes, method == DeliveryMethod.ReliableOrdered))
                {
                    return; // network lab: the packet will arrive later (or be lost)
                }
                Received?.Invoke(reader.RawData, reader.Position, reader.AvailableBytes);
            };
            m_listener.NetworkErrorEvent += (endPoint, error) => m_status = $"network error: {error}";
        }

        public static LnlTransport Host(int port)
        {
            var t = new LnlTransport(true);
            if (!t.m_manager.Start(port))
            {
                t.Dispose();
                throw new InvalidOperationException($"port {port} is in use");
            }
            t.m_status = $"waiting for a connection on port {port}";
            return t;
        }

        public static LnlTransport Join(string address, int port)
        {
            var t = new LnlTransport(false);
            t.m_manager.Start();
            t.m_status = $"connecting to {address}:{port}...";
            t.m_manager.Connect(address, port, NetConst.ConnectKey);
            return t;
        }

        public void Poll()
        {
            m_manager.PollEvents();
            m_sim.Pump(m_deliver ??= (d, o, l) => Received?.Invoke(d, o, l));
        }

        // --- network lab (NetLinkSim): latency/jitter/loss/bandwidth on receive; the profile is shared by the process
        private readonly NetLinkSim m_sim = new NetLinkSim();
        private Action<byte[], int, int> m_deliver;

        /// <summary>Old command "net sim N": latency only, N ms one way.</summary>
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

        /// <summary>Whether a UDP port is free (for choosing the host port).</summary>
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
