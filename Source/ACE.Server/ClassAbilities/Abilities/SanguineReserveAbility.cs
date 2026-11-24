using System.Collections.Generic;

using ACE.Entity.Enum;
using ACE.Server.EquipmentMods;
using ACE.Server.Managers;
using ACE.Server.WorldObjects;

namespace ACE.Server.ClassAbilities.Abilities
{
    /// <summary>
    /// Blood Mage T1 game-changer: every landed harmful Life Magic spell (Harm, Drain, Martyr's Hecatomb,
    /// Curse of Raven Fury, Bloodstone Bolt) grants a Blood Charge. Max 3/4/5 charges by rank; each charge
    /// adds +7% life-magic damage, so the full ramp is +21/28/35%. Charges decay after 15s with no life
    /// cast. PvE only. The stacks are its main scaling (the Frenzy / Mana Rush precedent); since the
    /// Blood Mage buff pass the PER-CHARGE value also carries a Creature Enchantment affinity multiplier
    /// (gear additive outside it), which flows into Exsanguinate's burst by design because the burst reads
    /// the same per-charge value. BLOOD-MAGE-DESIGN.md sec 3, rev 4.
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
    ///  - BLOODSTONE BOLT (since 2026-10-01) is a life spell of MetaSpellType Projectile, not
    ///    LifeProjectile, so it lands in the war/void branch of SpellProjectile.CalculateDamage. It is
    ///    matched by a spell-property predicate (BloodChargeMath.IsLifeHealthBolt), resolved once per cast by
    ///    the same ApplyLifeProjectileBloodCharge stamp, and both granted and spent at that branch through
    ///    Player.ApplyLifeHealthBoltBloodCharge - charge term only, since that branch already applies Blood
    ///    Price. It never exsanguinates (Exsanguinate is Hecatomb / Raven Fury only, ruling 2026-08-03).
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
            // Live text. Cap per rank: BloodChargeStackCap, Player_ClassAbilityBuffs.cs ~1241 (stack_cap_r1..r3); idle fade:
            // BloodChargeExpireSeconds, Player_ClassAbilityBuffs.cs ~1232; per-charge value: GetReadout below (SanguineReserveAbility.cs ~83).
            DescriptionTemplate = "Landing a harmful Life Magic spell gives you a Blood Charge. Each charge adds " +
                          "+{v:class_ability_bloodmage_charge_per_stack|pct}% Life Magic damage. Charges fade after " +
                          "{v:class_ability_bloodmage_charge_expire_seconds|num} seconds without a Life Magic cast.",
            RankTailTemplate = "up to {ranks:class_ability_bloodmage_charge_stack_cap_r|int} charges (+{fn:full_pool|pct}% when " +
                          "full)",
            // full_pool is the bonus of a FULL pool: BloodChargeMath.FullPoolBonus = DamageMultiplier(StackCap, per-charge value) - 1, the
            // ramp ApplyBloodChargeDamage applies (the per-charge value there is read through BloodChargeMath.PerChargeValue with
            // ClassAbilityText.Live). The text is per RANK, not per player, so it assumes the neutral Creature Enchantment affinity
            // and no Blood Charge gear; the mechanic multiplies the per-charge value by the caster's affinity.
            TextFormulas = new Dictionary<string, TextFormula>
            {
                ["full_pool"] = new TextFormula(
                    (values, rank) => BloodChargeMath.FullPoolBonus(values, rank),
                    "class_ability_bloodmage_charge_stack_cap_r1",
                    "class_ability_bloodmage_charge_stack_cap_r2",
                    "class_ability_bloodmage_charge_stack_cap_r3",
                    "class_ability_bloodmage_charge_per_stack"),
            },
            TunableSourceFiles = new[] { "WorldObjects/Player_ClassAbilityBuffs.cs", "ClassAbilities/BloodChargeMath.cs" },
            MaxRank = 3,
            CostPerRank = new[] { 1, 1, 1 },
            Implemented = true,
            AffinitySkill = Skill.CreatureEnchantment,
            Ceilings = new[]
            {
                new ClassAbilityCeiling { Id = "stacks", Bounds = CeilingBounds.Stacks, Source = CeilingSource.TunablePerRank, Key = "class_ability_bloodmage_charge_stack_cap_r", Label = "max blood charges", Format = "int", ZeroIsLimit = true },
            },
        };

        /// <summary>
        /// Reports the PER-CHARGE life-magic damage rate. Skill = class_ability_bloodmage_charge_per_stack, a
        /// flat rate shared with Exsanguinate's burst multiplier and not rank-scaled, so it is identical at
        /// every owned rank. Affinity = the amount the CREATURE ENCHANTMENT multiplier adds to it
        /// (perStack * multiplier - perStack), the same expression BloodChargePerStack in
        /// Player_ClassAbilityBuffs.cs applies through BloodChargeMath.PerChargeValue. Gear = the BLOOD
        /// CHARGE mod, which is added to the per-charge rate inside the ramp (BloodChargeMath.DamageMultiplier)
        /// and never multiplied, so the three sum in one unit exactly as the readout contract requires.
        ///
        /// PER-CHARGE, NOT THE WHOLE RAMP - Per is "/charge" and the caller multiplies mentally by the pool
        /// size. The stack CAP (3/4/5 by rank, see BloodChargeMath.StackCap) bounds how many charges the
        /// pool holds, not this rate, so it never reduces this readout and CapNote is always null.
        ///
        /// The null-conditionals on the affinity and gear reads are for the readout unit tests, which call
        /// this with a null Player because Player's static initializer cannot run under the test host; every
        /// live caller (/abilities list) passes session.Player. The affinity fallback is 1.0, the NEUTRAL
        /// factor, never 0.0.
        /// </summary>
        public ClassAbilityReadout GetReadout(Player player, int rank)
        {
            var perStack = BloodChargeMath.PerStack(ClassAbilityText.Live);

            var multiplier = player?.GetClassAbilityAffinityMultiplier(Skill.CreatureEnchantment) ?? 1.0;

            var gearPerStack = player?.GetEquippedModValue(EquipmentModId.BloodCharge) ?? 0.0;

            var skill = perStack * 100.0;
            var affinity = (BloodChargeMath.PerChargeValue(perStack, multiplier) - perStack) * 100.0;
            var gear = gearPerStack * 100.0;

            return new ClassAbilityReadout
            {
                HasValue = true,
                Skill = skill,
                Affinity = affinity,
                Gear = gear,
                Effective = skill + affinity + gear,
                Unit = "%",
                Label = "life dmg",
                Per = "/charge",
                CapNote = null,
                Ceilings = new[] { ClassAbilityCeilings.Read(Definition, "stacks", player?.GetBloodChargeStacks() ?? 0, rank, ClassAbilityText.Live) },
            };
        }
    }
}
