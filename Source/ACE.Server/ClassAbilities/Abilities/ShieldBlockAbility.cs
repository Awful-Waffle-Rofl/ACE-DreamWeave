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
    /// (the enemy hit the shield) - the base behaviour that makes Thorns + Shield Block synergise.
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
            CostPerRank = new[] { 1, 2, 3 },
            Implemented = true,
            AffinitySkill = Skill.ArmorTinkering, // the Deception read in GetReadout is Parry's half of the pooled roll, not this ability's rider
        };

        /// <summary>
        /// Block chance at a given rank: base + (rank-1)*step, plus the CAPPED Armor Tinkering rider, plus
        /// the Shield Wall equipment mod. Pure.
        ///
        /// <paramref name="affinityCap"/> bounds the Armor Tinkering rider ONLY
        /// (class_ability_affinity_chance_cap). It has to exist here for the same reason it exists on
        /// Spellblade/Runeblade/Sundermark/Acid Proc/Elemental Rend: GetClassAbilityScaling returns a RAW
        /// quotient (effectiveSkill / divisor) with no upper bound of its own, so the rider is linear in a
        /// skill value the server does not constrain. Shield Block is the worst case in that family because
        /// it sits under a SECOND clamp - the pooled Shield Block + Parry avoidance cap
        /// (class_ability_avoidance_cap, applied in ClassAbilityAvoidance.Pooled) - so an uncapped rider
        /// does not merely inflate this number, it monopolises the whole shared pool and leaves no room for
        /// anything else that feeds it. A cap of 0 means uncapped, which reproduces the pre-cap behaviour
        /// exactly.
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
        public static double BlockChance(int rank, double baseChance, double stepPerRank, double armorTinkFraction, double affinityCap = 0.0, double gearModChance = 0.0)
        {
            if (rank <= 0)
                return 0.0;

            var rider = Math.Max(0.0, armorTinkFraction);

            if (affinityCap > 0.0)
                rider = Math.Min(rider, affinityCap);

            return Math.Max(0.0, baseChance + (rank - 1) * stepPerRank + rider + Math.Max(0.0, gearModChance));
        }

        /// <summary>
        /// Mirrors the terms fed into BlockChance() above: base + step*(rank-1), the Armor Tinkering rider,
        /// and the SHIELD WALL equipment mod. Affinity carries the RAW (pre-clamp) rider while Effective
        /// uses the clamped one, matching AcidProcAbility's pattern.
        ///
        /// TWO SEPARATE CLAMPS CAN BITE HERE, and CapNote names the one that actually reduced the number
        /// this line reports. The pooled avoidance cap (class_ability_avoidance_cap, shared with Parry) is
        /// the LAST thing applied and therefore owns Effective whenever it bites, so it wins the note; the
        /// affinity clamp (class_ability_affinity_chance_cap, on the Armor Tinkering rider only) is reported
        /// when it reduced the rider and the pooled cap did not fire.
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

            var armorTink = Math.Max(0.0, player.GetClassAbilityScaling(Skill.ArmorTinkering,
                PropertyManager.GetDouble("class_ability_shieldblock_armortink_per_trained").Item,
                PropertyManager.GetDouble("class_ability_shieldblock_armortink_per_spec").Item) * 0.01);

            // BlockChance() below applies the clamp itself; this only records whether it bit, for CapNote.
            var affinityCapBites = affinityCap > 0.0 && armorTink > affinityCap;

            // Rank-gated like the skill term below: this is a MACHINERY mod, so it reports nothing without
            // the ability.
            var gearChance = rank <= 0
                ? 0.0
                : Math.Max(0.0, player?.GetEquippedModValue(EquipmentModId.ShieldWall) ?? 0.0);

            var blockChance = BlockChance(rank, baseChance, stepPerRank, armorTink, affinityCap, gearChance);

            player.TryGetClassAbility(ClassAbilityId.Parry, out var parryRank);
            var deception = player.GetClassAbilityScaling(Skill.Deception,
                PropertyManager.GetDouble("class_ability_parry_deception_per_trained").Item,
                PropertyManager.GetDouble("class_ability_parry_deception_per_spec").Item) * 0.01;
            // Surefooted feeds the same parry term combat builds (Player.RollClassAbilityAvoidance), so the
            // OTHER half of the pool has to include it here too - Pooled() scales the two shares against
            // each other, and an understated parry share silently OVERSTATES the block number this line
            // reports. Nothing about Shield Block changes; only the pooling input does.
            var parryChance = ParryAbility.ParryChance(parryRank,
                PropertyManager.GetDouble("class_ability_parry_percent_per_rank").Item,
                deception)
                + (player?.GetSurefootedParryBonus() ?? 0.0);

            // Shield Block and Parry share ONE pooled cap, so neither can compute its own effective chance
            // alone. Call the real pooling helper rather than mirroring its scale-down here - a hand-copied
            // clamp would drift from ClassAbilityAvoidance the first time the pooling rule changed.
            var cap = PropertyManager.GetDouble("class_ability_avoidance_cap").Item;
            var (effectiveBlock, _, capBites) = ClassAbilityAvoidance.Pooled(blockChance, parryChance, cap);

            var skill = rank <= 0 ? 0.0 : (baseChance + (rank - 1) * stepPerRank) * 100.0;
            var affinity = armorTink * 100.0;   // RAW (pre-clamp), so Total shows what the rider would be uncapped

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
