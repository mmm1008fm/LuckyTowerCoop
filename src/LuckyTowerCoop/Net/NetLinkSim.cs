using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;

namespace LuckyTowerCoop.Net
{
    /// <summary>
    /// Сетевая лаборатория: имитация плохого канала на приёме — задержка, разброс задержки (джиттер), потери, узкий канал.
    /// Профиль ставится на обеих копиях (dev «net link wifi» или --coop-link wifi) — получается симметричный канал.
    /// Работает для любого транспорта (Direct и Steam), потому что стоит на входе данных в игру.
    /// Ненадёжные пакеты теряются и обгоняют друг друга, надёжные при «потере» ждут повтора и держат очередь за собой —
    /// как в настоящем LiteNetLib/Steam.
    /// </summary>
    internal sealed class NetLinkSim
    {
        public struct Profile
        {
            public string Name;
            public int LatencyMs;   // в одну сторону
            public int JitterMs;    // верхняя граница случайной добавки (обычно маленькая, иногда всплеск)
            public float LossPct;
            public int KBps;        // ширина канала на приём, 0 — без ограничения

            public bool Active => LatencyMs > 0 || JitterMs > 0 || LossPct > 0f || KBps > 0;

            public override string ToString() => !Active ? "off" :
                string.Format(CultureInfo.InvariantCulture, "{0} ({1}±{2} мс, потери {3}%{4})", Name, LatencyMs, JitterMs, LossPct, KBps > 0 ? $", {KBps} кБ/с" : "");
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

        /// <summary>Текущий профиль (один на процесс: в игре одна сессия).</summary>
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

        /// <summary>Принять входящий пакет в имитацию. false — имитация выключена, доставлять сразу.</summary>
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
                // надёжный потерялся: отправитель заметит и повторит примерно через круг
                Retransmitted++;
                delayMs += 2 * p.LatencyMs + p.JitterMs + 20;
            }
            double due = now + delayMs / 1000.0;
            if (p.KBps > 0)
            {
                // узкий канал: пакеты стоят в очереди на «проводе», задержка растёт при всплесках
                m_wireFree = Math.Max(m_wireFree, now) + length / (p.KBps * 1024.0);
                due = Math.Max(due, m_wireFree + p.LatencyMs / 1000.0);
            }
            if (reliable)
            {
                // надёжные строго по порядку: потерянный держит все следующие (блокировка очереди)
                due = Math.Max(due, m_lastReliableDue);
                m_lastReliableDue = due;
            }
            var copy = new byte[length];
            Buffer.BlockCopy(data, offset, copy, 0, length);
            m_q.Add(new Item { Due = due, Seq = m_seq++, Data = copy });
            return true;
        }

        /// <summary>Отдать созревшие пакеты (вызывать каждый кадр из Poll транспорта).</summary>
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

        /// <summary>Джиттер как в жизни: чаще всего маленький, иногда всплеск до верхней границы.</summary>
        private double JitterMs(int max)
        {
            if (max <= 0)
            {
                return 0;
            }
            double u = 1.0 - m_rng.NextDouble();
            return Math.Min(1.0, -Math.Log(u) * 0.3) * max;
        }

        /// <summary>«wifi» | «off» | «lat=80 jit=40 loss=3 bw=64». Возвращает описание.</summary>
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
