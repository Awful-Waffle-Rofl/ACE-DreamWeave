using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;

using ACE.Server.PuzzleGates;
using ACE.Server.Tests.ThreadDungeons;
using ACE.Server.ThreadDungeons;
using ACE.Server.WorldObjects;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using Position = ACE.Entity.Position;

namespace ACE.Server.Tests
{
    /// <summary>
    /// The registry side of run puzzles (review F4 and F5): the /puzzlegate test tool never touches a run's
    /// placements, the run-scoped clear and reap notify each host exactly once, the reshuffle-failure closure, the
    /// scored/unscored wrong split, and that every gate wcid the shipped site file uses is a Door weenie in the repo.
    /// Placements are put in the registry through TrackForTest (no landblock, so no spawn is queued); every test
    /// removes what it tracked.
    /// </summary>
    [TestClass]
    public class PuzzleGateRunIsolationTests
    {
        private const string ManagerPath = "Source/ACE.Server/PuzzleGates/PuzzleGateManager.cs";

        private static int nextId = 910000;

        private sealed class CountingHost : IPuzzleGateHost
        {
            public CountingHost(uint runId) => RunId = runId;

            public readonly List<(PuzzleRemovalReason Reason, bool Solved)> Removed = new List<(PuzzleRemovalReason, bool)>();
            public readonly List<string> Reports = new List<string>();

            public PuzzlePolicyMode PolicyMode => RunId == 0 ? PuzzlePolicyMode.None : PuzzlePolicyMode.Run;
            public uint RunId { get; }
            public bool AllowAmbush => RunId == 0;
            public string CheckActivation(PuzzleGatePlacement placement, Player player) => null;
            public void OnSolved(PuzzleGatePlacement placement, Player player) { }
            public void OnWrong(PuzzleGatePlacement placement, Player player, bool scored) { }
            public void OnRemoved(PuzzleGatePlacement placement, PuzzleRemovalReason reason, bool solved) => Removed.Add((reason, solved));
            public void Report(PuzzleGatePlacement placement, string text) => Reports.Add(text);
        }

        private static PuzzleGatePlacement Placement(IPuzzleGateHost host, DateTime? queued = null)
        {
            Assert.IsTrue(PuzzleGateOptions.TryParse(new[] { "odd" }, out var options, out _));
            var anchor = new Position(0x0150018A, 1, 2, 0, 0, 0, 0, 1, 0);
            return new PuzzleGatePlacement(System.Threading.Interlocked.Increment(ref nextId), options, 7, anchor, 0f, null, 0, null, queued ?? DateTime.UtcNow, host);
        }

        private static void WithTracked(Action body, params PuzzleGatePlacement[] placements)
        {
            foreach (var p in placements)
                PuzzleGateManager.TrackForTest(p);

            try
            {
                body();
            }
            finally
            {
                foreach (var p in placements)
                    PuzzleGateManager.UntrackForTest(p);
            }
        }

        // ---- F4: the admin tool never touches a run placement ---------------------------------------------

        [TestMethod]
        public void Clear_all_clears_admin_placements_and_leaves_run_placements_and_their_seal()
        {
            var run = ThreadLootTestFixtures.PooledRun();
            var admin = Placement(new CountingHost(0));
            var reward = Placement(new ThreadPuzzleRunHost(run, true));
            Assert.IsTrue(run.SealReward(reward));

            WithTracked(() =>
            {
                var reply = PuzzleGateManager.Clear(null, "all");

                Assert.IsFalse(PuzzleGateManager.IsRegistered(admin), "the admin placement is cleared");
                Assert.IsTrue(PuzzleGateManager.IsRegistered(reward), "the run placement is not");
                Assert.IsTrue(run.IsRewardSealed, "and the run's reward stays sealed");
                Assert.AreEqual(PuzzlePlacementState.Spawning, reward.State);
                StringAssert.Contains(reply, "Cleared 1 ", "the count names only what /puzzlegate manages");
            }, admin, reward);
        }

        [TestMethod]
        public void Clear_by_id_refuses_a_run_placement_with_a_plain_line()
        {
            var run = ThreadLootTestFixtures.PooledRun();
            var reward = Placement(new ThreadPuzzleRunHost(run, true));
            Assert.IsTrue(run.SealReward(reward));

            WithTracked(() =>
            {
                var reply = PuzzleGateManager.Clear(null, reward.Id.ToString());

                StringAssert.Contains(reply, "belongs to a Thread run");
                Assert.IsTrue(PuzzleGateManager.IsRegistered(reward));
                Assert.IsTrue(run.IsRewardSealed);
            }, reward);
        }

        [TestMethod]
        public void List_shows_only_admin_placements()
        {
            var admin = Placement(new CountingHost(0));
            var runPlacement = Placement(new CountingHost(0x80005555u));

            WithTracked(() =>
            {
                var lines = string.Join("\n", PuzzleGateManager.List());

                StringAssert.Contains(lines, $"#{admin.Id} ");
                Assert.IsFalse(lines.Contains($"#{runPlacement.Id} "), "a run placement is not listed");
            }, admin, runPlacement);
        }

        [TestMethod]
        public void Every_admin_lookup_goes_through_the_admin_filter()
        {
            var src = PooledLootSourceText.Read(ManagerPath);
            var tryFind = PooledLootSourceText.MethodBody(src, "private static bool TryFind(Player admin, int? id, out PuzzleGatePlacement placement, out string error)");

            StringAssert.Contains(tryFind, "if (IsAdminManaged(placement))", "by id: a run placement is refused");
            StringAssert.Contains(tryFind, "foreach (var p in AdminPlacements())", "nearest: only admin placements are candidates");
            Assert.IsFalse(tryFind.Contains("Placements.Values"), "no unfiltered scan in the lookup");

            var reap = PooledLootSourceText.MethodBody(src, "private static int Reap(DateTime now)");
            StringAssert.Contains(reap, "AdminPlacements()", "the command-path reap never reaps (and so never unseals) a run");
        }

        // ---- F5: run-scoped clear and reap, exactly-once notification --------------------------------------

        [TestMethod]
        public void ClearForRun_clears_only_that_run_and_notifies_each_host_once()
        {
            var mine = new CountingHost(0x80006001u);
            var other = new CountingHost(0x80006002u);
            var admin = new CountingHost(0);
            var a = Placement(mine);
            var b = Placement(mine);
            var c = Placement(other);
            var d = Placement(admin);

            WithTracked(() =>
            {
                Assert.AreEqual(2, PuzzleGateManager.ClearForRun(0x80006001u));
                Assert.AreEqual(0, PuzzleGateManager.ClearForRun(0x80006001u), "a second call finds nothing");
                Assert.AreEqual(0, PuzzleGateManager.ClearForRun(0), "run id 0 is never a run");

                Assert.IsFalse(PuzzleGateManager.IsRegistered(a));
                Assert.IsFalse(PuzzleGateManager.IsRegistered(b));
                Assert.IsTrue(PuzzleGateManager.IsRegistered(c), "another run's placement stays");
                Assert.IsTrue(PuzzleGateManager.IsRegistered(d), "an admin placement stays");

                CollectionAssert.AreEqual(new[] { (PuzzleRemovalReason.Cleared, false), (PuzzleRemovalReason.Cleared, false) }, mine.Removed);
                Assert.AreEqual(0, other.Removed.Count);
                Assert.AreEqual(0, admin.Removed.Count);
            }, a, b, c, d);
        }

        [TestMethod]
        public void ReapForRun_reaps_a_spawn_that_never_ran_and_leaves_a_fresh_one()
        {
            var host = new CountingHost(0x80006003u);
            var t0 = DateTime.UtcNow;
            var stale = Placement(host, t0 - TimeSpan.FromSeconds(PuzzleGateTunables.SpawnTimeoutSeconds + 5));
            var fresh = Placement(host, t0);

            WithTracked(() =>
            {
                Assert.AreEqual(1, PuzzleGateManager.ReapForRun(0x80006003u, t0));
                Assert.IsFalse(PuzzleGateManager.IsRegistered(stale));
                Assert.IsTrue(PuzzleGateManager.IsRegistered(fresh));
                CollectionAssert.AreEqual(new[] { (PuzzleRemovalReason.Reaped, false) }, host.Removed);

                Assert.AreEqual(0, PuzzleGateManager.ReapForRun(0x80006003u, t0), "nothing else is dead");
                Assert.AreEqual(1, host.Removed.Count, "notified once");
            }, stale, fresh);
        }

        [TestMethod]
        public void The_reshuffle_failure_closure_reports_then_notifies_once()
        {
            var host = new CountingHost(0x80006004u);
            var p = Placement(host);

            var followUp = PuzzleGateManager.ReshuffleFailedForTest(p, "round 2 failed", false);
            Assert.AreEqual(0, host.Removed.Count, "nothing runs until the closure is invoked (after p.Sync is released)");

            followUp();
            followUp();

            CollectionAssert.AreEqual(new[] { "round 2 failed", "round 2 failed" }, host.Reports);
            CollectionAssert.AreEqual(new[] { (PuzzleRemovalReason.ReshuffleFailed, false) }, host.Removed, "OnRemoved exactly once (TryClaimRemovalNotice)");

            // And a later clear of the same placement cannot notify a second time.
            WithTracked(() => PuzzleGateManager.ClearForRun(0x80006004u), p);
            Assert.AreEqual(1, host.Removed.Count);
        }

        [TestMethod]
        public void A_scored_wrong_is_reported_only_from_the_Wrong_branch_and_an_unscored_one_only_from_Refused()
        {
            var src = PooledLootSourceText.Read(ManagerPath);
            var handle = PooledLootSourceText.MethodBody(src, "private static void HandleActivation(PuzzleGatePlacement p, WorldObject lever, Player player)");

            const string scored = "p.Host.OnWrong(p, player, true)";
            const string unscored = "p.Host.OnWrong(p, player, false)";

            Assert.AreEqual(1, Count(handle, scored), "one scored call");
            Assert.AreEqual(1, Count(handle, unscored), "one unscored call");
            Assert.AreEqual(1, Count(src, scored), "and nowhere else in the manager");
            Assert.AreEqual(1, Count(src, unscored));

            int Case(string name) => handle.IndexOf("case PuzzleActivation." + name + ":", StringComparison.Ordinal);

            var refused = Case("Refused");
            var alreadySolved = Case("AlreadySolved");
            var wrong = Case("Wrong");
            Assert.IsTrue(refused >= 0 && alreadySolved > refused && wrong > alreadySolved, "case layout as expected");

            var s = handle.IndexOf(scored, StringComparison.Ordinal);
            var u = handle.IndexOf(unscored, StringComparison.Ordinal);

            Assert.IsTrue(s > wrong, "the scored call sits in the Wrong case (the last case)");
            Assert.IsTrue(u > refused && u < alreadySolved, "the unscored call sits in the Refused case");
        }

        private static int Count(string s, string needle)
        {
            var n = 0;

            for (var i = s.IndexOf(needle, StringComparison.Ordinal); i >= 0; i = s.IndexOf(needle, i + needle.Length, StringComparison.Ordinal))
                n++;

            return n;
        }

        // ---- F5: shipped gate wcids are Door weenies ------------------------------------------------------

        [TestMethod]
        public void Every_gate_wcid_in_the_shipped_site_file_is_a_Door_weenie_in_the_repo()
        {
            var json = PooledLootSourceText.Read("Content/dungeons/dynamic/puzzle-gates.json");
            var wcids = new SortedSet<uint>();

            using (var doc = JsonDocument.Parse(json))
            {
                foreach (var dungeon in doc.RootElement.GetProperty("dungeons").EnumerateObject())
                {
                    foreach (var site in dungeon.Value.GetProperty("sites").EnumerateArray())
                    {
                        // A reward site's gateModel is an ignored placeholder (the loader clears it); only gates spawn one.
                        if (site.TryGetProperty("kind", out var siteKind) && siteKind.GetString() == "reward")
                            continue;

                        if (!site.TryGetProperty("gateModel", out var model) || model.ValueKind != JsonValueKind.Object)
                            continue;

                        var kind = model.TryGetProperty("kind", out var k) ? k.GetString() : null;

                        if (kind == "resident")
                            continue;

                        wcids.Add(model.GetProperty("wcid").GetUInt32());
                    }
                }
            }

            Assert.IsTrue(wcids.Count > 0, "the shipped file names gate wcids");

            var weenieDir = FindRepoDir("Content/sql/weenies");
            var insert = new Regex(@"INSERT INTO `weenie`[^;]*?VALUES \((\d+), '[^']*', (\d+),", RegexOptions.Singleline);

            foreach (var wcid in wcids)
            {
                var files = Directory.GetFiles(weenieDir, wcid + " *.sql");
                Assert.AreEqual(1, files.Length, $"wcid {wcid}: expected exactly one Content/sql/weenies/{wcid} *.sql");

                var m = insert.Match(File.ReadAllText(files[0]));
                Assert.IsTrue(m.Success, $"wcid {wcid}: no weenie INSERT");
                Assert.AreEqual(wcid.ToString(), m.Groups[1].Value);
                Assert.AreEqual("19", m.Groups[2].Value, $"wcid {wcid} must be WeenieType 19 (Door): PrepareGate refuses anything else");
            }
        }

        private static string FindRepoDir(string relative)
        {
            for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
            {
                var candidate = Path.Combine(dir.FullName, relative);

                if (Directory.Exists(candidate))
                    return candidate;
            }

            Assert.Fail($"Could not find {relative} by walking up from {AppContext.BaseDirectory}");
            return null;
        }
    }
}
