using System;

using ACE.Entity.Enum.Properties;
using ACE.Server.WorldObjects;

namespace ACE.Server.Entity
{
    /// <summary>
    /// FORK CHANGE - hollow intensity. "Hollow" is an attacker ignoring the defender's magic armor (impenetrability
    /// and banes, flag IgnoreMagicArmor 66) and magic resist (life armor/imperil, protections, vulnerabilities and
    /// natural resistance, flag IgnoreMagicResist 65). Retail made it all-or-nothing; this makes it a fraction.
    ///
    /// THE COMPATIBILITY CONTRACT (owner rulings 2026-09-17):
    ///   - An object carrying a flag with no PropertyFloat.HollowIntensity (9012) resolves to exactly 1.0, and at
    ///     1.0 every scale helper returns the same literal the old code returned (0, 0.0f), never a product with
    ///     (1 - 1.0). Every existing hollow monster and weapon therefore behaves bit-identically.
    ///   - PvP keeps its scalar form: eff = scalar * intensity, then the old "eff != 1.0 ? x * (1 - eff) : 0". At
    ///     intensity 1.0, eff is the scalar itself, so PvP results are unchanged. Only the intensity is clamped.
    ///   - No PropertyManager read happens unless the caller says the hit is PvP: the scalar is passed as a delegate
    ///     that is invoked only on the PvP branch, so these helpers run in a test host with no seeded config.
    ///
    /// "pvp" is whatever each call site decided PvP meant before this change, and the sites do not all agree:
    /// Monster_Melee's player-defender path keys on the attacker being a Player, the shield and GetResistanceMod
    /// paths on attacker AND defender being Players, and the creature-defender body-part path never scales at all.
    /// Each site passes its own old condition so its results are preserved.
    /// </summary>
    public static class HollowMath
    {
        /// <summary>
        /// Absent or NaN reads as 1.0 (full hollow, the pre-intensity meaning of the bare flag); anything else is
        /// clamped to [0, 1].
        /// </summary>
        public static double ClampOrOne(double? intensity)
        {
            if (!intensity.HasValue || double.IsNaN(intensity.Value))
                return 1.0;

            return Math.Clamp(intensity.Value, 0.0, 1.0);
        }

        /// <summary>
        /// One object's contribution: 0 when it does not carry the flag, otherwise its clamped HollowIntensity.
        /// </summary>
        public static double One(WorldObject source, PropertyBool flag)
        {
            if (source == null || !(source.GetProperty(flag) ?? false))
                return 0.0;

            return ClampOrOne(source.HollowIntensity);
        }

        /// <summary>
        /// The intensity a hit actually uses: the MAX of the weapon's and the attacker's contributions, the same
        /// max-combine WorldObject.GetIgnoreShieldMod uses for IgnoreShield. 0 means not hollow at all. A flag-only
        /// source on either side therefore makes the hit fully hollow regardless of the other side's fraction.
        /// </summary>
        public static double Resolve(WorldObject weapon, WorldObject attacker, PropertyBool flag)
        {
            return Math.Max(One(weapon, flag), One(attacker, flag));
        }

        /// <summary>
        /// A float enchantment term (armor banes). Non-PvP: 0.0f at full, x * (1 - i) when partial, x when 0.
        /// PvP: today's scalar form with eff = scalar * i.
        /// </summary>
        public static float ScaleFloat(float enchantments, double intensity, bool pvp, Func<double> pvpScalar)
        {
            var i = ClampOrOne(intensity);

            if (pvp)
            {
                var eff = pvpScalar() * i;

                if (eff != 1.0)
                    return (float)(enchantments * (1.0 - eff));
                else
                    return 0.0f;
            }

            if (i >= 1.0)
                return 0.0f;

            if (i <= 0.0)
                return enchantments;

            return (float)(enchantments * (1.0 - i));
        }

        /// <summary>
        /// An int term rounded straight from double (life armor/imperil, and the rating-space protection and
        /// vulnerability in GetResistanceMod). Non-PvP: 0 at full, (int)Math.Round(x * (1 - i)) when partial,
        /// x when 0. PvP: today's scalar form with eff = scalar * i.
        /// </summary>
        public static int ScaleInt(int enchantments, double intensity, bool pvp, Func<double> pvpScalar)
        {
            var i = ClampOrOne(intensity);

            if (pvp)
            {
                var eff = pvpScalar() * i;

                if (eff != 1.0)
                    return (int)Math.Round(enchantments * (1.0 - eff));
                else
                    return 0;
            }

            if (i >= 1.0)
                return 0;

            if (i <= 0.0)
                return enchantments;

            return (int)Math.Round(enchantments * (1.0 - i));
        }

        /// <summary>
        /// An int armor-level term (impenetrability on an armor piece or a shield). Non-PvP is identical to
        /// <see cref="ScaleInt"/>. PvP rounds the FLOAT result of <see cref="ScaleFloat"/>, because the pre-intensity
        /// code rounded IgnoreMagicArmorScaled's float and a float round-trip can round differently from a double.
        /// </summary>
        public static int ScaleArmorInt(int enchantments, double intensity, bool pvp, Func<double> pvpScalar)
        {
            if (pvp)
                return (int)Math.Round(ScaleFloat(enchantments, intensity, true, pvpScalar));

            return ScaleInt(enchantments, intensity, false, null);
        }

        /// <summary>
        /// Whether GetResistanceMod takes its hollow early return (weaponResistanceMod * mark, ignoring every
        /// protection, vulnerability and natural resistance). Non-PvP: only at full intensity. PvP: when
        /// scalar * i == 1.0, which at i = 1.0 is the pre-intensity "scalar == 1.0" test.
        /// </summary>
        public static bool IsFullResistBypass(double intensity, bool pvp, Func<double> pvpScalar)
        {
            var i = ClampOrOne(intensity);

            if (i <= 0.0)
                return false;

            if (!pvp)
                return i >= 1.0;

            return pvpScalar() * i == 1.0;
        }
    }
}
