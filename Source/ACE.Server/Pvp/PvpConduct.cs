using System;
using System.Collections.Generic;

using ACE.Server.Pvp.Battlegrounds;

namespace ACE.Server.Pvp
{
    /// <summary>
    /// When a player last did something that counts as PvP activity in a match (Docs/Pvp/BATTLEGROUNDS.md "Conduct signals"): dealt or took
    /// enemy damage, hit a crystal, attempted a hostile action on an enemy (misses included), healed a teammate, or cast a
    /// beneficial enchantment on a teammate. Movement never stamps it. Immutable; the player swaps the whole reference. The match id scopes
    /// it, so a stamp from an earlier match never counts in the next.
    /// </summary>
    public sealed record PvpActivityStamp(Guid MatchId, DateTime AtUtc);

    /// <summary>What a heal earns its healer (Docs/Pvp/BATTLEGROUNDS.md "Conduct signals").</summary>
    public enum PvpHealCredit
    {
        /// <summary>Nothing: no health restored, a self-heal, not a Live match participant, an enemy target, or another match.</summary>
        None,

        /// <summary>An activity stamp and HealingDone of the health restored: a heal of a TEAMMATE in the same Live match.</summary>
        ActivityAndHealing
    }

    /// <summary>
    /// The pure "does this count" rules behind the player-side activity stamp and the HealingDone credit (Docs/Pvp/BATTLEGROUNDS.md
    /// "Conduct signals"). Every input is a binding; the callers (Player_BattlegroundEngagement) do the stamping and the ledger write.
    /// </summary>
    public static class PvpConductRules
    {
        /// <summary>Bound to a Live match and not dead or waiting in a battleground pen (the binding's Respawning flag).</summary>
        public static bool IsLiveParticipant(PvpPlayerBinding binding) => PvpPlayerRules.InLiveMatch(binding) && !binding.Respawning;

        /// <summary>
        /// True when an action by <paramref name="actor"/> on <paramref name="target"/> is HOSTILE activity: two bindings to the same Live match
        /// on different teams (the engagement rule, <see cref="CrystalDefenderReduction.CountsAsEngagement"/>). A teammate never counts, so a
        /// friendly-fire swing is not activity.
        /// </summary>
        public static bool IsHostileAttempt(PvpPlayerBinding actor, PvpPlayerBinding target) => CrystalDefenderReduction.CountsAsEngagement(target, actor);

        /// <summary>
        /// True when <paramref name="source"/> supporting <paramref name="target"/> (a heal or a beneficial enchantment) is TEAMMATE support: both
        /// Live participants (<see cref="IsLiveParticipant"/>) of the same match instance, on the same team. Self is the caller's to exclude.
        /// </summary>
        public static bool IsTeammateSupport(PvpPlayerBinding source, PvpPlayerBinding target)
            => IsLiveParticipant(source)
               && IsLiveParticipant(target)
               && ReferenceEquals(source.Match, target.Match)
               && source.TeamIndex == target.TeamIndex;

        /// <summary>
        /// The credit for a heal that restored <paramref name="restored"/> points of health (the vital write's own result, so overheal is
        /// never in it). 0 earns nothing, and so does a self-heal (<paramref name="self"/>), like a self-buff; a heal of a
        /// teammate (<see cref="IsTeammateSupport"/>) earns activity and HealingDone; anything else (an enemy, another match, no match) nothing.
        /// </summary>
        public static PvpHealCredit DecideHeal(PvpPlayerBinding healer, PvpPlayerBinding healed, bool self, uint restored)
        {
            if (restored == 0)
                return PvpHealCredit.None;

            if (self)
                return PvpHealCredit.None;

            return IsTeammateSupport(healer, healed) ? PvpHealCredit.ActivityAndHealing : PvpHealCredit.None;
        }
    }

    /// <summary>One seat as the conduct sampler sees it on one sample (Docs/Pvp/BATTLEGROUNDS.md "Conduct signals").</summary>
    /// <param name="InPenCycle">The seat is DyingToPen, InPen or Respawning: its idle clock is frozen.</param>
    /// <param name="Sample">The player's position sample (with their activity stamp), or null when offline or not sampled.</param>
    /// <param name="OnObjective">The coordinator found the seat on the objective (the hill widened by its near margin, or near the vulnerable crystal).</param>
    /// <param name="LastActiveUtc">The seat's last-active time before this sample.</param>
    public readonly record struct ConductSeatInput(uint CharacterId, int TeamIndex, bool InPenCycle, BattlegroundZoneSample Sample, bool OnObjective, DateTime LastActiveUtc);

    /// <summary>
    /// One seat after a sample: its new last-active time, whether the sampled seconds count as engaged, and whether the seat was
    /// STANDING (<see cref="PvpConductSampler.IsStanding"/>: sampled, in the instance, alive, not teleporting, not in the pen cycle),
    /// the only state in which idle enforcement may judge it.
    /// </summary>
    public readonly record struct ConductSeatOutcome(uint CharacterId, DateTime LastActiveUtc, bool Engaged, bool Standing);

    /// <summary>
    /// The conduct sampler (Docs/Pvp/BATTLEGROUNDS.md "Conduct signals"), run by the coordinator about once a second for every Live
    /// battleground and Tugak Brawl match. Pure: the coordinator supplies the clock, the samples and the objective test.
    ///
    /// <para/>
    /// Last active. A seat's clock starts when the match goes Live. Each sample moves it to the latest of its current value, the player's
    /// activity stamp when the stamp belongs to this match, and now when the seat stands on the objective. It never runs ahead of now.
    /// Moving around changes nothing: only the stamp and the objective do. While the seat is frozen (in the pen cycle, dead, or teleporting)
    /// the clock is pushed forward by the sampled seconds, so idle time does not grow; a confirmed respawn resets it to now (the coordinator).
    /// Otherwise one sample adds at most <see cref="MaxIdleSecondsPerSample"/> of idle time, so a stalled tick is not idleness.
    ///
    /// <para/>
    /// Engaged. A STANDING seat (sampled, in the instance, alive, not teleporting, not in the pen cycle) is engaged for the sampled seconds
    /// when it is on the objective or a standing ENEMY sample lies within the engage radius (3D, inclusive, landblock frame).
    /// </summary>
    public static class PvpConductSampler
    {
        /// <summary>How often (at most) the coordinator samples a match.</summary>
        public const double SampleIntervalSeconds = 1.0;

        /// <summary>The most seconds one sample credits after a stalled tick, so a long hitch cannot hand out a burst of engaged time.</summary>
        public const int MaxCreditSeconds = 5;

        /// <summary>
        /// The most idle seconds one sample adds, by the same rule as <see cref="MaxCreditSeconds"/>: a coordinator stall or a clock jump
        /// never warns or removes a seat (Docs/Pvp/BATTLEGROUNDS.md "Conduct").
        /// </summary>
        public const int MaxIdleSecondsPerSample = MaxCreditSeconds;

        /// <summary>
        /// The whole seconds due since <paramref name="anchorUtc"/>, and the anchor moved forward by exactly those seconds so the remainder
        /// carries into the next sample. A null anchor starts the clock at <paramref name="nowUtc"/> with nothing due. A clock that ran
        /// backwards re-anchors at now with nothing due. Pure.
        /// </summary>
        public static int DueSeconds(DateTime? anchorUtc, DateTime nowUtc, out DateTime nextAnchorUtc)
        {
            if (!anchorUtc.HasValue || nowUtc < anchorUtc.Value)
            {
                nextAnchorUtc = nowUtc;
                return 0;
            }

            var whole = (long)Math.Floor((nowUtc - anchorUtc.Value).TotalSeconds);

            if (whole < 1)
            {
                nextAnchorUtc = anchorUtc.Value;
                return 0;
            }

            nextAnchorUtc = anchorUtc.Value.AddSeconds(whole);

            return (int)Math.Min(whole, int.MaxValue);
        }

        /// <summary>Sampled, in the match instance, alive, not teleporting and not in the pen cycle. Pure.</summary>
        public static bool IsStanding(in ConductSeatInput seat)
            => !seat.InPenCycle && seat.Sample != null && seat.Sample.InInstance && !seat.Sample.IsDead && !seat.Sample.IsTeleporting;

        /// <summary>In the pen cycle, dead or teleporting: the idle clock does not run. Pure.</summary>
        public static bool IsFrozen(in ConductSeatInput seat)
            => seat.InPenCycle || (seat.Sample != null && (seat.Sample.IsDead || seat.Sample.IsTeleporting));

        /// <summary>
        /// The seat's next last-active time (see the class remarks): frozen pushes it forward by <paramref name="seconds"/>, otherwise by any
        /// seconds beyond <see cref="MaxIdleSecondsPerSample"/>; a stamp of THIS match
        /// and standing on the objective raise it, and it never passes <paramref name="nowUtc"/>. Pure.
        /// </summary>
        public static DateTime NextLastActive(in ConductSeatInput seat, Guid matchId, DateTime nowUtc, int seconds)
        {
            var next = seat.LastActiveUtc;

            if (IsFrozen(seat))
            {
                if (seconds > 0)
                    next = next.AddSeconds(seconds);
            }
            else if (seconds > MaxIdleSecondsPerSample)
            {
                // A stalled tick (or a clock jump) adds at most MaxIdleSecondsPerSample of idle time: the excess is pushed forward like a
                // freeze, so nobody is warned or removed without real elapsed samples.
                next = next.AddSeconds(seconds - MaxIdleSecondsPerSample);
            }

            var s = seat.Sample;

            if (s != null && s.LastActivityUtc.HasValue && s.ActivityMatchId == matchId && s.LastActivityUtc.Value > next)
                next = s.LastActivityUtc.Value;

            if (seat.OnObjective && IsStanding(seat) && nowUtc > next)
                next = nowUtc;

            return next > nowUtc ? nowUtc : next;
        }

        /// <summary>
        /// One sample over every seat: each seat's next last-active time, and whether its <paramref name="seconds"/> count as engaged.
        /// <paramref name="engageRadius"/> NaN, infinite or not positive counts nobody by proximity (the objective still does). Pure.
        /// </summary>
        public static IReadOnlyList<ConductSeatOutcome> Step(Guid matchId, DateTime nowUtc, int seconds, double engageRadius, IReadOnlyList<ConductSeatInput> seats)
        {
            var result = new List<ConductSeatOutcome>(seats?.Count ?? 0);

            if (seats == null)
                return result;

            var radius = double.IsNaN(engageRadius) || double.IsInfinity(engageRadius) || engageRadius <= 0.0 ? 0.0 : engageRadius;

            for (var i = 0; i < seats.Count; i++)
            {
                var seat = seats[i];
                var engaged = IsStanding(seat) && (seat.OnObjective || (radius > 0.0 && HasEnemyWithin(seats, i, radius)));

                result.Add(new ConductSeatOutcome(seat.CharacterId, NextLastActive(seat, matchId, nowUtc, seconds), engaged, IsStanding(seat)));
            }

            return result;
        }

        /// <summary>True when a STANDING seat of another team is within <paramref name="radius"/> of seat <paramref name="index"/> (3D, inclusive). Pure.</summary>
        private static bool HasEnemyWithin(IReadOnlyList<ConductSeatInput> seats, int index, double radius)
        {
            var self = seats[index];
            var r2 = radius * radius;

            for (var j = 0; j < seats.Count; j++)
            {
                if (j == index)
                    continue;

                var other = seats[j];

                if (other.TeamIndex == self.TeamIndex || !IsStanding(other))
                    continue;

                var dx = other.Sample.X - self.Sample.X;
                var dy = other.Sample.Y - self.Sample.Y;
                var dz = other.Sample.Z - self.Sample.Z;

                if (dx * dx + dy * dy + dz * dz <= r2)
                    return true;
            }

            return false;
        }

        /// <summary>
        /// King of the Hill objective presence: the sample stands inside <paramref name="zone"/> widened by <paramref name="margin"/> metres
        /// horizontally (<see cref="BattlegroundPerformanceLedger.IsNearHill"/>, the near-hill killshot rule). Pure.
        /// </summary>
        public static bool OnKothObjective(KothZone zone, double margin, BattlegroundZoneSample sample)
            => sample != null && sample.InInstance && BattlegroundPerformanceLedger.IsNearHill(zone, margin, sample.X, sample.Y, sample.Z);
    }
}
