using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Entity;
using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Entity.Models;
using ACE.Server.Entity;
using ACE.Server.Managers;
using ACE.Server.Tests.Pvp;
using ACE.Server.WeaponMods;
using ACE.Server.WorldObjects;

namespace ACE.Server.Tests
{
    /// <summary>
    /// Tier B weapon mods on a Summoner focus (Ambush, Precision, Execution) reach the owner's combat pets and never the
    /// wielder, driven live through the real getters and Creature.TakeDamage. Seeds every PropertyManager key it reads and
    /// restores it from the defaults.
    /// </summary>
    [TestClass]
    public class SummonerFocusWeaponModTests
    {
        private static uint nextGuid = 0x7E600000;
        private static uint nextWcid = 996500;

        private static readonly string[] DoubleKeys =
        {
            SummonerFocus.PetDamageScaleKey, SummonerFocus.PetDefenseScaleKey, SummonerFocus.PetDefenseFloorKey, SummonerFocus.WielderDamageModKey,
            "weapon_mod_magnitude_scale",
        };

        // A pet hit runs Pet.ApplyUmbralSiphonLeech, which reads the owner's class-ability cache from Character quests, so the
        // seeded owner needs an empty Character and its lock. UnsafeAccessor, never FieldInfo.SetValue on a Player (static ctor).
        [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "<Character>k__BackingField")]
        private static extern ref ACE.Database.Models.Shard.Character CharacterField(Player player);

        [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "CharacterDatabaseLock")]
        private static extern ref ReaderWriterLockSlim CharacterLockField(Player player);

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

        private static readonly PropertyFloat[] FocusRecords =
        {
            PropertyFloat.WeaponModAmbush, PropertyFloat.WeaponModFocus, PropertyFloat.WeaponModExecution, PropertyFloat.WeaponModSiphon,
        };

        /// <summary>A wand (Summoner focus or plain) carrying Ambush, Precision, Execution and Siphon records at a perfect roll.</summary>
        private static Caster ModdedWand(bool focus)
        {
            var wand = SummonerFocusTests.Wand(focus);
            foreach (var record in FocusRecords)
                wand.SetProperty(record, 1.0);
            return wand;
        }

        private static double Mag(WeaponModId id, WorldObject item) =>
            WeaponModValue.MagnitudeFromFraction(WeaponModRegistry.Get(id), 1.0, 1.0, item);

        private static Player Owner(WorldObject wand)
        {
            var owner = SummonerFocusTests.OwnerWielding(wand);
            CharacterField(owner) = new ACE.Database.Models.Shard.Character();
            CharacterLockField(owner) = new ReaderWriterLockSlim();
            return owner;
        }

        private static Weenie CreatureWeenie(WeenieType type) => new Weenie
        {
            WeenieClassId = nextWcid++,
            ClassName = "focusweaponmodtest",
            WeenieType = type,
            PropertiesInt = new Dictionary<PropertyInt, int> { [PropertyInt.Level] = 100 },
            PropertiesString = new Dictionary<PropertyString, string> { [PropertyString.Name] = "Focus Mod Creature" },
            PropertiesAttribute = new Dictionary<PropertyAttribute, PropertiesAttribute>
            {
                [PropertyAttribute.Endurance] = new PropertiesAttribute { InitLevel = 100 },
            },
            PropertiesAttribute2nd = new Dictionary<PropertyAttribute2nd, PropertiesAttribute2nd>
            {
                [PropertyAttribute2nd.MaxHealth] = new PropertiesAttribute2nd { InitLevel = 1000 },
                [PropertyAttribute2nd.MaxStamina] = new PropertiesAttribute2nd { InitLevel = 200 },
                [PropertyAttribute2nd.MaxMana] = new PropertiesAttribute2nd { InitLevel = 200 },
            },
        };

        private static Creature Monster()
        {
            var c = new Creature(CreatureWeenie(WeenieType.Creature), new ObjectGuid(nextGuid++));
            c.Health.Current = c.Health.MaxValue;
            return c;
        }

        private static CombatPet Pet(Player owner)
        {
            var p = new CombatPet(CreatureWeenie(WeenieType.CombatPet), new ObjectGuid(nextGuid++));
            p.P_PetOwner = owner;
            p.Health.Current = p.Health.MaxValue;
            return p;
        }

        private static uint Drop(Creature c, WorldObject source, DamageType type, float amount)
        {
            var before = c.Health.Current;
            c.TakeDamage(source, type, amount);
            return before - c.Health.Current;
        }

        // AreEqual(double, double, delta) passes on NaN, so every tolerance compare goes through this
        private static void AssertNear(double expected, double actual, double tolerance, string because = null) =>
            Assert.IsTrue(!double.IsNaN(actual) && Math.Abs(expected - actual) < tolerance, $"expected {expected}, got {actual}. {because}");

        [TestMethod]
        public void Magnitudes_are_nonzero()
        {
            var wand = ModdedWand(true);
            Assert.IsTrue(Mag(WeaponModId.Ambush, wand) > 0.0);
            Assert.IsTrue(Mag(WeaponModId.Focus, wand) > 0.0);
            Assert.IsTrue(Mag(WeaponModId.Execution, wand) > 0.0);
        }

        [TestMethod]
        public void Pet_ambush_scales_the_opener_only()
        {
            var focus = ModdedWand(true);
            var pet = Pet(Owner(focus));
            var monster = Monster();

            var expectedOpener = (uint)Math.Round(100.0 * 1.7 * (1.0 + Mag(WeaponModId.Ambush, focus)));
            Assert.AreNotEqual(170u, expectedOpener, "positive control: Ambush must change the opener");

            Assert.AreEqual(expectedOpener, Drop(monster, pet, DamageType.Slash, 100f), "the opener at full health");
            Assert.AreEqual(170u, Drop(monster, pet, DamageType.Slash, 100f), "the second hit finds the target damaged");
        }

        [TestMethod]
        public void Pet_precision_adds_crit_chance()
        {
            var focus = ModdedWand(true);
            var owner = Owner(focus);
            var pet = Pet(owner);
            var monster = Monster();

            var with = WorldObject.GetWeaponCriticalChancePreResist(null, pet, null, monster);
            owner.EquippedObjects.Remove(focus.Guid);
            var without = WorldObject.GetWeaponCriticalChancePreResist(null, pet, null, monster);

            AssertNear(Mag(WeaponModId.Focus, focus), with - without, 1e-6);
        }

        [TestMethod]
        public void Pet_precision_reaches_the_no_launcher_magic_path()
        {
            var focus = ModdedWand(true);
            var pet = Pet(Owner(focus));
            var monster = Monster();

            AssertNear(0.05f + (float)Mag(WeaponModId.Focus, focus), WorldObject.GetWeaponMagicCritFrequency(null, pet, null, monster), 1e-6, "the weapon == null return");
            AssertNear(0.05f + (float)Mag(WeaponModId.Focus, focus), WorldObject.GetWeaponMagicCritFrequencyPreResist(null, pet, null, monster), 1e-6, "the pre-resist weapon == null return");
        }

        [TestMethod]
        public void Pet_precision_reaches_the_magic_path_with_a_launcher()
        {
            var focus = ModdedWand(true);
            var owner = Owner(focus);
            var pet = Pet(owner);
            var monster = Monster();
            var launcher = SummonerFocusTests.Wand(false);

            var with = WorldObject.GetWeaponMagicCritFrequencyPreResist(launcher, pet, null, monster);
            owner.EquippedObjects.Remove(focus.Guid);
            var without = WorldObject.GetWeaponMagicCritFrequencyPreResist(launcher, pet, null, monster);

            AssertNear(Mag(WeaponModId.Focus, focus), with - without, 1e-6);
        }

        [TestMethod]
        public void Pet_execution_reads_the_focus()
        {
            var focus = ModdedWand(true);
            var pet = Pet(Owner(focus));

            AssertNear(Mag(WeaponModId.Execution, focus), SummonerFocus.PetFocusModValue(pet, Monster(), WeaponModId.Execution), 1e-9);
            Assert.AreEqual(0.0, SummonerFocus.PetFocusModValue(pet, PvpTemplatePlayerTests.SeededPlayer(), WeaponModId.Execution), "PvP guard: a Player target");
        }

        [TestMethod]
        public void Pet_reads_nothing_off_a_plain_wand_or_a_utility_mod()
        {
            var pet = Pet(Owner(ModdedWand(false)));
            Assert.AreEqual(0.0, SummonerFocus.PetFocusModValue(pet, Monster(), WeaponModId.Execution), "a plain wand is not a focus");

            var focusPet = Pet(Owner(ModdedWand(true)));
            var efficient = ModdedWand(true);
            efficient.SetProperty(WeaponModRegistry.Get(WeaponModId.Efficiency).Record, 1.0);
            Assert.IsTrue(WeaponModCombat.ReadWeaponOnly(efficient, WeaponModId.Efficiency, true, 1.0) > 0.0, "positive control: the focus carries Efficiency");
            Assert.AreEqual(0.0, SummonerFocus.PetFocusModValue(Pet(Owner(efficient)), Monster(), WeaponModId.Efficiency), "Efficiency (Wielder route) is not transferred");
        }

        [TestMethod]
        public void Owner_suppression_turns_the_pet_mods_off()
        {
            var focus = ModdedWand(true);
            var pet = Pet(Owner(focus));
            var monster = Monster();

            Assert.IsTrue(SummonerFocus.PetFocusModValue(pet, monster, WeaponModId.Execution) > 0.0, "positive control before suppression");

            var saved = WeaponModSuppression.PlayerSuppressedSource;
            try
            {
                WeaponModSuppression.PlayerSuppressedSource = _ => true;

                Assert.AreEqual(0.0, SummonerFocus.PetFocusModValue(pet, monster, WeaponModId.Ambush));
                Assert.AreEqual(0.0, SummonerFocus.PetFocusModValue(pet, monster, WeaponModId.Focus));
                Assert.AreEqual(0.0, SummonerFocus.PetFocusModValue(pet, monster, WeaponModId.Execution));
            }
            finally
            {
                WeaponModSuppression.PlayerSuppressedSource = saved;
            }
        }

        [TestMethod]
        public void Wielder_never_gets_them()
        {
            var focus = ModdedWand(true);
            var plain = ModdedWand(false);

            foreach (var id in new[] { WeaponModId.Ambush, WeaponModId.Focus, WeaponModId.Execution })
            {
                Assert.IsTrue(WeaponModCombat.ReadWeaponOnly(plain, id, true, 1.0) > 0.0, $"positive control: a plain wand reads {id}");
                Assert.AreEqual(0.0, WeaponModCombat.ReadWeaponOnly(focus, id, true, 1.0), $"the focus strips {id}");
            }

            Assert.IsTrue(Owner(plain).GetWeaponModSpellDamageMod(Monster()) > 1.0f, "positive control: a plain wand's Ambush reaches its wielder");
            Assert.AreEqual(1.0f, Owner(focus).GetWeaponModSpellDamageMod(Monster()), "the focus wielder's spell Ambush is gone");

            // a Shared mod stays with the wielder too
            focus.SetProperty(WeaponModRegistry.Get(WeaponModId.DefensiveWeapon).Record, 1.0);
            Assert.IsTrue(WeaponModCombat.ReadWeaponOnly(focus, WeaponModId.DefensiveWeapon, true, 1.0) > 0.0, "Shared: the focus wielder keeps Defensive Weapon");
        }

        [TestMethod]
        public void Shared_mods_reach_the_pet_and_stay_with_the_wielder()
        {
            var focus = ModdedWand(true);
            focus.SetProperty(WeaponModRegistry.Get(WeaponModId.DefensiveWeapon).Record, 1.0);
            var pet = Pet(Owner(focus));
            var monster = Monster();

            foreach (var id in new[] { WeaponModId.DefensiveWeapon, WeaponModId.Siphon })
            {
                AssertNear(Mag(id, focus), SummonerFocus.PetFocusModValue(pet, monster, id), 1e-9, $"the pet reads {id} at the focus magnitude");
                AssertNear(Mag(id, focus), WeaponModCombat.ReadWeaponOnly(focus, id, true, 1.0), 1e-9, $"the wielder keeps {id}");
                Assert.AreEqual(0.0, SummonerFocus.PetFocusModValue(pet, PvpTemplatePlayerTests.SeededPlayer(), id), $"PvP guard: a Player target reads no {id}");
            }

            Assert.AreEqual(0.0, SummonerFocus.PetFocusModValue(Pet(Owner(ModdedWand(false))), monster, WeaponModId.Siphon), "a plain wand is not a focus");
        }

        /// <summary>
        /// Every WeaponModId is classified: a new id fails here until it is placed in the route table on purpose.
        /// </summary>
        [TestMethod]
        public void PetRoute_ClassifiesEveryWeaponModId()
        {
            var expected = new Dictionary<WeaponModId, SummonerPetRoute>
            {
                [WeaponModId.Devastation] = SummonerPetRoute.PetsOnly,
                [WeaponModId.WeakPoint] = SummonerPetRoute.PetsOnly,
                [WeaponModId.Bloodthirst] = SummonerPetRoute.PetsOnly,
                [WeaponModId.ShieldBypass] = SummonerPetRoute.Wielder,
                [WeaponModId.Ambush] = SummonerPetRoute.PetsOnly,
                [WeaponModId.Quickening] = SummonerPetRoute.Wielder,
                [WeaponModId.SecondWind] = SummonerPetRoute.Shared,
                [WeaponModId.Heft] = SummonerPetRoute.Wielder,
                [WeaponModId.Tension] = SummonerPetRoute.Wielder,
                [WeaponModId.Leverage] = SummonerPetRoute.Wielder,
                [WeaponModId.Attunement] = SummonerPetRoute.Wielder,
                [WeaponModId.Focus] = SummonerPetRoute.PetsOnly,
                [WeaponModId.Execution] = SummonerPetRoute.PetsOnly,
                [WeaponModId.Efficiency] = SummonerPetRoute.Wielder,
                [WeaponModId.Recovery] = SummonerPetRoute.Wielder,
                [WeaponModId.ManaWell] = SummonerPetRoute.Wielder,
                [WeaponModId.Cleanse] = SummonerPetRoute.Shared,
                [WeaponModId.Longevity] = SummonerPetRoute.Wielder,
                [WeaponModId.Siphon] = SummonerPetRoute.Shared,
                [WeaponModId.QuickRefresh] = SummonerPetRoute.Wielder,
                [WeaponModId.DefensiveWeapon] = SummonerPetRoute.Shared,
                [WeaponModId.PanicReload] = SummonerPetRoute.Wielder,
            };

            var ids = Enum.GetValues(typeof(WeaponModId)).Cast<WeaponModId>().ToArray();
            CollectionAssert.AreEquivalent(expected.Keys.ToArray(), ids, "every WeaponModId has an expected route");

            foreach (var id in ids)
                Assert.AreEqual(expected[id], SummonerFocus.PetRoute(WeaponModRegistry.Get(id)), $"{id}");

            Assert.AreEqual(SummonerPetRoute.Wielder, SummonerFocus.PetRoute(null));
        }

        [TestMethod]
        public void Utility_and_attunement_are_untouched()
        {
            var focus = ModdedWand(true);
            Assert.IsTrue(WeaponModCombat.ReadWeaponOnly(focus, WeaponModId.Siphon, true, 1.0) > 0.0, "Siphon stays with the wielder");

            var attuned = SummonerFocusTests.Wand(true);
            attuned.SetProperty(PropertyFloat.WeaponModAttunement, 1.0);
            Assert.IsTrue(WeaponModCombat.ReadWeaponOnly(attuned, WeaponModId.Attunement, true, 1.0) > 0.0, "Attunement keeps feeding the damage mod");
        }

        [TestMethod]
        public void Transferred_set_is_exactly_six()
        {
            var transferred = WeaponModRegistry.AllMods.Where(SummonerFocus.IsPetTransferredMod).Select(d => d.Id).OrderBy(id => id).ToArray();
            var expected = new[] { WeaponModId.Devastation, WeaponModId.WeakPoint, WeaponModId.Bloodthirst, WeaponModId.Ambush, WeaponModId.Focus, WeaponModId.Execution }.OrderBy(id => id).ToArray();

            CollectionAssert.AreEqual(expected, transferred, string.Join(",", transferred));
        }

        [TestMethod]
        public void Simulator_seam_feeds_an_ownerless_pet_and_resets()
        {
            var focus = ModdedWand(true);
            var pet = Pet(null);
            var monster = Monster();

            Assert.AreEqual(0.0, SummonerFocus.PetFocusModValue(pet, monster, WeaponModId.Execution), "no seam, no owner: nothing");

            try
            {
                SummonerFocus.SimulatedPetFocusSource = _ => focus;
                AssertNear(Mag(WeaponModId.Execution, focus), SummonerFocus.PetFocusModValue(pet, monster, WeaponModId.Execution), 1e-9);
                Assert.AreEqual(1.0f, SummonerFocus.PetTakenMod(pet, monster), "the taken side never reads the seam");
            }
            finally
            {
                SummonerFocus.SimulatedPetFocusSource = null;
            }

            Assert.AreEqual(0.0, SummonerFocus.PetFocusModValue(pet, monster, WeaponModId.Execution), "reset");
        }
    }
}
