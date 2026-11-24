using System;

using ACE.Entity.Enum.Properties;
using ACE.Server.WeaponMods;
using ACE.Server.WorldObjects;

namespace ACE.Server.Entity
{
    /// <summary>
    /// Pure helpers for the Cloak Mantle rear guard and the cloak proc amplifier. No world state is read except the
    /// amplifier property off the cloak passed in.
    /// </summary>
    public static class CloakMantleMath
    {
        /// <summary>
        /// The armor level a Mantle contributes against a rear-arc physical hit: itemLevel * armorPerLevel. 0.0 when
        /// there is no Mantle, the item has no positive level, the feature is off, the hit is PvP, or armorPerLevel is
        /// NaN, infinite or not positive.
        /// </summary>
        public static double RearGuardArmorLevel(bool hasMantle, int? itemLevel, double armorPerLevel, bool enabled, bool pvp)
        {
            if (!hasMantle || !enabled || pvp)
                return 0.0;

            if (itemLevel == null || itemLevel.Value <= 0)
                return 0.0;

            if (double.IsNaN(armorPerLevel) || double.IsInfinity(armorPerLevel) || armorPerLevel <= 0.0)
                return 0.0;

            return itemLevel.Value * armorPerLevel;
        }

        /// <summary>The damage multiplier for a rear-guard armor level; the same curve as the shieldless arcane row.</summary>
        public static float RearGuardMod(double armorLevel, float ignoreShieldMod)
        {
            return WeaponModCombat.ArcaneShieldMod(armorLevel, ignoreShieldMod);
        }

        /// <summary>
        /// 1 + CloakProcAmplifier when the item is a cloak, the value is set, finite and positive, and the hit is not
        /// PvP; otherwise 1.0. A null item returns 1.0.
        /// </summary>
        public static double AmplifierFactor(WorldObject cloak, bool pvp)
        {
            if (pvp || cloak == null || !Cloak.IsCloak(cloak))
                return 1.0;

            var amp = cloak.GetProperty(PropertyFloat.CloakProcAmplifier);

            if (amp == null || double.IsNaN(amp.Value) || double.IsInfinity(amp.Value) || amp.Value <= 0.0)
                return 1.0;

            return 1.0 + amp.Value;
        }

        public static int AmplifyReduction(int baseAmount, double factor)
        {
            return (int)Math.Round(baseAmount * factor, MidpointRounding.AwayFromZero);
        }
    }
}
