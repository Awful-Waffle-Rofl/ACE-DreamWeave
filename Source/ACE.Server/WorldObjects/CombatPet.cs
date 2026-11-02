using System;
using System.Collections.Generic;

using ACE.Entity;
using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Entity.Models;
using ACE.Server.ClassAbilities.Abilities;
using ACE.Server.Entity;
using ACE.Server.MonsterEffects;

namespace ACE.Server.WorldObjects
{
    /// <summary>
    /// Summonable monsters combat AI
    /// </summary>
    public partial class CombatPet : Pet
    {
        /// <summary>
        /// CORRECTNESS FLOOR, not a balance value - guards against sign inversion only. Empowered Summons'
        /// stat multiplier (Leadership affinity) is UNBOUNDED (see ClassAbilityAffinity.Multiplier /
        /// PropertyManager.cs:1200's live-observed 5226 effective skill, which is exactly why the proc-chance
        /// family has class_ability_affinity_chance_cap and this rider currently has nothing equivalent). Left
        /// unclamped, a resist float multiplied by (1 - bonusFraction) crosses zero once bonusFraction passes
        /// 1.0 and goes negative beyond that, which flips incoming elemental damage of that type into HEALING
        /// - not merely "too strong", a change of sign. Applied only at this multiplicative floor, not inside
        /// StatMultiplier itself, because that same multiplier also legitimately feeds Health/ArmorLevel/
        /// accuracy/DamageRating, where an arbitrarily large value is merely strong, never inverted.
        /// 0.05 keeps a sliver of incoming damage of every element always landing (5%) rather than clamping to
        /// exactly 0.0, which would still be "no damage ever" - a different, silent failure mode - and rather
        /// than something looser that leaves more headroom for the same inversion under a still-larger rider.
        /// This is a floor, not a design cap: how strong Empowered Summons should be is a separate question
        /// the repo owner is deciding independently.
        /// </summary>
        private const double MinResistMod = 0.05;

        /// <summary>
        /// A new biota be created taking all of its values from weenie.
        /// </summary>
        public CombatPet(Weenie weenie, ObjectGuid guid) : base(weenie, guid)
        {
            SetEphemeralValues();
        }

        /// <summary>
        /// Restore a WorldObject from the database.
        /// </summary>
        public CombatPet(Biota biota) : base(biota)
        {
            SetEphemeralValues();
        }

        private void SetEphemeralValues()
        {
            // Custom Summons: the two combat-role flags are weenie data that never change while the pet is out,
            // so they are read once here rather than on every tick and every target candidate
            NeverInitiates = GetProperty(PropertyBool.SummonNeverInitiates) ?? false;
            SummonTauntInterval = GetProperty(PropertyFloat.SummonTauntInterval) ?? 0.0;

            // Custom Summons Task 6: a caster brain pet never starts a fight, whatever its weenie says
            if (GetProperty(PropertyBool.SummonCasterBrain) ?? false)
                MarkCasterPet();
        }

        /// <summary>
        /// Puts this pet on the caster brain (CombatPet_Caster): from the weenie's SummonCasterBrain flag, or from a
        /// bound spell at summon time. A caster also never initiates (design doc: "Ranged summons never initiate
        /// aggro" applies to all casters).
        /// </summary>
        private void MarkCasterPet()
        {
            IsCasterPet = true;
            NeverInitiates = true;
        }

        public override bool? Init(Player player, PetDevice petDevice, PetSummonSource source, bool spawnStagger = false)
        {
            var success = base.Init(player, petDevice, source, spawnStagger);

            if (success == null || !success.Value)
                return success;

            // everything that shapes the pet from its source and its owner's Empowered Summons lives in
            // ApplySummonStats, which touches no world state so it can be tested without a Player
            ApplySummonStats(source, player.GetEmpoweredSummonsStatMod(), player.GetEmpoweredSummonsDurationMod());

            // the stance switch animates and broadcasts, so it is the world-touching half and stays here
            SetCombatMode(SummonStance);

            MonsterState = State.Awake;
            IsAwake = true;

            // class ability Summon 2x: put the owner's other combat pet, if any, on this one's expiry clock.
            // Must run AFTER the Lifespan bump in ApplySummonStats - the sibling adopts this pet's whole
            // clock, Lifespan included - which is why it is called here rather than from Pet.Init alongside
            // the slot write.
            SyncSiblingCombatPetLifespan(player);

            // are CombatPets supposed to attack monsters that are in the same faction as the pet owner?
            // if not, there are a couple of different approaches to this
            // the easiest way for the code would be to simply set Faction1Bits for the CombatPet to match the pet owner's
            // however, retail pcaps did not contain Faction1Bits for CombatPets

            // doing this the easiest way for the code here, and just removing during appraisal
            Faction1Bits = player.Faction1Bits;

            return true;
        }

        /// <summary>
        /// The combat stance a freshly summoned pet takes. A caster (it carries a bound spell) never melees, so
        /// it takes Magic; otherwise Missile when the pet weenie is ranged, else Melee - which is what every
        /// retail pet has always been. A ranged caster is still a caster.
        /// </summary>
        internal static CombatMode ChooseSummonStance(bool hasBoundSpells, bool isRanged)
        {
            if (hasBoundSpells)
                return CombatMode.Magic;

            return isRanged ? CombatMode.Missile : CombatMode.Melee;
        }

        /// <summary>
        /// The stance ApplySummonStats chose for this pet. Init performs the actual switch
        /// (SetCombatMode(SummonStance)) because that animates and broadcasts; this is the decision alone.
        /// </summary>
        public CombatMode SummonStance { get; private set; } = CombatMode.Melee;

        /// <summary>
        /// Shapes this pet from <paramref name="source"/> and the owner's Empowered Summons multipliers:
        /// gear ratings, combat stance (recorded in <see cref="SummonStance"/>), the Empowered Summons stat
        /// bump (health, damage, armor, resists, attack and magic skills), the Loyalty lifespan bump, and the
        /// bound spells. Reads nothing from the owner or the world and sends nothing - the owner's multipliers
        /// arrive as <paramref name="statMod"/> and <paramref name="durationMod"/> (1.0 = none), so a retail
        /// source with no bound spells comes out exactly as the inline code in Init always made it.
        /// </summary>
        public void ApplySummonStats(PetSummonSource source, float statMod, float durationMod)
        {
            SummonStance = ChooseSummonStance(source.HasBoundSpells, IsRanged);

            if (source.HasBoundSpells)
                MarkCasterPet();

            // copy ratings from the summon source
            DamageRating = source.GearDamage;
            DamageResistRating = source.GearDamageResist;
            CritDamageRating = source.GearCritDamage;
            CritDamageResistRating = source.GearCritDamageResist;
            CritRating = source.GearCrit;
            CritResistRating = source.GearCritResist;

            // class ability: Empowered Summons scales the pet's stats (health, damage, defenses). A rating
            // point is ~1% here, so the same fraction feeds health (as a %) and the rating pools (as points).
            // DamageRating/DamageResistRating/CritDamageResistRating are the pre-existing coverage and are
            // left exactly as they were.
            //
            // WIDENED 2026-09-12 per the signed-off design text (tools/ca-workbench/workspace.json rev 50):
            // "pets now gain health, armor, elemental resistances, accuracy and damage". The five named axes,
            // and the field each maps to:
            //   - health              -> Health (already handled above/below, unchanged)
            //   - armor               -> ArmorLevel
            //   - elemental resistances -> the per-damage-type resist floats a Creature carries (ResistSlash/
            //                            Pierce/Bludgeon/Fire/Cold/Acid/Electric/Nether) - lower is better, so
            //                            these are REDUCED by bonusFraction rather than added to
            //   - accuracy            -> the pet's current attack skill (GetCurrentAttackSkill/
            //                            GetCreatureSkill), the closest real analogue to "accuracy" a
            //                            non-player Creature has - bumped via CreatureSkill.InitLevel
            //   - damage              -> DamageRating (pre-existing, unchanged)
            // No new tunable was needed - this still reuses class_ability_empoweredsummons_percent_per_rank
            // and the existing Leadership rider unchanged; only the set of properties it reaches grew.
            if (statMod > 1.0f)
            {
                var bonusFraction = statMod - 1.0f;

                Health.StartingValue += (uint)Math.Round(Health.MaxValue * bonusFraction);
                Health.Current = Health.MaxValue;

                var ratingBonus = (int)Math.Round(bonusFraction * 100.0f);
                DamageRating = (DamageRating ?? 0) + ratingBonus;
                DamageResistRating = (DamageResistRating ?? 0) + ratingBonus;
                CritDamageResistRating = (CritDamageResistRating ?? 0) + ratingBonus;

                // armor
                if (ArmorLevel.HasValue)
                    ArmorLevel = (int)Math.Round(ArmorLevel.Value * statMod);

                // elemental resistances: multiplicative, 1.0 = neutral, LOWER = more resistant (see
                // Creature_Properties.ResistXMod, which multiplies this straight into damage taken). Reducing
                // by bonusFraction is the resistance-side mirror of the rating bonuses above.
                //
                // CLAMPED to MinResistMod (see that constant's doc comment, and
                // EmpoweredSummonsAbility.ResistMultiplier which owns the pure formula) - bonusFraction is
                // UNBOUNDED (Leadership affinity has no cap here), so an unclamped (1.0 - bonusFraction)
                // crosses zero and goes negative, which would turn incoming elemental damage into healing.
                // This is a sign-inversion guard, not a strength cap.
                var resistMod = EmpoweredSummonsAbility.ResistMultiplier(bonusFraction, MinResistMod);
                ResistSlash = (ResistSlash ?? 1.0) * resistMod;
                ResistPierce = (ResistPierce ?? 1.0) * resistMod;
                ResistBludgeon = (ResistBludgeon ?? 1.0) * resistMod;
                ResistFire = (ResistFire ?? 1.0) * resistMod;
                ResistCold = (ResistCold ?? 1.0) * resistMod;
                ResistAcid = (ResistAcid ?? 1.0) * resistMod;
                ResistElectric = (ResistElectric ?? 1.0) * resistMod;
                ResistNether = (ResistNether ?? 1.0) * resistMod;

                // accuracy: the pet's current attack skill is its weapon skill (melee or missile; a pet with
                // no weapon uses Unarmed). InitLevel is the same "bonus baked into the skill record" field a
                // player's character-creation bonus uses, so bumping it here scales Current (and everything
                // derived from it, including to-hit) permanently for this pet's lifetime.
                var attackSkillId = GetCurrentAttackSkill();
                var attackSkill = GetCreatureSkill(attackSkillId);
                if (attackSkill != null)
                    attackSkill.InitLevel += (uint)Math.Round(attackSkill.Current * bonusFraction);

                // a caster's spell power rides on its magic school skills, so they get the same bump the
                // attack skill got. Only schools the pet already has a row for (GetCreatureSkill with
                // add: false never creates one), and never the attack skill a second time.
                foreach (var school in MagicSchoolSkills)
                {
                    if (school == attackSkillId)
                        continue;

                    var magicSkill = GetCreatureSkill(school, false);
                    if (magicSkill != null)
                        magicSkill.InitLevel += (uint)Math.Round(magicSkill.Current * bonusFraction);
                }
            }

            // class ability: Empowered Summons' Loyalty rider lengthens how long the pet stays out. Read
            // at summon time (by Init), for the same reason the stat half above is: the pet keeps the
            // duration it was summoned with even if the owner's Loyalty changes while it is in the world.
            // Applied to the weenie's own Lifespan (43 s on every combat-pet weenie) rather than a
            // replacement value, so a pet weenie that ever carries a different number still scales
            // proportionally.
            if (durationMod > 1.0f && Lifespan.HasValue && Lifespan.Value > 0)
                Lifespan = (int)Math.Round(Lifespan.Value * durationMod);

            // a caster's bound spells go into its runtime spellbook, which is what the shared monster spell
            // path (HasKnownSpells, TryRollSpell) reads. A retail source has none, so nothing is added.
            if (source.BoundSpell1.HasValue)
                Biota.GetOrAddKnownSpell((int)source.BoundSpell1.Value, BiotaDatabaseLock, out _);

            if (source.BoundSpell2.HasValue)
                Biota.GetOrAddKnownSpell((int)source.BoundSpell2.Value, BiotaDatabaseLock, out _);
        }

        /// <summary>The five magic school skills Empowered Summons raises alongside the attack skill.</summary>
        private static readonly Skill[] MagicSchoolSkills =
        {
            Skill.WarMagic, Skill.LifeMagic, Skill.CreatureEnchantment, Skill.ItemEnchantment, Skill.VoidMagic
        };

        /// <summary>
        /// Soul Tether reduces the damage this pet takes. Read here - at the pet's incoming-damage site, on
        /// every hit - rather than at summon time, so a rank learned or unlearned while the pet is already
        /// in the world takes effect immediately. Returns the multiplier to 1.0 (no change) when the owner
        /// is gone or has not learned the ability.
        /// </summary>
        public override uint TakeDamage(WorldObject source, DamageType damageType, float amount, bool crit = false, IncomingDamageOrigin origin = IncomingDamageOrigin.Secondary)
        {
            var mod = P_PetOwner?.GetSoulTetherDamageReductionMod() ?? 1.0f;

            if (mod < 1.0f && amount > 0.0f)
                amount *= mod;

            return base.TakeDamage(source, damageType, amount, crit, origin);
        }

        /// <summary>
        /// Notifies the owner that its combat pet has fallen, which arms Soul Tether's resummon-cooldown
        /// skip. Recorded for every owner (the ability is checked when the skip is read), so the flag stays
        /// accurate if the ability is learned between the pet's death and the resummon.
        /// </summary>
        public override DeathMessage OnDeath(DamageHistoryInfo lastDamager, DamageType damageType, bool criticalHit = false)
        {
            P_PetOwner?.OnCombatPetDied();

            return base.OnDeath(lastDamager, damageType, criticalHit);
        }

        /// <summary>
        /// Runs first on every monster tick (Monster_Tick), before the idle-follow and attack branches.
        ///
        /// Summon assist (WaffleACE, 2026-09-10, /summonattackontarget, on by default): when the owner's setting
        /// is on and the monster the owner most recently attacked is a valid target for this pet, the pet
        /// switches to it. PULL model: Player.OnAttackMonster only records the monster - it can run on a
        /// projectile-collision path - and the pet reads it here, on its own tick, so no pet state is ever
        /// written from the owner's side. Both Summon 2x slots get this with no extra wiring, since each pet
        /// reads its own owner. When that monster dies or becomes invalid, the default nearest-monster pick
        /// takes over; when that finds nothing the target is dropped, and Monster_Tick's no-target branch sends
        /// the pet back to its owner (CombatPet_Follow).
        /// </summary>
        public override void HandleFindTarget()
        {
            // the follow and reach tunables, once per tick (see CombatPet_Follow.tickSettings)
            tickSettings = PetFollowSettings.Read();

            // Custom Summons: a never-initiates pet (Archer, casters) holds its current target only while that
            // monster is still engaged with its side, and takes an assist target only on the same terms. Read
            // once per tick. False for every other pet, which keeps exactly today's rule.
            var neverInitiates = NeverInitiates;

            var current = AttackTarget as Creature;
            var currentValid = current != null && !current.IsDead && IsVisibleTarget(current)
                && PetEngagement.HoldsCurrentTarget(neverInitiates, current, P_PetOwner);

            var owner = P_PetOwner;
            var assistCandidate = owner?.LastAttackedMonster;

            // the property read is skipped when there is nothing to assist with
            var assistOn = assistCandidate != null && owner.SummonAttackOnTarget;
            var assistValid = assistOn && IsValidAssistTarget(assistCandidate, neverInitiates);

            switch (PetAssistTargeting.Decide(assistOn, assistValid, ReferenceEquals(assistCandidate, AttackTarget), currentValid, IsAnimating,
                CasterHoldsTarget(current, currentValid)))
            {
                case PetTargetAction.SwitchToAssist:
                    SwitchAttackTarget(assistCandidate);
                    break;

                case PetTargetAction.FindNearest:
                    // FindNextTarget leaves AttackTarget alone when nothing is in range, so an invalid target used to
                    // stay in AttackTarget indefinitely - which kept the pet in Monster_Tick's attack branch, standing
                    // where the fight ended, and kept it out of the idle follow. Decide only returns FindNearest when
                    // the current target is NOT valid (null, dead, or no longer in this pet's visible set), so drop it
                    // whenever nothing replaces it. Until 2026-09-21 a live target that had left the visible set was
                    // kept, and a monster alive behind a wall held the pet in the attack branch forever
                    // (Monster_Tick skips its own visibility recheck for combat pets).
                    if (!FindNextTarget() && AttackTarget != null)
                        DropAttackTarget();
                    break;
            }

            // a target acquired while running back to the owner must end that run now: Monster_Tick's attack
            // branch sees IsMoving and calls Movement(), which would carry the pet on to its owner instead of
            // turning toward the new target. Not for a caster pet: holding a target does not mean it acts on
            // it (every spell may be Redundant there, and then it follows), so TickCaster ends the follow itself
            // exactly when it starts a cast or a chase - stopping here would cancel that follow every tick.
            if (AttackTarget != null && !IsCasterPet)
                StopFollowingOwner();

            CheckTargetReachable();
        }

        /// <summary>
        /// The unreachable-target rule (PetTargetReachTracker). Samples every tick the pet has a target.
        /// </summary>
        private readonly PetTargetReachTracker targetReach = new PetTargetReachTracker();

        /// <summary>Targets dropped as unreachable, skipped by IsPetTargetCandidate until each expires.</summary>
        private readonly PetTargetIgnoreList unreachableTargets = new PetTargetIgnoreList();

        /// <summary>
        /// Drops a target the pet has neither closed on nor attacked for pet_combat_unreachable_seconds, and
        /// ignores it for pet_combat_unreachable_ignore_seconds so FindNextTarget (and the assist pick) does not
        /// hand it straight back. With no target the next Monster_Tick branch is the idle follow, whose own
        /// teleport triggers take over. NOT a leash: the owner's position is never consulted, so a pet fighting
        /// far from its owner is untouched as long as it keeps reaching its target.
        /// </summary>
        private void CheckTargetReachable()
        {
            var target = AttackTarget as Creature;

            if (target?.Location == null || Location == null)
            {
                targetReach.Reset();
                return;
            }

            var now = Timers.RunningTime;

            if (!targetReach.Update(target, now, Location.ToGlobal(), target.Location.ToGlobal(), tickSettings.UnreachableSeconds))
                return;

            unreachableTargets.Ignore(target.Guid.Full, now, tickSettings.UnreachableIgnoreSeconds);

            targetReach.Reset();

            DropAttackTarget();
        }

        /// <summary>
        /// Called by Monster_Tick right after this pet starts an attack on its current target: a pet that is
        /// swinging at, shooting at or casting on its target has plainly reached it, so the unreachable clock
        /// restarts even though the pet is not moving any closer.
        /// </summary>
        public void NotifyAttackOnTarget()
        {
            targetReach.NotifyAttack(AttackTarget, Timers.RunningTime);
        }

        /// <summary>
        /// Whether the owner's most recently attacked monster is a target this pet may take: alive, in this
        /// pet's own visible targets (so the same landblock instance, a monster, and in view), passing the
        /// same filter as GetNearbyMonsters, and inside MaxChaseRange - Movement() abandons any chase at that
        /// range, so a farther assist target would be re-picked on the next tick and abandoned again, forever.
        /// </summary>
        private bool IsValidAssistTarget(Creature candidate, bool neverInitiates)
        {
            if (ReferenceEquals(candidate, this) || candidate.IsDead)
                return false;

            if (!IsVisibleTarget(candidate))
                return false;

            if (!AcceptsPetTarget(candidate, neverInitiates))
                return false;

            // the same cylinder distance GetDistanceToTarget measures for the MaxChaseRange cancel in Movement()
            return GetCylinderDistance(candidate) < MaxChaseRange;
        }

        /// <summary>Moves this pet onto a new target from whatever it was doing (see AbandonCurrentApproach).</summary>
        private void SwitchAttackTarget(Creature target)
        {
            AbandonCurrentApproach();
            AttackTarget = target;
        }

        /// <summary>Drops a dead target when no replacement is in range, so the next tick takes the idle branch.</summary>
        private void DropAttackTarget()
        {
            AbandonCurrentApproach();
            AttackTarget = null;
        }

        /// <summary>
        /// Clears the per-target attack and movement state, so the next attack-branch tick starts clean: any
        /// MoveTo/TurnTo still heading for the old target is cancelled, the melee stick is released, and
        /// ResetAttack() clears CurrentAttack and MaxRange so both are recomputed for the new target. A strike
        /// already scheduled by MeleeAttack still lands on the target it was swung at (its closure captured
        /// that target); PetAssistTargeting.Decide defers a switch until such an animation has finished.
        /// </summary>
        private void AbandonCurrentApproach()
        {
            if (IsMoving || IsTurning)
                CancelPhysicsMoveTo();

            // MeleeAttack sticks the pet to its target. A new MoveTo would unstick it
            // (MoveToManager.PerformMovement), but a ranged pet turning in place never issues one.
            PhysicsObj?.unstick_from_object();

            ResetAttack();
        }

        public override bool FindNextTarget()
        {
            var nearbyMonsters = GetNearbyMonsters(NeverInitiates);
            if (nearbyMonsters.Count == 0)
            {
                //Console.WriteLine($"{Name}.FindNextTarget(): empty");
                return false;
            }

            // get nearest monster
            var nearest = BuildTargetDistance(nearbyMonsters, true);

            if (nearest[0].Distance > VisualAwarenessRangeSq)
            {
                //Console.WriteLine($"{Name}.FindNextTarget(): next object out-of-range (dist: {Math.Round(Math.Sqrt(nearest[0].Distance))})");
                return false;
            }

            AttackTarget = nearest[0].Target;

            //Console.WriteLine($"{Name}.FindNextTarget(): {AttackTarget.Name}");

            return true;
        }

        /// <summary>
        /// Returns a list of attackable monsters in this pet's visible targets. With
        /// <paramref name="neverInitiates"/> (a SummonNeverInitiates pet), only monsters already engaged with
        /// the owner or the owner's pets (PetEngagement.IsEngagedWithOwner).
        /// </summary>
        public List<Creature> GetNearbyMonsters(bool neverInitiates = false)
        {
            var monsters = new List<Creature>();

            foreach (var creature in PhysicsObj.ObjMaint.GetVisibleTargetsValuesOfTypeCreature())
            {
                if (AcceptsPetTarget(creature, neverInitiates))
                    monsters.Add(creature);
            }

            return monsters;
        }

        /// <summary>
        /// Custom Summons (PropertyBool 9088): this pet never starts a fight. It skips PetCheckMonsters' waking,
        /// and picks targets - nearest, owner assist, and multishot extra arrows - only among monsters already
        /// engaged with its owner or the owner's pets. The Archer and every caster carry it; Tank, Melee and
        /// every retail pet do not, and behave exactly as before. Cached from the weenie at construction
        /// (SetEphemeralValues).
        /// </summary>
        public bool NeverInitiates { get; private set; }

        /// <summary>
        /// Custom Summons (item 3 of the review): whether this pet may loose a missile at
        /// <paramref name="target"/> at the moment of release. A never-initiates pet holds fire unless the
        /// target is still engaged with its owner's side - a monster can drop out of combat (go Return, fall
        /// asleep) during the wind-up, and an arrow landing then would wake it. Every other pet always may.
        /// </summary>
        internal bool MayLooseAt(WorldObject target) =>
            !NeverInitiates || (target is Creature monster && PetEngagement.IsEngagedWithOwner(monster, P_PetOwner));

        /// <summary>
        /// Custom Summons Tank (PropertyFloat 9021): seconds between taunt pulses, 0 for every other pet. Cached
        /// from the weenie at construction (SetEphemeralValues).
        /// </summary>
        public double SummonTauntInterval { get; private set; }

        /// <summary>
        /// The whole target rule for this pet: IsPetTargetCandidate (alive, attackable, not ignored as
        /// unreachable, faction), plus, for a never-initiates pet, engagement with the owner's side. Reads the
        /// candidate monster's AttackTarget across objects - see PetEngagement for why that read is safe on the
        /// parallel tick.
        /// </summary>
        internal bool AcceptsPetTarget(Creature creature, bool neverInitiates)
        {
            if (!IsPetTargetCandidate(creature))
                return false;

            return !neverInitiates || PetEngagement.IsEngagedWithOwner(creature, P_PetOwner);
        }

        /// <summary>
        /// A pet's multishot extra arrows obey its own target rule (AcceptsPetTarget): never a dead,
        /// unattackable or same-faction creature, and for a never-initiates pet never a monster that is not
        /// already engaged - so an extra arrow can never wake an idle monster. Splash from a caster's ring or
        /// blast spell is deliberately NOT filtered (owner ruling 2026-11-01): only the primary must be engaged.
        /// </summary>
        public override bool AcceptsMultiShotExtraTarget(Creature creature) => AcceptsPetTarget(creature, NeverInitiates);

        /// <summary>
        /// The pet half of Monster_Tick's combat branch, run after this pet's attack step: wakes nearby monsters
        /// (PetCheckMonsters, today's behaviour) unless this is a never-initiates pet, then fires the Tank's
        /// taunt pulse when one is due.
        /// </summary>
        public void PetCombatTick()
        {
            // returns at once for a never-initiates pet
            PetCheckMonsters();

            TickTauntPulse();
        }

        /// <summary>Timers.RunningTime at which the next taunt pulse may fire. Runtime only.</summary>
        private double nextTauntPulseAt;

        /// <summary>
        /// The pulse clock, pure over its inputs: true (and the next pulse scheduled <paramref name="interval"/>
        /// seconds out) when a pulse is due at <paramref name="now"/>. A non-positive interval never pulses. The
        /// first combat tick of a Tank pulses at once.
        /// </summary>
        internal bool TryConsumeTauntPulse(double now, double interval)
        {
            if (interval <= 0.0 || now < nextTauntPulseAt)
                return false;

            nextTauntPulseAt = now + interval;
            return true;
        }

        /// <summary>
        /// Custom Summons Tank: every SummonTauntInterval seconds (PropertyFloat 9021, read from the pet's weenie),
        /// taunt every monster already engaged with the owner's side within the player Taunt ability's rank-3
        /// radius, for the player Taunt's base hold. Never wakes an idle monster. A pet without the property
        /// never pulses.
        ///
        /// THREADING: runs on this pet's landblock tick. Each candidate is only WRITTEN (ApplyTaunt) when it is
        /// in this pet's own landblock group - the same cross-group guard Monster_Magic.TryRollSpell uses - and
        /// in direct line of sight, as the player Taunt requires.
        /// </summary>
        public void TickTauntPulse()
        {
            if (!TryConsumeTauntPulse(Timers.RunningTime, SummonTauntInterval))
                return;

            ApplyTauntPulse(PhysicsObj.ObjMaint.GetVisibleTargetsValuesOfTypeCreature(), CanTauntReach);
        }

        /// <summary>
        /// The pulse over an explicit candidate set: taunts each PetEngagement.IsTauntPulseCandidate monster that
        /// <paramref name="canReach"/> accepts, and returns how many were taunted. Split from TickTauntPulse so a
        /// test can drive the real ApplyTaunt without a landblock.
        /// </summary>
        internal int ApplyTauntPulse(IEnumerable<Creature> candidates, Func<Creature, bool> canReach)
        {
            var owner = P_PetOwner;

            if (owner == null || Location == null)
                return 0;

            var radius = TauntAbility.RadiusForRank(TauntAbility.TopRank);
            var duration = TauntAbility.EffectiveDuration(TauntAbility.BaseDuration());

            var taunted = 0;

            foreach (var monster in candidates)
            {
                if (monster?.Location == null)
                    continue;

                if (!PetEngagement.IsTauntPulseCandidate(monster, owner, Location.DistanceTo(monster.Location), radius))
                    continue;

                if (!canReach(monster))
                    continue;

                monster.ApplyTaunt(this, duration);
                taunted++;
            }

            return taunted;
        }

        /// <summary>The world-touching half of the pulse filter: same landblock group, then line of sight.</summary>
        private bool CanTauntReach(Creature monster)
        {
            if (CurrentLandblock == null || monster.CurrentLandblock == null ||
                CurrentLandblock.CurrentLandblockGroup != monster.CurrentLandblock.CurrentLandblockGroup)
                return false;

            return IsDirectVisible(monster);
        }

        /// <summary>
        /// The per-creature filter GetNearbyMonsters applies to this pet's visible targets. Split out so the
        /// summon-assist target (IsValidAssistTarget) is held to exactly the same rule.
        /// </summary>
        private bool IsPetTargetCandidate(Creature creature)
        {
            // why does this need to be in here?
            if (creature.IsDead || !creature.Attackable || creature.Visibility)
            {
                //Console.WriteLine($"{Name}.GetNearbyMonsters(): refusing to add dead creature {creature.Name} ({creature.Guid})");
                return false;
            }

            // recently dropped as unreachable (CheckTargetReachable): skipped until its ignore window ends
            if (unreachableTargets.IsIgnored(creature.Guid.Full, Timers.RunningTime))
                return false;

            // combat pets do not aggro monsters belonging to the same faction as the pet owner?
            if (SameFaction(creature))
            {
                // unless the pet owner or the pet is being retaliated against?
                if (!creature.HasRetaliateTarget(P_PetOwner) && !creature.HasRetaliateTarget(this))
                    return false;
            }

            return true;
        }

        public override void Sleep()
        {
            // pets dont really go to sleep, per say
            // they keep scanning for new targets,
            // which is the reverse of the current ACE jurassic park model

            return;  // empty by default
        }
    }
}
