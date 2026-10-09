using System;
using System.Collections.Generic;

using ACE.Server.Pvp.Battlegrounds;

namespace ACE.Server.Pvp
{
    /// <summary>
    /// One queue's state, as the Arena Crier's caller (<see cref="PvpMatchCoordinator"/>) observes it on a tick.
    /// <paramref name="Needed"/> is players still required to form a match - computed the same way for the
    /// periodic line, the last call and the FFA lobby line (<see cref="ArenaMapCatalog.FfaDecayedTargetSize"/>) -
    /// never a literal the Crier recomputes on its own.
    ///
    /// <para/>
    /// <paramref name="FillSecondsLeft"/> and <paramref name="Room"/> are the battleground's fill window (null /
    /// 0 for every other queue): FillSecondsLeft is non-null only while the room is IN its fill window (Count at or
    /// above pvp_bg_min_players and below pvp_bg_max_players), as whole seconds until it closes rounded UP (zero or
    /// negative once it has closed but the match has not yet formed); Room is the seats still open before the cap.
    /// FillCloseUtc is the window's close time from BattlegroundMatchmaker.FillWindowCloseUtc (null outside the window); it
    /// identifies the fill EPISODE: a different close time is a new episode, so a match that forms and leaves MinPlayers or
    /// more still queued (a new t4, a new close time) announces again. A close time that moves because someone LEFT (the
    /// earliest queuer, say) is not an episode: a new episode needs no stored one, or a changed close time together with a join
    /// (count increase) or <paramref name="MatchFormedSinceLastTick"/> (the coordinator formed a battleground match since the
    /// previous Crier tick).
    /// </summary>
    public sealed record CrierQueueSnapshot(string ModeKey, bool Enabled, int Count, int Needed, int? FillSecondsLeft = null, int Room = 0, DateTime? FillCloseUtc = null, bool MatchFormedSinceLastTick = false);

    /// <summary>
    /// The Arena Crier's decision logic (Docs/Pvp/DESIGN.md "Arena Crier"): PURE - time, dials and queue snapshots
    /// in, finished chat lines out - so it is unit-testable with a fake clock and needs no live player or chat
    /// seam. One instance lives for the coordinator's lifetime; every call to <see cref="Tick"/> is expected to be
    /// on the world thread, same as the coordinator itself.
    ///
    /// <para/>
    /// Three independent behaviours share one instance:
    ///   - Announce: the queue's status line (the periodic line) for an enabled, non-empty queue that still needs
    ///     players (Needed > 0). With pvp_arena_crier_announce_on_join (default) it goes out every time a queue's count
    ///     INCREASES; with it off it goes out every pvp_arena_crier_interval_seconds instead, one line per queue. A queue the matchmaker is deliberately holding back (Needed &lt;= 0 - a rating
    ///     window, a same-IP block) is skipped rather than announced "0 needed".
    ///   - Last call: arms only on a transition where a queue's player count INCREASES and its Needed becomes
    ///     exactly 1. It fires pvp_arena_crier_last_call_delay_seconds later IF the queue still needs exactly 1
    ///     THEN, regardless of what happened in between (formed and reopened, emptied and refilled) - the deadline
    ///     check is the only test that matters, per the owner's ruling. A per-queue cooldown after a fire blocks a
    ///     new arm for pvp_arena_crier_last_call_cooldown_seconds, so join/leave/rejoin churn (a lone 1v1 queuer in
    ///     particular) cannot spam it.
    /// </summary>
    public sealed class PvpArenaCrier
    {
        private sealed class QueueState
        {
            /// <summary>The last observed player count for this queue, so an increase can be detected. Starts at 0.</summary>
            public int PreviousCount;

            /// <summary>Set while a last call is armed and waiting on its delay; null when nothing is pending.</summary>
            public DateTime? ArmedUntilUtc;

            /// <summary>When the last last-call fired for this queue, for the cooldown; null before the first fire.</summary>
            public DateTime? LastFireUtc;

            /// <summary>The close time of the fill-window episode already announced; null when the room is not in a window.</summary>
            public DateTime? FillEpisodeCloseUtc;
        }

        private readonly Dictionary<string, QueueState> queues = new(StringComparer.OrdinalIgnoreCase);

        private DateTime? nextPeriodicUtc;

        /// <summary>
        /// Null before the first Tick; true/false after, so a disabled -&gt; enabled transition can be detected.
        /// While disabled, Tick returns before touching this or anything else, so the NEXT enabled tick is exactly
        /// the transition and re-baselines before any timer or arm check runs.
        /// </summary>
        private bool? wasEnabled;

        private QueueState StateFor(string modeKey)
        {
            if (!queues.TryGetValue(modeKey, out var state))
            {
                state = new QueueState();
                queues[modeKey] = state;
            }

            return state;
        }

        /// <summary>
        /// One tick: advances the periodic timer and every queue's last-call state, and returns the finished chat
        /// lines to announce, in no particular order. Returns an empty list, and touches no state at all, while
        /// <paramref name="dials"/>.CrierEnabled is false.
        /// </summary>
        public IReadOnlyList<string> Tick(DateTime now, PvpArenaDials dials, IReadOnlyList<CrierQueueSnapshot> snapshots)
        {
            var lines = new List<string>();

            if (snapshots == null)
                return lines;

            if (!dials.CrierEnabled)
            {
                wasEnabled = false;
                return lines;
            }

            if (wasEnabled == false)
            {
                // Toggle burst guard: a stale nextPeriodicUtc or an armed last call from before the mode was
                // disabled must never fire on the tick that re-enables it. Re-baseline every queue this tick sees
                // to ITS current count (so the very next StepQueue call sees no increase) and clear every arm,
                // and push the periodic timer a full fresh interval out, all before anything below runs.
                nextPeriodicUtc = now.AddSeconds(dials.CrierIntervalSeconds);

                foreach (var snapshot in snapshots)
                {
                    var state = StateFor(snapshot.ModeKey);
                    state.PreviousCount = snapshot.Count;
                    state.ArmedUntilUtc = null;
                    state.FillEpisodeCloseUtc = snapshot.FillCloseUtc;
                }
            }

            wasEnabled = true;

            foreach (var snapshot in snapshots)
                lines.AddRange(StepQueue(now, dials, snapshot));

            if (dials.CrierAnnounceOnJoin)
            {
                // Per-join mode: the interval timer is idle. Clearing it means switching to interval mode later
                // starts a FULL fresh interval (no immediate periodic line), and switching back needs no reset.
                nextPeriodicUtc = null;
                return lines;
            }

            if (!nextPeriodicUtc.HasValue)
                nextPeriodicUtc = now.AddSeconds(dials.CrierIntervalSeconds);

            if (now >= nextPeriodicUtc.Value)
            {
                foreach (var snapshot in snapshots)
                {
                    if (!snapshot.Enabled || snapshot.Count <= 0 || snapshot.Needed <= 0)
                        continue;

                    lines.Add(StatusLine(snapshot));
                }

                nextPeriodicUtc = now.AddSeconds(dials.CrierIntervalSeconds);
            }

            return lines;
        }

        private static string StatusLine(CrierQueueSnapshot snapshot) =>
            PvpArenaText.Fill(PvpArenaText.CrierPeriodicFor(snapshot.ModeKey),
                ("mode", PvpArenaText.CrierModeLabel(snapshot.ModeKey)),
                ("count", snapshot.Count),
                ("needed", snapshot.Needed),
                ("arg", ArenaMapCatalog.JoinWord(snapshot.ModeKey)));

        private IEnumerable<string> StepQueue(DateTime now, PvpArenaDials dials, CrierQueueSnapshot snapshot)
        {
            var state = StateFor(snapshot.ModeKey);

            if (!snapshot.Enabled)
            {
                // A disabled mode carries no last-call state forward: re-enabling it starts clean.
                state.PreviousCount = 0;
                state.ArmedUntilUtc = null;
                state.FillEpisodeCloseUtc = null;
                yield break;
            }

            // A pending last call is checked against THIS tick's fresh snapshot before anything else updates it,
            // so the deadline sees the queue exactly as it stands, however it got there.
            if (state.ArmedUntilUtc.HasValue && now >= state.ArmedUntilUtc.Value)
            {
                if (snapshot.Needed == 1)
                {
                    yield return PvpArenaText.Fill(PvpArenaText.CrierLastCallFor(snapshot.ModeKey),
                        ("mode", PvpArenaText.CrierModeLabel(snapshot.ModeKey)),
                        ("arg", ArenaMapCatalog.JoinWord(snapshot.ModeKey)));

                    state.LastFireUtc = now;
                }

                state.ArmedUntilUtc = null;
            }

            // One comparison, read BEFORE PreviousCount is overwritten below, serves the last-call arm, the per-join
            // status line and the per-join fill line.
            var increased = snapshot.Count > state.PreviousCount;

            if (increased && snapshot.Needed == 1 && !state.ArmedUntilUtc.HasValue)
            {
                var cooldownElapsed = !state.LastFireUtc.HasValue
                    || (now - state.LastFireUtc.Value).TotalSeconds >= dials.CrierLastCallCooldownSeconds;

                if (cooldownElapsed)
                {
                    state.ArmedUntilUtc = now.AddSeconds(dials.CrierLastCallDelaySeconds);
                }
            }

            state.PreviousCount = snapshot.Count;

            // The join that arms a last call still announces its status line: the last call, a few seconds later, is a reminder,
            // and if the final player joins before it fires nobody would otherwise ever hear the '1 more needed' line.
            if (dials.CrierAnnounceOnJoin && increased && snapshot.Count > 0 && snapshot.Needed > 0)
                yield return StatusLine(snapshot);

            // Battleground fill window: once on the transition into the window, and (per-join mode) again on every
            // further join while it stays open. A closed window (seconds <= 0) or a full room announces nothing but
            // keeps the episode, so it cannot re-fire until the close time changes or the room leaves the window (null).
            if (!snapshot.FillCloseUtc.HasValue || !snapshot.FillSecondsLeft.HasValue)
            {
                state.FillEpisodeCloseUtc = null;
            }
            else
            {
                var announceable = snapshot.FillSecondsLeft.Value > 0 && snapshot.Room > 0;
                var closeChanged = state.FillEpisodeCloseUtc != snapshot.FillCloseUtc;
                var entered = state.FillEpisodeCloseUtc == null || (closeChanged && (increased || snapshot.MatchFormedSinceLastTick));

                state.FillEpisodeCloseUtc = snapshot.FillCloseUtc;

                if (announceable && (entered || (dials.CrierAnnounceOnJoin && increased)))
                {
                    yield return PvpArenaText.Fill(BattlegroundText.CrierFilling,
                        ("seconds", snapshot.FillSecondsLeft.Value),
                        ("count", snapshot.Count),
                        ("room", snapshot.Room));
                }
            }
        }
    }
}
