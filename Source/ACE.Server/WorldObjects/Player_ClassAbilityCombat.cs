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
using ACE.Server.WorldObjects.Managers;

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
            if (combatType == CombatType.Melee)
            {
                if (TryGetClassAbility(ClassAbilityId.Parry, out var parryRank))
                {
                    var deception = GetClassAbilityScaling(Skill.Deception,
                        PropertyManager.GetDouble("class_ability_parry_deception_per_trained").Item,
                        PropertyManager.GetDouble("class_ability_parry_deception_per_spec").Item) * 0.01;

                    parryChance = ParryAbility.ParryChance(parryRank,
                        PropertyManager.GetDouble("class_ability_parry_percent_per_rank").Item,
                        deception);
                }

                // Surefooted (Rogue T2) feeds the SAME parry term, and deliberately does NOT require Parry
                // to be learned - it sits outside the branch above for exactly that reason. Added BEFORE
                // Resolve, so the pooled Shield Block + Parry cap applies to the combined figure and this
                // ability can only claim more of an already-bounded pool, never widen it. Returns 0 when
                // unowned or the stack pool is empty.
                parryChance += GetSurefootedParryBonus();
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

            // Pocket Sand (Rogue T3) fires on ANY avoidance kind, so it sits outside the block/parry fork -
            // a Vanguard-built Rogue gets it off blocks exactly as a dodge-built one gets it off evades.
            TryPocketSand(attacker);
        }

        /// <summary>
        /// Pocket Sand (Rogue T3): on any avoided attack, a chance to blind the attacker - a flat -30 to all
        /// of its attack skills for 20 seconds.
        ///
        /// CALLED FROM THREE PLACES, because "avoidance" has no single choke point in this engine:
        /// Player.OnEvade (the normal evade roll, melee and missile),
        /// <see cref="OnClassAbilityAttackAvoided"/> (a class-ability block or parry, which skips OnEvade
        /// entirely), and WorldObject.TryResistSpell (a resisted spell, which is not a physical attack at
        /// all). Each call site owns its own "was this actually avoided" decision; this method owns
        /// ownership, the roll, and the application, so no site can get the mechanic subtly different.
        ///
        /// THE DEBUFF IS WRITTEN OUTSIDE THE SPELL SYSTEM, through EnchantmentManager.AddClassAbilityDebuff
        /// in a fork-reserved synthetic SpellCategory. That is what makes it stack additively with retail
        /// Dirty Fighting's attack debuff instead of duelling it for one slot - see the ability's doc
        /// comment and SpellCategory_ClassAbility_Base. It borrows DF's spell id purely as the client-facing
        /// identity; no Spell object is constructed and nothing is cast, so it does not depend on the DF
        /// patch dat being installed (FightDirty_ApplyHighAttack, which does cast, bails when it is not).
        ///
        /// Re-proccing REFRESHES the existing entry rather than adding a layer (the primitive keys on
        /// category + spell id), which is why there is no cooldown here.
        /// </summary>
        public void TryPocketSand(Creature attacker)
        {
            if (!PropertyManager.GetBool("class_abilities_enabled").Item)
                return;

            // hot path (every avoided attack): a player with no class abilities at all pays only a cheap
            // dictionary-count check
            if (GetClassAbilityCache().Count == 0)
                return;

            if (attacker == null || attacker is Player || attacker.IsDead)
                return;

            if (!TryGetClassAbility(ClassAbilityId.PocketSand, out var rank))
                return;

            var deception = GetClassAbilityScaling(Skill.Deception,
                PropertyManager.GetDouble("class_ability_pocketsand_deception_per_trained").Item,
                PropertyManager.GetDouble("class_ability_pocketsand_deception_per_spec").Item) * 0.01;

            var chance = PocketSandAbility.Chance(rank,
                PropertyManager.GetDouble("class_ability_pocketsand_chance_base").Item,
                PropertyManager.GetDouble("class_ability_pocketsand_chance_step").Item,
                deception,
                PropertyManager.GetDouble("class_ability_affinity_chance_cap").Item);

            if (ThreadSafeRandom.Next(0.0f, 1.0f) > chance)
                return;

            var magnitude = (float)Math.Max(0.0, PropertyManager.GetDouble("class_ability_pocketsand_magnitude").Item);

            if (magnitude <= 0.0f)
                return;

            attacker.EnchantmentManager.AddClassAbilityDebuff(
                (uint)SpellId.DF_Specialized_AttackDebuff,
                PocketSandPowerLevel,
                this,
                // FULLY QUALIFIED on purpose: inside Player, the bare name EnchantmentManager binds to
                // WorldObject's FIELD of that name, not to the type, and the compiler rejects a const read
                // through an instance reference.
                (SpellCategory)Managers.EnchantmentManager.SpellCategory_ClassAbility_PocketSand,
                EnchantmentTypeFlags.Skill | EnchantmentTypeFlags.Additive | EnchantmentTypeFlags.AttackSkills,
                0,
                -magnitude,
                PropertyManager.GetDouble("class_ability_pocketsand_duration_seconds").Item);

            attacker.EnqueueBroadcast(new GameMessageScript(attacker.Guid, PlayScript.DirtyFightingAttackDebuff));

            // The sand puff itself: a raw 0x33 PhysicsScript sent by DataID (opcode 0xF754), which reaches every
            // creature regardless of its own script table. Default 0x330008B2 (a brown two-emitter Splatter
            // burst, 2 x 25 particles at the torso for ~0.6 s) was the owner's pick from the rendered contact
            // sheets in Content/preview/pocket_sand_vfx/ (2026-08-17); 0x33000038 (gold cloud) is the runner-up.
            // Tunable so it can be swapped live; 0 disables the puff and leaves only the DF head marker above.
            var puffScript = (uint)Math.Max(0, PropertyManager.GetLong("class_ability_pocketsand_puff_script").Item);
            if (puffScript != 0)
                attacker.EnqueueBroadcast(new GameMessagePlayScriptId(attacker.Guid, puffScript));

            SendClassAbilityCombatMessage($"You fling sand in {attacker.Name}'s eyes! (-{magnitude:N0} attack skills)");
        }

        /// <summary>
        /// PowerLevel stamped on the Pocket Sand entry. It orders layers WITHIN a spell category, and this
        /// ability is alone in its own synthetic category by construction, so the value never competes with
        /// anything - 1 rather than 0 only so a hand-inspected registry row is obviously not a defaulted
        /// struct.
        /// </summary>
        private const uint PocketSandPowerLevel = 1;

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
