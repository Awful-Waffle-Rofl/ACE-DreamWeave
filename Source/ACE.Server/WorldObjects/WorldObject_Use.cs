using System;
using ACE.Entity;
using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Server.Entity;
using ACE.Server.Managers;
using ACE.Server.Network.GameEvent.Events;
using ACE.Server.Network.GameMessages.Messages;

namespace ACE.Server.WorldObjects
{
    partial class WorldObject
    {
        public double? UseTimestamp
        {
            get => GetProperty(PropertyFloat.UseTimestamp);
            set { if (!value.HasValue) RemoveProperty(PropertyFloat.UseTimestamp); else SetProperty(PropertyFloat.UseTimestamp, value.Value); }
        }

        protected double? ResetTimestamp
        {
            get => GetProperty(PropertyFloat.ResetTimestamp);
            set { if (!value.HasValue) RemoveProperty(PropertyFloat.ResetTimestamp); else SetProperty(PropertyFloat.ResetTimestamp, value.Value); }
        }

        protected double? ResetInterval
        {
            get => GetProperty(PropertyFloat.ResetInterval);
            set { if (!value.HasValue) RemoveProperty(PropertyFloat.ResetInterval); else SetProperty(PropertyFloat.ResetInterval, value.Value); }
        }

        protected bool DefaultLocked
        {
            get => GetProperty(PropertyBool.DefaultLocked) ?? false;
            set { if (!value) RemoveProperty(PropertyBool.DefaultLocked); else SetProperty(PropertyBool.DefaultLocked, value); }
        }

        protected bool DefaultOpen
        {
            get => GetProperty(PropertyBool.DefaultOpen) ?? false;
            set { if (!value) RemoveProperty(PropertyBool.DefaultOpen); else SetProperty(PropertyBool.DefaultOpen, value); }
        }

        /// <summary>
        /// Used to determine how close you need to be to use an item.
        /// </summary>
        public bool IsWithinUseRadiusOf(WorldObject wo, float? useRadius = null)
        {
            if (useRadius == null)
                useRadius = wo.UseRadius ?? 0.6f;

            var cylDist = GetCylinderDistance(wo);

            return cylDist <= useRadius;
        }

        public float GetCylinderDistance(WorldObject wo)
        {
            return (float)Physics.Common.Position.CylinderDistance(PhysicsObj.GetRadius(), PhysicsObj.GetHeight(), PhysicsObj.Position,
                wo.PhysicsObj.GetRadius(), wo.PhysicsObj.GetHeight(), wo.PhysicsObj.Position);
        }

        /// <summary>
        /// Handles the 'GameAction 0x35 - UseWithTarget' network message
        /// on a per-object type basis.
        public virtual void HandleActionUseOnTarget(Player player, WorldObject target)
        {
            RecipeManager.UseObjectOnTarget(player, this, target);
        }

        public virtual void OnActivate(WorldObject activator)
        {
            //Console.WriteLine($"{Name}.OnActivate({activator.Name})");

            // when players double click an object,
            // and the packet comes in as GameAction 0x36 - UseItem
            // from the game perspective, technically this starts as an 'Activate',
            // which can have a list of possible ActivationResponses - 
            // Use (by far the most common), Animate, Talk, Emote, CastSpell, Generate

            // PropertyInt.Active indicates if this object can be activated, default is true
            if (!Active) return;

            // verify use requirements
            var result = CheckUseRequirements(activator);

            var player = activator as Player;
            if (!result.Success)
            {
                if (result.Message != null && player != null)
                    player.Session.Network.EnqueueSend(result.Message);

                return;
            }

            // speed challenge (WaffleACE): using an object flagged PropertyBool.SpeedChallengeStart REBASES a
            // timed Proving Grounds run's clock to now (Docs/ProvingGroundsSpeed/DESIGN.md; see
            // Player_SpeedChallenge.cs's class doc comment and TryStartSpeedChallengeClock). It sits BEFORE the
            // goal hook immediately below for the same two reasons that hook sits where it does: AFTER the
            // CheckUseRequirements gate above, so a locked/inactive/on-cooldown start object cannot arm early,
            // and INDEPENDENT of the ActivationResponse dispatch below, so a decorative lever with no
            // ActivationResponse.Use still rebases the clock. It is a rebase, not a gate - the run is already
            // armed and bound from arrival regardless of whether this hook ever fires, so a player who never
            // reaches or uses the start object is simply timed from arrival rather than left in any kind of
            // stuck or unfinishable state. TryStartSpeedChallengeClock re-validates everything that matters
            // (a valid, active run; the season's declared StartWcid) and refuses a second rebase.
            if (player != null && GetProperty(PropertyBool.SpeedChallengeStart) == true)
                player.TryStartSpeedChallengeClock(this);

            // speed challenge (WaffleACE): using an object flagged PropertyBool.SpeedChallengeGoal can complete a
            // timed Proving Grounds run (Docs/ProvingGroundsSpeed/DESIGN.md section 3.4). It lives HERE, on the
            // generic activation path, and not on any one type's ActOnUse, because a season's objective may be an
            // altar, a lever, a pedestal or an exit portal - i.e. any WorldObject subclass. Two placement
            // decisions, both load-bearing:
            //   * AFTER the CheckUseRequirements gate above, so an object that is locked, inactive, on cooldown
            //     or otherwise refused cannot finish a run;
            //   * INDEPENDENT of the ActivationResponse flag dispatch below, so a decorative prop that carries no
            //     ActivationResponse.Use still finishes the run, and so completing a run never depends on
            //     reaching the base ActOnUse "undefined for wcid" error path (WorldObject_Use.cs:143-152).
            // The funnel re-validates everything that matters - the run must be armed and the player still
            // standing in the exact ephemeral instance it is bound to, and this object's wcid must be the
            // SEASON's declared objective - so a stray flagged prop is inert.
            //
            // When the goal object is itself the in-dungeon exit portal, note what actually happens: the funnel
            // runs first, consumes the run and CLEARS PositionType.EphemeralRealmExitTo after capturing it, then
            // schedules its own exit teleport 3 seconds later. Portal.ActOnUse's PortalExitInstance block then
            // runs in the same activation and finds that stamp already gone, so it logs its "no
            // EphemeralRealmExitTo" warning and teleports to the portal's own static Destination fallback - the
            // player is bounced there first and lands at the captured exit position 3 seconds after. The run is
            // safe either way (it is already consumed, so CheckSpeedChallengeInstanceExit no-ops on both
            // teleports), but it is not tidy: prefer a non-portal goal object, or a goal object placed alongside
            // the exit portal rather than on it.
            if (player != null && GetProperty(PropertyBool.SpeedChallengeGoal) == true)
                player.TryFinishSpeedChallenge(this);

            // objective locks (WaffleACE): an object carrying PropertyString.ObjectiveLockKey is a
            // CONTRIBUTOR to a count-based puzzle gate - a bell in "ring three of five", a lever in "hold
            // three at once" (see WorldObject_Objective.cs). It lives HERE, on the generic activation path,
            // for the same reason the speed hook above does: a contributor may be a lever, a bell, a
            // pedestal, a pressure plate or a piece of scenery, i.e. any WorldObject subclass, and it must
            // still count when the object carries no ActivationResponse.Use of its own.
            //
            // Placement inside OnActivate is load-bearing twice over. AFTER the CheckUseRequirements gate
            // above, so an object that is locked, inactive, on cooldown or otherwise refused cannot advance
            // the puzzle - a player must not be able to bank progress off a use the server just denied. And
            // BEFORE the ActivationResponse dispatch below, so the contribution is independent of whether
            // the contributor also does something visible when used.
            //
            // The guard is a single property read so a normal activation - every door, chest, NPC and
            // portal in the game - pays one dictionary miss and nothing else. `activator as Player` is
            // already resolved above and may legitimately be null (a pressure plate tripped by a monster,
            // one object activating another through a link); that only costs the progress message, never
            // the contribution.
            if (ObjectiveLockKey != null)
                ContributeToObjectiveLock(player);

            // puzzle gates (WaffleACE): a lever spawned by /puzzlegate carries a runtime back-reference to its
            // placement (WorldObject_PuzzleGate.cs). Same placement rules as the objective-lock hook above:
            // AFTER CheckUseRequirements, independent of the ActivationResponse dispatch below. The manager
            // only QUEUES the activation onto the placement's landblock, so a reshuffle that destroys this very
            // lever never runs while the rest of this method is still using it.
            if (P_PuzzleGate != null)
                ACE.Server.PuzzleGates.PuzzleGateManager.OnActivated(this, player);

            // wave encounters (WaffleACE): an object flagged PropertyBool.WaveEncounterAnchor starts an
            // object-anchored wave encounter (ACE.Server.WaveEncounters) - the D6 Sounding Drum. Same
            // placement rules as the two hooks above: AFTER CheckUseRequirements, and independent of the
            // ActivationResponse dispatch below. Gated on the bool and NEVER on WaveChallengeRosterBaseWcid
            // (9031) alone: the Proving Grounds portal carries 9031 too. TryStart owns every refusal and its
            // chat line; a non-player activator (a linked lever, a pressure plate) cannot start one, because
            // the refusal lines have nobody to go to.
            if (player != null && GetProperty(PropertyBool.WaveEncounterAnchor) == true)
                ACE.Server.WaveEncounters.WaveEncounterManager.TryStart(this, player);

            if (player != null)
                player.EnchantmentManager.StartCooldown(this);

            // perform motion animation - rarely used (only 4 instances in PY16 db)
            if (ActivationResponse.HasFlag(ActivationResponse.Animate))
                OnAnimate(activator);

            // perform activation emote
            if (ActivationResponse.HasFlag(ActivationResponse.Emote))
                OnEmote(activator);

            // cast a spell on the player (spell traps)
            if (ActivationResponse.HasFlag(ActivationResponse.CastSpell))
                OnCastSpell(activator);

            // call to generator to spawn new object
            if (ActivationResponse.HasFlag(ActivationResponse.Generate))
                OnGenerate(activator);

            // default use action
            if (ActivationResponse.HasFlag(ActivationResponse.Use))
            {
                if (activator is Creature creature)
                {
                    //target.EmoteManager.OnActivation(creature); // found a few things with Activation on them but not ActivationResponse.Emote...
                    EmoteManager.OnUse(creature);
                }

                ActOnUse(activator);
            }

            // send chat text - rarely used (only 8 instances in PY16 db)
            if (ActivationResponse.HasFlag(ActivationResponse.Talk))
                OnTalk(activator);

            if (!(this is Creature) && ActivationTarget > 0)
            {
                var activationTarget = CurrentLandblock?.GetObject(new ObjectGuid(ActivationTarget));
                if (activationTarget != null)
                    activationTarget.OnActivate(activator);
                else
                {
                    log.Warn($"{Name}.OnActivate({activator.Name}): couldn't find activation target {ActivationTarget:X8}");
                }
            }
        }

        public virtual void ActOnUse(WorldObject activator)
        {
            // empty base - individual WorldObject types should override

            var msg = $"{Name}.ActOnUse({activator.Name}) - undefined for wcid {WeenieClassId} type {WeenieType}";
            log.Error(msg);

            if (activator is Player _player)
                _player.Session.Network.EnqueueSend(new GameMessageSystemChat(msg, ChatMessageType.Broadcast));
        }

        public virtual void OnAnimate(WorldObject activator)
        {
            var motion = new Motion(this, ActivationAnimation);
            EnqueueBroadcastMotion(motion);
        }

        public virtual void OnTalk(WorldObject activator)
        {
            // todo: verify the format of this message
            if (activator is Player player)
                player.Session.Network.EnqueueSend(new GameMessageSystemChat(ActivationTalk, ChatMessageType.Broadcast));
        }

        public virtual void OnEmote(WorldObject activator)
        {
            if (activator is Creature creature)
                EmoteManager.OnActivation(creature);
        }

        public virtual void OnCastSpell(WorldObject activator)
        {
            if (SpellDID != null)
            {
                var spell = new Spell(SpellDID.Value);
                TryCastSpell(spell, activator, this);
            }
        }

        public virtual void OnGenerate(WorldObject activator)
        {
            if (IsGenerator)
                Generator_Generate();
        }

        /// <summary>
        /// Verifies the use requirements for activating an item.
        ///
        /// Two of the checks below (arcane lore vs ItemDifficulty, ItemSkillLimit/ItemSkillLevelLimit vs
        /// Current) are individually waivable for a Player activator via
        /// Player.IsFacetSpellActivationWaived - see that method's remarks. This is a base-method-internal
        /// waiver, not a bypass of this call: a subclass override that calls
        /// base.CheckUseRequirements(activator) gets it applied to exactly these two checks and no others,
        /// and its own additional checks are never affected by it.
        /// </summary>
        public virtual ActivationResult CheckUseRequirements(WorldObject activator)
        {
            //Console.WriteLine($"{Name}.CheckUseRequirements({activator.Name})");

            if (activator == null)
            {
                log.Error($"0x{Guid}:{Name}.CheckUseRequirements() (wcid: {WeenieClassId}): activator is null");
                return new ActivationResult(false);
            }

            if (!(activator is Player player))
                return new ActivationResult(true);

            // Facet restore waiver: skips ONLY these two checks (they read Current, a buffed value,
            // exactly the reason a player who geared up buffed, swapped facets away, lost the buff, and
            // swapped back could otherwise be refused reactivating spells on gear they were provably
            // already wearing - CaptureFacetEquip only ever records EquippedObjects). Every other check in
            // this method, including the use cooldown below and anything a subclass override adds around
            // its own base.CheckUseRequirements call, is unaffected - see
            // Player.facetSpellActivationWaiver's remarks.
            var waiveBuffedMagicSkill = player.IsFacetSpellActivationWaived(Guid.Full);

            // verify arcane lore requirement
            if (ItemDifficulty != null && !waiveBuffedMagicSkill)
            {
                var arcaneLore = player.GetCreatureSkill(Skill.ArcaneLore);
                if (arcaneLore.Current < ItemDifficulty.Value)
                    return new ActivationResult(new GameEventWeenieErrorWithString(player.Session, WeenieErrorWithString.Your_IsTooLowToUseItemMagic, arcaneLore.Skill.ToSentence()));
            }

            // verify skill - does this have to be trained, or only in conjunction with UseRequiresSkillLevel?
            // only seems to be used for summoning so far...
            if (ItemSkillLimit != null && ItemSkillLevelLimit != null && !waiveBuffedMagicSkill)
            {
                var skill = activator.ConvertToMoASkill((Skill)ItemSkillLimit.Value);
                var playerSkill = player.GetCreatureSkill(skill);

                if (playerSkill.Current < ItemSkillLevelLimit.Value)
                    return new ActivationResult(new GameEventWeenieErrorWithString(player.Session, WeenieErrorWithString.Your_IsTooLowToUseItemMagic, playerSkill.Skill.ToSentence()));
            }

            if (UseRequiresSkill != null)
            {
                var skill = activator.ConvertToMoASkill((Skill)UseRequiresSkill.Value);
                var playerSkill = player.GetCreatureSkill(skill);

                if (playerSkill.AdvancementClass < SkillAdvancementClass.Trained)
                {
                    //return new ActivationResult(new GameEventWeenieErrorWithString(player.Session, WeenieErrorWithString.Your_SkillMustBeTrained, playerSkill.Skill.ToSentence()));
                    player.Session.Network.EnqueueSend(new GameEventCommunicationTransientString(player.Session, $"You must have {playerSkill.Skill.ToSentence()} trained to use that item's magic"));
                    return new ActivationResult(false);
                }

                // verify skill level
                if (UseRequiresSkillLevel != null)
                {
                    if (playerSkill.Current < UseRequiresSkillLevel.Value)
                        return new ActivationResult(new GameEventWeenieErrorWithString(player.Session, WeenieErrorWithString.Your_IsTooLowToUseItemMagic, playerSkill.Skill.ToSentence()));
                }
            }

            // verify skill specialized
            // is this always in conjunction with UseRequiresSkill?
            // again, only seems to be for summoning so far...
            if (UseRequiresSkillSpec != null)
            {
                var skill = activator.ConvertToMoASkill((Skill)UseRequiresSkillSpec.Value);
                var playerSkill = player.GetCreatureSkill(skill);

                if (playerSkill.AdvancementClass < SkillAdvancementClass.Specialized)
                    return new ActivationResult(new GameEventWeenieErrorWithString(player.Session, WeenieErrorWithString.YouMustSpecialize_ToUseItemMagic, playerSkill.Skill.ToSentence()));

                // verify skill level
                if (UseRequiresSkillLevel != null)
                {
                    if (playerSkill.Current < UseRequiresSkillLevel.Value)
                        return new ActivationResult(new GameEventWeenieErrorWithString(player.Session, WeenieErrorWithString.Your_IsTooLowToUseItemMagic, playerSkill.Skill.ToSentence()));
                }
            }

            // verify player level
            if (UseRequiresLevel != null)
            {
                var playerLevel = player.Level ?? 1;
                if (playerLevel < UseRequiresLevel.Value)
                    //return new ActivationResult(new GameEventWeenieErrorWithString(player.Session, WeenieErrorWithString.YouMustBe_ToUseItemMagic, $"level {UseRequiresLevel.Value}")); // not retail
                    return new ActivationResult(new GameEventCommunicationTransientString(player.Session, "You are not high enough level to use that!"));
            }

            // verify attribute / vital limits
            if (ItemAttributeLimit != null)
            {
                var playerAttr = player.Attributes[ItemAttributeLimit.Value];

                if (playerAttr.Current < ItemAttributeLevelLimit)
                    return new ActivationResult(new GameEventWeenieErrorWithString(player.Session, WeenieErrorWithString.Your_IsTooLowToUseItemMagic, playerAttr.Attribute.ToString()));
            }

            if (ItemAttribute2ndLimit != null)
            {
                var playerVital = player.Vitals[ItemAttribute2ndLimit.Value];

                if (playerVital.MaxValue < ItemAttribute2ndLevelLimit)
                    return new ActivationResult(new GameEventWeenieErrorWithString(player.Session, WeenieErrorWithString.Your_IsTooLowToUseItemMagic, playerVital.Vital.ToSentence()));
            }

            // verify heritage group
            if (HeritageGroup != HeritageGroup.Invalid)
            {
                if (this is not Creature) // Creatures are not restricted to their own hertigage group
                {
                    var playerHeritageGroup = player.HeritageGroup;

                    if (playerHeritageGroup != HeritageGroup)
                        return new ActivationResult(new GameEventWeenieErrorWithString(player.Session, WeenieErrorWithString.YouMustBe_ToUseItemMagic, HeritageGroup.ToSentence()));
                }
            }

            // Check for a cooldown. Soul Tether waives it for a summoning device whose combat pet was just
            // slain; CanSkipCombatPetSummonCooldown is false for every other item and every other player.
            if (!player.EnchantmentManager.CheckCooldown(CooldownId) && !player.CanSkipCombatPetSummonCooldown(this))
            {
                // TODO: werror/string not found, find exact message

                /*var cooldown = player.GetCooldown(this);
                var timer = cooldown.GetFriendlyString();
                player.Session.Network.EnqueueSend(new GameMessageSystemChat($"{Name} can be activated again in {timer}", ChatMessageType.Broadcast));*/

                player.Session.Network.EnqueueSend(new GameEventCommunicationTransientString(player.Session, "You have used this item too recently"));
                return new ActivationResult(false);
            }

            if (player.IsOlthoiPlayer)
            {
                //player.Session.Network.EnqueueSend(new GameEventCommunicationTransientString(player.Session, "Olthoi can't interact with that!"));
                //player.SendWeenieError(WeenieError.OlthoiCannotInteractWithThat);
                //return new ActivationResult(false);

                if (this is Creature)
                {
                    if (CreatureType == ACE.Entity.Enum.CreatureType.Olthoi)
                        return new ActivationResult(true);
                    else
                    {
                        if (this is Vendor)
                            player.SendWeenieError(WeenieError.OlthoiVendorLooksInHorror);
                        else if (NpcLooksLikeObject ?? false)
                            player.SendWeenieError(WeenieError.OlthoiCannotInteractWithThat);
                        else
                            player.SendWeenieErrorWithString(WeenieErrorWithString._CowersFromYou, Name);

                        return new ActivationResult(false);
                    }
                }
                else if (this is Lifestone)
                {
                    player.SendWeenieError(WeenieError.OlthoiCannotUseLifestones);
                    return new ActivationResult(false);
                }
                else if (this is Container && !(this is Corpse))
                {
                    player.SendWeenieError(WeenieError.OlthoiCannotInteractWithThat);
                    return new ActivationResult(false);
                }
                else if (this is AttributeTransferDevice || this is AugmentationDevice || this is Bindstone || this is Book
                    || this is Game || this is Gem || this is GenericObject || this is Key || this is SkillAlterationDevice)
                {
                    player.SendWeenieError(WeenieError.OlthoiCannotInteractWithThat);
                    return new ActivationResult(false);
                }
            }

            return new ActivationResult(true);
        }
    }
}
