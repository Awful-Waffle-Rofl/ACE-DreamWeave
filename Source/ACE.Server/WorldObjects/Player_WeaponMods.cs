using ACE.Common;
using ACE.Entity.Enum;
using ACE.Server.Entity;
using ACE.Server.Network.GameMessages.Messages;
using ACE.Server.WeaponMods;

namespace ACE.Server.WorldObjects
{
    /// <summary>
    /// The Tier B weapon-mod combat layer. Tier A needed none of this - every v1 modifier set a native
    /// property the server already read - so this file is where v2's seven modifiers actually happen.
    ///
    /// THERE IS NO DISPATCHER, AND THAT IS THE ESTABLISHED SHAPE. EquipmentModHookKind next door reads like a
    /// dispatch table and is not one; it is documentation. Effects in this codebase are hand-wired at the
    /// specific site that owns the numbers - Player_EquipmentMods.cs does exactly this for the seven rank-0
    /// gear mods - so each method below is called from one named core site and nothing routes by enum.
    ///
    /// THE WEAPON-ONLY RULE (HARD, AND THE EASIEST THING TO GET WRONG HERE). A weapon mod lives on ONE item:
    /// the weapon. The armor system's <see cref="Creature.GetEquippedModValue"/> and
    /// <see cref="Creature.GetEquippedModPotencySum"/> SUM a mod's value across every equipped item, which is
    /// correct there and would be a multi-fold overcount here - a player wearing twelve pieces could stack
    /// Life Leech off a helmet. Nothing in this file may call either of them. The accessors below take exactly
    /// one item, named so the distinction cannot be misread:
    ///
    ///   <see cref="GetWeaponOnlyModValue"/> - the equipped melee/missile weapon (GetEquippedWeapon)
    ///   <see cref="GetCasterOnlyModValue"/> - the equipped WAND (GetEquippedWand)
    ///
    /// WHICH ACCESSOR, AND WHY IT IS TWO AND NOT ONE. GetEquippedWeapon() is melee-or-missile and NEVER
    /// returns the wand (Creature_Equipment.cs:112-116 is GetEquippedMeleeWeapon ?? GetEquippedMissileWeapon;
    /// a wand sits in EquipMask.Held and is reached only by GetEquippedWand at :141-144). So a caster-side
    /// effect that reads GetEquippedWeapon() is silently dead on every caster. Overload is caster-only and
    /// therefore reads the wand; the leeches and Ambush are on all three classes and therefore wire BOTH a
    /// physical site and a spell site, each reading its own carrier.
    ///
    /// GATING. Every read goes through <see cref="WeaponModCombat.ReadWeaponOnly"/>, which returns 0 while
    /// weapon_mods_enabled - the system's ONE gate, crafting and combat alike - is false. That makes the whole
    /// layer inert with the gate off even on a weapon that already carries a record, without a gate check per
    /// hook. Do not add one: a per-hook check is exactly the shape that lets one hook be forgotten.
    /// </summary>
    partial class Player
    {
        // ---------------- the weapon-only accessors ----------------

        /// <summary>
        /// A Tier B modifier's magnitude on the equipped MELEE OR MISSILE weapon, or 0. Reads that one item and
        /// nothing else - see the class remarks for why summing across equipped items would be wrong here.
        /// </summary>
        public double GetWeaponOnlyModValue(WeaponModId modId) =>
            WeaponModCombat.ReadWeaponOnly(GetEquippedWeapon(), modId);

        /// <summary>
        /// A Tier B modifier's magnitude on the equipped WAND, or 0. Separate from
        /// <see cref="GetWeaponOnlyModValue"/> because GetEquippedWeapon() never returns a wand, so a
        /// caster-side effect reading that accessor would be dead on every caster.
        /// </summary>
        public double GetCasterOnlyModValue(WeaponModId modId) =>
            WeaponModCombat.ReadWeaponOnly(GetEquippedWand(), modId);

        /// <summary>
        /// The one item a kill is attributed to: the melee/missile weapon if one is held, otherwise the wand.
        /// Second Wind is the only effect that fires on an event carrying no damage-type context, so it cannot
        /// know whether the killing blow was a swing or a cast and has to resolve a single carrier.
        ///
        /// The precedence is the engine's own (Creature_Equipment.GetEquippedMainHand: melee, then missile,
        /// then held), and it is a CHOICE rather than a sum: a player somehow holding both a sword and a wand
        /// gets the sword's Second Wind, never the two added together.
        /// </summary>
        private double GetKillingWeaponModValue(WeaponModId modId)
        {
            var physical = GetWeaponOnlyModValue(modId);

            return physical > 0.0 ? physical : GetCasterOnlyModValue(modId);
        }

        // ---------------- carried sub-point leech remainders ----------------

        // Transient runtime state for the three leeches' fractional carry, one per vital. Deliberately NOT
        // persisted, matching bloodlustGearCarry in Player_EquipmentMods.cs and the frenzy / nether rush stack
        // state in Player_ClassAbilityBuffs: each is a sub-point remainder worth less than one point of a
        // vital, so losing it is beneath notice. All access is on the player's landblock thread (the outgoing
        // damage path and the spell-hit path), so no locking is needed - the same threading assumption the
        // rest of the combat runtime state relies on.
        //
        // Lifetime, stated rather than assumed: instance fields on Player, so they reset when the Player
        // object is rebuilt (logout/login) and SURVIVE a landblock transfer, which moves the existing object.
        private double lifeLeechCarry;
        private double manaLeechCarry;
        private double staminaLeechCarry;

        // ---------------- physical hits ----------------

        /// <summary>
        /// The physical half of Ambush and the three leeches. Called from Player.DamageTarget immediately
        /// after <see cref="ApplyEquipmentModOutgoingDamage"/>, under the same preconditions the class-ability
        /// dispatch uses (a damaging hit against a monster), so all three layers behave identically. That site
        /// covers melee, missile and multishot - spells never route through it, which is why the leeches and
        /// Ambush each need a second wiring at the spell sites below.
        ///
        /// ORDER INSIDE THIS METHOD MATTERS. Ambush multiplies the damage FIRST, so the leeches take their
        /// fraction of the boosted number. That matches how a player reads it - the leech is a fraction of the
        /// damage the combat log reports - and it is the same ordering the class-ability Bloodlust gets by
        /// running after the damage modifiers in its own dispatch.
        /// </summary>
        public void ApplyWeaponModOutgoingDamage(Creature target, DamageEvent damageEvent)
        {
            // same preconditions as ApplyOutgoingDamageClassAbilities: PvE only, damaging hits only
            if (damageEvent == null || !damageEvent.HasDamage || target == null || target is Player)
                return;

            // Ambush: conditional damage against a target that is still untouched. "First strike" needs no
            // per-target state - a target at full health IS one that has not been hit yet.
            var ambush = GetWeaponOnlyModValue(WeaponModId.Ambush);

            if (ambush > 0.0 && WeaponModCombat.IsFullHealth(target))
                damageEvent.Damage *= (float)WeaponModCombat.DamageMultiplier(ambush);

            ApplyWeaponModLeeches(damageEvent.Damage, GetWeaponOnlyModValue(WeaponModId.LifeLeech),
                GetWeaponOnlyModValue(WeaponModId.ManaLeech), GetWeaponOnlyModValue(WeaponModId.StaminaLeech));
        }

        // ---------------- spell hits ----------------

        /// <summary>
        /// Ambush's spell half, as a multiplier on a war/void projectile's final damage. Composed in
        /// SpellProjectile.CalculateDamage beside the class-ability spell-damage multiplier, which is the only
        /// place a spell's damage can be scaled before it lands.
        ///
        /// Reads the WAND, not GetEquippedWeapon() - see the class remarks. Returns 1.0 for everything else,
        /// so the call site needs no condition of its own.
        /// </summary>
        public float GetWeaponModSpellDamageMod(Creature target)
        {
            var ambush = GetCasterOnlyModValue(WeaponModId.Ambush);

            if (ambush <= 0.0 || !WeaponModCombat.IsFullHealth(target))
                return 1.0f;

            return (float)WeaponModCombat.DamageMultiplier(ambush);
        }

        /// <summary>
        /// The leeches' spell half. Called from SpellProjectile.OnCollideObject once a projectile of this
        /// player's has damaged a monster, with the damage figure that site is about to apply.
        ///
        /// DELIBERATELY NOT SCHOOL-GATED, unlike the class-ability spell-hit dispatch beside it, which is war
        /// magic only because its handlers radiate and recast war projectiles. A leech is a fraction of damage
        /// dealt and has no school in it, so a void nuke leeches exactly like a war one.
        ///
        /// A LEECH ON A MELEE WEAPON DOES NOT FIRE HERE, and that is the intended reading of the class column:
        /// this reads the wand, so a sword's Life Leech leeches on swings and a wand's Life Leech leeches on
        /// casts. Neither reaches the other's hits.
        /// </summary>
        public void ApplyWeaponModSpellHit(Creature target, float damage)
        {
            if (target == null || target is Player || damage <= 0.0f)
                return;

            ApplyWeaponModLeeches(damage, GetCasterOnlyModValue(WeaponModId.LifeLeech),
                GetCasterOnlyModValue(WeaponModId.ManaLeech), GetCasterOnlyModValue(WeaponModId.StaminaLeech));
        }

        /// <summary>
        /// Pays out the three leeches for one hit of <paramref name="damage"/>. Each vital carries its own
        /// sub-point remainder (see the carry fields above) so a fraction of a point is never rounded away.
        /// UpdateVitalDelta clamps to the vital's maximum, so an over-leech at full health is harmless.
        /// </summary>
        private void ApplyWeaponModLeeches(float damage, double lifeFraction, double manaFraction, double staminaFraction)
        {
            if (damage <= 0.0f)
                return;

            if (lifeFraction > 0.0)
            {
                var gained = WeaponModCombat.AccrueVital(lifeLeechCarry,
                    WeaponModCombat.LeechAmount(damage, lifeFraction), out lifeLeechCarry);

                if (gained > 0)
                    UpdateVitalDelta(Health, gained);
            }

            if (manaFraction > 0.0)
            {
                var gained = WeaponModCombat.AccrueVital(manaLeechCarry,
                    WeaponModCombat.LeechAmount(damage, manaFraction), out manaLeechCarry);

                if (gained > 0)
                    UpdateVitalDelta(Mana, gained);
            }

            if (staminaFraction > 0.0)
            {
                var gained = WeaponModCombat.AccrueVital(staminaLeechCarry,
                    WeaponModCombat.LeechAmount(damage, staminaFraction), out staminaLeechCarry);

                if (gained > 0)
                    UpdateVitalDelta(Stamina, gained);
            }
        }

        // ---------------- attack speed ----------------

        /// <summary>
        /// Quickening's attack-speed multiplier (1.0 = none). Composed into
        /// <see cref="ApplyClassAbilityAttackSpeed"/>'s multiplicative product, which means it lands INSIDE
        /// that method's single ceiling clamp against class_ability_attack_speed_ceiling.
        ///
        /// THAT PLACEMENT IS THE POINT, not an implementation detail. The attack-speed axis saturates: the 2.0
        /// base is MaxAttackSpeed, a private static no config can raise, and the ceiling is the only thing
        /// bounding what sits above it. Applying Quickening after the clamp would let it push a gear-stacked
        /// build past a ceiling every other term on the axis respects.
        ///
        /// Melee and missile only, and the accessor enforces it without a condition: GetEquippedWeapon()
        /// returns null for a wand-only loadout, and Quickening is not in the caster pool.
        /// </summary>
        public float GetWeaponModAttackSpeedMod()
        {
            var quickening = GetWeaponOnlyModValue(WeaponModId.Quickening);

            if (quickening <= 0.0)
                return 1.0f;

            return (float)WeaponModCombat.DamageMultiplier(quickening);
        }

        // ---------------- mana cost ----------------

        /// <summary>
        /// Overload's roll: TRUE when this cast should cost no mana at all. Read in
        /// <see cref="CalculateManaUsage(ACE.Server.Entity.CastingPreCheckStatus, Spell, WorldObject, WorldObject, out uint)"/>
        /// (Player_Magic.cs, NOT the same-named Creature_Magic overload) right after the class-ability mana
        /// surcharge, so a free cast is free of the surcharge too.
        ///
        /// READS THE WAND (GetEquippedWand), and the comment is here rather than implied because the mistake is
        /// silent: GetEquippedWeapon() is melee-or-missile only and would make this modifier - which is in the
        /// CASTER pool and no other - permanently dead.
        /// </summary>
        public bool RollWeaponModOverload() =>
            WeaponModCombat.RollsFree(GetCasterOnlyModValue(WeaponModId.Overload), ThreadSafeRandom.Next(0.0f, 1.0f));

        // ---------------- killing blow ----------------

        /// <summary>
        /// Second Wind: a killing blow restores a fraction of the player's MAXIMUM health, stamina and mana.
        /// Called from Creature.OnDeath beside <see cref="ApplyCreatureDeathClassAbilities"/>, which is the
        /// precedent site (NetherBloomAbility runs there); OnDeath's onDeathEntered guard makes it fire exactly
        /// once per death.
        ///
        /// Monster victims only - players (PvP), the player's own pets and self-kills are filtered here, the
        /// same exclusions the class-ability death dispatch owns.
        /// </summary>
        public void ApplyWeaponModCreatureDeath(Creature victim)
        {
            if (victim == null || victim == this || victim is Player || victim is Pet)
                return;

            var fraction = GetKillingWeaponModValue(WeaponModId.SecondWind);

            if (fraction <= 0.0)
                return;

            var health = WeaponModCombat.RestoreFromMax(Health.MaxValue, fraction);
            var stamina = WeaponModCombat.RestoreFromMax(Stamina.MaxValue, fraction);
            var mana = WeaponModCombat.RestoreFromMax(Mana.MaxValue, fraction);

            var restored = 0;

            if (health > 0)
                restored += UpdateVitalDelta(Health, health);

            if (stamina > 0)
                restored += UpdateVitalDelta(Stamina, stamina);

            if (mana > 0)
                restored += UpdateVitalDelta(Mana, mana);

            // only speak when something actually landed: at full health, stamina and mana every delta clamps
            // to zero and a message would be noise on every kill
            if (restored > 0)
                Session?.Network.EnqueueSend(new GameMessageSystemChat("You catch your second wind.", ChatMessageType.Broadcast));
        }
    }
}
