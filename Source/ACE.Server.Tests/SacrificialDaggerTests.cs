using System.Collections.Generic;
using System.Threading;

using ACE.Entity;
using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Entity.Models;
using ACE.Server.Entity;
using ACE.Server.WorldObjects;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ACE.Server.Tests
{
    /// <summary>
    /// Custom Summons Task 5: the Sacrificial Dagger. The decision (which of imprint / apply / refuse) and the
    /// rating copy are pure and tested on real PetDevice / Gem objects; the Player-facing handler
    /// (SacrificialDagger.UseOnTarget) needs a Session and is an owner in-game check.
    /// </summary>
    [TestClass]
    public class SacrificialDaggerTests
    {
        private static int nextGuid = 0x7F5B0000;
        private static uint nextWcid = 996000;

        [ClassInitialize]
        public static void Init(TestContext _) => TestGameTables.EnsureInitialized();

        private static ObjectGuid NextGuid() => new ObjectGuid((uint)Interlocked.Increment(ref nextGuid));

        private static PetDevice MakeSummon(int? damage, int? damageResist, int? crit, int? critResist, int? critDamage, int? critDamageResist, bool duo = false)
        {
            var ints = new Dictionary<PropertyInt, int> { { PropertyInt.PetClass, 1 } };
            if (damage.HasValue) ints[PropertyInt.GearDamage] = damage.Value;
            if (damageResist.HasValue) ints[PropertyInt.GearDamageResist] = damageResist.Value;
            if (crit.HasValue) ints[PropertyInt.GearCrit] = crit.Value;
            if (critResist.HasValue) ints[PropertyInt.GearCritResist] = critResist.Value;
            if (critDamage.HasValue) ints[PropertyInt.GearCritDamage] = critDamage.Value;
            if (critDamageResist.HasValue) ints[PropertyInt.GearCritDamageResist] = critDamageResist.Value;

            var weenie = new Weenie
            {
                WeenieClassId = nextWcid++,
                WeenieType = WeenieType.PetDevice,
                PropertiesInt = ints,
                PropertiesString = new Dictionary<PropertyString, string> { { PropertyString.Name, "Test Essence" } },
                PropertiesBool = duo ? new Dictionary<PropertyBool, bool> { { PropertyBool.DuoSummonGem, true } } : null,
            };

            return new PetDevice(weenie, NextGuid());
        }

        private static Gem MakeDagger(bool imprinted = false, bool withRatings = false)
        {
            var bools = new Dictionary<PropertyBool, bool> { { PropertyBool.SacrificialDagger, true } };
            if (imprinted) bools[PropertyBool.SummonImprinted] = true;

            var ints = new Dictionary<PropertyInt, int>();
            if (withRatings)
            {
                ints[PropertyInt.GearDamage] = 20;
                ints[PropertyInt.GearCrit] = 14;
            }

            var weenie = new Weenie
            {
                WeenieClassId = nextWcid++,
                WeenieType = WeenieType.Gem,
                PropertiesBool = bools,
                PropertiesInt = ints,
                PropertiesString = new Dictionary<PropertyString, string> { { PropertyString.Name, "Sacrificial Dagger" } },
            };

            return new Gem(weenie, NextGuid());
        }

        // ---- decision -------------------------------------------------------------------------------------

        [TestMethod]
        public void Unimprinted_OnSummon_Imprints()
        {
            Assert.AreEqual(DaggerDecision.Imprint, SacrificialDagger.Decide(false, DaggerTarget.Summon));
        }

        [TestMethod]
        public void Imprinted_OnSummon_Applies()
        {
            Assert.AreEqual(DaggerDecision.Apply, SacrificialDagger.Decide(true, DaggerTarget.Summon));
        }

        [TestMethod]
        public void ApplyOnDuoGem_Refused_AndSoIsImprintOnDuoGem()
        {
            Assert.AreEqual(DaggerDecision.RefusedDuoGem, SacrificialDagger.Decide(true, DaggerTarget.DuoGem));
            Assert.AreEqual(DaggerDecision.RefusedDuoGem, SacrificialDagger.Decide(false, DaggerTarget.DuoGem));
        }

        [TestMethod]
        public void Unimprinted_OnNonSummon_Refused_AndDaggerOnDaggerRefused()
        {
            Assert.AreEqual(DaggerDecision.RefusedNotASummon, SacrificialDagger.Decide(false, DaggerTarget.NotASummon));
            Assert.AreEqual(DaggerDecision.RefusedNotASummon, SacrificialDagger.Decide(true, DaggerTarget.NotASummon));
            Assert.AreEqual(DaggerDecision.RefusedDagger, SacrificialDagger.Decide(false, DaggerTarget.Dagger));
            Assert.AreEqual(DaggerDecision.RefusedDagger, SacrificialDagger.Decide(true, DaggerTarget.Dagger));
        }

        [TestMethod]
        public void KindOf_ClassifiesRealObjects()
        {
            var old = DuoSummonGem.PetWeenieTypeLookup;
            try
            {
                DuoSummonGem.PetWeenieTypeLookup = wcid => WeenieType.CombatPet;

                Assert.AreEqual(DaggerTarget.Summon, SacrificialDagger.KindOf(MakeSummon(1, 1, 1, 1, 1, 1)));
                Assert.AreEqual(DaggerTarget.DuoGem, SacrificialDagger.KindOf(MakeSummon(1, 1, 1, 1, 1, 1, duo: true)), "the Duo flag wins over being a PetDevice");
                Assert.AreEqual(DaggerTarget.Dagger, SacrificialDagger.KindOf(MakeDagger()));
            }
            finally
            {
                DuoSummonGem.PetWeenieTypeLookup = old;
            }
        }

        [TestMethod]
        public void DeviceWithPetOut_IsRefused_NothingConsumed()
        {
            var old = DuoSummonGem.PetWeenieTypeLookup;
            try
            {
                DuoSummonGem.PetWeenieTypeLookup = wcid => WeenieType.CombatPet;

                var summon = MakeSummon(7, null, null, null, null, null);
                summon.Pet = 0x80001234;
                var dagger = MakeDagger();

                var kind = SacrificialDagger.KindOf(summon);

                Assert.AreEqual(DaggerTarget.PetOut, kind);
                Assert.AreEqual(DaggerDecision.RefusedPetOut, SacrificialDagger.Decide(false, kind));
                Assert.AreEqual(DaggerDecision.RefusedPetOut, SacrificialDagger.Decide(true, kind));

                // the refusal returns before any mutation: the dagger stays blank and the device keeps its ratings
                Assert.IsFalse(SacrificialDagger.IsImprinted(dagger));
                Assert.AreEqual(7, summon.GearDamage);
            }
            finally
            {
                DuoSummonGem.PetWeenieTypeLookup = old;
            }
        }

        [TestMethod]
        public void DeviceWhosePetIsNotACombatPet_IsRefused_NothingConsumed()
        {
            var old = DuoSummonGem.PetWeenieTypeLookup;
            try
            {
                DuoSummonGem.PetWeenieTypeLookup = wcid => WeenieType.Pet;

                var summon = MakeSummon(7, null, null, null, null, null);
                var dagger = MakeDagger();

                var kind = SacrificialDagger.KindOf(summon);

                Assert.AreEqual(DaggerTarget.NotASummon, kind);
                Assert.AreEqual(DaggerDecision.RefusedNotASummon, SacrificialDagger.Decide(false, kind));
                Assert.AreEqual(DaggerDecision.RefusedNotASummon, SacrificialDagger.Decide(true, kind));

                Assert.IsFalse(SacrificialDagger.IsImprinted(dagger));
                Assert.AreEqual(7, summon.GearDamage);
            }
            finally
            {
                DuoSummonGem.PetWeenieTypeLookup = old;
            }
        }

        // ---- imprint / apply ------------------------------------------------------------------------------

        [TestMethod]
        public void Imprint_CopiesZeros()
        {
            // a source with damage 18 and NOTHING else: the five absent ratings are copied as zeros, and any
            // stale value the dagger carried is wiped rather than left behind
            var summon = MakeSummon(18, null, null, null, null, null);
            var dagger = MakeDagger(withRatings: true);   // starts with damage 20, crit 14

            SacrificialDagger.Imprint(dagger, summon);

            Assert.AreEqual(18, dagger.GearDamage);
            Assert.AreEqual(0, dagger.GearDamageResist);
            Assert.AreEqual(0, dagger.GearCrit, "the dagger's old crit is overwritten by the source's absence");
            Assert.AreEqual(0, dagger.GearCritResist);
            Assert.AreEqual(0, dagger.GearCritDamage);
            Assert.AreEqual(0, dagger.GearCritDamageResist);
            Assert.AreEqual(true, dagger.GetProperty(PropertyBool.SummonImprinted));
        }

        [TestMethod]
        public void Apply_Replaces_NotAdds()
        {
            var dagger = MakeDagger(imprinted: true);
            SacrificialDagger.Imprint(dagger, MakeSummon(5, 0, 0, 2, 0, 0));

            var target = MakeSummon(18, 7, 14, 3, 20, 5);

            SacrificialDagger.ApplyTo(dagger, target);

            Assert.AreEqual(5, target.GearDamage, "replaced, not 18 + 5");
            Assert.AreEqual(0, target.GearDamageResist, "a zero in the imprint wipes the target's rating");
            Assert.AreEqual(0, target.GearCrit);
            Assert.AreEqual(2, target.GearCritResist);
            Assert.AreEqual(0, target.GearCritDamage);
            Assert.AreEqual(0, target.GearCritDamageResist);
        }

        [TestMethod]
        public void Ratings_RoundTrip()
        {
            var summon = MakeSummon(1, 2, 3, 4, 5, 6);

            var r = SummonRatings.Read(summon);

            Assert.AreEqual(new SummonRatings(1, 2, 3, 4, 5, 6), r);
            Assert.AreEqual(new SummonRatings(0, 0, 0, 0, 0, 0), SummonRatings.Read(MakeSummon(null, null, null, null, null, null)));
        }
    }
}
