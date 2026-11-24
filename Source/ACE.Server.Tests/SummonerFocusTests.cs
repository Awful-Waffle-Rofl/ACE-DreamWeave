using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Entity;
using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Entity.Models;
using ACE.Server.Entity;
using ACE.Server.Managers;
using ACE.Server.Pvp;
using ACE.Server.Tests.Pvp;
using ACE.Server.WorldObjects;

namespace ACE.Server.Tests
{
    /// <summary>
    /// Live resolution of the Summoner casters mods (SummonerFocus): who a hit applies to, read from the owner's
    /// wielded focus at the hit. Seeds every PropertyManager key it reads and restores them from the code defaults.
    /// </summary>
    [TestClass]
    public class SummonerFocusTests
    {
        private static uint nextGuid = 0x7E400000;
        private static uint nextWcid = 996100;

        private static readonly string[] DoubleKeys =
        {
            SummonerFocus.PetDamageScaleKey, SummonerFocus.PetDefenseScaleKey, SummonerFocus.PetDefenseFloorKey, SummonerFocus.WielderDamageModKey,
            "weapon_mod_magnitude_scale",
        };

        [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "set_IsHuntersEcho")]
        private static extern void SetIsHuntersEcho(CombatPet pet, bool value);

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

        internal static Caster Wand(bool focus, double edm = 1.14)
        {
            var weenie = new Weenie
            {
                WeenieClassId = nextWcid++,
                WeenieType = WeenieType.Caster,
                PropertiesInt = new Dictionary<PropertyInt, int> { { PropertyInt.ItemType, (int)ItemType.Caster } },
                PropertiesFloat = new Dictionary<PropertyFloat, double> { { PropertyFloat.ElementalDamageMod, edm } },
                PropertiesBool = new Dictionary<PropertyBool, bool>(),
            };

            if (focus)
                weenie.PropertiesBool[PropertyBool.SummonerFocus] = true;

            var wand = new Caster(weenie, new ObjectGuid(nextGuid++));
            wand.CurrentWieldedLocation = EquipMask.Held;
            return wand;
        }

        internal static Player OwnerWielding(WorldObject wand)
        {
            var owner = PvpTemplatePlayerTests.SeededPlayer();
            if (wand != null)
                owner.EquippedObjects[wand.Guid] = wand;
            return owner;
        }

        private static CombatPet Pet(Player owner, bool echo = false)
        {
            var weenie = new Weenie
            {
                WeenieClassId = nextWcid++,
                WeenieType = WeenieType.CombatPet,
                PropertiesString = new Dictionary<PropertyString, string> { { PropertyString.Name, "Test Pet" } },
            };

            var pet = new CombatPet(weenie, new ObjectGuid(nextGuid++));
            pet.P_PetOwner = owner;
            if (echo)
                SetIsHuntersEcho(pet, true);
            return pet;
        }

        internal static PvpPlayerBinding LiveBinding()
        {
            var now = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            var teams = new List<PvpTeam>
            {
                new PvpTeam(0, new List<PvpParticipant> { new PvpParticipant(1, 1500) }),
                new PvpTeam(1, new List<PvpParticipant> { new PvpParticipant(2, 1500) }),
            };
            return new PvpPlayerBinding(new PvpMatch(Guid.NewGuid(), "arena_1v1", teams, now), 0, PvpMatchState.Live, true, true, true, true, true);
        }

        private static void AssertNear(float expected, float actual) =>
            Assert.IsTrue(!float.IsNaN(actual) && Math.Abs(expected - actual) < 1e-5f, $"expected {expected}, got {actual}");

        [TestMethod]
        public void IsSummonerFocus_NeedsTheCasterTypeAndTheMarker()
        {
            Assert.IsTrue(SummonerFocus.IsSummonerFocus(Wand(true)));
            Assert.IsFalse(SummonerFocus.IsSummonerFocus(Wand(false)));
            Assert.IsFalse(SummonerFocus.IsSummonerFocus(null));
        }

        [TestMethod]
        public void HitMod_ResolvesEveryPairing()
        {
            var focus = Wand(true);
            var owner = OwnerWielding(focus);
            var pet = Pet(owner);
            var monster = TestCreatures.CreateDefender();

            Assert.AreEqual(1.70f, SummonerFocus.HitMod(pet, monster), 1e-5f, "pet hits monster");
            Assert.AreEqual(0.65f, SummonerFocus.HitMod(monster, pet), 1e-5f, "monster hits pet");
            Assert.AreEqual(0.65f, SummonerFocus.HitMod(null, pet), 1e-5f, "a combined DoT tick on the pet");
            Assert.AreEqual(0.25f, SummonerFocus.HitMod(owner, monster), 1e-5f, "wielder hits monster");
            Assert.AreEqual(1.0f, SummonerFocus.HitMod(owner, owner), "self is never penalised");
            Assert.AreEqual(1.0f, SummonerFocus.HitMod(pet, PvpTemplatePlayerTests.SeededPlayer()), "pet vs player: PvP guard");
            Assert.AreEqual(1.0f, SummonerFocus.HitMod(Pet(null), monster), "P_PetOwner null");
            Assert.AreSame(focus, SummonerFocus.GetWieldedFocus(owner));
        }

        [TestMethod]
        public void ResolvePetFocus_TargetlessReadReturnsTheFocus()
        {
            var focus = Wand(true);
            var pet = Pet(OwnerWielding(focus));

            Assert.AreSame(focus, SummonerFocus.ResolvePetFocus(pet, null));
            Assert.IsNull(SummonerFocus.ResolvePetFocus(pet, PvpTemplatePlayerTests.SeededPlayer()), "a Player target");
            Assert.IsNull(SummonerFocus.ResolvePetFocus(pet, Pet(PvpTemplatePlayerTests.SeededPlayer())), "a player's pet target");
            Assert.IsNull(SummonerFocus.ResolvePetFocus(TestCreatures.CreateAttacker(), null), "not a CombatPet");
            Assert.IsNull(SummonerFocus.ResolvePetFocus(null, null));
        }

        [TestMethod]
        public void UnequippedFocus_IsOneEverywhere()
        {
            var focus = Wand(true);
            var owner = OwnerWielding(focus);
            var pet = Pet(owner);
            var monster = TestCreatures.CreateDefender();

            owner.EquippedObjects.Remove(focus.Guid);

            Assert.AreEqual(1.0f, SummonerFocus.HitMod(pet, monster));
            Assert.AreEqual(1.0f, SummonerFocus.HitMod(owner, monster));
        }

        [TestMethod]
        public void NonFocusWand_IsOneOnEveryPairing()
        {
            var owner = OwnerWielding(Wand(false));
            var pet = Pet(owner);
            var monster = TestCreatures.CreateDefender();

            Assert.AreEqual(1.0f, SummonerFocus.HitMod(pet, monster));
            Assert.AreEqual(1.0f, SummonerFocus.HitMod(monster, pet));
            Assert.AreEqual(1.0f, SummonerFocus.HitMod(null, pet));
            Assert.AreEqual(1.0f, SummonerFocus.HitMod(owner, monster));
        }

        [TestMethod]
        public void OwnerInPvpMatch_PetModsOffWielderCutStays()
        {
            var owner = OwnerWielding(Wand(true));
            owner.SetPvpBindingForTests(LiveBinding());
            Assert.IsTrue(owner.IsInPvpMatch, "the fixture binding must read as in-match");

            var pet = Pet(owner);
            var monster = TestCreatures.CreateDefender();

            Assert.AreEqual(1.0f, SummonerFocus.HitMod(pet, monster));
            Assert.AreEqual(1.0f, SummonerFocus.HitMod(monster, pet));
            Assert.AreEqual(0.25f, SummonerFocus.HitMod(owner, monster), 1e-5f);
        }

        [TestMethod]
        public void HuntersEchoPet_IsOneBothWays()
        {
            var owner = OwnerWielding(Wand(true));
            var echo = Pet(owner, echo: true);
            var monster = TestCreatures.CreateDefender();

            Assert.AreEqual(1.0f, SummonerFocus.HitMod(echo, monster));
            Assert.AreEqual(1.0f, SummonerFocus.HitMod(monster, echo));
        }

        [TestMethod]
        public void ShippedDefaults_GiveOneToOneDamageAndUnchangedDefense()
        {
            try
            {
                // seeded from the code defaults, not literals: this pins what a fresh shard actually runs
                foreach (var key in new[] { SummonerFocus.PetDamageScaleKey, SummonerFocus.PetDefenseScaleKey, SummonerFocus.PetDefenseFloorKey })
                    PropertyManager.ModifyDouble(key, DefaultPropertyManager.DefaultDoubleProperties[key].Item);

                var owner = OwnerWielding(Wand(true, edm: 1.14));
                var pet = Pet(owner);
                var monster = TestCreatures.CreateDefender();

                AssertNear(1.14f, SummonerFocus.HitMod(pet, monster));
                AssertNear(1.0f, SummonerFocus.HitMod(monster, pet));

                var line = SummonerFocusDisplay.GetAppraisalLines(Wand(true, edm: 1.27)).First();
                Assert.AreEqual("- Summoner Focus: your summons deal +27% damage while this is wielded", line);
            }
            finally
            {
                foreach (var key in DoubleKeys)
                    PropertyManager.ModifyDouble(key, DefaultPropertyManager.DefaultDoubleProperties[key].Item);
            }
        }

        [TestMethod]
        public void NanScale_FallsBackToTheDefault()
        {
            var owner = OwnerWielding(Wand(true));
            var pet = Pet(owner);
            var monster = TestCreatures.CreateDefender();

            PropertyManager.ModifyDouble(SummonerFocus.PetDamageScaleKey, double.NaN);

            // AreEqual with a tolerance passes when the actual value is NaN, so compare explicitly
            AssertNear(1.14f, SummonerFocus.HitMod(pet, monster));   // default damage scale 1.0: 1 + 1.0 * 0.14
        }

        [TestMethod]
        public void NullTarget_WielderHit_IsOne()
        {
            var owner = OwnerWielding(Wand(true));
            Assert.AreEqual(1.0f, SummonerFocus.HitMod(owner, null));
        }

        [TestMethod]
        public void TakenDirection_PvpGuards()
        {
            var owner = OwnerWielding(Wand(true));
            var pet = Pet(owner);

            Assert.AreEqual(1.0f, SummonerFocus.HitMod(PvpTemplatePlayerTests.SeededPlayer(), pet), "another player hits the pet");
            Assert.AreEqual(1.0f, SummonerFocus.HitMod(Pet(PvpTemplatePlayerTests.SeededPlayer()), pet), "another player's pet hits the pet");
        }

        [TestMethod]
        public void LauncherFocus_CutsTheWielder_AfterTheFocusIsSwappedOff()
        {
            var focus = Wand(true);
            var owner = OwnerWielding(focus);
            var monster = TestCreatures.CreateDefender();

            // the bolt left from the focus, then the wielder swapped it off before impact
            owner.EquippedObjects.Remove(focus.Guid);
            Assert.IsNull(SummonerFocus.GetWieldedFocus(owner), "positive control: nothing is wielded at impact");

            Assert.AreEqual(0.25f, SummonerFocus.WielderDamageMod(owner, monster, focus), 1e-5f);
            Assert.AreEqual(0.25f, SummonerFocus.HitMod(owner, monster, focus), 1e-5f);
            Assert.AreEqual(1.0f, SummonerFocus.HitMod(owner, monster), "no launcher: the live read alone");
            Assert.AreEqual(1.0f, SummonerFocus.HitMod(owner, monster, Wand(false)), "a plain launcher does not cut");
            Assert.AreEqual(1.0f, SummonerFocus.HitMod(owner, owner, focus), "self is never penalised");
        }

        [TestMethod]
        public void LauncherFocus_NeverReachesThePetHalves()
        {
            var focus = Wand(true);
            var owner = OwnerWielding(focus);
            var pet = Pet(owner);
            var monster = TestCreatures.CreateDefender();

            Assert.AreEqual(1.70f, SummonerFocus.HitMod(pet, monster, focus), 1e-5f, "positive control: the live focus still feeds the pet");

            owner.EquippedObjects.Remove(focus.Guid);

            // a pet bolt whose launcher is a focus gets no bonus once its owner no longer wields one: pets read live
            Assert.AreEqual(1.0f, SummonerFocus.HitMod(pet, monster, focus));
            Assert.AreEqual(1.0f, SummonerFocus.HitMod(monster, pet, focus));
        }

        [TestMethod]
        public void HuntersEchoTarget_IsNeverCutAgain()
        {
            var focus = Wand(true);
            var wielder = OwnerWielding(focus);
            var echo = Pet(PvpTemplatePlayerTests.SeededPlayer(), echo: true);

            Assert.AreEqual(0.25f, SummonerFocus.WielderDamageMod(wielder, Pet(PvpTemplatePlayerTests.SeededPlayer())), 1e-5f, "positive control: another player's ordinary pet is cut");
            Assert.AreEqual(1.0f, SummonerFocus.WielderDamageMod(wielder, echo));
            Assert.AreEqual(1.0f, SummonerFocus.WielderDamageMod(wielder, echo, focus), "nor through the launcher");
            Assert.AreEqual(1.0f, SummonerFocus.HitMod(wielder, echo, focus));
        }

        [TestMethod]
        public void NanDefenseFloor_FallsBackToQuarter()
        {
            var owner = OwnerWielding(Wand(true, edm: 1.50));
            var pet = Pet(owner);
            var monster = TestCreatures.CreateDefender();

            PropertyManager.ModifyDouble(SummonerFocus.PetDefenseFloorKey, double.NaN);

            AssertNear(0.25f, SummonerFocus.HitMod(monster, pet));   // 1 - 2.5 * 0.5 is below the floor, so the fallback floor 0.25 decides
        }
    }
}
