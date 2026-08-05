using System;
using System.Collections.Generic;

using ACE.Common;
using ACE.Entity.Enum;
using ACE.Server.ClassAbilities;
using ACE.Server.ClassAbilities.Abilities;
using ACE.Server.Entity;
using ACE.Server.Entity.Actions;
using ACE.Server.EquipmentMods;
using ACE.Server.Managers;
using ACE.Server.Network.GameMessages.Messages;

namespace ACE.Server.WorldObjects
{
    partial class Player
    {
        private void SendClassAbilityBuffMessage(string text)
        {
            Session?.Network.EnqueueSend(new GameMessageSystemChat(text, ChatMessageType.Broadcast));
        }

        /// <summary>
        /// Checked on the player heartbeat (~5s): fires the "wears off" notice the moment a stacking
        /// speed buff lapses from inactivity, and clears the stacks. The combat/cast hot paths only
        /// expire lazily (silently) when next touched, so this heartbeat is what makes the fade notice
        /// land while the player is idle rather than on their next swing/cast.
        /// </summary>
        public void ClassAbilityBuffsHeartbeat()
        {
            var now = Time.GetUnixTime();

            if (frenzyStacks > 0 && FrenzyExpired(now))
            {
                frenzyStacks = 0;
                SendClassAbilityBuffMessage("Your frenzy subsides.");
                ApplyVisualEffects(PlayScript.EnchantDownRed);
            }

            if (netherRushStacks > 0 && now > netherRushExpireTime)
            {
                netherRushStacks = 0;
                SendClassAbilityBuffMessage("Your nether rush fades.");
                ApplyVisualEffects(PlayScript.EnchantDownPurple);
            }

            if (bloodChargeStacks > 0 && BloodChargeExpired(now))
            {
                bloodChargeStacks = 0;
                SendClassAbilityBuffMessage("Your blood charge fades.");
                ApplyVisualEffects(PlayScript.EnchantDownRed);
            }

            if (resonanceStacks > 0 && ResonanceExpired(now))
            {
                resonanceStacks = 0;
                SendClassAbilityBuffMessage("Your resonance fades.");
                ApplyVisualEffects(PlayScript.EnchantDownBlue);
            }

            if (spellsurgeStacks > 0 && SpellsurgeExpired(now))
            {
                spellsurgeStacks = 0;
                SendClassAbilityBuffMessage("Your spellsurge subsides.");
                ApplyVisualEffects(PlayScript.EnchantDownBlue);
            }

            // Sanguine Ward EXPIRES UNUSED - whatever absorb is left when the window lapses is simply
            // dropped. It is never refunded, converted to health, or carried into the next cast.
            if (sanguineWard.Amount > 0 && SanguineWardMath.IsExpired(sanguineWard, now))
            {
                sanguineWard = default;
                SendClassAbilityBuffMessage("Your sanguine ward fades.");
                ApplyVisualEffects(PlayScript.EnchantDownRed);
            }
        }

        // Transient runtime state for the stacking speed class abilities (Frenzy / Nether Rush).
        // None of this is persisted: the buffs are short-lived and naturally reset when the
        // Player object is rebuilt on login. All access is on the player's landblock thread (combat,
        // attack, and cast paths), so no locking is needed - the same threading assumption the rest
        // of the class-ability state relies on. See ACE.Server.ClassAbilities.Abilities for the definitions.

        // ---- Frenzy: stacking attack-speed buff --------------------------------------------------

        private int frenzyStacks;
        private double frenzyLastHitTime;

        /// <summary>
        /// How long Frenzy stacks survive without a landed hit: the tunable window plus the Lingering Fury
        /// equipment mod. Lingering Fury is a MACHINERY mod, so the extension only applies to a player who
        /// actually owns Frenzy - <see cref="GetMachineryEquipmentModValue"/> enforces that, which also keeps
        /// the window honest if a player unlearns Frenzy while stacks are still live.
        /// </summary>
        private double FrenzyExpireSeconds() =>
            PropertyManager.GetDouble("class_ability_frenzy_expire_seconds").Item
            + GetMachineryEquipmentModValue(EquipmentModId.LingeringFury);

        private bool FrenzyExpired(double now) => now - frenzyLastHitTime > FrenzyExpireSeconds();

        /// <summary>
        /// TRUE while this player's current combat stance is one Frenzy is allowed on: two-handed melee or
        /// dual-wielded melee (FINDINGS-LEDGER L6-73). Gates both the stack trigger and the attack-speed
        /// application, so a bow neither builds stacks nor benefits from ones already held.
        /// </summary>
        public bool IsFrenzyWeaponStyle => FrenzyAbility.IsQualifyingStance(CurrentMotionState?.Stance);

        /// <summary>
        /// Called on every landed weapon hit against a monster (via the FrenzyAbility outgoing-damage
        /// hook). Resets the stack if the window lapsed since the last hit, then adds one stack up to
        /// the rank's cap and refreshes the timer.
        ///
        /// Gated on IsFrenzyWeaponStyle: a hit landed with anything other than a two-handed or
        /// dual-wielded melee weapon is ignored outright - it neither adds a stack nor refreshes the
        /// expiry timer, so existing stacks still lapse on schedule while a bow is out.
        /// </summary>
        public void OnFrenzyLandedHit(int rank)
        {
            if (!IsFrenzyWeaponStyle)
                return;

            var now = Time.GetUnixTime();

            if (FrenzyExpired(now))
                frenzyStacks = 0;

            frenzyLastHitTime = now;

            var cap = FrenzyAbility.StackCap(rank);
            if (frenzyStacks < cap)
            {
                frenzyStacks++;

                // announce once, on the hit that tops it off
                if (frenzyStacks == cap)
                {
                    var pct = (int)Math.Round(cap * PropertyManager.GetDouble("class_ability_frenzy_percent_per_stack").Item * 100);
                    SendClassAbilityBuffMessage($"Your frenzy reaches its peak! (+{pct}% attack speed)");
                    ApplyVisualEffects(PlayScript.EnchantUpRed);
                }
            }
        }

        /// <summary>
        /// Attack-speed multiplier from the current Frenzy stacks (1.0 = none). Read from
        /// Creature.GetAnimSpeed. Lazily expires the stacks if the no-hit window has lapsed.
        ///
        /// Gated on IsFrenzyWeaponStyle the same way the trigger is (FINDINGS-LEDGER L6-73): while a
        /// non-qualifying weapon is out the stacks go INERT rather than being consumed or cleared, so
        /// swapping back to a two-handed or dual-wield style inside the expiry window resumes them.
        /// </summary>
        public float GetFrenzyAttackSpeedMod()
        {
            if (!TryGetClassAbility(ClassAbilityId.Frenzy, out var rank))
                return 1.0f;

            if (frenzyStacks == 0)
                return 1.0f;

            if (!IsFrenzyWeaponStyle)
                return 1.0f;

            if (FrenzyExpired(Time.GetUnixTime()))
            {
                frenzyStacks = 0;
                return 1.0f;
            }

            var stacks = Math.Min(frenzyStacks, FrenzyAbility.StackCap(rank));

            // Frenzied Pace equipment mod (MACHINERY): adds to the PER-STACK rate, so it is worth nothing
            // without Frenzy stacks to multiply and scales with how deep into a frenzy the player is. Read
            // here rather than at the animation site, so it composes inside the shared ceiling clamp in
            // ApplyClassAbilityAttackSpeed like every other attack-speed term.
            //
            // Recklessness rider: additive on the same per-stack axis as the equipment mod. Untrained
            // Recklessness reproduces the pre-rider per-stack rate exactly (GetClassAbilityScaling returns 0).
            var reckless = GetClassAbilityScaling(Skill.Recklessness,
                PropertyManager.GetDouble("class_ability_frenzy_reckless_per_trained").Item,
                PropertyManager.GetDouble("class_ability_frenzy_reckless_per_spec").Item) * 0.01;

            var perStack = PropertyManager.GetDouble("class_ability_frenzy_percent_per_stack").Item
                + reckless
                + GetEquippedModValue(EquipmentModId.FrenziedPace);

            return FrenzyAbility.AttackSpeedMultiplier(stacks, perStack);
        }

        /// <summary>
        /// Constant attack-speed multiplier from the Attack Speed class ability (1.0 = none): +5%/rank plus
        /// the Lockpick rider. Unlike Frenzy this has no stacks - it is always on once learned.
        /// </summary>
        public float GetAttackSpeedSkillMod()
        {
            // Attack Speed equipment mod (STANDALONE): folded into the ability's own multiplier so the two
            // are additive on one axis, and reachable at rank 0 - hence no early-out when the ability is
            // unowned. Composed inside the shared ceiling clamp by ApplyClassAbilityAttackSpeed below.
            var gearMod = GetEquippedModValue(EquipmentModId.AttackSpeed);

            var owned = TryGetClassAbility(ClassAbilityId.AttackSpeed, out var rank);

            if (!owned && gearMod <= 0.0)
                return 1.0f;

            var perRank = PropertyManager.GetDouble("class_ability_attackspeed_percent_per_rank").Item;
            var lockpick = owned
                ? GetClassAbilityScaling(Skill.Lockpick,
                    PropertyManager.GetDouble("class_ability_attackspeed_lockpick_per_trained").Item,
                    PropertyManager.GetDouble("class_ability_attackspeed_lockpick_per_spec").Item) * 0.01
                : 0.0;

            return AttackSpeedAbility.AttackSpeedMultiplier(rank, perRank, lockpick, gearMod);
        }

        /// <summary>
        /// Applies the class-ability attack-speed buffs (Frenzy stacks + the constant Attack Speed skill,
        /// composed multiplicatively) on top of an already-clamped base animation speed. The combined buff
        /// may exceed the normal MaxAttackSpeed cap, up to class_ability_attack_speed_ceiling, and never
        /// slows the attack below its base speed.
        ///
        /// THE QUICKENING WEAPON MOD IS ONE MORE TERM IN THE SAME PRODUCT, deliberately composed here rather
        /// than at the animation site. The attack-speed axis saturates - the 2.0 base is MaxAttackSpeed, a
        /// private static no config can raise - so the clamp below is the only thing bounding the axis, and
        /// every term that rides it has to land inside that clamp. See Player_WeaponMods.cs.
        /// </summary>
        public float ApplyClassAbilityAttackSpeed(float baseAnimSpeed)
        {
            var mod = GetFrenzyAttackSpeedMod() * GetAttackSpeedSkillMod() * GetWeaponModAttackSpeedMod();
            if (mod <= 1.0f)
                return baseAnimSpeed;

            var ceiling = (float)PropertyManager.GetDouble("class_ability_attack_speed_ceiling").Item;
            return (float)Math.Clamp(baseAnimSpeed * mod, baseAnimSpeed, ceiling);
        }

        // ---- Nether Rush: stacking void-cast-speed buff ------------------------------------------

        private int netherRushStacks;
        private double netherRushExpireTime;

        /// <summary>
        /// Cast-speed multiplier applied to the CURRENT cast, then adds a stack for future casts.
        /// Returns 1.0 (and does nothing) for non-void spells or when the skill isn't learned, so the
        /// caller can call it unconditionally at cast start. The current cast benefits from stacks
        /// accumulated so far (first void cast = no bonus, ramping up); the 20s timer refreshes here.
        /// Must be called exactly once per committed cast.
        /// </summary>
        public float ApplyNetherRushCastSpeed(Spell spell)
        {
            if (spell == null || spell.School != MagicSchool.VoidMagic)
                return 1.0f;

            if (!TryGetClassAbility(ClassAbilityId.NetherRush, out var rank))
                return 1.0f;

            var now = Time.GetUnixTime();

            if (now > netherRushExpireTime)
                netherRushStacks = 0;

            var perRank = PropertyManager.GetDouble("class_ability_netherrush_percent_per_rank").Item;

            // Arcane Lore rider: additive on the per-stack axis (same treatment as Frenzy's Dual Wield
            // rider). Untrained Arcane Lore reproduces the pre-rider per-stack rate exactly.
            var arcaneLore = GetClassAbilityScaling(Skill.ArcaneLore,
                PropertyManager.GetDouble("class_ability_netherrush_arcanelore_per_trained").Item,
                PropertyManager.GetDouble("class_ability_netherrush_arcanelore_per_spec").Item) * 0.01;

            // this cast uses stacks accumulated so far
            var mult = NetherRushAbility.CastSpeedMultiplier(netherRushStacks, rank, perRank, arcaneLore);

            // then build toward the next cast
            if (netherRushStacks < NetherRushAbility.MaxStacks)
            {
                netherRushStacks++;

                // announce once, on the cast that tops it off
                if (netherRushStacks == NetherRushAbility.MaxStacks)
                {
                    var pct = (int)Math.Round(NetherRushAbility.MaxStacks * rank * perRank * 100);
                    SendClassAbilityBuffMessage($"Your nether rush surges to its peak! (+{pct}% cast speed)");
                    ApplyVisualEffects(PlayScript.EnchantUpPurple);
                }
            }

            netherRushExpireTime = now + PropertyManager.GetDouble("class_ability_netherrush_expire_seconds").Item;

            return mult;
        }

        // ---- Flat Cast Speed: constant war-magic cast-speed buff ---------------------------------

        /// <summary>
        /// Constant cast-speed multiplier from the Flat Cast Speed class ability (1.0 = none), applied to
        /// War Magic only. Stateless - safe to read anywhere, unlike the stack-building Nether Rush.
        /// </summary>
        public float GetFlatCastSpeedMod(Spell spell)
        {
            if (spell == null || spell.School != MagicSchool.WarMagic)
                return 1.0f;

            if (!TryGetClassAbility(ClassAbilityId.FlatCastSpeed, out var rank))
                return 1.0f;

            return FlatCastSpeedAbility.CastSpeedMultiplier(rank, PropertyManager.GetDouble("class_ability_flatcastspeed_percent_per_rank").Item);
        }

        /// <summary>
        /// The combined class-ability cast-speed multiplier for a committed cast: Nether Rush (Void Magic,
        /// stack-building) composed with Flat Cast Speed (War Magic, constant). Must be called exactly
        /// once per committed cast because Nether Rush advances its stack as a side effect.
        /// </summary>
        public float ApplyClassAbilityCastSpeed(Spell spell)
        {
            return ApplyNetherRushCastSpeed(spell) * GetFlatCastSpeedMod(spell);
        }

        /// <summary>
        /// Flat class-ability spell-damage multiplier for a war/void SpellProjectile (1.0 = none), applied
        /// to finalDamage in SpellProjectile.CalculateDamage: Void Damage (Void Magic), Overchannel (War
        /// Magic), and Blood Price, which is school-agnostic.
        ///
        /// The LifeProjectile branch of CalculateDamage does NOT call this - it has its own composition
        /// (Player.ApplyLifeProjectileClassAbilityDamage), which carries Blood Price along with the Blood
        /// Charge terms. The two paths are disjoint, so Blood Price is applied exactly once either way.
        ///
        /// NOT A PURE READ ANY MORE: this also GRANTS a Resonance stack, because it is called exactly once
        /// per landed war/void projectile and Resonance is earned per landed magic hit. See the ordering note
        /// at the grant below - the stack is added after this hit's own multiplier is fixed.
        /// </summary>
        public float GetClassAbilitySpellDamageMod(Spell spell)
        {
            if (spell == null)
                return 1.0f;

            // NOTE: the class_abilities_enabled early-out was removed so the STANDALONE Void Damage and
            // Overchannel equipment mods still resolve on a server running equipment mods with class
            // abilities off. TryGetClassAbility itself returns rank 0 when the class ability system is
            // disabled, and each rider below is only computed for an owned ability, so class-ability
            // behavior is unchanged in every configuration.
            // Blood Price (Blood Mage T3): school-agnostic by design, so it sits outside the school
            // branches. 1.0 unless the current cast actually paid health for it.
            var mod = GetBloodPriceDamageMod();

            // Resonance (Spellsword T1): also school-agnostic, so it sits beside Blood Price and outside the
            // branches below rather than inside either of them. This is the war/void leg of the three-site
            // wiring described on GetResonanceMagicDamageMod - the same three sites Blood Price uses,
            // for the same reason (there is no single choke point for magic damage).
            mod *= GetResonanceMagicDamageMod();

            if (spell.School == MagicSchool.VoidMagic)
            {
                var gearMod = GetEquippedModValue(EquipmentModId.VoidDamage);

                if (TryGetClassAbility(ClassAbilityId.VoidDamage, out var voidRank) || gearMod > 0.0)
                    mod *= VoidDamageAbility.DamageMultiplier(voidRank, PropertyManager.GetDouble("class_ability_voiddamage_percent_per_rank").Item, gearMod);
            }

            // Withering handled separately (DoT tick site), not on the direct-hit finalDamage path.
            if (spell.School == MagicSchool.WarMagic)
            {
                var gearMod = GetEquippedModValue(EquipmentModId.Overchannel);
                var owned = TryGetClassAbility(ClassAbilityId.Overchannel, out var overchannelRank);

                if (owned || gearMod > 0.0)
                {
                    // +1% per divisor points of effective Arcane Lore (dual-ratio, Trained-gated), added flat
                    var arcaneLore = owned
                        ? GetClassAbilityScaling(Skill.ArcaneLore,
                            PropertyManager.GetDouble("class_ability_overchannel_arcanelore_per_trained").Item,
                            PropertyManager.GetDouble("class_ability_overchannel_arcanelore_per_spec").Item) * 0.01
                        : 0.0;

                    mod *= OverchannelAbility.DamageMultiplier(overchannelRank,
                        PropertyManager.GetDouble("class_ability_overchannel_percent_per_rank").Item, arcaneLore, gearMod);
                }
            }

            // AFTER the multiplier for this hit is fixed, never before: a hit must not boost itself. Same
            // ordering discipline the Blood Charge sites use ("an accruing strike uses the charges
            // accumulated BEFORE it"), and the reason the opening hit of a fight is unbuffed.
            AddResonanceStack();

            return mod;
        }

        /// <summary>
        /// Eagle Eye's accuracy multiplier on the player's effective MISSILE attack skill (1.0 = none):
        /// +4/8/12% by rank. Returns 1.0 for a non-missile weapon so it never touches melee to-hit.
        /// </summary>
        public float GetEagleEyeAccuracyMod()
        {
            // Eagle Eye equipment mod (STANDALONE): folded into the ability's own multiplier, reachable at
            // rank 0. The missile-weapon condition is the ability's, and it applies to the mod too - a
            // "missile attack skill" mod has nothing to act on while swinging a sword.
            var gearMod = GetEquippedModValue(EquipmentModId.EagleEye);

            var owned = TryGetClassAbility(ClassAbilityId.EagleEye, out var rank);

            if (!owned && gearMod <= 0.0)
                return 1.0f;

            if (GetCurrentWeaponSkill() != Skill.MissileWeapons)
                return 1.0f;

            return EagleEyeAbility.AccuracyMultiplier(rank, PropertyManager.GetDouble("class_ability_eagleeye_percent_per_rank").Item, gearMod);
        }

        /// <summary>
        /// The class-ability mana surcharge multiplier for a player-cast spell (1.0 = none). Overchannel
        /// surcharges ALL war spells; Spell AOE additionally surcharges Arc war spells. Both default to
        /// +100%, summing to +200% on an Arc war cast when both are learned. Applied to the computed mana
        /// cost in CalculateManaUsage so an unaffordable surcharge blocks the cast - the intended
        /// late-game "make the overabundant mana pool matter again" brake. Only war spells qualify.
        /// </summary>
        public float GetClassAbilityManaSurcharge(Spell spell)
        {
            if (spell == null || spell.School != MagicSchool.WarMagic || !PropertyManager.GetBool("class_abilities_enabled").Item)
                return 1.0f;

            var surcharge = 0.0;

            if (TryGetClassAbility(ClassAbilityId.Overchannel, out _))
                surcharge += PropertyManager.GetDouble("class_ability_overchannel_mana_surcharge").Item;

            // Spell AOE only surcharges the Arc war casts it actually radiates from
            if (TryGetClassAbility(ClassAbilityId.SpellAoe, out _) &&
                SpellProjectile.GetProjectileSpellType(spell.Id) == ProjectileSpellType.Arc)
                surcharge += PropertyManager.GetDouble("class_ability_spellaoe_mana_surcharge").Item;

            return (float)(1.0 + Math.Max(0.0, surcharge));
        }

        /// <summary>
        /// Withering's multiplier on this caster's void (nether) DoT tick damage (1.0 = none): +8%/rank
        /// plus the Creature Enchantment rider. Read in EnchantmentManager.ApplyDamageTick for nether DoTs
        /// whose caster is this player.
        /// </summary>
        public float GetWitheringVoidDotMod()
        {
            // Withering equipment mod (STANDALONE): folded into the ability's own multiplier, reachable at
            // rank 0 so an unowned player's void DoTs still tick harder.
            var gearMod = GetEquippedModValue(EquipmentModId.Withering);

            var owned = TryGetClassAbility(ClassAbilityId.Withering, out var rank);

            if (!owned && gearMod <= 0.0)
                return 1.0f;

            var creatureEnchant = owned
                ? GetClassAbilityScaling(Skill.CreatureEnchantment,
                    PropertyManager.GetDouble("class_ability_withering_creatureench_per_trained").Item,
                    PropertyManager.GetDouble("class_ability_withering_creatureench_per_spec").Item) * 0.01
                : 0.0;

            return WitheringAbility.DotMultiplier(rank, PropertyManager.GetDouble("class_ability_withering_percent_per_rank").Item, creatureEnchant, gearMod);
        }

        // ---- Summon subsystem: Empowered Summons stats + Summon 2x second pet slot ---------------

        /// <summary>
        /// Empowered Summons' multiplier on a freshly summoned combat pet's stats (1.0 = none): +per-rank
        /// plus the Leadership rider. Read in CombatPet.Init to scale the pet's health, damage, and
        /// defenses.
        /// </summary>
        public float GetEmpoweredSummonsStatMod()
        {
            // Empowered Summons equipment mod (STANDALONE): folded into the ability's own multiplier,
            // reachable at rank 0. Read once at summon time (CombatPet.Init), so a pet keeps the stats it
            // was summoned with even if the gear changes - matching how the ability itself already behaves.
            var gearMod = GetEquippedModValue(EquipmentModId.EmpoweredSummons);

            var owned = TryGetClassAbility(ClassAbilityId.EmpoweredSummons, out var rank);

            if (!owned && gearMod <= 0.0)
                return 1.0f;

            var leadership = owned
                ? GetClassAbilityScaling(Skill.Leadership,
                    PropertyManager.GetDouble("class_ability_empoweredsummons_leadership_per_trained").Item,
                    PropertyManager.GetDouble("class_ability_empoweredsummons_leadership_per_spec").Item) * 0.01
                : 0.0;

            return EmpoweredSummonsAbility.StatMultiplier(rank, PropertyManager.GetDouble("class_ability_empoweredsummons_percent_per_rank").Item, leadership, gearMod);
        }

        /// <summary>
        /// Summon 2x: TRUE when the player already has one combat pet active, the second slot is free, and
        /// the skill is learned - allowing a second concurrent combat pet. Read twice per activation:
        /// PetDevice.ActOnUse calls it right after the first summon to decide whether to spawn the second pet
        /// of the pair, and the normal single-pet gate calls it before rejecting an "already active" summon.
        /// The SecondaryActivePet == null term is what caps a player at two.
        /// </summary>
        public bool CanSummonAdditionalCombatPet()
        {
            return CurrentActivePet is CombatPet && SecondaryActivePet == null && TryGetClassAbility(ClassAbilityId.Summon2x, out _);
        }

        // ---- Soul Tether: combat-pet damage reduction + a free resummon after the pet dies -------

        /// <summary>
        /// Soul Tether's multiplier on a combat pet's incoming damage (1.0 = none): -10/17.5/25% by rank plus
        /// the Loyalty rider, clamped by the ability's own cap. Read at the pet's damage site
        /// (CombatPet.TakeDamage) on EVERY hit rather than baked in at summon time the way Empowered Summons
        /// is, so learning a rank while a pet is already in the world takes effect immediately.
        /// </summary>
        public float GetSoulTetherDamageReductionMod()
        {
            // Soul Tether equipment mod (STANDALONE): folded into the ability's own multiplier, reachable at
            // rank 0 so an unowned player's combat pets still take reduced damage. Read LIVE (this method is
            // called on every pet hit, not cached at summon time), so an equipment change takes effect
            // immediately, matching the ability's own live-read behavior.
            var gearMod = GetEquippedModValue(EquipmentModId.SoulTether);

            var owned = TryGetClassAbility(ClassAbilityId.SoulTether, out var rank);

            if (!owned && gearMod <= 0.0)
                return 1.0f;

            var loyalty = owned
                ? GetClassAbilityScaling(Skill.Loyalty,
                    PropertyManager.GetDouble("class_ability_soultether_loyalty_per_trained").Item,
                    PropertyManager.GetDouble("class_ability_soultether_loyalty_per_spec").Item) * 0.01
                : 0.0;

            return SoulTetherAbility.DamageMultiplier(rank,
                PropertyManager.GetDouble("class_ability_soultether_base").Item,
                PropertyManager.GetDouble("class_ability_soultether_step").Item,
                loyalty,
                PropertyManager.GetDouble("class_ability_soultether_max_reduction").Item,
                gearMod);
        }

        /// <summary>
        /// Transient (never persisted) flag: this player's combat pet died and has not been replaced yet.
        /// Set from CombatPet.OnDeath, cleared when a combat pet next enters one of the pet slots.
        /// </summary>
        private bool combatPetDiedPendingResummon;

        /// <summary>
        /// Called when one of this player's combat pets is slain - arms the Soul Tether resummon skip.
        /// Recorded unconditionally (the ability is checked at read time), so learning Soul Tether is not
        /// required for the flag to be accurate.
        /// </summary>
        public void OnCombatPetDied()
        {
            combatPetDiedPendingResummon = true;
        }

        /// <summary>
        /// Called when a combat pet successfully takes a pet slot - disarms the Soul Tether resummon skip.
        /// </summary>
        public void OnCombatPetSummoned()
        {
            combatPetDiedPendingResummon = false;
        }

        /// <summary>
        /// Soul Tether: TRUE when a summoning device's use cooldown should be waived because the player's
        /// combat pet was killed and Soul Tether is learned. Read in WorldObject.CheckUseRequirements, which
        /// otherwise rejects the re-use outright. Inert for every other item and for an unlearned player.
        /// </summary>
        public bool CanSkipCombatPetSummonCooldown(WorldObject item)
        {
            return item is PetDevice && combatPetDiedPendingResummon && TryGetClassAbility(ClassAbilityId.SoulTether, out _);
        }

        // ---- Mana Barrier: pay part of an incoming hit out of Mana instead of Health -------------

        /// <summary>
        /// The fraction of incoming damage Mana Barrier diverts to Mana (0 = none): 10/17.5/25% by rank plus
        /// the Magic Defense rider, clamped by the ability's own cap.
        /// </summary>
        public double GetManaBarrierDivertShare()
        {
            // Mana Barrier equipment mod (STANDALONE): folded into the ability's own divert share, reachable
            // at rank 0 so an unowned player still diverts a small share of incoming damage to Mana. Subject
            // to the same class_ability_manabarrier_max_share cap the ability itself enforces.
            var gearMod = GetEquippedModValue(EquipmentModId.ManaBarrier);

            var owned = TryGetClassAbility(ClassAbilityId.ManaBarrier, out var rank);

            if (!owned && gearMod <= 0.0)
                return 0.0;

            var magicDef = owned
                ? GetClassAbilityScaling(Skill.MagicDefense,
                    PropertyManager.GetDouble("class_ability_manabarrier_magicdef_per_trained").Item,
                    PropertyManager.GetDouble("class_ability_manabarrier_magicdef_per_spec").Item) * 0.01
                : 0.0;

            return ManaBarrierAbility.DivertShare(rank,
                PropertyManager.GetDouble("class_ability_manabarrier_base").Item,
                PropertyManager.GetDouble("class_ability_manabarrier_step").Item,
                magicDef,
                PropertyManager.GetDouble("class_ability_manabarrier_max_share").Item,
                gearMod);
        }

        /// <summary>
        /// Refunds the Mana Barrier share of a hit that has ALREADY been deducted from Health: drains Mana
        /// and restores the same amount of Health. Called from the incoming-damage hook, which runs before
        /// TakeDamage's death check, so this can avert a killing blow (intended). Pays partially when Mana
        /// is short, and does nothing when Mana is empty.
        /// </summary>
        public void ApplyManaBarrierDivert(Creature attacker, uint damageTaken)
        {
            var share = GetManaBarrierDivertShare();
            if (share <= 0.0)
                return;

            var divert = ManaBarrierAbility.Resolve(damageTaken, share, Mana.Current,
                PropertyManager.GetDouble("class_ability_manabarrier_mana_per_health").Item);

            if (divert.HealthRestored == 0 || divert.ManaSpent == 0)
                return;

            UpdateVitalDelta(Mana, -(int)divert.ManaSpent);
            UpdateVitalDelta(Health, (int)divert.HealthRestored);

            // per-hit combat line, squelch-gated on the attacker - the same shape Thorns uses
            if (Session != null && (attacker == null || !SquelchManager.Squelches.Contains(attacker, ChatMessageType.CombatSelf)))
                Session.Network.EnqueueSend(new GameMessageSystemChat(
                    $"Your mana barrier absorbs {divert.HealthRestored:N0} points of damage.", ChatMessageType.CombatSelf));
        }

        // ---- Whirlwind: per-swing 360°/+1-target cleave, paid for with a stamina surcharge ------

        /// <summary>
        /// TRUE while the current melee swing has paid the Whirlwind stamina surcharge and should cleave in
        /// a full 360° arc for one extra target. Set by ApplyWhirlwindStamina at stamina-deduction time and
        /// read in Creature.GetCleaveTarget. Reset each swing.
        /// </summary>
        public bool WhirlwindSwingActive { get; private set; }

        /// <summary>
        /// Applies the Whirlwind stamina surcharge to a two-handed or dual-wield melee swing: if the player
        /// has Whirlwind and can afford the base cost plus the surcharge, returns the increased cost and
        /// flags WhirlwindSwingActive (enabling the 360°/+1-target cleave, even on a non-cleaving weapon).
        /// Otherwise returns the base cost unchanged and clears the flag (a normal swing). Called once per
        /// swing.
        /// </summary>
        public int ApplyWhirlwindStamina(WorldObject weapon, int baseStaminaCost)
        {
            WhirlwindSwingActive = false;

            // Berserker's two weapon styles: two-handed OR dual-wield. Works even on a weapon with no
            // innate cleave - Whirlwind grants the extra target itself.
            if (weapon == null || !(TwoHandedCombat || IsDualWieldAttack))
                return baseStaminaCost;

            if (!TryGetClassAbility(ClassAbilityId.Whirlwind, out _))
                return baseStaminaCost;

            var surcharge = (int)Math.Round(baseStaminaCost * PropertyManager.GetDouble("class_ability_whirlwind_stamina_surcharge").Item);
            if (surcharge <= 0)
                return baseStaminaCost;

            // affordability: need the base cost plus the surcharge, else fall back to a normal cleave
            if (Stamina.Current < (uint)(baseStaminaCost + surcharge))
                return baseStaminaCost;

            WhirlwindSwingActive = true;
            return baseStaminaCost + surcharge;
        }

        // ---- Acid Proc: a refreshing acid DoT, keyed per target ---------------------------------

        private sealed class AcidProcDot
        {
            public int RemainingTicks;
            public uint TickAmount;
            public bool LoopActive;
        }

        // keyed by target guid; a fresh proc refreshes the existing entry (duration + amount) rather
        // than stacking a second concurrent DoT. Single-threaded per player (landblock action thread).
        private readonly Dictionary<uint, AcidProcDot> acidProcDots = new Dictionary<uint, AcidProcDot>();

        /// <summary>
        /// Applies (or refreshes) an Acid Proc damage-over-time on the target: <paramref name="tickAmount"/>
        /// unresisted damage every class_ability_acidproc_dot_interval seconds for
        /// class_ability_acidproc_dot_ticks ticks. A re-proc refreshes the duration and tick amount instead
        /// of stacking (one running loop per target). Called from AcidProcAbility on a landed weapon hit.
        /// </summary>
        public void ApplyAcidProcDot(Creature target, uint tickAmount)
        {
            if (target == null || tickAmount == 0)
                return;

            var guid = target.Guid.Full;

            if (!acidProcDots.TryGetValue(guid, out var dot))
            {
                dot = new AcidProcDot();
                acidProcDots[guid] = dot;
            }

            dot.TickAmount = tickAmount;                                                          // refresh amount
            dot.RemainingTicks = (int)PropertyManager.GetLong("class_ability_acidproc_dot_ticks").Item;   // refresh duration

            if (!dot.LoopActive)
            {
                dot.LoopActive = true;
                ScheduleAcidProcTick(target, guid);
            }
        }

        private void ScheduleAcidProcTick(Creature target, uint guid)
        {
            var chain = new ActionChain();
            chain.AddDelaySeconds(PropertyManager.GetDouble("class_ability_acidproc_dot_interval").Item);
            chain.AddAction(this, () =>
            {
                if (!acidProcDots.TryGetValue(guid, out var dot))
                    return;

                if (target == null || target.IsDead)
                {
                    acidProcDots.Remove(guid);
                    return;
                }

                target.TakeDamage(this, DamageType.Base, dot.TickAmount);

                if (Session != null && !SquelchManager.Squelches.Contains(target, ChatMessageType.CombatSelf))
                    Session.Network.EnqueueSend(new GameMessageSystemChat(
                        $"Your acid deals {dot.TickAmount:N0} points of periodic damage to {target.Name}!", ChatMessageType.CombatSelf));

                dot.RemainingTicks--;

                if (dot.RemainingTicks > 0)
                    ScheduleAcidProcTick(target, guid);
                else
                    acidProcDots.Remove(guid);
            });
            chain.EnqueueChain();
        }

        // ---- Blood Charge: the Sanguine Reserve / Exsanguinate stack pool -----------------------
        //
        // Owned by Sanguine Reserve (Blood Mage T1 game-changer) and spent by Exsanguinate (T3 GC).
        // Mirrors Frenzy's shape exactly: non-persisted, mutated only on the player's landblock thread
        // (no locking needed), and lazily/heartbeat expired after an idle window with no new stack added.

        private int bloodChargeStacks;
        private double bloodChargeLastAddTime;

        private double BloodChargeExpireSeconds() =>
            PropertyManager.GetDouble("class_ability_bloodmage_charge_expire_seconds").Item;

        private bool BloodChargeExpired(double now) => BloodChargeMath.IsExpired(now, bloodChargeLastAddTime, BloodChargeExpireSeconds());

        /// <summary>
        /// Maximum number of Blood Charge stacks at a given rank: r1/r2/r3 tunables (3/4/5), 0 if
        /// unlearned. The rank is Sanguine Reserve's.
        /// </summary>
        private static int BloodChargeStackCap(int rank) => BloodChargeMath.StackCap(rank,
            PropertyManager.GetLong("class_ability_bloodmage_charge_stack_cap_r1").Item,
            PropertyManager.GetLong("class_ability_bloodmage_charge_stack_cap_r2").Item,
            PropertyManager.GetLong("class_ability_bloodmage_charge_stack_cap_r3").Item);

        /// <summary>
        /// Adds one Blood Charge stack (up to the rank's cap) and refreshes the idle-expiry timer.
        /// Resets the stack first if the idle window had already lapsed since the last stack was added.
        /// </summary>
        public void AddBloodChargeStack(int rank)
        {
            var now = Time.GetUnixTime();

            if (BloodChargeExpired(now))
                bloodChargeStacks = 0;

            bloodChargeLastAddTime = now;

            var cap = BloodChargeStackCap(rank);
            if (bloodChargeStacks < cap)
                bloodChargeStacks++;
        }

        /// <summary>
        /// Clears all Blood Charge stacks immediately - Exsanguinate consumes the whole pool.
        /// </summary>
        public void ClearBloodChargeStacks()
        {
            bloodChargeStacks = 0;
        }

        /// <summary>
        /// Current Blood Charge stack count, lazily expiring it first if the idle window has lapsed
        /// since the last stack was added.
        /// </summary>
        public int GetBloodChargeStacks()
        {
            if (bloodChargeStacks > 0 && BloodChargeExpired(Time.GetUnixTime()))
                bloodChargeStacks = 0;

            return bloodChargeStacks;
        }

        // ---- Sanguine Reserve / Exsanguinate / Weakened Blood: the life-strike package ----------
        //
        // ONE ENTRY POINT PER SITE, and there are two sites where a player's life damage is computed:
        //   - HARM, at WorldObject_Magic.HandleCastSpell_Boost -> ApplyHarmClassAbilityDamage;
        //   - the two LIFE PROJECTILES (Martyr's Hecatomb, Curse of Raven Fury), at the LifeProjectile
        //     branch of SpellProjectile.CalculateDamage -> ApplyLifeProjectileClassAbilityDamage.
        // Keeping each site behind one call is deliberate: the abilities compose in a fixed order (read the
        // pool -> maybe burst it -> then build toward the next strike), and spreading them across several
        // call sites is how the "current strike benefits from its own charge" off-by-one gets in.
        //
        // THE POOL IS READ AT DIFFERENT TIMES ON THE TWO SITES, and that asymmetry is the whole of the
        // Raven Fury change. Harm resolves synchronously inside its own cast, so it reads the pool where it
        // computes its damage. A life projectile cannot: Raven Fury is EIGHT projectiles from one cast, they
        // collide at different moments, and the pool is emptied by whichever strike consumes it - so a
        // per-projectile read gives the burst to exactly one arbitrary projectile and the other seven a
        // spent pool. The projectile path therefore resolves ONCE AT CAST TIME
        // (ApplyLifeProjectileBloodCharge) and every projectile of that cast reads the stamped answer.
        //
        // DRAIN IS NOT ONE OF THOSE SITES. It grants a charge and applies Weakened Blood, but takes no
        // damage multiplier - see the note on the grant helper below.
        //
        // A CAST EITHER BUILDS THE POOL OR SPENDS IT, NEVER BOTH. Both sites resolve one
        // BloodChargeCastOutcome and drive both halves - the burst and the grant - from that single value,
        // so the exclusion cannot be broken by getting one of two independent conditions wrong. See
        // ExsanguinateMath.Resolve for the ruling this encodes.

        /// <summary>
        /// Sanguine Reserve's per-charge value, shared with Exsanguinate so the burst multiplier stays the
        /// single containment lever (BLOOD-MAGE-DESIGN sec 3).
        /// </summary>
        private static double BloodChargePerStack() =>
            PropertyManager.GetDouble("class_ability_bloodmage_charge_per_stack").Item;

        /// <summary>
        /// The BLOOD CHARGE equipment mod's addition to that per-charge value. Machinery, so it is only ever
        /// read from inside a block that has ALREADY proven Sanguine Reserve ownership - TryGrantBloodCharge
        /// and ApplyBloodChargeDamage both open with TryGetClassAbility(SanguineReserve) - which is why this
        /// is <see cref="GetEquippedModValue"/> and not the ownership-testing
        /// <see cref="GetMachineryEquipmentModValue"/>. It is an instance method, unlike
        /// <see cref="BloodChargePerStack"/>, because an equipped-item sum is per-player state.
        ///
        /// It goes into the ramp's PARENTHESIS (1.0 + stacks * (perStack + gear)), never onto the result -
        /// see BloodChargeMath.DamageMultiplier and DESIGN.md 3.3.
        /// </summary>
        private double BloodChargeGearPerStack() => GetEquippedModValue(EquipmentModId.BloodCharge);

        /// <summary>
        /// Grants one Blood Charge for a landed harmful life spell (Harm, Drain, Hecatomb), if the player
        /// owns Sanguine Reserve. Silently does nothing otherwise, so call sites need no rank lookup.
        ///
        /// CALLED ONCE PER CAST, NOT ONCE PER TARGET OR PROJECTILE. Crimson Harvest's secondary drains and
        /// Curse of Raven Fury's seven untargeted projectiles both deliberately skip it: at a 3-5 stack cap
        /// a per-hit grant would top the pool off from a single cast and delete the ramp the ability is.
        ///
        /// EVERY GAINED CHARGE IS ANNOUNCED, not just the one that tops the pool off (user, live test: "the
        /// amount of blood charges stacked up is not clear to me"). That is a deliberate departure from
        /// Frenzy and Nether Rush, which announce at their peak only: those two are read off the attack or
        /// cast animation the player is already watching, whereas the Blood Charge pool has no tell at all
        /// until Exsanguinate spends it, so a peak-only notice leaves the whole ramp invisible.
        ///
        /// THE SPAM BOUND IS THE CAP, not a timer. A charge is granted once per cast and the pool holds 3-5,
        /// so a player sees at most 3-5 lines and then silence until the pool is spent or lapses - a cast at
        /// a full pool adds nothing and says nothing.
        ///
        /// NOT CALLED AT ALL ON AN EXSANGUINATING CAST. Both damage sites gate this on the cast's
        /// <see cref="BloodChargeCastOutcome"/>, so a cast that spent the pool cannot immediately put a charge
        /// back into it (user ruling, 2026-08-03: "Cannot accrue a blood charge in the same spell attack as
        /// exsang"). The peak line therefore also announces that the pool is now ARMED, which is the tell the
        /// player was missing for when the next Hecatomb or Raven Fury will burst.
        /// </summary>
        public void TryGrantBloodCharge()
        {
            if (!TryGetClassAbility(ClassAbilityId.SanguineReserve, out var rank))
                return;

            var before = GetBloodChargeStacks();

            AddBloodChargeStack(rank);

            var cap = BloodChargeStackCap(rank);

            // the pool did not move - already at the cap (or the rank has no cap at all). Say nothing.
            if (bloodChargeStacks == before)
                return;

            // quotes the SAME expression the damage site applies, gear term included, so the announced
            // percentage cannot drift from what the next strike actually gets
            var pct = BloodChargeMath.BonusPercent(
                BloodChargeMath.DamageMultiplier(bloodChargeStacks, BloodChargePerStack(), BloodChargeGearPerStack()));

            if (bloodChargeStacks >= cap)
            {
                var armed = TryGetClassAbility(ClassAbilityId.Exsanguinate, out _)
                    ? " Your next Martyr's Hecatomb or Curse of Raven Fury will EXSANGUINATE."
                    : "";

                SendClassAbilityBuffMessage($"Your blood charge reaches its peak! ({cap}/{cap} charges, +{pct}% Life Magic damage){armed}");
                ApplyVisualEffects(PlayScript.EnchantUpRed);
            }
            else
            {
                SendClassAbilityBuffMessage($"You gain a blood charge. ({bloodChargeStacks}/{cap} charges, +{pct}% Life Magic damage)");
            }
        }

        /// <summary>
        /// Resolves what ONE life cast does to the Blood Charge pool and returns that cast's damage
        /// multiplier (1.0 = none). The <paramref name="outcome"/> the caller receives is the SINGLE
        /// authority for both halves of the rule - whether the burst fired, and whether the cast may grant a
        /// charge when it lands.
        ///
        /// THE RULE, in full (user ruling, live test 2026-08-03):
        ///
        ///  - a MARTYR'S HECATOMB or CURSE OF RAVEN FURY cast at a FULL pool exsanguinates: the whole pool is
        ///    spent at the burst multiplier and NO charge is gained on that cast;
        ///  - every other qualifying life cast, including those two spells at a non-full pool, accrues a
        ///    charge and takes the plain Sanguine Reserve ramp;
        ///  - never both. The outcome is one value from <see cref="ExsanguinateMath.Resolve"/>, so "both" is
        ///    not a representable state rather than a case someone has to remember to exclude.
        ///
        /// WHAT MADE IT LOOK RANDOM BEFORE, since the fix only makes sense against it: the burst fired on ANY
        /// non-empty pool, gated only by a 10-second internal cooldown. Two identical Hecatombs therefore
        /// resolved differently based on wall-clock time the player could not see, at a magnitude that
        /// depended on however many charges happened to be held (+21% at one charge, +105% at five) - and the
        /// burst was immediately followed by a grant, so the pool refilled to 1 on the same cast that emptied
        /// it. The full-pool gate replaces the invisible clock with a condition the player counted into
        /// themselves; there is no cooldown any more, by design.
        ///
        /// <paramref name="exsanguinateEligible"/> is the caller's "this spell may spend the pool" answer.
        /// EXACTLY TWO CALLERS, AND ONLY ONE OF THEM CAN PASS TRUE. <see cref="ApplyHarmClassAbilityDamage"/>
        /// hardcodes false, so the Harm site is structurally incapable of consuming the pool;
        /// <see cref="ApplyLifeProjectileBloodCharge"/> is the only caller that can pass true, and it runs
        /// once per CAST. That is what makes "the pool is consumed at most once per cast" a property of the
        /// call graph rather than of a flag someone has to keep setting correctly.
        ///
        /// An accruing strike uses the charges accumulated BEFORE it; that strike's own charge is granted
        /// afterwards via <see cref="TryGrantBloodCharge"/>. That is the same ordering Nether Rush uses and it
        /// is why the opening cast of a fight is unbuffed.
        /// </summary>
        public float ApplyBloodChargeDamage(bool exsanguinateEligible, out BloodChargeCastOutcome outcome)
        {
            outcome = BloodChargeCastOutcome.Accrue;

            if (!TryGetClassAbility(ClassAbilityId.SanguineReserve, out var reserveRank))
                return 1.0f;

            var cap = BloodChargeStackCap(reserveRank);
            var stacks = Math.Min(GetBloodChargeStacks(), cap);
            var perStack = BloodChargePerStack();

            // BLOOD CHARGE (machinery on Sanguine Reserve, proven owned by the early return above). Read
            // ONCE here, at the cast's resolve, alongside the tunables - not per projectile: this method
            // runs once per cast and both damage sites read the answer it stamps.
            var gearPerStack = BloodChargeGearPerStack();

            outcome = ExsanguinateMath.Resolve(exsanguinateEligible,
                TryGetClassAbility(ClassAbilityId.Exsanguinate, out _), stacks, cap);

            if (ExsanguinateMath.Bursts(outcome))
            {
                // SANGUINATE (machinery on Exsanguinate). This branch is structurally unreachable unless
                // ExsanguinateMath.Resolve was handed hasExsanguinate == true just above, so ownership of
                // the linked ability is guaranteed by control flow rather than by a second lookup. Read at
                // the same point as the burst tunable, so every projectile of a Raven Fury ring carries the
                // one stamped answer.
                var burst = ExsanguinateMath.BurstMultiplier(stacks, perStack,
                    PropertyManager.GetDouble("class_ability_exsanguinate_multiplier").Item,
                    GetEquippedModValue(EquipmentModId.Sanguinate),
                    gearPerStack);

                ClearBloodChargeStacks();

                SendClassAbilityBuffMessage($"You EXSANGUINATE, spending your full pool of {stacks} blood charges at once for +{BloodChargeMath.BonusPercent(burst)}% damage on this cast. (0/{cap} charges - an exsanguinating cast gains no charge.)");
                ApplyVisualEffects(PlayScript.EnchantUpRed);

                return burst;
            }

            if (stacks <= 0)
                return 1.0f;

            return BloodChargeMath.DamageMultiplier(stacks, perStack, gearPerStack);
        }

        /// <summary>
        /// Exsanguinate's second half: the multiplier representing "ignores 25% of the target's HealthDrain
        /// resistance for this strike". Only applies against a target already under Weakened Blood, and
        /// only when the burst actually fired - the caller passes that through.
        ///
        /// Returns 1.0 for an unresistant target, so a monster with no HealthDrain resistance simply gains
        /// nothing from being marked.
        /// </summary>
        public float GetExsanguinateResistanceIgnoreMod(Creature target)
        {
            if (target == null || !target.HasWeakenedBlood)
                return 1.0f;

            return ExsanguinateMath.ResistanceIgnoreMultiplier(target.GetHealthDrainResistanceOnly(),
                PropertyManager.GetDouble("class_ability_exsanguinate_resist_ignore").Item);
        }

        /// <summary>
        /// The complete class-ability damage multiplier for one of this player's HARM casts (1.0 = none):
        /// Blood Price (paid at cast time) and the Blood Charge ramp, then this cast's own charge granted
        /// afterwards. Harm resolves synchronously inside its own cast, so there is nothing to stamp and no
        /// ordering problem to solve - the pool is read and the charge granted right here.
        ///
        /// EXSANGUINATE CANNOT FIRE FROM THIS METHOD, and that is structural rather than conditional: the
        /// false below is a literal, so no caller can opt Harm back into spending the pool without editing
        /// this line. Harm still BUILDS the pool (user ruling, live test 2026-08-03) - it simply no longer
        /// spends it - and with no burst possible there is nothing for the resistance-ignore half to attach
        /// to either, which is why this signature takes no target.
        ///
        /// PvE and player-cast is the CALLER'S gate, matching GetLifeCasterMods / TryLifeCriticalHit: this
        /// method is only reachable from a Player, and the call site checks the target is not a Player
        /// before entering. 409 creature weenies cast Harm and none of them can reach this.
        ///
        /// THE GRANT IS STILL GATED ON THE OUTCOME even though Harm's outcome is always Accrue. Reading the
        /// same value both sites read costs nothing and keeps the mutual-exclusion rule stated in one shape
        /// everywhere, so a future spell added to the eligible list cannot pick up the old unconditional
        /// grant by being wired to this method.
        /// </summary>
        public float ApplyHarmClassAbilityDamage()
        {
            var mod = GetBloodPriceDamageMod();

            // Resonance (Spellsword T1): the Harm leg of its three-site wiring. Harm is magic damage, so it
            // both takes the ramp and feeds it - see GetResonanceMagicDamageMod for why the surface is
            // school-agnostic and why these are the same three sites Blood Price uses.
            mod *= GetResonanceMagicDamageMod();

            mod *= ApplyBloodChargeDamage(exsanguinateEligible: false, out var outcome);

            if (ExsanguinateMath.GrantsCharge(outcome))
                TryGrantBloodCharge();

            // AFTER this cast's multiplier is fixed, so the Harm that earns the stack is not boosted by it.
            // Unlike the Blood Charge grant directly above, this is NOT gated on the cast outcome: Resonance
            // has no spend/accrue exclusion to respect - it is a plain per-landed-hit ramp.
            AddResonanceStack();

            return mod;
        }

        // ---- The life-projectile cast-time stamp ------------------------------------------------
        //
        // Resolved once per cast by ApplyLifeProjectileBloodCharge and read by every projectile that cast
        // produced. Transient, never persisted, mutated only on the player's landblock thread - the same
        // contract as bloodChargeStacks and bloodPriceDamageMod above.

        private float lifeProjectileChargeMod = 1.0f;
        private BloodChargeCastOutcome lifeProjectileOutcome = BloodChargeCastOutcome.Accrue;

        /// <summary>
        /// Resolves the Blood Charge half of a LIFE PROJECTILE cast - Martyr's Hecatomb or Curse of Raven
        /// Fury - exactly once, at cast time, and stamps the answer for every projectile that cast launches.
        /// Called from WorldObject_Magic.HandleCastSpell_Projectile immediately before the projectiles are
        /// created, so it runs once per cast whatever the projectile count.
        ///
        /// WHY NOT RESOLVE PER PROJECTILE, which is the obvious simplification and is WRONG. Raven Fury is
        /// eight projectiles from one cast. Exsanguinate empties the pool when it fires, so if each
        /// projectile asked the pool at its own collision, the first one to land would burst and the other
        /// seven would find an empty pool. Which projectile that is depends on flight time and on what each
        /// one happens to collide with, so the burst would land on an arbitrary target and the cast's total
        /// damage would vary run to run for reasons the player cannot see or control. Resolving at cast time
        /// is what makes "the pool is spent by the CAST" true - one read, one consumption, one multiplier
        /// shared by all eight. Do not move this back to the damage site.
        ///
        /// THIS IS THE bloodPriceDamageMod PATTERN, deliberately reused rather than reinvented: resolve once
        /// when the cast commits, store, and read at each damage site. It inherits that pattern's one
        /// trade-off too - a projectile still in flight reads the stamp of whatever the caster cast MOST
        /// RECENTLY. Both are the same player's own casts and a life projectile's flight is short next to a
        /// cast cycle, so the window is narrow; if it ever proves reachable in play the fix is to carry the
        /// stamp on the SpellProjectile alongside LifeProjectileDamage, not to go back to a per-projectile
        /// read.
        ///
        /// ALWAYS RESETS FIRST, so a cast that does not qualify (a mana or stamina life projectile, a cast
        /// at another player) disarms the previous cast's stamp rather than leaking it forward - again the
        /// ApplyBloodPriceCost shape.
        ///
        /// LEDGER WARNING - THIS IS A POWER CHANGE AND IT COMPOUNDS THE KNOWN RISK.
        /// BLOOD-MAGE-DESIGN sec 8a names Curse of Raven Fury as the kit's uncapped, pack-scaling ledger
        /// risk: it deals 0.50 x current health x damage_Ratio per CONNECTING projectile, fires eight of
        /// them across 360 degrees, and nothing caps how many enemies the ring touches. Every one of those
        /// eight now also carries the full Exsanguinate burst - up to +105% at a rank-3 pool - where
        /// previously the burst reached one strike. Raven Fury x Exsanguinate is therefore a multiplicative
        /// stack on exactly the column sec 8a says must be scored before anything is built on top of it, and
        /// it stacks further with Weakened Blood and Blood Price. Built to the user's explicit instruction
        /// (2026-08-03: "whole ring on raven fury + exsanguinate"), deliberately UNCAPPED and UNTUNED so it
        /// can be observed in play first - the same call made for Crimson Harvest's recoup. A power-assessor
        /// pass belongs here; do not add a limiter without a ruling, and if one is needed the containment
        /// lever is still class_ability_exsanguinate_multiplier (3.0 -> 2.5), not the per-charge value.
        /// </summary>
        public void ApplyLifeProjectileBloodCharge(Spell spell, WorldObject target)
        {
            lifeProjectileChargeMod = 1.0f;
            lifeProjectileOutcome = BloodChargeCastOutcome.Accrue;

            if (spell == null || spell.MetaSpellType != SpellType.LifeProjectile)
                return;

            // THE TARGET CHECK APPLIES ONLY TO A TARGETED CAST, and getting that wrong silently disabled
            // Exsanguinate on half the spells it exists for. Curse of Raven Fury is UNTARGETED: probed from
            // portal.dat 2026-08-03, it carries NonComponentTargetType None where both Hecatomb variants
            // carry Creature. So at cast time `target` is null or the caster, and the previous
            // unconditional "must be a non-player Creature" gate returned before the pool was ever read -
            // every ring cast, every time. User, live test 2026-08-03: "exsang didnt seem to work with
            // raven's fury". The other two gates were fine: Raven Fury is MetaSpellType LifeProjectile and
            // DamageType Health, same as Hecatomb.
            //
            // RELAXING THIS DOES NOT WEAKEN PvE GATING, which is the reason to be careful here. The
            // per-projectile damage site re-checks it against the victim each projectile actually struck,
            // rather than against a declared target a 360-degree ring does not have:
            // SpellProjectile.CalculateDamage:552 applies the multiplier only when
            // `sourceCreature is Player && targetPlayer == null`. That check was always the one doing the
            // real work; this one can only ever see the aimed target.
            //
            // A ring cast into players-only therefore still spends the pool for nothing, the same way a
            // cast that connects with nothing does - the burst is resolved by the CAST, by design.
            if (spell.NonComponentTargetType != ItemType.None && (target is not Creature || target is Player))
                return;

            // Only the health-basis life projectiles - Hecatomb and Raven Fury - can spend the pool. A mana
            // or stamina life projectile keeps the reset above and takes no multiplier at all.
            if (!spell.DamageType.HasFlag(DamageType.Health))
                return;

            lifeProjectileChargeMod = ApplyBloodChargeDamage(exsanguinateEligible: true, out lifeProjectileOutcome);
        }

        /// <summary>
        /// The complete class-ability damage multiplier for ONE projectile of a life-projectile cast
        /// (1.0 = none): Blood Price (paid at cast time), the cast's stamped Blood Charge ramp or
        /// Exsanguinate burst, and - only when that burst fired - the resistance-ignore against THIS
        /// projectile's own victim.
        ///
        /// The stamped terms are per-CAST; the resistance-ignore is per-TARGET and stays here, because the
        /// eight projectiles of a Raven Fury ring hit eight different creatures and only some of them carry
        /// Weakened Blood.
        ///
        /// <paramref name="grantCharge"/> is still per-projectile and still limited by the caller to the
        /// aimed bolt, so one cast grants one charge however many projectiles land. It is granted on a
        /// LANDED hit rather than at cast time on purpose - a cast that connects with nothing builds nothing.
        ///
        /// AND IT IS NOW ALSO GATED ON THE CAST'S OUTCOME. A cast that exsanguinated grants nothing, however
        /// many of its projectiles land (user ruling, 2026-08-03: "Cannot accrue a blood charge in the same
        /// spell attack as exsang"). Both the burst and the grant read the one stamped
        /// <see cref="BloodChargeCastOutcome"/>, so the ring cannot burst on the cast and then refill from
        /// its own aimed bolt.
        /// </summary>
        public float ApplyLifeProjectileClassAbilityDamage(Creature target, bool grantCharge)
        {
            var mod = GetBloodPriceDamageMod() * lifeProjectileChargeMod;

            // Resonance (Spellsword T1): the life-projectile leg of its three-site wiring. Read PER
            // PROJECTILE rather than stamped at cast time the way the Blood Charge terms above are, and that
            // difference is deliberate: the Blood Charge pool is CONSUMED by whoever reads it first, so a
            // per-projectile read would hand the burst to an arbitrary projectile. Resonance is consumed by
            // nobody - it is a ramp - so a per-projectile read is exact rather than arbitrary, and it is what
            // makes each of a Raven Fury ring's landed projectiles both take and build the ramp.
            mod *= GetResonanceMagicDamageMod();

            if (ExsanguinateMath.Bursts(lifeProjectileOutcome))
                mod *= GetExsanguinateResistanceIgnoreMod(target);

            if (grantCharge && ExsanguinateMath.GrantsCharge(lifeProjectileOutcome))
                TryGrantBloodCharge();

            // AFTER this projectile's multiplier is fixed, so a projectile never boosts itself.
            //
            // NOT gated on grantCharge, and that asymmetry with the Blood Charge grant above is the design,
            // not an oversight. Blood Charge is ONE PER CAST against a 3-5 stack cap, so it is restricted to
            // the aimed bolt; Resonance is ONE PER LANDED MAGIC HIT (SPELLSWORD-DESIGN sec 5d), so every
            // connecting projectile of a ring earns one. See the note on AddResonanceStack about a
            // 9-projectile Ring proc filling the pool in one shot - that is intended.
            AddResonanceStack();

            return mod;
        }

        /// <summary>
        /// Applies or refreshes Weakened Blood on a target this player's Harm or Drain just landed on.
        /// Silently does nothing without the ability, so call sites need no rank lookup. The magnitude and
        /// the merge-with-existing rule live on the target (Creature_ClassAbilityDebuffs.cs) because every
        /// life caster in the fellowship reads the same mark, not just this one.
        ///
        /// TWO THINGS LAND, NOT ONE (user, live test 2026-08-03: "the 'Fester' spell is what should be
        /// applied, and also multiply outgoing damage. The fester animation should be visible"):
        ///
        ///  - the transient target-side MARK, which is where the 2.00/2.50/3.10 life vulnerability lives and
        ///    is unchanged by this;
        ///  - the retail FESTER enchantment, which is what the player and everyone nearby can actually see.
        ///
        /// They are deliberately not the same object - see the note on <see cref="WeakenedBloodMath"/> for
        /// why reading the vulnerability out of the enchantment registry would hand it to
        /// GetEnchantmentsTopLayer's PowerLevel selection.
        /// </summary>
        public void TryApplyWeakenedBlood(Creature target)
        {
            if (target == null || target is Player)
                return;

            if (!TryGetClassAbility(ClassAbilityId.WeakenedBlood, out var rank))
                return;

            // HEMORRHAGE (machinery on Weakened Blood, proven owned by the early return above). Added to the
            // rank's table value in the SAME BARE-MULTIPLIER UNIT the table is in - +0.05 takes a rank-3
            // mark from 3.10 to 3.15 - not as a percentage of it. The applier's gear is baked into the
            // magnitude STORED ON THE TARGET, which is deliberate: the mark is already shared with every
            // other attacker, so their damage reads whatever the applier's mark was worth.
            var mod = WeakenedBloodMath.ResistanceMod(rank,
                PropertyManager.GetDouble("class_ability_weakenedblood_resist_r1").Item,
                PropertyManager.GetDouble("class_ability_weakenedblood_resist_r2").Item,
                PropertyManager.GetDouble("class_ability_weakenedblood_resist_r3").Item,
                GetEquippedModValue(EquipmentModId.Hemorrhage));

            var duration = PropertyManager.GetDouble("class_ability_weakenedblood_duration_seconds").Item;

            var tookHold = target.ApplyWeakenedBlood(mod, duration);

            ApplyWeakenedBloodFester(target, rank, mod, duration, tookHold);
        }

        /// <summary>
        /// Lands the retail Fester enchantment behind Weakened Blood, plays that spell's own target effect
        /// on the creature IMMEDIATELY (so this cast's drain script lands on top of it), and tells the
        /// caster in chat when the mark first takes hold.
        ///
        /// THE CHAT LINE IS THE TELL, NOT THE SCRIPT - and that is a deliberate choice, made twice.
        /// User, live test 2026-08-03: "I can't tell that fester is applying visually when weakened blood
        /// fires. Perhaps the spell is being overridden by the drain." That reading was correct, and it is
        /// measurable rather than inferred. Probing the client's portal.dat SpellBase entries directly gives:
        ///
        ///   Fester Other V (175) / VI (176) / Incantation (4489): TargetEffect 0x26 (PlayScript.RegenDownREd)
        ///   Harm Other I (7) / VI (1176) / Heart Rend (2070):     TargetEffect 0x20 (PlayScript.HealthDownRed)
        ///   Drain Health Other I (1237) / VI (1242) / Inc (4643): TargetEffect 0x20 (PlayScript.HealthDownRed)
        ///
        /// Both scripts fire on the SAME target guid in the SAME tick, and Fester's goes first: this method
        /// runs inside HandleCastSpell_Boost / HandleCastSpell_Transfer, which return to HandleCastSpell,
        /// which then calls DoSpellEffects to broadcast the Harm or Drain script. So the subtle regen-down
        /// puff is enqueued immediately before the big red health-down burst on the same creature, and the
        /// drain is what the player actually sees. The enchantment registry write itself is unconditional
        /// inside EnchantmentManager.Add - nothing in that path can silently drop it.
        ///
        /// A ONE-TICK DELAY WAS TRIED HERE AND DELIBERATELY REVERTED. Deferring the Fester broadcast by a
        /// tick does make it the visible script - and that is exactly the problem, because it then displaces
        /// the drain. User, same day, on the delayed build: "it seems the fester is now applying on drain
        /// instead of the drain effect. Drain effect is better ... revert to show the drain as a priority
        /// over fester effect." The drain burst is the primary combat feedback and outranks the mark's tell.
        /// Do not re-introduce the delay to make Fester visible; the visibility need is already met below.
        ///
        /// BECAUSE A VISUAL WAS NEVER THE RIGHT ANSWER HERE. The actual need is confirming the mark landed,
        /// in a fight already full of particles, so the caster gets a chat line naming the target and quoting
        /// the magnitude. That line, not the script, is what makes Weakened Blood confirmable - which is why
        /// reverting the delay costs nothing. It fires only when the mark TAKES HOLD, not on every refresh -
        /// see Creature.ApplyWeakenedBlood for why that gate is where the spam bound lives.
        ///
        /// THE SCRIPT IS THE SPELL'S, NOT A GUESS. spell.TargetEffect is read straight out of portal.dat via
        /// SpellBase, exactly as DoSpellEffects does for a normal cast, so whatever the client already plays
        /// for a cast Fester is what plays here. Hardcoding a PlayScript would be a guess about a value the
        /// dat file already answers - and the neighbouring PlayScript values are the WRONG VITAL rather than
        /// a different colour (0x1F HealthUpRed is a heal, 0x20 HealthDownRed is the drain), so an
        /// off-by-one there is not a cosmetic slip.
        ///
        /// NOT RESISTED, DELIBERATELY. This is the visible half of a class-ability mark, not a cast spell:
        /// the mark itself has never been resistable and the two must not diverge, or a resisted Fester would
        /// leave a target silently carrying the vulnerability with no tell at all - the exact complaint this
        /// change answers.
        ///
        /// DURATION IS THE SPELL'S OWN, which is longer than the mark's 20 seconds. The two are not required
        /// to agree: Fester is a real Life Magic debuff doing its real retail work (a health-regeneration
        /// penalty) and outliving the vulnerability costs nothing, whereas clipping it would mean mutating a
        /// dat-backed Spell. What the player must not see is a vulnerability with no visual, and this is on
        /// the safe side of that.
        ///
        /// A dead target is skipped: the enchantment would be written onto a biota that is about to become a
        /// corpse, and the strike that killed it is feedback enough.
        /// </summary>
        private void ApplyWeakenedBloodFester(Creature target, int rank, double mod, double durationSeconds, bool tookHold)
        {
            if (target.IsDead)
                return;

            var spellId = WeakenedBloodMath.FesterSpell(rank);

            if (spellId == SpellId.Undef)
                return;

            var spell = new Spell(spellId);

            if (spell.NotFound)
                return;

            target.EnchantmentManager.Add(spell, this, null);

            if (tookHold)
                SendClassAbilityBuffMessage($"Weakened Blood takes hold on {target.Name}. ({spell.Name} lands, life damage x{mod:0.00} for {durationSeconds:0}s)");

            if (spell.TargetEffect == 0)
                return;

            // IMMEDIATELY, and that ordering is the point: DoSpellEffects broadcasts this cast's own Harm or
            // Drain script on the same target a moment after this method returns, so the drain lands ON TOP
            // and stays the visible one. See the "deliberately reverted" note above before adding any delay.
            // BROADCAST, not a direct send - the same shape DoSpellEffects uses at its own TargetEffect site.
            target.EnqueueBroadcast(new GameMessageScript(target.Guid, spell.TargetEffect, spell.Formula.Scale));
        }

        // ---- Blood Price: damaging spells of any school also cost health ------------------------

        /// <summary>
        /// The damage multiplier the CURRENT cast bought with health (1.0 = none). Resolved once, at cast
        /// time, by <see cref="ApplyBloodPriceCost"/>.
        ///
        /// WHY THIS IS STORED RATHER THAN RECOMPUTED AT THE DAMAGE SITE. The ability's low-health floor is
        /// all-or-nothing - below 20% current health a cast costs nothing and gains nothing - and the cost
        /// itself moves current health. Re-deriving the bonus at impact would therefore bill a caster near
        /// the floor for a bonus their own payment had just disqualified them from. Resolving once and
        /// carrying the answer is what keeps the pair honest.
        ///
        /// The trade-off is that a projectile still in flight reads the multiplier of whatever the caster
        /// cast MOST RECENTLY, not the cast that launched it. Both are the same player's own casts and the
        /// value only varies with rank and the health floor, so the drift is at most one rank's bonus for
        /// the fraction of a second between two casts; Harm and Drain resolve synchronously inside their
        /// own cast and are exact.
        /// </summary>
        private float bloodPriceDamageMod = 1.0f;

        /// <summary>
        /// Blood Price's damage multiplier for the current cast (1.0 = none). Read at every damage site the
        /// ability covers: the war/void branch through GetClassAbilitySpellDamageMod, Harm through
        /// ApplyHarmClassAbilityDamage, and the life projectiles through
        /// ApplyLifeProjectileClassAbilityDamage.
        /// </summary>
        public float GetBloodPriceDamageMod() => bloodPriceDamageMod;

        /// <summary>
        /// Charges Blood Price for one committed cast: spends 3/4/5% of CURRENT health by rank and arms the
        /// matching +8/16/24% damage multiplier for the strike(s) that cast produces. Called exactly once
        /// per successful cast, from Player_Magic.DoCastSpell_Inner - after mana has been consumed and only
        /// on CastingPreCheckStatus.Success, so a fizzle never bills the player.
        ///
        /// Always resets the stored multiplier first, so a non-qualifying cast (a buff, a debuff, a Drain)
        /// disarms a previous cast's bonus rather than leaking it forward.
        ///
        /// PvE ONLY, matching GetLifeCasterMods / TryLifeCriticalHit: a cast at another player neither costs
        /// health nor gains damage. That is the conservative direction - the ability is a self-damaging
        /// power-up and this makes it strictly inert in PvP rather than differently balanced.
        /// </summary>
        public void ApplyBloodPriceCost(Spell spell, WorldObject target)
        {
            bloodPriceDamageMod = 1.0f;

            if (spell == null || target is not Creature targetCreature || targetCreature is Player)
                return;

            if (!TryGetClassAbility(ClassAbilityId.BloodPrice, out var rank))
                return;

            if (!BloodPriceMath.SpellQualifies(spell.IsHarmful, spell.MetaSpellType, spell.VitalDamageType == DamageType.Health))
                return;

            var charge = BloodPriceMath.Resolve(Health.Current, Health.MaxValue,
                BloodPriceMath.HealthCostFraction(rank,
                    PropertyManager.GetDouble("class_ability_bloodprice_health_cost_r1").Item,
                    PropertyManager.GetDouble("class_ability_bloodprice_health_cost_r2").Item,
                    PropertyManager.GetDouble("class_ability_bloodprice_health_cost_r3").Item),
                // BLOOD PRICE (machinery, proven owned by the early return above) rides the DAMAGE BONUS
                // ONLY. The HealthCostFraction call above deliberately takes no gear term: gear amplifies
                // the payoff and never discounts the price (DESIGN.md 2.3).
                BloodPriceMath.DamageBonus(rank,
                    PropertyManager.GetDouble("class_ability_bloodprice_damage_r1").Item,
                    PropertyManager.GetDouble("class_ability_bloodprice_damage_r2").Item,
                    PropertyManager.GetDouble("class_ability_bloodprice_damage_r3").Item,
                    GetEquippedModValue(EquipmentModId.BloodPrice)),
                PropertyManager.GetDouble("class_ability_bloodprice_min_health_fraction").Item);

            bloodPriceDamageMod = charge.DamageMultiplier;

            if (charge.HealthCost == 0)
                return;

            var spent = (uint)-UpdateVitalDelta(Health, -(int)charge.HealthCost);

            if (spent == 0)
                return;

            // the same self-attribution Martyr's Hecatomb already uses for its own health basis
            DamageHistory.Add(this, DamageType.Health, spent);

            SendClassAbilityBuffMessage($"You pay {spent:N0} points of health to empower {spell.Name}.");
        }

        // ---- Sanguine Ward: a transient absorb pool bought with the caster's own health ----------
        //
        // Blood Mage T3. Same transient-state contract as frenzyStacks / bloodChargeStacks above: never
        // persisted (a ward does not survive logout), mutated only on the player's landblock thread
        // (the cast path grants, the take-damage path consumes), so no locking - and expired on the
        // existing ClassAbilityBuffsHeartbeat as well as lazily on the next hit.
        //
        // All of the arithmetic lives in SanguineWardMath so it is unit-testable without a live Player.

        private SanguineWardMath.WardState sanguineWard;

        /// <summary>
        /// The fraction of a Martyr's Hecatomb / Curse of Raven Fury health basis this player ACTUALLY
        /// loses: 80/65/50% by Sanguine Ward rank, 100% (retail) when the ability is unlearned, so the
        /// cast site can multiply by this unconditionally. The spell's DAMAGE basis is not touched by
        /// this - only what the caster pays out of Health.
        /// </summary>
        public double GetSanguineWardSelfCostFraction()
        {
            if (!TryGetClassAbility(ClassAbilityId.SanguineWard, out var rank))
                return 1.0;

            return SanguineWardMath.SelfCostFraction(rank,
                PropertyManager.GetDouble("class_ability_sanguine_ward_selfcost_r1").Item,
                PropertyManager.GetDouble("class_ability_sanguine_ward_selfcost_r2").Item,
                PropertyManager.GetDouble("class_ability_sanguine_ward_selfcost_r3").Item);
        }

        /// <summary>
        /// Grants the ward for <paramref name="healthLost"/> points actually paid to a Hecatomb / Raven
        /// Fury cast: 50/75/100% of that amount by rank, for class_ability_sanguine_ward_duration_seconds.
        ///
        /// REFRESH, NOT STACK: a recast REPLACES whatever ward was still up rather than adding to it, so
        /// the mechanic is capped at one cast's worth however fast the caster chains. Called from the
        /// life-projectile cast site, immediately after the health is deducted.
        /// </summary>
        public void GrantSanguineWard(uint healthLost)
        {
            if (!TryGetClassAbility(ClassAbilityId.SanguineWard, out var rank))
                return;

            // CLOTTING (machinery, proven owned by the early return above) rides the ABSORB FRACTION ONLY.
            // GetSanguineWardSelfCostFraction above deliberately takes no gear term - the self-cost is the
            // ability's own bargain, the same ruling that keeps Blood Price's health cost unmoddable.
            var fraction = SanguineWardMath.WardFraction(rank,
                PropertyManager.GetDouble("class_ability_sanguine_ward_absorb_r1").Item,
                PropertyManager.GetDouble("class_ability_sanguine_ward_absorb_r2").Item,
                PropertyManager.GetDouble("class_ability_sanguine_ward_absorb_r3").Item,
                GetEquippedModValue(EquipmentModId.Clotting));

            sanguineWard = SanguineWardMath.Grant(sanguineWard, healthLost, fraction, Time.GetUnixTime(),
                PropertyManager.GetDouble("class_ability_sanguine_ward_duration_seconds").Item);

            if (sanguineWard.Amount == 0)
                return;

            SendClassAbilityBuffMessage($"A sanguine ward closes over you, absorbing your next {sanguineWard.Amount:N0} points of damage.");
            ApplyVisualEffects(PlayScript.EnchantUpRed);
        }

        /// <summary>
        /// Runs an incoming hit through the ward and returns what is LEFT of it to apply to Health.
        /// Returns <paramref name="incomingDamage"/> unchanged when no ward is up, which is the case for
        /// every player who has not just cast Hecatomb or Raven Fury - the early-out keeps this free on
        /// the hot path.
        ///
        /// ABSORB, NOT HEAL: this only ever reduces the hit. It never calls UpdateVitalDelta and can never
        /// return health to the player, so a caster who spent 40% of their pool on Hecatomb is still at
        /// 60% with a ward up, not back at full - burst thresholds and death risk are unchanged.
        /// </summary>
        public uint AbsorbWithSanguineWard(WorldObject source, uint incomingDamage)
        {
            if (sanguineWard.Amount == 0 || incomingDamage == 0)
                return incomingDamage;

            var result = SanguineWardMath.Absorb(sanguineWard, incomingDamage, Time.GetUnixTime());

            var expired = result.Absorbed == 0;

            sanguineWard = result.Remaining;

            if (expired)
                return incomingDamage;

            // per-hit combat line, squelch-gated on the attacker - the same shape Mana Barrier uses
            if (Session != null && (source == null || !SquelchManager.Squelches.Contains(source, ChatMessageType.CombatSelf)))
                Session.Network.EnqueueSend(new GameMessageSystemChat(
                    $"Your sanguine ward absorbs {result.Absorbed:N0} points of damage.", ChatMessageType.CombatSelf));

            return result.DamageAfterWard;
        }

        /// <summary>
        /// Absorb remaining on this player's sanguine ward (0 = none), lazily expiring it first.
        /// </summary>
        public uint GetSanguineWardRemaining()
        {
            if (sanguineWard.Amount > 0 && SanguineWardMath.IsExpired(sanguineWard, Time.GetUnixTime()))
                sanguineWard = default;

            return sanguineWard.Amount;
        }

        // ---- Spellsword: the Resonance and Spellsurge stack pools, and the proc-cast latch -------
        //
        // Same transient-state contract as frenzyStacks / bloodChargeStacks above: never persisted (these
        // are seconds-long buffs, and they reset naturally when the Player object is rebuilt on login),
        // mutated only on the player's landblock thread - the cast path, the projectile-impact path and the
        // melee-hit path all run there - so no locking is needed. Both pools expire twice over: lazily on
        // the next read (silently, on a hot path) and on ClassAbilityBuffsHeartbeat, which is what makes the
        // "wears off" line land while the player is standing still rather than on their next swing.

        // ---- Resonance (Spellsword T1): landing any magic damage ramps ALL your magic damage ----

        private int resonanceStacks;
        private double resonanceLastAddTime;

        private static double ResonanceWindowSeconds() =>
            PropertyManager.GetDouble("class_ability_resonance_window_seconds").Item;

        private bool ResonanceExpired(double now) => now - resonanceLastAddTime > ResonanceWindowSeconds();

        private static int ResonanceStackCap() =>
            (int)PropertyManager.GetLong("class_ability_resonance_max_stacks").Item;

        /// <summary>
        /// The per-stack magic-damage fraction at this player's Resonance rank: 2/3/4% by rank.
        /// </summary>
        private static double ResonancePerStack(int rank) => ResonanceAbility.PerStack(rank,
            PropertyManager.GetDouble("class_ability_resonance_per_stack_r1").Item,
            PropertyManager.GetDouble("class_ability_resonance_per_stack_r2").Item,
            PropertyManager.GetDouble("class_ability_resonance_per_stack_r3").Item);

        /// <summary>
        /// Adds one Resonance stack (up to the cap) and refreshes the idle-expiry timer. Resets the pool
        /// first if the window had already lapsed since the last stack.
        ///
        /// LOOKS UP ITS OWN RANK AND SILENTLY DOES NOTHING WHEN UNOWNED, so the call sites need no rank
        /// lookup - the same reason TryGrantBloodCharge takes no rank. This one is called from three
        /// different damage sites, and "does the caster own Resonance" is not a question any of them should
        /// have to answer to stay correct.
        ///
        /// ONE STACK PER LANDED MAGIC HIT, NOT PER CAST (SPELLSWORD-DESIGN sec 5d). That is the deliberate
        /// opposite of Blood Charge, which is once per cast precisely so a multi-projectile spell cannot top
        /// its pool off in one go. Here it can, and should: a 9-projectile Ring proc connecting with a pack
        /// fills the whole 5-stack pool in one shot. Per the user's stated intent for the class - "I want
        /// this class to be a firework" - the burst is the point. WHAT BOUNDS IT is the pair of the 5-stack
        /// cap and the 6 second window: the ceiling is +10/15/20% by rank however the stacks were earned,
        /// and holding it requires landing magic damage at least every 6 seconds. Sec 5d is explicit that
        /// the short window, not the earn rate, is the brake.
        ///
        /// ANNOUNCED AT THE PEAK ONLY, matching Frenzy and Nether Rush rather than Blood Charge. Blood
        /// Charge announces every gain because its pool has no tell at all until Exsanguinate spends it, and
        /// it is capped at one gain per cast so the spam is bounded by the cap. Neither holds here: five
        /// stacks can arrive from a single Ring proc within one tick, so a per-gain line would be five lines
        /// at once.
        /// </summary>
        public void AddResonanceStack()
        {
            if (!TryGetClassAbility(ClassAbilityId.Resonance, out var rank))
                return;

            var cap = ResonanceStackCap();

            if (cap <= 0)
                return;

            var now = Time.GetUnixTime();

            if (ResonanceExpired(now))
                resonanceStacks = 0;

            resonanceLastAddTime = now;

            if (resonanceStacks >= cap)
                return;

            resonanceStacks++;

            if (resonanceStacks != cap)
                return;

            var pct = (int)Math.Round(cap * ResonancePerStack(rank) * 100);
            SendClassAbilityBuffMessage($"Your resonance reaches its peak! (+{pct}% magic damage)");
            ApplyVisualEffects(PlayScript.EnchantUpBlue);
        }

        /// <summary>
        /// Resonance's multiplier on this player's magic damage (1.0 = none): 1 + stacks * 2/3/4% by rank.
        /// Lazily expires the pool first, and returns 1.0 when the ability is unowned or the pool is empty,
        /// so every call site can multiply by it unconditionally.
        ///
        /// READ AT THREE DISJOINT SITES, WHICH IS BLOOD PRICE'S WIRING AND NOT AN ACCIDENT. There is no
        /// single choke point for "magic damage" in this codebase: war and void projectiles resolve in
        /// SpellProjectile.CalculateDamage's war/void branch (GetClassAbilitySpellDamageMod), the two life
        /// projectiles resolve in that method's LifeProjectile branch
        /// (ApplyLifeProjectileClassAbilityDamage), and Harm resolves entirely outside SpellProjectile
        /// (ApplyHarmClassAbilityDamage). Blood Price is the school-agnostic precedent and rides exactly
        /// those three; Resonance rides them for the same reason, which also delivers sec 5d's requirement
        /// that the entry be school-agnostic across War, Life and Void.
        ///
        /// THE VOID DoT TICK SITE IS DELIBERATELY EXCLUDED. Nether damage-over-time ticks are scaled in
        /// EnchantmentManager.ApplyDamageTick through GetWitheringVoidDotMod, and Resonance is not read
        /// there - the same exclusion Blood Price already makes. A 6 second window against a DoT that ticks
        /// for far longer would snapshot whatever stack count happened to be live at an arbitrary tick, so
        /// the same DoT would do different damage depending on when each tick fell relative to the caster's
        /// unrelated melee swings. That is not a balance judgement about DoTs; it is that the mechanic has
        /// no coherent meaning on a multi-tick effect. It also keeps this off Withering's axis.
        ///
        /// Sundermark's vulnerability is likewise untouched, because it is a debuff rather than damage
        /// (sec 5d); it is not applied at any of these three sites, so nothing is needed to exclude it.
        /// </summary>
        public float GetResonanceMagicDamageMod()
        {
            if (!TryGetClassAbility(ClassAbilityId.Resonance, out var rank))
                return 1.0f;

            if (resonanceStacks == 0)
                return 1.0f;

            if (ResonanceExpired(Time.GetUnixTime()))
            {
                resonanceStacks = 0;
                return 1.0f;
            }

            var stacks = Math.Min(resonanceStacks, ResonanceStackCap());

            // Harmonics equipment mod (MACHINERY): +% magic damage PER STACK, added to the ability's own
            // per-stack rate inside the ramp. Ownership is already proven - TryGetClassAbility returned
            // above - so the plain read is correct and no GetMachineryEquipmentModValue gate is needed.
            return ResonanceAbility.DamageMultiplier(rank, stacks,
                PropertyManager.GetDouble("class_ability_resonance_per_stack_r1").Item,
                PropertyManager.GetDouble("class_ability_resonance_per_stack_r2").Item,
                PropertyManager.GetDouble("class_ability_resonance_per_stack_r3").Item,
                GetEquippedModValue(EquipmentModId.Harmonics));
        }

        // ---- Spellsurge (Spellsword T2): landed war procs raise your proc chance ----------------

        private int spellsurgeStacks;
        private double spellsurgeLastAddTime;

        private static double SpellsurgeWindowSeconds() =>
            PropertyManager.GetDouble("class_ability_spellsurge_window_seconds").Item;

        private bool SpellsurgeExpired(double now) => now - spellsurgeLastAddTime > SpellsurgeWindowSeconds();

        private static int SpellsurgeStackCap() =>
            (int)PropertyManager.GetLong("class_ability_spellsurge_max_stacks").Item;

        /// <summary>
        /// Adds one Spellsurge stack (up to the cap) and refreshes the decay timer. Resets the pool first if
        /// the window had already lapsed.
        ///
        /// CALLED BY THE THREE WAR PROC HANDLERS - Spellblade, Runeblade and Spellstorm - when their proc
        /// LANDS. Like AddResonanceStack it looks up its own ownership and silently does nothing when the
        /// player has not bought Spellsurge, so a proc handler never needs to ask.
        ///
        /// SUNDERMARK DELIBERATELY DOES NOT CALL THIS (SPELLSWORD-DESIGN sec 5e). Sundermark is the class's
        /// one ANY-WEAPON entry while the three war procs are light-weapon-only, so letting it feed the pool
        /// would hand a two-handed-mace build the light-weapon duelist's ramp. The exclusion is enforced by
        /// the Sundermark handler simply not calling this method - there is deliberately no source argument
        /// to get wrong here.
        ///
        /// THE FEEDBACK LOOP IS BOUNDED BY THE CAP, not by anything in this method. Stacks raise proc
        /// chance and are earned by proccing, so the mechanic feeds itself; the 5-stack cap is what makes
        /// the ceiling finite (+10 percentage points), and because stacks are only earned by proccing the
        /// steady state sits below the cap whenever the base chance is low. See SpellsurgeAbility.
        /// </summary>
        public void AddSpellsurgeStack()
        {
            if (!TryGetClassAbility(ClassAbilityId.Spellsurge, out var rank))
                return;

            var cap = SpellsurgeStackCap();

            if (cap <= 0)
                return;

            var now = Time.GetUnixTime();

            if (SpellsurgeExpired(now))
                spellsurgeStacks = 0;

            spellsurgeLastAddTime = now;

            if (spellsurgeStacks >= cap)
                return;

            spellsurgeStacks++;

            if (spellsurgeStacks != cap)
                return;

            // Quotes the SAME number GetSpellsurgeProcChanceBonus will hand the war procs, gear included -
            // the peak line would otherwise understate a Surge-modded player's actual peak.
            var points = (int)Math.Round(SpellsurgeAbility.ProcChanceBonus(rank, cap,
                PropertyManager.GetDouble("class_ability_spellsurge_per_stack").Item,
                GetEquippedModValue(EquipmentModId.Surge)) * 100);

            SendClassAbilityBuffMessage($"Your spellsurge peaks! (+{points}% proc chance)");
            ApplyVisualEffects(PlayScript.EnchantUpBlue);
        }

        /// <summary>
        /// Spellsurge's bonus to a proc chance, as a FRACTION to ADD to that chance (3 stacks at the 0.02
        /// default returns 0.06, i.e. +6 percentage points). Lazily expires the pool first, and returns 0.0
        /// when the ability is unowned or the pool is empty, so a proc handler can add it unconditionally.
        ///
        /// ADDITIVE, NOT MULTIPLICATIVE, and that is what makes "+10 percentage points at a full stack"
        /// literally true for all three war procs regardless of their very different base chances
        /// (Spellblade 20% -> 30%, Runeblade 15% -> 25%, Spellstorm 10% -> 20%; sec 5e). A multiplicative
        /// reading would have made the entry worth twice as much to Spellblade as to Spellstorm.
        /// </summary>
        public float GetSpellsurgeProcChanceBonus()
        {
            if (!TryGetClassAbility(ClassAbilityId.Spellsurge, out var rank))
                return 0.0f;

            if (spellsurgeStacks == 0)
                return 0.0f;

            if (SpellsurgeExpired(Time.GetUnixTime()))
            {
                spellsurgeStacks = 0;
                return 0.0f;
            }

            var stacks = Math.Min(spellsurgeStacks, SpellsurgeStackCap());

            // Surge equipment mod (MACHINERY): +pp of war-proc chance PER STACK, added to the ability's own
            // per-stack rate. Ownership is already proven - TryGetClassAbility returned above - so the plain
            // read is correct and no GetMachineryEquipmentModValue gate is needed.
            //
            // THIS IS THE ONLY PLACE SURGE IS READ FOR COMBAT, and that is deliberate. The three war proc
            // handlers take this method's result as their spellsurgeBonus argument, so one read reaches all
            // three; reading Surge again inside any of their Chance() helpers would double-count it
            // (DESIGN.md 2.4, the delegation rule). Their own Spellblade/Runeblade/Spellstorm mods are
            // separate registry rows and are added there instead.
            return SpellsurgeAbility.ProcChanceBonus(rank, stacks,
                PropertyManager.GetDouble("class_ability_spellsurge_per_stack").Item,
                GetEquippedModValue(EquipmentModId.Surge));
        }

        // ---- The class-ability proc latch: what lets Cascade recognise its own procs -------------

        /// <summary>
        /// TRUE only while this player is inside a class ability's proc cast. Read exactly once, in
        /// WorldObject_Magic.LaunchSpellProjectiles, to stamp SpellProjectile.IsClassAbilityProc on each
        /// projectile as it is constructed.
        ///
        /// WHY A LATCH AND NOT A PARAMETER. The proc handlers cast through Player.TryCastSpell, which
        /// returns void and threads six arguments down to LaunchSpellProjectiles before a projectile object
        /// exists - so a handler has nothing to set a flag on, and adding a seventh argument would touch
        /// every overload and every caller of the general cast path for one class's benefit.
        ///
        /// NO IN-FLIGHT DRIFT, unlike bloodPriceDamageMod and the life-projectile stamp, which are read at
        /// the DAMAGE site and therefore reflect the caster's most recent cast rather than the one that
        /// launched the projectile. This latch is read while the projectile is being BUILT, inside the same
        /// synchronous call the handler wrapped, so the value is exact by construction.
        /// </summary>
        public bool ClassAbilityProcCastActive { get; private set; }

        /// <summary>
        /// Runs one class-ability proc cast with <see cref="ClassAbilityProcCastActive"/> armed, so every
        /// projectile that cast launches is stamped as a class-ability proc. Used by the Spellsword war proc
        /// handlers (Spellblade, Runeblade, Spellstorm), and available to any future ability that needs its
        /// procs to be distinguishable from an ordinary cast.
        ///
        /// TAKES THE CAST AS A DELEGATE so the latch cannot be left armed: the finally clause disarms it
        /// even if the cast throws. A pair of public Arm/Disarm methods would work exactly as well right up
        /// until the first early return between them, after which every subsequent spell that player cast
        /// would be mislabelled a proc - a bug that would show up only as Cascade firing off ordinary casts.
        ///
        /// NOT REENTRANT BY DESIGN: nesting one proc cast inside another would leave the inner call's
        /// finally disarming the latch for the outer one. Nothing does that today (a proc cast is one
        /// synchronous cast), and Cascade's own children are stamped directly rather than through here.
        /// </summary>
        public void CastClassAbilityProc(Action cast)
        {
            if (cast == null)
                return;

            ClassAbilityProcCastActive = true;

            try
            {
                cast();
            }
            finally
            {
                ClassAbilityProcCastActive = false;
            }
        }
    }
}
