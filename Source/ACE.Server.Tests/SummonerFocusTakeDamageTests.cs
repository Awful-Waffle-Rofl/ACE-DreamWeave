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
using ACE.Server.Tests.Pvp;
using ACE.Server.WorldObjects;

namespace ACE.Server.Tests
{
    /// <summary>
    /// The Summoner casters focus multiplier at its first damage sink, Creature.TakeDamage, driven live: every assert is on
    /// Health.Current before and after the real call. Seeds every PropertyManager key it reads and restores it from the defaults.
    /// </summary>
    [TestClass]
    public class SummonerFocusTakeDamageTests
    {
        private static uint nextGuid = 0x7E500000;
        private static uint nextWcid = 996300;

        private static readonly string[] DoubleKeys =
        {
            SummonerFocus.PetDamageScaleKey, SummonerFocus.PetDefenseScaleKey, SummonerFocus.PetDefenseFloorKey, SummonerFocus.WielderDamageModKey,
            "weapon_mod_magnitude_scale",
        };

        // A pet hit runs Pet.ApplyUmbralSiphonLeech, which reads the owner's class-ability cache from Character quests, so the
        // seeded owner needs an empty Character and its lock. UnsafeAccessor, never FieldInfo.SetValue on a Player (static ctor).
        [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "<Character>k__BackingField")]
        private static extern ref ACE.Database.Models.Shard.Character CharacterField(Player player);

        [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "set_IsHuntersEcho")]
        private static extern void SetIsHuntersEcho(CombatPet pet, bool value);

        [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "CharacterDatabaseLock")]
        private static extern ref ReaderWriterLockSlim CharacterLockField(Player player);

        private static Player Owner(WorldObject wand)
        {
            var owner = SummonerFocusTests.OwnerWielding(wand);
            CharacterField(owner) = new ACE.Database.Models.Shard.Character();
            CharacterLockField(owner) = new ReaderWriterLockSlim();
            return owner;
        }

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

        private static Weenie VictimWeenie(WeenieType type) => new Weenie
        {
            WeenieClassId = nextWcid++,
            ClassName = "focustakedamagetest",
            WeenieType = type,
            PropertiesInt = new Dictionary<PropertyInt, int> { [PropertyInt.Level] = 100 },
            PropertiesString = new Dictionary<PropertyString, string> { [PropertyString.Name] = "Focus Victim" },
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
            var c = new Creature(VictimWeenie(WeenieType.Creature), new ObjectGuid(nextGuid++));
            c.Health.Current = c.Health.MaxValue;
            return c;
        }

        private static CombatPet Pet(Player owner)
        {
            var p = new CombatPet(VictimWeenie(WeenieType.CombatPet), new ObjectGuid(nextGuid++));
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

        [TestMethod]
        public void Fixture_has_health_to_lose()
        {
            // MaxHealth is the 1000 InitLevel plus the endurance contribution, so only assert it is at least 1000 and full
            var monster = Monster();
            var pet = Pet(null);
            Assert.IsTrue(monster.Health.MaxValue >= 1000u);
            Assert.AreEqual(monster.Health.MaxValue, monster.Health.Current);
            Assert.IsTrue(pet.Health.MaxValue >= 1000u);
            Assert.AreEqual(pet.Health.MaxValue, pet.Health.Current);
        }

        [TestMethod]
        public void Pet_melee_hit_is_scaled_by_the_owners_focus()
        {
            var owner = Owner(SummonerFocusTests.Wand(true));
            var monster = Monster();
            Assert.AreEqual(170u, Drop(monster, Pet(owner), DamageType.Slash, 100f));
        }

        [TestMethod]
        public void Monster_hit_on_the_pet_is_reduced()
        {
            var owner = Owner(SummonerFocusTests.Wand(true));
            Assert.AreEqual(65u, Drop(Pet(owner), Monster(), DamageType.Slash, 100f));
        }

        [TestMethod]
        public void Dot_tick_on_the_pet_is_reduced()
        {
            var owner = Owner(SummonerFocusTests.Wand(true));
            Assert.AreEqual(65u, Drop(Pet(owner), null, DamageType.Fire, 100f));
        }

        [TestMethod]
        public void Wielder_hit_is_penalised()
        {
            var owner = Owner(SummonerFocusTests.Wand(true));
            Assert.AreEqual(25u, Drop(Monster(), owner, DamageType.Slash, 100f));
        }

        [TestMethod]
        public void Swap_removes_the_bonus_at_the_next_hit()
        {
            var focus = SummonerFocusTests.Wand(true);
            var owner = Owner(focus);
            var pet = Pet(owner);
            var monster = Monster();

            Assert.AreEqual(170u, Drop(monster, pet, DamageType.Slash, 100f));
            owner.EquippedObjects.Remove(focus.Guid);
            Assert.AreEqual(100u, Drop(monster, pet, DamageType.Slash, 100f));
        }

        [TestMethod]
        public void Both_pets_under_Summon_2x_get_it()
        {
            var owner = Owner(SummonerFocusTests.Wand(true));
            var monster = Monster();
            Assert.AreEqual(170u, Drop(monster, Pet(owner), DamageType.Slash, 100f));
            Assert.AreEqual(170u, Drop(monster, Pet(owner), DamageType.Slash, 100f));
        }

        [TestMethod]
        public void Pvp_binding_turns_the_pet_bonus_off()
        {
            var owner = Owner(SummonerFocusTests.Wand(true));
            owner.SetPvpBindingForTests(SummonerFocusTests.LiveBinding());
            Assert.IsTrue(owner.IsInPvpMatch, "positive control: the binding reads as in-match");

            Assert.AreEqual(100u, Drop(Monster(), Pet(owner), DamageType.Slash, 100f));
        }

        /// <summary>
        /// Elemental Proficiency's Acid burn: sized from the bolt's applied damage (already cut) and landed tick by tick
        /// through Creature.TakeDamage (cut again), so the sizing divides the bolt's cut back out. A 100 bolt lands 25;
        /// the burn's 60% over 3 ticks is 20 pre-cut per tick, landing 5: one cut. Two cuts would land round(1.25) = 1.
        /// </summary>
        [TestMethod]
        public void Acid_burn_ticks_carry_the_wielder_cut_once()
        {
            var focus = SummonerFocusTests.Wand(true);
            var owner = Owner(focus);

            var applied = Drop(Monster(), owner, DamageType.Acid, 100f);
            var recordedCut = SummonerFocus.WielderDamageMod(owner, Monster(), focus);
            Assert.AreEqual(25u, applied, "positive control: the bolt itself is cut");
            Assert.AreEqual(0.25f, recordedCut, 1e-6f);

            var tick = Player.ElementalAcidTickAmount(applied, recordedCut, 0.6, 3);
            Assert.AreEqual(20u, tick, "sized from the bolt with its cut divided back out");
            Assert.AreEqual(5u, Drop(Monster(), owner, DamageType.Base, tick), "the tick lands with the one cut");
        }

        [TestMethod]
        public void Acid_burn_ticks_are_unchanged_without_a_focus()
        {
            var owner = Owner(SummonerFocusTests.Wand(false));

            var applied = Drop(Monster(), owner, DamageType.Acid, 100f);
            var tick = Player.ElementalAcidTickAmount(applied, SummonerFocus.WielderDamageMod(owner, Monster()), 0.6, 3);

            Assert.AreEqual(100u, applied);
            Assert.AreEqual(20u, tick);
            Assert.AreEqual(20u, Drop(Monster(), owner, DamageType.Base, tick));
        }

        [TestMethod]
        public void Acid_burn_tick_sizing_keeps_its_floor_and_zero()
        {
            Assert.AreEqual(0u, Player.ElementalAcidTickAmount(0, 1.0f, 0.6, 3), "no damage, no burn");
            Assert.AreEqual(0u, Player.ElementalAcidTickAmount(0, 0.0f, 0.6, 3), "a zero cut dealt nothing, so no burn");
            Assert.AreEqual(1u, Player.ElementalAcidTickAmount(1, 1.0f, 0.1, 3), "a positive burn is at least 1 per tick");
        }

        /// <summary>
        /// A focus wielder's hit on a player, absorbed by the victim's Hunter's Echo: the sink already cut it (100 to 25),
        /// and the Echo takes the handed-over 25 through Creature.TakeDamage uncut. Cut twice it would drop 6.
        /// </summary>
        [TestMethod]
        public void Echo_absorbed_hit_is_cut_exactly_once()
        {
            var wielder = Owner(SummonerFocusTests.Wand(true));
            var echo = Pet(Owner(null));
            SetIsHuntersEcho(echo, true);

            var handedOver = (uint)System.Math.Round(100f * SummonerFocus.WielderDamageMod(wielder, Monster()));
            Assert.AreEqual(25u, handedOver, "positive control: the hit reaching the absorb is already cut");

            var before = echo.Health.Current;
            echo.ApplyHuntersEchoAbsorbedHit(wielder, DamageType.Slash, handedOver);
            Assert.AreEqual(25u, before - echo.Health.Current);
        }

        [TestMethod]
        public void A_heal_is_never_scaled()
        {
            var owner = Owner(SummonerFocusTests.Wand(true));
            var monster = Monster();
            monster.Health.Current = 500;

            monster.TakeDamage(owner, DamageType.Slash, -100f);

            Assert.AreEqual(600u, monster.Health.Current);
        }
    }
}
