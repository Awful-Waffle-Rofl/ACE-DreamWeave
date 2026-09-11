using System.Collections.Generic;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Entity.Models;
using ACE.Server.Entity.AccountVault;

namespace ACE.Server.Tests
{
    /// <summary>
    /// The mule form body swap, as a pure function. No Player, no Session, no landblock and no dat:
    /// the setup-height lookup is injected, so everything here is deterministic.
    ///
    /// The test that matters most is <see cref="Clone_DoesNotMutateEitherSourceWeenie"/>. Both inputs
    /// come from WorldDatabaseWithEntityCache.GetCachedWeenie, which hands back the SHARED cached
    /// instance, so a clone that wrote into either one would corrupt every future spawn of that wcid
    /// for the whole process lifetime, with no error anywhere.
    /// </summary>
    [TestClass]
    public class MuleFormBodyTests
    {
        private const uint MuleSetup = 0x02000A0B;
        private const uint DonorSetup = 0x0200004E;

        /// <summary>
        /// A stand-in for the mule vendor weenie, carrying the properties the clone must keep and the
        /// body properties it must lose.
        /// </summary>
        private static Weenie MakeMule()
        {
            return new Weenie
            {
                WeenieClassId = 1003150,
                ClassName = "mulevendor",
                WeenieType = WeenieType.Vendor,
                PropertiesBool = new Dictionary<PropertyBool, bool>
                {
                    { PropertyBool.PersonalVendor, true },
                    { PropertyBool.Attackable, false },
                    { PropertyBool.Ethereal, true },
                },
                PropertiesDID = new Dictionary<PropertyDataId, uint>
                {
                    { PropertyDataId.Setup, MuleSetup },
                    { PropertyDataId.MotionTable, 0x09000025 },
                    { PropertyDataId.SoundTable, 0x20000018 },
                    { PropertyDataId.CombatTable, 0x30000006 },
                    { PropertyDataId.PaletteBase, 0x0400007E },
                    { PropertyDataId.ClothingBase, 0x100002BE },
                    { PropertyDataId.Icon, 0x060011F9 },
                    { PropertyDataId.PhysicsEffectTable, 0x3400002B },
                },
                PropertiesFloat = new Dictionary<PropertyFloat, double>
                {
                    { PropertyFloat.DefaultScale, 0.5 },
                    { PropertyFloat.Shade, 0.25 },
                },
                PropertiesInt = new Dictionary<PropertyInt, int>
                {
                    { PropertyInt.ItemType, (int)ItemType.Creature },
                    { PropertyInt.PaletteTemplate, 7 },
                    { PropertyInt.CreatureType, (int)CreatureType.Lugian },
                },
                PropertiesString = new Dictionary<PropertyString, string>
                {
                    { PropertyString.Name, "Mule" },
                },
            };
        }

        /// <summary>A donor carrying the full body set plus the two properties that must never travel.</summary>
        private static Weenie MakeDonor()
        {
            return new Weenie
            {
                WeenieClassId = 1234,
                ClassName = "tuskerguard",
                WeenieType = WeenieType.Creature,
                PropertiesBool = new Dictionary<PropertyBool, bool>
                {
                    { PropertyBool.Attackable, true },
                    { PropertyBool.Stuck, false },
                },
                PropertiesDID = new Dictionary<PropertyDataId, uint>
                {
                    { PropertyDataId.Setup, DonorSetup },
                    { PropertyDataId.MotionTable, 0x09000101 },
                    { PropertyDataId.SoundTable, 0x20000102 },
                    { PropertyDataId.CombatTable, 0x30000103 },
                    { PropertyDataId.PaletteBase, 0x04000104 },
                    { PropertyDataId.ClothingBase, 0x10000105 },
                    { PropertyDataId.Icon, 0x06000106 },
                    { PropertyDataId.PhysicsEffectTable, 0x34000107 },

                    // Neither of these may ever reach the clone. A vendor that rolled a death treasure
                    // type would drop loot; one with a wielded treasure type would spawn armed.
                    { PropertyDataId.WieldedTreasureType, 0x00000032 },
                    { PropertyDataId.DeathTreasureType, 0x00000035 },
                },
                PropertiesFloat = new Dictionary<PropertyFloat, double>
                {
                    { PropertyFloat.Shade, 0.75 },
                    { PropertyFloat.DefaultScale, 1.4 },
                },
                PropertiesInt = new Dictionary<PropertyInt, int>
                {
                    { PropertyInt.PaletteTemplate, 31 },
                    { PropertyInt.CreatureType, (int)CreatureType.Tusker },
                    { PropertyInt.EncumbranceVal, 500 },
                },
                PropertiesString = new Dictionary<PropertyString, string>
                {
                    { PropertyString.Name, "Tusker Guard" },
                },
            };
        }

        private static float Height(uint setupId)
        {
            if (setupId == MuleSetup)
                return 2.0f;

            if (setupId == DonorSetup)
                return 4.0f;

            return 0f;
        }

        private static Weenie Clone(Weenie mule, Weenie donor)
        {
            return MuleFormBody.CloneWithDonorBody(mule, donor, Height(MuleSetup) * 0.5f, Height);
        }

        [TestMethod]
        public void Clone_CarriesTheDonorBody()
        {
            var clone = Clone(MakeMule(), MakeDonor());

            Assert.AreEqual(DonorSetup, clone.PropertiesDID[PropertyDataId.Setup]);
            Assert.AreEqual(0x09000101u, clone.PropertiesDID[PropertyDataId.MotionTable]);
            Assert.AreEqual(0x20000102u, clone.PropertiesDID[PropertyDataId.SoundTable]);
            Assert.AreEqual(0x30000103u, clone.PropertiesDID[PropertyDataId.CombatTable]);
            Assert.AreEqual(0x04000104u, clone.PropertiesDID[PropertyDataId.PaletteBase]);
            Assert.AreEqual(0x10000105u, clone.PropertiesDID[PropertyDataId.ClothingBase]);
            Assert.AreEqual(0x06000106u, clone.PropertiesDID[PropertyDataId.Icon]);
            Assert.AreEqual(0x34000107u, clone.PropertiesDID[PropertyDataId.PhysicsEffectTable]);
            Assert.AreEqual(31, clone.PropertiesInt[PropertyInt.PaletteTemplate]);
            Assert.AreEqual(0.75, clone.PropertiesFloat[PropertyFloat.Shade], 0.0001);
        }

        [TestMethod]
        public void Clone_DropsAKeyTheDonorDoesNotCarry()
        {
            var donor = MakeDonor();
            donor.PropertiesDID.Remove(PropertyDataId.ClothingBase);

            var clone = Clone(MakeMule(), donor);

            Assert.IsFalse(clone.PropertiesDID.ContainsKey(PropertyDataId.ClothingBase),
                "a donor with no ClothingBase must not inherit the mule's - the body would wear the Lugian's clothing");
        }

        [TestMethod]
        public void Clone_CarriesTheDonorsCreatureType()
        {
            var clone = Clone(MakeMule(), MakeDonor());

            Assert.AreEqual((int)CreatureType.Tusker, clone.PropertiesInt[PropertyInt.CreatureType],
                "the appraisal panel prints CreatureType as the body's family; a Tusker donor must not show the mule's Lugian");
        }

        [TestMethod]
        public void Clone_WithADonorCarryingNoCreatureType_CarriesNone()
        {
            var donor = MakeDonor();
            donor.PropertiesInt.Remove(PropertyInt.CreatureType);

            var clone = Clone(MakeMule(), donor);

            Assert.IsFalse(clone.PropertiesInt.ContainsKey(PropertyInt.CreatureType),
                "a donor with no CreatureType must not inherit the mule's Lugian");
        }

        [TestMethod]
        public void Clone_KeepsPersonalVendor_SoTheFactoryStillReturnsAPersonalVendor()
        {
            var clone = Clone(MakeMule(), MakeDonor());

            Assert.IsTrue(clone.PropertiesBool[PropertyBool.PersonalVendor],
                "bool 9049 is the factory's opt-in; without it the clone comes back as a plain Vendor");
            Assert.IsFalse(clone.PropertiesBool[PropertyBool.Attackable], "the mule's own Attackable must survive");
            Assert.IsTrue(clone.PropertiesBool[PropertyBool.Ethereal]);
            Assert.IsFalse(clone.PropertiesBool.ContainsKey(PropertyBool.Stuck), "no donor bool may travel");
        }

        [TestMethod]
        public void Clone_NeverCarriesTreasureTypes()
        {
            var clone = Clone(MakeMule(), MakeDonor());

            Assert.IsFalse(clone.PropertiesDID.ContainsKey(PropertyDataId.WieldedTreasureType));
            Assert.IsFalse(clone.PropertiesDID.ContainsKey(PropertyDataId.DeathTreasureType));
            Assert.IsFalse(clone.PropertiesInt.ContainsKey(PropertyInt.EncumbranceVal),
                "the allow-list is eleven scalar keys; nothing else the donor carries may reach the clone");
        }

        [TestMethod]
        public void Clone_KeepsTheMulesName()
        {
            var clone = Clone(MakeMule(), MakeDonor());

            Assert.AreEqual("Mule", clone.PropertiesString[PropertyString.Name]);
            Assert.AreEqual(1003150u, clone.WeenieClassId);
            Assert.AreEqual(WeenieType.Vendor, clone.WeenieType);
        }

        [TestMethod]
        public void Clone_DoesNotMutateEitherSourceWeenie()
        {
            var mule = MakeMule();
            var donor = MakeDonor();

            var clone = Clone(mule, donor);

            // GetCachedWeenie hands back the shared instance; a write into either of these corrupts
            // every future spawn of that wcid, process-wide, with no error.
            Assert.AreEqual(MuleSetup, mule.PropertiesDID[PropertyDataId.Setup]);
            Assert.AreEqual(0x100002BEu, mule.PropertiesDID[PropertyDataId.ClothingBase]);
            Assert.AreEqual(7, mule.PropertiesInt[PropertyInt.PaletteTemplate]);
            Assert.AreEqual((int)CreatureType.Lugian, mule.PropertiesInt[PropertyInt.CreatureType]);
            Assert.AreEqual(0.25, mule.PropertiesFloat[PropertyFloat.Shade], 0.0001);
            Assert.AreEqual(0.5, mule.PropertiesFloat[PropertyFloat.DefaultScale], 0.0001);

            Assert.IsTrue(donor.PropertiesDID.ContainsKey(PropertyDataId.WieldedTreasureType));
            Assert.AreEqual("Tusker Guard", donor.PropertiesString[PropertyString.Name]);

            Assert.IsFalse(ReferenceEqualsAny(clone, mule), "the clone shares no scalar dictionary with the mule");
            Assert.IsFalse(ReferenceEqualsAny(clone, donor), "the clone shares no scalar dictionary with the donor");
        }

        [TestMethod]
        public void Clone_BuildsNewInstancesOfEveryScalarDictionary()
        {
            var mule = MakeMule();
            var donor = MakeDonor();

            var clone = Clone(mule, donor);

            Assert.IsNotNull(clone.PropertiesBool);
            Assert.IsNotNull(clone.PropertiesDID);
            Assert.IsNotNull(clone.PropertiesFloat);
            Assert.IsNotNull(clone.PropertiesIID);
            Assert.IsNotNull(clone.PropertiesInt);
            Assert.IsNotNull(clone.PropertiesInt64);
            Assert.IsNotNull(clone.PropertiesString);

            Assert.IsFalse(ReferenceEquals(clone.PropertiesBool, mule.PropertiesBool));
            Assert.IsFalse(ReferenceEquals(clone.PropertiesDID, mule.PropertiesDID));
            Assert.IsFalse(ReferenceEquals(clone.PropertiesFloat, mule.PropertiesFloat));
            Assert.IsFalse(ReferenceEquals(clone.PropertiesInt, mule.PropertiesInt));
            Assert.IsFalse(ReferenceEquals(clone.PropertiesString, mule.PropertiesString));
        }

        [TestMethod]
        public void Clone_TakesTheDonorsAppearanceLists_AndNotTheMules()
        {
            var mule = MakeMule();
            var donor = MakeDonor();

            donor.PropertiesPalette = new List<PropertiesPalette> { new PropertiesPalette { SubPaletteId = 0x04000200, Offset = 0, Length = 8 } };
            donor.PropertiesTextureMap = new List<PropertiesTextureMap> { new PropertiesTextureMap { PartIndex = 0, OldTexture = 1, NewTexture = 2 } };
            donor.PropertiesAnimPart = new List<PropertiesAnimPart> { new PropertiesAnimPart { Index = 0, AnimationId = 3 } };

            var clone = Clone(mule, donor);

            // Orchestrator ruling 2026-09-01: the three appearance lists are body-tied (AnimPart
            // indices are meaningless off their own Setup) so they travel with the body or not at all.
            Assert.AreSame(donor.PropertiesPalette, clone.PropertiesPalette);
            Assert.AreSame(donor.PropertiesTextureMap, clone.PropertiesTextureMap);
            Assert.AreSame(donor.PropertiesAnimPart, clone.PropertiesAnimPart);
        }

        [TestMethod]
        public void Clone_WithADonorCarryingNoAppearanceLists_CarriesNone()
        {
            var mule = MakeMule();
            mule.PropertiesPalette = new List<PropertiesPalette> { new PropertiesPalette { SubPaletteId = 0x04000999 } };

            var clone = Clone(mule, MakeDonor());

            Assert.IsNull(clone.PropertiesPalette, "the mule's own appearance rows must not leak onto a donor body");
            Assert.IsNull(clone.PropertiesTextureMap);
            Assert.IsNull(clone.PropertiesAnimPart);
        }

        [TestMethod]
        public void ComputeScale_NormalisesTheDonorToTheMulesHeight()
        {
            Assert.IsTrue(MuleFormBody.TryComputeScale(1.0f, 4.0f, out var scale));
            Assert.AreEqual(0.25f, scale, 0.0001f);

            Assert.IsTrue(MuleFormBody.TryComputeScale(1.0f, 0.5f, out scale));
            Assert.AreEqual(2.0f, scale, 0.0001f);
        }

        [TestMethod]
        public void ComputeScale_Clamps()
        {
            Assert.IsTrue(MuleFormBody.TryComputeScale(1.0f, 100.0f, out var scale));
            Assert.AreEqual(0.05f, scale, 0.0001f);

            Assert.IsTrue(MuleFormBody.TryComputeScale(100.0f, 1.0f, out scale));
            Assert.AreEqual(4.0f, scale, 0.0001f);
        }

        [TestMethod]
        public void ComputeScale_RejectsAZeroOrNegativeHeight_RatherThanReturningInfinity()
        {
            Assert.IsFalse(MuleFormBody.TryComputeScale(1.0f, 0f, out var scale));
            Assert.AreEqual(0f, scale);

            Assert.IsFalse(MuleFormBody.TryComputeScale(1.0f, -3f, out _));
            Assert.IsFalse(MuleFormBody.TryComputeScale(0f, 2f, out _));
        }

        [TestMethod]
        public void Clone_SetsDefaultScaleFromTheHeightRatio()
        {
            // Mule setup height 2.0, halved by its own DefaultScale of 0.5 -> a 1.0 target. Donor
            // height 4.0, so the donor stands at a quarter size to match.
            var clone = Clone(MakeMule(), MakeDonor());

            Assert.AreEqual(0.25, clone.PropertiesFloat[PropertyFloat.DefaultScale], 0.0001);
        }

        [TestMethod]
        public void Clone_WithAnUnmeasurableDonor_ReturnsNull()
        {
            var donor = MakeDonor();
            donor.PropertiesDID[PropertyDataId.Setup] = 0x02009999;   // Height() answers 0 for this

            Assert.IsNull(MuleFormBody.CloneWithDonorBody(MakeMule(), donor, 1.0f, Height),
                "an unmeasurable donor is refused here, so the caller falls back to the plain mule");
        }

        [TestMethod]
        public void Clone_WithADonorCarryingNoSetup_ReturnsNull()
        {
            var donor = MakeDonor();
            donor.PropertiesDID.Remove(PropertyDataId.Setup);

            Assert.IsNull(MuleFormBody.CloneWithDonorBody(MakeMule(), donor, 1.0f, Height));
        }

        private static bool ReferenceEqualsAny(Weenie clone, Weenie source)
        {
            return ReferenceEquals(clone.PropertiesBool, source.PropertiesBool)
                || ReferenceEquals(clone.PropertiesDID, source.PropertiesDID)
                || ReferenceEquals(clone.PropertiesFloat, source.PropertiesFloat)
                || ReferenceEquals(clone.PropertiesInt, source.PropertiesInt)
                || ReferenceEquals(clone.PropertiesInt64, source.PropertiesInt64)
                || ReferenceEquals(clone.PropertiesString, source.PropertiesString)
                || ReferenceEquals(clone.PropertiesIID, source.PropertiesIID);
        }
    }
}
