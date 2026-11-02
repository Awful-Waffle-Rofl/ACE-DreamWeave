using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

using ACE.Server.HazardMechanics;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ACE.Server.Tests.HazardMechanics
{
    /// <summary>
    /// The cross-mechanic contract: the balls, the wisps and the memory game were built separately from similar
    /// specs, and these tests hold the points on which they must stay identical (shared escape_speed, numeric
    /// refusal, one-line refusals, the dead-handle policy, the settings strings, and ONE source scan that covers every
    /// mechanic's files). Pure: no landblock, no PropertyManager (the PropertyManager and tsv files are read as text).
    /// </summary>
    [TestClass]
    public class HazardReconciliationTests
    {
        // ---- locating the repo files ---------------------------------------------------------------------------------

        private static string SourceRoot()
        {
            for (var d = new DirectoryInfo(AppContext.BaseDirectory); d != null; d = d.Parent)
            {
                var candidate = Path.Combine(d.FullName, "Source");

                if (Directory.Exists(Path.Combine(candidate, "ACE.Server", "HazardMechanics")) && File.Exists(Path.Combine(candidate, "config-defaults.tsv")))
                    return candidate;
            }

            throw new AssertFailedException("could not find Source/ACE.Server/HazardMechanics by walking up from " + AppContext.BaseDirectory);
        }

        private static string HazardDir() => Path.Combine(SourceRoot(), "ACE.Server", "HazardMechanics");

        private static string Read(string dir, string file) => File.ReadAllText(Path.Combine(dir, file));

        private static string[] Files(string pattern) => Directory.GetFiles(HazardDir(), pattern).Select(Path.GetFileName).OrderBy(f => f, StringComparer.Ordinal).ToArray();

        private static HazardRegion Region() => HazardRegionPresets.Pinnacle(0);

        // ---- (i) ONE source scan over every mechanic's files, each banned identifier bound -----------------------------

        /// <summary>APIs a mechanic must never touch: it goes through the context (clock, rng, damage, spawns, settings).</summary>
        private static readonly string[] Forbidden =
        {
            "PropertyManager", "DateTime", "new Random(", "WorldObjectFactory", "TakeDamage", "Stopwatch", "Environment.TickCount",
            "0x00", "GroundPlaneZ", "HazardPlaneZ",
        };

        /// <summary>The code lines of a source file: comment lines (// and ///) dropped, so doctrine text is not scanned.</summary>
        private static string CodeOnly(string text)
            => string.Join("\n", text.Split('\n').Where(l => !l.TrimStart().StartsWith("//", StringComparison.Ordinal)));

        private static List<string> Violations(string code)
            => Forbidden.Where(f => code.Contains(f, StringComparison.Ordinal)).ToList();

        /// <summary>mechanic kind -> the files it owns, found by name so a new file of a mechanic is scanned without editing this test.</summary>
        private static Dictionary<string, string[]> MechanicFiles() => new Dictionary<string, string[]>
        {
            // HazardRamp.cs is shared by both families, so it is scanned under both (and its keys, if it ever read any, would count for both)
            ["balls"] = Files("HazardBall*.cs").Concat(new[] { "RollingBallMechanic.cs", "HazardRamp.cs" }).ToArray(),
            ["wisps"] = Files("Wisp*.cs").Concat(new[] { "HazardRamp.cs" }).ToArray(),
            ["memory"] = Files("Memory*.cs"),
            ["fill"] = Files("Fill*.cs"),
            ["fog"] = Files("Fog*.cs"),
        };

        [TestMethod]
        public void EveryMechanicsFiles_AreFound_SoTheScanCannotPassOnAnEmptyList()
        {
            var files = MechanicFiles();

            Assert.IsTrue(files["balls"].Length >= 3, "RollingBallMechanic, HazardBallPatterns, HazardBallClock: " + string.Join(",", files["balls"]));
            Assert.IsTrue(files["wisps"].Length >= 6, "WispSquadMechanic, WispDirector, WispFormations, WispRules, WispScript, WispBurnBook: " + string.Join(",", files["wisps"]));
            Assert.IsTrue(files["memory"].Length >= 4, "MemoryMechanic, MemoryLayout, MemoryRules, MemoryWorld: " + string.Join(",", files["memory"]));
            Assert.IsTrue(files["fill"].Length >= 4, "FillMechanic, FillDirector, FillRules, FillSurface: " + string.Join(",", files["fill"]));
            Assert.IsTrue(files["fog"].Length >= 2, "FogMechanic, FogRules: " + string.Join(",", files["fog"]));

            foreach (var file in files.Values.SelectMany(f => f))
                Assert.IsTrue(CodeOnly(Read(HazardDir(), file)).Trim().Length > 200, file + " scanned as empty");
        }

        [TestMethod]
        public void EveryMechanicsSource_UsesNoForbiddenApi_AndNoLandblockOrZLiteral()
        {
            foreach (var kv in MechanicFiles())
            {
                foreach (var file in kv.Value)
                    CollectionAssert.AreEqual(new string[0], Violations(CodeOnly(Read(HazardDir(), file))), kv.Key + ": " + file);
            }
        }

        [TestMethod]
        public void SourceScan_BindsEveryBannedIdentifier_AndSkipsCommentLines()
        {
            foreach (var banned in Forbidden)
            {
                CollectionAssert.Contains(Violations("var x = " + banned + ";"), banned, "the scan must flag " + banned);
                Assert.AreEqual(0, Violations(CodeOnly("/// " + banned + " is only prose\nvar ok = 1;")).Count, banned + " in a comment line is not scanned");
            }

            // the comment filter drops whole lines only: trailing code after a comment on the SAME line is not hidden
            CollectionAssert.Contains(Violations(CodeOnly("var t = DateTime.UtcNow; // fine")), "DateTime");
        }

        [TestMethod]
        public void EachMechanic_SpawnsMovesAndDamagesOnlyThroughTheContext()
        {
            var dir = HazardDir();

            var balls = CodeOnly(Read(dir, "RollingBallMechanic.cs"));
            var wisps = CodeOnly(Read(dir, "WispSquadMechanic.cs"));
            var memory = CodeOnly(Read(dir, "MemoryWorld.cs"));

            foreach (var token in new[] { "ctx.Spawn(", "ctx.MoveLegs(", "ctx.HitPlayers(" })
            {
                StringAssert.Contains(balls, token, "balls");
                StringAssert.Contains(wisps, token, "wisps");
            }

            StringAssert.Contains(memory, "ctx.Spawn(");
            StringAssert.Contains(memory, "ctx.SpawnTimedEffect(");
            StringAssert.Contains(memory, "ctx.HitPlayers(");

            // the memory game damages only with its pad pillars, and only through the context (the world seam): no other
            // file of it calls a hit routine, and none reads pct itself (the context does; PctKey is only in the numeric-key list)
            foreach (var file in MechanicFiles()["memory"])
            {
                var code = CodeOnly(Read(dir, file));

                if (file != "MemoryWorld.cs")
                    Assert.IsFalse(code.Contains("HitPlayers", StringComparison.Ordinal), file + " must not damage anyone itself");

                Assert.IsFalse(code.Contains("GetDouble(\"pct\"", StringComparison.Ordinal) || code.Contains("GetDouble(HazardDefaults.PctKey", StringComparison.Ordinal), file + " must not read pct itself");
            }
        }

        // ---- (g) every telegraph and announce goes through ctx.Say with the [hazard #N] prefix ---------------------------

        private const string Prefix = "[hazard #{ctx.Run.Id}]";

        /// <summary>Every ctx.Say( call must carry the prefix inside its own statement (up to the first semicolon; a wrapped call is fine). Returns the offending snippets.</summary>
        private static List<string> UnprefixedSays(string code)
        {
            var bad = new List<string>();

            for (var i = code.IndexOf("ctx.Say(", StringComparison.Ordinal); i >= 0; i = code.IndexOf("ctx.Say(", i + 1, StringComparison.Ordinal))
            {
                // only the statement itself: up to the first ';' after the call
                var end = code.IndexOf(';', i);
                var statement = code.Substring(i, (end < 0 ? code.Length : end) - i);

                if (!statement.Contains(Prefix, StringComparison.Ordinal))
                    bad.Add(statement.Split('\n')[0].Trim());
            }

            return bad;
        }

        [TestMethod]
        public void EveryCtxSay_CarriesTheHazardPrefix_AndTheCheckerFlagsABareOne()
        {
            var dir = HazardDir();

            foreach (var file in new[] { "RollingBallMechanic.cs", "WispSquadMechanic.cs", "MemoryWorld.cs" })
            {
                var code = CodeOnly(Read(dir, file));

                Assert.IsTrue(code.Contains("ctx.Say(", StringComparison.Ordinal), file + " has no ctx.Say call: the check would pass vacuously");
                CollectionAssert.AreEqual(new string[0], UnprefixedSays(code), file);
            }

            Assert.AreEqual(1, UnprefixedSays("ctx.Say(\"bare\");").Count, "positive control");
            Assert.AreEqual(0, UnprefixedSays("ctx.Say($\"" + Prefix + " ok\");").Count);

            // a bare call right after a prefixed one must not borrow its prefix
            Assert.AreEqual(1, UnprefixedSays("ctx.Say($\"" + Prefix + " ok\"); ctx.Say(\"bare\");").Count, "adjacent bare call");
            Assert.AreEqual(1, UnprefixedSays("ctx.Say(\"bare\"); ctx.Say($\"" + Prefix + " ok\");").Count, "bare call before a prefixed one");
        }

        // ---- (a) escape_speed is ONE constant shared by the balls and the wisps -----------------------------------------

        [TestMethod]
        public void EscapeSpeed_IsOneKeyOneDefaultOneClamp_ForBallsAndWisps()
        {
            Assert.AreEqual("escape_speed", HazardDefaults.EscapeSpeedKey);
            Assert.AreEqual(3.0, HazardDefaults.EscapeSpeed);
            Assert.AreEqual(1.0, HazardDefaults.MinEscapeSpeed);
            Assert.AreEqual(12.0, HazardDefaults.MaxEscapeSpeed);

            Assert.AreEqual(HazardDefaults.EscapeSpeed, BallRules.Read(HazardArgs.Empty, BallPatterns.Row).EscapeSpeed);
            Assert.AreEqual(HazardDefaults.EscapeSpeed, WispSettings.From(HazardArgs.Empty).EscapeSpeed);

            foreach (var (raw, expected) in new[] { ("0.01", 1.0), ("-5", 1.0), ("99", 12.0), ("5", 5.0) })
            {
                var args = HazardArgs.Parse("escape_speed=" + raw);

                Assert.AreEqual(expected, BallRules.Read(args, BallPatterns.Row).EscapeSpeed, "balls " + raw);
                Assert.AreEqual(expected, WispSettings.From(args).EscapeSpeed, "wisps " + raw);
            }

            // spelled the same way in both settings strings
            Assert.AreEqual("3.0", HazardArgs.Parse(HazardDefaults.Balls).GetString("escape_speed"));
            Assert.AreEqual("3.0", HazardArgs.Parse(HazardDefaults.Wisps).GetString("escape_speed"));
        }

        // ---- (b) the dead-handle policy ------------------------------------------------------------------------------

        [TestMethod]
        public void BallWave_AllLost_IsAborted_ButAPartialLossCarriesOn()
        {
            // every ball died
            var all = new BallWaveClock(2);
            all.StartWave(2);
            all.OnSpawned(1);
            all.OnSpawned(2);
            Assert.IsFalse(all.WaveAllLost, "balls still in the air: the wave is not over");
            all.OnDied(1);
            all.OnDied(2);
            Assert.IsTrue(all.WaveOver);
            Assert.IsTrue(all.WaveAllLost);

            // none ever spawned
            var none = new BallWaveClock(1);
            none.StartWave(2);
            none.OnSpawnFailed();
            none.OnSpawnFailed();
            Assert.IsTrue(none.WaveAllLost);

            // one finished its leg, one died: carry on
            var some = new BallWaveClock(2);
            some.StartWave(2);
            some.OnSpawned(1);
            some.OnSpawned(2);
            some.OnLaunched(1);
            some.OnLegEnd(1);
            some.OnDied(2);
            Assert.IsTrue(some.WaveOver);
            Assert.IsFalse(some.WaveAllLost);
            Assert.AreEqual(BallWaveStep.StartNextWave, some.NextStep());

            // the next wave starts clean
            some.StartWave(1);
            Assert.IsFalse(some.WaveAllLost, "a new wave forgets the last one");
        }

        [TestMethod]
        public void TheThreeMechanics_AbortOnLostContent_AndFailOnlyForPlayerFailure()
        {
            // balls: a wave whose every ball is proven lost, and an empty plan, are Aborted, never Completed or Failed
            var all = new BallWaveClock(2);
            all.StartWave(2);
            all.OnSpawned(1);
            all.OnSpawned(2);
            all.OnDied(1);
            all.OnDied(2);
            var lost = BallVerdicts.WaveVerdict(all.Close());
            Assert.AreEqual(HazardOutcome.Aborted, lost.Value.Outcome);
            StringAssert.Contains(lost.Value.Reason, "all balls lost");

            Assert.AreEqual(HazardOutcome.Aborted, BallVerdicts.EmptyPlan(1).Outcome);
            StringAssert.Contains(BallVerdicts.EmptyPlan(1).Reason, "no routes");

            // wisps: no live wisp on a tick is Aborted
            var script = new WispScript(WispSettings.From(HazardArgs.Empty));
            script.Advance(0.1, 1);
            var end = script.Advance(0.2, 0).Single(a => a.Kind == WispActionKind.End);
            Assert.AreEqual(HazardOutcome.Aborted, end.Outcome);
            Assert.AreEqual("all wisps lost", end.Reason);
        }

        [TestMethod]
        public void BallVerdicts_CompletedAfterTheLastWave_ContinueBetweenWaves_AbortedOnlyOnProvenLoss()
        {
            var clock = new BallWaveClock(2);

            clock.StartWave(2);
            clock.OnSpawned(1);
            clock.OnSpawned(2);
            Assert.IsNull(BallVerdicts.WaveVerdict(clock.Close()), "balls in the air: wait");

            clock.OnLaunched(1);
            clock.OnLaunched(2);
            clock.OnLegEnd(1);
            clock.OnLegEnd(2);
            Assert.IsNull(BallVerdicts.WaveVerdict(clock.Close()), "wave 1 of 2 finished: the next wave starts");

            clock.StartWave(2);
            clock.OnSpawned(3);
            clock.OnSpawned(4);
            clock.OnLaunched(3);
            clock.OnLegEnd(3);
            clock.OnDied(4);

            var done = BallVerdicts.WaveVerdict(clock.Close());
            Assert.AreEqual(HazardOutcome.Completed, done.Value.Outcome, "one lost ball of the last wave still completes");
            StringAssert.Contains(done.Value.Reason, "1 ball(s) lost");
        }

        [TestMethod]
        public void TheAllLostSnapshot_IsTakenBeforeTheNextWaveResetsTheClock()
        {
            var clock = new BallWaveClock(3);

            clock.StartWave(1);
            clock.OnSpawned(1);
            clock.OnDied(1);

            var result = clock.Close();
            Assert.IsTrue(result.AllLost);

            // the next wave resets the per-wave state: a verdict read from the CLOCK afterwards is wrong, from the snapshot it is not
            clock.StartWave(2);
            Assert.IsFalse(clock.WaveAllLost, "after the reset the clock no longer knows the last wave was lost");
            Assert.AreEqual(HazardOutcome.Aborted, BallVerdicts.WaveVerdict(result).Value.Outcome, "the snapshot still does");

            // and the mechanic decides from the snapshot only
            var code = CodeOnly(Read(HazardDir(), "RollingBallMechanic.cs"));
            StringAssert.Contains(code, "clock.Close()");
            StringAssert.Contains(code, "BallVerdicts.WaveVerdict(result)");
            StringAssert.Contains(code, "BallVerdicts.EmptyPlan(");
            Assert.IsFalse(code.Contains("WaveAllLost", StringComparison.Ordinal), "the mechanic must not read the clock's live flag");
        }

        [TestMethod]
        public void ALagSpikeFallbackWriteOff_FinishesTheWave_ButDoesNotAbort()
        {
            // two balls launched, no leg end ever reported (a lag spike): the fallback writes both off
            var clock = new BallWaveClock(1);
            clock.StartWave(2);
            clock.OnSpawned(1);
            clock.OnSpawned(2);
            clock.OnLaunched(1);
            clock.OnLaunched(2);

            Assert.AreEqual(2, clock.ForceResolve(), "both outstanding balls are written off");
            Assert.AreEqual(2, clock.Lost, "counted lost for the log");

            var result = clock.Close();
            Assert.IsTrue(clock.WaveOver, "the wave is finished");
            Assert.IsFalse(result.AllLost, "a timeout is not proof the balls were lost");
            Assert.AreEqual(HazardOutcome.Completed, BallVerdicts.WaveVerdict(result).Value.Outcome);
            StringAssert.Contains(BallVerdicts.WaveVerdict(result).Value.Reason, "2 ball(s) lost, 2 written off by timeout");
            Assert.AreEqual(2, clock.WrittenOff);

            // balls that never reported a spawn at all are written off too, and also do not abort
            var silent = new BallWaveClock(1);
            silent.StartWave(3);
            silent.OnSpawned(1);
            silent.OnLaunched(1);
            Assert.AreEqual(3, silent.ForceResolve(), "one rolling, two that never reported");
            Assert.IsFalse(silent.Close().AllLost);

            // a mix of proven deaths and a write-off is still not "all proven lost"
            var mix = new BallWaveClock(1);
            mix.StartWave(2);
            mix.OnSpawned(1);
            mix.OnSpawned(2);
            mix.OnDied(1);
            mix.ForceResolve();
            Assert.IsFalse(mix.Close().AllLost);
        }
        // ---- (f) a non-numeric numeric arg is REFUSED by every mechanic, never silently defaulted -------------------------

        private static string Validate(string kind, string extra, string pattern = null)
        {
            var region = Region();
            var args = HazardArgs.Merge(HazardArgs.Parse(extra), HazardArgs.Parse(DefaultsOf(kind)));

            switch (kind)
            {
                case "balls": return new RollingBallMechanic().Validate(region, args, pattern ?? BallPatterns.Row);
                case "wisps": return new WispSquadMechanic().Validate(region, args, pattern ?? WispRules.Scatter);
                case "fill": return new FillMechanic().Validate(region, args, pattern);
                case "fog": return new FogMechanic().Validate(region, args, pattern);
                default: return new MemoryMechanic().Validate(region, args, pattern);
            }
        }

        private static string DefaultsOf(string kind) => kind == "balls" ? HazardDefaults.Balls : kind == "wisps" ? HazardDefaults.Wisps : kind == "memory" ? HazardDefaults.Memory : "";

        [TestMethod]
        public void Balls_RefuseANonNumericNumber_WithOneLine()
        {
            foreach (var (bad, key) in new[] { ("speed=fast", "speed"), ("lead=soon", "lead"), ("gap=NaN", "gap"), ("escape_speed=x", "escape_speed"), ("pct=lots", "pct"), ("ball_z=up", "ball_z"), ("waves=2.5", "waves"), ("count=many", "count") })
            {
                var error = Validate("balls", bad);

                Assert.IsNotNull(error, bad);
                StringAssert.Contains(error, key, bad);
                StringAssert.Contains(error, "is not a", bad);
                Assert.IsFalse(error.Contains('\n'), bad);
            }

            Assert.IsNull(Validate("balls", "speed=4 waves=2"), "positive control: good numbers are accepted");
        }

        [TestMethod]
        public void Wisps_RefuseANonNumericNumber_WithOneLine()
        {
            foreach (var (bad, key) in new[] { ("hold=slow", "hold"), ("n=3.5", "n"), ("escape_speed=x", "escape_speed"), ("pct=lots", "pct"), ("cycles=few", "cycles") })
            {
                var error = Validate("wisps", bad);

                Assert.IsNotNull(error, bad);
                StringAssert.Contains(error, key, bad);
                StringAssert.Contains(error, "is not a", bad);
                Assert.IsFalse(error.Contains('\n'), bad);
            }

            Assert.IsNull(Validate("wisps", "hold=2 n=4"), "positive control");
        }

        [TestMethod]
        public void Memory_RefusesANonNumericNumber_WithOneLine()
        {
            foreach (var (bad, key) in new[] { ("answer_timeout=soon", "answer_timeout"), ("len=two", "len"), ("pad_gap=wide", "pad_gap"), ("beams_per_pulse=1.5", "beams_per_pulse"), ("pulse_on=NaN", "pulse_on") })
            {
                var error = Validate("memory", bad);

                Assert.IsNotNull(error, bad);
                StringAssert.Contains(error, key, bad);
                StringAssert.Contains(error, "is not a", bad);
                Assert.IsFalse(error.Contains('\n'), bad);
            }

            StringAssert.Contains(Validate("memory", "layout=spiral"), "layout");
            Assert.IsNull(Validate("memory", "answer_timeout=9 len=2"), "positive control: the Pinnacle holds four pads");
        }

        // ---- choice keys: a typo is refused, one line each -----------------------------------------------------------------

        [TestMethod]
        public void Balls_RefuseAnInvalidMarker_AndAcceptAValidOne()
        {
            var error = Validate("balls", "marker=maybe");

            StringAssert.Contains(error, "marker");
            Assert.IsFalse(error.Contains('\n'));

            foreach (var ok in new[] { "marker=true", "marker=false", "marker=0", "marker=ON" })
                Assert.IsNull(Validate("balls", ok), ok);
        }

        [TestMethod]
        public void Wisps_RefuseAnUnparseableScript_AndAcceptDecimalAndHex()
        {
            var error = Validate("wisps", "script=flames");

            StringAssert.Contains(error, "script");
            Assert.IsFalse(error.Contains('\n'));

            Assert.IsNull(Validate("wisps", "script=0x33001028"));
            Assert.IsNull(Validate("wisps", "script=855642152"));
        }

        // ---- a settings-row typo names its setting ---------------------------------------------------------------------

        [TestMethod]
        public void ABadSettingsRow_WithEmptyCallerArgs_NamesTheSetting_ButACallersOwnTypoDoesNot()
        {
            foreach (var (kind, row, key) in new[] { ("balls", "speed=fast waves=2", "speed"), ("wisps", "hold=slow n=4", "hold"), ("memory", "len=two max_len=8", "len") })
            {
                var merged = HazardArgs.Merge(HazardArgs.Empty, HazardArgs.Parse(row));
                var error = Validate(kind, row);

                Assert.IsNotNull(error, kind);

                var hinted = HazardManager.WithSettingHint(error, kind, HazardArgs.Empty, merged);
                StringAssert.Contains(hinted, "hazard_mech_" + kind, kind);
                StringAssert.StartsWith(hinted, error);

                // the caller typed the key itself: no hint, the message is already about their input
                var caller = HazardArgs.Parse(key + "=" + merged.GetString(key));
                Assert.AreEqual(error, HazardManager.WithSettingHint(error, kind, caller, HazardArgs.Merge(caller, HazardArgs.Parse(row))), kind);
            }

            // an error that names no key is left alone
            Assert.AreEqual("no region", HazardManager.WithSettingHint("no region", "balls", HazardArgs.Empty, HazardArgs.Parse("speed=3")));
        }

        [TestMethod]
        public void TheHint_NamesEverySettingsOnlyKeyTheErrorNames_ButNotACallersOrAQuotedOne()
        {
            // settings len=4, caller max_len=2: "len" is named, "max_len" is the caller's
            var merged = HazardArgs.Merge(HazardArgs.Parse("max_len=2"), HazardArgs.Parse("len=4 max_len=8"));
            var hinted = HazardManager.WithSettingHint("len=4 is longer than max_len=2: the game would be over", "memory", HazardArgs.Parse("max_len=2"), merged);
            StringAssert.EndsWith(hinted, " (len from setting hazard_mech_memory)");
            Assert.IsFalse(hinted.Contains("max_len from"), "max_len was typed by the caller");

            // two settings-only keys are both named
            var both = HazardManager.WithSettingHint("gap 6 is too narrow for speed 3", "balls", HazardArgs.Empty, HazardArgs.Parse("speed=3 gap=6"));
            StringAssert.EndsWith(both, " (gap, speed from setting hazard_mech_balls)");

            // the caller typed dir=speed; "speed" is only a quoted VALUE in the message, so the settings row is not blamed
            var quoted = "dir='speed' is not one of n|s|e|w|random";
            Assert.AreEqual(quoted, HazardManager.WithSettingHint(quoted, "wisps", HazardArgs.Parse("dir=speed"), HazardArgs.Merge(HazardArgs.Parse("dir=speed"), HazardArgs.Parse("speed=3 hold=1.5"))));

            // one-letter keys only count as key=
            Assert.AreEqual("a run needs 1 wisp", HazardManager.WithSettingHint("a run needs 1 wisp", "wisps", HazardArgs.Empty, HazardArgs.Parse("n=4")));
            StringAssert.Contains(HazardManager.WithSettingHint("n=0: a run needs at least 1 wisp", "wisps", HazardArgs.Empty, HazardArgs.Parse("n=4")), "n from setting");
        }

        private sealed class ScriptedMechanic : IHazardMechanic
        {
            public Action OnTick = () => { };
            public Func<HazardArgs, string> OnValidate = _ => null;
            public HazardArgs SeenByValidate;

            public string Kind => "balls";
            public IReadOnlyList<string> Patterns => Array.Empty<string>();
            public IEnumerable<uint> RequiredWcids(HazardArgs args) => Array.Empty<uint>();
            public string Validate(HazardRegion region, HazardArgs args, string pattern) { SeenByValidate = args; return OnValidate(args); }
            public void Start(HazardContext ctx) { }
            public void Tick(HazardContext ctx) => OnTick();
            public void OnStop(HazardContext ctx, HazardOutcome outcome) { }
            public string Describe(HazardContext ctx) => "";
            public string Reveal(HazardContext ctx) => null;
        }

        [TestMethod]
        public void ValidateWithHint_PassesTheCallersArgsToTheHint_NotEmpty()
        {
            var mechanic = new ScriptedMechanic { OnValidate = _ => "speed=fast is not a number" };
            var merged = HazardArgs.Parse("speed=fast waves=2");

            // the settings row alone supplied speed: hinted
            StringAssert.EndsWith(HazardManager.ValidateWithHint(mechanic, Region(), HazardArgs.Parse("waves=3"), merged, null), " (speed from setting hazard_mech_balls)");
            Assert.AreSame(merged, mechanic.SeenByValidate, "Validate sees the merged args");

            // the caller supplied speed: not hinted. With Empty passed instead of the caller's args this would be hinted.
            Assert.AreEqual("speed=fast is not a number", HazardManager.ValidateWithHint(mechanic, Region(), HazardArgs.Parse("speed=fast"), merged, null));
        }

        [TestMethod]
        public void AMechanicWhoseTickThrows_EndsAborted_ThroughTickRun()
        {
            var mechanic = new ScriptedMechanic { OnTick = () => throw new InvalidOperationException("boom") };
            var run = HazardManager.RegisterForTest("balls", mechanic, Region(), null, DateTime.UtcNow);

            try
            {
                HazardManager.TickRun(run, DateTime.UtcNow); // Start
                Assert.IsFalse(run.IsEnded);

                HazardManager.TickRun(run, DateTime.UtcNow); // Tick throws

                Assert.IsTrue(run.IsEnded);
                Assert.AreEqual(HazardOutcome.Aborted, run.Outcome);
                StringAssert.Contains(run.EndReason, "mechanic threw InvalidOperationException");
            }
            finally
            {
                HazardManager.Stop(run, HazardOutcome.Stopped, "test cleanup");
            }
        }

        // ---- (e) one-line refusals, no exceptions ----------------------------------------------------------------------

        [TestMethod]
        public void Validate_NeverThrows_AndRefusesInOneLine_ForAnyGarbage()
        {
            var garbage = new[]
            {
                "", "speed=-1 waves=0", "n=0", "hold=0", "len=99 max_len=1", "pad_gap=0.1", "pad_radius=99", "escape_speed=0",
                "pillar_radius=999", "gap=0.01 count=0", "edge_inset=500", "cx=1", "dir=up", "fire=maybe", "layout=x", "n=2147483648",
            };

            foreach (var kind in new[] { "balls", "wisps", "memory", "fill", "fog" })
            {
                foreach (var g in garbage)
                {
                    string error = null;

                    try
                    {
                        error = Validate(kind, g);
                    }
                    catch (Exception ex)
                    {
                        Assert.Fail($"{kind} Validate('{g}') threw {ex.GetType().Name}: {ex.Message}");
                    }

                    if (error != null)
                        Assert.IsFalse(error.Contains('\n') || error.Contains('\r'), $"{kind} '{g}' refused in more than one line: {error}");
                }

                // a missing region or missing args is a refusal, not a crash
                IHazardMechanic mechanic = kind == "balls" ? new RollingBallMechanic() : kind == "wisps" ? (IHazardMechanic)new WispSquadMechanic() : kind == "fill" ? new FillMechanic() : kind == "fog" ? (IHazardMechanic)new FogMechanic() : new MemoryMechanic();
                var pattern = kind == "balls" ? BallPatterns.Row : kind == "wisps" ? WispRules.Scatter : null;

                Assert.IsNotNull(mechanic.Validate(null, HazardArgs.Empty, pattern), kind + " null region");
                mechanic.Validate(Region(), null, pattern);
            }
        }

        // ---- (j) the settings strings and the PropertyManager descriptions list every key the mechanic reads --------------

        private static readonly Regex KeyCall = new Regex(@"\.(?:GetDouble|GetInt|GetChoice|GetBool|GetString|Has)\(\s*(?:""(?<lit>[a-z_]+)""|HazardDefaults\.(?<const>\w+Key))", RegexOptions.Compiled);

        private static Dictionary<string, string> DefaultKeyConstants()
        {
            var map = new Dictionary<string, string>();

            foreach (Match m in Regex.Matches(Read(HazardDir(), "HazardDefaults.cs"), @"public const string (?<name>\w+Key) = ""(?<value>[a-z_]+)"""))
                map[m.Groups["name"].Value] = m.Groups["value"].Value;

            return map;
        }

        /// <summary>Every args key a mechanic's own files read (the framework's pct, read by the context, added for all three: the memory game damages with pad_pillar).</summary>
        private static SortedSet<string> KeysRead(string kind)
        {
            var constants = DefaultKeyConstants();
            var keys = new SortedSet<string>(StringComparer.Ordinal);

            foreach (var file in MechanicFiles()[kind])
            {
                foreach (Match m in KeyCall.Matches(CodeOnly(Read(HazardDir(), file))))
                    keys.Add(m.Groups["lit"].Success ? m.Groups["lit"].Value : constants[m.Groups["const"].Value]);
            }

            keys.Add(HazardDefaults.PctKey);

            return keys;
        }

        private static string PropertyDescription(string setting)
        {
            var line = File.ReadAllLines(Path.Combine(SourceRoot(), "ACE.Server", "Managers", "PropertyManager.cs"))
                .Single(l => l.Contains("(\"" + setting + "\"", StringComparison.Ordinal));

            return line;
        }

        [TestMethod]
        public void KeyExtraction_FindsTheKeys_SoTheDescriptionCheckCannotPassVacuously()
        {
            var balls = KeysRead("balls");
            var wisps = KeysRead("wisps");
            var memory = KeysRead("memory");

            foreach (var key in new[] { "speed", "lead", "gap", "waves", "wave_gap", "window", "escape_speed", "count", "dir", "dirs", "cx", "cy", "target", "spread", "ball_z", "ball_radius", "marker", "pct", "speed_end", "lead_end", "wave_gap_end", "stream", "hit_lag", "occupancy" })
                CollectionAssert.Contains(balls.ToList(), key, "balls should read " + key);

            foreach (var key in new[] { "n", "move", "hold", "burst", "rest", "cycles", "pillar_radius", "escape_speed", "fire", "dir", "pct", "edge_inset", "player_radius", "move_end", "hold_end", "rest_end", "hold_floor", "follow", "follow_lag" })
                CollectionAssert.Contains(wisps.ToList(), key, "wisps should read " + key);

            foreach (var key in new[] { "len", "max_len", "beams_per_pulse", "pulse_on", "pulse_off", "answer_timeout", "pad_radius", "pad_gap", "pad_spacing", "layout", "edge_inset", "beam_from", "beam_flip", "beam_corners", "pad_pillar", "pct" })
                CollectionAssert.Contains(memory.ToList(), key, "memory should read " + key);
        }

        [TestMethod]
        public void EveryKeyAMechanicReads_IsDocumentedInItsPropertyDescription()
        {
            foreach (var kind in new[] { "balls", "wisps", "memory" })
            {
                var description = PropertyDescription("hazard_mech_" + kind);

                foreach (var key in KeysRead(kind))
                    Assert.IsTrue(Regex.IsMatch(description, "(?<![a-z_])" + Regex.Escape(key) + "(?![a-z_])"), $"hazard_mech_{kind} does not document '{key}'");
            }
        }

        [TestMethod]
        public void EveryKeyInASettingsString_IsReadByItsMechanic()
        {
            foreach (var (kind, value) in new[] { ("balls", HazardDefaults.Balls), ("wisps", HazardDefaults.Wisps), ("memory", HazardDefaults.Memory) })
            {
                var read = KeysRead(kind);

                foreach (var key in HazardArgs.Parse(value).Keys)
                    Assert.IsTrue(read.Contains(key), $"hazard_mech_{kind} lists '{key}', which the mechanic never reads");
            }
        }

        [TestMethod]
        public void SettingsStrings_AgreeByteForByte_AcrossHazardDefaults_PropertyManager_AndTheTsv()
        {
            var tsv = File.ReadAllLines(Path.Combine(SourceRoot(), "config-defaults.tsv"));

            foreach (var (kind, constant, name) in new[] { ("balls", HazardDefaults.Balls, "Balls"), ("wisps", HazardDefaults.Wisps, "Wisps"), ("memory", HazardDefaults.Memory, "Memory"), ("beams", HazardDefaults.Beams, "Beams") })
            {
                var setting = "hazard_mech_" + kind;
                var row = tsv.Single(l => l.StartsWith(setting + "\t", StringComparison.Ordinal)).Split('\t');

                Assert.AreEqual("string", row[1], setting);
                Assert.AreEqual(constant, row[2], setting + " tsv value column");
                Assert.AreEqual(constant, row[3], setting + " tsv default column");

                StringAssert.Contains(PropertyDescription(setting), "HazardDefaults." + name + ",", setting + " PropertyManager default must be the HazardDefaults constant");
            }
        }

        [TestMethod]
        public void EveryDamagingMechanic_SharesThePctDefault()
        {
            Assert.AreEqual("0.60", HazardArgs.Parse(HazardDefaults.Balls).GetString("pct"));
            Assert.AreEqual("0.60", HazardArgs.Parse(HazardDefaults.Wisps).GetString("pct"));
            Assert.IsFalse(HazardArgs.Parse(HazardDefaults.Memory).Has("pct"), "memory ships no pct; the context default 0.60 applies when pad_pillar=1");
            Assert.AreEqual(0.60, HazardDefaults.DamagePct);
        }

        [TestMethod]
        public void TheWispHit_IsOnePerSourcePerPlayer_LikeTheBalls()
        {
            Assert.IsTrue(double.IsPositiveInfinity(WispSettings.From(HazardArgs.Empty).HitWindow));
            Assert.IsTrue(double.IsPositiveInfinity(RollingBallMechanic.HitWindowSeconds));
        }

        // ---- (h) the usage text lists every mechanic's patterns and args --------------------------------------------------

        [TestMethod]
        public void TheUsageText_ListsEveryMechanicsPatterns_AndEveryKeyItsDescriptionDocumentsInTheSameShape()
        {
            var usage = Read(Path.Combine(SourceRoot(), "ACE.Server", "Command", "Handlers"), "HazardCommands.cs");

            foreach (var kind in new[] { "balls", "wisps", "memory", "beams", "fill", "fog" })
                StringAssert.Contains(usage, "/hazard " + kind, kind);

            foreach (var pattern in BallPatterns.All)
                StringAssert.Contains(usage, pattern, "balls pattern " + pattern);

            foreach (var pattern in WispRules.Patterns)
                StringAssert.Contains(usage, pattern, "wisps pattern " + pattern);

            // each of the three real mechanics has a header line and an "args:" line, in that shape
            foreach (var kind in new[] { "balls", "wisps", "memory", "fill", "fog" })
            {
                var lines = usage.Split('\n');
                var header = Array.FindIndex(lines, l => l.Contains("\"  /hazard " + kind, StringComparison.Ordinal));

                Assert.IsTrue(header >= 0, kind + " header");
                StringAssert.Contains(lines[header + 1], "      args: ", kind + " args line follows the header");
            }

            // every key the mechanic reads and that is specific to it appears on its args line
            foreach (var kind in new[] { "balls", "wisps", "memory", "fill", "fog" })
            {
                var lines = usage.Split('\n');
                var argsAt = Array.FindIndex(lines, l => l.Contains("\"  /hazard " + kind, StringComparison.Ordinal)) + 1;
                var args = lines[argsAt];

                // an args line over the 250-character chat limit continues on "args+: " lines directly below it (each its own chat line)
                for (var next = argsAt + 1; next < lines.Length && lines[next].Contains("\"      args+: ", StringComparison.Ordinal); next++)
                {
                    var shown = Regex.Match(lines[next], "\"(?<text>.*)\\\\n\"").Groups["text"].Value; // the literal's text, without the quotes and the \n
                    Assert.IsTrue(shown.Length > 0 && shown.Length <= 250, kind + " args+ line is " + shown.Length + " characters; AC chat cuts lines at 255");
                    args += " " + lines[next];
                }

                foreach (var key in KeysRead(kind).Except(new[] { "pct", "player_radius", "edge_inset", "ball_wcid", "wisp_wcid", "script", "pattern" }))
                    Assert.IsTrue(Regex.IsMatch(args, "(?<![a-z_])" + Regex.Escape(key) + "="), $"usage for {kind} does not list '{key}'");
            }
        }
    }
}
