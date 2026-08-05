using ACE.Server.EquipmentMods;
using ACE.Server.Managers;
using ACE.Server.WorldObjects;

namespace ACE.Server.ClassAbilities.Abilities
{
    /// <summary>
    /// Blood Mage T3 (named Martyr's Bargain in earlier revisions): Martyr's Hecatomb and Curse of Raven Fury
    /// still spend their full health basis for damage, but the caster only actually loses 80/65/50% of it by
    /// rank - AND the health that is lost returns as a damage-absorbing ward worth 50/75/100% of that amount
    /// for 15s. The ward REFRESHES rather than stacks (so it is capped at one cast's worth) and it EXPIRES
    /// UNUSED - it is protection while you are being hit, never a refund. Deliberately unscaled. This is the
    /// class's defensive entry: it costs no slot, changes no damage axis, and protects the pool at exactly
    /// the moment 25-50% of current health leaves it. BLOOD-MAGE-DESIGN.md sec 3 / 4a-iv.
    ///
    /// WHY AN ABSORB AND NOT DAMAGE REDUCTION - this was the deciding constraint, not a flavour preference,
    /// so do not "improve" the shape into DR later:
    ///  - DAMAGE REDUCTION IS A SHARED AXIS. POWER-LEDGER records Mana Barrier and Battle Hardened as both
    ///    `damage-reduction` and ADDITIVE with each other, so a DR entry here would stack into a cross-class
    ///    ceiling with an Archmage or Vanguard splash and would need a ceiling check.
    ///  - AVOIDANCE IS SEPARATELY POOLED and already binding at its class_ability_avoidance_cap (50%), so a
    ///    negate-proc there would buy a wall build literally nothing.
    ///  - TEMPORARY HP TOUCHES NEITHER BUDGET, so it cannot blow a pooled ceiling and needs no new axis.
    ///
    /// It also deliberately does NOT restore health - it only stops health leaving. After a Hecatomb chain
    /// the caster is genuinely at low health with a ward up, so burst thresholds and death risk are
    /// unchanged. That is what keeps the entry safe under a DPS-parity target, where the health economy IS
    /// the balance mechanism.
    ///
    /// MECHANIC COMPLETE. The arithmetic (<see cref="SanguineWardMath"/>), the transient per-player ward pool
    /// and its heartbeat expiry (Player_ClassAbilityBuffs: GetSanguineWardSelfCostFraction /
    /// GrantSanguineWard / AbsorbWithSanguineWard), the consuming half on the take-damage path
    /// (Player_Combat.TakeDamage, absorbing BEFORE the deduction reaches Health) and the grant trigger at the
    /// life-projectile cast site (WorldObject_Magic.HandleCastSpell_Projectile, the DamageType.Health branch)
    /// are all in place, so <see cref="ClassAbilityDefinition.Implemented"/> is TRUE.
    ///
    /// THE CAST SITE CLAMPS THE DAMAGE BASIS to the caster's health before the deduction. That clamp is not
    /// incidental: before payment and basis were decoupled the basis WAS the health actually removed, and
    /// UpdateVital clamps to [0, MaxValue], so an over-pool basis could not occur. Paying a fraction removes
    /// that guard, and without the explicit clamp a rank 3 caster short of the full basis would bill damage
    /// against health that never left the pool.
    ///
    /// IPassiveStatAbility, like every other Blood Mage entry: the ward is read directly off the player at
    /// two computation sites (the cast site for the self-cost and the grant, Player_Combat.TakeDamage for the
    /// absorb), never dispatched from a combat event, so it hooks no interface. Without the marker the
    /// registry's "an Implemented ability must hook something" invariant fails - which is exactly what it did
    /// the moment Implemented flipped to true.
    /// </summary>
    public class SanguineWardAbility : IClassAbility, IPassiveStatAbility, IAbilityReadout
    {
        public ClassAbilityDefinition Definition { get; } = new ClassAbilityDefinition
        {
            Id = ClassAbilityId.SanguineWard,
            AbilityClass = ClassAbilityClass.BloodMage,
            Tier = 3,
            Name = "sanguine_ward",
            DisplayName = "Sanguine Ward",
            Description = "Martyr's Hecatomb and Curse of Raven Fury still spend their full health basis for " +
                          "damage, but you only lose 80/65/50% of it by rank, and the health you do lose " +
                          "returns as a damage-absorbing ward worth 50/75/100% of that amount for 15 seconds. " +
                          "The ward refreshes rather than stacks, and expires unused.",
            MaxRank = 3,
            CostPerRank = new[] { 3, 3, 3 },
            Implemented = true,
        };

        /// <summary>
        /// How much health the caster actually pays for a Health-drain-basis spell (Martyr's Hecatomb /
        /// Curse of Raven Fury), given the spell's full damage basis. This is the one call site in the
        /// Blood Mage set that used to name <see cref="SanguineWardMath"/> directly from a general engine
        /// file (WorldObject_Magic.HandleCastSpell_Transfer) rather than going through a Player/Ability
        /// method, so it is routed through here instead.
        ///
        /// CASTER MAY BE NULL - Hecatomb is castable by monsters, and a null caster must pay the FULL basis,
        /// matching the retail cost before Sanguine Ward existed. That is what the `?? 1.0` reproduces:
        /// <see cref="SanguineWardMath.SelfCost"/> with a fraction of 1.0 returns healthBasis unchanged.
        /// A live (non-null) Player pays their rank's <see cref="Player.GetSanguineWardSelfCostFraction"/>
        /// (80/65/50% by rank, 1.0 for rank 0 / unlearned) instead.
        /// </summary>
        public static uint SelfCost(Player caster, uint damageBasis)
        {
            return SanguineWardMath.SelfCost(damageBasis, caster?.GetSanguineWardSelfCostFraction() ?? 1.0);
        }

        /// <summary>
        /// Reports the ABSORB fraction (SanguineWardMath.WardFraction, 50/75/100% by rank) as a percent.
        /// Skill is the rank's fraction, there is no legacy-skill rider, and Gear is the CLOTTING mod,
        /// added to that fraction on the same additive axis. WardFraction has no upper clamp of its own
        /// (the tunable is the authority, see its doc comment), so CapNote is always null.
        ///
        /// THE ABSORB, NOT THE SELF-COST. Sanguine Ward's other half - how much of the health basis the
        /// caster actually pays - is unmoddable by ruling and is not reported here at any term.
        ///
        /// The null-conditional on the gear read is for the readout unit tests, which call this with a null
        /// Player because Player's static initializer cannot run under the test host; every live caller
        /// (/abilities list) passes session.Player.
        /// </summary>
        public ClassAbilityReadout GetReadout(Player player, int rank)
        {
            var gearFraction = player?.GetEquippedModValue(EquipmentModId.Clotting) ?? 0.0;

            var fraction = SanguineWardMath.WardFraction(rank,
                PropertyManager.GetDouble("class_ability_sanguine_ward_absorb_r1").Item,
                PropertyManager.GetDouble("class_ability_sanguine_ward_absorb_r2").Item,
                PropertyManager.GetDouble("class_ability_sanguine_ward_absorb_r3").Item);

            var skill = fraction * 100.0;
            var gear = gearFraction * 100.0;

            return new ClassAbilityReadout
            {
                HasValue = true,
                Skill = skill,
                Affinity = 0.0,
                Gear = gear,
                Effective = skill + gear,
                Unit = "%",
                Label = "ward",
                Per = null,
                CapNote = null,
            };
        }
    }
}
