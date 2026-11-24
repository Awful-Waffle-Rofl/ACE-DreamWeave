using System;

using ACE.Common;
using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Server.Entity.Actions;
using ACE.Server.Managers;
using ACE.Server.WeaponMods;
using ACE.Server.WorldObjects;

namespace ACE.Server.Entity
{
    /// <summary>
    /// Where a weapon mod on a Summoner focus takes effect (<see cref="SummonerFocus.PetRoute"/>).
    /// </summary>
    public enum SummonerPetRoute
    {
        /// <summary>The wielder keeps it and the pets get nothing (the default for every mod a pet cannot use).</summary>
        Wielder,

        /// <summary>The pets get it and the wielder reads 0 (the single-target damage mods).</summary>
        PetsOnly,

        /// <summary>The pets get it and the wielder keeps it too.</summary>
        Shared,
    }

    /// <summary>
    /// Summoner casters (Docs/SummonerCasters/DESIGN.md): a caster flagged <see cref="PropertyBool.SummonerFocus"/>
    /// sends its rolled power to its wielder's combat pets and cuts the wielder's own damage. Every method reads the
    /// wielded focus LIVE at the hit, so unequipping or swapping takes effect on the next hit, and every method is
    /// null-safe. The pet bonus never applies in PvP: not while the owner is in a PvP match, and not when the other
    /// side is a Player or a player's pet. The wielder cut does apply in PvP.
    /// </summary>
    public static class SummonerFocus
    {
        public const string PetDamageScaleKey = "summoner_pet_damage_scale";
        public const string PetDefenseScaleKey = "summoner_pet_defense_scale";
        public const string PetDefenseFloorKey = "summoner_pet_defense_floor";
        public const string WielderDamageModKey = "summoner_wielder_spell_damage_mod";
        public const string PetModTransferPercentKey = "summoner_pet_mod_transfer_percent";
        public const string PetImbueTransferPercentKey = "summoner_pet_imbue_transfer_percent";

        internal static double PetDamageScale() =>
            SummonerFocusMath.SanitizeScale(PropertyManager.GetDouble(PetDamageScaleKey, SummonerFocusMath.DefaultPetDamageScale).Item, SummonerFocusMath.DefaultPetDamageScale);

        internal static double PetDefenseScale() =>
            SummonerFocusMath.SanitizeScale(PropertyManager.GetDouble(PetDefenseScaleKey, SummonerFocusMath.DefaultPetDefenseScale).Item, SummonerFocusMath.DefaultPetDefenseScale);

        internal static double PetDefenseFloor() =>
            SummonerFocusMath.SanitizeUnit(PropertyManager.GetDouble(PetDefenseFloorKey, SummonerFocusMath.DefaultPetDefenseFloor).Item, SummonerFocusMath.DefaultPetDefenseFloor);

        internal static double WielderDamageModSetting() =>
            SummonerFocusMath.SanitizeUnit(PropertyManager.GetDouble(WielderDamageModKey, SummonerFocusMath.DefaultWielderDamageMod).Item, SummonerFocusMath.DefaultWielderDamageMod);

        /// <summary>The share of each focus weapon mod bonus a pet reads (summoner_pet_mod_transfer_percent), sanitized. Never applied to the wielder's own reads.</summary>
        internal static double PetModTransferPercent() =>
            SummonerFocusMath.SanitizeTransferPercent(PropertyManager.GetDouble(PetModTransferPercentKey, SummonerFocusMath.DefaultPetModTransferPercent).Item, SummonerFocusMath.DefaultPetModTransferPercent);

        /// <summary>The share of a focus imbue's bonus a pet hit takes (summoner_pet_imbue_transfer_percent), sanitized. Applied before the max against the pet's own gear.</summary>
        internal static double PetImbueTransferPercent() =>
            SummonerFocusMath.SanitizeTransferPercent(PropertyManager.GetDouble(PetImbueTransferPercentKey, SummonerFocusMath.DefaultPetImbueTransferPercent).Item, SummonerFocusMath.DefaultPetImbueTransferPercent);

        /// <summary>True for a Caster carrying the SummonerFocus marker.</summary>
        public static bool IsSummonerFocus(WorldObject wo) =>
            wo is Caster && wo.GetProperty(PropertyBool.SummonerFocus) == true;

        /// <summary>The focus the wielder currently holds in the Held slot, or null.</summary>
        /// <remarks>
        /// Runs on every player rating read and every player hit, so it pre-filters on the lock-free CLR type check
        /// (<c>is Caster</c>, the first condition of <see cref="IsSummonerFocus"/>) and only pays the locked property reads
        /// (CurrentWieldedLocation, the marker) for a caster, the same reason WeaponModParagon.RatingAddFor filters on
        /// WeenieType. A plain foreach over the dictionary's values allocates nothing. Same answer as
        /// GetEquippedWand plus IsSummonerFocus, because the equip path lets at most one item occupy the Held slot.
        /// </remarks>
        public static WorldObject GetWieldedFocus(Creature wielder)
        {
            // A creature with no equipment table (a bare test fixture) holds nothing
            if (wielder?.EquippedObjects == null)
                return null;

            foreach (var item in wielder.EquippedObjects.Values)
            {
                if (!(item is Caster))
                    continue;

                if (item.CurrentWieldedLocation == EquipMask.Held)
                    return IsSummonerFocus(item) ? item : null;
            }

            return null;
        }

        /// <summary>True for a combat pet that belongs to a player (an Echo does not count).</summary>
        private static bool IsOwnedPet(WorldObject wo) => wo is CombatPet pet && !pet.IsHuntersEcho && pet.P_PetOwner != null;

        /// <summary>
        /// The focus a pet's owner is wielding, or null. The pet must be a non-Echo CombatPet with an owner who is not
        /// in a PvP match, and the other side must be null (a target-less read, such as a DoT tick) or neither a
        /// Player nor a player's pet.
        /// </summary>
        // Forwards to ResolveForPet so PetTakenMod can reuse the same guards with the pet as the victim and any WorldObject (or null) as the other side.
        public static WorldObject ResolvePetFocus(WorldObject attacker, Creature target)
        {
            // The combat simulator's offline pet has no owner; the seam names the focus it fights with. Only the dealt side
            // reads it, so PetTakenMod (which needs an owner's enchantments) never sees an ownerless focus.
            var simulated = SimulatedPetFocusSource;
            if (simulated != null && attacker is CombatPet pet && !pet.IsHuntersEcho && pet.P_PetOwner == null && !IsPlayerSide(target))
                return simulated(pet);

            return ResolveForPet(attacker, target);
        }

        private static bool IsPlayerSide(WorldObject wo) => wo is Player || wo is CombatPet pet && pet.P_PetOwner != null;

        private static WorldObject ResolveForPet(WorldObject pet, WorldObject other)
        {
            if (!(pet is CombatPet combatPet) || combatPet.IsHuntersEcho)
                return null;

            var owner = combatPet.P_PetOwner;
            if (owner == null || owner.IsInPvpMatch)
                return null;

            if (IsPlayerSide(other))
                return null;

            return GetWieldedFocus(owner);
        }

        /// <summary>
        /// The combat simulator's seam: when set, <see cref="ResolvePetFocus"/> returns its answer for a CombatPet
        /// attacker that has no owner, so an offline pet can be measured with a focus. Production never sets it; the
        /// simulator sets it for a run and resets it to null in a finally.
        /// </summary>
        internal static Func<WorldObject, WorldObject> SimulatedPetFocusSource;

        /// <summary>
        /// Multiplier on damage a combat pet deals to a monster; 1 when no focus applies. The damage-mod term, times the
        /// focus's Ambush when the target is still at full health (read before the vital write, so health is pre-hit).
        /// </summary>
        public static float PetDealtMod(WorldObject source, Creature target)
        {
            var focus = ResolvePetFocus(source, target);
            if (focus == null)
                return 1.0f;

            // An ownerless pet resolves a focus only through the simulator seam; with no wielder to read enchantments
            // from, its damage-mod term is neutral
            var owner = ((CombatPet)source).P_PetOwner;
            var dealt = owner == null ? 1.0 : SummonerFocusMath.DealtMultiplier(WorldObject.SumCasterElementalDamageMod(focus, owner), PetDamageScale());

            return (float)(dealt * (WeaponModCombat.IsFullHealth(target) ? WeaponModCombat.DamageMultiplier(PetFocusModValue(source, target, WeaponModId.Ambush)) : 1.0));
        }

        /// <summary>
        /// The route table: the one place that says where each weapon mod on a Summoner focus takes effect.
        /// PetsOnly is DERIVED (a single-target damage mod in the caster pool, except Attunement, which already feeds the
        /// pet formula through <see cref="WorldObject.SumCasterElementalDamageMod"/>), so no site lists those ids. Shared
        /// has no registry flag that expresses it, so it is the one explicit list: Defensive Weapon (owner ruling Q3), and
        /// the pet-kill and pet-hit utility trio Second Wind, Cleanse and Siphon (Q4). Everything else stays with the wielder.
        /// </summary>
        public static SummonerPetRoute PetRoute(WeaponModDefinition definition)
        {
            if (definition == null)
                return SummonerPetRoute.Wielder;

            if (definition.AffectsSingleTargetDamage && definition.AppliesTo(WeaponClass.Caster) && definition.Id != WeaponModId.Attunement)
                return SummonerPetRoute.PetsOnly;

            switch (definition.Id)
            {
                case WeaponModId.DefensiveWeapon:
                case WeaponModId.SecondWind:
                case WeaponModId.Cleanse:
                case WeaponModId.Siphon:
                    return SummonerPetRoute.Shared;
            }

            return SummonerPetRoute.Wielder;
        }

        /// <summary>
        /// True for a weapon mod a Summoner focus sends to the pets INSTEAD of the wielder (<see cref="SummonerPetRoute.PetsOnly"/>):
        /// the set the wielder strip removes.
        /// </summary>
        public static bool IsPetTransferredMod(WeaponModDefinition definition) =>
            PetRoute(definition) == SummonerPetRoute.PetsOnly;

        /// <summary>True for a mod the pets read off the focus at all: PetsOnly or Shared.</summary>
        public static bool ReachesPets(WeaponModDefinition definition) =>
            PetRoute(definition) != SummonerPetRoute.Wielder;

        /// <summary>True when the wielder's read of this mod off this item must be 0: the item is a Summoner focus and the mod goes to the pets.</summary>
        public static bool IsWielderStrippedMod(WorldObject weapon, WeaponModDefinition definition) =>
            IsPetTransferredMod(definition) && IsSummonerFocus(weapon);

        /// <summary>
        /// A PetsOnly or Shared mod's magnitude on the focus the pet's owner wields, for this pet's hit on this target; 0
        /// when the mod does not reach pets, no focus resolves (PvP, no owner, Echo, no focus), or the owner's weapon
        /// mods are suppressed (PK facet, PvP weapon-mod mask). Read live at the hit, at
        /// <see cref="PetModTransferPercent"/> of the focus magnitude: every Tier B magnitude is a bonus whose 0 is no
        /// effect (an added chance, a (1 + m) multiplier's m, a shield level, a vitals fraction), so the share scales the bonus.
        /// </summary>
        public static double PetFocusModValue(WorldObject attacker, Creature target, WeaponModId modId)
        {
            if (!WeaponModRegistry.TryGet(modId, out var definition) || !ReachesPets(definition))
                return 0.0;

            var focus = ResolvePetFocus(attacker, target);
            if (focus == null)
                return 0.0;

            if (WeaponModSuppression.SuppressedFor(((CombatPet)attacker).P_PetOwner))
                return 0.0;

            return WeaponModCombat.ReadFocusModForPets(focus, modId) * PetModTransferPercent();
        }

        /// <summary>
        /// The Defensive Weapon shield level a combat pet takes from the focus its owner wields (a Shared mod, owner
        /// ruling Q3: the wielder keeps it too), against a hit from <paramref name="attacker"/>: 0 for anything but a
        /// CombatPet, in NonCombat (stance-bound, like the player's), and wherever <see cref="PetFocusModValue"/> reads 0
        /// (a Player-side attacker, an Echo, an owner in a PvP match, owner suppression, no focus). It reads through the
        /// dealt-side resolver, so the simulator's seam reaches an ownerless pet. Sanitized like the player's level.
        /// </summary>
        public static double PetDefensiveWeaponLevel(Creature pet, WorldObject attacker)
        {
            if (!(pet is CombatPet) || pet.CombatMode == CombatMode.NonCombat)
                return 0.0;

            // only a literal null means target-less; any other non-Creature attacker (a trap, a hotspot) gets none
            if (attacker != null && !(attacker is Creature))
                return 0.0;

            return DefensiveWeaponMath.Sanitize(PetFocusModValue(pet, attacker as Creature, WeaponModId.DefensiveWeapon));
        }

        /// <summary>
        /// Where <see cref="EnqueuePetKillUtility"/> queues the pet half of a kill: the killing PET's own action queue
        /// (WorldObject.EnqueueAction, drained on the pet's landblock tick), never the owner's or the victim's, because a
        /// pet is not tied to its owner's landblock group and this writes the pet's vitals and enchantments. A test seam
        /// records the target; production never replaces it.
        /// </summary>
        internal static Action<CombatPet, IAction> PetKillEnqueue = (pet, action) => pet.EnqueueAction(action);

        /// <summary>
        /// Called from Creature.OnDeath (the victim's thread) for every death: when the killer is a combat pet whose owner's
        /// focus resolves (<see cref="ResolvePetFocus"/>: non-Echo, owner not in a PvP match, a non-player victim) and carries
        /// Second Wind or Cleanse for this kill, queues
        /// <see cref="ApplyPetKillUtility"/> onto the PET's queue (<see cref="PetKillEnqueue"/>). Any other pet queues
        /// nothing. The owner's own Second Wind, Mana Well and Cleanse payout (Player.ApplyWeaponModPetKill) is separate
        /// and unchanged.
        /// </summary>
        public static void EnqueuePetKillUtility(WorldObject killer, Creature victim)
        {
            if (!(killer is CombatPet pet) || victim == null || victim is Player || victim is Pet)
                return;

            if (ResolvePetFocus(pet, victim) == null)
                return;

            // nothing to do on the pet's queue unless the focus carries Second Wind or Cleanse for this kill
            if (PetFocusModValue(pet, victim, WeaponModId.SecondWind) <= 0.0 && PetFocusModValue(pet, victim, WeaponModId.Cleanse) <= 0.0)
                return;

            PetKillEnqueue(pet, new ActionEventDelegate(() => ApplyPetKillUtility(pet, victim)));
        }

        /// <summary>
        /// The killing pet's half of the Shared on-kill mods (owner ruling Q4), on the PET's own queue: Second Wind restores
        /// that fraction of the PET's maximum health, and Cleanse rolls its chance to strip one harmful enchantment off the
        /// PET (Creature.CleanseOneHarmfulEnchantmentNoPvpLock). Both read through <see cref="PetFocusModValue"/> at run
        /// time, so the PvP, Echo, suppression and transfer-percent rules apply and a dead pet takes nothing.
        /// </summary>
        public static void ApplyPetKillUtility(CombatPet pet, Creature victim)
        {
            if (pet == null || pet.IsDead || victim == null || victim is Player || victim is Pet)
                return;

            var secondWind = PetFocusModValue(pet, victim, WeaponModId.SecondWind);

            if (secondWind > 0.0)
            {
                var health = WeaponModCombat.RestoreFromMax(pet.Health.MaxValue, secondWind);

                if (health > 0)
                    pet.UpdateVitalDelta(pet.Health, health);
            }

            var cleanse = PetFocusModValue(pet, victim, WeaponModId.Cleanse);

            if (cleanse > 0.0 && WeaponModCombat.RollsFree(cleanse, ThreadSafeRandom.Next(0f, 1f)))
                pet.CleanseOneHarmfulEnchantmentNoPvpLock();
        }

        /// <summary>
        /// Siphon on a combat pet's landed hit (Shared, owner ruling Q4): the owner's focus Siphon chance
        /// (<see cref="PetFocusModValue"/>, so PvE only, non-Echo, at the transfer percent) through the same body the
        /// player uses (Creature.SiphonOneBeneficialEnchantment), stealing one beneficial enchantment off the target onto
        /// the PET. One roll per call; the two sinks call it once per landed hit.
        /// </summary>
        public static void TryPetSiphon(CombatPet pet, Creature target)
        {
            var chance = PetFocusModValue(pet, target, WeaponModId.Siphon);

            if (chance > 0.0)
                pet.SiphonOneBeneficialEnchantment(target, chance);
        }

        /// <summary>
        /// The imbues a Summoner focus sends to its pets, each read at a pet hook in WorldObject_Weapon (Critical Strike and
        /// Crippling Blow at the crit helpers, Armor Rending at GetArmorRendingModFor, the rends at
        /// GetWeaponResistanceModifier). The appraisal and the summon-duel --summoner-imbue read this one list.
        /// </summary>
        public static readonly ImbuedEffectType[] PetImbues =
        {
            ImbuedEffectType.CriticalStrike, ImbuedEffectType.CripplingBlow, ImbuedEffectType.ArmorRending,
            ImbuedEffectType.SlashRending, ImbuedEffectType.PierceRending, ImbuedEffectType.BludgeonRending,
            ImbuedEffectType.FireRending, ImbuedEffectType.ColdRending, ImbuedEffectType.AcidRending, ImbuedEffectType.ElectricRending,
            ImbuedEffectType.HealthRending,
        };

        /// <summary>
        /// True when this pet's hit on this target takes <paramref name="imbue"/> from the focus its owner wields: the
        /// <see cref="ResolvePetFocus"/> guards (non-Echo CombatPet, owner not in a PvP match, the other side neither a
        /// Player nor a player's pet; the simulator seam for an ownerless pet) and the focus's ImbuedEffect. No weapon-mod
        /// suppression and no weapon_mods_enabled gate: an imbue is a retail item property, not a weapon mod, the same as
        /// the elemental damage mod transfer in <see cref="PetDealtMod"/>. The imbue is sized by the pet's own attack
        /// skill at the hook (owner ruling Q1) at <see cref="PetImbueTransferPercent"/> of its bonus.
        /// </summary>
        public static bool PetFocusHasImbue(WorldObject attacker, Creature target, ImbuedEffectType imbue) =>
            imbue != ImbuedEffectType.Undef && ResolvePetFocus(attacker, target)?.HasImbuedEffect(imbue) == true;

        /// <summary>
        /// The integer rating <paramref name="native"/> (GearDamage, GearCrit, GearCritDamage) a focus owes to Tier A
        /// weapon mods (Bloodthirst, Weak Point, Devastation): the mod-written part of its native, as the suppression path
        /// measures it, plus its Paragon gems for that row. The loot-rolled part of the native is not counted. 0 for a
        /// native no Tier A row writes.
        /// </summary>
        public static int FocusTierARating(WorldObject focus, PropertyInt native)
        {
            if (focus == null)
                return 0;

            var items = new[] { focus };

            return WeaponModSuppression.TierARatingOffset(items, native) + WeaponModParagon.TierARatingAdd(items, native, WeaponModRegistry.Enabled(), WeaponModValue.MagnitudeScale());
        }

        /// <summary>
        /// The Tier A rating a combat pet gets from the focus its owner wields, for the target-less rating getters. 0
        /// unless <see cref="ResolvePetFocus"/> resolves a focus (non-Echo pet, owner not in a PvP match) and the owner's
        /// weapon mods are not suppressed. A pet hitting a player is refused upstream by CanDamage.
        /// </summary>
        public static int PetFocusRatingBonus(Creature pet, PropertyInt native)
        {
            var focus = ResolvePetFocus(pet, null);
            if (focus == null)
                return 0;

            // An ownerless pet resolves only through the simulator seam; no owner, nothing to suppress
            if (WeaponModSuppression.SuppressedFor(((CombatPet)pet).P_PetOwner))
                return 0;

            // the pets' share of the rating, rounded down; the wielder strip below always removes the full amount
            return SummonerFocusMath.ScaleRating(FocusTierARating(focus, native), PetModTransferPercent());
        }

        /// <summary>
        /// The Tier A rating to take off a Player wielding a focus, so its mods reach only the pets. 0 for a non-player,
        /// for no focus, and for a suppressed wielder: there RatingOffsetFor already removes the mod-written part and
        /// RatingAddFor already adds no Paragon amount, so subtracting here too would count it twice. Applies in PvP.
        /// </summary>
        public static int WielderFocusRatingStrip(Creature wielder, PropertyInt native)
        {
            if (!(wielder is Player player))
                return 0;

            var focus = GetWieldedFocus(player);
            if (focus == null || WeaponModSuppression.SuppressedFor(player))
                return 0;

            // Inside a Hunter's Echo weapon scope RatingAddFor counts the Echo's snapshot weapon instead of the focus, so
            // the focus's Paragon amount was never added; only its mod-written native (in the equipped-items cache) is
            if (player.HuntersEchoWeaponScope != null)
                return WeaponModSuppression.TierARatingOffset(new[] { focus }, native);

            return FocusTierARating(focus, native);
        }

        /// <summary>
        /// Multiplier on damage dealt to a combat pet. Applies when the source is null (a DoT tick), a monster, or any
        /// non-player, non-player-pet source.
        /// </summary>
        public static float PetTakenMod(Creature victim, WorldObject source)
        {
            var focus = ResolveForPet(victim, source);
            if (focus == null)
                return 1.0f;

            var owner = ((CombatPet)victim).P_PetOwner;
            return (float)SummonerFocusMath.TakenMultiplier(WorldObject.SumCasterElementalDamageMod(focus, owner), PetDefenseScale(), PetDefenseFloor());
        }

        /// <summary>
        /// Multiplier on damage a player deals while wielding a focus; applies in PvP; never to the player themself, and
        /// never to a Hunter's Echo target, which the cloak absorb hands its owner's hit already cut.
        /// <paramref name="launcher"/> is the item a projectile left from (SpellProjectile.ProjectileLauncher, snapshotted
        /// at the cast): when it is a focus the cut applies even if the wielder has swapped it off since, so a bolt cast
        /// with the focus cannot land uncut. Null for a hit with no projectile, which reads only what is wielded now.
        /// </summary>
        public static float WielderDamageMod(WorldObject source, Creature target, WorldObject launcher = null)
        {
            if (!(source is Player player) || target == null || ReferenceEquals(target, player))
                return 1.0f;

            // the cloak absorb hands a Hunter's Echo its owner's hit already cut (SpellProjectile, Player.TakeDamage), and
            // the Echo takes it through Creature.TakeDamage, so cutting again here would cut an absorbed hit twice
            if (target is CombatPet echo && echo.IsHuntersEcho)
                return 1.0f;

            return GetWieldedFocus(player) == null && !IsSummonerFocus(launcher) ? 1.0f : (float)WielderDamageModSetting();
        }

        /// <summary>
        /// The three mods multiplied: the one number a hit site applies. <paramref name="launcher"/> reaches only the
        /// wielder cut (see <see cref="WielderDamageMod"/>); the pet halves always read the owner's focus live.
        /// </summary>
        public static float HitMod(WorldObject source, Creature target, WorldObject launcher = null) =>
            PetDealtMod(source, target) * PetTakenMod(target, source) * WielderDamageMod(source, target, launcher);
    }
}
