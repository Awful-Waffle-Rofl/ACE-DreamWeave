using System;

using ACE.Server.WorldObjects;

namespace ACE.Server.Pvp.Rules
{
    /// <summary>
    /// The PvP crit imbue levers (CS1, CS2, CB1) and effective-HP normalization (N3, N4; M1/M2 read it through
    /// <see cref="PvpRules.ApplyDamageMods(PvpChokePoint, PvpInteraction, PvpRuleDials, float, PvpDamageKind, bool)"/>).
    /// A separate partial of <see cref="PvpRules"/> so the levers stay in one block.
    /// </summary>
    public static partial class PvpRules
    {
        // ================= crit imbue levers (L11): CS1, CS2, CB1 =================

        /// <summary>
        /// PURE. The Critical Strike imbue's crit chance after pvp_cs_crit_mod: <paramref name="baseRate"/> +
        /// (<paramref name="criticalStrike"/> - <paramref name="baseRate"/>) x mod, the mod through
        /// <see cref="SanitizeMod"/>. Exactly <paramref name="criticalStrike"/> at the identity 1.0; exactly
        /// <paramref name="baseRate"/> at 0 (the imbue adds nothing).
        /// </summary>
        public static double ScaleCriticalStrike(double baseRate, double criticalStrike, double mod)
        {
            mod = SanitizeMod(mod);

            if (mod == 1.0)
                return criticalStrike;

            return baseRate + (criticalStrike - baseRate) * mod;
        }

        /// <summary>
        /// PURE. The Crippling Blow imbue's crit damage multiplier after pvp_cb_crit_mod: 1 + (<paramref name="cripplingBlow"/>
        /// - 1) x mod, the mod through <see cref="SanitizeMod"/>. Exactly <paramref name="cripplingBlow"/> at the
        /// identity 1.0; exactly 1.0 at 0 (the imbue adds nothing).
        /// </summary>
        public static double ScaleCripplingBlow(double cripplingBlow, double mod)
        {
            mod = SanitizeMod(mod);

            if (mod == 1.0)
                return cripplingBlow;

            return 1.0 + (cripplingBlow - 1.0) * mod;
        }

        /// <summary>
        /// CHOKE-POINT ENTRY for CS1 (physical, WorldObject_Weapon.GetWeaponCriticalChance) and CS2 (magic,
        /// GetWeaponMagicCritFrequency): the Critical Strike imbue's crit chance, before its Math.Max against the
        /// unimbued <paramref name="baseRate"/>. Classifies (<paramref name="wielder"/>, <paramref name="target"/>)
        /// FIRST - these statics are called with a null target by tests and report commands - and reads no setting
        /// for anything that is not a PvP pair.
        /// </summary>
        public static float ApplyCriticalStrikeMod(PvpChokePoint point, Creature wielder, Creature target, float baseRate, float criticalStrike)
        {
            var interaction = PvpClassifier.Classify(wielder, target);

            if (!interaction.IsPvp)
                return criticalStrike;

            var dials = ReadDials();

            if (dials == null || !dials.Enabled)
                return criticalStrike;

            var scaled = (float)ScaleCriticalStrike(baseRate, criticalStrike, dials.CsCritMod);

            if (scaled == criticalStrike)
                return criticalStrike;

            Report(point, criticalStrike, scaled);

            return scaled;
        }

        /// <summary>
        /// CHOKE-POINT ENTRY for CB1 (WorldObject_Weapon.GetWeaponCritDamageMod): the Crippling Blow imbue's crit
        /// damage multiplier, before its Math.Max against the weapon's own (tinkered) CriticalMultiplier, which this
        /// never touches. Classifies first; a null or non-PvP pair reads no setting.
        /// </summary>
        public static float ApplyCripplingBlowMod(Creature wielder, Creature target, float cripplingBlow)
        {
            var interaction = PvpClassifier.Classify(wielder, target);

            if (!interaction.IsPvp)
                return cripplingBlow;

            var dials = ReadDials();

            if (dials == null || !dials.Enabled)
                return cripplingBlow;

            var scaled = (float)ScaleCripplingBlow(cripplingBlow, dials.CbCritMod);

            if (scaled == cripplingBlow)
                return cripplingBlow;

            Report(PvpChokePoint.CB1, cripplingBlow, scaled);

            return scaled;
        }

        // ================= effective-HP normalization (L12): M1, M2, N3, N4 =================

        private static readonly object invalidHealthBoundsLock = new object();
        private static (long Floor, long Ceiling)? loggedInvalidHealthBounds;

        /// <summary>
        /// PURE. TRUE when pvp_health_floor and pvp_health_ceiling are BOTH set (&gt; 0) and floor &gt; ceiling - an
        /// invalid pair that normalization treats as the identity.
        /// </summary>
        public static bool HealthBoundsInvalid(long floor, long ceiling) => floor > 0 && ceiling > 0 && floor > ceiling;

        /// <summary>
        /// PURE. The effective-HP factor for a defender with max health <paramref name="maxHealth"/>: H / clamp(H,
        /// floor, ceiling), each bound ignored when 0 or less. 1.0 when both bounds are off, when the pair is
        /// invalid (<see cref="HealthBoundsInvalid"/>), or when H is not positive. Below a floor the factor is under
        /// 1 (less damage); above a ceiling it is over 1 (more damage).
        /// </summary>
        public static double HealthNormalizationFactor(double maxHealth, long floor, long ceiling)
        {
            if (floor <= 0 && ceiling <= 0)
                return 1.0;

            if (HealthBoundsInvalid(floor, ceiling))
                return 1.0;

            if (!(maxHealth > 0))
                return 1.0;

            var effective = maxHealth;

            if (floor > 0 && effective < floor)
                effective = floor;

            if (ceiling > 0 && effective > ceiling)
                effective = ceiling;

            return maxHealth / effective;
        }

        /// <summary>
        /// The effective-HP factor for an already-classified PvP hit on <paramref name="defender"/>. Reads the
        /// defender's max health (<see cref="MaxHealthSource"/>) ONLY when a bound is set and the pair is valid, so
        /// at the defaults nothing beyond the dials is read. An invalid pair is logged once per distinct value.
        /// </summary>
        internal static double ResolveHealthNormalization(Creature defender, PvpRuleDials dials)
        {
            if (dials == null || (dials.HealthFloor <= 0 && dials.HealthCeiling <= 0))
                return 1.0;

            if (HealthBoundsInvalid(dials.HealthFloor, dials.HealthCeiling))
            {
                LogInvalidHealthBoundsOnce(dials.HealthFloor, dials.HealthCeiling);
                return 1.0;
            }

            if (defender == null)
                return 1.0;

            return HealthNormalizationFactor(MaxHealthSource(defender), dials.HealthFloor, dials.HealthCeiling);
        }

        private static void LogInvalidHealthBoundsOnce(long floor, long ceiling)
        {
            lock (invalidHealthBoundsLock)
            {
                if (loggedInvalidHealthBounds == (floor, ceiling))
                    return;

                loggedInvalidHealthBounds = (floor, ceiling);
            }

            log.Warn($"[PVP] pvp_health_floor ({floor}) is above pvp_health_ceiling ({ceiling}); effective-HP normalization is OFF until that is fixed");
        }

        /// <summary>
        /// CHOKE-POINT ENTRY for N3 (Harm, before C3): classifies (<paramref name="source"/>, <paramref name="target"/>)
        /// first and reads the dials only for a PvP pair with the master switch on, then scales <paramref name="hit"/>
        /// by <see cref="ResolveHealthNormalization"/>. Unchanged input comes back exactly.
        /// </summary>
        public static float ApplyHealthNormalization(PvpChokePoint point, WorldObject source, Creature target, float hit)
        {
            var interaction = PvpClassifier.Classify(source, target);

            if (!interaction.IsPvp)
                return hit;

            if (!(hit > 0))
                return hit;

            var dials = ReadDials();

            if (dials == null || !dials.Enabled)
                return hit;

            var factor = ResolveHealthNormalization(interaction.Defender, dials);

            if (factor == 1.0)
                return hit;

            var scaled = (float)(hit * factor);

            if (scaled == hit)
                return hit;

            Report(point, hit, scaled);

            return scaled;
        }

        /// <summary>CHOKE-POINT ENTRY for N4 (Drain Health's source loss, before C4): the float entry, rounded to a whole point; unchanged input comes back exactly.</summary>
        public static uint ApplyHealthNormalization(PvpChokePoint point, WorldObject source, Creature target, uint hit)
        {
            var scaled = ApplyHealthNormalization(point, source, target, (float)hit);

            return scaled == hit ? hit : (uint)Math.Round(scaled);
        }
    }
}
