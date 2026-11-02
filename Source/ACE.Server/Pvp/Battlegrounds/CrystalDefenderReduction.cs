using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

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
    /// Why one <see cref="DefenderSample"/> does or does not count toward a crystal's engaged-defender reduction, in the order the rule
    /// checks: the first reason that applies wins. Only <see cref="Engaged"/> counts.
    /// </summary>
    public enum DefenderStanding
    {
        /// <summary>Counts: every condition holds.</summary>
        Engaged,

        /// <summary>The radius or the window is off (0, NaN, infinite or negative), so nobody counts.</summary>
        RuleOff,

        /// <summary>Not a defender of this crystal: unbound, bound to another match, or on a team other than the defending one.</summary>
        WrongTeam,

        /// <summary>Dead, in the death process, or respawning in the pen.</summary>
        NotAlive,

        /// <summary>No position, another instance, or farther from the crystal than the radius.</summary>
        OutOfRadius,

        /// <summary>No engagement stamp, or one from another match: has not dealt or taken landed damage against an enemy of this match.</summary>
        NoStamp,

        /// <summary>The engagement stamp is older than the window (or in the future, which is never trusted).</summary>
        StaleStamp
    }

    /// <summary>
    /// The per-hit breakdown of every candidate the crystal saw (Docs/Pvp/ATTACK-DEFEND.md "Engaged defenders"), for the diagnostics line:
    /// how many counted and, for each one that did not, the first reason it failed. <see cref="NearestMetres"/> is the distance to the
    /// nearest living defender of the crystal in its instance and <see cref="FreshestStampSeconds"/> the age of the most recent in-match
    /// engagement stamp among the living defenders in the crystal's instance (raw: a stamp in the future gives a negative age, which the
    /// printed line shows as 0); both are NaN when there is none.
    /// </summary>
    public readonly record struct DefenderTally(int Engaged, int RuleOff, int WrongTeam, int NotAlive, int OutOfRadius, int NoStamp, int StaleStamp, double NearestMetres, double FreshestStampSeconds)
    {
        /// <summary>Every candidate counted, engaged or not.</summary>
        public int Candidates => Engaged + RuleOff + WrongTeam + NotAlive + OutOfRadius + NoStamp + StaleStamp;
    }

    /// <summary>
    /// The whole decision for one hit on a crystal (<see cref="CrystalDefenderReduction.Resolve"/>): whether the hit qualified for the rule at
    /// all, the amount that lands, the multiplier applied and the tally behind it. An unqualified hit carries its amount, a multiplier of 1 and
    /// an empty tally.
    /// </summary>
    public readonly record struct CrystalHitResolution(bool Qualified, float Amount, double Multiplier, DefenderTally Tally);

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
            => Classify(tag, crystalAt, dials, defender, nowUtc, out _, out _) == DefenderStanding.Engaged;

        /// <summary>
        /// Why <paramref name="defender"/> does or does not count toward the crystal <paramref name="tag"/> (<see cref="IsEngagedDefender"/> is
        /// exactly "this returns <see cref="DefenderStanding.Engaged"/>"). <paramref name="metres"/> is the distance to the crystal and
        /// <paramref name="ageSeconds"/> the age of an in-match stamp, each NaN when the check that measures it was not reached. Pure.
        /// </summary>
        public static DefenderStanding Classify(BattlegroundObjectiveTag tag, Position crystalAt, CrystalDefenderReductionDials dials, DefenderSample defender, DateTime nowUtc, out double metres, out double ageSeconds)
        {
            metres = double.NaN;
            ageSeconds = double.NaN;

            if (tag == null || crystalAt == null || dials == null)
                return DefenderStanding.RuleOff;

            var radius = SanitizeExtent(dials.Radius);
            var window = SanitizeExtent(dials.WindowSeconds);

            if (radius <= 0.0 || window <= 0.0)
                return DefenderStanding.RuleOff;

            var binding = defender.Binding;

            if (binding?.Match == null || binding.Match.MatchId != tag.MatchId || binding.TeamIndex != tag.DefendingTeam)
                return DefenderStanding.WrongTeam;

            if (binding.Respawning || !defender.Alive)
                return DefenderStanding.NotAlive;

            if (defender.Location == null || defender.Location.Instance != crystalAt.Instance)
                return DefenderStanding.OutOfRadius;

            metres = crystalAt.DistanceTo(defender.Location);

            var engagement = defender.Engagement;
            var stamped = engagement != null && engagement.MatchId == tag.MatchId;

            if (stamped)
                ageSeconds = (nowUtc - engagement.AtUtc).TotalSeconds;

            if (metres > radius)
                return DefenderStanding.OutOfRadius;

            if (!stamped)
                return DefenderStanding.NoStamp;

            return ageSeconds >= 0.0 && ageSeconds <= window ? DefenderStanding.Engaged : DefenderStanding.StaleStamp;
        }

        /// <summary>
        /// Every candidate in <paramref name="defenders"/> classified (<see cref="Classify"/>) and counted by standing, with the nearest living
        /// defender's distance and the freshest in-match stamp age among the crystal's defenders. Its <see cref="DefenderTally.Engaged"/> always
        /// equals <see cref="CountEngaged"/> for the same inputs. Pure.
        /// </summary>
        public static DefenderTally Tally(BattlegroundObjectiveTag tag, Position crystalAt, CrystalDefenderReductionDials dials, IEnumerable<DefenderSample> defenders, DateTime nowUtc)
        {
            int engaged = 0, ruleOff = 0, wrongTeam = 0, notAlive = 0, outOfRadius = 0, noStamp = 0, staleStamp = 0;
            var nearest = double.NaN;
            var freshest = double.NaN;

            if (defenders != null)
            {
                foreach (var d in defenders)
                {
                    var standing = Classify(tag, crystalAt, dials, d, nowUtc, out var metres, out var age);

                    switch (standing)
                    {
                        case DefenderStanding.Engaged: engaged++; break;
                        case DefenderStanding.RuleOff: ruleOff++; break;
                        case DefenderStanding.WrongTeam: wrongTeam++; break;
                        case DefenderStanding.NotAlive: notAlive++; break;
                        case DefenderStanding.OutOfRadius: outOfRadius++; break;
                        case DefenderStanding.NoStamp: noStamp++; break;
                        default: staleStamp++; break;
                    }

                    if (!double.IsNaN(metres) && (double.IsNaN(nearest) || metres < nearest))
                        nearest = metres;

                    if (!double.IsNaN(age) && (double.IsNaN(freshest) || age < freshest))
                        freshest = age;
                }
            }

            return new DefenderTally(engaged, ruleOff, wrongTeam, notAlive, outOfRadius, noStamp, staleStamp, nearest, freshest);
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
            => Resolve(tag, attacker, crystalAt, defenders, nowUtc, amount).Amount;

        /// <summary>
        /// <see cref="Apply"/> with its working shown: the same amount, plus whether the hit qualified, the multiplier and the
        /// <see cref="Tally"/> of the candidates. <paramref name="defenders"/> is enumerated once, and only when the hit qualifies. Pure.
        /// </summary>
        public static CrystalHitResolution Resolve(BattlegroundObjectiveTag tag, PvpPlayerBinding attacker, Position crystalAt, IEnumerable<DefenderSample> defenders, DateTime nowUtc, float amount)
        {
            if (!(amount > 0f) || !IsAttackerHit(tag, attacker))
                return new CrystalHitResolution(false, amount, 1.0, default);

            var dials = attacker.Match.DefenderReduction;

            if (dials == null)
                return new CrystalHitResolution(false, amount, 1.0, default);

            var tally = Tally(tag, crystalAt, dials, defenders, nowUtc);
            var multiplier = Multiplier(tally.Engaged, dials);

            return new CrystalHitResolution(true, multiplier >= 1.0 ? amount : (float)(amount * multiplier), multiplier, tally);
        }

        /// <summary>
        /// The four dials as the formation log and the per-hit diagnostics print them: each raw value as set, and for a fraction the value the
        /// rule actually uses when sanitizing changes it (a per-defender 2 counts as 1, a cap of 4 as 0.95). "off" for no dials. Pure.
        /// </summary>
        public static string DescribeDials(CrystalDefenderReductionDials dials)
        {
            if (dials == null)
                return "off";

            static string Raw(double v) => v.ToString("0.###", CultureInfo.InvariantCulture);

            static string Fraction(double raw, double max)
            {
                var used = SanitizeFraction(raw, max);
                return used == raw ? Raw(raw) : $"{Raw(raw)} (counts as {Raw(used)})";
            }

            static string Extent(double raw, string unit)
            {
                var used = SanitizeExtent(raw);
                return used <= 0.0 ? $"{Raw(raw)} (off)" : $"{Raw(raw)} {unit}";
            }

            return $"per {Fraction(dials.PerDefender, MaxPerDefender)}, cap {Fraction(dials.Cap, MaxCap)}, radius {Extent(dials.Radius, "m")}, window {Extent(dials.WindowSeconds, "s")}";
        }

        /// <summary>
        /// One crystal-hit diagnostics line (Docs/Pvp/ATTACK-DEFEND.md "Engaged defenders"): what the hit rolled and what landed, the engaged
        /// count and multiplier, the dials, and when nobody counted the reason tally with the nearest defender's distance and the freshest
        /// stamp's age. <paramref name="unlogged"/> is how many hits on this crystal the throttle skipped since the previous line. Pure.
        /// </summary>
        public static string FormatHitLine(BattlegroundObjectiveTag tag, string crystal, string attackerName, CrystalDefenderReductionDials dials, float rolled, CrystalHitResolution resolution, int unlogged)
        {
            var t = resolution.Tally;
            var text = new StringBuilder();

            text.Append($"[PVP] crystal {tag?.Index} of match {tag?.MatchId} ({crystal}): {attackerName} hit {rolled.ToString("0.#", CultureInfo.InvariantCulture)} -> {resolution.Amount.ToString("0.#", CultureInfo.InvariantCulture)}");
            text.Append($" (x{resolution.Multiplier.ToString("0.###", CultureInfo.InvariantCulture)}); engaged defenders {t.Engaged} of {t.Candidates} candidate(s); dials {DescribeDials(dials)}");

            if (t.Engaged == 0)
            {
                text.Append($"; not counted: wrong team {t.WrongTeam}, not alive {t.NotAlive}, out of radius {t.OutOfRadius}, no stamp {t.NoStamp}, stale stamp {t.StaleStamp}");

                if (t.RuleOff > 0)
                    text.Append($", rule off {t.RuleOff}");

                text.Append($"; nearest defender {(double.IsNaN(t.NearestMetres) ? "none" : t.NearestMetres.ToString("0.0", CultureInfo.InvariantCulture) + " m")}");
                text.Append($", freshest stamp {(double.IsNaN(t.FreshestStampSeconds) ? "none" : Math.Max(0.0, t.FreshestStampSeconds).ToString("0.0", CultureInfo.InvariantCulture) + " s ago")}");
            }

            if (unlogged > 0)
                text.Append($" (+{unlogged} earlier hit(s) on this crystal not logged)");

            return text.ToString();
        }
    }
}
