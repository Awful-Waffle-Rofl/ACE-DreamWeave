using System;
using System.Collections.Generic;

using ACE.Common;
using ACE.DatLoader;
using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Entity.Models;
using ACE.Server.Entity;
using ACE.Server.Entity.Actions;
using ACE.Server.Managers;
using ACE.Server.Network.GameMessages.Messages;
using ACE.Server.Physics.Animation;

namespace ACE.Server.WorldObjects
{
    /// <summary>
    /// Monster casting for magic spells
    /// </summary>
    partial class Creature
    {
        private bool AiUsesMana
        {
            get => GetProperty(PropertyBool.AiUsesMana) ?? true;    // default true?
            set { if (!value) RemoveProperty(PropertyBool.AiUsesMana); else SetProperty(PropertyBool.AiUsesMana, value); }
        }

        /// <summary>
        /// Very specific monsters will be set to use the human casting animations,
        /// ie. the windup and casting gestures from the spell
        /// </summary>
        private bool AiUseHumanMagicAnimations
        {
            get => GetProperty(PropertyBool.AiUseHumanMagicAnimations) ?? false;
            set { if (!value) RemoveProperty(PropertyBool.AiUseHumanMagicAnimations); else SetProperty(PropertyBool.AiUseHumanMagicAnimations, value); }
        }

        /// <summary>
        /// The amount of time a monster waits to cast a magic spell
        /// defined as seconds from the start of the previous attack
        /// the most common value in the db is 3s
        /// some other common values include 2s and 1s, with some mobs having values up to 1m
        /// </summary>
        private double? AiUseMagicDelay
        {
            get => GetProperty(PropertyFloat.AiUseMagicDelay);
            set { if (!value.HasValue) RemoveProperty(PropertyFloat.AiUseMagicDelay); else SetProperty(PropertyFloat.AiUseMagicDelay, value.Value); }
        }

        /// <summary>
        /// Returns TRUE if monster has known spells
        /// </summary>
        private bool HasKnownSpells => Biota.HasKnownSpell(BiotaDatabaseLock);

        /// <summary>
        /// The next spell the monster will attempt to cast
        /// </summary>
        private Spell CurrentSpell { get; set; }

        /// <summary>
        /// Cached delegates for MonsterSpellSelector.TrySelect. Held statically because TryRollSpell can
        /// run up to ten times in one tick for one monster (Monster_Combat's line-of-sight reroll loop),
        /// and a method group or lambda written at the call site would allocate a fresh delegate each time.
        /// </summary>
        private static readonly Func<int, MonsterSpellShape> spellShapeLookup = MonsterSpellShapeCache.Get;

        private static readonly Func<double> spellRoll = () => ThreadSafeRandom.Next(0.0f, 1.0f);

        private bool TryRollSpell()
        {
            CurrentSpell = null;

            //Console.WriteLine($"{Name}.TryRollSpell(), probability={GetProbabilityAny()}");

            // monster spellbooks have probabilities with base 2.0
            // ie. a 5% chance would be 2.05 instead of 0.05

            // much less common, some monsters will have spells with just base 2.0 probability
            // there were probably other criteria used to select these spells (emote responses, monster ai responses)
            // for now, 2.0 base just becomes a 2% chance

            if (Biota.PropertiesSpellBook == null)
                return false;

            if (!PropertyManager.GetBool("monster_conditional_spell_selection", MonsterSpellSelector.DefaultConditionalSelectionEnabled).Item)
                return TryRollSpell_Unconditional();

            // no live target to reason about, so there is nothing to be conditional on
            if (!(AttackTarget is Creature target) || !target.IsAlive)
                return TryRollSpell_Unconditional();

            // The target's enchantment registry is about to be read, and this guard is what makes that
            // safe - it is NOT defensive boilerplate. EnchantmentManagerWithCaching memoizes into plain
            // UNSYNCHRONISED Dictionary fields and clears them wholesale in ClearCache(), so reading one
            // from another landblock group's thread can throw or spin. CastSpell already carries this
            // exact guard, for this exact reason. Falling back keeps the roll working either way.
            if (CurrentLandblock == null || target.CurrentLandblock == null ||
                CurrentLandblock.CurrentLandblockGroup != target.CurrentLandblock.CurrentLandblockGroup)
            {
                return TryRollSpell_Unconditional();
            }

            // ONCE per roll, before any pass. Re-reading this between passes would let one spell classify
            // into two tiers (rolled twice) or none (never rolled), and either breaks frequency neutrality.
            var profile = BuildVulnerabilityProfile(target);

            if (!MonsterSpellSelector.TrySelect(Biota.PropertiesSpellBook, spellShapeLookup, profile, spellRoll, out var spellId))
                return false;

            CurrentSpell = new Spell(spellId);
            return true;
        }

        /// <summary>
        /// Snapshots the debuffs already standing on the target: the seven elemental Vulnerability
        /// multipliers, the NEGATIVE-only body-armor modifier, and the top-layer entry of every spell
        /// category. Called once per roll, never per book entry.
        ///
        /// A target with no enchantments at all short-circuits to the all-identity profile, so the common
        /// case does no registry work whatsoever.
        ///
        /// GetBodyArmorMod(false), not the no-arg NET overload. The net value let Armor Self VIII (+250)
        /// cancel a standing Imperil (-225), so a buffed player read as un-imperiled and Imperil was promoted
        /// to the front of the book on every roll. The bool overload is not memoized by
        /// EnchantmentManagerWithCaching, which is why it is read exactly once here.
        ///
        /// The top layers come from ONE copy of the registry taken under its read lock (Clone), reduced by
        /// VulnerabilityProfile.BuildTopLayers. <paramref name="withDispelFacts"/> (a caster pet that holds a dispel) also
        /// resolves each layer's alignment, school and dispellability; the monster roll never asks for it, so it pays no
        /// spell lookups. Internal for ACE.Server.Tests (InternalsVisibleTo,
        /// ACE.Server.csproj:15), which builds it from a real Creature's registry.
        /// </summary>
        internal static VulnerabilityProfile BuildVulnerabilityProfile(Creature target, bool withDispelFacts = false)
        {
            var enchantments = target.EnchantmentManager;

            if (enchantments == null || !enchantments.HasEnchantments)
                return VulnerabilityProfile.None;

            var registry = target.Biota.PropertiesEnchantmentRegistry.Clone(target.BiotaDatabaseLock);

            return new VulnerabilityProfile(
                enchantments.GetVulnerabilityResistanceMod(DamageType.Slash),
                enchantments.GetVulnerabilityResistanceMod(DamageType.Pierce),
                enchantments.GetVulnerabilityResistanceMod(DamageType.Bludgeon),
                enchantments.GetVulnerabilityResistanceMod(DamageType.Cold),
                enchantments.GetVulnerabilityResistanceMod(DamageType.Fire),
                enchantments.GetVulnerabilityResistanceMod(DamageType.Acid),
                enchantments.GetVulnerabilityResistanceMod(DamageType.Electric),
                enchantments.GetBodyArmorMod(false),
                withDispelFacts
                    ? VulnerabilityProfile.BuildTopLayers(registry, EnchantmentDispelFactsCache.Resolve)
                    : VulnerabilityProfile.BuildTopLayers(registry));
        }

        /// <summary>
        /// The original roll, kept verbatim: one flat pass over the book in its own order, first
        /// successful Bernoulli trial wins. This is what runs when monster_conditional_spell_selection is
        /// off, and whenever there is no usable target to be conditional about, so turning the tunable off
        /// reproduces the previous behaviour exactly.
        /// </summary>
        private bool TryRollSpell_Unconditional()
        {
            // We don't use thread safety here. Monster spell books aren't mutated cross-threads.
            // This reduces memory consumption by not cloning the spell book every single TryRollSpell()
            //foreach (var spell in Biota.CloneSpells(BiotaDatabaseLock)) // Thread-safe
            foreach (var spell in Biota.PropertiesSpellBook) // Not thread-safe
            {
                var probability = spell.Value > 2.0f ? spell.Value - 2.0f : spell.Value / 100.0f;

                var rng = ThreadSafeRandom.Next(0.0f, 1.0f);

                if (rng < probability)
                {
                    CurrentSpell = new Spell(spell.Key);
                    return true;
                }
            }
            return false;
        }

        /// <summary>
        /// Returns the probability of this monster casting a spell for an attack
        /// </summary>
        private float GetProbabilityAny()
        {
            var probabilities = new List<float>();

            foreach (var spell in Biota.GetKnownSpellsProbabilities(BiotaDatabaseLock))
            {
                var probability = spell > 2.0f ? spell - 2.0f : spell / 100.0f;

                probabilities.Add(probability);
            }

            return Probability.GetProbabilityAny(probabilities);
        }

        /// <summary>
        /// Returns the maximum range for the current spell
        /// </summary>
        private float GetSpellMaxRange()
        {
            // Custom Summons caster pets: the range the caster brain resolved for the spell it is closing with
            // (CombatPet.TickCaster), so StartTurn and GetMovementParameters measure against the same number
            if (CasterSpellRangeOverride is float casterRange)
                return casterRange;

            var skill = GetMagicSkillForRangeCheck();

            var maxRange = Math.Min(CurrentSpell.BaseRangeConstant + skill * CurrentSpell.BaseRangeMod, Player.MaxRadarRange_Outdoors);

            if (maxRange == 0.0f)
                maxRange = float.PositiveInfinity;

            return maxRange;
        }

        /// <summary>
        /// Custom Summons: a caster pet's brain-resolved range for the spell it is closing with. Null (the formula)
        /// for every other creature, and for a caster pet that is not mid-approach.
        /// </summary>
        internal virtual float? CasterSpellRangeOverride => null;

        private bool IsSelfCast()
        {
            if (CurrentAttack != CombatType.Magic)
                return false;

            return GetSpellMaxRange() == float.PositiveInfinity;
        }

        /// <summary>
        /// Performs the monster windup spell animation,
        /// casts the spell, and returns to attack stance
        /// </summary>
        private void MagicAttack()
        {
            var target = AttackTarget as Creature;

            if (target == null || !target.IsAlive)
            {
                FindNextTarget();
                return;
            }

            var spell = CurrentSpell;

            // turn to?
            if (AiUsesMana && !UseMana()) return;

            // spell words
            if (AiUseHumanMagicAnimations)
            {
                var spellWords = spell._spellBase.GetSpellWords(DatManager.PortalDat.SpellComponentsTable);
                if (!string.IsNullOrWhiteSpace(spellWords))
                    EnqueueBroadcast(new GameMessageHearSpeech(spellWords, Name, Guid.Full, ChatMessageType.Spellcasting), LocalBroadcastRange);
            }

            // monster combat effects: both halves of the cadence are scaled on the cast axis, so a speed
            // effect shortens the windup and the recovery together rather than only the gap between casts
            var preCastTime = ScaleMonsterEffectCastTime(PreCastMotion(AttackTarget));

            var actionChain = new ActionChain();
            actionChain.AddDelaySeconds(preCastTime);
            actionChain.AddAction(this, () =>
            {
                if (IsDead || AttackTarget == null || target.IsDead)
                    return;

                // Elemental Proficiency Bludgeon stun: a cast winding up when the stun landed is lost (the mana
                // was already spent at the windup). Return to the ready stance instead of casting, so the
                // creature is not left frozen in the windup pose; the monster-effect cast hook does not fire,
                // because no cast completed.
                if (IsClassAbilityStunned)
                {
                    PostCastMotion();
                    return;
                }

                CastSpell(spell);

                // monster combat effects: the cast completed - the hook a recast rides. Bounded by
                // monster_effect_recast_cap, see the recast guard in Creature_MonsterEffects.
                OnMonsterEffectCastComplete(spell);

                PostCastMotion();
            });
            actionChain.EnqueueChain();

            var postCastTime = ScaleMonsterEffectCastTime(GetPostCastTime(spell));
            var animTime = preCastTime + postCastTime;

            //Console.WriteLine($"{Name}.MagicAttack(): preCastTime({preCastTime}), postCastTime({postCastTime})");

            // slight variation here
            PrevAttackTime = Timers.RunningTime + preCastTime;
            var powerupTime = (float)(PowerupTime ?? 1.0f);

            // Weakened Blood slow: both cast halves above are slowed inside ScaleMonsterEffectCastTime; the
            // random recovery delay is a duration too, so it stretches by the same factor
            var postDelay = ScaleWeakenedBloodSlowDuration(ThreadSafeRandom.Next(0.0f, powerupTime));

            NextMoveTime = NextAttackTime = PrevAttackTime + postCastTime + postDelay;
        }

        private bool UseMana()
        {
            // do any monsters have mana conversion?
            var target = GetSpellMaxRange() < float.PositiveInfinity ? AttackTarget : this;

            var manaUsed = CalculateManaUsage(this, CurrentSpell, target);

            if (manaUsed > Mana.Current)
                return false;

            Mana.Current -= manaUsed;
            return true;
        }

        /// <summary>Whether this creature spends mana to cast (PropertyBool.AiUsesMana, default true).</summary>
        internal bool UsesManaToCast => AiUsesMana;

        /// <summary>
        /// Whether TryStartExplicitCast could pay for <paramref name="spell"/> at <paramref name="target"/> now - the
        /// exact cost it charges. Lets a caster pet check before it stops what it is doing.
        /// </summary>
        internal bool CanPayCastMana(Spell spell, WorldObject target)
        {
            if (!AiUsesMana)
                return true;

            if (spell == null || spell.NotFound)
                return false;

            return CalculateManaUsage(this, spell, ExplicitCastManaTarget(spell, target)) <= Mana.Current;
        }

        /// <summary>The mana target MagicAttack's UseMana picks: the caster for an unbounded spell, else the target.</summary>
        private WorldObject ExplicitCastManaTarget(Spell spell, WorldObject target)
        {
            var formulaRange = Math.Min(spell.BaseRangeConstant + GetMagicSkillForRangeCheck(spell) * spell.BaseRangeMod, Player.MaxRadarRange_Outdoors);

            return formulaRange == 0.0f ? this : target;
        }

        /// <summary>
        /// Custom Summons caster pets: MagicAttack's cast, at an EXPLICIT target rather than AttackTarget - the
        /// owner or a sibling pet for a friendly spell, or whichever engaged enemy the caster brain chose for a
        /// harmful one. Same steps and cadence as MagicAttack: mana, spell words, the wind-up motion, the cast
        /// when the wind-up ends, the post-cast motion, and PrevAttackTime/NextAttackTime/NextMoveTime exactly as
        /// MagicAttack sets them (so NextMagicAttackTime gates the next cast). The pet is snapped to face the
        /// target first (FaceTargetDirectly), since the target need not be the one StartTurn turns toward.
        ///
        /// Two deliberate differences from MagicAttack: OnMonsterEffectCastComplete is called with chaining OFF
        /// (a recast re-casts at AttackTarget, which for a heal would be the enemy, so it is skipped; a ramp
        /// on=cast still accrues, which is the pet Mana Rush), and at release a harmful spell is
        /// dropped if the target has left combat with the owner's side during the wind-up (the never-initiates
        /// rule, like CombatPet.MayLooseAt). Returns FALSE, starting nothing, when the target is gone or the pet
        /// cannot pay the mana.
        /// </summary>
        internal bool TryStartExplicitCast(Spell spell, WorldObject target, bool friendly)
        {
            if (spell == null || spell.NotFound || target == null || target.IsDestroyed)
                return false;

            if (target is Creature targetCreature && !targetCreature.IsAlive)
                return false;

            uint manaUsed = 0;

            if (AiUsesMana)
            {
                manaUsed = CalculateManaUsage(this, spell, ExplicitCastManaTarget(spell, target));

                if (manaUsed > Mana.Current)
                    return false;

                Mana.Current -= manaUsed;
            }

            // PreCastMotion_Human reads CurrentSpell
            CurrentSpell = spell;

            if (AiUseHumanMagicAnimations)
            {
                var spellWords = spell._spellBase.GetSpellWords(DatManager.PortalDat.SpellComponentsTable);
                if (!string.IsNullOrWhiteSpace(spellWords))
                    EnqueueBroadcast(new GameMessageHearSpeech(spellWords, Name, Guid.Full, ChatMessageType.Spellcasting), LocalBroadcastRange);
            }

            FaceTargetDirectly(target);

            var preCastTime = ScaleMonsterEffectCastTime(PreCastMotion(target));

            var actionChain = new ActionChain();
            actionChain.AddDelaySeconds(preCastTime);
            actionChain.AddAction(this, () =>
            {
                if (IsDead || target.IsDestroyed || (target is Creature c && c.IsDead))
                    return;

                // a pet releases a harmful spell only at a monster engaged with its owner's side; the one exemption is a
                // by-design never-initiates pet whose owner has turned /summonaggro on (NeverInitiates is then false)
                if (!friendly && this is CombatPet pet && !(pet.NeverInitiatesByDesign && !pet.NeverInitiates) && !(target is Creature engaged && PetEngagement.IsEngagedWithOwner(engaged, pet.P_PetOwner)))
                    return;

                // a caster pet's heal whose target was topped up during the pre-cast (a kit, a potion, or the other
                // caster pet's heal landing first) is not released; the mana it reserved is refunded, and the
                // post-cast cooldown set below is released so the pet may act again at once (PrevAttackTime stays:
                // CasterCastReady's magic-delay gate still applies)
                if (friendly && this is CombatPet healer && !healer.FriendlyCastStillWanted(spell, target))
                {
                    Mana.Current = CombatPet.RefundedMana(Mana.Current, Mana.MaxValue, manaUsed);

                    NextMoveTime = NextAttackTime = Timers.RunningTime;

                    PostCastMotion();
                    return;
                }

                CastSpellAt(spell, target, friendly);

                // monster combat effects: the cast completed. Chaining off - see the summary above.
                OnMonsterEffectCastComplete(spell, allowChaining: false);

                PostCastMotion();
            });
            actionChain.EnqueueChain();

            var postCastTime = ScaleMonsterEffectCastTime(GetPostCastTime(spell));

            PrevAttackTime = Timers.RunningTime + preCastTime;
            var powerupTime = (float)(PowerupTime ?? 1.0f);

            var postDelay = ScaleWeakenedBloodSlowDuration(ThreadSafeRandom.Next(0.0f, powerupTime));

            NextMoveTime = NextAttackTime = PrevAttackTime + postCastTime + postDelay;

            return true;
        }

        private const float PreCastSpeed = 2.0f;
        private const float PostCastSpeed = 1.0f;
        private const float PostCastSpeed_Ranged = 1.66f;  // ??

        /// <summary>
        /// Perform the first part of monster spell casting animation - spreading arms out
        /// </summary>
        public float PreCastMotion(WorldObject target, bool fallback = false)
        {
            if (AiUseHumanMagicAnimations && !fallback)
                return PreCastMotion_Human(target);

            // a motion table with no CastSpell animation for this stance plays nothing and reports length 0, so the
            // caster stands still (lugian table 0x09000006): MonsterCastGesture swaps in a gesture the table does have
            var castSpellLength = MotionTable.GetAnimationLength(MotionTableId, CurrentMotionState.Stance, MotionCommand.CastSpell, PreCastSpeed);
            var castMotion = MonsterCastGesture.Select(MotionTableId, castSpellLength);

            var motion = new Motion(this, castMotion, PreCastSpeed);
            motion.MotionState.TurnSpeed = 2.25f;
            //motion.HasTarget = true;
            //motion.TargetGuid = target.Guid;
            CurrentMotionState = motion;

            EnqueueBroadcastMotion(motion);

            return MonsterCastGesture.PreCastLength(castMotion, castSpellLength,
                m => MotionTable.GetAnimationLength(MotionTableId, CurrentMotionState.Stance, m, PreCastSpeed));
        }

        /// <summary>
        /// For monsters with AiUseHumanMagicAnimations = true,
        /// performs the windup gestures from the spell scarabs
        /// 
        /// <returns>The amount of time for the windup gestures to complete</returns>
        private float PreCastMotion_Human(WorldObject target)
        {
            // todo: play each motion at the proper time,
            // ensuring the monster is still alive at each step
            CurrentSpell.Formula.GetMonsterFormula();

            // FIXME: data
            var castAnimTime = MotionTable.GetAnimationLength(MotionTableId, CurrentMotionState.Stance, CurrentSpell.Formula.CastGesture, PreCastSpeed);

            if (castAnimTime == 0)
                return PreCastMotion(target, true);

            var animTime = 0.0f;

            foreach (var windupGesture in CurrentSpell.Formula.WindupGestures)
            {
                var motion = new Motion(this, windupGesture, PreCastSpeed);
                motion.MotionState.TurnSpeed = 2.25f;
                CurrentMotionState = motion;

                EnqueueBroadcastMotion(motion);

                animTime += MotionTable.GetAnimationLength(MotionTableId, CurrentMotionState.Stance, windupGesture, PreCastSpeed);
            }

            var castMotion = new Motion(this, CurrentSpell.Formula.CastGesture, PreCastSpeed);
            castMotion.MotionState.TurnSpeed = 2.25f;
            CurrentMotionState = castMotion;

            EnqueueBroadcastMotion(castMotion);

            animTime += castAnimTime;

            return animTime;
        }

        /// <summary>
        /// Casts the current monster spell on target
        /// </summary>
        public void CastSpell(Spell spell)
        {
            if (AttackTarget == null) return;

            CastSpellAt(spell, AttackTarget);
        }

        /// <summary>
        /// The body of CastSpell, at an explicit target: CastSpell passes AttackTarget, and a Custom Summons caster
        /// pet passes the owner, a sibling pet or its chosen enemy (TryStartExplicitCast). With
        /// <paramref name="friendly"/>:
        ///   - the explicit target is HONOURED even for a self-targeted or untargeted spell, which CastSpell (and
        ///     HandleCastSpell) would otherwise redirect onto the caster - a caster pet's Heal Self or Strength Self
        ///     is aimed at its owner (ruling 2026-11-02);
        ///   - a life spell skips TryHandleFactionMob - a heal on a sibling pet must never start a faction fight.
        /// Everything else, the cross-landblock-group guard included, is unchanged.
        /// </summary>
        internal void CastSpellAt(Spell spell, WorldObject explicitTarget, bool friendly = false)
        {
            if (explicitTarget == null) return;

            var targetSelf = spell.Flags.HasFlag(SpellFlags.SelfTargeted);
            var untargeted = spell.NonComponentTargetType == ItemType.None;

            var target = explicitTarget;
            if (!friendly)
            {
                if (untargeted)
                    target = null;
                else if (targetSelf)
                    target = this;
            }

            var caster = GetEquippedWand();

            // handle self procs
            if (spell.IsHarmful && target != this)
                TryProcEquippedItems(this, this, true, caster);

            // If the target is too far away, don't cast. This checks to see of this monster and the target are on separate landblock groups, and potentially separate threads.
            // This also fixes cross-threading issues
            if (target != null && (CurrentLandblock == null || target.CurrentLandblock == null || CurrentLandblock.CurrentLandblockGroup != target.CurrentLandblock.CurrentLandblockGroup))
                return;

            // try to resist spell, if applicable
            if (TryResistSpell(target, spell))
            {
                TryHandleFactionMob(target);
                return;
            }

            var targetCreature = target as Creature;

            // TODO: see if this can be coalesced
            switch (spell.School)
            {
                case MagicSchool.CreatureEnchantment:

                    HandleCastSpell(spell, target, keepExplicitTarget: friendly);

                    if (spell.IsHarmful)
                    {
                        // handle target procs
                        if (targetCreature != null && targetCreature != this)
                            TryProcEquippedItems(this, targetCreature, false, caster);
                    }
                    break;

                case MagicSchool.ItemEnchantment:

                    TryCastItemEnchantment_WithRedirects(spell, target);
                    break;

                case MagicSchool.LifeMagic:

                    HandleCastSpell(spell, target, null, caster, keepExplicitTarget: friendly);

                    if (spell.MetaSpellType != SpellType.LifeProjectile)
                    {
                        if (!friendly)
                            TryHandleFactionMob(target);

                        if (spell.IsHarmful)
                        {
                            // handle target procs
                            if (targetCreature != null && targetCreature != this)
                                TryProcEquippedItems(this, targetCreature, false, caster);
                        }
                    }
                    break;

                case MagicSchool.WarMagic:
                case MagicSchool.VoidMagic:

                    HandleCastSpell(spell, target, caster, WarVoidSpellLauncher(this, caster));
                    break;
            }
        }

        /// <summary>
        /// The weapon a war or void cast hands HandleCastSpell, which becomes SpellProjectile.ProjectileLauncher (crit
        /// chance, Crippling Blow, elemental damage mod, slayer, resistance cleaving) and the DoT weapon of a void
        /// enchantment. A Custom Summons caster pet (CombatPet with IsCasterPet) gets its wielded wand, the same weapon a
        /// player's cast passes (Player_Magic: HandleCastSpell(spell, target, itemCaster, caster, ...)). Every other
        /// monster, a melee or missile pet included, gets null, exactly as before: retail ACE hands a monster's wand only
        /// as the item caster, so its spell damage never read the wand, and that stays bit-identical.
        ///
        /// Side effect, intended (owner ruling 2026-11-02): the launcher also lets the pet's CRIT RATING reach its bolts.
        /// GetWeaponMagicCritFrequency returns the flat default before it reads the wielder's crit rating when the
        /// launcher is null, so with no wand a caster pet's dagger Crit rating does nothing for war/void; with the wand
        /// it adds 1% crit chance per point (WorldObject_Weapon.GetWeaponMagicCritFrequency). Either way, a pet also gets
        /// the Precision on its owner's wielded Summoner focus (SummonerFocus.PetFocusModValue): the no-launcher return
        /// adds it to the flat default, and the launcher path adds it after the wielder's own Precision read.
        /// </summary>
        internal static WorldObject WarVoidSpellLauncher(Creature castingCreature, WorldObject wand)
        {
            return castingCreature is CombatPet { IsCasterPet: true } ? wand : null;
        }

        /// <summary>
        /// Perform the animations after casting a spell,
        /// ie. moving arms back in, returning to previous stance
        /// </summary>
        public void PostCastMotion()
        {
            var animSpeed = IsRanged ? PostCastSpeed_Ranged : PostCastSpeed;

            var motion = new Motion(this, MotionCommand.Ready, animSpeed);
            motion.MotionState.TurnSpeed = 2.25f;
            //motion.HasTarget = true;
            //motion.TargetGuid = target.Guid;
            CurrentMotionState = motion;

            EnqueueBroadcastMotion(motion);
        }

        public float GetPostCastTime(Spell spell, bool fallback = false)
        {
            if (AiUseHumanMagicAnimations && !fallback)
                return GetPostCastTime_Human(spell);

            var animSpeed = IsRanged ? PostCastSpeed_Ranged : PostCastSpeed;

            return MotionTable.GetAnimationLength(MotionTableId, CurrentMotionState.Stance, MotionCommand.CastSpell, MotionCommand.Ready, animSpeed);
        }

        private float GetPostCastTime_Human(Spell spell)
        {
            var animSpeed = IsRanged ? PostCastSpeed_Ranged : PostCastSpeed;

            var animTime = MotionTable.GetAnimationLength(MotionTableId, CurrentMotionState.Stance, spell.Formula.CastGesture, MotionCommand.Ready, animSpeed);

            // FIXME: data
            if (animTime == 0.0f)
                return GetPostCastTime(spell, true);

            return animTime;
        }

        /// <summary>
        /// Returns the magic skill level used for spell range checks.
        /// (initial points + points due to directly raising the skill)
        /// </summary>
        /// <returns></returns>
        private uint GetMagicSkillForRangeCheck() => GetMagicSkillForRangeCheck(CurrentSpell);

        private uint GetMagicSkillForRangeCheck(Spell spell)
        {
            var skill = GetCreatureSkill(spell.School);

            // verify this - should it be using base?
            // seems like it could be off, player formula uses current + cap?

            return skill.InitLevel + skill.Ranks;
        }

        public void TryHandleFactionMob(WorldObject target)
        {
            if (target == this || target is Player)
                return;

            var creatureTarget = target as Creature;

            if (creatureTarget == null || !AllowFactionCombat(creatureTarget) && !PotentialFoe(creatureTarget))
                return;

            MonsterOnAttackMonster(creatureTarget);
        }
        /// <summary>
        /// Checks for AiUseHumanMagicAnimations and if true, sets CurrentSpell and sets combat mode to Magic
        /// </summary>
        public void CheckForHumanPreCast(Spell spell)
        {
            if (AiUseHumanMagicAnimations)
            {
                CurrentSpell = new Spell(spell.Id);
                SetCombatMode(CombatMode.Magic);
            }
        }
    }
}
