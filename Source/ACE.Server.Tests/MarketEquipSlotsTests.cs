using System.Collections.Generic;
using System.Text.Json;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Entity.Enum;
using ACE.Server.Managers.Market;

namespace ACE.Server.Tests
{
    /// <summary>
    /// MarketEquipSlots.Classify is pure and takes a primitive, so these tests never need a
    /// WorldObject or a Weenie - they exercise Classify (and Label) directly, plus one round-trip
    /// through MarketSnapshot's serialization to prove the JSON keys the web app depends on.
    /// </summary>
    [TestClass]
    public class MarketEquipSlotsTests
    {
        // ---- per-bucket classification ----

        [TestMethod]
        public void Classify_HeadWear_ReturnsHead()
            => CollectionAssert.AreEqual(new List<string> { "head" }, MarketEquipSlots.Classify((int)EquipMask.HeadWear));

        [TestMethod]
        public void Classify_ChestWear_ReturnsChest()
            => CollectionAssert.AreEqual(new List<string> { "chest" }, MarketEquipSlots.Classify((int)EquipMask.ChestWear));

        [TestMethod]
        public void Classify_ChestArmor_ReturnsChest()
            => CollectionAssert.AreEqual(new List<string> { "chest" }, MarketEquipSlots.Classify((int)EquipMask.ChestArmor));

        [TestMethod]
        public void Classify_AbdomenWear_ReturnsAbdomen()
            => CollectionAssert.AreEqual(new List<string> { "abdomen" }, MarketEquipSlots.Classify((int)EquipMask.AbdomenWear));

        [TestMethod]
        public void Classify_AbdomenArmor_ReturnsAbdomen()
            => CollectionAssert.AreEqual(new List<string> { "abdomen" }, MarketEquipSlots.Classify((int)EquipMask.AbdomenArmor));

        [TestMethod]
        public void Classify_UpperArmWear_ReturnsUpperArm()
            => CollectionAssert.AreEqual(new List<string> { "upper_arm" }, MarketEquipSlots.Classify((int)EquipMask.UpperArmWear));

        [TestMethod]
        public void Classify_UpperArmArmor_ReturnsUpperArm()
            => CollectionAssert.AreEqual(new List<string> { "upper_arm" }, MarketEquipSlots.Classify((int)EquipMask.UpperArmArmor));

        [TestMethod]
        public void Classify_LowerArmWear_ReturnsLowerArm()
            => CollectionAssert.AreEqual(new List<string> { "lower_arm" }, MarketEquipSlots.Classify((int)EquipMask.LowerArmWear));

        [TestMethod]
        public void Classify_LowerArmArmor_ReturnsLowerArm()
            => CollectionAssert.AreEqual(new List<string> { "lower_arm" }, MarketEquipSlots.Classify((int)EquipMask.LowerArmArmor));

        [TestMethod]
        public void Classify_HandWear_ReturnsHands()
            => CollectionAssert.AreEqual(new List<string> { "hands" }, MarketEquipSlots.Classify((int)EquipMask.HandWear));

        [TestMethod]
        public void Classify_UpperLegWear_ReturnsUpperLeg()
            => CollectionAssert.AreEqual(new List<string> { "upper_leg" }, MarketEquipSlots.Classify((int)EquipMask.UpperLegWear));

        [TestMethod]
        public void Classify_UpperLegArmor_ReturnsUpperLeg()
            => CollectionAssert.AreEqual(new List<string> { "upper_leg" }, MarketEquipSlots.Classify((int)EquipMask.UpperLegArmor));

        [TestMethod]
        public void Classify_LowerLegWear_ReturnsLowerLeg()
            => CollectionAssert.AreEqual(new List<string> { "lower_leg" }, MarketEquipSlots.Classify((int)EquipMask.LowerLegWear));

        [TestMethod]
        public void Classify_LowerLegArmor_ReturnsLowerLeg()
            => CollectionAssert.AreEqual(new List<string> { "lower_leg" }, MarketEquipSlots.Classify((int)EquipMask.LowerLegArmor));

        [TestMethod]
        public void Classify_FootWear_ReturnsFeet()
            => CollectionAssert.AreEqual(new List<string> { "feet" }, MarketEquipSlots.Classify((int)EquipMask.FootWear));

        [TestMethod]
        public void Classify_NeckWear_ReturnsNeck()
            => CollectionAssert.AreEqual(new List<string> { "neck" }, MarketEquipSlots.Classify((int)EquipMask.NeckWear));

        [TestMethod]
        public void Classify_WristWearLeft_ReturnsWrist()
            => CollectionAssert.AreEqual(new List<string> { "wrist" }, MarketEquipSlots.Classify((int)EquipMask.WristWearLeft));

        [TestMethod]
        public void Classify_WristWearRight_ReturnsWrist()
            => CollectionAssert.AreEqual(new List<string> { "wrist" }, MarketEquipSlots.Classify((int)EquipMask.WristWearRight));

        [TestMethod]
        public void Classify_WristWearBothSides_ReturnsOneWristTokenNotTwo()
        {
            var combined = (int)(EquipMask.WristWearLeft | EquipMask.WristWearRight);
            CollectionAssert.AreEqual(new List<string> { "wrist" }, MarketEquipSlots.Classify(combined));
        }

        [TestMethod]
        public void Classify_FingerWearLeft_ReturnsFinger()
            => CollectionAssert.AreEqual(new List<string> { "finger" }, MarketEquipSlots.Classify((int)EquipMask.FingerWearLeft));

        [TestMethod]
        public void Classify_FingerWearRight_ReturnsFinger()
            => CollectionAssert.AreEqual(new List<string> { "finger" }, MarketEquipSlots.Classify((int)EquipMask.FingerWearRight));

        [TestMethod]
        public void Classify_FingerWearBothSides_ReturnsOneFingerTokenNotTwo()
        {
            var combined = (int)(EquipMask.FingerWearLeft | EquipMask.FingerWearRight);
            CollectionAssert.AreEqual(new List<string> { "finger" }, MarketEquipSlots.Classify(combined));
        }

        [TestMethod]
        public void Classify_TrinketOne_ReturnsTrinket()
            => CollectionAssert.AreEqual(new List<string> { "trinket" }, MarketEquipSlots.Classify((int)EquipMask.TrinketOne));

        [TestMethod]
        public void Classify_Cloak_ReturnsCloak()
            => CollectionAssert.AreEqual(new List<string> { "cloak" }, MarketEquipSlots.Classify((int)EquipMask.Cloak));

        [TestMethod]
        public void Classify_Shield_ReturnsShield()
            => CollectionAssert.AreEqual(new List<string> { "shield" }, MarketEquipSlots.Classify((int)EquipMask.Shield));

        // ---- multi-slot cases from real data ----

        [TestMethod]
        public void Classify_32512_SevenSlotArmorSuit()
        {
            // EquipMask.Armor: ChestArmor|AbdomenArmor|UpperArmArmor|LowerArmArmor|UpperLegArmor|
            // LowerLegArmor|FootWear = 0x7F00 = 32512.
            var expected = new List<string> { "chest", "abdomen", "upper_arm", "lower_arm", "upper_leg", "lower_leg", "feet" };
            CollectionAssert.AreEqual(expected, MarketEquipSlots.Classify(32512));
        }

        [TestMethod]
        public void Classify_25600_AbdomenAndBothLegs()
        {
            // AbdomenArmor(0x400)|UpperLegArmor(0x2000)|LowerLegArmor(0x4000) = 0x6400 = 25600.
            var expected = new List<string> { "abdomen", "upper_leg", "lower_leg" };
            CollectionAssert.AreEqual(expected, MarketEquipSlots.Classify(25600));
        }

        [TestMethod]
        public void Classify_786432_Finger()
        {
            // FingerWearLeft(0x40000)|FingerWearRight(0x80000) = 0xC0000 = 786432.
            CollectionAssert.AreEqual(new List<string> { "finger" }, MarketEquipSlots.Classify(786432));
        }

        [TestMethod]
        public void Classify_196608_Wrist()
        {
            // WristWearLeft(0x10000)|WristWearRight(0x20000) = 0x30000 = 196608.
            CollectionAssert.AreEqual(new List<string> { "wrist" }, MarketEquipSlots.Classify(196608));
        }

        [TestMethod]
        public void Classify_6656_ChestAndBothArms()
        {
            // ChestArmor(0x200)|UpperArmArmor(0x800)|LowerArmArmor(0x1000) = 0x1A00 = 6656.
            var expected = new List<string> { "chest", "upper_arm", "lower_arm" };
            CollectionAssert.AreEqual(expected, MarketEquipSlots.Classify(6656));
        }

        // ---- null, empty, weapon-only, and unknown-bit cases ----

        [TestMethod]
        public void Classify_Null_ReturnsNull()
            => Assert.IsNull(MarketEquipSlots.Classify(null));

        [TestMethod]
        public void Classify_WeaponOnlyMask_ReturnsEmptyList()
        {
            // MeleeWeapon|Shield|MissileWeapon|Held|TwoHanded (EquipMask.Selectable), minus Shield,
            // since Shield IS offered - use the weapon-only bits alone instead.
            var weaponOnly = (int)(EquipMask.MeleeWeapon | EquipMask.MissileWeapon | EquipMask.MissileAmmo | EquipMask.Held | EquipMask.TwoHanded);
            var result = MarketEquipSlots.Classify(weaponOnly);

            Assert.IsNotNull(result);
            Assert.AreEqual(0, result.Count);
        }

        [TestMethod]
        public void Classify_Zero_ReturnsEmptyList()
        {
            var result = MarketEquipSlots.Classify(0);

            Assert.IsNotNull(result);
            Assert.AreEqual(0, result.Count);
        }

        [TestMethod]
        public void Classify_UnknownHighBit_IsIgnoredWithoutThrowing()
        {
            // 0x80000000 is the high bit of the EquipMask.Clothing composite and maps to no bucket
            // on its own.
            var result = MarketEquipSlots.Classify(unchecked((int)0x80000000));

            Assert.IsNotNull(result);
            Assert.AreEqual(0, result.Count);
        }

        [TestMethod]
        public void Classify_SigilSlots_ReturnEmptyList()
        {
            var sigils = (int)(EquipMask.SigilOne | EquipMask.SigilTwo | EquipMask.SigilThree);
            var result = MarketEquipSlots.Classify(sigils);

            Assert.IsNotNull(result);
            Assert.AreEqual(0, result.Count);
        }

        // ---- canonical ordering is stable regardless of bit order in the mask ----

        [TestMethod]
        public void Classify_ScatteredBits_ReturnsCanonicalOrder()
        {
            // Bits set from high to low in the mask (shield, wrist, chest, head), the classifier
            // must still emit head, chest, wrist, shield in canonical order.
            var scattered = (int)(EquipMask.Shield | EquipMask.WristWearLeft | EquipMask.ChestWear | EquipMask.HeadWear);
            var expected = new List<string> { "head", "chest", "wrist", "shield" };
            CollectionAssert.AreEqual(expected, MarketEquipSlots.Classify(scattered));
        }

        // ---- Label ----

        [TestMethod]
        public void Label_ReturnsTheExactLabelForEveryToken()
        {
            Assert.AreEqual("Head", MarketEquipSlots.Label("head"));
            Assert.AreEqual("Chest", MarketEquipSlots.Label("chest"));
            Assert.AreEqual("Abdomen", MarketEquipSlots.Label("abdomen"));
            Assert.AreEqual("Upper Arm", MarketEquipSlots.Label("upper_arm"));
            Assert.AreEqual("Lower Arm", MarketEquipSlots.Label("lower_arm"));
            Assert.AreEqual("Hands", MarketEquipSlots.Label("hands"));
            Assert.AreEqual("Upper Leg", MarketEquipSlots.Label("upper_leg"));
            Assert.AreEqual("Lower Leg", MarketEquipSlots.Label("lower_leg"));
            Assert.AreEqual("Feet", MarketEquipSlots.Label("feet"));
            Assert.AreEqual("Neck", MarketEquipSlots.Label("neck"));
            Assert.AreEqual("Wrist", MarketEquipSlots.Label("wrist"));
            Assert.AreEqual("Finger", MarketEquipSlots.Label("finger"));
            Assert.AreEqual("Trinket", MarketEquipSlots.Label("trinket"));
            Assert.AreEqual("Cloak", MarketEquipSlots.Label("cloak"));
            Assert.AreEqual("Shield", MarketEquipSlots.Label("shield"));
        }

        [TestMethod]
        public void Label_UnknownToken_ReturnsNull()
            => Assert.IsNull(MarketEquipSlots.Label("not_a_real_token"));

        // ---- the wire contract ----

        [TestMethod]
        public void Serialize_UsesTheLiteralSlotAndSetFieldNames()
        {
            var snapshot = new ListingSnapshot
            {
                Slots = new List<string> { "chest", "abdomen" },
                EquipmentSet = "Defenders",
                EquipmentSetName = "Defenders",
            };

            var json = JsonSerializer.Serialize(snapshot, MarketSnapshot.JsonOptions);

            Assert.IsTrue(json.Contains("\"slots\":[\"chest\",\"abdomen\"]"), json);
            Assert.IsTrue(json.Contains("\"equipment_set\":\"Defenders\""), json);
            Assert.IsTrue(json.Contains("\"equipment_set_name\":\"Defenders\""), json);

            var after = MarketSnapshot.Deserialize(json);
            CollectionAssert.AreEqual(new List<string> { "chest", "abdomen" }, after.Slots);
            Assert.AreEqual("Defenders", after.EquipmentSet);
            Assert.AreEqual("Defenders", after.EquipmentSetName);
        }

        [TestMethod]
        public void Serialize_NullSlots_OmitsTheKey_EmptySlots_SerializesAsEmptyArray()
        {
            // MarketSnapshot.JsonOptions ignores null on write (DefaultIgnoreCondition.WhenWritingNull),
            // so a null Slots omits the key entirely, while an empty list still serializes as "[]" -
            // this is exactly the distinction that lets the web app tell "never classified" from
            // "classified as no slots".
            var nullSlots = new ListingSnapshot { Slots = null };
            var emptySlots = new ListingSnapshot { Slots = new List<string>() };

            var nullJson = JsonSerializer.Serialize(nullSlots, MarketSnapshot.JsonOptions);
            var emptyJson = JsonSerializer.Serialize(emptySlots, MarketSnapshot.JsonOptions);

            Assert.IsFalse(nullJson.Contains("\"slots\""), nullJson);
            Assert.IsTrue(emptyJson.Contains("\"slots\":[]"), emptyJson);

            Assert.IsNull(MarketSnapshot.Deserialize(nullJson).Slots);
            CollectionAssert.AreEqual(new List<string>(), MarketSnapshot.Deserialize(emptyJson).Slots);
        }
    }
}
