using System;
using System.Collections.Generic;

using ACE.DatLoader.Entity;
using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Server.Entity;

namespace ACE.Server.ThreadDungeons
{
    /// <summary>One stat axis the curve extrapolates, each with its own fitted per-level slope.</summary>
    public enum DungeonStatAxis
    {
        /// <summary>Best body-part DVal (melee max damage), and a wielded weapon's Damage.</summary>
        Damage,

        /// <summary>Best body-part BaseArmor.</summary>
        Armor,

        Strength,
        Endurance,
        Quickness,
        Coordination,
        Focus,
        Self,

        /// <summary>
        /// EFFECTIVE weapon attack skill (attribute term + InitLevel), every non-magic attack skill. Above
        /// <see cref="DungeonStatCurve.MeleeSoftenLevel"/> (400) its slope is multiplied by
        /// dynamic_dungeons_weapon_attack_curve_rate_above_400 (default 0.33).
        /// </summary>
        WeaponAttack,

        /// <summary>
        /// EFFECTIVE magic attack skill: War, Life, Void and Creature Enchantment. The curve itself grows unsoftened, but a
        /// Thread run creature's pre-enchantment skill is held at dynamic_dungeons_magic_attack_skill_cap (default 490) at
        /// runtime (owner ruling 2026-11-10: a top player, about 530 magic defense, resists about 77% of monster spells;
        /// see Creature.MagicAttackSkillCeiling). The ceiling never changes this axis' standards, only what the creature
        /// reads back.
        /// </summary>
        MagicAttack,

        /// <summary>
        /// EFFECTIVE melee defense. Softened above <see cref="DungeonStatCurve.DefenseSoftenLevel"/>, and above
        /// <see cref="DungeonStatCurve.MeleeSoftenLevel"/> (400) it takes dynamic_dungeons_melee_defense_curve_rate_above_400
        /// (default 0.19) of its full slope instead of the defense rate.
        /// </summary>
        MeleeDefense,

        /// <summary>EFFECTIVE missile defense. Softened above <see cref="DungeonStatCurve.DefenseSoftenLevel"/>.</summary>
        MissileDefense,

        /// <summary>
        /// EFFECTIVE magic defense: the retail curve, with its own rate above <see cref="DungeonStatCurve.DefenseSoftenLevel"/>
        /// (frozen there by default, owner ruling 2026-10-28).
        /// </summary>
        MagicDefense,
    }

    /// <summary>
    /// The natural stat curve above the top of the authored roster (owner ruling 2026-10-08, final): every
    /// monster stat - attributes, attack skills, defense skills, magic skills, melee damage and body armour -
    /// follows its own log-linear curve past the authored data instead of going flat, and very hard to
    /// impossible for a solo player at the top is intended. Health has its own curve
    /// (<see cref="DungeonHealthCurve"/>) and is deliberately not an axis here.
    ///
    /// <code>
    ///   Standard(M) = Standard(P) * e^(b * (M - P))        for every axis, M above the pivot P
    ///   D(M)        = D(P) * e^(d * b * (M - P))            melee damage above the pivot, d = 0.75
    ///   E(M)        = E(300) * e^(r * b * (M - 300))        EFFECTIVE melee/missile defense above monster level 300, r = 0.5
    ///   MagicD(L)   = 19.365 * L^0.5769                     retail fit, L up to 300
    ///   MagicD(L)   = MagicD(300) * (L / 300)^(0.5769 * m)  above 300, m = 0 (frozen at about 520)
    /// </code>
    ///
    /// WHY IT EXISTS. <see cref="DungeonBandStandard"/> measures a band's medians off the creatures it can
    /// really draw, and records its own known limitation: near the top of the ladder the sample is drawn from
    /// a small, fixed set of top-level creatures, so the standard FLATTENS. That was acceptable while 375 was
    /// both the gem ceiling and the top of the authored data. The 2026-10-08 run ceiling (DungeonGemSpec.
    /// MaxRunLevel, 500) put playable runs above any authored creature, and a flat standard there would make a
    /// +35 Mana Scarab buy nothing but health and reward. So the owner ruled that the stats extrapolate.
    ///
    /// THE PIVOT. P = floor(authored top / band high) - floor(375 / 1.15) = 326 on the shipped roster - is the
    /// highest gem level whose natural band [P, 1.15P] is still entirely inside the authored data. Its band
    /// standard is the last fully measured one, so it is the ANCHOR, and it is derived in code from the real
    /// band standard at P (DungeonPopulationBuilder.Build) - never hardcoded here. At or below P nothing in this
    /// type applies; the measured standard is used exactly as before.
    ///
    /// THE SLOPES ARE FITTED, NOT CHOSEN (every axis but magic defense, see below). Each b below is the per-level slope of an ordinary least-squares fit of
    /// ln(stat) against level over the fork's high-band bestiary - the 80 creatures wcid 1004000-1004079 (both
    /// roles) authored at levels 300 to 375, read from Content/sql and the local ace_world - in the 2026-10-08
    /// analysis (scratchpad curve/fit.py and final.py). A per-creature fit rather than a fit over the band
    /// medians, because the medians flatten in exactly the region the curve has to describe. Effective skills
    /// are fitted as attribute term + InitLevel, the quantity CreatureSkill.Base actually yields. Cross-check:
    /// from the measured level-326 standard (dmg 844, attack 810, melee D 820, missile D 745, war 680, armour
    /// 540, Str 410, End 410, Quick 360, Coord 410, Focus 580, Self 580, a 20-member sample) these slopes give,
    /// at 375, dmg 1386, attack 831, melee D 839, missile D 763, war 726, armour 592, Str 456, End 459, Quick
    /// 386, Coord 443, Focus 638, Self 641 (melee and missile D are softened from 300, so they run at half rate
    /// over the whole 326 to 375 stretch) - pinned by ThreadsRunCeilingTests.
    ///
    /// MAGIC DEFENSE IS THE RETAIL FIT (owner ruling 2026-10-22). Spell resist rolls a caster's raw magic skill
    /// against monster magic defense, while melee and missile attacks get a weapon-offense or accuracy
    /// multiplier, so the bestiary-fitted magic defense was oppressive for mages. From level 301 up, monster
    /// magic defense is an ABSOLUTE curve, <see cref="RetailMagicDefense"/>, and REPLACES the measured standard
    /// at every such level, including 301 to the pivot. Provenance: prod ace_world weenie_properties_skill
    /// (type 15) plus weenie_properties_attribute over the 1367 retail role-0 wcids listed in
    /// Content/events/axes/species/*.json, run 2026-10-22; upper outliers removed by an iterative residual
    /// Q3 + 1.5 * IQR fence (13 removed); power-law OLS of ln(effective magic D) on ln(level) for level >= 50,
    /// R2 0.728, effective = AttributeFormula term + InitLevel. It gives 520 at 300.
    ///
    /// MAGIC DEFENSE DOES NOT GROW ABOVE 300 (owner ruling 2026-10-28). Above monster level 300 the retail curve's
    /// log-slope is scaled by its OWN dial, dynamic_dungeons_magic_defense_curve_rate_above_300 (default 0: frozen
    /// at its level-300 value, about 520), not by the melee/missile dial. Reason: spell resist rolls the caster's
    /// raw magic skill against monster magic defense, and at the old shared rate of 0.5 (533 at 326, 555 at 375,
    /// 603 at 500) the top full-buffed War mage (599 skill) was resisted about 27% of the time at level 400 and
    /// 53% at 500 against the median; frozen, it is about 9%. Setting the dial to 0.5 reproduces the old curve
    /// exactly. The per-creature defense cap (median + dynamic_dungeons_defense_skill_cap_offset) is unchanged, so
    /// with a frozen median the magic cap is frozen too.
    ///
    /// THE DEFENSE SOFTENING. Above MONSTER level 300 (owner ruling 2026-10-22; it was 375) the effective
    /// MELEE and MISSILE defense standard grows at dynamic_dungeons_defense_curve_rate_above_375 (default 0.5;
    /// the key keeps its old name) of its fitted rate, continuously at 300. Magic defense has its own dial (see
    /// above). Because the pivot (326) is above 300, everything the curve extrapolates past the pivot is already
    /// softened.
    /// Effective defense is what decides whether an attack lands, so its rate is what makes the top of the
    /// ladder impossible rather than merely hard; halving it was the owner's call. Because the softening is on
    /// the EFFECTIVE value, a consumer that writes InitLevel must derive it as target effective minus the
    /// attribute term over the curve-raised attributes - otherwise the attributes' own slope would silently
    /// restore the full rate. <see cref="Extrapolate"/> does exactly that for the InitLevel medians.
    ///
    /// THE 400 KNEE (owner ruling 2026-11-17: "after 400 we need to further soften the slope of melee attack and
    /// defense ... roughly 15pts total for each of them between 400 and 500"). Above MONSTER level 400
    /// (<see cref="MeleeSoftenLevel"/>) two axes change, continuously at 400 and never falling:
    ///   WeaponAttack: per-level slope = WeaponAttackSlope * dynamic_dungeons_weapon_attack_curve_rate_above_400 (0.33);
    ///   MeleeDefense: per-level slope = MeleeDefenseSlope * dynamic_dungeons_melee_defense_curve_rate_above_400 (0.19),
    ///                 a fraction of the FULL slope that REPLACES the 0.5 defense rate for the stretch above 400.
    /// Missile defense and magic defense have no 400 knee. At the level-326 standards (attack 810, melee defense 820) the
    /// defaults raise attack from about 842 at 400 to about 857 at 500 and melee defense from about 849 to about 864,
    /// +15 each; the old curve gained about +46 attack and +41 melee defense. A rate of 0 is flat above 400, 1 on weapon attack is the unsoftened
    /// attack curve. The standard at level L is Standard(pivot) * Ratio(pivot, L), so both axes are
    /// exp(slope * (full + 0.5 * mid + rate400 * top)) with full/mid/top the path lengths below 300, 300-400 and above 400.
    ///
    /// THE DAMAGE SOFTENING (owner ruling 2026-10-29). Above the PIVOT (not 300: the curve only extrapolates
    /// damage past the pivot) the Damage axis - best body-part DVal and a wielded weapon's Damage, nothing else:
    /// no attribute, rating or spell - grows at dynamic_dungeons_damage_curve_rate_above_pivot (default 0.75) of
    /// its fitted slope, continuously at the pivot; 1.0 is the unsoftened curve exactly. Reason: at the full
    /// slope, physical per-hit damage grew x4.9 from level 300 to 410 and x14.7 by 500; 0.75 cuts it by about
    /// 19% at 410 and 36% at 500. The split level is the run's pivot, so <see cref="Ratio"/> takes it as an
    /// argument; the damage ceiling's prediction must pass the same rate and pivot as the live stamp, or its
    /// median drifts from the creatures it caps.
    ///
    /// SCOPE. Threads, and World Events with world_events_stat_curve on: WorldEventBandMemo.CurveStandardFor builds an
    /// <see cref="Anchored"/> the Threads way (at the Threads compiled default rates) and calls WithRetailMagicDefense from 301 up.
    /// DungeonBandStandard.Compute, BandUplift.Apply and the DungeonRosterSelector code they share are unchanged.
    ///
    /// PURE: no PropertyManager, no Creature, no logging. The one engine read is the skill formula table, and
    /// it arrives through a delegate (production: DungeonBandStandard.DefaultFormulaOf) so a test can supply
    /// its own.
    /// </summary>
    public static class DungeonStatCurve
    {
        // ---- fitted slopes (ln units per level) ----------------------------------------------------------
        // Provenance for every constant in this block: per-creature log-linear OLS over the 80 fork high-band
        // bestiary weenies (wcid 1004000-1004079, both roles, levels 300-375; Content/sql plus the local
        // ace_world), 2026-10-08 analysis. "Effective" = AttributeFormula term + authored InitLevel.

        /// <summary>Best body-part DVal: +1.017% per level.</summary>
        public const double DamageSlope = 0.01012;

        /// <summary>Best body-part BaseArmor: +0.188% per level.</summary>
        public const double ArmorSlope = 0.00188;

        public const double StrengthSlope = 0.00217;
        public const double EnduranceSlope = 0.00231;
        public const double QuicknessSlope = 0.00142;
        public const double CoordinationSlope = 0.00160;
        public const double FocusSlope = 0.00193;
        public const double SelfSlope = 0.00205;

        /// <summary>
        /// Effective weapon attack. FITTED ON HEAVY WEAPONS ONLY (the bestiary's dominant attack skill) and
        /// applied to every non-magic attack skill, the same "one attack standard for the band" reading
        /// DungeonCombatNormalizer.AttackSkillTarget already takes when it borrows the highest attack median.
        /// </summary>
        public const double WeaponAttackSlope = 0.00053;

        /// <summary>
        /// Effective magic attack. Fitted on War Magic and Life Magic, which came out identical; applied to Void
        /// Magic and Creature Enchantment as well, which the bestiary authors too rarely to fit on their own.
        /// </summary>
        public const double MagicAttackSlope = 0.00133;

        public const double MeleeDefenseSlope = 0.00094;
        public const double MissileDefenseSlope = 0.00099;

        // ---- retail magic defense (owner ruling 2026-10-22) ----------------------------------------------
        // Provenance: prod ace_world weenie_properties_skill (type 15, Magic Defense) plus
        // weenie_properties_attribute over the 1367 retail role-0 wcids listed in Content/events/axes/species/
        // *.json, run 2026-10-22. Upper outliers removed by an iterative residual Q3 + 1.5 * IQR fence (13
        // removed); power-law OLS of ln(effective magic D) on ln(level) for level >= 50, R2 0.728.
        // "Effective" = AttributeFormula term + InitLevel, the definition DungeonBandStandard uses.

        /// <summary>RetailMagicDefense(L) = Coefficient * L^Exponent for L up to <see cref="DefenseSoftenLevel"/>.</summary>
        public const double RetailMagicDefenseCoefficient = 19.365;

        /// <summary>The log-log slope of the retail magic defense fit.</summary>
        public const double RetailMagicDefenseExponent = 0.5769;

        /// <summary>
        /// The MONSTER level above which effective defense grows at the softened rate (owner ruling 2026-10-22:
        /// 300, down from 375), not the run's level: a stamped creature at 420 in a 375 run is softened from
        /// 300 up. It is also the level above which the retail magic defense curve replaces the measured
        /// magic defense standard.
        /// </summary>
        public const int DefenseSoftenLevel = 300;

        /// <summary>
        /// The compiled default of dynamic_dungeons_defense_curve_rate_above_375 (owner ruling 2026-10-08). Since
        /// 2026-10-28 it governs melee and missile defense only.
        /// </summary>
        public const double DefaultDefenseRateAbove375 = 0.5;

        /// <summary>
        /// The compiled default of dynamic_dungeons_magic_defense_curve_rate_above_300 (owner ruling 2026-10-28):
        /// 0, monster magic defense frozen at its level-300 retail value above 300. 0.5 reproduces the curve the
        /// shared dial gave before the split.
        /// </summary>
        public const double DefaultMagicDefenseRateAbove300 = 0.0;

        /// <summary>
        /// The compiled default of dynamic_dungeons_damage_curve_rate_above_pivot (owner ruling 2026-10-29): the Damage
        /// axis grows at 0.75 of its fitted slope above the pivot. 1.0 reproduces the curve before the dial existed.
        /// </summary>
        public const double DefaultDamageRateAbovePivot = 0.75;

        /// <summary>
        /// The MONSTER level above which WEAPON ATTACK and MELEE DEFENSE take their own, much flatter rate (owner
        /// ruling 2026-11-17: "after 400 we need to further soften the slope of melee attack and defense ... roughly
        /// 15 points total for each of them between 400 and 500"). Below it nothing differs from the 300 / pivot
        /// curve; MISSILE defense and MAGIC defense never use this knee.
        /// </summary>
        public const int MeleeSoftenLevel = 400;

        /// <summary>
        /// The compiled default of dynamic_dungeons_weapon_attack_curve_rate_above_400: the WeaponAttack axis grows at
        /// 0.33 of its fitted slope above <see cref="MeleeSoftenLevel"/>, which lifts the level-400 standard about 15
        /// points by level 500 (ln(857/842) / (0.00053 * 100) = 0.333). 1.0 is the unsoftened weapon attack curve.
        /// </summary>
        public const double DefaultWeaponAttackRateAbove400 = 0.33;

        /// <summary>
        /// The compiled default of dynamic_dungeons_melee_defense_curve_rate_above_400: the MeleeDefense axis grows at
        /// 0.19 of its FULL fitted slope above <see cref="MeleeSoftenLevel"/> (it replaces the 0.5 defense rate for that
        /// stretch, it is not multiplied by it), about 15 points from 400 to 500 (ln(864/849) / (0.00094 * 100) = 0.186).
        /// </summary>
        public const double DefaultMeleeDefenseRateAbove400 = 0.19;

        /// <summary>The fitted slope for <paramref name="axis"/>, before any softening.</summary>
        /// <remarks>
        /// Returns 0 for <see cref="DungeonStatAxis.MagicDefense"/>: that axis is the retail power law, handled
        /// inside <see cref="Ratio"/> and <see cref="RetailMagicDefense"/>, so a caller must never multiply by
        /// Slope(MagicDefense).
        /// </remarks>
        public static double Slope(DungeonStatAxis axis)
        {
            switch (axis)
            {
                case DungeonStatAxis.Damage: return DamageSlope;
                case DungeonStatAxis.Armor: return ArmorSlope;
                case DungeonStatAxis.Strength: return StrengthSlope;
                case DungeonStatAxis.Endurance: return EnduranceSlope;
                case DungeonStatAxis.Quickness: return QuicknessSlope;
                case DungeonStatAxis.Coordination: return CoordinationSlope;
                case DungeonStatAxis.Focus: return FocusSlope;
                case DungeonStatAxis.Self: return SelfSlope;
                case DungeonStatAxis.WeaponAttack: return WeaponAttackSlope;
                case DungeonStatAxis.MagicAttack: return MagicAttackSlope;
                case DungeonStatAxis.MeleeDefense: return MeleeDefenseSlope;
                case DungeonStatAxis.MissileDefense: return MissileDefenseSlope;
                default: return 0.0; // MagicDefense is a power law (RetailMagicDefense), not log-linear.
            }
        }

        /// <summary>
        /// The retail magic defense standard (EFFECTIVE) at monster <paramref name="level"/>:
        /// 19.365 * L^0.5769 up to <see cref="DefenseSoftenLevel"/>, and above it the value at the soften level
        /// times (L / soften)^(0.5769 * rate), i.e. the power law's log-slope scaled by the sanitized
        /// <paramref name="magicDefenseRate"/> (the MAGIC dial, never the melee/missile one; 0 freezes it at the
        /// soften level). Absolute: it does not depend on any measured standard.
        /// </summary>
        public static double RetailMagicDefense(int level, double magicDefenseRate)
        {
            if (level <= 0)
                return 0.0;

            var rate = SanitizeMagicDefenseRate(magicDefenseRate);
            var start = (double)DefenseSoftenLevel;

            if (level <= DefenseSoftenLevel)
                return RetailMagicDefenseCoefficient * Math.Pow(level, RetailMagicDefenseExponent);

            return RetailMagicDefenseCoefficient * Math.Pow(start, RetailMagicDefenseExponent)
                * Math.Pow(level / start, RetailMagicDefenseExponent * rate);
        }

        /// <summary><see cref="RetailMagicDefense"/> rounded half away from zero, never below 1 for a positive level.</summary>
        private static uint RetailMagicDefenseRounded(int level, double magicDefenseRate)
            => (uint)Math.Clamp(Math.Round(RetailMagicDefense(level, magicDefenseRate), MidpointRounding.AwayFromZero), 1, uint.MaxValue);

        /// <summary>True for the three EFFECTIVE defense axes, the only ones the softening touches.</summary>
        public static bool IsDefense(DungeonStatAxis axis)
            => axis == DungeonStatAxis.MeleeDefense || axis == DungeonStatAxis.MissileDefense || axis == DungeonStatAxis.MagicDefense;

        /// <summary>
        /// Sanitizes the softening factor into [0, 1]: NaN or Infinity reads as the default; a negative value
        /// reads as 0 (defense frozen at its 300 value - a falling defense curve is the one reading an admin
        /// cannot have meant); anything above 1 reads as 1 (the unsoftened curve - "softening" that STEEPENS is
        /// not softening). Pure, so the spawner's reader can warn against the same arithmetic.
        /// </summary>
        public static double SanitizeDefenseRate(double value)
        {
            if (double.IsNaN(value) || double.IsInfinity(value))
                return DefaultDefenseRateAbove375;

            return Math.Clamp(value, 0.0, 1.0);
        }

        /// <summary>
        /// <see cref="SanitizeDefenseRate"/>'s rule for the MAGIC dial: the same [0, 1] range, but NaN or Infinity
        /// reads as <see cref="DefaultMagicDefenseRateAbove300"/>, its own default.
        /// </summary>
        public static double SanitizeMagicDefenseRate(double value)
        {
            if (double.IsNaN(value) || double.IsInfinity(value))
                return DefaultMagicDefenseRateAbove300;

            return Math.Clamp(value, 0.0, 1.0);
        }

        /// <summary>
        /// The same [0, 1] rule for the WEAPON ATTACK dial above <see cref="MeleeSoftenLevel"/>: NaN or Infinity reads as
        /// <see cref="DefaultWeaponAttackRateAbove400"/>, a negative value as 0 (flat above 400), above 1 as 1.
        /// </summary>
        public static double SanitizeWeaponAttackRate(double value)
        {
            if (double.IsNaN(value) || double.IsInfinity(value))
                return DefaultWeaponAttackRateAbove400;

            return Math.Clamp(value, 0.0, 1.0);
        }

        /// <summary>
        /// The same [0, 1] rule for the MELEE DEFENSE dial above <see cref="MeleeSoftenLevel"/>: NaN or Infinity reads as
        /// <see cref="DefaultMeleeDefenseRateAbove400"/>, a negative value as 0 (flat above 400), above 1 as 1.
        /// </summary>
        public static double SanitizeMeleeDefenseRate(double value)
        {
            if (double.IsNaN(value) || double.IsInfinity(value))
                return DefaultMeleeDefenseRateAbove400;

            return Math.Clamp(value, 0.0, 1.0);
        }

        /// <summary>The length of [lo, hi] that lies inside [from, to] (to may be int.MaxValue for "no upper bound").</summary>
        private static int Overlap(int lo, int hi, int from, int to)
            => Math.Max(0, Math.Min(hi, to) - Math.Max(lo, from));

        /// <summary>
        /// The same [0, 1] rule for the DAMAGE dial: NaN or Infinity reads as <see cref="DefaultDamageRateAbovePivot"/>,
        /// a negative value as 0 (damage frozen at the pivot), above 1 as 1 (the unsoftened curve).
        /// </summary>
        public static double SanitizeDamageRate(double value)
        {
            if (double.IsNaN(value) || double.IsInfinity(value))
                return DefaultDamageRateAbovePivot;

            return Math.Clamp(value, 0.0, 1.0);
        }

        /// <summary>
        /// The curve's multiplier between two levels on one axis: Standard(<paramref name="toLevel"/>) /
        /// Standard(<paramref name="fromLevel"/>). Anchor-free, so it is also how a stamped creature is scaled
        /// from its authored level to its stamp ("preserving authored spread": every creature at the same raw
        /// and stamp levels is multiplied by the same factor, so the order within a pack survives).
        ///
        /// The exponent is the integral of the axis slope from one level to the other, with a melee or missile
        /// defense axis's slope multiplied by <paramref name="defenseRate"/> wherever the path lies above
        /// <see cref="DefenseSoftenLevel"/>. That makes it continuous at 300 by construction, and makes
        /// Ratio(a, c) == Ratio(a, b) * Ratio(b, c) for any b. Below the pivot the same log-linear line is simply
        /// extended backward; that only matters for a creature authored below the pivot, and only as a ratio.
        /// The magic defense axis is the ratio of two <see cref="RetailMagicDefense"/> values at
        /// <paramref name="magicDefenseRate"/>. The Damage axis's slope is multiplied by <paramref name="damageRate"/>
        /// wherever the path lies above <paramref name="damagePivot"/> (the run's stat-curve pivot; 0 or less means no
        /// pivot, so no damage softening). The WeaponAttack axis's slope is multiplied by
        /// <paramref name="weaponAttackRateAbove400"/> wherever the path lies above <see cref="MeleeSoftenLevel"/>, and the
        /// MeleeDefense axis's slope is multiplied by <paramref name="meleeDefenseRateAbove400"/> there INSTEAD of
        /// <paramref name="defenseRate"/> (missile and magic defense never use the 400 knee). Every rate is required so no
        /// caller can hand one axis's dial to another by omission: a World Events caller that forgot the 400 rates would
        /// silently get the Threads knee. World Events keeps its pre-knee curve by passing 1.0 for weapon attack (the
        /// unsoftened slope) and its own defense rate (0.5) for melee defense, so its output above 400 is unchanged.
        /// </summary>
        public static double Ratio(DungeonStatAxis axis, int fromLevel, int toLevel, double defenseRate, double magicDefenseRate,
            double damageRate, int damagePivot, double weaponAttackRateAbove400, double meleeDefenseRateAbove400)
        {
            if (fromLevel == toLevel)
                return 1.0;

            if (axis == DungeonStatAxis.MagicDefense)
            {
                // A non-positive level has no curve value: the neutral ratio, never 0 (which would zero a magic defense).
                if (fromLevel <= 0 || toLevel <= 0)
                    return 1.0;

                return RetailMagicDefense(toLevel, magicDefenseRate) / RetailMagicDefense(fromLevel, magicDefenseRate);
            }

            var lo = Math.Min(fromLevel, toLevel);
            var hi = Math.Max(fromLevel, toLevel);
            var slope = Slope(axis);
            double exponent;

            if (axis == DungeonStatAxis.MeleeDefense)
            {
                var rate = SanitizeDefenseRate(defenseRate);
                var rate400 = SanitizeMeleeDefenseRate(meleeDefenseRateAbove400);
                var full = Overlap(lo, hi, int.MinValue, DefenseSoftenLevel);
                var mid = Overlap(lo, hi, DefenseSoftenLevel, MeleeSoftenLevel);
                var top = Overlap(lo, hi, MeleeSoftenLevel, int.MaxValue);
                exponent = slope * (full + rate * mid + rate400 * top);
            }
            else if (IsDefense(axis))
            {
                var rate = SanitizeDefenseRate(defenseRate);
                var below = Math.Max(0, Math.Min(hi, DefenseSoftenLevel) - lo);
                var above = Math.Max(0, hi - Math.Max(lo, DefenseSoftenLevel));
                exponent = slope * (below + rate * above);
            }
            else if (axis == DungeonStatAxis.WeaponAttack)
            {
                var rate = SanitizeWeaponAttackRate(weaponAttackRateAbove400);
                var below = Overlap(lo, hi, int.MinValue, MeleeSoftenLevel);
                var above = Overlap(lo, hi, MeleeSoftenLevel, int.MaxValue);
                exponent = slope * (below + rate * above);
            }
            else if (axis == DungeonStatAxis.Damage && damagePivot > 0)
            {
                var rate = SanitizeDamageRate(damageRate);
                var below = Math.Max(0, Math.Min(hi, damagePivot) - lo);
                var above = Math.Max(0, hi - Math.Max(lo, damagePivot));
                exponent = slope * (below + rate * above);
            }
            else
            {
                exponent = slope * (hi - lo);
            }

            return Math.Exp(toLevel > fromLevel ? exponent : -exponent);
        }

        /// <summary>The axis an attack or defense skill's EFFECTIVE value follows, or null for a skill the curve does not move.</summary>
        public static DungeonStatAxis? AxisOf(Skill skill)
        {
            switch (skill)
            {
                case Skill.MeleeDefense: return DungeonStatAxis.MeleeDefense;
                case Skill.MissileDefense: return DungeonStatAxis.MissileDefense;
                case Skill.MagicDefense: return DungeonStatAxis.MagicDefense;
                case Skill.WarMagic:
                case Skill.LifeMagic:
                case Skill.VoidMagic:
                case Skill.CreatureEnchantment:
                    return DungeonStatAxis.MagicAttack;
            }

            return Array.IndexOf(DungeonCombatNormalizer.AttackSkills, skill) >= 0 ? DungeonStatAxis.WeaponAttack : (DungeonStatAxis?)null;
        }

        /// <summary>The axis a primary attribute follows, or null for Undef.</summary>
        public static DungeonStatAxis? AxisOf(PropertyAttribute attribute)
        {
            switch (attribute)
            {
                case PropertyAttribute.Strength: return DungeonStatAxis.Strength;
                case PropertyAttribute.Endurance: return DungeonStatAxis.Endurance;
                case PropertyAttribute.Quickness: return DungeonStatAxis.Quickness;
                case PropertyAttribute.Coordination: return DungeonStatAxis.Coordination;
                case PropertyAttribute.Focus: return DungeonStatAxis.Focus;
                case PropertyAttribute.Self: return DungeonStatAxis.Self;
                default: return null;
            }
        }

        /// <summary>
        /// <paramref name="value"/> times <paramref name="ratio"/>, rounded half away from zero and saturated
        /// into uint. A non-zero value never rounds to 0, so "no authored data" (0) stays distinguishable from
        /// a tiny one - the convention every DungeonStatProfile axis relies on.
        /// </summary>
        public static uint Scale(uint value, double ratio)
        {
            if (value == 0 || double.IsNaN(ratio) || double.IsInfinity(ratio) || ratio <= 0)
                return value;

            return (uint)Math.Clamp(Math.Round(value * ratio, MidpointRounding.AwayFromZero), 1, uint.MaxValue);
        }

        /// <summary>
        /// The InitLevel that puts a skill's effective value on <paramref name="targetEffective"/> given the
        /// (curve-raised) attribute term <paramref name="fixedTerm"/>, floored at 0. This is the line that keeps
        /// the defense softening honest: the attributes climb on their OWN slopes, and an InitLevel left alone
        /// (or scaled by the defense ratio) would let that climb carry the effective value past its softened
        /// target. It may therefore LOWER an InitLevel, which is correct - the effective value is the standard.
        /// </summary>
        public static uint InitFor(uint targetEffective, uint fixedTerm)
            => targetEffective > fixedTerm ? targetEffective - fixedTerm : 0u;

        /// <summary>
        /// The band standard at <paramref name="level"/>, extrapolated from <paramref name="anchor"/> (the
        /// measured standard at <paramref name="pivot"/>). Returns the anchor itself at or below the pivot, and
        /// for an empty or null anchor (the "normalize nothing" no-op stays a no-op).
        ///
        /// Per field:
        ///   - MaxBodyDamage, MaxBaseArmor, every attribute median and every EFFECTIVE skill median: the anchor
        ///     value times <see cref="Ratio"/> on its axis (defense softened above 300; magic defense is the retail curve);
        ///   - the InitLevel medians (SkillMedians) of every attack and defense skill: re-derived so a creature
        ///     SET to this standard - attributes to the attribute medians, InitLevel to this median, which is what
        ///     boss normalization does - lands on the anchor's attribute-term-plus-median carried up the same
        ///     curve: InitLevel(M) = round(effective(P) * ratio) - attributeTerm(attributes(M)), floored at 0;
        ///   - every other skill's InitLevel median (Run, Deception, ...), the spell tier, the health regen rate,
        ///     the sample count and the sample low ratio: unchanged. None of them has a fitted slope, and the
        ///     spell tier is already capped by the spell table.
        ///
        /// <paramref name="formulaOf"/> is consulted only when the anchor carries attribute medians, so a
        /// standard with none never touches the dat-backed formula table. A skill with no formula row has no
        /// attribute term and is scaled as InitLevel alone.
        /// </summary>
        public static DungeonBandStandard Extrapolate(DungeonBandStandard anchor, int pivot, int level, double defenseRate,
            double magicDefenseRate, double damageRate, double weaponAttackRateAbove400, double meleeDefenseRateAbove400,
            Func<Skill, SkillFormula> formulaOf = null)
            => anchor == null || anchor.IsEmpty || pivot <= 0 || level <= pivot
                ? anchor ?? DungeonBandStandard.Empty
                : Carry(anchor, pivot, level, pivot, defenseRate, magicDefenseRate, damageRate, weaponAttackRateAbove400,
                    meleeDefenseRateAbove400, formulaOf);

        /// <summary>
        /// <paramref name="from"/> (a band standard measured or carried at <paramref name="fromLevel"/>) carried to
        /// <paramref name="toLevel"/> by the same per-field rules as <see cref="Extrapolate"/>, which is the special case
        /// fromLevel == damagePivot. <paramref name="damagePivot"/> is where the damage curve's slope changes (see
        /// <see cref="Ratio"/>); a carry that starts below it (fromLevel &lt; damagePivot) is therefore allowed. Returns
        /// <paramref name="from"/> (or Empty) for a null or empty input, fromLevel == toLevel, or either level &lt;= 0.
        /// </summary>
        public static DungeonBandStandard Carry(DungeonBandStandard from, int fromLevel, int toLevel, int damagePivot, double defenseRate,
            double magicDefenseRate, double damageRate, double weaponAttackRateAbove400, double meleeDefenseRateAbove400,
            Func<Skill, SkillFormula> formulaOf = null)
        {
            var anchor = from;

            if (anchor == null || anchor.IsEmpty || fromLevel == toLevel || fromLevel <= 0 || toLevel <= 0)
                return anchor ?? DungeonBandStandard.Empty;

            double R(DungeonStatAxis axis) => Ratio(axis, fromLevel, toLevel, defenseRate, magicDefenseRate, damageRate, damagePivot,
                weaponAttackRateAbove400, meleeDefenseRateAbove400);

            var retailMagic = toLevel > DefenseSoftenLevel;

            var attributes = new Dictionary<PropertyAttribute, uint>(anchor.AttributeMedians.Count);

            foreach (var kvp in anchor.AttributeMedians)
            {
                var axis = AxisOf(kvp.Key);
                attributes[kvp.Key] = axis.HasValue ? Scale(kvp.Value, R(axis.Value)) : kvp.Value;
            }

            var effective = new Dictionary<Skill, uint>(anchor.EffectiveSkillMedians.Count);

            foreach (var kvp in anchor.EffectiveSkillMedians)
            {
                var axis = AxisOf(kvp.Key);

                // Magic defense above the soften level is the ABSOLUTE retail curve, not anchor x ratio. "0 = no
                // data" stays 0: an anchor with no magic defense median gets none.
                if (kvp.Key == Skill.MagicDefense && kvp.Value != 0 && retailMagic)
                    effective[kvp.Key] = RetailMagicDefenseRounded(toLevel, magicDefenseRate);
                else
                    effective[kvp.Key] = axis.HasValue ? Scale(kvp.Value, R(axis.Value)) : kvp.Value;
            }

            var hasAttributes = anchor.AttributeMedians.Count > 0;
            formulaOf ??= DungeonBandStandard.DefaultFormulaOf;

            uint AttributeTerm(Skill skill, IReadOnlyDictionary<PropertyAttribute, uint> attrs)
                => hasAttributes ? AttributeFormula.Compute(formulaOf(skill), a => attrs.TryGetValue(a, out var v) ? v : 0u) : 0u;

            var inits = new Dictionary<Skill, uint>(anchor.SkillMedians.Count);

            foreach (var kvp in anchor.SkillMedians)
            {
                var axis = AxisOf(kvp.Key);

                if (!axis.HasValue || kvp.Value == 0)
                {
                    inits[kvp.Key] = kvp.Value;
                    continue;
                }

                if (kvp.Key == Skill.MagicDefense && retailMagic)
                {
                    inits[kvp.Key] = InitFor(RetailMagicDefenseRounded(toLevel, magicDefenseRate), AttributeTerm(kvp.Key, attributes));
                    continue;
                }

                var anchorEffective = AttributeTerm(kvp.Key, anchor.AttributeMedians) + kvp.Value;
                var target = Scale(anchorEffective, R(axis.Value));

                inits[kvp.Key] = InitFor(target, AttributeTerm(kvp.Key, attributes));
            }

            return DungeonBandStandard.Create(inits,
                Scale(anchor.MaxBodyDamage, R(DungeonStatAxis.Damage)),
                Scale(anchor.MaxBaseArmor, R(DungeonStatAxis.Armor)),
                anchor.SpellTier, anchor.SampleCount, anchor.SampleLowRatio, anchor.HealthRate, attributes, effective, anchor.CritResistRatingMedian);
        }

        /// <summary>
        /// <paramref name="standard"/> with its MAGIC DEFENSE medians replaced by the retail curve at
        /// <paramref name="level"/> (owner ruling 2026-10-22): the EFFECTIVE median becomes the rounded
        /// <see cref="RetailMagicDefense"/>, and the InitLevel median becomes InitFor(that, attribute term over
        /// THIS standard's attribute medians). Every other field is carried over unchanged. Returns
        /// <paramref name="standard"/> itself at or below <see cref="DefenseSoftenLevel"/>, when it is null or
        /// empty, or when it carries no magic defense data (the "0 = no data" convention: a median that is
        /// absent or 0 stays absent). <paramref name="magicDefenseRate"/> is the MAGIC dial
        /// (dynamic_dungeons_magic_defense_curve_rate_above_300), never the melee/missile one.
        /// </summary>
        public static DungeonBandStandard WithRetailMagicDefense(DungeonBandStandard standard, int level, double magicDefenseRate,
            Func<Skill, SkillFormula> formulaOf = null)
        {
            if (standard == null || standard.IsEmpty || level <= DefenseSoftenLevel)
                return standard ?? DungeonBandStandard.Empty;

            var hasEffective = standard.EffectiveSkillMedianFor(Skill.MagicDefense) != 0;
            var hasInit = standard.MedianFor(Skill.MagicDefense) != 0;

            if (!hasEffective && !hasInit)
                return standard;

            var target = RetailMagicDefenseRounded(level, magicDefenseRate);
            var effective = new Dictionary<Skill, uint>(standard.EffectiveSkillMedians);
            var inits = new Dictionary<Skill, uint>(standard.SkillMedians);

            if (hasEffective)
                effective[Skill.MagicDefense] = target;

            if (hasInit)
            {
                uint term = 0u;

                if (standard.AttributeMedians.Count > 0)
                {
                    formulaOf ??= DungeonBandStandard.DefaultFormulaOf;
                    var attributes = standard.AttributeMedians;
                    term = AttributeFormula.Compute(formulaOf(Skill.MagicDefense), a => attributes.TryGetValue(a, out var v) ? v : 0u);
                }

                inits[Skill.MagicDefense] = InitFor(target, term);
            }

            return DungeonBandStandard.Create(inits, standard.MaxBodyDamage, standard.MaxBaseArmor, standard.SpellTier,
                standard.SampleCount, standard.SampleLowRatio, standard.HealthRate, standard.AttributeMedians, effective, standard.CritResistRatingMedian);
        }

        /// <summary>
        /// One run's curve: the measured anchor standard, its pivot, and the resolved softening factor. Carried
        /// on the plan (DungeonSpawnPlan.StatCurve) so the spawner can ask for the standard at ANY level - a
        /// stamped creature's own level, for its defense cap - and for the scaling ratio between two levels,
        /// without re-deriving any of it.
        ///
        /// Immutable apart from a memo of the standards already extrapolated, which is a pure function of the
        /// level and is guarded by a lock: a run's placement steps run one at a time on its landblock, but the
        /// plan object is not otherwise promised to one thread, and the memo is cheap to guard.
        /// </summary>
        public sealed class Anchored
        {
            private readonly Func<Skill, SkillFormula> formulaOf;
            private readonly Dictionary<int, DungeonBandStandard> memo = new Dictionary<int, DungeonBandStandard>();

            public Anchored(DungeonBandStandard anchor, int pivot, double defenseRate, double magicDefenseRate, double damageRate,
                double weaponAttackRateAbove400, double meleeDefenseRateAbove400, Func<Skill, SkillFormula> formulaOf = null)
            {
                Anchor = anchor ?? DungeonBandStandard.Empty;
                Pivot = pivot;
                DefenseRate = SanitizeDefenseRate(defenseRate);
                MagicDefenseRate = SanitizeMagicDefenseRate(magicDefenseRate);
                DamageRate = SanitizeDamageRate(damageRate);
                WeaponAttackRateAbove400 = SanitizeWeaponAttackRate(weaponAttackRateAbove400);
                MeleeDefenseRateAbove400 = SanitizeMeleeDefenseRate(meleeDefenseRateAbove400);
                this.formulaOf = formulaOf;
            }

            /// <summary>The measured band standard at <see cref="Pivot"/>.</summary>
            public DungeonBandStandard Anchor { get; }

            public int Pivot { get; }

            /// <summary>The sanitized melee/missile softening factor in force for this run.</summary>
            public double DefenseRate { get; }

            /// <summary>The sanitized magic defense rate above 300 in force for this run (0 = frozen).</summary>
            public double MagicDefenseRate { get; }

            /// <summary>The sanitized damage rate above <see cref="Pivot"/> in force for this run (1 = unsoftened).</summary>
            public double DamageRate { get; }

            /// <summary>The sanitized weapon attack rate above <see cref="MeleeSoftenLevel"/> in force for this run.</summary>
            public double WeaponAttackRateAbove400 { get; }

            /// <summary>The sanitized melee defense rate above <see cref="MeleeSoftenLevel"/> in force for this run.</summary>
            public double MeleeDefenseRateAbove400 { get; }

            /// <summary>
            /// The curve standard at <paramref name="level"/>; at or below the pivot the anchor, with its magic
            /// defense replaced by the retail curve when the level is above <see cref="DefenseSoftenLevel"/>.
            /// </summary>
            public DungeonBandStandard StandardAt(int level)
            {
                lock (memo)
                {
                    if (memo.TryGetValue(level, out var cached))
                        return cached;

                    var value = level <= Pivot
                        ? WithRetailMagicDefense(Anchor, level, MagicDefenseRate, formulaOf)
                        : Extrapolate(Anchor, Pivot, level, DefenseRate, MagicDefenseRate, DamageRate,
                            WeaponAttackRateAbove400, MeleeDefenseRateAbove400, formulaOf);
                    memo[level] = value;
                    return value;
                }
            }

            /// <summary><see cref="DungeonStatCurve.Ratio"/> at this run's five rates, damage split at this run's pivot.</summary>
            public double Ratio(DungeonStatAxis axis, int fromLevel, int toLevel)
                => DungeonStatCurve.Ratio(axis, fromLevel, toLevel, DefenseRate, MagicDefenseRate, DamageRate, Pivot,
                    WeaponAttackRateAbove400, MeleeDefenseRateAbove400);
        }
    }
}
