using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Serialization;
using System.Text.RegularExpressions;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Common;
using ACE.DatLoader;
using ACE.Database;
using ACE.Database.Models.Shard;
using ACE.Entity;
using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Entity.Models;
using ACE.Server.Entity;
using ACE.Server.Entity.AccountVault;
using ACE.Server.Factories;
using ACE.Server.Managers;
using ACE.Server.Network.GameMessages;
using ACE.Server.WorldObjects;

using ShardAccountVault = ACE.Database.Models.Shard.AccountVault;

namespace ACE.Server.Tests
{
    /// <summary>
    /// Mule Vendor: PersonalVendor, the disposable window onto an AccountVaultStore (Docs/MuleVendor/
    /// DESIGN.md sections 5, 9, 9.1, 10, 12; risk R10). Covers fix round 1's findings (I1-I8, M1-M9,
    /// Cl2) on top of the original 20.
    ///
    /// Three constructibility problems had to be solved before any of this could run, none of them
    /// obvious from the class under test alone:
    ///
    /// 1. Constructing ANY Vendor (base or PersonalVendor) touches three things a plain unit test does
    ///    not have, in order: GameTables (Creature.SetEphemeralValues reads vital formulas -
    ///    "GameTables.Initialize has not been called" is the exact failure without
    ///    TestGameTables.EnsureInitialized()), PropertyManager's boolean cache
    ///    (Vendor.SetEphemeralValues reads vendor_shop_uses_generator from ShardConfig - an EF NRE in
    ///    ShardDbContext.OnConfiguring without a config seed), and DatabaseManager.World's weenie cache
    ///    (Vendor.ValidateVendorRequirements looks up the currency weenie - a live MySqlException,
    ///    "Access denied for user 'root'@'localhost'", against this worktree's actual local MySQL
    ///    instance). All three were confirmed by constructing a bare Vendor and reading the exception
    ///    at each step before adding the seed that removed it - see <see cref="EnsureVendorConstructible"/>.
    ///
    /// 2. No test in this project constructs a live Player - Player's own constructor calls
    ///    DatabaseManager.Authentication.GetAccountById unconditionally (Player.cs:107), which is
    ///    exactly why StageTestCommandsTests.cs documents "No test in this project constructs a live
    ///    Player/Session". Every PersonalVendor member whose BASE signature takes a Player therefore
    ///    has an `internal` core overload taking a <see cref="VaultActor"/> directly - the same
    ///    testability split AccountVaultStore itself already uses for its own Player-typed public API
    ///    (TryDeposit/TryWithdraw/GetAccess all have VaultActor overloads). The tests below exercise
    ///    those cores, not the public Player-typed wrappers.
    ///
    /// 3. RebuildView's ledger branch calls the REAL WorldObjectFactory.CreateNewWorldObject, which
    ///    allocates a guid through GuidManager.NewDynamicGuid(). GuidManager.Initialize() is never
    ///    called in this test project either, so dynamicAlloc is null and that call throws a bare
    ///    NullReferenceException - not a database error, confirmed by constructing a bare allocator and
    ///    reading the failure before seeding it. <see cref="EnsureGuidManagerConstructible"/> seeds a
    ///    private DynamicGuidAllocator via reflection (FormatterServices.GetUninitializedObject plus
    ///    field sets, since its real constructor also hits the shard database) so the real ledger path
    ///    can actually run rather than being avoided.
    ///
    /// The store side is driven through Task 4's two seams (IAccountVaultBackend,
    /// IAccountVaultWorldSource, both in AccountVaultFakes.cs) exactly as AccountVaultStoreTests does,
    /// so none of this needs a database or a network layer either.
    /// </summary>
    [TestClass]
    public class PersonalVendorTests
    {
        /// <summary>
        /// The class tier's tunables are read on every non-pristine deposit, and an uncached read
        /// falls through to a shard database this process does not have. See VaultClassTestConfig.
        /// </summary>
        [TestInitialize]
        public void SeedVaultClassTunables()
        {
            VaultClassTestConfig.Seed();
        }

        private const uint OwnerAccount = 5001;
        private const uint StrangerAccount = 5002;
        private const uint GranteeAccount = 5003;

        private const uint OwnerCharacter = 0x50000201;
        private const uint StrangerCharacter = 0x50000202;
        private const uint GranteeCharacter = 0x50000203;

        private static readonly VaultActor Owner = new VaultActor(OwnerAccount, OwnerCharacter, "Vaultowner");
        private static readonly VaultActor Stranger = new VaultActor(StrangerAccount, StrangerCharacter, "Somebodyelse");
        private static readonly VaultActor DepositOnlyGrantee = new VaultActor(GranteeAccount, GranteeCharacter, "Grantee");

        private static uint nextGuid = 0x71300000;

        private static uint NextGuid() => ++nextGuid;

        private static bool vendorConstructibleSetUpDone;
        private static bool guidManagerSetUpDone;

        /// <summary>
        /// Seeds the three things Vendor's own constructor chain needs and a live shard does not
        /// provide here - see the class remarks. Idempotent; safe to call from every test.
        /// </summary>
        private static void EnsureVendorConstructible()
        {
            if (vendorConstructibleSetUpDone)
                return;

            TestGameTables.EnsureInitialized();

            Assert.IsTrue(PropertyManager.ModifyBool("vendor_shop_uses_generator", false),
                "vendor_shop_uses_generator is missing from DefaultBooleanProperties");

            SeedWorldWeenie((uint)WeenieClassName.W_COINSTACK_CLASS, WeenieType.Coin);

            // AccountVaultStore.BaseEntryCap and ReservedVaultLocation both read these two keys through
            // PropertyManager.GetLong on every deposit/withdraw - unseeded, GetLong falls through to
            // DatabaseManager.ShardConfig.GetLong, which NREs against ShardDbContext.OnConfiguring with
            // no live shard database here. Found by tracing a swallowed exception: AccountVaultStore's
            // own mutation queue (Drain) catches and logs any exception thrown by queued work rather
            // than propagating it, so a deposit that hit this NRE surfaced only as "ok=false,
            // failReason=null" with no visible cause until the queued action was invoked directly and
            // its TargetInvocationException.InnerException was read.
            Assert.IsTrue(PropertyManager.ModifyLong("account_vault_entry_cap", 500),
                "account_vault_entry_cap is missing from DefaultLongProperties");
            Assert.IsTrue(PropertyManager.ModifyLong("account_vault_landblock", 0x7F20),
                "account_vault_landblock is missing from DefaultLongProperties");

            // RestampRot reads this key too (fix round 2, the coordinator's post-summon-task
            // addendum) - same cold-cache NRE trap as the two keys above if left unseeded.
            Assert.IsTrue(PropertyManager.ModifyLong("account_vault_summon_rot_seconds", 600),
                "account_vault_summon_rot_seconds is missing from DefaultLongProperties");

            vendorConstructibleSetUpDone = true;
        }

        private static void SeedWorldWeenie(uint wcid, WeenieType type)
        {
            var weenie = new Weenie { WeenieClassId = wcid, WeenieType = type };

            var field = typeof(WorldDatabaseWithEntityCache).GetField("weenieCache", BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.IsNotNull(field, "WorldDatabaseWithEntityCache.weenieCache was not found by reflection - has it been renamed?");

            var dict = (ConcurrentDictionary<uint, Weenie>)field.GetValue(DatabaseManager.World);
            dict[wcid] = weenie;
        }

        /// <summary>Seeds a stackable weenie WITH properties, for the real WorldObjectFactory ledger-materialization path (I1).</summary>
        private static void SeedWorldStackableWeenie(uint wcid, int maxStackSize)
        {
            var weenie = new Weenie
            {
                WeenieClassId = wcid,
                WeenieType = WeenieType.Stackable,
                PropertiesInt = new Dictionary<PropertyInt, int>
                {
                    { PropertyInt.ItemType, (int)ItemType.Misc },
                    { PropertyInt.StackSize, 1 },
                    { PropertyInt.MaxStackSize, maxStackSize },
                    { PropertyInt.EncumbranceVal, 1 },
                    { PropertyInt.Value, 1 },
                },
                PropertiesString = new Dictionary<PropertyString, string> { { PropertyString.Name, $"Ledger Item {wcid}" } },
            };

            var field = typeof(WorldDatabaseWithEntityCache).GetField("weenieCache", BindingFlags.NonPublic | BindingFlags.Instance);
            var dict = (ConcurrentDictionary<uint, Weenie>)field.GetValue(DatabaseManager.World);
            dict[wcid] = weenie;
        }

        /// <summary>
        /// Same shape as <see cref="SeedWorldStackableWeenie"/>, but with an explicit Name (so two
        /// different wcids can be made to share one base name, which two independently-authored
        /// weenies legitimately can in production) and ItemWorkmanship/NumItemsInMaterial baked into
        /// the weenie itself, so the fresh WorldObject WorldObjectFactory materializes for a ledger row
        /// carries a real Workmanship value without needing a stored biota at all.
        /// </summary>
        private static void SeedWorldStackableWeenieWithWorkmanship(uint wcid, string name, int itemWorkmanship, int numItemsInMaterial)
        {
            var weenie = new Weenie
            {
                WeenieClassId = wcid,
                WeenieType = WeenieType.Stackable,
                PropertiesInt = new Dictionary<PropertyInt, int>
                {
                    { PropertyInt.ItemType, (int)ItemType.Misc },
                    { PropertyInt.StackSize, 1 },
                    { PropertyInt.MaxStackSize, 100 },
                    { PropertyInt.EncumbranceVal, 1 },
                    { PropertyInt.Value, 1 },
                    { PropertyInt.ItemWorkmanship, itemWorkmanship },
                    { PropertyInt.NumItemsInMaterial, numItemsInMaterial },
                },
                PropertiesString = new Dictionary<PropertyString, string> { { PropertyString.Name, name } },
            };

            var field = typeof(WorldDatabaseWithEntityCache).GetField("weenieCache", BindingFlags.NonPublic | BindingFlags.Instance);
            var dict = (ConcurrentDictionary<uint, Weenie>)field.GetValue(DatabaseManager.World);
            dict[wcid] = weenie;
        }

        /// <summary>
        /// Seeds GuidManager's private dynamicAlloc field via reflection so the REAL
        /// WorldObjectFactory.CreateNewWorldObject path (RebuildView's ledger branch) can run without a
        /// live shard database - see class remarks, point 3. Confirmed against a scratch probe test
        /// before being folded in here: NewDynamicGuid() and RecycleDynamicGuid() both work correctly
        /// against the seeded allocator afterward.
        /// </summary>
        private static void EnsureGuidManagerConstructible()
        {
            if (guidManagerSetUpDone)
                return;

            var allocatorType = typeof(GuidManager).GetNestedType("DynamicGuidAllocator", BindingFlags.NonPublic);
            Assert.IsNotNull(allocatorType, "GuidManager.DynamicGuidAllocator was not found by reflection - has it been renamed?");

            var allocator = FormatterServices.GetUninitializedObject(allocatorType);

            void SetField(string name, object value) =>
                allocatorType.GetField(name, BindingFlags.NonPublic | BindingFlags.Instance).SetValue(allocator, value);

            SetField("min", ObjectGuid.DynamicMin);
            SetField("max", ObjectGuid.DynamicMax);
            // Well clear of both this file's own static-range test guids (0x713xxxxx) and the dynamic
            // guids AccountVaultFakes hands out, so a real materialized display object's guid can never
            // collide with a hand-made test object's guid within one test run.
            SetField("current", 0x7C000000u);
            SetField("name", "test-dynamic");
            SetField("recycledGuids", Activator.CreateInstance(typeof(Queue<Tuple<DateTime, uint>>)));

            var availableIdsFieldType = allocatorType.GetField("availableIDs", BindingFlags.NonPublic | BindingFlags.Instance).FieldType;
            SetField("availableIDs", Activator.CreateInstance(availableIdsFieldType));
            SetField("useSequenceGapExhaustedMessageDisplayed", false);

            var dynamicAllocField = typeof(GuidManager).GetField("dynamicAlloc", BindingFlags.NonPublic | BindingFlags.Static);
            Assert.IsNotNull(dynamicAllocField, "GuidManager.dynamicAlloc was not found by reflection - has it been renamed?");
            dynamicAllocField.SetValue(null, allocator);

            guidManagerSetUpDone = true;
        }

        private static Weenie MakeVendorWeenie(bool personalVendor)
        {
            var properties = new Dictionary<PropertyBool, bool>();

            if (personalVendor)
                properties[PropertyBool.PersonalVendor] = true;

            return new Weenie
            {
                WeenieClassId = 90300,
                WeenieType = WeenieType.Vendor,
                PropertiesBool = properties,
                PropertiesInt = new Dictionary<PropertyInt, int>
                {
                    { PropertyInt.MerchandiseItemTypes, -1 },
                    { PropertyInt.MerchandiseMinValue, 0 },
                    { PropertyInt.MerchandiseMaxValue, 1000000 },
                },
                PropertiesFloat = new Dictionary<PropertyFloat, double>
                {
                    { PropertyFloat.BuyPrice, 1.0 },
                    { PropertyFloat.SellPrice, 1.0 },
                },
                PropertiesString = new Dictionary<PropertyString, string>
                {
                    { PropertyString.Name, "Test Mule" },
                },
            };
        }

        private static PersonalVendor MakeVendor()
        {
            EnsureVendorConstructible();

            return new PersonalVendor(MakeVendorWeenie(true), new ObjectGuid(NextGuid()));
        }

        /// <summary>
        /// Fix round 2, F3: records every call to the protected TryHandBack seam, so a test can assert
        /// the hand-back attempt genuinely RAN rather than inferring it from side effects (a log line,
        /// with a null player) that look identical whether it ran or not - the failure mode that let
        /// DepositItems_HandsBackOrphanOnRefusal pass with the whole hand-back mechanism deleted.
        /// </summary>
        private sealed class RecordingPersonalVendor : PersonalVendor
        {
            public int TryHandBackCallCount;

            /// <summary>
            /// WHICH items were handed back, not merely how many. A batch disposes of its items
            /// individually, so a count alone cannot tell "the right one came home" from "some other one
            /// did" - and the whole point of the batched throw arm is that it hands back a strict subset.
            /// </summary>
            public readonly List<WorldObject> HandedBack = new List<WorldObject>();

            public RecordingPersonalVendor(Weenie weenie, ObjectGuid guid) : base(weenie, guid) { }

            protected override bool TryHandBack(WorldObject item, Player player)
            {
                TryHandBackCallCount++;
                HandedBack.Add(item);

                return base.TryHandBack(item, player);
            }
        }

        private static RecordingPersonalVendor MakeRecordingVendor()
        {
            EnsureVendorConstructible();

            return new RecordingPersonalVendor(MakeVendorWeenie(true), new ObjectGuid(NextGuid()));
        }

        private static AccountVaultStore MakeStore(out FakeVaultBackend backend, out FakeVaultWorld world)
        {
            backend = new FakeVaultBackend();
            world = new FakeVaultWorld();

            return new AccountVaultStore(OwnerAccount, backend, world);
        }

        /// <summary>Seeds one vault container holding one item, mirroring AccountVaultStoreTests.SeedVault.</summary>
        private static (Container vault, WorldObject item) SeedStoredItem(FakeVaultBackend backend, FakeVaultWorld world, uint wcid = 8000)
        {
            var container = FakeVaultWorld.MakeContainer(255);
            world.Containers[container.Guid.Full] = container;

            backend.Vaults.Add(new ShardAccountVault
            {
                Id = 1,
                AccountId = OwnerAccount,
                ContainerGuid = container.Guid.Full,
                CreatedAt = new DateTime(2026, 1, 1),
            });

            var item = FakeVaultWorld.MakeStack(wcid, 1, 100);
            Assert.IsTrue(container.TryAddToInventory(item), "could not seed a vault item");

            return (container, item);
        }

        private static void SeedLedger(FakeVaultBackend backend, uint wcid, long count)
        {
            backend.Stacks.Add(new AccountVaultStack { Id = (uint)(7000 + backend.Stacks.Count), AccountId = OwnerAccount, Wcid = wcid, Count = count });
        }

        /// <summary>Seeds a NON-stackable weenie, for the ledger rows that collapse on IsPristine rather than on stackability.</summary>
        private static void SeedWorldNonStackableWeenie(uint wcid, string name)
        {
            var weenie = new Weenie
            {
                WeenieClassId = wcid,
                WeenieType = WeenieType.MeleeWeapon,
                PropertiesInt = new Dictionary<PropertyInt, int>
                {
                    { PropertyInt.ItemType, (int)ItemType.MeleeWeapon },
                    { PropertyInt.EncumbranceVal, 100 },
                    { PropertyInt.Value, 50 },
                },
                PropertiesString = new Dictionary<PropertyString, string> { { PropertyString.Name, name } },
            };

            var field = typeof(WorldDatabaseWithEntityCache).GetField("weenieCache", BindingFlags.NonPublic | BindingFlags.Instance);
            var dict = (ConcurrentDictionary<uint, Weenie>)field.GetValue(DatabaseManager.World);
            dict[wcid] = weenie;
        }

        #region Ledger row labelling (repo owner live-test note 2)

        /// <summary>
        /// The reported case, exactly: 295 mana scarabs against a MaxStackSize of 100. The row's stack
        /// graphic is CAPPED at 100 and is therefore not the amount held, so the true count has to
        /// reach the player some other way. It goes in the Name, the one field of a vendor row this
        /// server fully owns and the client renders verbatim.
        ///
        /// Both halves are asserted, because either alone would pass with the other broken: the stack
        /// size must still be the capped 100 (the client cannot render more), AND the name must carry
        /// the 295.
        /// </summary>
        [TestMethod]
        public void RebuildView_LabelsTheTrueCount_WhenTheStackIsCapped()
        {
            EnsureGuidManagerConstructible();
            SeedWorldStackableWeenie(7801, 100);

            var vendor = MakeVendor();
            var store = MakeStore(out var backend, out _);
            vendor.Store = store;

            SeedLedger(backend, 7801, 295);

            vendor.RebuildView();

            var display = vendor.DefaultItemsForSale.Values.Single(d => d.WeenieClassId == 7801);

            Assert.AreEqual(100, display.StackSize, "the rendered stack must stay capped at MaxStackSize");
            StringAssert.Contains(display.Name, "(295 in vault)", "the row must say how many are actually held, since the stack size cannot");
        }

        /// <summary>
        /// A stack WELL UNDER its MaxStackSize is labelled too, even though the panel's own number is
        /// already correct for it. This is the case a narrower rule got wrong in play and it is worth
        /// stating why the redundancy is deliberate: with 8,993 Prismatic Tapers (MaxStackSize 10,000)
        /// and 98 Silver Scarabs (MaxStackSize 100) sitting unlabelled beside a labelled
        /// "Mana Scarab (295 in vault)", the unlabelled rows read as broken. A player cannot tell an
        /// accurate row from a capped one without knowing every item's MaxStackSize, which is the
        /// knowledge the label exists to spare them.
        /// </summary>
        [TestMethod]
        public void RebuildView_LabelsAStackThatIsUnderItsCap()
        {
            EnsureGuidManagerConstructible();
            SeedWorldStackableWeenie(7802, 100);

            var vendor = MakeVendor();
            var store = MakeStore(out var backend, out _);
            vendor.Store = store;

            SeedLedger(backend, 7802, 5);

            vendor.RebuildView();

            var display = vendor.DefaultItemsForSale.Values.Single(d => d.WeenieClassId == 7802);

            Assert.AreEqual(5, display.StackSize, "the rendered stack is accurate here, and must stay so");
            StringAssert.Contains(display.Name, "(5 in vault)", "every stack of more than one carries the label, not only the capped ones");
        }

        /// <summary>
        /// Even ONE of a stackable is labelled, which is the case that separates this rule from the
        /// count-above-one rule that preceded it. VendorShopCreateListStackSize = -1 lets the buy panel
        /// request any amount of a stackable row, so a row holding a single taper still looks willing to
        /// sell ten thousand. The label is the only thing that says otherwise.
        /// </summary>
        [TestMethod]
        public void RebuildView_LabelsASingleStackable()
        {
            EnsureGuidManagerConstructible();
            SeedWorldStackableWeenie(7806, 100);

            var vendor = MakeVendor();
            var store = MakeStore(out var backend, out _);
            vendor.Store = store;

            SeedLedger(backend, 7806, 1);

            vendor.RebuildView();

            var display = vendor.DefaultItemsForSale.Values.Single(d => d.WeenieClassId == 7806);

            StringAssert.Contains(display.Name, "(1 in vault)", "a stackable row can be asked for any amount, so even one must say how many there are");
        }

        /// <summary>
        /// A non-stackable is NOT automatically quiet, and assuming it is would be the easy wrong fix
        /// here. AccountVaultStore's stack-ledger branch collapses on IsPristine, not on stackability, so
        /// several identical plain weapons become ONE ledger row with a count above 1 - which the client
        /// still draws as a single item, because SetStackSize is skipped for a weenie with no
        /// MaxStackSize. That row is exactly the ambiguous case the label exists for.
        /// </summary>
        [TestMethod]
        public void RebuildView_LabelsANonStackableHeldMoreThanOnce()
        {
            EnsureGuidManagerConstructible();
            SeedWorldNonStackableWeenie(7803, "Plain Dagger");

            var vendor = MakeVendor();
            var store = MakeStore(out var backend, out _);
            vendor.Store = store;

            SeedLedger(backend, 7803, 3);

            vendor.RebuildView();

            var display = vendor.DefaultItemsForSale.Values.Single(d => d.WeenieClassId == 7803);

            Assert.IsTrue((display.MaxStackSize ?? 0) == 0, "sanity: this weenie must be non-stackable, or the test is not covering the case it names");
            StringAssert.Contains(display.Name, "(3 in vault)");
        }

        /// <summary>
        /// The single non-stackable, which is the case the unconditional first draft got wrong.
        /// </summary>
        [TestMethod]
        public void RebuildView_DoesNotLabelASingleNonStackable()
        {
            EnsureGuidManagerConstructible();
            SeedWorldNonStackableWeenie(7804, "Lone Dagger");

            var vendor = MakeVendor();
            var store = MakeStore(out var backend, out _);
            vendor.Store = store;

            SeedLedger(backend, 7804, 1);

            vendor.RebuildView();

            var display = vendor.DefaultItemsForSale.Values.Single(d => d.WeenieClassId == 7804);

            Assert.IsFalse(display.Name.Contains("in vault"), $"one of a non-stackable is not ambiguous; got '{display.Name}'");
        }

        /// <summary>
        /// The label must never ACCUMULATE across rebuilds. RebuildView is called on every approach and
        /// after every transaction; if it appended to a display object it kept, a second pass would give
        /// "Ledger Item 7805 (295 in vault) (295 in vault)" and a tenth would be unreadable. What makes
        /// it safe is that RebuildView applies the suffix only when it BUILDS a display object, and a
        /// kept object (unchanged count) is never re-labelled - a changed count replaces the object
        /// instead. This test pins that property rather than the current implementation of it; the
        /// forced-rebuild case, which actually reaches the keep path rather than the cheap exit, is
        /// RebuildView_ForcedRebuildOfAnUnchangedStore_KeepsEveryGuidAndTheSortOrder.
        ///
        /// Note what this test deliberately does NOT do: drive the count DOWN by poking
        /// backend.Stacks between the two rebuilds. AccountVaultStore holds the ledger in memory once
        /// loaded and GetEntries reads that, not the backend, so a backend poke changes nothing and the
        /// test would be asserting against a mutation that never happened (it was written that way
        /// first, and failed with "actual: Ledger Item 7805 (295 in vault)"). The tracks-down behaviour
        /// is real - a withdrawal updates the in-memory ledger, which is what the next rebuild reads -
        /// and it is covered by the live row in Docs/VERIFY-QUEUE.md rather than faked here.
        /// </summary>
        [TestMethod]
        public void RebuildView_LabelNeverAccumulatesAcrossRebuilds()
        {
            EnsureGuidManagerConstructible();
            SeedWorldStackableWeenie(7805, 100);

            var vendor = MakeVendor();
            var store = MakeStore(out var backend, out _);
            vendor.Store = store;

            SeedLedger(backend, 7805, 295);

            vendor.RebuildView();
            vendor.RebuildView();
            vendor.RebuildView();

            var display = vendor.DefaultItemsForSale.Values.Single(d => d.WeenieClassId == 7805);
            var occurrences = display.Name.Split(new[] { "in vault" }, StringSplitOptions.None).Length - 1;

            Assert.AreEqual(1, occurrences, $"three rebuilds must leave exactly one label; got '{display.Name}'");
            StringAssert.Contains(display.Name, "(295 in vault)");
        }

        /// <summary>
        /// The ledger-branch half of the count-suffix sort bug, driven through the REAL
        /// RebuildView -> WorldObjectFactory.CreateNewWorldObject path rather than by calling
        /// PersonalVendor.RegisterSortName directly - unlike this test, that seam test cannot catch a
        /// regression that deletes the production call to RegisterSortName, since it would populate
        /// sortNames itself regardless of whether RebuildView still does.
        ///
        /// Two ledger rows CAN share a base name: nothing stops two different wcids' weenies from
        /// carrying the identical PropertyString.Name (a plain "different item, same label" content
        /// mistake, or two variants of one design meant to look identical in a vendor list), so this
        /// seeds exactly that with SeedWorldStackableWeenieWithWorkmanship. The counts are chosen so
        /// the count suffix and Workmanship DISAGREE on purpose: ordinally "(12 in vault)" sorts
        /// BEFORE "(3 in vault)" ('1' &lt; '3'), so a Name-based sort puts the LOWER-workmanship wcid
        /// (2.0) first - the exact regression this test exists to catch. The fix must instead order by
        /// Workmanship descending (9.0 before 2.0).
        /// </summary>
        [TestMethod]
        public void RebuildView_LedgerRowsSharingABaseName_SortByWorkmanshipNotCountSuffix()
        {
            EnsureGuidManagerConstructible();

            const string sharedName = "Ledger Twin";
            SeedWorldStackableWeenieWithWorkmanship(9001, sharedName, itemWorkmanship: 90, numItemsInMaterial: 10); // Workmanship 9.0
            SeedWorldStackableWeenieWithWorkmanship(9002, sharedName, itemWorkmanship: 20, numItemsInMaterial: 10); // Workmanship 2.0

            var vendor = MakeVendor();
            var store = MakeStore(out var backend, out _);
            vendor.Store = store;

            SeedLedger(backend, 9001, 3);
            SeedLedger(backend, 9002, 12);

            vendor.RebuildView();

            var highWork = vendor.DefaultItemsForSale.Values.Single(d => d.WeenieClassId == 9001);
            var lowWork = vendor.DefaultItemsForSale.Values.Single(d => d.WeenieClassId == 9002);

            StringAssert.Contains(highWork.Name, "(3 in vault)", "sanity: wcid 9001's ledger row must carry its own count");
            StringAssert.Contains(lowWork.Name, "(12 in vault)", "sanity: wcid 9002's ledger row must carry its own count");

            var actual = new List<WorldObject>();
            vendor.forEachItem(actual.Add);

            Assert.IsTrue(actual.IndexOf(highWork) < actual.IndexOf(lowWork),
                "forEachItem must order two ledger rows sharing a base name by Workmanship descending (9.0 before 2.0), not by the ordinal text of their count suffix - '(12 in vault)' sorting before '(3 in vault)' is the trap this asserts against.");
        }

        #endregion

        #region View caching (F7)

        /// <summary>
        /// F7, half one. RebuildView runs on EVERY approach, and the rejected-buy path re-approaches
        /// with no IsBusy check and no cooldown, so a loop of deliberately invalid buy profiles is a
        /// free rebuild treadmill. Every display object a rebuild materializes costs a dynamic guid,
        /// and GuidManager holds a recycled dynamic guid for 360 minutes before it can be reissued,
        /// so the treadmill grows server memory monotonically.
        ///
        /// Both halves are asserted, and the second is the one that matters: a cache that never
        /// invalidates would satisfy the first assertion perfectly while showing a player a panel that
        /// no longer matches their vault, which is worse than the bug being fixed. The sanity check on
        /// the seeded row count is there because an empty view would make both assertions vacuously
        /// true - an empty list equals an empty list, and the deposit is what would have to fail
        /// silently for that to happen.
        /// </summary>
        [TestMethod]
        public void RebuildView_IsANoOpWhenTheStoreHasNotChanged()
        {
            EnsureGuidManagerConstructible();
            SeedWorldStackableWeenie(7810, 100);
            SeedWorldStackableWeenie(7811, 100);
            SeedWorldStackableWeenie(7812, 100);

            var vendor = MakeVendor();
            var store = MakeStore(out var backend, out var world);
            vendor.Store = store;

            SeedLedger(backend, 7810, 12);
            SeedLedger(backend, 7811, 34);

            // Pristine, so the deposit below collapses into a THIRD ledger row and therefore a third
            // display object - the panel change this test needs the cache to notice.
            world.PristineResult = true;

            var anotherItem = FakeVaultWorld.MakeStack(7812, 5, 100);

            vendor.RebuildView();
            var firstGuids = vendor.DefaultItemsForSale.Keys.ToList();

            Assert.AreEqual(2, firstGuids.Count, "sanity: the two seeded ledger rows must produce two display objects, or neither assertion below means anything");

            vendor.RebuildView();
            var secondGuids = vendor.DefaultItemsForSale.Keys.ToList();

            CollectionAssert.AreEqual(firstGuids, secondGuids,
                "an unchanged store must not re-materialize its display objects. Each one costs a dynamic guid, and a recycled dynamic guid is not reissuable for 360 minutes - so a client looping approaches against a full vault grows server memory monotonically.");

            var deposited = false;
            string depositFailReason = null;

            store.Enqueue(() => deposited = store.TryDeposit(anotherItem, Owner, out depositFailReason));

            Assert.IsTrue(deposited, $"the deposit that is supposed to change the view did not happen: {depositFailReason}");

            vendor.RebuildView();
            var thirdGuids = vendor.DefaultItemsForSale.Keys.ToList();

            CollectionAssert.AreNotEqual(firstGuids, thirdGuids, "a changed store MUST rebuild - the cache must not go stale.");
        }

        #endregion

        [TestMethod]
        public void Factory_WithoutTheOptInBool_BuildsAPlainVendor()
        {
            EnsureVendorConstructible();

            var wo = WorldObjectFactory.CreateWorldObject(MakeVendorWeenie(false), new ObjectGuid(NextGuid()));

            Assert.IsInstanceOfType(wo, typeof(Vendor));
            Assert.IsFalse(wo is PersonalVendor, "no PropertyBool.PersonalVendor opt-in must still build a plain Vendor");
        }

        [TestMethod]
        public void Factory_WithTheOptInBool_BuildsAPersonalVendor()
        {
            EnsureVendorConstructible();

            var wo = WorldObjectFactory.CreateWorldObject(MakeVendorWeenie(true), new ObjectGuid(NextGuid()));

            Assert.IsInstanceOfType(wo, typeof(PersonalVendor));
        }

        [TestMethod]
        public void SellCost_IsZero()
        {
            var vendor = MakeVendor();
            var item = FakeVaultWorld.MakeStack(8000, 1, 100);
            item.Value = 500;

            Assert.AreEqual(0u, vendor.GetSellCost(item));
            Assert.AreEqual(0u, vendor.GetSellCost(MakeVendorWeenie(false)));
        }

        [TestMethod]
        public void BuyCost_IsZero()
        {
            var vendor = MakeVendor();
            var item = FakeVaultWorld.MakeStack(8000, 1, 100);
            item.Value = 500;

            Assert.AreEqual(0, vendor.GetBuyCost(item));
            Assert.AreEqual(0, vendor.GetBuyCost(MakeVendorWeenie(false)));
        }

        [TestMethod]
        public void PayoutCoinAmount_IsZero()
        {
            var vendor = MakeVendor();
            var item = FakeVaultWorld.MakeStack(8000, 1, 100);
            item.Value = 500;

            var items = new Dictionary<uint, WorldObject> { { item.Guid.Full, item } };

            Assert.AreEqual(0, vendor.CalculatePayoutCoinAmount(items));
        }

        [TestMethod]
        public void RotUniques_DoesNothing()
        {
            var vendor = MakeVendor();

            // Seed UniqueItemsForSale directly (a PersonalVendor never populates it itself - see
            // ProcessItemsForPurchase_NeverPopulatesUniqueItemsForSale) with an item that LOOKS rottable,
            // so an inert override is being proven rather than assumed from an empty dictionary.
            var uniqueItem = FakeVaultWorld.MakeStack(8000, 1, 100);
            uniqueItem.SoldTimestamp = Time.GetUnixTime() - 10_000;
            vendor.UniqueItemsForSale.Add(uniqueItem.Guid, uniqueItem);

            var rotUniques = typeof(Vendor).GetMethod("RotUniques", BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.IsNotNull(rotUniques);
            rotUniques.Invoke(vendor, null);

            Assert.IsTrue(vendor.UniqueItemsForSale.ContainsKey(uniqueItem.Guid), "RotUniques must never remove anything here");
            Assert.IsFalse(uniqueItem.IsDestroyed);
        }

        [TestMethod]
        public void ProcessItemsForPurchase_DestroysNothing()
        {
            var vendor = MakeVendor();
            var store = MakeStore(out var backend, out var world);
            vendor.Store = store;

            world.PristineResult = false;  // keep the biota - the vault path, not the ledger-collapse path

            var item = FakeVaultWorld.MakeStack(8000, 1, 100);
            var items = new Dictionary<uint, WorldObject> { { item.Guid.Full, item } };

            vendor.DepositItems(Owner, items, null);

            Assert.IsFalse(item.IsDestroyed);
            CollectionAssert.DoesNotContain(world.Destroyed, item);

            // M6: a positive assertion that the item actually reached the vault, not merely that
            // nothing bad happened to it - the three ProcessItemsForPurchase_* tests previously all
            // passed against an empty DepositItems body.
            var entries = store.GetEntries(0, -1);
            Assert.IsTrue(entries.Any(e => e.Kind == VaultEntryKind.StoredItem && e.Guid == item.Guid), "the item must have actually reached the vault");
        }

        [TestMethod]
        public void ProcessItemsForPurchase_RemovesNoBiotaFromDatabase()
        {
            var vendor = MakeVendor();
            var store = MakeStore(out var backend, out var world);
            vendor.Store = store;

            world.PristineResult = false;

            var item = FakeVaultWorld.MakeStack(8000, 1, 100);

            // Simulate a previously-persisted item without touching any real database: enqueueSave:
            // false runs SaveBiotaToDatabase's bookkeeping (stamps LastRequestedDatabaseSave) but skips
            // the actual DatabaseManager.Shard.SaveBiota call. RemoveBiotaFromDatabase resets
            // LastRequestedDatabaseSave to DateTime.MinValue as its very first act
            // (WorldObject_Database.cs:93) before it would go on to call DatabaseManager.Shard.RemoveBiota
            // - so an unchanged, non-MinValue value here is proof RemoveBiotaFromDatabase was never
            // called, without ever having to let a real removal call fire.
            item.SaveBiotaToDatabase(enqueueSave: false);
            var stampedSave = item.LastRequestedDatabaseSave;
            Assert.AreNotEqual(DateTime.MinValue, stampedSave);

            var items = new Dictionary<uint, WorldObject> { { item.Guid.Full, item } };

            vendor.DepositItems(Owner, items, null);

            Assert.AreEqual(stampedSave, item.LastRequestedDatabaseSave, "ProcessItemsForPurchase must never call RemoveBiotaFromDatabase");

            var entries = store.GetEntries(0, -1);
            Assert.IsTrue(entries.Any(e => e.Kind == VaultEntryKind.StoredItem && e.Guid == item.Guid), "the item must have actually reached the vault");
        }

        [TestMethod]
        public void ProcessItemsForPurchase_NeverPopulatesUniqueItemsForSale()
        {
            var vendor = MakeVendor();
            var store = MakeStore(out var backend, out var world);
            vendor.Store = store;

            world.PristineResult = false;

            var item = FakeVaultWorld.MakeStack(8000, 1, 100);
            var items = new Dictionary<uint, WorldObject> { { item.Guid.Full, item } };

            vendor.DepositItems(Owner, items, null);

            Assert.AreEqual(0, vendor.UniqueItemsForSale.Count);

            var entries = store.GetEntries(0, -1);
            Assert.IsTrue(entries.Any(e => e.Kind == VaultEntryKind.StoredItem && e.Guid == item.Guid), "the item must have actually reached the vault");
        }

        [TestMethod]
        public void StoredBiotas_AreNotInEitherVendorDictionary()
        {
            var vendor = MakeVendor();
            var store = MakeStore(out var backend, out var world);
            vendor.Store = store;

            var (_, item) = SeedStoredItem(backend, world);

            vendor.RebuildView();

            Assert.IsTrue(vendor.TryGetItemForSale(item.Guid, out var found), "the stored item must be reachable through the vendor view");
            Assert.AreSame(item, found);

            Assert.IsFalse(vendor.DefaultItemsForSale.ContainsKey(item.Guid));
            Assert.IsFalse(vendor.UniqueItemsForSale.ContainsKey(item.Guid));
        }

        [TestMethod]
        public void Destroy_LeavesEveryStoredItemAlive()
        {
            var vendor = MakeVendor();
            var store = MakeStore(out var backend, out var world);
            vendor.Store = store;

            var (_, item) = SeedStoredItem(backend, world);

            vendor.RebuildView();
            Assert.IsTrue(vendor.TryGetItemForSale(item.Guid, out _), "sanity: the item must be in view before Destroy() is exercised");

            vendor.Destroy();

            Assert.IsFalse(item.IsDestroyed, "WorldObject.Destroy's vendor branch must never reach a stored biota");
        }

        [TestMethod]
        public void Destroy_DoesDestroyLedgerMaterializations()
        {
            var vendor = MakeVendor();

            // A ledger display object, added the same way AddDefaultItem always would be - its guid is
            // NOT one of the store's own biotas, so the redundant guard does not interfere.
            var display = FakeVaultWorld.MakeStack(9999, 1, 100);
            vendor.AddDefaultItem(display);
            Assert.IsTrue(vendor.DefaultItemsForSale.ContainsKey(display.Guid), "sanity: AddDefaultItem must have accepted it");

            vendor.Destroy();

            Assert.IsTrue(display.IsDestroyed, "a disposable ledger display object IS meant to be destroyed by WorldObject.Destroy's vendor branch");
        }

        [TestMethod]
        public void AddDefaultItem_RefusesAStoreBiota()
        {
            var vendor = MakeVendor();
            var store = MakeStore(out var backend, out var world);
            vendor.Store = store;

            var (_, item) = SeedStoredItem(backend, world);
            vendor.RebuildView();

            Assert.IsTrue(vendor.TryGetItemForSale(item.Guid, out _), "sanity: the item must already be a store biota");

            vendor.AddDefaultItem(item);

            Assert.IsFalse(vendor.DefaultItemsForSale.ContainsKey(item.Guid), "a store biota must never be accepted into DefaultItemsForSale");
        }

        /// <summary>
        /// Code review finding 1. A stored item of a hidden ItemType lives only as a VALUE in
        /// proxiedItems, keyed by its PROXY's guid and never its own, so the guard's original
        /// storeItems.ContainsKey(item.Guid) test was false for every one of them - the guard silently
        /// stopped covering exactly the items this change introduced. No production caller passes a
        /// vault biota to AddDefaultItem today, so this was a hole in the defence rather than a live
        /// bug, but a guard whose only job is to be loud must not go quiet.
        ///
        /// The wcid is deliberately NON-STACKABLE, and the first draft of this test was wrong for
        /// exactly that reason. base.AddDefaultItem (Vendor.cs:187-208) first looks for an existing
        /// default item of the same wcid with room left in its stack and, finding one, bumps that
        /// stack and RETURNS - it only reaches `DefaultItemsForSale.Add(item.Guid, item)` when no such
        /// room exists. With a stackable wcid the proxy RebuildView just created is always that
        /// stack, so the real item was absorbed into it and never keyed by its own guid: the
        /// assertion below held whether the guard ran or not, and the test passed with the fix
        /// reverted. Proven by reverting it and watching this test go green. A non-stackable proxy has
        /// MaxStackSize null, so `(StackSize ?? 1) &lt; (MaxStackSize ?? 1)` is `1 &lt; 1` - no room, no
        /// merge, and control reaches the Add this guard exists to prevent.
        /// </summary>
        [TestMethod]
        public void AddDefaultItem_RefusesAProxiedStoreBiota()
        {
            EnsureGuidManagerConstructible();
            SeedWorldNonStackableWeenie(7907, "Proxied Non-Stackable");
            SetSeededWeenieItemType(7907, ItemType.TinkeringMaterial);

            var vendor = MakeVendor();
            var store = MakeStore(out var backend, out var world);
            vendor.Store = store;

            var (_, item) = SeedStoredItem(backend, world, 7907);
            item.ItemType = ItemType.TinkeringMaterial;

            vendor.RebuildView();

            var proxy = vendor.DefaultItemsForSale.Values.Single();
            Assert.IsNull(proxy.MaxStackSize, "sanity: the proxy must be non-stackable, or base.AddDefaultItem merges instead of adding and this test cannot fail");
            Assert.AreNotEqual(item.Guid, proxy.Guid, "sanity: the row is the proxy, not the stored item");

            vendor.AddDefaultItem(item);

            Assert.IsFalse(vendor.DefaultItemsForSale.ContainsKey(item.Guid), "a proxied store biota must never be accepted into DefaultItemsForSale either - Destroy walks that dictionary");
            Assert.AreEqual(1, vendor.DefaultItemsForSale.Count, "the refused item must not have been added under any key");
        }

        [TestMethod]
        public void CheckUseRequirements_RefusesAnUngrantedPlayer()
        {
            var vendor = MakeVendor();
            var store = MakeStore(out _, out _);
            vendor.Store = store;

            // M7: stated precondition, so the refusal below is provably "no grant", not "store never
            // finished loading" - both would otherwise return a non-empty failReason and look the same.
            Assert.IsTrue(store.IsLoaded);

            var authorized = vendor.TryAuthorize(Stranger, out var access, out var failReason);

            Assert.IsFalse(authorized);
            Assert.AreEqual(VaultAccess.None, access);
            Assert.AreEqual("You do not have permission to use this vault.", failReason);
        }

        [TestMethod]
        public void CheckUseRequirements_AllowsADepositOnlyGrantee()
        {
            var vendor = MakeVendor();
            var store = MakeStore(out var backend, out _);
            vendor.Store = store;

            backend.Grants.Add(new AccountVaultGrant
            {
                Id = 1,
                OwnerAccountId = OwnerAccount,
                GranteeCharacterGuid = GranteeCharacter,
                GranteeCharacterName = "Grantee",
                CanWithdraw = false,
                GrantedAt = DateTime.UtcNow,
            });

            var authorized = vendor.TryAuthorize(DepositOnlyGrantee, out var access, out _);

            Assert.IsTrue(authorized, "gate 1 is UX-only - a deposit-only grantee IS authorized to open the panel");
            Assert.AreEqual(VaultAccess.Deposit, access);
        }

        /// <summary>
        /// Common basket + seeded stored item for the I4 gate-2 tests below, so refusal/allow is
        /// asserted against something that would actually MOVE if the access check were not there -
        /// fix round 1, I4: the previous versions of these three tests passed `itemProfiles: null` and
        /// were satisfied by the null-list guard at TryWithdrawTransaction's line 284, never reaching
        /// the access check at all. Deleting the entire access block left all three still passing.
        /// </summary>
        private static (PersonalVendor vendor, AccountVaultStore store, FakeVaultBackend backend, FakeVaultWorld world, WorldObject item, List<ItemProfile> basket) MakeGate2Scenario()
        {
            var vendor = MakeVendor();
            var store = MakeStore(out var backend, out var world);
            vendor.Store = store;

            var (_, item) = SeedStoredItem(backend, world);
            vendor.RebuildView();

            var basket = new List<ItemProfile> { new ItemProfile(1, item.Guid.Full) };

            return (vendor, store, backend, world, item, basket);
        }

        [TestMethod]
        public void BuyItems_ValidateTransaction_RefusesADepositOnlyGrantee()
        {
            var (vendor, store, backend, _, item, basket) = MakeGate2Scenario();

            backend.Grants.Add(new AccountVaultGrant
            {
                Id = 1,
                OwnerAccountId = OwnerAccount,
                GranteeCharacterGuid = GranteeCharacter,
                GranteeCharacterName = "Grantee",
                CanWithdraw = false,
                GrantedAt = DateTime.UtcNow,
            });

            var ok = vendor.TryWithdrawTransaction(basket, DepositOnlyGrantee, null);

            Assert.IsFalse(ok, "gate 2 requires DepositWithdraw specifically - Deposit-only access must never authorize a withdrawal");
            Assert.IsTrue(vendor.TryGetItemForSale(item.Guid, out _), "the item must not have moved");
            CollectionAssert.Contains(store.GetEntries(0, -1).Select(e => (object)e.Guid).ToList(), item.Guid);
        }

        [TestMethod]
        public void BuyItems_ValidateTransaction_RefusesAnUngrantedPlayer()
        {
            var (vendor, store, _, _, item, basket) = MakeGate2Scenario();

            var ok = vendor.TryWithdrawTransaction(basket, Stranger, null);

            Assert.IsFalse(ok);
            Assert.IsTrue(vendor.TryGetItemForSale(item.Guid, out _), "the item must not have moved");
        }

        [TestMethod]
        public void BuyItems_ValidateTransaction_ChecksAuthorizationEvenWhenThePanelWasNeverOpened()
        {
            // Deliberately does NOT call ApproachVendor, CheckUseRequirements, TryAuthorize or
            // TryPrepareApproach at any point before this. Gate 2 must re-resolve authorization
            // entirely on its own - not because it is the only enforcement point (fix round 2: it is
            // defence in depth over AccountVaultStore's own unconditional access check inside
            // TryWithdraw, see TryWithdrawTransaction's remarks), but because both HandleActionBuyItem
            // and HandleActionSellItem resolve the vendor purely by guid off the landblock and never
            // read back player.LastOpenedContainerId, so this vendor-level gate must not assume a prior
            // panel-open either.
            var (vendor, store, _, _, item, basket) = MakeGate2Scenario();

            var ok = vendor.TryWithdrawTransaction(basket, Stranger, null);

            Assert.IsFalse(ok, "authorization must be checked inside the transaction itself, never assumed from a prior panel-open");
            Assert.IsTrue(vendor.TryGetItemForSale(item.Guid, out _), "the item must not have moved");
        }

        /// <summary>
        /// I4's positive control: the SAME basket and mechanism as the three refusal tests above, but
        /// with an actor that IS authorized, proving those tests fail for the right reason (the access
        /// gate) and not for some unrelated reason that would make EVERY actor fail regardless of
        /// authorization. Needs M3's null-player capacity-check-is-SKIPPED fix: with a null player and
        /// an authorized actor this reaches real delivery, where TryCreateInInventoryWithNetworking has
        /// no player to place the item in and the item is handed back to the vault instead - so `ok`
        /// is still true (the AUTHORIZATION succeeded) and the item ends back in the vault rather than
        /// vanishing, which is exactly the fix round 1 I3/M3 contract.
        /// </summary>
        [TestMethod]
        public void BuyItems_ValidateTransaction_PositiveControl_AuthorizedActorIsNotRefusedAtAccessGate()
        {
            var (vendor, store, _, _, item, basket) = MakeGate2Scenario();

            var ok = vendor.TryWithdrawTransaction(basket, Owner, null);

            Assert.IsTrue(ok, "an authorized actor with an otherwise-valid basket must not be refused at the access gate");
        }

        /// <summary>
        /// I7: a player's requested partial amount against a stored (non-ledger) biota must be refused,
        /// not silently rounded up to the whole stack. VaultEntry.ForItem always sets Count to the
        /// item's own StackSize, so passing that value straight through as "amount" made the store's
        /// own amount != entry.Count guard equal by construction and unable to ever fire.
        /// </summary>
        [TestMethod]
        public void StoredWithdrawal_RefusesAPartialAmount()
        {
            var vendor = MakeVendor();
            var store = MakeStore(out var backend, out var world);
            vendor.Store = store;

            var (_, item) = SeedStoredItem(backend, world);
            item.SetStackSize(100);
            vendor.RebuildView();

            var basket = new List<ItemProfile> { new ItemProfile(1, item.Guid.Full) };  // asking for 1 of 100

            var ok = vendor.TryWithdrawTransaction(basket, Owner, null);

            Assert.IsFalse(ok, "a stored item must be refused rather than withdrawn as a partial amount");
            Assert.IsTrue(vendor.TryGetItemForSale(item.Guid, out var found), "the item must still be in the vault, untouched");
            Assert.AreEqual(100, found.StackSize);
        }

        /// <summary>
        /// I2: TryDeposit's vault-full refusal is reachable in ordinary play (any player at the entry
        /// cap who sells one more item). By the time ProcessItemsForPurchase/DepositItems runs, the
        /// item is already detached from the player and already flushed - so a refusal here must hand
        /// the item back rather than leaving a detached, already-persisted orphan row.
        ///
        /// Fix round 2, F3: with a null player, HandBackOrphan's only observable side effect used to be
        /// a log line - which the PRE-FIX code (no hand-back at all) also produced by a different path,
        /// so an earlier version of this test asserting only "not destroyed" and "refused" passed with
        /// the hand-back mechanism entirely deleted. This now drives a RecordingPersonalVendor and
        /// asserts TryHandBack was actually invoked, which is the mechanism itself, not a side effect
        /// of it. Verified to FAIL before the fix: commenting out the `TryHandBackCallCount++;`
        /// increment (equivalent to the call never happening) drops the assertion below to
        /// `Failed: 1, Passed: 0` - see task-7-report.md for the exact run.
        /// </summary>
        [TestMethod]
        public void DepositItems_HandsBackOrphanOnRefusal()
        {
            // Reset to the shared default in a finally: CachedLongSettings is process-static and this
            // deliberately lowers it to 1, which would otherwise leak into any other test in this run
            // that assumes the default 500 seeded by EnsureVendorConstructible, regardless of MSTest's
            // (unspecified) method ordering within the class.
            // Run the one-time seed BEFORE lowering the cap: the first vendor build in a process seeds
            // account_vault_entry_cap to 500, which would otherwise overwrite the 1 set below (this
            // test failed when run alone).
            EnsureVendorConstructible();

            try
            {
                Assert.IsTrue(PropertyManager.ModifyLong("account_vault_entry_cap", 1),
                    "account_vault_entry_cap is missing from DefaultLongProperties");

                var vendor = MakeRecordingVendor();
                var store = MakeStore(out var backend, out var world);
                vendor.Store = store;

                SeedLedger(backend, 7777, 1);  // one entry already occupies the cap of 1

                world.PristineResult = false;

                var item = FakeVaultWorld.MakeStack(8000, 1, 100);
                var items = new Dictionary<uint, WorldObject> { { item.Guid.Full, item } };

                // player: null, per the coordinator's note that DepositItems(VaultActor, Dictionary,
                // Player) taking a nullable Player is the seam that needed exercising here. With no
                // player to hand it back to, HandBackOrphan logs an ORPHAN line - but the item itself is
                // never destroyed and never removed from the database, which is the actual invariant.
                vendor.DepositItems(Owner, items, null);

                Assert.AreEqual(1, vendor.TryHandBackCallCount, "the hand-back attempt must actually run for a refused deposit, not merely produce a log line that looks the same whether it ran or not");
                Assert.IsFalse(item.IsDestroyed, "a refused deposit must never destroy the item");
                CollectionAssert.DoesNotContain(world.Destroyed, item);
                Assert.IsFalse(store.GetEntries(0, -1).Any(e => e.Kind == VaultEntryKind.StoredItem && e.Guid == item.Guid), "sanity: the deposit really was refused, not silently accepted");
            }
            finally
            {
                PropertyManager.ModifyLong("account_vault_entry_cap", 500);
            }
        }

        /// <summary>
        /// IsPristine runs ONCE per deposited item across the whole sell path, not twice.
        ///
        /// It is the most expensive call on that path - VaultCollapse.IsPristine constructs a reference
        /// weenie, computes its object description and destroys it - and the sell path used to pay for
        /// it twice per item: once in this pre-flight, through AccountVaultStore.WouldAddEntry, and
        /// again inside TryDeposit, which needs the answer for real. A 512-item sale paid 1,024.
        ///
        /// This has to be driven through the pre-flight AND the deposit, not through TryDeposit alone,
        /// because the second call is the one that lives in the pre-flight: a test that only deposited
        /// would have counted one call before the change as well and proved nothing.
        ///
        /// The pre-flight's cap question is answered by AccountVaultStore.HasObviousHeadroom, which
        /// compares the counts it already has. That is a COST gate, never a replacement rule - so the
        /// at-the-cap case below asserts the full check still runs, because that is the only thing that
        /// can tell a deposit which opens a new row from one that joins a row already drawn.
        /// </summary>
        [TestMethod]
        public void Deposit_IsPristineCallCount_IsExactlyOnePerItem()
        {
            // Restored in a finally for the same reason DepositItems_HandsBackOrphanOnRefusal does it:
            // CachedLongSettings is process-static and the at-the-cap half below lowers it to 1.
            try
            {
                var vendor = MakeVendor();
                var store = MakeStore(out var backend, out var world);
                vendor.Store = store;

                var (vault, _) = SeedStoredItem(backend, world);

                // Not pristine, so every deposit takes the vault-container path rather than the ledger.
                world.PristineResult = false;

                Assert.IsTrue(store.TryGetAccess(Owner, out var access, out var accessReason), accessReason);

                const int itemCount = 4;

                var items = new Dictionary<uint, WorldObject>();

                for (var i = 0; i < itemCount; i++)
                {
                    var item = FakeVaultWorld.MakeStack(8000, 1, 100);
                    items[item.Guid.Full] = item;
                }

                world.ResetIsPristineCalls();

                // The pre-flight exactly as VerifySellItems runs it: once per profile, with the vault
                // access resolved once outside the loop (see
                // CallSite_VerifySellItems_ResolvesVaultAccessOnceOutsideTheProfileLoop).
                foreach (var item in items.Values)
                    Assert.IsTrue(vendor.CanAcceptCore(item, Owner, access, out var reason), reason);

                Assert.AreEqual(0, world.IsPristineCalls,
                    "well below the cap the pre-flight has no capacity question that IsPristine's answer could change, so it must not construct a reference weenie at all");

                vendor.DepositItems(Owner, items, null);

                Assert.AreEqual(1 + itemCount, vault.Inventory.Count, "sanity: every item really was deposited");
                Assert.AreEqual(itemCount, world.IsPristineCalls,
                    "IsPristine must run exactly once per item over the whole pre-flight plus deposit path");

                // At the cap the shortcut must not fire, or the cap would start refusing deposits that
                // only join a row the panel already draws (DESIGN 7.3).
                Assert.IsTrue(PropertyManager.ModifyLong("account_vault_entry_cap", 1),
                    "account_vault_entry_cap is missing from DefaultLongProperties");

                world.ResetIsPristineCalls();

                vendor.CanAcceptCore(FakeVaultWorld.MakeStack(8000, 1, 100), Owner, access, out _);

                Assert.AreEqual(1, world.IsPristineCalls,
                    "at the cap the pre-flight must still run the full WouldAddEntry check");
            }
            finally
            {
                PropertyManager.ModifyLong("account_vault_entry_cap", 500);
            }
        }

        /// <summary>
        /// I3 (plus the coordinator's exception-safety amendment): a multi-item withdrawal where a
        /// LATER entry fails must not abandon items already withdrawn from EARLIER entries in the same
        /// basket. Basket: one stored item (withdraws fine) plus one ledger row requested for more than
        /// it holds (fails). The stored item must end up BACK in the vault, not leaked in memory with
        /// its vault-side row already gone.
        /// </summary>
        [TestMethod]
        public void WithdrawTransaction_PartialFailureReturnsEverythingAlreadyWithdrawn()
        {
            // RebuildView materializes the ledger row through the REAL WorldObjectFactory (see class
            // remarks, point 3), which needs both a resolvable weenie for wcid 7778 and a working
            // GuidManager.
            EnsureGuidManagerConstructible();
            SeedWorldStackableWeenie(7778, 100);

            var vendor = MakeVendor();
            var store = MakeStore(out var backend, out var world);
            vendor.Store = store;

            var (_, storedItem) = SeedStoredItem(backend, world, wcid: 8001);
            SeedLedger(backend, 7778, 5);  // only 5 held

            vendor.RebuildView();

            Assert.IsTrue(vendor.TryGetItemForSale(storedItem.Guid, out _), "sanity: stored item must be in view");
            Assert.IsTrue(vendor.DefaultItemsForSale.Values.Any(d => d.WeenieClassId == 7778), "sanity: ledger row must be materialized in view");

            var ledgerDisplayGuid = vendor.DefaultItemsForSale.Values.First(d => d.WeenieClassId == 7778).Guid;

            var basket = new List<ItemProfile>
            {
                new ItemProfile(1, storedItem.Guid.Full),
                new ItemProfile(50, ledgerDisplayGuid.Full),  // asking for 50 of a row that holds 5 - fails
            };

            var ok = vendor.TryWithdrawTransaction(basket, Owner, null);

            Assert.IsFalse(ok, "the basket must fail overall");

            // The stored item must be back in the vault - not lost, not left withdrawn-but-undelivered.
            var entries = store.GetEntries(0, -1);
            Assert.IsTrue(entries.Any(e => e.Kind == VaultEntryKind.StoredItem && e.Guid == storedItem.Guid), "the stored item withdrawn earlier in the basket must have been returned to the vault after the later entry failed");

            // And the ledger row must be untouched - the failed adjust must not have moved it.
            var ledgerEntry = entries.FirstOrDefault(e => e.Kind == VaultEntryKind.Ledger && e.Wcid == 7778);
            Assert.IsNotNull(ledgerEntry);
            Assert.AreEqual(5L, ledgerEntry.Count);
        }

        /// <summary>
        /// F5, half one: a basket naming the SAME ledger display row twice, with the two amounts
        /// summing to more than the row holds, is the supply of mid-basket failures the cap-bypass
        /// exploit needs. Left unguarded it withdraws the first amount, has the second refused by the
        /// ledger's non-negative guard, and then unwinds the first - which is the path that converts
        /// one ledger entry into an uncapped stored-biota entry. The basket must be refused outright,
        /// before anything is withdrawn at all.
        ///
        /// The assertions on the STORE's version and audit rows are the discriminating half, and they
        /// are not decoration. An end-state-only test (ok == false, ledger still at 3) passes with this
        /// guard fully reverted, because the unguarded path reaches the same end state by a different
        /// route - withdraw the first profile, have the second refused by the ledger's non-negative
        /// guard, then let the finally block's unwind put the first one back. What this test has to
        /// prove is that the store was never mutated AT ALL.
        /// </summary>
        [TestMethod]
        public void WithdrawTransaction_RefusesABasketNamingTheSameGuidTwice()
        {
            // RebuildView materializes the ledger row through the REAL WorldObjectFactory (see class
            // remarks, point 3), which needs both a resolvable weenie for wcid 7779 and a working
            // GuidManager.
            EnsureGuidManagerConstructible();
            SeedWorldStackableWeenie(7779, 100);

            var vendor = MakeVendor();
            var store = MakeStore(out var backend, out _);
            vendor.Store = store;

            // ONE collapsed ledger row, so the panel draws a single display object - and only 3 held,
            // so 2 + 2 is the over-ask that produces the mid-basket failure.
            SeedLedger(backend, 7779, 3);

            vendor.RebuildView();

            var displayGuid = vendor.DefaultItemsForSale.Keys.Single().Full;

            var profiles = new List<ItemProfile>
            {
                new ItemProfile(2, displayGuid),
                new ItemProfile(2, displayGuid),
            };

            // Captured AFTER RebuildView, so only the transaction below can move them.
            var versionBefore = store.Version;
            var logCallsBefore = backend.AddLogCalls;

            var ok = vendor.TryWithdrawTransaction(profiles, Owner, player: null);

            Assert.IsFalse(ok, "a basket naming the same row twice must be refused outright, before anything is withdrawn.");
            Assert.AreEqual(3, store.GetEntries(0, -1).Single().Count, "the ledger must be untouched - nothing may be withdrawn from a basket that is refused.");

            // THE discriminating assertion. Do not simplify this test back down to the two above: they
            // both pass with the duplicate guard reverted, because the unguarded path withdraws the
            // first profile, has the second refused, and then repairs itself through the finally
            // block's ledger unwind - same end state, entirely different behaviour. TryWithdraw bumps
            // the version on its failure path as well as its success path, and the ledger return bumps
            // it again, so an unguarded run moves this counter three times. A guarded run never calls
            // TryWithdraw at all.
            Assert.AreEqual(versionBefore, store.Version,
                "the store must not have been mutated even once. A changed version means the basket was partially withdrawn and then repaired by the unwind, which is exactly the state the duplicate guard exists to prevent.");

            Assert.AreEqual(logCallsBefore, backend.AddLogCalls,
                "a basket refused before it is acted on writes no audit row of any kind.");
            Assert.AreEqual(0, backend.Logs.Count(l => l.Action == (int)AccountVaultAction.Withdraw),
                "nothing was withdrawn, so no Withdraw row may exist - a Withdraw row here is the audit trail of the partial withdrawal this guard exists to prevent.");
        }

        /// <summary>
        /// F5, half two, end to end: the AccountVaultStoreTests companion proves the ledger-return
        /// primitive itself; this proves the vendor actually ROUTES to it. Nothing about a delivered
        /// object distinguishes a ledger-derived one from a stored biota, so the provenance flag
        /// carried through `delivered` is the only thing standing between a rolled-back ledger
        /// withdrawal and a fresh, uncapped stored entry. A null player has nowhere to place the item,
        /// which is the same unwind a full pack takes in play.
        /// </summary>
        [TestMethod]
        public void LedgerWithdrawal_ThatCannotBeDelivered_GoesBackToTheLedgerNotIntoAVaultContainer()
        {
            EnsureGuidManagerConstructible();
            SeedWorldStackableWeenie(7780, 100);

            var vendor = MakeVendor();
            var store = MakeStore(out var backend, out _);
            vendor.Store = store;

            SeedLedger(backend, 7780, 3);

            vendor.RebuildView();

            var displayGuid = vendor.DefaultItemsForSale.Keys.Single().Full;

            var basket = new List<ItemProfile> { new ItemProfile(2, displayGuid) };

            var ok = vendor.TryWithdrawTransaction(basket, Owner, null);

            Assert.IsTrue(ok, "authorization and resolution succeeded; only the delivery had nowhere to go");

            var entries = store.GetEntries(0, -1);

            Assert.AreEqual(1, entries.Count, "a rolled-back ledger withdrawal filed as a stored biota is a SECOND entry, and an uncapped one");
            Assert.IsTrue(entries.Single().Kind == VaultEntryKind.Ledger, "it must come back as the ledger row it left, not as a stored biota");
            Assert.AreEqual(3L, entries.Single().Count, "every unit must be back on the ledger");
        }

        /// <summary>
        /// Fix round 1: the ledger credit is the point of no return, and a throw in the bookkeeping
        /// AFTER it must not be reported to the caller as a failed return.
        ///
        /// The whole retry chain is real here rather than simulated. A throw inside queued work unwinds
        /// through the store's Drain, which logs it and does NOT rethrow, so Enqueue still reports
        /// success while the caller's `returnedOk` is left at its default false. ReturnOne reads that as
        /// a failed return, leaves the item undischarged, and the finally block's ReturnAll retries the
        /// SAME item - and nothing about a ledger-returned item is parented, so the parented guard does
        /// not refuse the retry and the same units are credited a second time. Free uncapped units,
        /// which is the exact bug class this task exists to close.
        ///
        /// TryReturnWithdrawn is immune by construction: TryAddToInventory sets item.ContainerId before
        /// its own throw-prone steps, so a retry's parented guard refuses. The ledger path has no
        /// equivalent mark, so its success has to be made authoritative instead.
        /// </summary>
        [TestMethod]
        public void LedgerReturn_WhoseTeardownThrows_IsNotRetriedAndDoesNotDoubleTheCredit()
        {
            EnsureGuidManagerConstructible();
            SeedWorldStackableWeenie(7781, 100);

            var vendor = MakeVendor();
            var store = MakeStore(out var backend, out var world);
            vendor.Store = store;

            SeedLedger(backend, 7781, 3);

            vendor.RebuildView();

            var displayGuid = vendor.DefaultItemsForSale.Keys.Single().Full;

            // Throw ONCE, from the teardown that runs after the credit has committed. A second
            // DestroyItem - which only a retry can produce - would behave normally, so the doubled
            // credit is visible rather than masked by a second failure.
            world.ThrowFromDestroyItemCount = 1;

            var basket = new List<ItemProfile> { new ItemProfile(2, displayGuid) };

            // Null player, so delivery has nowhere to go and the ledger unwind runs for real.
            vendor.TryWithdrawTransaction(basket, Owner, null);

            var entries = store.GetEntries(0, -1);

            Assert.AreEqual(1, entries.Count, "the ledger row is still the only entry");
            Assert.IsTrue(entries.Single().Kind == VaultEntryKind.Ledger);
            Assert.AreEqual(3L, entries.Single().Count,
                "the units must be credited back exactly once. A doubled count here means the return reported failure after the credit had already committed, and the caller's retry credited the same units again.");
            Assert.AreEqual(1, store.EntryCount, "the entry count must be untouched by the retry either way");
        }

        /// <summary>
        /// I1: GameEventApproachVendor.cs previously computed numItems from DefaultItemsForSale.Count +
        /// UniqueItemsForSale.Count directly and never saw storeItems, so an account holding only
        /// stored (non-ledger) items wrote numItems = 0 while forEachItem still serialized every one of
        /// them - the panel rendered empty for the single most common vault state. This drives the REAL
        /// RebuildView ledger-materialization path (not a shortcut around it - see
        /// EnsureGuidManagerConstructible), seeding two stored items plus one ledger row, and asserts
        /// the count GameEventApproachVendor would have written (via forEachItem) can never disagree
        /// with ItemsForSaleCount.
        /// </summary>
        [TestMethod]
        public void ItemsForSaleCount_MatchesForEachItemEnumeration()
        {
            EnsureGuidManagerConstructible();

            var vendor = MakeVendor();
            var store = MakeStore(out var backend, out var world);
            vendor.Store = store;

            var (_, item1) = SeedStoredItem(backend, world, wcid: 8010);

            var container2 = FakeVaultWorld.MakeContainer(255);
            world.Containers[container2.Guid.Full] = container2;
            backend.Vaults.Add(new ShardAccountVault { Id = 2, AccountId = OwnerAccount, ContainerGuid = container2.Guid.Full, CreatedAt = new DateTime(2026, 1, 2) });
            var item2 = FakeVaultWorld.MakeStack(8011, 1, 100);
            Assert.IsTrue(container2.TryAddToInventory(item2));

            const uint ledgerWcid = 91500;
            SeedWorldStackableWeenie(ledgerWcid, 100);
            SeedLedger(backend, ledgerWcid, 30);

            vendor.RebuildView();

            var forEachItemCount = 0;
            vendor.forEachItem(_ => forEachItemCount++);

            Assert.AreEqual(3, forEachItemCount, "sanity: two stored items plus one materialized ledger row");
            Assert.AreEqual(forEachItemCount, vendor.ItemsForSaleCount, "GameEventApproachVendor's numItems and forEachItem's actual serialization must never be able to disagree");
        }

        #region forEachItem default display sort

        private static readonly object datInitLock = new object();

        private static bool datInitialized;

        private static string datSkipReason;

        /// <summary>
        /// Loads client_portal.dat once for this class, or records why it could not be loaded. The
        /// material key in PersonalVendor.forEachItem calls RecipeManager.GetMaterialName, which reads
        /// DatManager.PortalDat - see BadSetupDidTests.RequireDats for the original of this pattern
        /// (that class's own doc comment claiming to be the sole DatManager consumer in this project is
        /// no longer true now that this is a second one). Any test that plants an item with a
        /// MaterialType must call this first; CI has no client dat files, so those tests report
        /// Inconclusive there rather than throwing a bare NullReferenceException out of forEachItem.
        /// </summary>
        private static void RequireDats()
        {
            lock (datInitLock)
            {
                if (!datInitialized)
                {
                    datInitialized = true;
                    datSkipReason = InitializeDats();
                }
            }

            if (datSkipReason != null)
                Assert.Inconclusive(datSkipReason);
        }

        private static string InitializeDats()
        {
            if (DatManager.PortalDat != null)
                return null;    // already loaded by something else in this test host

            var candidates = new List<string>();

            var fromEnv = System.Environment.GetEnvironmentVariable("ACE_DAT_PATH");
            if (!string.IsNullOrWhiteSpace(fromEnv))
                candidates.Add(fromEnv);

            candidates.Add(@"C:\ACE\Dats\");
            candidates.Add(@"C:\Turbine\Asheron's Call\");

            var datDir = candidates.FirstOrDefault(c => File.Exists(Path.Combine(c, "client_portal.dat")));

            if (datDir == null)
                return $"Skipped: client_portal.dat not found in any of [{string.Join(", ", candidates)}]. Set ACE_DAT_PATH to a directory containing the client .dat files.";

            var portalDat = Path.Combine(datDir, "client_portal.dat");

            try
            {
                using (new FileStream(portalDat, FileMode.Open, FileAccess.Read))
                { }
            }
            catch (IOException ex)
            {
                return $"Skipped: {portalDat} could not be opened for reading ({ex.Message}). Close any running client that holds the .dat files.";
            }

            try
            {
                // the dat readers use Encoding.Default (cp1252), which is not present on .NET without this
                System.Text.Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance);

                // loadCell: false - this needs only client_portal.dat's DualDidMapper
                DatManager.Initialize(datDir, true, false);
            }
            catch (Exception ex)
            {
                return $"Skipped: DatManager.Initialize({datDir}) failed: {ex.Message}";
            }

            return DatManager.PortalDat == null ? $"Skipped: DatManager.Initialize({datDir}) did not produce a PortalDat." : null;
        }

        /// <summary>
        /// Reflection seam onto PersonalVendor's private storeItems dictionary, so a test can plant a
        /// "real stored biota" row without going through the full RebuildView/vault machinery - the
        /// composite sort below cares only about what forEachItem does with the two dictionaries'
        /// combined contents, not how they got populated.
        /// </summary>
        private static void SeedStoreItem(PersonalVendor vendor, WorldObject item)
        {
            var field = typeof(PersonalVendor).GetField("storeItems", BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.IsNotNull(field, "PersonalVendor.storeItems was not found by reflection - has it been renamed?");

            var dict = (Dictionary<ObjectGuid, WorldObject>)field.GetValue(vendor);
            dict[item.Guid] = item;
        }

        /// <summary>
        /// Builds a bare GenericObject with an explicit ItemType (the trap: some fakes elsewhere in
        /// this file declare no ItemType at all, which arrives as ItemType.None and sorts first - every
        /// item built here sets it explicitly so the assertion is about the sort key, not a fake gap),
        /// name, value, and optional workmanship. WeenieClassId is a fresh NextGuid() per call so
        /// Vendor.AddDefaultItem's same-wcid stackable merge (Vendor.cs:190-201) never fires and every
        /// item lands as its own row.
        /// </summary>
        private static WorldObject MakeSortableItem(ItemType itemType, string name, int? value, int? itemWorkmanship = null, MaterialType? materialType = null)
        {
            var properties = new Dictionary<PropertyInt, int>
            {
                { PropertyInt.ItemType, (int)itemType },
            };

            if (value.HasValue)
                properties[PropertyInt.Value] = value.Value;

            if (itemWorkmanship.HasValue)
                properties[PropertyInt.ItemWorkmanship] = itemWorkmanship.Value;

            if (materialType.HasValue)
                properties[PropertyInt.MaterialType] = (int)materialType.Value;

            var weenie = new Weenie
            {
                WeenieClassId = NextGuid(),
                WeenieType = WeenieType.Generic,
                PropertiesInt = properties,
                PropertiesString = new Dictionary<PropertyString, string>
                {
                    { PropertyString.Name, name },
                },
            };

            return new GenericObject(weenie, new ObjectGuid(NextGuid()));
        }

        /// <summary>
        /// Extended sibling of MakeSortableItem for the CategoryOrdinal/SlotOrdinal/WeaponClassOrdinal/
        /// damage-type/min-requirement rungs the new VaultDisplayOrder-driven part of the chain adds
        /// ahead of the material key. Every additional slot is optional so a test can plant only the
        /// properties its case cares about; WeenieType defaults to Generic (matching MakeSortableItem)
        /// but can be overridden for the weapon-class cases (MissileLauncher, Caster, etc).
        /// </summary>
        private static WorldObject MakeVaultOrderItem(
            ItemType itemType,
            string name,
            int? value,
            WeenieType weenieType = WeenieType.Generic,
            int? weaponSkill = null,
            int? ammoType = null,
            int? weaponType = null,
            int? validLocations = null,
            int? damageType = null,
            int? wieldRequirements = null,
            int? wieldDifficulty = null,
            int? wieldRequirements2 = null,
            int? wieldDifficulty2 = null,
            int? uiEffects = null,
            int? useRequiresSkillLevel = null)
        {
            var properties = new Dictionary<PropertyInt, int>
            {
                { PropertyInt.ItemType, (int)itemType },
            };

            if (value.HasValue)
                properties[PropertyInt.Value] = value.Value;

            if (weaponSkill.HasValue)
                properties[PropertyInt.WeaponSkill] = weaponSkill.Value;

            if (ammoType.HasValue)
                properties[PropertyInt.AmmoType] = ammoType.Value;

            if (weaponType.HasValue)
                properties[PropertyInt.WeaponType] = weaponType.Value;

            if (validLocations.HasValue)
                properties[PropertyInt.ValidLocations] = validLocations.Value;

            if (damageType.HasValue)
                properties[PropertyInt.DamageType] = damageType.Value;

            if (wieldRequirements.HasValue)
                properties[PropertyInt.WieldRequirements] = wieldRequirements.Value;

            if (wieldDifficulty.HasValue)
                properties[PropertyInt.WieldDifficulty] = wieldDifficulty.Value;

            if (wieldRequirements2.HasValue)
                properties[PropertyInt.WieldRequirements2] = wieldRequirements2.Value;

            if (wieldDifficulty2.HasValue)
                properties[PropertyInt.WieldDifficulty2] = wieldDifficulty2.Value;

            if (uiEffects.HasValue)
                properties[PropertyInt.UiEffects] = uiEffects.Value;

            if (useRequiresSkillLevel.HasValue)
                properties[PropertyInt.UseRequiresSkillLevel] = useRequiresSkillLevel.Value;

            var weenie = new Weenie
            {
                WeenieClassId = NextGuid(),
                WeenieType = weenieType,
                PropertiesInt = properties,
                PropertiesString = new Dictionary<PropertyString, string>
                {
                    { PropertyString.Name, name },
                },
            };

            return new GenericObject(weenie, new ObjectGuid(NextGuid()));
        }

        /// <summary>
        /// The dat-free part of the composite key from PersonalVendor.forEachItem's doc comment:
        /// ItemType ascending, then Name ascending (ordinal, case-insensitive), then Workmanship (the
        /// per-unit average) descending with null last, then Value descending with null coalesced to
        /// 0, then Guid.Full ascending as the final tiebreak. Deliberately carries NO item with a
        /// MaterialType (see ForEachItem_GroupsSameMaterialFamilyByMaterialName for that) and calls no
        /// RequireDats, so it keeps discriminating on CI even with no client dat reachable there -
        /// code review on PR #792 found that folding the material assertions into this same test made
        /// RequireDats its first line, which put these dat-free regression cases behind the same
        /// Inconclusive gate as the material ones and silently dropped their CI coverage. Rows are
        /// planted across BOTH storeItems and DefaultItemsForSale, and inserted in a shuffled order
        /// that does not match the expected output, so a pass here cannot be explained by insertion
        /// order leaking through.
        ///
        /// SCOPE, and why the name is narrower than "the full composite key": every item this
        /// fixture builds via MakeSortableItem carries no WeaponSkill, AmmoType, WeaponType,
        /// ValidLocations, DamageType or WieldRequirements, so all three of the mid-chain category
        /// rungs (2 weapon-class/slot, 3 damage type, 4 minimum requirement) TIE across every row
        /// here and the sort falls straight through to material/name/workmanship/value/guid. That
        /// is deliberate - it keeps this test pinning the tail of the chain in isolation - but it
        /// means a pass here says nothing about rungs 2-4. Those are covered end-to-end by
        /// ForEachItem_OrdersEquipmentCategoryBySlotThenMinLevelDescendingNoLevelLast and
        /// ForEachItem_OrdersWeaponCategoryByWeaponClassThenDamageTypeThenMinSkillDescending.
        /// </summary>
        [TestMethod]
        public void ForEachItem_OrdersByItemTypeThenNameThenWorkmanshipThenValueThenGuid_WhenCategoryRungsTie()
        {
            var vendor = MakeVendor();

            // ItemType.MeleeWeapon (1) group: two same-type, different-name rows.
            var apple = MakeSortableItem(ItemType.MeleeWeapon, "Apple", value: 100);
            var banana = MakeSortableItem(ItemType.MeleeWeapon, "Banana", value: 50);

            // ItemType.Armor (2) group, name "Bag Tiger Eye": three same-name salvage bags,
            // distinguished only by workmanship (descending, null last).
            var bagHighWork = MakeSortableItem(ItemType.Armor, "Bag Tiger Eye", value: 200, itemWorkmanship: 8);
            var bagLowWork = MakeSortableItem(ItemType.Armor, "Bag Tiger Eye", value: 999, itemWorkmanship: 3);
            var bagNoWork = MakeSortableItem(ItemType.Armor, "Bag Tiger Eye", value: 9999, itemWorkmanship: null);

            // ItemType.Armor, name "Guid Tie": identical on every key except guid - the guid tiebreak.
            var guidTieLow = MakeSortableItem(ItemType.Armor, "Guid Tie", value: 0);
            var guidTieHigh = MakeSortableItem(ItemType.Armor, "Guid Tie", value: 0);
            Assert.IsTrue(guidTieLow.Guid.Full < guidTieHigh.Guid.Full, "sanity: NextGuid() must have produced an ascending pair here");

            // ItemType.Armor, name "Trinket": two same-name rows, distinguished only by Value.
            var trinketHighValue = MakeSortableItem(ItemType.Armor, "Trinket", value: 300);
            var trinketLowValue = MakeSortableItem(ItemType.Armor, "Trinket", value: 100);

            // ItemType.Gameboard (0x80000000) - the high-bit edge case. Its uint value is larger than
            // every other ItemType used here, so it must sort LAST. Sorting on (int)ItemType instead
            // of (uint)ItemType would wrap 0x80000000 to int.MinValue and put this item FIRST instead -
            // this is exactly the regression this case exists to catch.
            var gameboard = MakeSortableItem(ItemType.Gameboard, "Aaa Gameboard", value: 1);

            // Planted across both dictionaries and in an order that matches none of the expected
            // output, so the sort - not insertion order - is what the assertion below is checking.
            SeedStoreItem(vendor, trinketLowValue);
            vendor.AddDefaultItem(bagLowWork);
            SeedStoreItem(vendor, guidTieHigh);
            vendor.AddDefaultItem(gameboard);
            vendor.AddDefaultItem(banana);
            SeedStoreItem(vendor, bagNoWork);
            vendor.AddDefaultItem(trinketHighValue);
            SeedStoreItem(vendor, apple);
            vendor.AddDefaultItem(guidTieLow);
            SeedStoreItem(vendor, bagHighWork);

            var expected = new[]
            {
                apple, banana,
                bagHighWork, bagLowWork, bagNoWork,
                guidTieLow, guidTieHigh,
                trinketHighValue, trinketLowValue,
                gameboard,
            };

            var actual = new List<WorldObject>();
            vendor.forEachItem(actual.Add);

            CollectionAssert.AreEqual(expected, actual, "forEachItem must emit the composite-key order: ItemType asc, Name asc (ordinal ci), Workmanship desc (null last), Value desc (null->0), Guid.Full asc");
        }

        /// <summary>
        /// The END-TO-END sharing check for the 2026-09-21 solo Thread Cache sort: a real PersonalVendor's
        /// forEachItem and a real Thread Cache's placement order must emit the SAME sequence for the same
        /// mixed set. Both now read VaultDisplaySort, and this is the test that would fail if either side
        /// grew a rung of its own - ThreadCacheSortTests pins the cache's order against a hand-written
        /// expectation, but only a comparison against the live vendor can say the two agree.
        ///
        /// The vendor order is collected FIRST, before the items are put in a container, because
        /// Container.TryAddToInventory writes ContainerId and PlacementPosition onto them; neither is in the
        /// sort key, so the order cannot depend on which side ran first, and doing it in this order keeps
        /// that independent of any future key change. No item carries a MaterialType, so this needs no dats
        /// and keeps discriminating on CI.
        /// </summary>
        [TestMethod]
        public void ForEachItem_AndAThreadCache_EmitTheSameOrderForTheSameMixedSet()
        {
            var vendor = MakeVendor();

            var items = new[]
            {
                MakeVaultOrderItem(ItemType.Caster, "Wand of Frost", value: 500, weenieType: WeenieType.Caster),
                MakeVaultOrderItem(ItemType.Armor, "Hauberk", value: 650, validLocations: (int)EquipMask.ChestArmor),
                MakeVaultOrderItem(ItemType.MeleeWeapon, "Axe of Ire", value: 900, weaponSkill: (int)Skill.HeavyWeapons,
                    damageType: (int)DamageType.Slash, wieldRequirements: (int)WieldRequirement.Skill, wieldDifficulty: 400),
                MakeVaultOrderItem(ItemType.MeleeWeapon, "Zephyr Blade", value: 120, weaponSkill: (int)Skill.HeavyWeapons,
                    damageType: (int)DamageType.Slash),
                MakeVaultOrderItem(ItemType.Jewelry, "Band of Embers", value: 210,
                    validLocations: (int)(EquipMask.FingerWearLeft | EquipMask.FingerWearRight)),
                MakeVaultOrderItem(ItemType.Armor, "Iron Helm", value: 80, validLocations: (int)EquipMask.HeadWear),
                MakeVaultOrderItem(ItemType.MissileWeapon, "Yew Bow", value: 400, weenieType: WeenieType.MissileLauncher,
                    ammoType: (int)AmmoType.Arrow),
                MakeVaultOrderItem(ItemType.Money, "Pyreal", value: 5000),
                // Coverage add (code review, b38e2b1d6): a Misc-family pair so the parity check also
                // exercises the new rung-2 (FamilyOrdinal) and rung-3/4 (PetDevice UiEffects/
                // UseRequiresSkillLevel) behaviour, not just the pre-existing weapon/equipment rungs.
                MakeVaultOrderItem(ItemType.Misc, "Acid Child Essence (125)", value: 15000, weenieType: WeenieType.PetDevice,
                    uiEffects: (int)UiEffects.Acid, useRequiresSkillLevel: 430),
                MakeVaultOrderItem(ItemType.Misc, "Glyph of Alchemy", value: 50, weenieType: WeenieType.Stackable),
            };

            foreach (var item in items)
                SeedStoreItem(vendor, item);

            var vendorOrder = new List<WorldObject>();
            vendor.forEachItem(vendorOrder.Add);

            Assert.AreEqual(items.Length, vendorOrder.Count, "fixture: every planted row must reach forEachItem");

            // A Thread Cache, filled in an order that matches neither the input nor the expected output.
            var cache = ACE.Server.Tests.ThreadDungeons.ThreadLootTestFixtures.Cache();

            for (var i = 0; i < items.Length; i++)
                Assert.IsTrue(cache.TryAddToInventory(items[items.Length - 1 - i], i), $"fixture: the cache refused {items[items.Length - 1 - i].Name}");

            ACE.Server.ThreadDungeons.ThreadCacheSort.Reorder(cache);

            var cacheOrder = cache.Inventory.Values.OrderBy(i => i.PlacementPosition ?? 0).ToList();

            CollectionAssert.AreEqual(vendorOrder.Select(i => i.Name).ToList(), cacheOrder.Select(i => i.Name).ToList(),
                "the mule panel and a solo Thread Cache must present the same items in the same order - they share VaultDisplaySort");
        }

        /// <summary>
        /// Reproduces the player-reported bug: withdrawing salvage bags of the same material made rows
        /// "jump around" instead of staying in workmanship order. Root cause was rung 6 (Name) sorting
        /// on the DISPLAYED name, which a group/ledger row has a " (N in vault)" count suffix appended
        /// to - so three same-material rows sharing a base name were actually ordered by the ordinal
        /// string compare of "", " (3 in vault)", " (12 in vault)" rather than by Workmanship, and every
        /// withdrawal (changing N, or dropping the suffix entirely at N=1) reshuffled them.
        ///
        /// Grouping/ledger materialization itself needs a live account vault or dats (TryCreateDisplayProxy,
        /// RebuildView's entry loop), which this unit test does not have, so this drives forEachItem's
        /// sort directly through the same seam production uses: PersonalVendor.RegisterSortName records
        /// the base name a row must sort on, exactly as the two production call sites do right before
        /// they append the count suffix to Name. All three rows here share the base name "Salvaged Tiger
        /// Eye" and carry the suffix already applied to Name (mirroring what a player would actually see
        /// in the panel), so a sort that fell through to comparing Name would scatter them by the
        /// suffix's ordinal text instead of by Workmanship.
        /// </summary>
        [TestMethod]
        public void ForEachItem_IgnoresCountSuffixWhenSortingByName_SoWorkmanshipDecidesGroupOrder()
        {
            var vendor = MakeVendor();

            const string baseName = "Salvaged Tiger Eye";

            // Highest workmanship, group of 12 - "(12 in vault)" sorts AFTER "(3 in vault)" ordinally,
            // so a Name-based sort would put this SECOND despite it having the highest workmanship.
            var highWorkGroupOf12 = MakeSortableItem(ItemType.Armor, $"{baseName} (12 in vault)", value: 1, itemWorkmanship: 9);
            vendor.AddDefaultItem(highWorkGroupOf12);
            vendor.RegisterSortName(highWorkGroupOf12.Guid, baseName);

            // Middle workmanship, a single unsuffixed bag - the bare base name, no suffix at all.
            var midWorkSingle = MakeSortableItem(ItemType.Armor, baseName, value: 1, itemWorkmanship: 5);
            vendor.AddDefaultItem(midWorkSingle);

            // Lowest workmanship, group of 3 - "(3 in vault)" sorts FIRST ordinally among the three
            // suffixes/names here, so a Name-based sort would put this FIRST despite the lowest workmanship.
            var lowWorkGroupOf3 = MakeSortableItem(ItemType.Armor, $"{baseName} (3 in vault)", value: 1, itemWorkmanship: 2);
            vendor.AddDefaultItem(lowWorkGroupOf3);
            vendor.RegisterSortName(lowWorkGroupOf3.Guid, baseName);

            var expected = new[] { highWorkGroupOf12, midWorkSingle, lowWorkGroupOf3 };

            var actual = new List<WorldObject>();
            vendor.forEachItem(actual.Add);

            CollectionAssert.AreEqual(expected, actual, "forEachItem must order same-material rows by Workmanship descending regardless of their count suffix - the count must never be a sort key.");
        }

        /// <summary>
        /// The material key inserted between ItemType and Name (PersonalVendor.forEachItem's doc
        /// comment, level 2). Needs real client dat (RequireDats) because the material-keyed rows
        /// exercise the real RecipeManager.GetMaterialName call, not a stub - split out of
        /// ForEachItem_OrdersByItemTypeThenNameThenWorkmanshipThenValueThenGuid_WhenCategoryRungsTie (code review, PR #792) specifically so that
        /// test's dat-free regression cases keep discriminating on CI even when this one goes
        /// Inconclusive for lack of a dat.
        /// </summary>
        [TestMethod]
        public void ForEachItem_GroupsSameMaterialFamilyByMaterialName()
        {
            RequireDats();

            var vendor = MakeVendor();

            // ItemType.Armor, no MaterialType at all (property absent) - the ordinary "sealed hammer"
            // shape from the doc comment's known-consequence note. Material key "".
            var noMaterial = MakeSortableItem(ItemType.Armor, "No Material Item", value: 1);

            // ItemType.Armor, MaterialType.Unknown (0) - the log-spam guard gap code review found:
            // HasValue alone treated an EXPLICIT Unknown the same as a real material and called
            // GetMaterialName on it, which has no DualDidMapper entry for Unknown and logs an error on
            // every vendor approach. Unknown must land in the SAME empty-material run as an absent
            // MaterialType, not get its own material group and not trigger that log.
            var unknownMaterial = MakeSortableItem(ItemType.Armor, "Unknown Material Item", value: 1, materialType: MaterialType.Unknown);

            // ItemType.Armor, MaterialType.Amethyst: the naming-shapes case the material key exists
            // for. A hammer names the material directly ("Amethyst Hammer"); a full bag leads with
            // "Full Bag of" ("Full Bag of Amethyst Salvage") - pure Name order would strand these two
            // items far apart. The material key clusters both under "Amethyst" instead, adjacent to
            // each other and after every empty-material row.
            var amethystHammer = MakeSortableItem(ItemType.Armor, "Amethyst Hammer", value: 50, materialType: MaterialType.Amethyst);
            var amethystFullBag = MakeSortableItem(ItemType.Armor, "Full Bag of Amethyst Salvage", value: 500, materialType: MaterialType.Amethyst);

            // ItemType.Armor, MaterialType.TigerEye: a second, different material - proves the
            // material key SEPARATES families rather than just clustering everything that has some
            // material together. "Amethyst" < "Tiger Eye" ordinally, so this group sorts after it.
            var tigerEyeHammer = MakeSortableItem(ItemType.Armor, "Tiger Eye Hammer", value: 50, materialType: MaterialType.TigerEye);

            // Planted in an order that matches none of the expected output, so the sort - not
            // insertion order - is what the assertion below is checking.
            vendor.AddDefaultItem(tigerEyeHammer);
            SeedStoreItem(vendor, amethystFullBag);
            vendor.AddDefaultItem(unknownMaterial);
            SeedStoreItem(vendor, amethystHammer);
            vendor.AddDefaultItem(noMaterial);

            var expected = new[]
            {
                // Empty-material run first, Name ascending - null and Unknown both land here.
                noMaterial, unknownMaterial,
                // Then material groups, material name ascending, adjacent within each material.
                amethystHammer, amethystFullBag,
                tigerEyeHammer,
            };

            var actual = new List<WorldObject>();
            vendor.forEachItem(actual.Add);

            CollectionAssert.AreEqual(expected, actual, "forEachItem must group same-material rows adjacently by material name (empty run first, covering both a null and an explicit MaterialType.Unknown), ahead of the Name-only tiebreak within each group");
        }

        /// <summary>
        /// I1's bug (GameEventApproachVendor.cs:58-72 serializes forEachItem's enumeration order
        /// directly) is what makes this matter: the base implementation emits storeItems in full
        /// before DefaultItemsForSale, so a real stored biota and a ledger display row never land next
        /// to each other however their names compare. Two stored items and two ledger rows, named so
        /// the correct alphabetical order interleaves them (Alpha stored, Bravo ledger, Charlie stored,
        /// Delta ledger) - the old two-runs behavior would instead emit Alpha, Charlie, Bravo, Delta.
        /// </summary>
        [TestMethod]
        public void ForEachItem_InterleavesStoredItemsAndLedgerRowsIntoOneOrderedSequence()
        {
            var vendor = MakeVendor();

            var alphaStored = MakeSortableItem(ItemType.Misc, "Alpha", value: 1);
            var bravoLedger = MakeSortableItem(ItemType.Misc, "Bravo", value: 1);
            var charlieStored = MakeSortableItem(ItemType.Misc, "Charlie", value: 1);
            var deltaLedger = MakeSortableItem(ItemType.Misc, "Delta", value: 1);

            SeedStoreItem(vendor, charlieStored);
            vendor.AddDefaultItem(deltaLedger);
            SeedStoreItem(vendor, alphaStored);
            vendor.AddDefaultItem(bravoLedger);

            var actual = new List<WorldObject>();
            vendor.forEachItem(actual.Add);

            CollectionAssert.AreEqual(new[] { alphaStored, bravoLedger, charlieStored, deltaLedger }, actual,
                "a real stored item and a ledger display row must interleave into one ordered sequence by name, not arrive as two separate runs");
        }

        /// <summary>
        /// End-to-end equipment-category ordering: SlotOrdinal (head-down primary slot) is the primary
        /// rung within the equipment category, then MinLevelRequirement descending with no-level items
        /// last. Two chest items with different level requirements prove the descending/no-level-last
        /// half; a feet item after both chest items proves SlotOrdinal (chest is head-down of feet)
        /// still wins over level requirement across slots.
        /// </summary>
        [TestMethod]
        public void ForEachItem_OrdersEquipmentCategoryBySlotThenMinLevelDescendingNoLevelLast()
        {
            var vendor = MakeVendor();

            var chestHighLevel = MakeVaultOrderItem(ItemType.Armor, "Chest High Level", value: 1,
                validLocations: (int)EquipMask.ChestArmor, wieldRequirements: (int)WieldRequirement.Level, wieldDifficulty: 150);

            var chestLowLevel = MakeVaultOrderItem(ItemType.Armor, "Chest Low Level", value: 1,
                validLocations: (int)EquipMask.ChestArmor, wieldRequirements: (int)WieldRequirement.Level, wieldDifficulty: 50);

            // No wield requirement at all - must sort last within the chest slot group.
            var chestNoLevel = MakeVaultOrderItem(ItemType.Clothing, "Chest No Level", value: 1,
                validLocations: (int)EquipMask.ChestWear);

            // The covenant/olthoi armor shape: the Level requirement lives in slot 2, not slot 1.
            var chestLevelInSlotTwo = MakeVaultOrderItem(ItemType.Armor, "Chest Level Slot Two", value: 1,
                validLocations: (int)EquipMask.ChestArmor,
                wieldRequirements: (int)WieldRequirement.Attrib, wieldDifficulty: 999,
                wieldRequirements2: (int)WieldRequirement.Level, wieldDifficulty2: 100);

            // Feet sorts after chest (chest is head-down of feet), regardless of its own level requirement.
            var feetHighLevel = MakeVaultOrderItem(ItemType.Armor, "Feet High Level", value: 1,
                validLocations: (int)EquipMask.FootWear, wieldRequirements: (int)WieldRequirement.Level, wieldDifficulty: 275);

            vendor.AddDefaultItem(feetHighLevel);
            SeedStoreItem(vendor, chestNoLevel);
            vendor.AddDefaultItem(chestLevelInSlotTwo);
            SeedStoreItem(vendor, chestLowLevel);
            vendor.AddDefaultItem(chestHighLevel);

            var expected = new[] { chestHighLevel, chestLevelInSlotTwo, chestLowLevel, chestNoLevel, feetHighLevel };

            var actual = new List<WorldObject>();
            vendor.forEachItem(actual.Add);

            CollectionAssert.AreEqual(expected, actual,
                "equipment-category items must order by head-down slot first (chest before feet), then by min level requirement descending with no-level items last");
        }

        /// <summary>
        /// End-to-end weapon-category ordering: WeaponClassOrdinal is the primary rung, then damage
        /// type, then MinSkillRequirement descending with no-skill last. A melee heavy weapon sorts
        /// before a melee light weapon; within the same weapon class, two different damage types
        /// group separately; within the same class and damage type, skill requirement breaks the tie.
        /// </summary>
        [TestMethod]
        public void ForEachItem_OrdersWeaponCategoryByWeaponClassThenDamageTypeThenMinSkillDescending()
        {
            var vendor = MakeVendor();

            var heavySlash = MakeVaultOrderItem(ItemType.MeleeWeapon, "Heavy Slash", value: 1,
                weaponSkill: (int)Skill.HeavyWeapons, damageType: (int)DamageType.Slash);

            var heavyPierceHighSkill = MakeVaultOrderItem(ItemType.MeleeWeapon, "Heavy Pierce High Skill", value: 1,
                weaponSkill: (int)Skill.HeavyWeapons, damageType: (int)DamageType.Pierce,
                wieldRequirements: (int)WieldRequirement.Skill, wieldDifficulty: 300);

            var heavyPierceLowSkill = MakeVaultOrderItem(ItemType.MeleeWeapon, "Heavy Pierce Low Skill", value: 1,
                weaponSkill: (int)Skill.HeavyWeapons, damageType: (int)DamageType.Pierce,
                wieldRequirements: (int)WieldRequirement.Skill, wieldDifficulty: 100);

            var heavyPierceNoSkill = MakeVaultOrderItem(ItemType.MeleeWeapon, "Heavy Pierce No Skill", value: 1,
                weaponSkill: (int)Skill.HeavyWeapons, damageType: (int)DamageType.Pierce);

            // Light Weapons sorts after Heavy Weapons in the display order (WeaponClassOrdinal).
            var lightSlash = MakeVaultOrderItem(ItemType.MeleeWeapon, "Light Slash", value: 1,
                weaponSkill: (int)Skill.LightWeapons, damageType: (int)DamageType.Slash);

            SeedStoreItem(vendor, lightSlash);
            vendor.AddDefaultItem(heavyPierceNoSkill);
            SeedStoreItem(vendor, heavySlash);
            vendor.AddDefaultItem(heavyPierceLowSkill);
            SeedStoreItem(vendor, heavyPierceHighSkill);

            var expected = new[] { heavySlash, heavyPierceHighSkill, heavyPierceLowSkill, heavyPierceNoSkill, lightSlash };

            var actual = new List<WorldObject>();
            vendor.forEachItem(actual.Add);

            CollectionAssert.AreEqual(expected, actual,
                "weapon-category items must order by weapon class first (heavy before light), then damage type (slash before pierce), then min skill requirement descending with no-requirement items last");
        }

        #endregion

        /// <summary>
        /// I5: an unauthorized actor must not have the view built at all - RebuildView (and therefore
        /// storeItems/DefaultItemsForSale) must be left exactly as it was, because the base ApproachVendor
        /// call this would otherwise lead into serializes the entire listing to whoever is asking.
        ///
        /// VERIFY-QUEUE note (live, two-account check): confirm in-game that a stranger's crafted/forced
        /// approach at another account's mule receives no vendor panel content and that
        /// player.LastOpenedContainerId is never set to that vendor's guid.
        /// </summary>
        [TestMethod]
        public void ApproachVendor_RefusesAnUnauthorizedActor_LeavesViewUnbuilt()
        {
            var vendor = MakeVendor();
            var store = MakeStore(out var backend, out var world);
            vendor.Store = store;

            SeedStoredItem(backend, world);

            var ok = vendor.TryPrepareApproach(Stranger, out var failReason);

            Assert.IsFalse(ok);
            Assert.IsFalse(string.IsNullOrEmpty(failReason));
            Assert.AreEqual(0, vendor.DefaultItemsForSale.Count, "RebuildView must not have run for an unauthorized actor");
            Assert.AreEqual(0, vendor.ItemsForSaleCount, "the view must be exactly as empty as it started");
        }

        [TestMethod]
        public void ApproachVendor_WithHomeSet_DoesNotThrow()
        {
            // Sanity: an authorized approach with Home already set does not throw and DOES build the view.
            var vendor = MakeVendor();
            var store = MakeStore(out var backend, out var world);
            vendor.Store = store;
            SeedStoredItem(backend, world);

            var pos = new Position(0x7F200002, 1f, 2f, 3f, 0f, 0f, 0f, 1f, 0);
            vendor.Location = pos;
            vendor.Home = pos;

            Assert.IsTrue(vendor.TryPrepareApproach(Owner, out _));
            Assert.AreEqual(1, vendor.ItemsForSaleCount);
        }

        /// <summary>
        /// I6, the real regression test: R10's actual risk is CheckResetToHome (Vendor.cs:387,
        /// `Location.Pos.Equals(Home.Pos)`) being scheduled at all for a vendor with no Home. Retail's
        /// base ApproachVendor always calls the virtual PrepareResetToHome, which schedules
        /// CheckResetToHome on a 300s ActionChain by stamping ResetTimestamp - Task 6 virtualized
        /// PrepareResetToHome specifically so a subclass could make that never happen.
        ///
        /// This test builds a vendor with Home deliberately left NULL (never set, unlike the previous
        /// version of this test which set Home itself and therefore could not have caught a missing
        /// override), calls PrepareResetToHome() directly via reflection (it is protected), and asserts
        /// ResetTimestamp is STILL null afterward - i.e. nothing was scheduled. Verified to FAIL before
        /// the fix: see task-7-report.md for the exact before/after run this test was checked against
        /// with PersonalVendor.PrepareResetToHome commented out.
        /// </summary>
        [TestMethod]
        public void PrepareResetToHome_NeverSchedulesAResetWhenHomeIsUnset()
        {
            var vendor = MakeVendor();

            Assert.IsNull(vendor.Home, "sanity: Home must start unset for this test to mean anything");

            var prepareResetToHome = typeof(Vendor).GetMethod("PrepareResetToHome", BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.IsNotNull(prepareResetToHome);
            prepareResetToHome.Invoke(vendor, null);

            var resetTimestampProperty = typeof(WorldObject).GetProperty("ResetTimestamp", BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.IsNotNull(resetTimestampProperty);
            var resetTimestamp = resetTimestampProperty.GetValue(vendor);

            Assert.IsNull(resetTimestamp, "PrepareResetToHome must never schedule a reset on a PersonalVendor - R10 must be UNREACHABLE, not merely safe if Home happens to be set");
        }

        [TestMethod]
        public void Ethereal_And_NotAttackable_AreSetInCode()
        {
            var vendor = MakeVendor();

            Assert.IsTrue(vendor.Ethereal, "an unpinned mule must be intangible - see DESIGN section 12");
            Assert.IsFalse(vendor.Attackable, "an unpinned mule must never be a targetable creature - see DESIGN section 12");
        }

        /// <summary>
        /// I8: nothing pinned a store while a window was open, so the idle sweep could retire a store a
        /// live PersonalVendor still pointed at. The Store setter now calls AddWindow on bind (M2), and
        /// TryEvict's own first check is OpenWindows > 0 - this proves the two are actually wired
        /// together, not merely that each exists in isolation.
        /// </summary>
        [TestMethod]
        public void BoundStore_IsNotEvictedWhileVendorIsAlive()
        {
            var vendor = MakeVendor();
            var store = MakeStore(out _, out _);
            vendor.Store = store;

            Assert.AreEqual(1, store.OpenWindows, "binding Store must call AddWindow exactly once");

            var pastIdleThreshold = Time.GetUnixTime() + AccountVaultStore.IdleEvictionSeconds + 10.0;
            var evicted = store.TryEvict(pastIdleThreshold);

            Assert.IsFalse(evicted, "a store with an open vendor window must never be evicted");
            Assert.IsFalse(store.IsEvicted);
        }

        [TestMethod]
        public void Store_RebindToADifferentAccountIsRefused()
        {
            var vendor = MakeVendor();
            var store = MakeStore(out _, out _);
            vendor.Store = store;

            var otherStore = new AccountVaultStore(StrangerAccount, new FakeVaultBackend(), new FakeVaultWorld());

            vendor.Store = otherStore;

            Assert.AreSame(store, vendor.Store, "a window must never be silently re-pointed at a different account's vault");
        }

        /// <summary>
        /// CanAccept (post-I2 addendum): the non-mutating preflight consulted BEFORE an item is
        /// detached from the player's pack, mirroring TryDeposit's own refusal set. Four cases in one
        /// test, matching the coordinator's spec: false for an empty Container (UseBackpackSlot), false
        /// for VaultAccess.None, false at capacity, true otherwise.
        ///
        /// The VaultAccess.None case is asserted twice, once through each CanAcceptCore overload. The
        /// resolving two-argument core is what the other three cases drive and what the rest of this
        /// file drives; the four-argument access-passed core is the one the live sell path reaches
        /// after Task 6's hoist, and nothing here drove it at all.
        /// </summary>
        [TestMethod]
        public void CanAccept_CoversItsFourCases()
        {
            // See DepositItems_HandsBackOrphanOnRefusal's remark: restoring the shared default in a
            // finally keeps this test's capacity mutation from leaking into any other test in the run.
            try
            {
                var vendor = MakeVendor();
                var store = MakeStore(out var backend, out var world);
                vendor.Store = store;

                var ordinaryItem = FakeVaultWorld.MakeStack(8000, 1, 100);

                // True otherwise: owner, ordinary item, cap not reached.
                Assert.IsTrue(vendor.CanAcceptCore(ordinaryItem, Owner, out var okReason), okReason);

                // False for an empty Container - UseBackpackSlot is true for any Container regardless of
                // what it holds, per the coordinator's "an EMPTY backpack" scenario.
                var emptyPack = FakeVaultWorld.MakeContainer(itemsCapacity: 6);
                Assert.IsFalse(vendor.CanAcceptCore(emptyPack, Owner, out var packReason));
                Assert.AreEqual("A pack cannot be stored in your vault. Store what is inside it instead.", packReason);

                // False for VaultAccess.None.
                Assert.IsFalse(vendor.CanAcceptCore(ordinaryItem, Stranger, out var accessReason));
                Assert.AreEqual("You do not have permission to use this vault.", accessReason);

                // And the same refusal through the ACCESS-PASSED core, which is the one the live sell
                // path reaches: Task 6 hoisted resolution out of the per-item loop, so
                // Player_Commerce calls the four-argument CanAccept and every other assertion in this
                // file drives the resolving two-argument core that no longer has a production caller.
                Assert.IsFalse(vendor.CanAcceptCore(ordinaryItem, Stranger, VaultAccess.None, out var passedAccessReason));
                Assert.AreEqual("You do not have permission to use this vault.", passedAccessReason);

                // False at capacity. A FRESH store, seeded with its one occupying entry BEFORE its
                // first load, rather than reusing the store above: AccountVaultStore reads the ledger
                // from the backend exactly once (TryEnsureLoadedLocked, on first use) and does not
                // re-poll it afterward, so a row added to the backend AFTER the store above already
                // loaded (every CanAcceptCore call above forces a load) would be invisible to
                // EntryCountLocked - this reused-store shape was tried first and silently passed
                // capacity when it should have refused, which is why this uses its own store instead.
                Assert.IsTrue(PropertyManager.ModifyLong("account_vault_entry_cap", 1),
                    "account_vault_entry_cap is missing from DefaultLongProperties");
                var capacityStore = MakeStore(out var capacityBackend, out _);
                SeedLedger(capacityBackend, 7779, 1);
                var capacityVendor = MakeVendor();
                capacityVendor.Store = capacityStore;
                Assert.IsFalse(capacityVendor.CanAcceptCore(ordinaryItem, Owner, out var capacityReason));
                StringAssert.Contains(capacityReason, "Your vault is full");
            }
            finally
            {
                PropertyManager.ModifyLong("account_vault_entry_cap", 500);
            }
        }

        /// <summary>
        /// F1: CanAcceptCore's capacity check must honour DESIGN 7.3 exactly the way TryDeposit's own
        /// cap check does - the cap counts ENTRIES, not units, so a PRISTINE item that would only top
        /// up a ledger row the account already holds adds no entry and must not be refused at the cap.
        /// The re-review found this live in production, not latent: another agent already wired
        /// CanAccept into VerifySellItems (e08ea4b42), so a player at cap topping up an existing ledger
        /// row was being refused at the pre-flight for a deposit TryDeposit itself would accept.
        ///
        /// BEFORE the fix (WouldAddEntry not consulted, capacity refused unconditionally on
        /// EntryCount >= the cap): the pristine top-up assertion below fails with
        /// `Failed: 1, Passed: 0` - see task-7-report.md for the exact run.
        /// </summary>
        [TestMethod]
        public void CanAccept_CapacityCheckHonoursDESIGN73_PristineTopUpDoesNotConsumeTheCap()
        {
            // One-time seed first, or the MakeVendor below resets the cap of 1 to 500 when this test
            // is the first in the process to build a vendor (it failed when run alone).
            EnsureVendorConstructible();

            try
            {
                Assert.IsTrue(PropertyManager.ModifyLong("account_vault_entry_cap", 1),
                    "account_vault_entry_cap is missing from DefaultLongProperties");

                var store = MakeStore(out var backend, out var world);
                const uint wcid = 9100;
                SeedLedger(backend, wcid, 5);  // one entry already occupies the cap of 1

                var vendor = MakeVendor();
                vendor.Store = store;

                // A pristine item for the SAME wcid the ledger already holds tops up that row rather
                // than adding a new entry - must be ACCEPTED even though EntryCount already equals cap.
                world.PristineResult = true;
                var pristineTopUp = FakeVaultWorld.MakeStack(wcid, 1, 100);
                Assert.IsTrue(vendor.CanAcceptCore(pristineTopUp, Owner, out var topUpReason), topUpReason);

                // A non-pristine item for the same wcid genuinely adds a new stored-biota entry and
                // must still be refused - proves the gate is not simply disabled.
                world.PristineResult = false;
                var nonPristineSameWcid = FakeVaultWorld.MakeStack(wcid, 1, 100);
                Assert.IsFalse(vendor.CanAcceptCore(nonPristineSameWcid, Owner, out var refusedReason));
                StringAssert.Contains(refusedReason, "Your vault is full");
            }
            finally
            {
                PropertyManager.ModifyLong("account_vault_entry_cap", 500);
            }
        }

        /// <summary>
        /// Fix round 2, F4(a): an item that is already in a container is not an orphan, and must not be
        /// handed back on top of wherever it already is.
        ///
        /// TryDeposit refuses a still-parented item outright, which is the reachable way to get a
        /// refusal for an item that has somewhere to be. Before the fix that refusal went straight into
        /// HandBackOrphan -> TryCreateInInventoryWithNetworking with no ContainerId check at all -
        /// unlike TryReturnWithdrawn, TryReturnWithdrawnToLedger and ReturnAll, which have carried that
        /// guard from the start.
        ///
        /// TryHandBackCallCount is the mechanism: it is 1 before the fix and 0 after, and the loose-item
        /// control in the same test proves the hand-back is not simply disabled.
        /// </summary>
        [TestMethod]
        public void DepositItems_DoesNotHandBackAnItemThatIsAlreadyParented()
        {
            var vendor = MakeRecordingVendor();
            var store = MakeStore(out _, out var world);
            vendor.Store = store;

            world.PristineResult = false;

            // Parented: TryDeposit refuses anything that still has a container, because the caller was
            // supposed to detach it.
            var holder = FakeVaultWorld.MakeContainer(itemsCapacity: 10);
            var parented = FakeVaultWorld.MakeStack(9400, 1, 100);
            Assert.IsTrue(holder.TryAddToInventory(parented), "could not seed the parented fixture");
            Assert.IsNotNull(parented.ContainerId, "sanity: the fixture must actually be parented, or this test proves nothing");

            vendor.DepositItems(Owner, new Dictionary<uint, WorldObject> { { parented.Guid.Full, parented } }, null);

            Assert.AreEqual(0, vendor.TryHandBackCallCount,
                "an item that is already in a container must NOT be handed back - placing it again would put one object in two containers, which is a duplicate on the next load");
            Assert.AreEqual(holder.Guid.Full, parented.ContainerId, "and it must be left exactly where it already was");

            // Control: a LOOSE item refused for an ordinary reason is still handed back, so the guard
            // above is about being parented and not about hand-back having been removed.
            var loose = FakeVaultWorld.MakeStack(9401, 1, 100);

            vendor.DepositItems(Stranger, new Dictionary<uint, WorldObject> { { loose.Guid.Full, loose } }, null);

            Assert.AreEqual(1, vendor.TryHandBackCallCount, "a genuinely detached item refused by the store must still be handed back");
        }

        /// <summary>
        /// Fix round 2, F4(b): queued vault work that THREW is not a refusal, and must not be handed
        /// back.
        ///
        /// AccountVaultStore.Drain catches a work item's exception and does not rethrow - correctly,
        /// because a throw must not strand the queue - so the standard closure shape leaves `ok` at its
        /// initialized false and Enqueue still reports true. The vendor read that default false as "the
        /// store refused" and handed the item back. But TryDeposit's ledger path CREDITS the ledger and
        /// then calls DestroyItem, so a throw in that tail leaves the units credited AND the item back
        /// in the player's pack: a dupe, produced by the error handling rather than by the error.
        ///
        /// Driven here through FakeVaultWorld.ThrowFromDestroyItemCount, which is exactly that tail.
        /// </summary>
        [TestMethod]
        public void DepositItems_WhenTheQueuedWorkThrows_DoesNotHandTheItemBack()
        {
            var vendor = MakeRecordingVendor();
            var store = MakeStore(out var backend, out var world);
            vendor.Store = store;

            world.PristineResult = true;            // the ledger path, whose tail is DestroyItem
            world.ThrowFromDestroyItemCount = 1;    // throw once, after the credit has committed

            var item = FakeVaultWorld.MakeStack(9402, 6, 100);

            vendor.DepositItems(Owner, new Dictionary<uint, WorldObject> { { item.Guid.Full, item } }, null);

            Assert.AreEqual(6L, backend.Stacks.Single(s => s.Wcid == 9402).Count,
                "sanity: the throw must land AFTER the ledger credit, or this is testing a mutation that never happened");

            Assert.AreEqual(0, vendor.TryHandBackCallCount,
                "work that threw may have credited the ledger already, so the item must NOT be handed back - that is the dupe. It is left where the throw left it and a DEPOSIT STATE UNKNOWN marker is logged instead.");

            Assert.IsFalse(item.IsDestroyed);
        }

        /// <summary>
        /// WHAT THIS TEST ACTUALLY PINS, corrected. It was written as guarantee 1b's PersonalVendor-level
        /// proof, and it is not that: while the throw arm withheld every item unconditionally its headline
        /// assertion could not fail for the stated reason, because the arm returns before the only
        /// HandBackOrphan call site that reads an outcome record. It passed identically against the
        /// initialise-as-the-loop-goes mutant it named. That mutant is discriminated at the store level, by
        /// VaultBatchDepositTests.Batch_WhenTheDestroyPhaseThrows_EveryCreditedItemAlreadySaysDoNotHandBack,
        /// and it is still the test that carries guarantee 1b.
        ///
        /// What this one pins now that the throw arm hands back SELECTIVELY: an item whose group committed
        /// in phase B is still withheld when the throw arrives, and so is one whose credit is merely
        /// Unknown. Those are the two states the selective arm must keep excluding, and an arm that read
        /// `!Deposited` rather than `Commit == None` would return both of these items on top of a credit
        /// that had already landed.
        ///
        /// The setup remains the right one for that. Two items of the SAME wcid share one ledger group, so
        /// both are marked Committed together in phase B, before phase C destroys either of them.
        /// BeforeDestroyItem throws on the SECOND DestroyItem call regardless of which item that is, so
        /// exactly one item is actually destroyed and the other is left alive but ALREADY credited - a
        /// disposition that reads only "was it destroyed" would treat that one as an orphan.
        ///
        /// The backend assertion below is not decoration: it is what proves both items were credited
        /// BEFORE either destroy ran, without which the hand-back assertion would be about nothing.
        /// </summary>
        [TestMethod]
        public void DepositItems_BatchThrowMidwayThroughPhaseC_NeverHandsBackAnAlreadyCommittedItem()
        {
            var vendor = MakeRecordingVendor();
            var store = MakeStore(out var backend, out var world);
            vendor.Store = store;

            world.PristineResult = true; // the ledger path, whose tail is DestroyItem

            var item1 = FakeVaultWorld.MakeStack(9410, 3, 100);
            var item2 = FakeVaultWorld.MakeStack(9410, 4, 100); // same wcid: same ledger group as item1

            var destroyCalls = 0;

            world.BeforeDestroyItem = destroyed =>
            {
                destroyCalls++;

                if (destroyCalls == 2)
                    throw new InvalidOperationException("fake teardown failure on the second item of the group");
            };

            var items = new Dictionary<uint, WorldObject>
            {
                { item1.Guid.Full, item1 },
                { item2.Guid.Full, item2 },
            };

            vendor.DepositItems(Owner, items, null);

            Assert.AreEqual(2, destroyCalls, "sanity: both items must have reached DestroyItem, or this test proves nothing about the second one");

            Assert.AreEqual(7L, backend.Stacks.Single(s => s.Wcid == 9410).Count,
                "sanity: BOTH items' units were credited together in phase B, before phase C destroyed either - 3 would mean only the first item's credit landed");

            Assert.AreEqual(1, world.Destroyed.Count(d => d == item1 || d == item2),
                "sanity: exactly one of the two items must have actually been destroyed, or the throw did not land where this test assumes it did");

            Assert.AreEqual(0, vendor.TryHandBackCallCount,
                "THE RULE THAT MATTERS MOST: neither item may be handed back. Both were credited in the same group before phase C ran, so even the one DestroyItem never reached for is already the vault's - handing it back on top of that credit is the dupe guarantee 1b exists to prevent.");
        }

        /// <summary>
        /// THE LOSS-DIRECTION HALF of the same throw arm, and the defect it was written for: withholding
        /// every item unconditionally is only correct when every item might have been credited, and in a
        /// batch that is false.
        ///
        /// An item a phase A guard refused is at Commit == None, and None is written by nothing downstream
        /// of a credit - the in-flight Unknown mark for a deferred group is written BEFORE the statement
        /// that credits it runs. So None after a throw provably means no credit was attempted for THAT
        /// item, and withholding it is not caution: the item is detached, undestroyed, in no container and
        /// never returned, which is the one state outside all three of guarantee 1's.
        ///
        /// The pack is the refused item (a container can never be vault content). The two stackables share
        /// one ledger group whose credit throws, so they are Unknown and must still be withheld - which is
        /// what makes this a SUBSET assertion rather than "hand everything back", the wrong fix in the
        /// other direction.
        ///
        /// Shown to FAIL against the pre-fix arm, which handed back nothing at all: the pack is then left
        /// an orphan and TryHandBackCallCount is 0.
        /// </summary>
        [TestMethod]
        public void DepositItems_WhenTheBatchThrows_HandsBackOnlyTheItemsNoCreditWasAttemptedFor()
        {
            var vendor = MakeRecordingVendor();
            var store = MakeStore(out var backend, out var world);
            vendor.Store = store;

            world.PristineResult = true;         // the two stackables take the ledger path
            backend.ThrowFromStackBatchAfterGroups = 0; // and the credit itself throws

            var pack = FakeVaultWorld.MakeContainer(itemsCapacity: 10);

            Assert.IsTrue(pack.UseBackpackSlot, "sanity: the refused fixture must actually trip the pack guard");
            Assert.IsNull(pack.ContainerId, "sanity: and it must be detached, or HandBackOrphan's own guard is what refuses it");

            var first = FakeVaultWorld.MakeStack(9420, 2, 100);
            var second = FakeVaultWorld.MakeStack(9420, 3, 100);

            var items = new Dictionary<uint, WorldObject>
            {
                { pack.Guid.Full, pack },
                { first.Guid.Full, first },
                { second.Guid.Full, second },
            };

            vendor.DepositItems(Owner, items, null);

            Assert.AreEqual(VaultDepositCommit.Unknown, store.LastDepositCommit,
                "sanity: the credit really was in flight when the throw arrived, or the two stackables are not in the state this test is about");

            Assert.AreEqual(1, vendor.TryHandBackCallCount,
                "exactly one hand-back: the item nothing was ever attempted for");

            CollectionAssert.Contains(vendor.HandedBack, (WorldObject)pack,
                "THE LOSS DIRECTION: an item a phase A guard refused is provably still the player's and must go home even though the batch later threw");

            CollectionAssert.DoesNotContain(vendor.HandedBack, (WorldObject)first,
                "and an item whose credit is UNKNOWN must still be withheld - nobody can prove it was not credited");

            CollectionAssert.DoesNotContain(vendor.HandedBack, (WorldObject)second);

            Assert.AreEqual(0, world.Destroyed.Count, "nothing is destroyed on a credit nobody can account for");
        }

        /// <summary>
        /// Fix round 2, F4(b), the withdraw mirror - and the one where doing nothing LEAKS rather than
        /// dupes.
        ///
        /// TryWithdraw debits the ledger and builds the replacement stacks before its tail runs, and it
        /// hands those stacks back through an OUT parameter, so they are visible to the caller even
        /// when the call itself does not complete. Read as a plain refusal, they were dropped on the
        /// floor: the ledger short, the objects live and held by nobody, and a live duplicate waiting
        /// for the next rehydration.
        ///
        /// The assertion is on the ledger, which is the mechanism: the unwind credits the units back.
        /// FakeVaultBackend.ThrowFromDeleteEmptyStacksCount is the throw site because it is the one
        /// step of WithdrawFromLedger that runs after withdrawn.AddRange(created).
        /// </summary>
        [TestMethod]
        public void WithdrawTransaction_WhenTheQueuedWorkThrows_ReturnsTheBuiltStacksToTheVault()
        {
            EnsureGuidManagerConstructible();
            SeedWorldStackableWeenie(9403, 100);

            var vendor = MakeVendor();
            var store = MakeStore(out var backend, out _);
            vendor.Store = store;

            SeedLedger(backend, 9403, 20);

            vendor.RebuildView();

            var display = vendor.DefaultItemsForSale.Values.Single(d => d.WeenieClassId == 9403);

            // Withdrawing the WHOLE row is what drives newCount to zero and so reaches the
            // opportunistic DeleteEmptyAccountVaultStacks call this test makes throw.
            backend.ThrowFromDeleteEmptyStacksCount = 1;

            var profiles = new List<ItemProfile> { new ItemProfile(20, display.Guid.Full) };

            Assert.IsFalse(vendor.TryWithdrawTransaction(profiles, Owner, null), "a withdrawal whose work threw must not report success");

            Assert.AreEqual(20L, backend.Stacks.Single(s => s.Wcid == 9403).Count,
                "the stacks the throwing withdraw had already built must be returned to the ledger. Dropped instead, they are live objects nobody holds while the ledger stays debited - which is the leak that becomes a live duplicate on the next rehydration.");
        }

        /// <summary>
        /// Fix round 2, F3: a timed item is refused at BOTH sites, with the SAME message.
        ///
        /// The pairing is the point, not a nicety. CanAcceptCore is the pre-flight consulted while the
        /// item is still in the player's pack; TryDeposit is the enforcing gate, reached only after
        /// Player_Commerce has already detached the item and flushed that state to the shard. A guard
        /// in only one of them either lets the exploit through (store only missing) or detaches an item
        /// for a deposit that is then refused (pre-flight only missing) - so this test drives both from
        /// one item and asserts the strings are identical rather than merely both non-empty.
        ///
        /// The exploit itself: collapse ignores CreationTimestamp by design, so a timed stackable
        /// collapses to a ledger row and comes back out as a fresh object with its clock restarted.
        /// See AccountVaultStore.TryDeposit's own remark.
        /// </summary>
        [TestMethod]
        public void TimedItem_IsRefusedByBothThePreflightAndTheStore_WithTheSameMessage()
        {
            var vendor = MakeVendor();
            var store = MakeStore(out _, out var world);
            vendor.Store = store;

            world.PristineResult = true;

            var timed = FakeVaultWorld.MakeStack(9300, 1, 100);
            timed.Lifespan = 300;

            Assert.IsFalse(vendor.CanAcceptCore(timed, Owner, out var preflightReason), "the pre-flight must refuse a timed item before it is ever detached from the player");

            var deposited = false;
            string storeReason = null;
            store.Enqueue(() => deposited = store.TryDeposit(timed, Owner, out storeReason));

            Assert.IsFalse(deposited, "the store's own gate must refuse it too - the pre-flight is TOCTOU by construction and is never the enforcing one");

            Assert.AreEqual(storeReason, preflightReason,
                "both sites must give the player the SAME reason. A pre-flight that refuses for a different stated cause than the store is how the two drift apart.");
            Assert.AreEqual("A timed item cannot be stored in your vault.", storeReason);

            // Control: the same item with no Lifespan passes both gates, so neither assertion above is
            // satisfied by a vault that simply refuses everything.
            var untimed = FakeVaultWorld.MakeStack(9300, 1, 100);

            Assert.IsTrue(vendor.CanAcceptCore(untimed, Owner, out var okReason), okReason);

            var ok = false;
            store.Enqueue(() => ok = store.TryDeposit(untimed, Owner, out okReason));
            Assert.IsTrue(ok, okReason);
        }

        /// <summary>
        /// A trade note (ItemType.PromissoryNote) must never become vault content: the store refuses
        /// it outright and nothing is written to the ledger.
        ///
        /// The reason lives on the WITHDRAW side. Withdrawal is free on the server, but the client's
        /// vendor panel gates every row on the player affording that row's Value in coin, and the
        /// client cannot be changed. A stored Trade Note (250,000) is therefore unrecoverable by
        /// anyone not already carrying 250,000 pyreals. PersonalVendor.DepositItems diverts a note to
        /// the player's banked pyreals before the store is consulted, so THIS guard is never reached
        /// by the vendor path - it is defence in depth for any other caller of TryDeposit.
        ///
        /// The control below is what makes the assertion mean something: the same stack with an
        /// ordinary ItemType, deposited into the same store, DOES land on the ledger. Without it,
        /// "nothing was stored" is satisfied by a store that stores nothing at all.
        /// </summary>
        [TestMethod]
        public void TradeNote_IsRefusedByTheStore_AndStoresNothing()
        {
            // Required even though this test constructs no vendor: the CONTROL deposit below reaches
            // AccountVaultStore.BaseEntryCap -> PropertyManager.GetLong, whose static cache is unprimed
            // when this test runs first in the process. That NREs inside queued work, single-arg
            // Enqueue swallows it, and the control silently reports ok=false - so run in isolation the
            // test failed on its own control while the guard assertion it exists for passed. See the
            // account_vault_entry_cap remark in EnsureVendorConstructible.
            EnsureVendorConstructible();

            var store = MakeStore(out var backend, out var world);

            world.PristineResult = true;

            var note = FakeVaultWorld.MakeStack(9500, 1, 1000);
            note.ItemType = ItemType.PromissoryNote;

            var deposited = false;
            string failReason = null;
            store.Enqueue(() => deposited = store.TryDeposit(note, Owner, out failReason));

            Assert.IsFalse(deposited, "the store must refuse a trade note");
            Assert.AreEqual("Trade notes are banked, not stored.", failReason);

            Assert.AreEqual(0, backend.Stacks.Count,
                "a refused trade note must leave no ledger row. A note that reaches the ledger is unwithdrawable in practice - the client's own affordability gate on the row's Value is what stops the player taking it back, and that gate is not ours to move.");

            Assert.IsFalse(note.IsDestroyed, "the store must not destroy a note it refused - the caller still owns it");

            // Control: identical stack, ordinary ItemType, same store. It IS stored, so the assertions
            // above are about the PromissoryNote guard and not about a store that refuses everything.
            var ordinary = FakeVaultWorld.MakeStack(9500, 1, 1000);

            var ok = false;
            string okReason = null;
            store.Enqueue(() => ok = store.TryDeposit(ordinary, Owner, out okReason));

            Assert.IsTrue(ok, okReason);
            Assert.AreEqual(1, backend.Stacks.Count, "control: an ordinary item of the same wcid must reach the ledger");
        }

        /// <summary>
        /// The deliberate ASYMMETRY, and the one place a store refusal is NOT mirrored in the
        /// pre-flight: CanAcceptCore must ACCEPT a trade note.
        ///
        /// Every other TryDeposit refusal has a copy in CanAcceptCore so an item is never detached
        /// from the player's pack for a deposit the store will reject. A note is the opposite case -
        /// it has to pass the pre-flight to reach the sell list at all, because being banked is the
        /// outcome the player wants. A copy of the store's guard here would refuse the note at the
        /// panel and make PersonalVendor.DepositItems' divert dead code, which is the exact regression
        /// this test exists to catch (the "keep the two gates in step" instinct is a strong one, and
        /// it is wrong here).
        /// </summary>
        [TestMethod]
        public void CanAccept_AcceptsATradeNote_SoTheDepositLoopCanDivertItToTheBank()
        {
            var vendor = MakeVendor();
            var store = MakeStore(out _, out var world);
            vendor.Store = store;

            world.PristineResult = true;

            var note = FakeVaultWorld.MakeStack(9501, 1, 1000);
            note.ItemType = ItemType.PromissoryNote;

            Assert.AreEqual(ItemType.PromissoryNote, note.ItemType, "sanity: this fixture must actually be a trade note");

            Assert.IsTrue(vendor.CanAcceptCore(note, Owner, out var reason),
                $"the pre-flight must accept a trade note so it reaches the sell list and can be banked by DepositItems. Refused with: {reason}");

            Assert.IsTrue(vendor.CanAcceptCore(note, Owner, VaultAccess.DepositWithdraw, out var passedAccessReason),
                $"the resolved-access overload must accept it too - that is the one the sell path actually calls. Refused with: {passedAccessReason}");
        }

        /// <summary>
        /// The DIVERT ITSELF is not executable here, and this scan is what covers it instead.
        ///
        /// PersonalVendor.DepositItems' trade-note branch calls Player.BankTradeNote, which credits a
        /// PropertyInt64 on a live Player, sends a network message and calls RushNextPlayerSave. No
        /// test in this project constructs a live Player (class remarks above, point 2), and there is
        /// no VaultActor-shaped seam to extract here because the whole point of the branch is that it
        /// acts on the PLAYER rather than on the store. Faking a Player to assert a balance moved
        /// would test the fake.
        ///
        /// What a source scan CAN pin is ORDER, which is the property that actually matters: the note
        /// must be diverted BEFORE the item is offered to the store. Diverted after, a note would be
        /// stored first and banked second - both a duplication and the unwithdrawable-row bug the
        /// whole change exists to remove. It cannot prove the branch is correct; it is named CallSite_
        /// for the same reason the other scans in this file are.
        /// </summary>
        [TestMethod]
        public void CallSite_DepositItems_DivertsATradeNoteBeforeOfferingItToTheStore()
        {
            const string relativePath = "Source/ACE.Server/WorldObjects/PersonalVendor.cs";

            var path = FindInSourceTree(relativePath);
            Assert.IsNotNull(path, $"Could not find {relativePath} by walking up from {AppContext.BaseDirectory}.");

            // Comments stripped, or every assertion below is satisfiable by a doc comment that merely
            // NAMES the divert (and this file's comments do name it, twice).
            var code = AccountVaultPurgeTests.StripComments(File.ReadAllText(path));

            var iMethod = code.IndexOf("internal void DepositItems(VaultActor actor, Dictionary<uint, WorldObject> items, Player player)", StringComparison.Ordinal);
            Assert.IsTrue(iMethod >= 0, "DepositItems was not found - if it was resignatured, update this test rather than deleting it.");

            var iEnqueue = code.IndexOf("activeStore.Enqueue(work", iMethod, StringComparison.Ordinal);
            Assert.IsTrue(iEnqueue > iMethod, "the store hand-off (activeStore.Enqueue(work...)) was not found inside DepositItems.");

            var iTest = code.IndexOf("item.ItemType == ItemType.PromissoryNote", iMethod, StringComparison.Ordinal);

            Assert.IsTrue(iTest >= 0 && iTest < iEnqueue,
                "DepositItems must test for ItemType.PromissoryNote BEFORE handing the item to the store. After it, the note is stored first and banked second - the player is credited for an item that is also sitting in their vault, unwithdrawable behind the client's affordability gate.");

            var iBank = code.IndexOf("BankTradeNote(", iMethod, StringComparison.Ordinal);

            Assert.IsTrue(iBank >= 0 && iBank < iEnqueue,
                "the divert must credit the note through Player.BankTradeNote. The credit deliberately lives in Player_Bank.cs beside DepositTradeNotes, because ModifyBankBalance and BankMsg are private there and a second copy of the credit sequence is a second place it can drift.");
        }

        /// <summary>
        /// F2: CanAcceptCore must give a RequiresPackSlot item (a Focusing Stone, in DESIGN's own
        /// example) the SAME message TryDeposit does - not the Container-only "store what is inside it"
        /// wording, which its owner cannot act on because there is nothing inside it. The pre-fix code's
        /// single unconditional pack message passed the existing four-cases test only because that test
        /// used a Container (MakeContainer), which the ternary's Container branch happens to match
        /// either way.
        /// </summary>
        [TestMethod]
        public void CanAccept_GivesAPackSlotItemTheNonContainerMessage()
        {
            var vendor = MakeVendor();
            var store = MakeStore(out _, out _);
            vendor.Store = store;

            var focusingStoneLike = FakeVaultWorld.MakeStack(9200, 1, 1);
            focusingStoneLike.RequiresPackSlot = true;

            Assert.IsTrue(focusingStoneLike.UseBackpackSlot, "sanity: RequiresPackSlot must make UseBackpackSlot true even though this is not a Container");
            Assert.AreNotEqual(WeenieType.Container, focusingStoneLike.WeenieType, "sanity: this must NOT be a Container, or it would exercise the other branch of the ternary");

            Assert.IsFalse(vendor.CanAcceptCore(focusingStoneLike, Owner, out var reason));
            Assert.AreEqual("That takes up a pack slot of its own and cannot be stored in your vault.", reason);
        }

        /// <summary>
        /// F4: a grant-read FAILURE must report UnavailableMessage, not the "no permission" string -
        /// TryGetAccess distinguishes the two (a database outage versus a genuine "no grant"), but the
        /// collapsed public GetAccess/IsLoaded pair CanAcceptCore used before this fix could not tell
        /// them apart, so a database outage told the player they had no permission, which is both wrong
        /// and needlessly alarming.
        /// </summary>
        [TestMethod]
        public void CanAccept_ReportsUnavailableRatherThanNoPermission_OnAGrantReadFailure()
        {
            var backend = new FakeVaultBackend();
            var world = new FakeVaultWorld();
            var store = new AccountVaultStore(OwnerAccount, backend, world);
            backend.FailGrantRead = true;

            var vendor = MakeVendor();
            vendor.Store = store;

            var item = FakeVaultWorld.MakeStack(9300, 1, 100);

            // Stranger (not the account owner) is the path that actually reads grants - the owner's own
            // access short-circuits before ever touching the grant list.
            Assert.IsFalse(vendor.CanAcceptCore(item, Stranger, out var reason));
            Assert.AreEqual(AccountVaultStore.UnavailableMessage, reason, "a grant-read failure must report UnavailableMessage, not the no-permission string");
        }

        /// <summary>
        /// Reconciliation fix (Task 12): TryAuthorize (gate 1) used AccountVaultStore.GetAccess, which
        /// collapses a grant-read FAILURE into VaultAccess.None - a database outage was reported to the
        /// player as "you do not have permission" rather than "unavailable". TryGetAccess distinguishes
        /// the two, matching CanAcceptCore's own pattern (F4).
        /// </summary>
        [TestMethod]
        public void TryAuthorize_ReportsUnavailableRatherThanNoPermission_OnAGrantReadFailure()
        {
            var backend = new FakeVaultBackend();
            var world = new FakeVaultWorld();
            var store = new AccountVaultStore(OwnerAccount, backend, world);
            backend.FailGrantRead = true;

            var vendor = MakeVendor();
            vendor.Store = store;

            // Stranger (not the account owner) is the path that actually reads grants - the owner's own
            // access short-circuits before ever touching the grant list.
            Assert.IsFalse(vendor.TryAuthorize(Stranger, out _, out var reason));
            Assert.AreEqual(AccountVaultStore.UnavailableMessage, reason, "a grant-read failure must report UnavailableMessage, not the no-permission string");
        }

        /// <summary>
        /// Reconciliation fix (Task 12): TryAuthorizeWithdraw (gate 2, extracted from
        /// TryWithdrawTransaction so this could be tested without a live Player - see the class remarks
        /// on Player constructibility and MuleSummonTests.cs's identical constraint) used
        /// AccountVaultStore.GetAccess, same collapsing bug as the other two sites.
        /// </summary>
        [TestMethod]
        public void TryAuthorizeWithdraw_ReportsUnavailableRatherThanNoPermission_OnAGrantReadFailure()
        {
            var backend = new FakeVaultBackend();
            var world = new FakeVaultWorld();
            var store = new AccountVaultStore(OwnerAccount, backend, world);
            backend.FailGrantRead = true;

            var vendor = MakeVendor();
            vendor.Store = store;

            // Stranger (not the account owner) is the path that actually reads grants - the owner's own
            // access short-circuits before ever touching the grant list.
            Assert.IsFalse(vendor.TryAuthorizeWithdraw(Stranger, out _, out var reason));
            Assert.AreEqual(AccountVaultStore.UnavailableMessage, reason, "a grant-read failure must report UnavailableMessage, not the no-permission string");
        }

        /// <summary>
        /// M5: without VendorShopCreateListStackSize = -1, the client presents one MaxStackSize stack
        /// per approach for a ledger row, so DESIGN 7.3's headline case (a 10,000-kit row) needs about
        /// 100 re-approaches to buy out. -1 lets the buy panel request an arbitrary amount;
        /// TryAdjustAccountVaultStack already adjudicates an over-request against what the ledger
        /// actually holds, so this is safe.
        /// </summary>
        [TestMethod]
        public void RebuildView_LedgerDisplays_AllowArbitraryStackSize()
        {
            EnsureGuidManagerConstructible();

            const uint ledgerWcid = 91600;
            SeedWorldStackableWeenie(ledgerWcid, 100);

            var vendor = MakeVendor();
            var store = MakeStore(out var backend, out _);
            vendor.Store = store;

            SeedLedger(backend, ledgerWcid, 10000);

            vendor.RebuildView();

            var display = vendor.DefaultItemsForSale.Values.First(d => d.WeenieClassId == ledgerWcid);
            Assert.AreEqual(-1, display.VendorShopCreateListStackSize, "a ledger display must allow an arbitrary buy amount, not be capped at one MaxStackSize per approach");
        }

        /// <summary>
        /// Important 1 from the whole-branch review. PersonalVendor.BuyItems_ValidateTransaction IS
        /// TryWithdrawTransaction, so false on the buy path means the withdrawal was REFUSED, not that
        /// the client sent junk - and one way it is refused is that a second window on the same store
        /// already drained the row this panel is still showing. Task 12 removed the only panel refresh
        /// on that branch, so the panel stayed wrong until the player walked out of range and back,
        /// while every retry cost two more permanent audit rows. ViewIsStale is what lets
        /// Player_Commerce re-approach for exactly that case and skip it for every refusal that changed
        /// nothing.
        /// </summary>
        [TestMethod]
        public void ViewIsStale_IsFalseAfterARebuild_AndTrueOnceTheStoreMutatesOffWindow()
        {
            EnsureGuidManagerConstructible();

            const uint ledgerWcid = 91700;
            SeedWorldStackableWeenie(ledgerWcid, 100);

            var vendor = MakeVendor();
            var store = MakeStore(out var backend, out var world);
            vendor.Store = store;

            SeedLedger(backend, ledgerWcid, 10);

            vendor.RebuildView();

            Assert.IsFalse(vendor.ViewIsStale, "a view rebuilt from the store's current version is not stale, so a refusal that changed nothing must cost no re-approach");

            // Mutated WITHOUT going through this window, which is exactly what a second vendor window on
            // the same store does. The deposit bumps the store's Version; nothing tells this window.
            world.PristineResult = true;

            var ok = false;
            store.Enqueue(() => ok = store.TryDeposit(FakeVaultWorld.MakeStack(ledgerWcid, 5, 100), Owner, out _));
            Assert.IsTrue(ok, "precondition: the off-window deposit must succeed, or the store never moved and this test proves nothing");

            Assert.IsTrue(vendor.ViewIsStale, "a window whose store has moved since its last rebuild is stale - this is what makes a refused withdraw re-approach and correct the panel instead of leaving it showing a row somebody else already drained");

            vendor.RebuildView();

            Assert.IsFalse(vendor.ViewIsStale, "and a rebuild clears it again");
        }

        /// <summary>
        /// F5: no other test covers the actual I1 bug site. ItemsForSaleCount_MatchesForEachItemEnumeration
        /// fails if the ItemsForSaleCount override is deleted, but reverting
        /// GameEventApproachVendor.cs's numItems expression back to the old
        /// DefaultItemsForSale.Count + UniqueItemsForSale.Count form leaves that test green, because
        /// nothing in this project actually constructs a live GameEventApproachVendor (that needs a
        /// live Player/Session). A source-level guard, in the style PropertyRegistryTests.cs already
        /// uses (walk up from AppContext.BaseDirectory), is the only thing that can catch the specific
        /// line regressing back to the old expression.
        /// </summary>
        [TestMethod]
        public void GameEventApproachVendor_UsesItemsForSaleCount_NotTheOldExpression()
        {
            const string relativePath = "Source/ACE.Server/Network/GameEvent/Events/GameEventApproachVendor.cs";

            var path = FindInSourceTree(relativePath);
            Assert.IsNotNull(path, $"Could not find {relativePath} by walking up from {AppContext.BaseDirectory}.");

            var content = File.ReadAllText(path);

            Assert.IsTrue(content.Contains("vendor.ItemsForSaleCount"), $"{relativePath} must compute numItems from vendor.ItemsForSaleCount - the line that fixed I1 (a stored-only mule rendering an empty panel).");
            Assert.IsFalse(content.Contains("UniqueItemsForSale.Count +"), $"{relativePath} must not re-derive numItems from the two base dictionaries directly - that expression never saw PersonalVendor's storeItems and is the I1 bug site.");
        }

        /// <summary>
        /// Fix round A, A10. THIS IS A SOURCE SCAN AND IT PROVES PLACEMENT, NOT BEHAVIOUR.
        ///
        /// Each of PersonalVendor's six authorization entry points is a one-line delegation to a
        /// testable core, and every test in this file drives the CORE directly - none goes through the
        /// override. So deleting the body of CheckUseRequirements and returning
        /// base.CheckUseRequirements(activator) removes gate 1 from production entirely and leaves the
        /// whole suite green. Same for the other five. RequiredOverrides_AreDeclaredOnPersonalVendorItself
        /// pins that the overrides EXIST; this pins that each one still routes into its core.
        ///
        /// BOTH CanAccept overloads are listed, and the FOUR-argument one is the load-bearing entry:
        /// Task 6 hoisted access resolution out of the per-item loop, so `Player_Commerce.cs`'s
        /// `vendor.CanAccept(` call site is the four-argument form and the two-argument one has no
        /// production caller left. Listing only the two-argument overload pinned the dead one and left
        /// the live deposit pre-flight free to be emptied with the whole suite still green - which is
        /// the exact failure this test exists to make impossible.
        ///
        /// What it cannot do is prove the core is called correctly, or with the right arguments. That is
        /// the deliberate limit of a source scan, and the reason it is named CallSite_ rather than
        /// after a behaviour.
        /// </summary>
        [TestMethod]
        public void CallSite_PersonalVendorOverrides_EachDelegateToTheirTestableCore()
        {
            const string relativePath = "Source/ACE.Server/WorldObjects/PersonalVendor.cs";

            var path = FindInSourceTree(relativePath);
            Assert.IsNotNull(path, $"Could not find {relativePath} by walking up from {AppContext.BaseDirectory}.");

            // Comments stripped for AccountVaultPurgeTests.StripComments' own stated reason: without it
            // every assertion below is satisfiable by a doc comment that merely NAMES the core.
            var code = AccountVaultPurgeTests.StripComments(File.ReadAllText(path));

            var pairs = new[]
            {
                ("public override ActivationResult CheckUseRequirements(WorldObject activator)", "TryAuthorize(", "gate 1 - use of the vendor at all"),
                ("public override bool CanAccept(WorldObject wo, Player player, out string reason)", "CanAcceptCore(", "the deposit pre-flight"),
                ("public override bool CanAccept(WorldObject wo, Player player, VaultAccess resolvedAccess, out string reason)", "CanAcceptCore(", "the deposit pre-flight as the sell path actually calls it"),
                ("public override void ApproachVendor(Player player, VendorType action = VendorType.Undef, uint altCurrencySpent = 0)", "TryPrepareApproach(", "the panel build, which is where storeItems is populated"),
                ("public override bool BuyItems_ValidateTransaction(List<ItemProfile> itemProfiles, Player player)", "TryWithdrawTransaction(", "gate 2 - the withdraw transaction"),
                ("public override void ProcessItemsForPurchase(Player player, Dictionary<uint, WorldObject> items)", "DepositItems(", "the deposit transaction"),
            };

            foreach (var (signature, delegatingCall, what) in pairs)
            {
                var iDecl = code.IndexOf(signature, StringComparison.Ordinal);

                Assert.IsTrue(iDecl >= 0,
                    $"{relativePath} must still declare `{signature}` - if it was renamed or resignatured, update this test rather than deleting it, because it is the only thing standing between a deleted override body and a green suite.");

                // Body bound: the next member declaration at class-member indentation. Brace matching
                // is deliberately not used - an interpolated string in a body can carry braces.
                var next = Regex.Match(code.Substring(iDecl + signature.Length), @"\n        (public|internal|private|protected)\s");

                var iEnd = next.Success ? iDecl + signature.Length + next.Index : code.Length;

                var iCall = code.IndexOf(delegatingCall, iDecl, StringComparison.Ordinal);

                Assert.IsTrue(iCall >= 0 && iCall < iEnd,
                    $"{signature} must call {delegatingCall} in its own body ({what}). Every test for this path drives the core directly, so an emptied override is invisible to all of them.");
            }
        }

        /// <summary>
        /// Fix round A, A1. THIS IS A SOURCE SCAN AND IT PROVES PLACEMENT, NOT BEHAVIOUR - no test in
        /// this project can drive Player_Inventory's handlers (no live Player). The behaviour itself is
        /// queued for a live test.
        ///
        /// The exploit it pins: PersonalVendor.TryGetItemForSale is overridden to also return the REAL
        /// persisted vault biotas, FindObject's LastUsedContainer branch resolves any guid through it
        /// and reports the vendor as rootOwner, and four of the five handlers that search
        /// LocationsICanMove refuse a vendor-rooted item. HandleActionGetAndWieldItem did not. The
        /// stored item's CurrentLandblock is null, so the detach ran against the vendor, failed, and
        /// execution FELL THROUGH to TryEquipObjectWithNetworking - which sets WielderId and never
        /// touches ContainerId. The item was then worn while still parented to the vault container, so
        /// the next load produced two live WorldObjects on one guid, and a deposit-only grantee could
        /// take a wieldable item out of someone else's vault without ever passing the withdraw check.
        ///
        /// Both halves are asserted, because either alone leaves it exploitable through some other
        /// route of the same shape.
        /// </summary>
        [TestMethod]
        public void CallSite_GetAndWieldItem_RefusesVendorRootedItems_AndFailsClosedOnADetachFailure()
        {
            const string relativePath = "Source/ACE.Server/WorldObjects/Player_Inventory.cs";

            var path = FindInSourceTree(relativePath);
            Assert.IsNotNull(path, $"Could not find {relativePath} by walking up from {AppContext.BaseDirectory}.");

            var code = AccountVaultPurgeTests.StripComments(File.ReadAllText(path));

            var iWield = code.IndexOf("public void HandleActionGetAndWieldItem(uint itemGuid, EquipMask wieldedLocation)", StringComparison.Ordinal);
            Assert.IsTrue(iWield >= 0, "HandleActionGetAndWieldItem was not found - if it was resignatured, update this test rather than deleting it.");

            var iDo = code.IndexOf("private bool DoHandleActionGetAndWieldItem(WorldObject item, Container fromContainer,", iWield, StringComparison.Ordinal);
            Assert.IsTrue(iDo > iWield, "DoHandleActionGetAndWieldItem was not found after HandleActionGetAndWieldItem - this test needs it to bound the first method's body.");

            var iGuard = code.IndexOf("rootOwner is Vendor", iWield, StringComparison.Ordinal);

            Assert.IsTrue(iGuard >= 0 && iGuard < iDo,
                "HandleActionGetAndWieldItem must refuse a vendor-rooted item, exactly as HandleActionPutItemInContainer, HandleActionStackableSplitToContainer and HandleActionStackableMerge all do. Without it a stored vault item can be equipped straight out of the vault panel, which duplicates it on the next load. Those four handlers carry a call-site test each. The fifth, HandleActionStackableSplitToWield, deliberately has none and is not hoisted: it makes a single FindObject call and its destination is always the player's own body, so its nesting condition is the single test stackRootOwner != this, which is TRUE whenever the source is vendor-rooted - unlike the two-sided disjuncts, it cannot be skipped for a vendor.");

            // Second half: the detach failure must return, not fall through. Scoped past iDo because
            // there is an identically worded refusal in an earlier handler in this same file.
            var iDetachMsg = code.IndexOf("\"TryRemoveFromInventory failed!\"", iDo, StringComparison.Ordinal);
            Assert.IsTrue(iDetachMsg >= 0, "DoHandleActionGetAndWieldItem's detach-failure branch was not found.");

            var iEquip = code.IndexOf("if (!TryEquipObjectWithNetworking(item, wieldedLocation))", iDetachMsg, StringComparison.Ordinal);
            Assert.IsTrue(iEquip > iDetachMsg, "the equip call was not found after the detach-failure branch.");

            var iReturn = code.IndexOf("return false;", iDetachMsg, StringComparison.Ordinal);

            Assert.IsTrue(iReturn >= 0 && iReturn < iEquip,
                "a failed TryRemoveFromInventory must return false, not fall through to TryEquipObjectWithNetworking. Equipping an item you failed to detach leaves it both wielded and still parented to its old container, which is a duplication on the next load - this is what turns any hole of the vendor-rooted shape from 'refused' into 'duped'.");
        }

        [TestMethod]
        public void CallSite_StackableSplitToContainer_RefusesVendorRootedEndpointsBeforeAnyBranch()
        {
            const string relativePath = "Source/ACE.Server/WorldObjects/Player_Inventory.cs";

            var path = FindInSourceTree(relativePath);
            Assert.IsNotNull(path, $"Could not find {relativePath} by walking up from {AppContext.BaseDirectory}.");

            var code = AccountVaultPurgeTests.StripComments(File.ReadAllText(path));

            var iMethod = code.IndexOf("public void HandleActionStackableSplitToContainer(", StringComparison.Ordinal);
            Assert.IsTrue(iMethod >= 0, "HandleActionStackableSplitToContainer was not found - if it was resignatured, update this test rather than deleting it.");

            // Anchored on the branch CONDITION, not its trailing comment: StripComments erases everything
            // from the first unquoted // to end of line, so a comment-text anchor can never be found here.
            var iBranch = code.IndexOf("if ((stackRootOwner == this && containerRootOwner != this)", iMethod, StringComparison.Ordinal);
            Assert.IsTrue(iBranch > iMethod, "the player-to-world branch was not found inside HandleActionStackableSplitToContainer.");

            var iGuard = code.IndexOf("stackRootOwner is Vendor", iMethod, StringComparison.Ordinal);

            Assert.IsTrue(iGuard >= 0 && iGuard < iBranch,
                "the vendor refusal must precede the player-to-world branch, not sit inside it. Nested, it is skipped whenever neither endpoint is the player - a split from a mule's ledger display row into a landblock container then mints real items while the ledger is never decremented.");

            var iContainerGuard = code.IndexOf("container is Vendor", iMethod, StringComparison.Ordinal);

            Assert.IsTrue(iContainerGuard >= 0 && iContainerGuard < iBranch,
                "the destination must be refused too. FindObject's Landblock arm returns without assigning rootOwner, so a client sending the vendor's own guid as the destination leaves containerRootOwner null and only a direct 'container is Vendor' test catches it.");
        }

        [TestMethod]
        public void CallSite_StackableMerge_RefusesBothVendorRootedEndpointsBeforeAnyBranch()
        {
            const string relativePath = "Source/ACE.Server/WorldObjects/Player_Inventory.cs";

            var path = FindInSourceTree(relativePath);
            Assert.IsNotNull(path, $"Could not find {relativePath} by walking up from {AppContext.BaseDirectory}.");

            var code = AccountVaultPurgeTests.StripComments(File.ReadAllText(path));

            var iMethod = code.IndexOf("public void HandleActionStackableMerge(", StringComparison.Ordinal);
            Assert.IsTrue(iMethod >= 0, "HandleActionStackableMerge was not found - if it was resignatured, update this test rather than deleting it.");

            // Anchored on the branch CONDITION, not its trailing comment: StripComments erases everything
            // from the first unquoted // to end of line, so a comment-text anchor can never be found here.
            var iBranch = code.IndexOf("if ((sourceStackRootOwner == this && targetStackRootOwner != this)", iMethod, StringComparison.Ordinal);
            Assert.IsTrue(iBranch > iMethod, "the player-to-world branch was not found inside HandleActionStackableMerge.");

            var iSource = code.IndexOf("sourceStackRootOwner is Vendor", iMethod, StringComparison.Ordinal);

            Assert.IsTrue(iSource >= 0 && iSource < iBranch,
                "the source refusal must precede the player-to-world branch. Nested, a merge with BOTH endpoints vendor-rooted skips it, and the partial-merge arm then adds free units to a real stored vault biota while the ledger display it came from costs nothing.");

            var iTarget = code.IndexOf("targetStackRootOwner is Vendor", iMethod, StringComparison.Ordinal);

            Assert.IsTrue(iTarget >= 0 && iTarget < iBranch,
                "the TARGET must be refused too. The original guard tested only the source, so merging a player's own stack INTO a disposable ledger display object passed - and the next RebuildView destroys that object, silently consuming the player's units with no ledger credit.");

            // The DIRECT-OBJECT disjuncts, which the split and put-item guards already carry as
            // `container is Vendor`. Note these substrings cannot be satisfied by the two root-owner
            // assertions above: "sourceStackRootOwner is Vendor" does not contain "sourceStack is
            // Vendor", because "sourceStack" is followed by "RootOwner" rather than by " is".
            var iSourceObject = code.IndexOf("sourceStack is Vendor", iMethod, StringComparison.Ordinal);

            Assert.IsTrue(iSourceObject >= 0 && iSourceObject < iBranch,
                "the source OBJECT must be refused as well as its root owner. FindObject's Landblock arm returns without assigning rootOwner, so a client naming the vendor's own guid arrives with a null root owner and passes every root-owner test.");

            var iTargetObject = code.IndexOf("targetStack is Vendor", iMethod, StringComparison.Ordinal);

            Assert.IsTrue(iTargetObject >= 0 && iTargetObject < iBranch,
                "and so must the target OBJECT. Without it a mergeToGuid naming the mule itself passed this guard and was refused only eight checks later by targetIsStackable - a Vendor is a Creature and so a Container, never a Stackable - which is exactly the 'closed by a check that exists for a different reason' condition this hoist was written to eliminate for the sibling handlers.");
        }

        [TestMethod]
        public void CallSite_PutItemInContainer_RefusesVendorRootedEndpointsBeforeAnyBranch()
        {
            const string relativePath = "Source/ACE.Server/WorldObjects/Player_Inventory.cs";

            var path = FindInSourceTree(relativePath);
            Assert.IsNotNull(path, $"Could not find {relativePath} by walking up from {AppContext.BaseDirectory}.");

            var code = AccountVaultPurgeTests.StripComments(File.ReadAllText(path));

            var iMethod = code.IndexOf("public void HandleActionPutItemInContainer(uint itemGuid, uint containerGuid, int placement = 0)", StringComparison.Ordinal);
            Assert.IsTrue(iMethod >= 0, "HandleActionPutItemInContainer was not found - if it was resignatured, update this test rather than deleting it.");

            // Anchored on the branch CONDITION, not its trailing comment: StripComments erases everything
            // from the first unquoted // to end of line, so a comment-text anchor can never be found here.
            var iBranch = code.IndexOf("if ((itemRootOwner == this && containerRootOwner != this)", iMethod, StringComparison.Ordinal);
            Assert.IsTrue(iBranch > iMethod, "the player-to-world branch was not found inside HandleActionPutItemInContainer.");

            var iGuard = code.IndexOf("itemRootOwner is Vendor", iMethod, StringComparison.Ordinal);

            Assert.IsTrue(iGuard >= 0 && iGuard < iBranch,
                "the vendor refusal must precede the player-to-world branch. It is currently closed only as a side effect of the later container-ownership check, which is not a guarantee this handler owns.");

            var iContainerGuard = code.IndexOf("container is Vendor", iMethod, StringComparison.Ordinal);

            Assert.IsTrue(iContainerGuard >= 0 && iContainerGuard < iBranch,
                "the destination must be refused too, for the same reason as the split handler: FindObject's Landblock arm leaves containerRootOwner null when the vendor's own guid is the destination.");
        }

        /// <summary>
        /// Same walk-up idiom as PropertyRegistryTests.cs's FindInSourceTree (kept private to that file,
        /// so this is a local copy rather than a cross-test-file dependency).
        /// </summary>
        private static string FindInSourceTree(string relativePath)
        {
            var native = relativePath.Replace('/', System.IO.Path.DirectorySeparatorChar);

            for (var dir = new System.IO.DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
            {
                var candidate = System.IO.Path.Combine(dir.FullName, native);

                if (File.Exists(candidate))
                    return candidate;
            }

            return null;
        }

        /// <summary>
        /// M6: without this, deleting any one of PersonalVendor's one-line overrides sends production
        /// back to retail Vendor behaviour - which destroys stackables and calls
        /// RemoveBiotaFromDatabase - while every test named for that override stays green, because
        /// those tests exercise the METHOD (which C#'s virtual dispatch would still route to the
        /// deleted override's absence, i.e. straight to Vendor's own implementation) rather than
        /// asserting WHERE it is declared. Mirrors VendorVirtualizationTests.cs's pinning idiom, applied
        /// to PersonalVendor's own overrides instead of Vendor's virtuals.
        /// </summary>
        [TestMethod]
        public void RequiredOverrides_AreDeclaredOnPersonalVendorItself()
        {
            const BindingFlags Any = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly;

            var required = new[]
            {
                "LoadInventory",
                "PrepareResetToHome",
                "RotUniques",
                "forEachItem",
                "TryGetItemForSale",
                "GetSellCost",
                "GetBuyCost",
                "CalculatePayoutCoinAmount",
                "ProcessItemsForPurchase",
                "BuyItems_ValidateTransaction",
                "ApproachVendor",
                "AddDefaultItem",
                "CheckUseRequirements",
                "CanAccept",
            };

            foreach (var name in required)
            {
                // GetMethod(name, ...) throws AmbiguousMatchException for GetSellCost/GetBuyCost, each
                // of which has two overloads (WorldObject and Weenie) - GetMethods().Where(...) handles
                // both the single- and multi-overload cases uniformly.
                var methods = typeof(PersonalVendor).GetMethods(Any).Where(m => m.Name == name).ToList();
                Assert.IsTrue(methods.Count > 0, $"PersonalVendor.{name} not found declared directly on PersonalVendor (BindingFlags.DeclaredOnly) - has it been removed or pulled up?");

                foreach (var method in methods)
                    Assert.AreEqual(typeof(PersonalVendor), method.DeclaringType, $"PersonalVendor.{name} must be declared on PersonalVendor itself, not inherited");
            }

            // ItemsForSaleCount is a property, not a method - GetMethods/GetMethod would look for a
            // method literally named "ItemsForSaleCount" and find nothing (the compiled name is
            // get_ItemsForSaleCount), so it cannot be folded into the loop above.
            var itemsForSaleCount = typeof(PersonalVendor).GetProperty("ItemsForSaleCount", Any);
            Assert.IsNotNull(itemsForSaleCount, "PersonalVendor.ItemsForSaleCount not found declared directly on PersonalVendor");
            Assert.AreEqual(typeof(PersonalVendor), itemsForSaleCount.GetGetMethod(true).DeclaringType, "PersonalVendor.ItemsForSaleCount's getter must be declared on PersonalVendor itself");
        }

        /// <summary>
        /// Coordinator addendum (post-summon-task): RestampRot must read account_vault_summon_rot_seconds
        /// rather than a hardcoded placeholder, so a mid-life re-stamp can never diverge from the
        /// lifetime MuleSummonHandler.TrySummon stamps at summon time. BEFORE the fix (RestampRot still
        /// assigning the deleted PlaceholderRotSeconds constant of 300): this assertion fails, because
        /// 123 != 300 - see task-7-report.md for the exact run.
        /// </summary>
        [TestMethod]
        public void RestampRot_HonoursTheConfiguredRotSeconds()
        {
            // Build the vendor BEFORE setting the key: the first MakeVendor in a process runs
            // EnsureVendorConstructible, which seeds this key to 600 and would overwrite the value
            // under test (the test failed when run alone and passed only after another test had
            // already done that one-time setup).
            var vendor = MakeVendor();

            try
            {
                Assert.IsTrue(PropertyManager.ModifyLong("account_vault_summon_rot_seconds", 123),
                    "account_vault_summon_rot_seconds is missing from DefaultLongProperties");

                vendor.RestampRot();

                Assert.AreEqual(123.0, vendor.TimeToRot);
            }
            finally
            {
                PropertyManager.ModifyLong("account_vault_summon_rot_seconds", 600);
            }
        }

        /// <summary>
        /// A misconfigured non-positive value must fail toward a SAFE SHORT lifetime, never toward
        /// "never rot" - WorldObject_Decay.cs treats a TimeToRot of exactly -1 as Never Rot, so passing
        /// a misconfigured -1 (or 0, or any other non-positive value) straight through would make an
        /// abandoned mule immortal rather than short-lived, which also permanently pins its store's
        /// OpenWindows count for as long as it lives.
        /// </summary>
        [TestMethod]
        public void RestampRot_FallsBackToASafeShortLifetime_WhenConfiguredNonPositive()
        {
            // Vendor first, then the key - see RestampRot_HonoursTheConfiguredRotSeconds. Run alone,
            // the old order let the one-time seed replace -1 with 600, so this passed without ever
            // exercising the fallback.
            var vendor = MakeVendor();

            try
            {
                Assert.IsTrue(PropertyManager.ModifyLong("account_vault_summon_rot_seconds", -1),
                    "account_vault_summon_rot_seconds is missing from DefaultLongProperties");

                vendor.RestampRot();

                Assert.IsTrue(vendor.TimeToRot > 0, "a misconfigured non-positive rot value must never result in Never Rot (-1) or instant rot (0)");
                Assert.AreNotEqual(600.0, vendor.TimeToRot, "the configured -1 never reached RestampRot - the fallback was not exercised");
            }
            finally
            {
                PropertyManager.ModifyLong("account_vault_summon_rot_seconds", 600);
            }
        }

        #region Hidden-ItemType display proxies (repo owner live test: stored salvage invisible in the panel)

        /// <summary>Overwrites the ItemType on a weenie already in the world cache.</summary>
        private static void SetSeededWeenieItemType(uint wcid, ItemType itemType)
        {
            var field = typeof(WorldDatabaseWithEntityCache).GetField("weenieCache", BindingFlags.NonPublic | BindingFlags.Instance);
            var dict = (ConcurrentDictionary<uint, Weenie>)field.GetValue(DatabaseManager.World);

            dict[wcid].PropertiesInt[PropertyInt.ItemType] = (int)itemType;
        }

        /// <summary>Seeds a stackable weenie in the world cache AND stamps the hidden ItemType on it, so a
        /// proxy materialized from this wcid starts out just as undrawable as the item it stands for -
        /// which is what makes StampForDisplay's work visible rather than accidentally already true.</summary>
        private static void SeedWorldTinkeringMaterialWeenie(uint wcid, int maxStackSize = 100)
        {
            SeedWorldStackableWeenie(wcid, maxStackSize);
            SetSeededWeenieItemType(wcid, ItemType.TinkeringMaterial);
        }

        /// <summary>
        /// Every ItemType no vendor anywhere in ace_world stocks. TinkeringMaterial is the one confirmed
        /// in play; the other seven are in on the repo owner's ruling to proxy unconfirmed types for
        /// safety, since the cost of a wrong inclusion is one row's appraisal detail and the cost of a
        /// wrong omission is an item nobody can get back out of their vault.
        /// </summary>
        [TestMethod]
        public void NeedsDisplayProxy_IsTrueForEveryTypeNoVendorStocks()
        {
            Assert.IsTrue(PersonalVendor.NeedsDisplayProxy(ItemType.TinkeringMaterial), "the type the panel was observed to file under no category");
            Assert.IsTrue(PersonalVendor.NeedsDisplayProxy(ItemType.Creature));
            Assert.IsTrue(PersonalVendor.NeedsDisplayProxy(ItemType.Portal));
            Assert.IsTrue(PersonalVendor.NeedsDisplayProxy(ItemType.Lockable));
            Assert.IsTrue(PersonalVendor.NeedsDisplayProxy(ItemType.MagicWieldable));
            Assert.IsTrue(PersonalVendor.NeedsDisplayProxy(ItemType.CraftFletchingBase));
            Assert.IsTrue(PersonalVendor.NeedsDisplayProxy(ItemType.LifeStone));
            Assert.IsTrue(PersonalVendor.NeedsDisplayProxy(ItemType.Gameboard));
        }

        /// <summary>
        /// The negative half, and the one that matters most: this rule runs over EVERY stored item, so
        /// over-matching would cost ordinary items their real appraisal for nothing. All 23 ItemTypes
        /// that retail vendor create lists actually stock are asserted, not a sample - a missing entry
        /// here is exactly the silent regression this test exists to catch. Misc is the one to watch:
        /// it is what a proxy is stamped WITH, so if it were ever hidden, proxying would be an infinite
        /// regress rather than a fix.
        /// </summary>
        [TestMethod]
        public void NeedsDisplayProxy_IsFalseForEveryTypeVendorsStock()
        {
            var stocked = new[]
            {
                ItemType.MeleeWeapon, ItemType.Armor, ItemType.Clothing, ItemType.Jewelry,
                ItemType.Food, ItemType.Money, ItemType.Misc, ItemType.MissileWeapon,
                ItemType.Container, ItemType.Useless, ItemType.Gem, ItemType.SpellComponents,
                ItemType.Writable, ItemType.Key, ItemType.Caster, ItemType.PromissoryNote,
                ItemType.ManaStone, ItemType.Service, ItemType.CraftCookingBase,
                ItemType.CraftAlchemyBase, ItemType.CraftAlchemyIntermediate,
                ItemType.CraftFletchingIntermediate, ItemType.TinkeringTool,
            };

            foreach (var itemType in stocked)
                Assert.IsFalse(PersonalVendor.NeedsDisplayProxy(itemType), $"{itemType} is stocked by retail vendors and demonstrably renders");

            Assert.AreEqual(23, stocked.Length, "the stocked set was counted from ace_world; a change here should be a deliberate re-count");
        }

        /// <summary>
        /// ItemType is a [Flags] enum and combinations really do occur - ace_world holds two weenies at
        /// 63, MeleeWeapon|Armor|Clothing|Jewelry|Creature|Food. Creature is hidden, so a "carries a
        /// hidden bit" rule would proxy those; the panel can file them as a melee weapon perfectly
        /// well. This is the case that decided the rule's direction: ask whether ANY renderable bit is
        /// present, not whether any hidden one is.
        /// </summary>
        [TestMethod]
        public void NeedsDisplayProxy_IsFalseWhenACombinationCarriesOneRenderableBit()
        {
            var combined = ItemType.MeleeWeapon | ItemType.Armor | ItemType.Clothing | ItemType.Jewelry | ItemType.Creature | ItemType.Food;

            Assert.AreEqual(63, (int)combined, "this is the real combination found in ace_world, not an invented one");
            Assert.IsFalse(PersonalVendor.NeedsDisplayProxy(combined), "a combination the client can file under any one of its bits must be left alone");
            Assert.IsTrue(PersonalVendor.NeedsDisplayProxy(ItemType.Creature | ItemType.Portal), "a combination of hidden bits only is still hidden");
        }

        /// <summary>
        /// WorldObject.ItemType is NON-nullable - it reads `(ItemType)(GetProperty(...) ?? 0)` - so an
        /// item with the property unset arrives as None rather than null, and 68 weenies in ace_world
        /// carry ItemType 0. No bits means no tab, so it needs a proxy like any other hidden type.
        /// </summary>
        [TestMethod]
        public void NeedsDisplayProxy_IsTrueForNone()
        {
            Assert.IsTrue(PersonalVendor.NeedsDisplayProxy(ItemType.None));
            Assert.IsTrue(PersonalVendor.NeedsDisplayProxy(null), "a nullable with no value is the same unfilable case as None");
        }

        /// <summary>
        /// The reported case: a stored item of a hidden ItemType must reach the panel as a drawable row.
        ///
        /// Three things are asserted together because any two of them pass with the third broken. The
        /// row must EXIST (before this change there was none), it must be stamped with a drawable
        /// ItemType (a row that exists and is still TinkeringMaterial is the bug, unchanged), and it
        /// must NOT be the stored biota itself - putting a real vault item into DefaultItemsForSale is
        /// precisely what this class's central invariant forbids, since WorldObject.Destroy walks that
        /// dictionary and a mule is destroyed on every logout.
        /// </summary>
        [TestMethod]
        public void RebuildView_ProxiesAStoredItemOfAHiddenItemType()
        {
            EnsureGuidManagerConstructible();
            SeedWorldTinkeringMaterialWeenie(7901);

            var vendor = MakeVendor();
            var store = MakeStore(out var backend, out var world);
            vendor.Store = store;

            var (_, item) = SeedStoredItem(backend, world, 7901);
            item.ItemType = ItemType.TinkeringMaterial;
            item.Name = "Full Bag of Tiger Eye Salvage";

            vendor.RebuildView();

            var row = vendor.DefaultItemsForSale.Values.Single();

            Assert.AreEqual(ItemType.Misc, row.ItemType, "the row the client is sent must carry a drawable ItemType");
            Assert.AreEqual("Full Bag of Tiger Eye Salvage", row.Name, "the row must read as the item it stands for");
            Assert.AreNotEqual(item.Guid, row.Guid, "the stored biota itself must never enter DefaultItemsForSale - Destroy walks it");
            Assert.AreEqual(1, vendor.ItemsForSaleCount, "one stored item is one row, never both a proxy and a bare stored entry");

            // The invariant the whole proxy mechanism exists to protect, and the one every other
            // assertion here would still pass without. Every custom crafting intercept matches on
            // ItemType plus MaterialType, so an edit that stamped the REAL object instead of the proxy
            // would break crafting for it permanently while leaving the panel looking perfectly fixed.
            // Flagged by code review as the diff's most safety-critical claim and its only unverified
            // one. To confirm this assertion bites, stamp entry.WorldObject instead of proxy in
            // TryCreateDisplayProxy and watch it fail.
            Assert.AreEqual(ItemType.TinkeringMaterial, item.ItemType, "the stored biota's own ItemType must never be touched");
        }

        /// <summary>
        /// The control. An ordinary stored item must go on taking the untouched path - no proxy, no
        /// second guid, and its real biota reachable through TryGetItemForSale as before. Without this
        /// the proxy test above would still pass with the rule inverted to "proxy everything".
        /// </summary>
        [TestMethod]
        public void RebuildView_DoesNotProxyAStoredItemThePanelAlreadyDraws()
        {
            EnsureGuidManagerConstructible();
            SeedWorldStackableWeenie(7902, 100);

            var vendor = MakeVendor();
            var store = MakeStore(out var backend, out var world);
            vendor.Store = store;

            var (_, item) = SeedStoredItem(backend, world, 7902);
            item.ItemType = ItemType.Misc;

            vendor.RebuildView();

            Assert.AreEqual(0, vendor.DefaultItemsForSale.Count, "a drawable stored item needs no display object at all");
            Assert.AreEqual(1, vendor.ItemsForSaleCount);
            Assert.IsTrue(vendor.TryGetItemForSale(item.Guid, out var found));
            Assert.AreSame(item, found, "a drawable stored item must still resolve to its own biota");
        }

        /// <summary>
        /// A proxy built from the bare weenie would show none of what distinguishes one salvage bag
        /// from another - a player looking at three rows would have to withdraw each to find out which
        /// is which. These are the properties a bag's appraisal is actually made of.
        /// </summary>
        [TestMethod]
        public void RebuildView_ProxyCarriesTheStoredInstancesIdentity()
        {
            EnsureGuidManagerConstructible();
            SeedWorldTinkeringMaterialWeenie(7903);

            var vendor = MakeVendor();
            var store = MakeStore(out var backend, out var world);
            vendor.Store = store;

            var (_, item) = SeedStoredItem(backend, world, 7903);
            // SetStackSize FIRST here for the same reason TryCreateDisplayProxy does it first: it
            // recomputes Value from the weenie's per-unit figure, so setting Value before it would
            // silently discard the 640 and this test would be asserting against its own bug.
            item.SetStackSize(1);

            item.ItemType = ItemType.TinkeringMaterial;
            item.MaterialType = MaterialType.TigerEye;
            item.ItemWorkmanship = 8;
            item.Structure = 73;
            item.MaxStructure = 100;
            item.Value = 640;

            vendor.RebuildView();

            var row = vendor.DefaultItemsForSale.Values.Single();

            Assert.AreEqual(MaterialType.TigerEye, row.MaterialType);
            Assert.AreEqual(8, row.ItemWorkmanship);
            Assert.AreEqual((ushort)73, row.Structure, "units are the whole point of telling two salvage bags apart");
            Assert.AreEqual((ushort)100, row.MaxStructure);
            Assert.AreEqual(640, row.Value);
        }

        /// <summary>
        /// The regression guard for the "no workmanship in the vault" defect, reported in play
        /// 2026-08-31: the same bag read "Nearly flawless (6.42)" and "Salvaged from 12 items" in the
        /// pack, and "Workmanship: crafted (0)" with no items line inside the vault panel.
        ///
        /// <see cref="RebuildView_ProxyCarriesTheStoredInstancesIdentity"/> above could not catch it,
        /// and that is the lesson rather than an aside: it seeds ItemWorkmanship = 8 with NO
        /// NumItemsInMaterial, a shape no real bag has. Every real bag sets both
        /// (Player_Crafting.cs:249-286), and WorldObject.Workmanship is
        /// ItemWorkmanship / (NumItemsInMaterial ?? 1), so a proxy carrying only the first computed 77
        /// instead of 6.42, tripped that getter's out-of-range recovery branch
        /// (WorldObject_Properties.cs:1587-1598), and had its own ItemWorkmanship rewritten to 0.
        ///
        /// Workmanship is read BEFORE ItemWorkmanship is asserted, deliberately: the recovery branch
        /// only fires when something reads the getter, so asserting the raw property first would pass
        /// against the bug.
        /// </summary>
        [TestMethod]
        public void RebuildView_ProxyCarriesTheWorkmanshipOfARealBagShape()
        {
            EnsureGuidManagerConstructible();
            SeedWorldTinkeringMaterialWeenie(7907);

            var vendor = MakeVendor();
            var store = MakeStore(out var backend, out var world);
            vendor.Store = store;

            var (_, item) = SeedStoredItem(backend, world, 7907);
            item.SetStackSize(1);

            item.ItemType = ItemType.TinkeringMaterial;
            item.MaterialType = MaterialType.TigerEye;
            item.Structure = 100;
            item.MaxStructure = 100;
            item.ItemWorkmanship = 77;
            item.NumItemsInMaterial = 12;

            Assert.AreEqual(6.42f, item.Workmanship.Value, 0.005f,
                "precondition: the stored bag itself reads 6.42, so anything else on the proxy is the proxy's fault");

            vendor.RebuildView();

            var row = vendor.DefaultItemsForSale.Values.Single();

            // The player-visible symptom first, then the two properties behind it, so a control run
            // fails on the thing that was actually reported rather than on a supporting detail.
            Assert.IsNotNull(row.Workmanship, "a bag with a workmanship must not present without one");
            Assert.AreEqual(6.42f, row.Workmanship.Value, 0.005f, "the proxy must render the same number the bag does in the pack");

            Assert.AreEqual(77, row.ItemWorkmanship,
                "reading Workmanship must not have tripped the out-of-range recovery branch and zeroed this");

            Assert.AreEqual(12, row.NumItemsInMaterial, "the appraisal's \"Salvaged from N items\" line comes from this property");
        }

        /// <summary>
        /// A pristine bag of salvage collapses into the LEDGER rather than being stored as a biota, so
        /// stamping only the stored branch would leave the commonest salvage case exactly as invisible
        /// as it was. The ledger display is materialized from the same wcid and inherits its ItemType.
        /// </summary>
        [TestMethod]
        public void RebuildView_StampsALedgerRowOfAHiddenItemType()
        {
            EnsureGuidManagerConstructible();
            SeedWorldTinkeringMaterialWeenie(7904);

            var vendor = MakeVendor();
            var store = MakeStore(out var backend, out _);
            vendor.Store = store;

            SeedLedger(backend, 7904, 3);

            vendor.RebuildView();

            var row = vendor.DefaultItemsForSale.Values.Single(d => d.WeenieClassId == 7904);

            Assert.AreEqual(ItemType.Misc, row.ItemType, "a collapsed stack of a hidden type is just as undrawable as a stored one");
            StringAssert.Contains(row.Name, "(3 in vault)", "stamping the type must not cost the row its count label");
        }

        /// <summary>
        /// The buy the client sends back carries the PROXY's guid, not the stored biota's, so the
        /// withdraw path has to resolve it - and has to do so BEFORE its DefaultItemsForSale branch,
        /// where a proxy also lives. Falling through to that branch would build a ledger request for a
        /// wcid that has no ledger row, which is why a true result here discriminates: it can only be
        /// reached by resolving the proxy back to the stored item.
        ///
        /// With a null player the withdrawn item has nowhere to be delivered and is handed back to the
        /// vault, so `ok` reports the resolution and authorization, exactly as
        /// BuyItems_ValidateTransaction_PositiveControl does.
        /// </summary>
        [TestMethod]
        public void ProxiedWithdrawal_ResolvesTheProxyGuidToTheStoredItem()
        {
            EnsureGuidManagerConstructible();
            SeedWorldTinkeringMaterialWeenie(7905);

            var vendor = MakeVendor();
            var store = MakeStore(out var backend, out var world);
            vendor.Store = store;

            var (_, item) = SeedStoredItem(backend, world, 7905);
            item.ItemType = ItemType.TinkeringMaterial;
            item.SetStackSize(1);

            vendor.RebuildView();

            var proxyGuid = vendor.DefaultItemsForSale.Values.Single().Guid;
            Assert.AreNotEqual(item.Guid, proxyGuid, "the test is meaningless if the guids happen to match");

            var basket = new List<ItemProfile> { new ItemProfile(1, proxyGuid.Full) };

            var ok = vendor.TryWithdrawTransaction(basket, Owner, null);

            Assert.IsTrue(ok, "a buy against the proxy's guid must resolve to the stored biota behind it");
        }

        /// <summary>
        /// RebuildView runs on every approach, and the proxy map is rebuilt with it. A map that grew
        /// instead of being cleared would leave dead proxy guids resolving to items no longer in the
        /// vault - the same class of stale-reference bug the Store setter clears storeItems to avoid.
        /// </summary>
        [TestMethod]
        public void RebuildView_ProxyDoesNotAccumulateAcrossRebuilds()
        {
            EnsureGuidManagerConstructible();
            SeedWorldTinkeringMaterialWeenie(7906);

            var vendor = MakeVendor();
            var store = MakeStore(out var backend, out var world);
            vendor.Store = store;

            var (_, item) = SeedStoredItem(backend, world, 7906);
            item.ItemType = ItemType.TinkeringMaterial;

            vendor.RebuildView();
            vendor.RebuildView();
            vendor.RebuildView();

            Assert.AreEqual(1, vendor.DefaultItemsForSale.Count, "one stored item must be one row however many times the panel is rebuilt");
            Assert.AreEqual(1, vendor.ItemsForSaleCount);
        }

        #endregion

        #region Grouped salvage rows (Docs/MuleVendor/2026-08-31-vault-grouping-design.md)

        /// <summary>Seeds one vault container holding exactly these objects, in this order.</summary>
        private static Container SeedVaultHolding(FakeVaultBackend backend, FakeVaultWorld world, params WorldObject[] items)
        {
            var container = FakeVaultWorld.MakeContainer(255);
            world.Containers[container.Guid.Full] = container;

            backend.Vaults.Add(new ShardAccountVault
            {
                Id = 1,
                AccountId = OwnerAccount,
                ContainerGuid = container.Guid.Full,
                CreatedAt = new DateTime(2026, 1, 1),
            });

            foreach (var item in items)
                Assert.IsTrue(container.TryAddToInventory(item), "could not seed a vault item");

            return container;
        }

        private static WorldObject Bag(uint wcid, int structure = 100, int itemWorkmanship = 77, int numItemsInMaterial = 12, int value = 640, string name = null)
        {
            return FakeVaultWorld.MakeSalvageBag(wcid, structure, itemWorkmanship, numItemsInMaterial, value, name);
        }

        /// <summary>
        /// The headline of the grouping design: hundreds of equivalent bags become a handful of legible
        /// rows. Three things are asserted together because any two pass with the third broken - there
        /// must be exactly ONE row, it must carry the true count where the player can read it, and it
        /// must still be a proxy rather than one of the stored biotas.
        /// </summary>
        [TestMethod]
        public void RebuildView_EquivalentBagsAreOneRowLabelledWithTheirCount()
        {
            EnsureGuidManagerConstructible();
            SeedWorldTinkeringMaterialWeenie(7920);

            var vendor = MakeVendor();
            var store = MakeStore(out var backend, out var world);
            vendor.Store = store;

            var bags = new[] { Bag(7920), Bag(7920), Bag(7920) };

            SeedVaultHolding(backend, world, bags);

            vendor.RebuildView();

            var row = vendor.DefaultItemsForSale.Values.Single();

            Assert.AreEqual(1, vendor.ItemsForSaleCount, "three equivalent bags are ONE panel row");
            StringAssert.Contains(row.Name, "(3 in vault)", "the count has to reach the player, and the Name is the field the client renders verbatim");
            Assert.IsFalse(bags.Any(b => b.Guid == row.Guid), "the row must be a proxy, never one of the stored biotas - Destroy walks DefaultItemsForSale");
        }

        /// <summary>
        /// A group of N is divisible even though each member is not, so the panel must be allowed to
        /// request an arbitrary k. The control below is the half that matters: a LONE stored biota must
        /// keep its old behaviour, because it really is indivisible and a -1 there would let the panel
        /// ask for an amount the withdraw path then refuses.
        /// </summary>
        [TestMethod]
        public void RebuildView_AGroupRowIsDivisibleAndASingletonIsNot()
        {
            EnsureGuidManagerConstructible();
            SeedWorldTinkeringMaterialWeenie(7921);

            var vendor = MakeVendor();
            var store = MakeStore(out var backend, out var world);
            vendor.Store = store;

            // One group of two, plus a bag that cannot join it (different Structure, so a different
            // bucket key) standing alone.
            SeedVaultHolding(backend, world, Bag(7921), Bag(7921), Bag(7921, structure: 73));

            vendor.RebuildView();

            Assert.AreEqual(2, vendor.DefaultItemsForSale.Count, "two rows: the group and the odd one out");

            var group = vendor.DefaultItemsForSale.Values.Single(d => d.Name.Contains("in vault"));
            var singleton = vendor.DefaultItemsForSale.Values.Single(d => !d.Name.Contains("in vault"));

            Assert.AreEqual(-1, group.VendorShopCreateListStackSize, "the panel must be able to ask for any k of a group");
            Assert.IsNull(singleton.VendorShopCreateListStackSize, "a lone stored biota is indivisible and must not advertise otherwise");
        }

        /// <summary>
        /// The buy the client sends back carries the GROUP PROXY's guid and an amount meaning "how many
        /// members". A true result here can only be reached by resolving that one guid to k separate
        /// stored biotas, and the audit rows are what prove k of them actually moved: with a null
        /// player they are all handed straight back to the vault, so the vault count alone would look
        /// identical whether one member moved or three did.
        /// </summary>
        [TestMethod]
        public void GroupWithdrawal_ResolvesOneProxyGuidToKMembers()
        {
            EnsureGuidManagerConstructible();
            SeedWorldTinkeringMaterialWeenie(7922);

            var vendor = MakeVendor();
            var store = MakeStore(out var backend, out var world);
            vendor.Store = store;

            SeedVaultHolding(backend, world, Bag(7922), Bag(7922), Bag(7922), Bag(7922));

            vendor.RebuildView();

            var proxyGuid = vendor.DefaultItemsForSale.Values.Single().Guid;

            var basket = new List<ItemProfile> { new ItemProfile(3, proxyGuid.Full) };

            Assert.IsTrue(vendor.TryWithdrawTransaction(basket, Owner, null),
                "a buy of 3 against a group proxy must resolve to three of its members");

            Assert.AreEqual(3, backend.Logs.Count(l => l.Action == (int)AccountVaultAction.Withdraw),
                "exactly three members must have been withdrawn - one audit row each");
        }

        [TestMethod]
        public void GroupWithdrawal_MoreThanTheGroupHolds_IsRefused()
        {
            EnsureGuidManagerConstructible();
            SeedWorldTinkeringMaterialWeenie(7923);

            var vendor = MakeVendor();
            var store = MakeStore(out var backend, out var world);
            vendor.Store = store;

            var vault = SeedVaultHolding(backend, world, Bag(7923), Bag(7923));

            vendor.RebuildView();

            var proxyGuid = vendor.DefaultItemsForSale.Values.Single().Guid;

            var basket = new List<ItemProfile> { new ItemProfile(5, proxyGuid.Full) };

            Assert.IsFalse(vendor.TryWithdrawTransaction(basket, Owner, null), "an over-withdraw is refused, not clamped");
            Assert.AreEqual(2, vault.Inventory.Count, "and nothing leaves the vault");
            Assert.AreEqual(0, backend.Logs.Count(l => l.Action == (int)AccountVaultAction.Withdraw), "no member may be withdrawn on the refusal path");
        }

        /// <summary>
        /// The group-branch half of the count-suffix sort bug, driven through the REAL
        /// RebuildView -> TryCreateDisplayProxy grouping path rather than by calling
        /// PersonalVendor.RegisterSortName directly - unlike this test, that seam test cannot catch a
        /// regression that deletes the production call to RegisterSortName in TryCreateDisplayProxy,
        /// since it would populate sortNames itself regardless of whether that method still does.
        ///
        /// Three groups of the same wcid/structure/base-name ("Salvage", MakeSalvageBag's default),
        /// distinguished only by Workmanship - the grouping bucket key is (wcid, Structure,
        /// Workmanship rounded to 2dp: AccountVaultStore.cs:728), so three distinct Workmanship values
        /// land in three distinct buckets even sharing wcid and Structure. Counts are chosen so the
        /// count suffix DISAGREES with Workmanship on purpose: ordinally "(12 in vault)" sorts BEFORE
        /// "(3 in vault)" ('1' &lt; '3'), and a bare singleton (no suffix at all, since a group of 1 is
        /// never labelled) is a PREFIX of both suffixed names and so sorts before either one under a
        /// Name-based compare regardless of its own middling Workmanship. A Name-based sort would
        /// therefore order these singleton, lowWork(12), highWork(3); the fix must instead order
        /// strictly by Workmanship descending: highWork(9.0), singleton(5.0), lowWork(2.0).
        /// </summary>
        [TestMethod]
        public void RebuildView_GroupRowsSharingABaseName_SortByWorkmanshipNotCountSuffix()
        {
            EnsureGuidManagerConstructible();
            SeedWorldTinkeringMaterialWeenie(7930);

            var vendor = MakeVendor();
            var store = MakeStore(out var backend, out var world);
            vendor.Store = store;

            var highWorkBags = new[]
            {
                Bag(7930, itemWorkmanship: 90, numItemsInMaterial: 10),
                Bag(7930, itemWorkmanship: 90, numItemsInMaterial: 10),
                Bag(7930, itemWorkmanship: 90, numItemsInMaterial: 10),
            };
            var singletonBag = Bag(7930, itemWorkmanship: 50, numItemsInMaterial: 10);
            var lowWorkBags = Enumerable.Range(0, 12).Select(_ => Bag(7930, itemWorkmanship: 20, numItemsInMaterial: 10)).ToArray();

            var allBags = highWorkBags.Concat(new[] { singletonBag }).Concat(lowWorkBags).ToArray();
            SeedVaultHolding(backend, world, allBags);

            vendor.RebuildView();

            Assert.AreEqual(3, vendor.DefaultItemsForSale.Count, "three distinct Workmanship buckets must be three rows");

            var highWork = vendor.DefaultItemsForSale.Values.Single(d => d.Name.Contains("(3 in vault)"));
            var singleton = vendor.DefaultItemsForSale.Values.Single(d => !d.Name.Contains("in vault"));
            var lowWork = vendor.DefaultItemsForSale.Values.Single(d => d.Name.Contains("(12 in vault)"));

            var actual = new List<WorldObject>();
            vendor.forEachItem(actual.Add);

            Assert.IsTrue(actual.IndexOf(highWork) < actual.IndexOf(singleton),
                "the Workmanship-9.0 group must sort before the Workmanship-5.0 singleton, not after it because '(3 in vault)' loses an ordinal compare against the bare name.");
            Assert.IsTrue(actual.IndexOf(singleton) < actual.IndexOf(lowWork),
                "the Workmanship-5.0 singleton must sort before the Workmanship-2.0 group, not after it because the bare name is a prefix of '(12 in vault)'.");
        }

        #endregion

        #region Deferred withdraw refresh (fix/mule-refresh-debounce, fix/mule-panel-no-timer-refresh)

        // CALL SITES. Every test in this region reaches PersonalVendor.PrepareWithdrawRefresh and/or
        // PersonalVendor.ApplyElapsedUpgrade directly (the vendor-side decision + lookup patch + the
        // elapsed-window upgrade), after withdrawing through the store's own queue. The ones that then
        // call vendor.TryWithdrawTransaction reach the LIVE request-resolution and amount-validation
        // path, but with a null player - so that live call never itself defers (every withdrawn item is
        // handed back to the vault, which is an Immediate case) and RefreshAfterWithdraw returns at its
        // null-player guard. There is no timer anymore (fix/mule-panel-no-timer-refresh: a pending
        // refresh is delivered only by piggybacking on the player's own next withdraw click, never by a
        // server-initiated send - see PersonalVendor_WithdrawRefresh.cs and MuleWithdrawRefresh.cs's
        // class remarks). RefreshAfterWithdraw itself, and ApproachVendor's network send, still need a
        // live Player/Session and are NOT exercised here; their pure halves are covered here via
        // ApplyElapsedUpgrade and CancelPendingRefresh (both internal test seams), and in
        // MuleWithdrawRefreshTests.

        private static MuleRefreshTiming PrepareAfterOne(PersonalVendor vendor, AccountVaultStore store, ObjectGuid displayGuid, MuleWithdrawRowKind kind,
                                                         VaultEntry entry, int amount, List<WorldObject> withdrawn)
        {
            return vendor.PrepareWithdrawRefresh(
                store,
                new List<(VaultEntry, int)> { (entry, amount) },
                new List<(ObjectGuid, MuleWithdrawRowKind)> { (displayGuid, kind) },
                new List<IReadOnlyList<WorldObject>> { withdrawn },
                anyReturnedToVault: false);
        }

        /// <summary>
        /// A partial group withdraw defers, and the group's display guid - which the client still holds
        /// because no list was sent - must go on resolving to the row's REMAINING members. The final
        /// TryWithdrawTransaction is the discriminating half: with the stale member list it would try to
        /// take the member already handed out, find it gone from every vault, and refuse.
        /// </summary>
        [TestMethod]
        public void DeferredRefresh_PartialGroupWithdraw_Defers_AndTheOldDisplayGuidStillWithdraws()
        {
            EnsureGuidManagerConstructible();
            SeedWorldTinkeringMaterialWeenie(7962);

            var vendor = MakeVendor();
            var store = MakeStore(out var backend, out var world);
            vendor.Store = store;

            SeedVaultHolding(backend, world, Bag(7962), Bag(7962), Bag(7962), Bag(7962));

            vendor.RebuildView();

            var displayGuid = vendor.DefaultItemsForSale.Values.Single().Guid;
            var keysBefore = vendor.DefaultItemsForSale.Keys.ToList();

            Assert.IsTrue(vendor.TryGetLookupEntry(displayGuid, out var entry, out var kind));
            Assert.AreEqual(MuleWithdrawRowKind.Group, kind);

            var withdrawn = WithdrawOnQueue(store, entry, 1);

            Assert.AreEqual(MuleRefreshTiming.Defer, PrepareAfterOne(vendor, store, displayGuid, kind, entry, 1, withdrawn));
            Assert.AreEqual(3L, vendor.WithdrawableCountFor(displayGuid), "the deferred patch must drop the member just withdrawn");
            CollectionAssert.AreEqual(keysBefore, vendor.DefaultItemsForSale.Keys.ToList(), "deferring must not rebuild the view - the client's display guids stay valid");

            var logsBefore = backend.Logs.Count(l => l.Action == (int)AccountVaultAction.Withdraw);

            Assert.IsTrue(vendor.TryWithdrawTransaction(new List<ItemProfile> { new ItemProfile(2, displayGuid.Full) }, Owner, null),
                "the OLD display guid must still withdraw from the row's live members");
            Assert.AreEqual(logsBefore + 2, backend.Logs.Count(l => l.Action == (int)AccountVaultAction.Withdraw), "exactly two more members moved");
        }

        [TestMethod]
        public void DeferredRefresh_PartialGroupWithdraw_AmountIsValidatedAgainstTheLiveCount()
        {
            EnsureGuidManagerConstructible();
            SeedWorldTinkeringMaterialWeenie(7963);

            var vendor = MakeVendor();
            var store = MakeStore(out var backend, out var world);
            vendor.Store = store;

            var vault = SeedVaultHolding(backend, world, Bag(7963), Bag(7963), Bag(7963));

            vendor.RebuildView();

            var displayGuid = vendor.DefaultItemsForSale.Values.Single().Guid;
            vendor.TryGetLookupEntry(displayGuid, out var entry, out var kind);

            var withdrawn = WithdrawOnQueue(store, entry, 1);
            Assert.AreEqual(MuleRefreshTiming.Defer, PrepareAfterOne(vendor, store, displayGuid, kind, entry, 1, withdrawn));

            var logsBefore = backend.Logs.Count(l => l.Action == (int)AccountVaultAction.Withdraw);

            // The client still shows 3; the vault holds 2.
            Assert.IsFalse(vendor.TryWithdrawTransaction(new List<ItemProfile> { new ItemProfile(3, displayGuid.Full) }, Owner, null),
                "a request sized to the client's stale count must be refused against the live count");
            Assert.AreEqual(logsBefore, backend.Logs.Count(l => l.Action == (int)AccountVaultAction.Withdraw), "and nothing may move");
            Assert.AreEqual(2, vault.Inventory.Count);
        }

        [TestMethod]
        public void DeferredRefresh_GroupWithdrawThatEmptiesTheRow_IsImmediate_AndDoesNotPatch()
        {
            EnsureGuidManagerConstructible();
            SeedWorldTinkeringMaterialWeenie(7964);

            var vendor = MakeVendor();
            var store = MakeStore(out var backend, out var world);
            vendor.Store = store;

            SeedVaultHolding(backend, world, Bag(7964), Bag(7964));

            vendor.RebuildView();

            var displayGuid = vendor.DefaultItemsForSale.Values.Single().Guid;
            vendor.TryGetLookupEntry(displayGuid, out var entry, out var kind);

            var withdrawn = WithdrawOnQueue(store, entry, 2);

            Assert.AreEqual(MuleRefreshTiming.Immediate, PrepareAfterOne(vendor, store, displayGuid, kind, entry, 2, withdrawn));
            Assert.AreEqual(2L, vendor.WithdrawableCountFor(displayGuid), "an immediate refresh rebuilds the view itself; the lookup is left for it");
        }

        /// <summary>
        /// A partial class withdraw defers and the class row's lookup is re-read LIVE. The refusal is the
        /// discriminating half: against the stale count the vendor would let a 4 through to the store,
        /// which writes a Withdraw audit row before its own debit is refused.
        /// </summary>
        [TestMethod]
        public void DeferredRefresh_PartialClassWithdraw_Defers_AndValidatesAgainstTheLiveCount()
        {
            EnsureGuidManagerConstructible();

            var vendor = MakeVendor();
            var store = MakeStore(out var backend, out var world);
            vendor.Store = store;

            SeedVaultHolding(backend, world);

            world.PristineResult = false;
            world.ClassifyResult = true;

            for (var i = 0; i < 5; i++)
                DepositOnQueue(store, FakeVaultWorld.MakeSalvageBag(7965, 100, 77, 12, 640));

            Assert.AreEqual(VaultEntryKind.Class, store.GetEntries(0, -1).Single().Kind, "sanity: the deposits must have folded into a class row");

            vendor.RebuildView();

            var displayGuid = vendor.DefaultItemsForSale.Values.Single().Guid;
            var keysBefore = vendor.DefaultItemsForSale.Keys.ToList();

            Assert.IsTrue(vendor.TryGetLookupEntry(displayGuid, out var entry, out var kind));
            Assert.AreEqual(MuleWithdrawRowKind.Class, kind);
            Assert.AreEqual(5L, entry.Count);

            var withdrawn = WithdrawOnQueue(store, entry, 2);

            Assert.AreEqual(MuleRefreshTiming.Defer, PrepareAfterOne(vendor, store, displayGuid, kind, entry, 2, withdrawn));
            Assert.AreEqual(3L, vendor.WithdrawableCountFor(displayGuid), "the class lookup must carry the live count after a deferred withdraw");
            CollectionAssert.AreEqual(keysBefore, vendor.DefaultItemsForSale.Keys.ToList(), "deferring must not rebuild the view");

            var logsBefore = backend.Logs.Count(l => l.Action == (int)AccountVaultAction.Withdraw);

            Assert.IsFalse(vendor.TryWithdrawTransaction(new List<ItemProfile> { new ItemProfile(4, displayGuid.Full) }, Owner, null),
                "4 against a live 3 must be refused");
            Assert.AreEqual(logsBefore, backend.Logs.Count(l => l.Action == (int)AccountVaultAction.Withdraw),
                "and refused by the VENDOR, on the live count, before the store writes anything");
        }

        [TestMethod]
        public void DeferredRefresh_ClassWithdrawThatEmptiesTheRow_IsImmediate()
        {
            EnsureGuidManagerConstructible();

            var vendor = MakeVendor();
            var store = MakeStore(out var backend, out var world);
            vendor.Store = store;

            SeedVaultHolding(backend, world);

            world.PristineResult = false;
            world.ClassifyResult = true;

            DepositOnQueue(store, FakeVaultWorld.MakeSalvageBag(7966, 100, 77, 12, 640));
            DepositOnQueue(store, FakeVaultWorld.MakeSalvageBag(7966, 100, 77, 12, 640));

            vendor.RebuildView();

            var displayGuid = vendor.DefaultItemsForSale.Values.Single().Guid;
            vendor.TryGetLookupEntry(displayGuid, out var entry, out var kind);

            var withdrawn = WithdrawOnQueue(store, entry, 2);

            Assert.AreEqual(MuleRefreshTiming.Immediate, PrepareAfterOne(vendor, store, displayGuid, kind, entry, 2, withdrawn));
        }

        [TestMethod]
        public void DeferredRefresh_LedgerWithABalanceLeft_Defers_AndAnEmptiedLedger_IsImmediate()
        {
            EnsureGuidManagerConstructible();
            SeedWorldStackableWeenie(7967, 100);
            SeedWorldStackableWeenie(7968, 100);

            var vendor = MakeVendor();
            var store = MakeStore(out var backend, out _);
            vendor.Store = store;

            SeedLedger(backend, 7967, 40);
            SeedLedger(backend, 7968, 10);

            vendor.RebuildView();

            var partialGuid = vendor.DefaultItemsForSale.Values.Single(d => d.WeenieClassId == 7967).Guid;
            var emptiedGuid = vendor.DefaultItemsForSale.Values.Single(d => d.WeenieClassId == 7968).Guid;

            var partialEntry = VaultEntry.ForLedger(7967, 10);
            var partialWithdrawn = WithdrawOnQueue(store, partialEntry, 10);

            Assert.AreEqual(MuleRefreshTiming.Defer, PrepareAfterOne(vendor, store, partialGuid, MuleWithdrawRowKind.Ledger, partialEntry, 10, partialWithdrawn),
                "a ledger row with 30 left is a partial decrement");

            var emptiedEntry = VaultEntry.ForLedger(7968, 10);
            var emptiedWithdrawn = WithdrawOnQueue(store, emptiedEntry, 10);

            Assert.AreEqual(MuleRefreshTiming.Immediate, PrepareAfterOne(vendor, store, emptiedGuid, MuleWithdrawRowKind.Ledger, emptiedEntry, 10, emptiedWithdrawn),
                "a ledger row withdrawn to zero is gone from the panel");
        }

        [TestMethod]
        public void DeferredRefresh_AnIndividualStoredItem_IsImmediate()
        {
            var vendor = MakeVendor();
            var store = MakeStore(out var backend, out var world);
            vendor.Store = store;

            var (_, item) = SeedStoredItem(backend, world);
            var entry = VaultEntry.ForItem(item);

            Assert.AreEqual(MuleRefreshTiming.Immediate, PrepareAfterOne(vendor, store, item.Guid, MuleWithdrawRowKind.Individual, entry, 1, new List<WorldObject> { item }));
        }

        /// <summary>
        /// Two deferred withdrawals of the same group in a row: the second resolves through the lookup
        /// the FIRST one patched (its entry wraps the patched list), so the second patch must apply too.
        /// </summary>
        [TestMethod]
        public void DeferredRefresh_TwoConsecutivePartialGroupWithdraws_BothDefer_AndBothPatch()
        {
            EnsureGuidManagerConstructible();
            SeedWorldTinkeringMaterialWeenie(7969);

            var vendor = MakeVendor();
            var store = MakeStore(out var backend, out var world);
            vendor.Store = store;

            SeedVaultHolding(backend, world, Bag(7969), Bag(7969), Bag(7969), Bag(7969));

            vendor.RebuildView();

            var displayGuid = vendor.DefaultItemsForSale.Values.Single().Guid;

            vendor.TryGetLookupEntry(displayGuid, out var first, out var kind);
            var firstWithdrawn = WithdrawOnQueue(store, first, 1);
            Assert.AreEqual(MuleRefreshTiming.Defer, PrepareAfterOne(vendor, store, displayGuid, kind, first, 1, firstWithdrawn));

            Assert.IsTrue(vendor.TryGetLookupEntry(displayGuid, out var second, out _), "the patched group must still resolve");
            var secondWithdrawn = WithdrawOnQueue(store, second, 1);

            Assert.AreNotEqual(firstWithdrawn.Single().Guid, secondWithdrawn.Single().Guid, "the second withdraw must take a different member");
            Assert.AreEqual(MuleRefreshTiming.Defer, PrepareAfterOne(vendor, store, displayGuid, kind, second, 1, secondWithdrawn), "a second partial inside the same view must defer too");
            Assert.AreEqual(2L, vendor.WithdrawableCountFor(displayGuid), "and the second patch must have applied on top of the first");
        }

        /// <summary>
        /// The I8 store re-fetch in TryWithdrawTransaction rebinds the window mid-basket, and the Store
        /// setter clears every lookup. A patch target that is no longer there means the client's display
        /// guids cannot be trusted, so the refresh must NOT be deferred.
        /// </summary>
        [TestMethod]
        public void DeferredRefresh_StoreReboundBeforeThePatch_FallsBackToImmediate()
        {
            EnsureGuidManagerConstructible();
            SeedWorldTinkeringMaterialWeenie(7970);

            var vendor = MakeVendor();
            var store = MakeStore(out var backend, out var world);
            vendor.Store = store;

            SeedVaultHolding(backend, world, Bag(7970), Bag(7970), Bag(7970));

            vendor.RebuildView();

            var displayGuid = vendor.DefaultItemsForSale.Values.Single().Guid;
            vendor.TryGetLookupEntry(displayGuid, out var entry, out var kind);

            var withdrawn = WithdrawOnQueue(store, entry, 1);

            // Same account, new instance: exactly what the I8 retry assigns.
            var rebound = new AccountVaultStore(OwnerAccount, backend, world);
            vendor.Store = rebound;

            Assert.AreEqual(MuleRefreshTiming.Immediate, PrepareAfterOne(vendor, rebound, displayGuid, kind, entry, 1, withdrawn));
            Assert.IsNull(vendor.WithdrawableCountFor(displayGuid), "nothing may be written into lookups the rebind cleared");
        }

        /// <summary>A mixed basket: a partial class decrement beside an emptied group is Immediate, and nothing is patched.</summary>
        [TestMethod]
        public void DeferredRefresh_MixedBasket_PartialClassPlusEmptiedGroup_IsImmediate()
        {
            EnsureGuidManagerConstructible();
            SeedWorldTinkeringMaterialWeenie(7971);

            var vendor = MakeVendor();
            var store = MakeStore(out var backend, out var world);
            vendor.Store = store;

            SeedVaultHolding(backend, world, Bag(7971), Bag(7971));

            world.PristineResult = false;
            world.ClassifyResult = true;

            for (var i = 0; i < 5; i++)
                DepositOnQueue(store, FakeVaultWorld.MakeSalvageBag(7972, 100, 77, 12, 640));

            vendor.RebuildView();

            Assert.AreEqual(2, vendor.DefaultItemsForSale.Count, "sanity: one group row and one class row");

            ObjectGuid groupGuid = default, classGuid = default;
            VaultEntry groupEntry = null, classEntry = null;

            foreach (var guid in vendor.DefaultItemsForSale.Keys.ToList())
            {
                Assert.IsTrue(vendor.TryGetLookupEntry(guid, out var e, out var k));

                if (k == MuleWithdrawRowKind.Group) { groupGuid = guid; groupEntry = e; }
                else if (k == MuleWithdrawRowKind.Class) { classGuid = guid; classEntry = e; }
            }

            Assert.IsNotNull(groupEntry, "sanity: a group row");
            Assert.IsNotNull(classEntry, "sanity: a class row");

            var classWithdrawn = WithdrawOnQueue(store, classEntry, 2);
            var groupWithdrawn = WithdrawOnQueue(store, groupEntry, 2);

            var timing = vendor.PrepareWithdrawRefresh(
                store,
                new List<(VaultEntry, int)> { (classEntry, 2), (groupEntry, 2) },
                new List<(ObjectGuid, MuleWithdrawRowKind)> { (classGuid, MuleWithdrawRowKind.Class), (groupGuid, MuleWithdrawRowKind.Group) },
                new List<IReadOnlyList<WorldObject>> { classWithdrawn, groupWithdrawn },
                anyReturnedToVault: false);

            Assert.AreEqual(MuleRefreshTiming.Immediate, timing, "an emptied group anywhere in the basket forces an immediate refresh");
            Assert.AreEqual(5L, vendor.WithdrawableCountFor(classGuid), "the immediate path leaves the lookups to the rebuild - no patch");
        }

        [TestMethod]
        public void DeferredRefresh_MismatchedOrMissingRequestLists_FailTowardImmediate()
        {
            var vendor = MakeVendor();
            var store = MakeStore(out _, out _);
            vendor.Store = store;

            var entry = VaultEntry.ForLedger(7973, 1);

            Assert.AreEqual(MuleRefreshTiming.Immediate, vendor.PrepareWithdrawRefresh(
                store,
                new List<(VaultEntry, int)> { (entry, 1) },
                new List<(ObjectGuid, MuleWithdrawRowKind)>(),
                new List<IReadOnlyList<WorldObject>> { new List<WorldObject>() },
                false), "rows shorter than requests");

            Assert.AreEqual(MuleRefreshTiming.Immediate, vendor.PrepareWithdrawRefresh(
                store,
                new List<(VaultEntry, int)> { (entry, 1) },
                new List<(ObjectGuid, MuleWithdrawRowKind)> { (new ObjectGuid(1), MuleWithdrawRowKind.Ledger) },
                new List<IReadOnlyList<WorldObject>>(),
                false), "withdrawn list shorter than requests");

            Assert.AreEqual(MuleRefreshTiming.Immediate, vendor.PrepareWithdrawRefresh(store, null, null, null, false), "null lists");
        }

        [TestMethod]
        public void DeferredRefresh_UnbindingTheStore_DropsPendingRefreshes()
        {
            var vendor = MakeVendor();
            var store = MakeStore(out _, out _);
            vendor.Store = store;

            Assert.IsTrue(vendor.RefreshDebouncer.TryOpenWindow(OwnerCharacter, DateTime.UtcNow));
            Assert.IsTrue(vendor.RefreshDebouncer.TryOpenWindow(GranteeCharacter, DateTime.UtcNow));

            // WorldObject.Destroy's PersonalVendor branch does exactly this.
            vendor.Store = null;

            Assert.AreEqual(0, vendor.RefreshDebouncer.PendingCount, "a destroyed/unbound window must not keep pending refreshes");
        }

        // ---- ApplyElapsedUpgrade (no timer: the elapsed-window upgrade piggybacks on the player's own next click) ----

        private static readonly DateTime WindowEpoch = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        [TestMethod]
        public void ElapsedUpgrade_DeferInsideTheWindow_StaysDeferred_NoSend()
        {
            var vendor = MakeVendor();

            Assert.IsTrue(vendor.RefreshDebouncer.TryOpenWindow(OwnerCharacter, WindowEpoch));

            var timing = vendor.ApplyElapsedUpgrade(OwnerCharacter, MuleRefreshTiming.Defer, WindowEpoch.AddSeconds(4));

            Assert.AreEqual(MuleRefreshTiming.Defer, timing, "4s into a 5s window must not upgrade");
            Assert.IsTrue(vendor.RefreshDebouncer.IsPending(OwnerCharacter), "and the pending window must survive untouched");
        }

        [TestMethod]
        public void ElapsedUpgrade_DeferAfterTheWindowElapsed_UpgradesToImmediate()
        {
            var vendor = MakeVendor();

            Assert.IsTrue(vendor.RefreshDebouncer.TryOpenWindow(OwnerCharacter, WindowEpoch));

            var timing = vendor.ApplyElapsedUpgrade(OwnerCharacter, MuleRefreshTiming.Defer, WindowEpoch.AddSeconds(5));

            Assert.AreEqual(MuleRefreshTiming.Immediate, timing, "5s into a 5s window must upgrade");

            // ApplyElapsedUpgrade itself only DECIDES - it never cancels. RefreshAfterWithdraw's caller,
            // ApproachVendor, is what clears the pending window (see CancelPendingRefresh below); the
            // window is proven still pending here so that wiring's job is not this test's job.
            Assert.IsTrue(vendor.RefreshDebouncer.IsPending(OwnerCharacter), "ApplyElapsedUpgrade alone must not clear the window - that is CancelPendingRefresh's job");
        }

        [TestMethod]
        public void ElapsedUpgrade_WindowIsNotExtendedByAnInWindowDeferral_TheClickAtPlusSixSecondsUpgrades()
        {
            // A click at open+4s (still inside the window: no upgrade, and TryOpenWindow at +4s is a
            // no-op because a window is already pending - exactly what DeferRefresh does today). A
            // second click at open+6s must upgrade, proving the window is still measured from the
            // FIRST deferral, not pushed back by the one at +4s.
            var vendor = MakeVendor();

            Assert.IsTrue(vendor.RefreshDebouncer.TryOpenWindow(OwnerCharacter, WindowEpoch));
            Assert.IsFalse(vendor.RefreshDebouncer.TryOpenWindow(OwnerCharacter, WindowEpoch.AddSeconds(4)), "an in-window deferral must not reopen the window");

            var atFour = vendor.ApplyElapsedUpgrade(OwnerCharacter, MuleRefreshTiming.Defer, WindowEpoch.AddSeconds(4));
            Assert.AreEqual(MuleRefreshTiming.Defer, atFour, "still inside the window at +4s");

            var atSix = vendor.ApplyElapsedUpgrade(OwnerCharacter, MuleRefreshTiming.Defer, WindowEpoch.AddSeconds(6));
            Assert.AreEqual(MuleRefreshTiming.Immediate, atSix, "+6s must have elapsed from the ORIGINAL open at 0s (a pushed-back window would only be at +2s)");
        }

        [TestMethod]
        public void ElapsedUpgrade_NothingPending_NeverUpgrades()
        {
            var vendor = MakeVendor();

            var timing = vendor.ApplyElapsedUpgrade(OwnerCharacter, MuleRefreshTiming.Defer, WindowEpoch.AddSeconds(1000));

            Assert.AreEqual(MuleRefreshTiming.Defer, timing, "nothing pending must never upgrade, however much time has passed");
        }

        [TestMethod]
        public void ElapsedUpgrade_AnAlreadyImmediateTiming_PassesThrough()
        {
            var vendor = MakeVendor();
            vendor.RefreshDebouncer.TryOpenWindow(OwnerCharacter, WindowEpoch);

            var timing = vendor.ApplyElapsedUpgrade(OwnerCharacter, MuleRefreshTiming.Immediate, WindowEpoch.AddSeconds(5));

            Assert.AreEqual(MuleRefreshTiming.Immediate, timing, "an already-Immediate timing is untouched by the upgrade check");
        }

        /// <summary>
        /// Proves the elapsed check actually discriminates rather than always returning one answer: the
        /// SAME setup at +4s (not yet elapsed) and +5s (elapsed) must produce different timings.
        /// </summary>
        [TestMethod]
        public void ElapsedUpgrade_DiscriminatesOnElapsedTime()
        {
            var vendor = MakeVendor();
            vendor.RefreshDebouncer.TryOpenWindow(OwnerCharacter, WindowEpoch);

            var before = vendor.ApplyElapsedUpgrade(OwnerCharacter, MuleRefreshTiming.Defer, WindowEpoch.AddSeconds(4));
            Assert.AreEqual(MuleRefreshTiming.Defer, before);

            vendor.RefreshDebouncer.TryOpenWindow(OwnerCharacter, WindowEpoch); // no-op: still pending from above
            var after = vendor.ApplyElapsedUpgrade(OwnerCharacter, MuleRefreshTiming.Defer, WindowEpoch.AddSeconds(5));
            Assert.AreEqual(MuleRefreshTiming.Immediate, after);

            Assert.AreNotEqual(before, after, "the elapsed comparison must change the outcome, not just be present");
        }

        /// <summary>
        /// One Buy line per successful withdrawal, on all three paths PersonalVendor.RefreshAfterWithdraw
        /// can take: the pure half is MuleWithdrawRefreshTests' Actions_* tests; this is the vendor-side
        /// half, proving the elapsed-window upgrade feeds ActionsAfterWithdraw the SAME timing (Immediate)
        /// an ordinary immediate refresh does, rather than some fourth, un-auditied plan.
        /// </summary>
        [TestMethod]
        public void ElapsedUpgrade_ProducesTheSameActionsAsAnOrdinaryImmediateRefresh()
        {
            var vendor = MakeVendor();
            vendor.RefreshDebouncer.TryOpenWindow(OwnerCharacter, WindowEpoch);

            var upgraded = vendor.ApplyElapsedUpgrade(OwnerCharacter, MuleRefreshTiming.Defer, WindowEpoch.AddSeconds(5));
            var upgradedActions = MuleWithdrawRefresh.ActionsAfterWithdraw(upgraded);
            var immediateActions = MuleWithdrawRefresh.ActionsAfterWithdraw(MuleRefreshTiming.Immediate);

            Assert.AreEqual(immediateActions, upgradedActions, "the upgrade must plan exactly what an ordinary immediate refresh plans - one Buy line, through the list send");
        }

        // ---- Approach cancels pending (CancelPendingRefresh itself needs a live Player.Session to be
        // called the way ApproachVendor calls it - see this file's class remarks - so the wiring is
        // proven at the RefreshDebouncer level it delegates to; ApproachVendor's own unconditional
        // CancelPendingRefresh(player) call is a one-line, unchanged-by-this-fix call this session
        // re-read at PersonalVendor.cs:1729.) ----

        [TestMethod]
        public void ApproachCancelsPending_RefreshDebouncerCancel_DropsTheWindow_AndItCanNeverLaterUpgrade()
        {
            var vendor = MakeVendor();
            vendor.RefreshDebouncer.TryOpenWindow(OwnerCharacter, WindowEpoch);

            Assert.IsTrue(vendor.RefreshDebouncer.Cancel(OwnerCharacter), "the approach's cancel must report a window was pending");
            Assert.IsFalse(vendor.RefreshDebouncer.IsPending(OwnerCharacter));

            // A later Defer for the same viewer must not spuriously upgrade against the cancelled window.
            var timing = vendor.ApplyElapsedUpgrade(OwnerCharacter, MuleRefreshTiming.Defer, WindowEpoch.AddSeconds(1000));
            Assert.AreEqual(MuleRefreshTiming.Defer, timing, "a cancelled window must not upgrade a later, unrelated Defer");
        }

        /// <summary>
        /// THIS IS A SOURCE SCAN AND IT PROVES PLACEMENT, NOT BEHAVIOUR (see the CallSite_ pattern
        /// elsewhere in this file for why that limit is named rather than hidden). Pins the
        /// fix/mule-panel-no-timer-refresh removal: PersonalVendor_WithdrawRefresh.cs must schedule no
        /// server-initiated ActionChain/delayed action at all - the whole point of the fix is that a
        /// pending refresh is only ever delivered by piggybacking on the player's own next click.
        /// </summary>
        [TestMethod]
        public void CallSite_WithdrawRefreshFile_SchedulesNoActionChainOrDelay()
        {
            const string relativePath = "Source/ACE.Server/WorldObjects/PersonalVendor_WithdrawRefresh.cs";

            var path = FindInSourceTree(relativePath);
            Assert.IsNotNull(path, $"Could not find {relativePath} by walking up from {AppContext.BaseDirectory}.");

            var code = AccountVaultPurgeTests.StripComments(File.ReadAllText(path));

            Assert.IsFalse(code.Contains("ActionChain"), $"{relativePath} must not construct an ActionChain - no server-initiated refresh may ever be scheduled.");
            Assert.IsFalse(code.Contains("AddDelaySeconds"), $"{relativePath} must not schedule any delayed action.");
            Assert.IsFalse(code.Contains("EnqueueChain"), $"{relativePath} must not enqueue any action chain.");
        }

        /// <summary>
        /// THIS IS A SOURCE SCAN AND IT PROVES PLACEMENT, NOT BEHAVIOUR. Pins fix/mule-deferred-buy-unlock:
        /// the DEFERRED branch of RefreshAfterWithdraw (everything after the SendListNow block's return)
        /// must send GameEventInventoryServerSaveFailed to the player, because that branch sends no vendor
        /// list and the client holds its vendor-buy lock until a list or that event arrives. Comments are
        /// stripped so prose alone cannot satisfy it, and the scan is bound to the deferred tail of the
        /// method, so a send placed only on the immediate path does not count.
        /// </summary>
        [TestMethod]
        public void CallSite_RefreshAfterWithdraw_DeferredBranchSendsInventoryServerSaveFailed()
        {
            const string relativePath = "Source/ACE.Server/WorldObjects/PersonalVendor_WithdrawRefresh.cs";

            var path = FindInSourceTree(relativePath);
            Assert.IsNotNull(path, $"Could not find {relativePath} by walking up from {AppContext.BaseDirectory}.");

            var code = AccountVaultPurgeTests.StripComments(File.ReadAllText(path));

            var methodStart = code.IndexOf("private void RefreshAfterWithdraw(", StringComparison.Ordinal);
            Assert.IsTrue(methodStart >= 0, "RefreshAfterWithdraw was not found.");

            var open = code.IndexOf('{', methodStart);
            Assert.IsTrue(open >= 0, "RefreshAfterWithdraw has no body.");

            var depth = 0;
            var end = -1;
            for (var i = open; i < code.Length; i++)
            {
                if (code[i] == '{') depth++;
                else if (code[i] == '}' && --depth == 0) { end = i; break; }
            }
            Assert.IsTrue(end > open, "RefreshAfterWithdraw's closing brace was not found.");

            var body = code.Substring(open, end - open);

            var gate = body.IndexOf("if (actions.SendListNow)", StringComparison.Ordinal);
            Assert.IsTrue(gate >= 0, "the SendListNow gate was not found in RefreshAfterWithdraw.");

            var ret = body.IndexOf("return;", gate, StringComparison.Ordinal);
            Assert.IsTrue(ret >= 0, "the SendListNow block's return was not found.");

            var deferredTail = body.Substring(ret + "return;".Length);

            Assert.IsTrue(deferredTail.Contains("DeferRefresh(player)"), "the deferred tail must still contain the DeferRefresh call (the scan is bound to the wrong region otherwise).");
            Assert.IsTrue(deferredTail.Contains("GameEventInventoryServerSaveFailed"), "the deferred branch must send GameEventInventoryServerSaveFailed to release the client's vendor-buy lock.");
            Assert.IsTrue(deferredTail.Contains("player.Guid.Full"), "the deferred branch's send must name the player's guid.");
        }

        #endregion

        #region Stable display guids (perf/mule-stable-display-guids)

        /// <summary>Withdraws through the store's own queue, the way AccountVaultStore's own tests do.</summary>
        private static List<WorldObject> WithdrawOnQueue(AccountVaultStore store, VaultEntry entry, int amount, GroupTakeOrder takeOrder = GroupTakeOrder.Front)
        {
            var ok = false;
            List<WorldObject> withdrawn = null;
            string failReason = null;

            store.Enqueue(() => ok = store.TryWithdraw(entry, amount, Owner, out withdrawn, out failReason, takeOrder));

            Assert.IsTrue(ok, $"the withdraw that is supposed to change the view did not happen: {failReason}");

            return withdrawn;
        }

        private static void DepositOnQueue(AccountVaultStore store, WorldObject item)
        {
            var ok = false;
            string failReason = null;

            store.Enqueue(() => ok = store.TryDeposit(item, Owner, out failReason));

            Assert.IsTrue(ok, $"the deposit that is supposed to change the view did not happen: {failReason}");
        }

        private static int CountOf(string text, string needle) => text.Split(new[] { needle }, StringSplitOptions.None).Length - 1;

        /// <summary>
        /// (a) The headline. A partial withdraw from ONE of two ledger rows must cost exactly one new
        /// display object: the untouched row keeps its instance and guid (so a client plugin does not
        /// re-identify it), and only the changed row is replaced - old one destroyed, new one carrying
        /// the new count exactly once. The counters pin the create/reuse/destroy arithmetic so a
        /// "rebuild everything but copy the guid" implementation cannot pass.
        /// </summary>
        [TestMethod]
        public void RebuildView_PartialWithdraw_KeepsTheUntouchedRowAndReplacesOnlyTheChangedOne()
        {
            EnsureGuidManagerConstructible();
            SeedWorldStackableWeenie(7940, 100);
            SeedWorldStackableWeenie(7941, 100);

            var vendor = MakeVendor();
            var store = MakeStore(out var backend, out _);
            vendor.Store = store;

            SeedLedger(backend, 7940, 40);
            SeedLedger(backend, 7941, 30);

            vendor.RebuildView();

            var row1Before = vendor.DefaultItemsForSale.Values.Single(d => d.WeenieClassId == 7940);
            var row2Before = vendor.DefaultItemsForSale.Values.Single(d => d.WeenieClassId == 7941);

            WithdrawOnQueue(store, VaultEntry.ForLedger(7940, 10), 10);

            vendor.RebuildView();

            var row1After = vendor.DefaultItemsForSale.Values.Single(d => d.WeenieClassId == 7940);
            var row2After = vendor.DefaultItemsForSale.Values.Single(d => d.WeenieClassId == 7941);

            Assert.AreSame(row2Before, row2After, "the untouched row must keep its display object");
            Assert.AreEqual(row2Before.Guid, row2After.Guid, "and therefore its guid");
            Assert.IsFalse(row2After.IsDestroyed, "a kept display object must not have been destroyed");

            Assert.AreNotEqual(row1Before.Guid, row1After.Guid, "the changed row must be REPLACED, not edited in place");
            Assert.IsTrue(row1Before.IsDestroyed, "the replaced display object must be destroyed");
            Assert.AreEqual(1, CountOf(row1After.Name, "in vault"), $"the new row must carry exactly one count suffix; got '{row1After.Name}'");
            StringAssert.Contains(row1After.Name, "(30 in vault)");

            Assert.AreEqual(1, vendor.LastRebuildCreated, "one row changed, so one object is created");
            Assert.AreEqual(1, vendor.LastRebuildReused, "one row did not, so one object is kept");
            Assert.AreEqual(1, vendor.LastRebuildDestroyed, "and the replaced one is destroyed");
        }

        /// <summary>
        /// (b) A forced rebuild over an unchanged store (SetSearchFilter invalidates the cache) must keep
        /// every row, must not re-append the count suffix to a kept row's Name, and must re-register
        /// every kept row's BASE name for sorting - clearing sortNames without re-registering would sort
        /// kept rows on their suffixed Name, which is the '(12 in vault)' before '(3 in vault)' trap.
        /// </summary>
        [TestMethod]
        public void RebuildView_ForcedRebuildOfAnUnchangedStore_KeepsEveryGuidAndTheSortOrder()
        {
            EnsureGuidManagerConstructible();

            const string sharedName = "Stable Twin";
            SeedWorldStackableWeenieWithWorkmanship(9003, sharedName, itemWorkmanship: 90, numItemsInMaterial: 10); // 9.0
            SeedWorldStackableWeenieWithWorkmanship(9004, sharedName, itemWorkmanship: 20, numItemsInMaterial: 10); // 2.0

            var vendor = MakeVendor();
            var store = MakeStore(out var backend, out _);
            vendor.Store = store;

            SeedLedger(backend, 9003, 3);
            SeedLedger(backend, 9004, 12);

            vendor.RebuildView();

            var before = vendor.DefaultItemsForSale.ToDictionary(kv => kv.Key, kv => kv.Value);

            vendor.SetSearchFilter(null);
            vendor.RebuildView();

            CollectionAssert.AreEquivalent(before.Keys.ToList(), vendor.DefaultItemsForSale.Keys.ToList(), "a forced rebuild of an unchanged store must keep every display guid");

            foreach (var (guid, display) in vendor.DefaultItemsForSale)
            {
                Assert.AreSame(before[guid], display, "the same guid must be the same kept object");
                Assert.AreEqual(1, CountOf(display.Name, "in vault"), $"a kept row's suffix must never be re-applied; got '{display.Name}'");
            }

            Assert.AreEqual(0, vendor.LastRebuildCreated);
            Assert.AreEqual(2, vendor.LastRebuildReused);
            Assert.AreEqual(0, vendor.LastRebuildDestroyed);

            var highWork = vendor.DefaultItemsForSale.Values.Single(d => d.WeenieClassId == 9003);
            var lowWork = vendor.DefaultItemsForSale.Values.Single(d => d.WeenieClassId == 9004);

            var actual = new List<WorldObject>();
            vendor.forEachItem(actual.Add);

            Assert.IsTrue(actual.IndexOf(highWork) < actual.IndexOf(lowWork),
                "kept rows must still sort on their BASE name (Workmanship decides), not on '(12 in vault)' vs '(3 in vault)'");
        }

        /// <summary>
        /// (c) A kept class row must not be re-materialized at all - MaterializeClass is the per-row cost
        /// this change exists to skip.
        /// </summary>
        [TestMethod]
        public void RebuildView_ForcedRebuild_DoesNotRematerializeAnUnchangedClassRow()
        {
            EnsureGuidManagerConstructible();

            var vendor = MakeVendor();
            var store = MakeStore(out var backend, out var world);
            vendor.Store = store;

            SeedVaultHolding(backend, world);

            world.PristineResult = false;
            world.ClassifyResult = true;

            DepositOnQueue(store, FakeVaultWorld.MakeSalvageBag(7995, 100, 77, 12, 640));
            DepositOnQueue(store, FakeVaultWorld.MakeSalvageBag(7995, 100, 77, 12, 640));

            Assert.AreEqual(VaultEntryKind.Class, store.GetEntries(0, -1).Single().Kind, "sanity: the deposits must have folded into a class row, or this covers the wrong branch");

            vendor.RebuildView();

            var before = vendor.DefaultItemsForSale.Values.Single();

            world.ResetClassCalls();

            vendor.SetSearchFilter(null);
            vendor.RebuildView();

            var after = vendor.DefaultItemsForSale.Values.Single();

            Assert.AreEqual(0, world.MaterializeClassCalls, "an unchanged class row must not be materialized again");
            Assert.AreSame(before, after, "and must keep its display object");
            Assert.AreEqual(before.Guid, after.Guid);
        }

        /// <summary>
        /// (d) A deposit that changes only an unrelated ledger row must leave both group proxies alone.
        /// </summary>
        [TestMethod]
        public void RebuildView_UnrelatedLedgerDeposit_KeepsBothGroupProxies()
        {
            EnsureGuidManagerConstructible();
            SeedWorldTinkeringMaterialWeenie(7960);
            SeedWorldStackableWeenie(7961, 100);

            var vendor = MakeVendor();
            var store = MakeStore(out var backend, out var world);
            vendor.Store = store;

            SeedVaultHolding(backend, world, Bag(7960), Bag(7960), Bag(7960), Bag(7960, structure: 73), Bag(7960, structure: 73));
            SeedLedger(backend, 7961, 10);

            vendor.RebuildView();

            var groupsBefore = vendor.DefaultItemsForSale.Values.Where(d => d.WeenieClassId == 7960).ToList();
            Assert.AreEqual(2, groupsBefore.Count, "sanity: two group rows");

            world.PristineResult = true;
            DepositOnQueue(store, FakeVaultWorld.MakeStack(7961, 5, 100));

            vendor.RebuildView();

            var groupsAfter = vendor.DefaultItemsForSale.Values.Where(d => d.WeenieClassId == 7960).ToList();

            CollectionAssert.AreEquivalent(groupsBefore.Select(g => g.Guid).ToList(), groupsAfter.Select(g => g.Guid).ToList(),
                "a deposit that changes only an unrelated ledger row must not replace either group proxy");
            Assert.IsTrue(groupsAfter.All(g => !g.IsDestroyed));

            StringAssert.Contains(vendor.DefaultItemsForSale.Values.Single(d => d.WeenieClassId == 7961).Name, "(15 in vault)",
                "control: the ledger row the deposit DID change must show its new count");
        }

        /// <summary>
        /// (e) Changing the search filter keeps every row visible under both filters and destroys the
        /// one the new filter hides.
        /// </summary>
        [TestMethod]
        public void RebuildView_FilterChange_KeepsRowsVisibleUnderBothAndDestroysTheNewlyHiddenOne()
        {
            EnsureGuidManagerConstructible();
            SeedWorldStackableWeenie(8300, 100);
            SeedWorldStackableWeenie(8301, 100);
            SeedWorldStackableWeenie(8302, 100);

            var vendor = MakeVendor();
            var store = MakeStore(out var backend, out _);
            vendor.Store = store;

            SeedLedger(backend, 8300, 5);
            SeedLedger(backend, 8301, 5);
            SeedLedger(backend, 8302, 5);

            vendor.SetSearchFilter(new Regex("830[01]"));
            vendor.RebuildView();

            var both = vendor.DefaultItemsForSale.Values.Single(d => d.WeenieClassId == 8300);
            var hidden = vendor.DefaultItemsForSale.Values.Single(d => d.WeenieClassId == 8301);

            vendor.SetSearchFilter(new Regex("830[02]"));
            vendor.RebuildView();

            Assert.AreSame(both, vendor.DefaultItemsForSale.Values.Single(d => d.WeenieClassId == 8300), "a row visible under both filters keeps its display object");
            Assert.IsTrue(hidden.IsDestroyed, "the newly hidden row's display object must be destroyed");
            Assert.IsFalse(vendor.DefaultItemsForSale.ContainsKey(hidden.Guid), "and must be gone from DefaultItemsForSale");
            Assert.IsTrue(vendor.DefaultItemsForSale.Values.Any(d => d.WeenieClassId == 8302), "control: the newly shown row is present");
        }

        /// <summary>
        /// (f) THE DUPE/LOSS GUARD. A group whose membership changed but whose rendered state did not
        /// (one member withdrawn from the back, an equivalent d deposited, the representative unmoved,
        /// count still 3) keeps its proxy guid - and a buy of 3 against that kept guid must resolve to
        /// the CURRENT members, d included, not the list the proxy was first built for. The withdrawn
        /// member is no longer in the vault, so a stale member list would be refused by the store. The reuse half fails on
        /// the pre-change code (every rebuild re-guids); the withdraw half passes on both.
        /// </summary>
        [TestMethod]
        public void RebuildView_KeptGroupProxy_ResolvesToTheCurrentMembers()
        {
            EnsureGuidManagerConstructible();
            SeedWorldTinkeringMaterialWeenie(7990);

            var vendor = MakeVendor();
            var store = MakeStore(out var backend, out var world);
            vendor.Store = store;

            var a = Bag(7990);
            var b = Bag(7990);
            var c = Bag(7990);

            SeedVaultHolding(backend, world, a, b, c);

            vendor.RebuildView();

            var proxyBefore = vendor.DefaultItemsForSale.Values.Single();

            var groupBefore = store.GetEntries(0, -1).Single();
            var representative = groupBefore.Members[0];

            // The store enumerates members in its own order, not necessarily seeding order, so the
            // member Back takes is read off the result rather than assumed to be c.
            var removed = WithdrawOnQueue(store, groupBefore, 1, GroupTakeOrder.Back).Single();
            Assert.AreNotSame(representative, removed, "sanity: a Back withdraw must leave the representative in place");
            Assert.IsTrue(new[] { a, b, c }.Contains(removed));

            // Stays a stored biota: ClassifyResult is false and PristineResult false.
            world.PristineResult = false;
            world.ClassifyResult = false;

            var d = Bag(7990);
            DepositOnQueue(store, d);

            var group = store.GetEntries(0, -1).Single();
            Assert.AreSame(representative, group.Members[0], "sanity: the representative must not have moved");
            Assert.AreEqual(3, group.Count, "sanity: the group must be back to three");
            CollectionAssert.Contains(group.Members.ToList(), d);
            CollectionAssert.DoesNotContain(group.Members.ToList(), removed, "sanity: the withdrawn member is no longer in the vault");

            vendor.RebuildView();

            var proxyAfter = vendor.DefaultItemsForSale.Values.Single();

            Assert.AreEqual(proxyBefore.Guid, proxyAfter.Guid, "an unchanged-looking group row keeps its proxy guid");

            var withdrawsBefore = backend.Logs.Count(l => l.Action == (int)AccountVaultAction.Withdraw);

            Assert.IsTrue(vendor.TryWithdrawTransaction(new List<ItemProfile> { new ItemProfile(3, proxyAfter.Guid.Full) }, Owner, null),
                "a buy of 3 against the kept proxy must resolve to the CURRENT three members");

            Assert.AreEqual(withdrawsBefore + 3, backend.Logs.Count(l => l.Action == (int)AccountVaultAction.Withdraw),
                "three members must actually have been withdrawn");
        }

        /// <summary>
        /// (g) A vendor Destroy after a reuse pass still reaches every display object - kept ones
        /// included - and never a stored item.
        /// </summary>
        [TestMethod]
        public void Destroy_AfterAReusePass_DestroysEveryDisplayAndNoStoredItem()
        {
            EnsureGuidManagerConstructible();
            SeedWorldStackableWeenie(7996, 100);
            SeedWorldTinkeringMaterialWeenie(7997);

            var vendor = MakeVendor();
            var store = MakeStore(out var backend, out var world);
            vendor.Store = store;

            var plain = FakeVaultWorld.MakeStack(8000, 1, 100);
            var bags = new[] { Bag(7997), Bag(7997) };

            SeedVaultHolding(backend, world, new[] { plain }.Concat(bags).ToArray());
            SeedLedger(backend, 7996, 7);

            vendor.RebuildView();
            vendor.SetSearchFilter(null);
            vendor.RebuildView();

            var displays = vendor.DefaultItemsForSale.Values.ToList();
            Assert.AreEqual(2, displays.Count, "sanity: a ledger display and a group proxy");

            vendor.Destroy();

            Assert.IsTrue(displays.All(d => d.IsDestroyed), "every current display object must be destroyed with the vendor");
            Assert.IsFalse(plain.IsDestroyed, "a stored item must never be destroyed");
            Assert.IsFalse(bags.Any(bag => bag.IsDestroyed), "nor a group member");
        }

        /// <summary>
        /// (h) A buy naming a REPLACED display's old guid is refused: nothing resolves it any more. The
        /// player-facing "That is no longer in your vault." text goes through player?.SendTransientError
        /// and cannot be observed without a live Player (class remarks, point 2), so this asserts the
        /// refusal and that nothing moved, with a positive control on the new guid.
        /// </summary>
        [TestMethod]
        public void WithdrawTransaction_AgainstAReplacedDisplaysOldGuid_IsRefused()
        {
            EnsureGuidManagerConstructible();
            SeedWorldStackableWeenie(7998, 100);

            var vendor = MakeVendor();
            var store = MakeStore(out var backend, out _);
            vendor.Store = store;

            SeedLedger(backend, 7998, 40);

            vendor.RebuildView();
            var oldGuid = vendor.DefaultItemsForSale.Values.Single().Guid;

            WithdrawOnQueue(store, VaultEntry.ForLedger(7998, 10), 10);

            vendor.RebuildView();
            var newGuid = vendor.DefaultItemsForSale.Values.Single().Guid;

            Assert.AreNotEqual(oldGuid, newGuid, "sanity: the row must have been replaced");
            Assert.IsFalse(vendor.TryGetItemForSale(oldGuid, out _), "the old guid must resolve to nothing");

            var withdrawsBefore = backend.Logs.Count(l => l.Action == (int)AccountVaultAction.Withdraw);

            Assert.IsFalse(vendor.TryWithdrawTransaction(new List<ItemProfile> { new ItemProfile(5, oldGuid.Full) }, Owner, null),
                "a buy against a replaced display's old guid must be refused");
            Assert.AreEqual(withdrawsBefore, backend.Logs.Count(l => l.Action == (int)AccountVaultAction.Withdraw), "and nothing may be withdrawn");

            Assert.IsTrue(vendor.TryWithdrawTransaction(new List<ItemProfile> { new ItemProfile(5, newGuid.Full) }, Owner, null),
                "control: the same buy against the current guid resolves");
        }

        /// <summary>
        /// A window rebound to a DIFFERENT store must never reuse a display object built for the old
        /// one. DisplayKey is not account-scoped, so without the Store setter clearing the records a
        /// same-wcid, same-count ledger row in store B would claim store A's display. Unreachable in
        /// production today (only fresh vendors are bound), pinned here so it stays that way.
        /// </summary>
        [TestMethod]
        public void RebuildView_AfterARebindToAnotherStore_NeverReusesTheOldStoresDisplay()
        {
            EnsureGuidManagerConstructible();
            SeedWorldStackableWeenie(7999, 100);

            var vendor = MakeVendor();

            var storeA = MakeStore(out var backendA, out _);
            SeedLedger(backendA, 7999, 25);

            vendor.Store = storeA;
            vendor.RebuildView();

            var displayA = vendor.DefaultItemsForSale.Values.Single();

            vendor.Store = null;

            var storeB = MakeStore(out var backendB, out _);
            SeedLedger(backendB, 7999, 25);

            vendor.Store = storeB;
            vendor.RebuildView();

            var displayB = vendor.DefaultItemsForSale.Values.Single();

            Assert.AreNotEqual(displayA.Guid, displayB.Guid, "store B's row must get its own display object, never store A's");
            Assert.IsTrue(displayA.IsDestroyed, "store A's display must be destroyed by the rebuild after the rebind");
            Assert.AreEqual(0, vendor.LastRebuildReused, "nothing from store A may be reused");
        }

        #endregion

        #region SelectZeroValueItems (fix/mule-zero-value-client-drag)

        /// <summary>
        /// Builds a bare <see cref="GenericObject"/> with a given (possibly absent) Value, for
        /// SelectZeroValueItems coverage. GenericObject.SetEphemeralValues does not default Value the
        /// way Stackable's does, so omitting PropertyInt.Value here actually yields a null Value on the
        /// constructed item rather than being silently coerced to 0.
        /// </summary>
        private static WorldObject MakeItemWithValue(int? value)
        {
            var weenie = new Weenie
            {
                WeenieClassId = NextGuid(),
                WeenieType = WeenieType.Generic,
                PropertiesInt = new Dictionary<PropertyInt, int>(),
            };

            if (value.HasValue)
                weenie.PropertiesInt[PropertyInt.Value] = value.Value;

            return new GenericObject(weenie, new ObjectGuid(NextGuid()));
        }

        /// <summary>
        /// SelectZeroValueItems is the selection half of SendClientOnlyValueOverrideForZeroValueItems,
        /// split out (code review on 8e0324d54) specifically so this can be tested without a live
        /// Player.Session. A positive Value must be excluded; zero, negative, and null Value must all
        /// be included; and the returned set must be exactly the qualifying guids, no more and no
        /// fewer.
        /// </summary>
        [TestMethod]
        public void SelectZeroValueItems_ExcludesPositiveIncludesZeroNegativeAndNull()
        {
            var positive = MakeItemWithValue(100);
            var zero = MakeItemWithValue(0);
            var negative = MakeItemWithValue(-1);
            var nullValue = MakeItemWithValue(null);

            var possessions = new List<WorldObject> { positive, zero, negative, nullValue };

            var selected = PersonalVendor.SelectZeroValueItems(possessions).ToList();

            var selectedGuids = selected.Select(i => i.Guid).ToHashSet();
            var expectedGuids = new HashSet<ObjectGuid> { zero.Guid, negative.Guid, nullValue.Guid };

            CollectionAssert.DoesNotContain(selected, positive, "a positive Value item must never be selected");
            Assert.IsTrue(selectedGuids.SetEquals(expectedGuids), "the selected set must be exactly the zero/negative/null Value items, no more and no fewer");
        }

        /// <summary>
        /// The 2026-08-30 version of this fix sent GameMessagePrivateUpdatePropertyInt, which writes
        /// sequence, property and value with NO object guid, so the client applied Value=1 to the
        /// player rather than the item and the drag stayed refused in-game (fix/mule-zero-value-public-update).
        /// Pin the wire shape: every message must carry the Public opcode, and its payload must name
        /// the item's guid at the position the Public message writes it (opcode, sequence, guid).
        /// </summary>
        [TestMethod]
        public void BuildZeroValueOverrideMessages_UsesPublicOpcodeAndNamesTheItemGuid()
        {
            var zero = MakeItemWithValue(0);
            var positive = MakeItemWithValue(50);

            var messages = PersonalVendor.BuildZeroValueOverrideMessages(new List<WorldObject> { zero, positive });

            Assert.AreEqual(1, messages.Count, "only the zero-value item gets a nudge");

            var msg = messages[0];
            Assert.AreEqual(GameMessageOpcode.PublicUpdatePropertyInt, msg.Opcode, "the Private variant carries no object guid and would target the player, not the item");

            var bytes = msg.Data.ToArray();
            var guidInPayload = BitConverter.ToUInt32(bytes, 4 + 1); // uint opcode, byte sequence, then guid
            Assert.AreEqual(zero.Guid.Full, guidInPayload, "the payload must name the item's guid");
            Assert.AreEqual((uint)PropertyInt.Value, BitConverter.ToUInt32(bytes, 4 + 1 + 4), "property is Value");
            Assert.AreEqual(1, BitConverter.ToInt32(bytes, 4 + 1 + 4 + 4), "override value is 1");
        }

        /// <summary>
        /// Live test 2026-09-02: the mule-open nudge alone was not enough - a pearl created after the
        /// mule opened, or appraised after it, was back at Value 0 on the client and refused again.
        /// WorldObject.ClientValue is the single rule every client-facing channel (create-object
        /// header, identify response) now reads: 1 for a zero/negative/null-Value non-creature item,
        /// the real Value for anything worth at least 1, and the real Value for creatures and corpses.
        /// </summary>
        [TestMethod]
        public void GetClientValue_SpoofsOneForZeroValueItemsOnly()
        {
            Assert.AreEqual(1, WorldObject.GetClientValue(MakeItemWithValue(0)), "zero-value item is told 1");
            Assert.AreEqual(1, WorldObject.GetClientValue(MakeItemWithValue(-5)), "negative-value item is told 1");
            Assert.AreEqual(1, WorldObject.GetClientValue(MakeItemWithValue(null)), "null-value item is told 1");
            Assert.AreEqual(250, WorldObject.GetClientValue(MakeItemWithValue(250)), "a real Value passes through untouched");

            var creature = TestCreatures.CreateQuestBearer("Zero Value Creature");
            Assert.AreEqual(0, WorldObject.GetClientValue(creature), "creatures are never spoofed");
            Assert.AreEqual(0, (creature.Value ?? 0), "and the creature's real Value is untouched");

            var fixture = MakeItemWithValue(0);
            fixture.Stuck = true;
            Assert.AreEqual(0, WorldObject.GetClientValue(fixture), "a Stuck fixture (door, portal, lifestone, hook, chest) is never spoofed");
        }

        /// <summary>
        /// The identify response is the channel that undid the first fix: it carried the item's real
        /// Value 0 and overwrote the client's nudged 1. ApplyClientValue rewrites an EXISTING Value
        /// entry of 0 or less to the ClientValue and leaves a real Value, or an absent one, alone.
        /// </summary>
        [TestMethod]
        public void ApplyClientValue_RewritesOnlyAnExistingZeroValueEntry()
        {
            var zero = MakeItemWithValue(0);
            var ints = new Dictionary<PropertyInt, int> { { PropertyInt.Value, 0 } };
            ACE.Server.Network.Structure.AppraiseInfo.ApplyClientValue(zero, ints);
            Assert.AreEqual(1, ints[PropertyInt.Value], "an existing Value 0 entry becomes 1");

            var real = MakeItemWithValue(700);
            ints = new Dictionary<PropertyInt, int> { { PropertyInt.Value, 700 } };
            ACE.Server.Network.Structure.AppraiseInfo.ApplyClientValue(real, ints);
            Assert.AreEqual(700, ints[PropertyInt.Value], "a real Value is untouched");

            var absent = MakeItemWithValue(null);
            ints = new Dictionary<PropertyInt, int>();
            ACE.Server.Network.Structure.AppraiseInfo.ApplyClientValue(absent, ints);
            Assert.IsFalse(ints.ContainsKey(PropertyInt.Value), "no Value entry is added where none was sent");

            var creature = TestCreatures.CreateQuestBearer("Appraised Creature");
            ints = new Dictionary<PropertyInt, int> { { PropertyInt.Value, 0 } };
            ACE.Server.Network.Structure.AppraiseInfo.ApplyClientValue(creature, ints);
            Assert.AreEqual(0, ints[PropertyInt.Value], "a creature's Value 0 stays 0");

            // Code review on 60471c991: BuildProfile's Container block masks a salvage bag's accumulated
            // Value down to its base weenie's (0). The rewrite must not leak the real Value back out.
            var maskedBag = MakeItemWithValue(5000);
            ints = new Dictionary<PropertyInt, int> { { PropertyInt.Value, 0 } };
            ACE.Server.Network.Structure.AppraiseInfo.ApplyClientValue(maskedBag, ints);
            Assert.AreEqual(0, ints[PropertyInt.Value], "an entry masked to 0 for an item with a real positive Value stays 0, never the real Value");
        }

        /// <summary>
        /// Third client channel that carries an item's Value: the stack-size message sent on every
        /// split or merge. Code review on 60471c991 caught it still writing the raw Value, which would
        /// hand the client a 0 again for a zero-value stackable. Layout: uint opcode, byte sequence,
        /// uint guid, uint stack size, uint value.
        /// </summary>
        [TestMethod]
        public void SetStackSize_WritesClientValue()
        {
            var zero = MakeItemWithValue(0);
            zero.SetProperty(PropertyInt.StackSize, 3);

            var bytes = new ACE.Server.Network.GameMessages.Messages.GameMessageSetStackSize(zero).Data.ToArray();

            Assert.AreEqual(zero.Guid.Full, BitConverter.ToUInt32(bytes, 4 + 1), "guid");
            Assert.AreEqual(3u, BitConverter.ToUInt32(bytes, 4 + 1 + 4), "stack size");
            Assert.AreEqual(1u, BitConverter.ToUInt32(bytes, 4 + 1 + 4 + 4), "a zero-value stack is told 1");
        }

        #endregion

        #region /mule search filter

        /// <summary>
        /// The stored (non-ledger) side of the filter: two stored items in two separate vault
        /// containers, one whose name matches the pattern and one that does not. Only the matching one
        /// is materialized into the view, and LastRebuildShown/LastRebuildTotal report the split.
        ///
        /// The ledger side of this filter (matching against DatabaseManager.World.GetCachedWeenie) is
        /// NOT covered here, per this task's own instruction to say so explicitly rather than fake a
        /// shortcut around the real GetCachedWeenie call path - SeedWorldStackableWeenie/SeedLedger seed
        /// exactly that cache via reflection, so the call path IS real, but no test below exercises it;
        /// only the stored-item path is asserted.
        /// </summary>
        [TestMethod]
        public void RebuildView_WithAFilter_ShowsOnlyMatchingStoredItems()
        {
            EnsureGuidManagerConstructible();

            var vendor = MakeVendor();
            var store = MakeStore(out var backend, out var world);
            vendor.Store = store;

            // "Test Item 8100" (FakeVaultWorld.MakeStack's own naming) - matches "8100".
            var (_, matching) = SeedStoredItem(backend, world, wcid: 8100);

            var container2 = FakeVaultWorld.MakeContainer(255);
            world.Containers[container2.Guid.Full] = container2;
            backend.Vaults.Add(new ShardAccountVault { Id = 2, AccountId = OwnerAccount, ContainerGuid = container2.Guid.Full, CreatedAt = new DateTime(2026, 1, 2) });
            var nonMatching = FakeVaultWorld.MakeStack(8101, 1, 100);
            Assert.IsTrue(container2.TryAddToInventory(nonMatching));

            vendor.SetSearchFilter(new Regex("8100"));
            vendor.RebuildView();

            Assert.IsTrue(vendor.TryGetItemForSale(matching.Guid, out _), "the matching item must be shown");
            Assert.IsFalse(vendor.TryGetItemForSale(nonMatching.Guid, out _), "the non-matching item must be filtered out");

            Assert.AreEqual(1, vendor.LastRebuildShown);
            Assert.AreEqual(2, vendor.LastRebuildTotal);
        }

        /// <summary>
        /// Clearing the filter (SetSearchFilter(null)) restores every row - and forces the rebuild to
        /// actually run rather than short-circuiting on the cheap-exit, since SetSearchFilter resets
        /// builtFromVersion for exactly that reason.
        /// </summary>
        [TestMethod]
        public void RebuildView_ClearingTheFilter_RestoresAllRows()
        {
            EnsureGuidManagerConstructible();

            var vendor = MakeVendor();
            var store = MakeStore(out var backend, out var world);
            vendor.Store = store;

            var (_, first) = SeedStoredItem(backend, world, wcid: 8102);

            var container2 = FakeVaultWorld.MakeContainer(255);
            world.Containers[container2.Guid.Full] = container2;
            backend.Vaults.Add(new ShardAccountVault { Id = 2, AccountId = OwnerAccount, ContainerGuid = container2.Guid.Full, CreatedAt = new DateTime(2026, 1, 2) });
            var second = FakeVaultWorld.MakeStack(8103, 1, 100);
            Assert.IsTrue(container2.TryAddToInventory(second));

            vendor.SetSearchFilter(new Regex("8102"));
            vendor.RebuildView();

            Assert.AreEqual(1, vendor.LastRebuildShown, "sanity: the filter must actually be filtering before it is cleared");

            vendor.SetSearchFilter(null);
            vendor.RebuildView();

            Assert.IsTrue(vendor.TryGetItemForSale(first.Guid, out _));
            Assert.IsTrue(vendor.TryGetItemForSale(second.Guid, out _));
            Assert.AreEqual(2, vendor.LastRebuildShown);
            Assert.AreEqual(2, vendor.LastRebuildTotal);
        }

        /// <summary>
        /// Code review on a2677799e, finding 2: the ledger branch
        /// (a Ledger-kind entry -> DatabaseManager.World.GetCachedWeenie(entry.Wcid) ->
        /// ItemTextSearch.SearchText(weenie) -> continue on no match) had no dedicated filter test,
        /// only the stored/non-ledger branch above did. SeedWorldStackableWeenie names its weenie
        /// "Ledger Item {wcid}" (see that helper's own definition), so filtering on the wcid itself
        /// exercises the exact same GetCachedWeenie call path RebuildView uses, not a shortcut around it.
        /// </summary>
        [TestMethod]
        public void RebuildView_WithAFilter_ShowsOnlyMatchingLedgerRows()
        {
            EnsureGuidManagerConstructible();
            SeedWorldStackableWeenie(8200, 100);
            SeedWorldStackableWeenie(8201, 100);

            var vendor = MakeVendor();
            var store = MakeStore(out var backend, out _);
            vendor.Store = store;

            SeedLedger(backend, 8200, 5);
            SeedLedger(backend, 8201, 5);

            vendor.SetSearchFilter(new Regex("8200"));
            vendor.RebuildView();

            Assert.IsTrue(vendor.DefaultItemsForSale.Values.Any(d => d.WeenieClassId == 8200), "the matching ledger row must be shown");
            Assert.IsFalse(vendor.DefaultItemsForSale.Values.Any(d => d.WeenieClassId == 8201), "the non-matching ledger row must be filtered out");

            Assert.AreEqual(1, vendor.LastRebuildShown);
            Assert.AreEqual(2, vendor.LastRebuildTotal);
        }

        /// <summary>
        /// Code review on a2677799e, finding 1 (mechanism half - see
        /// MuleSummonTests.CallSite_HandleActionMuleSearch_NeverDiscardsATryPrepareApproachFailure for
        /// the call-site half no live Player/Session can drive here). A vault whose async load has not
        /// finished (Container.InventoryLoaded false - the same R4 state AccountVaultStoreTests models
        /// via FakeVaultWorld.SetInventoryLoaded) makes TryAuthorize refuse with StillLoadingMessage
        /// before RebuildView ever runs, so LastRebuildShown/LastRebuildTotal never move off their 0/0
        /// default. This is exactly the race HandleActionMuleSearch must not misreport as a real "0 of 0
        /// entries shown" count for a freshly summoned, still-loading vendor.
        /// </summary>
        [TestMethod]
        public void TryPrepareApproach_OnAColdStore_RefusesWithStillLoadingMessage_AndNeverRebuilds()
        {
            var vendor = MakeVendor();
            var store = MakeStore(out var backend, out var world);
            vendor.Store = store;

            var (vault, _) = SeedStoredItem(backend, world);
            FakeVaultWorld.SetInventoryLoaded(vault, false);

            var ok = vendor.TryPrepareApproach(Owner, out var failReason);

            Assert.IsFalse(ok, "a cold store must refuse the approach rather than build a partial or empty view from it");
            Assert.AreEqual(AccountVaultStore.StillLoadingMessage, failReason);
            Assert.AreEqual(0, vendor.LastRebuildShown);
            Assert.AreEqual(0, vendor.LastRebuildTotal);
        }

        /// <summary>
        /// Independent code review, finding 1 (defect): the cheap-exit's own count clause
        /// (DefaultItemsForSale.Count + storeItems.Count > 0) exists only to catch a store still
        /// finishing its cold load - but a search filter that matches NOTHING against an already-loaded,
        /// non-empty store produces that exact same "materialized nothing" shape, so before this fix the
        /// count clause could not tell the two apart and re-ran the full GetEntries loop on EVERY
        /// approach for as long as the filter stood. Proven here by mutating the live container directly
        /// (bypassing the store's own mutation queue, so store.Version never bumps) between two
        /// RebuildView calls: AccountVaultStore's stored-item enumeration reads the container's live
        /// Inventory on every call (EnumerateStoredItemsLocked, AccountVaultStore.cs), so a genuine
        /// second pass through GetEntries WOULD see the extra item and LastRebuildTotal would grow to 2 -
        /// the exact wrong shown-count HandleActionMuleSearch would report. Staying at 1 is what proves
        /// the second RebuildView call short-circuited instead of recomputing.
        /// </summary>
        [TestMethod]
        public void RebuildView_WithAFilterMatchingNothing_ShortCircuitsOnASubsequentCall_WhenTheStoreIsLoaded()
        {
            EnsureGuidManagerConstructible();

            var vendor = MakeVendor();
            var store = MakeStore(out var backend, out var world);
            vendor.Store = store;

            var (vault, _) = SeedStoredItem(backend, world, wcid: 8300);

            vendor.SetSearchFilter(new Regex("does-not-match-anything"));
            vendor.RebuildView();

            Assert.AreEqual(0, vendor.LastRebuildShown, "sanity: the filter must actually match nothing");
            Assert.AreEqual(1, vendor.LastRebuildTotal, "sanity: the one seeded item must have been considered");

            // Added directly to the live container the store already holds a reference to - NOT through
            // the store's own mutation queue, so store.Version does not move. A short-circuited
            // RebuildView never calls GetEntries again and so can never see this item; a re-run one
            // would.
            var extra = FakeVaultWorld.MakeStack(8301, 1, 100);
            Assert.IsTrue(vault.TryAddToInventory(extra), "could not add the out-of-band item the short-circuit check depends on");

            vendor.RebuildView();

            Assert.AreEqual(1, vendor.LastRebuildTotal,
                "the second RebuildView call must have short-circuited on the version match rather than re-running GetEntries - a real second pass would have counted the extra item and read 2");
        }

        /// <summary>
        /// Independent code review, finding 1's own inverse: a still-loading store must keep retrying on
        /// every approach exactly as it did before this fix - the count clause remains the escape hatch
        /// for that case, and builtFromLoadedStore must never itself get stamped true from a cold build.
        /// Proven the same way AccountVaultStoreTests proves R4's own IsLoaded transition
        /// (IsLoaded_IsFalse_WhileAnyOneVaultIsUnloaded): flip Container.InventoryLoaded from false to
        /// true, with no store mutation and so no version bump (finishing a load is not itself a
        /// mutation - RebuildView's own remarks), and confirm the NEXT RebuildView call actually picks up
        /// the entries rather than staying cached on the first cold build's empty result.
        /// </summary>
        [TestMethod]
        public void RebuildView_OnAColdStoreWithAFilter_StillRebuildsOnceTheStoreFinishesLoading()
        {
            EnsureGuidManagerConstructible();

            var vendor = MakeVendor();
            var store = MakeStore(out var backend, out var world);
            vendor.Store = store;

            var (vault, item) = SeedStoredItem(backend, world, wcid: 8302);
            FakeVaultWorld.SetInventoryLoaded(vault, false);

            vendor.SetSearchFilter(new Regex("8302"));
            vendor.RebuildView();

            Assert.AreEqual(0, vendor.LastRebuildTotal, "sanity: a cold store's GetEntries must return nothing to consider yet");

            FakeVaultWorld.SetInventoryLoaded(vault, true);

            vendor.RebuildView();

            Assert.AreEqual(1, vendor.LastRebuildTotal, "the store finishing its load (with no version bump) must still be picked up on the next approach, not cached against the first cold build's empty result");
            Assert.AreEqual(1, vendor.LastRebuildShown, "the seeded item must now be shown - it matches the filter and the store is loaded");
            Assert.IsTrue(vendor.TryGetItemForSale(item.Guid, out _));
        }

        #endregion
    }
}
