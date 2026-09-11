using System;

using ACE.Entity.Enum;
using ACE.Server.ClassAbilities;
using ACE.Server.ClassAbilities.Abilities;
using ACE.Server.Entity;
using ACE.Server.EquipmentMods;
using ACE.Server.Managers;

namespace ACE.Server.WorldObjects
{
    /// <summary>
    /// The equipment-mod combat layer: the STANDALONE half of the hybrid standalone rule (plan decision 2).
    ///
    /// Every mod's value composes INSIDE its own class ability's parenthesis - (1 + rankBonus + rider +
    /// gearMod) - so a mod is on the same additive axis as the ability it belongs to and never becomes a
    /// separate multiplier. Most mods reach that parenthesis through a getter the core site already calls
    /// (Player_ClassAbilityBuffs / Player_ClassAbilities), which is why those needed no combat-site edit at
    /// all. Seven mods cannot: their abilities are IOutgoingDamageAbility handlers, and the hook cache only
    /// contains handlers the player has LEARNED, so at rank 0 nothing runs. This file is their rank-0 half.
    ///
    /// THE NO-DOUBLE-APPLY INVARIANT. Exactly one of two paths applies each of those seven mods per hit:
    ///   - ability owned  -> the handler folds the mod into its own expression (see each ability class)
    ///   - ability unowned -> the mirror below applies it, with the ability's terms at zero
    /// <see cref="GetStandaloneEquipmentModValue"/> is the gate: it returns 0 whenever the linked ability is
    /// live, so this file can never stack on top of a handler that already counted the mod. The reverse gate,
    /// <see cref="GetMachineryEquipmentModValue"/>, exists for machinery mods read somewhere their owning
    /// ability's rank was not already proven.
    ///
    /// The rank-0 expressions here deliberately re-use each ability's own pure math (LowHealthBonus,
    /// DistanceBonus, IsExecuteRange, SchedulePoisonProc, ApplyLifesteal) rather than restating it, so a
    /// modded player without the ability gets the identical curve, condition and feedback - just smaller.
    /// </summary>
    partial class Player
    {
        /// <summary>
        /// A mod's equipped value, but ONLY when its linked class ability is NOT active for this player -
        /// i.e. the ability's own computation did not (and will not) fold the mod in. Returns 0 when the
        /// ability is owned, which is what makes the standalone paths in this file safe to run
        /// unconditionally.
        ///
        /// Note this correctly covers a server running equipment mods with class abilities DISABLED: no
        /// handler runs in that configuration, TryGetClassAbility reports "not owned", and the full mod value
        /// is returned here - so equipment mods do not silently depend on the class ability tunable.
        /// </summary>
        public double GetStandaloneEquipmentModValue(EquipmentModId modId)
        {
            if (!EquipmentModRegistry.TryGet(modId, out var definition))
                return 0.0;

            if (TryGetClassAbility(definition.LinkedAbility, out _))
                return 0.0;

            return GetEquippedModValue(modId);
        }

        // Transient runtime state for the Bloodlust equipment mod's fractional heal carry. Deliberately NOT
        // persisted, matching the frenzy / nether rush stack state in Player_ClassAbilityBuffs: it is a
        // sub-point remainder worth less than one health, so losing it is beneath notice. All access is on
        // the player's landblock thread (the outgoing-damage path), so no locking is needed - the same
        // threading assumption the rest of the class-ability runtime state relies on.
        //
        // Lifetime, stated precisely rather than assumed: this is an instance field on Player, so it is reset
        // when the Player object is rebuilt - i.e. on logout/login. It SURVIVES a landblock transfer, because
        // that moves the existing Player object rather than rebuilding it.
        private double bloodlustGearCarry;

        /// <summary>
        /// Accrues one hit's exact fractional Bloodlust gear lifesteal and returns the whole points to heal
        /// now, carrying the remainder to the next hit. See BloodlustAbility.AccrueHeal for the math and why
        /// this mod accumulates instead of taking a potency floor.
        /// </summary>
        public int AccrueBloodlustGearHeal(double exactHeal)
        {
            var heal = BloodlustAbility.AccrueHeal(bloodlustGearCarry, exactHeal, out var carry);

            bloodlustGearCarry = carry;

            return heal;
        }

        /// <summary>
        /// A machinery mod's equipped value, but ONLY when its linked class ability IS active - machinery
        /// mods amplify an ability's own machinery and are inert without it. Most machinery call sites sit
        /// inside a handler that already proved ownership and read the value directly; this is for the few
        /// read from shared state (Lingering Fury's stack-expiry window) where ownership is not implied.
        /// </summary>
        public double GetMachineryEquipmentModValue(EquipmentModId modId)
        {
            if (!EquipmentModRegistry.TryGet(modId, out var definition))
                return 0.0;

            if (!TryGetClassAbility(definition.LinkedAbility, out _))
                return 0.0;

            return GetEquippedModValue(modId);
        }

        /// <summary>
        /// Applies the outgoing-damage equipment mods whose class ability the player has NOT learned, so the
        /// magnitude still stands alone at rank 0. Called from Player.DamageTarget immediately after
        /// <see cref="ApplyOutgoingDamageClassAbilities"/>, under the same preconditions the class-ability
        /// dispatch uses (a damaging hit against a monster), so the two halves behave identically.
        ///
        /// Because this runs after the ability dispatch, an OWNED Bloodlust computes its lifesteal from a
        /// damage figure that does not yet include rank-0 percent mods. The discrepancy is a fraction of a
        /// point of healing (0.5% lifesteal applied to a 2% damage delta) and was preferred over splitting
        /// this into two core call sites.
        ///
        /// Each percent mod is applied as its own factor, mirroring exactly how its ability handler would
        /// have applied it - different abilities are already separate factors in this engine, so a mod is
        /// additive with its OWN ability and composes with other abilities the way abilities compose with
        /// each other.
        /// </summary>
        public void ApplyEquipmentModOutgoingDamage(Creature target, DamageEvent damageEvent)
        {
            if (!PropertyManager.GetBool("equipment_mods_enabled").Item)
                return;

            // same preconditions as ApplyOutgoingDamageClassAbilities: PvE only, damaging hits only
            if (damageEvent == null || !damageEvent.HasDamage || target == null || target is Player)
                return;

            if (damageEvent.CombatType == CombatType.Missile)
            {
                // Deadeye: unconditional missile percent
                var deadeye = GetStandaloneEquipmentModValue(EquipmentModId.Deadeye);
                if (deadeye > 0.0)
                    damageEvent.Damage *= (float)(1.0 + deadeye);

                // Long Draw: the ability's own distance ramp, with the mod value as the peak
                var longDraw = GetStandaloneEquipmentModValue(EquipmentModId.LongDraw);
                if (longDraw > 0.0 && Location != null && target.Location != null)
                {
                    var bonus = LongDrawAbility.DistanceBonus(Location.DistanceTo(target.Location), 1, longDraw,
                        PropertyManager.GetDouble("class_ability_longdraw_min_distance").Item,
                        PropertyManager.GetDouble("class_ability_longdraw_max_distance").Item);

                    if (bonus > 0.0)
                        damageEvent.Damage *= (float)(1.0 + bonus);
                }
            }
            else if (damageEvent.CombatType == CombatType.Melee)
            {
                // Savage Blows: unconditional melee percent. No stamina is charged - the surcharge is the
                // ABILITY's cost mechanic, and a player who never learned it has nothing to pay it with.
                var savageBlows = GetStandaloneEquipmentModValue(EquipmentModId.SavageBlows);
                if (savageBlows > 0.0)
                    damageEvent.Damage *= (float)(1.0 + savageBlows);

                // Blood Fury's rank-0 mirror lived here until 2026-08-17. The ability was retired and mod 18 /
                // PropertyFloat 8117 was repurposed as Break Armor's proc-chance MACHINERY mod, which has no
                // rank-0 half by definition - it is read inside the Break Armor handler, so there is nothing
                // to mirror here. See EquipmentModRegistry's Break Armor row.

                // Executioner: the ability's own execute-range condition
                var executioner = GetStandaloneEquipmentModValue(EquipmentModId.Executioner);
                if (executioner > 0.0 &&
                    ExecutionerAbility.IsExecuteRange(target, PropertyManager.GetDouble("class_ability_executioner_hp_fraction").Item))
                {
                    damageEvent.Damage *= (float)(1.0 + executioner);
                }
            }

            // Venom: a flat poison proc on any landed WEAPON hit (melee, missile, multi-shot arrows and
            // Riposte counters all route through DamageTarget). Fires the ability's own deferred proc so the
            // damage, timing and combat line are identical - just smaller.
            if (damageEvent.Weapon != null)
            {
                var venom = GetStandaloneEquipmentModValue(EquipmentModId.Venom);
                if (venom > 0.0)
                {
                    // the ability contributes nothing here (rank 0 by construction), so this is the gear sum
                    // alone, rounded half-up once. Venom's MinPotency floor of 0.25 guarantees a single
                    // equipped roll is worth at least 0.5 flat and therefore always at least +1 damage.
                    var poison = PoisonWeaponAbility.ComputePoisonDamage(0.0, venom);
                    if (poison > 0)
                        PoisonWeaponAbility.SchedulePoisonProc(this, target, poison);
                }
            }

            // Bloodlust: heal a fraction of the (now fully boosted) melee damage dealt. The ability
            // contributes nothing here (rank 0 by construction), so this is the gear fraction alone, which
            // accumulates its sub-point remainder across hits rather than rounding away every time.
            if (damageEvent.CombatType == CombatType.Melee)
            {
                var bloodlust = GetStandaloneEquipmentModValue(EquipmentModId.Bloodlust);
                if (bloodlust > 0.0)
                    BloodlustAbility.ApplyLifesteal(this, damageEvent.Damage, 0.0, bloodlust);
            }
        }

        /// <summary>
        /// The rank-0 half of the Thorns mod: reflects when the player never learned Thorns, so the ability's
        /// incoming-damage hook never fired. Called from Player.TakeDamage immediately after
        /// <see cref="ApplyIncomingDamageClassAbilities"/>, mirroring its preconditions.
        ///
        /// No double-reflect is possible: this returns immediately when Thorns is owned (the hook already
        /// reflected), and a blocked or parried hit never reaches TakeDamage at all - it is resolved to zero
        /// damage inside DamageEvent, where OnClassAbilityAttackAvoided calls ApplyThornsReflect directly.
        /// </summary>
        public void ApplyEquipmentModIncomingDamage(WorldObject source, DamageType damageType)
        {
            if (!PropertyManager.GetBool("equipment_mods_enabled").Item)
                return;

            // same attacker filter as ApplyIncomingDamageClassAbilities: monsters only, never PvP or self
            if (source is not Creature attacker || attacker is Player || attacker == this || attacker.IsDead)
                return;

            if (TryGetClassAbility(ClassAbilityId.Thorns, out _))
                return;

            if (GetEquippedModValue(EquipmentModId.Thorns) <= 0.0)
                return;

            // ApplyThornsReflect owns the shield requirement, the reflect math and the messaging, and now
            // composes the mod at rank 0 - so the mod reflects exactly the way the ability does
            ApplyThornsReflect(attacker, damageType, 1.0);
        }
    }
}
