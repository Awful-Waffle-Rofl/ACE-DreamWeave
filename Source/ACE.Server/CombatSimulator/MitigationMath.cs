using ACE.Entity.Enum;
using ACE.Server.Entity;
using ACE.Server.WorldObjects;

namespace ACE.Server.CombatSimulator
{
    /// <summary>
    /// Applies an attacker's factored terms to a stored profile row, reproducing the engine's
    /// arithmetic exactly. Each method mirrors one term of the final multiply at
    /// DamageEvent.cs:413. See Docs/CombatSimulator/DESIGN.md, "Exact factorization", for why
    /// each of these factors out without approximation.
    ///
    /// Every lookup here fails soft on an absent key AND on an absent table, returning that
    /// term's neutral value: a DefenderProfile is a decomposition of one defender's mitigation,
    /// so a table that is not there says that term does not apply, which is a statement to
    /// honour rather than an error to throw on - particularly inside a shard-worker callback,
    /// where an exception is expensive and lands far from its cause.
    /// </summary>
    public static class MitigationMath
    {
        public static float ArmorMod(DefenderProfile profile, AttackerSpec spec, BodyPart bodyPart)
        {
            if (spec.IgnoreAllArmor)
                return 1.0f;

            var key = new ArmorKey(bodyPart, spec.DamageType, spec.IgnoreMagicArmor, spec.IgnoreMagicResist);

            if (profile.Armor == null || !profile.Armor.TryGetValue(key, out var row))
                return 1.0f;

            var effectiveAl = row.EffectiveArmorLevel;

            // the engine guards this multiply on the sign of the summed armor level
            if (effectiveAl > 0)
                effectiveAl *= spec.ArmorRendingMod;

            return SkillFormula.CalcArmorMod(effectiveAl);
        }

        public static float ShieldMod(DefenderProfile profile, AttackerSpec spec)
        {
            // the angle check is a pure binary gate: outside the 180 degree frontal arc the
            // engine returns 1.0 immediately and the angle value is used for nothing else
            if (!spec.WithinShieldArc || spec.IgnoreAllArmor)
                return 1.0f;

            var key = new ShieldKey(spec.DamageType, spec.IgnoreMagicArmor);

            if (profile.Shield == null || !profile.Shield.TryGetValue(key, out var row))
                return 1.0f;

            // the shield-skill cap is already baked into the stored level; the attacker's term
            // multiplies afterwards, which is the engine's order
            var effectiveLevel = row.CappedEffectiveLevel * spec.IgnoreShieldMod;

            return SkillFormula.CalcArmorMod(effectiveLevel);
        }

        public static float ResistanceMod(DefenderProfile profile, AttackerSpec spec)
        {
            if (profile.Resistance == null || !profile.Resistance.TryGetValue(spec.DamageType, out var pair))
                return spec.WeaponResistanceMod;

            // ignore-magic-resist against a non-player attacker bypasses the defender's
            // protection and vulnerability entirely
            if (spec.IgnoreMagicResist && !spec.AttackerIsPlayer)
                return spec.WeaponResistanceMod;

            var vulnMod = pair.V0;

            if (vulnMod < spec.WeaponResistanceMod)
                vulnMod = spec.WeaponResistanceMod;

            return pair.P0 * vulnMod;
        }

        public static float DamageResistRatingMod(DefenderProfile profile, AttackerSpec spec)
        {
            // the stored base is per combat type, because the specialized-defense damage rating
            // resist inside it depends on which defense skill the attack is tested against
            if (profile.DamageResistRatingBase == null || !profile.DamageResistRatingBase.TryGetValue(spec.CombatType, out var mod))
                mod = 1.0f;

            // Battle Hardened is PvE only
            if (!spec.AttackerIsPlayer)
                mod *= profile.BattleHardenedMultiplier;

            return mod;
        }
    }
}
