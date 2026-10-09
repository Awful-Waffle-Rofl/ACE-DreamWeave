using System;
using System.Collections.Generic;
using System.Linq;

namespace ACE.Server.Pvp.Battlegrounds
{
    /// <summary>One crystal of a planned match: its index (0 .. N-1, the position in the plan), site, starting health and compass word.</summary>
    public sealed record PlannedCrystal(int Index, BattlegroundCrystalSite Site, int MaxHealth, string Compass)
    {
        /// <summary>The location words alerts quote: the site's hint when it has one, else the compass word.</summary>
        public string Where => string.IsNullOrEmpty(Site.Hint) ? Compass : Site.Hint;
    }

    /// <summary>
    /// The crystals one Attack/Defend match plays with, decided ONCE when the match forms from the layout and the dials snapshot
    /// (Docs/Pvp/ATTACK-DEFEND.md "Crystals", "Settings"). The crystal INDEX is the position in <see cref="Crystals"/>; it is the
    /// identity the match tag, the destroyed intent and the alerts all use. Pure: no clock, no random, no live setting.
    /// </summary>
    public sealed record AttackDefendPlan(IReadOnlyList<PlannedCrystal> Crystals, int HealthPerCrystal, int AttackerCount)
    {
        /// <summary>How many crystals the attackers must destroy.</summary>
        public int Count => Crystals.Count;

        /// <summary>
        /// The sequential-crystal state (Docs/Pvp/ATTACK-DEFEND.md "Sequential crystals"), or null for any-order play: the dial was off at
        /// formation, or the plan has fewer than two crystals (one crystal has no order to keep). Shared by reference with the match
        /// (<c>PvpMatch.CrystalSequence</c>), which is how the crystal damage gate reads it.
        /// </summary>
        public CrystalSequence Sequence { get; init; }

        /// <summary>
        /// How many crystals a match on <paramref name="layout"/> plays with: pvp_bg_ad_crystal_count of 0 means the layout's own
        /// default, any other value is clamped to 1 .. the number of sites. A layout with no sites plays with none. Pure.
        /// </summary>
        public static int EffectiveCount(BattlegroundLayout layout, BattlegroundDials dials)
        {
            var sites = layout?.CrystalSites?.Count ?? 0;

            if (sites == 0)
                return 0;

            var wanted = dials.AdCrystalCount == 0 ? layout.DefaultCrystalCount : dials.AdCrystalCount;

            return Math.Clamp(wanted, 1, sites);
        }

        /// <summary>
        /// The plan for a match: the first <see cref="EffectiveCount"/> sites in list order, each with
        /// <c>pvp_bg_ad_crystal_health_per_attacker x attackerCount</c> health (saturating at int.MaxValue, and counting at least one
        /// attacker so a crystal is never born dead). <paramref name="attackerCount"/> is the attacking team's size at formation. With
        /// pvp_bg_ad_sequential_crystals on (and two or more crystals) the plan also carries a fresh <see cref="CrystalSequence"/>.
        /// </summary>
        public static AttackDefendPlan Build(BattlegroundLayout layout, BattlegroundDials dials, int attackerCount)
        {
            var attackers = Math.Max(1, attackerCount);
            var health = (int)Math.Min(int.MaxValue, (long)Math.Max(1, dials.AdCrystalHealthPerAttacker) * attackers);
            var count = EffectiveCount(layout, dials);

            var crystals = layout == null
                ? new List<PlannedCrystal>()
                : layout.CrystalSites.Take(count).Select((site, i) => new PlannedCrystal(i, site, health, layout.CompassFor(site))).ToList();

            // The names are the sites' display names, in plan order; the sequence shares the plan's own order and count.
            var sequence = dials.AdSequentialCrystals && crystals.Count >= 2 ? new CrystalSequence(crystals.Select(c => c.Site.Name)) : null;

            return new AttackDefendPlan(crystals.AsReadOnly(), health, attackers) { Sequence = sequence };
        }
    }

    /// <summary>One standing crystal's health as the match samples it: its plan index and its current and maximum health.</summary>
    public readonly record struct CrystalHealth(int Index, int Current, int Max);

    /// <summary>
    /// Rescales standing crystals when an attacker leaves a live match (Docs/Pvp/ATTACK-DEFEND.md ruling 12): every crystal's current
    /// and maximum health scale by (attackers now / attackers the values are sized for), so the remaining attackers are not asked
    /// for the leaver's share and a half-damaged crystal stays half-damaged. Pure.
    ///
    /// <para/>
    /// <paramref name="sizedForAttackers"/> is the attacker count the CURRENT values correspond to: the formation count on the first
    /// call, the count passed as <c>currentAttackers</c> on the previous call after that. Passing the formation count every time
    /// would apply the ratio on top of an already-scaled value and compound it. The ratio never exceeds 1 (a returning attacker does
    /// not grow a crystal). A standing crystal (current above 0) keeps at least 1 current and 1 maximum, so scaling alone can never
    /// destroy one; a crystal already at 0 stays at 0.
    /// </summary>
    public static class CrystalHealthScaler
    {
        public static IReadOnlyList<CrystalHealth> Scale(IReadOnlyList<CrystalHealth> standing, int sizedForAttackers, int currentAttackers)
        {
            if (standing == null)
                return Array.Empty<CrystalHealth>();

            var from = Math.Max(1, sizedForAttackers);
            var to = Math.Clamp(currentAttackers, 0, from);

            return standing.Select(c => ScaleOne(c, from, to)).ToList().AsReadOnly();
        }

        private static CrystalHealth ScaleOne(CrystalHealth c, int from, int to)
        {
            if (c.Max <= 0)
                return c;

            var max = (int)Math.Max(1, Math.Round((double)c.Max * to / from, MidpointRounding.AwayFromZero));

            if (c.Current <= 0)
                return c with { Max = max, Current = 0 };

            var current = (int)Math.Clamp(Math.Round((double)c.Current * to / from, MidpointRounding.AwayFromZero), 1, max);

            return c with { Max = max, Current = current };
        }
    }
}
