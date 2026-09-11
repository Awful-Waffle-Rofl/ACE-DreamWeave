using ACE.Server.EquipmentMods;
using ACE.Server.Managers;
using ACE.Server.WorldObjects;

namespace ACE.Server.ClassAbilities.Abilities
{
    /// <summary>
    /// Blood Mage T3: damaging spells of ANY school also cost health - each cast spends 3/4/5% of current
    /// health by rank and deals +8/16/24% damage. Below 20% current health the cast lands normally with no
    /// bonus and no health cost. PvE only. Deliberately unscaled: the health cost IS the brake, and the brake
    /// grows with the payoff rather than sitting flat. BLOOD-MAGE-DESIGN.md sec 3.
    ///
    /// This is the class's only school-agnostic damage multiplier, so it is the entry whose cross-class
    /// product needs sign-off; the design deliberately did NOT use it to reach the x2.0 late-game floor
    /// (Sanguine Reserve's per-charge value was raised instead, being class-locked and worthless to a
    /// splasher).
    ///
    /// WHERE THE MECHANIC LIVES. No hook interface. The health is charged ONCE PER CAST at
    /// Player_Magic.DoCastSpell_Inner (the CastingPreCheckStatus.Success branch, so a fizzle bills nothing),
    /// by Player.ApplyBloodPriceCost over <see cref="BloodPriceMath"/>. The resulting multiplier is stored
    /// and read at the damage sites: war and void through Player.GetClassAbilitySpellDamageMod, the two life
    /// projectiles through Player.ApplyLifeProjectileClassAbilityDamage and Harm through
    /// Player.ApplyHarmClassAbilityDamage. The three paths are disjoint, so the bonus is applied exactly
    /// once.
    ///
    /// THE 20% FLOOR IS ALL-OR-NOTHING AND RESOLVED ONCE. Below it a cast costs nothing and gains nothing,
    /// never one without the other - which is precisely why the multiplier is carried from cast time rather
    /// than re-derived at impact, since the payment itself moves current health across the floor.
    ///
    /// DRAIN IS EXCLUDED, and that is mechanical rather than a school judgement: its damage is bounded by
    /// spell.TransferCap, so the bonus would be unreachable and the health cost pure loss. See
    /// BloodPriceMath.SpellQualifies.
    /// </summary>
    public class BloodPriceAbility : IClassAbility, IPassiveStatAbility, IAbilityReadout
    {
        public ClassAbilityDefinition Definition { get; } = new ClassAbilityDefinition
        {
            Id = ClassAbilityId.BloodPrice,
            AbilityClass = ClassAbilityClass.BloodMage,
            Tier = 3,
            Name = "blood_price",
            DisplayName = "Blood Price",
            Description = "Your damaging spells of any school also cost health: each cast spends 3/4/5% of " +
                          "your current health by rank and deals +8/16/24% damage. Below 20% health the cast " +
                          "lands normally, with no bonus and no health cost. Against monsters only.",
            MaxRank = 3,
            CostPerRank = new[] { 1, 1, 1 },
            Implemented = true,
        };

        /// <summary>
        /// Reports BloodPriceMath.DamageBonus as a percent. Skill is the rank's bonus, there is no
        /// legacy-skill rider, and Gear is the BLOOD PRICE mod, added to that bonus on the same additive
        /// axis. The 20% current-health floor is an ALL-OR-NOTHING GATE on whether a cast qualifies at all
        /// (BloodPriceMath.Resolve), not a numeric clamp on this magnitude, so CapNote is never set for it.
        ///
        /// THIS LINE IS THE PAYOFF, NOT THE PRICE. The health cost (3/4/5% of current health) is unmoddable
        /// by ruling and is not part of this readout at any term.
        ///
        /// The null-conditional on the gear read is for the readout unit tests, which call this with a null
        /// Player because Player's static initializer cannot run under the test host; every live caller
        /// (/abilities list) passes session.Player.
        /// </summary>
        public ClassAbilityReadout GetReadout(Player player, int rank)
        {
            var gearBonus = player?.GetEquippedModValue(EquipmentModId.BloodPrice) ?? 0.0;

            var bonus = BloodPriceMath.DamageBonus(rank,
                PropertyManager.GetDouble("class_ability_bloodprice_damage_r1").Item,
                PropertyManager.GetDouble("class_ability_bloodprice_damage_r2").Item,
                PropertyManager.GetDouble("class_ability_bloodprice_damage_r3").Item);

            var skill = bonus * 100.0;
            var gear = gearBonus * 100.0;

            return new ClassAbilityReadout
            {
                HasValue = true,
                Skill = skill,
                Affinity = 0.0,
                Gear = gear,
                Effective = skill + gear,
                Unit = "%",
                Label = "spell dmg",
                Per = null,
                CapNote = null,
            };
        }
    }
}
