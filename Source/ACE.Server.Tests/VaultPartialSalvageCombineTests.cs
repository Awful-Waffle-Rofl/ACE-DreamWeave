using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Database;
using ACE.Database.Models.Shard;
using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Entity.Models;
using ACE.Server.Entity.AccountVault;
using ACE.Server.Managers;

namespace ACE.Server.Tests
{
    /// <summary>
    /// The mule partial-salvage combine, store side: AccountVaultStore.ConsolidatePartialSalvageOnQueue
    /// driven directly against seeded class rows. The deposit trigger is covered in PersonalVendorTests.
    ///
    /// Three oracles run on every combine that applies: structure units (Structure x count) per wcid are
    /// conserved exactly; total pooled value drops by exactly ValueCappedOff + ValueRoundingDelta; and a
    /// second pass makes ZERO exchange calls.
    /// </summary>
    [TestClass]
    public class VaultPartialSalvageCombineTests
    {
        private const uint OwnerAccount = 6301;
        private const uint OwnerCharacter = 0x50000301;

        /// <summary>Iron and Tiger Eye salvage, both in Player.MaterialSalvage.</summary>
        private const uint Iron = 20986;
        private const uint TigerEye = 21081;

        /// <summary>The Tiger Eye Hammer: a salvage TOOL, and not in Player.MaterialSalvage.</summary>
        private const uint TigerEyeHammer = 1001918;

        private static readonly VaultActor Owner = new VaultActor(OwnerAccount, OwnerCharacter, "Combineowner");

        private FakeVaultBackend backend;
        private FakeVaultWorld world;
        private uint nextRowId = 1;

        [TestInitialize]
        public void Setup()
        {
            backend = new FakeVaultBackend();
            world = new FakeVaultWorld { PristineResult = false, ClassifyResult = true };

            Assert.IsTrue(PropertyManager.ModifyLong("account_vault_entry_cap", 100000));
            Assert.IsTrue(PropertyManager.ModifyLong("account_vault_landblock",
                DefaultPropertyManager.DefaultLongProperties["account_vault_landblock"].Item));
            Assert.IsTrue(PropertyManager.ModifyLong("salvage_bag_value_cap", 150000));

            VaultClassTestConfig.Seed();

            EnsurePlayerStaticFieldsSeeded();

            AccountVaultStore.IsListedHook = null;
            AccountVaultStore.IsListedHookIndexTrusted = null;
        }

        private static bool playerStaticFieldsSeeded;

        private static uint nextSeededWcid = 90950;

        /// <summary>
        /// The combine's material allowlist reads Player.MaterialSalvage, a static FIELD, so it runs Player's
        /// static initializer, which looks up "portalmarketplace" and ten PK-arena portal weenies by name
        /// (Player_Location.cs) and NREs with no world database. Same fix as MarketSalvageMaterialsTests and
        /// its siblings: seed those names into WorldDatabaseWithEntityCache's private caches first.
        /// </summary>
        internal static void EnsurePlayerStaticFieldsSeeded()
        {
            if (playerStaticFieldsSeeded)
                return;

            var cacheField = typeof(WorldDatabaseWithEntityCache).GetField("weenieCache", BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.IsNotNull(cacheField, "WorldDatabaseWithEntityCache.weenieCache was not found by reflection - has it been renamed?");
            var nameField = typeof(WorldDatabaseWithEntityCache).GetField("weenieClassNameToClassIdCache", BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.IsNotNull(nameField, "WorldDatabaseWithEntityCache.weenieClassNameToClassIdCache was not found by reflection - has it been renamed?");

            var cache = (ConcurrentDictionary<uint, Weenie>)cacheField.GetValue(DatabaseManager.World);
            var nameCache = (ConcurrentDictionary<string, uint>)nameField.GetValue(DatabaseManager.World);

            foreach (var name in new[]
            {
                "portalmarketplace",
                "portalpkarenanew1", "portalpkarenanew2", "portalpkarenanew3", "portalpkarenanew4", "portalpkarenanew5",
                "portalpklarenanew1", "portalpklarenanew2", "portalpklarenanew3", "portalpklarenanew4", "portalpklarenanew5",
            })
            {
                // A name some earlier class already seeded is left pointing where it points.
                if (nameCache.ContainsKey(name))
                    continue;

                var wcid = ++nextSeededWcid;
                cache[wcid] = new Weenie { WeenieClassId = wcid, WeenieType = WeenieType.Generic };
                nameCache[name] = wcid;
            }

            playerStaticFieldsSeeded = true;
        }

        [TestCleanup]
        public void Cleanup()
        {
            AccountVaultStore.IsListedHook = null;
            AccountVaultStore.IsListedHookIndexTrusted = null;

            PropertyManager.ModifyLong("account_vault_entry_cap", DefaultPropertyManager.DefaultLongProperties["account_vault_entry_cap"].Item);
            PropertyManager.ModifyLong("salvage_bag_value_cap", DefaultPropertyManager.DefaultLongProperties["salvage_bag_value_cap"].Item);
            PropertyManager.ModifyBool("account_vault_class_storage", DefaultPropertyManager.DefaultBooleanProperties["account_vault_class_storage"].Item);
            PropertyManager.ModifyBool("account_vault_salvage_combine", DefaultPropertyManager.DefaultBooleanProperties["account_vault_salvage_combine"].Item);
        }

        // ------------------------------------------------------------------ fixture helpers

        private AccountVaultStore NewStore() => new AccountVaultStore(OwnerAccount, backend, world);

        internal static VaultItemClassOverrides Shape(int structure, int? iw, int? nim, string name = null)
        {
            return new VaultItemClassOverrides(
                new[]
                {
                    new KeyValuePair<PropertyInt, int?>(PropertyInt.Value, null),
                    new KeyValuePair<PropertyInt, int?>(PropertyInt.Structure, structure),
                    new KeyValuePair<PropertyInt, int?>(PropertyInt.ItemWorkmanship, iw),
                    new KeyValuePair<PropertyInt, int?>(PropertyInt.NumItemsInMaterial, nim),
                },
                new[] { new KeyValuePair<PropertyString, string>(PropertyString.Name, name ?? $"Salvage ({structure})") });
        }

        /// <summary>Seeds one account_vault_class row exactly as a deposit would have written it, and returns its key.</summary>
        internal static string SeedRow(FakeVaultBackend backend, uint accountId, ref uint nextId, uint wcid, int structure, int iw, int nim, long count, long total, string name = null)
        {
            var overrides = Shape(structure, iw, nim, name);
            var key = VaultItemClass.ClassKey(wcid, overrides, VaultItemClass.ValueExcluded);

            backend.Classes.Add(new AccountVaultClass
            {
                Id = nextId++,
                AccountId = accountId,
                ClassKey = key,
                Wcid = wcid,
                Count = count,
                TotalValue = total,
                ValueBandPct = VaultItemClass.ValueExcluded,
                CanonicalForm = VaultItemClass.CanonicalForm(wcid, overrides, VaultItemClass.ValueExcluded),
            });

            return key;
        }

        private string Seed(uint wcid, int structure, int iw, int nim, long count, long total, string name = null)
            => SeedRow(backend, OwnerAccount, ref nextRowId, wcid, structure, iw, nim, count, total, name);

        private static SalvageCombineSummary Run(AccountVaultStore store, params uint[] wcids)
        {
            SalvageCombineSummary summary = null;

            Assert.IsTrue(store.Enqueue(() =>
            {
                store.ConsolidatePartialSalvageOnQueue(wcids, Owner, out var result);
                summary = result;
            }), "the store refused the queued work");

            Assert.IsNotNull(summary, "the pass must always report a summary");

            return summary;
        }

        private AccountVaultClass Row(string key) => backend.Classes.SingleOrDefault(c => c.ClassKey == key);

        private List<AccountVaultClass> LiveRows(uint wcid) => backend.Classes.Where(c => c.AccountId == OwnerAccount && c.Wcid == wcid && c.Count > 0).ToList();

        internal static int StructureOf(AccountVaultClass row)
        {
            Assert.IsTrue(VaultItemClass.TryParseCanonicalForm(row.CanonicalForm, out _, out var overrides, out _), "a row the combine wrote must parse");

            return overrides.GetInt(PropertyInt.Structure) ?? 0;
        }

        internal static (long Units, long Value) Totals(FakeVaultBackend backend, uint accountId, uint wcid)
        {
            var rows = backend.Classes.Where(c => c.AccountId == accountId && c.Wcid == wcid && c.Count > 0).ToList();

            return (rows.Sum(r => r.Count * StructureOf(r)), rows.Sum(r => r.TotalValue));
        }

        /// <summary>The three oracles, around one applied pass.</summary>
        private SalvageCombineSummary RunWithOracles(AccountVaultStore store, params uint[] wcids)
        {
            (long Units, long Value) SumTotals() => wcids.Select(x => Totals(backend, OwnerAccount, x)).Aggregate((a, b) => (a.Units + b.Units, a.Value + b.Value));

            var before = SumTotals();

            var summary = Run(store, wcids);

            string why = null;

            if (backend.LastClassExchangeSteps != null)
                ShardDatabase.IsValidAccountVaultClassExchange(OwnerAccount, backend.LastClassExchangeSteps, out why);

            Assert.IsTrue(summary.Applied, $"the pass must have applied (exchange calls {backend.ClassExchangeCalls}, request {why ?? "valid"}, bags {summary.BagsIn})");

            var after = SumTotals();

            Assert.AreEqual(before.Units, after.Units, "structure units must be conserved exactly");
            Assert.AreEqual(before.Value - summary.ValueCappedOff - summary.ValueRoundingDelta, after.Value, "value must drop by exactly the capped-off and rounding amounts");

            var calls = backend.ClassExchangeCalls;
            var second = Run(store, wcids);

            Assert.IsFalse(second.Applied, "a second pass must be a no-op");
            Assert.AreEqual(calls, backend.ClassExchangeCalls, "a second pass must make ZERO exchange calls");

            return summary;
        }

        // ------------------------------------------------------------------ the core combine

        [TestMethod]
        public void TwoPartials_OfOneMaterial_BecomeOneRow()
        {
            var a = Seed(Iron, 30, 60, 10, 1, 1000);
            var b = Seed(Iron, 40, 62, 10, 1, 2000);

            var store = NewStore();

            var summary = RunWithOracles(store, Iron);

            Assert.AreEqual(2, summary.BagsIn);
            Assert.AreEqual(0, summary.FullOut);
            Assert.AreEqual(1, summary.PartialOut);
            Assert.AreEqual(1, backend.ClassExchangeCalls);

            var live = LiveRows(Iron);
            Assert.AreEqual(1, live.Count, "two partials must become one row");
            Assert.AreEqual(1, live[0].Count);
            Assert.AreEqual(70, StructureOf(live[0]));

            Assert.IsNull(Row(a), "a drained input row is reaped");
            Assert.IsNull(Row(b), "a drained input row is reaped");

            // One Combine audit row per changed class row, carrying the value and the key.
            var audits = backend.Logs.Where(l => l.Action == (int)AccountVaultAction.Combine).ToList();
            Assert.AreEqual(3, audits.Count);
            Assert.IsTrue(audits.All(l => l.ClassKey != null && l.Value != null));
            Assert.AreEqual(-1L, audits.Sum(l => l.Count), "two debits of one item and one credit of one item net to -1");

            // The store's own view was reloaded: one class line.
            Assert.AreEqual(1, store.GetEntries(0, -1).Count(e => e.Kind == VaultEntryKind.Class));
        }

        [TestMethod]
        public void ThreeIdenticalPartials_InOneRow_MakeAFullBagAndARemainder()
        {
            Seed(Iron, 40, 60, 10, 3, 3000);

            var summary = RunWithOracles(NewStore(), Iron);

            Assert.AreEqual(3, summary.BagsIn, "bags are counted by items, not by rows");
            Assert.AreEqual(1, summary.FullOut);
            Assert.AreEqual(1, summary.PartialOut);

            var structures = LiveRows(Iron).Select(StructureOf).OrderBy(s => s).ToList();
            CollectionAssert.AreEqual(new List<int> { 20, 100 }, structures);
        }

        [TestMethod]
        public void PartialsOfDifferentWorkmanship_CombineIntoOneItemWeightedBag()
        {
            // Averages 7.2 and 8.9 (whole-number bands 7 and 8), 10 items each. Weighted: (72 + 89) / 20 = 8.05.
            // A partial keeps the raw sums (161 / 20), no rounding.
            Seed(Iron, 30, 72, 10, 1, 1000);    // 7.2
            Seed(Iron, 40, 89, 10, 1, 2000);    // 8.9

            var summary = RunWithOracles(NewStore(), Iron);

            Assert.AreEqual(2, summary.BagsIn);
            Assert.AreEqual(0, summary.FullOut);
            Assert.AreEqual(1, summary.PartialOut);

            var live = LiveRows(Iron);
            Assert.AreEqual(1, live.Count, "different workmanship must not keep two partials apart");
            Assert.AreEqual(70, StructureOf(live[0]));
            Assert.AreEqual(3000, live[0].TotalValue);

            Assert.IsTrue(VaultItemClass.TryParseCanonicalForm(live[0].CanonicalForm, out _, out var merged, out _));
            Assert.AreEqual(161, merged.GetInt(PropertyInt.ItemWorkmanship), "item-weighted: 72 + 89");
            Assert.AreEqual(20, merged.GetInt(PropertyInt.NumItemsInMaterial));
        }

        [TestMethod]
        public void AFullBagRow_IsUntouched()
        {
            var full = Seed(Iron, 100, 60, 10, 2, 9000);
            Seed(Iron, 30, 60, 10, 1, 1000);
            Seed(Iron, 40, 62, 10, 1, 2000);

            RunWithOracles(NewStore(), Iron);

            Assert.AreEqual(2, Row(full).Count, "a full-bag row must not be an input");
            Assert.AreEqual(9000, Row(full).TotalValue);
            Assert.IsFalse(backend.LastClassExchangeSteps.Any(s => s.ClassKey == full), "the full row must not appear in the exchange at all");
        }

        [TestMethod]
        public void AHammerAtPartialCharges_IsUntouched()
        {
            // A real hammer wcid: not a salvage material, so the allowlist refuses it before anything else.
            var h1 = Seed(TigerEyeHammer, 7, 60, 10, 1, 500);
            var h2 = Seed(TigerEyeHammer, 3, 60, 10, 1, 500);

            // A material wcid whose TEMPLATE declares a salvage-tool capacity: the template gate refuses it.
            world.MaterializeTransform = built =>
            {
                if (built.WeenieClassId == TigerEye)
                    built.SetProperty(PropertyInt.SalvageToolCharges, 10);

                return built;
            };

            var t1 = Seed(TigerEye, 30, 60, 10, 1, 1000);
            var t2 = Seed(TigerEye, 40, 60, 10, 1, 1000);

            // Positive control in the same pass: ordinary iron partials DO combine.
            Seed(Iron, 30, 60, 10, 1, 1000);
            Seed(Iron, 40, 62, 10, 1, 2000);

            var summary = Run(NewStore(), TigerEyeHammer, TigerEye, Iron);

            Assert.IsTrue(summary.Applied, "positive control: the iron partials must combine");
            Assert.AreEqual(2, summary.BagsIn, "only the two iron bags may go in");

            foreach (var key in new[] { h1, h2, t1, t2 })
            {
                Assert.AreEqual(1, Row(key).Count, "a hammer-shaped row must be untouched");
                Assert.IsFalse(backend.LastClassExchangeSteps.Any(s => s.ClassKey == key));
            }
        }

        [TestMethod]
        public void AListedLine_IsUntouched()
        {
            var listed = Seed(Iron, 30, 60, 10, 1, 1000);
            var b = Seed(Iron, 40, 62, 10, 1, 2000);
            var c = Seed(Iron, 20, 61, 10, 1, 500);

            var listedDisplayId = VaultCollapse.ClassDisplayId(VaultCollapse.ClassDisplayGroupKey(Iron, Shape(30, 60, 10)));

            AccountVaultStore.IsListedHook = (account, guid, wcid, displayId) => displayId == listedDisplayId;
            AccountVaultStore.IsListedHookIndexTrusted = () => true;

            var summary = Run(NewStore(), Iron);

            Assert.IsTrue(summary.Applied);
            Assert.AreEqual(2, summary.BagsIn);
            Assert.AreEqual(1, Row(listed).Count, "a listed line must be untouched");
            Assert.IsFalse(backend.LastClassExchangeSteps.Any(s => s.ClassKey == listed));
            Assert.IsNull(Row(b));
            Assert.IsNull(Row(c));
        }

        [TestMethod]
        public void AListingHookThatThrows_LeavesEverythingAlone()
        {
            Seed(Iron, 30, 60, 10, 1, 1000);
            Seed(Iron, 40, 62, 10, 1, 2000);

            AccountVaultStore.IsListedHook = (account, guid, wcid, displayId) => throw new InvalidOperationException("market index hiccup");
            AccountVaultStore.IsListedHookIndexTrusted = () => true;

            var summary = Run(NewStore(), Iron);

            Assert.IsFalse(summary.Applied, "the combine fails CLOSED on a listing hook that throws");
            Assert.AreEqual(0, backend.ClassExchangeCalls);
        }

        [TestMethod]
        public void ARenamedBag_IsUntouched()
        {
            var renamed = Seed(Iron, 30, 60, 10, 1, 1000, "My Lucky Iron");
            Seed(Iron, 40, 62, 10, 1, 2000);
            Seed(Iron, 20, 61, 10, 1, 500);

            var summary = Run(NewStore(), Iron);

            Assert.IsTrue(summary.Applied);
            Assert.AreEqual(2, summary.BagsIn);
            Assert.AreEqual(1, Row(renamed).Count, "a renamed bag is the player's and must be untouched");
            Assert.IsFalse(backend.LastClassExchangeSteps.Any(s => s.ClassKey == renamed));
        }

        [TestMethod]
        public void OnlyThePassedWcidsAreConsidered()
        {
            Seed(Iron, 30, 60, 10, 1, 1000);
            Seed(Iron, 40, 62, 10, 1, 2000);

            var summary = Run(NewStore(), TigerEye);

            Assert.IsFalse(summary.Applied);
            Assert.AreEqual(0, backend.ClassExchangeCalls);
        }

        // ------------------------------------------------------------------ gates

        [TestMethod]
        public void ServerTunableOff_MakesNoExchange()
        {
            Seed(Iron, 30, 60, 10, 1, 1000);
            Seed(Iron, 40, 62, 10, 1, 2000);

            Assert.IsTrue(PropertyManager.ModifyBool("account_vault_salvage_combine", false));

            var summary = Run(NewStore(), Iron);

            Assert.IsFalse(summary.Applied);
            Assert.AreEqual(0, backend.ClassExchangeCalls);
            Assert.AreEqual(2, LiveRows(Iron).Count);
        }

        [TestMethod]
        public void ClassStorageOff_MakesNoExchange()
        {
            Seed(Iron, 30, 60, 10, 1, 1000);
            Seed(Iron, 40, 62, 10, 1, 2000);

            VaultClassTestConfig.Seed(classStorageEnabled: false);

            var summary = Run(NewStore(), Iron);

            Assert.IsFalse(summary.Applied);
            Assert.AreEqual(0, backend.ClassExchangeCalls);
            Assert.AreEqual(2, LiveRows(Iron).Count);
        }

        private void SetAccountToggle(bool enabled)
        {
            backend.VaultSettings[OwnerAccount] = new AccountVaultSetting { AccountId = OwnerAccount, SalvageCombine = enabled, SetByCharacterGuid = OwnerCharacter };
        }

        /// <summary>
        /// THE DISCRIMINATING PAIR. Identical fixture; only the account toggle differs. OFF: two partial rows
        /// remain and the exchange is never called. ON (the positive control below): one row, one exchange.
        /// </summary>
        [TestMethod]
        public void AccountToggleOff_LeavesTwoPartialRows_AndMakesZeroExchangeCalls()
        {
            Seed(Iron, 30, 60, 10, 1, 1000);
            Seed(Iron, 40, 62, 10, 1, 2000);

            SetAccountToggle(false);

            var summary = Run(NewStore(), Iron);

            Assert.IsFalse(summary.Applied);
            Assert.AreEqual(0, backend.ClassExchangeCalls, "toggle OFF must make ZERO exchange calls");
            Assert.AreEqual(2, LiveRows(Iron).Count, "toggle OFF must leave both partial rows");
        }

        [TestMethod]
        public void AccountToggleOn_PositiveControl_CombinesWithOneExchange()
        {
            Seed(Iron, 30, 60, 10, 1, 1000);
            Seed(Iron, 40, 62, 10, 1, 2000);

            SetAccountToggle(true);

            var summary = Run(NewStore(), Iron);

            Assert.IsTrue(summary.Applied);
            Assert.AreEqual(1, backend.ClassExchangeCalls);
            Assert.AreEqual(1, LiveRows(Iron).Count);
        }

        [TestMethod]
        public void AccountToggleReadFailure_SkipsThePass()
        {
            Seed(Iron, 30, 60, 10, 1, 1000);
            Seed(Iron, 40, 62, 10, 1, 2000);

            backend.FailVaultSettingRead = true;

            var summary = Run(NewStore(), Iron);

            Assert.IsFalse(summary.Applied);
            Assert.AreEqual(0, backend.ClassExchangeCalls, "an unreadable setting must never be read as ON");
            Assert.AreEqual(2, LiveRows(Iron).Count);
        }

        [TestMethod]
        public void OffTheMutationQueue_IsANoOpAndDoesNotThrow()
        {
            Seed(Iron, 30, 60, 10, 1, 1000);
            Seed(Iron, 40, 62, 10, 1, 2000);

            var store = NewStore();

            store.ConsolidatePartialSalvageOnQueue(new[] { Iron }, Owner, out var summary);

            Assert.AreSame(SalvageCombineSummary.None, summary);
            Assert.AreEqual(0, backend.ClassExchangeCalls);
        }

        // ------------------------------------------------------------------ entry cap

        [TestMethod]
        public void EntryCap_SkipsAGroupThatWouldAddALinePastTheCap()
        {
            // One row of three Salvage (40): ONE panel line. Combining it yields a full bag and a
            // Salvage (20) - TWO lines.
            Seed(Iron, 40, 60, 10, 3, 3000);

            Assert.IsTrue(PropertyManager.ModifyLong("account_vault_entry_cap", 1));

            var summary = Run(NewStore(), Iron);

            Assert.IsFalse(summary.Applied);
            Assert.AreEqual(0, backend.ClassExchangeCalls, "a combine that would take the vault over its cap must be skipped");
        }

        [TestMethod]
        public void EntryCap_PositiveControl_AllowsTheSameGroupWithRoom()
        {
            Seed(Iron, 40, 60, 10, 3, 3000);

            Assert.IsTrue(PropertyManager.ModifyLong("account_vault_entry_cap", 2));

            var summary = Run(NewStore(), Iron);

            Assert.IsTrue(summary.Applied);
            Assert.AreEqual(1, backend.ClassExchangeCalls);
        }

        // ------------------------------------------------------------------ exchange outcomes

        [TestMethod]
        public void ExchangeFailedBeforeCommit_InvalidatesClasses_AndReportsNotApplied()
        {
            Seed(Iron, 30, 60, 10, 1, 1000);
            Seed(Iron, 40, 62, 10, 1, 2000);

            var store = NewStore();
            Assert.IsTrue(store.IsLoaded);

            backend.ClassExchangeFailMode = FakeVaultBackend.ExchangeFailure.BeforeCommit;

            var readsBefore = backend.ClassReads;

            var summary = Run(store, Iron);

            Assert.IsFalse(summary.Applied);
            Assert.AreEqual(1, backend.ClassExchangeCalls, "an unknown outcome must not be retried");
            Assert.IsTrue(backend.ClassReads > readsBefore, "the class map must be re-read after an unknown outcome");
            Assert.AreEqual(2, LiveRows(Iron).Count);
            Assert.AreEqual(0, backend.Logs.Count(l => l.Action == (int)AccountVaultAction.Combine), "no Combine audit rows for an unknown outcome");
        }

        [TestMethod]
        public void ExchangeFailedAfterCommit_InvalidatesClasses_AndTheStoreSeesThePostState()
        {
            Seed(Iron, 30, 60, 10, 1, 1000);
            Seed(Iron, 40, 62, 10, 1, 2000);

            var store = NewStore();
            Assert.IsTrue(store.IsLoaded);

            backend.ClassExchangeFailMode = FakeVaultBackend.ExchangeFailure.AfterCommit;

            var readsBefore = backend.ClassReads;

            var summary = Run(store, Iron);

            Assert.IsFalse(summary.Applied);
            Assert.IsTrue(backend.ClassReads > readsBefore, "the class map must be re-read after an unknown outcome");

            // The trade really landed; the reloaded store must show it rather than the stale pre-state.
            Assert.AreEqual(1, store.GetEntries(0, -1).Count(e => e.Kind == VaultEntryKind.Class && e.Count > 0 && e.Wcid == Iron));
        }

        [TestMethod]
        public void ExchangeRefused_ReloadsAndReportsNotApplied()
        {
            var a = Seed(Iron, 30, 60, 10, 1, 1000);
            Seed(Iron, 40, 62, 10, 1, 2000);

            var store = NewStore();
            Assert.IsTrue(store.IsLoaded);

            // The ledger moves underneath the store's in-memory map, so the exchange's pre-state is wrong.
            Row(a).TotalValue += 1;

            var readsBefore = backend.ClassReads;

            var summary = Run(store, Iron);

            Assert.IsFalse(summary.Applied);
            Assert.AreEqual(1, backend.ClassExchangeCalls);
            Assert.IsTrue(backend.ClassReads > readsBefore, "a refusal must reload the class map");
            Assert.AreEqual(2, LiveRows(Iron).Count, "a refused exchange moves nothing");
        }

        [TestMethod]
        public void AThrowInsideThePass_IsCaught_AndReportsNone()
        {
            Seed(Iron, 30, 60, 10, 1, 1000);
            Seed(Iron, 40, 62, 10, 1, 2000);

            var store = NewStore();
            store.BeforeSalvageCombineExchangeForTest = () => throw new InvalidOperationException("forced");

            var summary = Run(store, Iron);

            Assert.AreSame(SalvageCombineSummary.None, summary);
            Assert.AreEqual(0, backend.ClassExchangeCalls);
            Assert.AreEqual(2, LiveRows(Iron).Count);
        }

        // ------------------------------------------------------------------ market index trust (review F3)

        [TestMethod]
        public void MarketHookRegistered_ButIndexUntrusted_MakesZeroExchangeCalls()
        {
            Seed(Iron, 30, 60, 10, 1, 1000);
            Seed(Iron, 40, 62, 10, 1, 2000);

            AccountVaultStore.IsListedHook = (account, guid, wcid, displayId) => false;
            AccountVaultStore.IsListedHookIndexTrusted = () => false;

            var summary = Run(NewStore(), Iron);

            Assert.IsFalse(summary.Applied);
            Assert.AreEqual(0, backend.ClassExchangeCalls, "an untrusted index answers 'not listed' for listed lines, so nothing may be rewritten");
            Assert.AreEqual(2, LiveRows(Iron).Count);
        }

        [TestMethod]
        public void MarketHookRegistered_WithNoTrustPredicate_IsUntrusted()
        {
            Seed(Iron, 30, 60, 10, 1, 1000);
            Seed(Iron, 40, 62, 10, 1, 2000);

            AccountVaultStore.IsListedHook = (account, guid, wcid, displayId) => false;
            AccountVaultStore.IsListedHookIndexTrusted = null;

            Assert.IsFalse(Run(NewStore(), Iron).Applied);
            Assert.AreEqual(0, backend.ClassExchangeCalls);
        }

        [TestMethod]
        public void MarketHookRegistered_WithATrustPredicateThatThrows_IsUntrusted()
        {
            Seed(Iron, 30, 60, 10, 1, 1000);
            Seed(Iron, 40, 62, 10, 1, 2000);

            AccountVaultStore.IsListedHook = (account, guid, wcid, displayId) => false;
            AccountVaultStore.IsListedHookIndexTrusted = () => throw new InvalidOperationException("index state unreadable");

            Assert.IsFalse(Run(NewStore(), Iron).Applied);
            Assert.AreEqual(0, backend.ClassExchangeCalls);
        }

        [TestMethod]
        public void MarketHookRegistered_AndIndexTrusted_PositiveControl_Combines()
        {
            Seed(Iron, 30, 60, 10, 1, 1000);
            Seed(Iron, 40, 62, 10, 1, 2000);

            AccountVaultStore.IsListedHook = (account, guid, wcid, displayId) => false;
            AccountVaultStore.IsListedHookIndexTrusted = () => true;

            Assert.IsTrue(Run(NewStore(), Iron).Applied);
            Assert.AreEqual(1, backend.ClassExchangeCalls);
        }

        // ------------------------------------------------------------------ untested gates (review F5)

        [TestMethod]
        public void AnOpenDepositWindow_MakesZeroExchangeCalls_AndClosingItLetsThePassRun()
        {
            Seed(Iron, 30, 60, 10, 1, 1000);
            Seed(Iron, 40, 62, 10, 1, 2000);

            var store = NewStore();

            SalvageCombineSummary inWindow = null;

            Assert.IsTrue(store.Enqueue(() =>
            {
                using (store.BeginDepositWindow())
                {
                    store.ConsolidatePartialSalvageOnQueue(new[] { Iron }, Owner, out var result);
                    inWindow = result;
                }
            }));

            Assert.IsFalse(inWindow.Applied);
            Assert.AreEqual(0, backend.ClassExchangeCalls, "no combine may run while a deposit window is open");

            // Positive control: the same store, window closed.
            Assert.IsTrue(Run(store, Iron).Applied);
            Assert.AreEqual(1, backend.ClassExchangeCalls);
        }

        [TestMethod]
        public void MaxStepsPerPass_TheFirstGroupIsAlwaysTaken_AndALaterOneIsDeferred()
        {
            // Iron: 210 distinct partial rows of one material - more steps than the per-pass bound on its own.
            for (var structure = 1; structure <= 21; structure++)
            {
                for (var iw = 60; iw <= 69; iw++)
                    Seed(Iron, structure, iw, 10, 1, 10);
            }

            Assert.IsTrue(210 > AccountVaultStore.SalvageCombineMaxStepsPerPass, "sanity: the first group alone must exceed the bound");

            // Tiger Eye: a small second group, sorted after Iron by wcid.
            var t1 = Seed(TigerEye, 30, 60, 10, 1, 1000);
            var t2 = Seed(TigerEye, 40, 62, 10, 1, 2000);

            var store = NewStore();

            var first = Run(store, Iron, TigerEye);

            Assert.IsTrue(first.Applied, "the first group is always taken, however large");
            Assert.AreEqual(210, first.BagsIn, "only the iron group went in");
            Assert.IsTrue(backend.LastClassExchangeSteps.Count > AccountVaultStore.SalvageCombineMaxStepsPerPass);
            Assert.AreEqual(1, Row(t1).Count, "the second group must be deferred, not dropped into an oversized exchange");
            Assert.AreEqual(1, Row(t2).Count);

            var second = Run(store, Iron, TigerEye);

            Assert.IsTrue(second.Applied, "the deferred group runs on the next pass");
            Assert.AreEqual(2, second.BagsIn);
            Assert.AreEqual(1, LiveRows(TigerEye).Count);
        }

        [TestMethod]
        public void MaxBagsPerGroup_SkipsAnOversizedGroup_WhileAnotherGroupStillCombines()
        {
            var huge = Seed(Iron, 1, 6, 1, AccountVaultStore.SalvageCombineMaxBagsPerGroup + 1, 0);

            // Positive control in the same pass.
            Seed(TigerEye, 30, 60, 10, 1, 1000);
            Seed(TigerEye, 40, 62, 10, 1, 2000);

            var summary = Run(NewStore(), Iron, TigerEye);

            Assert.IsTrue(summary.Applied, "positive control: the tiger eye group combines");
            Assert.AreEqual(2, summary.BagsIn);
            Assert.AreEqual(AccountVaultStore.SalvageCombineMaxBagsPerGroup + 1, Row(huge).Count, "the oversized group must be untouched");
            Assert.IsFalse(backend.LastClassExchangeSteps.Any(s => s.ClassKey == huge));
        }

        [TestMethod]
        public void AnOutputThatDoesNotRoundTrip_SkipsItsGroup_WhileAnotherGroupStillCombines()
        {
            // Every Tiger Eye object the materializer builds comes back renamed, so a Tiger Eye output class
            // rebuilds to a different key and fails the round trip. Iron is untouched by the transform.
            world.MaterializeTransform = built =>
            {
                if (built.WeenieClassId == TigerEye)
                    built.Name = "Mangled";

                return built;
            };

            var t1 = Seed(TigerEye, 30, 60, 10, 1, 1000);
            var t2 = Seed(TigerEye, 40, 62, 10, 1, 2000);

            Seed(Iron, 30, 60, 10, 1, 1000);
            Seed(Iron, 40, 62, 10, 1, 2000);

            var summary = Run(NewStore(), Iron, TigerEye);

            Assert.IsTrue(summary.Applied, "positive control: the iron group combines");
            Assert.AreEqual(2, summary.BagsIn);
            Assert.AreEqual(1, Row(t1).Count, "a group whose output does not round-trip must be skipped whole");
            Assert.AreEqual(1, Row(t2).Count);
            Assert.IsFalse(backend.LastClassExchangeSteps.Any(s => s.ClassKey == t1 || s.ClassKey == t2));
        }

        [TestMethod]
        public void ANegativeValueCap_IsClampedToZero()
        {
            Seed(Iron, 30, 60, 10, 1, 1000);
            Seed(Iron, 40, 62, 10, 1, 2000);

            Assert.IsTrue(PropertyManager.ModifyLong("salvage_bag_value_cap", -5));

            var summary = RunWithOracles(NewStore(), Iron);

            // Clamped to 0, the cap takes the whole pooled value (CapValue clamps the target's running total,
            // its own starting value included). An UNclamped -5 would have produced a negative bag value.
            Assert.AreEqual(3000, summary.ValueCappedOff);
            Assert.IsTrue(backend.Classes.All(c => c.TotalValue >= 0));
            Assert.AreEqual(0, LiveRows(Iron).Single().TotalValue);
        }

        [TestMethod]
        public void AValueCapAboveIntMax_IsClampedToIntMax_AndCapsNothing()
        {
            Seed(Iron, 30, 60, 10, 1, 1000);
            Seed(Iron, 40, 62, 10, 1, 2000);

            Assert.IsTrue(PropertyManager.ModifyLong("salvage_bag_value_cap", long.MaxValue));

            var summary = RunWithOracles(NewStore(), Iron);

            Assert.AreEqual(0, summary.ValueCappedOff);
            Assert.AreEqual(3000, LiveRows(Iron).Single().TotalValue);
        }

        [TestMethod]
        public void TwoMaterialsInDifferentBands_CombineInOneExchange_WithHandComputedWorkmanship()
        {
            // Iron: 7.2 and 8.9 (bands 7 and 8), 60 + 40 structure, 10 items each. Full bag: Iw 72 + 89 = 161
            // over Nim 20 = 8.05, rounded to 8 -> Iw 160 / Nim 20.
            Seed(Iron, 60, 72, 10, 1, 1000);
            Seed(Iron, 40, 89, 10, 1, 2000);

            // Tiger Eye: 6.2 and 5.8 (bands 6 and 5), 70 + 30 structure, 5 items each. Full bag: Iw 31 + 29 = 60
            // over Nim 10 = 6.0 -> Iw 60 / Nim 10.
            Seed(TigerEye, 70, 31, 5, 1, 1500);
            Seed(TigerEye, 30, 29, 5, 1, 500);

            var summary = RunWithOracles(NewStore(), Iron, TigerEye);

            Assert.AreEqual(4, summary.BagsIn);
            Assert.AreEqual(2, summary.FullOut);
            Assert.AreEqual(0, summary.PartialOut);
            Assert.AreEqual(1, backend.ClassExchangeCalls, "both materials travel in ONE exchange");

            var iron = LiveRows(Iron).Single();
            var tiger = LiveRows(TigerEye).Single();

            Assert.AreEqual(100, StructureOf(iron));
            Assert.AreEqual(100, StructureOf(tiger));

            Assert.IsTrue(VaultItemClass.TryParseCanonicalForm(iron.CanonicalForm, out _, out var ironShape, out _));
            Assert.AreEqual(160, ironShape.GetInt(PropertyInt.ItemWorkmanship));
            Assert.AreEqual(20, ironShape.GetInt(PropertyInt.NumItemsInMaterial));

            Assert.IsTrue(VaultItemClass.TryParseCanonicalForm(tiger.CanonicalForm, out _, out var tigerShape, out _));
            Assert.AreEqual(60, tigerShape.GetInt(PropertyInt.ItemWorkmanship));
            Assert.AreEqual(10, tigerShape.GetInt(PropertyInt.NumItemsInMaterial));
        }

        [TestMethod]
        public void OneGroupMakingTwoIdenticalFullBags_NetsIntoOneStepWithCountDeltaTwo()
        {
            // Four 50-structure bags of identical shape in one row: pours 50+50 and 50+50, no splits. Each full
            // bag is Iw 120 / Nim 20 - the SAME class key twice within one group.
            Seed(Iron, 50, 60, 10, 4, 4000);

            var shared = VaultItemClass.ClassKey(Iron, Shape(100, 120, 20), VaultItemClass.ValueExcluded);

            var summary = RunWithOracles(NewStore(), Iron);

            Assert.AreEqual(4, summary.BagsIn);
            Assert.AreEqual(2, summary.FullOut);

            var sharedSteps = backend.LastClassExchangeSteps.Where(s => s.ClassKey == shared).ToList();
            Assert.AreEqual(1, sharedSteps.Count, "a key produced twice must be netted into a single step");
            Assert.AreEqual(2, sharedSteps[0].CountDelta);
            Assert.AreEqual(2, Row(shared).Count);
        }
        // ------------------------------------------------------------------ the player message

        [TestMethod]
        public void PlayerMessage_Formatter()
        {
            Assert.AreEqual("Your mule combined 3 partial salvage bags into 1 full bag and 1 partial bag.", SalvageCombineSummary.FormatPlayerMessage(3, 1, 1));
            Assert.AreEqual("Your mule combined 5 partial salvage bags into 2 full bags.", SalvageCombineSummary.FormatPlayerMessage(5, 2, 0));
            Assert.AreEqual("Your mule combined 2 partial salvage bags into 1 full bag.", SalvageCombineSummary.FormatPlayerMessage(2, 1, 0));
            Assert.AreEqual("Your mule combined 7 partial salvage bags into 2 full bags and 2 partial bags.", SalvageCombineSummary.FormatPlayerMessage(7, 2, 2));
            Assert.AreEqual("Your mule combined 2 partial salvage bags into 1 partial bag.", SalvageCombineSummary.FormatPlayerMessage(2, 0, 1));
            Assert.AreEqual("Your mule combined 4 partial salvage bags into 2 partial bags.", SalvageCombineSummary.FormatPlayerMessage(4, 0, 2));
            Assert.IsNull(SalvageCombineSummary.FormatPlayerMessage(0, 0, 0));
        }

        [TestMethod]
        public void PlayerMessage_IsNullUnlessApplied()
        {
            Assert.IsNull(new SalvageCombineSummary(3, 1, 1, 0, 0, applied: false).PlayerMessage);
            Assert.IsNull(SalvageCombineSummary.None.PlayerMessage);
            Assert.AreEqual("Your mule combined 3 partial salvage bags into 1 full bag and 1 partial bag.", new SalvageCombineSummary(3, 1, 1, 0, 0, applied: true).PlayerMessage);
        }
    }
}
