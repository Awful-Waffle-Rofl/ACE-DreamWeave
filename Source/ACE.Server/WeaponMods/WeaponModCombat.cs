using System;
using System.Collections.Generic;
using System.Linq;

using ACE.Entity.Models;
using ACE.Server.ClassAbilities.Abilities;
using ACE.Server.WorldObjects;

namespace ACE.Server.WeaponMods
{
    /// <summary>
    /// The pure arithmetic behind every Tier B effect, plus the ONE accessor that reads a modifier off a
    /// weapon. Deliberately free of Player, Session and database so the conditions and the rounding can be
    /// tested directly - the hooks in Player_WeaponMods.cs are thin wrappers over the functions here.
    ///
    /// THE WEAPON-ONLY RULE (HARD). <see cref="ReadWeaponOnly"/> reads exactly ONE item and never enumerates
    /// anything. Do not reach for Creature.GetEquippedModValue or Creature.GetEquippedModPotencySum next door:
    /// those SUM a mod's stored value across every equipped item, which is right for the armor system (mods
    /// land on up to a dozen pieces and stack additively) and wrong here by a factor of however many items a
    /// player is wearing. A weapon mod lives on ONE item - the weapon - and a summing read would let a player
    /// stack Life Leech off a helmet.
    ///
    /// The name says "weapon only" for that reason. If a future hook needs a value, it calls this with the
    /// specific item that carries it.
    /// </summary>
    public static class WeaponModCombat
    {
        // ---------------- the weapon-only accessor ----------------

        /// <summary>
        /// A Tier B modifier's applied magnitude on ONE item, as a fraction, or 0 when the item does not carry
        /// it. Reads the live weapon_mods_enabled gate - the system's single master switch - so every caller is
        /// inert while it is off even on a weapon that already carries a record from when it was on.
        ///
        /// See the class remarks for why this takes a single item rather than a creature.
        ///
        /// PER-WIELDER SUPPRESSION (2026-09-25). Also returns 0 when the item's <see cref="WorldObject.Wielder"/>
        /// is a Player whose weapon mods are suppressed (<see cref="WeaponModSuppression.SuppressedFor"/>; today
        /// the PK facet). A null Wielder is never read as suppressed. Wielder is set by
        /// Creature.TryEquipObject and, for items loaded with the character, by
        /// Player.SendInventoryAndWieldedItems at login, and cleared only by Creature.TryDequipObject - so it
        /// resolves on every read of an item that is still equipped. A projectile hit can land after its
        /// launcher was unequipped; the sites that can see such a launcher pass the attacker explicitly through
        /// <see cref="ReadWeaponOnlyWieldedBy"/> instead.
        /// </summary>
        public static double ReadWeaponOnly(WorldObject weapon, WeaponModId modId) =>
            ReadWeaponOnlyWieldedBy(weapon, modId, null);

        /// <summary>
        /// <see cref="ReadWeaponOnly(WorldObject, WeaponModId)"/> with the wielder named by the caller: suppression
        /// is resolved against <paramref name="wielder"/> when it is non-null, and against the item's own
        /// <see cref="WorldObject.Wielder"/> otherwise. For call sites that hold the attacker and may be reading a
        /// launcher or caster that is no longer equipped (a projectile landing after a weapon swap).
        /// </summary>
        public static double ReadWeaponOnlyWieldedBy(WorldObject weapon, WeaponModId modId, WorldObject wielder)
        {
            if (weapon == null)
                return 0.0;

            if (WeaponModSuppression.SuppressedFor(wielder ?? weapon.Wielder))
                return 0.0;

            return ReadWeaponOnly(weapon, modId, WeaponModRegistry.Enabled());
        }

        /// <summary>
        /// Folds the three base-damage Tier B mods into an already-constructed <see cref="Entity.BaseDamageMod"/>:
        /// Heft into DamageBonus, Tension/Leverage into DamageMod. Both land INSIDE the
        /// "(BaseDamage.MaxDamage + DamageBonus + ElementalBonus) * DamageMod" bracket, so Heft multiplies with
        /// Tension rather than adding alongside it.
        ///
        /// This exists as a helper rather than as a hook in BaseDamageMod's own constructor because that class is
        /// byte-identical to upstream 08471633e and is kept that way deliberately. The one caller is
        /// DamageEvent.GetBaseDamage(Player), which already mutates the returned BaseDamageMod for the quest-bow
        /// damage bonus and the missile elemental bonus - this is the same established seam. Being player-only is
        /// correct: weapon mods are a player crafting system, and a monster wielding a modded weapon must not benefit.
        ///
        /// Tension and Leverage are mutually exclusive by weapon class at roll time (missile vs melee), so reading
        /// both unconditionally is harmless - at most one is ever nonzero on a given weapon.
        ///
        /// <paramref name="wielder"/> is the attacking player, passed so per-wielder suppression resolves even
        /// when the weapon is a missile launcher unequipped while its projectile was in flight; null falls back
        /// to the weapon's own Wielder (see <see cref="ReadWeaponOnlyWieldedBy"/>).
        /// </summary>
        public static void ApplyBaseDamageMods(Entity.BaseDamageMod mod, WorldObject weapon, WorldObject wielder = null)
        {
            if (mod == null || weapon == null)
                return;

            mod.DamageBonus += (float)ReadWeaponOnlyWieldedBy(weapon, WeaponModId.Heft, wielder);
            mod.DamageMod += (float)(ReadWeaponOnlyWieldedBy(weapon, WeaponModId.Tension, wielder)
                                   + ReadWeaponOnlyWieldedBy(weapon, WeaponModId.Leverage, wielder));
        }

        /// <summary>
        /// <see cref="ReadWeaponOnly(WorldObject, WeaponModId)"/> at an EXPLICIT gate state. Pure apart from
        /// the item read, so both gate states can be exercised without touching PropertyManager.
        /// </summary>
        public static double ReadWeaponOnly(WorldObject weapon, WeaponModId modId, bool enabled) =>
            ReadWeaponOnly(weapon, modId, enabled, WeaponModValue.MagnitudeScale());

        /// <summary>
        /// <see cref="ReadWeaponOnly(WorldObject, WeaponModId, bool)"/> at an explicit gate state AND an
        /// explicit magnitude scale. Fully pure apart from the item read.
        ///
        /// THE RECORD IS A ROLL FRACTION, NOT A MAGNITUDE, so this resolves against the live catalog rather
        /// than returning what it read. That resolution is the mechanism by which a MaxRoll retune reaches
        /// weapons already in the world - see WeaponModValue's roll-fraction remarks. It costs one multiply
        /// and one tunable lookup per read on the combat path, which is the price of retunes being retroactive.
        ///
        /// Workmanship is deliberately NOT read here: it is already folded into the stored fraction at roll
        /// time. That keeps the roll's inputs frozen where they belong - a weapon's effect cannot shift because
        /// its workmanship property was edited afterwards - and keeps this path off
        /// <see cref="WorldObject.Workmanship"/>, whose getter can WRITE to the item on malformed data.
        /// </summary>
        public static double ReadWeaponOnly(WorldObject weapon, WeaponModId modId, bool enabled, double scale)
        {
            if (!enabled || weapon == null)
                return 0.0;

            if (!WeaponModRegistry.TryGet(modId, out var definition) || definition.Tier != WeaponModTier.B)
                return 0.0;

            var fraction = weapon.GetProperty(definition.Record);

            if (fraction == null || double.IsNaN(fraction.Value) || fraction.Value <= 0.0)
                return 0.0;

            return WeaponModValue.MagnitudeFromFraction(definition, fraction.Value, scale);
        }

        // ---------------- conditions ----------------

        /// <summary>
        /// TRUE when the target is at FULL health, which is Ambush's whole condition - "the first strike"
        /// against a target is exactly the strike that finds it undamaged, so no per-target state is needed to
        /// express it.
        ///
        /// Shaped after ExecutionerAbility.IsExecuteRange, the nearest existing idiom, including its treatment
        /// of a target with no maximum health: never in range, rather than dividing by zero or defaulting to
        /// true. Current above maximum (a buffed vital mid-recalculation) still reads as full.
        /// </summary>
        public static bool IsFullHealth(Creature target)
        {
            var max = target?.Health.MaxValue ?? 0;

            if (max == 0)
                return false;

            return target.Health.Current >= max;
        }

        /// <summary>
        /// Recovery's natural-regeneration multiplier (1.0 = none), from the two carriers it can ride.
        ///
        /// A CHOICE, NOT A SUM. A player somehow holding both a melee/missile weapon and a wand that each
        /// carry Recovery gets the better of the two, never the two added together - the same rule
        /// Player.GetKillingWeaponModValue applies to Second Wind, and for the same reason: the modifier
        /// belongs to a weapon, not to a loadout.
        ///
        /// The caller multiplies this INTO the VitalHeartBeat tick product, beside stanceMod, so combat
        /// suppression is untouched: Creature.GetStanceMod returns 0.5 in combat mode and this cannot bypass a
        /// term it multiplies with.
        /// </summary>
        public static double RegenerationMultiplier(double weaponMagnitude, double casterMagnitude)
        {
            var best = 0.0;

            if (!double.IsNaN(weaponMagnitude) && weaponMagnitude > best)
                best = weaponMagnitude;

            if (!double.IsNaN(casterMagnitude) && casterMagnitude > best)
                best = casterMagnitude;

            return DamageMultiplier(best);
        }

        /// <summary>
        /// Arcane Defender's virtual-shield damage multiplier: a flat armor level, reduced by the attacker's
        /// IgnoreShield term, run through the same <see cref="SkillFormula.CalcArmorMod"/> the equipped-shield
        /// path ends on. 1.0 when the modifier is absent, which is exactly what a shieldless defender already
        /// returned.
        ///
        /// NO SHIELD-SKILL CAP, deliberately, and this is the one place it differs from the equipped-shield
        /// path. That cap is half the Shield skill (or the full skill when specialized), and a caster's Shield
        /// skill is untrained and effectively zero - applying it here would clamp the row to nothing on
        /// precisely the build the row exists for.
        ///
        /// At the catalog MaxRoll of 25 and an untouched ignoreShieldMod of 1.0 this is
        /// 66.667 / (25 + 66.667) = 0.7273 (SkillFormula.ArmorMod is 200/3).
        /// </summary>
        public static float ArcaneShieldMod(double armorLevel, float ignoreShieldMod)
        {
            if (double.IsNaN(armorLevel) || armorLevel <= 0.0)
                return 1.0f;

            if (float.IsNaN(ignoreShieldMod))
                return 1.0f;

            return SkillFormula.CalcArmorMod((float)armorLevel * ignoreShieldMod);
        }

        /// <summary>
        /// Panic Reload's proximity radius, in meters. The distance actually measured against it is the
        /// CYLINDER (edge to edge) distance PhysicsObj.get_distance_sq_to_object(obj, true) returns, which is
        /// the engine's own "is this creature near me" measure - the same one Player.CheckMonsters and the
        /// monster awareness sweeps compare their ranges against - not a centre-to-centre distance.
        /// </summary>
        public const double PanicReloadRange = 6.0;

        /// <summary>
        /// <see cref="PanicReloadRange"/> squared, so the hook can compare against a squared distance and skip
        /// a square root per candidate.
        /// </summary>
        public const double PanicReloadRangeSq = PanicReloadRange * PanicReloadRange;

        /// <summary>
        /// TRUE when a candidate creature counts as a hostile inside Panic Reload's radius. Pure - every
        /// property the decision needs arrives as an argument - so the predicate is testable without a live
        /// Player, an ObjMaint or a landblock.
        ///
        /// The exclusions are the ones every "hostile near me" check in this codebase carries: the wielder
        /// itself, other players (this is a PvE modifier), the player's own pets, and anything dead or
        /// unattackable. A negative or NaN distance never qualifies rather than being treated as touching.
        /// </summary>
        public static bool IsPanicReloadThreat(bool isSelf, bool isPlayer, bool isPet, bool isDead, bool attackable, double distanceSq)
        {
            if (isSelf || isPlayer || isPet || isDead || !attackable)
                return false;

            if (double.IsNaN(distanceSq) || distanceSq < 0.0)
                return false;

            return distanceSq <= PanicReloadRangeSq;
        }

        /// <summary>
        /// TRUE when a probability-gated effect fires. <paramref name="chance"/> is the modifier's magnitude
        /// read as a PROBABILITY (0.20 = 20% chance), compared against a caller-supplied uniform draw in
        /// [0, 1). Pure for testability - a live call site draws via ThreadSafeRandom. Used by Overload until
        /// its 2026-08-17 retirement; Cleanse and Siphon are its intended reusers in Phase 2.
        ///
        /// A chance of 0 (unequipped, or the gate off) never fires, and a chance of 1 or more always does.
        /// </summary>
        public static bool RollsFree(double chance, double roll)
        {
            if (double.IsNaN(chance) || chance <= 0.0 || double.IsNaN(roll))
                return false;

            return roll < chance;
        }

        /// <summary>
        /// Mana Well's payout for ONE equipped item: whole points of mana to add, clamped to the item's
        /// remaining headroom. Pure - the caller supplies the item's two numbers, so the clamp is testable
        /// without an item, a player or a session.
        ///
        /// THE MAGNITUDE IS FLAT MANA POINTS, NOT A FRACTION, and Mana Well is the only row in the catalog
        /// shaped that way (see its registry comment: "MaxRoll is mana POINTS restored to EACH equipped item").
        /// Reading it as a fraction of the item's pool would make a 5-point mod pay out thousands on a
        /// high-capacity caster, so this deliberately does no multiplication against <paramref name="maxMana"/>
        /// at all - it only uses it as the ceiling.
        ///
        /// Rounded half AWAY FROM ZERO, matching <see cref="RestoreFromMax"/>, so a partial roll never silently
        /// truncates to nothing at the low end of the range. An item already at or above its maximum, or one
        /// with no maximum, takes 0 rather than being driven past its cap.
        /// </summary>
        public static int ManaWellTopUp(int currentMana, int maxMana, double magnitude)
        {
            if (maxMana <= 0 || double.IsNaN(magnitude) || magnitude <= 0.0)
                return 0;

            var headroom = maxMana - currentMana;

            if (headroom <= 0)
                return 0;

            var points = (int)Math.Round(magnitude, MidpointRounding.AwayFromZero);

            if (points <= 0)
                return 0;

            return Math.Min(points, headroom);
        }

        /// <summary>
        /// Cleanse's selection rule: the entries on the WIELDER that a killing blow is allowed to strip. Two
        /// filters, in this order, and both are load-bearing.
        ///
        /// FIRST the shipped dispel exclusions, via <see cref="DispellingEdgeAbility.SelectDispellable"/> -
        /// Duration == -1 (an item-sourced enchantment, or vitae) and SpellId above short.MaxValue (an
        /// item/cooldown pseudo-spell) are never removable. Reusing that function rather than restating its two
        /// conditions is deliberate: there is then exactly one place in the codebase where "what a dispel may
        /// touch" is written down, and Cleanse cannot drift from Dispelling Edge.
        ///
        /// SECOND the harmful test, which is what makes this Cleanse rather than a self-inflicted dispel.
        /// THERE IS NO "MALIGNANT" FLAG TO TEST: <see cref="ACE.Entity.Enum.EnchantmentTypeFlags"/> carries
        /// Beneficial and no opposite, so harmful is the ABSENCE of that flag on the spell - which is a
        /// property of the SPELL, not of the registry entry, and reaching it means a dat lookup. That lookup is
        /// why it arrives here as a predicate over the spell id rather than being performed inline: the
        /// selection rule is then testable against a fake predicate, with no DatManager and no Player.
        ///
        /// Note the predicate is asked about EVERY surviving candidate, so a caller should keep it cheap; the
        /// live caller only reaches this after a successful roll on a killing blow.
        /// </summary>
        public static List<PropertiesEnchantmentRegistry> SelectDispellableHarmful(List<PropertiesEnchantmentRegistry> candidates, Func<int, bool> isHarmful)
        {
            var dispellable = DispellingEdgeAbility.SelectDispellable(candidates);

            if (isHarmful == null)
                return new List<PropertiesEnchantmentRegistry>();

            return dispellable.Where(e => isHarmful(e.SpellId)).ToList();
        }

        // ---------------- magnitudes ----------------

        /// <summary>
        /// A conditional-damage modifier as a damage multiplier: 1.0 when it is absent, 1 + fraction when it is
        /// held. Kept as a named function rather than an inline "1 + x" so the NaN and negative cases have one
        /// answer instead of one per call site.
        /// </summary>
        public static double DamageMultiplier(double fraction)
        {
            if (double.IsNaN(fraction) || fraction <= 0.0)
                return 1.0;

            return 1.0 + fraction;
        }

        /// <summary>
        /// Second Wind's payout: whole points of a vital to restore, as a fraction of its MAXIMUM. Rounded half
        /// up and floored at 0. A max of 0 (a vital that has not been initialised) restores nothing rather than
        /// throwing.
        /// </summary>
        public static int RestoreFromMax(uint maxValue, double fraction)
        {
            if (maxValue == 0 || double.IsNaN(fraction) || fraction <= 0.0)
                return 0;

            var exact = maxValue * fraction;

            var restored = (int)Math.Round(exact, MidpointRounding.AwayFromZero);

            return Math.Max(0, restored);
        }

        /// <summary>
        /// Efficiency's cost multiplier (1.0 = none): a fractional reduction on attack stamina cost or spell
        /// mana cost. Clamped to [0, 1] so a malformed fraction above 1.0 never produces a negative multiplier
        /// (and therefore a cost that pays the player instead of merely discounting them).
        /// </summary>
        public static double CostMultiplier(double fraction)
        {
            return 1 - Math.Clamp(fraction, 0, 1);
        }
    }
}
