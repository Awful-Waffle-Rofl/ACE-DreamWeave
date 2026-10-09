using ACE.Common;
using ACE.Entity.Enum;
using ACE.Server.ClassAbilities.Abilities;
using ACE.Server.Entity;
using ACE.Server.Network.GameEvent.Events;
using ACE.Server.Network.GameMessages.Messages;
using ACE.Server.Network.Structure;
using ACE.Server.Pvp.Rules;
using ACE.Server.WeaponMods;

namespace ACE.Server.WorldObjects
{
    /// <summary>
    /// The Tier B weapon-mod combat layer. Tier A needed none of this - every v1 modifier set a native
    /// property the server already read - so this file is where the surviving v2 modifiers (Ambush,
    /// Quickening, Second Wind) actually happen. The three leeches and Overload were retired 2026-08-17
    /// in the catalog v4 pass; Siphon replaced them at the same two call sites in the v4 hook phase, which
    /// also wired Mana Well and Cleanse onto the killing-blow site beside Second Wind.
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
    /// effect that reads GetEquippedWeapon() is silently dead on every caster. Ambush is on all three classes
    /// and therefore wires BOTH a physical site and a spell site, each reading its own carrier.
    ///
    /// GATING. Every read goes through <see cref="WeaponModCombat.ReadWeaponOnly"/>, which returns 0 while
    /// weapon_mods_enabled - the system's ONE gate, crafting and combat alike - is false. That makes the whole
    /// layer inert with the gate off even on a weapon that already carries a record, without a gate check per
    /// hook. Do not add one: a per-hook check is exactly the shape that lets one hook be forgotten.
    ///
    /// The same accessor also returns 0 while the wielder's weapon mods are SUPPRESSED
    /// (<see cref="WeaponModSuppressed"/>, today the PK facet; owner ruling 2026-09-25). That is a per-wielder
    /// condition, not a second gate, and it lives in the accessor for the same reason. Tier A, which lives in
    /// native item properties, is suppressed at its read sites instead - see WeaponModSuppression.
    /// </summary>
    partial class Player
    {
        /// <summary>
        /// TRUE while this player's weapon mods do not work: every Tier B read returns 0 and every Tier A amount
        /// is taken back off at its read site. The PK facet rule OR the PvP arena weapon-mod mask; a further
        /// ruleset ORs its term in through <see cref="WeaponModSuppression.Suppressed"/>, never here, so the
        /// pure rule stays testable without a live Player.
        /// </summary>
        public bool WeaponModSuppressed => WeaponModSuppression.Suppressed(IsPkFacetRuleActive, IsPvpWeaponModMaskActive);

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

        // ---------------- physical hits ----------------

        /// <summary>
        /// The physical half of Ambush. Called from Player.DamageTarget immediately after
        /// <see cref="ApplyEquipmentModOutgoingDamage"/>, under the same preconditions the class-ability
        /// dispatch uses (a damaging hit against a monster), so all layers behave identically. That site covers
        /// melee, missile and multishot - spells never route through it, which is why Ambush needs a second
        /// wiring at the spell sites below.
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

            // Siphon, physical half. Reads the MELEE/MISSILE weapon; the spell half in
            // ApplyWeaponModSpellHit reads the wand. Both funnel into the one helper below.
            TryWeaponModSiphon(target, GetWeaponOnlyModValue(WeaponModId.Siphon));
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
        /// The spell-hit hook site. Called from SpellProjectile.OnCollideObject once a projectile of this
        /// player's has damaged a monster, with the damage figure that site is about to apply.
        ///
        /// Reads the WAND, not GetEquippedWeapon() - see the class remarks. Siphon is on all three classes, so
        /// it needs this wiring as well as the physical one; a caster-side effect reading GetEquippedWeapon()
        /// is silently dead on every caster.
        /// </summary>
        public void ApplyWeaponModSpellHit(Creature target, float damage)
        {
            if (target == null || target is Player || damage <= 0.0f)
                return;

            // Siphon, spell half. One projectile, one roll: SpellProjectile.OnCollideObject fires once per
            // projectile, so a multi-projectile volley rolls per projectile and each roll can steal at most
            // one enchantment. The helper never loops.
            TryWeaponModSiphon(target, GetCasterOnlyModValue(WeaponModId.Siphon));
        }

        /// <summary>
        /// Siphon: a landed hit has a chance to STRIP one of the target's beneficial enchantments and apply the
        /// same spell to the wielder. The one body behind both hook sites above - the only thing that differs
        /// between them is which item the magnitude was read off, which is why the chance arrives as a
        /// parameter rather than being read here.
        ///
        /// BENEFICIAL ENCHANTMENTS ONLY, and for the reason DispellingEdgeAbility spells out at its own
        /// selection call: stripping a HARMFUL enchantment off an enemy would remove the party's own debuffs.
        /// "Steal an enchantment" can only mean one of the enemy's own buffs. Selection reuses
        /// <see cref="DispellingEdgeAbility.SelectDispellable"/> verbatim rather than restating its two
        /// exclusions, so Siphon cannot drift from the shipped dispel rule.
        ///
        /// THE ITEM-SOURCED EXCLUSION IS RE-ASSERTED HERE rather than trusted. SelectDispellable already drops
        /// Duration == -1, and that is the only thing standing between this and RESURRECTING an aura an enemy
        /// gets from its equipment as a permanent, undispellable enchantment on the player - a Duration of -1
        /// survives BuildEntry untouched on the equip path and never expires. A second check costs one compare
        /// on a path that already did a dat lookup.
        ///
        /// FULL DURATION, NOT THE REMAINDER (repo-owner decision). The spell is applied to the wielder through
        /// the ordinary EnchantmentManager.Add path, so it lands at the spell's own duration exactly as if the
        /// player had been the target of a fresh cast, and it stacks/refreshes/surpasses by the same rules.
        /// Transferring the remaining duration would need a hand-built registry entry and would bypass all of
        /// that.
        ///
        /// LOCK DISCIPLINE. This writes TWO enchantment registries, and it never holds both objects' locks at
        /// once: every EnchantmentManager entry point takes its own WorldObject's BiotaDatabaseLock inside the
        /// single registry call and releases it before returning, so the target's read, the target's Dispel and
        /// the player's Add are three separate, non-overlapping critical sections. Nothing here wraps them in a
        /// lock of its own, and nothing should - a helper that held the target's lock across the player's Add
        /// would introduce the first lock-ordering hazard in this file.
        ///
        /// The Spell is resolved and validated BEFORE the Dispel, so a spell id the dat cannot resolve leaves
        /// the target's buff intact rather than deleting it into nothing.
        /// </summary>
        private void TryWeaponModSiphon(Creature target, double chance)
        {
            // magnitude first: nothing below this line runs on a weapon without Siphon, or with the gate off
            if (chance <= 0.0)
                return;

            if (target == null || target == this || PvpClassifier.IsPvp(this, target) || target is Pet || target.IsDead)
                return;

            if (!WeaponModCombat.RollsFree(chance, ThreadSafeRandom.Next(0f, 1f)))
                return;

            // Top layer per spell category, so a stack of the same buff counts once and the strongest is taken.
            var stealable = DispellingEdgeAbility.SelectDispellable(target.EnchantmentManager.GetEnchantments_TopLayer(EnchantmentTypeFlags.Beneficial));

            if (stealable.Count == 0)
                return;

            // Exactly ONE, chosen at random. Deliberately not a loop: one hit steals one enchantment even when
            // the hit came from a multishot volley or an AOE, and each hit rolls separately.
            var entry = stealable[ThreadSafeRandom.Next(0, stealable.Count - 1)];

            // see the remarks: never resurrect an item-sourced aura on the player
            if (entry == null || entry.Duration == -1 || entry.SpellId > short.MaxValue)
                return;

            var spell = new Spell(entry.SpellId);

            if (spell.NotFound)
                return;

            target.EnchantmentManager.Dispel(entry);

            var addResult = EnchantmentManager.Add(spell, this, null);

            if (addResult?.Enchantment != null)
                Session?.Network.EnqueueSend(new GameEventMagicUpdateEnchantment(Session, new Enchantment(this, addResult.Enchantment)));

            Session?.Network.EnqueueSend(new GameMessageSystemChat($"You siphon {spell.Name} from {target.Name}.", ChatMessageType.Magic));
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
        ///
        /// THIS OVERLOAD IS UNCONDITIONAL BY CONTRACT, and that is a requirement rather than a default. The
        /// /abilities chat readouts call it (AttackSpeedAbility.GetReadout and FrenzyAbility.GetReadout both
        /// compose it into the figure they print), so anything positional read here would make a player's
        /// printed attack speed change as they walked around. Panic Reload is positional, so it lives in the
        /// includeConditional overload below and reaches only the combat site.
        /// </summary>
        public float GetWeaponModAttackSpeedMod() => GetWeaponModAttackSpeedMod(false);

        /// <summary>
        /// <see cref="GetWeaponModAttackSpeedMod()"/> plus, when <paramref name="includeConditional"/> is set,
        /// the CONDITIONAL attack-speed weapon mods - currently just Panic Reload, whose condition is where
        /// the player is standing.
        ///
        /// Exactly one caller passes true: Player.ApplyClassAbilityAttackSpeed's combat overload, reached from
        /// Creature.GetAnimSpeed. Every readout path takes the parameterless overload above.
        /// </summary>
        public float GetWeaponModAttackSpeedMod(bool includeConditional)
        {
            var mod = 1.0f;

            var quickening = GetWeaponOnlyModValue(WeaponModId.Quickening);

            if (quickening > 0.0)
                mod *= (float)WeaponModCombat.DamageMultiplier(quickening);

            if (includeConditional)
                mod *= GetWeaponModPanicReloadMod();

            return mod;
        }

        /// <summary>
        /// Panic Reload's attack-speed multiplier (1.0 = none): a missile launcher carrying the record fires
        /// faster while a hostile creature is inside <see cref="WeaponModCombat.PanicReloadRange"/>.
        ///
        /// READS GetEquippedMissileWeapon() RATHER THAN GetEquippedWeapon(). Panic Reload is a missile-only
        /// row, so a melee weapon cannot carry it at roll time, but GetEquippedWeapon() is melee-first and
        /// would hand a melee weapon to the read on a dual-carrying loadout. Naming the carrier keeps the
        /// class restriction true at read time and not only at roll time.
        ///
        /// THE MAGNITUDE IS READ BEFORE THE SWEEP, deliberately: the proximity check allocates a
        /// visible-creature list, and this runs once per attack for every player, so a player not carrying the
        /// modifier must pay only the record read.
        ///
        /// It may sit on the same bow as Quickening, and the two multiply; both land inside
        /// ApplyClassAbilityAttackSpeed's single ceiling clamp, which is what bounds the axis.
        /// </summary>
        private float GetWeaponModPanicReloadMod()
        {
            var magnitude = WeaponModCombat.ReadWeaponOnly(GetEquippedMissileWeapon(), WeaponModId.PanicReload);

            if (magnitude <= 0.0)
                return 1.0f;

            if (!HasHostileWithinPanicReloadRange())
                return 1.0f;

            return (float)WeaponModCombat.DamageMultiplier(magnitude);
        }

        /// <summary>
        /// TRUE when at least one hostile creature is inside Panic Reload's radius. Breaks on the first match
        /// rather than counting.
        ///
        /// The distance measured is the engine's CYLINDER (edge to edge) distance, via
        /// PhysicsObj.get_distance_sq_to_object(obj, true) - the same call Player.CheckMonsters and the monster
        /// awareness sweeps use for their own ranges, not a centre-to-centre distance. The per-candidate
        /// decision itself lives in <see cref="WeaponModCombat.IsPanicReloadThreat"/> so it can be tested
        /// without a live Player.
        /// </summary>
        private bool HasHostileWithinPanicReloadRange()
        {
            var physics = PhysicsObj;

            if (physics?.ObjMaint == null)
                return false;

            foreach (var creature in physics.ObjMaint.GetVisibleObjectsValuesOfTypeCreature())
            {
                if (creature?.PhysicsObj == null)
                    continue;

                var distanceSq = physics.get_distance_sq_to_object(creature.PhysicsObj, true);

                if (WeaponModCombat.IsPanicReloadThreat(creature == this, creature is Player, creature is Pet,
                        creature.IsDead, creature.Attackable, distanceSq))
                    return true;
            }

            return false;
        }

        // ---------------- killing blow ----------------

        /// <summary>
        /// The killing-blow hook site. Called from Creature.OnDeath beside
        /// <see cref="ApplyCreatureDeathClassAbilities"/>, which is the precedent site (NetherBloomAbility runs
        /// there); OnDeath's onDeathEntered guard makes it fire exactly once per death.
        ///
        /// Monster victims only - players (PvP), the player's own pets and self-kills are filtered ONCE here,
        /// the same exclusions the class-ability death dispatch owns, and the three effects below inherit it.
        /// That single filter is why they are separate methods rather than one body: each has its own magnitude
        /// and its own early return, and a shared "no Second Wind, therefore return" would have made Mana Well
        /// and Cleanse silently conditional on a modifier they have nothing to do with.
        /// </summary>
        public void ApplyWeaponModCreatureDeath(Creature victim)
        {
            if (victim == null || victim == this || PvpClassifier.IsPvp(this, victim) || victim is Pet)
                return;

            ApplyWeaponModSecondWind();
            ApplyWeaponModManaWell();
            ApplyWeaponModCleanse();
        }

        /// <summary>
        /// Second Wind: a killing blow restores a fraction of the player's MAXIMUM health, stamina and mana.
        /// The victim filter lives in <see cref="ApplyWeaponModCreatureDeath"/>, the one caller.
        /// </summary>
        private void ApplyWeaponModSecondWind()
        {
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

        /// <summary>
        /// Mana Well: a killing blow pours FLAT mana points into every equipped item that has room for them.
        /// The victim filter lives in <see cref="ApplyWeaponModCreatureDeath"/>, the one caller.
        ///
        /// FLAT POINTS PER ITEM, NOT A FRACTION OF ANYTHING, and this is the only row in the catalog shaped
        /// that way (see the registry comment on the row and <see cref="WeaponModCombat.ManaWellTopUp"/>). The
        /// same number goes to each qualifying item; it is not a budget divided among them, which is where this
        /// deliberately parts company with ManaStone's ration loop next door.
        ///
        /// THE ITEM FILTER IS ManaStone's, verbatim (ManaStone.cs:124): ItemCurMana and ItemMaxMana both
        /// present and the current below the maximum. An item with no mana pool at all takes nothing rather
        /// than acquiring one.
        ///
        /// THERE IS NO SERVER PUSH FOR EQUIPPED-ITEM MANA and this method does not invent one. The client
        /// POLLS: GameActionQueryItemMana -> Player.HandleActionQueryItemMana -> WorldObject.QueryItemMana,
        /// which is the only thing that ever sends GameEventQueryItemManaResponse. ManaStone, the precedent for
        /// writing ItemCurMana on equipped items, likewise sends nothing but a chat line, so that is what this
        /// sends - one summary line, and only when something actually landed, matching Second Wind's rule
        /// above rather than speaking on every kill.
        /// </summary>
        private void ApplyWeaponModManaWell()
        {
            var magnitude = GetKillingWeaponModValue(WeaponModId.ManaWell);

            if (magnitude <= 0.0)
                return;

            if (!EquippedObjectsLoaded)
                return;

            var restored = 0;
            var items = 0;

            foreach (var item in EquippedObjects.Values)
            {
                if (item == null || !item.ItemCurMana.HasValue || !item.ItemMaxMana.HasValue || item.ItemCurMana >= item.ItemMaxMana)
                    continue;

                var topUp = WeaponModCombat.ManaWellTopUp(item.ItemCurMana.Value, item.ItemMaxMana.Value, magnitude);

                if (topUp <= 0)
                    continue;

                item.ItemCurMana += topUp;

                restored += topUp;
                items++;
            }

            if (restored > 0)
                Session?.Network.EnqueueSend(new GameMessageSystemChat($"Your weapon draws {restored:N0} points of mana into {items} of your equipped items.", ChatMessageType.Magic));
        }

        /// <summary>
        /// Cleanse: a killing blow has a chance to strip ONE harmful enchantment off the wielder, every caster's
        /// layer of it (see <see cref="DispellingEdgeAbility.SelectAllLayers"/>). The victim
        /// filter lives in <see cref="ApplyWeaponModCreatureDeath"/>, the one caller.
        ///
        /// THE MAGNITUDE IS A PROBABILITY, not an amount - MaxRoll is 1.00 and the row's display format reads
        /// "% chance", so it goes through <see cref="WeaponModCombat.RollsFree"/> and never into an arithmetic
        /// helper.
        ///
        /// THE ROLL COMES BEFORE THE ENUMERATION, deliberately. Deciding what is dispellable means a dat lookup
        /// per candidate enchantment (see below), and this fires on every kill; rolling first keeps that cost
        /// off the overwhelming majority of killing blows.
        ///
        /// EnchantmentTypeFlags.Undef IS THE "EVERYTHING" ARGUMENT, not a mistake. Undef is 0, and the registry
        /// filter is "(entry.StatModType and statModType) == statModType", so 0 matches every entry. That is
        /// the only way to reach the harmful entries: EnchantmentTypeFlags carries Beneficial and has no
        /// opposite flag, so harmful ones are identified by the ABSENCE of Beneficial and cannot be selected
        /// for positively. The harmful test itself is a property of the SPELL, which is why it arrives at
        /// <see cref="WeaponModCombat.SelectDispellableHarmful"/> as a predicate.
        /// </summary>
        private void ApplyWeaponModCleanse()
        {
            var chance = GetKillingWeaponModValue(WeaponModId.Cleanse);

            if (chance <= 0.0)
                return;

            if (!WeaponModCombat.RollsFree(chance, ThreadSafeRandom.Next(0f, 1f)))
                return;

            var harmful = WeaponModCombat.SelectDispellableHarmful(
                EnchantmentManager.GetEnchantments_TopLayer(EnchantmentTypeFlags.Undef), IsHarmfulSpell);

            // PvP dispel vuln lock (D2): after a recent PK attack, a vulnerability another player cast on
            // this wielder cannot be cleansed away by a killing blow either. No-op outside PvP / lever off.
            harmful = PvpRules.ApplyDispelVulnLock(this, harmful, PvpChokePoint.D2);

            if (harmful.Count == 0)
                return;

            // exactly one, chosen at random - the same rule Dispelling Edge uses, and for the same reason: a
            // deterministic pick would turn this into a debuff-priority tool
            var entry = harmful[ThreadSafeRandom.Next(0, harmful.Count - 1)];

            var spell = new Spell(entry.SpellId);

            // every caster's copy of that debuff - removing one layer only exposes the next, so two archers'
            // Gelidite's Gift left the vulnerability fully in force (DispellingEdgeAbility.SelectAllLayers).
            // The harmful filter is re-run per layer, same as the top-layer selection above.
            var layers = WeaponModCombat.SelectDispellableHarmful(
                DispellingEdgeAbility.SelectAllLayers(entry, EnchantmentManager.GetEnchantments(entry.SpellCategory)), IsHarmfulSpell);

            // PvP dispel vuln lock (D2), again: SelectAllLayers re-expands to EVERY caster's copy of entry's
            // category, which can reintroduce another player's locked vulnerability layer in the same
            // category that the filter above had already excluded from the pre-pick list. Filter the
            // expansion too, right before the Dispel call it feeds.
            layers = PvpRules.ApplyDispelVulnLock(this, layers, PvpChokePoint.D2);

            EnchantmentManager.Dispel(layers);

            Session?.Network.EnqueueSend(new GameMessageSystemChat($"Your weapon cleanses {spell.Name} from you.", ChatMessageType.Magic));
        }

        /// <summary>
        /// TRUE when a registry entry's spell is HARMFUL. Resolves the spell from the dat and reports false for
        /// anything it cannot resolve, so an unresolvable id is left alone rather than dispelled blind - the
        /// same treatment EnchantmentManager.GetEnchantments(MagicSchool) gives a missing spell.
        ///
        /// <see cref="Spell.IsHarmful"/> is simply "not Beneficial" (Spell.cs:121), which is the whole reason
        /// this has to be a dat read: the registry entry alone cannot answer it for a spell whose
        /// StatModType never carried the Beneficial flag in the first place.
        /// </summary>
        private static bool IsHarmfulSpell(int spellId)
        {
            var spell = new Spell(spellId);

            return !spell.NotFound && spell.IsHarmful;
        }
    }
}
