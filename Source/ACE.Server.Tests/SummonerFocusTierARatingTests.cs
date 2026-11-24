using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Entity;
using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Entity.Models;
using ACE.Server.Entity;
using ACE.Server.Managers;
using ACE.Server.WeaponMods;
using ACE.Server.WorldObjects;

namespace ACE.Server.Tests
{
    /// <summary>
    /// Tier A weapon mods on a Summoner focus (Bloodthirst, Weak Point, Devastation) write gear-rating natives onto the
    /// item. Through the three rating getters (GetDamageRating, GetCritRating, GetCritDamageRating) those amounts reach
    /// the owner's combat pets and are stripped from the wielder, exactly once. The focus is equipped through
    /// Creature.TryEquipObject, the path that fills the equipped-items rating cache, and the getters are driven live.
    /// Seeds every PropertyManager key it reads and restores it from the defaults.
    /// </summary>
    [TestClass]
    public class SummonerFocusTierARatingTests
    {
        private static uint nextGuid = 0x7E680000;
        private static uint nextWcid = 996700;

        private static readonly string[] DoubleKeys =
        {
            SummonerFocus.PetDamageScaleKey, SummonerFocus.PetDefenseScaleKey, SummonerFocus.PetDefenseFloorKey, SummonerFocus.WielderDamageModKey,
            "weapon_mod_magnitude_scale",
        };

        // The rating getters read the owner's class-ability cache from Character quests, so the seeded owner needs an empty
        // Character and its lock. UnsafeAccessor, never FieldInfo.SetValue on a Player (static ctor).
        [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "<Character>k__BackingField")]
        private static extern ref ACE.Database.Models.Shard.Character CharacterField(Player player);

        [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "CharacterDatabaseLock")]
        private static extern ref ReaderWriterLockSlim CharacterLockField(Player player);

        // The seeded owner is built uninitialized, so the held-item list TryEquipObject appends to is null until set
        [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "<Children>k__BackingField")]
        private static extern ref List<HeldItem> ChildrenField(WorldObject wo);

        // ...and so are its position dictionaries, which TrySetChild reads for the held item's Location
        [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "ephemeralPositions")]
        private static extern ref Dictionary<PositionType, Position> EphemeralPositionsField(WorldObject wo);

        [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "positionCache")]
        private static extern ref Dictionary<PositionType, Position> PositionCacheField(WorldObject wo);

        [ClassInitialize]
        public static void Init(TestContext _)
        {
            TestGameTables.EnsureInitialized();
            DefaultPropertyManager.LoadDefaultProperties();
        }

        [TestInitialize]
        public void Seed()
        {
            PropertyManager.ModifyDouble(SummonerFocus.PetDamageScaleKey, 5.0);
            PropertyManager.ModifyDouble(SummonerFocus.PetDefenseScaleKey, 2.5);
            PropertyManager.ModifyDouble(SummonerFocus.PetDefenseFloorKey, 0.25);
            PropertyManager.ModifyDouble(SummonerFocus.WielderDamageModKey, 0.25);
            PropertyManager.ModifyDouble("weapon_mod_magnitude_scale", 1.0);
            PropertyManager.ModifyBool("weapon_mods_enabled", true);
        }

        [TestCleanup]
        public void Restore()
        {
            foreach (var key in DoubleKeys)
                PropertyManager.ModifyDouble(key, DefaultPropertyManager.DefaultDoubleProperties[key].Item);

            PropertyManager.ModifyBool("weapon_mods_enabled", (bool)DefaultPropertyManager.DefaultBooleanProperties["weapon_mods_enabled"].Item);
        }

        /// <summary>The loot-rolled part of each native, before any mod: what the wielder keeps.</summary>
        private const int LootBase = 2;

        private static readonly (WeaponModId Id, PropertyInt Native)[] TierA =
        {
            (WeaponModId.Bloodthirst, PropertyInt.GearDamage),
            (WeaponModId.WeakPoint, PropertyInt.GearCrit),
            (WeaponModId.Devastation, PropertyInt.GearCritDamage),
        };

        /// <summary>
        /// A wand carrying a loot base on each Tier A native, plus (when <paramref name="modded"/>) the three Tier A mods at a
        /// perfect roll written the way the applicator writes them: native raised, magnitude recorded.
        /// </summary>
        private static Caster Wand(bool focus, bool modded)
        {
            var wand = SummonerFocusTests.Wand(focus);
            wand.RemoveProperty(PropertyInt.CurrentWieldedLocation);

            foreach (var (id, native) in TierA)
            {
                wand.SetProperty(native, LootBase);
                if (modded)
                    WeaponModTinkerSet.ApplySpecialAtFraction(wand, WeaponModRegistry.Get(id), 1.0, 1.0);
            }

            return wand;
        }

        private static Player Owner(WorldObject wand)
        {
            var owner = SummonerFocusTests.OwnerWielding(null);
            CharacterField(owner) = new ACE.Database.Models.Shard.Character();
            CharacterLockField(owner) = new ReaderWriterLockSlim();
            ChildrenField(owner) = new List<HeldItem>();
            EphemeralPositionsField(owner) = new Dictionary<PositionType, Position>();
            PositionCacheField(owner) = new Dictionary<PositionType, Position> { [PositionType.Location] = null };
            if (wand != null)
                Assert.IsTrue(owner.TryEquipObject(wand, EquipMask.Held), "fixture: the equip path that fills the rating cache");
            return owner;
        }

        private static Weenie CreatureWeenie(WeenieType type) => new Weenie
        {
            WeenieClassId = nextWcid++,
            ClassName = "focustierarating",
            WeenieType = type,
            PropertiesInt = new Dictionary<PropertyInt, int>
            {
                [PropertyInt.Level] = 100,
                [PropertyInt.DamageRating] = 7,
                [PropertyInt.CritRating] = 4,
                [PropertyInt.CritDamageRating] = 5,
            },
            PropertiesString = new Dictionary<PropertyString, string> { [PropertyString.Name] = "Focus Rating Creature" },
        };

        private static CombatPet Pet(Player owner)
        {
            var p = new CombatPet(CreatureWeenie(WeenieType.CombatPet), new ObjectGuid(nextGuid++));
            p.P_PetOwner = owner;
            return p;
        }

        private static int Rating(Creature c, PropertyInt native) => native switch
        {
            PropertyInt.GearDamage => c.GetDamageRating(),
            PropertyInt.GearCrit => c.GetCritRating(),
            PropertyInt.GearCritDamage => c.GetCritDamageRating(),
            _ => throw new ArgumentOutOfRangeException(nameof(native)),
        };

        /// <summary>The mod-written amount on the item, derived the way the suppression path derives it.</summary>
        private static int Amount(WorldObject wand, PropertyInt native)
        {
            var amount = WeaponModSuppression.TierARatingOffset(new[] { wand }, native);
            Assert.IsTrue(amount > 0, $"positive control: the {native} mod amount must be nonzero");
            return amount;
        }

        private static void WithSuppressed(Action body)
        {
            var saved = WeaponModSuppression.PlayerSuppressedSource;
            try
            {
                WeaponModSuppression.PlayerSuppressedSource = _ => true;
                body();
            }
            finally
            {
                WeaponModSuppression.PlayerSuppressedSource = saved;
            }
        }

        private static void PetGets(PropertyInt native)
        {
            var focus = Wand(focus: true, modded: true);
            var petBase = Rating(Pet(Owner(null)), native);
            var pet = Pet(Owner(focus));

            Assert.AreEqual(petBase + Amount(focus, native), Rating(pet, native));
        }

        [TestMethod]
        public void Pet_gets_bloodthirst() => PetGets(PropertyInt.GearDamage);

        [TestMethod]
        public void Pet_gets_weak_point() => PetGets(PropertyInt.GearCrit);

        [TestMethod]
        public void Pet_gets_devastation() => PetGets(PropertyInt.GearCritDamage);

        [TestMethod]
        public void Pet_of_a_plain_wand_owner_gets_nothing()
        {
            var petBase = Rating(Pet(Owner(null)), PropertyInt.GearDamage);
            Assert.AreEqual(petBase, Rating(Pet(Owner(Wand(focus: false, modded: true))), PropertyInt.GearDamage));
        }

        [TestMethod]
        public void Wielder_loses_all_three()
        {
            foreach (var (_, native) in TierA)
            {
                var cleared = Rating(Owner(Wand(focus: true, modded: false)), native);
                var plain = Wand(focus: false, modded: true);

                Assert.AreEqual(cleared + Amount(plain, native), Rating(Owner(plain), native), $"positive control: a plain wand's {native} mod reaches its wielder");
                Assert.AreEqual(cleared, Rating(Owner(Wand(focus: true, modded: true)), native), $"the focus wielder keeps the loot {native} and loses the mod amount");
            }
        }

        [TestMethod]
        public void Swap_moves_them_back()
        {
            var focus = Wand(focus: true, modded: true);
            var owner = Owner(focus);
            var pet = Pet(owner);
            var petBase = Rating(Pet(Owner(null)), PropertyInt.GearCritDamage);
            var ownerBare = Rating(Owner(null), PropertyInt.GearCritDamage);

            Assert.AreEqual(petBase + Amount(focus, PropertyInt.GearCritDamage), Rating(pet, PropertyInt.GearCritDamage), "fixture: equipped");

            Assert.IsTrue(owner.TryDequipObject(focus.Guid, out _, out _));
            Assert.AreEqual(petBase, Rating(pet, PropertyInt.GearCritDamage), "unequipped: the pet loses it");
            Assert.AreEqual(ownerBare, Rating(owner, PropertyInt.GearCritDamage), "unequipped: no strip and no cache entry left on the owner");

            var plain = Wand(focus: false, modded: true);
            Assert.IsTrue(owner.TryEquipObject(plain, EquipMask.Held));
            Assert.AreEqual(petBase, Rating(pet, PropertyInt.GearCritDamage), "a plain wand feeds no pet");
            Assert.AreEqual(ownerBare + LootBase + Amount(plain, PropertyInt.GearCritDamage), Rating(owner, PropertyInt.GearCritDamage), "a plain wand's mod is the wielder's again");
        }

        [TestMethod]
        public void Suppressed_owner_counts_once()
        {
            foreach (var (_, native) in TierA)
            {
                var focus = Wand(focus: true, modded: true);
                var owner = Owner(focus);
                var pet = Pet(owner);
                var cleared = Rating(Owner(Wand(focus: true, modded: false)), native);
                var petBase = Rating(Pet(Owner(null)), native);

                WithSuppressed(() =>
                {
                    Assert.AreEqual(cleared, Rating(owner, native), $"{native}: the suppression offset removes the amount once, no strip on top");
                    Assert.AreEqual(petBase, Rating(pet, native), $"{native}: a suppressed owner's focus feeds no pet");
                });
            }
        }

        [TestMethod]
        public void Pvp_match_owner()
        {
            var focus = Wand(focus: true, modded: true);
            var owner = Owner(focus);
            owner.SetPvpBindingForTests(SummonerFocusTests.LiveBinding());
            Assert.IsTrue(owner.IsInPvpMatch, "fixture: in-match");

            var pet = Pet(owner);
            var cleared = Rating(Owner(Wand(focus: true, modded: false)), PropertyInt.GearDamage);

            Assert.AreEqual(Rating(Pet(Owner(null)), PropertyInt.GearDamage), Rating(pet, PropertyInt.GearDamage), "no pet bonus in a PvP match");
            Assert.AreEqual(cleared, Rating(owner, PropertyInt.GearDamage), "the wielder strip still applies in PvP");
        }

        [TestMethod]
        public void Paragon_gem_amount_moves_too()
        {
            var focus = Wand(focus: true, modded: false);
            focus.SetProperty(ParagonModRegistry.Slots[0], (int)WeaponModId.Bloodthirst);
            var add = WeaponModParagon.TierARatingAdd(new[] { focus }, PropertyInt.GearDamage, true, 1.0);
            Assert.IsTrue(add > 0, "positive control: the gem adds rating");

            var cleared = Rating(Owner(Wand(focus: true, modded: false)), PropertyInt.GearDamage);
            var petBase = Rating(Pet(Owner(null)), PropertyInt.GearDamage);
            var owner = Owner(focus);

            Assert.AreEqual(add, SummonerFocus.FocusTierARating(focus, PropertyInt.GearDamage));
            Assert.AreEqual(cleared, Rating(owner, PropertyInt.GearDamage), "the gem's amount leaves the wielder");
            Assert.AreEqual(petBase + add, Rating(Pet(owner), PropertyInt.GearDamage), "and reaches the pet");
        }

        [TestMethod]
        public void Echo_scope_strips_only_what_was_counted()
        {
            // Inside a Hunter's Echo weapon scope RatingAddFor counts the Echo's snapshot weapon, not the focus, so the
            // strip must take off the focus's mod-written native and not its (never added) Paragon amount
            // A partial roll, so the gem has headroom above the record (a perfect roll leaves the gem nothing to add)
            var focus = Wand(focus: true, modded: false);
            WeaponModTinkerSet.ApplySpecialAtFraction(focus, WeaponModRegistry.Get(WeaponModId.Bloodthirst), 0.3, 1.0);
            focus.SetProperty(ParagonModRegistry.Slots[0], (int)WeaponModId.Bloodthirst);
            Assert.IsTrue(WeaponModSuppression.TierARatingOffset(new[] { focus }, PropertyInt.GearDamage) > 0, "positive control: the mod wrote a native");
            Assert.IsTrue(WeaponModParagon.TierARatingAdd(new[] { focus }, PropertyInt.GearDamage, true, 1.0) > 0, "positive control: the gem adds rating");

            var owner = Owner(focus);
            var clearedOwner = Owner(Wand(focus: true, modded: false));
            var snapshot = new ACE.Server.ClassAbilities.HuntersEchoWeapons(Wand(focus: false, modded: false), null, null);

            var outside = owner.GetDamageRating();
            Assert.AreEqual(clearedOwner.GetDamageRating(), outside, "fixture: outside the scope");

            var previous = owner.EnterHuntersEchoWeaponScope(snapshot);
            try
            {
                Assert.AreEqual(outside, owner.GetDamageRating(), "inside the scope: same wielder rating, no Paragon over-subtract");
            }
            finally
            {
                Creature.RestoreHuntersEchoWeaponScope(previous);
            }
        }

        [TestMethod]
        public void A_monster_wielding_a_focus_keeps_its_rating()
        {
            var focus = Wand(focus: true, modded: true);
            var monster = new Creature(CreatureWeenie(WeenieType.Creature), new ObjectGuid(nextGuid++));
            var bare = monster.GetDamageRating();
            Assert.IsTrue(monster.TryEquipObject(focus, EquipMask.Held));

            Assert.AreEqual(bare + LootBase + Amount(focus, PropertyInt.GearDamage), monster.GetDamageRating(), "the strip is for Player wielders only");
            Assert.AreEqual(0, SummonerFocus.WielderFocusRatingStrip(monster, PropertyInt.GearDamage));
            Assert.AreEqual(0, SummonerFocus.PetFocusRatingBonus(monster, PropertyInt.GearDamage), "not a pet");
        }
    }
}
