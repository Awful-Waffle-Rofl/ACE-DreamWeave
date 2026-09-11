using System;

using ACE.Entity.Enum;
using ACE.Server.EquipmentMods;
using ACE.Server.Managers;
using ACE.Server.WorldObjects;

namespace ACE.Server.ClassAbilities.Abilities
{
    /// <summary>
    /// Rogue T2 game-changer: a chance to fully negate an incoming melee hit (5/10/15% by rank), pooled
    /// with Shield Block into one combined avoidance roll (capped, see ClassAbilityAvoidance). The negate
    /// chance scales with Deception (a parry is a feint). Parried hits do NOT trigger Thorns by
    /// themselves - only via the Vanguard's Shield Check - but DO feed the Rogue's Riposte counter.
    ///
    /// Bespoke: the chance is read in Player.RollClassAbilityAvoidance (before the evade roll), not through
    /// a standard combat hook. Carries IPassiveStatAbility.
    /// </summary>
    public class ParryAbility : IClassAbility, IPassiveStatAbility, IAbilityReadout
    {
        public ClassAbilityDefinition Definition { get; } = new ClassAbilityDefinition
        {
            Id = ClassAbilityId.Parry,
            AbilityClass = ClassAbilityClass.Rogue,
            Tier = 2,
            Name = "parry",
            DisplayName = "Parry",
            Description = "A 5% chance per rank to fully negate an incoming melee hit (pooled with Shield Block, " +
                          "50% combined cap). Higher Deception increases the chance.",
            MaxRank = 3,
            CostPerRank = new[] { 1, 2, 3 },
            Implemented = true,
            AffinitySkill = Skill.Deception, // the Armor Tinkering read in GetReadout is Shield Block's half of the pooled roll, not this ability's rider
        };

        /// <summary>Parry chance at a given rank: rank*perRank plus the Deception rider fraction. Pure.</summary>
        public static double ParryChance(int rank, double percentPerRank, double deceptionFraction)
        {
            if (rank <= 0)
                return 0.0;

            return Math.Max(0.0, rank * percentPerRank + Math.Max(0.0, deceptionFraction));
        }

        /// <summary>
        /// No equipment mod exists for Parry (ParryChance takes no gear parameter), so Gear is always 0.0.
        ///
        /// Parry and Shield Block share ONE pooled cap (class_ability_avoidance_cap), applied where the two
        /// chances are combined in Player.RollClassAbilityAvoidance. Neither ability can compute its own
        /// effective chance alone, because ClassAbilityAvoidance.Pooled scales the two shares AGAINST EACH
        /// OTHER. This calls Pooled directly rather than restating its scale-down: a hand-copied clamp in
        /// two readouts would drift from the real rule the first time the pooling changed.
        ///
        /// Shield Block's own chance is read here unconditionally from rank (ignoring the shield-equipped
        /// gate that real combat applies), matching this readout family's established convention of
        /// reporting "how strong the ability is" rather than "will it fire on the next hit" (see SavageBlows
        /// / Frenzy, which ignore their own runtime gates the same way).
        /// </summary>
        public ClassAbilityReadout GetReadout(Player player, int rank)
        {
            var percentPerRank = PropertyManager.GetDouble("class_ability_parry_percent_per_rank").Item;

            var deception = player?.GetClassAbilityScaling(Skill.Deception,
                PropertyManager.GetDouble("class_ability_parry_deception_per_trained").Item,
                PropertyManager.GetDouble("class_ability_parry_deception_per_spec").Item) * 0.01 ?? 0.0;

            // Surefooted (Rogue T2) adds into the SAME parryChance term combat builds, so it has to be part
            // of the input to Pooled() below - the pooled cap scales the two shares against each other, and
            // an understated parry share silently understates this whole line. It is reported as Skill
            // rather than Affinity because it is another class ability's rank contribution, not a
            // legacy-skill rider; Surefooted's own /abilities line reports it separately, which is a
            // deliberate double-count across two lines for readability, exactly as Shield Block's line
            // already restates Parry's chance.
            var surefooted = player?.GetSurefootedParryBonus() ?? 0.0;

            var parryChance = ParryChance(rank, percentPerRank, deception) + surefooted;

            player.TryGetClassAbility(ClassAbilityId.ShieldBlock, out var blockRank);
            var armorTink = player.GetClassAbilityScaling(Skill.ArmorTinkering,
                PropertyManager.GetDouble("class_ability_shieldblock_armortink_per_trained").Item,
                PropertyManager.GetDouble("class_ability_shieldblock_armortink_per_spec").Item) * 0.01;

            // The other half of the pool has to be computed EXACTLY as combat computes it, cap and gear
            // included - Pooled() scales the two shares against each other, so an overstated block chance
            // silently understates the parry number reported on this line.
            var blockChance = ShieldBlockAbility.BlockChance(blockRank,
                PropertyManager.GetDouble("class_ability_shieldblock_base").Item,
                PropertyManager.GetDouble("class_ability_shieldblock_step").Item,
                armorTink,
                PropertyManager.GetDouble("class_ability_affinity_chance_cap").Item,
                blockRank <= 0 ? 0.0 : Math.Max(0.0, player?.GetEquippedModValue(EquipmentModId.ShieldWall) ?? 0.0));

            // Parry and Shield Block share ONE pooled cap, so neither can compute its own effective chance
            // alone. Call the real pooling helper rather than mirroring its scale-down here - a hand-copied
            // clamp would drift from ClassAbilityAvoidance the first time the pooling rule changed.
            var cap = PropertyManager.GetDouble("class_ability_avoidance_cap").Item;
            var (_, effectiveParry, capBites) = ClassAbilityAvoidance.Pooled(blockChance, parryChance, cap);

            var skill = (rank <= 0 ? 0.0 : rank * percentPerRank) * 100.0 + surefooted * 100.0;
            var affinity = deception * 100.0;

            return new ClassAbilityReadout
            {
                HasValue = true,
                Skill = skill,
                Affinity = affinity,
                Gear = 0.0,
                Effective = effectiveParry * 100.0,
                Unit = "%",
                Label = "parry",
                Per = null,
                CapNote = capBites ? "avoidance cap" : null,
            };
        }
    }
}
