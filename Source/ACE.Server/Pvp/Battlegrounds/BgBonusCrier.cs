using System;
using System.Collections.Generic;

using ACE.Server.ThreadDungeons;

namespace ACE.Server.Pvp.Battlegrounds
{
    /// <summary>
    /// The Arena Crier's Double Marks announcements (Docs/Pvp/BATTLEGROUNDS.md "Double Marks windows"): a heads-up before a
    /// window opens, a line when it opens, and a line when it ends. PURE and stateful: time, schedule and the warn lead in,
    /// finished chat lines out, so it is unit-testable with a fake clock. One instance lives for the coordinator's lifetime
    /// and is ticked on the world thread.
    ///
    /// <para/>
    /// Edge-triggered: it remembers the (phase, window) it last saw and speaks only when that changes, so each line goes out
    /// once per transition however often it is ticked. The first evaluation (boot, or the tick that re-enables it) speaks
    /// only for a state already in progress: an Open line inside a window, a heads-up inside a warn period, nothing
    /// otherwise. A tick that finds the state moved across a whole transition announces only the state it finds, never the
    /// lines it skipped.
    /// </summary>
    public sealed class BgBonusCrier
    {
        private enum Phase { Idle, Warn, Open }

        private bool initialized;
        private string lastSignature;
        private IReadOnlyList<BgBonusSpan> lastSpans;
        private TimeZoneInfo lastZone;
        private int lastWarn = -1;
        private Phase lastPhase;
        private DateTime? lastId;

        /// <summary>The queue command appended to the Open line (the same one the Crier's queue lines name).</summary>
        public const string QueueHint = " Use /arena join bg.";

        public static string WarnLine(int minutes) =>
            $"Battleground Double Marks begins in {minutes} {(minutes == 1 ? "minute" : "minutes")}! Every battleground match that starts during the window pays double Marks of the Hopeslayer.";

        public static string OpenLine(string remaining) =>
            $"Battleground Double Marks is now live for the next {remaining}! Queue for battlegrounds now.{QueueHint}";

        public static string EndLine(string untilNext) =>
            untilNext == null
                ? "Battleground Double Marks has ended."
                : $"Battleground Double Marks has ended. The next window opens in {untilNext}.";

        private static string Signature(IReadOnlyList<BgBonusSpan> spans, TimeZoneInfo zone, int warnMinutes)
        {
            var parts = new List<string>(spans.Count);

            foreach (var span in spans)
                parts.Add(span.Start.ToString("c") + "-" + span.End.ToString("c"));

            parts.Sort(StringComparer.Ordinal);

            return string.Join(",", parts) + "|" + zone.Id + "|" + warnMinutes;
        }

        /// <summary>
        /// One tick. <paramref name="enabled"/> false (or no windows) clears the memory, so the tick that turns it back on is
        /// a first evaluation. <paramref name="warnMinutes"/> is clamped to 0..180; 0 means no heads-up.
        /// </summary>
        public IReadOnlyList<string> Tick(DateTime utcNow, bool enabled, IReadOnlyList<BgBonusSpan> spans, TimeZoneInfo zone, int warnMinutes)
        {
            var lines = new List<string>();

            if (!enabled || spans == null || spans.Count == 0 || zone == null)
            {
                initialized = false;
                return lines;
            }

            warnMinutes = Math.Clamp(warnMinutes, 0, 180);

            // A config edit (windows, zone or warn lead) changes the identity of the window we are in or waiting for, so it must not
            // read as a transition: the new phase is adopted silently and the next real transition fires once.
            // BgBonusConfig hands back the same cached list per raw string, so unchanged inputs skip building the signature at all.
            var configChanged = false;

            if (!(ReferenceEquals(spans, lastSpans) && zone == lastZone && warnMinutes == lastWarn))
            {
                var signature = Signature(spans, zone, warnMinutes);
                configChanged = initialized && signature != lastSignature;
                lastSignature = signature;
                lastSpans = spans;
                lastZone = zone;
                lastWarn = warnMinutes;
            }

            var (nextStart, nextEnd) = BgBonusWindow.NextTransition(utcNow, spans, zone);
            Phase phase;
            DateTime? id;

            if (BgBonusWindow.IsActive(utcNow, spans, zone) && nextEnd.HasValue)
            {
                phase = Phase.Open;
                id = nextEnd;
            }
            else if (warnMinutes > 0 && nextStart.HasValue && nextStart.Value - utcNow <= TimeSpan.FromMinutes(warnMinutes))
            {
                phase = Phase.Warn;
                id = nextStart;
            }
            else
            {
                phase = Phase.Idle;
                id = null;
            }

            var wasInitialized = initialized;
            var previousPhase = lastPhase;
            var previousId = lastId;

            initialized = true;
            lastPhase = phase;
            lastId = id;

            // A config edit is adopted silently only while the phase is unchanged: an edit that lands on the very tick of a real transition
            // (Idle -> Open, Open -> Idle) must not swallow that transition.
            if ((configChanged && phase == previousPhase) || (wasInitialized && phase == previousPhase && id == previousId))
                return lines;

            switch (phase)
            {
                case Phase.Open:
                    lines.Add(OpenLine(SurveyDay.FormatDuration(nextEnd.Value - utcNow)));
                    break;

                case Phase.Warn:
                    lines.Add(WarnLine((int)Math.Max(1, Math.Ceiling((nextStart.Value - utcNow).TotalMinutes))));
                    break;

                default:
                    if (wasInitialized && previousPhase == Phase.Open)
                        lines.Add(EndLine(nextStart.HasValue ? SurveyDay.FormatDuration(nextStart.Value - utcNow) : null));
                    break;
            }

            return lines;
        }
    }
}
