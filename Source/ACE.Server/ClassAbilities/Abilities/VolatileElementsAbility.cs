using ACE.Entity.Enum;
using ACE.Server.Managers;
using ACE.Server.WorldObjects;

namespace ACE.Server.ClassAbilities.Abilities
{
    /// <summary>
    /// VOLATILE ELEMENTS (Spellsword T2, owner rulings 2026-10-30). Always on, PvE only (player attacker,
    /// non-player creature defender): every landed melee or missile WEAPON hit is re-typed to one of slash,
    /// pierce, bludgeon, fire, cold, acid, electric or nether. With an 8/16/24% chance (by rank, times the Item
    /// Tinkering affinity, the affinity's added amount capped by class_ability_affinity_chance_cap) the new type
    /// is the monster's WEAKEST innate resist among the eight (ties uniform); otherwise a uniform pick of all
    /// eight. Immune types are not skipped. The arithmetic lives in <see cref="VolatileElementsMath"/>.
    ///
    /// ONE ROLL PER DamageEvent, at one seam: DamageEvent.DoCalculateDamage, right after
    /// GetBaseDamage(playerAttacker) (so the missile launcher's flat elemental bonus is still computed from the
    /// ORIGINAL type) and before ResolveDefenderHalf, through Player.RollVolatileElement. Each cleave target and
    /// each Multishot arrow builds its own DamageEvent, so each rolls separately. Directly cast spells, PvP,
    /// weapon cast-on-strike spells and the launcher's flat elemental bonus are out of scope.
    ///
    /// WHAT FOLLOWS THE ROLL: DamageEvent.DamageType becomes the rolled type, so armor, vulnerabilities and
    /// protections, innate resist (Elemental Inversion then rolls on the rolled type), shields, and the same
    /// hit's Spellblade / Runeblade / Spellstorm spell element and Sundermark vulnerability element (all of
    /// which read damageEvent.DamageType) follow it. REND LOOKS BACK: DamageEvent.OriginalDamageType keeps the
    /// weapon's own type, and both the hit's WeaponResistanceMod and the war procs' rend
    /// (SpellProjectile.RendSourceDamageType) take the better of the original and the rolled type's rend.
    ///
    /// Bespoke integration; carries IPassiveStatAbility because it hooks no dispatched combat event.
    /// </summary>
    public class VolatileElementsAbility : IClassAbility, IPassiveStatAbility, IAbilityReadout
    {
        public ClassAbilityDefinition Definition { get; } = new ClassAbilityDefinition
        {
            Id = ClassAbilityId.VolatileElements,
            AbilityClass = ClassAbilityClass.Spellsword,
            Tier = 2,
            Name = "volatile_elements",
            DisplayName = "Volatile Elements",
            Description = "Your melee and missile weapon hits deal a random damage type - slash, pierce, bludgeon, fire, " +
                          "cold, acid, electric or nether - with an 8/16/24% chance (by rank) to be the element the target " +
                          "is weakest to. Your weapon's rend applies to whichever element is dealt, and the element carries " +
                          "through to your Spellblade, Runeblade, Spellstorm and Sundermark procs. Higher Item Tinkering " +
                          "increases the chance.",
            MaxRank = 3,
            CostPerRank = new[] { 1, 1, 1 },
            Implemented = true,
            AffinitySkill = Skill.ItemTinkering,
        };

        /// <summary>
        /// Mirrors VolatileElementsMath.Chance (x100 for display): the rank term, plus the CAPPED amount the Item
        /// Tinkering multiplier adds, so the displayed terms sum to Effective. No equipment mod exists for this
        /// ability, so Gear is 0.
        /// </summary>
        public ClassAbilityReadout GetReadout(Player player, int rank)
        {
            var perRank = PropertyManager.GetDouble("class_ability_volatileelements_chance_per_rank").Item;
            var affinityCap = PropertyManager.GetDouble("class_ability_affinity_chance_cap").Item;

            var rankChance = rank <= 0 ? 0.0 : rank * perRank;

            var multiplier = player.GetClassAbilityAffinityMultiplier(Skill.ItemTinkering);

            var rawAdded = rankChance * multiplier - rankChance;
            var effective = VolatileElementsMath.Chance(rank, perRank, multiplier, affinityCap);

            var capBit = affinityCap > 0.0 && rawAdded > affinityCap;

            return new ClassAbilityReadout
            {
                HasValue = true,
                Skill = rankChance * 100.0,
                Affinity = (effective - rankChance) * 100.0,
                Gear = 0.0,
                Effective = effective * 100.0,
                Unit = "%",
                Label = "weakest-element chance",
                Per = null,
                CapNote = capBit ? "affinity cap" : null,
            };
        }
    }
}
