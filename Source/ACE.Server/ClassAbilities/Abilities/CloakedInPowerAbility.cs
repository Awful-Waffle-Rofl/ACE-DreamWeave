using System;

using ACE.Entity.Enum;
using ACE.Server.Managers;
using ACE.Server.WorldObjects;

namespace ACE.Server.ClassAbilities.Abilities
{
    /// <summary>
    /// Vanguard T2 (added 2026-09-29): a FLOOR under the wearer's cloak spell-proc chance. One rank, 1 CAP.
    ///
    /// WHAT IT CHANGES, AND ONLY THAT. Cloak.RollProc computes its chance as
    /// <c>Math.Max(Math.Min(damage_percent, maxProcRate), floor)</c>, where damage_percent is the hit as a
    /// fraction of the wearer's MaxHealth and maxProcRate is the cloak's item-level-scaled cap (0.25 at item
    /// level 1 rising to 0.30 at level 5 for a spell-proc cloak). This ability raises the floor term from
    /// the shipped <c>cloak_min_proc</c> of 0 to <see cref="Floor"/>. Nothing else about the roll moves, and
    /// because the floor only ever enters through a Math.Max it can never LOWER a chance that is already
    /// above it - a big hit against a high-level cloak rolls exactly the number it rolled before.
    ///
    /// IT DOES NOT BYPASS EITHER GATE, AND THAT IS A RULING RATHER THAN AN IMPLEMENTATION DETAIL. Two early
    /// returns sit AHEAD of the chance computation in Cloak.RollProc: the 5 second per-cloak cooldown keyed
    /// on cloak.UseTimestamp, and the hard refusal when cloak.ItemLevel &lt; 1. The floor is applied strictly
    /// after both, so an unleveled cloak still never procs and the cooldown still caps throughput no matter
    /// how high the floor is tuned. A future change that moved the floor ahead of either gate would turn a
    /// 10% floor into a 10%-per-hit proc with no cooldown, which is not the ability that was signed off.
    ///
    /// WHY THE FLOOR IS READ INSIDE Cloak.TryProcSpell RATHER THAN PASSED IN BY EACH CALLER. RollProc never
    /// receives the defender and so cannot read a class ability itself, but TryProcSpell already takes the
    /// cloak's wearer as its <c>defender</c> parameter at every one of its four call sites (the melee /
    /// missile / hotspot path in Player.TakeDamage, the spell-projectile path in SpellProjectile, and the
    /// Harm and Drain Health paths in WorldObject_Magic). Reading the floor there means a FIFTH cloak
    /// spell-proc site cannot skip it: there is no floor argument for a new call site to omit, because the
    /// only way to reach HandleProcSpell is through TryProcSpell, which computes the floor itself.
    /// CloakProcWiringSourceScanTests pins that last clause as a source scan.
    ///
    /// DAMAGE-OVER-TIME TICKS STILL DO NOT PROC A CLOAK AT ALL. Player.TakeDamageOverTime deliberately
    /// carries no cloak call, and this ability adds none - it only changes the chance at sites that already
    /// rolled.
    ///
    /// THE DAMAGE-REDUCTION (CloakWeaveProc = 2) ROLL IS OUT OF SCOPE. Those four sites call
    /// Cloak.RollProc directly rather than through TryProcSpell, so they keep the shipped
    /// <c>cloak_min_proc</c> floor. The ability is advertised as a SPELL-proc floor and is implemented as
    /// exactly that.
    ///
    /// IPassiveStatAbility, not a combat hook: the value is an always-on number read at the computation site,
    /// the same shape as Reflect, Parry, Shield Block and Pocket Sand. The marker is what exempts an
    /// Implemented entry from the "must hook something" rule - see IPassiveStatAbility in
    /// ClassAbilityHooks.cs.
    /// </summary>
    public class CloakedInPowerAbility : IClassAbility, IPassiveStatAbility, IAbilityReadout
    {
        public ClassAbilityDefinition Definition { get; } = new ClassAbilityDefinition
        {
            Id = ClassAbilityId.CloakedInPower,
            AbilityClass = ClassAbilityClass.Vanguard,
            Tier = 2,
            Name = "cloaked_in_power",
            DisplayName = "Cloaked in Power",
            Description = "Your equipped cloak's spell proc never has less than a 10% chance to fire on a " +
                          "hit that reaches you, however small the hit was. It does not shorten the cloak's " +
                          "5 second proc cooldown, and a cloak that has not been leveled still never procs. " +
                          "Higher Armor Tinkering multiplies the floor.",
            MaxRank = 1,
            CostPerRank = new[] { 1 },
            Implemented = true,
            AffinitySkill = Skill.ArmorTinkering,
        };

        /// <summary>
        /// The proc-chance floor at a given rank, as a fraction: the ability's OWN rank bonus (the
        /// <c>class_ability_cloakedinpower_floor</c> tunable, rank-invariant because MaxRank is 1) SCALED by
        /// the Armor Tinkering affinity multiplier, with the added amount CAPPED. Pure for testability,
        /// matching PinningShotAbility.Chance / ReflectMagicAbility.ReflectChance.
        ///
        /// <paramref name="affinityMultiplier"/> is what
        /// Player.GetClassAbilityAffinityMultiplier(Skill.ArmorTinkering) returns: a factor &gt;= 1.0 that is
        /// EXACTLY 1.0 at zero effective Armor Tinkering, leaving the floor bit-identical to the bare tunable.
        /// Floored at 1.0 here as well, so a caller that hands over 0.0 - the neutral value of the OLD
        /// additive primitive, and an easy mistake because the two have identical shapes - degrades to
        /// tunable-only rather than silently multiplying the whole floor away.
        ///
        /// <paramref name="affinityCap"/> bounds the AMOUNT the affinity multiply ADDS
        /// (rankBonus * multiplier - rankBonus), never the tunable itself and never the factor
        /// (class_ability_affinity_chance_cap). It applies here because the floor IS a proc chance, which is
        /// the scope that tunable's own doc comment gives it, and because the multiplier grows without bound
        /// in Armor Tinkering - unclamped, a high-Armor-Tinkering Vanguard would walk a 10% floor up toward
        /// the cloak's own cap and past it, at which point the floor would be the only term that mattered and
        /// item level and hit size would both stop meaning anything. A cap of 0 means uncapped. There is no
        /// equipment mod for this entry, so no gear term sits outside the clamp.
        /// </summary>
        public static double Floor(int rank, double floorBase, double affinityMultiplier, double affinityCap = 0.0)
        {
            if (rank <= 0)
                return 0.0;

            // Rank-invariant: MaxRank is 1, so there is no ladder to walk - the tunable IS the rank bonus.
            var rankBonus = floorBase;

            // The multiplier scales this ability's OWN floor. What the cap bounds is the AMOUNT that multiply
            // ADDS, not the factor itself - the factor is a bare number like 1.51 and clamping it against a
            // tunable expressed in percentage points would be a unit error.
            var added = rankBonus * Math.Max(1.0, affinityMultiplier) - rankBonus;

            if (affinityCap > 0.0)
                added = Math.Min(added, affinityCap);

            return Math.Max(0.0, rankBonus + added);
        }

        /// <summary>
        /// Reports the FLOOR - the only number this entry carries, since MaxRank is 1 and the cooldown and
        /// item-level gates it deliberately leaves alone are not its own values. Affinity is the CAPPED
        /// amount the Armor Tinkering multiply ADDS (clamped the same way <see cref="Floor"/> clamps it), so
        /// Skill + Affinity + Gear sums to Effective exactly, which
        /// ClassAbilityAffinityCapReadoutInvariantTests pins for this readout family.
        ///
        /// The null-conditional on the affinity read is for the readout unit tests, which call this with a
        /// null Player because Player's static initializer cannot run under the test host - and it falls
        /// back to 1.0, the NEUTRAL factor, never 0.0.
        /// </summary>
        public ClassAbilityReadout GetReadout(Player player, int rank)
        {
            var floorBase = PropertyManager.GetDouble("class_ability_cloakedinpower_floor").Item;
            var affinityCap = PropertyManager.GetDouble("class_ability_affinity_chance_cap").Item;

            var rankBonus = rank <= 0 ? 0.0 : floorBase;

            // NEUTRAL IS 1.0, NOT 0.0. The null-Player fallback must be the identity factor: 0.0 would not
            // omit the rider, it would multiply this ability's entire floor away.
            var multiplier = player?.GetClassAbilityAffinityMultiplier(Skill.ArmorTinkering) ?? 1.0;

            // The RAW (pre-clamp) amount the multiply adds. This is the quantity the affinity cap bounds, so
            // it is also the quantity the cap must be compared against.
            var rawAdded = Math.Max(0.0, rankBonus * multiplier - rankBonus);

            // Floor() applies the clamp itself; this only records whether it bit, for CapNote.
            var affinityCapBites = affinityCap > 0.0 && rawAdded > affinityCap;

            // The CAPPED added amount, so Affinity below matches what Floor() actually folds in.
            var clampedAdded = affinityCap > 0.0 ? Math.Min(rawAdded, affinityCap) : rawAdded;

            var floor = Floor(rank, floorBase, multiplier, affinityCap);

            return new ClassAbilityReadout
            {
                HasValue = true,
                Skill = rankBonus * 100.0,
                Affinity = clampedAdded * 100.0,
                Gear = 0.0,
                Effective = floor * 100.0,
                Unit = "%",
                Label = "cloak proc floor",
                Per = null,
                CapNote = affinityCapBites ? "affinity cap" : null,
            };
        }
    }
}
