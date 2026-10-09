using ACE.Server.EquipmentMods;
using ACE.Server.Managers;
using ACE.Server.WorldObjects;

namespace ACE.Server.ClassAbilities.Abilities
{
    /// <summary>
    /// Blood Mage T2 game-changer: a landed Harm or Drain applies/refreshes Weakened Blood on the target for
    /// 20s. While it holds, every life-magic hit on that target - from ANY caster, not only the one who
    /// applied it - resolves against a life resistance mod of 2.00/2.50/3.10 by rank: the top three rungs of
    /// the retail vulnerability ladder (Vulnerability V, VI, and the Incantation). Rank 3 is the Incantation
    /// value exactly, the same ceiling the war elements get. No rider - rank alone carries it. It is a
    /// target-side debuff, not caster state, precisely so a second life caster in the fellowship reads it.
    /// BLOOD-MAGE-DESIGN.md sec 3 / 4a.
    ///
    /// WHERE THE MECHANIC LIVES. No hook interface. The mark is TARGET-SIDE transient state
    /// (Creature_ClassAbilityDebuffs.cs) over <see cref="WeakenedBloodMath"/>, applied by
    /// Player.TryApplyWeakenedBlood from the two landed-spell sites (Harm in
    /// WorldObject_Magic.HandleCastSpell_Boost, Drain in HandleCastSpell_Transfer, including every one of
    /// Crimson Harvest's secondary targets).
    ///
    /// IT ALSO LANDS THE RETAIL FESTER ENCHANTMENT (user, live test 2026-08-03: "the 'Fester' spell is what
    /// should be applied, and also multiply outgoing damage. The fester animation should be visible when
    /// this happens"). Fester Other V / VI / the Incantation by rank - the same rungs of the retail ladder
    /// the 2.00/2.50/3.10 vulnerability values themselves come from (see
    /// <see cref="WeakenedBloodMath.FesterSpell"/>). The enchantment is what the player SEES: it plays its
    /// own portal.dat TargetEffect on the creature, broadcast so nearby players see it too. It is NOT where
    /// the vulnerability is stored, and must not become so - the registry's top-layer selection is by
    /// PowerLevel, never by StatModValue, so a Fester from any other source would otherwise displace a
    /// blood mage's mark.
    ///
    /// It is fed into <c>Creature.GetLifeVulnerabilityMod</c> rather than multiplied at the damage sites.
    /// That seam takes MAX against weapon cleave/rend, so "one life-vulnerability axis" stays a mechanical
    /// guarantee instead of a spreadsheet convention - a second multiplier at a damage site would silently
    /// re-open the stacking that method exists to prevent.
    ///
    /// DRAIN APPLIES THE MARK BUT DOES NOT BENEFIT FROM IT (user ruling, 2026-08-02: "Lets exclude drains
    /// from the blood rend/vuln ... Blood rend/vuln should apply to harm, heca, raven"). Applying and
    /// benefiting are separate questions and only the second one is excluded, so a blood mage's Drain still
    /// sets up every Harm and Hecatomb that follows, including a fellow's. The beneficiary list is
    /// <see cref="WeakenedBloodMath.DamageBenefits"/>; the exclusion is enforced in
    /// HandleCastSpell_Transfer, which reads Creature.GetHealthDrainResistanceOnly rather than entering the
    /// axis at all. Drain is the odd one out because spell.TransferCap binds against anything worth
    /// draining, which makes a vulnerability there either invisible or a filler-into-nuke conversion -
    /// making Drain better is a deferred design decision, not a gap to close here.
    ///
    /// The applying strike does not amplify itself - the mark is applied after that strike's damage has
    /// resolved.
    /// </summary>
    public class WeakenedBloodAbility : IClassAbility, IPassiveStatAbility, IAbilityReadout
    {
        public ClassAbilityDefinition Definition { get; } = new ClassAbilityDefinition
        {
            Id = ClassAbilityId.WeakenedBlood,
            AbilityClass = ClassAbilityClass.BloodMage,
            Tier = 2,
            Name = "weakened_blood",
            DisplayName = "Weakened Blood",
            Description = "A landed Harm or Drain leaves the target festering with Weakened Blood for 20 " +
                          "seconds. While it holds, every Harm, Martyr's Hecatomb and Curse of Raven Fury " +
                          "that strikes that target, from any caster and not only you, resolves against a " +
                          "life resistance of 2.00/2.50/3.10 by rank. Drain sets the mark but does not gain " +
                          "from it. It shares one axis with weapon resistance cleaving: the stronger " +
                          "applies, never both.",
            MaxRank = 3,
            CostPerRank = new[] { 1, 1, 1 },
            Implemented = true,
        };

        /// <summary>
        /// Reports WeakenedBloodMath.ResistanceMod as a BARE MULTIPLIER (2.00/2.50/3.10 by rank), not a
        /// percentage. Skill is the rank's table value; there is no legacy-skill rider; Gear is the
        /// HEMORRHAGE mod, reported in that same bare-multiplier unit because that is the unit it is added
        /// in (+0.05 takes a rank-3 mark to 3.15) - so Skill + Gear reads as the real mark. No cap: rank
        /// and gear are the only inputs, and the 1.0 floor is unreachable from any non-negative pair.
        ///
        /// The null-conditional on the gear read is for the readout unit tests, which call this with a null
        /// Player because Player's static initializer cannot run under the test host; every live caller
        /// (/abilities list) passes session.Player.
        ///
        /// Effective calls the REAL helper with the gear term rather than adding the two numbers here, so
        /// the 1.0 vulnerability floor and the rank-0 early return are honoured by the same code combat
        /// runs; Gear is then reported as the difference the mod actually made.
        /// </summary>
        public ClassAbilityReadout GetReadout(Player player, int rank)
        {
            var gearMod = player?.GetEquippedModValue(EquipmentModId.Hemorrhage) ?? 0.0;

            var r1 = PropertyManager.GetDouble("class_ability_weakenedblood_resist_r1").Item;
            var r2 = PropertyManager.GetDouble("class_ability_weakenedblood_resist_r2").Item;
            var r3 = PropertyManager.GetDouble("class_ability_weakenedblood_resist_r3").Item;

            var mod = WeakenedBloodMath.ResistanceMod(rank, r1, r2, r3);
            var effective = WeakenedBloodMath.ResistanceMod(rank, r1, r2, r3, gearMod);

            return new ClassAbilityReadout
            {
                HasValue = true,
                Skill = mod,
                Affinity = 0.0,
                Gear = effective - mod,
                Effective = effective,
                Unit = "x",
                Label = "life vuln",
                Per = null,
                CapNote = null,
            };
        }
    }
}
