using System;
using System.Collections.Generic;

using ACE.Entity;

namespace ACE.Server.Pvp.Battlegrounds
{
    /// <summary>
    /// The four engaged-defender settings as one match uses them (Docs/Pvp/ATTACK-DEFEND.md "Engaged defenders"), snapshotted when the
    /// match forms like every other Attack/Defend dial and carried on <see cref="PvpMatch.DefenderReduction"/>. Raw values: the rule
    /// sanitizes them where it reads them.
    /// </summary>
    /// <param name="PerDefender">pvp_bg_ad_defender_dr_per - the fraction of an attacker's hit each engaged defender takes off.</param>
    /// <param name="Cap">pvp_bg_ad_defender_dr_cap - the largest total fraction, however many defenders are engaged.</param>
    /// <param name="Radius">pvp_bg_ad_defender_dr_radius - metres (3D, landblock frame) from the crystal a defender must stand within.</param>
    /// <param name="WindowSeconds">pvp_bg_ad_defender_dr_window_s - how recently the defender must have dealt or taken player damage.</param>
    public sealed record CrystalDefenderReductionDials(double PerDefender, double Cap, double Radius, double WindowSeconds)
    {
        /// <summary>The four values out of a formation dials snapshot; null for null.</summary>
        public static CrystalDefenderReductionDials From(BattlegroundDials dials)
            => dials == null ? null : new CrystalDefenderReductionDials(dials.AdDefenderDrPer, dials.AdDefenderDrCap, dials.AdDefenderDrRadius, dials.AdDefenderDrWindowSeconds);
    }

    /// <summary>
    /// When a player last dealt or took damage against an OPPOSING player of the same Live match (Docs/Pvp/ATTACK-DEFEND.md "Engaged
    /// defenders"). Immutable; the player swaps the whole reference. The match id scopes it, so a stamp from an earlier match never counts.
    /// </summary>
    public sealed record PvpEngagementStamp(Guid MatchId, DateTime AtUtc);

    /// <summary>
    /// One possible defender as the rule sees it, read on the crystal's landblock thread: the player's binding, whether they are alive, where
    /// they stand and their last engagement stamp.
    /// </summary>
    public readonly record struct DefenderSample(PvpPlayerBinding Binding, bool Alive, Position Location, PvpEngagementStamp Engagement);

    /// <summary>
    /// Attack/Defend engaged-defender damage reduction (Docs/Pvp/ATTACK-DEFEND.md "Engaged defenders"), owner playtest note 2026-10-08:
    /// an attacker's hit on a crystal is multiplied by <c>1 - min(cap, perDefender x N)</c>, where N counts the defenders who are alive, on
    /// the crystal's defending team in its own match, within the radius of THAT crystal, and who dealt or took damage against an opposing
    /// player of the match within the window. Pure: the callers (Creature_BattlegroundObjective on the crystal's landblock thread, and the
    /// damage-history hook in Player_BattlegroundEngagement) supply every input, the clock included.
    /// </summary>
    public static class CrystalDefenderReduction
    {
        /// <summary>
        /// The largest total reduction any setting can produce: an attacker's hit always keeps at least 5%, so no number of engaged
        /// defenders makes a crystal immune.
        /// </summary>
        public const double MaxCap = 0.95;

        /// <summary>The largest per-defender fraction: one defender can never take off more than the whole hit (the cap still applies).</summary>
        public const double MaxPerDefender = 1.0;

        /// <summary>A fraction setting as the rule uses it: 0 for NaN, infinite or not positive, at most <paramref name="max"/>. Pure.</summary>
        public static double SanitizeFraction(double raw, double max)
            => double.IsNaN(raw) || double.IsInfinity(raw) || raw <= 0.0 ? 0.0 : Math.Min(raw, max);

        /// <summary>A distance or time setting as the rule uses it: 0 (off) for NaN, infinite or not positive. Pure.</summary>
        public static double SanitizeExtent(double raw)
            => double.IsNaN(raw) || double.IsInfinity(raw) || raw <= 0.0 ? 0.0 : raw;

        /// <summary>
        /// The multiplier for <paramref name="engaged"/> engaged defenders: <c>1 - min(cap, perDefender x engaged)</c>, with both fractions
        /// sanitized (<see cref="SanitizeFraction"/>; the cap at most <see cref="MaxCap"/>). 1 for no dials, no engaged defender, or a fraction that is off. Pure.
        /// </summary>
        public static double Multiplier(int engaged, CrystalDefenderReductionDials dials)
        {
            if (dials == null || engaged <= 0)
                return 1.0;

            var per = SanitizeFraction(dials.PerDefender, MaxPerDefender);
            var cap = SanitizeFraction(dials.Cap, MaxCap);

            if (per <= 0.0 || cap <= 0.0)
                return 1.0;

            return 1.0 - Math.Min(cap, per * engaged);
        }

        /// <summary>
        /// True when <paramref name="attacker"/> is a player whose hit on the crystal <paramref name="tag"/> this rule may reduce: bound to the
        /// crystal's own match, that match Live, and on a team other than the defending one. Pure.
        /// </summary>
        public static bool IsAttackerHit(BattlegroundObjectiveTag tag, PvpPlayerBinding attacker)
            => tag != null
               && attacker?.Match != null
               && attacker.Match.MatchId == tag.MatchId
               && attacker.State == PvpMatchState.Live
               && attacker.TeamIndex != tag.DefendingTeam;

        /// <summary>
        /// True when damage between <paramref name="victim"/> and <paramref name="attacker"/> counts as engagement for both: two bindings to
        /// the SAME Live match on DIFFERENT teams. A teammate, an NPC, a crystal, another match or a match not yet Live never counts. Pure.
        /// </summary>
        public static bool CountsAsEngagement(PvpPlayerBinding victim, PvpPlayerBinding attacker)
            => victim?.Match != null
               && attacker?.Match != null
               && victim.Match.MatchId == attacker.Match.MatchId
               && victim.State == PvpMatchState.Live
               && attacker.State == PvpMatchState.Live
               && victim.TeamIndex != attacker.TeamIndex;

        /// <summary>
        /// True when <paramref name="defender"/> counts toward the crystal <paramref name="tag"/> standing at <paramref name="crystalAt"/>: bound
        /// to its match, on the defending team, not respawning, alive, in the crystal's instance, within the radius (3D, inclusive) and engaged
        /// in this match within the window (inclusive) before <paramref name="nowUtc"/>. A radius or window that is off counts nobody. Pure.
        /// </summary>
        public static bool IsEngagedDefender(BattlegroundObjectiveTag tag, Position crystalAt, CrystalDefenderReductionDials dials, DefenderSample defender, DateTime nowUtc)
        {
            if (tag == null || crystalAt == null || dials == null)
                return false;

            var radius = SanitizeExtent(dials.Radius);
            var window = SanitizeExtent(dials.WindowSeconds);

            if (radius <= 0.0 || window <= 0.0)
                return false;

            var binding = defender.Binding;

            if (binding?.Match == null || binding.Match.MatchId != tag.MatchId || binding.TeamIndex != tag.DefendingTeam || binding.Respawning)
                return false;

            if (!defender.Alive || defender.Location == null || defender.Location.Instance != crystalAt.Instance)
                return false;

            if (crystalAt.DistanceTo(defender.Location) > radius)
                return false;

            var engagement = defender.Engagement;

            if (engagement == null || engagement.MatchId != tag.MatchId)
                return false;

            var age = (nowUtc - engagement.AtUtc).TotalSeconds;

            return age >= 0.0 && age <= window;
        }

        /// <summary>How many of <paramref name="defenders"/> are engaged defenders of the crystal (<see cref="IsEngagedDefender"/>). Pure.</summary>
        public static int CountEngaged(BattlegroundObjectiveTag tag, Position crystalAt, CrystalDefenderReductionDials dials, IEnumerable<DefenderSample> defenders, DateTime nowUtc)
        {
            if (defenders == null)
                return 0;

            var count = 0;

            foreach (var d in defenders)
            {
                if (IsEngagedDefender(tag, crystalAt, dials, d, nowUtc))
                    count++;
            }

            return count;
        }

        /// <summary>
        /// The whole decision for one hit: <paramref name="amount"/> unchanged unless it is a positive attacker hit
        /// (<see cref="IsAttackerHit"/>) on a tagged crystal of a match carrying dials, then multiplied by <see cref="Multiplier"/> for the
        /// engaged count. <paramref name="defenders"/> is enumerated only when the hit qualifies. Pure.
        /// </summary>
        public static float Apply(BattlegroundObjectiveTag tag, PvpPlayerBinding attacker, Position crystalAt, IEnumerable<DefenderSample> defenders, DateTime nowUtc, float amount)
        {
            if (!(amount > 0f) || !IsAttackerHit(tag, attacker))
                return amount;

            var dials = attacker.Match.DefenderReduction;

            if (dials == null)
                return amount;

            var multiplier = Multiplier(CountEngaged(tag, crystalAt, dials, defenders, nowUtc), dials);

            return multiplier >= 1.0 ? amount : (float)(amount * multiplier);
        }
    }
}
