using System;
using System.Collections.Generic;
using System.Linq;

namespace ACE.Server.Pvp.Battlegrounds
{
    /// <summary>
    /// Attack/Defend's per-match tick behaviour (Docs/Pvp/ATTACK-DEFEND.md "Alerts"). One instance per match. It snapshots its dials at
    /// construction, holds the match's <see cref="AttackDefendPlan"/> and keeps its own alert and status state. Pure: reads no clock and
    /// does nothing unless the context is an <see cref="IObjectiveMatchContext"/>.
    ///
    /// <para/>
    /// Destruction is PUSHED: the coordinator calls <see cref="OnCrystalDestroyed"/> when a crystal's death intent drains, and the first
    /// call for an index adds one to <c>ScoreBoard[0]</c> (the only place the destroyed count lives) and tells everyone; a duplicate or
    /// out-of-range index is ignored. Health is POLLED, cosmetically, through <see cref="IObjectiveMatchContext.SampleCrystals"/>: when a
    /// standing crystal has lost another whole step of pvp_bg_ad_alert_step_percent, the defenders (only) hear about it, at most once
    /// per pvp_bg_ad_alert_min_interval_seconds across the match. A throttled alert is deferred, not dropped: the step stays unannounced
    /// and the next tick past the interval sends it with the then-current percentage. A periodic status line goes to everyone every
    /// pvp_bg_ad_status_announce_seconds.
    ///
    /// <para/>
    /// Sequential crystals (<see cref="AttackDefendPlan.Sequence"/>, Docs/Pvp/ATTACK-DEFEND.md "Sequential crystals"): the plan's
    /// <see cref="CrystalSequence"/> is where the vulnerable crystal lives, next to the destroyed set this handler already keeps.
    /// <see cref="OnCrystalDestroyed"/> advances it and announces the unlock; the crystal damage gate reads it. A sealed crystal sends no
    /// under-attack alert, and the role and status lines name the order and the vulnerable crystal.
    /// </summary>
    public sealed class AttackDefendTickHandler : IObjectiveModeHandler
    {
        private readonly BattlegroundDials _dials;
        private AttackDefendPlan _plan;

        private readonly HashSet<int> _destroyed = new();
        private readonly Dictionary<int, int> _alertedSteps = new();

        private DateTime? _lastAlertUtc;
        private DateTime? _nextStatusUtc;

        /// <param name="plan">
        /// The match's crystals. Null (the mode definition builds the handler before the match's layout and attacker count are known)
        /// means no crystals until <see cref="BindPlan"/> supplies them.
        /// </param>
        public AttackDefendTickHandler(BattlegroundDials dials, AttackDefendPlan plan = null)
        {
            _dials = dials;
            _plan = plan;
        }

        /// <summary>Binds the match's plan, once, when the match forms. Ignored once a crystal has fallen, or for a null plan.</summary>
        public void BindPlan(AttackDefendPlan plan)
        {
            if (plan == null || _destroyed.Count > 0)
                return;

            _plan = plan;
        }

        /// <summary>The plan this handler announces against, or null before one is bound.</summary>
        public AttackDefendPlan Plan => _plan;

        /// <summary>The next time a status line is due. Null until the match goes live.</summary>
        public DateTime? NextStatusUtc => _nextStatusUtc;

        // ---------------- IObjectiveModeHandler ----------------

        public string TeamName(int team) => BattlegroundText.AttackDefendTeamName(team);

        /// <summary>
        /// The attackers' win (every crystal destroyed) or the defenders' timeout win, which names how many crystals they held once any
        /// fell; null for any other outcome.
        /// </summary>
        public string ResultLine(MatchOutcome outcome, IReadOnlyDictionary<int, int> scores)
        {
            if (outcome == null || outcome.WinningTeams.Count != 1)
                return null;

            var winner = outcome.WinningTeams.First();

            if (outcome.Reason == EndReason.Score && winner == CrystalWinCondition.AttackerTeam)
                return BattlegroundText.AttackersWin();

            if (outcome.Reason == EndReason.Timeout && winner == CrystalWinCondition.DefenderTeam)
            {
                var destroyed = scores != null && scores.TryGetValue(CrystalWinCondition.AttackerTeam, out var d) ? d : 0;

                return BattlegroundText.DefendersTimeoutWin(destroyed, _plan?.Count ?? 0);
            }

            return null;
        }

        /// <summary>" Crystals destroyed: X of N." (the status line already names the time left), or empty without a crystal count.</summary>
        public string StatusLine(IReadOnlyDictionary<int, int> scores, int scoreTarget)
        {
            if (scores == null || scoreTarget <= 0)
                return "";

            var line = BattlegroundText.StatusCrystals(scores.TryGetValue(CrystalWinCondition.AttackerTeam, out var destroyed) ? destroyed : 0, scoreTarget);

            // Sequential mode names the one crystal that takes damage; nothing is added once none is left or in any-order play.
            return _plan?.Sequence == null ? line : line + BattlegroundText.StatusVulnerable(_plan.Sequence.CurrentName);
        }

        /// <summary>Binds the plan (<see cref="BindPlan"/>); the crystals carry everything this mode needs from the layout.</summary>
        public void BindMatch(BattlegroundLayout layout, AttackDefendPlan plan) => BindPlan(plan);

        /// <summary>Attack/Defend places no marker ring.</summary>
        public KothZone? MarkerZone => null;

        /// <summary>A crystal fell (<see cref="OnCrystalDestroyed"/>).</summary>
        public bool OnObjectiveDestroyed(IObjectiveMatchContext ctx, int index) => OnCrystalDestroyed(ctx, index);

        /// <summary>A planned crystal whose destruction has not been counted. False before a plan is bound and for an index outside it.</summary>
        public bool IsObjectiveStanding(int index) => _plan != null && index >= 0 && index < _plan.Count && !_destroyed.Contains(index);

        /// <summary>
        /// Sequential mode: "[Battleground] Vulnerable crystal: West Cavern (hint)." for the crystal that takes damage now, sent to a player
        /// whose respawn is confirmed so they know where the push stands. Null in any-order play and once no crystal is left.
        /// </summary>
        public string VulnerableLine()
        {
            var seq = _plan?.Sequence;

            if (seq == null || seq.Current < 0)
                return null;

            var crystal = _plan.Crystals[seq.Current];

            return BattlegroundText.VulnerableCrystal(crystal.Site.Name, crystal.Where);
        }

        /// <summary>The attacker's or defender's role line; null for any other team or before a plan is bound.</summary>
        public string RoleLine(int team)
        {
            if (_plan == null)
                return null;

            // Sequential mode: both sides are told the order, as a numbered list.
            if (_plan.Sequence != null)
            {
                var sites = _plan.Crystals.Select(c => c.Site).ToList();

                return team switch
                {
                    CrystalWinCondition.AttackerTeam => BattlegroundText.RoleAttackerOrdered(sites),
                    CrystalWinCondition.DefenderTeam => BattlegroundText.RoleDefenderOrdered(sites),
                    _ => null
                };
            }

            return team switch
            {
                CrystalWinCondition.AttackerTeam => BattlegroundText.RoleAttacker(_plan.Count, BattlegroundText.CrystalList(_plan.Crystals.Select(c => c.Site))),
                CrystalWinCondition.DefenderTeam => BattlegroundText.RoleDefender(_plan.Count, BattlegroundText.CrystalList(_plan.Crystals.Select(c => c.Site))),
                _ => null
            };
        }

        // ---------------- IMatchTickHandler ----------------

        public void OnLive(IMatchContext m)
        {
            if (m.LiveSinceUtc != null)
                StartClocks(m.LiveSinceUtc.Value);
        }

        public void OnTick(IMatchContext m, DateTime utcNow)
        {
            if (m is not IObjectiveMatchContext ctx || _plan == null)
                return;

            // OnLive had no live time to read (or never ran): start the clocks from this tick, never from a clock read.
            if (_nextStatusUtc == null)
                StartClocks(utcNow);

            AlertOnDamage(ctx, utcNow);
            AnnounceStatus(ctx, utcNow);
        }

        // ---------------- destruction ----------------

        /// <summary>
        /// A crystal fell. Counts it once per index (ScoreBoard[0] + 1) and announces the fall to everyone; returns true only for the first
        /// report of a valid index. A duplicate, a negative index and an index past the plan return false and change nothing.
        /// </summary>
        public bool OnCrystalDestroyed(IObjectiveMatchContext ctx, int crystalIndex)
        {
            if (ctx == null || _plan == null || crystalIndex < 0 || crystalIndex >= _plan.Count || !_destroyed.Add(crystalIndex))
                return false;

            ctx.ScoreBoard.TryGetValue(CrystalWinCondition.AttackerTeam, out var current);
            var destroyed = current + 1;
            ctx.ScoreBoard[CrystalWinCondition.AttackerTeam] = destroyed;

            var crystal = _plan.Crystals[crystalIndex];

            // Sequential mode: the fall moves the vulnerable crystal on before anything is announced, so the next crystal already takes
            // damage when its unlock line is read. A crystal that was not the vulnerable one moves nothing.
            var sequence = _plan.Sequence;
            var unlocked = sequence != null && sequence.MarkDestroyed(crystalIndex);

            ctx.Announce(BattlegroundText.CrystalDestroyed(crystal.Site.Name, crystal.Where, Math.Max(0, _plan.Count - destroyed), _plan.Count));

            // The fall unlocks the next crystal; nothing to unlock after the last.
            if (unlocked && sequence.Current >= 0)
            {
                var next = _plan.Crystals[sequence.Current];

                ctx.Announce(BattlegroundText.CrystalUnlocked(next.Site.Name, next.Where));
            }

            return true;
        }

        // ---------------- alerts ----------------

        private void AlertOnDamage(IObjectiveMatchContext ctx, DateTime utcNow)
        {
            var step = Math.Max(1, _dials.AdAlertStepPercent);
            var interval = TimeSpan.FromSeconds(Math.Max(1, _dials.AdAlertMinIntervalSeconds));

            foreach (var sample in ctx.SampleCrystals().OrderBy(s => s.Index))
            {
                if (sample.Index < 0 || sample.Index >= _plan.Count || _destroyed.Contains(sample.Index) || sample.Max <= 0 || sample.Current <= 0)
                    continue;

                // A sealed crystal takes no damage, so it sends no alert (sequential mode; any-order play has no sequence).
                if (_plan.Sequence != null && !_plan.Sequence.IsVulnerable(sample.Index))
                    continue;

                var lost = Math.Max(0, sample.Max - sample.Current);

                // Whole steps lost: floor(lost% / step), in integers.
                var steps = (int)((long)lost * 100 / ((long)sample.Max * step));

                _alertedSteps.TryGetValue(sample.Index, out var alerted);

                // A heal (Docs/Pvp/ATTACK-DEFEND.md "Kill chip and heal") can lift the crystal back above a step already announced: lower
                // the latch to match, before the throttle, so losing that step again alerts again.
                if (steps < alerted)
                    _alertedSteps[sample.Index] = steps;

                if (steps <= alerted)
                    continue;

                if (_lastAlertUtc != null && utcNow - _lastAlertUtc.Value < interval)
                    continue;

                var crystal = _plan.Crystals[sample.Index];
                var percent = (int)(((long)sample.Current * 100 + sample.Max - 1) / sample.Max);

                ctx.AnnounceToTeam(CrystalWinCondition.DefenderTeam, BattlegroundText.CrystalUnderAttack(crystal.Site.Name, crystal.Where, percent));

                _alertedSteps[sample.Index] = steps;
                _lastAlertUtc = utcNow;
            }
        }

        // ---------------- status ----------------

        private void StartClocks(DateTime from)
        {
            _nextStatusUtc = from + TimeSpan.FromSeconds(Math.Max(1, _dials.AdStatusAnnounceSeconds));
        }

        private void AnnounceStatus(IObjectiveMatchContext ctx, DateTime utcNow)
        {
            if (_nextStatusUtc == null || utcNow < _nextStatusUtc.Value)
                return;

            var left = ctx.LiveSinceUtc == null ? _dials.TimeLimitSecondsAd : _dials.TimeLimitSecondsAd - (int)(utcNow - ctx.LiveSinceUtc.Value).TotalSeconds;

            ctx.ScoreBoard.TryGetValue(CrystalWinCondition.AttackerTeam, out var destroyed);
            ctx.Announce(BattlegroundText.CrystalStatus(destroyed, _plan.Count, left, _plan.Crystals.Where(c => !_destroyed.Contains(c.Index)).Select(c => c.Site.Name), _plan.Sequence?.CurrentName));

            var every = TimeSpan.FromSeconds(Math.Max(1, _dials.AdStatusAnnounceSeconds));
            var next = _nextStatusUtc.Value + every;
            _nextStatusUtc = next <= utcNow ? utcNow + every : next;
        }
    }
}
