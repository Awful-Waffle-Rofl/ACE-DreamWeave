using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Entity;
using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Entity.Models;
using ACE.Server.Entity;
using ACE.Server.Factories;
using ACE.Server.Managers;
using ACE.Server.ThreadDungeons;
using ACE.Server.WorldObjects;

namespace ACE.Server.Tests
{
    /// <summary>
    /// The WorldObjectFactory opt-in for the Thread Encounters Wandering Peddler (PropertyBool.ThreadPeddlerVendor, 9096):
    /// a weenie WITHOUT the flag builds exactly the type it built before; WITH it, a type-10 (Creature) weenie builds a
    /// ThreadPeddlerVendor, keyed on the bool and not on WeenieType, in both factory arms. An UNBOUND peddler (never placed by
    /// PeddlerEncounterType) is inert: ethereal, unattackable, no create-list stock, never rotates, refuses every trade.
    ///
    /// Constructing any Vendor needs GameTables, vendor_shop_uses_generator cached and the coin weenie in the world cache
    /// (SalvageRollerVendorTests.SeedVendorConstructible). The one PropertyManager key that seeds is restored to its shipped
    /// default in TestCleanup, so the class passes alone.
    /// </summary>
    [TestClass]
    public class ThreadPeddlerVendorFactoryTests
    {
        private const string ShopGeneratorKey = "vendor_shop_uses_generator";

        private static uint nextGuid = 0x7EB80000;

        private static uint NextGuid() => ++nextGuid;

        [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "<lastPlayerInfo>k__BackingField")]
        private static extern ref WorldObjectInfo LastPlayerInfoField(Vendor vendor);

        [ClassInitialize]
        public static void PrimePlayerType(TestContext _)
        {
            // The inert-commerce tests use a bare Player (VendorAlternateCurrencyBalanceTests.BareViewer).
            ThornsCloseRangeTests.EnsurePlayerTypeInitializable();
        }

        [TestInitialize]
        public void Setup()
        {
            SalvageRollerVendorTests.SeedVendorConstructible();
        }

        [TestCleanup]
        public void Cleanup()
        {
            PropertyManager.ModifyBool(ShopGeneratorKey, DefaultPropertyManager.DefaultBooleanProperties[ShopGeneratorKey].Item);
        }

        /// <summary>The peddler weenie's shape: type 10 (Creature) with the vendor property set and, opted in, bool 9096.</summary>
        private static Weenie MakePeddlerWeenie(bool optIn, WeenieType type = WeenieType.Creature)
        {
            var properties = new Dictionary<PropertyBool, bool>();

            if (optIn)
                properties[PropertyBool.ThreadPeddlerVendor] = true;

            return new Weenie
            {
                WeenieClassId = 1007981,
                WeenieType = type,
                PropertiesBool = properties,
                PropertiesInt = new Dictionary<PropertyInt, int>
                {
                    { PropertyInt.MerchandiseItemTypes, 1073774863 },
                    { PropertyInt.MerchandiseMinValue, 0 },
                    { PropertyInt.MerchandiseMaxValue, int.MaxValue },
                },
                PropertiesFloat = new Dictionary<PropertyFloat, double>
                {
                    { PropertyFloat.BuyPrice, 0 },
                    { PropertyFloat.SellPrice, 0 },
                },
                PropertiesString = new Dictionary<PropertyString, string>
                {
                    { PropertyString.Name, "Wandering Peddler" },
                },
            };
        }

        [TestMethod]
        public void PropertyId_Is9096()
        {
            Assert.AreEqual(9096, (int)PropertyBool.ThreadPeddlerVendor);
        }

        [TestMethod]
        public void Factory_CreatureWeenieWithoutTheFlag_BuildsAPlainCreature()
        {
            var wo = WorldObjectFactory.CreateWorldObject(MakePeddlerWeenie(optIn: false), new ObjectGuid(NextGuid()));

            Assert.AreEqual(typeof(Creature), wo.GetType(), "without bool 9096 a type-10 weenie builds exactly what it built before");
        }

        [TestMethod]
        public void Factory_VendorWeenieWithoutTheFlag_BuildsAPlainVendor()
        {
            var wo = WorldObjectFactory.CreateWorldObject(MakePeddlerWeenie(optIn: false, type: WeenieType.Vendor), new ObjectGuid(NextGuid()));

            Assert.AreEqual(typeof(Vendor), wo.GetType(), "without bool 9096 a type-12 weenie builds exactly a retail Vendor");
        }

        [TestMethod]
        public void Factory_CreatureWeenieWithTheFlag_BuildsAThreadPeddlerVendor()
        {
            var weenie = MakePeddlerWeenie(optIn: true);
            Assert.AreEqual(WeenieType.Creature, weenie.WeenieType, "sanity: the peddler ships as type 10");

            var wo = WorldObjectFactory.CreateWorldObject(weenie, new ObjectGuid(NextGuid()));

            Assert.AreEqual(typeof(ThreadPeddlerVendor), wo.GetType(), "bool 9096 decides the class, not WeenieType");
        }

        [TestMethod]
        public void Factory_FalseFlag_IsNotAnOptIn()
        {
            var weenie = MakePeddlerWeenie(optIn: false);
            weenie.PropertiesBool[PropertyBool.ThreadPeddlerVendor] = false;

            var wo = WorldObjectFactory.CreateWorldObject(weenie, new ObjectGuid(NextGuid()));

            Assert.AreEqual(typeof(Creature), wo.GetType(), "bool 9096 = false must take the unchanged Creature arm");
        }

        [TestMethod]
        public void Stub_IsInert()
        {
            var peddler = (ThreadPeddlerVendor)WorldObjectFactory.CreateWorldObject(MakePeddlerWeenie(optIn: true), new ObjectGuid(NextGuid()));

            Assert.IsTrue(peddler.Ethereal ?? false, "set in code, never a body-blocker");
            Assert.IsFalse(peddler.Attackable, "set in code, never an aggro target");
            Assert.AreEqual(0, peddler.ItemsForSaleCount, "no create-list stock: listings come only from Bind");
            Assert.AreEqual(0f, peddler.Rotate(peddler), "never turns toward one customer");
        }

        private ThreadPeddlerVendor BuildStub()
        {
            var peddler = (ThreadPeddlerVendor)WorldObjectFactory.CreateWorldObject(MakePeddlerWeenie(optIn: true), new ObjectGuid(NextGuid()));
            Assert.IsTrue(peddler.OpenForBusiness, "sanity: the test weenie carries every vendor row, so the refusals below are the stub's, not a closed shop's");
            return peddler;
        }

        private static WorldObject SellableItem()
        {
            var weenie = new Weenie
            {
                WeenieClassId = 1007982,
                WeenieType = WeenieType.Generic,
                PropertiesInt = new Dictionary<PropertyInt, int>
                {
                    { PropertyInt.Value, 500 },
                    { PropertyInt.ItemType, (int)ItemType.Misc },
                },
            };

            return WorldObjectFactory.CreateWorldObject(weenie, new ObjectGuid(NextGuid()));
        }

        [TestMethod]
        public void Stub_RefusesEverySale_InBothCanAcceptOverloads_AndTheBasketHook()
        {
            var peddler = BuildStub();
            var viewer = VendorAlternateCurrencyBalanceTests.BareViewer();
            var item = SellableItem();

            Assert.IsFalse(peddler.CanAccept(item, viewer, out var reason), "the single-item CanAccept must refuse");
            Assert.AreEqual(ThreadPeddlerVendor.BuysNothingMessage, reason);

            Assert.IsFalse(peddler.CanAccept(item, viewer, ACE.Server.Entity.AccountVault.VaultAccess.DepositWithdraw, out reason), "the resolved-access CanAccept (the one HandleActionSellItemCore calls) must refuse");
            Assert.AreEqual(ThreadPeddlerVendor.BuysNothingMessage, reason);

            Assert.IsFalse(peddler.TryValidateSellBasket(viewer, new List<WorldObject> { item }, new List<WorldObject> { item }, out reason), "the basket hook must refuse");
            Assert.AreEqual(ThreadPeddlerVendor.BuysNothingMessage, reason);
        }

        [TestMethod]
        public void Stub_PaysNothing_AndKeepsNothing()
        {
            var peddler = BuildStub();
            var item = SellableItem();
            var items = new Dictionary<uint, WorldObject> { { item.Guid.Full, item } };

            Assert.IsTrue(new Vendor(MakePeddlerWeenie(optIn: false, type: WeenieType.Vendor), new ObjectGuid(NextGuid())).CalculatePayoutCoinAmount(items) >= 1,
                "control: a retail vendor pays at least 1 pyreal for the same item");
            Assert.AreEqual(0, peddler.CalculatePayoutCoinAmount(items));

            peddler.ProcessItemsForPurchase(VendorAlternateCurrencyBalanceTests.BareViewer(), items);
            Assert.AreEqual(0, peddler.ItemsForSaleCount, "ProcessItemsForPurchase must not park the item in UniqueItemsForSale");
        }

        [TestMethod]
        public void Stub_RefusesEveryPurchase()
        {
            var peddler = BuildStub();

            Assert.IsFalse(peddler.BuyItems_ValidateTransaction(new List<ItemProfile>(), VendorAlternateCurrencyBalanceTests.BareViewer()));
        }

        /// <summary>
        /// Base Vendor.ActOnUse records the customer in lastPlayerInfo (and enqueues the rotate, open and close chains);
        /// the stub's no-op leaves it null.
        /// </summary>
        [TestMethod]
        public void Stub_ActOnUse_DoesNothing()
        {
            var peddler = BuildStub();

            peddler.ActOnUse(VendorAlternateCurrencyBalanceTests.BareViewer());

            Assert.IsNull(LastPlayerInfoField(peddler), "the peddler's Use must not take the retail single-customer path");
        }

        [TestMethod]
        public void Factory_BiotaArm_TypeTenBiotaWithTheFlag_BuildsAThreadPeddlerVendor()
        {
            var weenie = MakePeddlerWeenie(optIn: true);
            var biota = new Biota
            {
                Id = NextGuid(),
                WeenieClassId = weenie.WeenieClassId,
                WeenieType = WeenieType.Creature,
                PropertiesBool = new Dictionary<PropertyBool, bool>(weenie.PropertiesBool),
                PropertiesInt = new Dictionary<PropertyInt, int>(weenie.PropertiesInt),
                PropertiesFloat = new Dictionary<PropertyFloat, double>(weenie.PropertiesFloat),
                PropertiesString = new Dictionary<PropertyString, string>(weenie.PropertiesString),
            };

            // As in SalvageRollerVendorTests: the biota constructor chain loads inventory through DatabaseManager.Shard,
            // which this host does not have, so construction cannot finish. The constructor the factory entered is on the
            // stack of the exception the missing shard raises.
            StringAssert.Contains(ConstructorEntered(biota), "ThreadPeddlerVendor..ctor", "a stray type-10 peddler row must come back as the peddler");

            biota.PropertiesBool.Remove(PropertyBool.ThreadPeddlerVendor);
            biota.Id = NextGuid();

            var control = ConstructorEntered(biota);
            Assert.IsFalse(control.Contains("ThreadPeddlerVendor..ctor"), "control: without 9096 a type-10 biota is not the peddler");
            StringAssert.Contains(control, "Creature..ctor", "control: it takes the Creature arm");
        }

        private static string ConstructorEntered(Biota biota)
        {
            try
            {
                var wo = WorldObjectFactory.CreateWorldObject(biota);
                return wo == null ? "" : $"{wo.GetType().Name}..ctor (completed)";
            }
            catch (Exception ex)
            {
                return ex.StackTrace ?? "";
            }
        }

        [TestMethod]
        public void Factory_BothArmsKeyOnTheFlag_AheadOfTheTypeSwitch()
        {
            var code = AccountVaultPurgeTests.StripComments(File.ReadAllText(SalvageRollerVendorTests.FindInSourceTree("Source/ACE.Server/Factories/WorldObjectFactory.cs")));

            var weenieCheck = code.IndexOf("if (weenie.GetProperty(PropertyBool.ThreadPeddlerVendor) == true)", StringComparison.Ordinal);
            var weenieSwitch = code.IndexOf("switch (objWeenieType)", StringComparison.Ordinal);
            var biotaCheck = code.IndexOf("biota.PropertiesBool.TryGetValue(PropertyBool.ThreadPeddlerVendor, out var isThreadPeddler) && isThreadPeddler)", StringComparison.Ordinal);
            var biotaSwitch = code.IndexOf("switch (biota.WeenieType)", StringComparison.Ordinal);

            Assert.IsTrue(weenieCheck > 0 && weenieCheck < weenieSwitch, $"the weenie arm must test 9096 before its type switch (check={weenieCheck}, switch={weenieSwitch})");
            Assert.IsTrue(biotaCheck > 0 && biotaCheck < biotaSwitch, $"the biota arm must test 9096 before its type switch (check={biotaCheck}, switch={biotaSwitch})");
            Assert.AreEqual(1, Regex.Matches(code, Regex.Escape("return new ThreadPeddlerVendor(weenie, guid);")).Count);
            Assert.AreEqual(1, Regex.Matches(code, Regex.Escape("return new ThreadPeddlerVendor(biota);")).Count);
        }
    }
}
