using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using System.Threading;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Entity;
using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Entity.Models;
using ACE.Server.Factories;
using ACE.Server.ThreadDungeons;
using ACE.Server.WorldObjects;

namespace ACE.Server.Tests
{
    /// <summary>
    /// Vendor.AlternateCurrencyBalanceFor, the per-viewer alternate-currency seam GameEventApproachVendor reads. Its
    /// default must be exactly the inventory + banked sum the approach message wrote before the seam existed, for every
    /// alternate currency the bank backs (promissory note, Mark of the Hopeslayer, Class Ability Points, MMD) and for a
    /// plain inventory-only currency (pyreal coin). The viewer is a bare Player (never constructed, so no auth or world
    /// database read runs): its private state is set through UnsafeAccessor, never FieldInfo.SetValue, after priming the
    /// Player type initializer (ThornsCloseRangeTests.EnsurePlayerTypeInitializable).
    ///
    /// The vendor is never constructed either: the default reads only the viewer.
    ///
    /// This class reads no PropertyManager key, so there is nothing to seed or restore.
    /// </summary>
    [TestClass]
    public class VendorAlternateCurrencyBalanceTests
    {
        private const uint PyrealWcid = 273;
        private const uint PromissoryNoteWcid = 43901;
        private const uint ClassAbilityPointWcid = 1001010;

        private static uint nextGuid = 0x7EB00000;

        [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "<Biota>k__BackingField")]
        private static extern ref Biota BiotaField(WorldObject wo);

        [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "BiotaDatabaseLock")]
        private static extern ref ReaderWriterLockSlim BiotaLockField(WorldObject wo);

        [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "<Inventory>k__BackingField")]
        private static extern ref Dictionary<ObjectGuid, WorldObject> InventoryField(Container container);

        [ClassInitialize]
        public static void PrimePlayerType(TestContext _)
        {
            ThornsCloseRangeTests.EnsurePlayerTypeInitializable();
        }

        /// <summary>A bare viewer with a biota, its lock and an empty pack. Callers must prime the Player type first.</summary>
        internal static Player BareViewer()
        {
            var player = (Player)RuntimeHelpers.GetUninitializedObject(typeof(Player));

            BiotaField(player) = new Biota { Id = 0x50000777, WeenieType = WeenieType.Creature };
            BiotaLockField(player) = new ReaderWriterLockSlim();
            InventoryField(player) = new Dictionary<ObjectGuid, WorldObject>();

            return player;
        }

        private static void Hold(Player player, uint wcid, int stackSize)
        {
            var weenie = new Weenie
            {
                WeenieClassId = wcid,
                WeenieType = WeenieType.Generic,
                PropertiesInt = new Dictionary<PropertyInt, int> { { PropertyInt.StackSize, stackSize } },
            };

            var item = WorldObjectFactory.CreateWorldObject(weenie, new ObjectGuid(++nextGuid));
            player.Inventory[item.Guid] = item;
        }

        private static Vendor BareVendor() => (Vendor)RuntimeHelpers.GetUninitializedObject(typeof(Vendor));

        /// <summary>The expression GameEventApproachVendor used before the seam, term for term.</summary>
        private static long OldSum(Player viewer, uint wcid) => viewer.GetNumInventoryItemsOfWCID(wcid) + viewer.GetBankedAlternateCurrency(wcid);

        private static void AssertDefault(Player viewer, uint wcid, long expected, string what)
        {
            var hook = BareVendor().AlternateCurrencyBalanceFor(viewer, wcid);

            Assert.AreEqual(expected, hook, $"{what}: the default hook must be inventory + banked");
            Assert.AreEqual(OldSum(viewer, wcid), hook, $"{what}: the default hook must equal the old approach-message sum");
            Assert.AreEqual((uint)viewer.GetNumInventoryItemsOfWCID(wcid) + (uint)viewer.GetBankedAlternateCurrency(wcid), (uint)hook,
                $"{what}: the uint the message writes must equal the old per-term uint sum");
        }

        [TestMethod]
        public void Default_Pyreal_IsInventoryOnly_AcrossStacks()
        {
            var viewer = BareViewer();
            Hold(viewer, PyrealWcid, 1000);
            Hold(viewer, PyrealWcid, 250);

            Assert.AreEqual(0, viewer.GetBankedAlternateCurrency(PyrealWcid), "sanity: pyreal coin has no banked backing as an alternate currency");
            AssertDefault(viewer, PyrealWcid, 1250, "pyreal");
        }

        [TestMethod]
        public void Default_PromissoryNote_IsInventoryPlusBanked()
        {
            var viewer = BareViewer();
            Hold(viewer, PromissoryNoteWcid, 5);
            viewer.BankedPromissoryNotes = 7;

            AssertDefault(viewer, PromissoryNoteWcid, 12, "promissory note");
        }

        [TestMethod]
        public void Default_HopeslayerMark_IsInventoryPlusBanked()
        {
            var viewer = BareViewer();
            Hold(viewer, Player.HopeslayerMarkWcid, 20);
            viewer.BankedHopeslayerMarks = 300;

            AssertDefault(viewer, Player.HopeslayerMarkWcid, 320, "Hopeslayer mark");
        }

        [TestMethod]
        public void Default_ClassAbilityPoints_IsTheCapPropertyWithNoInventoryItem()
        {
            var viewer = BareViewer();
            viewer.SetProperty(PropertyInt.AvailableClassAbilityPoints, 40);

            AssertDefault(viewer, ClassAbilityPointWcid, 40, "CAP");
        }

        /// <summary>
        /// MMD's banked leg is BankedPyreals / MmdValue, and BankedPyreals reads 0 for a viewer with no Account (this bare
        /// player), so the banked leg is 0 here and the inventory leg carries the value. The equality with the old sum is
        /// what this pins, whatever the banked leg returns.
        /// </summary>
        [TestMethod]
        public void Default_Mmd_IsInventoryPlusBanked()
        {
            var viewer = BareViewer();
            Hold(viewer, Player.MmdWcid, 3);

            AssertDefault(viewer, Player.MmdWcid, 3 + viewer.GetBankedAlternateCurrency(Player.MmdWcid), "MMD");
        }

        /// <summary>
        /// Pins the truncation equivalence: a balance past uint range keeps its low 32 bits, exactly as the old per-term
        /// (uint) casts did. 2^32 + 5 banked notes write 5.
        /// </summary>
        [TestMethod]
        public void Default_BalancePastUintRange_TruncatesToTheLow32Bits()
        {
            var viewer = BareViewer();
            viewer.BankedPromissoryNotes = 4294967296L + 5;

            var hook = BareVendor().AlternateCurrencyBalanceFor(viewer, PromissoryNoteWcid);

            Assert.AreEqual(4294967296L + 5, hook, "the hook itself returns the full long");
            Assert.AreEqual(5u, (uint)hook, "the approach message's uint cast keeps the low 32 bits");
            AssertDefault(viewer, PromissoryNoteWcid, 4294967296L + 5, "promissory note past uint range");
        }

        [TestMethod]
        public void Default_NothingHeld_IsZero()
        {
            var viewer = BareViewer();

            foreach (var wcid in new[] { PyrealWcid, PromissoryNoteWcid, Player.HopeslayerMarkWcid, ClassAbilityPointWcid, Player.MmdWcid })
                AssertDefault(viewer, wcid, 0, $"wcid {wcid} with nothing held");
        }

        [TestMethod]
        public void ThreadPeddler_OverridesTheHook()
        {
            var method = typeof(ThreadPeddlerVendor).GetMethod(nameof(Vendor.AlternateCurrencyBalanceFor));

            Assert.IsNotNull(method);
            Assert.AreEqual(typeof(ThreadPeddlerVendor), method.DeclaringType, "the peddler shows the run's per-player balance, never an inventory count (ThreadPeddlerVendorTests pins its values)");
        }

        /// <summary>
        /// The approach message reads the balance through the hook alone: one call, the altCurrencySpent term kept, and
        /// neither of the two old direct reads left in the constructor.
        /// </summary>
        [TestMethod]
        public void ApproachVendor_ReadsTheBalanceThroughTheHook()
        {
            var code = AccountVaultPurgeTests.StripComments(File.ReadAllText(SalvageRollerVendorTests.FindInSourceTree("Source/ACE.Server/Network/GameEvent/Events/GameEventApproachVendor.cs")));

            Assert.AreEqual(1, Regex.Matches(code, Regex.Escape("vendor.AlternateCurrencyBalanceFor(session.Player, vendor.AlternateCurrency.Value)")).Count);
            StringAssert.Contains(code, "Writer.Write(altCurrencyBalance + altCurrencySpent);");
            Assert.IsFalse(code.Contains("GetNumInventoryItemsOfWCID"), "the inventory read moved into the hook");
            Assert.IsFalse(code.Contains("GetBankedAlternateCurrency"), "the banked read moved into the hook");
        }
    }
}
