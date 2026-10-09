using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.DatLoader;
using ACE.Entity;
using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Entity.Models;
using ACE.Server.Managers.Market;
using ACE.Server.Managers.Market.Suit;
using ACE.Server.WorldObjects;

namespace ACE.Server.Tests
{
    /// <summary>
    /// SuitItemProjector: the one projection of an equippable item the suit optimizer consumes. Like
    /// MarketSnapshotFieldTests, nothing here may depend on the dats being present.
    /// </summary>
    [TestClass]
    public class SuitItemProjectorTests
    {
        private static int nextGuid = 0x7F600000;

        private IClothingIconSource savedClothingIcons;

        [TestInitialize]
        public void Setup()
        {
            savedClothingIcons = MarketSnapshot.ClothingIcons;
            MarketSnapshot.ClothingIcons = null;
        }

        [TestCleanup]
        public void Teardown() => MarketSnapshot.ClothingIcons = savedClothingIcons;

        // ---- fixtures ----

        private static Weenie NewWeenie(uint wcid, string name, ItemType type, EquipMask locations)
        {
            return new Weenie
            {
                WeenieClassId = wcid,
                WeenieType = WeenieType.Clothing,
                PropertiesString = new Dictionary<PropertyString, string> { { PropertyString.Name, name } },
                PropertiesInt = new Dictionary<PropertyInt, int>
                {
                    { PropertyInt.ItemType, (int)type },
                    { PropertyInt.ValidLocations, unchecked((int)locations) },
                    { PropertyInt.EncumbranceVal, 120 },
                },
            };
        }

        /// <summary>An armor piece with every field the projection reads set to a distinct value.</summary>
        private static Weenie FullArmor()
        {
            var w = NewWeenie(1001001, "Breastplate of Testing", ItemType.Armor, EquipMask.ChestArmor | EquipMask.AbdomenArmor);

            w.PropertiesInt[PropertyInt.ClothingPriority] = (int)(CoverageMask.OuterwearChest | CoverageMask.OuterwearAbdomen);
            w.PropertiesInt[PropertyInt.ArmorLevel] = 345;
            w.PropertiesInt[PropertyInt.EquipmentSetId] = (int)EquipmentSet.Defenders;
            w.PropertiesInt[PropertyInt.ItemCurMana] = 410;
            w.PropertiesInt[PropertyInt.ItemMaxMana] = 520;
            w.PropertiesInt[PropertyInt.ItemDifficulty] = 285;
            w.PropertiesInt[PropertyInt.ItemSkillLevelLimit] = 330;
            w.PropertiesInt[PropertyInt.HeritageSpecificArmor] = (int)HeritageGroup.Gearknight;
            w.PropertiesInt[PropertyInt.ItemMaxLevel] = 5;
            w.PropertiesInt[PropertyInt.ItemXpStyle] = (int)ItemXpStyle.ScalesWithLevel;
            w.PropertiesInt[PropertyInt.WieldRequirements] = (int)WieldRequirement.RawSkill;
            w.PropertiesInt[PropertyInt.WieldSkillType] = (int)Skill.HeavyWeapons;
            w.PropertiesInt[PropertyInt.WieldDifficulty] = 250;
            w.PropertiesInt64 = new Dictionary<PropertyInt64, long>
            {
                { PropertyInt64.ItemBaseXp, 1000 },
                { PropertyInt64.ItemTotalXp, 3500 },
            };
            w.PropertiesDID = new Dictionary<PropertyDataId, uint>
            {
                { PropertyDataId.Icon, 0x06001234 },
                { PropertyDataId.IconOverlay, 0x06005678 },
                { PropertyDataId.IconUnderlay, 0x06009ABC },
                { PropertyDataId.ItemSkillLimit, (uint)Skill.WarMagic },
                { PropertyDataId.Spell, 2137 },
                { PropertyDataId.ProcSpell, 5204 },
            };
            w.PropertiesIID = new Dictionary<PropertyInstanceId, uint> { { PropertyInstanceId.AllowedWielder, 0x50000123 } };
            // Out of order, and including the spell DID and proc spell: only 4325 and 1000 are worn buffs.
            w.PropertiesSpellBook = new Dictionary<int, float>
            {
                { 4325, 2f },
                { 2137, 2f },
                { 5204, 2f },
                { 1000, 2f },
            };

            return w;
        }

        private static Weenie Ring()
            => NewWeenie(1001002, "Ring of Testing", ItemType.Jewelry, EquipMask.FingerWearLeft | EquipMask.FingerWearRight);

        private static Weenie Cloak()
        {
            var w = NewWeenie(1001003, "Cloak of Testing", ItemType.Armor, EquipMask.Cloak);
            w.PropertiesDID = new Dictionary<PropertyDataId, uint> { { PropertyDataId.ProcSpell, 5204 } };
            w.PropertiesSpellBook = new Dictionary<int, float> { { 4325, 2f } };
            return w;
        }

        private static Weenie Shirt()
        {
            var w = NewWeenie(1001004, "Shirt of Testing", ItemType.Clothing,
                EquipMask.ChestWear | EquipMask.AbdomenWear | EquipMask.UpperArmWear | EquipMask.LowerArmWear);
            w.PropertiesInt[PropertyInt.ClothingPriority] = (int)(CoverageMask.UnderwearChest | CoverageMask.UnderwearAbdomen);
            return w;
        }

        private static WorldObject AsClothing(Weenie weenie)
            => new Clothing(weenie, new ObjectGuid((uint)nextGuid++));

        private static string Json(SuitItem item) => JsonSerializer.Serialize(item, MarketSnapshot.JsonOptions);

        // ---- field mirror ----

        [TestMethod]
        public void Armor_EveryFieldComesFromTheRightProperty()
        {
            var weenie = FullArmor();
            var wo = AsClothing(weenie);

            var s = SuitItemProjector.FromItem(wo, SuitItem.SourceEquipped, classKey: "k1", count: 3);

            Assert.AreEqual("equipped", s.Source);
            Assert.AreEqual(wo.Guid.Full, s.ItemGuid);
            Assert.AreEqual(1001001u, s.Wcid);
            Assert.AreEqual("k1", s.ClassKey);
            Assert.AreEqual(3, s.Count);
            Assert.IsFalse(s.Listed);
            Assert.AreEqual("Breastplate of Testing", s.Name);
            Assert.AreEqual(0x06001234u, s.IconId);
            Assert.AreEqual(0x06005678u, s.IconOverlayId);
            Assert.AreEqual(0x06009ABCu, s.IconUnderlayId);
            Assert.IsFalse(s.IsClothing);
            Assert.AreEqual((int)(EquipMask.ChestArmor | EquipMask.AbdomenArmor), s.ValidLocations);
            Assert.AreEqual((int)(CoverageMask.OuterwearChest | CoverageMask.OuterwearAbdomen), s.ClothingPriority);
            Assert.AreEqual(345, s.ArmorLevel);
            Assert.AreEqual((int)EquipmentSet.Defenders, s.EquipmentSetId);
            Assert.AreEqual(2, s.ItemLevel, "3500 xp on a 1000 base doubling scale is level 2");
            CollectionAssert.AreEqual(new uint[] { 1000, 4325 }, s.WornSpellIds);
            Assert.AreEqual(410, s.ItemCurMana);
            Assert.AreEqual(520, s.ItemMaxMana);
            Assert.AreEqual(285, s.ItemDifficulty);
            Assert.AreEqual((uint)Skill.WarMagic, s.ItemSkillLimit);
            Assert.AreEqual(330, s.ItemSkillLevelLimit);
            Assert.AreEqual(1, s.WieldReqs.Count);
            Assert.AreEqual((int)HeritageGroup.Gearknight, s.HeritageSpecificArmor);
            Assert.AreEqual(0x50000123u, s.AllowedWielder);
            Assert.AreEqual(120, s.Encumbrance);
        }

        [TestMethod]
        public void Ring_ProjectsWithNoArmorFields()
        {
            var s = SuitItemProjector.FromItem(AsClothing(Ring()));

            Assert.AreEqual((int)(EquipMask.FingerWearLeft | EquipMask.FingerWearRight), s.ValidLocations);
            Assert.AreEqual(0, s.ClothingPriority);
            Assert.IsNull(s.ArmorLevel);
            Assert.IsNull(s.EquipmentSetId);
            Assert.IsNull(s.ItemLevel);
            Assert.IsFalse(s.IsClothing);
            Assert.AreEqual(0, s.WornSpellIds.Length);
            Assert.AreEqual(0, s.WieldReqs.Count);
            Assert.AreEqual("vault", s.Source);
            Assert.AreEqual(1, s.Count);
        }

        [TestMethod]
        public void Cloak_KeepsItsBookSpellsButNotItsSurge()
        {
            var s = SuitItemProjector.FromItem(AsClothing(Cloak()));

            Assert.AreEqual((int)EquipMask.Cloak, s.ValidLocations);
            CollectionAssert.AreEqual(new uint[] { 4325 }, s.WornSpellIds);
        }

        [TestMethod]
        public void UnderclothingShirt_IsClothingWithItsCoverage()
        {
            var s = SuitItemProjector.FromItem(AsClothing(Shirt()));

            Assert.IsTrue(s.IsClothing);
            Assert.AreEqual((int)(CoverageMask.UnderwearChest | CoverageMask.UnderwearAbdomen), s.ClothingPriority);
        }

        [TestMethod]
        public void NullInputs_ProjectToNull()
        {
            Assert.IsNull(SuitItemProjector.FromItem(null));
            Assert.IsNull(SuitItemProjector.FromWeenie(null));
            Assert.IsNull(SuitItemProjector.FromLedgerProbe(null, 1, "k", 1));
        }

        [TestMethod]
        public void ItemLevel_NeverGainedXp_IsZeroAndDoesNotThrow()
        {
            var weenie = FullArmor();
            weenie.PropertiesInt64.Remove(PropertyInt64.ItemTotalXp);

            Assert.AreEqual(0, SuitItemProjector.FromItem(AsClothing(weenie)).ItemLevel);
            Assert.AreEqual(0, SuitItemProjector.FromWeenie(weenie).ItemLevel);
        }

        [TestMethod]
        public void SerializedKeys_AreExactlyTheContract()
        {
            // Populate the one optional collection too, so nothing is omitted by WhenWritingNull.
            var weenie = FullArmor();
            var s = SuitItemProjector.FromItem(AsClothing(weenie), classKey: "k1");

            var keys = JsonDocument.Parse(Json(s)).RootElement.EnumerateObject().Select(p => p.Name).OrderBy(n => n, StringComparer.Ordinal).ToArray();

            var expected = new[]
            {
                "source", "item_guid", "wcid", "class_key", "count", "listed", "name", "icon_id", "icon_overlay_id",
                "icon_underlay_id", "is_clothing", "valid_locations", "clothing_priority", "armor_level", "equipment_set_id",
                "item_level", "worn_spell_ids", "item_cur_mana", "item_max_mana", "item_difficulty", "item_skill_limit",
                "item_skill_level_limit", "wield_reqs", "heritage_specific_armor", "allowed_wielder", "encumbrance",
            }.OrderBy(n => n, StringComparer.Ordinal).ToArray();

            CollectionAssert.AreEqual(expected, keys);

            var req = JsonDocument.Parse(Json(s)).RootElement.GetProperty("wield_reqs")[0];
            CollectionAssert.AreEqual(new[] { "kind", "skill_or_attr", "difficulty" }, req.EnumerateObject().Select(p => p.Name).ToArray());
        }

        // ---- wield requirements ----

        [TestMethod]
        public void WieldReqs_AllFourSlots_AreReadInSlotOrder()
        {
            var w = Ring();
            w.PropertiesInt[PropertyInt.WieldRequirements] = (int)WieldRequirement.RawSkill;
            w.PropertiesInt[PropertyInt.WieldSkillType] = (int)Skill.HeavyWeapons;
            w.PropertiesInt[PropertyInt.WieldDifficulty] = 100;
            w.PropertiesInt[PropertyInt.WieldRequirements2] = (int)WieldRequirement.Level;
            w.PropertiesInt[PropertyInt.WieldSkillType2] = (int)Skill.Axe;
            w.PropertiesInt[PropertyInt.WieldDifficulty2] = 180;
            w.PropertiesInt[PropertyInt.WieldRequirements3] = (int)WieldRequirement.Training;
            w.PropertiesInt[PropertyInt.WieldSkillType3] = (int)Skill.WarMagic;
            w.PropertiesInt[PropertyInt.WieldDifficulty3] = 2;
            w.PropertiesInt[PropertyInt.WieldRequirements4] = (int)WieldRequirement.HeritageType;
            w.PropertiesInt[PropertyInt.WieldSkillType4] = 0;
            w.PropertiesInt[PropertyInt.WieldDifficulty4] = 7;

            var reqs = SuitItemProjector.FromItem(AsClothing(w)).WieldReqs;

            Assert.AreEqual(4, reqs.Count);
            Assert.AreEqual(new WieldReq { Kind = (int)WieldRequirement.RawSkill, SkillOrAttr = (int)Skill.HeavyWeapons, Difficulty = 100 }, reqs[0]);
            Assert.AreEqual(new WieldReq { Kind = (int)WieldRequirement.Level, SkillOrAttr = (int)Skill.Axe, Difficulty = 180 }, reqs[1]);
            Assert.AreEqual(new WieldReq { Kind = (int)WieldRequirement.Training, SkillOrAttr = (int)Skill.WarMagic, Difficulty = 2 }, reqs[2]);
            Assert.AreEqual(new WieldReq { Kind = (int)WieldRequirement.HeritageType, SkillOrAttr = 0, Difficulty = 7 }, reqs[3]);
        }

        [TestMethod]
        public void WieldReqs_LevelInSlotTwo_WithSlotOneOccupied_IsKept()
        {
            // The covenant/olthoi shape SetWieldLevelReq produces: slot 1 holds something else.
            var w = Shirt();
            w.PropertiesInt[PropertyInt.WieldRequirements] = (int)WieldRequirement.Training;
            w.PropertiesInt[PropertyInt.WieldSkillType] = (int)Skill.HeavyWeapons;
            w.PropertiesInt[PropertyInt.WieldDifficulty] = 2;
            w.PropertiesInt[PropertyInt.WieldRequirements2] = (int)WieldRequirement.Level;
            w.PropertiesInt[PropertyInt.WieldSkillType2] = (int)Skill.Axe;
            w.PropertiesInt[PropertyInt.WieldDifficulty2] = 180;

            var reqs = SuitItemProjector.FromItem(AsClothing(w)).WieldReqs;

            Assert.AreEqual(2, reqs.Count);
            Assert.AreEqual((int)WieldRequirement.Level, reqs[1].Kind);
            Assert.AreEqual(180, reqs[1].Difficulty);
        }

        [TestMethod]
        public void WieldReqs_EmptySlotsAreOmitted_ButOrderIsKept()
        {
            var w = Ring();
            w.PropertiesInt[PropertyInt.WieldRequirements3] = (int)WieldRequirement.Level;
            w.PropertiesInt[PropertyInt.WieldDifficulty3] = 150;
            w.PropertiesInt[PropertyInt.WieldRequirements4] = (int)WieldRequirement.Training;
            w.PropertiesInt[PropertyInt.WieldDifficulty4] = 1;

            var reqs = SuitItemProjector.FromItem(AsClothing(w)).WieldReqs;

            Assert.AreEqual(2, reqs.Count);
            Assert.AreEqual((int)WieldRequirement.Level, reqs[0].Kind);
            Assert.AreEqual((int)WieldRequirement.Training, reqs[1].Kind);
        }

        // ---- worn spells ----

        [TestMethod]
        public void WornSpellIds_ExcludeTheProcSpellAndTheSpellDid()
        {
            var w = Cloak();
            w.PropertiesDID[PropertyDataId.Spell] = 2137;
            w.PropertiesSpellBook = new Dictionary<int, float> { { 5204, 2f }, { 2137, 2f }, { 4325, 2f } };

            CollectionAssert.AreEqual(new uint[] { 4325 }, SuitItemProjector.FromItem(AsClothing(w)).WornSpellIds);
            CollectionAssert.AreEqual(new uint[] { 4325 }, SuitItemProjector.FromWeenie(w).WornSpellIds);
        }

        // ---- ledger / weenie vs item ----

        [TestMethod]
        public void WeenieProjection_MatchesItemProjection_ForAnUnmodifiedItem()
        {
            foreach (var weenie in new[] { FullArmor(), Ring(), Cloak(), Shirt() })
            {
                var fromItem = SuitItemProjector.FromItem(AsClothing(weenie), classKey: "kk", count: 5);
                var fromWeenie = SuitItemProjector.FromWeenie(weenie, classKey: "kk", count: 5);

                Assert.IsNotNull(fromItem.ItemGuid);
                Assert.IsNull(fromWeenie.ItemGuid, "a ledger row has no biota to name");
                Assert.AreEqual(Json(fromItem with { ItemGuid = null }), Json(fromWeenie), weenie.PropertiesString[PropertyString.Name]);
            }
        }

        [TestMethod]
        public void LedgerProbe_ClearsTheProbeGuid_AndTakesTheLedgerWcidAndCount()
        {
            var probe = AsClothing(FullArmor());

            var s = SuitItemProjector.FromLedgerProbe(probe, 777u, "pool", 12);

            Assert.IsNull(s.ItemGuid);
            Assert.AreEqual(777u, s.Wcid);
            Assert.AreEqual("pool", s.ClassKey);
            Assert.AreEqual(12, s.Count);
            Assert.AreEqual("vault", s.Source);
        }

        // ---- relevance ----

        [TestMethod]
        public void IsSuitRelevant_Table()
        {
            var cases = new (string what, EquipMask? mask, bool expected)[]
            {
                ("armor chest", EquipMask.ChestArmor, true),
                ("armor multi", EquipMask.ChestArmor | EquipMask.AbdomenArmor | EquipMask.UpperArmArmor, true),
                ("underclothing shirt", EquipMask.ChestWear | EquipMask.AbdomenWear | EquipMask.UpperArmWear | EquipMask.LowerArmWear, true),
                ("pants", EquipMask.UpperLegWear | EquipMask.LowerLegWear, true),
                ("helm", EquipMask.HeadWear, true),
                ("boots", EquipMask.FootWear, true),
                ("gloves", EquipMask.HandWear, true),
                ("clothing flag set", EquipMask.Clothing, true),
                ("necklace", EquipMask.NeckWear, true),
                ("bracelet left", EquipMask.WristWearLeft, true),
                ("bracelet either", EquipMask.WristWearLeft | EquipMask.WristWearRight, true),
                ("ring", EquipMask.FingerWearLeft | EquipMask.FingerWearRight, true),
                ("trinket", EquipMask.TrinketOne, true),
                ("cloak", EquipMask.Cloak, true),
                ("shield", EquipMask.Shield, false),
                ("melee weapon", EquipMask.MeleeWeapon, false),
                ("missile weapon", EquipMask.MissileWeapon, false),
                ("ammo", EquipMask.MissileAmmo, false),
                ("held caster", EquipMask.Held, false),
                ("two handed", EquipMask.TwoHanded, false),
                ("aetheria blue", EquipMask.SigilOne, false),
                ("aetheria all", EquipMask.SigilOne | EquipMask.SigilTwo | EquipMask.SigilThree, false),
                ("no locations", EquipMask.None, false),
                ("absent", null, false),
            };

            foreach (var (what, mask, expected) in cases)
            {
                int? raw = mask.HasValue ? unchecked((int)(uint)mask.Value) : (int?)null;
                Assert.AreEqual(expected, SuitItemProjector.IsSuitRelevant(raw), what);
            }

            Assert.IsTrue(SuitItemProjector.IsSuitRelevant(AsClothing(Cloak())));
            Assert.IsTrue(SuitItemProjector.IsSuitRelevant(Ring()));
            Assert.IsFalse(SuitItemProjector.IsSuitRelevant(AsClothing(NewWeenie(1, "Shield", ItemType.Armor, EquipMask.Shield))));
            Assert.IsFalse(SuitItemProjector.IsSuitRelevant((WorldObject)null));
            Assert.IsFalse(SuitItemProjector.IsSuitRelevant((Weenie)null));
        }

        // ---- purity ----

        [TestMethod]
        public void Projection_NeverReadsWorkmanship_SoItWritesNothing()
        {
            // ItemWorkmanship 117 with no NumItemsInMaterial is out of [1, 10], which is the exact
            // shape on which the Workmanship getter rewrites ItemWorkmanship in place.
            var weenie = FullArmor();
            weenie.PropertiesInt[PropertyInt.ItemWorkmanship] = 117;

            // Positive control: reading the getter DOES change the stored value, so the assertion
            // below can fail. Without this the test would pass on any item.
            var control = AsClothing(weenie);
            Assert.AreEqual(117, control.GetProperty(PropertyInt.ItemWorkmanship));
            _ = control.Workmanship;
            Assert.AreNotEqual(117, control.GetProperty(PropertyInt.ItemWorkmanship), "the control read must write, or this test proves nothing");

            var wo = AsClothing(weenie);
            var before = Snapshot(wo);

            SuitItemProjector.FromItem(wo);
            SuitItemProjector.FromLedgerProbe(wo, 1, "k", 1);

            Assert.AreEqual(117, wo.GetProperty(PropertyInt.ItemWorkmanship));
            CollectionAssert.AreEqual(before, Snapshot(wo), "the projection changed the item's properties");
        }

        private static List<string> Snapshot(WorldObject wo)
        {
            var b = wo.Biota;

            IEnumerable<string> Dump<TKey, TValue>(string tag, IDictionary<TKey, TValue> d)
                => (d ?? new Dictionary<TKey, TValue>()).Select(k => $"{tag}{Convert.ToInt32(k.Key)}={k.Value}").OrderBy(s => s, StringComparer.Ordinal);

            return Dump("i", b.PropertiesInt)
                .Concat(Dump("l", b.PropertiesInt64))
                .Concat(Dump("d", b.PropertiesDID))
                .Concat(Dump("o", b.PropertiesIID))
                .Concat(Dump("f", b.PropertiesFloat))
                .Concat(Dump("b", b.PropertiesBool))
                .Concat(Dump("s", b.PropertiesString))
                .ToList();
        }
    }
}
