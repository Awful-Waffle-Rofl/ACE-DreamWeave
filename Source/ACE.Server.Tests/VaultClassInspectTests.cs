using System;
using System.Collections.Generic;
using System.Linq;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Database.Models.Shard;
using ACE.Entity.Enum.Properties;
using ACE.Server.Command.Handlers;
using ACE.Server.Entity.AccountVault;
using ACE.Server.WorldObjects;

namespace ACE.Server.Tests
{
    /// <summary>
    /// /vaultclassinspect: the read-only way to see a counted class row, including one the store
    /// itself refused to load.
    ///
    /// WHY IT HAS TO EXIST. A folded class row holds no biota. If its canonical_Form stops parsing, or
    /// its wcid stops instantiating, the store skips the row on load - so the items are not lost (the
    /// row still carries count, total and payload) but they become invisible: they do not appear in
    /// the panel, they do not appear in /vaultclassdryrun, and nothing reports that they exist. This
    /// command reads the rows STRAIGHT FROM THE SHARD rather than through the store, which is the only
    /// way to see a row the store declined.
    ///
    /// Everything below drives the pure half, so none of it needs a world database.
    /// </summary>
    [TestClass]
    public class VaultClassInspectTests
    {
        private const uint BagWcid = 21013;

        private static VaultItemClassOverrides Payload(int? value, int? structure, int? workmanship, int? numItems, string name)
        {
            return new VaultItemClassOverrides(
                new[]
                {
                    new KeyValuePair<PropertyInt, int?>(PropertyInt.Value, value),
                    new KeyValuePair<PropertyInt, int?>(PropertyInt.Structure, structure),
                    new KeyValuePair<PropertyInt, int?>(PropertyInt.ItemWorkmanship, workmanship),
                    new KeyValuePair<PropertyInt, int?>(PropertyInt.NumItemsInMaterial, numItems),
                },
                new[] { new KeyValuePair<PropertyString, string>(PropertyString.Name, name) });
        }

        private static AccountVaultClass Row(uint wcid, long count, long total, string canonical, string key = null)
        {
            return new AccountVaultClass
            {
                AccountId = 7001,
                ClassKey = key ?? "0123456789abcdef0123456789abcdef",
                Wcid = wcid,
                Count = count,
                TotalValue = total,
                ValueBandPct = VaultItemClass.ValueExcluded,
                CanonicalForm = canonical,
            };
        }

        /// <summary>A healthy row's canonical form, and the key that really hashes from it.</summary>
        private static AccountVaultClass HealthyRow(long count = 3, long total = 11)
        {
            var payload = Payload(500, 100, 7, 10, "Salvage (100)");

            return Row(BagWcid, count, total,
                       VaultItemClass.CanonicalForm(BagWcid, payload, VaultItemClass.ValueExcluded),
                       VaultItemClass.ClassKey(BagWcid, payload, VaultItemClass.ValueExcluded));
        }

        private readonly List<WorldObject> destroyed = new List<WorldObject>();

        private WorldObject Build(uint wcid, VaultItemClassOverrides overrides, int? pooledValue)
        {
            return FakeVaultWorld.MakeSalvageBag(wcid,
                                                 overrides.GetInt(PropertyInt.Structure) ?? 0,
                                                 overrides.GetInt(PropertyInt.ItemWorkmanship),
                                                 overrides.GetInt(PropertyInt.NumItemsInMaterial),
                                                 pooledValue ?? 0,
                                                 overrides.GetString(PropertyString.Name));
        }

        private VaultClassInspectReport Inspect(IReadOnlyList<AccountVaultClass> rows,
                                                HashSet<string> served,
                                                Func<uint, VaultItemClassOverrides, int?, WorldObject> materialize = null)
        {
            return VaultClassCommands.BuildInspectReport("account test (7001)", rows, served,
                                                         materialize ?? Build,
                                                         probe => destroyed.Add(probe));
        }

        [TestMethod]
        public void HealthyRow_ReportsItsNumbers_AndTheShareTheNextWithdrawWouldHandOut()
        {
            var row = HealthyRow(count: 3, total: 11);

            var report = Inspect(new[] { row }, new HashSet<string>(StringComparer.Ordinal) { row.ClassKey });

            Assert.AreEqual(1, report.Rows.Count);
            Assert.AreEqual(0, report.StrandedCount);

            var line = report.Rows[0];

            Assert.AreEqual(BagWcid, line.Wcid);
            Assert.AreEqual(3, line.Count);
            Assert.AreEqual(11, line.TotalValue);
            Assert.AreEqual(4, line.PerItemShare, "round(11/3) is 4, which is what the next withdrawn item would carry");
            Assert.IsTrue(line.Parses);
            Assert.IsTrue(line.Materializes);
            Assert.IsTrue(line.Served);
            Assert.IsFalse(line.Stranded);
            Assert.AreEqual("Salvage (100)", line.Name);
            Assert.IsNull(line.Note);

            StringAssert.Contains(report.Render(), "1 row(s), 0 stranded");
        }

        /// <summary>
        /// The case the command exists for: a row whose payload no longer parses. It is STRANDED, it is
        /// reported, and it is reported as NOT served by the live store - which is precisely the state
        /// that was invisible before.
        /// </summary>
        [TestMethod]
        public void UnparseableRow_IsReportedAsStranded_AndAsNotServed()
        {
            var row = Row(BagWcid, 5, 500, "this is not a canonical form");

            var report = Inspect(new[] { row }, new HashSet<string>(StringComparer.Ordinal));

            Assert.AreEqual(1, report.StrandedCount);

            var line = report.Rows[0];

            Assert.IsFalse(line.Parses);
            Assert.IsFalse(line.Materializes);
            Assert.IsFalse(line.Served);
            Assert.IsTrue(line.Stranded);
            StringAssert.Contains(line.Note, "does NOT parse");

            // The items are still accounted for, which is the half that makes this recoverable.
            Assert.AreEqual(5, line.Count);
            Assert.AreEqual(500, line.TotalValue);

            var rendered = report.Render();

            StringAssert.Contains(rendered, "STRANDED");
            StringAssert.Contains(rendered, "NOT served by the live store");
            StringAssert.Contains(rendered, "does not repair");
        }

        [TestMethod]
        public void RowWhoseWcidWillNotInstantiate_IsReportedAsStranded()
        {
            var row = HealthyRow();

            var report = Inspect(new[] { row }, new HashSet<string>(StringComparer.Ordinal), (_, __, ___) => null);

            var line = report.Rows[0];

            Assert.IsTrue(line.Parses, "the payload is fine; it is the weenie that is gone");
            Assert.IsFalse(line.Materializes);
            Assert.IsTrue(line.Stranded);
            StringAssert.Contains(line.Note, "will NOT instantiate");
        }

        /// <summary>
        /// The denormalized wcid column and the payload disagreeing is its own fault, distinct from a
        /// parse failure: neither source can be trusted to say what the items are, so the row is
        /// reported rather than resolved in favour of one of them.
        /// </summary>
        [TestMethod]
        public void RowWhoseColumnAndPayloadDisagreeOnWcid_IsReportedRatherThanResolved()
        {
            var payload = Payload(500, 100, 7, 10, "Salvage (100)");

            var row = Row(9999, 2, 20, VaultItemClass.CanonicalForm(BagWcid, payload, VaultItemClass.ValueExcluded));

            var report = Inspect(new[] { row }, new HashSet<string>(StringComparer.Ordinal));

            var line = report.Rows[0];

            Assert.IsTrue(line.Parses);
            Assert.IsFalse(line.Materializes, "nothing may be built while the two sources disagree");
            Assert.IsTrue(line.Stranded);
            StringAssert.Contains(line.Note, "disagrees");

            Assert.AreEqual(0, destroyed.Count, "nothing was built, so nothing should have been destroyed");
        }

        /// <summary>
        /// READ ONLY, and the probe is the one place that could break it. Every object built to answer
        /// "does this still materialize" is destroyed, so a long inspect leaves nothing behind but
        /// consumed guids - and the report never mutates a row.
        /// </summary>
        [TestMethod]
        public void EveryProbeIsDestroyed_AndNoRowIsMutated()
        {
            var rows = new[] { HealthyRow(3, 11), HealthyRow(1, 7), HealthyRow(10, 100) };

            var before = rows.Select(r => (r.Count, r.TotalValue, r.CanonicalForm, r.Wcid)).ToList();

            var report = Inspect(rows, new HashSet<string>(StringComparer.Ordinal));

            Assert.AreEqual(3, report.Rows.Count);
            Assert.AreEqual(3, destroyed.Count, "one probe per healthy row, and every one destroyed");
            Assert.IsTrue(destroyed.All(p => p != null));

            for (var i = 0; i < rows.Length; i++)
                Assert.AreEqual(before[i], (rows[i].Count, rows[i].TotalValue, rows[i].CanonicalForm, rows[i].Wcid),
                    "inspect must not repair, fold or otherwise mutate a row");
        }

        /// <summary>
        /// A store that could not be read makes every Served flag meaningless, so it is rendered as
        /// UNKNOWN rather than as "not served". Reporting an unreadable store as "not served" would
        /// turn a transient outage into a fleet of false stranded rows.
        /// </summary>
        [TestMethod]
        public void AnUnreadableStore_RendersServedAsUnknown_NotAsFalse()
        {
            var report = Inspect(new[] { HealthyRow() }, served: null);

            Assert.IsFalse(report.ServedKeysKnown);
            StringAssert.Contains(report.Render(), "served unknown");

            // Control: with a readable store the same row renders a definite answer.
            var known = Inspect(new[] { HealthyRow() }, new HashSet<string>(StringComparer.Ordinal));

            Assert.IsTrue(known.ServedKeysKnown);
            StringAssert.Contains(known.Render(), "NOT served by the live store");
        }

        [TestMethod]
        public void NoRows_RendersEmptyRatherThanBlank()
        {
            var report = Inspect(Array.Empty<AccountVaultClass>(), new HashSet<string>(StringComparer.Ordinal));

            Assert.AreEqual(0, report.Rows.Count);
            StringAssert.Contains(report.Render(), "(none)");
        }

        /// <summary>Stranded rows sort FIRST, because they are the only reason to run this command.</summary>
        [TestMethod]
        public void StrandedRowsSortFirst()
        {
            var healthy = HealthyRow();
            var broken = Row(BagWcid, 1, 1, "garbage", key: "ffffffffffffffffffffffffffffffff");

            var report = Inspect(new[] { healthy, broken }, new HashSet<string>(StringComparer.Ordinal) { healthy.ClassKey });

            var rendered = report.Render();

            Assert.IsTrue(rendered.IndexOf(broken.ClassKey, StringComparison.Ordinal) < rendered.IndexOf(healthy.ClassKey, StringComparison.Ordinal),
                "the stranded row must be listed before the healthy one");
        }
    }
}
