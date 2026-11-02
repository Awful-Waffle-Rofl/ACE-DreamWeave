using System;

using ACE.Entity;

namespace ACE.Server.Pvp.Battlegrounds
{
    /// <summary>
    /// What one player kill does to a crystal (Docs/Pvp/ATTACK-DEFEND.md "Kill chip and heal"): <see cref="Percent"/> of the crystal's
    /// MAXIMUM health, taken off (<see cref="IsChip"/>) or restored, on the crystal at plan index <see cref="CrystalIndex"/>.
    /// </summary>
    public readonly record struct CrystalKillEffect(int CrystalIndex, bool IsChip, double Percent);

    /// <summary>
    /// The Attack/Defend kill chip and heal decision (Docs/Pvp/ATTACK-DEFEND.md "Kill chip and heal"). An attacker killing a defender
    /// chips the vulnerable crystal by pvp_bg_ad_kill_chip_pct of its maximum health; a defender killing an attacker heals it by
    /// pvp_bg_ad_kill_heal_pct. Either counts only when the VICTIM died within pvp_bg_ad_kill_range metres of that crystal. Pure: the
    /// caller (the coordinator, on the world thread) has already established that the match is Live, the victim's death was handled
    /// once, and who the killer is; this decides only sides, crystal, range and amount. Applying it is the space seam's job, on the
    /// crystal's own landblock thread, where a chip goes through the crystal's normal damage path.
    /// </summary>
    public static class CrystalKillRules
    {
        /// <summary>The largest percent a single kill can move a crystal by; a larger setting counts as this.</summary>
        public const double MaxPercent = 100.0;

        /// <summary>
        /// The effect of a kill, or null when it does nothing. Null for: a missing plan or dials, a killer and victim on the same side,
        /// any team other than the attackers (<see cref="CrystalWinCondition.AttackerTeam"/>) and defenders
        /// (<see cref="CrystalWinCondition.DefenderTeam"/>), a percent that is 0, NaN, infinite or negative, a range that is 0, NaN,
        /// infinite or negative, no death position, a death outside the match instance, no vulnerable crystal, or a death farther than the range.
        ///
        /// <para/>
        /// Which crystal: in sequential play the vulnerable one (<see cref="CrystalSequence.Current"/>); in any-order play (the dial off, or
        /// one crystal) the standing crystal nearest the death, if it is within range. A crystal <paramref name="isStanding"/> reports
        /// fallen is skipped, so a kill near a fallen crystal and a standing one lands on the standing one; the seam that applies the effect
        /// also skips a fallen crystal, so none is ever revived.
        /// </summary>
        /// <param name="matchLandblock">The match space's landblock id (high 16 bits of every cell in it), which the plan's site cells sit in.</param>
        /// <param name="matchInstance">The match space's instance; a death in any other instance never counts.</param>
        /// <param name="isStanding">True for a crystal index not yet destroyed (the mode handler's IsObjectiveStanding); null treats every planned crystal as standing.</param>
        public static CrystalKillEffect? Decide(AttackDefendPlan plan, BattlegroundDials dials, uint matchLandblock, uint matchInstance, int killerTeam, int victimTeam, Position deathPosition, Func<int, bool> isStanding = null)
        {
            if (plan == null || dials == null || plan.Count == 0)
                return null;

            bool isChip;

            if (killerTeam == CrystalWinCondition.AttackerTeam && victimTeam == CrystalWinCondition.DefenderTeam)
                isChip = true;
            else if (killerTeam == CrystalWinCondition.DefenderTeam && victimTeam == CrystalWinCondition.AttackerTeam)
                isChip = false;
            else
                return null;

            var percent = SanitizePercent(isChip ? dials.AdKillChipPercent : dials.AdKillHealPercent);

            if (percent <= 0.0)
                return null;

            // Range, instance and which crystal: the one shared definition of "near the vulnerable crystal", which the performance
            // ledger also uses (Docs/Pvp/BATTLEGROUNDS.md "Performance stats").
            var index = CrystalProximity.VulnerableIndexNear(plan.Crystals, plan.Sequence, matchLandblock, matchInstance, dials.AdKillRange, deathPosition, isStanding);

            return index < 0 ? null : new CrystalKillEffect(index, isChip, percent);
        }

        /// <summary>A percent setting as the rule uses it: 0 for NaN, infinite or not positive, at most <see cref="MaxPercent"/>. Pure.</summary>
        public static double SanitizePercent(double raw)
            => double.IsNaN(raw) || double.IsInfinity(raw) || raw <= 0.0 ? 0.0 : Math.Min(raw, MaxPercent);

        /// <summary>
        /// The health points <paramref name="percent"/> of <paramref name="max"/> is: rounded, and at least 1 for any positive percent of a
        /// positive maximum, so a small crystal still moves. 0 for a non-positive maximum or percent. Pure.
        /// </summary>
        public static int Amount(int max, double percent)
        {
            if (max <= 0 || !(percent > 0.0))
                return 0;

            var amount = Math.Round(max * Math.Min(percent, MaxPercent) / 100.0, MidpointRounding.AwayFromZero);

            return (int)Math.Clamp(amount, 1.0, max);
        }

        /// <summary>
        /// The 3D distance from <paramref name="deathPosition"/> to a crystal site, with the same function Creature.GetDistance uses
        /// (ACE.Entity.Position.DistanceTo: plain x, y, z in the landblock frame for two points in one landblock). Pure.
        /// </summary>
        public static double DistanceToSite(Position deathPosition, BattlegroundCrystalSite site, uint matchLandblock, uint matchInstance)
            => CrystalProximity.DistanceToSite(deathPosition, site, matchLandblock, matchInstance);
    }
}
