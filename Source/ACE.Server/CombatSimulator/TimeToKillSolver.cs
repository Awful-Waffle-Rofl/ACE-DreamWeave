using System.Collections.Generic;

namespace ACE.Server.CombatSimulator
{
    /// <summary>
    /// Solves the multiplier on the attacker's damage source that puts the roster's mean
    /// landed-hits-to-kill on a target. Damage is linear in the base damage range and mitigation
    /// does not depend on it, so this is a ratio rather than a search.
    ///
    /// The output is a factor to apply to the attacker's damage DATA: DVal and DVar on the
    /// weenie's combat body parts for a natural-weapon monster, or the wielded weapon's Damage
    /// and DamageVariance for a weapon-wielding one.
    ///
    /// Returns 1.0f in two distinct cases:
    ///
    /// - 1.0f means the attacker is already exactly on target - a real, solved answer.
    /// - 1.0f also means no scale could be determined at all, which happens when an empty
    ///   profile list is passed, a non-positive target landed hits is passed, or the mean
    ///   landed-hits-to-kill is not finite because one or more profiles take no damage from
    ///   this attacker.
    ///
    /// A caller needing to distinguish these two cases should examine the per-profile
    /// LandedHitsToKill values from Analytic; a value of infinity indicates the profile was
    /// unhittable by this attacker's damage.
    /// </summary>
    public static class TimeToKillSolver
    {
        public static float ScaleForTarget(AttackerSpec spec, IReadOnlyList<DefenderProfile> profiles, float targetLandedHits)
        {
            if (profiles.Count == 0 || targetLandedHits <= 0.0f)
                return 1.0f;

            var total = 0.0f;

            foreach (var profile in profiles)
                total += CombatSimulator.Analytic(spec, profile).LandedHitsToKill;

            var currentMean = total / profiles.Count;

            if (!float.IsFinite(currentMean))
                return 1.0f;

            // hits to kill is inversely proportional to damage
            return currentMean / targetLandedHits;
        }
    }
}
