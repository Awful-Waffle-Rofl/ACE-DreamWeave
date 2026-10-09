using System;

using ACE.Entity.Enum;
using ACE.Server.EquipmentMods;
using ACE.Server.Managers;
using ACE.Server.WorldObjects;

namespace ACE.Server.ClassAbilities.Abilities
{
    /// <summary>
    /// Rogue T2 game-changer: a chance to fully negate an incoming melee hit (8/16/24% by rank), pooled
    /// with Shield Block into one combined avoidance roll (capped, see ClassAbilityAvoidance). The negate
    /// chance scales with Deception (a parry is a feint). Parried hits do NOT trigger Thorns, but DO feed
    /// the Rogue's Riposte counter. (The Vanguard's Shield Check used to convert a parry into a scaled
    /// Thorns reflect; it was retired on 2026-09-12, so nothing converts one now.)
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
            Description = "An 8% chance per rank to fully negate an incoming melee hit (pooled with Shield Block, " +
                          "50% combined cap). Higher Deception increases the chance.",
            MaxRank = 3,
            CostPerRank = new[] { 1, 1, 1 },
            Implemented = true,
            AffinitySkill = Skill.Deception, // the Armor Tinkering read in GetReadout is Shield Block's half of the pooled roll, not this ability's rider
        };

        /// <summary>
        /// Parry chance at a given rank: the ability's OWN rank bonus (rank*perRank) SCALED by the Deception
        /// affinity multiplier, not a flat Deception rider added beside it (2026-09-12 overhaul). Pure.
        ///
        /// <paramref name="affinityMultiplier"/> is what
        /// Player.GetClassAbilityAffinityMultiplier(Skill.Deception) returns: a factor >= 1.0 that is
        /// EXACTLY 1.0 at zero effective Deception, leaving the chance bit-identical to rank alone. It is
        /// floored at 1.0 here as well, so a caller that hands over 0.0 - the neutral value of the OLD
        /// additive primitive, and an easy mistake because the two have identical shapes - degrades to
        /// rank-only rather than silently multiplying the whole bonus away.
        ///
        /// There is deliberately NO affinity cap parameter. Parry is not one of the abilities
        /// class_ability_affinity_chance_cap covers (that tunable's own doc comment lists them, and Parry is
        /// not among them); its ceiling is the pooled Shield Block + Parry avoidance cap, applied later in
        /// ClassAbilityAvoidance.Pooled.
        /// </summary>
        public static double ParryChance(int rank, double percentPerRank, double affinityMultiplier)
        {
            if (rank <= 0)
                return 0.0;

            var rankBonus = rank * percentPerRank;

            return Math.Max(0.0, rankBonus * Math.Max(1.0, affinityMultiplier));
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

            var rankBonus = rank <= 0 ? 0.0 : rank * percentPerRank;

            // NEUTRAL IS 1.0, NOT 0.0. A null Player must fall back to the identity factor: 0.0 would not
            // "omit the rider", it would multiply this ability's entire rank bonus away. That is exactly the
            // confusion ClassAbilityAffinity's doc comment warns the two same-shaped primitives invite.
            var multiplier = player?.GetClassAbilityAffinityMultiplier(Skill.Deception) ?? 1.0;

            // Surefooted added into this same parryChance term, and had to be part of the input to Pooled()
            // below, until it was retired on 2026-09-12 (class ability overhaul).
            var parryChance = ParryChance(rank, percentPerRank, multiplier);

            player.TryGetClassAbility(ClassAbilityId.ShieldBlock, out var blockRank);

            // Shield Block's half of the pool moved to the MULTIPLICATIVE affinity model in the same change
            // as Parry's own half, deliberately: this readout prints both, and a panel showing one ability's
            // affinity as a multiplier beside the other's as a flat rider would be reporting two different
            // models side by side.
            var blockMultiplier = player.GetClassAbilityAffinityMultiplier(Skill.ArmorTinkering);

            // The other half of the pool has to be computed EXACTLY as combat computes it, cap and gear
            // included - Pooled() scales the two shares against each other, so an overstated block chance
            // silently understates the parry number reported on this line.
            var blockChance = ShieldBlockAbility.BlockChance(blockRank,
                PropertyManager.GetDouble("class_ability_shieldblock_base").Item,
                PropertyManager.GetDouble("class_ability_shieldblock_step").Item,
                blockMultiplier,
                PropertyManager.GetDouble("class_ability_affinity_chance_cap").Item,
                blockRank <= 0 ? 0.0 : Math.Max(0.0, player?.GetEquippedModValue(EquipmentModId.ShieldWall) ?? 0.0));

            // Parry and Shield Block share ONE pooled cap, so neither can compute its own effective chance
            // alone. Call the real pooling helper rather than mirroring its scale-down here - a hand-copied
            // clamp would drift from ClassAbilityAvoidance the first time the pooling rule changed.
            var cap = PropertyManager.GetDouble("class_ability_avoidance_cap").Item;
            var (_, effectiveParry, capBites) = ClassAbilityAvoidance.Pooled(blockChance, parryChance, cap);

            var skill = rankBonus * 100.0;

            // Affinity is a MULTIPLIER on the rank term, but it is reported as the AMOUNT that multiplier
            // adds (rankBonus * multiplier - rankBonus), so all three displayed terms stay in the same unit
            // and still sum to the uncapped total. Reporting the bare factor would put a number like "1.51"
            // beside two percentages.
            var affinity = (rankBonus * multiplier - rankBonus) * 100.0;

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
