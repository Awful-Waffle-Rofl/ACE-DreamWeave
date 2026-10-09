using System;

using ACE.Common;
using ACE.Entity;
using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Entity.Models;
using ACE.Server.Entity;
using ACE.Server.Entity.Actions;
using ACE.Server.Network.GameEvent.Events;
using ACE.Server.Network.GameMessages.Messages;
using ACE.Server.Physics;
using ACE.Server.Physics.Animation;
using ACE.Server.Pvp.Rules;
using ACE.Server.WorldObjects.Entity;

namespace ACE.Server.WorldObjects
{
    public class Healer : WorldObject
    {
        // TODO: change structure / maxstructure to int,
        // cast to ushort at network level
        public ushort? UsesLeft
        {
            get => Structure;
            set => Structure = value;
        }

        /// <summary>
        /// A new biota be created taking all of its values from weenie.
        /// </summary>
        public Healer(Weenie weenie, ObjectGuid guid) : base(weenie, guid)
        {
            SetEphemeralValues();
        }

        /// <summary>
        /// Restore a WorldObject from the database.
        /// </summary>
        public Healer(Biota biota) : base(biota)
        {
            SetEphemeralValues();
        }

        private void SetEphemeralValues()
        {
            ObjectDescriptionFlags |= ObjectDescriptionFlag.Healer;
        }

        public override void HandleActionUseOnTarget(Player healer, WorldObject target)
        {
            if (healer.GetCreatureSkill(Skill.Healing).AdvancementClass < SkillAdvancementClass.Trained)
            {
                healer.SendUseDoneEvent(WeenieError.YouArentTrainedInHealing);
                return;
            }

            if (healer.IsBusy || healer.Teleporting || healer.suicideInProgress)
            {
                healer.SendUseDoneEvent(WeenieError.YoureTooBusy);
                return;
            }

            if (!(target is Player targetPlayer) || targetPlayer.Teleporting)
            {
                healer.SendUseDoneEvent(WeenieError.YouCantHealThat);
                return;
            }

            // PvP battlegrounds (Docs/Pvp/BATTLEGROUNDS.md "Respawn" 7): a respawning player cannot heal another player
            // from the pen with a kit, the same rule as casting.
            if (healer.PvpHealKitRefusedWhileRespawning(targetPlayer))
            {
                healer.SendUseDoneEvent(WeenieError.YoureTooBusy);
                return;
            }

            if (healer.IsJumping)
            {
                healer.SendUseDoneEvent(WeenieError.YouCantDoThatWhileInTheAir);
                return;
            }

            // ensure same PKType, although PK and PKLite players can heal NPKs:
            // https://asheron.fandom.com/wiki/Player_Killer
            // https://asheron.fandom.com/wiki/Player_Killer_Lite

            if (targetPlayer.PlayerKillerStatus != healer.PlayerKillerStatus && targetPlayer.PlayerKillerStatus != PlayerKillerStatus.NPK)
            {
                healer.SendWeenieErrorWithString(WeenieErrorWithString.YouFailToAffect_NotSamePKType, targetPlayer.Name);
                healer.SendUseDoneEvent();
                return;
            }

            // ensure target player vital < MaxValue
            var vital = targetPlayer.GetCreatureVital(BoosterEnum);

            if (vital.Current == vital.MaxValue)
            {
                switch (vital.Vital)
                {
                    case PropertyAttribute2nd.MaxHealth:
                        healer.Session.Network.EnqueueSend(new GameEventWeenieErrorWithString(healer.Session, WeenieErrorWithString._IsAtFullHealth, target.Name));
                        break;
                    case PropertyAttribute2nd.MaxStamina:
                        healer.Session.Network.EnqueueSend(new GameEventCommunicationTransientString(healer.Session, $"{target.Name} is already at full stamina!"));
                        break;
                    case PropertyAttribute2nd.MaxMana:
                        healer.Session.Network.EnqueueSend(new GameEventCommunicationTransientString(healer.Session, $"{target.Name} is already at full mana!"));
                        break;
                }
                healer.SendUseDoneEvent();
                return;
            }

            /*if (!healer.Equals(targetPlayer))
            {
                // perform moveto
                healer.CreateMoveToChain(target, (success) => DoHealMotion(healer, targetPlayer, success));
            }
            else
                DoHealMotion(healer, targetPlayer, true);*/

            // MoveTo is now handled in base Player_Use
            DoHealMotion(healer, targetPlayer, true);
        }

        public const float Healing_MaxMove = 5.0f;

        public void DoHealMotion(Player healer, Player target, bool success)
        {
            if (!success || target.IsDead || target.Teleporting || target.suicideInProgress)
            {
                healer.SendUseDoneEvent();
                return;
            }

            healer.IsBusy = true;

            var motionCommand = healer.Equals(target) ? MotionCommand.SkillHealSelf : MotionCommand.SkillHealOther;

            var motion = new Motion(healer, motionCommand);
            var currentStance = healer.CurrentMotionState.Stance;
            var animLength = MotionTable.GetAnimationLength(healer.MotionTableId, currentStance, motionCommand);

            var startPos = new Physics.Common.Position(healer.PhysicsObj.Position);

            var vital = target.GetCreatureVital(BoosterEnum);

            var missingVital = vital.Missing;

            var actionChain = new ActionChain();
            //actionChain.AddAction(healer, () => healer.EnqueueBroadcastMotion(motion));
            actionChain.AddAction(healer, () => healer.SendMotionAsCommands(motionCommand, currentStance));
            actionChain.AddDelaySeconds(animLength);
            actionChain.AddAction(healer, () =>
            {
                // check healing move distance cap
                var endPos = new Physics.Common.Position(healer.PhysicsObj.Position);
                var dist = startPos.Distance(endPos);

                //Console.WriteLine($"Dist: {dist}");

                // PvP airborne lever (H1), arena only (the healer must be in a Live arena match): refuse the
                // completion, beside the PK-only movement cap below.
                // Keeps the kit unused - it returns before DoHealing, so no use is decremented and no vital
                // changes. This is the only SendUseDoneEvent for this healing use (there is no separate
                // trailing UseDone the way TryUseItem adds one for Food/Gem), so sending it here on refusal,
                // and returning, is not a double-send. lastUseUtc is always null here, so the interval lever
                // (H2 only, per the design) can never be the reason - only Airborne is reachable.
                if (PvpRules.TryBlockConsumable(PvpChokePoint.H1, healer, healer.IsJumping, null) == PvpConsumableBlock.Airborne)
                {
                    healer.IsBusy = false;
                    healer.SendUseDoneEvent(WeenieError.YouCantDoThatWhileInTheAir);
                    return;
                }

                // Arena overtime (Docs/Pvp/DESIGN.md "Overtime"), the overtime half of H1: a HEALTH kit on a target
                // in overtime with a healing factor of 0 is refused before DoHealing, so no use is spent and the kit
                // is kept, and the healer is told why. Keyed on the HEALED player's binding. Stamina and Mana kits
                // are untouched.
                if (PvpRules.HealsHealth(BoosterEnum) && PvpRules.TryBlockOvertimeHeal(PvpChokePoint.H1, target) == PvpConsumableBlock.Overtime)
                {
                    healer.IsBusy = false;
                    healer.Session?.Network.EnqueueSend(new GameMessageSystemChat(Pvp.PvpArenaText.OvertimeHealRefused, ChatMessageType.Broadcast));
                    healer.SendUseDoneEvent();
                    return;
                }

                // only PKs affected by these caps?
                if (dist < Healing_MaxMove || healer.PlayerKillerStatus == PlayerKillerStatus.NPK)
                    DoHealing(healer, target, missingVital);
                else
                    healer.Session.Network.EnqueueSend(new GameMessageSystemChat("Your movement disrupted healing!", ChatMessageType.Broadcast));

                healer.IsBusy = false;

                healer.SendUseDoneEvent();
            });

            healer.EnqueueMotion(actionChain, MotionCommand.Ready);

            actionChain.EnqueueChain();

            healer.NextUseTime = DateTime.UtcNow.AddSeconds(animLength);
        }

        public void DoHealing(Player healer, Player target, uint missingVital)
        {
            if (target.IsDead || target.Teleporting) return;

            var remainingMsg = "";

            if (!UnlimitedUse)
            {
                UsesLeft--;
                var s = UsesLeft == 1 ? "" : "s";
                remainingMsg = UsesLeft > 0 ? $" Your {Name} has {UsesLeft} use{s} left." : $" Your {Name} is used up.";

                Value -= StructureUnitValue;

                if (Value < 0) // fix negative value
                    Value = 0;
            }

            var stackSize = new GameMessagePublicUpdatePropertyInt(this, PropertyInt.Structure, UsesLeft.Value);
            var targetName = healer == target ? "yourself" : target.Name;

            var vital = target.GetCreatureVital(BoosterEnum);

            // skill check
            var difficulty = 0;
            var skillCheck = DoSkillCheck(healer, target, missingVital, ref difficulty);
            if (!skillCheck)
            {
                var failMsg = new GameMessageSystemChat($"You fail to heal {targetName}.{remainingMsg}", ChatMessageType.Broadcast);
                healer.Session.Network.EnqueueSend(failMsg, stackSize);
                if (healer != target)
                    target.Session.Network.EnqueueSend(new GameMessageSystemChat($"{healer.Name} fails to heal you.", ChatMessageType.Broadcast));
                if (UsesLeft <= 0 && !UnlimitedUse)
                    healer.TryConsumeFromInventoryWithNetworking(this, 1);
                return;
            }

            // heal up
            var healAmount = GetHealAmount(healer, target, missingVital, out var critical, out var staminaCost);

            healer.UpdateVitalDelta(healer.Stamina, (int)-staminaCost);
            // Amount displayed to player can exceed actual amount healed due to heal boost ratings, but we only want to record the actual amount healed
            var actualHealAmount = (uint)target.UpdateVitalDelta(vital, healAmount);
            if (vital.Vital == PropertyAttribute2nd.MaxHealth)
                target.DamageHistory.OnHeal(actualHealAmount);

            //if (target.Fellowship != null)
            //target.Fellowship.OnVitalUpdate(target);

            var healingSkill = healer.GetCreatureSkill(Skill.Healing);
            Proficiency.OnSuccessUse(healer, healingSkill, difficulty);

            var crit = critical ? "expertly " : "";
            var message = new GameMessageSystemChat($"You {crit}heal {targetName} for {healAmount} {BoosterEnum.ToString()} points.{remainingMsg}", ChatMessageType.Broadcast);

            healer.Session.Network.EnqueueSend(message, stackSize);

            if (healer != target)
                target.Session.Network.EnqueueSend(new GameMessageSystemChat($"{healer.Name} heals you for {healAmount} {BoosterEnum.ToString()} points.", ChatMessageType.Broadcast));

            if (UsesLeft <= 0 && !UnlimitedUse)
                healer.TryConsumeFromInventoryWithNetworking(this, 1);
        }

        /// <summary>
        /// Determines if healer successfully heals target for attempt
        /// </summary>
        public bool DoSkillCheck(Player healer, Player target, uint missingVital, ref int difficulty)
        {
            // skill check:
            // (healing skill + healing kit boost) * trainedMod
            // vs. damage * 2 * combatMod
            var healingSkill = healer.GetCreatureSkill(Skill.Healing);
            var trainedMod = healingSkill.AdvancementClass == SkillAdvancementClass.Specialized ? 1.5f : 1.1f;

            var combatMod = healer.CombatMode == CombatMode.NonCombat ? 1.0f : 1.1f;

            // PvP rules choke point HK1 (Docs/Pvp/DESIGN.md "Tunables"): pvp_arena_healkit_skill_cap_1v1,
            // ported from Doctide arena_1v1_healkit_skill_bonus_cap. A no-op unless target is bound to a
            // Live 1v1 match.
            var boostValue = Pvp.Rules.PvpArenaOneVOneRules.ApplyHealkitSkillCap1v1(target, BoostValue);

            var effectiveSkill = (int)Math.Round((healingSkill.Current + boostValue) * trainedMod);
            difficulty = (int)Math.Round(missingVital * 2 * combatMod);

            var skillCheck = SkillCheck.GetSkillChance(effectiveSkill, difficulty);
            return skillCheck > ThreadSafeRandom.Next(0.0f, 1.0f);
        }

        /// <summary>
        /// Returns the healing amount for this attempt
        /// </summary>
        public uint GetHealAmount(Player healer, Player target, uint missingVital, out bool criticalHeal, out uint staminaCost)
        {
            // factors: healing skill, healing kit bonus, stamina, critical chance
            var healingSkill = healer.GetCreatureSkill(Skill.Healing).Current;
            // PvP rules choke point HK2 (Docs/Pvp/DESIGN.md "Tunables"): pvp_arena_healkit_restoration_cap_1v1,
            // ported from Doctide arena_1v1_healkit_restoration_bonus_cap. A no-op unless target is bound to a
            // Live 1v1 match.
            var healkitMod = Pvp.Rules.PvpArenaOneVOneRules.ApplyHealkitRestorationCap1v1(target, (double)HealkitMod.Value);

            var healBase = healingSkill * (float)healkitMod;

            // todo: determine applicable range from pcaps
            var healMin = healBase * 0.2f;      // ??
            var healMax = healBase * 0.5f;
            var healAmount = ThreadSafeRandom.Next(healMin, healMax);

            // chance for critical healing
            criticalHeal = ThreadSafeRandom.Next(0.0f, 1.0f) < 0.1f;
            if (criticalHeal) healAmount *= 2;

            // cap to missing vital
            if (healAmount > missingVital)
                healAmount = missingVital;

            // stamina check? On the Q&A board a dev posted that stamina directly effects the amount of damage you can heal
            // low stam = less vital healed. I don't have exact numbers for it. Working through forum archive.

            // stamina cost: 1 stamina per 5 vital healed 
            staminaCost = (uint)Math.Round(healAmount / 5.0f);
            if (staminaCost > healer.Stamina.Current)
            {
                staminaCost = healer.Stamina.Current;
                healAmount = staminaCost * 5;
            }

            // verify healing boost comes from target instead of healer?
            // sounds like target in LumAugHealingRating...
            var ratingMod = target.GetHealingRatingMod();

            healAmount *= ratingMod;

            // PvP rules choke point HL2 (Docs/Pvp/DESIGN.md "PvP rules (levers)"): pvp_healing_mod on a HEALTH kit
            // (never Stamina or Mana) received by a target bound to a Live arena match (arena only). After the HK1/HK2 arena caps above, so the
            // two multiply. Floored when scaled, so the Math.Round below never rounds a scaled heal up.
            if (PvpRules.HealsHealth(BoosterEnum))
                healAmount = PvpRules.ApplyHealingMod(PvpChokePoint.HL2, target, healAmount);

            return (uint)Math.Round(healAmount);
        }
    }
}
