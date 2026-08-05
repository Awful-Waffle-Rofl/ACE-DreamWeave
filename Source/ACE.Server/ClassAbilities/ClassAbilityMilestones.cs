using System.Collections.Generic;

namespace ACE.Server.ClassAbilities
{
    /// <summary>
    /// The level-milestone lane of the class-ability-point economy (DESIGN.md sec 2a): a character earns
    /// exactly one class ability point on reaching each of a fixed set of levels, twelve in all.
    ///
    /// This type is the PURE, side-effect-free core of that rule so it can be unit-tested without a live
    /// <see cref="WorldObjects.Player"/>. The grant itself (crediting the point, persisting the paid-out
    /// counter, respecting the lifetime cap) lives in <c>Player.GrantMilestoneClassAbilityPoints</c>, which
    /// is an idempotent catch-up: it compares <see cref="EntitledCount"/> for the character's current level
    /// against the persisted count already paid and grants the difference. That makes it retroactive-safe
    /// for existing characters and immune to missed level-ups.
    /// </summary>
    public static class ClassAbilityMilestones
    {
        /// <summary>
        /// The character levels at which a class ability point is granted, ascending. Twelve total.
        /// </summary>
        public static readonly IReadOnlyList<int> Levels =
            new[] { 20, 40, 60, 80, 100, 125, 150, 175, 200, 225, 250, 275 };

        /// <summary>
        /// How many milestone points a character at <paramref name="level"/> is entitled to in total
        /// (i.e. the number of milestone levels at or below their current level).
        /// </summary>
        public static int EntitledCount(int level)
        {
            var count = 0;

            foreach (var milestone in Levels)
            {
                if (level >= milestone)
                    count++;
                else
                    break; // Levels is ascending, so nothing further can qualify
            }

            return count;
        }
    }
}
