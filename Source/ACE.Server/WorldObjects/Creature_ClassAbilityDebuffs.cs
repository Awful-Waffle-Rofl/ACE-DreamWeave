using ACE.Common;
using ACE.Entity.Enum;
using ACE.Server.ClassAbilities;

namespace ACE.Server.WorldObjects
{
    partial class Creature
    {
        // ---- Weakened Blood: the Blood Mage's TARGET-SIDE life vulnerability ---------------------
        //
        // Deliberately transient state on the VICTIM rather than an enchantment or caster state
        // (BLOOD-MAGE-DESIGN sec 3):
        //
        //  - on the victim, because every life caster attacking that target reads it, not just the blood
        //    mage who applied it. That is the entry's stated party contribution and it is why it is not
        //    modelled as a buff on the applier.
        //  - transient rather than an EnchantmentManager entry, because it must not stack, must not be
        //    dispellable, must not persist to the shard, and must not appear as a spell the player can
        //    resist - it is a class-ability mark, not a Vulnerability cast.
        //
        // Mutated on the applying player's landblock thread, the same assumption every other class-ability
        // effect that reaches across to another creature already makes (Spell AOE spawns projectiles on a
        // neighbour, Acid Proc ticks damage onto a target). Never persisted: it is 20 seconds long and is
        // rebuilt from scratch when the creature is.

        private double weakenedBloodMod = 1.0;
        private double weakenedBloodExpireTime;

        /// <summary>
        /// Applies or refreshes Weakened Blood on this creature. Both the magnitude and the expiry take the
        /// MAXIMUM against whatever is already held (<see cref="WeakenedBloodMath.Refresh"/>), so a
        /// low-rank blood mage can never downgrade a high-rank mark that is still running - important
        /// because the mark is shared by the whole fellowship.
        ///
        /// RETURNS TRUE ONLY FOR A FRESH LANDING - the creature was not already carrying a live mark. A
        /// refresh of a mark that is still running returns FALSE, and so does a rejected application (a
        /// non-vulnerability magnitude or a non-positive duration).
        ///
        /// That distinction exists for the CASTER'S confirmation line, which is the only thing that reads it.
        /// Weakened Blood is applied by every landed Harm and every landed Drain - including each of Crimson
        /// Harvest's up-to-five secondary strikes - so a message on every application would be five lines from
        /// one cast and several a second in a sustained fight. One line when the mark takes hold, then
        /// silence for the 20 seconds it holds, is the same "announce the transition, not the state" shape
        /// the Frenzy and Nether Rush fade notices already use.
        /// </summary>
        public bool ApplyWeakenedBlood(double mod, double durationSeconds)
        {
            if (mod <= 1.0 || durationSeconds <= 0.0)
                return false;

            var now = Time.GetUnixTime();

            var wasHeld = weakenedBloodMod > 1.0 && WeakenedBloodMath.IsActive(now, weakenedBloodExpireTime);

            var refreshed = WeakenedBloodMath.Refresh(now, weakenedBloodMod, weakenedBloodExpireTime, mod, now + durationSeconds);

            weakenedBloodMod = refreshed.Mod;
            weakenedBloodExpireTime = refreshed.ExpireTime;

            return !wasHeld;
        }

        /// <summary>
        /// This creature's current Weakened Blood life-vulnerability modifier (1.0 = none), lazily expiring
        /// the mark once its window has lapsed. Read from <see cref="GetLifeVulnerabilityMod"/>, which
        /// takes MAX across the whole life-vulnerability axis rather than multiplying.
        /// </summary>
        public float GetWeakenedBloodMod()
        {
            if (weakenedBloodMod <= 1.0)
                return 1.0f;

            if (!WeakenedBloodMath.IsActive(Time.GetUnixTime(), weakenedBloodExpireTime))
            {
                weakenedBloodMod = 1.0;
                return 1.0f;
            }

            return (float)weakenedBloodMod;
        }

        /// <summary>
        /// TRUE while this creature is under Weakened Blood. Read by Exsanguinate, whose resistance-ignore
        /// half only applies against a marked target.
        /// </summary>
        public bool HasWeakenedBlood => GetWeakenedBloodMod() > 1.0f;

        /// <summary>
        /// The RESISTANCE-ONLY half of this creature's HealthDrain modifier: the ResistHealthDrain property,
        /// natural resistance, and the Life Resist rating - deliberately WITHOUT the vulnerability term that
        /// <see cref="GetResistanceMod(ResistanceType, WorldObject, WorldObject, float)"/> multiplies in
        /// alongside them.
        ///
        /// TWO CALLERS, BOTH BECAUSE THE VULNERABILITY TERM WOULD BE WRONG FOR THEM:
        ///
        ///  - EXSANGUINATE's "ignores 25% of the target's HealthDrain resistance" has to act on resistance
        ///    alone. Under Weakened Blood the combined modifier is 2.0 or more, and nudging THAT toward 1.0
        ///    would cut damage instead of raising it.
        ///  - DRAIN is excluded from the life-vulnerability axis by ruling (user, 2026-08-02), so
        ///    HandleCastSpell_Transfer reads this instead of GetResistanceMod(HealthDrain). This returns
        ///    exactly what that call returned before Weakened Blood existed, which is what makes the
        ///    exclusion a true revert rather than an approximation.
        ///
        /// Keeping the split here rather than at either ability keeps it in one file with
        /// GetResistanceMod's own chain, so a future change there is visibly next to the copy that has to
        /// track it.
        /// </summary>
        public double GetHealthDrainResistanceOnly()
        {
            return GetEffectiveResistHealthDrain() * GetNaturalResistance(DamageType.Health) * GetLifeResistRatingMod();
        }
    }
}
