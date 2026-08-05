using System;

using ACE.Common;
using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Server.ClassAbilities;
using ACE.Server.ClassAbilities.Abilities;
using ACE.Server.Entity.Actions;
using ACE.Server.EquipmentMods;
using ACE.Server.Managers;
using ACE.Server.Network.GameMessages.Messages;

namespace ACE.Server.WorldObjects
{
    partial class Player
    {
        // The class-ability avoidance tier (Shield Block + Parry) and the Thorns reflect it can trigger.
        // The roll is resolved in DamageEvent.DoCalculateDamage BEFORE the normal evade roll, so a
        // blocked or parried hit can proc its synergy (Thorns / Shield Check / Riposte) while an evaded
        // hit procs nothing - the Vanguard-vs-Rogue fork the design turns on. See ClassAbilityAvoidance for
        // the pure pooling math and SKILL-TABLES-PREVIEW for the trigger matrix.

        /// <summary>
        /// Rolls the pooled Shield Block + Parry avoidance for an incoming attack (players only; call site
        /// gates on class_abilities_enabled). Block needs an equipped shield and applies to any hit type;
        /// Parry applies to melee only. Returns the outcome; None means the attack proceeds to the normal
        /// evade roll.
        /// </summary>
        public ClassAbilityAvoidanceOutcome RollClassAbilityAvoidance(Creature attacker, CombatType combatType)
        {
            if (!PropertyManager.GetBool("class_abilities_enabled").Item)
                return ClassAbilityAvoidanceOutcome.None;

            // hot path (every incoming hit): a player with no class abilities at all pays only a cheap
            // dictionary-count check, never an equipped-item scan
            if (GetClassAbilityCache().Count == 0)
                return ClassAbilityAvoidanceOutcome.None;

            double blockChance = 0.0;
            // check the (O(1)) learned-skill lookup before the equipped-item scan
            if (TryGetClassAbility(ClassAbilityId.ShieldBlock, out var blockRank) && GetEquippedShield() != null)
            {
                var armorTink = GetClassAbilityScaling(Skill.ArmorTinkering,
                    PropertyManager.GetDouble("class_ability_shieldblock_armortink_per_trained").Item,
                    PropertyManager.GetDouble("class_ability_shieldblock_armortink_per_spec").Item) * 0.01;

                // The Armor Tinkering rider is CAPPED (class_ability_affinity_chance_cap) and the Shield Wall
                // equipment mod is added OUTSIDE that cap - see ShieldBlockAbility.BlockChance for why the
                // placement is load-bearing. Shield Wall is MACHINERY, and ownership is already proven by the
                // TryGetClassAbility(ShieldBlock) gate on this branch, so the plain read is correct here and
                // no GetMachineryEquipmentModValue gate is needed.
                blockChance = ShieldBlockAbility.BlockChance(blockRank,
                    PropertyManager.GetDouble("class_ability_shieldblock_base").Item,
                    PropertyManager.GetDouble("class_ability_shieldblock_step").Item,
                    armorTink,
                    PropertyManager.GetDouble("class_ability_affinity_chance_cap").Item,
                    GetEquippedModValue(EquipmentModId.ShieldWall));
            }

            double parryChance = 0.0;
            if (combatType == CombatType.Melee && TryGetClassAbility(ClassAbilityId.Parry, out var parryRank))
            {
                var deception = GetClassAbilityScaling(Skill.Deception,
                    PropertyManager.GetDouble("class_ability_parry_deception_per_trained").Item,
                    PropertyManager.GetDouble("class_ability_parry_deception_per_spec").Item) * 0.01;

                parryChance = ParryAbility.ParryChance(parryRank,
                    PropertyManager.GetDouble("class_ability_parry_percent_per_rank").Item,
                    deception);
            }

            if (blockChance <= 0.0 && parryChance <= 0.0)
                return ClassAbilityAvoidanceOutcome.None;

            var cap = PropertyManager.GetDouble("class_ability_avoidance_cap").Item;
            var roll = ThreadSafeRandom.Next(0.0f, 1.0f);

            return ClassAbilityAvoidance.Resolve(blockChance, parryChance, cap, roll);
        }

        /// <summary>
        /// Fires the synergy effects of a blocked or parried hit: Blocked -> Thorns at full strength (the
        /// enemy hit the shield); Parried -> Thorns via Shield Check (scaled) and a Riposte counter. Sends
        /// the player-facing avoidance notice.
        /// </summary>
        public void OnClassAbilityAttackAvoided(Creature attacker, ClassAbilityAvoidanceOutcome outcome, CombatType combatType)
        {
            if (attacker == null)
                return;

            // a blocked/parried hit skips the normal evade path (OnEvade), so register the attacker here
            // the way OnEvade would have (aggro/retaliation UI)
            SetCurrentAttacker(attacker);

            var damageType = attacker.GetDamageType(false, combatType);
            if (damageType == DamageType.Undef)
                damageType = DamageType.Bludgeon;

            if (outcome == ClassAbilityAvoidanceOutcome.Block)
            {
                SendClassAbilityCombatMessage($"You block {attacker.Name}'s attack with your shield!");

                // blocked hits trigger Thorns at full strength (base behaviour - the enemy hit the shield)
                ApplyThornsReflect(attacker, damageType, 1.0);
            }
            else if (outcome == ClassAbilityAvoidanceOutcome.Parry)
            {
                SendClassAbilityCombatMessage($"You parry {attacker.Name}'s attack!");

                // Shield Check turns a parry into a (scaled) Thorns reflect
                if (TryGetClassAbility(ClassAbilityId.ShieldCheck, out var checkRank))
                {
                    // Shield Check equipment mod (MACHINERY): +pp on the parry-to-thorns conversion, capped
                    // at a full-strength reflect. Unreachable without the ability - the gate above returned.
                    var strength = ShieldCheckAbility.ReflectStrength(checkRank,
                        PropertyManager.GetDouble("class_ability_shieldcheck_strength_base").Item,
                        PropertyManager.GetDouble("class_ability_shieldcheck_strength_step").Item,
                        GetEquippedModValue(EquipmentModId.ShieldCheck));

                    ApplyThornsReflect(attacker, damageType, strength);
                }

                // Riposte turns a parry into a free counter-strike
                TriggerRiposte(attacker);
            }
        }

        /// <summary>
        /// Reflects a fraction of the equipped shield's effective armor level back at the attacker via the
        /// Thorns class ability, at strengthMultiplier of full (1.0 for a damaging/blocked hit, the Shield
        /// Check fraction for a parry). No-op without Thorns learned or a shield equipped. Shared by the
        /// Thorns incoming-damage hook and the block/parry avoidance path.
        /// </summary>
        public void ApplyThornsReflect(Creature attacker, DamageType damageType, double strengthMultiplier)
        {
            if (attacker == null || attacker.IsDead || strengthMultiplier <= 0.0)
                return;

            // Thorns equipment mod (STANDALONE): the reflect now runs when the ability is owned OR the mod is
            // present, and the mod is an additive term on the same reflect percentage. The shield requirement
            // is the ability's own condition and is kept for the mod - reflecting a fraction of a shield you
            // are not carrying is meaningless. Every trigger-matrix rule is untouched: this method is still
            // the single place a reflect happens, so blocked hits reflect at full strength and parried hits
            // only reflect through Shield Check.
            var gearMod = GetEquippedModValue(EquipmentModId.Thorns);

            var owned = TryGetClassAbility(ClassAbilityId.Thorns, out var rank);

            if (!owned && gearMod <= 0.0)
                return;

            var shield = GetEquippedShield();
            if (shield == null)
                return;

            var shieldArmorLevel = (shield.GetProperty(PropertyInt.ArmorLevel) ?? 0) + shield.EnchantmentManager.GetArmorMod();

            var percentPerRank = PropertyManager.GetDouble("class_ability_thorns_percent_per_rank").Item;

            var shieldScale = owned
                ? GetClassAbilityScaling(Skill.Shield,
                    PropertyManager.GetDouble("class_ability_thorns_shield_per_trained").Item,
                    PropertyManager.GetDouble("class_ability_thorns_shield_per_spec").Item) * 0.01
                : 0.0;

            var baseReflect = ThornsAbility.ComputeReflectDamage(shieldArmorLevel, rank, percentPerRank, shieldScale, gearMod);
            var reflect = (uint)Math.Round(baseReflect * strengthMultiplier);

            if (reflect == 0)
                return;

            attacker.TakeDamage(this, damageType, reflect);

            if (!SquelchManager.Squelches.Contains(attacker, ChatMessageType.CombatSelf))
                Session.Network.EnqueueSend(new GameMessageSystemChat(
                    $"Your thorns reflect {reflect:N0} points of damage back at {attacker.Name}!", ChatMessageType.CombatSelf));
        }

        /// <summary>
        /// Riposte: when a hit is parried, counter with a free weapon strike at the rank's damage fraction.
        /// Deferred onto a short action chain so it doesn't re-enter the attacker's in-progress combat
        /// frame; routed through the normal DamageTarget path so Poison Weapon / Acid Proc apply.
        /// </summary>
        private void TriggerRiposte(Creature attacker)
        {
            if (attacker == null || attacker.IsDead)
                return;

            if (!TryGetClassAbility(ClassAbilityId.Riposte, out var rank))
                return;

            // Riposte equipment mod (MACHINERY): +pp on the counter's weapon-damage fraction, unreachable
            // without the ability - the gate above returned. The counter still routes through DamageTarget,
            // so it carries the standalone outgoing mods (Venom, Savage Blows, ...) exactly like the
            // ability's own counter does.
            var fraction = (float)RiposteAbility.CounterFraction(rank,
                PropertyManager.GetDouble("class_ability_riposte_fraction_base").Item,
                PropertyManager.GetDouble("class_ability_riposte_fraction_step").Item,
                GetEquippedModValue(EquipmentModId.Riposte));

            if (fraction <= 0.0f)
                return;

            var weapon = GetEquippedMeleeWeapon();

            var chain = new ActionChain();
            chain.AddDelaySeconds(0.1);
            chain.AddAction(this, () =>
            {
                if (IsDead || attacker.IsDead)
                    return;

                SendClassAbilityCombatMessage($"You riposte {attacker.Name}!");
                DamageTarget(attacker, weapon, fraction);
            });
            chain.EnqueueChain();
        }

        private void SendClassAbilityCombatMessage(string text)
        {
            Session?.Network.EnqueueSend(new GameMessageSystemChat(text, ChatMessageType.CombatSelf));
        }
    }
}
