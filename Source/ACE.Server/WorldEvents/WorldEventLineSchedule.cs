using System;
using System.Collections.Generic;
using System.Linq;

namespace ACE.Server.WorldEvents
{
    /// <summary>
    /// World Event lines that go out a few seconds later instead of at once (2026-10-19), so a stage change reads in
    /// order: the outcome, the thank-you, the story beat, the next stage. <see cref="Shared"/> is flushed once a second by
    /// WorldEventManager.Tick, so a line goes out within about a second of its time. Thread-safe.
    /// </summary>
    public sealed class WorldEventLineSchedule
    {
        public static readonly WorldEventLineSchedule Shared = new WorldEventLineSchedule();

        private readonly object sync = new object();
        private readonly List<Entry> entries = new List<Entry>();
        private long sequence;

        private sealed class Entry
        {
            public double DueAt;
            public long Order;
            public string Text;
            public Func<bool> StillWanted;
        }

        /// <summary>How many lines are waiting.</summary>
        public int Count
        {
            get
            {
                lock (sync)
                    return entries.Count;
            }
        }

        /// <summary>
        /// Queues <paramref name="text"/> for <paramref name="dueAt"/> (unix seconds). <paramref name="stillWanted"/>, when
        /// given, is asked at that time; false drops the line (say, the next stage was stopped before its announcement).
        /// </summary>
        public void Add(double dueAt, string text, Func<bool> stillWanted = null)
        {
            if (string.IsNullOrEmpty(text) || !double.IsFinite(dueAt))
                return;

            lock (sync)
                entries.Add(new Entry { DueAt = dueAt, Order = sequence++, Text = text, StillWanted = stillWanted });
        }

        /// <summary>
        /// Queues each line of a plan at <paramref name="start"/> plus its delay. <paramref name="nextStageStillOn"/> guards
        /// the lines about the next stage (<see cref="WorldEventChain.PacedLine.AboutNextStage"/>).
        /// </summary>
        public void AddPlan(double start, IEnumerable<WorldEventChain.PacedLine> plan, Func<bool> nextStageStillOn = null)
        {
            foreach (var line in plan ?? Enumerable.Empty<WorldEventChain.PacedLine>())
                Add(start + Math.Max(0, line.Delay), line.Text, line.AboutNextStage ? nextStageStillOn : null);
        }

        /// <summary>
        /// The lines due by <paramref name="now"/>, in time order (ties in the order they were added), removed from the
        /// schedule. Lines whose stillWanted says false are dropped.
        /// </summary>
        public List<string> TakeDue(double now)
        {
            List<Entry> due;

            lock (sync)
            {
                due = entries.Where(e => e.DueAt <= now).OrderBy(e => e.DueAt).ThenBy(e => e.Order).ToList();

                foreach (var e in due)
                    entries.Remove(e);
            }

            var lines = new List<string>();

            foreach (var e in due)
            {
                bool wanted;

                try
                {
                    wanted = e.StillWanted?.Invoke() ?? true;
                }
                catch
                {
                    wanted = false;
                }

                if (wanted)
                    lines.Add(e.Text);
            }

            return lines;
        }

        /// <summary>Drops every waiting line (tests, shutdown).</summary>
        public void Clear()
        {
            lock (sync)
                entries.Clear();
        }
    }
}
