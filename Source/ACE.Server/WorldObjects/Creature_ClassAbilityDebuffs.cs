using System;

using ACE.Common;
using ACE.Entity.Enum;
using ACE.Server.ClassAbilities;
using ACE.Server.Entity;
using ACE.Server.WorldObjects.Managers;

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

        // The mark's attack/cast SLOW, as the speed factor it leaves this monster at (1.0 = none). Lives
        // beside the mark and SHARES ITS EXPIRY (weakenedBloodExpireTime): there is no second timer, so the
        // slow can never outlive or undercut the vulnerability it rides with. Merged by the strongest slow
        // (smallest factor) while the mark is running - WeakenedBloodMath.RefreshSpeedFactor.
        private double weakenedBloodSpeedFactor = 1.0;

        /// <summary>
        /// Whether this creature may carry the Weakened Blood SLOW. Players, pets (CombatPet derives from
        /// Pet) and chess pieces never do - the same exclusion, for the same reasons, as
        /// <see cref="CanBeClassAbilityPinned"/>: the slow is PvE only, a player's own summon must never be
        /// slowed by a stray hit, and GamePiece drives its own timing. Enforced HERE, at the state boundary,
        /// so no present or future application site can slow one. The vulnerability half of the mark is not
        /// gated by this.
        /// </summary>
        public bool CanBeWeakenedBloodSlowed => !(this is Player || this is Pet || this is GamePiece);

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
        /// Weakened Blood can land from every Harm, every Drain strike and every Health-damage life
        /// projectile, so a message on every application would be several lines from one cast and several a
        /// second in a sustained fight. One line when the mark takes hold, then silence for the 20 seconds it
        /// holds, is the same "announce the transition, not the state" shape the Frenzy and Mana Rush fade
        /// notices already use; the caster additionally caps it at one line per cast
        /// (Player.TryApplyWeakenedBlood).
        ///
        /// <paramref name="speedFactor"/> is the applier's slow (1.0 = none, see
        /// WeakenedBloodMath.SpeedFactor). It is dropped silently on a creature that
        /// <see cref="CanBeWeakenedBloodSlowed"/> refuses, and otherwise merged by the strongest slow under
        /// the mark's shared expiry.
        /// </summary>
        public bool ApplyWeakenedBlood(double mod, double durationSeconds, double speedFactor = 1.0)
        {
            return ApplyWeakenedBlood(mod, durationSeconds, speedFactor, Time.GetUnixTime());
        }

        /// <summary>
        /// <see cref="ApplyWeakenedBlood(double, double, double)"/> at an explicit clock, so the merge and
        /// lapse rules are testable without waiting out a real 20-second window.
        /// </summary>
        internal bool ApplyWeakenedBlood(double mod, double durationSeconds, double speedFactor, double now)
        {
            if (mod <= 1.0 || durationSeconds <= 0.0)
                return false;

            var wasHeld = weakenedBloodMod > 1.0 && WeakenedBloodMath.IsActive(now, weakenedBloodExpireTime);

            // the slow merges against the expiry as it stood BEFORE this application - that is the window
            // the existing factor was actually alive in
            var incomingFactor = CanBeWeakenedBloodSlowed ? Math.Clamp(speedFactor, WeakenedBloodMath.MinSpeedFactor, 1.0) : 1.0;

            weakenedBloodSpeedFactor = WeakenedBloodMath.RefreshSpeedFactor(now, weakenedBloodSpeedFactor, weakenedBloodExpireTime, incomingFactor);

            var refreshed = WeakenedBloodMath.Refresh(now, weakenedBloodMod, weakenedBloodExpireTime, mod, now + durationSeconds);

            weakenedBloodMod = refreshed.Mod;
            weakenedBloodExpireTime = refreshed.ExpireTime;

            return !wasHeld;
        }

        /// <summary>
        /// The speed factor the Weakened Blood slow currently leaves this monster at (1.0 = not slowed),
        /// lazily clearing it once the mark's shared window has lapsed. Multiplies the monster's attack
        /// animation speed (Creature.GetAnimSpeed) and divides its cast times and cadence delays
        /// (<see cref="ScaleWeakenedBloodSlowDuration"/>). Always 1.0 for a creature that cannot be slowed,
        /// because nothing below 1.0 is ever stored on one.
        /// </summary>
        public double GetWeakenedBloodSpeedFactor()
        {
            // the common case - never slowed - reads no clock
            if (weakenedBloodSpeedFactor >= 1.0)
                return 1.0;

            return GetWeakenedBloodSpeedFactor(Time.GetUnixTime());
        }

        internal double GetWeakenedBloodSpeedFactor(double now)
        {
            if (weakenedBloodSpeedFactor >= 1.0)
                return 1.0;

            if (!WeakenedBloodMath.IsActive(now, weakenedBloodExpireTime))
            {
                weakenedBloodSpeedFactor = 1.0;
                return 1.0;
            }

            return weakenedBloodSpeedFactor;
        }

        /// <summary>
        /// A monster DURATION (a cast time or a cadence delay) under the Weakened Blood slow: divided by the
        /// speed factor, so a factor of 0.5 doubles it. Returns the input unchanged - bit for bit - when the
        /// monster is not slowed.
        /// </summary>
        public float ScaleWeakenedBloodSlowDuration(float duration)
        {
            var factor = GetWeakenedBloodSpeedFactor();

            return factor >= 1.0 ? duration : (float)(duration / factor);
        }

        /// <summary>
        /// The double-precision twin of <see cref="ScaleWeakenedBloodSlowDuration(float)"/>, for the cadence
        /// delays that are rolled as doubles (ThreadSafeRandom.Next returns double).
        /// </summary>
        public double ScaleWeakenedBloodSlowDuration(double duration)
        {
            var factor = GetWeakenedBloodSpeedFactor();

            return factor >= 1.0 ? duration : duration / factor;
        }

        /// <summary>
        /// The magic-attack gate's delay (AiUseMagicDelay, 3 seconds when unset) under the Weakened Blood
        /// slow. Monster_Combat reads this in NextMagicAttackTime and in DoAttackStance's alignment of
        /// PrevAttackTime, so both sides of that arithmetic always use the same value.
        /// </summary>
        public double GetWeakenedBloodScaledMagicDelay()
        {
            // defaults to most common value found in py16 db (the same default NextMagicAttackTime had).
            // Kept in double, the property's own type, so an unslowed monster's gate is bit-identical.
            var magicDelay = AiUseMagicDelay ?? 3.0f;

            var factor = GetWeakenedBloodSpeedFactor();

            return factor >= 1.0 ? magicDelay : magicDelay / factor;
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

        /// <summary>
        /// TRUE while this creature is carrying at least one Pocket Sand blind debuff entry
        /// (EnchantmentManager.SpellCategory_ClassAbility_PocketSand), from ANY caster - unlike
        /// OpportunistAbility.TargetHasQualifyingMark, which filters that same category down to entries
        /// applied by the attacker or their fellowship. This one is deliberately caster-agnostic: the Pocket
        /// Sand rider on GetSneakAttackMod pays out for whoever attacks the blinded target next, not only the
        /// Rogue who threw the sand.
        /// </summary>
        public bool IsBlindedByPocketSand => EnchantmentManager.GetEnchantments((SpellCategory)Managers.EnchantmentManager.SpellCategory_ClassAbility_PocketSand).Count > 0;

        /// <summary>
        /// This creature's Hunter's Mark "more damage taken" multiplier (1.0 = unmarked).
        ///
        /// Reads EVERY entry in EnchantmentManager.SpellCategory_ClassAbility_HuntersMark - one per archer,
        /// across all layers, deliberately NOT a top-layer read - and folds them through
        /// <see cref="HuntersMarkMath.Combine"/>, so the strongest mark applies (owner ruling 2026-09-14).
        ///
        /// Multiplied onto the resistance term by GetResistanceMod AFTER the vulnerability-vs-weapon max, on
        /// every return path of the DamageType overload and on the HealthDrain case of the ResistanceType
        /// overload. That placement is the whole ability: it is a separate multiplicative axis, so it stacks with
        /// Vulnerability, Sundermark, Elemental Rend, rending weapons and (through the armor term) Imperil.
        ///
        /// A player is never marked (HuntersMarkAbility is PvE only), so a player reads 1.0 without touching
        /// the registry. Uncached on purpose: the category list is read straight from the registry, so there is
        /// no memoized value for a missed invalidation to leave stale.
        /// </summary>
        public float GetHuntersMarkMod()
        {
            if (this is Player)
                return 1.0f;

            var entries = EnchantmentManager.GetEnchantments((SpellCategory)Managers.EnchantmentManager.SpellCategory_ClassAbility_HuntersMark);

            if (entries == null || entries.Count == 0)
                return 1.0f;

            var mods = new float[entries.Count];
            for (var i = 0; i < entries.Count; i++)
                mods[i] = entries[i].StatModValue;

            return HuntersMarkMath.Combine(mods);
        }

        // ---- Elemental Proficiency Lightning: a TARGET-SIDE "more damage taken" debuff ------------------
        //
        // Archmage T3 (ELEMENTAL-PROFICIENCY-DESIGN.md). Transient state on the VICTIM, for the same reasons
        // as Weakened Blood above: it is read by every attacker, it must not be dispellable, must not persist
        // and must not show up as a resistable spell. ONE debuff per target, not one per caster (owner
        // ruling): a second Archmage's application merges into the same slot through
        // ElementalProficiencyMath.Merge - the stronger bonus is kept and the end time is the later of the
        // two - so a low-rank caster can never downgrade or shorten a high-rank caster's debuff.
        //
        // Clock: Timers.RunningTime, the same clock the pin and stun timers use. Mutated on the applying
        // caster's landblock thread, the same assumption Weakened Blood documents above. Never persisted.

        private double elementalLightningBonus;
        private double elementalLightningEndTime;

        /// <summary>
        /// Applies or refreshes the Elemental Proficiency Lightning debuff: this creature takes
        /// (1 + <paramref name="damageTakenBonus"/>) times the damage from every source until it lapses.
        ///
        /// RETURNS TRUE ONLY FOR A NEW LANDING - the creature was not already carrying a live debuff. A refresh
        /// of a running debuff returns FALSE (it still merges), and so does a refusal: an ineligible target
        /// (<see cref="ElementalProficiencyMath.IsEligibleTarget"/> - players, pets and game pieces), a dead
        /// creature, a non-positive bonus or a non-positive duration.
        /// </summary>
        public bool TryApplyElementalLightning(double damageTakenBonus, double seconds)
        {
            return TryApplyElementalLightning(damageTakenBonus, seconds, Timers.RunningTime);
        }

        /// <summary>
        /// <see cref="TryApplyElementalLightning(double, double)"/> at an explicit running-time clock, so the
        /// merge and lapse rules are testable without waiting out a real window.
        /// </summary>
        internal bool TryApplyElementalLightning(double damageTakenBonus, double seconds, double now)
        {
            if (damageTakenBonus <= 0.0 || seconds <= 0.0)
                return false;

            if (!ElementalProficiencyMath.IsEligibleTarget(this) || IsDead)
                return false;

            var wasHeld = IsElementalLightningActive(now);

            var existing = wasHeld
                ? new ElementalProficiencyMath.Effect(elementalLightningBonus, elementalLightningEndTime)
                : new ElementalProficiencyMath.Effect(0.0, 0.0);

            var merged = ElementalProficiencyMath.Merge(existing, new ElementalProficiencyMath.Effect(damageTakenBonus, now + seconds));

            elementalLightningBonus = merged.Value;
            elementalLightningEndTime = merged.EndTime;

            return !wasHeld;
        }

        private bool IsElementalLightningActive(double now)
        {
            return elementalLightningBonus > 0.0 && now < elementalLightningEndTime;
        }

        /// <summary>
        /// This creature's Lightning "more damage taken" multiplier (1.0 = none), lazily clearing the debuff
        /// once it has lapsed. Multiplied into Creature.GetResistanceMod beside GetHuntersMarkMod on every
        /// site that applies the mark, so it is its own multiplicative axis: it stacks with Hunter's Mark,
        /// Vulnerability, Weakened Blood, rending weapons and every other damage-taken source.
        ///
        /// HOT PATH - every damage resolution against every creature and player. An unaffected creature
        /// pays one double compare and reads no clock.
        /// </summary>
        public float GetElementalLightningMod()
        {
            if (elementalLightningBonus <= 0.0)
                return 1.0f;

            return GetElementalLightningMod(Timers.RunningTime);
        }

        internal float GetElementalLightningMod(double now)
        {
            if (elementalLightningBonus <= 0.0)
                return 1.0f;

            if (now >= elementalLightningEndTime)
            {
                elementalLightningBonus = 0.0;
                elementalLightningEndTime = 0.0;
                return 1.0f;
            }

            return (float)(1.0 + elementalLightningBonus);
        }

        /// <summary>TRUE while this creature carries a live Lightning debuff.</summary>
        public bool HasElementalLightning => GetElementalLightningMod() > 1.0f;
    }
}
