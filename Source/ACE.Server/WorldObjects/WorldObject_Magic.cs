using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Text;

using ACE.Common;
using ACE.Database;
using ACE.DatLoader;
using ACE.DatLoader.FileTypes;
using ACE.Entity;
using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Entity.Models;
using ACE.Server.ClassAbilities;
using ACE.Server.ClassAbilities.Abilities;
using ACE.Server.Entity;
using ACE.Server.EquipmentMods;
using ACE.Server.Factories;
using ACE.Server.Factories.Entity;
using ACE.Server.Network.GameEvent.Events;
using ACE.Server.Network.GameMessages.Messages;
using ACE.Server.Network.Structure;
using ACE.Server.Managers;
using ACE.Server.Entity.Actions;
using ACE.Server.Physics;
using ACE.Server.Physics.Extensions;
using ACE.Server.WorldObjects.Managers;

namespace ACE.Server.WorldObjects
{
    partial class WorldObject
    {
        /// <summary>
        /// Instantly casts a spell for a WorldObject (ie. spell traps)
        /// </summary>
        public void TryCastSpell(Spell spell, WorldObject target, WorldObject itemCaster = null, WorldObject weapon = null, bool isWeaponSpell = false, bool fromProc = false, bool tryResist = true)
        {
            // TODO: look into further normalizing this / caster / weapon

            // verify spell exists in database
            if (spell._spell == null)
            {
                if (target is Player targetPlayer)
                    targetPlayer.Session.Network.EnqueueSend(new GameMessageSystemChat($"{spell.Name} spell not implemented, yet!", ChatMessageType.System));

                return;
            }

            if (spell.IsFellowshipSpell)
            {
                if (target is not Player targetPlayer || targetPlayer.Fellowship == null)
                    return;

                var fellows = targetPlayer.Fellowship.GetFellowshipMembers();

                foreach (var fellow in fellows.Values)
                    TryCastSpell_Inner(spell, fellow, itemCaster, weapon, isWeaponSpell, fromProc, tryResist);
            }
            else
                TryCastSpell_Inner(spell, target, itemCaster, weapon, isWeaponSpell, fromProc, tryResist);
        }

        public void TryCastSpell_Inner(Spell spell, WorldObject target, WorldObject itemCaster = null, WorldObject weapon = null, bool isWeaponSpell = false, bool fromProc = false, bool tryResist = true)
        {
            // verify before resist, still consumes source item
            if (spell.MetaSpellType == SpellType.Dispel && !VerifyDispelPKStatus(itemCaster, target))
                return;

            // perform resistance check, if applicable
            if (tryResist && TryResistSpell(target, spell, itemCaster))
                return;

            // if not resisted, cast spell
            HandleCastSpell(spell, target, itemCaster, weapon, isWeaponSpell, fromProc);
        }

        /// <summary>
        /// Instantly casts a spell for a WorldObject, with optional redirects for item enchantments
        /// </summary>
        public bool TryCastSpell_WithRedirects(Spell spell, WorldObject target, WorldObject itemCaster = null, WorldObject weapon = null, bool isWeaponSpell = false, bool fromProc = false, bool tryResist = true)
        {
            if (target is Creature creatureTarget)
            {
                var targets = GetNonComponentTargetTypes(spell, creatureTarget);

                if (targets != null)
                {
                    foreach (var itemTarget in targets)
                        TryCastSpell(spell, itemTarget, itemCaster, weapon, isWeaponSpell, fromProc, tryResist);

                    return targets.Count > 0;
                }
            }

            TryCastSpell(spell, target, itemCaster, weapon, isWeaponSpell, fromProc, tryResist);

            return true;
        }

        /// <summary>
        /// Determines whether a spell will be resisted,
        /// based upon the caster's magic skill vs target's magic defense skill
        /// </summary>
        /// <returns>TRUE if spell is resisted</returns>
        public static bool MagicDefenseCheck(uint casterMagicSkill, uint targetMagicDefenseSkill, out float resistChance)
        {
            // uses regular 0.03 factor, and not magic casting 0.07 factor
            var chance = SkillCheck.GetSkillChance((int)casterMagicSkill, (int)targetMagicDefenseSkill);
            var rng = ThreadSafeRandom.Next(0.0f, 1.0f);

            resistChance = (float)(1.0f - chance);

            return chance <= rng;
        }

        /// <summary>
        /// If this spell has a chance to be resisted, rolls for a chance
        /// Returns TRUE if spell is resistable and was resisted for this attempt
        /// </summary>
        public bool TryResistSpell(WorldObject target, Spell spell, WorldObject itemCaster = null, bool projectileHit = false)
        {
            // fix hermetic void?
            if (!spell.IsResistable && spell.Category != SpellCategory.ManaConversionModLowering || spell.IsSelfTargeted)
            //if (!spell.IsResistable || spell.IsSelfTargeted)
                return false;

            if (spell.MetaSpellType == SpellType.Dispel && spell.Align == DispelType.Negative && !PropertyManager.GetBool("allow_negative_dispel_resist").Item)
                return false;

            if (spell.NumProjectiles > 0 && !projectileHit)
                return false;

            if (itemCaster != null && Cloak.IsCloak(itemCaster))
                return false;

            uint magicSkill = 0;

            var caster = itemCaster ?? this;

            var casterCreature = caster as Creature;

            if (casterCreature != null)
            {
                // Retrieve caster's skill level in the Magic School
                magicSkill = casterCreature.GetCreatureSkill(spell.School).Current;

            }
            else if (caster.ItemSpellcraft != null)
            {
                // Retrieve casting item's spellcraft
                magicSkill = (uint)caster.ItemSpellcraft;
            }
            else if (caster.Wielder is Creature wielder)
            {
                // Receive wielder's skill level in the Magic School?
                magicSkill = wielder.GetCreatureSkill(spell.School).Current;
            }

            //Console.WriteLine($"Magic skill: {magicSkill}");

            // only creatures can resist spells?
            if (target is not Creature targetCreature)
                return false;

            // Retrieve target's Magic Defense Skill
            var difficulty = targetCreature.GetEffectiveMagicDefense();

            //Console.WriteLine($"{target.Name}.ResistSpell({Name}, {spell.Name}): magicSkill: {magicSkill}, difficulty: {difficulty}");
            bool resisted = MagicDefenseCheck(magicSkill, difficulty, out float resistChance);

            var player = this as Player;
            var targetPlayer = target as Player;

            if (targetPlayer != null)
            {
                if (targetPlayer.Invincible)
                    resisted = true;

                if (targetPlayer.UnderLifestoneProtection)
                {
                    targetPlayer.HandleLifestoneProtection();
                    resisted = true;
                }
            }

            if (caster == target)
                resisted = false;

            if (resisted)
            {
                if (player != null)
                {
                    player.SendChatMessage(targetCreature, $"{targetCreature.Name} resists your spell", ChatMessageType.Magic);

                    player.Session.Network.EnqueueSend(new GameMessageSound(player.Guid, Sound.ResistSpell, 1.0f));
                }

                if (targetPlayer != null)
                {
                    targetPlayer.SendChatMessage(this, $"You resist the spell cast by {Name}", ChatMessageType.Magic);

                    targetPlayer.Session.Network.EnqueueSend(new GameMessageSound(targetPlayer.Guid, Sound.ResistSpell, 1.0f));

                    if (casterCreature != null)
                        targetPlayer.SetCurrentAttacker(casterCreature);

                    // Pocket Sand (Rogue T3): a resisted spell is an avoided attack, so it feeds the same
                    // proc an evade or a parry does. Monster casters only. The multi-projectile early return
                    // near the top of this method means a projectile spell reaches here once per projectile
                    // that actually connected and was resisted, which is the right granularity - each of
                    // those was an attack this player turned aside. Re-procs refresh rather than stack.
                    // Same guard as the OnEvade trigger: a "resist" that is really Invincible or lifestone
                    // immunity (forced true above) is not an attack this player turned aside, and must not
                    // be a risk-free proc farm.
                    if (casterCreature != null && casterCreature is not Player
                        && !targetPlayer.Invincible && !targetPlayer.UnderLifestoneProtection)
                        targetPlayer.TryPocketSand(casterCreature);

                    Proficiency.OnSuccessUse(targetPlayer, targetPlayer.GetCreatureSkill(Skill.MagicDefense), magicSkill);
                }

                if (this is Creature creature)
                    targetCreature.EmoteManager.OnResistSpell(creature);
            }

            if (player != null && player.DebugDamage.HasFlag(Creature.DebugDamageType.Attacker))
            {
                ShowResistInfo(player, this, target, spell, magicSkill, difficulty, resistChance, resisted);
            }
            if (targetCreature != null && targetCreature.DebugDamage.HasFlag(Creature.DebugDamageType.Defender))
            {
                ShowResistInfo(targetCreature, this, target, spell, magicSkill, difficulty, resistChance, resisted);
            }

            return resisted;
        }

        public static void ShowResistInfo(Creature observed, WorldObject attacker, WorldObject defender, Spell spell, uint attackSkill, uint defenseSkill, float resistChance, bool resisted)
        {
            var targetInfo = PlayerManager.GetOnlinePlayer(observed.DebugDamageTarget);

            if (targetInfo == null)
            {
                observed.DebugDamage = Creature.DebugDamageType.None;
                return;
            }

            // initial info / resist chance
            var info = $"Attacker: {attacker.Name} ({attacker.Guid})\n";
            info += $"Defender: {defender.Name} ({defender.Guid})\n";

            info += $"CombatType: Magic\n";

            info += $"Spell: {spell.Name} ({spell.Id})\n";

            info += $"EffectiveAttackSkill: {attackSkill}\n";
            info += $"EffectiveDefenseSkill: {defenseSkill}\n";

            info += $"ResistChance: {resistChance}\n";

            info += $"Resisted: {resisted}";

            if (resisted || spell.NumProjectiles == 0)
                targetInfo.Session.Network.EnqueueSend(new GameMessageSystemChat(info, ChatMessageType.Broadcast));
            else
                targetInfo.DebugDamageBuffer = $"{info}\n";
        }

        /// <summary>
        /// Creates a spell based on MetaSpellType
        /// </summary>
        protected bool HandleCastSpell(Spell spell, WorldObject target, WorldObject itemCaster = null, WorldObject weapon = null, bool isWeaponSpell = false, bool fromProc = false, bool equip = false)
        {
            var targetCreature = !spell.IsSelfTargeted || spell.IsFellowshipSpell ? target as Creature : this as Creature;

            if (this is Gem || this is Food || this is Hook)
                targetCreature = target as Creature;

            if (spell.School == MagicSchool.LifeMagic || spell.MetaSpellType == SpellType.Dispel)
            {
                // NonComponentTargetType should be 0 for untargeted spells.
                // Return if the spell type is targeted with no target defined or the target is already dead.
                if ((targetCreature == null || !targetCreature.IsAlive) && spell.NonComponentTargetType != ItemType.None
                    && spell.DispelSchool != MagicSchool.ItemEnchantment)
                {
                    return false;
                }
            }

            switch (spell.MetaSpellType)
            {
                case SpellType.Enchantment:
                case SpellType.FellowEnchantment:

                    // TODO: replace with some kind of 'rootOwner unless equip' concept?
                    if (itemCaster != null && (equip || itemCaster is Gem || itemCaster is Food))
                        CreateEnchantment(targetCreature ?? target, itemCaster, itemCaster, spell, equip);
                    else
                        CreateEnchantment(targetCreature ?? target, this, weapon, spell, equip, isWeaponSpell: isWeaponSpell);

                    break;

                case SpellType.Boost:
                case SpellType.FellowBoost:

                    HandleCastSpell_Boost(spell, targetCreature);
                    break;

                case SpellType.Transfer:

                    HandleCastSpell_Transfer(spell, targetCreature);
                    break;

                case SpellType.Projectile:
                case SpellType.LifeProjectile:
                case SpellType.EnchantmentProjectile:

                    HandleCastSpell_Projectile(spell, targetCreature, itemCaster, weapon, isWeaponSpell, fromProc);
                    break;

                case SpellType.PortalLink:

                    HandleCastSpell_PortalLink(spell, target);
                    break;

                case SpellType.PortalRecall:

                    HandleCastSpell_PortalRecall(spell, targetCreature);
                    break;

                case SpellType.PortalSummon:

                    HandleCastSpell_PortalSummon(spell, targetCreature, itemCaster);
                    break;

                case SpellType.PortalSending:

                    HandleCastSpell_PortalSending(spell, targetCreature, itemCaster);
                    break;

                case SpellType.FellowPortalSending:

                    HandleCastSpell_FellowPortalSending(spell, targetCreature, itemCaster);
                    break;

                case SpellType.Dispel:
                case SpellType.FellowDispel:

                    HandleCastSpell_Dispel(spell, targetCreature ?? target);
                    break;

                default:

                    if (this is Player player)
                        player.Session.Network.EnqueueSend(new GameMessageSystemChat("Spell not implemented, yet!", ChatMessageType.Magic));

                    return false;
            }

            // play spell effects
            DoSpellEffects(spell, this, target);

            return true;
        }

        /// <summary>
        /// Plays the caster/target effects for a spell
        /// </summary>
        protected void DoSpellEffects(Spell spell, WorldObject caster, WorldObject target, bool projectileHit = false)
        {
            if (spell.CasterEffect != 0 && (!spell.IsProjectile || !projectileHit))
                caster.EnqueueBroadcast(new GameMessageScript(caster.Guid, spell.CasterEffect, spell.Formula.Scale));

            if (spell.TargetEffect != 0 && (!spell.IsProjectile || projectileHit) && target != null)
            {
                var targetBroadcaster = target.Wielder ?? target;

                targetBroadcaster.EnqueueBroadcast(new GameMessageScript(target.Guid, spell.TargetEffect, spell.Formula.Scale));
            }
        }

        /// <summary>
        /// Handles casting SpellType.Enchantment / FellowEnchantment spells
        /// this is also called if SpellType.EnchantmentProjectile successfully hits
        /// </summary>
        public void CreateEnchantment(WorldObject target, WorldObject caster, WorldObject weapon, Spell spell, bool equip = false, bool fromProc = false, bool isWeaponSpell = false)
        {
            // weird itemCaster -> caster collapsing going on here -- fixme

            var player = this as Player;

            var aetheriaProc = false;
            var cloakProc = false;

            // technically unsafe, should be using fromProc
            if (caster.ProcSpell == spell.Id)
            {
                if (caster is Gem && Aetheria.IsAetheria(caster.WeenieClassId))
                {
                    caster = this;
                    aetheriaProc = true;
                }
                else if (Cloak.IsCloak(caster))
                {
                    caster = this;
                    cloakProc = true;
                }
            }
            else if (fromProc)
            {
                // fromProc is assumed to be cloakProc currently
                // todo: change fromProc from bool to WorldObject
                // do we need separate concepts for itemCaster and fromProc objects?
                caster = this;
                cloakProc = true;
            }

            // create enchantment
            var addResult = target.EnchantmentManager.Add(spell, caster, weapon, equip, isWeaponSpell);

            // build message
            var suffix = "";
            switch (addResult.StackType)
            {
                case StackType.Surpass:
                    suffix = $", surpassing {addResult.SurpassSpell.Name}";
                    break;
                case StackType.Refresh:
                    suffix = $", refreshing {addResult.RefreshSpell.Name}";
                    break;
                case StackType.Surpassed:
                    suffix = $", but it is surpassed by {addResult.SurpassedSpell.Name}";
                    break;
            }

            if (aetheriaProc)
            {
                var message = new GameMessageSystemChat($"Aetheria surges on {target.Name} with the power of {spell.Name}!", ChatMessageType.Spellcasting);

                EnqueueBroadcast(message, LocalBroadcastRange, ChatMessageType.Spellcasting);
            }
            else if (player != null && !cloakProc)
            {
                // TODO: replace with some kind of 'rootOwner unless equip' concept?
                // for item casters where the message should be 'You cast', we still need pass the caster as item
                // down this far, to prevent using player's AugmentationIncreasedSpellDuration
                var casterCheck = caster == this || caster is Gem || caster is Food;

                if (casterCheck || target == this || caster != target)
                {
                    var casterName = casterCheck ? "You" : caster.Name;
                    var targetName = target.Name;
                    if (target == this)
                        targetName = casterCheck ? "yourself" : "you";

                    player.SendChatMessage(player, $"{casterName} cast {spell.Name} on {targetName}{suffix}", ChatMessageType.Magic);
                }
            }

            var playerTarget = target as Player;

            if (playerTarget != null)
            {
                playerTarget.Session.Network.EnqueueSend(new GameEventMagicUpdateEnchantment(playerTarget.Session, new Enchantment(playerTarget, addResult.Enchantment)));

                playerTarget.HandleSpellHooks(spell);

                if (!spell.IsBeneficial && this is Creature creatureCaster)
                    playerTarget.SetCurrentAttacker(creatureCaster);
            }

            if (playerTarget == null && target.Wielder is Player wielder)
                playerTarget = wielder;

            if (playerTarget != null && playerTarget != this && !cloakProc)
            {
                var targetName = target == playerTarget ? "you" : $"your {target.Name}";

                playerTarget.SendChatMessage(this, $"{caster.Name} cast {spell.Name} on {targetName}{suffix}", ChatMessageType.Magic);
            }
        }

        /// <summary>
        /// Handles casting SpellType.Boost / FellowBoost spells
        /// typically for Life Magic, ie. Heal, Harm
        /// </summary>
        private void HandleCastSpell_Boost(Spell spell, Creature targetCreature)
        {
            var player = this as Player;
            var creature = this as Creature;

            // prevent double deaths from indirect casts
            // caster is already checked in player/monster, and re-checking caster here would break death emotes such as bunny smite
            if (targetCreature != null && targetCreature.IsDead)
                return;

            // handle negatives?
            int minBoostValue = Math.Min(spell.Boost, spell.MaxBoost);
            int maxBoostValue = Math.Max(spell.Boost, spell.MaxBoost);

            var resistanceType = minBoostValue > 0 ? GetBoostResistanceType(spell.VitalDamageType) : GetDrainResistanceType(spell.VitalDamageType);

            int tryBoost = ThreadSafeRandom.Next(minBoostValue, maxBoostValue);

            // FORK: a harmful life boost (Harm) now rides the caster's two weapon levers, the same way a war
            // bolt does at SpellProjectile.CalculateDamage. Retail applied ONLY the target's resistance here,
            // which is why Harm was the one nuke in the game that no weapon could improve.
            var isHarmfulLifeBoost = minBoostValue < 0 && spell.VitalDamageType == DamageType.Health;

            var lifeElementalMod = 1.0f;
            var lifeWeaponResistMod = 1.0f;

            if (isHarmfulLifeBoost)
                GetLifeCasterMods(targetCreature, spell, out lifeElementalMod, out lifeWeaponResistMod);

            // The Harm base-damage retune is DATA, not code - Content/sql/patches/harm_damage_retune.sql
            // raises spell.boost / boost_Variance directly, so it applies to monster casts too (accepted,
            // user 2026-08-02). Nothing to do here.

            // FORK: Harm can now CRITICALLY HIT. Retail never rolled a crit here at all - HandleCastSpell_Boost
            // resolves entirely outside SpellProjectile, so the crit branch that every war bolt and Martyr's
            // Hecatomb passes through was simply not on Harm's path. With CriticalStrike/CripplingBlow imbues
            // that is not a rounding error: a maxed magic imbue is 50% crit chance at 6.0x crit damage
            // (GetCriticalStrikeMod / GetCripplingBlowMod), so Harm was giving up roughly 150% expected damage
            // against a war bolt purely by which method resolved it.
            //
            // Applied to the ROLL, before the cloak proc below, so a cloak reduces the critted number - the
            // same order SpellProjectile uses.
            var lifeCritMultiplier = 1.0f;

            if (isHarmfulLifeBoost && TryLifeCriticalHit(targetCreature, spell, out var harmCritDamageMod))
                lifeCritMultiplier = 1.0f + 0.5f * harmCritDamageMod;

            // FORK: the Blood Mage life-strike package on Harm - Blood Price (paid at cast time) and the
            // Sanguine Reserve charge ramp. Harm still BUILDS the pool; it no longer spends it.
            //
            // HARM IS NOT EXSANGUINATE-ELIGIBLE (user ruling, live test 2026-08-03: "Exsanguinate should be
            // specifically for Hecatomb or Raven Fury"). This supersedes BLOOD-MAGE-DESIGN sec 3's "Harm or
            // Hecatomb" row. The burst belongs entirely to the two life projectiles now, and
            // ApplyHarmClassAbilityDamage is structurally unable to fire it - see that method.
            //
            // PLAYER-CAST AND PvE ONLY, the same gate as GetLifeCasterMods / TryLifeCriticalHit above.
            // The Weakened Blood mark this cast applies is deliberately NOT part of this multiplier: it
            // rides Creature.GetLifeVulnerabilityMod inside the GetResistanceMod call below, so it takes
            // MAX against weapon rending instead of multiplying with it.
            var lifeClassAbilityMod = 1.0f;

            if (isHarmfulLifeBoost && this is Player bloodMage && targetCreature is not Player)
                lifeClassAbilityMod = bloodMage.ApplyHarmClassAbilityDamage();

            // tryBoost is NEGATIVE for Harm, so this is a multiply rather than an add - a bonus added to a
            // negative roll would heal the target.
            tryBoost = (int)Math.Round(tryBoost
                * targetCreature.GetResistanceMod(resistanceType, this, null, lifeWeaponResistMod)
                * lifeElementalMod
                * lifeCritMultiplier
                * lifeClassAbilityMod);

            int boost = tryBoost;

            // handle cloak damage proc for harm other
            var equippedCloak = targetCreature?.EquippedCloak;

            if (targetCreature != this && spell.VitalDamageType == DamageType.Health && tryBoost < 0)
            {
                var percent = (float)-tryBoost / targetCreature.Health.MaxValue;

                if (equippedCloak != null && Cloak.HasDamageProc(equippedCloak) && Cloak.RollProc(equippedCloak, percent))
                {
                    var reduced = -Cloak.GetReducedAmount(this, -tryBoost);

                    Cloak.ShowMessage(targetCreature, this, -tryBoost, -reduced);

                    tryBoost = boost = reduced;
                }
            }

            // Sanguine Ward (Blood Mage T3): the transient absorb pool eats the hit BEFORE it reaches
            // Health. Harm never reaches Player.TakeDamage, where the ward used to be consumed from, so it
            // did nothing against a Harm until this call was added.
            //
            // A REDUCTION. tryBoost AND boost are both rewritten here (the same pairing the cloak proc
            // above uses), so the vital write, the caster's "you drain N points" line, the victim's line
            // and the death check all see the post-ward number.
            //
            // Placed AFTER the cloak damage proc, matching the physical path's order, and gated to the
            // harmful Health direction only so a Heal can never consume the pool. No attacker filter and no
            // class_abilities_enabled gate: the physical site in Player.TakeDamage calls the ward
            // unconditionally, before any PvP/self/dead-attacker filtering, so the ward already absorbs PvP
            // and self-damage and this site must not be stricter. Deliberately NOT gated on
            // `targetCreature != this` either, unlike the cloak block above, for that same reason.
            if (spell.VitalDamageType == DamageType.Health && tryBoost < 0 && targetCreature is Player sanguineWardTarget)
            {
                var afterSanguineWard = (int)sanguineWardTarget.AbsorbWithSanguineWard(this, (uint)-tryBoost);

                tryBoost = boost = -afterSanguineWard;
            }

            // Mana Barrier (Archmage T2): Harm writes the victim's Health straight to the vital below and
            // never reaches Player.TakeDamage, so the incoming-damage dispatch that used to carry the
            // barrier on a melee/missile hit never runs here. Absorbed at this point instead, in the ward's
            // slot immediately above and with the identical rewrite - tryBoost AND boost both - so the
            // vital write, both combat lines and HandleBoostTransferDeath at the end of the method all see
            // the post-barrier number.
            //
            // IT USED TO SIT BELOW THE VITAL WRITE AS A REFUND, AND THAT WAS A BUG: UpdateVitalDelta clamps
            // at zero, so on a killing Harm the barrier was handed the victim's remaining health instead of
            // the damage thrown, refunded a share of that, and averted a death it had not paid for.
            //
            // `boost` IS NOW REWRITTEN, where the old refund deliberately left it alone. That is the
            // intended consequence of the convention change, and it matches the ward sitting right above:
            // the caster's "you drain N points" line reports what the victim actually lost, so the absorb
            // line and the damage line add up. Gated to the harmful Health direction only, so a beneficial
            // Heal is never touched.
            if (spell.VitalDamageType == DamageType.Health && tryBoost < 0 && targetCreature is Player manaBarrierTarget)
            {
                var afterManaBarrier = (int)manaBarrierTarget.AbsorbWithManaBarrier(this, (uint)-tryBoost);

                tryBoost = boost = -afterManaBarrier;
            }

            // Monster combat effects: the non-player mirror of the Sanguine Ward call above, gated to the
            // harmful Health direction for the same reason, and rewriting tryBoost AND boost the same way so
            // the vital write, both combat lines and the death check see the filtered number.
            //
            // THIS IS THE THIRD independent path by which damage reaches a monster: life magic writes the
            // vital right here, so neither Creature.TakeDamage nor SpellProjectile.DamageTarget covers a
            // Harm landing on one. A filter wired at only two of the three is silently partial.
            if (spell.VitalDamageType == DamageType.Health && tryBoost < 0 && targetCreature is not Player)
            {
                var afterMonsterEffects = (int)targetCreature.AbsorbMonsterEffectDamage(this, DamageType.Health, (uint)-tryBoost);

                tryBoost = boost = -afterMonsterEffects;
            }

            string srcVital;

            switch (spell.VitalDamageType)
            {
                case DamageType.Mana:
                    boost = targetCreature.UpdateVitalDelta(targetCreature.Mana, tryBoost);
                    srcVital = "mana";
                    break;
                case DamageType.Stamina:
                    boost = targetCreature.UpdateVitalDelta(targetCreature.Stamina, tryBoost);
                    srcVital = "stamina";
                    break;
                default:   // Health
                    boost = targetCreature.UpdateVitalDelta(targetCreature.Health, tryBoost);
                    srcVital = "health";

                    if (boost >= 0)
                        targetCreature.DamageHistory.OnHeal((uint)boost);
                    else
                    {
                        targetCreature.DamageHistory.Add(this, DamageType.Health, (uint)-boost);

                        // World Events measured boss damage scaling (TECH-DESIGN 2.16). Life magic resolved
                        // without a projectile - Harm / drain health - reaches neither Player.TakeDamage nor
                        // SpellProjectile.DamageTarget, so it needs its own call to the same hook. Crit is
                        // false here: the life crit above is a FORK addition on the player-cast path only
                        // (it is gated on `this is Player`), so a monster's Harm never crits.
                        ACE.Server.WorldEvents.WorldEventBossDamageHook.NoteHit(this, targetCreature as Player, -boost, false);

                        // summon damage feed ("/summondamage"): the third route a pet's damage can take to a
                        // creature's health, alongside Creature.TakeDamage (melee, missile) and
                        // SpellProjectile.DamageTarget (spell projectiles). A Harm / drain-health resolves
                        // with no projectile at all and writes the vital right here, so it reaches neither.
                        if (this is Pet damagingPet)
                            damagingPet.NotifyOwnerOfDamage(targetCreature, -boost);
                    }

                    //if (targetPlayer != null && targetPlayer.Fellowship != null)
                        //targetPlayer.Fellowship.OnVitalUpdate(targetPlayer);

                    break;
            }

            if (player != null)
            {
                string casterMessage;

                if (player != targetCreature)
                {
                    if (spell.IsBeneficial)
                        casterMessage = $"With {spell.Name} you restore {boost} points of {srcVital} to {targetCreature.Name}.";
                    else
                        casterMessage = $"With {spell.Name} you drain {Math.Abs(boost)} points of {srcVital} from {targetCreature.Name}.";
                }
                else
                {
                    var verb = spell.IsBeneficial ? "restore" : "drain";

                    casterMessage = $"You cast {spell.Name} and {verb} {Math.Abs(boost)} points of your {srcVital}.";
                }

                player.SendChatMessage(player, casterMessage, ChatMessageType.Magic);
            }

            if (targetCreature is Player targetPlayer && player != targetPlayer)
            {
                string targetMessage;

                if (spell.IsBeneficial)
                    targetMessage = $"{Name} casts {spell.Name} and restores {boost} points of your {srcVital}.";
                else
                {
                    targetMessage = $"{Name} casts {spell.Name} and drains {Math.Abs(boost)} points of your {srcVital}.";

                    if (creature != null)
                        targetPlayer.SetCurrentAttacker(creature);
                }

                targetPlayer.SendChatMessage(player, targetMessage, ChatMessageType.Magic);
            }

            // FORK: a LANDED Harm leaves Weakened Blood on the target (Blood Mage T2). Applied here, after
            // the damage has resolved, so the applying strike does not amplify itself - the mark is for
            // every life hit that follows, from any caster.
            if (isHarmfulLifeBoost && boost < 0 && targetCreature != this && targetCreature is not Player && this is Player weakeningCaster)
                weakeningCaster.TryApplyWeakenedBlood(targetCreature);

            if (targetCreature != this && targetCreature.IsAlive && spell.VitalDamageType == DamageType.Health && boost < 0)
            {
                // handle cloak spell proc
                if (equippedCloak != null && Cloak.HasProcSpell(equippedCloak))
                {
                    var pct = (float)-boost / targetCreature.Health.MaxValue;

                    // ensure message is sent after enchantment.Message
                    var actionChain = new ActionChain();
                    actionChain.AddDelayForOneTick();
                    actionChain.AddAction(this, () => Cloak.TryProcSpell(targetCreature, this, equippedCloak, pct));
                    actionChain.EnqueueChain();
                }

                // ensure emote process occurs after damage msg
                var emoteChain = new ActionChain();
                emoteChain.AddDelayForOneTick();
                emoteChain.AddAction(targetCreature, () => targetCreature.EmoteManager.OnDamage(creature));
                //if (critical)
                //    emoteChain.AddAction(target, () => target.EmoteManager.OnReceiveCritical(creature));
                emoteChain.EnqueueChain();
            }

            HandleBoostTransferDeath(creature, targetCreature);
        }

        /// <summary>
        /// Returns the boost resistance for a damage type
        /// </summary>
        private static ResistanceType GetBoostResistanceType(DamageType damageType)
        {
            switch (damageType)
            {
                case DamageType.Health:
                    return ResistanceType.HealthBoost;
                case DamageType.Stamina:
                    return ResistanceType.StaminaBoost;
                case DamageType.Mana:
                    return ResistanceType.ManaBoost;
                default:
                    return ResistanceType.Undef;
            }
        }

        /// <summary>
        /// FORK ADDITION - the two caster-weapon damage levers, for LIFE magic.
        ///
        /// A war caster gets both of these at SpellProjectile.CalculateDamage. Martyr's Hecatomb and Curse of
        /// Raven Fury go through SpellProjectile and pick them up there; HARM does not - it resolves entirely
        /// inside HandleCastSpell_Boost - so this method exists for Harm.
        ///
        /// DRAIN is deliberately NOT a caller: it is excluded from the life-vulnerability axis (user,
        /// 2026-08-02) because its TransferCap makes the multiplier misbehave. See the comment in
        /// HandleCastSpell_Transfer.
        ///
        /// The two levers are deliberately separate axes and DO multiply with each other:
        ///   - elementalMod    = "Damage bonus for Blood spells" (PropertyFloat.ElementalDamageMod), the
        ///                       conservative flat multiplier, halved against players by the caster helper.
        ///   - weaponResistMod = "Resistance Cleaving: Health" / Blood Rending, the large multiplier. This one
        ///                       is folded into the single life-vulnerability axis by
        ///                       Creature.GetLifeVulnerabilityMod, so it takes MAX against any other
        ///                       vulnerability rather than multiplying with it.
        ///
        /// Player-cast and PvE only, matching the class-ability and weapon-mod gates in SpellProjectile:
        /// monsters casting Harm at players must not start scaling off gear.
        /// </summary>
        private void GetLifeCasterMods(Creature targetCreature, Spell spell, out float elementalMod, out float weaponResistMod)
        {
            elementalMod = 1.0f;
            weaponResistMod = 1.0f;

            if (this is not Player player || targetCreature == null || targetCreature is Player)
                return;

            var wand = player.GetEquippedWand();

            if (wand == null)
                return;

            var attackSkill = player.GetCreatureSkill(spell.School);

            elementalMod = GetCasterElementalDamageModifier(wand, player, targetCreature, DamageType.Health);
            weaponResistMod = GetWeaponResistanceModifier(wand, player, attackSkill, DamageType.Health);
        }

        /// <summary>
        /// FORK ADDITION - critical hits for the two life spells that resolve OUTSIDE SpellProjectile.
        ///
        /// WHY THIS EXISTS. Every other damage source in the game can crit. War bolts, Martyr's Hecatomb and
        /// Curse of Raven Fury all roll one at SpellProjectile.CalculateDamage:468, which is computed BEFORE
        /// the life/war branch split and so is already shared - Hecatomb has always critted. Harm and Drain
        /// never reach that method, so they were the only damaging spells in the game that could not crit at
        /// all. That is a mechanism gap, not a balance decision.
        ///
        /// NOTE FOR ANYONE READING ace_world.spell: `crit_Freq` DOES NOT GATE THIS, and does not gate spell
        /// crits anywhere. Spell.CritFrequency (Entity/SpellProperties.cs) has ZERO consumers in the whole
        /// server - it is dead data, and its own definition carries the author's "// default: 0, 1, or 0.03?"
        /// hedge. Reading `crit_Freq = 0` off Hecatomb and concluding it cannot crit is a real and repeated
        /// mistake; the only thing that decides a spell crit is GetWeaponMagicCritFrequency.
        ///
        /// MAGNITUDE - and the two systems that feed it, which are easy to conflate:
        ///
        ///   - IMBUES (ImbuedEffectType.CriticalStrike 0x1 / CripplingBlow 0x2) are the strong versions:
        ///     GetCriticalStrikeMod = (baseSkill - 60) / 600 caps at 50% crit chance; GetCripplingBlowMod
        ///     = baseSkill / 60 caps at MaxCripplingBlowMod = 6.0x crit damage.
        ///   - "Biting Strike" and "Crushing Blow" are the WEAKER NON-IMBUE versions, and they are the plain
        ///     properties PropertyFloat.CriticalFrequency 147 and CriticalMultiplier 136 - obtained by
        ///     TINKERING, not imbuing. Both consumers combine property and imbue with Math.Max, so an item
        ///     never benefits from carrying both forms of the same effect.
        ///
        /// The two forms compete for the same weapon: an imbue slot spent on Critical Strike is a slot NOT
        /// spent on a rend, so the typical build imbues a REND and tinkers Crushing Blow / Biting Strike.
        /// A Sanguine caster wants Blood Rending in that slot, so it should be assumed to carry the TINKERED
        /// crit values, not the imbued ones. Do not model this at 50% / 6.0x.
        ///
        /// Expected damage multiplier is `1 + critChance * 0.5 * critDamageMod`. Measured against ace_world
        /// (WeenieType.Caster = 35): of 435 casters, **66 carry CriticalFrequency 147** (0.06 to 0.75) and
        /// **15 carry CriticalMultiplier 136** (1.7 to 3.5). So the values are NOT rare and a caster does not
        /// start from the 5% default - the ledger's own endgame reference caster, ace51989-rynthidtentaclewand,
        /// ships CriticalFrequency 0.30. At 0.30 crit and a 3.0 multiplier the expected damage multiplier is
        /// ~1.45, which is what Harm and Drain were giving up entirely before this change.
        ///
        /// PLAYER-CAST AND PvE ONLY, matching GetLifeCasterMods. 409 creature weenies cast Harm (315 on live
        /// landblocks) and monsters cast Drain too - letting them crit would silently retune a wide swathe of
        /// content that nobody asked to change. The PvE gate also means AugmentationCriticalDefense never
        /// needs consulting here: it only ever mitigates crits taken by a player.
        /// </summary>
        private bool TryLifeCriticalHit(Creature targetCreature, Spell spell, out float critDamageMod)
        {
            critDamageMod = 1.0f;

            if (this is not Player player || targetCreature == null || targetCreature is Player)
                return false;

            var wand = player.GetEquippedWand();
            var attackSkill = player.GetCreatureSkill(spell.School);

            var criticalChance = GetWeaponMagicCritFrequency(wand, player, attackSkill, targetCreature);

            if (ThreadSafeRandom.Next(0.0f, 1.0f) >= criticalChance)
                return false;

            critDamageMod = GetWeaponCritDamageMod(wand, player, attackSkill, targetCreature);

            return true;
        }

        /// <summary>
        /// FORK ADDITION - how far a fellow or summon may stand from a blood mage and still catch the Drain
        /// surplus. Deliberately short: this is a "stand with your healer" range, not a fellowship-wide aura.
        /// </summary>
        private const float DrainSurplusRange = 10.0f;

        /// <summary>
        /// FORK ADDITION - the visual played on anyone a Drain Health returns health to: the CASTER, once
        /// per cast, and every fellow or summon who catches part of the surplus. Both read this one constant
        /// so the two can never drift to different scripts.
        ///
        /// VERIFIED AGAINST portal.dat, NOT INFERRED FROM THE ENUM NAME. Nothing in this repo plays any
        /// HealthUp* script - even Healer.cs's healing-kit path plays no effect at all - so there was no
        /// in-repo precedent to copy, and the Red/Blue/Yellow suffixes look like arbitrary colours.
        ///
        /// Probing DatManager.PortalDat.SpellTable settles it. All twelve retail Heal Self / Heal Other
        /// tiers (spellIds 5, 6, 1157-1166) carry SpellBase.TargetEffect = 0x1F, which is exactly
        /// PlayScript.HealthUpRed - so this is the script the client already plays for a heal.
        ///
        /// The same probe also shows the suffix is the VITAL, not a palette choice, which is why the
        /// neighbouring values would be wrong here:
        ///   0x1F HealthUpRed      Heal Self / Heal Other          (health up)   &lt;- this one
        ///   0x20 HealthDownRed    Harm and Drain Health Other     (health down)
        ///   0x21 HealthUpBlue     Infuse Mana Other               (mana up)
        ///   0x23 HealthUpYellow   Revitalize Self / Other         (stamina up)
        /// </summary>
        private const PlayScript DrainSurplusHealEffect = PlayScript.HealthUpRed;

        /// <summary>
        /// FORK ADDITION - the eligibility half of Drain Health's surplus cascade (user, 2026-08-03).
        /// See <see cref="DrainSurplusDistribution"/> for the allocation math, and the comment at the
        /// maxDestVitalChange site in HandleCastSpell_Transfer for why the surplus exists at all.
        ///
        /// RECIPIENTS ARE FELLOWS **AND THE CASTER'S OWN SUMMONS** (user, 2026-08-03: "Transfusion should
        /// also work on player summons in range"). That is why this returns List&lt;Creature&gt; rather than
        /// List&lt;Player&gt;: a Pet is a Creature, not a Player, and it has no session to send chat to - see
        /// the payout loop in HandleCastSpell_Transfer, which routes a summon's heal notice to its OWNER.
        /// A summon is subject to exactly the same per-recipient filters as a fellow (alive, caster's own
        /// landblock, within <see cref="DrainSurplusRange"/> of the caster, actually missing health).
        ///
        /// ONLY THE CASTER'S OWN SUMMONS, never a fellow's. The only reads are Player.CurrentActivePet and
        /// Player.SecondaryActivePet off the CASTER; SecondaryActivePet is usually null because it exists
        /// only for holders of the separate Summon 2x ability.
        ///
        /// THE CASCADE IS A CLASS ABILITY, NOT BASE BEHAVIOUR (user, 2026-08-03). It is gated on the Blood
        /// Mage skill "Transfusion" (<see cref="ClassAbilityId.Transfusion"/>), a T1 entry with 3 ranks at
        /// cost 1/1/1. Rank does NOT change the drain: it sets the DELIVERY FRACTION of the surplus
        /// (40/70/100%), plus an additive, deliberately uncapped Healing rider. See
        /// <see cref="DrainSurplusDistribution.ShareFraction"/> for why a share above 100% is safe.
        ///
        /// DO NOT "FIX" THE ZERO-DRAIN CASE FOR EVERYONE. Retail bounds the transfer by the caster's own
        /// missing health, so a life caster at full health drains for ZERO - the scalar in
        /// HandleCastSpell_Transfer is literally 0. That looks like a bug and it is not: it is retail, and
        /// as of this ruling it is the intended baseline. Transfusion is the Blood Mage's answer to it, and
        /// lifting the restriction school-wide would delete the entire reason the ability exists. If a
        /// future session wants full-value Drain for non-Blood-Mages, that is a design decision for the
        /// repo owner, not a cleanup.
        ///
        /// Returns null - meaning "behave exactly as retail" - unless ALL of the following hold. A caster
        /// without Transfusion, with neither a fellowship nor a summon, or with recipients present but none
        /// of them eligible, takes the null path and is byte-identical to the old behaviour.
        ///
        ///  - the spell is a Health drain whose destination is also Health. The Source check matches the
        ///    existing crit gate in this method; the Destination check is what makes it safe to add MISSING
        ///    HEALTH to maxDestVitalChange, which is otherwise a missing-mana or missing-stamina figure.
        ///    Stamina and Mana drains keep retail behaviour untouched.
        ///  - PLAYER-CAST AND PvE ONLY, the same gate as TryLifeCriticalHit / GetLifeCasterMods: the caster
        ///    is a Player, the caster is the transfer destination, and the drained target is NOT a Player.
        ///    Monsters casting Drain are completely unaffected, and this never becomes a PvP lever.
        ///  - the caster has learned Transfusion (rank >= 1). Rank 0 is the retail path; rank 1+ scales the
        ///    delivered share, never the drain, via the shareFraction out parameter.
        ///
        /// NOTE THAT THE CRIT WORK IS DELIBERATELY NOT GATED. TryLifeCriticalHit, the Harm crit and the
        /// crit-raised Drain TransferCap are SCHOOL-WIDE mechanism fixes for life magic - Harm and Drain
        /// were the only damaging spells in the game that could not crit at all - and they are not class
        /// abilities. Only the cascade is behind Transfusion.
        ///
        /// A recipient is eligible when they are in the caster's fellowship OR are one of the caster's own
        /// summons, are not the caster, are alive, are on the CASTER'S OWN LANDBLOCK, and are within
        /// <see cref="DrainSurplusRange"/> of the caster.
        ///
        /// THE LANDBLOCK FILTER IS A DELIBERATE APPROXIMATION, NOT A BUG (user-accepted, 2026-08-03). A
        /// fellow standing 5m away but across a landblock boundary is excluded. It is filtered FIRST, before
        /// any distance math, so every Location read stays on this landblock's own tick thread. Do not
        /// "fix" it by widening the check - reading a position owned by another landblock's thread is the
        /// thing this is avoiding, and the lost edge case is worth far less than that.
        ///
        /// Cost: one pass over the fellowship roster per Drain cast, plus two direct property reads for the
        /// summons. Fellowship.MaxFellows defaults to 20 and is hard-clamped to 100 (Entity/Fellowship.cs),
        /// so the worst case is a 102-element scan on a spell that already does far more work than that.
        /// No caching, and none is wanted.
        ///
        /// NAMING: the out parameters stay fellowMissing / fellowMissingTotal, and the returned list is
        /// still the "fellows" list at the call site, because that accounting is what feeds
        /// <see cref="DrainSurplusDistribution.Distribute"/> unchanged. Read "fellow" throughout this
        /// mechanic as "surplus recipient" - a fellowship member or one of the caster's own summons.
        /// </summary>
        private List<Creature> GetDrainSurplusFellows(Spell spell, bool isDrain, Creature destination, Creature targetCreature, out List<uint> fellowMissing, out ulong fellowMissingTotal, out double shareFraction)
        {
            fellowMissing = null;
            fellowMissingTotal = 0;
            shareFraction = 0.0;

            // spell shape first, so a non-drain transfer never pays for a class-ability or fellowship lookup
            if (!DrainSurplusEligibility.SpellQualifies(isDrain, spell.Source, spell.Destination))
                return null;

            var caster = this as Player;

            // THE CLASS GATE lives in here: transfusionRank. The cascade is the Blood Mage ability
            // "Transfusion", not base life magic. Rank 0 returns null and Drain behaves exactly as retail -
            // see the doc comment above for why that baseline is intended, not a bug to fix school-wide.
            //
            // A null caster (a monster) short-circuits both lookups below to their defaults, so a monster
            // casting Drain still touches neither the class-ability cache nor a fellowship.
            var transfusionRank = caster?.GetClassAbilityRank(ClassAbilityId.Transfusion) ?? 0;

            // hasFellowship OR hasSummon: a solo Blood Mage with a hurt pet beside them is a real cascade
            // target, so a fellowship is no longer required (user, 2026-08-03). Both are PRESENCE checks -
            // the alive/landblock/range/missing filters run below on fellows and summons alike.
            if (!DrainSurplusEligibility.CasterQualifies(
                    casterIsPlayer: caster != null,
                    casterIsTransferDestination: caster != null && destination == caster,
                    targetIsEligibleVictim: targetCreature != null && targetCreature is not Player,
                    transfusionRank: transfusionRank,
                    hasFellowship: caster?.Fellowship != null,
                    hasSummon: caster?.CurrentActivePet != null || caster?.SecondaryActivePet != null))
                return null;

            // How much of the surplus is DELIVERED. Rank sets the base fraction (40/70/100%); the Healing
            // rider is additive on top and uncapped, so a skilled healer passes on more than the drain
            // produced. This is delivery only - it never touches srcVitalChange, TransferCap or the caster's
            // own share, which is exactly why Drain damage is identical at every rank of Transfusion.
            shareFraction = TransfusionAbility.ShareFraction(caster, transfusionRank);

            var casterLandblock = caster.CurrentLandblock;
            var casterLocation = caster.Location;

            if (casterLandblock == null || casterLocation == null)
                return null;

            List<Creature> results = null;
            List<uint> missingByRecipient = null;
            ulong missingTotal = 0;

            // The per-recipient filter, identical for fellows and summons. A local function rather than a
            // method because it closes over the caster's landblock and location, which are read exactly once
            // above; note it cannot capture the out parameters directly (C# forbids that), so the totals are
            // accumulated into locals and handed to the out parameters at the end.
            void TryAddRecipient(Creature recipient)
            {
                if (recipient == null || recipient == caster || !recipient.IsAlive)
                    return;

                // landblock first - see the approximation note above
                if (recipient.CurrentLandblock != casterLandblock)
                    return;

                if (recipient.Location == null || casterLocation.DistanceTo(recipient.Location) > DrainSurplusRange)
                    return;

                var missing = recipient.Health.Missing;

                if (missing == 0)
                    return;

                // a recipient must never appear twice, or Distribute would hand them two shares against one
                // pool of missing health. Fellowship members are unique by construction; the summon slots are
                // the case this actually guards, since nothing structurally forbids the same Pet object
                // sitting in both CurrentActivePet and SecondaryActivePet.
                if (results != null && results.Contains(recipient))
                    return;

                results ??= new List<Creature>();
                missingByRecipient ??= new List<uint>();

                results.Add(recipient);
                missingByRecipient.Add(missing);

                missingTotal += missing;
            }

            // fellows first, then summons. The order is what Distribute's largest-remainder tie-break reads,
            // so it must stay deterministic - it is index order, not an arbitrary enumeration.
            var fellowship = caster.Fellowship;

            if (fellowship != null)
            {
                foreach (var fellow in fellowship.GetFellowshipMembers().Values)
                    TryAddRecipient(fellow);
            }

            // THE CASTER'S OWN SUMMONS ONLY. Never a fellow's pet - these two properties are read off the
            // caster and nowhere else. SecondaryActivePet is null unless the caster holds Summon 2x.
            TryAddRecipient(caster.CurrentActivePet);
            TryAddRecipient(caster.SecondaryActivePet);

            fellowMissing = missingByRecipient;
            fellowMissingTotal = missingTotal;

            return results;
        }

        /// <summary>
        /// Returns the drain resistance for a damage type
        /// </summary>
        private static ResistanceType GetDrainResistanceType(DamageType damageType)
        {
            switch (damageType)
            {
                case DamageType.Health:
                    return ResistanceType.HealthDrain;
                case DamageType.Stamina:
                    return ResistanceType.StaminaDrain;
                case DamageType.Mana:
                    return ResistanceType.ManaDrain;
                default:
                    return ResistanceType.Undef;
            }
        }

        /// <summary>
        /// Returns the boost resistance type for a vital
        /// </summary>
        private static ResistanceType GetBoostResistanceType(PropertyAttribute2nd vital)
        {
            switch (vital)
            {
                case PropertyAttribute2nd.Health:
                    return ResistanceType.HealthBoost;
                case PropertyAttribute2nd.Stamina:
                    return ResistanceType.StaminaBoost;
                case PropertyAttribute2nd.Mana:
                    return ResistanceType.ManaBoost;
                default:
                    return ResistanceType.Undef;
            }
        }

        /// <summary>
        /// Returns the drain resistance type for a vital
        /// </summary>
        private static ResistanceType GetDrainResistanceType(PropertyAttribute2nd vital)
        {
            switch (vital)
            {
                case PropertyAttribute2nd.Health:
                    return ResistanceType.HealthDrain;
                case PropertyAttribute2nd.Stamina:
                    return ResistanceType.StaminaDrain;
                case PropertyAttribute2nd.Mana:
                    return ResistanceType.ManaDrain;
                default:
                    return ResistanceType.Undef;
            }
        }

        /// <summary>
        /// Checks for death from a boost / transfer spell
        /// </summary>
        private void HandleBoostTransferDeath(Creature caster, Creature target)
        {
            if (caster != null && caster.IsDead)
            {
                caster.OnDeath(caster.DamageHistory.LastDamager, DamageType.Health, false);
                caster.Die();
            }

            if (target != null && target.IsDead && target != caster)
            {
                target.OnDeath(target.DamageHistory.LastDamager, DamageType.Health, false);
                target.Die();
            }
        }

        /// <summary>
        /// Handles casting SpellType.Transfer spells
        /// usually for Life Magic, ie. Stamina to Mana, Drain
        /// </summary>
        /// <param name="isSecondaryStrike">
        /// TRUE for one of Crimson Harvest's extra targets. It is the RE-ENTRY GUARD as well as a flag: a
        /// secondary strike never fans out again (so a pack cannot chain into an unbounded harvest) and
        /// never grants a second Blood Charge, which is what makes "at most one charge per cast" true no
        /// matter how many creatures the drain touches. Everything else about a secondary strike - its own
        /// TransferCap, its own resistance roll, its own heal back to the caster, its own Weakened Blood
        /// mark - is deliberately identical to a primary one.
        /// </param>
        private void HandleCastSpell_Transfer(Spell spell, Creature targetCreature, bool isSecondaryStrike = false)
        {
            var player = this as Player;
            var creature = this as Creature;

            var targetPlayer = targetCreature as Player;

            // prevent double deaths from indirect casts
            // caster is already checked in player/monster, and re-checking caster here would break death emotes such as bunny smite
            if (targetCreature != null && targetCreature.IsDead)
                return;

            // source and destination can be the same creature, or different creatures
            var caster = this as Creature;
            var transferSource = spell.TransferFlags.HasFlag(TransferFlags.CasterSource) ? caster : targetCreature;
            var destination = spell.TransferFlags.HasFlag(TransferFlags.CasterDestination) ? caster : targetCreature;

            // Calculate vital changes
            uint srcVitalChange, destVitalChange;

            // Drain Resistances - allows one to partially resist drain health/stamina/mana and harm attacks (not including other life transfer spells).
            var isDrain = spell.TransferFlags.HasFlag(TransferFlags.TargetSource | TransferFlags.CasterDestination);

            // DELIBERATELY RETAIL - Drain is EXCLUDED from the life-vulnerability axis (user, 2026-08-02):
            // "Lets exclude drains from the blood rend/vuln. I have thoughts for making drains better, but I
            // think the multiplier is too much. Blood rend/vuln should apply to harm, heca, raven."
            //
            // Blood Rending, "Resistance Cleaving: Health" and Weakened Blood apply to Harm, Martyr's
            // Hecatomb and Curse of Raven Fury only. Drain is left alone because its TransferCap makes the
            // multiplier behave unlike every other spell: the cap binds against anything worth draining, so
            // scaling the roll alone is invisible and scaling the CAP turns a 200-point filler into a
            // 775-point primary nuke that also heals. Modelling put a cap-scaled Drain slightly AHEAD of a
            // war bolt per cast while still returning 35% of itself as health - the wrong shape for the
            // builder spell in the kit. Drain gets its own treatment separately.
            //
            // A LANDED DRAIN STILL APPLIES THE MARK - see the Weakened Blood block at the end of this
            // method. The ruling is about who the multiplier reaches, not about who can apply it, so a blood
            // mage's Drain still sets up every Harm and Hecatomb that follows, including a fellow's.
            //
            // THE WEAPON HALF NEEDS NO GATE, THE CAST HALF DOES. GetLifeVulnerabilityMod defaults its
            // weaponResistanceMod to 1.0, so simply not passing one leaves Blood Rending out - that much was
            // always true. Weakened Blood lives INSIDE that method and would therefore arrive uninvited, so
            // the Health branch reads GetHealthDrainResistanceOnly() instead: the same
            // ResistHealthDrain * natural * LifeResistRating product, with the vulnerability term left off.
            // That is byte-identical to what GetResistanceMod(HealthDrain) returned before the mark existed.
            // Mana and Stamina drains never touch the axis at all, so they keep the plain call.
            var drainMod = 1.0f;

            if (isDrain)
            {
                drainMod = !WeakenedBloodMath.DamageBenefits(spell.MetaSpellType) && spell.Source == PropertyAttribute2nd.Health
                    ? (float)transferSource.GetHealthDrainResistanceOnly()
                    : (float)transferSource.GetResistanceMod(GetDrainResistanceType(spell.Source));
            }

            srcVitalChange = (uint)Math.Round(transferSource.GetCreatureVital(spell.Source).Current * spell.Proportion * drainMod);

            // FORK: Drain can now CRITICALLY HIT, and a crit RAISES ITS CAP (user, 2026-08-03).
            //
            // The cap is the whole mechanism for Drain - the proportional roll binds against it on anything
            // worth draining, so a crit that scaled only the roll would be invisible. Scaling the cap is what
            // a Drain crit has to mean.
            //
            // The multiplier matches the Harm/Hecatomb crit formula exactly (1 + 0.5 * critDamageMod), so all
            // four life spells crit by the same rule.
            //
            // NOTE THIS IS NOT THE 2026-08-02 RULING BEING REVERSED. That ruling excluded Drain from the
            // life-VULNERABILITY axis - an always-on multiplier from Blood Rending / Weakened Blood - because a
            // permanently cap-scaled Drain becomes a primary nuke that also heals. A crit is probabilistic and
            // costs the player nothing to build around, so the sustained value moves by the crit RATE, not by
            // the full multiplier. Drain remains off the vulnerability axis: drainMod above is still retail.
            var drainCritCapMod = 1.0f;

            if (isDrain && spell.Source == PropertyAttribute2nd.Health
                && TryLifeCriticalHit(targetCreature, spell, out var drainCritDamageMod))
            {
                drainCritCapMod = 1.0f + 0.5f * drainCritDamageMod;
            }

            // TransferCap caps both srcVitalChange and destVitalChange
            // https://asheron.fandom.com/wiki/Announcements_-_2003/01_-_The_Slumbering_Giant#Letter_to_the_Players
            //
            // The crit-raised cap MUST be used at BOTH cap sites. The destination cap below feeds an overflow
            // rescale that drags srcVitalChange back down with it, so raising only the source cap would be
            // silently undone by that rescale on any target worth critting. The heal is still bounded by the
            // caster's ACTUAL missing health (maxDestVitalChange takes the min), so a healthy blood mage gains
            // damage from the crit without gaining a windfall heal.
            var effectiveTransferCap = spell.TransferCap != 0
                ? (int)Math.Round(spell.TransferCap * drainCritCapMod)
                : 0;

            if (effectiveTransferCap != 0 && srcVitalChange > effectiveTransferCap)
                srcVitalChange = (uint)effectiveTransferCap;

            // should healing resistances be applied here?
            var boostMod = isDrain ? (float)destination.GetResistanceMod(GetBoostResistanceType(spell.Destination)) : 1.0f;

            destVitalChange = (uint)Math.Round(srcVitalChange * (1.0f - spell.LossPercent) * boostMod);

            // scale srcVitalChange to destVitalChange?
            var missingDest = destination.GetCreatureVital(spell.Destination).Missing;

            // FORK: a Health drain's SURPLUS now flows to nearby fellows AND to the caster's own summons
            // instead of being thrown away (user, 2026-08-03).
            //
            // Retail bounds maxDestVitalChange by the caster's OWN missing health, and the overflow branch
            // below scales srcVitalChange down by the same ratio - so a caster at full health has
            // missingDest == 0, the scalar is 0, and DRAIN DEALS ZERO DAMAGE. That is the bug: the spell is
            // dead weight exactly when the blood mage is healthy.
            //
            // The fix widens the RECEIVING capacity to the caster plus their eligible fellows and summons, so
            // the drain stops being scaled away. A SOLO caster at full health with one hurt pet in range is
            // the case this must reach: fellowMissingTotal carries the summon's missing health exactly as it
            // carries a fellow's, or the transfer would still be capped to zero and the drain would do
            // nothing. It does NOT widen the ceiling: effectiveTransferCap still clamps
            // maxDestVitalChange immediately below, so the most a single cast can ever move is unchanged.
            // The drain simply reaches that cap more often.
            var drainFellows = GetDrainSurplusFellows(spell, isDrain, destination, targetCreature, out var fellowMissing, out var fellowMissingTotal, out var drainShareFraction);

            var maxDestVitalChange = missingDest;

            if (fellowMissingTotal > 0)
                maxDestVitalChange = (uint)Math.Min(uint.MaxValue, (ulong)missingDest + fellowMissingTotal);

            if (effectiveTransferCap != 0 && maxDestVitalChange > effectiveTransferCap)
                maxDestVitalChange = (uint)effectiveTransferCap;

            if (destVitalChange > maxDestVitalChange)
            {
                var scalar = (float)maxDestVitalChange / destVitalChange;

                srcVitalChange = (uint)Math.Round(srcVitalChange * scalar);
                destVitalChange = maxDestVitalChange;
            }

            // handle cloak damage procs for drain health other
            var equippedCloak = targetCreature?.EquippedCloak;

            if (isDrain && spell.Source == PropertyAttribute2nd.Health)
            {
                var percent = (float)srcVitalChange / targetCreature.Health.MaxValue;

                if (equippedCloak != null && Cloak.HasDamageProc(equippedCloak) && Cloak.RollProc(equippedCloak, percent))
                {
                    var reduced = Cloak.GetReducedAmount(this, srcVitalChange);

                    Cloak.ShowMessage(targetCreature, this, srcVitalChange, reduced);

                    srcVitalChange = reduced;
                    destVitalChange = (uint)Math.Round(srcVitalChange * (1.0f - spell.LossPercent) * boostMod);
                }
            }

            // FORK: split the (already capped) destination transfer between the caster and their recipients
            // (fellows and their own summons).
            //
            // This sits AFTER the cloak proc block on purpose - that block can recompute destVitalChange
            // downward, and the caster must be paid out of the FINAL number. The caster is served first, up
            // to their own missing health; only what they cannot absorb is offered to the fellows.
            //
            // The Transfusion share fraction is applied HERE and nowhere else - after effectiveTransferCap,
            // after the destination cap, after the overflow rescale and after the cloak proc. Every cap has
            // already run against the full surplus, so the caster's own share and srcVitalChange (the actual
            // drain damage) are byte-identical at every rank of the ability. The rank changes only how much
            // of what the caster COULD NOT USE gets passed on.
            uint[] fellowShares = null;

            if (drainFellows != null && destVitalChange > missingDest)
            {
                var deliveredSurplus = DrainSurplusDistribution.ApplyShare(destVitalChange - missingDest, drainShareFraction);

                fellowShares = DrainSurplusDistribution.Distribute(deliveredSurplus, fellowMissing);

                destVitalChange = missingDest;
            }

            string srcVital, destVital;

            // Apply the change in vitals to the source
            switch (spell.Source)
            {
                case PropertyAttribute2nd.Mana:
                    srcVital = "mana";
                    srcVitalChange = (uint)-transferSource.UpdateVitalDelta(transferSource.Mana, -(int)srcVitalChange);
                    break;
                case PropertyAttribute2nd.Stamina:
                    srcVital = "stamina";
                    srcVitalChange = (uint)-transferSource.UpdateVitalDelta(transferSource.Stamina, -(int)srcVitalChange);
                    break;
                default:   // Health
                    srcVital = "health";

                    // Sanguine Ward (Blood Mage T3): the absorb pool eats the hit BEFORE it reaches Health.
                    // A Drain Health never reaches Player.TakeDamage, where the ward used to be consumed
                    // from, so it did nothing against a drain until this call was added. A REDUCTION applied
                    // ahead of the vital write.
                    //
                    // ONLY THE VICTIM'S LOSS IS REDUCED. destVitalChange - what the caster gains - was
                    // computed well upstream, before the fellowship surplus distribution, and is left at the
                    // pre-ward amount on purpose: the drain still feeds the caster in full. Looks like a bug
                    // and is not. Netting the ward out of the transfer would make a warded victim a debuff
                    // on the caster, and would mean restructuring the surplus distribution to re-derive a
                    // number that has already been capped, rescaled and split. Same accounting the Mana
                    // Barrier call below uses, for the same reason.
                    //
                    // Unconditional for a player victim - no attacker filter, no class_abilities_enabled
                    // gate - because the physical site calls the ward unconditionally and so absorbs PvP and
                    // self-damage already.
                    if (transferSource is Player sanguineWardTarget)
                        srcVitalChange = sanguineWardTarget.AbsorbWithSanguineWard(this, srcVitalChange);

                    // Mana Barrier (Archmage T2): a Drain Health writes the victim's Health straight to the
                    // vital just below and never reaches Player.TakeDamage, so the incoming-damage dispatch
                    // that used to carry the barrier on a melee/missile hit never runs here. Absorbed at
                    // this point instead, in the ward's slot immediately above and with the identical
                    // rewrite of srcVitalChange, so the vital write, DamageHistory, the summon damage feed
                    // and HandleBoostTransferDeath at the end of the method all see the post-barrier number.
                    //
                    // IT USED TO SIT BELOW THE VITAL WRITE AS A REFUND, AND THAT WAS A BUG: UpdateVitalDelta
                    // clamps at zero, so on a killing drain the barrier was handed the victim's remaining
                    // health instead of the amount drained, refunded a share of that, and averted a death it
                    // had not paid for.
                    //
                    // THE SAME ASYMMETRY THE WARD KEEPS, for the same reason: only the victim's loss is
                    // reduced. destVitalChange - what the caster gains - is computed far upstream through
                    // the fellowship surplus distribution and is deliberately left alone, so a
                    // barrier-carrying victim is never a debuff on the caster who drained them.
                    if (transferSource is Player manaBarrierTarget)
                        srcVitalChange = manaBarrierTarget.AbsorbWithManaBarrier(this, srcVitalChange);

                    srcVitalChange = (uint)-transferSource.UpdateVitalDelta(transferSource.Health, -(int)srcVitalChange);

                    transferSource.DamageHistory.Add(this, DamageType.Health, srcVitalChange);

                    // summon damage feed ("/summondamage"): the fourth and last route a pet's damage takes to
                    // a creature's health. A Drain Health is a Transfer, not a Boost, so the hook in
                    // HandleCastSpell_Boost does not cover it - the same reason Mana Barrier and Sanguine
                    // Ward each need a call at both sites. Reports the post-ward, post-barrier loss the
                    // victim actually took, which is what srcVitalChange holds by this point.
                    if (this is Pet drainingPet)
                        drainingPet.NotifyOwnerOfDamage(transferSource, (int)srcVitalChange);

                    //var sourcePlayer = source as Player;
                    //if (sourcePlayer != null && sourcePlayer.Fellowship != null)
                        //sourcePlayer.Fellowship.OnVitalUpdate(sourcePlayer);

                    break;
            }

            // Apply the scaled change in vitals to the caster
            switch (spell.Destination)
            {
                case PropertyAttribute2nd.Mana:
                    destVital = "mana";
                    destVitalChange = (uint)destination.UpdateVitalDelta(destination.Mana, destVitalChange);
                    break;
                case PropertyAttribute2nd.Stamina:
                    destVital = "stamina";
                    destVitalChange = (uint)destination.UpdateVitalDelta(destination.Stamina, destVitalChange);
                    break;
                default:   // Health
                    destVital = "health";
                    destVitalChange = (uint)destination.UpdateVitalDelta(destination.Health, destVitalChange);

                    destination.DamageHistory.OnHeal(destVitalChange);

                    //var destPlayer = destination as Player;
                    //if (destPlayer != null && destPlayer.Fellowship != null)
                        //destPlayer.Fellowship.OnVitalUpdate(destPlayer);

                    break;
            }

            // FORK: the caster sees the health a Drain returned to them (user, 2026-08-03: "Heal visual on
            // caster and allies"). Retail plays nothing here - the drain's own CasterEffect fires once at
            // the cast and says nothing about whether it fed you - so this is the caster-side half of the
            // same feedback gap the per-fellow broadcast below already closes.
            //
            // SAME SCRIPT AS THE FELLOWS GET, deliberately the shared DrainSurplusHealEffect constant rather
            // than a second reference to PlayScript.HealthUpRed: the suffix on those PlayScript values is
            // the VITAL, not a colour (0x1F HealthUpRed is health UP, 0x20 HealthDownRed is what this very
            // spell plays on its victim), so the two must never be able to drift to different values.
            //
            // ONCE PER CAST, NOT ONCE PER STRIKE. Crimson Harvest re-enters this method for every secondary
            // target, and each of those strikes heals the caster too - so without the isSecondaryStrike
            // guard a four-target harvest would flash the caster four times in a fraction of a second. The
            // primary strike is the one that reports.
            //
            // ZERO HEALS PLAY NOTHING. destVitalChange is the amount UpdateVitalDelta ACTUALLY applied by
            // this point, so a caster already at full health (missingDest == 0, the retail case the
            // Transfusion cascade exists to answer) gets no flash rather than a misleading one.
            if (isDrain && !isSecondaryStrike && spell.Destination == PropertyAttribute2nd.Health
                && destVitalChange > 0 && destination is Player)
            {
                destination.EnqueueBroadcast(new GameMessageScript(destination.Guid, DrainSurplusHealEffect, spell.Formula.Scale));
            }

            // FORK: pay the surplus out to the recipients - fellows and the caster's own summons - weighted
            // to the most hurt.
            //
            // A recipient with a zero share is skipped entirely - no vital change, no visual, no message. The
            // only way to be in this list at all is to have been missing health at the top of the method, so
            // a zero share means the surplus was too small to reach them, and a silent skip is correct.
            if (fellowShares != null)
            {
                for (var i = 0; i < fellowShares.Length; i++)
                {
                    if (fellowShares[i] == 0)
                        continue;

                    var fellow = drainFellows[i];

                    var fellowGain = (uint)fellow.UpdateVitalDelta(fellow.Health, fellowShares[i]);

                    if (fellowGain == 0)
                        continue;

                    fellow.DamageHistory.OnHeal(fellowGain);

                    // BROADCAST, not a direct send: everyone nearby should see that the blood mage's drain
                    // fed this recipient. The Wielder indirection DoSpellEffects uses at the TargetEffect
                    // site is for WIELDED targets and is vacuous here - neither a Player nor a Pet is ever
                    // wielded - so the recipient broadcasts for itself. This is the half that works
                    // unchanged for a summon: EnqueueBroadcast is a WorldObject concern, not a session one.
                    fellow.EnqueueBroadcast(new GameMessageScript(fellow.Guid, DrainSurplusHealEffect, spell.Formula.Scale));

                    // CHAT IS THE HALF THAT DOES NOT. SendChatMessage is a Player method backed by a session,
                    // and a summon has neither - calling it on a Pet would not compile, and reaching for the
                    // Pet's own "session" would be a null dereference at runtime. A summon's notice therefore
                    // goes to its OWNER, which is always this caster (only the caster's own summons are ever
                    // in this list), worded so the owner can tell it apart from their own heal line.
                    if (fellow is Player fellowPlayer)
                        fellowPlayer.SendChatMessage(this, $"You gain {fellowGain} points of health due to {Name} casting {spell.Name} on {targetCreature.Name}", ChatMessageType.Magic);
                    else if (fellow is Pet summon && summon.P_PetOwner != null)
                        summon.P_PetOwner.SendChatMessage(this, $"Your {summon.Name} gains {fellowGain} points of health due to your {spell.Name} on {targetCreature.Name}", ChatMessageType.Magic);
                }
            }

            // You gain 52 points of health due to casting Drain Health Other I on Olthoi Warrior
            // You lose 22 points of mana due to casting Incantation of Infuse Mana Other on High-Voltage VI
            // You lose 12 points of mana due to Zofrit Zefir casting Drain Mana Other II on you

            // You cast Stamina to Mana Self I on yourself and lose 50 points of stamina and also gain 45 points of mana
            // You cast Stamina to Health Self VI on yourself and fail to affect your  stamina and also gain 1 point of health

            // unverified:
            // You gain X points of vital due to caster casting spell on you
            // You lose X points of vital due to caster casting spell on you

            var playerSource = transferSource as Player;
            var playerDestination = destination as Player;

            string sourceMsg = null, targetMsg = null;

            if (playerSource != null && playerDestination != null && transferSource.Guid == destination.Guid)
            {
                sourceMsg = $"You cast {spell.Name} on yourself and lose {srcVitalChange} points of {srcVital} and also gain {destVitalChange} points of {destVital}";
            }
            else
            {
                if (playerSource != null)
                {
                    if (transferSource == this)
                        sourceMsg = $"You lose {srcVitalChange} points of {srcVital} due to casting {spell.Name} on {targetCreature.Name}";
                    else
                        targetMsg = $"You lose {srcVitalChange} points of {srcVital} due to {caster.Name} casting {spell.Name} on you";

                    if (destination is Creature creatureDestination)
                        playerSource.SetCurrentAttacker(creatureDestination);
                }

                if (playerDestination != null)
                {
                    if (destination == this)
                        sourceMsg = $"You gain {destVitalChange} points of {destVital} due to casting {spell.Name} on {targetCreature.Name}";
                    else
                        targetMsg = $"You gain {destVitalChange} points of {destVital} due to {caster.Name} casting {spell.Name} on you";
                }
            }

            if (player != null && sourceMsg != null)
                player.SendChatMessage(player, sourceMsg, ChatMessageType.Magic);

            if (targetPlayer != null && targetMsg != null)
                targetPlayer.SendChatMessage(caster, targetMsg, ChatMessageType.Magic);


            if (isDrain && targetCreature.IsAlive && spell.Source == PropertyAttribute2nd.Health)
            {
                // handle cloak spell proc
                if (equippedCloak != null && Cloak.HasProcSpell(equippedCloak))
                {
                    var pct = (float)srcVitalChange / targetCreature.Health.MaxValue;

                    // ensure message is sent after enchantment.Message
                    var actionChain = new ActionChain();
                    actionChain.AddDelayForOneTick();
                    actionChain.AddAction(this, () => Cloak.TryProcSpell(targetCreature, this, equippedCloak, pct));
                    actionChain.EnqueueChain();
                }

                // ensure emote process occurs after damage msg
                var emoteChain = new ActionChain();
                emoteChain.AddDelayForOneTick();
                emoteChain.AddAction(targetCreature, () => targetCreature.EmoteManager.OnDamage(creature));
                //if (critical)
                //    emoteChain.AddAction(targetCreature, () => targetCreature.EmoteManager.OnReceiveCritical(creature));
                emoteChain.EnqueueChain();
            }

            // FORK: the Blood Mage's three Drain-side entries. PLAYER-CAST AND PvE ONLY, the same gate as
            // GetLifeCasterMods / TryLifeCriticalHit, and only on a Health drain that actually took health.
            //
            // Drain deliberately takes NO damage multiplier from Sanguine Reserve or Blood Price - it grants
            // a charge without spending one. Its damage is bounded by effectiveTransferCap, so a multiplier
            // on its roll is invisible against anything worth draining, and a multiplier on its cap is the
            // exact shape the drainMod comment above rejects. What Drain contributes to the kit is the ramp
            // and the mark, not its own damage.
            if (isDrain && spell.Source == PropertyAttribute2nd.Health && srcVitalChange > 0
                && this is Player drainCaster && targetCreature is not Player)
            {
                // Sanguine Reserve: one charge per CAST. A Crimson Harvest secondary strike is part of the
                // same cast, so it is skipped here rather than granting a fifth charge from one drain.
                if (!isSecondaryStrike)
                    drainCaster.TryGrantBloodCharge();

                // Weakened Blood: applied by EVERY strike, primary and secondary alike, which is what
                // "applies Weakened Blood to every target it lands on" means for Crimson Harvest.
                drainCaster.TryApplyWeakenedBlood(targetCreature);

                if (!isSecondaryStrike)
                    TryCrimsonHarvest(drainCaster, spell, targetCreature);
            }

            HandleBoostTransferDeath(creature, targetCreature);
        }

        /// <summary>
        /// FORK ADDITION - CRIMSON HARVEST (Blood Mage T2): the caster's Drain spells strike up to 4 more
        /// creatures within 8m of the primary target.
        ///
        /// Each secondary target is drained by RE-ENTERING <see cref="HandleCastSpell_Transfer"/> with
        /// isSecondaryStrike set, rather than by copying a reduced-damage version of the transfer math.
        /// That is the whole design: every secondary drain resolves independently - its own proportional
        /// roll, its own resistance, its own TransferCap clamp, its own crit, its own Transfusion cascade -
        /// and the healing from all of them returns to the caster. Nothing is scaled down for being
        /// secondary; the target count IS the effect (BLOOD-MAGE-DESIGN sec 3 / 4c).
        ///
        /// THE RECOUP IS DELIBERATELY UNCAPPED (user, 2026-08-03): four independent drains can return four
        /// full heals to a caster who is missing enough health to absorb them. That is to be observed in
        /// play before it is tuned. Do NOT add a limiter here without a ruling - the per-cast ceiling that
        /// does exist is each individual strike's own TransferCap, which is untouched.
        ///
        /// Selection mirrors Spell AOE's radiated blasts: the same visible-objects source, the same
        /// hostile-creature filters, and the radius measured from the PRIMARY TARGET rather than the
        /// caster. The only addition is the cap, applied nearest-first by
        /// <see cref="CrimsonHarvestMath.SelectSecondaryTargets"/>.
        ///
        /// The candidate list is materialised before any strike is resolved, because a drain can kill and
        /// a death mutates the landblock's object collections mid-enumeration.
        /// </summary>
        private void TryCrimsonHarvest(Player caster, Spell spell, Creature primaryTarget)
        {
            if (!caster.TryGetClassAbility(ClassAbilityId.CrimsonHarvest, out _))
                return;

            // BLOODLETTING (EquipmentModId.Bloodletting) extends the RADIUS only, additively, inside the
            // ability's own reach figure. The TryGetClassAbility early return directly above is what makes
            // this machinery mod inert without Crimson Harvest, so the plain equipped-value read is correct
            // here rather than the ownership-testing GetMachineryEquipmentModValue.
            //
            // THE TARGET COUNT DELIBERATELY TAKES NO GEAR TERM. maxTargets is an integer cap and a
            // fractional addition to it would round away to nothing (DESIGN.md 2.3, "two discrete numbers
            // are deliberately NOT moddable"); the radius is the continuous half, and widening it only
            // helps a caster reach a cap that still binds.
            var radius = PropertyManager.GetDouble("class_ability_crimsonharvest_radius").Item
                + caster.GetEquippedModValue(EquipmentModId.Bloodletting);

            var maxTargets = (int)PropertyManager.GetLong("class_ability_crimsonharvest_max_targets").Item;

            if (radius <= 0.0 || maxTargets <= 0)
                return;

            // the primary may have just died from the drain; its WorldObject lingers (removal is deferred)
            // but guard against a torn-down Location/PhysicsObj before reading them
            if (caster.PhysicsObj == null || primaryTarget.Location == null)
                return;

            var visible = caster.PhysicsObj.ObjMaint.GetVisibleObjectsValuesWhere(o => o.WeenieObj.WorldObject != null);

            var candidates = new List<Creature>();
            var distances = new List<double>();

            foreach (var obj in visible)
            {
                if (obj.WeenieObj.WorldObject is not Creature creature)
                    continue;

                if (creature == primaryTarget || creature == caster)
                    continue;

                if (creature.IsDead || creature.Teleporting || creature.Location == null)
                    continue;

                // PvE exclusion: the harvest never spreads to players or to the caster's own pets
                if (creature is Player || creature is CombatPet)
                    continue;

                if (!caster.CanDamage(creature) || caster.CheckPKStatusVsTarget(creature, null) != null)
                    continue;

                candidates.Add(creature);
                distances.Add(primaryTarget.Location.DistanceTo(creature.Location));
            }

            if (candidates.Count == 0)
                return;

            foreach (var index in CrimsonHarvestMath.SelectSecondaryTargets(distances, radius, maxTargets))
            {
                var secondary = candidates[index];

                if (secondary.IsDead)
                    continue;

                HandleCastSpell_Transfer(spell, secondary, isSecondaryStrike: true);

                // FORK: play the drain visual on every harvested target, not just the primary (user, live
                // test 2026-08-03: "should show the same health drain animation on all of the enemies it
                // applies to").
                //
                // The primary gets this from DoSpellEffects, which runs once per CAST at the end of
                // HandleCastSpell and therefore never sees a secondary. Re-broadcasting the SPELL'S OWN
                // TargetEffect is what makes "the same animation" literally true - for Drain Health Other
                // that is HealthDownRed, read from portal.dat rather than named here, so the two can never
                // drift apart. Only the target half is replayed: the caster effect belongs to the cast and
                // has already played once.
                //
                // Ordered after the strike, matching DoSpellEffects running after the handler.
                if (spell.TargetEffect != 0)
                    secondary.EnqueueBroadcast(new GameMessageScript(secondary.Guid, spell.TargetEffect, spell.Formula.Scale));
            }
        }

        /// <summary>
        /// Handles casting SpellType.Projectile / LifeProjectile / EnchantmentProjectile spells
        /// </summary>
        private void HandleCastSpell_Projectile(Spell spell, WorldObject target, WorldObject itemCaster, WorldObject weapon, bool isWeaponSpell, bool fromProc)
        {
            uint damage = 0;
            var caster = this as Creature;
            var damageType = DamageType.Undef;

            if (spell.School == MagicSchool.LifeMagic)
            {
                if (spell.DamageType.HasFlag(DamageType.Mana))
                {
                    var tryDamage = (int)Math.Round(caster.GetCreatureVital(PropertyAttribute2nd.Mana).Current * spell.DrainPercentage);
                    damage = (uint)-caster.UpdateVitalDelta(caster.Mana, -tryDamage);
                    damageType = DamageType.Mana;
                }
                else if (spell.DamageType.HasFlag(DamageType.Stamina))
                {
                    var tryDamage = (int)Math.Round(caster.GetCreatureVital(PropertyAttribute2nd.Stamina).Current * spell.DrainPercentage);
                    damage = (uint)-caster.UpdateVitalDelta(caster.Stamina, -tryDamage);
                    damageType = DamageType.Stamina;
                }
                else if (spell.DamageType.HasFlag(DamageType.Health))
                {
                    var healthBefore = caster.GetCreatureVital(PropertyAttribute2nd.Health).Current;
                    var tryDamage = (int)Math.Round(healthBefore * spell.DrainPercentage);

                    // Sanguine Ward (Blood Mage T3) decouples what the caster PAYS from what the spell is
                    // WORTH: the damage basis stays the full amount, only the health deduction shrinks, to
                    // 80/65/50% by rank. GetSanguineWardSelfCostFraction returns 1.0 for anyone without the
                    // ability, so this applies unconditionally and is retail-identical when unlearned.
                    //
                    // CLAMP THE BASIS - this is the one way the split can go silently wrong. Before it,
                    // `damage` was whatever UpdateVital actually removed, and UpdateVital clamps to
                    // [0, MaxValue] (Creature_Vitals.cs:53), so a basis larger than the caster's pool was
                    // impossible by construction. Paying only a fraction removes that guard: a rank 3 caster
                    // whose pool is short of tryDamage would otherwise bill damage against health that was
                    // never there. min(tryDamage, healthBefore) reproduces the old implicit clamp exactly.
                    damage = (uint)Math.Min(Math.Max(tryDamage, 0), (long)healthBefore);

                    var casterPlayer = caster as Player;

                    var selfCost = SanguineWardAbility.SelfCost(casterPlayer, damage);

                    var paid = (uint)-caster.UpdateVitalDelta(caster.Health, -(int)selfCost);

                    casterPlayer?.GrantSanguineWard(paid);

                    // self-attribution tracks the health the caster ACTUALLY lost, which is what this line
                    // recorded before the split; the damage basis is not what was taken out of the pool
                    caster.DamageHistory.Add(this, DamageType.Health, paid);
                    damageType = DamageType.Health;

                    //if (player != null && player.Fellowship != null)
                    //player.Fellowship.OnVitalUpdate(player);
                }
                else if(spell.DamageType != DamageType.Undef)
                {
                    // Handle rare case where some of these "Life Magic" spells do physical damage e.g. Hunter's Lash 2970 and Thorn Valley 6159
                    damageType = spell.DamageType;
                }
                else
                {
                    log.Warn($"Unknown DamageType ({spell.DamageType}) for LifeProjectile {spell.Name} - {spell.Id}");
                    return;
                }
            }

            // FORK: resolve the Blood Mage's Blood Charge pool for this cast BEFORE its projectiles exist.
            //
            // Curse of Raven Fury launches eight projectiles from one cast and Exsanguinate empties the pool
            // when it fires, so the read has to happen here, once, rather than at each projectile's own
            // collision - otherwise the first projectile to land takes the burst and the other seven find
            // nothing. The whole ring carries it (user, 2026-08-03: "whole ring on raven fury +
            // exsanguinate"); see Player.ApplyLifeProjectileBloodCharge for the ledger warning that
            // decision carries. Self-gating and self-resetting, so it is safe to call for every projectile
            // cast including war and void ones.
            (this as Player)?.ApplyLifeProjectileBloodCharge(spell, target);

            CreateSpellProjectiles(spell, target, weapon, isWeaponSpell, fromProc, damage);

            if (spell.School == MagicSchool.LifeMagic)
            {
                if (caster.Health.Current <= 0)
                {
                    // should this be possible?
                    var lastDamager = caster != null ? new DamageHistoryInfo(caster) : null;

                    caster.OnDeath(lastDamager, damageType, false);
                    caster.Die();
                }
            }
        }

        /// <summary>
        /// Handles casting SpellType.PortalLink spells
        /// </summary>
        private void HandleCastSpell_PortalLink(Spell spell, WorldObject target)
        {
            var player = this as Player;

            if (player == null) return;

            if (player.IsOlthoiPlayer)
            {
                player.Session.Network.EnqueueSend(new GameEventWeenieError(player.Session, WeenieError.OlthoiCanOnlyRecallToLifestone));
                return;
            }

            switch ((SpellId)spell.Id)
            {
                case SpellId.LifestoneTie1:  // Lifestone Tie

                    if (target.WeenieType == WeenieType.LifeStone)
                    {
                        player.SendChatMessage(this, "You have successfully linked with the life stone.", ChatMessageType.Magic);
                        player.LinkedLifestone = target.Location;
                    }
                    else
                        player.SendChatMessage(this, "You cannot link that.", ChatMessageType.Magic);

                    break;

                case SpellId.PortalTie1:    // Primary Portal Tie
                case SpellId.PortalTie2:    // Secondary Portal Tie

                    if (target.WeenieType != WeenieType.Portal)
                    {
                        player.SendChatMessage(this, "You cannot link that.", ChatMessageType.Magic);
                        break;
                    }

                    var targetPortal = target as Portal;

                    var summoned = targetPortal.OriginalPortal != null;

                    var targetDID = summoned ? targetPortal.OriginalPortal : targetPortal.WeenieClassId;

                    var tiePortal = GetPortal(targetDID.Value);

                    if (tiePortal == null || tiePortal.Destination == null)
                    {
                        player.Session.Network.EnqueueSend(new GameEventWeenieError(player.Session, WeenieError.YouCannotLinkToThatPortal));
                        break;
                    }

                    var result = tiePortal.CheckUseRequirements(player);

                    if (!result.Success && result.Message != null)
                        player.Session.Network.EnqueueSend(result.Message);

                    if (tiePortal.NoTie || !result.Success)
                    {
                        player.Session.Network.EnqueueSend(new GameEventWeenieError(player.Session, WeenieError.YouCannotLinkToThatPortal));
                        break;
                    }

                    var isPrimary = spell.Id == (int)SpellId.PortalTie1;

                    if (isPrimary)
                    {
                        player.LinkedPortalOneDID = targetDID;
                        player.SetProperty(PropertyBool.LinkedPortalOneSummon, summoned);
                    }
                    else
                    {
                        player.LinkedPortalTwoDID = targetDID;
                        player.SetProperty(PropertyBool.LinkedPortalTwoSummon, summoned);
                    }

                    player.SendChatMessage(this, "You have successfully linked with the portal.", ChatMessageType.Magic);
                    break;
            }
        }

        /// <summary>
        /// Returns a Portal object for a WCID
        /// </summary>
        private static Portal GetPortal(uint wcid)
        {
            var weenie = DatabaseManager.World.GetCachedWeenie(wcid);

            return WorldObjectFactory.CreateWorldObject(weenie, new ObjectGuid(wcid)) as Portal;
        }

        /// <summary>
        /// Handles casting SpellType.PortalRecall spells
        /// </summary>
        private void HandleCastSpell_PortalRecall(Spell spell, Creature targetCreature)
        {
            var player = this as Player;

            if (player != null && player.IsOlthoiPlayer)
            {
                player.Session.Network.EnqueueSend(new GameEventWeenieError(player.Session, WeenieError.OlthoiCanOnlyRecallToLifestone));
                return;
            }

            var creature = this as Creature;

            var targetPlayer = targetCreature as Player;

            if (player != null && player.PKTimerActive)
            {
                player.Session.Network.EnqueueSend(new GameEventWeenieError(player.Session, WeenieError.YouHaveBeenInPKBattleTooRecently));
                return;
            }

            PositionType recall = PositionType.Undef;
            uint? recallDID = null;

            // verify pre-requirements for recalls

            switch ((SpellId)spell.Id)
            {
                case SpellId.PortalRecall:       // portal recall

                    if (targetPlayer.LastPortalDID == null)
                    {
                        // You must link to a portal to recall it!
                        targetPlayer.Session.Network.EnqueueSend(new GameEventWeenieError(targetPlayer.Session, WeenieError.YouMustLinkToPortalToRecall));
                    }
                    else
                    {
                        recall = PositionType.LastPortal;
                        recallDID = targetPlayer.LastPortalDID;
                    }
                    break;

                case SpellId.LifestoneRecall1:   // lifestone recall

                    if (targetPlayer.GetPosition(PositionType.LinkedLifestone) == null)
                    {
                        // You must link to a lifestone to recall it!
                        targetPlayer.Session.Network.EnqueueSend(new GameEventWeenieError(targetPlayer.Session, WeenieError.YouMustLinkToLifestoneToRecall));
                    }
                    else
                        recall = PositionType.LinkedLifestone;
                    break;

                case SpellId.LifestoneSending1:

                    if (player != null && player.GetPosition(PositionType.Sanctuary) != null)
                        recall = PositionType.Sanctuary;
                    else if (targetPlayer != null && targetPlayer.GetPosition(PositionType.Sanctuary) != null)
                        recall = PositionType.Sanctuary;

                    break;

                case SpellId.PortalTieRecall1:   // primary portal tie recall

                    if (targetPlayer.LinkedPortalOneDID == null)
                    {
                        // You must link to a portal to recall it!
                        targetPlayer.Session.Network.EnqueueSend(new GameEventWeenieError(targetPlayer.Session, WeenieError.YouMustLinkToPortalToRecall));
                    }
                    else
                    {
                        recall = PositionType.LinkedPortalOne;
                        recallDID = targetPlayer.LinkedPortalOneDID;
                    }
                    break;

                case SpellId.PortalTieRecall2:   // secondary portal tie recall

                    if (targetPlayer.LinkedPortalTwoDID == null)
                    {
                        // You must link to a portal to recall it!
                        targetPlayer.Session.Network.EnqueueSend(new GameEventWeenieError(targetPlayer.Session, WeenieError.YouMustLinkToPortalToRecall));
                    }
                    else
                    {
                        recall = PositionType.LinkedPortalTwo;
                        recallDID = targetPlayer.LinkedPortalTwoDID;
                    }
                    break;
            }

            if (recall != PositionType.Undef)
            {
                if (recallDID == null)
                {
                    // lifestone recall
                    ActionChain lifestoneRecall = new ActionChain();
                    lifestoneRecall.AddAction(targetPlayer, () => targetPlayer.DoPreTeleportHide());
                    lifestoneRecall.AddDelaySeconds(2.0f);  // 2 second delay
                    lifestoneRecall.AddAction(targetPlayer, () => targetPlayer.TeleToPosition(recall));
                    lifestoneRecall.EnqueueChain();
                }
                else
                {
                    // portal recall
                    var portal = GetPortal(recallDID.Value);
                    // A weenie with no Destination position row leaves Portal.Destination null;
                    // new Position(null) below would throw. Refuse the recall instead of crashing.
                    if (portal == null || portal.NoRecall || portal.Destination == null)
                    {
                        // You cannot recall that portal!
                        player.Session.Network.EnqueueSend(new GameEventWeenieError(player.Session, WeenieError.YouCannotRecallPortal));
                        return;
                    }

                    var result = portal.CheckUseRequirements(targetPlayer);
                    if (!result.Success)
                    {
                        if (result.Message != null)
                            targetPlayer.Session.Network.EnqueueSend(result.Message);

                        return;
                    }

                    ActionChain portalRecall = new ActionChain();
                    portalRecall.AddAction(targetPlayer, () => targetPlayer.DoPreTeleportHide());
                    portalRecall.AddDelaySeconds(2.0f);  // 2 second delay
                    portalRecall.AddAction(targetPlayer, () =>
                    {
                        var teleportDest = new Position(portal.Destination);
                        AdjustDungeon(teleportDest);

                        // The portal above was rebuilt from its weenie, and a weenie-derived position is
                        // always instance 0 (Weenie.GetPosition hardcodes it), so recalling would land the
                        // player in the base realm even for a realm-attuned portal. Route the destination
                        // through the same resolution that walking through the portal uses - home-realm
                        // default instance, overridden by an explicit PortalRealm. Must run AFTER
                        // AdjustDungeon, which mutates the raw destination.
                        teleportDest = Portal.ResolvePortalDestination(portal, targetPlayer, teleportDest);

                        targetPlayer.Teleport(teleportDest);
                    });
                    portalRecall.EnqueueChain();
                }
            }
        }

        /// <summary>
        /// Handles casting SpellType.PortalSummon spells
        /// </summary>
        private void HandleCastSpell_PortalSummon(Spell spell, Creature targetCreature, WorldObject itemCaster)
        {
            var player = this as Player;

            if (player != null && player.IsOlthoiPlayer)
            {
                player.Session.Network.EnqueueSend(new GameEventWeenieError(player.Session, WeenieError.OlthoiCanOnlyRecallToLifestone));
                return;
            }

            if (player != null && player.PKTimerActive)
            {
                player.Session.Network.EnqueueSend(new GameEventWeenieError(player.Session, WeenieError.YouHaveBeenInPKBattleTooRecently));
                return;
            }

            var source = player ?? itemCaster;

            uint portalId = 0;
            bool linkSummoned;

            // spell.link = 1 = LinkedPortalOneDID
            // spell.link = 2 = LinkedPortalTwoDID

            if (spell.Link <= 1)
            {
                portalId = source.LinkedPortalOneDID ?? 0;
                linkSummoned = source.GetProperty(PropertyBool.LinkedPortalOneSummon) ?? false;
            }
            else
            {
                portalId = source.LinkedPortalTwoDID ?? 0;
                linkSummoned = source.GetProperty(PropertyBool.LinkedPortalTwoSummon) ?? false;
            }

            Position summonLoc = null;

            if (player != null)
            {
                if (portalId == 0)
                {
                    // You must link to a portal to summon it!
                    player.Session.Network.EnqueueSend(new GameEventWeenieError(player.Session, WeenieError.YouMustLinkToPortalToSummonIt));
                    return;
                }

                var summonPortal = GetPortal(portalId);
                if (summonPortal == null || summonPortal.NoSummon || (linkSummoned && !PropertyManager.GetBool("gateway_ties_summonable").Item))
                {
                    // You cannot summon that portal!
                    player.Session.Network.EnqueueSend(new GameEventWeenieError(player.Session, WeenieError.YouCannotSummonPortal));
                    return;
                }

                var result = summonPortal.CheckUseRequirements(player);
                if (!result.Success)
                {
                    if (result.Message != null)
                        player.Session.Network.EnqueueSend(result.Message);

                    return;
                }

                summonLoc = player.Location.InFrontOf(3.0f);
            }
            else if (itemCaster != null)
            {
                if (itemCaster.PortalSummonLoc != null)
                    summonLoc = new Position(PortalSummonLoc);
                else
                {
                    if (itemCaster.Location != null)
                        summonLoc = itemCaster.Location.InFrontOf(3.0f);
                    else if (targetCreature != null && targetCreature.Location != null)
                        summonLoc = targetCreature.Location.InFrontOf(3.0f);
                }
            }

            if (summonLoc != null)
                summonLoc.LandblockId = new LandblockId(summonLoc.GetCell());

            var success = SummonPortal(portalId, summonLoc, spell.PortalLifetime);

            if (!success && player != null)
                player.Session.Network.EnqueueSend(new GameEventWeenieError(player.Session, WeenieError.YouFailToSummonPortal));
        }

        /// <summary>
        /// Spawns a portal for SpellType.PortalSummon spells
        /// </summary>
        protected static bool SummonPortal(uint portalId, Position location, double portalLifetime)
        {
            var portal = GetPortal(portalId);

            // A weenie with no Destination position row leaves Portal.Destination null;
            // new Position(null) below would throw. Refuse the summon instead of crashing.
            if (portal == null || location == null || portal.Destination == null)
                return false;

            var gateway = WorldObjectFactory.CreateNewWorldObject("portalgateway") as Portal;

            if (gateway == null)
                return false;

            gateway.Location = new Position(location);
            gateway.OriginalPortal = portalId;

            gateway.UpdatePortalDestination(new Position(portal.Destination));

            gateway.TimeToRot = portalLifetime;

            gateway.MinLevel = portal.MinLevel;
            gateway.MaxLevel = portal.MaxLevel;
            gateway.PortalRestrictions = portal.PortalRestrictions;
            gateway.AccountRequirements = portal.AccountRequirements;
            gateway.AdvocateQuest = portal.AdvocateQuest;

            gateway.Quest = portal.Quest;
            gateway.QuestRestriction = portal.QuestRestriction;

            gateway.Biota.PropertiesEmote = portal.Biota.PropertiesEmote;

            // ApplyPortalRealm reads PortalRealm off the object the player actually walks through - the
            // gateway - not the original portal, so without copying it a summoned realm-attuned portal
            // would drop the player into the base realm (instance 0) instead of the realm copy.
            var portalRealm = portal.GetProperty(PropertyInt.PortalRealm);
            if (portalRealm != null)
                gateway.SetProperty(PropertyInt.PortalRealm, portalRealm.Value);

            gateway.PortalRestrictions |= PortalBitmask.NoSummon; // all gateways are marked NoSummon but by default ruleset, the OriginalPortal is the one that is checked against

            gateway.EnterWorld();

            return true;
        }

        /// <summary>
        /// Handles casting SpellType.PortalSending spells
        /// </summary>
        private void HandleCastSpell_PortalSending(Spell spell, Creature targetCreature, WorldObject itemCaster)
        {
            if (targetCreature is Player targetPlayer)
            {
                if (targetPlayer.PKTimerActive)
                {
                    targetPlayer.Session.Network.EnqueueSend(new GameEventWeenieError(targetPlayer.Session, WeenieError.YouHaveBeenInPKBattleTooRecently));
                    return;
                }

                ActionChain portalSendingChain = new ActionChain();
                //portalSendingChain.AddDelaySeconds(2.0f);  // 2 second delay
                portalSendingChain.AddAction(targetPlayer, () => targetPlayer.DoPreTeleportHide());
                portalSendingChain.AddAction(targetPlayer, () =>
                {
                    var teleportDest = new Position(spell.Position);
                    AdjustDungeon(teleportDest);

                    targetPlayer.Teleport(teleportDest);

                    targetPlayer.SendTeleportedViaMagicMessage(itemCaster, spell);
                });
                portalSendingChain.EnqueueChain();
            }
            else if (targetCreature != null)
            {
                // monsters can cast some portal spells on themselves too, possibly?
                // under certain circumstances, such as ensuring the destination is the same landblock
                var teleportDest = new Position(spell.Position);
                AdjustDungeon(teleportDest);

                targetCreature.FakeTeleport(teleportDest);
            }
        }

        /// <summary>
        /// Handles casting SpellType.FellowPortalSending spells
        /// </summary>
        private bool HandleCastSpell_FellowPortalSending(Spell spell, Creature targetCreature, WorldObject itemCaster)
        {
            var creature = this as Creature;

            var targetPlayer = targetCreature as Player;

            if (targetPlayer == null || targetPlayer.Fellowship == null)
                return false;

            if (targetPlayer.PKTimerActive)
            {
                targetPlayer.Session.Network.EnqueueSend(new GameEventWeenieError(targetPlayer.Session, WeenieError.YouHaveBeenInPKBattleTooRecently));
                return false;
            }

            var distanceToTarget = creature.GetDistance(targetPlayer);
            var skill = creature.GetCreatureSkill(spell.School);
            var magicSkill = skill.InitLevel + skill.Ranks;     // synced with acclient DetermineSpellRange -> InqSkillLevel

            var maxRange = spell.BaseRangeConstant + magicSkill * spell.BaseRangeMod;
            if (maxRange == 0.0f)
                maxRange = float.PositiveInfinity;

            if (distanceToTarget > maxRange)
                return false;

            var portalSendingChain = new ActionChain();
            portalSendingChain.AddAction(targetPlayer, () => targetPlayer.DoPreTeleportHide());
            portalSendingChain.AddAction(targetPlayer, () =>
            {
                var teleportDest = new Position(spell.Position);
                AdjustDungeon(teleportDest);

                targetPlayer.Teleport(teleportDest);

                targetPlayer.SendTeleportedViaMagicMessage(itemCaster, spell);
            });
            portalSendingChain.EnqueueChain();

            return true;
        }

        /// <summary>
        /// Handles casting SpellType.Dispel / FellowDispel spells
        /// </summary>
        private void HandleCastSpell_Dispel(Spell spell, WorldObject target)
        {
            var player = this as Player;
            var creature = this as Creature;

            var removeSpells = target.EnchantmentManager.SelectDispel(spell);

            // dispel on server and client
            target.EnchantmentManager.Dispel(removeSpells.Select(s => s.Enchantment).ToList());

            var spellList = BuildSpellList(removeSpells);
            var suffix = "";
            if (removeSpells.Count > 0)
                suffix = $" and dispel: {spellList}.";
            else
                suffix = ", but the dispel fails.";

            if (player != null)
            {
                string casterMsg;

                if (player == target)
                    casterMsg = $"You cast {spell.Name} on yourself{suffix}";
                else
                    casterMsg = $"You cast {spell.Name} on {target.Name}{suffix}";

                player.SendChatMessage(player, casterMsg, ChatMessageType.Magic);
            }

            if (target is Player targetPlayer && targetPlayer != player)
            {
                var targetMsg = $"{Name} casts {spell.Name} on you{suffix.Replace("and dispel", "and dispels")}";

                targetPlayer.SendChatMessage(this, targetMsg, ChatMessageType.Magic);

                // all dispels appear to be listed as non-beneficial, even the ones that only dispel negative spells
                // we filter here to positive or all
                if (creature != null && spell.Align != DispelType.Negative)
                    targetPlayer.SetCurrentAttacker(creature);
            }
        }

        public static bool VerifyDispelPKStatus(WorldObject caster, WorldObject target)
        {
            // https://asheron.fandom.com/wiki/Announcements_-_2004/04_-_A_New_Threat
            // https://asheron.fandom.com/wiki/Dispel_Spells

            // Dispel spells and potions have been revised. All dispels are also now tied to the PK/L timer.

            // The feedback on the suggested dispel timer for PK/L was very mixed. There was no clear majority either for or against.
            // With that in mind, we've gone ahead with the changes that we feel best improve majority of PK/L combat:
            // we've decided to implement the PK/L timer on dispels.

            // If you have been in a PK/L action within the last 20 seconds, you will not be able to:

            // - Use a dispel gem.
            // - Use a dispel potion.
            // - Use the Awakener or Attenuated Awakener on someone else.
            // - Cast any dispel spell on yourself.
            // - Cast any dispel spell on someone else.

            var casterPlayer = caster as Player;

            if (casterPlayer != null && casterPlayer.PKTimerActive)
            {
                casterPlayer.SendWeenieError(WeenieError.YouHaveBeenInPKBattleTooRecently);
                return false;
            }

            if ((target.Wielder ?? target) is Player targetPlayer && targetPlayer.PKTimerActive)
            {
                if (/* casterPlayer != null || */ caster is Gem || caster is Food)
                {
                    if (casterPlayer != null)
                        casterPlayer.Session.Network.EnqueueSend(new GameMessageSystemChat($"{targetPlayer.Name} has been involved in a player killer battle too recently to do that!", ChatMessageType.Magic));
                    else
                        targetPlayer.SendWeenieError(WeenieError.YouHaveBeenInPKBattleTooRecently);

                    return false;
                }
            }

            return true;
        }

        /// <summary>
        /// Returns a string with the spell list format as:
        /// Spell Name 1, Spell Name 2, and Spell Name 3
        /// </summary>
        private static string BuildSpellList(List<SpellEnchantment> spells)
        {
            var sb = new StringBuilder();
            for (var i = 0; i < spells.Count; i++)
            {
                var spell = spells[i];

                if (i > 0)
                {
                    sb.Append(", ");
                    if (i == spells.Count - 1)
                        sb.Append("and ");
                }

                sb.Append(spell.Spell.Name);
            }
            return sb.ToString();
        }

        /// <summary>
        /// Creates and launches the projectiles for a spell
        /// </summary>
        public List<SpellProjectile> CreateSpellProjectiles(Spell spell, WorldObject target, WorldObject weapon, bool isWeaponSpell = false, bool fromProc = false, uint lifeProjectileDamage = 0)
        {
            if (spell.NumProjectiles == 0)
            {
                log.Error($"{Name} ({Guid}).CreateSpellProjectiles({spell.Id} - {spell.Name}) - spell.NumProjectiles == 0");
                return new List<SpellProjectile>();
            }

            var spellType = SpellProjectile.GetProjectileSpellType(spell.Id);

            var origins = CalculateProjectileOrigins(spell, spellType, target);

            var velocity = CalculateProjectileVelocity(spell, target, spellType, origins[0]);

            return LaunchSpellProjectiles(spell, target, spellType, weapon, isWeaponSpell, fromProc, origins, velocity, lifeProjectileDamage);
        }

        public const float ProjHeight = 2.0f / 3.0f;

        public Vector3 CalculatePreOffset(Spell spell, ProjectileSpellType spellType, WorldObject target)
        {
            var startFactor = spellType == ProjectileSpellType.Arc ? 1.0f : ProjHeight;

            var preOffset = new Vector3(0, 0, Height * startFactor);

            if (target == null)
                return preOffset;

            var startPos = new Physics.Common.Position(PhysicsObj.Position);
            startPos.Frame.Origin.Z += Height * startFactor;

            var endFactor = spellType == ProjectileSpellType.Arc ? ProjHeightArc : ProjHeight;

            var endPos = new Physics.Common.Position(target.PhysicsObj.Position);
            endPos.Frame.Origin.Z += target.Height * endFactor;

            var globOffset = startPos.GetOffset(endPos);

            // align in x
            var rotate = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, (float)Math.Atan2(globOffset.X, globOffset.Y));

            var offset = Vector3.Transform(globOffset, rotate);

            var localDir = Vector3.Normalize(offset);

            var radsum = PhysicsObj.GetPhysicsRadius() + GetProjectileRadius(spell);

            var defaultSpawnPos = Vector3.UnitY * radsum;

            var spawnPos = localDir * radsum;

            var spawnOffset = spawnPos - defaultSpawnPos;

            return preOffset + spawnOffset;
        }

        /// <summary>
        /// Returns a list of positions to spawn projectiles for a spell,
        /// in local space relative to the caster
        /// </summary>
        public List<Vector3> CalculateProjectileOrigins(Spell spell, ProjectileSpellType spellType, WorldObject target)
        {
            var origins = new List<Vector3>();

            var radius = GetProjectileRadius(spell);
            //Console.WriteLine($"Radius: {radius}");

            var vRadius = Vector3.One * radius;

            var baseOffset = spell.CreateOffset;

            var radsum = PhysicsObj.GetPhysicsRadius() * 2.0f + radius * 2.0f;

            var heightOffset = CalculatePreOffset(spell, spellType, target);

            if (target != null)
            {
                var cylDist = GetCylinderDistance(target);
                //Console.WriteLine($"CylDist: {cylDist}");
                if (cylDist < 0.6f)
                    radsum = PhysicsObj.GetPhysicsRadius() + radius;
            }

            if (spell.SpreadAngle == 360)
                radsum *= 0.6f;

            baseOffset.Y += radsum;

            baseOffset += heightOffset;

            var anglePerStep = GetSpreadAnglePerStep(spell);

            // TODO: normalize data
            var dims = new Vector3(spell._spell.DimsOriginX ?? spell.NumProjectiles, spell._spell.DimsOriginY ?? 1, spell._spell.DimsOriginZ ?? 1);

            var i = 0;
            for (var z = 0; z < dims.Z; z++)
            {
                for (var y = 0; y < dims.Y; y++)
                {
                    var oddRow = (int)Math.Min(dims.X, spell.NumProjectiles - i) % 2 == 1;

                    for (var x = 0; x < dims.X; x++)
                    {
                        if (i >= spell.NumProjectiles)
                            break;

                        var curOffset = baseOffset;

                        if (spell.Peturbation != Vector3.Zero)
                        {
                            var rng = new Vector3((float)ThreadSafeRandom.Next(-1.0f, 1.0f), (float)ThreadSafeRandom.Next(-1.0f, 1.0f), (float)ThreadSafeRandom.Next(-1.0f, 1.0f));

                            curOffset += rng * spell.Peturbation * spell.Padding;
                        }

                        if (!oddRow && spell.SpreadAngle == 0)
                            curOffset.X += spell.Padding.X * 0.5f + radius;

                        var xFactor = spell.SpreadAngle == 0 ? oddRow ? (float)Math.Ceiling(x * 0.5f) : (float)Math.Floor(x * 0.5f) : 0;

                        var origin = curOffset + (vRadius * 2.0f + spell.Padding) * new Vector3(xFactor, y, z);

                        if (spell.SpreadAngle == 0)
                        {
                            if (x % 2 == (oddRow ? 1 : 0))
                                origin.X *= -1.0f;
                        }
                        else
                        {
                            // get the rotation matrix to apply to x
                            var numSteps = (x + 1) / 2;
                            if (x % 2 == 0)
                                numSteps *= -1;

                            //Console.WriteLine($"NumSteps: {numSteps}");

                            var curAngle = anglePerStep * numSteps;
                            var rads = curAngle.ToRadians();

                            var rot = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, rads);
                            origin = Vector3.Transform(origin, rot);
                        }

                        origins.Add(origin);
                        i++;
                    }

                    if (i >= spell.NumProjectiles)
                        break;
                }

                if (i >= spell.NumProjectiles)
                    break;
            }

            /*foreach (var origin in origins)
                Console.WriteLine(origin);*/

            return origins;
        }

        /// <summary>
        /// Returns the angle in degrees between projectiles
        /// for spells with SpreadAngle
        /// </summary>
        public static float GetSpreadAnglePerStep(Spell spell)
        {
            if (spell.SpreadAngle == 0.0f || spell.NumProjectiles == 1)
                return 0.0f;

            var numProjectiles = spell.NumProjectiles;

            if (numProjectiles % 2 == 1)
                numProjectiles--;

            return spell.SpreadAngle / numProjectiles;
        }

        public static readonly Quaternion OneEighty = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, (float)Math.PI);

        public const float ProjHeightArc = 5.0f / 6.0f;

        /// <summary>
        /// Calculates the spell projectile velocity in global space
        /// </summary>
        public Vector3 CalculateProjectileVelocity(Spell spell, WorldObject target, ProjectileSpellType spellType, Vector3 origin)
        {
            var casterLoc = PhysicsObj.Position.ACEPosition(PhysicsObj.CurInstance);

            var speed = GetProjectileSpeed(spell);

            if (target == null && this is Creature creature && !(this is Player))
                target = creature.AttackTarget;

            if (target == null)
            {
                // launch along forward vector
                return Vector3.Transform(Vector3.UnitY, casterLoc.Rotation) * speed;
            }

            var targetLoc = target.PhysicsObj.Position.ACEPosition(target.PhysicsObj.CurInstance);

            var strikeSpell = spellType == ProjectileSpellType.Strike;

            var crossLandblock = !strikeSpell && casterLoc.InstancedLandblock != targetLoc.InstancedLandblock;

            var qDir = PhysicsObj.Position.GetOffset(target.PhysicsObj.Position);
            var rotate = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, (float)Math.Atan2(-qDir.X, qDir.Y));

            var startPos = strikeSpell ? targetLoc.Pos : crossLandblock ? casterLoc.ToGlobal(false) : casterLoc.Pos;
            startPos += Vector3.Transform(origin, strikeSpell ? rotate * OneEighty : rotate);

            var endPos = crossLandblock ? targetLoc.ToGlobal(false) : targetLoc.Pos;

            endPos.Z += target.Height * (spellType == ProjectileSpellType.Arc ? ProjHeightArc : ProjHeight);

            var dir = Vector3.Normalize(endPos - startPos);

            var targetVelocity = spell.IsTracking ? target.PhysicsObj.CachedVelocity : Vector3.Zero;

            var useGravity = spellType == ProjectileSpellType.Arc;

            var velocity = Vector3.Zero;

            if (useGravity || targetVelocity != Vector3.Zero)
            {
                var gravity = useGravity ? PhysicsGlobals.Gravity : 0.0f;

                if (!PropertyManager.GetBool("trajectory_alt_solver").Item)
                    Trajectory.solve_ballistic_arc_lateral(startPos, speed, endPos, targetVelocity, gravity, out velocity, out var time, out var impactPoint);
                else
                    velocity = Trajectory2.CalculateTrajectory(startPos, endPos, targetVelocity, speed, useGravity);

                if (velocity == Vector3.Zero && useGravity && targetVelocity != Vector3.Zero)
                {
                    // intractable?
                    // try to solve w/ zero velocity
                    if (!PropertyManager.GetBool("trajectory_alt_solver").Item)
                        Trajectory.solve_ballistic_arc_lateral(startPos, speed, endPos, Vector3.Zero, gravity, out velocity, out var time, out var impactPoint);
                    else
                        velocity = Trajectory2.CalculateTrajectory(startPos, endPos, Vector3.Zero, speed, useGravity);
                }
                if (velocity != Vector3.Zero)
                    return velocity;
            }

            return dir * speed;
        }

        public List<SpellProjectile> LaunchSpellProjectiles(Spell spell, WorldObject target, ProjectileSpellType spellType, WorldObject weapon, bool isWeaponSpell, bool fromProc, List<Vector3> origins, Vector3 velocity, uint lifeProjectileDamage = 0)
        {
            var useGravity = spellType == ProjectileSpellType.Arc;

            var strikeSpell = target != null && spellType == ProjectileSpellType.Strike;

            var spellProjectiles = new List<SpellProjectile>();

            var casterLoc = PhysicsObj.Position.ACEPosition(PhysicsObj.CurInstance);
            var targetLoc = target?.PhysicsObj.Position.ACEPosition(target.PhysicsObj.CurInstance);

            for (var i = 0; i < origins.Count; i++)
            {
                var origin = origins[i];

                var sp = WorldObjectFactory.CreateNewWorldObject(spell.Wcid) as SpellProjectile;

                if (sp == null)
                {
                    log.Error($"{Name} ({Guid}).LaunchSpellProjectiles({spell.Id} - {spell.Name}) - failed to create spell projectile from wcid {spell.Wcid}");
                    break;
                }

                sp.Setup(spell, spellType);

                var rotate = casterLoc.Rotation;
                if (target != null)
                {
                    var qDir = PhysicsObj.Position.GetOffset(target.PhysicsObj.Position);
                    rotate = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, (float)Math.Atan2(-qDir.X, qDir.Y));
                }

                sp.Location = strikeSpell ? new Position(targetLoc) : new Position(casterLoc);
                sp.Location.Pos += Vector3.Transform(origin, strikeSpell ? rotate * OneEighty : rotate);

                sp.PhysicsObj.Velocity = velocity;

                if (spell.SpreadAngle > 0)
                {
                    var n = Vector3.Normalize(origin);
                    var angle = Math.Atan2(-n.X, n.Y);
                    var q = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, (float)angle);
                    sp.PhysicsObj.Velocity = Vector3.Transform(velocity, q);
                }

                // set orientation
                var dir = Vector3.Normalize(sp.Velocity);
                sp.PhysicsObj.Position.Frame.set_vector_heading(dir);
                sp.Location.Rotation = sp.PhysicsObj.Position.Frame.Orientation;

                sp.ProjectileSource = this;
                sp.FromProc = fromProc;
                sp.IsClassAbilityProc = (this as Player)?.ClassAbilityProcCastActive ?? false;

                // side projectiles always untargeted?
                if (i == 0)
                    sp.ProjectileTarget = target;

                sp.ProjectileLauncher = weapon;
                sp.IsWeaponSpell = isWeaponSpell;

                sp.SetProjectilePhysicsState(sp.ProjectileTarget, useGravity);
                sp.SpawnPos = new Position(sp.Location);

                sp.LifeProjectileDamage = lifeProjectileDamage;

                if (!LandblockManager.AddObject(sp))
                {
                    sp.Destroy();
                    continue;
                }

                if (sp.WorldEntryCollision)
                    continue;

                sp.EnqueueBroadcast(new GameMessageScript(sp.Guid, PlayScript.Launch, sp.GetProjectileScriptIntensity(spellType)));

                if (!IsProjectileVisible(sp))
                {
                    sp.OnCollideEnvironment();
                    continue;
                }

                spellProjectiles.Add(sp);
            }

            return spellProjectiles;
        }

        public static void ClearSpellCache()
        {
            ProjectileRadiusCache.Clear();
            ProjectileSpeedCache.Clear();
        }

        public static readonly ConcurrentDictionary<uint, float> ProjectileRadiusCache = new ConcurrentDictionary<uint, float>();

        private float GetProjectileRadius(Spell spell)
        {
            var projectileWcid = spell.WeenieClassId;

            if (ProjectileRadiusCache.TryGetValue(projectileWcid, out var radius))
                return radius;

            var weenie = DatabaseManager.World.GetCachedWeenie(projectileWcid);

            if (weenie == null)
            {
                log.Error($"{Name} ({Guid}).GetSetupRadius({spell.Id} - {spell.Name}): couldn't find weenie {projectileWcid}");
                return 0.0f;
            }

            if (!weenie.PropertiesDID.TryGetValue(PropertyDataId.Setup, out var setupId))
            {
                log.Error($"{Name} ({Guid}).GetSetupRadius({spell.Id} - {spell.Name}): couldn't find setup ID for {weenie.WeenieClassId} - {weenie.ClassName}");
                return 0.0f;
            }

            var setup = DatManager.PortalDat.ReadFromDat<SetupModel>(setupId);

            if (!weenie.PropertiesFloat.TryGetValue(PropertyFloat.DefaultScale, out var scale))
                scale = 1.0f;

            var result = (float)(setup.Spheres[0].Radius * scale);

            ProjectileRadiusCache.TryAdd(projectileWcid, result);

            return result;
        }

        /// <summary>
        /// This is a temporary structure
        /// GetSpellProjectileSpeed() can easily be moved to SpellProjectile.CalculateSpeed()
        /// however the current calling pattern for Rings and Walls needs some work still..
        /// </summary>
        private static readonly ConcurrentDictionary<uint, float> ProjectileSpeedCache = new ConcurrentDictionary<uint, float>();

        /// <summary>
        /// Gets the speed of a projectile based on the distance to the target.
        /// </summary>
        private float GetProjectileSpeed(Spell spell, float? distance = null)
        {
            var projectileWcid = spell.WeenieClassId;

            if (!ProjectileSpeedCache.TryGetValue(projectileWcid, out var baseSpeed))
            {
                var weenie = DatabaseManager.World.GetCachedWeenie(projectileWcid);

                if (weenie == null)
                {
                    log.Error($"{Name} ({Guid}).GetSpellProjectileSpeed({spell.Id} - {spell.Name}, {distance}): couldn't find weenie {projectileWcid}");
                    return 0.0f;
                }

                if (!weenie.PropertiesFloat.TryGetValue(PropertyFloat.MaximumVelocity, out var maxVelocity))
                {
                    log.Error($"{Name} ({Guid}).GetSpellProjectileSpeed({spell.Id} - {spell.Name}, {distance}): couldn't find MaxVelocity for {weenie.WeenieClassId} - {weenie.ClassName}");
                    return 0.0f;
                }

                baseSpeed = (float)maxVelocity;

                ProjectileSpeedCache.TryAdd(projectileWcid, baseSpeed);
            }

            // TODO:
            // Speed seems to increase when target is moving away from the caster and decrease when
            // the target is moving toward the caster. This still needs more research.
            if (distance == null)
                return baseSpeed;

            var speed = (float)((baseSpeed * .9998363f) - (baseSpeed * .62034f) / distance +
                                   (baseSpeed * .44868f) / Math.Pow(distance.Value, 2f) - (baseSpeed * .25256f)
                                   / Math.Pow(distance.Value, 3f));

            speed = Math.Clamp(speed, 1, 50);

            return speed;
        }

        /// <summary>
        /// Returns the epic cantrips from this item's spellbook
        /// </summary>
        public Dictionary<int, float /* probability */> EpicCantrips => Biota.GetMatchingSpells(LootTables.EpicCantrips, BiotaDatabaseLock);

        /// <summary>
        /// Returns the legendary cantrips from this item's spellbook
        /// </summary>
        public Dictionary<int, float /* probability */> LegendaryCantrips => Biota.GetMatchingSpells(LootTables.LegendaryCantrips, BiotaDatabaseLock);

        private int? _maxSpellLevel;

        public int GetMaxSpellLevel()
        {
            if (_maxSpellLevel == null)
            {
                _maxSpellLevel = Biota.PropertiesSpellBook != null && Biota.PropertiesSpellBook.Count > 0 ?
                    Biota.PropertiesSpellBook.Keys.Max(i => SpellLevelCache.GetSpellLevel(i)) : 0;
            }
            return _maxSpellLevel.Value;
        }

        /// <summary>
        /// Calculates the StatModVal x buffs to enter into the enchantment registry
        /// </summary>
        /// <param name="spell">A spell with a DotDuration</param>
        public float CalculateDotEnchantment_StatModValue(Spell spell, WorldObject target, WorldObject weapon, float statModVal)
        {
            // here are all the dots with current content:

            // - 3 void dots (2 projectiles, 1 direct enchantment)
            // - surge of affliction (target loses health over time)
            // - surge of regeneration (caster gains health over time)
            // - dirty fighting bleed

            if (spell.DotDuration == 0)
                return statModVal;

            var enchantment_statModVal = statModVal;

            var creatureTarget = target as Creature;

            if (spell.Category == SpellCategory.AetheriaProcHealthOverTimeRaising)
            {
                // no healing boost rating modifier found in retail pcaps on apply,
                // could there have been one on tick?
                //if (creatureTarget != null)
                //enchantment_statModVal *= creatureTarget.GetHealingRatingMod();

                return enchantment_statModVal;
            }

            if (spell.Category == SpellCategory.AetheriaProcDamageOverTimeRaising)
            {
                // no mods found in retail pcaps
                return enchantment_statModVal;
            }

            var player = this as Player;
            var creatureSource = this as Creature;

            var damageRatingMod = 1.0f;

            if (creatureSource != null)
            {
                // damage rating mod
                var damageRating = creatureSource.GetDamageRating();

                if (player != null)
                {
                    // TODO: merge this with damage rating
                    var equippedWeapon = player.GetEquippedWeapon() ?? player.GetEquippedWand();
                    if (player.GetHeritageBonus(equippedWeapon))
                        damageRating += 5;

                    if (target is Player)
                        damageRating += player.GetPKDamageRating();
                }
                damageRatingMod = Creature.GetPositiveRatingMod(damageRating);
            }

            if (spell.Category == SpellCategory.DFBleedDamage)
            {
                // retail pcaps have modifiers in the range of 1.1x - 1.7x
                return enchantment_statModVal * damageRatingMod;
            }

            if (spell.Category != SpellCategory.NetherDamageOverTimeRaising && spell.Category != SpellCategory.NetherDamageOverTimeRaising2 && spell.Category != SpellCategory.NetherDamageOverTimeRaising3)
            {
                log.Error($"{Name}.CalculateDamageOverTimeBase({spell.Id} - {spell.Name}, {target?.Name}) - unknown dot spell category {spell.Category}");
                return enchantment_statModVal;
            }

            // factors:
            // - damage rating
            // - heritage bonus (universal masteries at end of retail, TODO: merge this with damage rating)
            // - caster damage type bonus (pvm, half for pvp)
            // - skill in magic school vs. spell difficulty (for projectiles)

            // thanks to Xenocide for figuring this part out!

            var elementalDamageMod = 1.0f;
            var skillMod = 1.0f;

            if (creatureSource != null)
            {
                // elemental damage mod
                elementalDamageMod = GetCasterElementalDamageModifier(weapon, creatureSource, creatureTarget, spell.DamageType);

                // skillMod only applied to projectiles -- no destructive curse
                if (player != null && spell.NumProjectiles > 0)
                {
                    // from SpellProjectile, slightly modified
                    // convert this to common function
                    var magicSkill = player.GetCreatureSkill(spell.School).Current;

                    if (magicSkill > spell.Power)
                    {
                        var percentageBonus = (magicSkill - spell.Power) / 1000.0f;

                        skillMod = 1.0f + percentageBonus;
                    }
                }
            }
            enchantment_statModVal *= skillMod * elementalDamageMod * damageRatingMod;

            return enchantment_statModVal;
        }

        public void TryCastItemEnchantment_WithRedirects(Spell spell, WorldObject target, WorldObject itemCaster = null)
        {
            var caster = itemCaster ?? this;

            var creature = this as Creature;
            var player = this as Player;

            var targetCreature = target as Creature;
            var targetPlayer = target as Player;

            // if negative item spell, can be resisted by the wielder
            if (spell.IsHarmful)
            {
                var targetResist = targetCreature;

                if (targetResist == null && target?.WielderId != null)
                    targetResist = CurrentLandblock?.GetObject(target.WielderId.Value) as Creature;

                // skip TryResistSpell() for non-player casters, they already performed it previously
                if (player != null && targetResist != null)
                {
                    if (TryResistSpell(targetResist, spell, caster))
                        return;
                }
                // should this be set if the spell is invalid / 'fails to affect' below?
                if (creature != null && targetResist is Player playerTargetResist)
                    playerTargetResist.SetCurrentAttacker(creature);
            }

            if (spell.IsImpenBaneType)
            {
                // impen / bane / brittlemail / lure

                // a lot of these will already be filtered out by IsInvalidTarget()
                if (targetCreature == null)
                {
                    // targeting an individual item / wo
                    HandleCastSpell(spell, target);
                }
                else
                {
                    // targeting a creature
                    if (targetPlayer == this)
                    {
                        // targeting self
                        if (creature != null)
                        {
                            var items = creature.EquippedObjects.Values.Where(i => (i.WeenieType == WeenieType.Clothing || i.IsShield) && i.IsEnchantable).ToList();

                            foreach (var item in items)
                                HandleCastSpell(spell, item);

                            if (items.Count > 0)
                                DoSpellEffects(spell, this, creature);
                        }
                    }
                    else
                    {
                        // targeting another player or monster
                        var item = targetCreature.EquippedObjects.Values.FirstOrDefault(i => i.IsShield && i.IsEnchantable);

                        if (item != null)
                        {
                            HandleCastSpell(spell, item);
                        }
                        else
                        {
                            // 'fails to affect'?
                            if (player != null && targetCreature != null)
                                player.Session.Network.EnqueueSend(new GameMessageSystemChat($"You fail to affect {targetCreature.Name} with {spell.Name}", ChatMessageType.Magic));

                            if (targetPlayer != null && !targetPlayer.SquelchManager.Squelches.Contains(this, ChatMessageType.Magic))
                                targetPlayer.Session.Network.EnqueueSend(new GameMessageSystemChat($"{Name} fails to affect you with {spell.Name}", ChatMessageType.Magic));
                        }
                    }
                }
            }
            else if (spell.IsOtherNegativeRedirectable || spell.IsItemRedirectableType)
            {
                // blood loather, spirit loather, lure blade, turn blade, leaden weapon, hermetic void
                if (targetCreature == null)
                {
                    // targeting an individual item / wo
                    HandleCastSpell(spell, target);
                }
                else
                {
                    // targeting a creature, try to redirect to primary weapon
                    var weapon = spell.NonComponentTargetType switch
                    {
                        ItemType.Weapon => targetCreature.GetEquippedWeapon(),
                        ItemType.Caster => targetCreature.GetEquippedWand(),
                        ItemType.WeaponOrCaster => targetCreature.GetEquippedWeapon() ?? targetCreature.GetEquippedWand(),
                        ItemType.MeleeWeapon => targetCreature.GetEquippedMeleeWeapon(),
                        ItemType.MissileWeapon => targetCreature.GetEquippedMissileWeapon(),
                        _ => null
                    };

                    if (weapon != null && weapon.IsEnchantable)
                    {
                        HandleCastSpell(spell, weapon);
                    }
                    else
                    {
                        // 'fails to affect'?
                        if (player != null)
                            player.Session.Network.EnqueueSend(new GameMessageSystemChat($"You fail to affect {targetCreature.Name} with {spell.Name}", ChatMessageType.Magic));

                        if (targetPlayer != null && !targetPlayer.SquelchManager.Squelches.Contains(this, ChatMessageType.Magic))
                            targetPlayer.Session.Network.EnqueueSend(new GameMessageSystemChat($"{Name} fails to affect you with {spell.Name}", ChatMessageType.Magic));
                    }
                }
            }
            else
            {
                // all other item spells, cast directly on target
                HandleCastSpell(spell, target);
            }
        }

        public float ItemManaRateAccumulator { get; set; }

        public bool ItemManaDepletionMessage { get; set; }

        public void OnSpellsActivated()
        {
            IsAffecting = true;
            ItemManaRateAccumulator = 0;
            ItemManaDepletionMessage = false;
        }

        public void OnSpellsDeactivated()
        {
            IsAffecting = false;
        }

        private const double defaultIgnoreSomeMagicProjectileDamage = 0.25;

        public double? GetAbsorbMagicDamage()
        {
            var absorbMagicDamage = AbsorbMagicDamage;

            if (absorbMagicDamage == null && HasImbuedEffect(ImbuedEffectType.IgnoreSomeMagicProjectileDamage))
                absorbMagicDamage = defaultIgnoreSomeMagicProjectileDamage;

            return absorbMagicDamage;
        }

        /// <summary>
        /// For spells with NonComponentTargetType, returns the list of equipped items matching the target type
        /// </summary>
        private static List<WorldObject> GetNonComponentTargetTypes(Spell spell, Creature target)
        {
            switch (spell.NonComponentTargetType)
            {
                case ItemType.Vestements:               // impen / bane
                case ItemType.Weapon:                   // blood drinker
                case ItemType.LockableMagicTarget:      // strengthen lock
                case ItemType.Caster:                   // hermetic void
                case ItemType.WeaponOrCaster:           // lure blade, defender cantrip, hermetic link cantrip, mukkir sense
                case ItemType.Item:                     // essence lull

                    return target.EquippedObjects.Values.Where(i => (i.ItemType & spell.NonComponentTargetType) != 0 && (i.ValidLocations & EquipMask.Selectable) != 0 && i.IsEnchantable).ToList();
            }
            return null;
        }
    }
}
