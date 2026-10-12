using System;
using System.Collections.Generic;
using System.Linq;
using BepInEx.Logging;

namespace LuckyTowerCoop.Dev
{
    /// <summary>Ring buffer of all BepInEx logs (including Unity) for the "log" debug command.</summary>
    internal sealed class LogBuffer : ILogListener
    {
        private const int Capacity = 8000;
        private static readonly Queue<string> s_lines = new Queue<string>();
        private static readonly object s_lock = new object();
        private static long s_seq;

        public static LogBuffer Instance { get; } = new LogBuffer();

        public void LogEvent(object sender, LogEventArgs e)
        {
            lock (s_lock)
            {
                s_seq++;
                s_lines.Enqueue($"{s_seq} [{e.Level,-7}:{e.Source.SourceName}] {e.Data}");
                while (s_lines.Count > Capacity)
                {
                    s_lines.Dequeue();
                }
            }
        }

        public static string Tail(int count, string filter = null)
        {
            lock (s_lock)
            {
                IEnumerable<string> lines = s_lines;
                if (!string.IsNullOrEmpty(filter))
                {
                    lines = lines.Where(l => l.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0);
                }
                var list = lines.ToList();
                return string.Join("\n", list.Skip(Math.Max(0, list.Count - count)));
            }
        }

        public void Dispose()
        {
        }
    }
}
