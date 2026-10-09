using System.Globalization;
using System.Linq;
using System.Text;

using ACE.Server.Entity;
using ACE.Server.Managers;
using ACE.Server.Network.GameAction;
using ACE.Server.Network.GameMessages;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ACE.Server.Tests
{
    /// <summary>
    /// WaffleACE slow-tick capture: the SLOW_TICK logfmt line - key order, culture, optional fields, and the
    /// top-5 landblock slots.
    /// </summary>
    [TestClass]
    public class SlowTickLineTests
    {
        [TestCleanup]
        public void Teardown()
        {
            // the cmd_* tests register command names in the process-static id table
            ACE.Server.Command.CommandProfileIds.ResetForTests();
        }

        private static SlowTickSnapshot Sample(bool worldUpdated)
        {
            var s = new SlowTickSnapshot
            {
                Iteration = 42,
                TotalMs = 123.456,
                WorldMs = 100.5,
                MedianMs = 12.25,
                Players = 7,
                ShardQueue = 3,
                Gc0 = 1,
                Gc1 = 0,
                Gc2 = 0,
                GcPauseMs = 4.5,
                Suppressed = 2,
                SuppressedMaxMs = 150,
                WeenieMiss = 895,
                WorldUpdated = worldUpdated,
            };

            for (var i = 0; i < WorldTickPhases.Count; i++)
                s.PhaseMs[i] = i + 0.5;

            return s;
        }

        private static string Render(SlowTickSnapshot s)
        {
            var sb = new StringBuilder();
            SlowTickLine.Append(sb, s);
            return sb.ToString();
        }

        private static string[] Keys(string line) => line.Split(' ').Skip(1).Select(kv => kv.Substring(0, kv.IndexOf('='))).ToArray();

        [TestMethod]
        public void KeysAppearInTheFixedOrder()
        {
            var s = Sample(true);
            s.Groups = 4;
            s.GroupPhysicsMaxMs = 20;
            s.GroupMultiMaxMs = 30;
            s.OfferLandblock(0x7D64FFFF, 0, 40, 2, 300);
            s.OfferOpcode(InboundOpcodeProfile.ActionKey((int)GameActionType.PutItemInContainer), 3, 12.5, 9);

            var line = Render(s);

            Assert.IsTrue(line.StartsWith("SLOW_TICK "), line);
            Assert.IsFalse(line.Contains('\n'), "one line");

            var expected = new[]
            {
                "iter", "total_ms", "world_ms", "median_ms", "players", "shard_queue", "gc0", "gc1", "gc2", "gc_pause_ms",
                "suppressed", "suppressed_max_ms", "weenie_miss",
                "ph_player_manager", "ph_inbound_messages", "ph_action_queue", "ph_delay_manager", "ph_lb_physics",
                "ph_lb_multithreaded", "ph_lb_singlethreaded", "ph_lb_unload", "ph_house_manager", "ph_thread_dungeons",
                "ph_world_managers", "ph_session_work", "ph_other",
                "par", "grp_physics_max_ms", "grp_multi_max_ms", "lb_count", "lb_sum_ms",
                "lb1", "lb1_inst", "lb1_ms", "lb1_players", "lb1_objs",
                "op_calls", "op_ms", "op1", "op1_n", "op1_ms", "op1_max_ms",
            };

            CollectionAssert.AreEqual(expected, Keys(line), line);
        }

        [TestMethod]
        public void ValuesAreFormattedAsExpected()
        {
            var s = Sample(true);
            s.Groups = 4;
            s.OfferLandblock(0x7D64FFFF, 0x00010002, 40.126, 2, 300);

            var line = Render(s);

            StringAssert.Contains(line, " iter=42 total_ms=123.46 world_ms=100.5 median_ms=12.25 players=7 shard_queue=3 gc0=1 gc1=0 gc2=0 gc_pause_ms=4.5 suppressed=2 suppressed_max_ms=150 weenie_miss=895 ph_player_manager=0.5 ");
            StringAssert.Contains(line, " ph_other=12.5 par=4 lb_count=1 lb_sum_ms=40.13 lb1=0x7D64FFFF lb1_inst=0x00010002 lb1_ms=40.13 lb1_players=2 lb1_objs=300");
        }

        [TestMethod]
        public void OpcodeSlotsNameTheInformativeIdentifier()
        {
            var s = Sample(true);
            s.ResetLandblocks();

            s.OfferOpcode(InboundOpcodeProfile.ActionKey((int)GameActionType.PutItemInContainer), 412, 284.312, 1.921);
            s.OfferOpcode(InboundOpcodeProfile.MessageKey((int)GameMessageOpcode.CharacterEnterWorld), 1, 60.5, 60.5);

            var line = Render(s);

            StringAssert.Contains(line, " op_calls=413 op_ms=344.81 ");
            StringAssert.Contains(line, " op1=ga_PutItemInContainer op1_n=412 op1_ms=284.31 op1_max_ms=1.92 ");
            StringAssert.Contains(line, " op2=gm_CharacterEnterWorld op2_n=1 op2_ms=60.5 op2_max_ms=60.5");
            Assert.IsFalse(line.Contains(" op3"), line);
            Assert.IsFalse(line.Contains(" op_over_"), "no overflow, so the overflow pair is omitted: " + line);
        }

        [TestMethod]
        public void AnOpcodeTheEnumDoesNotNameRendersAsHex()
        {
            var s = Sample(true);
            s.ResetLandblocks();

            s.OfferOpcode(InboundOpcodeProfile.ActionKey(0x0BAD), 1, 5, 5);
            s.OfferOpcode(InboundOpcodeProfile.MessageKey(0x0BAD), 1, 4, 4);

            var line = Render(s);

            StringAssert.Contains(line, " op1=ga_0x0BAD ");
            StringAssert.Contains(line, " op2=gm_0x0BAD ");
        }

        [TestMethod]
        public void OverflowIsCountedInTheTotalsAndReportedSeparately()
        {
            var s = Sample(true);
            s.ResetLandblocks();

            s.OfferOpcode(InboundOpcodeProfile.ActionKey((int)GameActionType.Talk), 2, 10, 6);
            s.AddOpcodeOverflow(7, 3.5);

            var line = Render(s);

            StringAssert.Contains(line, " op_calls=9 op_ms=13.5 op_over_calls=7 op_over_ms=3.5 op1=ga_Talk ");
        }

        [TestMethod]
        public void TopOpcodeSlotsKeepTheFiveSlowestInDescendingOrder()
        {
            var s = new SlowTickSnapshot();
            var ms = new[] { 3.0, 10, 1, 7, 12, 5, 9, 2 };

            for (var i = 0; i < ms.Length; i++)
                s.OfferOpcode(InboundOpcodeProfile.MessageKey(i), i + 1, ms[i], ms[i]);

            Assert.AreEqual(5, s.OpTopCount);
            CollectionAssert.AreEqual(new[] { 12.0, 10, 9, 7, 5 }, s.OpTopMs);
            CollectionAssert.AreEqual(new[] { 5, 2, 7, 4, 6 }, s.OpTopCalls, "call counts travel with their times");
            Assert.AreEqual(36, s.OpCalls, "OpCalls sums every offer (1..8), including the three that won no slot");
            Assert.AreEqual(ms.Sum(), s.OpMs, 1e-9);
        }

        [TestMethod]
        public void OpcodeFieldsSurviveACommaDecimalCulture()
        {
            var previous = CultureInfo.CurrentCulture;

            try
            {
                CultureInfo.CurrentCulture = new CultureInfo("de-DE");

                var s = Sample(true);
                s.ResetLandblocks();
                s.OfferOpcode(InboundOpcodeProfile.ActionKey((int)GameActionType.Talk), 1234, 1234.5, 12.25);

                var line = Render(s);

                StringAssert.Contains(line, " op1=ga_Talk op1_n=1234 op1_ms=1234.5 op1_max_ms=12.25");
                Assert.IsFalse(line.Contains(','), "no decimal or group comma may appear: " + line);
            }
            finally
            {
                CultureInfo.CurrentCulture = previous;
            }
        }

        [TestMethod]
        public void InvariantCultureUnderACommaDecimalCulture()
        {
            var previous = CultureInfo.CurrentCulture;

            try
            {
                CultureInfo.CurrentCulture = new CultureInfo("de-DE");

                var s = Sample(true);
                s.OfferLandblock(0xABCDFFFF, 0, 1234.5, 1, 1);

                var line = Render(s);

                StringAssert.Contains(line, "total_ms=123.46");
                StringAssert.Contains(line, "lb1_ms=1234.5");
                Assert.IsFalse(line.Contains(','), "no decimal or group comma may appear: " + line);
            }
            finally
            {
                CultureInfo.CurrentCulture = previous;
            }
        }

        [TestMethod]
        public void GroupMaxFieldsAreOmittedWhenTheirModeIsOff()
        {
            var s = Sample(true);
            s.ResetLandblocks();
            s.Groups = 2;

            var line = Render(s);

            StringAssert.Contains(line, " par=2 lb_count=0 lb_sum_ms=0");
            Assert.IsFalse(line.Contains("grp_"), line);
        }

        [TestMethod]
        public void LandblockFieldsAreOmittedWhenTheWorldDidNotUpdate()
        {
            var s = Sample(false);
            s.OfferLandblock(0x12340000, 0, 5, 0, 0);
            s.OfferOpcode(InboundOpcodeProfile.ActionKey((int)GameActionType.Talk), 1, 2, 2);

            var line = Render(s);

            // the op_* block is NOT conditional on the world updating: the inbound-message phase runs every
            // iteration, so an iteration the rate limiter skipped can still have spent all its time there
            Assert.IsTrue(line.EndsWith(" ph_other=12.5 op_calls=1 op_ms=2 op1=ga_Talk op1_n=1 op1_ms=2 op1_max_ms=2"), line);
            Assert.IsFalse(line.Contains(" lb"), line);
            Assert.IsFalse(line.Contains(" par="), line);
        }

        [TestMethod]
        public void FewerThanFiveLandblocksOmitsTheEmptySlots()
        {
            var s = Sample(true);
            s.OfferLandblock(0x11110000, 0, 5, 0, 0);
            s.OfferLandblock(0x22220000, 0, 9, 0, 0);

            var line = Render(s);

            StringAssert.Contains(line, " lb1=0x22220000 ");
            StringAssert.Contains(line, " lb2=0x11110000 ");
            Assert.IsFalse(line.Contains(" lb3"), line);
            StringAssert.Contains(line, " lb_count=2 lb_sum_ms=14 ");
        }

        // ---- per-landblock tick sections (lbN_s1 / lbN_s2) ----

        private static double[] Sections(params (LandblockTickSection section, double ms)[] values)
        {
            var ms = new double[LandblockTickSections.Count];

            foreach (var (section, v) in values)
                ms[(int)section] = v;

            return ms;
        }

        [TestMethod]
        public void SectionKeysAreAppendedAfterTheOpBlockWithoutMovingAnyKey()
        {
            var s = Sample(true);
            s.Groups = 4;
            s.GroupPhysicsMaxMs = 20;
            s.GroupMultiMaxMs = 30;
            s.OfferLandblock(0x7D64FFFF, 0, 40, 2, 300, Sections((LandblockTickSection.GenUpdate, 30), (LandblockTickSection.DbSave, 6)));
            s.OfferOpcode(InboundOpcodeProfile.ActionKey((int)GameActionType.PutItemInContainer), 3, 12.5, 9);

            var expected = new[]
            {
                "iter", "total_ms", "world_ms", "median_ms", "players", "shard_queue", "gc0", "gc1", "gc2", "gc_pause_ms",
                "suppressed", "suppressed_max_ms", "weenie_miss",
                "ph_player_manager", "ph_inbound_messages", "ph_action_queue", "ph_delay_manager", "ph_lb_physics",
                "ph_lb_multithreaded", "ph_lb_singlethreaded", "ph_lb_unload", "ph_house_manager", "ph_thread_dungeons",
                "ph_world_managers", "ph_session_work", "ph_other",
                "par", "grp_physics_max_ms", "grp_multi_max_ms", "lb_count", "lb_sum_ms",
                "lb1", "lb1_inst", "lb1_ms", "lb1_players", "lb1_objs",
                "op_calls", "op_ms", "op1", "op1_n", "op1_ms", "op1_max_ms",
                "lb1_s1", "lb1_s1_ms", "lb1_s2", "lb1_s2_ms",
            };

            CollectionAssert.AreEqual(expected, Keys(Render(s)));
        }

        [TestMethod]
        public void SectionValuesNameTheTwoLargestSectionsOfTheirOwnLandblock()
        {
            var s = Sample(true);

            // offered smallest first, so the slots are re-ordered and the sections must travel with them
            s.OfferLandblock(0x11110000, 0, 10, 0, 50, Sections((LandblockTickSection.PlayerTick, 4), (LandblockTickSection.Physics, 5), (LandblockTickSection.Monster, 0.5)));
            s.OfferLandblock(0x482EFFFF, 0, 84.4, 0, 900, Sections((LandblockTickSection.GenUpdate, 80.123), (LandblockTickSection.LbHeartbeat, 3.5), (LandblockTickSection.WoHeartbeat, 0.2)));

            var line = Render(s);

            StringAssert.Contains(line, " lb1=0x482EFFFF ");
            StringAssert.Contains(line, " lb1_s1=gen_update lb1_s1_ms=80.12 lb1_s2=lb_heartbeat lb1_s2_ms=3.5 lb2_s1=physics lb2_s1_ms=5 lb2_s2=player_tick lb2_s2_ms=4");
        }

        [TestMethod]
        public void SectionKeysAreOmittedForSlotsWithFewerReportableSections()
        {
            var s = Sample(true);
            s.OfferLandblock(0x22220000, 0, 9, 0, 0, Sections((LandblockTickSection.RunActions, 7), (LandblockTickSection.GenRegen, 0.001)));
            s.OfferLandblock(0x11110000, 0, 5, 0, 0, Sections());
            s.OfferLandblock(0x33330000, 0, 1, 0, 0); // no section data at all

            var line = Render(s);

            StringAssert.Contains(line, " lb1_s1=run_actions lb1_s1_ms=7");
            Assert.IsFalse(line.Contains(" lb1_s2"), "one reportable section, so no s2: " + line);
            Assert.IsFalse(line.Contains(" lb2_s"), "no reportable section, so no s1: " + line);
            Assert.IsFalse(line.Contains(" lb3_s"), line);
            Assert.IsTrue(line.EndsWith(" lb1_s1=run_actions lb1_s1_ms=7"), line);
        }

        [TestMethod]
        public void SectionKeysAreOmittedWhenTheWorldDidNotUpdate()
        {
            var s = Sample(false);
            s.OfferLandblock(0x12340000, 0, 5, 0, 0, Sections((LandblockTickSection.DbSave, 5)));

            var line = Render(s);

            Assert.IsFalse(line.Contains("_s1"), line);
        }

        // ---- chat @command attribution (cmd_*) ----

        [TestMethod]
        public void ACommandKeyRendersAsTheRegisteredCommandName()
        {
            var info = new ACE.Server.Command.CommandHandlerInfo
            {
                Attribute = new ACE.Server.Command.CommandHandlerAttribute("SlowTick.Line Probe", ACE.Entity.Enum.AccessLevel.Player, ACE.Server.Command.CommandHandlerFlag.None, "", ""),
            };

            var id = ACE.Server.Command.CommandProfileIds.IdFor(info);

            var s = Sample(false);
            s.OfferOpcode(InboundOpcodeProfile.CommandKey(id), 1, 30, 30);
            s.OfferOpcode(InboundOpcodeProfile.CommandKey(ACE.Server.Command.CommandProfileIds.Unknown), 1, 20, 20);
            s.OfferOpcode(InboundOpcodeProfile.CommandKey(ACE.Server.Command.CommandProfileIds.MaxIds - 1), 1, 10, 10);

            var line = Render(s);

            StringAssert.Contains(line, " op1=cmd_slowtick_line_probe ", "lowercased, and every character outside [a-z0-9_-] replaced");
            StringAssert.Contains(line, " op2=cmd_unknown ");
            StringAssert.Contains(line, " op3=cmd_0x0FFF ", "an id with no name renders as hex, never blank");
        }

        [TestMethod]
        public void AnIdCachedBeforeAResetIsReassignedAndStillNamed()
        {
            var other = new ACE.Server.Command.CommandHandlerInfo
            {
                Attribute = new ACE.Server.Command.CommandHandlerAttribute("resetprobe_a", ACE.Entity.Enum.AccessLevel.Player, ACE.Server.Command.CommandHandlerFlag.None, "", ""),
            };
            var info = new ACE.Server.Command.CommandHandlerInfo
            {
                Attribute = new ACE.Server.Command.CommandHandlerAttribute("resetprobe_b", ACE.Entity.Enum.AccessLevel.Player, ACE.Server.Command.CommandHandlerFlag.None, "", ""),
            };

            ACE.Server.Command.CommandProfileIds.IdFor(other);
            var before = ACE.Server.Command.CommandProfileIds.IdFor(info);

            ACE.Server.Command.CommandProfileIds.ResetForTests();

            Assert.IsNull(ACE.Server.Command.CommandProfileIds.NameOf(before), "the reset forgot every name");

            var after = ACE.Server.Command.CommandProfileIds.IdFor(info);

            Assert.AreNotEqual(before, after, "control: the stale cached id was not simply reused");
            Assert.AreEqual("resetprobe_b", ACE.Server.Command.CommandProfileIds.NameOf(after));
        }

        /// <summary>
        /// Command ids, GameActionTypes and GameMessageOpcodes overlap numerically, so the same low 32 bits must
        /// render three different ways depending only on the flag bits.
        /// </summary>
        [TestMethod]
        public void CommandActionAndMessageNamespacesDoNotCollide()
        {
            var raw = (int)GameActionType.Talk;

            var keys = new[] { InboundOpcodeProfile.MessageKey(raw), InboundOpcodeProfile.ActionKey(raw), InboundOpcodeProfile.CommandKey(raw) };

            Assert.AreEqual(3, keys.Distinct().Count(), "the three keys for one raw value must differ");
            Assert.AreEqual(0L, InboundOpcodeProfile.ActionKey(raw) & InboundOpcodeProfile.CommandFlag);
            Assert.AreEqual(0L, InboundOpcodeProfile.CommandKey(raw) & InboundOpcodeProfile.ActionFlag);
            Assert.AreEqual(0L, InboundOpcodeProfile.MessageKey(-1) & (InboundOpcodeProfile.ActionFlag | InboundOpcodeProfile.CommandFlag), "no opcode value can reach a flag bit");

            var s = Sample(false);
            s.OfferOpcode(keys[0], 1, 3, 3);
            s.OfferOpcode(keys[1], 1, 2, 2);
            s.OfferOpcode(keys[2], 1, 1, 1);

            var line = Render(s);

            StringAssert.Contains(line, " op1=gm_");
            StringAssert.Contains(line, " op2=ga_Talk ");
            StringAssert.Contains(line, " op3=cmd_");
            Assert.IsFalse(line.Contains(" op3=ga_") || line.Contains(" op3=gm_"), line);
        }

        [TestMethod]
        public void TopSlotsKeepTheFiveSlowestInDescendingOrder()
        {
            var s = new SlowTickSnapshot();
            var ms = new[] { 3.0, 10, 1, 7, 12, 5, 9, 2 };

            for (var i = 0; i < ms.Length; i++)
                s.OfferLandblock((uint)i, 0, ms[i], i, i);

            Assert.AreEqual(5, s.TopCount);
            CollectionAssert.AreEqual(new[] { 12.0, 10, 9, 7, 5 }, s.TopMs);
            CollectionAssert.AreEqual(new uint[] { 4, 1, 6, 3, 5 }, s.TopId, "ids travel with their times");
            CollectionAssert.AreEqual(new[] { 4, 1, 6, 3, 5 }, s.TopObjects);
            Assert.AreEqual(ms.Length, s.LandblockCount);
            Assert.AreEqual(ms.Sum(), s.LandblockSumMs, 1e-9);
        }
    }
}
