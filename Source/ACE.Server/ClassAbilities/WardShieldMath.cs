using System;

namespace ACE.Server.ClassAbilities
{
    /// <summary>
    /// Pure arithmetic for the WARD SHIELD (owner ruling 2026-10-24): while a Runic Ward or Sanguine Ward pool
    /// is up, the ward also acts as a shield of a flat armor level against frontal physical attacks from PvE
    /// attackers. Kept as a pure static for the same reason RunicWardMath is: Player's static initializer
    /// cannot run under the unit test host.
    ///
    /// HOW IT ENTERS THE SHIELD FACTOR (Creature.GetShieldMod), so the two branches there read as one rule:
    ///  - NO real shield equipped: the ward level joins Defensive Weapon's flat level on the UNCAPPED virtual
    ///    shield path (WeaponModCombat.ArcaneShieldMod). A shieldless defender's Shield skill is untrained, so
    ///    the Shield-skill cap would zero the ward on exactly the build that has no shield.
    ///  - A real shield equipped: the ward level is added AFTER the Shield-skill cap (so the cap bounds only
    ///    the shield's own level) and BEFORE the attacker's ignore-shield term (so a shield-cleaving weapon
    ///    cleaves the ward too, exactly as it cleaves the shield). Defensive Weapon's level joins in the same slot,
    ///    summed with the ward (DefensiveWeaponMath.AfterShieldCap).
    /// Either way the frontal cone and the phantom-weapon exemption GetShieldMod already applies to a shield
    /// apply to the ward unchanged.
    /// </summary>
    public static class WardShieldMath
    {
        /// <summary>
        /// The ward shield level from the two wards' individual levels (each 0 when that ward is down):
        /// the LARGER of the two, never the sum. A negative (mis-tuned) level counts as 0.
        /// </summary>
        public static double Level(double runicWardLevel, double sanguineWardLevel) =>
            Math.Max(Math.Max(0.0, runicWardLevel), Math.Max(0.0, sanguineWardLevel));

        /// <summary>The shield's frontal cone, in degrees - the same 180 degrees GetShieldMod applies to a real shield.</summary>
        public const double FrontalConeDegrees = 180.0;

        /// <summary>TRUE when <paramref name="angleDegrees"/> (Creature.GetAngle to the attacker) is inside the frontal cone.</summary>
        public static bool IsFrontal(double angleDegrees) => Math.Abs(angleDegrees) <= FrontalConeDegrees / 2.0;

        /// <summary>
        /// THE WHOLE WARD-SHIELD DECISION, pure so every branch is unit-testable (Player.GetWardShieldLevel only
        /// gathers the inputs). 0 unless at least one ward is up, the attacker is an enemy creature (a live
        /// non-player creature other than the defender - so PvP and self give 0), the attacker is in front, and
        /// class abilities are enabled; otherwise the larger of the levels of the wards that are up
        /// (<see cref="Level"/>), never the sum.
        /// </summary>
        public static double LevelFor(bool runicUp, bool sanguineUp, bool attackerIsEnemyCreature, bool frontal, bool enabled,
            double runicLevel, double sanguineLevel)
        {
            if (!runicUp && !sanguineUp)
                return 0.0;

            if (!attackerIsEnemyCreature || !frontal || !enabled)
                return 0.0;

            return Level(runicUp ? runicLevel : 0.0, sanguineUp ? sanguineLevel : 0.0);
        }

        /// <summary>
        /// The equipped-shield path's level after the ward: the shield's level ALREADY clamped to the
        /// Shield-skill cap, plus the ward level on top of it. The caller then multiplies by the attacker's
        /// ignore-shield term.
        /// </summary>
        public static double AfterShieldCap(double cappedShieldLevel, double wardLevel) =>
            cappedShieldLevel + Math.Max(0.0, wardLevel);
    }
}
