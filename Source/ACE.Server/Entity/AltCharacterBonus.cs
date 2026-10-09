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

            // ENLIGHTENMENT IS RETIRED and its term is deliberately dropped (XP-LANE-SPEC sec 4). This used
            // to return enlightenment * maxLevel + level, because a reset character's level 1 badly
            // understated its progress. That reasoning is now inverted: the retirement credit converts an
            // enlightened character's count INTO level (Player.GrantEnlightenmentRetirementCredit), so
            // level already carries everything enlightenment used to stand for. Keeping the term would
            // count the same progress twice - an ENL 20 character credited to level 531 would score
            // 20 * 275 + 531, and every one of its alts would read as hopelessly behind and take a
            // permanent catch-up bonus.
            //
            // The parameters are kept so the persisted call sites and their tests stay honest about what
            // they used to pass; both are ignored.
            _ = enlightenment;
            _ = maxLevel;

            return level;
        }

        /// <summary>
        /// TRUE when <paramref name="selfProgression"/> trails <paramref name="highestProgression"/> by at least
        /// <paramref name="gap"/> progression points, i.e. some other character on the account is far enough
        /// ahead to justify the catch-up boost. A negative <paramref name="gap"/> (bad config) is clamped to 0.
        /// At <paramref name="gap"/> = 5: exactly 5 behind is TRUE, 4 behind is FALSE, and being AHEAD (a negative
        /// difference) is always FALSE regardless of gap. At gap = 0 this restores the old bare-equality
        /// behaviour (any nonzero deficit qualifies).
        /// </summary>
        public static bool IsBelow(long selfProgression, long highestProgression, long gap)
        {
            if (gap < 0)
                gap = 0;

            var deficit = highestProgression - selfProgression;

            // deficit > 0 is always required (equal or ahead never qualifies, even at gap 0) on top of
            // meeting the configured gap, so gap 0 reproduces the old strict "<" behaviour exactly.
            return deficit > 0 && deficit >= gap;
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
