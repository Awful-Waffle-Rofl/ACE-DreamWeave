using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Server.Entity;
using ACE.Server.Managers;
using ACE.Server.Pvp.Rules;

namespace ACE.Server.WorldObjects
{
    partial class Player
    {
        /// <summary>
        /// Cloak Mantle rear guard: the damage multiplier a Mantle cloak gives a REAR-arc physical hit. Called from
        /// Creature.GetShieldMod, which hoists the rear arc here for a Player. 1.0 means no reduction: no attacker,
        /// a phantom weapon (ignores all armor and shields), no worn cloak, a cloak without PropertyBool.CloakRearGuard,
        /// the setting off, or a PvP attacker (CloakMantleMath.RearGuardArmorLevel gates the last three).
        /// </summary>
        public float GetCloakRearGuardMod(WorldObject attacker, WorldObject weapon)
        {
            if (attacker == null)
                return 1.0f;

            // phantom weapons ignore all armor and shields
            if (weapon != null && weapon.HasImbuedEffect(ImbuedEffectType.IgnoreAllArmor))
                return 1.0f;

            var armorLevel = GetCloakRearGuardArmorLevel(PvpClassifier.IsPvp(attacker, this));

            if (armorLevel <= 0.0)
                return 1.0f;

            return CloakMantleMath.RearGuardMod(armorLevel, attacker.GetIgnoreShieldMod(weapon));
        }

        /// <summary>
        /// The worn Mantle cloak's rear-guard armor level (CloakMantleMath.RearGuardArmorLevel), 0.0 for no worn cloak, a
        /// cloak without PropertyBool.CloakRearGuard, the setting off, or <paramref name="pvp"/>. The attacker-free half of
        /// <see cref="GetCloakRearGuardMod"/>, which composes through it; ProfileBuilder reads it for the PvE profile.
        /// </summary>
        public double GetCloakRearGuardArmorLevel(bool pvp)
        {
            // same null guard as Cloak.WornCloak: EquippedObjects can be absent on a half-built creature
            var cloak = EquippedObjects == null ? null : EquippedCloak;
            if (cloak == null)
                return 0.0;

            var hasMantle = cloak.GetProperty(PropertyBool.CloakRearGuard) == true;

            return CloakMantleMath.RearGuardArmorLevel(
                hasMantle,
                cloak.ItemLevel,
                PropertyManager.GetDouble("cloak_rear_guard_armor_per_level").Item,
                PropertyManager.GetBool("cloak_rear_guard_enabled").Item,
                pvp);
        }
    }
}
