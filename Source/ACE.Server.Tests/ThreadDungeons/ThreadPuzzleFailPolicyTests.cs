using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text.RegularExpressions;

using ACE.Server.Managers;
using ACE.Server.PuzzleGates;
using ACE.Server.ThreadDungeons;
using ACE.Server.ThreadDungeons.Defs;
using ACE.Server.WorldObjects;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ACE.Server.Tests.ThreadDungeons
{
    /// <summary>
    /// The IP-wide Thread puzzle fail policy: the key (ThreadPuzzleIpKey), the pure decision (PuzzleFailPolicy), the
    /// clamped settings snapshot, recording against a memory-only ledger, the sweep plan and its execution through
    /// seams, the run's removed set, and the end-state mapping. No PropertyManager read and no Player: every live
    /// edge is a swapped seam, restored in cleanup.
    /// </summary>
    [TestClass]
    public class ThreadPuzzleFailPolicyTests
    {
        private static readonly DateTime Now = new DateTime(2026, 10, 6, 12, 0, 0, DateTimeKind.Utc);
        private static readonly IPAddress SharedIp = IPAddress.Parse("203.0.113.7");

        private Action<ThreadDungeonRun, string> savedEndRun;
        private Action<uint, string> savedTell;
        private Action<ThreadDungeonRun, uint, string> savedEject;
        private Func<string, ThreadPuzzlePolicyConfig, IReadOnlyList<LockoutSweepCandidate>> savedCandidates;
        private Func<ThreadDungeonRun, IReadOnlyList<uint>> savedInside;
        private ThreadPuzzlePolicyConfig savedConfig;
        private IThreadPuzzleFailSink savedSink;

        [TestInitialize]
        public void Setup()
        {
            savedEndRun = ThreadPuzzleFailPolicy.EndRunAction;
            savedTell = ThreadPuzzleFailPolicy.TellAction;
            savedEject = ThreadPuzzleFailPolicy.EjectAction;
            savedCandidates = ThreadPuzzleFailPolicy.CandidateSource;
            savedInside = ThreadPuzzleFailPolicy.OnlineInsideSource;
            savedConfig = ThreadPuzzlePolicyConfig.Current;
            savedSink = ThreadPuzzleRunHost.FailSink;
            ThreadPuzzleFailPolicy.ClearPendingSweepsForTest();
        }

        [TestCleanup]
        public void Cleanup()
        {
            ThreadPuzzleFailPolicy.EndRunAction = savedEndRun;
            ThreadPuzzleFailPolicy.TellAction = savedTell;
            ThreadPuzzleFailPolicy.EjectAction = savedEject;
            ThreadPuzzleFailPolicy.CandidateSource = savedCandidates;
            ThreadPuzzleFailPolicy.OnlineInsideSource = savedInside;
            ThreadPuzzlePolicyConfig.Current = savedConfig;
            ThreadPuzzleRunHost.FailSink = savedSink;
            ThreadPuzzleFailPolicy.ClearPendingSweepsForTest();
        }

        private static ThreadPuzzlePolicyConfig Config(bool enabled = true, long threshold = 3, long window = 60, long lockout = 120)
            => ThreadPuzzlePolicyConfig.Create(enabled, threshold, window, lockout);

        private static ThreadPuzzleIpLedger MemoryLedger() => new ThreadPuzzleIpLedger(null, ThreadPuzzleIpLedger.DefaultRetention);

        private static PuzzleActor Actor(uint guid, uint account) => new PuzzleActor(guid, $"P{guid:X}", account, SharedIp.ToString());

        private static ThreadDungeonRun SoloRun(uint runId, uint owner)
        {
            var spec = new DungeonGemSpec("filos_doom", 200, 6, "any", 1, new (string, double)[0], 0, 0);
            var dungeon = new DungeonEntryDef { Id = "filos_doom", Landblock = 0x0150, ExitPortalWcid = 1003601 };
            return new ThreadDungeonRun(runId, owner, $"P{owner:X}", 7, 0x80000099u, spec, dungeon, DateTime.UtcNow, TimeSpan.FromMinutes(180));
        }

        private static ThreadDungeonRun GroupRun(uint runId, uint owner, params uint[] members)
        {
            var seats = new List<RosterSeat> { new RosterSeat(owner, "Owner", 7, 150) };
            seats.AddRange(members.Select(g => new RosterSeat(g, $"P{g:X}", 8, 150)));
            var n = ThreadDungeonRun.OrderRoster(seats).Count;
            var group = GroupScaling.Compute(n, GroupScaling.DefaultEffortPerMember, GroupScaling.DefaultCountShare, GroupScaling.DefaultGroupMaxMonsters,
                GroupScaling.DefaultDamageRatingPerMember, GroupScaling.DefaultDamageRatingCap, GroupScaling.DefaultRewardBonusPerMember,
                GroupScaling.DefaultRewardBonusCap, 2.7);
            var spec = new DungeonGemSpec("filos_doom", 200, 6, "any", 1, new (string, double)[0], 0, 0);
            var dungeon = new DungeonEntryDef { Id = "filos_doom", Landblock = 0x0150, ExitPortalWcid = 1003601 };
            return new ThreadDungeonRun(runId, seats, 0x80000099u, spec, dungeon, DateTime.UtcNow, TimeSpan.FromMinutes(180), group);
        }

        // ---- the key ----------------------------------------------------------------------------------------

        [TestMethod]
        public void Key_is_the_connection_so_two_accounts_on_one_address_share_it()
        {
            Assert.AreEqual("203.0.113.7", ThreadPuzzleIpKey.For(SharedIp, false, 1));
            Assert.AreEqual(ThreadPuzzleIpKey.For(SharedIp, false, 1), ThreadPuzzleIpKey.For(SharedIp, false, 2));
            Assert.AreEqual("203.0.113.7", ThreadPuzzleIpKey.For(IPAddress.Parse("::ffff:203.0.113.7"), false, 3), "IPv4-mapped collapses to IPv4");
            Assert.AreEqual("127.0.0.1", ThreadPuzzleIpKey.For(IPAddress.Loopback, false, 1), "loopback is an ordinary key");
        }

        [TestMethod]
        public void A_household_connection_is_keyed_per_account()
        {
            Assert.AreEqual("acct:1", ThreadPuzzleIpKey.For(SharedIp, true, 1));
            Assert.AreEqual("acct:2", ThreadPuzzleIpKey.For(SharedIp, true, 2));
            Assert.IsNull(ThreadPuzzleIpKey.For(SharedIp, true, 0), "a household with no account id is not counted");
        }

        [TestMethod]
        public void IPv6_is_keyed_by_its_64_prefix()
        {
            var a = ThreadPuzzleIpKey.For(IPAddress.Parse("2001:db8:1:2:aaaa::1"), false, 1);
            var b = ThreadPuzzleIpKey.For(IPAddress.Parse("2001:DB8:1:2:bbbb:cccc:dddd:9%4"), false, 2);
            var other = ThreadPuzzleIpKey.For(IPAddress.Parse("2001:db8:1:3::1"), false, 1);

            Assert.AreEqual("2001:db8:1:2::/64", a);
            Assert.AreEqual(a, b, "same /64, scope id dropped");
            Assert.AreNotEqual(a, other);
            Assert.IsTrue("ffff:ffff:ffff:ffff::/64".Length <= 45, "the longest IPv6 key fits the ledger's 45-character key");
        }

        [TestMethod]
        public void No_address_or_no_session_means_no_key()
        {
            Assert.IsNull(ThreadPuzzleIpKey.For((IPAddress)null, false, 1));
            Assert.IsNull(ThreadPuzzleIpKey.For((IPAddress)null, true, 1));
            Assert.IsNull(ThreadPuzzleIpKey.For((ACE.Server.Network.Session)null, IpLimitManager.HouseholdLists.Empty));
        }

        [TestMethod]
        public void Household_test_matches_account_names_case_insensitively_and_addresses_exactly()
        {
            var lists = new IpLimitManager.HouseholdLists(new[] { "FamilyAcct" }, new[] { "203.0.113.7" });

            Assert.IsTrue(IpLimitManager.IsHouseholdExemptCore("familyacct", IPAddress.Parse("198.51.100.1"), lists));
            Assert.IsTrue(IpLimitManager.IsHouseholdExemptCore("someone", SharedIp, lists));
            Assert.IsFalse(IpLimitManager.IsHouseholdExemptCore("someone", IPAddress.Parse("198.51.100.1"), lists));
            Assert.IsFalse(IpLimitManager.IsHouseholdExemptCore("someone", IPAddress.Loopback, lists), "loopback is not a household");
            Assert.IsFalse(IpLimitManager.IsHouseholdExemptCore("FamilyAcct", SharedIp, null));
        }

        /// <summary>
        /// The ONE-key-source invariant: ThreadPuzzleIpKey is the only new caller of NormalizeIpKey, and no new
        /// caller of IpLimitManager.IsExempt(session) appeared anywhere in ACE.Server. Pinned as exact per-file
        /// counts over non-comment lines, so a new call site anywhere fails here.
        /// </summary>
        [TestMethod]
        public void NormalizeIpKey_and_IsExempt_have_no_new_call_sites()
        {
            var normalize = CallSites((file, line) => Regex.IsMatch(line, @"\bNormalizeIpKey\(") && !line.Contains("static string NormalizeIpKey"));

            // Qualified calls anywhere, plus IpLimitManager's own unqualified calls. LeaderboardExemptionManager.IsExempt
            // and RewardClaimService's request.IsExempt delegate are different members and are not matched.
            var exempt = CallSites((file, line) =>
                !line.Contains("static bool IsExempt")
                && (Regex.IsMatch(line, @"\bIpLimitManager\.IsExempt\(")
                    || (Path.GetFileName(file) == "IpLimitManager.cs" && Regex.IsMatch(line, @"(?<![\w.])IsExempt\("))));

            CollectionAssert.AreEquivalent(
                new[] { "GroupRosterRules.cs=1", "RewardClaimCommands.cs=1", "RewardClaimService.cs=1", "ThreadPuzzleIpKey.cs=1" },
                normalize, string.Join(", ", normalize));

            CollectionAssert.AreEquivalent(
                new[] { "CharacterHandler.cs=1", "EmoteManager.cs=1", "GroupRosterRules.cs=1", "IpLimitCommands.cs=1", "IpLimitManager.cs=2" },
                exempt, string.Join(", ", exempt));
        }

        private static List<string> CallSites(Func<string, string, bool> isCallSite)
        {
            var root = FindServerSource();
            var result = new List<string>();

            foreach (var file in Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories))
            {
                if (file.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar) || file.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar))
                    continue;

                var count = File.ReadLines(file)
                    .Select(l => l.Trim())
                    .Where(l => !l.StartsWith("//") && !l.StartsWith("*"))
                    .Count(l => isCallSite(file, l));

                if (count > 0)
                    result.Add($"{Path.GetFileName(file)}={count}");
            }

            return result;
        }

        private static string FindServerSource()
        {
            for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
            {
                var candidate = Path.Combine(dir.FullName, "Source", "ACE.Server");
                if (Directory.Exists(candidate))
                    return candidate;

                candidate = Path.Combine(dir.FullName, "ACE.Server");
                if (Directory.Exists(Path.Combine(candidate, "ThreadDungeons")))
                    return candidate;
            }

            Assert.Fail($"Could not find Source/ACE.Server by walking up from {AppContext.BaseDirectory}");
            return null;
        }

        // ---- the pure decision and the settings ------------------------------------------------------------

        [TestMethod]
        public void Decide_counts_down_then_triggers_on_reaching_the_threshold()
        {
            var config = Config();

            var first = PuzzleFailPolicy.Decide(new[] { Now }, Now, config);
            Assert.IsFalse(first.Trigger);
            Assert.AreEqual(2, first.Remaining);

            var second = PuzzleFailPolicy.Decide(new[] { Now.AddMinutes(-5), Now }, Now, config);
            Assert.IsFalse(second.Trigger);
            Assert.AreEqual(1, second.Remaining);

            var third = PuzzleFailPolicy.Decide(new[] { Now.AddMinutes(-9), Now.AddMinutes(-5), Now }, Now, config);
            Assert.IsTrue(third.Trigger, "reaching the threshold triggers, not exceeding it");
            Assert.AreEqual(Now.AddMinutes(120), third.LockoutUntil);
            Assert.AreEqual(0, third.Remaining);
        }

        [TestMethod]
        public void Decide_prunes_fails_outside_the_window()
        {
            var config = Config(window: 60);
            var fails = new[] { Now.AddMinutes(-61), Now.AddMinutes(-60), Now.AddMinutes(-1), Now };

            var decision = PuzzleFailPolicy.Decide(fails, Now, config);

            Assert.AreEqual(2, decision.Count, "the fail exactly a window old is out, as is anything older");
            Assert.IsFalse(decision.Trigger);
            Assert.AreEqual(1, decision.Remaining);
        }

        [TestMethod]
        public void Threshold_one_triggers_on_the_first_fail()
        {
            var decision = PuzzleFailPolicy.Decide(new[] { Now }, Now, Config(threshold: 1));
            Assert.IsTrue(decision.Trigger);
        }

        [TestMethod]
        public void Settings_are_clamped()
        {
            var low = ThreadPuzzlePolicyConfig.Create(true, 0, 0, -5);
            Assert.AreEqual(1, low.Threshold);
            Assert.AreEqual(TimeSpan.FromMinutes(1), low.Window);
            Assert.AreEqual(TimeSpan.FromMinutes(1), low.Lockout);

            var high = ThreadPuzzlePolicyConfig.Create(true, long.MaxValue, 999999, long.MaxValue);
            Assert.AreEqual(int.MaxValue, high.Threshold);
            Assert.AreEqual(ThreadPuzzleIpLedger.DefaultRetention, high.Window, "the window never exceeds what the ledger keeps");
            Assert.AreEqual(TimeSpan.FromMinutes(ThreadPuzzlePolicyConfig.MaxLockoutMinutes), high.Lockout);

            var defaults = ThreadPuzzlePolicyConfig.Defaults();
            Assert.IsTrue(defaults.Enabled);
            Assert.AreEqual(3, defaults.Threshold);
            Assert.AreEqual(TimeSpan.FromMinutes(60), defaults.Window);
            Assert.AreEqual(TimeSpan.FromMinutes(120), defaults.Lockout);
        }

        [TestMethod]
        public void Player_lines_are_the_approved_text()
        {
            Assert.AreEqual("The ward recoils. 2 more failed attempts and you will be cast out of the Threads.", PuzzleGateText.FailWarning(2));
            Assert.AreEqual("The ward recoils. 1 more failed attempt and you will be cast out of the Threads.", PuzzleGateText.FailWarning(1));
            Assert.AreEqual("The ward rejects you. You are barred from the Threads for 2h.", PuzzleGateText.LockedOut(TimeSpan.FromMinutes(120)));
            Assert.AreEqual("You are barred from the Threads for another 1h 54m.", PuzzleGateText.Barred(TimeSpan.FromMinutes(114)));

            Assert.AreEqual("1h 55m", PuzzleGateText.FormatDuration(TimeSpan.FromMinutes(114) + TimeSpan.FromSeconds(1)), "rounded up");
            Assert.AreEqual("45m", PuzzleGateText.FormatDuration(TimeSpan.FromMinutes(45)));
            Assert.AreEqual("1m", PuzzleGateText.FormatDuration(TimeSpan.FromSeconds(10)));
            Assert.AreEqual("1m", PuzzleGateText.FormatDuration(TimeSpan.Zero), "never reads as over");
        }

        // ---- recording and enforcement -----------------------------------------------------------------------

        [TestMethod]
        public void A_lockout_from_one_account_refuses_another_account_on_the_same_address()
        {
            var config = Config();
            var ledger = MemoryLedger();
            var keyA = ThreadPuzzleIpKey.For(SharedIp, false, 1);
            var keyB = ThreadPuzzleIpKey.For(SharedIp, false, 2);
            var elsewhere = ThreadPuzzleIpKey.For(IPAddress.Parse("198.51.100.1"), false, 2);

            var o1 = ThreadPuzzleFailPolicy.RecordScoredWrong(keyA, Actor(0x50000001u, 1), null, Now, config, ledger);
            var o2 = ThreadPuzzleFailPolicy.RecordScoredWrong(keyA, Actor(0x50000001u, 1), null, Now.AddSeconds(10), config, ledger);
            var o3 = ThreadPuzzleFailPolicy.RecordScoredWrong(keyA, Actor(0x50000001u, 1), null, Now.AddSeconds(20), config, ledger);

            Assert.AreEqual(PuzzleFailOutcomeKind.Warned, o1.Kind);
            Assert.AreEqual(2, o1.Decision.Remaining);
            Assert.AreEqual(PuzzleFailOutcomeKind.Warned, o2.Kind);
            Assert.AreEqual(1, o2.Decision.Remaining);
            Assert.AreEqual(PuzzleFailOutcomeKind.LockedOut, o3.Kind);
            Assert.AreEqual(1, ThreadPuzzleFailPolicy.PendingSweepCount, "a lockout queues one sweep");

            var later = Now.AddMinutes(6);
            var remaining = ThreadPuzzleFailPolicy.LockoutRemaining(keyB, later, config, ledger);
            Assert.IsTrue(remaining.HasValue, "the second account on the address is barred");
            Assert.AreEqual(TimeSpan.FromMinutes(114) + TimeSpan.FromSeconds(20), remaining.Value);
            Assert.AreEqual("You are barred from the Threads for another 1h 55m.", PuzzleGateText.Barred(remaining.Value));

            Assert.IsNull(ThreadPuzzleFailPolicy.LockoutRemaining(elsewhere, later, config, ledger), "another address is not");
            Assert.IsNull(ThreadPuzzleFailPolicy.LockoutRemaining(keyB, Now.AddMinutes(121), config, ledger), "the lockout runs out");
        }

        [TestMethod]
        public void A_household_lockout_bars_only_its_own_account()
        {
            var config = Config(threshold: 1);
            var ledger = MemoryLedger();
            var key1 = ThreadPuzzleIpKey.For(SharedIp, true, 1);
            var key2 = ThreadPuzzleIpKey.For(SharedIp, true, 2);

            Assert.AreEqual(PuzzleFailOutcomeKind.LockedOut, ThreadPuzzleFailPolicy.RecordScoredWrong(key1, Actor(0x50000001u, 1), null, Now, config, ledger).Kind);

            Assert.IsTrue(ThreadPuzzleFailPolicy.LockoutRemaining(key1, Now, config, ledger).HasValue);
            Assert.IsNull(ThreadPuzzleFailPolicy.LockoutRemaining(key2, Now, config, ledger), "the other household account plays on");
        }

        [TestMethod]
        public void Disabled_records_nothing_and_enforces_nothing()
        {
            var ledger = MemoryLedger();
            var key = ThreadPuzzleIpKey.For(SharedIp, false, 1);

            var outcome = ThreadPuzzleFailPolicy.RecordScoredWrong(key, Actor(0x50000001u, 1), null, Now, Config(enabled: false, threshold: 1), ledger);

            Assert.AreEqual(PuzzleFailOutcomeKind.Ignored, outcome.Kind);
            Assert.AreEqual(0, ledger.GetFails(key, Now, TimeSpan.FromHours(1)).Count, "nothing recorded");
            Assert.AreEqual(0, ThreadPuzzleFailPolicy.PendingSweepCount);

            // A lockout already in the ledger is not enforced while the switch is off (the escape hatch).
            ledger.ApplyLockout(key, Now, Now.AddHours(2));
            Assert.IsNull(ThreadPuzzleFailPolicy.LockoutRemaining(key, Now, Config(enabled: false), ledger));
            Assert.IsTrue(ThreadPuzzleFailPolicy.LockoutRemaining(key, Now, Config(enabled: true), ledger).HasValue);
        }

        [TestMethod]
        public void A_pull_while_locked_is_not_counted_and_queues_no_second_sweep()
        {
            var config = Config(threshold: 1);
            var ledger = MemoryLedger();
            var key = ThreadPuzzleIpKey.For(SharedIp, false, 1);

            ThreadPuzzleFailPolicy.RecordScoredWrong(key, Actor(0x50000001u, 1), null, Now, config, ledger);
            var again = ThreadPuzzleFailPolicy.RecordScoredWrong(key, Actor(0x50000002u, 2), null, Now.AddSeconds(1), config, ledger);

            Assert.AreEqual(PuzzleFailOutcomeKind.AlreadyLocked, again.Kind);
            Assert.AreEqual(0, ledger.GetFails(key, Now.AddSeconds(1), TimeSpan.FromHours(1)).Count, "the lockout consumed the one fail; the racing pull added none");
            Assert.AreEqual(1, ThreadPuzzleFailPolicy.PendingSweepCount, "the lockout's own sweep scans the whole key");
        }

        [TestMethod]
        public void No_key_is_ignored()
        {
            var outcome = ThreadPuzzleFailPolicy.RecordScoredWrong(null, Actor(0x50000001u, 1), null, Now, Config(threshold: 1), MemoryLedger());
            Assert.AreEqual(PuzzleFailOutcomeKind.Ignored, outcome.Kind);
            Assert.IsNull(ThreadPuzzleFailPolicy.LockoutRemaining(null, Now, Config(), MemoryLedger()));
        }

        /// <summary>Refused (unscored) pulls never reach the policy; scored ones do. And production installs the policy sink.</summary>
        [TestMethod]
        public void Only_scored_pulls_reach_the_fail_sink()
        {
            Assert.AreSame(ThreadPuzzleFailPolicySink.Instance, savedSink, "production default is the policy sink");

            var counting = new CountingSink();
            ThreadPuzzleRunHost.FailSink = counting;

            var host = new ThreadPuzzleRunHost(ThreadLootTestFixtures.NewRun(), isReward: false);

            host.OnWrong(null, null, scored: false);
            Assert.AreEqual(0, counting.Calls, "a refused or unscored pull is never a fail");

            host.OnWrong(null, null, scored: true);
            Assert.AreEqual(1, counting.Calls);

            // The production sink with no player records nothing (no key) and does not throw.
            ThreadPuzzleRunHost.FailSink = ThreadPuzzleFailPolicySink.Instance;
            host.OnWrong(null, null, scored: true);
            Assert.AreEqual(0, ThreadPuzzleFailPolicy.PendingSweepCount);
        }

        private sealed class CountingSink : IThreadPuzzleFailSink
        {
            public int Calls;

            public void OnScoredWrong(ThreadDungeonRun run, Player player, PuzzleGatePlacement placement) => Calls++;
        }

        // ---- the sweep -----------------------------------------------------------------------------------------

        private sealed class SweepRecorder
        {
            public readonly List<(ThreadDungeonRun Run, string Why)> Ended = new List<(ThreadDungeonRun, string)>();
            public readonly List<(uint Guid, string Text)> Told = new List<(uint, string)>();
            public readonly List<(ThreadDungeonRun Run, uint Guid, string Text)> Ejected = new List<(ThreadDungeonRun, uint, string)>();

            public void Install()
            {
                ThreadPuzzleFailPolicy.EndRunAction = (r, why) => Ended.Add((r, why));
                ThreadPuzzleFailPolicy.TellAction = (g, t) => Told.Add((g, t));
                ThreadPuzzleFailPolicy.EjectAction = (r, g, t) => Ejected.Add((r, g, t));
            }
        }

        [TestMethod]
        public void The_sweep_removes_a_second_character_on_the_key_from_another_run()
        {
            var runA = SoloRun(0x80000a01u, 0x50000a01u);
            var runB = SoloRun(0x80000b01u, 0x50000b01u);
            var bystander = 0x50000c01u;
            var until = Now.AddHours(2);

            var request = new LockoutSweepRequest("203.0.113.7", until, runA, runA.OwnerGuid);
            var candidates = new[]
            {
                new LockoutSweepCandidate(runA.OwnerGuid, "Puller", runA, inside: true),
                new LockoutSweepCandidate(runB.OwnerGuid, "Alt", runB, inside: true),
                new LockoutSweepCandidate(bystander, "InTown", null, inside: false),
            };

            var plan = ThreadPuzzleFailPolicy.PlanSweep(request, candidates, _ => Array.Empty<uint>());

            Assert.AreEqual(2, plan.Count);
            Assert.IsTrue(plan.All(a => a.EndRun), "solo runs end");

            var rec = new SweepRecorder();
            rec.Install();
            ThreadPuzzleFailPolicy.Execute(plan, until, Now);

            CollectionAssert.AreEquivalent(new[] { runA, runB }, rec.Ended.Select(e => e.Run).ToList());
            Assert.IsTrue(rec.Ended.All(e => e.Why == "puzzle lockout"));
            CollectionAssert.AreEquivalent(new[] { runA.OwnerGuid, runB.OwnerGuid }, rec.Told.Select(t => t.Guid).ToList(), "the bystander outside any run is not told");
            Assert.IsTrue(rec.Told.All(t => t.Text == "The ward rejects you. You are barred from the Threads for 2h."));
            Assert.IsTrue(runB.IsPuzzleRemoved(runB.OwnerGuid));
        }

        [TestMethod]
        public void The_puller_leaves_the_failing_run_even_if_already_outside()
        {
            var run = SoloRun(0x80000a02u, 0x50000a02u);
            var request = new LockoutSweepRequest("k", Now.AddHours(1), run, run.OwnerGuid);

            var plan = ThreadPuzzleFailPolicy.PlanSweep(request, new[] { new LockoutSweepCandidate(run.OwnerGuid, "Puller", run, inside: false) }, _ => Array.Empty<uint>());

            Assert.AreEqual(1, plan.Count);
            Assert.IsTrue(plan[0].EndRun);
        }

        [TestMethod]
        public void A_group_member_is_removed_and_the_group_continues_without_them()
        {
            const uint owner = 0x50000d01u, puller = 0x50000d02u, friend = 0x50000d03u;
            var run = GroupRun(0x80000d01u, owner, puller, friend);
            run.SetMemberKey(puller, 0x80000d02u);
            run.SetMemberKey(friend, 0x80000d03u);
            run.MarkMemberEntered(puller);
            run.MarkMemberEntered(friend);

            var request = new LockoutSweepRequest("k", Now.AddHours(2), run, puller);
            var plan = ThreadPuzzleFailPolicy.PlanSweep(request,
                new[] { new LockoutSweepCandidate(puller, "Puller", run, inside: true) },
                _ => new[] { owner, puller, friend });

            Assert.AreEqual(1, plan.Count);
            Assert.IsFalse(plan[0].EndRun, "others are still inside");
            CollectionAssert.AreEqual(new[] { puller }, plan[0].Guids.ToList());

            var rec = new SweepRecorder();
            rec.Install();
            ThreadPuzzleFailPolicy.Execute(plan, Now.AddHours(2), Now);

            Assert.AreEqual(0, rec.Ended.Count, "the run continues");
            Assert.AreEqual(1, rec.Ejected.Count);
            Assert.AreEqual(puller, rec.Ejected[0].Guid);
            Assert.AreEqual("The ward rejects you. You are barred from the Threads for 2h.", rec.Ejected[0].Text);

            // Excluded from re-entry (the gem handler's ReEnter check), the loot deal, and the clear's survey and count.
            Assert.IsTrue(run.IsPuzzleRemoved(puller));
            Assert.IsFalse(run.IsPuzzleRemoved(friend));
            CollectionAssert.AreEqual(new[] { owner, friend }, run.DealSeats().ToList());

            var snapshot = run.SnapshotRoster();
            Assert.IsTrue(snapshot.Single(m => m.Guid == puller).PuzzleRemoved);
            CollectionAssert.AreEqual(new[] { owner, friend }, ThreadDungeonManager.SurveyRecipients(snapshot, _ => true).ToList());
            CollectionAssert.AreEqual(new[] { owner, friend }, ThreadGuideFlow.ClearCountRecipients(snapshot, _ => true, 5).ToList());
        }

        /// <summary>
        /// Coordinator ruling 2026-10-06: a same-key roster member standing OUTSIDE a run is removed from it too - no
        /// survey, clear count, guide credit, deal seat or re-entry - and a group run that only loses outside members
        /// carries on, even when nobody is inside it right now.
        /// </summary>
        [TestMethod]
        public void A_same_key_member_outside_a_group_run_is_removed_and_the_run_continues()
        {
            const uint owner = 0x50001101u, alt = 0x50001102u;
            var failing = SoloRun(0x80001100u, 0x50001100u);
            var group = GroupRun(0x80001101u, owner, alt);
            group.SetMemberKey(alt, 0x80001102u);
            group.MarkMemberEntered(alt);

            var plan = ThreadPuzzleFailPolicy.PlanSweep(new LockoutSweepRequest("k", Now.AddHours(2), failing, failing.OwnerGuid),
                new[]
                {
                    new LockoutSweepCandidate(failing.OwnerGuid, "Puller", failing, inside: true),
                    new LockoutSweepCandidate(alt, "Alt", group, inside: false),
                },
                _ => Array.Empty<uint>());

            var groupAction = plan.Single(a => a.Run == group);
            Assert.IsFalse(groupAction.EndRun, "only an outside member left; nobody inside is no reason to end it");
            CollectionAssert.AreEqual(new[] { alt }, groupAction.Guids.ToList());

            var rec = new SweepRecorder();
            rec.Install();
            ThreadPuzzleFailPolicy.Execute(plan, Now.AddHours(2), Now);

            CollectionAssert.AreEqual(new[] { failing }, rec.Ended.Select(e => e.Run).ToList());
            Assert.IsTrue(group.IsPuzzleRemoved(alt));
            CollectionAssert.AreEqual(new[] { owner }, group.DealSeats().ToList());
            CollectionAssert.AreEqual(new[] { owner }, ThreadDungeonManager.SurveyRecipients(group.SnapshotRoster(), _ => true).ToList());
            Assert.AreEqual(alt, rec.Ejected.Single().Guid, "told (and teleported only if standing in the copy)");
        }

        [TestMethod]
        public void A_character_leaving_two_runs_is_told_once()
        {
            const uint owner = 0x50001201u, alt = 0x50001202u;
            var groupA = GroupRun(0x80001201u, owner, alt);
            var groupB = GroupRun(0x80001202u, 0x50001203u, alt);

            var plan = ThreadPuzzleFailPolicy.PlanSweep(null,
                new[] { new LockoutSweepCandidate(alt, "Alt", groupA, inside: false), new LockoutSweepCandidate(alt, "Alt", groupB, inside: false) },
                _ => Array.Empty<uint>());

            var rec = new SweepRecorder();
            rec.Install();
            ThreadPuzzleFailPolicy.Execute(plan, Now.AddHours(2), Now);

            Assert.AreEqual(2, rec.Ejected.Count);
            Assert.AreEqual(1, rec.Ejected.Count(e => e.Text != null), "one lockout line per character");
            Assert.IsTrue(groupA.IsPuzzleRemoved(alt) && groupB.IsPuzzleRemoved(alt));
        }

        [TestMethod]
        public void A_group_run_ends_when_its_last_member_inside_is_removed()
        {
            const uint owner = 0x50000e01u, puller = 0x50000e02u;
            var run = GroupRun(0x80000e01u, owner, puller);

            var plan = ThreadPuzzleFailPolicy.PlanSweep(new LockoutSweepRequest("k", Now.AddHours(1), run, puller),
                new[] { new LockoutSweepCandidate(puller, "Puller", run, inside: true) },
                _ => new[] { puller });

            Assert.AreEqual(1, plan.Count);
            Assert.IsTrue(plan[0].EndRun, "nobody else inside");
        }

        [TestMethod]
        public void A_removed_owner_holds_no_deal_seat()
        {
            const uint owner = 0x50000f01u, member = 0x50000f02u;
            var run = GroupRun(0x80000f01u, owner, member);
            run.SetMemberKey(member, 0x80000f02u);

            Assert.IsTrue(run.MarkPuzzleRemoved(owner));
            Assert.IsFalse(run.MarkPuzzleRemoved(owner), "a repeat is not a new removal");
            Assert.IsFalse(run.MarkPuzzleRemoved(0x5000ffffu), "a non-member cannot be removed");
            CollectionAssert.AreEqual(new[] { member }, run.DealSeats().ToList());
        }

        // ---- review round: entry gating, idempotent sweeps, one sweep per lockout, consumed fails ---------------

        [TestMethod]
        public void A_thread_copy_refuses_a_removed_member_and_admits_the_rest()
        {
            const uint owner = 0x50001301u, removed = 0x50001302u, kept = 0x50001303u;
            var run = GroupRun(0x80001301u, owner, removed, kept);
            run.MarkPuzzleRemoved(removed);

            Assert.IsFalse(ACE.Server.Realms.EphemeralRealm.AcceptsRosterGuid(run, removed), "removed: refused");
            Assert.IsTrue(ACE.Server.Realms.EphemeralRealm.AcceptsRosterGuid(run, kept), "unremoved: accepted");
            Assert.IsTrue(ACE.Server.Realms.EphemeralRealm.AcceptsRosterGuid(run, owner));
            Assert.IsFalse(ACE.Server.Realms.EphemeralRealm.AcceptsRosterGuid(run, 0x5000ffffu), "not on the roster");

            var ledger = MemoryLedger();
            Assert.IsTrue(ThreadPuzzleFailPolicy.RefusesEntry(run, removed, null, Now, Config(enabled: false), ledger), "a removal holds whatever the switch says");
            Assert.IsFalse(ThreadPuzzleFailPolicy.RefusesEntry(run, kept, null, Now, Config(), ledger));
        }

        /// <summary>
        /// The login reroute and every teleport validate through InstanceRouting.ValidateInstanceDestination, which asks
        /// EphemeralRealm.Accepts, which asks RefusesEntry first. Here: a locked key (an offline same-key member who
        /// logs in, never swept) is refused; the same member on another key is admitted.
        /// </summary>
        [TestMethod]
        public void A_locked_key_is_refused_entry_to_a_thread_copy()
        {
            const uint owner = 0x50001401u, member = 0x50001402u;
            var run = GroupRun(0x80001401u, owner, member);
            var config = Config();
            var ledger = MemoryLedger();
            var key = ThreadPuzzleIpKey.For(SharedIp, false, 2);

            ledger.ApplyLockout(key, Now, Now.AddHours(2));

            Assert.IsTrue(ThreadPuzzleFailPolicy.RefusesEntry(run, member, key, Now.AddMinutes(1), config, ledger));
            Assert.IsFalse(ThreadPuzzleFailPolicy.RefusesEntry(run, member, ThreadPuzzleIpKey.For(IPAddress.Parse("198.51.100.1"), false, 2), Now.AddMinutes(1), config, ledger));
            Assert.IsFalse(ThreadPuzzleFailPolicy.RefusesEntry(run, member, key, Now.AddHours(3), config, ledger), "the lockout runs out");
            Assert.IsFalse(ThreadPuzzleFailPolicy.RefusesEntry(null, member, key, Now, config, ledger), "not a Thread copy");
        }

        [TestMethod]
        public void A_second_sweep_over_the_same_key_does_nothing()
        {
            const uint owner = 0x50001501u, puller = 0x50001502u, friend = 0x50001503u;
            var run = GroupRun(0x80001501u, owner, puller, friend);
            var request = new LockoutSweepRequest("k", Now.AddHours(2), run, puller);
            var candidates = new[] { new LockoutSweepCandidate(puller, "Puller", run, inside: true) };
            Func<ThreadDungeonRun, IReadOnlyList<uint>> inside = _ => new[] { owner, puller, friend };

            var rec = new SweepRecorder();
            rec.Install();

            ThreadPuzzleFailPolicy.Execute(ThreadPuzzleFailPolicy.PlanSweep(request, candidates, inside), request.UntilUtc, Now);
            Assert.AreEqual(1, rec.Ejected.Count);

            var again = ThreadPuzzleFailPolicy.PlanSweep(request, candidates, inside);
            Assert.AreEqual(0, again.Count, "everyone it would remove is already removed");

            ThreadPuzzleFailPolicy.Execute(again, request.UntilUtc, Now);
            Assert.AreEqual(1, rec.Ejected.Count, "no second tell or teleport");
            Assert.AreEqual(0, rec.Ended.Count, "no spurious EndRun");
        }

        [TestMethod]
        public void Two_pulls_racing_at_threshold_minus_one_queue_exactly_one_sweep()
        {
            var config = Config(threshold: 3);

            for (var i = 0; i < 200; i++)
            {
                ThreadPuzzleFailPolicy.ClearPendingSweepsForTest();

                var ledger = MemoryLedger();
                var key = $"198.51.100.{i % 250}";
                ledger.RecordFailUnlessLocked(key, Now, config.Window, _ => null);
                ledger.RecordFailUnlessLocked(key, Now, config.Window, _ => null);

                var outcomes = new PuzzleFailOutcome[2];
                using (var barrier = new System.Threading.Barrier(2))
                {
                    var threads = Enumerable.Range(0, 2).Select(n => new System.Threading.Thread(() =>
                    {
                        barrier.SignalAndWait();
                        outcomes[n] = ThreadPuzzleFailPolicy.RecordScoredWrong(key, Actor((uint)(0x50001600u + n), (uint)(n + 1)), null, Now, config, ledger);
                    })).ToList();

                    threads.ForEach(t => t.Start());
                    threads.ForEach(t => t.Join());
                }

                Assert.AreEqual(1, ThreadPuzzleFailPolicy.PendingSweepCount, $"iteration {i}: exactly one sweep");
                Assert.AreEqual(1, outcomes.Count(o => o.Kind == PuzzleFailOutcomeKind.LockedOut), $"iteration {i}: exactly one creator");
                Assert.AreEqual(1, outcomes.Count(o => o.Kind == PuzzleFailOutcomeKind.AlreadyLocked), $"iteration {i}: the other is not counted");
            }
        }

        /// <summary>A lockout consumes the fails that earned it, so one shorter than the window cannot re-trigger as it ends.</summary>
        [TestMethod]
        public void A_lockout_shorter_than_the_window_does_not_retrigger_on_the_next_pull()
        {
            var config = Config(threshold: 3, window: 60, lockout: 5);
            var ledger = MemoryLedger();
            var key = ThreadPuzzleIpKey.For(SharedIp, false, 1);

            for (var n = 0; n < 3; n++)
                ThreadPuzzleFailPolicy.RecordScoredWrong(key, Actor(0x50001701u, 1), null, Now.AddSeconds(n), config, ledger);

            Assert.AreEqual(0, ledger.GetFails(key, Now.AddSeconds(3), config.Window).Count, "consumed by the lockout");

            var after = ThreadPuzzleFailPolicy.RecordScoredWrong(key, Actor(0x50001701u, 1), null, Now.AddMinutes(6), config, ledger);

            Assert.AreEqual(PuzzleFailOutcomeKind.Warned, after.Kind, "a fresh count, not an instant second lockout");
            Assert.AreEqual(2, after.Decision.Remaining);
        }

        // ---- production wiring, bound by source scans ---------------------------------------------------------

        [TestMethod]
        public void RefusedByUseGates_asks_the_policy_first()
        {
            var body = PooledLootSourceText.MethodBody(
                PooledLootSourceText.Read("Source/ACE.Server/ThreadDungeons/ThreadDungeonGemHandler.cs"),
                "private static bool RefusedByUseGates(");

            var refusal = body.IndexOf("ThreadPuzzleFailPolicy.Refusal(player.Session, DateTime.UtcNow)", StringComparison.Ordinal);
            var combat = body.IndexOf("player.CombatMode", StringComparison.Ordinal);

            Assert.IsTrue(refusal >= 0 && combat > refusal, "the lockout refusal is the first gate");
        }

        [TestMethod]
        public void Tick_reaches_the_sweep_drain_and_the_ledger_sweep()
        {
            var source = PooledLootSourceText.Read("Source/ACE.Server/ThreadDungeons/ThreadDungeonManager.cs");

            StringAssert.Contains(PooledLootSourceText.MethodBody(source, "public static void Tick()"), "TickPuzzleFailPolicy(now, reap);");

            var policyTick = PooledLootSourceText.MethodBody(source, "private static void TickPuzzleFailPolicy(");
            StringAssert.Contains(policyTick, "ThreadPuzzlePolicyConfig.Refresh();");
            StringAssert.Contains(policyTick, "ThreadPuzzleFailPolicy.DrainSweeps(now);");
            StringAssert.Contains(policyTick, "ThreadPuzzleIpLedger.Shared.Sweep(now);");
        }

        [TestMethod]
        public void Thread_copy_admission_asks_the_policy_before_any_rule_that_admits()
        {
            var accepts = PooledLootSourceText.MethodBody(
                PooledLootSourceText.Read("Source/ACE.Server/Realms/EphemeralRealm.cs"),
                "public bool Accepts(Player player)");

            var guard = accepts.IndexOf("ThreadPuzzleFailPolicy.RefusesEntry(Run, player)", StringComparison.Ordinal);
            var owner = accepts.IndexOf("player == Owner", StringComparison.Ordinal);

            Assert.IsTrue(guard >= 0 && owner > guard, "the policy guard precedes the Owner, roster, allowed and fellowship admits");

            // The login reroute and the teleport path both validate through Accepts.
            var validate = PooledLootSourceText.MethodBody(
                PooledLootSourceText.Read("Source/ACE.Server/Realms/InstanceRouting.cs"),
                "public static Position ValidateInstanceDestination(this Position pos, Player player, out InstanceRejection rejection)");
            StringAssert.Contains(validate, "InnerRealmInfo?.Accepts(player)");
        }

        // ---- the end state --------------------------------------------------------------------------------------

        [TestMethod]
        public void A_lockout_end_is_aborted_not_the_abandoned_default()
        {
            Assert.AreEqual("puzzle lockout", ThreadPuzzleFailPolicy.EndReason);
            Assert.AreEqual(DungeonRunTelemetry.EndStates.Aborted, DungeonRunTelemetry.EndStateFor(ThreadDungeonRunState.Active, ThreadPuzzleFailPolicy.EndReason));
            Assert.AreEqual(DungeonRunTelemetry.EndStates.Aborted, DungeonRunTelemetry.EndStateFor(ThreadDungeonRunState.Starting, ThreadPuzzleFailPolicy.EndReason));
            Assert.AreEqual(DungeonRunTelemetry.EndStates.Cleared, DungeonRunTelemetry.EndStateFor(ThreadDungeonRunState.Cleared, ThreadPuzzleFailPolicy.EndReason), "cleared still wins");

            // Control: an unknown reason is the Abandoned default, so the Aborted above came from an explicit branch.
            Assert.AreEqual(DungeonRunTelemetry.EndStates.Abandoned, DungeonRunTelemetry.EndStateFor(ThreadDungeonRunState.Active, "puzzle lockout!"));
        }
    }
}
