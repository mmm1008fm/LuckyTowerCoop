using LiteNetLib.Utils;
using LuckyTowerCoop.Coop;
using UnityEngine;

namespace LuckyTowerCoop.Net
{
    /// <summary>Actions the client forwards to the host (gameplay; menus and UI stay local).</summary>
    internal static class NetInputMap
    {
        public static readonly int[] Actions = { 2, 23, 21, 22, 3, 6, 7, 8, 9, 10, 11, 13, 14, 15, 26 };

        private static readonly int[] s_bitOfAction = BuildIndex();

        private static int[] BuildIndex()
        {
            var idx = new int[32];
            for (int i = 0; i < idx.Length; i++)
            {
                idx[i] = -1;
            }
            for (int i = 0; i < Actions.Length; i++)
            {
                idx[Actions[i]] = i;
            }
            return idx;
        }

        public static int BitOf(int actionId) => actionId >= 0 && actionId < s_bitOfAction.Length ? s_bitOfAction[actionId] : -1;
    }

    /// <summary>
    /// Client: polls its own devices (Rewired player 0) and sends to the host 60 times/s:
    /// a mask of held buttons + 4-bit press counters (a lost packet doesn't lose a tap, a resend doesn't double it).
    /// </summary>
    internal sealed class NetInputSender
    {
        private readonly byte[] m_downCounters = new byte[NetInputMap.Actions.Length];
        private readonly float[] m_sentAt = new float[64];
        private readonly ushort[] m_sentSeq = new ushort[64];
        private ushort m_seq;
        private float m_next;

        /// <summary>Smoothed time "input sent -> host applied it and sent a snapshot", ms (phase 0 measurement).</summary>
        public float AppliedLagMs { get; private set; }

        /// <summary>Number of the last sent input packet (for checking the prediction).</summary>
        public ushort LastSentSeq => (ushort)(m_seq - 1);

        public void OnAck(ushort seq)
        {
            int i = seq & 63;
            if (m_sentSeq[i] != seq || m_sentAt[i] <= 0f)
            {
                return;
            }
            float ms = (Time.unscaledTime - m_sentAt[i]) * 1000f;
            m_sentAt[i] = 0f;
            NetQuality.OnInputLag(ms);
            AppliedLagMs = AppliedLagMs <= 0f ? ms : Mathf.Lerp(AppliedLagMs, ms, 0.2f);
        }

        public void Update(NetSession session)
        {
            var p = CoopInput.P1Input;
            if (p == null)
            {
                return;
            }
            uint held = 0;
            for (int i = 0; i < NetInputMap.Actions.Length; i++)
            {
                int a = NetInputMap.Actions[i];
                if (p.GetButton(a))
                {
                    held |= 1u << i;
                }
                if (p.GetButtonDown(a))
                {
                    m_downCounters[i] = (byte)((m_downCounters[i] + 1) & 15);
                }
            }
            if (Time.unscaledTime < m_next - 0.002f)
            {
                return;
            }
            m_next = NetConst.NextTick(m_next, Time.unscaledTime, NetConst.InputInterval);

            ulong counters = 0;
            for (int i = 0; i < m_downCounters.Length; i++)
            {
                counters |= (ulong)m_downCounters[i] << (i * 4);
            }
            var w = session.Begin(Msg.Input);
            m_sentAt[m_seq & 63] = Time.unscaledTime;
            m_sentSeq[m_seq & 63] = m_seq;
            w.Put(m_seq++);
            w.Put(held);
            w.Put(counters);
            session.Send(w, Delivery.Unreliable);
        }
    }

    /// <summary>Host: the client's input as the button source for the second character's Rewired player.</summary>
    internal sealed class NetInputSource : IButtonSource
    {
        private readonly byte[] m_lastCounters = new byte[NetInputMap.Actions.Length];
        private bool m_hasPacket;
        private ushort m_lastSeq;
        private float m_lastPacketTime;
        private uint m_held;
        private uint m_pulse;
        private uint m_cur;
        private uint m_prev;
        private int m_frame = -1;

        /// <summary>Number of the last applied input packet (the host returns it in the snapshot - latency measurement on the client).</summary>
        public ushort LastSeq => m_lastSeq;

        public void OnPacket(NetDataReader r)
        {
            ushort seq = r.GetUShort();
            uint held = r.GetUInt();
            ulong counters = r.GetULong();
            if (m_hasPacket && (short)(seq - m_lastSeq) <= 0)
            {
                return; // old packet
            }
            for (int i = 0; i < m_lastCounters.Length; i++)
            {
                byte c = (byte)((counters >> (i * 4)) & 15);
                if (m_hasPacket && c != m_lastCounters[i])
                {
                    m_pulse |= 1u << i;
                }
                m_lastCounters[i] = c;
            }
            m_hasPacket = true;
            m_lastSeq = seq;
            m_held = held;
            m_lastPacketTime = Time.unscaledTime;
        }

        public void Reset()
        {
            m_held = m_pulse = m_cur = m_prev = 0;
            m_hasPacket = false;
        }

        private void Tick()
        {
            if (Time.frameCount == m_frame)
            {
                return;
            }
            m_frame = Time.frameCount;
            m_prev = m_cur;
            if (Time.unscaledTime - m_lastPacketTime > 0.5f)
            {
                m_held = 0; // no packets coming - release everything
            }
            m_cur = m_held | m_pulse;
            m_pulse = 0;
        }

        private static uint Bit(int actionId)
        {
            int b = NetInputMap.BitOf(actionId);
            return b < 0 ? 0u : 1u << b;
        }

        public bool GetButton(int actionId)
        {
            Tick();
            return (m_cur & Bit(actionId)) != 0;
        }

        public bool GetButtonDown(int actionId)
        {
            Tick();
            uint b = Bit(actionId);
            return (m_cur & b) != 0 && (m_prev & b) == 0;
        }

        public bool GetButtonUp(int actionId)
        {
            Tick();
            uint b = Bit(actionId);
            return (m_cur & b) == 0 && (m_prev & b) != 0;
        }
    }
}
