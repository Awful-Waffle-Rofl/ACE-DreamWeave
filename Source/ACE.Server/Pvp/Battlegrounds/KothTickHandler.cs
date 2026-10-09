using System;
using System.Collections.Generic;
using System.Linq;

namespace ACE.Server.Pvp.Battlegrounds
{
    /// <summary>
    /// King of the Hill's per-match tick behaviour (Docs/Pvp/BATTLEGROUNDS.md "King of the Hill"). One instance per
    /// match: it snapshots its dials at construction and keeps its own NextScoreUtc and announce state. Pure: reads no
    /// clock, and does nothing unless the context is an <see cref="IBattlegroundMatchContext"/>.
    ///
    /// Each score tick: sample the zone; a sample counts when its seat counts on the zone
    /// (<see cref="BattlegroundSeats.CountsOnZone"/>), is in the instance, alive, not teleporting and inside the
    /// <see cref="KothZone"/> (with IP dedupe on, each IP counts once per team). Exactly one team with at least
    /// min_holders counted and the other with none holds: the holder gains hold_points, every other team loses
    /// decay_points, floored at 0. Contested or empty changes nothing. Dedupe is a scoring rule only: every eligible player on the point is drained.
    /// </summary>
    public sealed class KothTickHandler : IObjectiveModeHandler
    {
        private enum HoldKind { Neutral, Contested, Held }

        private readonly BattlegroundDials _dials;
        private BattlegroundLayout _layout;
        private int _moves;

        private KothZone _zone;
        private KothSiteChoice _site = KothSiteChoice.Start;
        private int _movesMade;

        private DateTime? _nextScoreUtc;
        private DateTime? _nextAnnounceUtc;
        private HoldKind _holdKind = HoldKind.Neutral;
        private int _holder = -1;

        /// <param name="layout">
        /// The map the hill moves over (<see cref="BattlegroundLayout.ZonePairs"/>). Null, or a layout with no pairs, means
        /// the hill never moves, exactly as before the moving hill existed. The number of moves is
        /// pvp_bg_koth_hill_moves clamped to 0..<see cref="KothHillSchedule.MaxMoves"/> (none when it has no pairs).
        /// </param>
        public KothTickHandler(BattlegroundDials dials, KothZone zone, BattlegroundLayout layout = null)
        {
            _dials = dials;
            _zone = zone;
            _layout = layout;
            _moves = layout == null || layout.ZonePairs.Count == 0 ? 0 : Math.Clamp(dials.KothHillMoves, 0, KothHillSchedule.MaxMoves);
        }

        /// <summary>
        /// Binds the match's resolved layout, once, when the match forms (the mode definition builds this handler before the
        /// match's layout is known). The starting zone is rebuilt from the layout through
        /// <see cref="BattlegroundModes.KothZoneFor(BattlegroundLayout, BattlegroundDials)"/> and the move count is
        /// pvp_bg_koth_hill_moves clamped to the layout's pairs, so the scored zone and the marker ring (planned from the same
        /// layout) can never disagree. Ignored once the hill has moved or for a null layout.
        /// </summary>
        public void BindLayout(BattlegroundLayout layout)
        {
            if (layout == null || _movesMade > 0)
                return;

            _layout = layout;
            _zone = BattlegroundModes.KothZoneFor(layout, _dials);
            _site = KothSiteChoice.Start;
            _moves = layout.ZonePairs.Count == 0 ? 0 : Math.Clamp(_dials.KothHillMoves, 0, KothHillSchedule.MaxMoves);
        }

        /// <summary>
        /// The control zone this handler scores NOW: the starting zone until the hill moves, then the current site's. The
        /// coordinator plans the zone-marker ring from THIS value, so the ring and the scored cylinder cannot disagree
        /// whichever layout the match resolved.
        /// </summary>
        public KothZone Zone => _zone;

        /// <summary>The hill site the zone is on now (the centre until the first move).</summary>
        public KothSiteChoice Site => _site;

        /// <summary>How many times the hill has moved.</summary>
        public int MovesMade => _movesMade;

        /// <summary>The next time a score tick runs. Null until the match goes live.</summary>
        public DateTime? NextScoreUtc => _nextScoreUtc;

        /// <summary>"West" / "East" (<see cref="BattlegroundText.TeamName"/>).</summary>
        public string TeamName(int team) => BattlegroundText.TeamName(team);

        /// <summary>
        /// The result line the coordinator has always sent for a King of the Hill match: the score win, the timeout draw, or the timeout
        /// win; null for any other outcome.
        /// </summary>
        public string ResultLine(MatchOutcome outcome, IReadOnlyDictionary<int, int> scores)
        {
            var score0 = scores != null && scores.TryGetValue(0, out var a) ? a : 0;
            var score1 = scores != null && scores.TryGetValue(1, out var b) ? b : 0;

            return outcome.Reason == EndReason.Score && outcome.WinningTeams.Count == 1 ? BattlegroundText.ScoreWin(outcome.WinningTeams.First(), score0, score1)
                : outcome.Reason == EndReason.Timeout && outcome.IsDraw ? BattlegroundText.TimeUpDraw(score0, score1)
                : outcome.Reason == EndReason.Timeout && outcome.WinningTeams.Count == 1 ? BattlegroundText.TimeUpWin(outcome.WinningTeams.First(), score0, score1)
                : null;
        }

        /// <summary>King of the Hill sends no role line: both teams play the same objective.</summary>
        public string RoleLine(int team) => null;

        /// <summary>King of the Hill has no vulnerable objective to name.</summary>
        public string VulnerableLine() => null;

        /// <summary>Binds the layout (<see cref="BindLayout"/>); King of the Hill has no crystal plan.</summary>
        public void BindMatch(BattlegroundLayout layout, AttackDefendPlan plan) => BindLayout(layout);

        /// <summary>The ring outlines the zone this handler scores (<see cref="Zone"/>).</summary>
        public KothZone? MarkerZone => _zone;

        /// <summary>King of the Hill has no destructible objectives.</summary>
        public bool OnObjectiveDestroyed(IObjectiveMatchContext ctx, int index) => false;

        /// <summary>King of the Hill has no destructible objectives.</summary>
        public bool IsObjectiveStanding(int index) => false;

        /// <summary>The /arena status suffix: both scores and the target, or empty without a score board or target.</summary>
        public string StatusLine(IReadOnlyDictionary<int, int> scores, int scoreTarget)
        {
            if (scores == null || scoreTarget <= 0)
                return "";

            return BattlegroundText.StatusScore(scores.TryGetValue(0, out var a) ? a : 0, scores.TryGetValue(1, out var b) ? b : 0, scoreTarget);
        }

        public void OnLive(IMatchContext m)
        {
            if (m.LiveSinceUtc != null)
                StartClocks(m.LiveSinceUtc.Value);
        }

        public void OnTick(IMatchContext m, DateTime utcNow)
        {
            if (m is not IBattlegroundMatchContext ctx)
                return;

            // OnLive had no live time to read (or never ran): start the clocks from this tick, never from a clock read.
            if (_nextScoreUtc == null)
                StartClocks(utcNow);

            if (utcNow >= _nextScoreUtc.Value)
            {
                ScoreTick(ctx, utcNow);

                var interval = TimeSpan.FromSeconds(_dials.KothTickSeconds);
                var next = _nextScoreUtc.Value + interval;
                _nextScoreUtc = next <= utcNow ? utcNow + interval : next;
            }

            // After scoring, so a move this tick only changes the zone the NEXT score tick reads.
            CheckHillMove(ctx, utcNow);
        }

        /// <summary>
        /// The moving hill (Docs/Pvp/BATTLEGROUNDS.md "Moving hill"): asks <see cref="KothHillSchedule.NextSite"/> whether the
        /// next step is due, and if so switches the scored zone, resets the holding state (so no holder carries over and the
        /// next tick announces the hold afresh), tells every participant and has the coordinator re-plan the markers.
        /// </summary>
        private void CheckHillMove(IBattlegroundMatchContext ctx, DateTime utcNow)
        {
            if (_movesMade >= _moves || ctx.LiveSinceUtc == null)
                return;

            // A tick that decides the match (any team at or past the target) sends no "hill has moved" line.
            if (ctx.ScoreBoard.Values.Any(v => v >= _dials.KothScoreTarget))
                return;

            ctx.ScoreBoard.TryGetValue(0, out var west);
            ctx.ScoreBoard.TryGetValue(1, out var east);

            var elapsed = (utcNow - ctx.LiveSinceUtc.Value).TotalSeconds;

            var choice = KothHillSchedule.NextSite(west, east, _dials.KothScoreTarget, elapsed, _dials.TimeLimitSecondsKoth,
                _movesMade, _moves, _site.Side, ctx.RollHillTieBreakWest);

            if (choice == null)
                return;

            _site = choice.Value;
            _movesMade++;
            _zone = BattlegroundModes.KothZoneFor(_layout, _dials, _site);
            var (x, y) = _layout.SiteFor(_site);
            _holdKind = HoldKind.Neutral;
            _holder = -1;

            ctx.Announce(BattlegroundText.HillMoved(KothHillSchedule.Compass(x - _layout.ZoneX, y - _layout.ZoneY)));
            ctx.ReplaceZoneMarkers(_zone);
        }

        private void StartClocks(DateTime from)
        {
            _nextScoreUtc = from + TimeSpan.FromSeconds(_dials.KothTickSeconds);
            _nextAnnounceUtc = from + TimeSpan.FromSeconds(_dials.KothScoreAnnounceSeconds);
        }

        private void ScoreTick(IBattlegroundMatchContext ctx, DateTime utcNow)
        {
            var eligible = EligibleSamples(ctx);
            var counted = DedupeForScoring(eligible);

            var perTeam = counted.GroupBy(s => s.TeamIndex).ToDictionary(g => g.Key, g => g.Count());
            var teamsPresent = perTeam.Keys.ToList();

            int? holder = null;

            if (teamsPresent.Count == 1 && perTeam[teamsPresent[0]] >= _dials.KothMinHolders)
                holder = teamsPresent[0];

            if (holder != null)
            {
                foreach (var team in ctx.Teams)
                {
                    ctx.ScoreBoard.TryGetValue(team.TeamIndex, out var current);

                    ctx.ScoreBoard[team.TeamIndex] = team.TeamIndex == holder.Value
                        ? current + _dials.KothHoldPoints
                        : Math.Max(0, current - _dials.KothDecayPoints);
                }
            }

            // Dedupe is a scoring rule only: every eligible player on the point is drained.
            foreach (var s in eligible)
                ctx.RequestDrain(s.CharacterId, _dials.KothDrainHealth, _dials.KothDrainStamina, _dials.KothDrainMana, _dials.KothDrainLethal);

            var kind = holder != null ? HoldKind.Held : teamsPresent.Count > 1 ? HoldKind.Contested : HoldKind.Neutral;
            var heldBy = holder ?? -1;

            if (kind != _holdKind || heldBy != _holder)
            {
                _holdKind = kind;
                _holder = heldBy;

                ctx.Announce(kind switch
                {
                    HoldKind.Held => BattlegroundText.ZoneHeld(heldBy),
                    HoldKind.Contested => BattlegroundText.ZoneContested(),
                    _ => BattlegroundText.ZoneNeutral()
                });
            }

            if (_nextAnnounceUtc != null && utcNow >= _nextAnnounceUtc.Value)
            {
                ctx.ScoreBoard.TryGetValue(0, out var west);
                ctx.ScoreBoard.TryGetValue(1, out var east);
                ctx.Announce(BattlegroundText.Scores(west, east));
                _nextAnnounceUtc = utcNow + TimeSpan.FromSeconds(_dials.KothScoreAnnounceSeconds);
            }
        }

        private List<BattlegroundZoneSample> EligibleSamples(IBattlegroundMatchContext ctx)
        {
            var seats = ctx.ActiveSeats.ToDictionary(s => s.Participant.CharacterId);
            var eligible = new List<BattlegroundZoneSample>();

            foreach (var s in ctx.SampleZone())
            {
                if (!seats.TryGetValue(s.CharacterId, out var seat) || !BattlegroundSeats.CountsOnZone(seat))
                    continue;

                if (!s.InInstance || s.IsDead || s.IsTeleporting || !_zone.Contains(s.X, s.Y, s.Z))
                    continue;

                eligible.Add(s);
            }

            return eligible;
        }

        private List<BattlegroundZoneSample> DedupeForScoring(List<BattlegroundZoneSample> eligible)
        {
            var seenIps = new HashSet<(int Team, string Ip)>();
            var counted = new List<BattlegroundZoneSample>();

            foreach (var s in eligible)
            {
                // A null or empty IP key means "unknown": it never dedupes.
                if (_dials.KothDedupeIp && !string.IsNullOrEmpty(s.IpKey) && !seenIps.Add((s.TeamIndex, s.IpKey)))
                    continue;

                counted.Add(s);
            }

            return counted;
        }
    }
}