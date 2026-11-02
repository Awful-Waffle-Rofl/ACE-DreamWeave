using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;

using ACE.Server.Knots;
using ACE.Server.Managers.Analytics;
using ACE.Server.Pinnacles;
using ACE.Server.Tests.ThreadDungeons;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ACE.Server.Tests.Knots
{
    /// <summary>
    /// knot_event telemetry (ace_analytics.sql, Knots/KnotTelemetry.cs): the closed kind/result matrix, the pure row
    /// builders, the enqueue path, failure isolation, and source scans that pin each hook inside its method and after
    /// the effect it describes. AnalyticsManager's queue and enabled flag are process statics, so every test starts
    /// from a known state and leaves analytics disabled for whoever runs next.
    /// </summary>
    [TestClass]
    public class KnotTelemetryTests
    {
        private static readonly DateTime Ts = new DateTime(2026, 10, 21, 12, 0, 0, DateTimeKind.Utc);
        private static readonly KnotTelemetry.Who Alice = new KnotTelemetry.Who(0x50000001, "Alice", 77, 300);

        [TestInitialize]
        public void Setup()
        {
            AnalyticsManager.SetEnabledForTests(true);
            AnalyticsManager.ResetKnotQueueForTests();
            KnotTelemetry.ResetWarningsForTests();
        }

        [TestCleanup]
        public void Teardown()
        {
            AnalyticsManager.SetEnabledForTests(false);
            AnalyticsManager.ResetKnotQueueForTests();
            KnotTelemetry.ResetWarningsForTests();
        }

        // ---------------------------------------------------------------- matrix

        // The design's matrix, spelled out a second time on purpose: the dashboard SQL is written against these strings.
        private static readonly Dictionary<string, string[]> DesignMatrix = new Dictionary<string, string[]>
        {
            ["fiber_roll"] = new[] { "dropped", "dry", "lost" },
            ["pattern_roll"] = new[] { "dropped", "none", "lost" },
            ["weave"] = new[] { "ok", "partial" },
            ["bind"] = new[] { "pattern", "random" },
            ["copy_open"] = new[] { "ok" },
            ["entry"] = new[] { "charge", "paid_return", "seam" },
            ["attempt_end"] = new[] { "won", "unpaid", "expired", "forfeit", "reset", "unloaded", "fault", "lost" },
            ["knot_spent"] = new[] { "won", "lost_at_zero", "forfeit", "no_entries", "last_entry" },
        };

        private static IEnumerable<string> ConstStrings(Type t)
            => t.GetFields(BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy)
                .Where(f => f.IsLiteral && f.FieldType == typeof(string))
                .Select(f => (string)f.GetRawConstantValue());

        [TestMethod]
        public void ResultAndKindConstants_AreLowercaseAndFitTheirColumns()
        {
            foreach (var s in ConstStrings(typeof(KnotTelemetry.Result)).Concat(ConstStrings(typeof(KnotTelemetry.Kind))))
            {
                Assert.AreEqual(s.ToLowerInvariant(), s, s);
                Assert.IsTrue(s.Length > 0 && s.Length <= 16, s);
                Assert.IsTrue(Regex.IsMatch(s, "^[a-z_]+$"), s);
            }
        }

        [TestMethod]
        public void KindAndResultSets_EqualTheDesignMatrix()
        {
            CollectionAssert.AreEquivalent(DesignMatrix.Keys.ToList(), KnotTelemetry.Matrix.Keys.ToList());
            CollectionAssert.AreEquivalent(DesignMatrix.Keys.ToList(), ConstStrings(typeof(KnotTelemetry.Kind)).ToList());

            foreach (var kv in DesignMatrix)
                CollectionAssert.AreEquivalent(kv.Value, KnotTelemetry.Matrix[kv.Key], kv.Key);

            // every Result constant is used by some kind, and every kind's results are Result constants
            var all = new HashSet<string>(ConstStrings(typeof(KnotTelemetry.Result)));
            CollectionAssert.AreEquivalent(all.ToList(), DesignMatrix.Values.SelectMany(v => v).Distinct().ToList());
        }

        [TestMethod]
        public void TheDocumentedMatrixInTheSchema_MentionsEveryKindAndResult()
        {
            var sql = PooledLootSourceText.Read("Source/ACE.Server/Managers/Analytics/ace_analytics.sql");
            var start = sql.IndexOf("Knot telemetry. One row per Knot-economy EVENT", StringComparison.Ordinal);
            Assert.IsTrue(start >= 0);
            var doc = sql.Substring(start, sql.IndexOf("CREATE TABLE IF NOT EXISTS `knot_event`", start, StringComparison.Ordinal) - start);

            foreach (var kv in DesignMatrix)
            {
                var line = doc.Split('\n').FirstOrDefault(l => Regex.IsMatch(l, @"^--\s+" + kv.Key + @"\s"));
                Assert.IsNotNull(line, "documented kind " + kv.Key);

                foreach (var r in kv.Value)
                    StringAssert.Contains(doc.Substring(doc.IndexOf(line, StringComparison.Ordinal)), r, kv.Key + " / " + r);
            }
        }

        // ---------------------------------------------------------------- AttemptResultFor

        [DataTestMethod]
        [DataRow(PinnacleFightState.Won, "the owner left the copy with the cache delivered", false, true, "won")]
        [DataRow(PinnacleFightState.Lost, "fault in tick", false, true, "won")]
        [DataRow(PinnacleFightState.Lost, "reward never collected", true, false, "unpaid")]
        [DataRow(PinnacleFightState.Lost, "fault in tick", false, false, "fault")]
        [DataRow(PinnacleFightState.Lost, "forfeit: no entries", false, false, "forfeit")]
        [DataRow(PinnacleFightState.Lost, "reset: admin", false, false, "reset")]
        [DataRow(PinnacleFightState.Lost, "copy unloaded", false, false, "unloaded")]
        [DataRow(PinnacleFightState.Lost, "hold expired", false, false, "expired")]
        [DataRow(PinnacleFightState.Won, "something new", false, false, "won")]
        [DataRow(PinnacleFightState.Lost, "something new", false, false, "lost")]
        [DataRow(PinnacleFightState.Lost, null, false, false, "lost")]
        public void AttemptResultFor_FollowsTheDocumentedOrder(PinnacleFightState outcome, string reason, bool refund, bool delivered, string expected)
            => Assert.AreEqual(expected, KnotTelemetry.AttemptResultFor(outcome, reason, refund, delivered));

        [TestMethod]
        public void AttemptResultFor_CoversEveryEndReasonLiteralInPinnacleFightSource()
        {
            // expected result per reason literal, for the cache-not-delivered case (the delivered case is always won)
            var expected = new Dictionary<string, string>
            {
                ["forfeit: "] = "forfeit",
                ["reset: "] = "reset",
                ["copy unloaded"] = "unloaded",
                ["hold expired"] = "expired",
                ["reward never collected"] = "unpaid",
                ["the owner left the copy with the cache delivered"] = "won",
                ["collecting closed at hold expiry"] = "won",
                ["fault in "] = "fault",
            };

            var src = PooledLootSourceText.Read("Source/ACE.Server/Pinnacles/PinnacleFight.cs");
            var found = new List<string>();

            foreach (Match m in Regex.Matches(src, @"End\(PinnacleFightState\.(?<o>\w+),\s*(?<arg>""[^""]*""|FaultPrefix)(?<rest>[^;]*)\);"))
            {
                var literal = m.Groups["arg"].Value == "FaultPrefix" ? "fault in " : m.Groups["arg"].Value.Trim('"');
                var outcome = (PinnacleFightState)Enum.Parse(typeof(PinnacleFightState), m.Groups["o"].Value);
                var refund = m.Groups["rest"].Value.Contains("refund: true");
                var delivered = outcome == PinnacleFightState.Won;

                Assert.IsTrue(expected.ContainsKey(literal), $"new End reason literal '{literal}': teach KnotTelemetry.AttemptResultFor and add it to this table");
                Assert.AreEqual(expected[literal], KnotTelemetry.AttemptResultFor(outcome, literal, refund, delivered), literal);
                found.Add(literal);
            }

            CollectionAssert.AreEquivalent(expected.Keys.ToList(), found.Distinct().ToList(), "every table row is still an End reason in the source");
        }

        // ---------------------------------------------------------------- builders

        [TestMethod]
        public void BuildRollRows_NoPatternRow_WhenNoPinnacleWasEnabled()
        {
            var rows = KnotTelemetry.BuildRollRows(Ts, Alice, 250, 3, 4, false, false, null, 0, false, null, null, "tiny_hive", 0x8000FC92, "abc");

            Assert.AreEqual(1, rows.Count);
            Assert.AreEqual("fiber_roll", rows[0].Kind);
            Assert.AreEqual("dry", rows[0].Result);
            Assert.AreEqual(3, rows[0].MissesBefore);
            Assert.AreEqual(4, rows[0].MissesAfter);
            Assert.AreEqual(250, rows[0].KnotLevel);
        }

        [TestMethod]
        public void BuildRollRows_PatternRowCarriesKeyGuidAndEnabledCount()
        {
            var rows = KnotTelemetry.BuildRollRows(Ts, Alice, 250, 0, 0, true, true, 0x80001111u, 2, true, "grael", 0x80002222u, "tiny_hive", 7, "sg");

            Assert.AreEqual(2, rows.Count);
            Assert.AreEqual("dropped", rows[0].Result);
            Assert.AreEqual(0x80001111u, rows[0].ItemGuid);
            Assert.AreEqual("pattern_roll", rows[1].Kind);
            Assert.AreEqual("dropped", rows[1].Result);
            Assert.AreEqual("grael", rows[1].PinnacleKey);
            Assert.AreEqual(2, rows[1].EnabledPinnacles);
            Assert.AreEqual(0x80002222u, rows[1].ItemGuid);
            Assert.AreEqual("sg", rows[1].StartGroup);
            Assert.AreEqual("live", rows[1].Source);
        }

        [TestMethod]
        public void BuildRollRows_LostAndNoneResults()
        {
            var lost = KnotTelemetry.BuildRollRows(Ts, Alice, 250, 1, 1, true, false, null, 1, true, null, null, null, 1, null);
            Assert.AreEqual("lost", lost[0].Result);
            Assert.AreEqual("lost", lost[1].Result);

            var none = KnotTelemetry.BuildRollRows(Ts, Alice, 250, 1, 2, false, false, null, 1, false, null, null, null, 1, null);
            Assert.AreEqual("none", none[1].Result);
        }

        [TestMethod]
        public void Builders_ClipStringsToTheirColumns()
        {
            var long200 = new string('x', 200);
            var w = KnotTelemetry.BuildWeaveRow(Ts, new KnotTelemetry.Who(1, long200, 1, 1), "ok", long200, 250, 5, 10, long200);
            Assert.AreEqual(AnalyticsDatabase.KnotNameMaxLength, w.Name.Length);
            Assert.AreEqual(AnalyticsDatabase.KnotReasonMaxLength, w.Reason.Length);
            Assert.AreEqual(AnalyticsDatabase.KnotFiberLevelsMaxLength, w.FiberLevels.Length);
            Assert.AreEqual(10, w.Qty);

            var b = KnotTelemetry.BuildBindRow(Ts, Alice, true, 250, 5, long200, 3, 9);
            Assert.AreEqual(AnalyticsDatabase.KnotPinnacleKeyMaxLength, b.PinnacleKey.Length);

            var e = new PinnacleAttemptEnd(PinnacleFightState.Lost, long200, false, false, true, 2, 5, 250, 5, 0x50000001);
            var a = KnotTelemetry.BuildAttemptEndRow(Ts, Alice, e, long200, long200, 1.5, 7);
            Assert.AreEqual(AnalyticsDatabase.KnotReasonMaxLength, a.Reason.Length);
            Assert.AreEqual(AnalyticsDatabase.KnotGroupIdMaxLength, a.AttemptId.Length);
            Assert.AreEqual(AnalyticsDatabase.KnotPinnacleKeyMaxLength, a.PinnacleKey.Length);
        }

        [TestMethod]
        public void FiberLevelList_IsACommaListWithZeroForUnreadable()
            => Assert.AreEqual("250,0,251", KnotTelemetry.FiberLevelList(new int?[] { 250, null, 251 }));

        [TestMethod]
        public void AttemptEndRow_PopulatesTheDashboardGroupingColumns()
        {
            var e = new PinnacleAttemptEnd(PinnacleFightState.Won, "collecting closed at hold expiry", false, true, true, 4, 5, 260, 0x80000009, 0x50000001);
            var row = KnotTelemetry.BuildAttemptEndRow(Ts, Alice, e, "grael", "attemptidattemptidattemptid1234", 91.5, 0x8000FC92);

            Assert.AreEqual("attempt_end", row.Kind);
            Assert.AreEqual("won", row.Result);
            Assert.AreEqual(260, row.KnotLevel, "dashboards band on knot_level and filter IS NOT NULL");
            Assert.AreEqual("grael", row.PinnacleKey);
            Assert.AreEqual(0x80000009u, row.KnotGuid);
            Assert.AreEqual("attemptidattemptidattemptid1234", row.AttemptId);
            Assert.AreEqual(0x50000001u, row.CharacterId);
            Assert.AreEqual("Alice", row.Name);
            Assert.AreEqual(77u, row.AccountId);
            Assert.AreEqual(4, row.PhaseReached);
            Assert.AreEqual(5, row.PhaseCount);
            Assert.AreEqual(true, row.Charged);
            Assert.AreEqual(91.5, row.DurationSecs);
            Assert.AreEqual("collecting closed at hold expiry", row.Reason);
        }

        [TestMethod]
        public void CopyOpenAndEntryRows_PopulateLevelKeyAndAttempt()
        {
            var open = KnotTelemetry.BuildCopyOpenRow(Ts, Alice, "att", 5, 260, "grael", 9);
            Assert.AreEqual(260, open.KnotLevel);
            Assert.AreEqual("grael", open.PinnacleKey);
            Assert.AreEqual("att", open.AttemptId);
            Assert.AreEqual(5u, open.KnotGuid);
            Assert.AreEqual(9u, open.InstanceId);

            var entry = KnotTelemetry.BuildEntryRow(Ts, Alice, "charge", 5, 260, "grael", 2, "att");
            Assert.AreEqual(260, entry.KnotLevel);
            Assert.AreEqual("grael", entry.PinnacleKey);
            Assert.AreEqual("att", entry.AttemptId);
            Assert.AreEqual(2, entry.EntriesLeft);
            Assert.AreEqual(1, entry.Qty);
        }

        [TestMethod]
        public void BindAndSpentRows()
        {
            var pattern = KnotTelemetry.BuildBindRow(Ts, Alice, true, 250, 5, "grael", 3, 99);
            Assert.AreEqual("pattern", pattern.Result);
            Assert.AreEqual(99u, pattern.ItemGuid);
            Assert.AreEqual(3, pattern.EntriesMax);
            Assert.AreEqual(3, pattern.EntriesLeft, "a bind sets the full count");
            Assert.AreEqual(3, pattern.EntriesLeft, "a bind sets the full count");

            var random = KnotTelemetry.BuildBindRow(Ts, Alice, false, 250, 5, "grael", 3, 99);
            Assert.AreEqual("random", random.Result);
            Assert.IsNull(random.ItemGuid, "a random bind has no Pattern");

            var spent = KnotTelemetry.BuildKnotSpentRow(Ts, Alice, "won", 5, 250, "grael", 0);
            Assert.AreEqual("knot_spent", spent.Kind);
            Assert.AreEqual(0, spent.EntriesLeft);
        }

        [TestMethod]
        public void EntryResultFor_HeldFightIsAPaidReturn()
        {
            Assert.AreEqual("paid_return", KnotTelemetry.EntryResultFor(true));
            Assert.AreEqual("charge", KnotTelemetry.EntryResultFor(false));
        }

        // ---------------------------------------------------------------- enqueue path

        [TestMethod]
        public void RecordAttemptEnd_Enabled_QueuesTheRow()
        {
            var ctx = new PinnacleAttemptContext("grael", "Alice", 77, 300, 0x8000FC92);
            var e = new PinnacleAttemptEnd(PinnacleFightState.Lost, "hold expired", false, false, true, 2, 5, 250, 5, 0x50000001);

            KnotTelemetry.RecordAttemptEnd(e, ctx);

            var rows = AnalyticsManager.DrainKnotQueueForTests();
            Assert.AreEqual(1, rows.Count);
            Assert.AreEqual("attempt_end", rows[0].Kind);
            Assert.AreEqual("expired", rows[0].Result);
            Assert.AreEqual("live", rows[0].Source);
            Assert.AreEqual(250, rows[0].KnotLevel);
            Assert.AreEqual("grael", rows[0].PinnacleKey);
            Assert.AreEqual(ctx.AttemptId, rows[0].AttemptId);
            Assert.AreEqual(32, ctx.AttemptId.Length);
            Assert.AreEqual("Alice", rows[0].Name);
            Assert.AreEqual(77u, rows[0].AccountId);
            Assert.AreEqual(0x50000001u, rows[0].CharacterId);
            Assert.IsNotNull(rows[0].DurationSecs);
        }

        [TestMethod]
        public void Disabled_QueuesNothing()
        {
            AnalyticsManager.SetEnabledForTests(false);

            KnotTelemetry.RecordAttemptEnd(new PinnacleAttemptEnd(PinnacleFightState.Lost, "x", false, false, true, 1, 1, 1, 1, 1), null);
            AnalyticsManager.RecordKnotEvent(new AnalyticsDatabase.KnotEventRow { Kind = "k", Result = "r", Name = "n" });

            Assert.AreEqual(0, AnalyticsManager.DrainKnotQueueForTests().Count);
        }

        [TestMethod]
        public void FullQueue_DropsAndCounts()
        {
            for (var i = 0; i < AnalyticsManager.KnotEventQueueCapForTests; i++)
                AnalyticsManager.RecordKnotEvent(new AnalyticsDatabase.KnotEventRow { Kind = "k", Result = "r", Name = "n" });

            Assert.AreEqual(0, AnalyticsManager.KnotEventsDropped);

            AnalyticsManager.RecordKnotEvent(new AnalyticsDatabase.KnotEventRow { Kind = "k", Result = "r", Name = "n" });
            AnalyticsManager.RecordKnotEvent(new AnalyticsDatabase.KnotEventRow { Kind = "k", Result = "r", Name = "n" });

            Assert.AreEqual(2, AnalyticsManager.KnotEventsDropped);
            Assert.AreEqual(AnalyticsManager.KnotEventQueueCapForTests, AnalyticsManager.DrainKnotQueueForTests().Count);
        }

        [TestMethod]
        public void NullRowIsIgnored()
        {
            AnalyticsManager.RecordKnotEvent(null);
            Assert.AreEqual(0, AnalyticsManager.DrainKnotQueueForTests().Count);
        }

        // ---------------------------------------------------------------- failure isolation

        [TestMethod]
        public void NullInputs_NeverThrowOutOfRecord()
        {
            KnotTelemetry.RecordRoll(null, 0u, 1, 0, 0, false, false, null, 0, false, null, null);
            KnotTelemetry.RecordWeave(null, null, 1, "ok", "maelis", null);
            KnotTelemetry.RecordBind(null, null, null, null);
            KnotTelemetry.RecordCopyOpen(null, null, 1, null, null, 0);
            KnotTelemetry.RecordEntry(null, null, 0);
            KnotTelemetry.RecordKnotSpent(null, null, "won");
            KnotTelemetry.RecordAttemptEnd(null, null);

            Assert.AreEqual(0, AnalyticsManager.DrainKnotQueueForTests().Count, "a failed hook queues nothing");
        }

        [TestMethod]
        public void SeatWho_NameAccountAndLevelRules()
        {
            const uint owner = 0x50000001, other = 0x50000002;

            // owner seat: roster member wins, level falls back to the run's start level
            var o = KnotTelemetry.SeatWho(owner, owner, "Own", 200, "Own", 7, null, null, null);
            Assert.AreEqual("Own", o.Name);
            Assert.AreEqual(7u, o.AccountId);
            Assert.AreEqual(200, o.Level);

            // owner seat with no roster member: the run's owner name
            Assert.AreEqual("Own", KnotTelemetry.SeatWho(owner, owner, "Own", 200, null, null, null, null, null).Name);

            // non-owner offline: level NULL, never the owner's start level
            var s = KnotTelemetry.SeatWho(other, owner, "Own", 200, "Bob", 8, null, null, null);
            Assert.AreEqual("Bob", s.Name);
            Assert.AreEqual(8u, s.AccountId);
            Assert.IsNull(s.Level);

            // online: the live level wins, and fills a missing account
            var on = KnotTelemetry.SeatWho(other, owner, "Own", 200, "Bob", null, "Bob", 9, 301);
            Assert.AreEqual(301, on.Level);
            Assert.AreEqual(9u, on.AccountId);
        }

        [TestMethod]
        public void FiberLevelsOf_ThrowingInput_ReturnsNullAndNeverThrows()
        {
            static IEnumerable<ACE.Server.WorldObjects.WorldObject> Boom()
            {
                throw new InvalidOperationException("boom");
#pragma warning disable CS0162
                yield break;
#pragma warning restore CS0162
            }

            Assert.IsNull(KnotTelemetry.FiberLevelsOf(Boom()));

            AnalyticsManager.SetEnabledForTests(false);
            Assert.IsNull(KnotTelemetry.FiberLevelsOf(Boom()), "disabled: returns before touching the input");
        }


        [TestMethod]
        public void Wiring_DrainKnotEventsRunsInTheWorkerLoopAndTheFinalPass()
        {
            var loop = Body("Managers/Analytics/AnalyticsManager.cs", "private static void WorkerLoop(");
            AssertAfter(loop, "DrainDungeonRuns();", "DrainKnotEvents();");
            AssertAfter(loop, "DrainKnotEvents();", "DrainSessions();");

            var final = Body("Managers/Analytics/AnalyticsManager.cs", "internal static void FinalSessionPass(");
            AssertAfter(final, "while (!knotEventQueue.IsEmpty", "DrainKnotEvents();");
            AssertAfter(final, "DrainKnotEvents();", "if (sessionWriter == null)");
        }
        // ---------------------------------------------------------------- wiring (source scans)

        private static string Body(string file, string signature)
            => PooledLootSourceText.MethodBody(PooledLootSourceText.Read("Source/ACE.Server/" + file), signature);

        private static void AssertAfter(string body, string effect, string hook)
        {
            var e = body.IndexOf(effect, StringComparison.Ordinal);
            var h = body.IndexOf(hook, StringComparison.Ordinal);
            Assert.IsTrue(e >= 0, "effect missing: " + effect);
            Assert.IsTrue(h > e, $"hook '{hook}' must follow the effect '{effect}' (effect={e}, hook={h})");
        }

        [TestMethod]
        public void Wiring_RollHookFollowsTheSummaryLog()
        {
            var body = Body("Knots/KnotDrops.cs", "private static void RollSeat(");
            AssertAfter(body, "log.Info($\"[KNOTS] {run} seat 0x{seat:X8}", "KnotTelemetry.RecordRoll(run, seat,");
        }

        [TestMethod]
        public void Wiring_WeaveHooks()
        {
            var done = Body("Knots/KnotSeam.cs", "internal static void TryCompletePattern(");
            AssertAfter(done, "TryPlaceKnot(", "TryConsumeFromInventoryWithNetworking(pattern, 1)");
            AssertAfter(done, "TryConsumeFromInventoryWithNetworking(pattern, 1)", "log.Info($\"[KNOTS]");
            AssertAfter(done, "log.Info($\"[KNOTS]", "KnotTelemetry.RecordWeave(player, knot, level, KnotTelemetry.Result.Ok, KnotTelemetry.WeaveReasonPattern, null);");
            AssertAfter(done, "KnotTelemetry.RecordWeave(", "KnotTelemetry.RecordBind(player, knot, def, pattern);");
        }

        [TestMethod]
        public void Wiring_SeamHooks()
        {
            var handle = Body("Knots/KnotSeam.cs", "private static bool RunUse(");
            AssertAfter(handle, "Bind(player, knot, decision.Def);", "KnotTelemetry.RecordBind(player, knot, decision.Def, null);");

            // the bare consume statement stays bare; the record is on the NEXT line, unconditionally
            StringAssert.Contains(handle, "player.TryConsumeFromInventoryWithNetworking(knot);\n                    KnotTelemetry.RecordKnotSpent(player, knot, decision.ForfeitsAttempt ? KnotTelemetry.Result.Forfeit : KnotTelemetry.Result.NoEntries);");
            StringAssert.Contains(handle, "player.TryConsumeFromInventoryWithNetworking(knot);\n                KnotTelemetry.RecordEntry(player, knot, left, KnotTelemetry.Result.Seam);\n                KnotTelemetry.RecordKnotSpent(player, knot, KnotTelemetry.Result.LastEntry, left);");
            StringAssert.Contains(handle, "player.UpdateProperty(knot, PropertyInt.Structure, left);\n                KnotTelemetry.RecordEntry(player, knot, left, KnotTelemetry.Result.Seam);");
        }

        [TestMethod]
        public void Wiring_EncounterEventHooks()
        {
            var src = PooledLootSourceText.Read("Source/ACE.Server/Knots/KnotEncounterEvents.cs");

            AssertAfter(Body("Knots/KnotEncounterEvents.cs", "public static void OnBossKilled("), "TryConsumeFromInventoryWithNetworking(knot);", "KnotTelemetry.RecordKnotSpent(owner, knot, KnotTelemetry.Result.Won);");
            AssertAfter(Body("Knots/KnotEncounterEvents.cs", "public static int SpendEntry("), "owner.UpdateProperty(knot, PropertyInt.Structure, left);", "KnotTelemetry.RecordEntry(owner, knot, left);");

            var lost = Body("Knots/KnotEncounterEvents.cs", "public static void OnAttemptLost(");
            AssertAfter(lost, "TryConsumeFromInventoryWithNetworking(knot);", "KnotTelemetry.RecordKnotSpent(owner, knot, KnotTelemetry.Result.LostAtZero);");

            // SpendEntry down to OnAttemptLost must still hold no consume text
            var s = src.IndexOf("public static int SpendEntry(", StringComparison.Ordinal);
            var l = src.IndexOf("public static void OnAttemptLost(", StringComparison.Ordinal);
            Assert.IsFalse(src.Substring(s, l - s).Contains("TryConsumeFromInventoryWithNetworking"));
        }

        [TestMethod]
        public void Wiring_PinnacleHooks()
        {
            var copy = Body("Pinnacles/PinnacleEncounter.cs", "private bool CreateCopy(");
            AssertAfter(copy, "PinnacleFightManager.Add(new LiveFight(fight, world, landblock));", "KnotTelemetry.RecordCopyOpen(player, knotOrNull, knotLevel, def, attempt.AttemptId, landblock.Instance);");
            AssertAfter(copy, "KnotTelemetry.RecordCopyOpen(", "Teleport(player, arrival);");

            var end = Body("Pinnacles/PinnacleFight.cs", "private void End(");
            AssertAfter(end, "SafeLog($\"pinnacle: ended {outcome} ({reason})\");", "world.ReportEnded(new PinnacleAttemptEnd(");

            // expression-bodied, so scanned as a line: it hands straight to telemetry and touches nothing on the landblock
            var live = PooledLootSourceText.Read("Source/ACE.Server/Pinnacles/LivePinnacleWorld.cs");
            StringAssert.Contains(live, "public void ReportEnded(PinnacleAttemptEnd e) => KnotTelemetry.RecordAttemptEnd(e, attempt);");
        }
    }
}
