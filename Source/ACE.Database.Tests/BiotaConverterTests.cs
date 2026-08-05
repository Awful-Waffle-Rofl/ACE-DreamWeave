using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Database.Adapter;
using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Entity.Models;

namespace ACE.Database.Tests
{
    /// <summary>
    /// Round-trip tests for BiotaConverter, which bridges the two Biota shapes
    /// (ACE.Entity.Models.Biota used at runtime vs ACE.Database.Models.Shard.Biota used by EF).
    /// Mixing the two shapes up, or losing data crossing between them, is the most common
    /// source of confusing bugs in this codebase - these tests pin the mapping.
    /// No database is required; the converter is pure object mapping.
    /// </summary>
    [TestClass]
    public class BiotaConverterTests
    {
        private static readonly JsonSerializerOptions jsonOptions = new JsonSerializerOptions { WriteIndented = true };

        [TestMethod]
        public void RoundTrip_FullyPopulatedBiota_IsLossless()
        {
            var original = CreateFullyPopulatedBiota();

            var shard = BiotaConverter.ConvertFromEntityBiota(original, includeDatabaseRecordIds: true);
            var roundTripped = BiotaConverter.ConvertToEntityBiota(shard);

            // localize the most likely failures before the exhaustive comparison
            Assert.AreEqual(original.Id, roundTripped.Id);
            Assert.AreEqual(original.WeenieClassId, roundTripped.WeenieClassId);
            Assert.AreEqual(original.WeenieType, roundTripped.WeenieType);
            Assert.AreEqual(original.PropertiesInt.Count, roundTripped.PropertiesInt.Count, "PropertiesInt count");
            Assert.AreEqual(original.PropertiesEmote.Count, roundTripped.PropertiesEmote.Count, "PropertiesEmote count");
            Assert.AreEqual(original.PropertiesEmote.First().PropertiesEmoteAction.Count,
                roundTripped.PropertiesEmote.First().PropertiesEmoteAction.Count, "PropertiesEmoteAction count");
            Assert.AreEqual(original.PropertiesEnchantmentRegistry.Count, roundTripped.PropertiesEnchantmentRegistry.Count, "PropertiesEnchantmentRegistry count");

            // exhaustive comparison: every mapped property in every section must survive the trip
            var expected = JsonSerializer.Serialize(original, jsonOptions);
            var actual = JsonSerializer.Serialize(roundTripped, jsonOptions);

            // guard against the comparison degenerating (e.g. the serializer skipping the collections
            // on both sides): the snapshot must actually contain deeply nested fixture data
            StringAssert.Contains(expected, "TestQuestStamp");
            StringAssert.Contains(expected, "Well met, %s!");

            Assert.AreEqual(expected, actual);
        }

        [TestMethod]
        public void RoundTrip_PreservesOrderedCollectionOrder()
        {
            // AnimParts, Palettes and TextureMaps are ordered client-facing data: the converter
            // persists list position in the Order column and must restore it, not sort by content
            var original = new Biota
            {
                Id = 0x80000001,
                WeenieClassId = 1000031,
                WeenieType = WeenieType.Creature,

                PropertiesAnimPart = new List<PropertiesAnimPart>
                {
                    new PropertiesAnimPart { Index = 5, AnimationId = 0x0100_0300 },
                    new PropertiesAnimPart { Index = 0, AnimationId = 0x0100_0100 },
                    new PropertiesAnimPart { Index = 2, AnimationId = 0x0100_0200 },
                },
                PropertiesPalette = new List<PropertiesPalette>
                {
                    new PropertiesPalette { SubPaletteId = 0x0400_0333, Offset = 64, Length = 128 },
                    new PropertiesPalette { SubPaletteId = 0x0400_0111, Offset = 0, Length = 0 },
                    new PropertiesPalette { SubPaletteId = 0x0400_0222, Offset = 224, Length = 32 },
                },
                PropertiesTextureMap = new List<PropertiesTextureMap>
                {
                    new PropertiesTextureMap { PartIndex = 9, OldTexture = 0x0500_0900, NewTexture = 0x0500_0901 },
                    new PropertiesTextureMap { PartIndex = 1, OldTexture = 0x0500_0100, NewTexture = 0x0500_0101 },
                },
            };

            var shard = BiotaConverter.ConvertFromEntityBiota(original);

            // shuffle the EF collections to simulate the database returning rows in arbitrary order;
            // ConvertToEntityBiota must restore list order from the Order column
            var reversedAnimParts = shard.BiotaPropertiesAnimPart.Reverse().ToList();
            shard.BiotaPropertiesAnimPart.Clear();
            foreach (var record in reversedAnimParts)
                shard.BiotaPropertiesAnimPart.Add(record);

            var reversedPalettes = shard.BiotaPropertiesPalette.Reverse().ToList();
            shard.BiotaPropertiesPalette.Clear();
            foreach (var record in reversedPalettes)
                shard.BiotaPropertiesPalette.Add(record);

            var reversedTextureMaps = shard.BiotaPropertiesTextureMap.Reverse().ToList();
            shard.BiotaPropertiesTextureMap.Clear();
            foreach (var record in reversedTextureMaps)
                shard.BiotaPropertiesTextureMap.Add(record);

            var roundTripped = BiotaConverter.ConvertToEntityBiota(shard);

            CollectionAssert.AreEqual(
                original.PropertiesAnimPart.Select(p => p.AnimationId).ToList(),
                roundTripped.PropertiesAnimPart.Select(p => p.AnimationId).ToList(), "AnimPart order");
            CollectionAssert.AreEqual(
                original.PropertiesPalette.Select(p => p.SubPaletteId).ToList(),
                roundTripped.PropertiesPalette.Select(p => p.SubPaletteId).ToList(), "Palette order");
            CollectionAssert.AreEqual(
                original.PropertiesTextureMap.Select(p => p.OldTexture).ToList(),
                roundTripped.PropertiesTextureMap.Select(p => p.OldTexture).ToList(), "TextureMap order");
        }

        [TestMethod]
        public void ConvertToEntityBiota_EmptyCollections_StayNullUnlessRequested()
        {
            // the entity model deliberately leaves unpopulated collections null to conserve
            // server memory - a converter that instantiates them all would silently regress that
            var shard = new ACE.Database.Models.Shard.Biota
            {
                Id = 0x80000002,
                WeenieClassId = 123,
                WeenieType = (int)WeenieType.Generic,
            };

            var entity = BiotaConverter.ConvertToEntityBiota(shard);

            Assert.IsNull(entity.PropertiesBool);
            Assert.IsNull(entity.PropertiesInt);
            Assert.IsNull(entity.PropertiesString);
            Assert.IsNull(entity.PropertiesPosition);
            Assert.IsNull(entity.PropertiesSpellBook);
            Assert.IsNull(entity.PropertiesEmote);
            Assert.IsNull(entity.PropertiesSkill);
            Assert.IsNull(entity.PropertiesBook);
            Assert.IsNull(entity.PropertiesEnchantmentRegistry);

            var entityWithCollections = BiotaConverter.ConvertToEntityBiota(shard, instantiateEmptyCollections: true);

            Assert.IsNotNull(entityWithCollections.PropertiesBool);
            Assert.AreEqual(0, entityWithCollections.PropertiesBool.Count);
            Assert.IsNotNull(entityWithCollections.PropertiesInt);
            Assert.AreEqual(0, entityWithCollections.PropertiesInt.Count);
            Assert.IsNotNull(entityWithCollections.PropertiesEmote);
            Assert.AreEqual(0, entityWithCollections.PropertiesEmote.Count);
        }

        [TestMethod]
        public void ConvertFromEntityBiota_SanitizesNaNRotationsToIdentity()
        {
            // EF can't store NaN floats ("Unknown column 'NaN' in 'field list'"), so the converter
            // replaces a NaN rotation with the identity quaternion before it reaches the database
            var entity = new Biota
            {
                Id = 0x80000003,
                WeenieClassId = 456,
                WeenieType = WeenieType.Creature,

                PropertiesPosition = new Dictionary<PositionType, PropertiesPosition>
                {
                    [PositionType.Location] = new PropertiesPosition
                    {
                        ObjCellId = 0x0007D35A,
                        PositionX = 10f,
                        PositionY = 20f,
                        PositionZ = 30f,
                        RotationW = float.NaN,
                        RotationX = float.NaN,
                        RotationY = 0f,
                        RotationZ = 0f,
                    },
                },
            };

            var shard = BiotaConverter.ConvertFromEntityBiota(entity);
            var position = shard.BiotaPropertiesPosition.Single();

            Assert.AreEqual(1f, position.AnglesW);
            Assert.AreEqual(0f, position.AnglesX);
            Assert.AreEqual(0f, position.AnglesY);
            Assert.AreEqual(0f, position.AnglesZ);

            // the origin is untouched
            Assert.AreEqual(10f, position.OriginX);
            Assert.AreEqual(20f, position.OriginY);
            Assert.AreEqual(30f, position.OriginZ);
        }

        /// <summary>
        /// An entity-model biota with every section the converter maps populated with
        /// representative, distinct values (2+ entries per collection where possible).
        /// </summary>
        private static Biota CreateFullyPopulatedBiota()
        {
            return new Biota
            {
                Id = 0x80001234,
                WeenieClassId = 1000042,
                WeenieType = WeenieType.Creature,

                PropertiesBool = new Dictionary<PropertyBool, bool>
                {
                    [PropertyBool.Attackable] = true,
                    [PropertyBool.Stuck] = false,
                },
                PropertiesDID = new Dictionary<PropertyDataId, uint>
                {
                    [PropertyDataId.Setup] = 0x0200004E,
                    [PropertyDataId.MotionTable] = 0x09000001,
                },
                PropertiesFloat = new Dictionary<PropertyFloat, double>
                {
                    [PropertyFloat.HeartbeatInterval] = 5.0,
                    [PropertyFloat.DefaultScale] = 1.25,
                },
                PropertiesIID = new Dictionary<PropertyInstanceId, uint>
                {
                    [PropertyInstanceId.Owner] = 0x50000001,
                    [PropertyInstanceId.Container] = 0x50000002,
                },
                PropertiesInt = new Dictionary<PropertyInt, int>
                {
                    [PropertyInt.ItemsCapacity] = 120,
                    [PropertyInt.Value] = 2500,
                },
                PropertiesInt64 = new Dictionary<PropertyInt64, long>
                {
                    [PropertyInt64.TotalExperience] = 123_456_789_012_345,
                },
                PropertiesString = new Dictionary<PropertyString, string>
                {
                    [PropertyString.Name] = "Round Trip Test Subject",
                    [PropertyString.Quest] = "TestQuestStamp",
                },

                PropertiesPosition = new Dictionary<PositionType, PropertiesPosition>
                {
                    [PositionType.Location] = new PropertiesPosition
                    {
                        ObjCellId = 0x0007D35A,
                        PositionX = 12.5f, PositionY = -34.25f, PositionZ = 56.125f,
                        RotationW = 0.7071f, RotationX = 0f, RotationY = 0f, RotationZ = 0.7071f,
                    },
                    [PositionType.Destination] = new PropertiesPosition
                    {
                        ObjCellId = 0x01234567,
                        PositionX = 1f, PositionY = 2f, PositionZ = 3f,
                        RotationW = 1f, RotationX = 0f, RotationY = 0f, RotationZ = 0f,
                    },
                },

                PropertiesSpellBook = new Dictionary<int, float>
                {
                    [2931] = 2.0f,
                    [1234] = 0.25f,
                },

                PropertiesAnimPart = new List<PropertiesAnimPart>
                {
                    new PropertiesAnimPart { Index = 0, AnimationId = 0x01000A01 },
                    new PropertiesAnimPart { Index = 3, AnimationId = 0x01000A02 },
                },
                PropertiesPalette = new List<PropertiesPalette>
                {
                    new PropertiesPalette { SubPaletteId = 0x04000BEF, Offset = 64, Length = 160 },
                    new PropertiesPalette { SubPaletteId = 0x04001A1B, Offset = 224, Length = 32 },
                },
                PropertiesTextureMap = new List<PropertiesTextureMap>
                {
                    new PropertiesTextureMap { PartIndex = 0, OldTexture = 0x05001111, NewTexture = 0x05002222 },
                    new PropertiesTextureMap { PartIndex = 4, OldTexture = 0x05003333, NewTexture = 0x05004444 },
                },

                PropertiesCreateList = new List<PropertiesCreateList>
                {
                    new PropertiesCreateList
                    {
                        DatabaseRecordId = 11,
                        DestinationType = (DestinationType)2,
                        WeenieClassId = 1000023,
                        StackSize = 1,
                        Palette = 0,
                        Shade = 0f,
                        TryToBond = false,
                    },
                    new PropertiesCreateList
                    {
                        DatabaseRecordId = 12,
                        DestinationType = (DestinationType)4,
                        WeenieClassId = 273,
                        StackSize = 50,
                        Palette = 2,
                        Shade = 0.5f,
                        TryToBond = true,
                    },
                },

                PropertiesEmote = new List<PropertiesEmote>
                {
                    new PropertiesEmote
                    {
                        DatabaseRecordId = 21,
                        Category = EmoteCategory.Use,
                        Probability = 1.0f,
                        WeenieClassId = 1000035,
                        Style = MotionStance.NonCombat,
                        Substyle = MotionCommand.Wave,
                        Quest = "TestTurnInAccepted",
                        VendorType = (VendorType)2,
                        MinHealth = 0.25f,
                        MaxHealth = 0.75f,
                        PropertiesEmoteAction =
                        {
                            new PropertiesEmoteAction
                            {
                                DatabaseRecordId = 31,
                                Type = 1,
                                Delay = 0.5f,
                                Extent = 1.0f,
                                Motion = MotionCommand.Wave,
                                Message = "Well met, %s!",
                                TestString = "TestQuestStamp",
                                Min = 1, Max = 10,
                                Min64 = 100L, Max64 = 200L,
                                MinDbl = 0.1, MaxDbl = 0.9,
                                Stat = 5,
                                Display = true,
                                Amount = 25,
                                Amount64 = 50_000L,
                                HeroXP64 = 1_000L,
                                Percent = 0.15,
                                SpellId = 2931,
                                WealthRating = 3,
                                TreasureClass = 2,
                                TreasureType = 1,
                                PScript = (PlayScript)93,
                                Sound = (Sound)8,
                                DestinationType = 2,
                                WeenieClassId = 273,
                                StackSize = 5,
                                Palette = 1,
                                Shade = 0.25f,
                                TryToBond = false,
                                ObjCellId = 0x0007D35A,
                                OriginX = 1f, OriginY = 2f, OriginZ = 3f,
                                AnglesW = 1f, AnglesX = 0f, AnglesY = 0f, AnglesZ = 0f,
                            },
                            new PropertiesEmoteAction
                            {
                                DatabaseRecordId = 32,
                                Type = 2,
                                Delay = 1.5f,
                                Extent = 0f,
                                Message = "Farewell.",
                            },
                        },
                    },
                },

                PropertiesEventFilter = new HashSet<int> { 100, 250, 999 },

                PropertiesGenerator = new List<PropertiesGenerator>
                {
                    new PropertiesGenerator
                    {
                        DatabaseRecordId = 41,
                        Probability = -1f,
                        WeenieClassId = 1000024,
                        Delay = 300f,
                        InitCreate = 2,
                        MaxCreate = 4,
                        WhenCreate = RegenerationType.Destruction,
                        WhereCreate = RegenLocationType.Scatter,
                        StackSize = 1,
                        PaletteId = 0x04000BEF,
                        Shade = 0.1f,
                        ObjCellId = 0x0007D35A,
                        OriginX = 5f, OriginY = 6f, OriginZ = 7f,
                        AnglesW = 1f, AnglesX = 0f, AnglesY = 0f, AnglesZ = 0f,
                    },
                },

                PropertiesAttribute = new Dictionary<PropertyAttribute, PropertiesAttribute>
                {
                    [PropertyAttribute.Strength] = new PropertiesAttribute { InitLevel = 100, LevelFromCP = 25, CPSpent = 123456 },
                    [PropertyAttribute.Endurance] = new PropertiesAttribute { InitLevel = 90, LevelFromCP = 10, CPSpent = 54321 },
                },
                PropertiesAttribute2nd = new Dictionary<PropertyAttribute2nd, PropertiesAttribute2nd>
                {
                    [PropertyAttribute2nd.MaxHealth] = new PropertiesAttribute2nd { InitLevel = 50, LevelFromCP = 5, CPSpent = 9999, CurrentLevel = 137 },
                },
                PropertiesBodyPart = new Dictionary<CombatBodyPart, PropertiesBodyPart>
                {
                    [CombatBodyPart.Head] = new PropertiesBodyPart
                    {
                        DType = DamageType.Slash,
                        DVal = 12,
                        DVar = 0.4f,
                        BaseArmor = 60,
                        ArmorVsSlash = 60, ArmorVsPierce = 55, ArmorVsBludgeon = 50,
                        ArmorVsCold = 40, ArmorVsFire = 40, ArmorVsAcid = 45,
                        ArmorVsElectric = 35, ArmorVsNether = 30,
                        BH = 1,
                        HLF = 0.1f, MLF = 0.2f, LLF = 0.3f,
                        HRF = 0.15f, MRF = 0.25f, LRF = 0.35f,
                        HLB = 0.05f, MLB = 0.1f, LLB = 0.15f,
                        HRB = 0.06f, MRB = 0.12f, LRB = 0.18f,
                    },
                },
                PropertiesSkill = new Dictionary<Skill, PropertiesSkill>
                {
                    [Skill.MeleeDefense] = new PropertiesSkill { LevelFromPP = 200, SAC = SkillAdvancementClass.Specialized, PP = 4_000_000, InitLevel = 10, ResistanceAtLastCheck = 150, LastUsedTime = 1234567.89 },
                    [Skill.HeavyWeapons] = new PropertiesSkill { LevelFromPP = 180, SAC = SkillAdvancementClass.Trained, PP = 2_500_000, InitLevel = 0, ResistanceAtLastCheck = 0, LastUsedTime = 0 },
                },

                PropertiesBook = new PropertiesBook { MaxNumPages = 10, MaxNumCharsPerPage = 1000 },
                PropertiesBookPageData = new List<PropertiesBookPageData>
                {
                    new PropertiesBookPageData { AuthorId = 0x50000001, AuthorName = "Ostyn Hale", AuthorAccount = "testaccount", IgnoreAuthor = false, PageText = "Page one." },
                    new PropertiesBookPageData { AuthorId = 0x50000002, AuthorName = "Torvic", AuthorAccount = "otheraccount", IgnoreAuthor = true, PageText = "Page two." },
                },

                PropertiesAllegiance = new Dictionary<uint, PropertiesAllegiance>
                {
                    [0x50000010] = new PropertiesAllegiance { Banned = false, ApprovedVassal = true },
                    [0x50000011] = new PropertiesAllegiance { Banned = true, ApprovedVassal = false },
                },
                PropertiesEnchantmentRegistry = new List<PropertiesEnchantmentRegistry>
                {
                    new PropertiesEnchantmentRegistry
                    {
                        EnchantmentCategory = 1,
                        SpellId = 2931,
                        LayerId = 2,
                        HasSpellSetId = true,
                        SpellCategory = (SpellCategory)152,
                        PowerLevel = 300,
                        StartTime = -120.5,
                        Duration = 1800,
                        CasterObjectId = 0x50000001,
                        DegradeModifier = 1.0f,
                        DegradeLimit = -0.5f,
                        LastTimeDegraded = -60.25,
                        StatModType = EnchantmentTypeFlags.Additive,
                        StatModKey = 31,
                        StatModValue = 20.5f,
                        SpellSetId = (EquipmentSet)15,
                    },
                },
                HousePermissions = new Dictionary<uint, bool>
                {
                    [0x50000020] = true,
                    [0x50000021] = false,
                },
            };
        }
    }
}
