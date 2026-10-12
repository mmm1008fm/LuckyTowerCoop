using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;

namespace LuckyTowerCoop.Net
{
    /// <summary>
    /// Network lab: simulates a bad link on receive - latency, latency spread (jitter), loss, narrow bandwidth.
    /// The profile is set on both copies (dev "net link wifi" or --coop-link wifi), giving a symmetric link.
    /// Works for any transport (Direct and Steam), because it sits at the point where data enters the game.
    /// Unreliable packets are lost and overtake each other; reliable ones, when "lost", wait for a resend and hold the queue behind them -
    /// like real LiteNetLib/Steam.
    /// </summary>
    internal sealed class NetLinkSim
    {
        public struct Profile
        {
            public string Name;
            public int LatencyMs;   // one way
            public int JitterMs;    // upper bound of the random addition (usually small, occasionally a spike)
            public float LossPct;
            public int KBps;        // receive bandwidth, 0 = unlimited

            public bool Active => LatencyMs > 0 || JitterMs > 0 || LossPct > 0f || KBps > 0;

            public override string ToString() => !Active ? "off" :
                string.Format(CultureInfo.InvariantCulture, "{0} ({1}+/-{2} ms, loss {3}%{4})", Name, LatencyMs, JitterMs, LossPct, KBps > 0 ? $", {KBps} KB/s" : "");
        }

        public static readonly Profile[] Presets =
        {
            new Profile { Name = "off" },
            new Profile { Name = "lan", LatencyMs = 1, JitterMs = 1 },
            new Profile { Name = "good", LatencyMs = 25, JitterMs = 6, LossPct = 0.5f },
            new Profile { Name = "wifi", LatencyMs = 40, JitterMs = 30, LossPct = 2f },
            new Profile { Name = "bad", LatencyMs = 90, JitterMs = 60, LossPct = 5f, KBps = 96 },
            new Profile { Name = "awful", LatencyMs = 160, JitterMs = 100, LossPct = 10f, KBps = 48 },
        };

        /// <summary>Current profile (one per process: one session per game).</summary>
        public static Profile Current = Presets[0];

        public static int Dropped;
        public static int Retransmitted;

        private readonly List<Item> m_q = new List<Item>();
        private readonly Random m_rng = new Random();
        private double m_lastReliableDue;
        private double m_wireFree;
        private long m_seq;

        private struct Item
        {
            public double Due;
            public long Seq;
            public byte[] Data;
        }

        private static double Now => Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;

        /// <summary>Accept an incoming packet into the simulation. false - simulation is off, deliver immediately.</summary>
        public bool Intercept(byte[] data, int offset, int length, bool reliable)
        {
            var p = Current;
            if (!p.Active && m_q.Count == 0)
            {
                return false;
            }
            double now = Now;
            bool lost = p.LossPct > 0f && m_rng.NextDouble() * 100.0 < p.LossPct;
            if (lost && !reliable)
            {
                Dropped++;
                return true;
            }
            double delayMs = p.LatencyMs + JitterMs(p.JitterMs);
            if (lost)
            {
                // a reliable packet was lost: the sender will notice and resend after about one round trip
                Retransmitted++;
                delayMs += 2 * p.LatencyMs + p.JitterMs + 20;
            }
            double due = now + delayMs / 1000.0;
            if (p.KBps > 0)
            {
                // narrow link: packets queue on the "wire", delay grows during bursts
                m_wireFree = Math.Max(m_wireFree, now) + length / (p.KBps * 1024.0);
                due = Math.Max(due, m_wireFree + p.LatencyMs / 1000.0);
            }
            if (reliable)
            {
                // reliable ones strictly in order: a lost one holds all following ones (queue blocking)
                due = Math.Max(due, m_lastReliableDue);
                m_lastReliableDue = due;
            }
            var copy = new byte[length];
            Buffer.BlockCopy(data, offset, copy, 0, length);
            m_q.Add(new Item { Due = due, Seq = m_seq++, Data = copy });
            return true;
        }

        /// <summary>Release matured packets (call every frame from the transport's Poll).</summary>
        public void Pump(Action<byte[], int, int> deliver)
        {
            if (m_q.Count == 0)
            {
                return;
            }
            double now = Now;
            while (true)
            {
                int best = -1;
                for (int i = 0; i < m_q.Count; i++)
                {
                    var it = m_q[i];
                    if (it.Due <= now && (best < 0 || it.Due < m_q[best].Due || (it.Due == m_q[best].Due && it.Seq < m_q[best].Seq)))
                    {
                        best = i;
                    }
                }
                if (best < 0)
                {
                    return;
                }
                var item = m_q[best];
                m_q.RemoveAt(best);
                deliver(item.Data, 0, item.Data.Length);
            }
        }

        public void Clear()
        {
            m_q.Clear();
            m_lastReliableDue = 0;
            m_wireFree = 0;
        }

        /// <summary>Jitter as in real life: usually small, occasionally a spike up to the upper bound.</summary>
        private double JitterMs(int max)
        {
            if (max <= 0)
            {
                return 0;
            }
            double u = 1.0 - m_rng.NextDouble();
            return Math.Min(1.0, -Math.Log(u) * 0.3) * max;
        }

        /// <summary>"wifi" | "off" | "lat=80 jit=40 loss=3 bw=64". Returns a description.</summary>
        public static string Set(string spec)
        {
            spec = (spec ?? "").Trim().ToLowerInvariant();
            foreach (var pr in Presets)
            {
                if (pr.Name == spec)
                {
                    Current = pr;
                    Dropped = 0;
                    Retransmitted = 0;
                    return Current.ToString();
                }
            }
            var p = new Profile { Name = "custom" };
            foreach (var part in spec.Split(new[] { ' ', ',' }, StringSplitOptions.RemoveEmptyEntries))
            {
                var kv = part.Split('=');
                if (kv.Length != 2 || !float.TryParse(kv[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var v))
                {
                    continue;
                }
                switch (kv[0])
                {
                    case "lat": p.LatencyMs = (int)v; break;
                    case "jit": p.JitterMs = (int)v; break;
                    case "loss": p.LossPct = v; break;
                    case "bw": p.KBps = (int)v; break;
                }
            }
            Current = p;
            Dropped = 0;
            Retransmitted = 0;
            return Current.ToString();
        }
    }
}
