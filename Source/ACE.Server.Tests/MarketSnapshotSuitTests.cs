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
using ACE.Server.Managers.CharacterSheets;
using ACE.Server.Managers.Market;
using ACE.Server.Managers.Market.Suit;
using ACE.Server.WorldObjects;

namespace ACE.Server.Tests
{
    /// <summary>
    /// The suit projection embedded in a listing snapshot (projection version 5): present for a suit
    /// piece on both the item and the weenie path, absent for a non-suit item, guid-free for a ledger
    /// or class line, and on the wire as <c>suit</c> in snake_case with nulls omitted.
    /// </summary>
    [TestClass]
    public class MarketSnapshotSuitTests
    {
        private static int nextGuid = 0x7F600000;

        private const int SetId = (int)EquipmentSet.Defenders;
        private const EquipMask Locations = EquipMask.ChestArmor | EquipMask.AbdomenArmor;

        private IClothingIconSource savedClothingIcons;

        [TestInitialize]
        public void Setup()
        {
            savedClothingIcons = MarketSnapshot.ClothingIcons;
            MarketSnapshot.ClothingIcons = null;
        }

        [TestCleanup]
        public void Teardown() => MarketSnapshot.ClothingIcons = savedClothingIcons;

        private static Weenie Breastplate(int? workmanship = null, uint? allowedWielder = null)
        {
            var w = new Weenie
            {
                WeenieClassId = 1001910,
                WeenieType = WeenieType.Clothing,
                PropertiesString = new Dictionary<PropertyString, string> { { PropertyString.Name, "Suit Breastplate" } },
                PropertiesInt = new Dictionary<PropertyInt, int>
                {
                    { PropertyInt.ItemType, (int)ItemType.Armor },
                    { PropertyInt.ValidLocations, (int)Locations },
                    { PropertyInt.EquipmentSetId, SetId },
                    { PropertyInt.ArmorLevel, 400 },
                    { PropertyInt.EncumbranceVal, 100 },
                    { PropertyInt.Value, 500 },
                },
                PropertiesSpellBook = new Dictionary<int, float> { { 4325, 2f }, { 2137, 2f } },
            };

            if (workmanship.HasValue)
                w.PropertiesInt[PropertyInt.ItemWorkmanship] = workmanship.Value;

            if (allowedWielder.HasValue)
                w.PropertiesIID = new Dictionary<PropertyInstanceId, uint> { { PropertyInstanceId.AllowedWielder, allowedWielder.Value } };

            return w;
        }

        private static Weenie SalvageBag()
        {
            return new Weenie
            {
                WeenieClassId = 20983,
                WeenieType = WeenieType.Generic,
                PropertiesString = new Dictionary<PropertyString, string> { { PropertyString.Name, "Salvage (100)" } },
                PropertiesInt = new Dictionary<PropertyInt, int>
                {
                    { PropertyInt.ItemType, (int)ItemType.TinkeringMaterial },
                    { PropertyInt.MaterialType, (int)MaterialType.Steel },
                    { PropertyInt.EncumbranceVal, 500 },
                    { PropertyInt.Value, 1000 },
                },
            };
        }

        private static WorldObject AsItem(Weenie weenie)
            => new Clothing(weenie, new ObjectGuid((uint)nextGuid++));

        private static void AssertBreastplateSuit(SuitItem suit, string path)
        {
            Assert.IsNotNull(suit, $"{path}: a suit piece's snapshot carries no Suit");
            Assert.AreEqual((int)Locations, suit.ValidLocations, $"{path}: valid locations");
            Assert.AreEqual(SetId, suit.EquipmentSetId, $"{path}: set id");
            CollectionAssert.AreEqual(new uint[] { 2137, 4325 }, suit.WornSpellIds, $"{path}: worn spell ids");
            Assert.AreEqual(400, suit.ArmorLevel, $"{path}: armor level");
            Assert.AreEqual(1001910u, suit.Wcid, $"{path}: wcid");
        }

        [TestMethod]
        public void SuitPiece_CarriesTheSuitProjection_OnBothTheItemAndTheWeeniePath()
        {
            var weenie = Breastplate();

            var fromItem = MarketSnapshot.FromItem(AsItem(weenie), includeSuit: true);
            var fromWeenie = MarketSnapshot.FromWeenie(weenie, includeSuit: true);

            AssertBreastplateSuit(fromItem.Suit, "FromItem");
            AssertBreastplateSuit(fromWeenie.Suit, "FromWeenie");

            Assert.IsNotNull(fromItem.Suit.ItemGuid, "a stored item's suit names the item");
            Assert.IsNull(fromWeenie.Suit.ItemGuid, "a weenie has no item guid");
        }

        [TestMethod]
        public void NonSuitItem_HasNoSuit()
        {
            var weenie = SalvageBag();

            Assert.IsNull(MarketSnapshot.FromItem(new GenericObject(weenie, new ObjectGuid((uint)nextGuid++)), includeSuit: true).Suit);
            Assert.IsNull(MarketSnapshot.FromWeenie(weenie, includeSuit: true).Suit);
        }

        [TestMethod]
        public void SnapshotVersion_IsAtLeastFive_BecauseSuitWasAdded()
        {
            Assert.IsTrue(MarketSnapshot.CurrentVersion >= 5);
            Assert.AreEqual(MarketSnapshot.CurrentVersion, MarketSnapshot.FromWeenie(Breastplate(), includeSuit: true).SnapshotVersion);
        }

        [TestMethod]
        public void LedgerProbe_ClearsTheProbesItemGuid_AndKeepsTheLedgerWcid()
        {
            var probe = AsItem(Breastplate());

            // Control: the same probe through FromItem DOES carry a guid, so the null below is the clear.
            Assert.IsNotNull(MarketSnapshot.FromItem(probe, includeSuit: true).Suit.ItemGuid);

            var snapshot = MarketSnapshot.FromLedgerProbe(probe, 777u);

            Assert.IsNotNull(snapshot.Suit);
            Assert.IsNull(snapshot.Suit.ItemGuid);
            Assert.AreEqual(777u, snapshot.Suit.Wcid);
            Assert.AreEqual((int)Locations, snapshot.Suit.ValidLocations);
        }

        [TestMethod]
        public void ClassLine_SuitHasNoItemGuid_AndNamesItsClassKey()
        {
            // DescribeClass materializes a real display object (it needs a world), then hands the
            // projected suit to this helper; the helper is the rule under test.
            var projected = MarketSnapshot.FromItem(AsItem(Breastplate()), includeSuit: true).Suit;
            Assert.IsNotNull(projected.ItemGuid);

            var line = MarketSnapshot.ClearedOfItemGuid(projected, 1001910u, "k:abc");

            Assert.IsNull(line.ItemGuid);
            Assert.AreEqual("k:abc", line.ClassKey);
            Assert.AreEqual(1, line.Count, "the pool count is never stamped");
            Assert.AreEqual((int)Locations, line.ValidLocations);
            Assert.IsNull(MarketSnapshot.ClearedOfItemGuid(null, 1u, "k"), "a non-suit class line stays without a suit");
        }

        [TestMethod]
        public void Serialize_WritesSuitInSnakeCase_AndOmitsItWhenNull()
        {
            var json = MarketSnapshot.Serialize(MarketSnapshot.FromItem(AsItem(Breastplate()), includeSuit: true));

            Assert.IsTrue(json.Contains("\"suit\":{"), json);

            using (var doc = JsonDocument.Parse(json))
            {
                var suit = doc.RootElement.GetProperty("suit");

                Assert.IsTrue(suit.TryGetProperty("valid_locations", out _), json);
                Assert.IsTrue(suit.TryGetProperty("equipment_set_id", out _), json);
                Assert.IsTrue(suit.TryGetProperty("worn_spell_ids", out _), json);
                Assert.IsTrue(suit.TryGetProperty("item_guid", out _), json);
            }

            var salvage = MarketSnapshot.Serialize(MarketSnapshot.FromWeenie(SalvageBag(), includeSuit: true));
            Assert.IsFalse(salvage.Contains("\"suit\""), salvage);

            var ledger = MarketSnapshot.Serialize(MarketSnapshot.FromWeenie(Breastplate(), includeSuit: true));
            using (var doc = JsonDocument.Parse(ledger))
                Assert.IsFalse(doc.RootElement.GetProperty("suit").TryGetProperty("item_guid", out _), "a null item guid must be omitted");
        }

        [TestMethod]
        public void Deserialize_RoundTripsTheSuit()
        {
            var before = MarketSnapshot.FromWeenie(Breastplate(), includeSuit: true);

            var after = MarketSnapshot.Deserialize(MarketSnapshot.Serialize(before));

            AssertBreastplateSuit(after.Suit, "round trip");
        }

        // ---- review round: F2 default off, F3 throw, F4 wielder, F5 reductions, F6 json ----

        [TestMethod]
        public void TheSuitIsOptIn_ADefaultSnapshotNeverCarriesIt()
        {
            var weenie = Breastplate();

            Assert.IsNull(MarketSnapshot.FromItem(AsItem(weenie)).Suit, "FromItem must not attach a suit unless asked");
            Assert.IsNull(MarketSnapshot.FromWeenie(weenie).Suit, "FromWeenie must not attach a suit unless asked");
        }

        [TestMethod]
        public void CharacterSheetEquippedSnapshots_CarryNoSuit()
        {
            var entry = CharacterSheetProjector.ProjectEquippedItem(AsItem(Breastplate()));

            Assert.IsNull(entry.Snapshot.Suit);
            Assert.IsFalse(MarketSnapshot.Serialize(entry.Snapshot).Contains("\"suit\""));
        }

        [TestMethod]
        public void AProjectionThatThrows_LeavesSuitNull_AndTheSnapshotIntact()
        {
            var savedItem = MarketSnapshot.ItemSuitProjection;
            var savedWeenie = MarketSnapshot.WeenieSuitProjection;

            try
            {
                MarketSnapshot.ItemSuitProjection = _ => throw new InvalidOperationException("suit projection broke");
                MarketSnapshot.WeenieSuitProjection = _ => throw new InvalidOperationException("suit projection broke");

                var weenie = Breastplate();
                var fromItem = MarketSnapshot.FromItem(AsItem(weenie), includeSuit: true);
                var fromWeenie = MarketSnapshot.FromWeenie(weenie, includeSuit: true);

                Assert.IsNull(fromItem.Suit);
                Assert.IsNull(fromWeenie.Suit);
                Assert.AreEqual("Suit Breastplate", fromItem.Name, "the rest of the snapshot survives");
                Assert.AreEqual(MarketSnapshot.CurrentVersion, fromItem.SnapshotVersion);
                Assert.AreEqual("Suit Breastplate", fromWeenie.Name);
            }
            finally
            {
                MarketSnapshot.ItemSuitProjection = savedItem;
                MarketSnapshot.WeenieSuitProjection = savedWeenie;
            }
        }

        [TestMethod]
        public void AnItemBoundToOneWielder_HasNoSuit_AndNoWielderGuidInItsJson()
        {
            var weenie = Breastplate(allowedWielder: 0x50001234u);

            foreach (var snapshot in new[]
            {
                MarketSnapshot.FromItem(AsItem(weenie), includeSuit: true),
                MarketSnapshot.FromWeenie(weenie, includeSuit: true),
            })
            {
                Assert.IsNull(snapshot.Suit);

                var json = MarketSnapshot.Serialize(snapshot);
                Assert.IsFalse(json.Contains("\"suit\""), json);
                Assert.IsFalse(json.Contains("allowed_wielder"), json);
            }

            // Control: the same piece without the wielder DOES carry a suit, so the null above is the rule.
            Assert.IsNotNull(MarketSnapshot.FromItem(AsItem(Breastplate()), includeSuit: true).Suit);
        }

        [TestMethod]
        public void AttunedAndBondedFlags_SurviveIntoTheListingSuit()
        {
            var weenie = Breastplate();
            weenie.PropertiesInt[PropertyInt.Attuned] = 1;
            weenie.PropertiesInt[PropertyInt.Bonded] = 1;

            var suit = MarketSnapshot.FromItem(AsItem(weenie), includeSuit: true).Suit;

            Assert.AreEqual(true, suit.Attuned);
            Assert.AreEqual(true, suit.Bonded);
        }

        [TestMethod]
        public void AWorkmanshipBearingMultiSlotPiece_ReportsReductionsOnTheItem_ButNotTheWeenie()
        {
            // Tailoring.ReductionResult needs an ItemWorkmanship, and a chest+abdomen piece's main tool
            // narrows it to chest, so the item side is non-null; the weenie projection has no live item
            // to judge and is null by contract (SuitItem.Reductions).
            var weenie = Breastplate(workmanship: 8);

            var fromItem = MarketSnapshot.FromItem(AsItem(weenie), includeSuit: true).Suit;
            var fromWeenie = MarketSnapshot.FromWeenie(weenie, includeSuit: true).Suit;

            Assert.IsNotNull(fromItem.Reductions);
            Assert.IsTrue(fromItem.Reductions.Any(r => r.Tool == "main"));
            Assert.IsNull(fromWeenie.Reductions);

            // Control: without workmanship nothing is reducible.
            Assert.IsNull(MarketSnapshot.FromItem(AsItem(Breastplate()), includeSuit: true).Suit.Reductions);
        }

        [TestMethod]
        public void ListingSnapshotJson_NeverCarriesTheSuitInventoryOnlyFields()
        {
            var json = MarketSnapshot.Serialize(MarketSnapshot.FromItem(AsItem(Breastplate()), includeSuit: true));

            Assert.IsTrue(json.Contains("\"suit\":{"), json);

            foreach (var key in new[] { "vault_key", "vault_label", "holder_guid", "current_wielded_location" })
                Assert.IsFalse(json.Contains("\"" + key + "\""), $"{key} is a suit-inventory field and must not appear in a listing snapshot:\n{json}");
        }
    }
}
