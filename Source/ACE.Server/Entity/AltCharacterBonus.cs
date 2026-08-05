using System;

namespace ACE.Server.Entity
{
    /// <summary>
    /// Pure math for the "alt character bonus" system. A character whose account already has a further-along
    /// character earns boosted leveling XP until it catches up, letting a returning player bring a fresh alt
    /// up to the account's high-water mark quickly. A character's standing on the account is measured by a
    /// single "progression" score that folds enlightenment and level together (enlightenment resets level to 1
    /// but is a far larger achievement, so each enlightenment tier is worth a full level climb). While a
    /// character's progression is below the highest on its account, its own leveling XP is scaled up by a
    /// configured multiplier - multiplicative on top of any other bonus (offline bonus, augs, gear).
    /// Kept free of <see cref="WorldObjects.Player"/> state so the boundary behavior (ordering, overflow guard)
    /// is unit-testable in isolation. See Player_AltCharacterBonus.cs for the login/XP-grant wiring.
    /// </summary>
    public static class AltCharacterBonus
    {
        /// <summary>
        /// Folds a character's <paramref name="enlightenment"/> and <paramref name="level"/> into a single
        /// monotonic progression score, using <paramref name="maxLevel"/> as the worth of one enlightenment
        /// tier (a character must be at max level to enlighten, so each tier represents a full level climb).
        /// A level-1 character with 1 enlightenment therefore outranks an un-enlightened max-level character,
        /// matching how the game treats enlightenment as continued progression past the level cap.
        /// </summary>
        public static long GetProgression(int level, int enlightenment, int maxLevel)
        {
            if (level < 0)
                level = 0;
            if (enlightenment < 0)
                enlightenment = 0;
            if (maxLevel < 0)
                maxLevel = 0;

            return (long)enlightenment * maxLevel + level;
        }

        /// <summary>
        /// TRUE when <paramref name="selfProgression"/> is strictly below <paramref name="highestProgression"/>,
        /// i.e. some other character on the account is further along. Equality yields FALSE, so the bonus turns
        /// off the instant an alt reaches (or a character already sits at) the account's high-water mark.
        /// </summary>
        public static bool IsBelow(long selfProgression, long highestProgression)
        {
            return selfProgression < highestProgression;
        }

        /// <summary>
        /// Scales <paramref name="amount"/> by (1 + <paramref name="multiplier"/>). Returns the amount unchanged
        /// for a non-positive amount or multiplier, or when the boosted value would overflow a long. Mirrors
        /// <see cref="OfflineBonus.Apply"/> so the two bonuses compose predictably when chained.
        /// </summary>
        public static long Apply(long amount, double multiplier)
        {
            if (amount <= 0 || multiplier <= 0)
                return amount;

            var boosted = amount * (1.0 + multiplier);

            if (!double.IsFinite(boosted) || boosted >= long.MaxValue)
                return amount;

            return (long)Math.Round(boosted);
        }
    }
}
