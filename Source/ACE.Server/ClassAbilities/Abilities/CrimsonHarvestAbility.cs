using ACE.Server.WorldObjects;

namespace ACE.Server.ClassAbilities.Abilities
{
    /// <summary>
    /// Blood Mage T2: Drain spells strike up to 4 creatures within 8m of the primary target instead of one.
    /// Each secondary drain resolves independently against its own target (its own transfer_Cap, its own
    /// resistance roll) and the healing from all of them returns to the caster. Single rank, no scaling - the
    /// target count IS the effect, which is why it compounds with nothing: Drain sits outside both caster
    /// levers and the vulnerability axis, so improving it by target count adds no multiplier to the
    /// single-target product. Grants at most ONE Blood Charge per cast regardless of how many targets it
    /// strikes, and applies Weakened Blood to every target it lands on. PvE only.
    /// BLOOD-MAGE-DESIGN.md sec 3 / 4c.
    ///
    /// WHERE THE MECHANIC LIVES. No hook interface: WorldObject_Magic.TryCrimsonHarvest runs at the end of
    /// a primary Drain and re-enters HandleCastSpell_Transfer once per extra target with
    /// isSecondaryStrike set. Re-entering rather than copying a reduced transfer is the whole point - each
    /// secondary drain gets its own roll, its own resistance, its own TransferCap clamp, its own crit and
    /// its own Transfusion cascade. The nearest-first selection rule is <see cref="CrimsonHarvestMath"/>.
    ///
    /// THE RECOUP IS DELIBERATELY UNCAPPED (user, 2026-08-03): four independent drains can return four full
    /// heals to a caster with the missing health to absorb them. That is to be watched in play before it is
    /// tuned - do not add a limiter without a ruling.
    ///
    /// The isSecondaryStrike flag is also the re-entry guard, so a dense pack cannot chain into an
    /// unbounded harvest, and it is what makes "at most one Blood Charge per cast" true regardless of how
    /// many creatures the drain touches.
    ///
    /// BOTH VISUALS REACH EVERY PARTICIPANT (user, live test 2026-08-03). The two halves are covered
    /// differently and deliberately so:
    ///  - the DRAIN visual on each secondary enemy is broadcast by TryCrimsonHarvest itself, replaying the
    ///    spell's own TargetEffect, because DoSpellEffects runs once per CAST and only ever sees the
    ///    primary;
    ///  - the HEAL visual on each fellow who catches cascaded health needs nothing new - the Transfusion
    ///    cascade in HandleCastSpell_Transfer already broadcasts PlayScript.HealthUpRed per paid fellow, and
    ///    because a secondary strike RE-ENTERS that method in full, it runs its own cascade with its own
    ///    visuals. Do not add a second broadcast for it; that would double the effect on every fellow;
    ///  - the CASTER'S OWN heal visual is broadcast once per cast, guarded on isSecondaryStrike for exactly
    ///    the opposite reason: every secondary strike also heals the caster, so an unguarded broadcast would
    ///    flash a four-target harvest four times in a fraction of a second.
    /// </summary>
    public class CrimsonHarvestAbility : IClassAbility, IPassiveStatAbility, IAbilityReadout
    {
        public ClassAbilityDefinition Definition { get; } = new ClassAbilityDefinition
        {
            Id = ClassAbilityId.CrimsonHarvest,
            AbilityClass = ClassAbilityClass.BloodMage,
            Tier = 2,
            Name = "crimson_harvest",
            DisplayName = "Crimson Harvest",
            Description = "Your Drain spells strike up to 4 creatures within 8 metres of the primary target " +
                          "instead of one. Each secondary drain resolves on its own and all of the healing " +
                          "returns to you. Grants at most one Blood Charge per cast. Against monsters only.",
            MaxRank = 1,
            CostPerRank = new[] { 5 },
            Implemented = true,
        };

        /// <summary>
        /// Single rank, and its effect is a target COUNT plus a radius - no single scalable magnitude on
        /// one axis worth printing, matching WhirlwindAbility's shape.
        /// </summary>
        public ClassAbilityReadout GetReadout(Player player, int rank) => new ClassAbilityReadout { HasValue = false };
    }
}
