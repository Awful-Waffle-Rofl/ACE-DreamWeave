using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

using ACE.Server.Realms;
using ACE.Server.ThreadDungeons;
using ACE.Server.ThreadDungeons.Defs;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ACE.Server.Tests.ThreadDungeons
{
    /// <summary>
    /// Open World Threads WP3a: the run lifecycle across a multi-landblock footprint.
    ///   * the run-end eviction set reaches every landblock under the run's instance (outdoor), the primary alone (dungeon);
    ///   * footprint members are held against their own idle unload while the run keeps its copy, and released at run end;
    ///   * population waits for the primary AND every member;
    ///   * TryStart's outdoor gate counts admitted-but-not-started runs, under the same lock as the registry insert.
    /// A Landblock and a Player cannot be built in this harness, so the decisions are tested pure (over fakes) and their
    /// wiring is pinned against the source text.
    /// </summary>
    [TestClass]
    public class OutdoorRunLifecycleTests
    {
        private const uint Instance = 0x80001234u;
        private const uint OtherInstance = 0x80005678u;

        private sealed class FakeLandblock
        {
            public FakeLandblock(string name, uint instance) { Name = name; Instance = instance; }
            public string Name { get; }
            public uint Instance { get; }
            public override string ToString() => Name;
        }

        private static ThreadDungeonRun OutdoorRun()
        {
            var dungeon = new DungeonEntryDef
            {
                Id = "ow_example",
                Kind = DungeonEntryDef.KindOutdoor,
                Landblock = 0x5456,
                ExitPortalWcid = 1003604,
                Outdoor = new OutdoorSiteDef { Primary = 0x5456, Footprint = new List<ushort> { 0x5456, 0x5556 } },
            };
            var spec = new DungeonGemSpec(dungeon.Id, 100, 6, "any", 1, new (string, double)[0], 0, 0);
            return new ThreadDungeonRun(Instance, 0x50000001u, "Tester", 7, 0x80000099u, spec, dungeon, DateTime.UtcNow, TimeSpan.FromMinutes(180));
        }

        // ---- eviction set ----

        [TestMethod]
        public void Outdoor_eviction_reaches_every_landblock_under_the_instance()
        {
            var primary = new FakeLandblock("primary", Instance);
            var member = new FakeLandblock("member", Instance);
            var buffer = new FakeLandblock("buffer", Instance);
            var foreign = new FakeLandblock("someone else's copy", OtherInstance);

            var set = ThreadRunGeometry.EvictionSet(true, primary, new[] { foreign, member, primary, buffer }, Instance, lb => lb.Instance);

            CollectionAssert.AreEqual(new[] { primary, member, buffer }, set, "primary first, then every other landblock under the run's instance, each once");
        }

        [TestMethod]
        public void Dungeon_eviction_is_the_primary_alone()
        {
            var primary = new FakeLandblock("primary", Instance);
            var stray = new FakeLandblock("stray", Instance);

            var set = ThreadRunGeometry.EvictionSet(false, primary, new[] { primary, stray }, Instance, lb => lb.Instance);

            CollectionAssert.AreEqual(new[] { primary }, set, "a dungeon run's eviction is unchanged: its copy is one landblock");
        }

        [TestMethod]
        public void Eviction_without_a_primary_still_reaches_the_members()
        {
            var member = new FakeLandblock("member", Instance);

            var set = ThreadRunGeometry.EvictionSet<FakeLandblock>(true, null, new[] { member }, Instance, lb => lb.Instance);

            CollectionAssert.AreEqual(new[] { member }, set);
        }

        // ---- member hold ----

        [TestMethod]
        public void Only_a_member_of_a_holding_realm_is_held()
        {
            Assert.IsTrue(EphemeralInstanceGroup.MemberIdleUnloadHeld(EphemeralLoadRole.Member, realmHoldsMembers: true));

            Assert.IsFalse(EphemeralInstanceGroup.MemberIdleUnloadHeld(EphemeralLoadRole.Member, realmHoldsMembers: false), "released: #1753's own idle unload");
            Assert.IsFalse(EphemeralInstanceGroup.MemberIdleUnloadHeld(EphemeralLoadRole.Primary, realmHoldsMembers: true), "the primary measures the grace and drains the group");

            foreach (var role in new[] { EphemeralLoadRole.NotEphemeral, EphemeralLoadRole.Buffer, EphemeralLoadRole.Unrelated, EphemeralLoadRole.Orphan })
                Assert.IsFalse(EphemeralInstanceGroup.MemberIdleUnloadHeld(role, realmHoldsMembers: true), role.ToString());
        }

        [TestMethod]
        public void A_run_holds_its_members_while_live_and_releases_them_at_the_end()
        {
            var run = OutdoorRun();
            var realm = new EphemeralRealm(null) { Run = run };

            Assert.AreEqual(ThreadDungeonRunState.Starting, run.State);
            Assert.IsTrue(realm.HoldsFootprintMembers, "Starting: held");

            run.MarkPopulated(planned: 5, spawned: 5, bossWcid: 0);
            Assert.AreEqual(ThreadDungeonRunState.Active, run.State);
            Assert.IsTrue(realm.HoldsFootprintMembers, "Active: held");

            Assert.IsTrue(run.MarkEnded("test"));
            Assert.IsFalse(realm.HoldsFootprintMembers, "Ended: released, so the group unloads under the primary-last rule");
        }

        [TestMethod]
        public void A_cleared_run_holds_until_it_ends()
        {
            var run = OutdoorRun();
            var realm = new EphemeralRealm(null) { Run = run };

            run.MarkPopulated(planned: 0, spawned: 0, bossWcid: 0);   // nothing placed: Cleared at once
            Assert.AreEqual(ThreadDungeonRunState.Cleared, run.State);
            Assert.IsNull(run.UnloadIntervalOverride, "precondition: no loot or PvP hold, so the run supplies no idle window");
            Assert.IsTrue(realm.HoldsFootprintMembers, "R22: a Cleared run still holds its members for its whole lifetime, hold window or not");

            Assert.IsTrue(run.MarkEnded("test"));
            Assert.IsFalse(realm.HoldsFootprintMembers, "Ended releases the members");
        }

        [TestMethod]
        public void A_realm_without_a_run_never_holds()
        {
            Assert.IsFalse(new EphemeralRealm(null).HoldsFootprintMembers, "the /enter-instance-group seam and other groups keep #1753's semantics");
        }

        // ---- population gate ----

        [TestMethod]
        public void Population_waits_for_every_member()
        {
            var landblocks = new ushort[] { 0x5456, 0x5556, 0x5457 };
            var state = new Dictionary<ushort, bool?> { [0x5456] = true, [0x5556] = true, [0x5457] = true };

            Assert.IsTrue(ThreadRunGeometry.AllCreateCompleted(landblocks, lb => state[lb]), "all loaded and complete");

            state[0x5457] = false;
            Assert.IsFalse(ThreadRunGeometry.AllCreateCompleted(landblocks, lb => state[lb]), "a member still loading holds population");

            state[0x5457] = null;
            Assert.IsFalse(ThreadRunGeometry.AllCreateCompleted(landblocks, lb => state[lb]), "a member that is not loaded holds population");

            state[0x5457] = true;
            state[0x5456] = false;
            Assert.IsFalse(ThreadRunGeometry.AllCreateCompleted(landblocks, lb => state[lb]), "the primary still loading holds population");
        }

        [TestMethod]
        public void Population_gate_edge_cases()
        {
            Assert.IsTrue(ThreadRunGeometry.AllCreateCompleted(new ushort[] { 0x0150 }, _ => true), "a dungeon: its one landblock");
            Assert.IsFalse(ThreadRunGeometry.AllCreateCompleted(new ushort[0], _ => true), "nothing to populate never passes");
            Assert.IsFalse(ThreadRunGeometry.AllCreateCompleted(null, _ => true));
        }

        // ---- TryStart outdoor gate ----

        [TestMethod]
        public void The_outdoor_cap_counts_admitted_runs_that_have_not_started()
        {
            var registry = new List<(ThreadDungeonRunState, bool)>
            {
                (ThreadDungeonRunState.Starting, true),     // admitted, still loading: counts
                (ThreadDungeonRunState.Active, true),
                (ThreadDungeonRunState.Cleared, true),
                (ThreadDungeonRunState.Ended, true),        // gone
                (ThreadDungeonRunState.Starting, false),    // a dungeon: not outdoor
            };

            Assert.AreEqual(3, ThreadDungeonManager.CountOutdoorTowardCap(registry));
            Assert.AreEqual(0, ThreadDungeonManager.CountOutdoorTowardCap(null));
        }

        [TestMethod]
        public void Two_starts_for_the_last_slot_cannot_both_pass()
        {
            // TryStart's critical section, replayed: gate, then admit (register as Starting) on a pass.
            var registry = new List<(ThreadDungeonRunState State, bool IsOutdoor)> { (ThreadDungeonRunState.Active, true) };
            const long cap = 2;

            bool TryAdmit(out string why)
            {
                if (!ThreadDungeonManager.PassesOutdoorGate(true, true, cap, ThreadDungeonManager.CountOutdoorTowardCap(registry), out why))
                    return false;

                registry.Add((ThreadDungeonRunState.Starting, true));
                return true;
            }

            Assert.IsTrue(TryAdmit(out _), "the first start takes the last slot");
            Assert.IsFalse(TryAdmit(out var reason), "the second sees the first, still Starting, and is refused");
            Assert.AreEqual(ThreadDungeonManager.ServerBusyReason, reason);
        }

        [TestMethod]
        public void The_outdoor_gate_refuses_with_the_generic_reasons()
        {
            Assert.IsFalse(ThreadDungeonManager.PassesOutdoorGate(true, outdoorEnabled: false, 10, 0, out var off));
            Assert.AreEqual(ThreadDungeonManager.DungeonUnavailableReason, off, "the kill switch reads as an unavailable dungeon");

            Assert.IsFalse(ThreadDungeonManager.PassesOutdoorGate(true, true, 3, 3, out var busy));
            Assert.AreEqual(ThreadDungeonManager.ServerBusyReason, busy);

            Assert.IsTrue(ThreadDungeonManager.PassesOutdoorGate(true, true, 3, 2, out var none));
            Assert.IsNull(none);

            Assert.IsTrue(ThreadDungeonManager.PassesOutdoorGate(true, true, 0, 1000, out _), "a cap of 0 is unlimited");
        }

        [TestMethod]
        public void A_dungeon_start_never_meets_the_outdoor_gate()
        {
            Assert.IsTrue(ThreadDungeonManager.PassesOutdoorGate(false, outdoorEnabled: false, 1, 99, out var reason));
            Assert.IsNull(reason);
        }

        // ---- review F2: the group coming down ----

        [TestMethod]
        public void A_live_outdoor_run_missing_a_member_is_gone()
        {
            foreach (var state in new[] { ThreadDungeonRunState.Active, ThreadDungeonRunState.Cleared })
            {
                Assert.IsTrue(ThreadDungeonManager.RunGroupGone(true, isOutdoor: true, state, allMembersLoaded: false), $"{state}: a drained member means the group is going");
                Assert.IsFalse(ThreadDungeonManager.RunGroupGone(true, isOutdoor: true, state, allMembersLoaded: true), $"{state}: the whole group is up");
            }

            Assert.IsFalse(ThreadDungeonManager.RunGroupGone(true, true, ThreadDungeonRunState.Starting, allMembersLoaded: false),
                "Starting: members may still be loading; the population gate and the load timeout decide");
        }

        [TestMethod]
        public void The_primary_rule_is_unchanged()
        {
            foreach (var outdoor in new[] { false, true })
            foreach (var state in new[] { ThreadDungeonRunState.Starting, ThreadDungeonRunState.Active, ThreadDungeonRunState.Cleared })
                Assert.IsTrue(ThreadDungeonManager.RunGroupGone(false, outdoor, state, allMembersLoaded: true), "no primary is gone, whatever else");

            Assert.IsFalse(ThreadDungeonManager.RunGroupGone(true, isOutdoor: false, ThreadDungeonRunState.Active, allMembersLoaded: false),
                "a dungeon run has no members to miss");
        }

        [TestMethod]
        public void An_ended_outdoor_primary_is_always_watched()
        {
            Assert.IsTrue(ThreadDungeonManager.EndedCopyNeedsWatch(isOutdoor: true, playersOnCopy: 0), "its first claim only drains the members");
            Assert.IsTrue(ThreadDungeonManager.EndedCopyNeedsWatch(isOutdoor: false, playersOnCopy: 1), "an occupied dungeon copy, as before");
            Assert.IsFalse(ThreadDungeonManager.EndedCopyNeedsWatch(isOutdoor: false, playersOnCopy: 0), "an empty dungeon copy is queued at once, as before");
        }

        [TestMethod]
        public void A_refused_start_cleans_up_only_its_own_copy()
        {
            Assert.AreEqual(ThreadDungeonManager.RefusedCopyAction.QueueNow, ThreadDungeonManager.DecideRefusedCopy(false, true, false), "a dungeon copy: queued once, as before");
            Assert.AreEqual(ThreadDungeonManager.RefusedCopyAction.Watch, ThreadDungeonManager.DecideRefusedCopy(true, true, true), "an outdoor primary: watched until the instance is gone");
            Assert.AreEqual(ThreadDungeonManager.RefusedCopyAction.None, ThreadDungeonManager.DecideRefusedCopy(true, true, false), "an id collision's landblock is someone else's");
            Assert.AreEqual(ThreadDungeonManager.RefusedCopyAction.None, ThreadDungeonManager.DecideRefusedCopy(true, false, false));
            Assert.AreEqual(ThreadDungeonManager.RefusedCopyAction.None, ThreadDungeonManager.DecideRefusedCopy(false, false, false));
        }

        // ---- review F4: grace clock ----

        [TestMethod]
        public void The_grace_clock_is_the_latest_activity_in_the_group()
        {
            var t0 = new DateTime(2026, 10, 20, 12, 0, 0, DateTimeKind.Utc);

            Assert.AreEqual(t0.AddMinutes(4), ThreadDungeonManager.GroupLastActiveUtc(new[] { t0, t0.AddMinutes(4), t0.AddMinutes(1) }),
                "a player on a member keeps the group active even when the primary is idle");
            Assert.AreEqual(t0, ThreadDungeonManager.GroupLastActiveUtc(new[] { t0 }), "a dungeon: its one landblock");
            Assert.AreEqual(DateTime.MinValue, ThreadDungeonManager.GroupLastActiveUtc(null));
        }

        // ---- review F5: member load failure ----

        [TestMethod]
        public void A_throwing_member_load_stops_and_reports()
        {
            var loaded = new List<ushort>();
            ushort failedAt = 0;
            Exception reported = null;

            var ok = ACE.Server.Managers.RealmManager.TryLoadMembers(new ushort[] { 0x5657, 0x5456, 0x5556 }, 0x5456,
                lb => { if (lb == 0x5556) throw new InvalidOperationException("dat read"); loaded.Add(lb); },
                (lb, ex) => { failedAt = lb; reported = ex; });

            Assert.IsFalse(ok);
            Assert.AreEqual((ushort)0x5556, failedAt);
            Assert.IsInstanceOfType(reported, typeof(InvalidOperationException));
            Assert.AreEqual(0, loaded.Count, "ascending order: 0x5556 is the first member, and nothing after a failure is loaded");
        }

        [TestMethod]
        public void Every_member_but_the_primary_is_loaded_in_order()
        {
            var loaded = new List<ushort>();

            Assert.IsTrue(ACE.Server.Managers.RealmManager.TryLoadMembers(new ushort[] { 0x5657, 0x5456, 0x5556 }, 0x5456, loaded.Add, null));
            CollectionAssert.AreEqual(new ushort[] { 0x5556, 0x5657 }, loaded);
        }

        // ---- wiring pins (the parts that need a Landblock, a Player or PropertyManager) ----

        [TestMethod]
        public void Review_fixes_are_wired()
        {
            var manager = Manager();

            var tick = Compact(PooledLootSourceText.MethodBody(manager, "public static void Tick()"));
            StringAssert.Contains(tick, "LoadedRunLandblocks(run,landblock).Count==ThreadRunGeometry.RunLandblocks(run.Dungeon).Count");
            StringAssert.Contains(tick, "ShouldEnd(state,!RunGroupGone(landblock!=null,run.Dungeon?.IsOutdoor==true,state,allMembersLoaded),createCompleted,");
            StringAssert.Contains(tick, "WarnIfEmptyGraceRunningOut(run,GroupLastActiveUtc(LoadedRunLandblocks(run,landblock).Select(copy=>copy.LastActiveUtc)),");

            var start = PooledLootSourceText.MethodBody(manager, "public static bool TryStart(Player owner, WorldObject gem, DungeonGemSpec spec, DungeonEntryDef dungeon,\n            IReadOnlyList<RosterSeat> seats");
            var ended = start.IndexOf("run.MarkEnded(DungeonRunTelemetry.EndReasons.LoadFailed);", StringComparison.Ordinal);
            var decide = start.IndexOf("switch (DecideRefusedCopy(", StringComparison.Ordinal);
            var watch = start.IndexOf("QueueCopyForDestructionWhenEmpty(copy, true);", StringComparison.Ordinal);
            Assert.IsTrue(ended > 0 && decide > ended && watch > decide, "the refused run is Ended (releasing the hold) before its copy is let go");
            StringAssert.Contains(start, "landblock ?? (isOutdoor ? LandblockManager.GetEphemeralLandblock(instance) : null)");

            var realm = Compact(PooledLootSourceText.MethodBody(PooledLootSourceText.Read("Source/ACE.Server/Managers/RealmManager.cs"),
                "public static Entity.Landblock GetNewThreadOutdoorLandblock("));
            // Review delta F5: the PRIMARY load is inside LoadOutdoorGroup's failure path too, not only the members.
            StringAssert.Contains(realm, "returnLoadOutdoorGroup(()=>LandblockManager.GetLandblock(newACE.Entity.LandblockId((uint)site.Primary<<16),");
            Assert.AreEqual(1, Regex.Matches(realm, @"LandblockManager\.GetLandblock\(").Count - 1,
                "exactly one GetLandblock besides the primary's (the member loader); no load outside LoadOutdoorGroup");

            var group = Compact(PooledLootSourceText.MethodBody(PooledLootSourceText.Read("Source/ACE.Server/Managers/RealmManager.cs"), "internal static T LoadOutdoorGroup<T>("));
            StringAssert.Contains(group, "try{primary=loadPrimary();}catch(System.Exceptionex){onError?.Invoke(");
            StringAssert.Contains(group, "if(!TryLoadMembers(footprint,primaryId,loadMember,");

            // A load that fails before the player enters must not cost the gem: the refusal neither ends the run through EndRun
            // (whose OnRunEnded is the only GemDestroyer call) nor calls either directly.
            var refusal = start.Substring(start.IndexOf("if (landblock == null || !ThreadRunGeometry.LoadedKindMatches(", StringComparison.Ordinal));
            refusal = refusal.Substring(0, refusal.IndexOf("return false;", StringComparison.Ordinal));
            StringAssert.Contains(refusal, "runs.TryRemove(instance, out _);");
            foreach (var forbidden in new[] { "EndRun(", "OnRunEnded(", "GemDestroyer(", "DestroyGem(" })
                Assert.IsFalse(refusal.Contains(forbidden), $"the load-failure refusal must not call {forbidden}");
        }

        // ---- review delta F5: the primary load's failure path ----

        private sealed class FakeCopy { }

        [TestMethod]
        public void A_throwing_primary_load_is_reported_and_refused()
        {
            var membersLoaded = new List<ushort>();
            string what = null;
            Exception reported = null;

            var result = ACE.Server.Managers.RealmManager.LoadOutdoorGroup<FakeCopy>(
                () => throw new InvalidOperationException("Init threw"),
                _ => true, new ushort[] { 0x5456, 0x5556 }, 0x5456, membersLoaded.Add,
                (w, ex) => { what = w; reported = ex; });

            Assert.IsNull(result, "null sends TryStart down its refusal path");
            Assert.AreEqual("primary 0x5456", what);
            Assert.IsInstanceOfType(reported, typeof(InvalidOperationException));
            Assert.AreEqual(0, membersLoaded.Count, "no member is loaded behind a failed primary");
        }

        [TestMethod]
        public void The_group_load_sequence()
        {
            var copy = new FakeCopy();

            var loaded = new List<ushort>();
            Assert.AreSame(copy, ACE.Server.Managers.RealmManager.LoadOutdoorGroup(() => copy, _ => true, new ushort[] { 0x5456, 0x5556 }, 0x5456, loaded.Add, null), "all loaded: the primary");
            CollectionAssert.AreEqual(new ushort[] { 0x5556 }, loaded);

            loaded.Clear();
            Assert.AreSame(copy, ACE.Server.Managers.RealmManager.LoadOutdoorGroup(() => copy, _ => false, new ushort[] { 0x5456, 0x5556 }, 0x5456, loaded.Add, null),
                "an id collision's landblock is returned for TryStart to refuse");
            Assert.AreEqual(0, loaded.Count, "and no member is loaded under someone else's instance");

            string what = null;
            Assert.IsNull(ACE.Server.Managers.RealmManager.LoadOutdoorGroup(() => copy, _ => true, new ushort[] { 0x5456, 0x5556 }, 0x5456,
                _ => throw new InvalidOperationException("dat"), (w, _) => what = w), "a member throw refuses too");
            Assert.AreEqual("footprint member 0x5556", what);

            Assert.IsNull(ACE.Server.Managers.RealmManager.LoadOutdoorGroup<FakeCopy>(() => null, _ => true, new ushort[] { 0x5456 }, 0x5456, loaded.Add, null));
        }

        private static string Manager() => PooledLootSourceText.Read("Source/ACE.Server/ThreadDungeons/ThreadDungeonManager.cs");

        private static string Compact(string s) => Regex.Replace(s, @"\s+", "");

        [TestMethod]
        public void TryStart_checks_the_outdoor_gate_in_the_pre_check_and_under_the_admitting_lock()
        {
            var body = PooledLootSourceText.MethodBody(Manager(), "public static bool TryStart(Player owner, WorldObject gem, DungeonGemSpec spec, DungeonEntryDef dungeon,\n            IReadOnlyList<RosterSeat> seats");

            var pre = body.IndexOf("PassesStartGatesAfterSweep(", StringComparison.Ordinal);
            var preOutdoor = body.IndexOf("PassesOutdoorGate(isOutdoor, outdoorEnabled, outdoorMax, preOutdoor, out reason)", StringComparison.Ordinal);
            var sweep = body.IndexOf("EndClearedRunsForOwner(owner.Guid.Full);", StringComparison.Ordinal);
            var gates = body.IndexOf("PassesStartGates(gateCounts,", StringComparison.Ordinal);
            var authoritative = body.IndexOf("PassesOutdoorGate(isOutdoor, outdoorEnabled, outdoorMax, CountOutdoorTowardCap(runs.Values", StringComparison.Ordinal);
            var insert = body.IndexOf("runs.TryAdd(instance, run)", StringComparison.Ordinal);

            Assert.IsTrue(pre >= 0 && preOutdoor > pre && sweep > preOutdoor, "a start the outdoor gate refuses ends nothing: the gate runs before the sweep");
            Assert.IsTrue(gates > sweep && authoritative > gates && insert > authoritative, "the authoritative gate sits between the start gates and the insert");

            // Same critical section: no lock boundary between the authoritative gate and the insert.
            var between = body.Substring(authoritative, insert - authoritative);
            Assert.IsFalse(between.Contains("lock ("), "the gate and the TryAdd share one startLock section");
            Assert.AreEqual(2, Regex.Matches(body.Substring(0, authoritative), @"lock \(startLock\)").Count,
                "the authoritative gate is in the second startLock section (the admitting one), not the pre-check");
        }

        [TestMethod]
        public void TryStart_opens_an_outdoor_group_and_the_refusal_is_kind_aware()
        {
            var body = Compact(PooledLootSourceText.MethodBody(Manager(), "public static bool TryStart(Player owner, WorldObject gem, DungeonGemSpec spec, DungeonEntryDef dungeon,\n            IReadOnlyList<RosterSeat> seats"));

            StringAssert.Contains(body, "varlandblock=isOutdoor?RealmManager.GetNewThreadOutdoorLandblock(dungeon.Outdoor,owner,run,instance):RealmManager.GetNewThreadDungeonLandblock(");
            StringAssert.Contains(body, "!ThreadRunGeometry.LoadedKindMatches(isOutdoor,landblock.IsDungeon)");
            Assert.IsFalse(body.Contains("!landblock.IsDungeon)"), "the old dungeon-only refusal is gone");
        }

        [TestMethod]
        public void The_outdoor_group_loads_every_member_under_the_runs_realm()
        {
            var body = Compact(PooledLootSourceText.MethodBody(PooledLootSourceText.Read("Source/ACE.Server/Managers/RealmManager.cs"),
                "public static Entity.Landblock GetNewThreadOutdoorLandblock("));

            StringAssert.Contains(body, "Run=run,");
            StringAssert.Contains(body, "Footprint=EphemeralFootprint.Create(site.Primary,site.Footprint)");
            StringAssert.Contains(body, "LandblockManager.GetLandblock(newACE.Entity.LandblockId((uint)site.Primary<<16),instance,loadAdjacents:true,permaload:false,ephemeralRealm:ephemeralRealm)");
            StringAssert.Contains(body, "ephemeralRealm.Footprint.Landblocks,site.Primary,");
            StringAssert.Contains(body, "LandblockManager.GetLandblock(newACE.Entity.LandblockId((uint)member<<16),instance,loadAdjacents:true)");
        }

        [TestMethod]
        public void Tick_populates_and_reaps_on_the_whole_footprint()
        {
            var tick = Compact(PooledLootSourceText.MethodBody(Manager(), "public static void Tick()"));

            StringAssert.Contains(tick, "varcreateCompleted=RunCopyCreateCompleted(run,landblock);");
            StringAssert.Contains(tick, "LoadedRunLandblocks(run,landblock).Any(copy=>copy.GetAllWorldObjectsForDiagnostics().OfType<Player>().Any())");
            StringAssert.Contains(tick, "if(state==ThreadDungeonRunState.Starting&&createCompleted)");

            var gate = Compact(PooledLootSourceText.MethodBody(Manager(), "private static bool RunCopyCreateCompleted("));
            StringAssert.Contains(gate, "ThreadRunGeometry.AllCreateCompleted(ThreadRunGeometry.RunLandblocks(run.Dungeon)");
        }

        [TestMethod]
        public void EndRun_evicts_from_the_whole_eviction_set()
        {
            var endRun = Compact(PooledLootSourceText.MethodBody(Manager(), "public static void EndRun(ThreadDungeonRun run, string why)"));

            StringAssert.Contains(endRun, "varcopies=ThreadRunGeometry.EvictionSet(");
            StringAssert.Contains(endRun, "LandblockManager.GetLoadedLandblocksUnder(run.Instance)");
            StringAssert.Contains(endRun, "varplayers=copies.SelectMany(copy=>copy.GetAllWorldObjectsForDiagnostics().OfType<Player>())");
        }

        [TestMethod]
        public void The_heartbeat_consults_the_member_hold_before_queueing_itself()
        {
            var landblock = Compact(PooledLootSourceText.Read("Source/ACE.Server/Entity/Landblock.cs"));

            var held = landblock.IndexOf("varheldMember=Realms.EphemeralInstanceGroup.MemberIdleUnloadHeld(GroupRole,InnerRealmInfo?.HoldsFootprintMembers==true);", StringComparison.Ordinal);
            var queue = landblock.IndexOf("if(!heldMember&&ShouldQueueForUnload(players.Count,lastActiveTime,thisHeartBeat,InnerRealmInfo?.UnloadIntervalOverride??UnloadInterval))", StringComparison.Ordinal);

            Assert.IsTrue(held >= 0 && queue > held, "the hold is decided, then gates the idle unload");

            var realm = PooledLootSourceText.ExpressionBody(PooledLootSourceText.Read("Source/ACE.Server/Realms/EphemeralRealm.cs"), "public bool HoldsFootprintMembers");
            StringAssert.Contains(realm, "=> Run != null && Run.State != ACE.Server.ThreadDungeons.ThreadDungeonRunState.Ended;");
        }
    }
}
