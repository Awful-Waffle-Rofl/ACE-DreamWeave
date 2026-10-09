using System;

using ACE.Entity.Enum;
using ACE.Server.EquipmentMods;
using ACE.Server.Managers;
using ACE.Server.WorldObjects;

namespace ACE.Server.ClassAbilities.Abilities
{
    /// <summary>
    /// Vanguard T2 game-changer: while a shield is equipped, a chance to reduce an incoming hit to 0
    /// (8/14/20% by rank), pooled with Parry into one combined avoidance roll (capped). Block chance
    /// scales with Armor Tinkering (a maintained shield blocks more). Blocked hits STILL trigger Thorns
    /// (the enemy hit the shield) - the base behaviour that makes Thorns + Shield Block synergise. Subject to
    /// Thorns' own close-range gate (class_ability_thorns_max_range, inside Player.ApplyThornsReflect): a
    /// blocked hit from an attacker beyond it reflects nothing.
    ///
    /// Bespoke: the chance is read in Player.RollClassAbilityAvoidance (before the evade roll). Carries
    /// IPassiveStatAbility.
    /// </summary>
    public class ShieldBlockAbility : IClassAbility, IPassiveStatAbility, IAbilityReadout
    {
        public ClassAbilityDefinition Definition { get; } = new ClassAbilityDefinition
        {
            Id = ClassAbilityId.ShieldBlock,
            AbilityClass = ClassAbilityClass.Vanguard,
            Tier = 2,
            Name = "shieldblock",
            DisplayName = "Shield Block",
            Description = "While a shield is equipped, 8/14/20% of incoming hits are reduced to 0 (pooled with " +
                          "Parry, 50% combined cap). Higher Armor Tinkering increases the chance. Blocked hits still trigger Thorns.",
            MaxRank = 3,
            CostPerRank = new[] { 1, 1, 1 },
            Implemented = true,
            AffinitySkill = Skill.ArmorTinkering, // the Deception read in GetReadout is Parry's half of the pooled roll, not this ability's rider
        };

        /// <summary>
        /// Block chance at a given rank: the ability's OWN rank bonus (base + (rank-1)*step) SCALED by the
        /// Armor Tinkering affinity multiplier, with the added amount CAPPED, plus the Shield Wall
        /// equipment mod. Pure.
        ///
        /// <paramref name="affinityMultiplier"/> is what
        /// Player.GetClassAbilityAffinityMultiplier(Skill.ArmorTinkering) returns: a factor >= 1.0 that is
        /// EXACTLY 1.0 at zero effective Armor Tinkering, leaving the chance bit-identical to rank alone.
        /// Floored at 1.0 here as well, so a caller that hands over 0.0 - the neutral value of the OLD
        /// additive primitive, and an easy mistake because the two have identical shapes - degrades to
        /// rank-only rather than silently multiplying the whole bonus away.
        ///
        /// <paramref name="affinityCap"/> bounds the AMOUNT the affinity multiply ADDS
        /// (rankBonus * multiplier - rankBonus), never the rank ladder and never the factor itself
        /// (class_ability_affinity_chance_cap). It has to exist here for the same reason it exists on
        /// Spellblade/Runeblade/Sundermark/Acid Proc/Elemental Rend: the affinity is linear in a skill value
        /// the server does not constrain, so the added amount has no upper bound of its own. Shield Block is
        /// the worst case in that family because it sits under a SECOND clamp - the pooled Shield Block +
        /// Parry avoidance cap (class_ability_avoidance_cap, applied in ClassAbilityAvoidance.Pooled) - so
        /// an uncapped affinity does not merely inflate this number, it monopolises the whole shared pool
        /// and leaves no room for anything else that feeds it. A cap of 0 means uncapped.
        ///
        /// <paramref name="gearModChance"/> is the SHIELD WALL equipment mod (EquipmentModId.ShieldWall), a
        /// MACHINERY mod: it amplifies this ability's own block roll and is unreachable without it, because
        /// the rank test above returns first. It is added OUTSIDE the affinity clamp, in the same position
        /// spellsurgeBonus occupies in SpellbladeAbility.Chance - affinityCap exists to bound an unbounded
        /// SKILL quotient, while a gear term is already bounded twice over by its registry MaxMagnitude and
        /// its StackCap. Folding it inside the clamp would let a saturating rider silently eat the mod,
        /// which is the Resonance failure recorded in DESIGN.md section 2.2. Last and defaulted to 0, so an
        /// unmodded build reproduces the previous chance bit-for-bit.
        /// </summary>
        public static double BlockChance(int rank, double baseChance, double stepPerRank, double affinityMultiplier, double affinityCap = 0.0, double gearModChance = 0.0)
        {
            if (rank <= 0)
                return 0.0;

            var rankBonus = baseChance + (rank - 1) * stepPerRank;

            // The multiplier scales this ability's OWN rank bonus. What the cap bounds is the AMOUNT that
            // multiply ADDS, not the factor itself - the factor is a bare number like 1.51 and clamping it
            // against a tunable expressed in percentage points would be a unit error.
            var added = rankBonus * Math.Max(1.0, affinityMultiplier) - rankBonus;

            if (affinityCap > 0.0)
                added = Math.Min(added, affinityCap);

            return Math.Max(0.0, rankBonus + added + Math.Max(0.0, gearModChance));
        }

        /// <summary>
        /// Mirrors the terms fed into BlockChance() above: the rank bonus base + step*(rank-1), the amount
        /// the Armor Tinkering affinity multiply ADDS to it, and the SHIELD WALL equipment mod. Affinity is
        /// the CAPPED added amount (clamped by the affinity cap the same way BlockChance() clamps it), so
        /// the three displayed terms still sum to the pre-avoidance-pool total. Reporting the added amount
        /// rather than the bare factor keeps all three displayed terms in one unit - a factor like "1.51"
        /// beside two percentages would be unreadable.
        ///
        /// TWO SEPARATE CLAMPS CAN BITE HERE, and CapNote names the one that actually reduced the number
        /// this line reports. The pooled avoidance cap (class_ability_avoidance_cap, shared with Parry) is
        /// the LAST thing applied and therefore owns Effective whenever it bites, so it wins the note; the
        /// affinity clamp (class_ability_affinity_chance_cap, on the added amount only) is reported when it
        /// reduced that amount and the pooled cap did not fire.
        ///
        /// See ParryAbility.GetReadout for the full explanation of the pooled cap. Both readouts call
        /// ClassAbilityAvoidance.Pooled directly rather than restating its scale-down, because Pooled scales
        /// the two shares against each other and a hand-copied clamp would drift from the real rule.
        /// Parry's own chance is read here unconditionally from rank, matching the same "how strong is the
        /// ability" convention.
        ///
        /// The null-conditional on the gear read is for the readout unit tests, which call this with a null
        /// Player because Player's static initializer cannot run under the test host.
        /// </summary>
        public ClassAbilityReadout GetReadout(Player player, int rank)
        {
            var baseChance = PropertyManager.GetDouble("class_ability_shieldblock_base").Item;
            var stepPerRank = PropertyManager.GetDouble("class_ability_shieldblock_step").Item;
            var affinityCap = PropertyManager.GetDouble("class_ability_affinity_chance_cap").Item;

            var rankBonus = rank <= 0 ? 0.0 : baseChance + (rank - 1) * stepPerRank;

            // NEUTRAL IS 1.0, NOT 0.0 - see ParryAbility.GetReadout for why a 0.0 fallback here would
            // multiply the ability's whole rank bonus away rather than omit the rider.
            var multiplier = player?.GetClassAbilityAffinityMultiplier(Skill.ArmorTinkering) ?? 1.0;

            // The RAW (pre-clamp) amount the multiply adds. This is the quantity the affinity cap bounds,
            // so it is also the quantity the cap must be compared against.
            var armorTink = Math.Max(0.0, rankBonus * multiplier - rankBonus);

            // BlockChance() below applies the clamp itself; this only records whether it bit, for CapNote.
            var affinityCapBites = affinityCap > 0.0 && armorTink > affinityCap;

            // The CAPPED added amount, so Affinity below matches what BlockChance() actually folds in.
            var clampedArmorTink = affinityCap > 0.0 ? Math.Min(armorTink, affinityCap) : armorTink;

            // Rank-gated like the skill term below: this is a MACHINERY mod, so it reports nothing without
            // the ability.
            var gearChance = rank <= 0
                ? 0.0
                : Math.Max(0.0, player?.GetEquippedModValue(EquipmentModId.ShieldWall) ?? 0.0);

            var blockChance = BlockChance(rank, baseChance, stepPerRank, multiplier, affinityCap, gearChance);

            player.TryGetClassAbility(ClassAbilityId.Parry, out var parryRank);

            // Parry's half of the pool moved to the MULTIPLICATIVE affinity model in the same change as
            // Shield Block's own half, deliberately: this readout prints both, and a panel showing one
            // ability's affinity as a multiplier beside the other's as a flat rider would be reporting two
            // different models side by side.
            var parryMultiplier = player.GetClassAbilityAffinityMultiplier(Skill.Deception);
            // Surefooted fed the same parry term combat builds (Player.RollClassAbilityAvoidance), so the
            // OTHER half of the pool had to include it here too, until it was retired on 2026-09-12 (class
            // ability overhaul).
            var parryChance = ParryAbility.ParryChance(parryRank,
                PropertyManager.GetDouble("class_ability_parry_percent_per_rank").Item,
                parryMultiplier);

            // Shield Block and Parry share ONE pooled cap, so neither can compute its own effective chance
            // alone. Call the real pooling helper rather than mirroring its scale-down here - a hand-copied
            // clamp would drift from ClassAbilityAvoidance the first time the pooling rule changed.
            var cap = PropertyManager.GetDouble("class_ability_avoidance_cap").Item;
            var (effectiveBlock, _, capBites) = ClassAbilityAvoidance.Pooled(blockChance, parryChance, cap);

            var skill = rankBonus * 100.0;
            var affinity = clampedArmorTink * 100.0;

            return new ClassAbilityReadout
            {
                HasValue = true,
                Skill = skill,
                Affinity = affinity,
                Gear = gearChance * 100.0,
                Effective = effectiveBlock * 100.0,
                Unit = "%",
                Label = "block",
                Per = null,
                CapNote = capBites ? "avoidance cap" : (affinityCapBites ? "affinity cap" : null),
            };
        }
    }
}
