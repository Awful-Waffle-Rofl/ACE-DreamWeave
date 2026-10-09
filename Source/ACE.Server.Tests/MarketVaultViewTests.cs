using System.Collections.Generic;
using System.Text.Json;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.DatLoader;
using ACE.Entity;
using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Entity.Models;
using ACE.Server.Managers.Market;
using ACE.Server.WorldObjects;

namespace ACE.Server.Tests
{
    /// <summary>
    /// TryGetVaultView's display fields. A ledger row has no WorldObject, so its name and icons come
    /// from the weenie; both row kinds must carry RESOLVED icon ids, never raw copies.
    /// </summary>
    [TestClass]
    public class MarketVaultViewTests
    {
        private const uint VaultAccount = 7101;

        private const uint LedgerWcid = 20631;
        private const uint StoredWcid = 31769;

        private const string LedgerName = "Lead Scarab";
        private const string StoredName = "Studded Leather Coat";

        /// <summary>What the item's own Icon property says. Never what the player sees, for these fixtures.</summary>
        private const uint RawIcon = 0x06001111u;

        /// <summary>What the ClothingBase table actually supplies, so a raw copy is visibly wrong.</summary>
        private const uint ResolvedIcon = 0x0600AAAAu;

        private const uint OverlayIcon = 0x06002222u;
        private const uint UnderlayIcon = 0x06003333u;

        private const uint TestClothingBase = 0x10000123u;
        private const uint TestSetup = 0x02000001u;
        private const int TestTemplate = 8;

        private static int nextGuid = 0x7F300000;

        /// <summary>One-table clothing oracle, the same shape IconKeyTests injects.</summary>
        private sealed class FakeClothingSource : IClothingIconSource
        {
            public ClothingIconTable TryGetTable(uint clothingBase)
            {
                if (clothingBase != TestClothingBase)
                    return null;

                return new ClothingIconTable(
                    new List<uint> { TestSetup },
                    new List<uint> { (uint)TestTemplate },
                    new Dictionary<uint, uint> { { (uint)TestTemplate, ResolvedIcon } });
            }
        }

        private FakeMarketRepository repo;
        private FakeMarketItemStore store;
        private FakeMarketWallet wallet;

        private IClothingIconSource savedClothingIcons;
        private System.Func<uint, Weenie> savedWeenieLookup;

        /// <summary>Times the seam was consulted. It runs per row by design; the INSTANCE is what the memo keys on.</summary>
        private int weenieLookups;

        /// <summary>
        /// The instance the seam hands back, mirroring WorldDatabaseWithEntityCache: a cache hit is the
        /// same object every time, and only ClearCachedWeenie makes the next read a different one.
        /// </summary>
        private Weenie ledgerWeenie;

        [TestInitialize]
        public void Setup()
        {
            repo = new FakeMarketRepository();
            store = new FakeMarketItemStore();
            wallet = new FakeMarketWallet();

            MarketManagerTests.SeedMarketTunables();
            MarketManager.Initialize(store, wallet, repo);

            savedClothingIcons = MarketSnapshot.ClothingIcons;
            savedWeenieLookup = MarketManager.WeenieLookup;

            MarketSnapshot.ClothingIcons = new FakeClothingSource();

            weenieLookups = 0;
            ledgerWeenie = IconFixtureWeenie(LedgerWcid, LedgerName);

            // The production seam reads the world database; these tests must not.
            MarketManager.WeenieLookup = wcid =>
            {
                weenieLookups++;
                return wcid == LedgerWcid ? ledgerWeenie : null;
            };
        }

        [TestCleanup]
        public void Teardown()
        {
            MarketManager.WeenieLookup = savedWeenieLookup;
            MarketSnapshot.ClothingIcons = savedClothingIcons;
            MarketManager.Shutdown();
        }

        /// <summary>A weenie whose drawn icon is NOT its Icon property: ClothingBase supplies ResolvedIcon.</summary>
        private static Weenie IconFixtureWeenie(uint wcid, string name)
        {
            return new Weenie
            {
                WeenieClassId = wcid,
                WeenieType = WeenieType.Stackable,
                PropertiesString = new Dictionary<PropertyString, string> { { PropertyString.Name, name } },
                PropertiesInt = new Dictionary<PropertyInt, int>
                {
                    { PropertyInt.ItemType, (int)ItemType.Misc },
                    { PropertyInt.MaterialType, (int)MaterialType.Silver },
                    { PropertyInt.ItemWorkmanship, 7 },
                    { PropertyInt.PaletteTemplate, TestTemplate },
                    { PropertyInt.StackSize, 1 },
                    { PropertyInt.MaxStackSize, 1 },
                },
                PropertiesDID = new Dictionary<PropertyDataId, uint>
                {
                    { PropertyDataId.Icon, RawIcon },
                    { PropertyDataId.IconOverlay, OverlayIcon },
                    { PropertyDataId.IconUnderlay, UnderlayIcon },
                    { PropertyDataId.ClothingBase, TestClothingBase },
                    { PropertyDataId.Setup, TestSetup },
                },
            };
        }

        /// <summary>Serializes one view row through the API's own options, so field names are the wire names.</summary>
        private static JsonElement Row(object entry)
            => JsonSerializer.Deserialize<JsonElement>(JsonSerializer.Serialize(entry, MarketApiHost.Json));

        private JsonElement SingleRow()
        {
            Assert.IsTrue(MarketManager.TryGetVaultView(VaultAccount, out var entries, out var error),
                $"vault view refused: {error}");
            Assert.AreEqual(1, entries.Count, "expected exactly one seeded vault row");

            return Row(entries[0]);
        }

        [TestMethod]
        public void VaultView_LedgerEntryName_ComesFromTheWeenieNotTheWcidFallback()
        {
            store.SeedLedger(VaultAccount, LedgerWcid, 8993);

            var row = SingleRow();

            Assert.IsTrue(row.GetProperty("is_ledger").GetBoolean(), "the seeded row must be a ledger row");
            Assert.AreEqual(LedgerName, row.GetProperty("name").GetString());
            Assert.AreNotEqual($"Item {LedgerWcid}", row.GetProperty("name").GetString(),
                "a ledger row has no WorldObject, so the name must come from the weenie");
        }

        [TestMethod]
        public void VaultView_LedgerEntryIcons_AreResolvedNotCopiedFromTheWeenie()
        {
            store.SeedLedger(VaultAccount, LedgerWcid, 8993);

            var row = SingleRow();

            Assert.AreEqual(ResolvedIcon, row.GetProperty("icon_id").GetUInt32(),
                "icon_id must be the ClothingBase-resolved id, not the weenie's own Icon property");
            Assert.AreNotEqual(RawIcon, row.GetProperty("icon_id").GetUInt32(), "raw Icon was copied through");
            Assert.AreEqual(TestTemplate, row.GetProperty("palette_template").GetInt32(),
                "palette_template must be the template that actually supplied icon_id");
            Assert.AreEqual(OverlayIcon, row.GetProperty("icon_overlay_id").GetUInt32());
            Assert.AreEqual(UnderlayIcon, row.GetProperty("icon_underlay_id").GetUInt32());
        }

        [TestMethod]
        public void VaultView_StoredEntryIcons_AreResolvedNotCopiedFromTheItem()
        {
            var item = new Stackable(IconFixtureWeenie(StoredWcid, StoredName), new ObjectGuid((uint)nextGuid++));
            store.SeedItem(VaultAccount, item);

            var row = SingleRow();

            Assert.IsFalse(row.GetProperty("is_ledger").GetBoolean(), "the seeded row must be a stored item");
            Assert.AreEqual(StoredName, row.GetProperty("name").GetString());
            Assert.AreEqual(ResolvedIcon, row.GetProperty("icon_id").GetUInt32(),
                "icon_id must be the ClothingBase-resolved id, not the item's own IconId");
            Assert.AreNotEqual(RawIcon, row.GetProperty("icon_id").GetUInt32(), "raw IconId was copied through");
            Assert.AreEqual(TestTemplate, row.GetProperty("palette_template").GetInt32());
            Assert.AreEqual(OverlayIcon, row.GetProperty("icon_overlay_id").GetUInt32());
            Assert.AreEqual(UnderlayIcon, row.GetProperty("icon_underlay_id").GetUInt32());
        }

        /// <summary>
        /// is_group across ALL THREE row kinds, because the field only means anything as a
        /// discrimination: it tells the web that this row's `count` is a MEMBER count and that a
        /// listing over it may name more than 1. A version that returned true everywhere, or that
        /// confused a group with a ledger row, would pass a single-kind test.
        /// </summary>
        [TestMethod]
        public void VaultView_IsGroup_IsTrueForAGroupRowAndFalseForTheOtherTwo()
        {
            var lone = new Stackable(IconFixtureWeenie(StoredWcid, StoredName), new ObjectGuid((uint)nextGuid++));
            store.SeedItem(VaultAccount, lone);

            var group = store.SeedGroup(VaultAccount, StoredWcid, 3);

            store.SeedLedger(VaultAccount, LedgerWcid, 8993);

            Assert.IsTrue(MarketManager.TryGetVaultView(VaultAccount, out var entries, out var error), $"vault view refused: {error}");
            Assert.AreEqual(3, entries.Count, "expected a lone stored item, a group row and a ledger row");

            var loneRow = Row(entries[0]);
            var groupRow = Row(entries[1]);
            var ledgerRow = Row(entries[2]);

            Assert.IsFalse(loneRow.GetProperty("is_ledger").GetBoolean());
            Assert.IsFalse(loneRow.GetProperty("is_group").GetBoolean(), "one stored biota is never a group of one");
            Assert.AreEqual(lone.Guid.Full, loneRow.GetProperty("item_guid").GetUInt32());

            Assert.IsFalse(groupRow.GetProperty("is_ledger").GetBoolean(), "a group is stored biotas, not a collapsed stack");
            Assert.IsTrue(groupRow.GetProperty("is_group").GetBoolean());
            Assert.AreEqual(3, groupRow.GetProperty("count").GetInt32(), "a group's count is its MEMBER count");
            Assert.AreEqual(group[0].Guid.Full, groupRow.GetProperty("item_guid").GetUInt32(),
                "item_guid stays the representative member, unchanged by this field");

            Assert.IsTrue(ledgerRow.GetProperty("is_ledger").GetBoolean());
            Assert.IsFalse(ledgerRow.GetProperty("is_group").GetBoolean(), "a ledger row has no members at all");
        }

        [TestMethod]
        public void VaultView_Row_CarriesTheSearchableSnapshot()
        {
            store.SeedLedger(VaultAccount, LedgerWcid, 8993);

            var snapshot = SingleRow().GetProperty("snapshot");

            Assert.AreEqual(LedgerName, snapshot.GetProperty("name").GetString());
            Assert.AreEqual((int)ItemType.Misc, snapshot.GetProperty("item_type").GetInt32());
            Assert.AreEqual(ItemType.Misc.ToString(), snapshot.GetProperty("item_type_name").GetString());
            Assert.AreEqual((int)MaterialType.Silver, snapshot.GetProperty("material_type").GetInt32());
            Assert.AreEqual(MaterialType.Silver.ToString(), snapshot.GetProperty("material_name").GetString());
            Assert.AreEqual(7, snapshot.GetProperty("workmanship").GetInt32());
        }

        /// <summary>The name on the single seeded row of one account, through the API's own serializer.</summary>
        private string NameOf(uint accountId)
        {
            Assert.IsTrue(MarketManager.TryGetVaultView(accountId, out var entries, out var error),
                $"vault view refused for account {accountId}: {error}");
            Assert.AreEqual(1, entries.Count);

            return Row(entries[0]).GetProperty("name").GetString();
        }

        [TestMethod]
        public void VaultView_LedgerSnapshot_IsNotRebuiltWhileTheWeenieInstanceIsUnchanged()
        {
            // A vault is dominated by ledger stacks and every row of a wcid projects identically, so
            // an unchanged weenie must be projected once, not once per row. Mutating the instance in
            // place is the probe: only a REBUILD could pick the new value up.
            const int rows = 5;

            for (uint account = VaultAccount; account < VaultAccount + rows; account++)
                store.SeedLedger(account, LedgerWcid, 8993);

            Assert.AreEqual(LedgerName, NameOf(VaultAccount));

            ledgerWeenie.PropertiesString[PropertyString.Name] = "Rewritten In Place";

            for (uint account = VaultAccount + 1; account < VaultAccount + rows; account++)
                Assert.AreEqual(LedgerName, NameOf(account),
                    "the snapshot was rebuilt even though the world cache handed back the same instance");
        }

        [TestMethod]
        public void VaultView_LedgerSnapshot_RebuildsWhenTheWorldCacheHandsBackANewInstance()
        {
            // ClearCachedWeenie, which the live /import content path calls, makes the next GetCachedWeenie
            // construct a NEW Weenie. Identity alone must therefore invalidate: without this the market
            // serves the old name, icon, item type and material until the process restarts.
            store.SeedLedger(VaultAccount, LedgerWcid, 8993);

            Assert.AreEqual(LedgerName, NameOf(VaultAccount));

            var before = weenieLookups;

            ledgerWeenie = IconFixtureWeenie(LedgerWcid, "Reimported Scarab");

            Assert.AreEqual("Reimported Scarab", NameOf(VaultAccount),
                "a reimported weenie must be reprojected, not served from the memo");
            Assert.IsTrue(weenieLookups > before,
                "the world cache must be consulted on every view; it is the instance that decides a rebuild");
        }

        [TestMethod]
        public void VaultView_UnreadableWeenie_FallsBackWithoutFailingTheRow()
        {
            // 9999 is outside the lookup fake, standing in for a weenie the world database cannot read.
            store.SeedLedger(VaultAccount, 9999, 5);

            var row = SingleRow();

            Assert.AreEqual("Item 9999", row.GetProperty("name").GetString());
            Assert.AreEqual(0u, row.GetProperty("icon_id").GetUInt32());
        }
    }
}
