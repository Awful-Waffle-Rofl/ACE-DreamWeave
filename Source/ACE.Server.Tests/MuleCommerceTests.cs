using System;
using System.IO;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Server.Entity;
using ACE.Server.WorldObjects;

namespace ACE.Server.Tests
{
    /// <summary>
    /// Mule Vendor (Docs/MuleVendor/DESIGN.md section 9.2; risk R5): the sell/buy gate changes that
    /// make a PersonalVendor's zero-cost commerce path mean "deposit" and "withdraw".
    ///
    /// SCOPE NOTE - why this file does not exercise Player_Commerce.HandleActionSellItem,
    /// Player.FinalizeBuyTransaction, or VerifySellItems (the full instance method) directly:
    ///
    /// No test in this project constructs a live Player. This is independently documented twice
    /// already - PersonalVendorTests.cs's class remarks ("No test in this project constructs a live
    /// Player - Player's own constructor calls DatabaseManager.Authentication.GetAccountById
    /// unconditionally") and StageTestCommandsTests.cs's class remarks ("confirmed by grepping
    /// ACE.Server.Tests for 'new Player(' ... before writing this file") - and BankTests.cs notes the
    /// same constraint from the other side: "Deposit/withdraw/transfer flows mutate inventory + DB and
    /// are exercised in-game via the test-loop rather than here."
    ///
    /// This session re-verified the blocker rather than taking it on faith: BOTH Player constructors
    /// (Player.cs:100, Player.cs:131) call DatabaseManager.Authentication.GetAccountById
    /// unconditionally (a live MySQL round-trip against ace_auth, unavailable in this test project's
    /// environment), and SetEphemeralValues (Player.cs) separately requires
    /// DatManager.PortalDat.ReadFromDat&lt;CombatManeuverTable&gt;(CombatTableDID.Value) - a real client
    /// dat lookup. Session (Network/Session.cs:86) additionally requires a live ConnectionListener/
    /// socket. Bypassing all three via FormatterServices.GetUninitializedObject was evaluated and
    /// rejected: it also skips every field initializer up the WorldObject/Container/Creature chain
    /// (Container.Inventory, Creature's equipped-object dictionary, etc. are all `= new ...()` field
    /// initializers, not constructor-body assignments), so almost any method touched afterward NREs
    /// on a null collection - reconstructing that state by hand, one private field at a time, with no
    /// existing precedent anywhere in this project, is exactly the kind of fragile one-off harness the
    /// established convention above avoids.
    ///
    /// What IS tested here: Player_Commerce.IsAcceptableToSell, a new `internal static` helper this
    /// task extracted from VerifySellItems specifically so the added Attuned rejection (originally
    /// Attuned OR Bonded, narrowed to Attuned only on 2026-08-28 once Bonded was traced to the death
    /// handler alone) and the
    /// Retained/Value&lt;1 vault bypasses have real regression coverage - the same "split out so it is
    /// testable without a Player" rationale Player_Inventory.GetAllPossessions's static core already
    /// uses (see its own doc comment). VerifySellItems itself calls this helper unchanged; the
    /// possession/duplicate-guid/amount/in-trade checks stay inline (the in-trade check needs
    /// ItemsInTradeWindow, which only a live Player carries). The non-empty-container gate moved INTO
    /// the helper in fix round 1 (F5) - it needed no Player state either, so it is exercised directly
    /// below against the real helper rather than structurally against data a test built itself.
    ///
    /// Fix round 1 (F1) added Vendor.CanAccept, a pre-flight the sell path consults before detaching an
    /// item - not covered here either, same live-Player reason; that gate belongs to Vendor.cs, not
    /// this file's scope.
    ///
    /// The sell path's bank-skip/R5-ordering behavior is NOT covered by an executable test in this file
    /// for the same live-Player reason - it is embedded in HandleActionSellItem, a Player instance
    /// method, with no further extractable decision logic. It is marked Assert.Inconclusive below,
    /// naming exactly what would need to exist (a Player/Session test harness) to make it real. This is
    /// the same idiom TestEnvironment.RequireDatabases already uses in this project to make an
    /// environment gap visible instead of silently green. FinalizeBuyTransaction's own zero-cost branch
    /// is UNREACHABLE for a PersonalVendor (fix round 1, F2 - see that method's remarks in
    /// Player_Commerce.cs) and so is deliberately NOT tested here at all, in either direction; see the
    /// comment above where its two Inconclusive stubs used to be.
    /// </summary>
    [TestClass]
    public class MuleCommerceTests
    {
        private static uint nextGuid = 0x72300000;

        private static ACE.Entity.ObjectGuid NextGuid() => new ACE.Entity.ObjectGuid(++nextGuid);

        private const ItemType AcceptsEverything = unchecked((ItemType)(-1));

        private static Stackable MakeItem(int value = 100, int stackSize = 1)
        {
            var item = FakeVaultWorld.MakeStack(8000, stackSize, 1000);
            item.ItemType = ItemType.TinkeringMaterial;
            item.Value = value;
            return item;
        }

        [TestMethod]
        public void VerifySellItems_RejectsAnAttunedItem()
        {
            var item = MakeItem();
            item.Attuned = AttunedStatus.Attuned;

            var accepted = Player.IsAcceptableToSell(item, AcceptsEverything, isVault: true, out var rejectionMessage);

            Assert.IsFalse(accepted);
            StringAssert.Contains(rejectionMessage, "attuned to you");
        }

        /// <summary>
        /// Bonded is ACCEPTED. The first build refused it alongside Attuned, on the reading that Bonded
        /// also means "bound to this character" - it does not. PropertyInt.Bonded is read only by the
        /// death handler (Player_Death.cs:549 filters bonded items out of the death drop, :1086 handles
        /// BondedStatus.Destroy, :1083 handles Slippery) and appears nowhere in Player_Trade.cs or the
        /// give path in Player_Inventory.cs. Bonded means "does not drop on death", full stop, so it
        /// says nothing about whether an item may change hands and is none of the sell gate's business.
        /// Refusing it also cost the vault a large fraction of this fork's own authored gear.
        ///
        /// This test is the inverse of the one it replaces and exists to pin that: if a future change
        /// re-adds a Bonded clause to IsAcceptableToSell, this goes red.
        ///
        /// The asymmetry with attunement is deliberate and load-bearing rather than an oversight -
        /// IsAttunedOrContainsAttuned is the flag that actually gates trade (Player_Trade.cs:141) and
        /// give (Player_Inventory.cs:1012, 1533, 2494, 2711), so an attuned item that survived a
        /// deposit could reach another account through a share grant. See
        /// VerifySellItems_RejectsAnAttunedItem.
        /// </summary>
        [TestMethod]
        public void VerifySellItems_AcceptsABondedItem()
        {
            var item = MakeItem();
            item.Bonded = BondedStatus.Bonded;

            var accepted = Player.IsAcceptableToSell(item, AcceptsEverything, isVault: true, out var rejectionMessage);

            Assert.IsTrue(accepted, $"bonded must be storable; refused with: {rejectionMessage}");
        }

        /// <summary>
        /// The stronger bonded tier must not sneak a rejection back in through a >= comparison, the way
        /// AttunedStatus.Sticky did on the attuned side before fix round B.
        /// </summary>
        [TestMethod]
        public void VerifySellItems_AcceptsAStickyBondedItem()
        {
            var item = MakeItem();
            item.Bonded = BondedStatus.Sticky;

            var accepted = Player.IsAcceptableToSell(item, AcceptsEverything, isVault: true, out var rejectionMessage);

            Assert.IsTrue(accepted, $"every bonded tier must be storable; refused with: {rejectionMessage}");
        }

        /// <summary>
        /// Fix round B, B2: the engine's own canonical predicate (WorldObject.cs:1132,
        /// IsAttunedOrContainsAttuned) is `Attuned >= AttunedStatus.Attuned` - Sticky (2) is the
        /// STRONGER tier and must fall into the same rejection as Attuned (1), not slip past a `==`
        /// gate that only matched the weaker tier. Deliberately leaves Bonded unset, so this fails
        /// against the pre-fix `==` comparison (all 17 real Sticky weenies also carry Bonded, which
        /// would mask the bug here) - see the fix report for the before/after run.
        /// </summary>
        [TestMethod]
        public void VerifySellItems_RejectsAStickyAttunedItem_WithNoBondedFlag()
        {
            var item = MakeItem();
            item.Attuned = AttunedStatus.Sticky;

            var accepted = Player.IsAcceptableToSell(item, AcceptsEverything, isVault: true, out var rejectionMessage);

            Assert.IsFalse(accepted, "Sticky is the STRONGER attunement tier and must be rejected same as Attuned");
            StringAssert.Contains(rejectionMessage, "attuned to you");
        }

        [TestMethod]
        public void VerifySellItems_RejectsAnAttunedItem_AtARetailVendorToo()
        {
            var item = MakeItem();
            item.Attuned = AttunedStatus.Attuned;

            // isVault: false - retail never had this check at all (Vendor.cs:713 only suppressed
            // reselling), and DESIGN 9.2 makes it unconditional. Must reject at a plain Vendor too.
            var accepted = Player.IsAcceptableToSell(item, AcceptsEverything, isVault: false, out var rejectionMessage);

            Assert.IsFalse(accepted);
            StringAssert.Contains(rejectionMessage, "attuned to you");
        }

        [TestMethod]
        public void VerifySellItems_AtAVault_AcceptsAZeroValueItem()
        {
            var item = MakeItem(value: 0);

            var accepted = Player.IsAcceptableToSell(item, AcceptsEverything, isVault: true, out var rejectionMessage);

            Assert.IsTrue(accepted, rejectionMessage);
        }

        [TestMethod]
        public void VerifySellItems_AtARetailVendor_StillRejectsAZeroValueItem()
        {
            var item = MakeItem(value: 0);

            var accepted = Player.IsAcceptableToSell(item, AcceptsEverything, isVault: false, out var rejectionMessage);

            Assert.IsFalse(accepted);
            StringAssert.Contains(rejectionMessage, "no value");
        }

        [TestMethod]
        public void VerifySellItems_AtAVault_AcceptsARetainedItem()
        {
            var item = MakeItem();
            item.Retained = true;

            var accepted = Player.IsAcceptableToSell(item, AcceptsEverything, isVault: true, out var rejectionMessage);

            Assert.IsTrue(accepted, rejectionMessage);
        }

        [TestMethod]
        public void VerifySellItems_AtARetailVendor_StillRejectsARetainedItem()
        {
            var item = MakeItem();
            item.Retained = true;

            var accepted = Player.IsAcceptableToSell(item, AcceptsEverything, isVault: false, out var rejectionMessage);

            Assert.IsFalse(accepted);
            StringAssert.Contains(rejectionMessage, "unsellable");
        }

        [TestMethod]
        public void VerifySellItems_AtAVault_AcceptsAnUnsellableItem()
        {
            var item = MakeItem();
            item.IsSellable = false;

            // User ruling 2026-08-29: the vault's rule is "can I hand this to another player?", and
            // IsSellable is a coin-price flag no give or trade path reads. A pyreal nugget (wcid 6354)
            // carries it and was refused with "unsellable" in live test - see IsAcceptableToSell.
            var accepted = Player.IsAcceptableToSell(item, AcceptsEverything, isVault: true, out var rejectionMessage);

            Assert.IsTrue(accepted, rejectionMessage);
            Assert.IsNull(rejectionMessage);
        }

        [TestMethod]
        public void VerifySellItems_AtARetailVendor_StillRejectsAnUnsellableItem()
        {
            var item = MakeItem();
            item.IsSellable = false;

            var accepted = Player.IsAcceptableToSell(item, AcceptsEverything, isVault: false, out var rejectionMessage);

            Assert.IsFalse(accepted);
            StringAssert.Contains(rejectionMessage, "unsellable");
        }

        private static PetDevice MakePetDevice()
        {
            var weenie = new ACE.Entity.Models.Weenie
            {
                WeenieClassId = 8001,
                WeenieType = WeenieType.PetDevice,
                PropertiesInt = new System.Collections.Generic.Dictionary<PropertyInt, int>
                {
                    { PropertyInt.ItemType, (int)ItemType.Misc },
                    { PropertyInt.Value, 100 },
                },
                PropertiesString = new System.Collections.Generic.Dictionary<PropertyString, string>
                {
                    { PropertyString.Name, "Test Essence" },
                },
            };

            return new PetDevice(weenie, NextGuid());
        }

        [TestMethod]
        public void VerifySellItems_AtAVault_RejectsAPetDeviceWithItsPetSummoned()
        {
            // Mirrors GiveObjectToPlayer (Player_Inventory.cs) and Player_Trade.cs: a device whose pet
            // is out cannot change hands, so it cannot be stored either.
            var device = MakePetDevice();
            device.Pet = 0x80000001;

            var accepted = Player.IsAcceptableToSell(device, AcceptsEverything, isVault: true, out var rejectionMessage);

            Assert.IsFalse(accepted);
            StringAssert.Contains(rejectionMessage, "unsummon your pet");
        }

        [TestMethod]
        public void VerifySellItems_AtAVault_AcceptsAPetDeviceWithNoPetOut()
        {
            var device = MakePetDevice();

            var accepted = Player.IsAcceptableToSell(device, AcceptsEverything, isVault: true, out var rejectionMessage);

            Assert.IsTrue(accepted, rejectionMessage);
        }

        [TestMethod]
        public void VerifySellItems_AtAVault_StillRejectsAnUnacceptedItemType()
        {
            // The MerchandiseItemTypes mask term had zero coverage: all other tests here pass
            // AcceptsEverything (unchecked((ItemType)(-1))), so (acceptedItemTypes & wo.ItemType) == 0
            // is false in every one of them and deleting the term would break nothing. The Edit-2
            // table's "keep, and solve it in data" decision is only meaningful if this path stays live.
            var item = MakeItem(); // ItemType.TinkeringMaterial

            var accepted = Player.IsAcceptableToSell(item, ItemType.Gem, isVault: true, out var rejectionMessage);

            Assert.IsFalse(accepted);
            StringAssert.Contains(rejectionMessage, "unsellable");
        }

        [TestMethod]
        public void VerifySellItems_AtAVault_StillRejectsANonEmptyContainer()
        {
            // The non-empty-container gate now lives INSIDE IsAcceptableToSell (moved there in fix
            // round 1 - it needs no Player state, so it belongs with the rest of the item-level gates
            // rather than staying inline in VerifySellItems). This drives the real helper against a
            // real Container instead of re-stating the production predicate against data the test
            // itself just built.
            var container = FakeVaultWorld.MakeContainer(itemsCapacity: 6);
            container.ItemType = ItemType.Container; // MakeContainer's weenie sets no ItemType; without this the mask gate (a different, unrelated check) would fire first.
            var contents = MakeItem();
            Assert.IsTrue(container.TryAddToInventory(contents), "sanity: the container must actually hold an item");

            var accepted = Player.IsAcceptableToSell(container, AcceptsEverything, isVault: true, out var rejectionMessage);

            Assert.IsFalse(accepted, "a container holding an item must fail the gate regardless of isVault - that gate is never bypassed");
            StringAssert.Contains(rejectionMessage, "must be empty");
        }

        // Fix round 1 (F2): the two FinalizeBuyTransaction_* inconclusive tests that used to live here
        // are deleted, not just retitled. FinalizeBuyTransaction's `vendor is PersonalVendor` branch
        // (Player_Commerce.cs, in FinalizeBuyTransaction) is UNREACHABLE for a PersonalVendor - see that
        // method's own remarks for why - so "does withdrawal charge the player?" is not a question this
        // branch can answer either way, and a test named around it would misdirect a future reader the
        // same way the old comment did. The real ground - GetBuyCost/GetSellCost are always 0 for a
        // PersonalVendor - is already covered by an executing test in PersonalVendorTests.cs
        // (BuyCost_IsZero, SellCost_IsZero); pointing at those is more honest than keeping an
        // Inconclusive stub aimed at code that never runs.

        [TestMethod]
        public void SellPath_AtAVault_CreditsNoBank()
        {
            Assert.Inconclusive("Requires a live Player/Session harness - see the class remarks. Player_Commerce.cs's HandleActionSellItem payout guard (`if (!(vendor is PersonalVendor))`) is the code under test; verify via the live test-loop instead.");
        }

        [TestMethod]
        public void SellPath_AtAVault_SendsNoBankChatMessage()
        {
            Assert.Inconclusive("Requires a live Player/Session harness - see the class remarks. Same guard as SellPath_AtAVault_CreditsNoBank; verify via the live test-loop instead.");
        }

        [TestMethod]
        public void SellPath_FlushesDeferredSaves_BeforeProcessItemsForPurchase()
        {
            // R5 (DESIGN section 14): FlushDeferredSaves must run before vendor.ProcessItemsForPurchase
            // in HandleActionSellItem (Player_Commerce.cs, inside the sell path). That ordering was not
            // reordered by this task - see the brief's R5 note - and driving it end-to-end needs the
            // same live Player/Session harness this file cannot build (see class remarks).
            Assert.Inconclusive("Requires a live Player/Session harness plus a recording fake shard queue - see the class remarks. Player_Commerce.cs's HandleActionSellItem (FlushDeferredSaves then vendor.ProcessItemsForPurchase) is the code under test; verify via the live test-loop instead.");
        }

        [TestMethod]
        public void ItemProfile_ExposesATransactionCeiling_AndBothVendorHandlersEnforceIt()
        {
            Assert.AreEqual(512, ItemProfile.MaxProfilesPerTransaction,
                "the ceiling is deliberately generous - well above any real inventory - because its job is to bound a hostile numItems, not to constrain play.");

            foreach (var relativePath in new[]
                     {
                         "Source/ACE.Server/Network/GameAction/Actions/GameActionSellItems.cs",
                         "Source/ACE.Server/Network/GameAction/Actions/GameActionBuyItems.cs",
                     })
            {
                var path = FindInSourceTree(relativePath);
                Assert.IsNotNull(path, $"Could not find {relativePath} by walking up from {AppContext.BaseDirectory}.");

                var code = File.ReadAllText(path);

                var iCheck = code.IndexOf("MaxProfilesPerTransaction", StringComparison.Ordinal);
                Assert.IsTrue(iCheck >= 0, $"{relativePath} must refuse a numItems above ItemProfile.MaxProfilesPerTransaction. numItems arrives as an unvalidated uint straight off the wire and is used directly as a loop bound.");

                var iSendUseDone = code.IndexOf("SendUseDoneEvent", StringComparison.Ordinal);
                Assert.IsTrue(iSendUseDone > iCheck, $"{relativePath} must send SendUseDoneEvent (GameEventUseDone) from inside the ceiling refusal, after MaxProfilesPerTransaction. A wire-level drop that returns without it leaves the vendor panel waiting on a response that never arrives - every other refusal on this path (Player_Commerce.cs:25-46) answers the client before returning.");

                var iLoop = code.IndexOf("for (var i = 0; i < numItems; i++)", StringComparison.Ordinal);
                Assert.IsTrue(iLoop > iCheck, $"{relativePath} must check the ceiling BEFORE the loop, not inside or after it.");
                Assert.IsTrue(iLoop > iSendUseDone, $"{relativePath} must send SendUseDoneEvent BEFORE the loop, as part of the ceiling refusal, not after it.");
            }
        }

        [TestMethod]
        public void CallSite_VerifySellItems_DeduplicatesEveryProfileNotOnlyAcceptedOnes()
        {
            const string relativePath = "Source/ACE.Server/WorldObjects/Player_Commerce.cs";

            var path = FindInSourceTree(relativePath);
            Assert.IsNotNull(path, $"Could not find {relativePath} by walking up from {AppContext.BaseDirectory}.");

            var code = AccountVaultPurgeTests.StripComments(File.ReadAllText(path));

            var iMethod = code.IndexOf("private Dictionary<uint, WorldObject> VerifySellItems(", StringComparison.Ordinal);
            Assert.IsTrue(iMethod >= 0, "VerifySellItems was not found - if it was resignatured, update this test rather than deleting it.");

            var iCanAccept = code.IndexOf("vendor.CanAccept(", iMethod, StringComparison.Ordinal);
            Assert.IsTrue(iCanAccept > iMethod, "the CanAccept call was not found inside VerifySellItems.");

            var iSeen = code.IndexOf("seen.Add(", iMethod, StringComparison.Ordinal);

            Assert.IsTrue(iSeen >= 0 && iSeen < iCanAccept,
                "the duplicate guard must be populated for EVERY profile, before the CanAccept call that can refuse it. Testing 'verified' instead is the bug: verified is only populated on full acceptance, so against a vendor that refuses - which is every PersonalVendor an actor is unauthorized on - the guard never arms and each repetition of one guid costs another shard query.");

            Assert.IsFalse(code.IndexOf("verified.ContainsKey(", iMethod, StringComparison.Ordinal) >= 0
                           && code.IndexOf("verified.ContainsKey(", iMethod, StringComparison.Ordinal) < iCanAccept,
                "the old verified.ContainsKey duplicate test must be gone - leaving it beside the new one invites a later reader to delete the wrong one.");
        }

        [TestMethod]
        public void CallSite_VerifySellItems_ResolvesVaultAccessOnceOutsideTheProfileLoop()
        {
            const string relativePath = "Source/ACE.Server/WorldObjects/Player_Commerce.cs";

            var path = FindInSourceTree(relativePath);
            Assert.IsNotNull(path, $"Could not find {relativePath} by walking up from {AppContext.BaseDirectory}.");

            var code = AccountVaultPurgeTests.StripComments(File.ReadAllText(path));

            var iMethod = code.IndexOf("private Dictionary<uint, WorldObject> VerifySellItems(", StringComparison.Ordinal);
            Assert.IsTrue(iMethod >= 0, "VerifySellItems was not found.");

            var iLoop = code.IndexOf("foreach (var sellItem in sellItems)", iMethod, StringComparison.Ordinal);
            Assert.IsTrue(iLoop > iMethod, "the profile loop was not found inside VerifySellItems.");

            var iResolve = code.IndexOf("TryResolveSellAccess(", iMethod, StringComparison.Ordinal);

            Assert.IsTrue(iResolve >= 0 && iResolve < iLoop,
                "vault access must be resolved ONCE, before the profile loop. Resolving inside it puts a shard query on every iteration. This is safe not because grants cannot change mid-loop - they can, since the loop does not run on the store's mutation queue and landblock groups tick in parallel - but because CanAccept is a preflight and TryDeposit re-resolves access on the queue inside every actual deposit, refusing and handing the item back.");

            var iCanAccept = code.IndexOf("vendor.CanAccept(", iLoop, StringComparison.Ordinal);
            Assert.IsTrue(iCanAccept > iLoop, "the CanAccept call was not found inside the loop.");

            var iPassed = code.IndexOf("resolvedAccess", iCanAccept, StringComparison.Ordinal);
            Assert.IsTrue(iPassed >= 0 && iPassed < iCanAccept + 200,
                "the resolved access must be PASSED IN to CanAccept, not re-resolved inside it.");
        }

        /// <summary>
        /// F7, half two. A source scan rather than a behavioural test for the reason this file's class
        /// remarks give at length: HandleActionBuyItem needs a live Player and a live Session, and no
        /// test in this project constructs either.
        ///
        /// The assertion deliberately anchors on `is PersonalVendor`, a piece of real code, and never
        /// on the comment that sits above it: AccountVaultPurgeTests.StripComments cuts every line at
        /// its first unquoted `//`, so an assertion anchored on comment text would be asserting against
        /// a string the scan has already erased.
        /// </summary>
        [TestMethod]
        public void CallSite_RejectedBuy_ReApproachesAPersonalVendorOnlyWhenItsViewIsStale()
        {
            const string relativePath = "Source/ACE.Server/WorldObjects/Player_Commerce.cs";

            var path = FindInSourceTree(relativePath);
            Assert.IsNotNull(path, $"Could not find {relativePath} by walking up from {AppContext.BaseDirectory}.");

            var code = AccountVaultPurgeTests.StripComments(File.ReadAllText(path));

            var iReject = code.IndexOf("if (!vendor.BuyItems_ValidateTransaction(items, this))", StringComparison.Ordinal);
            Assert.IsTrue(iReject >= 0, "the rejected-buy branch was not found.");

            var iApproach = code.IndexOf("vendor.ApproachVendor(this, VendorType.Undef)", iReject, StringComparison.Ordinal);
            Assert.IsTrue(iApproach > iReject, "the re-approach call was not found in the rejected-buy branch.");

            var between = code.Substring(iReject, iApproach - iReject);

            Assert.IsTrue(between.Contains("is PersonalVendor"),
                "the re-approach must be conditional for a PersonalVendor. Its stated purpose is refreshing the alternate-currency figure, and a mule has no alternate currency - so on a mule an unconditional re-approach is pure panel-rebuild cost, plus a grants SELECT for a non-owner, on a path a client can loop with deliberately invalid profiles.");

            Assert.IsTrue(between.Contains("ViewIsStale"),
                "the re-approach must still happen when the mule's window is stale. BuyItems_ValidateTransaction IS TryWithdrawTransaction for a PersonalVendor, so a false return means the withdrawal was refused - and one way it is refused is that another window on the same store drained the row this panel still shows. Skipping the rebuild unconditionally leaves that panel wrong until the player walks out of range, and every retry writes two more permanent audit rows.");
        }

        private static string FindInSourceTree(string relativePath)
        {
            var native = relativePath.Replace('/', Path.DirectorySeparatorChar);

            for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
            {
                var candidate = Path.Combine(dir.FullName, native);

                if (File.Exists(candidate))
                    return candidate;
            }

            return null;
        }
    }
}
