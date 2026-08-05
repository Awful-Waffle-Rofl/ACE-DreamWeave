using ACE.Server.EquipmentMods;
using ACE.Server.Managers;
using ACE.Server.WorldObjects;

namespace ACE.Server.ClassAbilities.Abilities
{
    /// <summary>
    /// Blood Mage T3 game-changer: a Martyr's Hecatomb or Curse of Raven Fury cast at a FULL Blood Charge
    /// pool consumes the ENTIRE pool and applies it at 3x its normal per-charge value (a full 5-charge pool
    /// becomes +105% instead of +35%), and against a target under Weakened Blood additionally ignores 25% of
    /// that target's HealthDrain resistance for that strike. Deliberately unscaled. This is the
    /// T3-builds-on-T1 pattern (Acid Proc from Poison Weapon, Double Volley from Multishot), so it inherits
    /// Sanguine Reserve's per-charge value: if the burst ever fails a power-assessor pass, the containment
    /// lever is the exsanguinate multiplier (3.0 -> 2.5), NOT the per-charge value. BLOOD-MAGE-DESIGN.md
    /// sec 3.
    ///
    /// THE SPELL LIST IS HECATOMB AND RAVEN FURY, NOT HARM AND HECATOMB. User ruling from live play,
    /// 2026-08-03: "Exsanguinate should be specifically for Hecatomb or Raven Fury." That supersedes sec 3's
    /// tier-table row, which still reads "Harm or Hecatomb" - both ends moved, Harm out and Raven Fury in.
    /// Harm continues to BUILD the pool through Sanguine Reserve; it simply no longer spends it.
    ///
    /// A FULL POOL IS THE ONLY GATE, AND THERE IS NO COOLDOWN. Second user ruling from the same live test:
    /// "It is not clear when hecatomb is exsanguinating vs gaining a blood charge. It seems random ... exsang
    /// when full charges and on cast of heca/raven - otherwise accrue a blood charge. Cannot accrue a blood
    /// charge in the same spell attack as exsang." The predecessor fired on any non-empty pool behind a 10s
    /// internal cooldown and then granted a charge on the same cast, so an identical Hecatomb could burst for
    /// +21% or +105% or not at all depending on an invisible clock, and the pool refilled the instant it
    /// emptied. Both halves of that are gone: the cap is the gate, and a bursting cast gains nothing.
    /// class_ability_exsanguinate_cooldown_seconds was deleted with the code that read it.
    ///
    /// WHERE THE MECHANIC LIVES. No hook interface. The firing decision, the burst and the resistance-ignore
    /// arithmetic are <see cref="ExsanguinateMath"/> (<see cref="BloodChargeCastOutcome"/> is the one value
    /// that decides burst-or-accrue); the pool itself is in Player_ClassAbilityBuffs.cs
    /// (ApplyBloodChargeDamage).
    ///
    /// IT IS WORTH NOTHING WITHOUT SANGUINE RESERVE, by design: with no charges held there is no pool to
    /// spend and the strike resolves as a normal one. That is the T3-builds-on-T1 pattern, not a bug.
    ///
    /// THE POOL IS SPENT BY THE CAST, NOT BY A STRIKE. Both qualifying spells are life projectiles, so the
    /// decision is made once, at cast time, in Player.ApplyLifeProjectileBloodCharge - Harm's site
    /// (Player.ApplyHarmClassAbilityDamage) hardcodes ineligible. Cast-time resolution is what lets the
    /// WHOLE Raven Fury ring carry the burst (user, 2026-08-03: "whole ring on raven fury + exsanguinate")
    /// while the pool is still consumed exactly once: eight projectiles reading the pool independently would
    /// give the burst to whichever landed first and an empty pool to the rest.
    ///
    /// THAT MAKES THIS A POWER CHANGE ON THE KIT'S FLAGGED RISK COLUMN. BLOOD-MAGE-DESIGN sec 8a names Raven
    /// Fury as an uncapped, pack-scaling AOE; every projectile of it now carries up to +105%. Deliberately
    /// unlimited pending live observation, on the user's instruction. A power-assessor pass belongs here -
    /// the containment lever, if one is needed, remains class_ability_exsanguinate_multiplier.
    ///
    /// The resistance-ignore half acts on the target's RESISTANCE only, never on the vulnerability term
    /// Weakened Blood contributes - see Creature.GetHealthDrainResistanceOnly for why folding the two
    /// together would invert the effect.
    /// </summary>
    public class ExsanguinateAbility : IClassAbility, IPassiveStatAbility, IAbilityReadout
    {
        public ClassAbilityDefinition Definition { get; } = new ClassAbilityDefinition
        {
            Id = ClassAbilityId.Exsanguinate,
            AbilityClass = ClassAbilityClass.BloodMage,
            Tier = 3,
            Name = "exsanguinate",
            DisplayName = "Exsanguinate",
            Description = "Casting Martyr's Hecatomb or Curse of Raven Fury with a FULL Blood Charge pool " +
                          "consumes the whole pool and applies it at three times its normal per-charge " +
                          "value (a full pool of 5 becomes +105% instead of +35%). Against a target under " +
                          "Weakened Blood it also ignores 25% of that target's health-drain resistance for " +
                          "the strike. On Curse of Raven Fury every projectile of the ring carries it. An " +
                          "exsanguinating cast gains no Blood Charge; any other life cast builds one " +
                          "instead. Harm still builds charges but never spends them.",
            MaxRank = 1,
            CostPerRank = new[] { 5 },
            Implemented = true,
        };

        /// <summary>
        /// Reports the burst bonus at a FULL Blood Charge pool, as a percent. CROSS-ABILITY DEPENDENCY:
        /// the number that varies is how many charges a full pool holds, and that size is SANGUINE
        /// RESERVE'S stack cap (BloodChargeMath.StackCap), not any rank of Exsanguinate itself -
        /// Exsanguinate has MaxRank 1 and no scaling of its own. Player.GetClassAbilityRank is the public
        /// cross-ability lookup used to read the caster's Sanguine Reserve rank here, exactly as the real
        /// burst site does (Player_ClassAbilityBuffs.cs, ApplyLifeProjectileBloodCharge /
        /// ApplyHarmClassAbilityDamage) by reading the same pool.
        ///
        /// A player with no Sanguine Reserve rank has no pool to burst - StackCap returns 0 and
        /// ExsanguinateMath.BurstMultiplier(0, ...) degrades to the neutral 1.0 multiplier - so this
        /// reports Skill 0 rather than a misleading number for an unreachable burst. No affinity rider. No
        /// cap on the readout itself - the containment lever (class_ability_exsanguinate_multiplier) is a
        /// tunable input, not a clamp applied after the fact.
        ///
        /// GEAR IS THE SANGUINATE MOD ALONE, AND THAT SCOPE IS DELIBERATE. Two different equipment mods
        /// reach this number: Sanguinate (this ability's own, raising the burst multiplier) and Blood
        /// Charge (Sanguine Reserve's, raising the per-charge value the burst multiplies). Only the first
        /// belongs on this line - the second is already reported on Sanguine Reserve's own /abilities line,
        /// and counting it here as well would show a player the same item twice. Skill is therefore the
        /// burst with NEITHER mod, and Gear is the delta Sanguinate alone adds, computed by calling the
        /// real helper twice rather than by restating its arithmetic.
        /// </summary>
        public ClassAbilityReadout GetReadout(Player player, int rank)
        {
            var reserveRank = player.GetClassAbilityRank(ClassAbilityId.SanguineReserve);

            var stackCap = BloodChargeMath.StackCap(reserveRank,
                PropertyManager.GetLong("class_ability_bloodmage_charge_stack_cap_r1").Item,
                PropertyManager.GetLong("class_ability_bloodmage_charge_stack_cap_r2").Item,
                PropertyManager.GetLong("class_ability_bloodmage_charge_stack_cap_r3").Item);

            var perStack = PropertyManager.GetDouble("class_ability_bloodmage_charge_per_stack").Item;
            var burstMultiplierTunable = PropertyManager.GetDouble("class_ability_exsanguinate_multiplier").Item;

            var gearBurst = player.GetEquippedModValue(EquipmentModId.Sanguinate);

            var burst = ExsanguinateMath.BurstMultiplier(stackCap, perStack, burstMultiplierTunable);
            var gearedBurst = ExsanguinateMath.BurstMultiplier(stackCap, perStack, burstMultiplierTunable, gearBurst);

            var skill = (burst - 1.0) * 100.0;
            var gear = (gearedBurst - burst) * 100.0;

            return new ClassAbilityReadout
            {
                HasValue = true,
                Skill = skill,
                Affinity = 0.0,
                Gear = gear,
                Effective = skill + gear,
                Unit = "%",
                Label = "burst dmg",
                Per = null,
                CapNote = null,
            };
        }
    }
}
