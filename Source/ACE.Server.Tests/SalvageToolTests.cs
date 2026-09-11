using System.Collections.Generic;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Entity;
using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Entity.Models;
using ACE.Server.Entity;
using ACE.Server.EquipmentMods;
using ACE.Server.WeaponMods;
using ACE.Server.WorldObjects;

namespace ACE.Server.Tests
{
    /// <summary>
    /// The multi-charge salvage tool - the three Hammer items - covering recognition, the capacity/remaining
    /// split, the player-facing wording, the appraisal line, and the regression guards around the fullness
    /// gate both mod systems share with ordinary salvage bags.
    ///
    /// THE SHAPE UNDER TEST: PropertyInt.SalvageToolCharges (9035) is the tool's fixed CAPACITY and its
    /// identity marker; PropertyInt.Structure is the LIVE count, with MaxStructure equal to the capacity, so
    /// the client draws its own uses-remaining bar. The consequence is that a Hammer below full is no longer a
    /// "full bag", which is why both managers gate on IsFullBag OR SalvageTool.HasUsableCharge.
    ///
    /// SalvageTool.TryConsume is NOT covered here and cannot be. All of its branches need a live Player: the
    /// whole-object path calls TryConsumeFromInventoryWithNetworking, which needs a session and an inventory,
    /// and the decrement path calls SaveBiotaToDatabase and enqueues a network message on player.Session.
    /// Constructing a Player in a test needs a live world database (a static field on Player triggers it), so
    /// there is nothing to fake short of one. The consume itself belongs to the live loop; everything it
    /// decides FROM is exercised below.
    /// </summary>
    [TestClass]
    public class SalvageToolTests
    {
        private static uint nextGuid = 0x7D100000;   // static guid range, clear of GuidManager
        private static uint nextWcid = 992000;

        /// <summary>
        /// A Hammer-shaped source built from an in-memory weenie: WeenieType.CraftTool (whose
        /// SetEphemeralValues is empty, so no database and no dat files are touched), ItemType
        /// TinkeringMaterial - the shipped Hammers' type, the same as the bag they stand in for - and the given
        /// material. <paramref name="capacity"/> is PropertyInt 9035; null produces an ordinary FULL salvage
        /// bag instead (100/100 structure, no charge property). <paramref name="remaining"/> overrides
        /// Structure, which otherwise starts at the capacity like a freshly bought tool.
        /// <paramref name="itemType"/> is overridable so the refused ItemTypes can be exercised.
        /// </summary>
        private static WorldObject MakeTool(MaterialType material = MaterialType.Obsidian, int? capacity = 10, string name = "Obsidian Hammer", ItemType itemType = ItemType.TinkeringMaterial, int? remaining = null)
        {
            // a capacity of 0 is a degenerate marker rather than a tool, so it keeps the bag's structure
            var max = (capacity ?? 0) > 0 ? capacity.Value : 100;

            var weenie = new Weenie
            {
                WeenieClassId = nextWcid++,
                WeenieType = WeenieType.CraftTool,
                PropertiesInt = new Dictionary<PropertyInt, int>
                {
                    { PropertyInt.ItemType, (int)itemType },
                    { PropertyInt.MaterialType, (int)material },
                    { PropertyInt.Structure, remaining ?? max },
                    { PropertyInt.MaxStructure, max },
                },
                PropertiesString = new Dictionary<PropertyString, string> { { PropertyString.Name, name } },
            };

            if (capacity != null)
                weenie.PropertiesInt[PropertyInt.SalvageToolCharges] = capacity.Value;

            return new CraftTool(weenie, new ObjectGuid(nextGuid++));
        }

        /// <summary>An ordinary salvage bag: no charge property, and whatever fraction of a unit is left.</summary>
        private static WorldObject MakeBag(int structure, MaterialType material = MaterialType.Obsidian, string name = "Salvage (Obsidian)") =>
            MakeTool(material, capacity: null, name: name, remaining: structure);

        // ---------------- recognition ----------------

        [TestMethod]
        public void IsSalvageTool_NeedsTheCapacityPropertyAndAPositiveValue()
        {
            Assert.IsFalse(SalvageTool.IsSalvageTool(null));

            // an ordinary salvage bag does not carry the property at all
            Assert.IsFalse(SalvageTool.IsSalvageTool(MakeTool(capacity: null)));

            // a zero capacity is not a tool either
            Assert.IsFalse(SalvageTool.IsSalvageTool(MakeTool(capacity: 0)));

            Assert.IsTrue(SalvageTool.IsSalvageTool(MakeTool(capacity: 1)));
            Assert.IsTrue(SalvageTool.IsSalvageTool(MakeTool(capacity: 10)));

            // and it stays a tool once spent down - recognition is the CAPACITY, not what is left
            Assert.IsTrue(SalvageTool.IsSalvageTool(MakeTool(capacity: 10, remaining: 0)));
        }

        [TestMethod]
        public void GetCapacity_ReadsTheChargeProperty_GetRemaining_ReadsStructure()
        {
            Assert.AreEqual(0, SalvageTool.GetCapacity(null));
            Assert.AreEqual(0, SalvageTool.GetRemaining(null));

            // a bag has no capacity, but it does have structure
            var bag = MakeTool(capacity: null);
            Assert.AreEqual(0, SalvageTool.GetCapacity(bag));
            Assert.AreEqual(100, SalvageTool.GetRemaining(bag));

            // a fresh tool: both read the same
            var fresh = MakeTool(capacity: 10);
            Assert.AreEqual(10, SalvageTool.GetCapacity(fresh));
            Assert.AreEqual(10, SalvageTool.GetRemaining(fresh));

            // a partly spent tool: the capacity does not move, the live count does
            var spent = MakeTool(capacity: 10, remaining: 3);
            Assert.AreEqual(10, SalvageTool.GetCapacity(spent));
            Assert.AreEqual(3, SalvageTool.GetRemaining(spent));
        }

        // ---------------- the fullness allowance ----------------

        /// <summary>
        /// The rule both managers' fullness gates read. TRUE only for a tool with something left; FALSE for
        /// every bag, whatever its structure, because a bag is judged by IsFullBag instead.
        /// </summary>
        [TestMethod]
        public void HasUsableCharge_IsToolsOnlyAndCountsRemaining()
        {
            Assert.IsFalse(SalvageTool.HasUsableCharge(null));

            Assert.IsTrue(SalvageTool.HasUsableCharge(MakeTool(capacity: 10)));
            Assert.IsTrue(SalvageTool.HasUsableCharge(MakeTool(capacity: 10, remaining: 7)));
            Assert.IsTrue(SalvageTool.HasUsableCharge(MakeTool(capacity: 10, remaining: 1)));

            // spent: should not exist in play (destroyed on the last use) but must never be usable
            Assert.IsFalse(SalvageTool.HasUsableCharge(MakeTool(capacity: 10, remaining: 0)));

            // an ordinary bag is FALSE on this axis whether it is full or not - IsFullBag decides bags
            Assert.IsFalse(SalvageTool.HasUsableCharge(MakeBag(100)));
            Assert.IsFalse(SalvageTool.HasUsableCharge(MakeBag(50)));
        }

        /// <summary>
        /// THE REGRESSION THAT MATTERS MOST. Moving the charge count onto Structure meant relaxing the
        /// fullness gate, and the thing that must not follow from it is a PARTIAL BAG becoming acceptable: a
        /// bag's Structure is a fraction of one unit of salvage, a tool's is a count of whole applications, and
        /// only PropertyInt 9035 tells them apart. This asserts the gate exactly as both managers spell it.
        /// </summary>
        [TestMethod]
        public void CombinedGate_AcceptsAFullBagAndAChargedTool_ButStillRefusesAPartialBag()
        {
            var partial = MakeBag(50);

            Assert.IsFalse(EquipmentModManager.IsFullBag(partial) || SalvageTool.HasUsableCharge(partial));
            Assert.IsFalse(WeaponModManager.IsFullBag(partial) || SalvageTool.HasUsableCharge(partial));

            // an empty bag likewise
            var empty = MakeBag(0);

            Assert.IsFalse(EquipmentModManager.IsFullBag(empty) || SalvageTool.HasUsableCharge(empty));
            Assert.IsFalse(WeaponModManager.IsFullBag(empty) || SalvageTool.HasUsableCharge(empty));

            // a full bag passes on the IsFullBag half, exactly as before
            var full = MakeBag(100);

            Assert.IsTrue(EquipmentModManager.IsFullBag(full) || SalvageTool.HasUsableCharge(full));
            Assert.IsTrue(WeaponModManager.IsFullBag(full) || SalvageTool.HasUsableCharge(full));

            // a part-spent tool fails the IsFullBag half and passes on the allowance - which is the whole
            // reason the allowance exists
            var tool = MakeTool(capacity: 10, remaining: 4);

            Assert.IsFalse(EquipmentModManager.IsFullBag(tool));
            Assert.IsFalse(WeaponModManager.IsFullBag(tool));
            Assert.IsTrue(EquipmentModManager.IsFullBag(tool) || SalvageTool.HasUsableCharge(tool));
            Assert.IsTrue(WeaponModManager.IsFullBag(tool) || SalvageTool.HasUsableCharge(tool));

            // and a spent tool fails BOTH halves, so it is refused rather than silently free
            var dead = MakeTool(capacity: 10, remaining: 0);

            Assert.IsFalse(EquipmentModManager.IsFullBag(dead) || SalvageTool.HasUsableCharge(dead));
            Assert.IsFalse(WeaponModManager.IsFullBag(dead) || SalvageTool.HasUsableCharge(dead));
        }

        // ---------------- player-facing wording ----------------

        [TestMethod]
        public void ChargeMessage_PluralisesAndAnnouncesTheLastCharge()
        {
            // an ordinary bag has nothing to report - the caller skips the line entirely
            Assert.IsNull(SalvageTool.GetChargeMessage("Salvage (Obsidian)", false, 0));

            Assert.AreEqual("The Obsidian Hammer has 9 uses remaining.", SalvageTool.GetChargeMessage("Obsidian Hammer", true, 9));
            Assert.AreEqual("The Obsidian Hammer has 2 uses remaining.", SalvageTool.GetChargeMessage("Obsidian Hammer", true, 2));

            // exactly one left is singular
            Assert.AreEqual("The Obsidian Hammer has 1 use remaining.", SalvageTool.GetChargeMessage("Obsidian Hammer", true, 1));

            // nothing left: the object itself is gone, which is why the name is passed in rather than read
            // off a destroyed WorldObject
            Assert.AreEqual("The Tourmaline Hammer crumbles to dust.", SalvageTool.GetChargeMessage("Tourmaline Hammer", true, 0));
        }

        // ---------------- appraisal ----------------

        /// <summary>
        /// The line is kept alongside the client's own green bar because the bar carries no number label. It
        /// reads remaining OF capacity, so the two halves of the split are both visible to a player.
        /// </summary>
        [TestMethod]
        public void AppraisalLines_AreEmptyForABagAndShowRemainingOfCapacityForATool()
        {
            CollectionAssert.AreEqual(new List<string>(), SalvageTool.GetAppraisalLines(null));
            CollectionAssert.AreEqual(new List<string>(), SalvageTool.GetAppraisalLines(MakeTool(capacity: null)));
            CollectionAssert.AreEqual(new List<string>(), SalvageTool.GetAppraisalLines(MakeTool(capacity: 0)));

            var fresh = SalvageTool.GetAppraisalLines(MakeTool(capacity: 10));

            Assert.AreEqual(1, fresh.Count);
            Assert.AreEqual("- Uses remaining: 10 of 10", fresh[0]);

            var lines = SalvageTool.GetAppraisalLines(MakeTool(capacity: 10, remaining: 7));

            Assert.AreEqual(1, lines.Count);
            Assert.AreEqual("- Uses remaining: 7 of 10", lines[0]);
        }

        // ---------------- regression guard: the material gate needs no special case ----------------

        /// <summary>
        /// A Hammer is ItemType.TinkeringMaterial with the corresponding bag's MaterialType, so both mod
        /// systems' MATERIAL gates accept it unchanged and route it to the right flow. The fullness gate is the
        /// one place a Hammer needs an allowance, and that is covered above.
        /// </summary>
        [TestMethod]
        public void HammerShapedSource_PassesBothManagersMaterialGates()
        {
            var obsidian = MakeTool(MaterialType.Obsidian, 10, "Obsidian Hammer");

            Assert.IsTrue(EquipmentModManager.IsModMaterial(obsidian));

            var tourmaline = MakeTool(MaterialType.Tourmaline, 10, "Tourmaline Hammer");

            Assert.IsTrue(WeaponModManager.IsModMaterial(tourmaline));
            Assert.AreEqual(WeaponModManager.WeaponModAction.Reroll, WeaponModManager.ResolveAction(tourmaline));

            var amethyst = MakeTool(MaterialType.Amethyst, 10, "Amethyst Hammer");

            Assert.IsTrue(WeaponModManager.IsModMaterial(amethyst));
            Assert.AreEqual(WeaponModManager.WeaponModAction.Swap, WeaponModManager.ResolveAction(amethyst));

            // IsFullBag itself is UNCHANGED and still the correct rule for a bag: an explicit MaxStructure 100
            // is honored the same as a bag's absent one
            Assert.IsTrue(EquipmentModManager.IsFullBag(100, 100));
            Assert.IsTrue(WeaponModManager.IsFullBag(100, 100));
            Assert.IsFalse(EquipmentModManager.IsFullBag(50, 100));
            Assert.IsFalse(WeaponModManager.IsFullBag(50, 100));
        }

        /// <summary>
        /// ItemType.TinkeringTool must stay REFUSED by both managers, and the reason is the client, not the
        /// server: using an ItemType.TinkeringTool item makes the client open its own salvage panel and send
        /// GameActionCreateTinkeringTool (0x027D, a client-to-server action), so the use-on-target never
        /// reaches the server and no mod could ever be applied. It was allowed here on 2026-08-01 and reverted
        /// the same day after live play. Anyone widening the gate again fails here first.
        /// </summary>
        [TestMethod]
        public void IsModMaterial_RefusesTinkeringTool_BecauseTheClientHijacksItsUseForItsOwnSalvagePanel()
        {
            Assert.IsFalse(EquipmentModManager.IsModMaterial(ItemType.TinkeringTool, EquipmentModManager.HighTierMaterial));
            Assert.IsFalse(TigerEyeArmorTinker.IsTigerEyeSalvage(ItemType.TinkeringTool, TigerEyeArmorTinker.SourceMaterial));
            Assert.IsFalse(WeaponModManager.IsModMaterial(ItemType.TinkeringTool, WeaponModManager.RerollMaterial));
            Assert.IsFalse(WeaponModManager.IsModMaterial(ItemType.TinkeringTool, WeaponModManager.SwapMaterial));

            // and the same via the WorldObject overload, which must keep delegating rather than duplicating
            Assert.IsFalse(EquipmentModManager.IsModMaterial(MakeTool(MaterialType.Obsidian, 10, "Obsidian Hammer", ItemType.TinkeringTool)));
            Assert.IsFalse(WeaponModManager.IsModMaterial(MakeTool(MaterialType.Tourmaline, 10, "Tourmaline Hammer", ItemType.TinkeringTool)));

            // the bag path is untouched
            Assert.IsTrue(EquipmentModManager.IsModMaterial(ItemType.TinkeringMaterial, EquipmentModManager.HighTierMaterial));
            Assert.IsTrue(TigerEyeArmorTinker.IsTigerEyeSalvage(ItemType.TinkeringMaterial, TigerEyeArmorTinker.SourceMaterial));
            Assert.IsTrue(WeaponModManager.IsModMaterial(ItemType.TinkeringMaterial, WeaponModManager.RerollMaterial));
        }

        /// <summary>
        /// The ItemType check is not decoration: a raw Tourmaline/Amethyst/TigerEye GEM carries exactly the
        /// MaterialType a mod source needs, and only the ItemType stops it being taken for a bag. An
        /// undesignated MaterialType is refused on the other axis.
        /// </summary>
        [TestMethod]
        public void IsModMaterial_RefusesRawGemsAndUndesignatedMaterials()
        {
            // refused: the raw-gem guard, which is the reason the ItemType is checked at all
            Assert.IsFalse(EquipmentModManager.IsModMaterial(ItemType.Gem, EquipmentModManager.HighTierMaterial));
            Assert.IsFalse(TigerEyeArmorTinker.IsTigerEyeSalvage(ItemType.Gem, TigerEyeArmorTinker.SourceMaterial));
            Assert.IsFalse(WeaponModManager.IsModMaterial(ItemType.Gem, WeaponModManager.RerollMaterial));
            Assert.IsFalse(WeaponModManager.IsModMaterial(ItemType.Gem, WeaponModManager.SwapMaterial));

            // refused: a bag-typed item carrying an UNdesignated material
            Assert.IsFalse(EquipmentModManager.IsModMaterial(ItemType.TinkeringMaterial, MaterialType.Silver));
            Assert.IsFalse(EquipmentModManager.IsModMaterial(ItemType.TinkeringMaterial, null));
            Assert.IsFalse(WeaponModManager.IsModMaterial(ItemType.TinkeringMaterial, MaterialType.Silver));
            Assert.IsFalse(WeaponModManager.IsModMaterial(ItemType.TinkeringMaterial, null));

            // and the same via the WorldObject overload, which must keep delegating rather than duplicating
            Assert.IsFalse(EquipmentModManager.IsModMaterial(MakeTool(MaterialType.Obsidian, 10, "Obsidian Gem", ItemType.Gem)));
            Assert.IsFalse(WeaponModManager.IsModMaterial(MakeTool(MaterialType.Tourmaline, 10, "Tourmaline Gem", ItemType.Gem)));
        }
    }
}
