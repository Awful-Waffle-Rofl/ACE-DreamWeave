using System;
using System.Collections.Generic;
using System.Linq;

using ACE.Entity.Models;
using ACE.Server.MlDigsite;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ACE.Server.Tests
{
    /// <summary>
    /// The ML digsite startup weenie warm-up.
    ///
    /// Everything exercised here is either pure or takes its world-database access as a delegate, so no test
    /// in this class reads PropertyManager, opens a database or touches a landblock. That is deliberate: this
    /// class must pass when run ALONE (PropertyManager keeps its values in process-wide static caches that
    /// leak between test classes, so a class that reads an unseeded key passes only by luck of ordering).
    /// </summary>
    [TestClass]
    public class MlDigsiteWeenieWarmupTests
    {
        // ---- helpers ---------------------------------------------------------------------------------------

        private static Weenie Weenie(uint wcid, params uint[] createList)
        {
            return new Weenie
            {
                WeenieClassId = wcid,
                PropertiesCreateList = createList.Length == 0
                    ? null
                    : createList.Select(w => new PropertiesCreateList { WeenieClassId = w, StackSize = 1 }).ToList(),
            };
        }

        /// <summary>
        /// A stand-in for the world database's weenie cache: <paramref name="present"/> is what exists,
        /// <paramref name="cached"/> is what is already warm (so asking for it costs no miss), and
        /// <paramref name="throwOn"/> is a wcid whose read blows up. Also records the order wcids were asked
        /// for, which is how the create_list pass is observed.
        /// </summary>
        private sealed class FakeWorld
        {
            private readonly Dictionary<uint, Weenie> present;
            private readonly HashSet<uint> cached;
            private readonly HashSet<uint> throwOn;

            public readonly List<uint> Asked = new List<uint>();

            public long Misses;

            public FakeWorld(IEnumerable<Weenie> present, IEnumerable<uint> cached = null, IEnumerable<uint> throwOn = null)
            {
                this.present = present.ToDictionary(w => w.WeenieClassId);
                this.cached = new HashSet<uint>(cached ?? Enumerable.Empty<uint>());
                this.throwOn = new HashSet<uint>(throwOn ?? Enumerable.Empty<uint>());
            }

            public Weenie Get(uint wcid)
            {
                Asked.Add(wcid);

                if (throwOn.Contains(wcid))
                    throw new InvalidOperationException($"world database read failed for wcid {wcid}");

                if (!cached.Contains(wcid))
                {
                    // A miss is counted whether or not the row exists: GetCachedWeenie increments before it
                    // knows, and caches the null.
                    Misses++;
                    cached.Add(wcid);
                }

                return present.TryGetValue(wcid, out var weenie) ? weenie : null;
            }

            public long MissCounter() => Misses;
        }

        private static MlDigsiteRosterEntry Entry(uint wcid) => new MlDigsiteRosterEntry(wcid, 100, $"test {wcid}");

        // ---- the warm set is DERIVED from the roster -------------------------------------------------------

        /// <summary>
        /// The production wiring: every wcid of every role, plus the Kept Siraluun band, is in the warm set.
        /// The roster's own AllWcids is the independent source here - if a role is added to the enum and
        /// wired, both sides pick it up.
        /// </summary>
        [TestMethod]
        public void Collect_CoversEveryShippedRosterWcid()
        {
            var set = MlDigsiteWeenieWarmup.Collect(MlDigsiteRoster.Entries, MlDigsiteRoster.KeptSiraluun, null, null);

            foreach (var wcid in MlDigsiteRoster.AllWcids())
                Assert.IsTrue(set.Wcids.Contains(wcid), $"roster wcid {wcid} is not in the warm set");

            foreach (var entry in MlDigsiteRoster.KeptSiraluun)
                Assert.IsTrue(set.Wcids.Contains(entry.Wcid), $"Kept Siraluun wcid {entry.Wcid} is not in the warm set");

            // The Kept Siraluun band is NOT reachable through MlDigsiteRoster.Entries, so a warm set built
            // from the roles alone would miss all eight. Pin that it is genuinely a separate contribution.
            var rolesOnly = MlDigsiteWeenieWarmup.Collect(MlDigsiteRoster.Entries, null, null, null);

            Assert.IsFalse(rolesOnly.Wcids.Contains(MlDigsiteRoster.KeptSiraluun[0].Wcid),
                "the Kept Siraluun band must be contributed separately, not through the role tables");
        }

        /// <summary>
        /// THE derivation test: a roster that has grown an entry produces a warm set that has grown with it.
        /// The roster's real tables are private, so the fake is injected through the same delegate parameter
        /// production hands MlDigsiteRoster.Entries to.
        /// </summary>
        [TestMethod]
        public void Collect_FollowsTheRoster_WhenAnEntryIsAdded()
        {
            const uint added = 999001;

            Assert.IsFalse(MlDigsiteRoster.AllWcids().Contains(added), "the fake wcid must not already be a real roster entry");

            var baseline = MlDigsiteWeenieWarmup.Collect(MlDigsiteRoster.Entries, MlDigsiteRoster.KeptSiraluun, null, null);

            Assert.IsFalse(baseline.Wcids.Contains(added));

            Func<MlDigsiteRole, IReadOnlyList<MlDigsiteRosterEntry>> grown = role => role == MlDigsiteRole.Wave
                ? MlDigsiteRoster.Entries(role).Concat(new[] { Entry(added) }).ToList()
                : MlDigsiteRoster.Entries(role);

            var after = MlDigsiteWeenieWarmup.Collect(grown, MlDigsiteRoster.KeptSiraluun, null, null);

            Assert.IsTrue(after.Wcids.Contains(added), "a roster entry added to a band must appear in the warm set");
            Assert.AreEqual(baseline.Wcids.Count + 1, after.Wcids.Count, "exactly the added entry should be new");
        }

        /// <summary>A prop or reward wcid of 0 means "disabled" and must never be asked for.</summary>
        [TestMethod]
        public void Collect_DropsZeroWcids()
        {
            var set = MlDigsiteWeenieWarmup.Collect(role => Array.Empty<MlDigsiteRosterEntry>(), null,
                new uint[] { 0, 4242 }, new uint[] { 0 });

            CollectionAssert.AreEqual(new List<uint> { 4242 }, set.Wcids.ToList());
        }

        /// <summary>The prop and reward wcids traced from their creation sites are carried through.</summary>
        [TestMethod]
        public void Collect_CarriesPropAndRewardWcids()
        {
            var set = MlDigsiteWeenieWarmup.Collect(role => Array.Empty<MlDigsiteRosterEntry>(), null,
                new uint[] { 11, 12 }, new uint[] { 21, 22 });

            CollectionAssert.AreEqual(new List<uint> { 11, 12, 21, 22 }, set.Wcids.ToList());
        }

        // ---- the skip rules --------------------------------------------------------------------------------

        [TestMethod]
        public void ShouldRun_SkippedWhenPrecachingIsOn()
        {
            Assert.IsFalse(MlDigsiteWeenieWarmup.ShouldRun(true, true, out var reason));
            StringAssert.Contains(reason, "WorldDatabasePrecaching");
        }

        [TestMethod]
        public void ShouldRun_SkippedWhenTheTunableIsOff()
        {
            Assert.IsFalse(MlDigsiteWeenieWarmup.ShouldRun(false, false, out var reason));
            StringAssert.Contains(reason, MlDigsiteWeenieWarmup.EnabledKey);
        }

        [TestMethod]
        public void ShouldRun_RunsWhenPrecachingIsOffAndTheTunableIsOn()
        {
            Assert.IsTrue(MlDigsiteWeenieWarmup.ShouldRun(false, true, out var reason));
            Assert.IsNull(reason);
        }

        [TestMethod]
        public void EnabledKey_IsTheRegisteredTunableName()
        {
            // Pinned as a literal: the key is a database row name, so a rename here must be a deliberate edit
            // in two places rather than a silent drift away from config-defaults.tsv.
            Assert.AreEqual("ml_digsite_weenie_warmup", MlDigsiteWeenieWarmup.EnabledKey);
        }

        // ---- the pass itself -------------------------------------------------------------------------------

        [TestMethod]
        public void Warm_CountsLoadedAlreadyCachedAndMissing()
        {
            var set = MlDigsiteWeenieWarmup.Collect(role => role == MlDigsiteRole.Wave
                ? new[] { Entry(1), Entry(2), Entry(3) }
                : Array.Empty<MlDigsiteRosterEntry>(), null, null, null);

            // 1 exists and is cold, 2 exists and is already warm, 3 has no weenie at all.
            var world = new FakeWorld(new[] { Weenie(1), Weenie(2) }, cached: new uint[] { 2 });

            var result = MlDigsiteWeenieWarmup.Warm(set, world.Get, world.MissCounter);

            Assert.AreEqual(3, result.Wcids);
            Assert.AreEqual(2, result.Loaded, "wcid 1 and the missing wcid 3 both cost a read");
            Assert.AreEqual(1, result.AlreadyCached);
            Assert.AreEqual(1, result.Missing);
            Assert.AreEqual(0, result.Threw);
            CollectionAssert.AreEqual(new List<uint> { 3 }, result.MissingWcids);
        }

        [TestMethod]
        public void Warm_AWcidThatThrowsDoesNotAbortThePass()
        {
            var set = MlDigsiteWeenieWarmup.Collect(role => role == MlDigsiteRole.Wave
                ? new[] { Entry(1), Entry(2), Entry(3) }
                : Array.Empty<MlDigsiteRosterEntry>(), null, null, null);

            var world = new FakeWorld(new[] { Weenie(1), Weenie(3) }, throwOn: new uint[] { 2 });

            var result = MlDigsiteWeenieWarmup.Warm(set, world.Get, world.MissCounter);

            Assert.AreEqual(1, result.Threw);
            Assert.AreEqual(0, result.Missing, "a throw is not a missing weenie; it is an unknown outcome");
            CollectionAssert.AreEqual(new List<uint> { 1, 2, 3 }, world.Asked,
                "every wcid after the one that threw must still be asked for");
        }

        [TestMethod]
        public void Warm_ExpandsOneLevelOfCreateList()
        {
            var set = MlDigsiteWeenieWarmup.Collect(role => role == MlDigsiteRole.Wave
                ? new[] { Entry(1) }
                : Array.Empty<MlDigsiteRosterEntry>(), null, null, null);

            // 1 wields 50; 50 in turn "creates" 60, which must NOT be followed (one level only).
            var world = new FakeWorld(new[] { Weenie(1, 50), Weenie(50, 60), Weenie(60) });

            var result = MlDigsiteWeenieWarmup.Warm(set, world.Get, world.MissCounter);

            Assert.AreEqual(1, result.Wcids);
            Assert.AreEqual(1, result.CreateListWcids);
            Assert.AreEqual(1, result.CreateListLoaded);
            CollectionAssert.AreEqual(new List<uint> { 1, 50 }, world.Asked,
                "the create_list pass must warm the wielded item and stop there");
        }

        [TestMethod]
        public void Warm_DoesNotReaskForACreateListRowAlreadyInTheWarmSet()
        {
            var set = MlDigsiteWeenieWarmup.Collect(role => role == MlDigsiteRole.Wave
                ? new[] { Entry(1), Entry(2) }
                : Array.Empty<MlDigsiteRosterEntry>(), null, null, null);

            // Both roster creatures wield each other's wcid; neither is a new create_list wcid.
            var world = new FakeWorld(new[] { Weenie(1, 2), Weenie(2, 1) });

            var result = MlDigsiteWeenieWarmup.Warm(set, world.Get, world.MissCounter);

            Assert.AreEqual(0, result.CreateListWcids);
            CollectionAssert.AreEqual(new List<uint> { 1, 2 }, world.Asked);
        }

        [TestMethod]
        public void Warm_CreateListMissingIsReported()
        {
            var set = MlDigsiteWeenieWarmup.Collect(role => role == MlDigsiteRole.Wave
                ? new[] { Entry(1) }
                : Array.Empty<MlDigsiteRosterEntry>(), null, null, null);

            // 1 wields 50, and 50 has no weenie in the database.
            var world = new FakeWorld(new[] { Weenie(1, 50) });

            var result = MlDigsiteWeenieWarmup.Warm(set, world.Get, world.MissCounter);

            Assert.AreEqual(1, result.CreateListMissing);
            CollectionAssert.AreEqual(new List<uint> { 50 }, result.MissingWcids);
        }

        // ---- the summary line ------------------------------------------------------------------------------

        [TestMethod]
        public void SummaryLine_ReportsTheCounts()
        {
            var set = MlDigsiteWeenieWarmup.Collect(role => role == MlDigsiteRole.Wave
                ? new[] { Entry(1), Entry(2), Entry(3), Entry(4) }
                : Array.Empty<MlDigsiteRosterEntry>(), null, null, null);

            // 1 cold and wielding 50; 2 already cached; 3 absent from the database; 4 throws.
            var world = new FakeWorld(new[] { Weenie(1, 50), Weenie(2), Weenie(50) },
                cached: new uint[] { 2 }, throwOn: new uint[] { 4 });

            var result = MlDigsiteWeenieWarmup.Warm(set, world.Get, world.MissCounter);

            var line = MlDigsiteWeenieWarmup.SummaryLine(result);

            StringAssert.Contains(line, "wcids=4");
            StringAssert.Contains(line, "loaded=2");
            StringAssert.Contains(line, "alreadyCached=1");
            StringAssert.Contains(line, "missing=1");
            StringAssert.Contains(line, "threw=1");
            StringAssert.Contains(line, "createListWcids=1");
            StringAssert.Contains(line, "createListLoaded=1");
            StringAssert.Contains(line, "createListMissing=0");
            StringAssert.Contains(line, "ms=");
        }

        [TestMethod]
        public void SummaryLine_SurvivesANullResult()
        {
            Assert.IsNotNull(MlDigsiteWeenieWarmup.SummaryLine(null));
        }
    }
}
