using System;

using ACE.Server.Managers;

namespace ACE.Server.WorldObjects
{
    partial class Player
    {
        /// <summary>
        /// FORK: Focus spell damage. Pure math for <see cref="GetFocusSpellDamageMod"/>: 1 + focus x perPoint.
        /// A zero, negative, NaN or infinite perPoint (the setting is live-tunable) means no bonus, and so does a
        /// product too large for a float, so a bad admin value can never zero, invert or infinity-out a spell's damage.
        /// </summary>
        public static float GetFocusSpellDamageMod(uint focus, double perPoint)
        {
            if (double.IsNaN(perPoint) || double.IsInfinity(perPoint) || perPoint <= 0.0)
                return 1.0f;

            var mod = 1.0 + focus * perPoint;

            if (double.IsNaN(mod) || double.IsInfinity(mod) || mod > float.MaxValue)
                return 1.0f;

            return (float)mod;
        }

        /// <summary>
        /// FORK: the single home of the Focus spell damage gates. Qualifies only for a direct hit the caster cast
        /// (not a proc, cascade, echo recast or AOE child) against a non-player (PvE only). The caster-is-Player
        /// check stays at each call site as a pattern match.
        /// </summary>
        internal static bool QualifiesForFocusSpellDamage(bool directHit, bool targetIsPlayer, bool fromProc, bool classAbilityProc)
        {
            return directHit && !targetIsPlayer && !fromProc && !classAbilityProc;
        }

        /// <summary>
        /// FORK: the damage multiplier a player-cast, non-proc, PvE direct damage spell earns from the caster's
        /// BUFFED Focus (Attributes[Focus].Current). Read from magic_damage_per_focus_point at each hit so a
        /// /modifydouble takes effect live. The CALLER owns the gates (target is not a Player, and the cast is
        /// not a proc); this method only reads the setting and the attribute.
        /// </summary>
        public float GetFocusSpellDamageMod()
        {
            return GetFocusSpellDamageMod(Focus.Current, PropertyManager.GetDouble("magic_damage_per_focus_point").Item);
        }
    }
}