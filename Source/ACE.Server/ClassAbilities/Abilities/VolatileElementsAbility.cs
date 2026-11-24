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
            // Live text. Chance is rank * class_ability_volatileelements_chance_per_rank (VolatileElementsAbility.cs ~57 GetReadout,
            // Player_VolatileElements.cs ~33). The article is {an}, chosen from the first rank's value (8 -> "an"), so a retune to another first-rank value re-picks it. The token tail re-picks it per rank ("an 8%" / "a 16%" / "a 24%").
            DescriptionTemplate = "Your melee and missile weapon hits deal a random damage type: slash, pierce, bludgeon, fire, " +
                          "cold, acid, electric or nether. Each hit has a chance to be the element the target is weakest to. Your " +
                          "weapon's rend applies to whatever element you deal, and the element carries into your Spellblade, " +
                          "Runeblade, Spellstorm and Sundermark procs.",
            RankTailTemplate = "{an} {per:class_ability_volatileelements_chance_per_rank|pct}% chance of the weakest element",
            MaxRank = 3,
            CostPerRank = new[] { 1, 1, 1 },
            Implemented = true,
            AffinitySkill = Skill.ItemTinkering,
            Ceilings = new[]
            {
                new ClassAbilityCeiling { Id = "affinity", Bounds = CeilingBounds.Affinity, Source = CeilingSource.Tunable, Key = "class_ability_affinity_chance_cap", Label = "max chance from affinity", Format = "pct" },
            },
        };

        /// <summary>
        /// Mirrors VolatileElementsMath.Chance (x100 for display): the rank term, plus the CAPPED amount the Item
        /// Tinkering multiplier adds, so the displayed terms sum to Effective. No equipment mod exists for this
        /// ability, so Gear is 0.
        /// </summary>
        public ClassAbilityReadout GetReadout(Player player, int rank)
        {
            return BuildReadout(rank, player.GetClassAbilityAffinityMultiplier(Skill.ItemTinkering));
        }

        /// <summary>
        /// The pure core of <see cref="GetReadout"/>: every live input is a parameter, so a test can pin the readout (and its ceiling readings) without a Player.
        /// </summary>
        public ClassAbilityReadout BuildReadout(int rank, double affinityMultiplier)
        {
            var perRank = PropertyManager.GetDouble("class_ability_volatileelements_chance_per_rank").Item;
            var affinityCap = PropertyManager.GetDouble("class_ability_affinity_chance_cap").Item;

            var rankChance = rank <= 0 ? 0.0 : rank * perRank;

            var rawAdded = rankChance * affinityMultiplier - rankChance;
            var effective = VolatileElementsMath.Chance(rank, perRank, affinityMultiplier, affinityCap);

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
                Ceilings = new[] { ClassAbilityCeilings.Read(Definition, "affinity", rawAdded * 100.0, rank, ClassAbilityText.Live) },
            };
        }
    }
}
