using System.Collections.Generic;

using ACE.Server.EquipmentMods;
using ACE.Server.Managers;
using ACE.Server.WorldObjects;

namespace ACE.Server.ClassAbilities.Abilities
{
    /// <summary>
    /// Blood Mage T3 (named Martyr's Bargain in earlier revisions): Martyr's Hecatomb and Curse of Raven Fury
    /// still spend their full health basis for damage, but the caster only actually loses 75/55/35% of it by
    /// rank - AND the health that is lost returns as a damage-absorbing ward worth 60/90/120% of that amount
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
    /// GrantSanguineWard / AbsorbWithSanguineWard), the consuming half on every damage path (see below) and
    /// the grant trigger at the life-projectile cast site (WorldObject_Magic.HandleCastSpell_Projectile, the
    /// DamageType.Health branch) are all in place, so <see cref="ClassAbilityDefinition.Implemented"/> is
    /// TRUE.
    ///
    /// THE CONSUMING HALF IS FIVE CALL SITES, NOT ONE, and none of them is redundant. There is no single
    /// choke point through which a player loses health: Player.TakeDamage covers melee, missile and
    /// hotspots, and the four magic paths write Health straight to the vital without ever passing through
    /// it - SpellProjectile.DamageTarget (war/void/life bolts), HandleCastSpell_Boost (Harm),
    /// HandleCastSpell_Transfer (Drain Health) and EnchantmentManager.ApplyDamageTick (DoT ticks). Only the
    /// first was wired until 2026-09-03, so the ward absorbed melee and missile damage but not the spell
    /// damage a Blood Mage actually faces. Do not "simplify" the other four away.
    ///
    /// THE DoT CALL SITS AT THE ACCUMULATION POINT, not in Player.TakeDamageOverTime where it started. It
    /// was moved up on 2026-09-08: ApplyDamageTick used to cap its accumulated tick total to the victim's
    /// current Health before calling down, so a ward applied below that cap could only ever reduce a figure
    /// already at most current Health, leaving the player strictly alive - a warded player could not be
    /// killed by a DoT of any size. Mana Barrier had the identical exposure at the identical line and both
    /// were fixed together. Do not move it back down.
    ///
    /// Every site absorbs BEFORE its health write and after any cloak damage proc it has, replaces the
    /// damage with what the ward returns, and reports the REDUCED number - see AbsorbWithSanguineWard for
    /// the full caller contract, including why the ward applies NO attacker filter and so deliberately
    /// absorbs PvP and self-damage, where Mana Barrier on the same hook absorbs neither.
    ///
    /// THE ONE BEHAVIOUR THE HOOK CHANGED: the dispatch gates on class_abilities_enabled, as every class
    /// ability dispatch does, where the hand-written call sites did not. A ward can only be GRANTED while
    /// the system is enabled, so this is reachable only by an admin flipping the switch off (or by a
    /// respec) inside the 15 second window of a ward already up, in which case the ward stops absorbing
    /// instead of finishing its window.
    ///
    /// THE CAST SITE CLAMPS THE DAMAGE BASIS to the caster's health before the deduction. That clamp is not
    /// incidental: before payment and basis were decoupled the basis WAS the health actually removed, and
    /// UpdateVital clamps to [0, MaxValue], so an over-pool basis could not occur. Paying a fraction removes
    /// that guard, and without the explicit clamp a rank 3 caster short of the full basis would bill damage
    /// against health that never left the pool.
    ///
    /// IPreWriteDamageAbility, the pre-write mitigation hook. The self-cost and the grant are still read
    /// directly off the player at the cast site, but the ABSORB half is dispatched now: the five damage
    /// sites above call Player.ApplyPreWriteDamageClassAbilities, which reaches this handler, which calls
    /// AbsorbWithSanguineWard. It was an IPassiveStatAbility marker with five hand-written call sites until
    /// that hook existed - which is what the hook was built to end, since the next mitigation ability would
    /// otherwise have added five more by hand.
    /// </summary>
    public class SanguineWardAbility : IClassAbility, IPreWriteDamageAbility, IAbilityReadout
    {
        public ClassAbilityDefinition Definition { get; } = new ClassAbilityDefinition
        {
            Id = ClassAbilityId.SanguineWard,
            AbilityClass = ClassAbilityClass.BloodMage,
            Tier = 3,
            Name = "sanguine_ward",
            DisplayName = "Sanguine Ward",
            Description = "Martyr's Hecatomb and Curse of Raven Fury still spend their full health basis for " +
                          "damage, but you only lose 75/55/35% of it by rank, and the health you do lose " +
                          "returns as a damage-absorbing ward worth 60/90/120% of that amount for 15 seconds. " +
                          "The ward refreshes rather than stacks, and expires unused.",
            MaxRank = 3,
            CostPerRank = new[] { 1, 2, 3 },
            Implemented = true,
        };

        /// <summary>
        /// AbsorbPool band: a finite pool that eats damage outright runs FIRST, ahead of any proportional
        /// diversion (Mana Barrier), so the pool is spent against the full hit. That is the order the two
        /// have run in at every site since they were wired by hand, and the two orders are NOT
        /// interchangeable - they leave different damage on the same hit and spend different Mana.
        /// </summary>
        public int MitigationOrder => DamageMitigationOrder.AbsorbPool;

        /// <summary>
        /// TRUE, and this is load-bearing rather than tidy. THE WARD OUTLIVES THE RANK THAT GRANTED IT: it
        /// is granted at cast time and then absorbs for its own 15 second window, and AbsorbWithSanguineWard
        /// applies no rank check at all - only an empty-pool early-out. The per-player hook cache is
        /// rank-filtered, so without this declaration a player who unlearned the ability (/abilities has no
        /// combat-state gate), swapped facet, or was caught by a wholesale rank sweep would drop out of the
        /// dispatch and their still-live ward would silently absorb NOTHING for the rest of its window.
        /// That is a combat-number change against the hand-wired behaviour, where the five sites called
        /// AbsorbWithSanguineWard by name regardless of rank.
        ///
        /// The alternative fix - clearing the ward on unlearn - is a DIFFERENT behaviour change (the pool
        /// would vanish rather than drain) and was rejected for that reason.
        /// </summary>
        public bool RunsWithoutLearnedRank => true;

        /// <summary>
        /// RANK-INDEPENDENT. <paramref name="rank"/> is deliberately unused and may be 0: the ward's size
        /// was fixed by rank at GRANT time and is carried in the pool itself, so nothing here reads rank,
        /// and a player who no longer holds the ability still drains a ward that is already up (see
        /// <see cref="RunsWithoutLearnedRank"/>).
        ///
        /// Unconditional by design - no attacker filter either. The ward absorbs PvP hits, self-damage and
        /// unattributed DoT ticks alike, so that it is never weaker on one damage path than on another,
        /// which is why this ignores the context's filtered Attacker and passes the RAW Source through
        /// (used only for the squelch check, and tolerating null). Do not add gates here to match Mana
        /// Barrier: the two gate differently on purpose.
        /// </summary>
        public void ModifyIncomingDamage(Player defender, int rank, PreWriteDamageContext context)
        {
            context.Damage = defender.AbsorbWithSanguineWard(context.Source, context.Damage);
        }

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
        /// (75/55/35% by rank, 1.0 for rank 0 / unlearned) instead.
        /// </summary>
        public static uint SelfCost(Player caster, uint damageBasis)
        {
            return SanguineWardMath.SelfCost(damageBasis, caster?.GetSanguineWardSelfCostFraction() ?? 1.0);
        }

        /// <summary>
        /// Reports the ABSORB fraction (SanguineWardMath.WardFraction, 60/90/120% by rank) as a percent.
        /// Skill is the rank's fraction, there is no legacy-skill rider, and Gear is the CLOTTING mod,
        /// added to that fraction on the same additive axis. WardFraction has no upper clamp of its own
        /// (the tunable is the authority, see its doc comment), so CapNote is always null.
        ///
        /// SECONDARY: the SELF-COST fraction (SanguineWardMath.SelfCostFraction, 75/55/35% by rank -
        /// unmoddable by ruling, matching Blood Price's health cost, so Affinity and Gear are always 0) is
        /// the second player-meaningful magnitude rank buys here - how much of the health basis the caster
        /// actually pays. Reported as a Secondary now that the contract supports one; mirrors
        /// Player.GetSanguineWardSelfCostFraction exactly.
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

            var selfCostFraction = SanguineWardMath.SelfCostFraction(rank,
                PropertyManager.GetDouble("class_ability_sanguine_ward_selfcost_r1").Item,
                PropertyManager.GetDouble("class_ability_sanguine_ward_selfcost_r2").Item,
                PropertyManager.GetDouble("class_ability_sanguine_ward_selfcost_r3").Item);

            var selfCostSecondary = new ClassAbilityReadout
            {
                HasValue = true,
                Skill = selfCostFraction * 100.0,
                Affinity = 0.0,
                Gear = 0.0,
                Effective = selfCostFraction * 100.0,
                Unit = "%",
                Label = "self cost",
                Per = null,
                CapNote = null,
            };

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
                Secondary = new List<ClassAbilityReadout> { selfCostSecondary },
            };
        }
    }
}
