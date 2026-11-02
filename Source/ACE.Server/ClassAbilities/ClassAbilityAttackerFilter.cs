using System;
using System.Collections.Generic;

using ACE.Server.Pvp.Rules;
using ACE.Server.WorldObjects;

namespace ACE.Server.ClassAbilities
{
    /// <summary>
    /// The one definition of "an enemy creature" for incoming-damage class abilities: a live, non-player
    /// Creature that is not the defender. Player.ApplyPreWriteDamageClassAbilities fills
    /// PreWriteDamageContext.Attacker from it, and EnchantmentManager.ApplyDamageTick applies it to each
    /// DoT caster to size PreWriteDamageContext.SpellDamageFromEnemyCreature. ONE predicate, so the dispatch
    /// and the tick site cannot drift into two different answers about the same caster.
    /// </summary>
    public static class ClassAbilityAttackerFilter
    {
        /// <summary>
        /// <paramref name="source"/> as a Creature when it is a live non-player creature other than
        /// <paramref name="defender"/>; null for a player (PvP), the defender themselves, a dead creature,
        /// a non-creature source or no source at all.
        /// </summary>
        public static Creature EnemyCreature(WorldObject source, WorldObject defender)
        {
            return source is Creature creature && !PvpClassifier.IsPvp(creature, defender) && creature != defender && !creature.IsDead ? creature : null;
        }

        /// <summary>
        /// The dispatch's PreWriteDamageContext.SpellDamageFromEnemyCreature, as a pure function. 0 for a
        /// non-spell hit whatever else is passed (the physical path). For a spell: the explicit share when
        /// the site supplied one (the DoT tick, which has no source), clamped to the hit; otherwise the
        /// whole hit when the filtered attacker is an enemy creature, and 0 when it is not (PvP, self, a
        /// dead caster, no creature at all).
        /// </summary>
        public static uint SpellDamageShare(bool isSpell, bool hasEnemyCreatureAttacker, uint? explicitShare, uint incomingDamage)
        {
            if (!isSpell)
                return 0;

            if (explicitShare.HasValue)
                return Math.Min(explicitShare.Value, incomingDamage);

            return hasEnemyCreatureAttacker ? incomingDamage : 0;
        }

        /// <summary>
        /// The enemy-creature share of one accumulated DoT tick: the rounded sum of the contributions flagged
        /// as coming from an enemy creature, clamped to <paramref name="tickDamage"/> (the whole tick as it
        /// enters the dispatch). Pure, so a mixed tick can be unit tested without a live landblock. Negative
        /// or non-finite contributions add nothing.
        /// </summary>
        public static uint EnemyCreatureShareOfTick(IEnumerable<(bool FromEnemyCreature, float Amount)> contributions, uint tickDamage)
        {
            if (contributions == null || tickDamage == 0)
                return 0;

            var sum = 0.0;

            foreach (var (fromEnemyCreature, amount) in contributions)
            {
                if (fromEnemyCreature && amount > 0.0f && !float.IsInfinity(amount))
                    sum += amount;
            }

            var rounded = Math.Round(sum);

            return rounded >= tickDamage ? tickDamage : (uint)rounded;
        }
    }
}
