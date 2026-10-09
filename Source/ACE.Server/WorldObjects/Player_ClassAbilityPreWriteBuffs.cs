using System;

using ACE.Common;
using ACE.Entity.Enum;
using ACE.Server.ClassAbilities;
using ACE.Server.Entity;
using ACE.Server.Managers;
using ACE.Server.Pvp.Rules;

namespace ACE.Server.WorldObjects
{
    partial class Player
    {
        // Transient per-player state for the four 2026-09-12 abilities that ride the pre-write damage hook:
        // Adrenaline's window, Kinetic Charge's charges, Runic Ward's absorb pool and Soul Jump's cooldown.
        //
        // SAME CONTRACT AS Player_ClassAbilityBuffs.cs, which is where the rest of this family lives: never
        // persisted (all four are seconds-to-minutes long and reset naturally when the Player object is
        // rebuilt on login), mutated only on the player's landblock thread - the damage paths, the attack
        // path and the cast path all run there - so no locking is needed. Each one expires twice over:
        // lazily on its next read (silently, on a hot path) and on the shared ClassAbilityBuffsHeartbeat,
        // which is what makes the "wears off" line land while the player is standing still rather than on
        // their next swing.
        //
        // FILED IN ITS OWN PARTIAL rather than appended to Player_ClassAbilityBuffs.cs, which is the only
        // deviation from that file's shape: six sibling slices of the same wave are editing that file
        // concurrently, and this keeps the shared footprint down to one call in the heartbeat sweep. The
        // arithmetic lives in ClassAbilities/RunicWardMath.cs and SoulJumpMath.cs so it is unit-testable
        // without a live Player, exactly as SanguineWardMath is.
        //
        // NO AFFINITY CALL APPEARS IN THIS FILE, deliberately. ClassAbilityAffinityDeclarationTests scans
        // only ClassAbilities/Abilities for the affinity primitives and treats a rider computed elsewhere as
        // an explicit table exception; all four riders are therefore computed in their own handler files and
        // the resulting amount is passed in here. Do not move one down into this file without adding its row
        // to ScalingCallsOutsideTheHandler.

        /// <summary>
        /// The heartbeat sweep for the four abilities above, called from
        /// <see cref="ClassAbilityBuffsHeartbeat"/> so there is still exactly one heartbeat entry point for
        /// the whole class-ability buff family.
        /// </summary>
        private void ClassAbilityPreWriteBuffsHeartbeat(double now)
        {
            if (adrenalineWindowEndTime > 0.0 && now > adrenalineWindowEndTime)
            {
                adrenalineWindowEndTime = 0.0;
                SendClassAbilityBuffMessage("Your adrenaline subsides.");
                ApplyVisualEffects(PlayScript.EnchantDownRed);
            }

            if (kineticChargeStacks > 0 && now > kineticChargeExpireTime)
            {
                kineticChargeStacks = 0;
                SendClassAbilityBuffMessage("Your kinetic charge dissipates.");
                ApplyVisualEffects(PlayScript.EnchantDownBlue);
            }

            // Runic Ward LAPSES UNUSED, exactly like Sanguine Ward: whatever is left when the window closes
            // is dropped, never refunded, converted to health, or carried into the next hit. This is a
            // lifetime boundary, so the running absorbed-total and the fully-inscribed flag reset here too.
            if (runicWard.Amount > 0 && RunicWardMath.IsExpired(runicWard, now))
            {
                var totalAbsorbed = runicWardLifetimeAbsorbed;

                runicWard = default;
                runicWardLifetimeAbsorbed = 0;
                runicWardFullyInscribedAnnounced = false;

                SendClassAbilityBuffMessage(RunicWardMessages.FadeMessage(totalAbsorbed));
                ApplyVisualEffects(PlayScript.EnchantDownBlue);
            }
        }

        // ---- Adrenaline (Berserker T1): taking damage arms the next attack or spell ------------------

        /// <summary>
        /// Unix time the armed window closes at; 0 means nothing is armed. A TIME rather than a stored
        /// bonus, on purpose: the bonus is recomputed from the live rank when the window is spent, so a rank
        /// bought or lost inside the window applies to the strike rather than being frozen at arming time.
        /// </summary>
        private double adrenalineWindowEndTime;

        /// <summary>
        /// Arms (or re-arms) the window, called from AdrenalineAbility's pre-write hook on every damaging
        /// hit from a monster. IDEMPOTENT - a second hit inside the window simply re-stamps the expiry, which
        /// is what makes it safe for an observer riding a hook that fires at five separate damage sites.
        /// </summary>
        public void ArmAdrenaline(double windowSeconds)
        {
            if (windowSeconds <= 0.0)
                return;

            var wasArmed = adrenalineWindowEndTime > 0.0 && Time.GetUnixTime() <= adrenalineWindowEndTime;

            adrenalineWindowEndTime = Time.GetUnixTime() + windowSeconds;

            // announced only on the arming that OPENS a window, never on a re-stamp inside one: a Berserker
            // standing in a pack is hit several times a second, and a line per hit would be unreadable
            if (!wasArmed)
            {
                SendClassAbilityBuffMessage("Adrenaline surges - your next attack or spell hits harder.");
                ApplyVisualEffects(PlayScript.EnchantUpRed);
            }
        }

        /// <summary>
        /// TRUE if an armed window was live, which this CONSUMES. The single consumption point shared by the
        /// weapon-hit half (AdrenalineAbility.ModifyOutgoingDamage) and the spell half
        /// (AdrenalineAbility.ConsumeSpellDamageMultiplier), so "the first thing that qualifies spends it,
        /// never both" is a property of there being one method rather than of two call sites agreeing.
        ///
        /// A lapsed window is cleared here as well as on the heartbeat, so a player who was hit long ago and
        /// swings now gets nothing and pays nothing.
        /// </summary>
        public bool TryConsumeAdrenaline()
        {
            if (adrenalineWindowEndTime <= 0.0)
                return false;

            var live = Time.GetUnixTime() <= adrenalineWindowEndTime;

            adrenalineWindowEndTime = 0.0;

            return live;
        }

        // ---- Kinetic Charge (Vanguard T2): attacks that reach you build charges ----------------------

        private int kineticChargeStacks;
        private double kineticChargeExpireTime;

        /// <summary>
        /// How many charges the next attack must be holding before it may spend them, from the tunable.
        /// Floored at 1 so a mis-tuned 0 cannot make every swing a spending swing.
        /// </summary>
        private static int KineticChargeThreshold() =>
            (int)Math.Max(1L, PropertyManager.GetLong("class_ability_kineticcharge_charge_threshold").Item);

        /// <summary>
        /// Adds one charge and refreshes the fade timer. Called from KineticChargeAbility's pre-write hook
        /// for a damaging attack, and from <see cref="TryBuildKineticChargeFromAvoidedAttack"/> for one that
        /// was blocked or parried.
        ///
        /// The stack is CAPPED AT THE THRESHOLD rather than climbing past it, so charges accumulated while
        /// the Vanguard is not swinging cannot bank a second spend. Every hit still refreshes the timer, so
        /// a tank under sustained attack keeps a full stack ready.
        /// </summary>
        public void AddKineticCharge()
        {
            var now = Time.GetUnixTime();

            if (kineticChargeStacks > 0 && now > kineticChargeExpireTime)
                kineticChargeStacks = 0;

            kineticChargeExpireTime = now + PropertyManager.GetDouble("class_ability_kineticcharge_expire_seconds").Item;

            var threshold = KineticChargeThreshold();

            if (kineticChargeStacks >= threshold)
                return;

            kineticChargeStacks++;

            // announce once, on the hit that tops the stack off - the same spam bound Frenzy and Nether Rush
            // use, and for the same reason: the charges have no tell of their own until they are spent
            if (kineticChargeStacks == threshold)
            {
                SendClassAbilityBuffMessage($"Kinetic charge at full ({threshold}/{threshold}) - your next attack spends it.");
                ApplyVisualEffects(PlayScript.EnchantUpBlue);
            }
        }

        /// <summary>
        /// The blocked/parried feed, called from <see cref="OnClassAbilityAttackAvoided"/>. An avoided
        /// attack never reaches any damage site, so this is the ONLY path by which one can build a charge -
        /// which is what makes "whether it lands, is blocked, or is parried" true.
        ///
        /// Gated here rather than by a hook dispatch, so it re-applies the same preconditions the dispatch
        /// would: class abilities enabled and a rank held (TryGetClassAbility covers both), and a live
        /// non-player attacker (the PvE gate).
        /// </summary>
        public void TryBuildKineticChargeFromAvoidedAttack(Creature attacker)
        {
            if (attacker == null || PvpClassifier.IsPvp(attacker, this) || attacker == this || attacker.IsDead)
                return;

            if (!TryGetClassAbility(ClassAbilityId.KineticCharge, out _))
                return;

            AddKineticCharge();
        }

        /// <summary>
        /// TRUE if a FULL stack was held, which this spends in its entirety. All or nothing: below the
        /// threshold it spends nothing and returns false, so a partial stack is never cashed for a partial
        /// bonus. A stack whose window has lapsed is cleared here as well as on the heartbeat.
        /// </summary>
        public bool TryConsumeKineticCharges()
        {
            if (kineticChargeStacks <= 0)
                return false;

            if (Time.GetUnixTime() > kineticChargeExpireTime)
            {
                kineticChargeStacks = 0;
                return false;
            }

            if (kineticChargeStacks < KineticChargeThreshold())
                return false;

            kineticChargeStacks = 0;

            return true;
        }

        /// <summary>
        /// How many EXTRA creatures the strike currently in progress should cleave into, on top of the
        /// weapon's own CleaveTargets and Whirlwind's +1, because it just spent a full Kinetic Charge stack
        /// on a melee attack. Set by KineticChargeAbility.ModifyOutgoingDamage (a different class, hence the
        /// public setter method below) and read back by Creature.GetCleaveTarget from the SAME strike.
        ///
        /// The Player_Melee strike loop resets this to 0 immediately before every primary DamageTarget call
        /// and again after the cleave block, so only the ONE strike that spent the stack ever cleaves extra -
        /// never the next strike of a multi-strike swing, and never a later swing.
        /// </summary>
        public int KineticChargeCleaveTargets { get; private set; }

        /// <summary>Floored at 0 - see KineticChargeAbility.FlooredCleaveTargets, which already floors the
        /// tunable before calling here; this is a second line of defense against a negative value leaking
        /// in some other way.</summary>
        public void SetKineticChargeCleaveTargets(int extraTargets) => KineticChargeCleaveTargets = Math.Max(0, extraTargets);

        /// <summary>
        /// The damage-bonus FRACTION (same units as KineticChargeAbility.SpendBonus) that a melee spend on
        /// this strike's PRIMARY hit is worth, recorded so every cleave hit the same strike lands can also
        /// receive it (user ruling 2026-09-14). Set alongside <see cref="KineticChargeCleaveTargets"/>, by
        /// the same setter pattern, and reset at exactly the same two points in the Player_Melee strike loop
        /// - before the primary DamageTarget call and again after the cleave block - so it can never leak
        /// into the next strike of a multi-strike swing, or apply when the primary missed (no spend).
        /// </summary>
        public double KineticChargeCleaveBonus { get; private set; }

        /// <summary>Floored at 0 alongside the value - a negative recorded bonus would otherwise let a
        /// cleave hit deal LESS damage than an unbuffed one, which is not a thing this ability does.</summary>
        public void SetKineticChargeCleaveBonus(double bonus) => KineticChargeCleaveBonus = Math.Max(0.0, bonus);

        /// <summary>
        /// TRUE for the duration of the cleave-hit foreach in Player_Melee.cs - i.e. while DamageTarget is
        /// being called with primaryTarget: false. KineticChargeAbility.ModifyOutgoingDamage reads this and
        /// refuses to CONSUME the stack while it is set - a cleave hit is a target the player did not aim
        /// at, and by the time cleave hits are resolved the cleave LIST is already fixed (GetCleaveTarget
        /// already ran), so a spend here could not even add its own extra targets to it - it would only cash
        /// the stack for a bonus attributed to the wrong strike. The stack stays held until an attack lands
        /// on the AIMED (primary) target instead.
        ///
        /// That does NOT mean a cleave hit gets no bonus: while this flag is set, ModifyOutgoingDamage
        /// instead APPLIES <see cref="KineticChargeCleaveBonus"/> (recorded by the primary hit that just
        /// spent) to every cleave hit of the SAME strike, without consuming anything a second time and
        /// without a second chat line - see the flag-set branch in ModifyOutgoingDamage. A cleave hit on a
        /// strike whose primary missed (bonus left at 0.0, never recorded) gets nothing.
        ///
        /// Set true immediately around the cleave-hit foreach and cleared in a finally, so an exception
        /// mid-loop can never strand it true and silently disable every later spend this swing.
        /// </summary>
        public bool ResolvingMeleeCleaveHits { get; set; }

        // ---- Runic Ward (Spellsword T2): weapon hits inscribe a pool, a war spell cashes it -----------

        private RunicWardMath.WardState runicWard;

        /// <summary>
        /// The current ward LIFETIME's running total: how much damage it has absorbed since the pool last
        /// went from empty to non-zero. Told to the player exactly once, in the message that ends the
        /// lifetime (shatter or fade) - see RunicWardMessages. Reset at every lifetime boundary: shatter,
        /// spend, lapse, and a fresh Inscribe landing on a ward the heartbeat has not yet swept.
        /// </summary>
        private uint runicWardLifetimeAbsorbed;

        /// <summary>Whether the "fully inscribed" notice has already fired for the CURRENT lifetime.</summary>
        private bool runicWardFullyInscribedAnnounced;

        /// <summary>
        /// Adds one landed weapon hit's share to the ward, from the gain fraction the handler computed (rank
        /// plus its Item Tinkering affinity). The pool's ceiling is read here rather than passed in, because
        /// it is a fraction of THIS player's maximum health.
        /// </summary>
        public void InscribeRunicWard(uint hitDamage, double gainFraction)
        {
            if (hitDamage == 0 || gainFraction <= 0.0)
                return;

            var now = Time.GetUnixTime();

            var cap = RunicWardMath.Cap(Health.MaxValue,
                PropertyManager.GetDouble("class_ability_runicward_cap_fraction").Item);

            var before = RunicWardMath.IsExpired(runicWard, now) ? 0u : runicWard.Amount;

            // a hit landing on a ward the heartbeat has not yet swept is still the start of a fresh
            // lifetime - reset here so a stale "already announced" flag from the last ward cannot suppress
            // this one's fully-inscribed notice (see the heartbeat sweep above for the normal case)
            if (before == 0)
            {
                runicWardLifetimeAbsorbed = 0;
                runicWardFullyInscribedAnnounced = false;
            }

            runicWard = RunicWardMath.Inscribe(runicWard, hitDamage, gainFraction, cap, now,
                PropertyManager.GetDouble("class_ability_runicward_duration_seconds").Item);

            // announced only on the hit that first tops the ward off, and at most once per lifetime - a
            // ward drained by an absorb and topped back up must not announce a second time.
            if (RunicWardMessages.ShouldAnnounceFullyInscribed(runicWard.Amount, cap, runicWardFullyInscribedAnnounced))
            {
                runicWardFullyInscribedAnnounced = true;
                SendClassAbilityBuffMessage(RunicWardMessages.FullyInscribedMessage(runicWard.Amount));
                ApplyVisualEffects(PlayScript.EnchantUpBlue);
            }
        }

        /// <summary>
        /// Runs one incoming hit through the ward and returns what is LEFT of it to apply to Health, the
        /// same reduction-before-the-write contract Sanguine Ward and Mana Barrier use: this never calls
        /// UpdateVitalDelta and can never put health back.
        ///
        /// Returns the hit untouched whenever no ward is standing, which is every hit on every player who is
        /// not a Spellsword mid-fight - that early-out is what keeps an always-cached handler (see
        /// RunicWardAbility.RunsWithoutLearnedRank) free on the hot path.
        ///
        /// NO RANK CHECK, deliberately: the ward's size was fixed by rank when it was inscribed, and a pool
        /// already standing must keep draining after an unlearn rather than being stranded.
        /// </summary>
        public uint AbsorbWithRunicWard(uint incomingDamage)
        {
            if (incomingDamage == 0 || runicWard.Amount == 0)
                return incomingDamage;

            var result = RunicWardMath.Absorb(runicWard, incomingDamage, Time.GetUnixTime());

            runicWard = result.Remaining;

            if (result.Absorbed == 0)
                return incomingDamage;

            // NO PER-HIT LINE - beta feedback called the old one-line-per-hit chatter the loudest spam in
            // the ability. The running total is told once, when the lifetime ends.
            runicWardLifetimeAbsorbed += result.Absorbed;

            // the absorb that empties the pool ends the lifetime - shatter is the one place that total gets
            // told to the player
            if (runicWard.Amount == 0)
            {
                SendClassAbilityBuffMessage(RunicWardMessages.ShatterMessage(runicWardLifetimeAbsorbed));
                ApplyVisualEffects(PlayScript.EnchantDownBlue);

                runicWardLifetimeAbsorbed = 0;
                runicWardFullyInscribedAnnounced = false;
            }

            return result.DamageAfterWard;
        }

        /// <summary>
        /// The offensive half of the choice: a landed WAR spell cashes the entire remaining ward as FLAT
        /// extra damage on that spell, and the pool is emptied whether or not it was worth anything. Read
        /// from SpellProjectile.CalculateDamage's war/void branch, the only place a spell's damage can still
        /// be changed; returns 0 for every other school and for an empty or lapsed pool.
        ///
        /// NO RANK CHECK, matching the absorb half above - the pool itself is the authority, so an unlearned
        /// Spellsword can still cash a ward they inscribed while they held the ability rather than having it
        /// become undrainable.
        ///
        /// A MULTI-PROJECTILE WAR CAST SPENDS IT ON THE BOLT THAT LANDS FIRST, because this is read per
        /// projectile. That is the same "first strike spends it" shape Adrenaline uses, and it is what keeps
        /// the whole ward from being multiplied across every bolt of a Ring.
        /// </summary>
        public float SpendRunicWardIntoWarSpell(Spell spell)
        {
            if (spell == null || spell.School != MagicSchool.WarMagic || runicWard.Amount == 0)
                return 0.0f;

            var spent = RunicWardMath.Spend(runicWard, Time.GetUnixTime());

            // the spend ends the lifetime, same as a shatter or a lapse - reset here, before the
            // empty-spend guard below, so a war spell landing on an expired-but-not-yet-swept ward
            // still clears the running total and the announce flag rather than leaving them stale
            runicWard = default;
            runicWardLifetimeAbsorbed = 0;
            runicWardFullyInscribedAnnounced = false;

            if (spent == 0)
                return 0.0f;

            SendClassAbilityBuffMessage(RunicWardMessages.DischargeMessage(spent, spell.Name));
            ApplyVisualEffects(PlayScript.EnchantUpWhite);

            return spent;
        }

        // ---- Soul Jump (Void/Summon T3): a combat pet dies in the summoner's place --------------------

        /// <summary>
        /// Unix time the next save becomes available; 0 (the initial value) means ready. Transient, so a
        /// relog clears the cooldown - accepted for the same reason every other buff in this family resets
        /// on login, and the alternative is a persisted property for a minutes-long timer.
        /// </summary>
        private double soulJumpReadyTime;

        /// <summary>
        /// The death save itself, called from SoulJumpAbility's pre-write hook at the DeathSave band with
        /// the damage that is still heading for Health AFTER every mitigation ahead of it has run. Returns
        /// what should now be written to the vital: the incoming damage untouched when the save does not
        /// fire, or the figure that leaves the summoner on exactly the restore line when it does.
        ///
        /// RUNNING LAST IS WHAT MAKES THIS CORRECT. The lethality test is `damage >= Health.Current`, so a
        /// blow a ward or a barrier has already defanged is no longer lethal by the time this sees it and
        /// the save is not spent. A save placed ahead of those mitigations would burn a pet and a
        /// minutes-long cooldown on hits the player was going to survive anyway.
        ///
        /// THE PET DIES BY THE ATTACKER'S HAND, not the summoner's: Smite is called with the attacking
        /// creature as the killer, so the death is attributed the way any other monster kill of a pet is and
        /// the summoner is not recorded as having killed their own summons. That also routes through
        /// CombatPet.OnDeath, which arms Soul Tether's free resummon exactly as a normal pet death does.
        /// </summary>
        public uint TrySoulJumpSave(uint incomingDamage, Creature attacker, double cooldownSeconds, double restoreFraction)
        {
            if (incomingDamage == 0 || attacker == null)
                return incomingDamage;

            var now = Time.GetUnixTime();

            var pet = GetLiveCombatPet();

            if (!SoulJumpMath.SavesFromDeath(incomingDamage, Health.Current, pet != null, now >= soulJumpReadyTime))
                return incomingDamage;

            var save = SoulJumpMath.Resolve(Health.Current, Health.MaxValue, restoreFraction);

            soulJumpReadyTime = now + Math.Max(0.0, cooldownSeconds);

            var petName = pet.Name;

            pet.Smite(attacker);

            // Only a summoner already BELOW the restore line needs health added; one above it is brought
            // down to the line by the damage this returns, so the save can never read as a heal.
            if (Health.Current < save.HealthAfterSave)
                UpdateVitalDelta(Health, (int)(save.HealthAfterSave - Health.Current));

            SendClassAbilityBuffMessage($"{petName} dies in your place! You are restored to {save.HealthAfterSave:N0} health.");
            ApplyVisualEffects(PlayScript.EnchantUpPurple);

            return save.DamageAfterSave;
        }

        /// <summary>
        /// This summoner's first live combat pet, or null. Both slots are checked because Summon 2x gives a
        /// second one, and a pet that is dead or already destroyed cannot be spent.
        /// </summary>
        private CombatPet GetLiveCombatPet()
        {
            if (CurrentActivePet is CombatPet primary && !primary.IsDead && !primary.IsDestroyed)
                return primary;

            if (SecondaryActivePet is CombatPet secondary && !secondary.IsDead && !secondary.IsDestroyed)
                return secondary;

            return null;
        }
    }
}
