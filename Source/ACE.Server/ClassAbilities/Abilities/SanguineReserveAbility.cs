using ACE.Server.EquipmentMods;
using ACE.Server.Managers;
using ACE.Server.WorldObjects;

namespace ACE.Server.ClassAbilities.Abilities
{
    /// <summary>
    /// Blood Mage T1 game-changer: every landed harmful Life Magic spell (Harm, Drain, Martyr's Hecatomb)
    /// grants a Blood Charge. Max 3/4/5 charges by rank; each charge adds +7% life-magic damage, so the full
    /// ramp is +21/28/35%. Charges decay after 15s with no life cast. PvE only. Deliberately unscaled - the
    /// stacks are its scaling (the Frenzy / Nether Rush precedent). BLOOD-MAGE-DESIGN.md sec 3, rev 4.
    ///
    /// WHERE THE MECHANIC LIVES. No hook interface: like the Enhanced-stat family this is read directly at
    /// the computation sites rather than dispatched from a combat event.
    ///  - the pool: Player_ClassAbilityBuffs.cs (bloodChargeStacks, TryGrantBloodCharge,
    ///    ApplyBloodChargeDamage) over <see cref="BloodChargeMath"/>;
    ///  - granted on a landed Harm (WorldObject_Magic.HandleCastSpell_Boost), a landed Drain
    ///    (HandleCastSpell_Transfer) and a landed life projectile (SpellProjectile.CalculateDamage);
    ///  - spent as damage at the first and third of those sites, through
    ///    Player.ApplyHarmClassAbilityDamage and Player.ApplyLifeProjectileClassAbilityDamage respectively.
    ///    The life-projectile side reads a value stamped once per cast by
    ///    Player.ApplyLifeProjectileBloodCharge, because Curse of Raven Fury's eight projectiles must all
    ///    carry the same answer.
    ///
    /// ONE CHARGE PER CAST, NOT PER HIT. Crimson Harvest's secondary drains and Curse of Raven Fury's seven
    /// untargeted projectiles are deliberately excluded from the grant: against a 3-5 stack cap a per-hit
    /// grant would fill the pool from one cast and delete the ramp this ability is.
    ///
    /// DRAIN GRANTS BUT DOES NOT SPEND. A charge ramp on Drain's damage would be invisible - its roll binds
    /// against spell.TransferCap on anything worth draining - and scaling the cap instead is the shape the
    /// design rejected when it took Drain off the life-vulnerability axis. Drain contributes the ramp and
    /// the Weakened Blood mark; Harm and Hecatomb are what the ramp pays off on.
    /// </summary>
    public class SanguineReserveAbility : IClassAbility, IPassiveStatAbility, IAbilityReadout
    {
        public ClassAbilityDefinition Definition { get; } = new ClassAbilityDefinition
        {
            Id = ClassAbilityId.SanguineReserve,
            AbilityClass = ClassAbilityClass.BloodMage,
            Tier = 1,
            Name = "sanguine_reserve",
            DisplayName = "Sanguine Reserve",
            Description = "Landing a harmful Life Magic spell grants a Blood Charge, up to 3/4/5 by rank. Each " +
                          "charge adds +7% Life Magic damage (up to +21/28/35%). Charges fade after 15 seconds " +
                          "with no Life Magic cast. Against monsters only.",
            MaxRank = 3,
            CostPerRank = new[] { 1, 2, 3 },
            Implemented = true,
        };

        /// <summary>
        /// Reports the PER-CHARGE life-magic damage rate. Skill = class_ability_bloodmage_charge_per_stack
        /// (BloodChargePerStack in Player_ClassAbilityBuffs.cs), a flat rate shared with Exsanguinate's
        /// burst multiplier and not rank-scaled, so it is identical at every owned rank. There is no
        /// affinity rider. Gear = the BLOOD CHARGE mod, which is added to that same per-charge rate inside
        /// the ramp (BloodChargeMath.DamageMultiplier), so the two sum in one unit exactly as the readout
        /// contract requires.
        ///
        /// PER-CHARGE, NOT THE WHOLE RAMP - Per is "/charge" and the caller multiplies mentally by the pool
        /// size. The stack CAP (3/4/5 by rank, see BloodChargeMath.StackCap) bounds how many charges the
        /// pool holds, not this rate, so it never reduces this readout and CapNote is always null.
        ///
        /// The null-conditional on the gear read is for the readout unit tests, which call this with a null
        /// Player because Player's static initializer cannot run under the test host; every live caller
        /// (/abilities list) passes session.Player.
        /// </summary>
        public ClassAbilityReadout GetReadout(Player player, int rank)
        {
            var perStack = PropertyManager.GetDouble("class_ability_bloodmage_charge_per_stack").Item;

            var gearPerStack = player?.GetEquippedModValue(EquipmentModId.BloodCharge) ?? 0.0;

            var skill = perStack * 100.0;
            var gear = gearPerStack * 100.0;

            return new ClassAbilityReadout
            {
                HasValue = true,
                Skill = skill,
                Affinity = 0.0,
                Gear = gear,
                Effective = skill + gear,
                Unit = "%",
                Label = "life dmg",
                Per = "/charge",
                CapNote = null,
            };
        }
    }
}
