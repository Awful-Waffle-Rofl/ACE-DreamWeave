using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

using ACE.Server.ThreadDungeons;
using ACE.Server.ThreadDungeons.Defs;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ACE.Server.Tests.ThreadDungeons
{
    /// <summary>
    /// Open World Threads WP4b: the lethal-Unraveling entry exemption (R18), the live-wiring pure helpers, and source pins on
    /// the few lines the feature adds to existing files (a pin fails if the line is moved away from the place that makes it work).
    /// </summary>
    [TestClass]
    public class OutdoorPerimeterWiringTests
    {
        private const uint Instance = 0x80001234u;
        private const uint Owner = 0x50000001u;

        private long clock;

        [TestInitialize]
        public void Setup()
        {
            clock = 1000;
            ThreadUnravelDeath.Clock = () => clock;
            ThreadUnravelDeath.Reset();
        }

        [TestCleanup]
        public void Teardown()
        {
            ThreadUnravelDeath.Clock = System.Diagnostics.Stopwatch.GetTimestamp;
            ThreadUnravelDeath.Reset();
        }

        private static ThreadDungeonRun Run()
        {
            var dungeon = new DungeonEntryDef { Id = "ow_example", Kind = DungeonEntryDef.KindOutdoor, Landblock = 0x5456, ExitPortalWcid = 1003604 };
            var spec = new DungeonGemSpec(dungeon.Id, 100, 6, "any", 1, new (string, double)[0], 0, 0);
            return new ThreadDungeonRun(Instance, Owner, "Tester", 7, 0x80000099u, spec, dungeon, DateTime.UtcNow, TimeSpan.FromMinutes(180));
        }

        // ---- death exemption --------------------------------------------------------------------------------------

        [TestMethod]
        public void A_mark_is_consumed_once()
        {
            ThreadUnravelDeath.Mark(Owner);
            Assert.IsTrue(ThreadUnravelDeath.TryConsume(Owner));
            Assert.IsFalse(ThreadUnravelDeath.TryConsume(Owner), "one mark, one death");
        }

        [TestMethod]
        public void No_mark_means_no_exemption()
        {
            Assert.IsFalse(ThreadUnravelDeath.TryConsume(Owner), "an ordinary Thread death earns nothing");
        }

        [TestMethod]
        public void A_stale_mark_does_not_exempt_a_later_death()
        {
            ThreadUnravelDeath.Mark(Owner);
            clock += (long)(ThreadUnravelDeath.MarkWindow.TotalSeconds * System.Diagnostics.Stopwatch.Frequency) + 1;
            Assert.IsFalse(ThreadUnravelDeath.TryConsume(Owner));
            Assert.IsFalse(ThreadUnravelDeath.TryConsume(Owner), "and the stale mark is gone");
        }

        [TestMethod]
        public void Marks_are_per_player()
        {
            ThreadUnravelDeath.Mark(Owner);
            Assert.IsFalse(ThreadUnravelDeath.TryConsume(Owner + 1));
            Assert.IsTrue(ThreadUnravelDeath.TryConsume(Owner));
        }

        [TestMethod]
        public void OnPlayerDied_with_a_mark_and_no_registered_run_consumes_the_mark_and_does_not_throw()
        {
            ThreadUnravelDeath.Mark(Owner);
            ThreadUnravelDeath.OnPlayerDied(Owner, 0xDEAD0000u);
            Assert.IsFalse(ThreadUnravelDeath.TryConsume(Owner));
        }

        [TestMethod]
        public void A_credited_member_re_enters_free_once_even_at_zero_entries()
        {
            var run = Run();
            Assert.IsTrue(run.IsRosterMember(Owner));
            run.GrantUnravelReentry(Owner);
            Assert.IsTrue(run.HasUnravelReentry(Owner));

            Assert.IsTrue(run.TryConsumeUnravelReentry(Owner));
            Assert.IsFalse(run.TryConsumeUnravelReentry(Owner), "the credit is spent");

            // The handler feeds a consumed credit into DecideEntryCharge as the free-entry flag.
            Assert.AreEqual(DungeonGemRules.EntryCharge.Free, DungeonGemRules.DecideEntryCharge(true, 0));
            Assert.AreEqual(DungeonGemRules.EntryCharge.Spend, DungeonGemRules.DecideEntryCharge(false, 3), "an ordinary death re-entry still spends");
        }

        [TestMethod]
        public void A_non_member_earns_no_credit()
        {
            var run = Run();
            run.GrantUnravelReentry(0x50000099u);
            Assert.IsFalse(run.TryConsumeUnravelReentry(0x50000099u));
        }

        [TestMethod]
        public void Only_a_write_that_would_kill_is_marked()
        {
            Assert.IsTrue(OutdoorPerimeterLive.WouldKill(40, 50));
            Assert.IsTrue(OutdoorPerimeterLive.WouldKill(50, 50));
            Assert.IsFalse(OutdoorPerimeterLive.WouldKill(51, 50));
        }

        [TestMethod]
        public void ClampNonLethal_never_takes_a_player_to_zero_and_uses_the_current_health()
        {
            Assert.AreEqual(9L, OutdoorPerimeterLive.ClampNonLethal(10, 39));
            Assert.AreEqual(0L, OutdoorPerimeterLive.ClampNonLethal(1, 39));
            Assert.AreEqual(0L, OutdoorPerimeterLive.ClampNonLethal(0, 39));
            Assert.AreEqual(39L, OutdoorPerimeterLive.ClampNonLethal(500, 39));
            Assert.AreEqual(0L, OutdoorPerimeterLive.ClampNonLethal(500, -5), "a negative amount never heals");
        }

        [TestMethod]
        public void RunIfApplicable_skips_when_applicability_flips_between_enqueue_and_execute()
        {
            var applicable = true;
            var ran = 0;
            var queued = new System.Collections.Generic.List<Action>();
            queued.Add(() => OutdoorPerimeterLive.RunIfApplicable(() => applicable, () => ran++));

            applicable = false; // died or began a teleport after the enqueue
            queued[0]();
            Assert.AreEqual(0, ran);

            applicable = true;
            queued[0]();
            Assert.AreEqual(1, ran);
        }

        [TestMethod]
        public void An_unspent_credit_makes_Decide_accept_a_zero_entry_gem_and_without_it_the_gem_is_refused()
        {
            var run = Run();
            var spec = new DungeonGemSpec("ow_example", 100, 6, "any", 1, new (string, double)[0], run.RunId, Owner);
            run.GrantUnravelReentry(Owner);
            var credit = run.HasUnravelReentry(Owner);
            Assert.IsTrue(credit);
            Assert.AreEqual(GemUseDecision.ReEnter, DungeonGemRules.Decide(spec, Owner, 0, true, true, 150, 100, out _, freeEntry: credit));

            run.TryConsumeUnravelReentry(Owner);
            Assert.AreEqual(GemUseDecision.Refuse, DungeonGemRules.Decide(spec, Owner, 0, true, true, 150, 100, out _, freeEntry: run.HasUnravelReentry(Owner)));
        }

        [TestMethod]
        public void The_credited_return_has_its_own_line()
        {
            Assert.AreEqual("The Thread gathers you back in. This return costs you nothing.", DungeonGemRules.UnravelReturnMessage);
        }

        [TestMethod]
        public void Pin_the_gem_handler_feeds_the_credit_into_Decide_and_the_line()
        {
            var text = Read("ThreadDungeons", "ThreadDungeonGemHandler.cs");
            var read = text.IndexOf("var unravelCredit = run != null && run.HasUnravelReentry(player.Guid.Full);", StringComparison.Ordinal);
            var decide = text.IndexOf("|| clearedReturn || unravelCredit);", StringComparison.Ordinal);
            var consume = text.IndexOf("run.TryConsumeUnravelReentry(player.Guid.Full)", StringComparison.Ordinal);
            var line = text.IndexOf("unravelFree ? DungeonGemRules.UnravelReturnMessage", StringComparison.Ordinal);
            Assert.IsTrue(read > 0 && read < decide && decide < consume && consume < line, "read, then Decide, then consume after admission, then the line");
        }

        [TestMethod]
        public void Pin_damage_and_health_set_honour_invincible_but_the_snap_back_teleport_does_not()
        {
            var text = Read("ThreadDungeons", "OutdoorPerimeterLive.cs");
            StringAssert.Contains(text, "if (player.Invincible || amount <= 0)");
            StringAssert.Contains(text, "OutdoorPerimeterLive.ClampNonLethal(current, amount)");
            var snap = text.IndexOf("public void SetHealthToOne()", StringComparison.Ordinal);
            var teleport = text.IndexOf("public void TeleportToDrop()", StringComparison.Ordinal);
            var body = text.Substring(snap, teleport - snap);
            var guard = body.IndexOf("if (player.Invincible)", StringComparison.Ordinal);
            var write = body.IndexOf("player.UpdateVital(player.Health, 1);", StringComparison.Ordinal);
            Assert.IsTrue(guard > 0 && guard < write, "an invincible player's health is never set to 1");
            StringAssert.Contains(body, "if (player.Health.Current > 1)");
            var teleportEnd = text.IndexOf("\n        }", teleport, StringComparison.Ordinal);
            Assert.IsFalse(text.Substring(teleport, teleportEnd - teleport).Contains("Invincible"), "an invincible player still snaps back");
        }

        [TestMethod]
        public void Pin_the_teleport_rechecks_applicability_on_the_world_queue_and_the_ring_takes_the_probe_cell()
        {
            var text = Read("ThreadDungeons", "OutdoorPerimeterLive.cs");
            StringAssert.Contains(text, "WorldManager.EnqueueAction(");
            StringAssert.Contains(text, "RunIfApplicable(() => StillApplicable, () => player.Teleport(new Position(run.EntryPosition), true))");
            Assert.IsFalse(text.Contains("ThreadSafeTeleport"));
            StringAssert.Contains(text, "position.LandblockId = new LandblockId(probe.Cell);");
            StringAssert.Contains(text, "position.PositionX = probe.Rest.X;");
            StringAssert.Contains(text, "position.PositionY = probe.Rest.Y;");
        }

        // ---- input helpers ----------------------------------------------------------------------------------------

        [TestMethod]
        public void SecondsSince_reads_the_arrival_stamp_and_treats_never_arrived_as_long_ago()
        {
            const long second = TimeSpan.TicksPerSecond;
            Assert.AreEqual(4.0, OutdoorPerimeterLive.SecondsSince(10 * second, 14 * second), 1e-9);
            Assert.AreEqual(0.0, OutdoorPerimeterLive.SecondsSince(20 * second, 14 * second), "a stamp from the future clamps to zero");
            Assert.IsTrue(OutdoorPerimeterLive.SecondsSince(0, 14 * second) > 1e6, "never stamped: the grace must not protect an unknown");
        }

        [TestMethod]
        public void SecondsSinceInbound_reads_the_last_packet_ticks_and_treats_none_as_stale()
        {
            const long second = TimeSpan.TicksPerSecond;
            Assert.AreEqual(1.5, OutdoorPerimeterLive.SecondsSinceInbound(10 * second, 10 * second + (long)(1.5 * second)), 1e-9);
            Assert.IsTrue(OutdoorPerimeterLive.SecondsSinceInbound(0, 14 * second) > OutdoorPerimeterSettings.InboundStalenessS, "no packet on record reads as stale, the player-safe side");
        }

        // ---- source pins -------------------------------------------------------------------------------------------

        private static string SourceRoot()
        {
            for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
            {
                var candidate = Path.Combine(dir.FullName, "Source", "ACE.Server");
                if (Directory.Exists(Path.Combine(candidate, "ThreadDungeons")))
                    return candidate;
            }

            Assert.Fail($"Could not find Source/ACE.Server by walking up from {AppContext.BaseDirectory}");
            return null;
        }

        private static string Read(params string[] parts)
            => File.ReadAllText(Path.Combine(new[] { SourceRoot() }.Concat(parts).ToArray())).Replace("\r\n", "\n");

        [TestMethod]
        public void Pin_ThreadDungeonManager_Tick_makes_exactly_one_ticker_call_inside_Tick()
        {
            var text = Read("ThreadDungeons", "ThreadDungeonManager.cs");
            Assert.AreEqual(1, Regex.Matches(text, @"OutdoorPerimeterTicker\.Tick\(").Count, "ONE call line in the manager");

            var tickStart = text.IndexOf("public static void Tick()", StringComparison.Ordinal);
            var call = text.IndexOf("OutdoorPerimeterTicker.Tick(runs.Values)", StringComparison.Ordinal);
            var loop = text.IndexOf("foreach (var run in runs.Values.ToList())", tickStart, StringComparison.Ordinal);
            Assert.IsTrue(tickStart > 0 && call > tickStart && call < loop, "the call is in Tick, before the run loop");
        }

        [TestMethod]
        public void Pin_the_arrival_stamp_sits_on_the_line_after_Teleporting_is_cleared()
        {
            var text = Read("WorldObjects", "Player_Location.cs");
            Assert.AreEqual(1, Regex.Matches(text, @"LastArrivalTicks\s*=").Count);
            Assert.IsTrue(Regex.IsMatch(text, @"Teleporting = false;\n\s*LastArrivalTicks = System\.DateTime\.UtcNow\.Ticks;"), "stamped at arrival, immediately after Teleporting = false");
        }

        [TestMethod]
        public void Pin_Die_hooks_the_unravel_death_after_the_thread_latch()
        {
            var text = Read("WorldObjects", "Player_Death.cs");
            var latch = text.IndexOf("dynamicDungeonDeathInProgress = ACE.Server.ThreadDungeons.ThreadDungeonManager.GetRun", StringComparison.Ordinal);
            var hook = text.IndexOf("ThreadUnravelDeath.OnPlayerDied(Guid.Full, Location.Instance)", StringComparison.Ordinal);
            Assert.IsTrue(latch > 0 && hook > latch, "the hook reads the latch it follows");
        }

        [TestMethod]
        public void Pin_the_gem_handler_spends_the_unravel_credit_before_deciding_the_charge()
        {
            var text = Read("ThreadDungeons", "ThreadDungeonGemHandler.cs");
            var consume = text.IndexOf("run.TryConsumeUnravelReentry(player.Guid.Full)", StringComparison.Ordinal);
            var decide = text.IndexOf("DungeonGemRules.DecideEntryCharge(keyFree || unravelFree", StringComparison.Ordinal);
            Assert.IsTrue(consume > 0 && decide > consume);
        }

        [TestMethod]
        public void Pin_subject_actions_run_on_the_players_own_landblock_queue()
        {
            var text = Read("ThreadDungeons", "OutdoorPerimeterLive.cs");
            StringAssert.Contains(text, "var landblock = player.CurrentLandblock;");
            StringAssert.Contains(text, "landblock.EnqueueAction(new ActionEventDelegate(action));");
        }

        [TestMethod]
        public void Pin_the_ticker_never_re_implements_a_rule()
        {
            var text = Read("ThreadDungeons", "OutdoorPerimeterTicker.cs");
            StringAssert.Contains(text, "OutdoorPerimeterRules.Classify(");
            StringAssert.Contains(text, "OutdoorPerimeterRules.Tick(");
        }
    }
}
