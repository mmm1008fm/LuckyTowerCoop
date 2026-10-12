using System;
using System.Diagnostics;
using UnityEngine;

namespace LuckyTowerCoop.Net
{
    /// <summary>
    /// Оценка «сколько сейчас времени у хоста» по меткам времени в снапшотах.
    /// Смещение часов берём по наименее задержанному пакету (максимум hostTime − localTime), джиттер — насколько
    /// остальные пакеты опаздывают относительно него (верхняя оценка, быстро растёт и медленно спадает).
    /// </summary>
    internal sealed class NetClock
    {
        private double m_offset;
        private double m_lastLocal;
        private bool m_has;
        private float m_jitter;

        public static double LocalNow => Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;

        /// <summary>Разброс задержки пакетов, с (≈ 95-й процентиль).</summary>
        public float Jitter => m_jitter;

        public bool Ready => m_has;

        public double HostNow => LocalNow + m_offset;

        public void OnSample(float hostTime)
        {
            double local = LocalNow;
            double s = hostTime - local;
            if (!m_has || s > m_offset || s < m_offset - 1.0)
            {
                // пакет пришёл быстрее прежнего лучшего (или хост перезапустился) — новое смещение
                m_offset = s;
                m_has = true;
            }
            else
            {
                // медленно опускаем оценку: часы могут уплывать, маршрут — меняться
                m_offset = Math.Max(s, m_offset - 0.002 * (local - m_lastLocal));
            }
            m_lastLocal = local;
            float late = (float)(m_offset - s);
            m_jitter = late > m_jitter ? Mathf.Lerp(m_jitter, late, 0.25f) : Mathf.Lerp(m_jitter, late, 0.005f);
        }

        public void Reset()
        {
            m_has = false;
            m_jitter = 0f;
        }
    }

    /// <summary>
    /// Буфер снапшотов одной сущности: показываем её в моменте «время хоста − задержка показа» и ведём между
    /// соседними снапшотами по времени. Позднее пришедший пакет не даёт рывка, потерянный — короткая экстраполяция.
    /// </summary>
    internal sealed class InterpBuffer
    {
        private const int N = 12;
        private readonly float[] m_t = new float[N];
        private readonly Vector3[] m_p = new Vector3[N];
        private readonly float[] m_r = new float[N];
        private readonly bool[] m_f = new bool[N];
        private readonly Vector2[] m_v = new Vector2[N];
        private int m_count;
        private int m_head = -1;

        public float NewestTime => m_count > 0 ? m_t[m_head] : float.NegativeInfinity;

        public Vector2 NewestVel => m_count > 0 ? m_v[m_head] : Vector2.zero;

        public void Clear()
        {
            m_count = 0;
            m_head = -1;
        }

        /// <param name="interval">как часто хост шлёт эту сущность, когда она движется</param>
        public void Add(float t, Vector3 p, float rot, bool flip, Vector2 vel, float interval)
        {
            if (m_count > 0)
            {
                float last = m_t[m_head];
                if (t <= last)
                {
                    return; // устаревший (обогнали) — уже есть новее
                }
                if (t - last > interval * 2.5f)
                {
                    // хост шлёт только изменившееся: объект стоял, пока не было пакетов. Без этой точки движение
                    // размазалось бы на всю паузу.
                    Push(t - interval, m_p[m_head], m_r[m_head], m_f[m_head], Vector2.zero);
                }
            }
            Push(t, p, rot, flip, vel);
        }

        private void Push(float t, Vector3 p, float rot, bool flip, Vector2 vel)
        {
            m_head = (m_head + 1) % N;
            m_t[m_head] = t;
            m_p[m_head] = p;
            m_r[m_head] = rot;
            m_f[m_head] = flip;
            m_v[m_head] = vel;
            if (m_count < N)
            {
                m_count++;
            }
        }

        private int Idx(int back) => (m_head - back + N * 2) % N;

        /// <summary>Положение в момент времени хоста <paramref name="rt"/>. extrapolated — снапшотов на этот момент ещё нет.</summary>
        public bool Sample(float rt, float maxExtrapolate, out Vector3 pos, out float rot, out bool flip, out bool extrapolated, out Vector2 velocity)
        {
            velocity = Vector2.zero;
            pos = default;
            rot = 0f;
            flip = false;
            extrapolated = false;
            if (m_count == 0)
            {
                return false;
            }
            int newest = m_head;
            if (rt >= m_t[newest])
            {
                // данных на этот момент нет: немного продолжаем движение по скорости, дальше стоим
                float dt = Mathf.Min(rt - m_t[newest], maxExtrapolate);
                Vector2 vel = m_v[newest];
                if (vel == Vector2.zero && m_count >= 2)
                {
                    int prev = Idx(1);
                    float span = m_t[newest] - m_t[prev];
                    if (span > 0.001f && span < 0.25f && (m_p[newest] - m_p[prev]).sqrMagnitude < 9f)
                    {
                        vel = (m_p[newest] - m_p[prev]) / span;
                    }
                }
                pos = m_p[newest] + (Vector3)(vel * dt);
                velocity = dt < maxExtrapolate ? vel : Vector2.zero;
                rot = m_r[newest];
                flip = m_f[newest];
                extrapolated = rt - m_t[newest] > 0.004f;
                return true;
            }
            for (int back = 1; back < m_count; back++)
            {
                int older = Idx(back);
                int newer = Idx(back - 1);
                if (m_t[older] <= rt)
                {
                    float span = m_t[newer] - m_t[older];
                    float a = span > 0.0001f ? (rt - m_t[older]) / span : 1f;
                    if ((m_p[newer] - m_p[older]).sqrMagnitude > 9f)
                    {
                        // телепорт (дверь, возрождение): не тянем через всю комнату
                        pos = m_p[older];
                        rot = m_r[older];
                        flip = m_f[older];
                        return true;
                    }
                    pos = Vector3.LerpUnclamped(m_p[older], m_p[newer], a);
                    velocity = span > 0.0001f ? (Vector2)((m_p[newer] - m_p[older]) / span) : Vector2.zero;
                    rot = Mathf.LerpAngle(m_r[older], m_r[newer], a);
                    flip = a < 0.5f ? m_f[older] : m_f[newer];
                    return true;
                }
            }
            int oldest = Idx(m_count - 1);
            pos = m_p[oldest];
            rot = m_r[oldest];
            flip = m_f[oldest];
            return true;
        }
    }

    /// <summary>Метрики качества сети у клиента (dev «net quality», стенд tools\net-quality.ps1).</summary>
    internal static class NetQuality
    {
        private const int LagCap = 4096;
        private static readonly float[] s_lag = new float[LagCap];
        private static int s_lagN;
        private static int s_movingFrames;
        private static int s_hitchFrames;
        private static int s_extrapFrames;
        private static int s_snaps;
        private static int s_gaps;
        private static double s_lastSeatPacket;
        private static float s_maxGapMs;
        private static Vector3 s_prevPartner;
        private static bool s_hasPrev;
        private static double s_since = NetClock.LocalNow;

        public static void Reset()
        {
            s_lagN = 0;
            s_movingFrames = 0;
            s_hitchFrames = 0;
            s_extrapFrames = 0;
            s_snaps = 0;
            s_gaps = 0;
            s_maxGapMs = 0f;
            s_hasPrev = false;
            s_since = NetClock.LocalNow;
        }

        public static void OnInputLag(float ms)
        {
            if (ms > 0f && s_lagN < LagCap)
            {
                s_lag[s_lagN++] = ms;
            }
        }

        /// <summary>Пришёл пакет с персонажами игроков (60 Гц): провалы в потоке.</summary>
        public static void OnSeatPacket()
        {
            double now = NetClock.LocalNow;
            if (s_lastSeatPacket > 0)
            {
                float gap = (float)(now - s_lastSeatPacket) * 1000f;
                if (gap > 50f)
                {
                    s_gaps++;
                }
                if (gap > s_maxGapMs && gap < 5000f)
                {
                    s_maxGapMs = gap;
                }
            }
            s_lastSeatPacket = now;
        }

        /// <summary>
        /// Кадр показа напарника: «рывок», если показанный сдвиг сильно расходится с его скоростью у хоста
        /// (стоит, хотя идёт, — или прыгает вперёд).
        /// </summary>
        public static void OnPartnerFrame(Vector3 shown, Vector2 hostVel, float dt, bool extrapolated)
        {
            if (s_hasPrev && dt > 0.0001f)
            {
                float expected = hostVel.magnitude * dt;
                if (hostVel.magnitude > 0.8f)
                {
                    s_movingFrames++;
                    float moved = (shown - s_prevPartner).magnitude;
                    if (moved < expected * 0.35f || moved > expected * 2.5f)
                    {
                        s_hitchFrames++;
                    }
                    if (extrapolated)
                    {
                        s_extrapFrames++;
                    }
                }
            }
            s_prevPartner = shown;
            s_hasPrev = true;
        }

        public static void OnCorrectionSnap() => s_snaps++;

        public static string Report(string mode, float delayMs, float jitterMs)
        {
            float p50 = 0f, p95 = 0f;
            if (s_lagN > 0)
            {
                var a = new float[s_lagN];
                Array.Copy(s_lag, a, s_lagN);
                Array.Sort(a);
                p50 = a[s_lagN / 2];
                p95 = a[Math.Min(s_lagN - 1, (int)(s_lagN * 0.95f))];
            }
            float hitch = s_movingFrames > 0 ? 100f * s_hitchFrames / s_movingFrames : 0f;
            float extrap = s_movingFrames > 0 ? 100f * s_extrapFrames / s_movingFrames : 0f;
            return string.Format(System.Globalization.CultureInfo.InvariantCulture,
                "mode={0} link={1} secs={2:F0} lag50={3:F0} lag95={4:F0} hitch={5:F1}% movingFrames={6} extrap={7:F1}% gaps={8} maxGap={9:F0} snaps={10} delay={11:F0} jitter={12:F0} dropped={13} retrans={14}",
                mode, NetLinkSim.Current.Name, NetClock.LocalNow - s_since, p50, p95, hitch, s_movingFrames, extrap, s_gaps, s_maxGapMs, s_snaps,
                delayMs, jitterMs, NetLinkSim.Dropped, NetLinkSim.Retransmitted);
        }
    }
}
