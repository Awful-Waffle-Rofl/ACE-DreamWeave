using System;
using System.Collections.Generic;

using ACE.Entity.Enum;
using ACE.Server.Managers;
using ACE.Server.WorldObjects;

namespace ACE.Server.ClassAbilities.Abilities
{
    /// <summary>
    /// Vanguard T2 (added 2026-09-29): a FLOOR under the wearer's cloak spell-proc chance. Three ranks, 1 CAP each
    /// since the Vanguard pass of 2026-11-06, which added the CRITICAL THORNS window: any successful cloak SPELL
    /// proc roll opens (or refreshes) a 0.5 / 1.0 / 1.5 s (by rank) window in which every Thorns reflect the
    /// wearer deals is critical, the triggering hit's own reflect included - see Cloak.RollSpellProc and
    /// Player.ApplyThornsReflect. The floor itself is the same at every rank.
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
    /// cloak's wearer as its <c>defender</c> parameter at every one of its six call sites (the melee /
    /// missile / hotspot path in Player.TakeDamage, the spell-projectile path in SpellProjectile, the
    /// Harm and Drain Health paths in WorldObject_Magic, the Reflect bounce in Player.TryReflectProjectile,
    /// and the Shield Block in Player.OnClassAbilityAttackAvoided, which has no damage figure and so rolls on
    /// the floor alone). Reading the floor there means a SEVENTH cloak
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
            // Live text: the floor is class_ability_cloakedinpower_floor (GetReadout). The description no longer quotes the
            // cloak's own proc cooldown (Cloak.MinDelay, Entity/Cloak.cs, is the constant 5.0 unless use_cloak_proc_custom_scale,
            // so cloak_cooldown_seconds is not what the game uses by default). The Critical Thorns window is quoted per rank by the
            // tail, {ranks:...crit_window_r|f1} (CritWindowSeconds builds the same keys from CritWindowPrefix).
            DescriptionTemplate = "Your cloak's spell proc always has at least a {v:class_ability_cloakedinpower_floor|pct}% chance " +
                          "to fire on a hit that reaches you, however small the hit. It does not shorten the cloak's cooldown, and " +
                          "an unleveled cloak still never procs. When the cloak procs, your Thorns reflects as a critical hit for a " +
                          "few seconds, including on the hit that triggered it. Those critical reflects use " +
                          "your shield's full critical damage.",
            RankTailTemplate = "{ranks:class_ability_cloakedinpower_crit_window_r|f1} seconds of critical Thorns",
            MaxRank = 3,
            CostPerRank = new[] { 1, 1, 1 },
            Implemented = true,
            AffinitySkill = Skill.ArmorTinkering,
            Ceilings = new[]
            {
                new ClassAbilityCeiling { Id = "affinity", Bounds = CeilingBounds.Affinity, Source = CeilingSource.Tunable, Key = "class_ability_affinity_chance_cap", Label = "max chance from affinity", Format = "pct" },
            },
        };

        /// <summary>
        /// The tunable-key prefix (rank number 1-3 appended) for the Critical Thorns window length in seconds.
        /// </summary>
        public const string CritWindowPrefix = "class_ability_cloakedinpower_crit_window_r";

        /// <summary>
        /// The base multiplier of a CRITICAL Thorns reflect with no Crippling Blow and no Execution (owner ruling
        /// 2026-11-06: "your crit multiplier"): the same 2x a critical hit's base damage uses. Production no longer
        /// reads it: ThornsAbility.CritDamageMultiplier and ThornsAbility.DamageRatingMultiplier are the live formula. It is kept as a parity pin that
        /// formula is tested against.
        /// </summary>
        public const double CritThornsBaseMultiplier = 2.0;

        /// <summary>
        /// How long the Critical Thorns window stays open after a cloak spell proc, in seconds, at a rank
        /// (0 at rank 0). Ranks above the ladder clamp to the top rung.
        /// </summary>
        public static double CritWindowSeconds(int rank)
        {
            if (rank <= 0)
                return 0.0;

            return Math.Max(0.0, PropertyManager.GetDouble(CritWindowPrefix + Math.Clamp(rank, 1, EnhancedStatAbility.MaxTier)).Item);
        }

        /// <summary>
        /// When the window closes after a proc at <paramref name="now"/>: always now + duration, never an
        /// extension on top of the time left, so a proc inside an open window REFRESHES it and nothing stacks.
        /// A zero duration leaves the current end untouched.
        /// </summary>
        public static double WindowEnd(double now, double durationSeconds, double currentEnd)
        {
            return durationSeconds > 0.0 ? now + durationSeconds : currentEnd;
        }

        /// <summary>
        /// The no-Crippling-Blow, no-Execution, zero-Damage-Rating value of a Thorns crit (ThornsAbility.CritDamageMultiplier
        /// x ThornsAbility.DamageRatingMultiplier), kept as a parity pin (no production caller; the tests compare the
        /// live formula against it): 2.0 x the positive rating mod of
        /// the wearer's total Critical Damage Rating (Creature.GetPositiveRatingMod, (100 + rating) / 100; a
        /// negative rating takes the negative-rating branch there, so it can shrink the crit but the 2.0 base stays).
        /// </summary>
        public static double CritThornsMultiplier(int critDamageRating)
        {
            return CritThornsBaseMultiplier * Creature.GetPositiveRatingMod(critDamageRating);
        }

        /// <summary>
        /// The proc-chance floor at a given rank, as a fraction: the ability's OWN rank bonus (the
        /// <c>class_ability_cloakedinpower_floor</c> tunable, rank-INVARIANT: ranks 2 and 3 (added 2026-11-06)
        /// buy a longer Critical Thorns window, not a higher floor) SCALED by
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

            // Rank-invariant: the floor does not climb with rank (the extra ranks lengthen the Critical Thorns
            // window) - the tunable IS the rank bonus at every rank.
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
        /// Reports the FLOOR as the primary figure (rank-invariant: the cooldown and
        /// item-level gates it deliberately leaves alone are not its own values) and the Critical Thorns window,
        /// in seconds, as a Secondary. Affinity is the CAPPED
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
                Ceilings = new[] { ClassAbilityCeilings.Read(Definition, "affinity", rawAdded * 100.0, rank, ClassAbilityText.Live) },
                Secondary = new List<ClassAbilityReadout>
                {
                    new ClassAbilityReadout
                    {
                        HasValue = true,
                        Skill = CritWindowSeconds(rank),
                        Affinity = 0.0,
                        Gear = 0.0,
                        Effective = CritWindowSeconds(rank),
                        Unit = "s",
                        Label = "critical thorns window",
                        Per = null,
                        CapNote = null,
                    },
                },
            };
        }
    }
}
