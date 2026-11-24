using System.Linq;

using ACE.Common;
using ACE.Entity.Enum;
using ACE.Entity.Models;
using ACE.Server.Pinnacles;
using ACE.Server.WorldObjects.Managers;

namespace ACE.Server.WorldObjects
{
    partial class Player
    {
        // The attack half of a hazard hit slow. Runtime only, never persisted; the defaults (1.0, until 0) read as exactly
        // 1 in GetPinnacleAttackSlowFactor, so a player never hit is untouched.
        private double pinnacleAttackSlowFactor = 1.0;
        private double pinnacleAttackSlowUntil;

        /// <summary>
        /// A Pinnacle hazard hit this player with a slow (PinnacleHazardOptions.HitSlow; only the Harbinger fight sets
        /// one). Run: a hand-made Leaden Feet VI layer on Run sized so the run RATE falls by the fraction, refreshed (not
        /// stacked) by a repeat hit. Attack: GetAnimSpeed's factor for the same time. Then the run rate is re-broadcast.
        /// A null slow or a non-positive duration does nothing.
        /// </summary>
        public void ApplyPinnacleHitSlow(PinnacleHitSlow slow)
        {
            if (slow == null || !(slow.Seconds > 0) || !double.IsFinite(slow.Seconds))
                return;

            ApplyPinnacleRunSlow(EnchantmentManager, (int)GetCreatureSkill(Skill.Run).Current, slow.RunSlowFraction, slow.Seconds);

            pinnacleAttackSlowFactor = slow.AttackSpeedFactor;
            pinnacleAttackSlowUntil = Time.GetUnixTime() + slow.Seconds;

            HandleRunRateUpdate();
        }

        /// <summary>The attack-speed factor GetAnimSpeed applies after the class-ability cap: exactly 1 unless a hit slow is live.</summary>
        public double GetPinnacleAttackSlowFactor()
            => PinnacleHitSlowMath.AttackFactorAt(pinnacleAttackSlowFactor, pinnacleAttackSlowUntil, Time.GetUnixTime());

        /// <summary>
        /// The run half of <see cref="ApplyPinnacleHitSlow"/>, on any enchantment manager (a test drives it on a bare
        /// creature). <paramref name="currentRun"/> is the Run skill's Current, which already counts the top layer of
        /// RunLowering.
        ///
        /// THE COLLISION WITH A REAL LEADEN FEET. The layer reuses the retail identity (spell 1005 in RunLowering) for its
        /// icon and name, and a real Leaden Feet VI cast by a monster carries the same pair. It is written with no caster
        /// and refreshOnlyOwnCaster, so the refresh lookup only ever finds this hazard's own caster-0 layer: a real cast is
        /// never clipped or overwritten, and the hazard lays an independent layer beside it instead (the Tinkerer's
        /// Inspiration precedent). RunLowering contributes only its TOP layer (highest power), so the own layer's
        /// magnitude is added back to Current only when it is that top layer; under a stronger real cast it contributes
        /// nothing and Current is already the unslowed-by-us value. Null when nothing is laid.
        /// </summary>
        internal static PropertiesEnchantmentRegistry ApplyPinnacleRunSlow(EnchantmentManager em, int currentRun, double fraction, double seconds)
        {
            if (em == null || !(seconds > 0) || !double.IsFinite(seconds))
                return null;

            var spellId = (int)SpellId.LeadenFeetOther6;

            var own = em.GetEnchantments(SpellCategory.RunLowering).FirstOrDefault(e => e.SpellId == spellId && e.CasterObjectId == 0);

            var runUnslowed = currentRun;

            if (own != null && em.GetEnchantments_TopLayer(EnchantmentTypeFlags.Skill | EnchantmentTypeFlags.Additive, (uint)Skill.Run, true).Contains(own))
                runUnslowed -= (int)own.StatModValue;

            var points = PinnacleHitSlowMath.RunPointsFor(runUnslowed, fraction);

            if (points <= 0)
                return null;

            return em.AddClassAbilityDebuff((uint)SpellId.LeadenFeetOther6, 1, null, SpellCategory.RunLowering,
                PinnacleHitSlowMath.RunSlowStatModType, (uint)Skill.Run, -points, seconds, refreshOnlyOwnCaster: true);
        }
    }
}
