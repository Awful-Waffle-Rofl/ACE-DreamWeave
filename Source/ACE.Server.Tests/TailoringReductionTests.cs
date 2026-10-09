using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.DatLoader;
using ACE.Entity;
using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Entity.Models;
using ACE.Server.Entity;
using ACE.Server.Managers.CharacterSheets;
using ACE.Server.Managers.Market;
using ACE.Server.Managers.Market.Suit;
using ACE.Server.WorldObjects;

namespace ACE.Server.Tests
{
    /// <summary>
    /// Tailoring.ReductionResult (the one decision behind both the in-game armor reduction tools and the
    /// suit API's "reductions") and its projection into SuitItem. TailorReduceArmor itself needs a live
    /// Player, which this test assembly cannot construct, so equivalence is by construction: TailorReduceArmor
    /// has no branch of its own and applies exactly what ReductionResult returns.
    /// </summary>
    [TestClass]
    public class TailoringReductionTests
    {
        private static int nextGuid = 0x7F700000;

        private IClothingIconSource savedClothingIcons;

        [TestInitialize]
        public void Setup()
        {
            savedClothingIcons = MarketSnapshot.ClothingIcons;
            MarketSnapshot.ClothingIcons = null;
        }

        [TestCleanup]
        public void Teardown() => MarketSnapshot.ClothingIcons = savedClothingIcons;

        private const EquipMask CoatMask = EquipMask.ChestArmor | EquipMask.UpperArmArmor | EquipMask.LowerArmArmor;
        private const EquipMask SleevesMask = EquipMask.UpperArmArmor | EquipMask.LowerArmArmor;
        private const EquipMask PantsMask = EquipMask.AbdomenArmor | EquipMask.UpperLegArmor | EquipMask.LowerLegArmor;
        private const EquipMask LeggingsMask = EquipMask.LowerLegArmor | EquipMask.FootWear;

        private static WorldObject Armor(EquipMask locations, bool workmanship = true, bool retained = false, int? wieldSkillType = null, CoverageMask priority = CoverageMask.OuterwearChest)
        {
            var w = new Weenie
            {
                WeenieClassId = 1001900,
                WeenieType = WeenieType.Clothing,
                PropertiesString = new Dictionary<PropertyString, string> { { PropertyString.Name, "Test Armor" } },
                PropertiesInt = new Dictionary<PropertyInt, int>
                {
                    { PropertyInt.ItemType, (int)ItemType.Armor },
                    { PropertyInt.ValidLocations, (int)locations },
                    { PropertyInt.ClothingPriority, (int)priority },
                },
            };

            if (workmanship)
                w.PropertiesInt[PropertyInt.ItemWorkmanship] = 8;
            if (wieldSkillType.HasValue)
                w.PropertiesInt[PropertyInt.WieldSkillType] = wieldSkillType.Value;
            if (retained)
                w.PropertiesBool = new Dictionary<PropertyBool, bool> { { PropertyBool.Retained, true } };

            return new Clothing(w, new ObjectGuid((uint)nextGuid++));
        }

        private static void AssertResult(WorldObject item, Tailoring.ReductionTool tool, EquipMask? expectedLocations, CoverageMask expectedPriority = CoverageMask.Unknown)
        {
            var r = Tailoring.ReductionResult(item, tool);

            if (expectedLocations == null)
            {
                Assert.IsNull(r, $"{tool} should be refused");
                return;
            }

            Assert.IsNotNull(r, $"{tool} should succeed");
            Assert.AreEqual(expectedLocations.Value, r.Value.validLocations, $"{tool} locations");
            Assert.AreEqual(expectedPriority, r.Value.clothingPriority, $"{tool} priority");
        }

        // ---- pure function ----

        [TestMethod]
        public void Coat_MainGoesToChest_LowerAndMiddleRefused()
        {
            var coat = Armor(CoatMask);

            AssertResult(coat, Tailoring.ReductionTool.Main, EquipMask.ChestArmor, CoverageMask.OuterwearChest);
            AssertResult(coat, Tailoring.ReductionTool.Lower, null);
            AssertResult(coat, Tailoring.ReductionTool.Middle, null);
        }

        [TestMethod]
        public void FullSleeves_MainToUpperArm_LowerToLowerArm()
        {
            var sleeves = Armor(SleevesMask);

            AssertResult(sleeves, Tailoring.ReductionTool.Main, EquipMask.UpperArmArmor, CoverageMask.OuterwearUpperArms);
            AssertResult(sleeves, Tailoring.ReductionTool.Lower, EquipMask.LowerArmArmor, CoverageMask.OuterwearLowerArms);
            AssertResult(sleeves, Tailoring.ReductionTool.Middle, null);
        }

        [TestMethod]
        public void Pants_MainAbdomen_LowerLowerLeg_MiddleUpperLeg()
        {
            var pants = Armor(PantsMask);

            AssertResult(pants, Tailoring.ReductionTool.Main, EquipMask.AbdomenArmor, CoverageMask.OuterwearAbdomen);
            AssertResult(pants, Tailoring.ReductionTool.Lower, EquipMask.LowerLegArmor, CoverageMask.OuterwearLowerLegs);
            AssertResult(pants, Tailoring.ReductionTool.Middle, EquipMask.UpperLegArmor, CoverageMask.OuterwearUpperLegs);
        }

        [TestMethod]
        public void UpperArmOnly_LowerGivesLowerArm()
        {
            var piece = Armor(EquipMask.UpperArmArmor);

            AssertResult(piece, Tailoring.ReductionTool.Lower, EquipMask.LowerArmArmor, CoverageMask.OuterwearLowerArms);
        }

        [TestMethod]
        public void LowerLegAndFeet_LowerGivesFeet()
        {
            var leggings = Armor(LeggingsMask);

            AssertResult(leggings, Tailoring.ReductionTool.Lower, EquipMask.FootWear, CoverageMask.Feet);
            AssertResult(leggings, Tailoring.ReductionTool.Main, null);
            AssertResult(leggings, Tailoring.ReductionTool.Middle, null);
        }

        [TestMethod]
        public void NoWorkmanship_EveryToolRefused()
        {
            var coat = Armor(CoatMask, workmanship: false);
            var pants = Armor(PantsMask, workmanship: false);

            foreach (var tool in System.Enum.GetValues<Tailoring.ReductionTool>())
            {
                AssertResult(coat, tool, null);
                AssertResult(pants, tool, null);
            }
        }

        [TestMethod]
        public void Retained_EveryToolRefused()
        {
            var pants = Armor(PantsMask, retained: true);

            foreach (var tool in System.Enum.GetValues<Tailoring.ReductionTool>())
                AssertResult(pants, tool, null);
        }

        [TestMethod]
        public void SocietyArmor_EveryToolRefused()
        {
            var pants = Armor(PantsMask, wieldSkillType: (int)PropertyInt.SocietyRankCelhan);

            foreach (var tool in System.Enum.GetValues<Tailoring.ReductionTool>())
                AssertResult(pants, tool, null);
        }

        [TestMethod]
        public void ToolForWcid_MapsOnlyTheThreeRetailTools()
        {
            Assert.AreEqual(Tailoring.ReductionTool.Main, Tailoring.ToolForWcid(Tailoring.ArmorMainReductionTool));
            Assert.AreEqual(Tailoring.ReductionTool.Lower, Tailoring.ToolForWcid(Tailoring.ArmorLowerReductionTool));
            Assert.AreEqual(Tailoring.ReductionTool.Middle, Tailoring.ToolForWcid(Tailoring.ArmorMiddleReductionTool));
            Assert.IsNull(Tailoring.ToolForWcid(Tailoring.ArmorTailoringKit));
            Assert.IsNull(Tailoring.ToolForWcid(Tailoring.ResolveKitWcid(1004316) + 1));
            Assert.AreEqual(Tailoring.ReductionTool.Main, Tailoring.ToolForWcid(Tailoring.ResolveKitWcid(1004316)));
        }

        // ---- projector ----

        private static string Json(SuitItem item) => JsonSerializer.Serialize(item, MarketSnapshot.JsonOptions);

        [TestMethod]
        public void Projector_Coat_CarriesExactlyOneMainReductionToChest()
        {
            var item = SuitItemProjector.FromItem(Armor(CoatMask));

            Assert.IsNotNull(item.Reductions);
            Assert.AreEqual(1, item.Reductions.Count);
            Assert.AreEqual("main", item.Reductions[0].Tool);
            Assert.AreEqual((int)EquipMask.ChestArmor, item.Reductions[0].ValidLocations);
            Assert.AreEqual((int)CoverageMask.OuterwearChest, item.Reductions[0].ClothingPriority);

            Assert.IsTrue(Json(item).Contains("\"reductions\":[{\"tool\":\"main\",\"valid_locations\":" + (int)EquipMask.ChestArmor + ",\"clothing_priority\":" + (int)CoverageMask.OuterwearChest + "}]"), Json(item));
        }

        [TestMethod]
        public void Projector_Pants_ListsMainLowerMiddleInOrder()
        {
            var item = SuitItemProjector.FromItem(Armor(PantsMask));

            CollectionAssert.AreEqual(new[] { "main", "lower", "middle" }, item.Reductions.Select(r => r.Tool).ToArray());
        }

        [TestMethod]
        public void Projector_SingleSlotItem_HasNoReductionsKey()
        {
            var item = SuitItemProjector.FromItem(Armor(EquipMask.ChestArmor));

            // chest-only: main would "reduce" to the same slot, but it is a single-slot item with no real choice
            Assert.IsFalse(Json(item).Contains("reductions"), Json(item));
        }

        [TestMethod]
        public void Projector_ChestOnlyWithMultiBitPriority_StillGetsMainReduction()
        {
            // ValidLocations already equals the result, but in game the tool still rewrites ClothingPriority.
            var piece = Armor(EquipMask.ChestArmor, priority: CoverageMask.OuterwearChest | CoverageMask.OuterwearAbdomen | CoverageMask.OuterwearUpperArms);
            var item = SuitItemProjector.FromItem(piece);

            Assert.IsNotNull(item.Reductions);
            Assert.AreEqual(1, item.Reductions.Count);
            Assert.AreEqual("main", item.Reductions[0].Tool);
            Assert.AreEqual((int)EquipMask.ChestArmor, item.Reductions[0].ValidLocations);
            Assert.AreEqual((int)CoverageMask.OuterwearChest, item.Reductions[0].ClothingPriority);
        }

        [TestMethod]
        public void EquippedItem_CarriesReductions()
        {
            var coat = Armor(CoatMask);
            coat.CurrentWieldedLocation = EquipMask.ChestArmor;

            var worn = CharacterSheetProjector.ProjectEquippedForSuit(new[] { coat }, "Tester").Single();

            Assert.AreEqual(SuitItem.SourceEquipped, worn.Source);
            Assert.IsNotNull(worn.Reductions);
            Assert.AreEqual("main", worn.Reductions.Single().Tool);
            Assert.AreEqual((int)EquipMask.ChestArmor, worn.Reductions.Single().ValidLocations);
        }

        // ---- differential oracle ----

        /// <summary>
        /// A frozen copy of the decision in TailorReduceArmor as it stood on origin/master (bb590efdb), with the
        /// player side effects replaced by a return value. DO NOT edit it to follow Tailoring.cs: its job is to
        /// pin that the extracted ReductionResult decides exactly as the original in-game switch did.
        /// </summary>
        private static (EquipMask, CoverageMask)? OriginalSwitch(bool hasWorkmanship, EquipMask validLocations, Tailoring.ReductionTool tool)
        {
            if (!hasWorkmanship)
                return null;

            var clothingPriority = CoverageMask.Unknown;
            var newLocations = EquipMask.None;

            switch (tool)
            {
                case Tailoring.ReductionTool.Main:

                    if (validLocations.HasFlag(EquipMask.ChestArmor))
                    {
                        newLocations = EquipMask.ChestArmor;
                        clothingPriority = CoverageMask.OuterwearChest;
                    }
                    else if (validLocations.HasFlag(EquipMask.UpperArmArmor))
                    {
                        newLocations = EquipMask.UpperArmArmor;
                        clothingPriority = CoverageMask.OuterwearUpperArms;
                    }
                    else if (validLocations.HasFlag(EquipMask.AbdomenArmor))
                    {
                        newLocations = EquipMask.AbdomenArmor;
                        clothingPriority = CoverageMask.OuterwearAbdomen;
                    }
                    break;

                case Tailoring.ReductionTool.Lower:
                    // Can't reduce Chest Armor to anything but chest!
                    if (validLocations.HasFlag(EquipMask.ChestArmor))
                        break;

                    if (validLocations.HasFlag(EquipMask.UpperArmArmor))
                    {
                        newLocations = EquipMask.LowerArmArmor;
                        clothingPriority = CoverageMask.OuterwearLowerArms;
                    }
                    else if (validLocations.HasFlag(EquipMask.UpperLegArmor))
                    {
                        newLocations = EquipMask.LowerLegArmor;
                        clothingPriority = CoverageMask.OuterwearLowerLegs;
                    }
                    else if (validLocations.HasFlag(EquipMask.LowerLegArmor | EquipMask.FootWear))
                    {
                        newLocations = EquipMask.FootWear;
                        clothingPriority = CoverageMask.Feet;
                    }
                    break;

                case Tailoring.ReductionTool.Middle:
                    if (validLocations.HasFlag(EquipMask.UpperLegArmor))
                    {
                        newLocations = EquipMask.UpperLegArmor;
                        clothingPriority = CoverageMask.OuterwearUpperLegs;
                    }
                    break;
            }

            if (clothingPriority == CoverageMask.Unknown)
                return null;

            return (newLocations, clothingPriority);
        }

        [TestMethod]
        public void ReductionResult_MatchesTheOriginalSwitchOnEveryMaskToolAndWorkmanship()
        {
            // Every bit the original switch tests, plus one bit it ignores (Cloak); subsets include None.
            var bits = new[]
            {
                EquipMask.ChestArmor, EquipMask.UpperArmArmor, EquipMask.AbdomenArmor,
                EquipMask.UpperLegArmor, EquipMask.LowerLegArmor, EquipMask.FootWear,
                EquipMask.Cloak,
            };

            var cases = 0;

            for (var subset = 0; subset < (1 << bits.Length); subset++)
            {
                var mask = EquipMask.None;
                for (var i = 0; i < bits.Length; i++)
                    if ((subset & (1 << i)) != 0)
                        mask |= bits[i];

                foreach (var workmanship in new[] { true, false })
                {
                    var item = Armor(mask, workmanship);

                    foreach (var tool in System.Enum.GetValues<Tailoring.ReductionTool>())
                    {
                        var expected = OriginalSwitch(workmanship, mask, tool);
                        var actual = Tailoring.ReductionResult(item, tool);

                        Assert.AreEqual(expected, actual, $"mask={mask} ({(int)mask}) workmanship={workmanship} tool={tool}");
                        cases++;
                    }
                }
            }

            Assert.AreEqual(128 * 2 * 3, cases);
        }
        [TestMethod]
        public void Projector_IneligibleCoat_HasNoReductionsKey()
        {
            Assert.IsFalse(Json(SuitItemProjector.FromItem(Armor(CoatMask, workmanship: false))).Contains("reductions"));
            Assert.IsFalse(Json(SuitItemProjector.FromItem(Armor(CoatMask, retained: true))).Contains("reductions"));
        }
    }
}