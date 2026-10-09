using System;

using ACE.Common;
using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Server.ClassAbilities;
using ACE.Server.ClassAbilities.Abilities;
using ACE.Server.Entity;
using ACE.Server.Entity.Actions;
using ACE.Server.EquipmentMods;
using ACE.Server.Managers;
using ACE.Server.MonsterEffects;
using ACE.Server.Network.GameMessages.Messages;
using ACE.Server.Pvp.Rules;
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
                // Armor Tinkering is MULTIPLICATIVE on Shield Block's own rank bonus (2026-09-12 overhaul),
                // not an additive rider beside it - so the skill is worth more the more ranks are bought and
                // exactly nothing at rank 0. At zero effective Armor Tinkering the factor is exactly 1.0 and
                // the block chance is bit-identical to rank alone.
                var armorTink = GetClassAbilityAffinityMultiplier(Skill.ArmorTinkering);

                // What class_ability_affinity_chance_cap bounds is the AMOUNT the multiply adds, and the
                // Shield Wall equipment mod is added OUTSIDE that cap - see ShieldBlockAbility.BlockChance
                // for why the placement is load-bearing. Shield Wall is MACHINERY, and ownership is already
                // proven by the TryGetClassAbility(ShieldBlock) gate on this branch, so the plain read is
                // correct here and no GetMachineryEquipmentModValue gate is needed.
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
                    // Deception is MULTIPLICATIVE on Parry's own rank bonus (2026-09-12 overhaul), not an
                    // additive rider beside it. At zero effective Deception the factor is exactly 1.0 and
                    // the parry chance is bit-identical to rank alone. No affinity cap: Parry is not one of
                    // the abilities class_ability_affinity_chance_cap covers, and the pooled avoidance cap
                    // below is its ceiling.
                    var deception = GetClassAbilityAffinityMultiplier(Skill.Deception);

                    parryChance = ParryAbility.ParryChance(parryRank,
                        PropertyManager.GetDouble("class_ability_parry_percent_per_rank").Item,
                        deception);
                }

                // Surefooted fed this same parryChance term until it was retired on 2026-09-12 (class
                // ability overhaul) - its stacking avoidance overlapped Parry inside the same pooled cap.
            }

            if (blockChance <= 0.0 && parryChance <= 0.0)
                return ClassAbilityAvoidanceOutcome.None;

            var cap = PropertyManager.GetDouble("class_ability_avoidance_cap").Item;
            var roll = ThreadSafeRandom.Next(0.0f, 1.0f);

            return ClassAbilityAvoidance.Resolve(blockChance, parryChance, cap, roll);
        }

        /// <summary>
        /// Fires the synergy effects of a blocked or parried hit: Blocked -> Thorns at full strength (the
        /// enemy hit the shield); Parried -> a Riposte counter. Sends the player-facing avoidance notice.
        /// A parry also triggered Thorns, scaled, while Shield Check existed; that ability was retired on
        /// 2026-09-12 and a parry now reflects nothing.
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

                // Shield Check turned a parry into a scaled Thorns reflect here until it was retired on
                // 2026-09-12 (class ability overhaul). A parry now reflects nothing; only a BLOCK does.

                // Riposte turns a parry into a free counter-strike
                TriggerRiposte(attacker);
            }

            // Kinetic Charge (Vanguard T2) counts every attack that REACHED the Vanguard, which by its own
            // description includes one that was blocked or parried. An avoided attack never reaches any of
            // the five damage sites, so this is the ONLY feed for those - the damaging case is counted from
            // the pre-write hook instead, and the two paths are disjoint by construction.
            //
            // Outside the block/parry fork for the same reason Pocket Sand below is: both avoidance kinds
            // count, and neither is worth more than the other here.
            TryBuildKineticChargeFromAvoidedAttack(attacker);

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

            // Deception is MULTIPLICATIVE on Pocket Sand's own rank bonus (2026-09-12 overhaul), not an
            // additive rider beside it. At zero effective Deception the factor is exactly 1.0 and the proc
            // chance is bit-identical to rank alone. class_ability_affinity_chance_cap, passed below, now
            // bounds the AMOUNT the multiply adds rather than a raw skill quotient.
            var deception = GetClassAbilityAffinityMultiplier(Skill.Deception);

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
        /// Thorns class ability (and/or the Thorns equipment mod), at strengthMultiplier of full (1.0 for
        /// every trigger today; Shield Check's scaled parry reflect was retired on 2026-09-12). No-op without
        /// Thorns learned or the mod equipped, without a shield equipped, or when the attacker is out of
        /// range.
        ///
        /// THIS IS THE SINGLE PLACE A THORNS REFLECT HAPPENS, and every trigger reaches it: the physical
        /// incoming-damage hook (ThornsAbility.OnDamageTaken, from Player.TakeDamage - melee, missile), the
        /// rank-0 equipment-mod path (ApplyEquipmentModIncomingDamage), the shield-BLOCK avoidance path
        /// (OnClassAbilityAttackAvoided) and the three direct magic sites (ApplyThornsOnMagicHit, from
        /// SpellProjectile.DamageTarget, HandleCastSpell_Boost and HandleCastSpell_Transfer).
        ///
        /// CLOSE-RANGE PUNISH (user ruling 2026-10-07, mirroring the monster reflect effect's on=hit ruling):
        /// the attacker must be within class_ability_thorns_max_range metres of this player when the hit
        /// lands, measured edge to edge with WorldObject.GetCylinderDistance via the shared
        /// Entity.CloseRangeReflect gate, so a larger monster effectively reaches further. Either side lacking
        /// a PhysicsObj fails closed. The gate sits HERE rather than at each caller so that no trigger - the
        /// block path included - can bypass it; a long-range archer or caster is never answered.
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
            // reflect nothing.
            var gearMod = GetEquippedModValue(EquipmentModId.Thorns);

            var owned = TryGetClassAbility(ClassAbilityId.Thorns, out var rank);

            if (!owned && gearMod <= 0.0)
                return;

            // the close-range gate - see the summary. After the ownership checks so a player with neither
            // the ability nor the mod pays nothing for it.
            if (!CloseRangeReflect.IsWithinRange(this, attacker, PropertyManager.GetDouble("class_ability_thorns_max_range").Item))
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

            // null-tolerant only so a session-less player (the unit-test harness) can drive the real reflect;
            // a logged-in player always has both
            if (Session != null && SquelchManager != null && !SquelchManager.Squelches.Contains(attacker, ChatMessageType.CombatSelf))
                Session.Network.EnqueueSend(new GameMessageSystemChat(
                    $"Your thorns reflect {reflect:N0} points of damage back at {attacker.Name}!", ChatMessageType.CombatSelf));
        }

        /// <summary>
        /// Thorns on a landed DIRECT magic hit. Called from the three direct magic health-loss sites -
        /// SpellProjectile.DamageTarget, WorldObject_Magic.HandleCastSpell_Boost (Harm) and
        /// HandleCastSpell_Transfer (Drain Health) - each AFTER its health write, the same slot the physical
        /// hook occupies in Player.TakeDamage, so it fires on a killing blow and on a hit mitigated to 0. The
        /// fifth health-loss site, EnchantmentManager.ApplyDamageTick, is a DoT tick with no attacker and is
        /// deliberately not wired: a DoT never triggers Thorns.
        ///
        /// WHY THORNS IS WIRED BY NAME HERE INSTEAD OF THROUGH ApplyIncomingDamageClassAbilities. That
        /// dispatch reaches every IIncomingDamageAbility, and Vengeance rides it too with physical-only
        /// tracking; calling the whole dispatch from a magic site would silently widen every handler on it.
        /// This method reaches Thorns and nothing else.
        ///
        /// Mirrors the physical path's two calls in Player.TakeDamage: the attacker filter is the one
        /// ApplyIncomingDamageClassAbilities and ApplyEquipmentModIncomingDamage both apply (a live monster
        /// attacker only - never PvP, never self), and the kill switches come with the reads ApplyThornsReflect
        /// already makes (TryGetClassAbility honours class_abilities_enabled, GetEquippedModValue honours
        /// equipment_mods_enabled), so the ability and the rank-0 equipment mod answer a spell exactly as they
        /// answer a sword. The close-range gate is inside ApplyThornsReflect.
        ///
        /// Only a DirectHit origin qualifies - the same split the monster reflect effect draws: an item or
        /// cloak proc, a Cascade hop, a Spell AOE splash and an Echo Cast recast are Secondary and never
        /// trigger Thorns.
        /// </summary>
        public void ApplyThornsOnMagicHit(WorldObject source, DamageType spellDamageType, IncomingDamageOrigin origin)
        {
            if (origin != IncomingDamageOrigin.DirectHit)
                return;

            if (source is not Creature attacker || PvpClassifier.IsPvp(attacker, this) || attacker == this || attacker.IsDead)
                return;

            ApplyThornsReflect(attacker, GetThornsMagicReflectDamageType(spellDamageType), 1.0);
        }

        /// <summary>
        /// The damage type a magic-triggered Thorns reflect deals: the incoming spell's own type when it is a
        /// single physical, elemental or nether type; otherwise (Health, Stamina, Mana, Undef, Base, or a
        /// multi-type mask) Bludgeon, the same Undef fallback OnClassAbilityAttackAvoided uses for a block.
        /// </summary>
        public static DamageType GetThornsMagicReflectDamageType(DamageType spellDamageType)
        {
            var reflectable = spellDamageType & (DamageType.Physical | DamageType.Elemental | DamageType.Nether);

            if (reflectable == spellDamageType && reflectable != DamageType.Undef && !reflectable.IsMultiDamage())
                return reflectable;

            return DamageType.Bludgeon;
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

                // the counter's own base swing cost stays free (it's a reflex, not a paid swing), but it
                // still pays the Savage Blows per-swing surcharge like any other swing, so it gets the
                // damage bonus on the same terms. GetPowerRange() reflects the player's own outgoing-attack
                // power bar, which has nothing to do with a reflex triggered by being parried, so use a
                // fixed PowerAccuracy.Medium base for the surcharge calculation instead.
                //
                // The counter fires from its own delayed ActionChain, which can land BETWEEN the strikes
                // of a swing already in flight (the swing's own strikes are delayed chain actions too), so
                // ApplySavageBlowsStamina here would otherwise clobber SavageBlowsSwingPaid with the
                // counter's own outcome and strand the in-flight swing's remaining strikes/cleave hits with
                // the wrong bonus state. Save and restore the flag around the counter so it never leaks.
                var swingPaid = SavageBlowsSwingPaid;
                try
                {
                    var counterBase = GetAttackStamina(PowerAccuracy.Medium);
                    var sbCost = ApplySavageBlowsStamina(attacker, counterBase, 0);
                    if (sbCost > 0)
                        UpdateVitalDelta(Stamina, -sbCost);

                    // a riposte is a reflex against whoever hit YOU, not a target the player chose, so it must
                    // not steal the pet off the player's target.
                    DamageTarget(attacker, weapon, fraction, primaryTarget: false);
                }
                finally
                {
                    SavageBlowsSwingPaid = swingPaid;
                }
            });
            chain.EnqueueChain();
        }

        private void SendClassAbilityCombatMessage(string text)
        {
            Session?.Network.EnqueueSend(new GameMessageSystemChat(text, ChatMessageType.CombatSelf));
        }
    }
}
