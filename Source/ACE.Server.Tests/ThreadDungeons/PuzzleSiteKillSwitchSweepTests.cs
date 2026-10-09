using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Text.RegularExpressions;

using ACE.Server.PuzzleGates;
using ACE.Server.ThreadDungeons;
using ACE.Server.ThreadDungeons.Defs;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ACE.Server.Tests.ThreadDungeons
{
    /// <summary>
    /// The per-site kill switch ("disabled" / "rewardDisabled" in puzzle-gates.json: loader, picker, selector, tour tag)
    /// and puzzlegate-sweep's pure half (PuzzleSiteSweep's checks a-e over a recorded placement, the TSV row, the
    /// disable decision, and PuzzleSiteKillSwitch's byte-preserving text edit). No world, no dat, no database.
    /// </summary>
    [TestClass]
    public class PuzzleSiteKillSwitchSweepTests
    {
        private static readonly PuzzleGateType[] AllTypes = { PuzzleGateType.Sigil, PuzzleGateType.Beam, PuzzleGateType.Odd, PuzzleGateType.Shuffle };

        private static int nextSite;

        /// <summary>A site 40 m from the last, so the picker's spacing rule never interferes.</summary>
        private static PuzzleSiteDef Site(string id, PuzzleSiteKind kind = PuzzleSiteKind.Gate, bool disabled = false, bool rewardDisabled = false)
        {
            var x = 40f * System.Threading.Interlocked.Increment(ref nextSite);

            return new PuzzleSiteDef
            {
                Id = id,
                Kind = kind,
                Anchor = new PuzzleSitePointDef { Cell = 0x0150018A, X = x, Y = 10, Z = 0 },
                MaxN = 5,
                Types = AllTypes.ToList(),
                GateModel = new PuzzleGateModelDef { Kind = PuzzleGateModelKind.Door, Wcid = 1006850u, Scale = 1f, Panels = 1 },
                ShuffleSpots = Enumerable.Range(0, 2).Select(i => new PuzzleSitePointDef { Cell = 0x0150018A, X = x + i, Y = 0, Z = 0 }).ToList(),
                Disabled = disabled,
                RewardDisabled = rewardDisabled,
                DisabledReason = disabled || rewardDisabled ? "sweep" : null,
            };
        }

        // =============================================================================================
        // Kill switch: who honours it
        // =============================================================================================

        [TestMethod]
        public void A_disabled_site_is_never_picked_as_a_gate_and_the_control_is()
        {
            for (var seed = 1; seed <= 200; seed++)
            {
                var off = Site("off", disabled: true);
                var on = Site("on");
                var picks = ThreadPuzzleSitePicker.Pick(new[] { off, on }, 2, false, seed);

                Assert.IsFalse(picks.Any(p => p.Site.Id == "off"), $"seed {seed}: the disabled site was picked");
                Assert.IsTrue(picks.Any(p => p.Site.Id == "on" && !p.IsReward), $"seed {seed}: control, the enabled site is picked");
            }
        }

        [TestMethod]
        public void A_reward_disabled_gate_site_is_still_a_gate()
        {
            var picks = ThreadPuzzleSitePicker.Pick(new[] { Site("gate-only", rewardDisabled: true) }, 1, false, 5);

            Assert.AreEqual(1, picks.Count(p => p.Site.Id == "gate-only" && !p.IsReward));
        }

        [TestMethod]
        public void The_populate_time_reward_pick_skips_disabled_and_reward_disabled_reward_sites()
        {
            for (var seed = 1; seed <= 50; seed++)
            {
                Assert.AreEqual(0, ThreadPuzzleSitePicker.Pick(new[] { Site("r", PuzzleSiteKind.Reward, disabled: true) }, 0, true, seed).Count(p => p.IsReward), "disabled");
                Assert.AreEqual(0, ThreadPuzzleSitePicker.Pick(new[] { Site("r", PuzzleSiteKind.Reward, rewardDisabled: true) }, 0, true, seed).Count(p => p.IsReward), "reward-disabled");
                Assert.AreEqual(1, ThreadPuzzleSitePicker.Pick(new[] { Site("r", PuzzleSiteKind.Reward) }, 0, true, seed).Count(p => p.IsReward), "control");
            }
        }

        [TestMethod]
        public void The_arming_selector_skips_switched_off_sites_and_the_curated_fallback_honours_the_switch()
        {
            Assert.IsFalse(ThreadPuzzleRewardSelector.IsCandidate(Site("a", PuzzleSiteKind.Reward, disabled: true), null));
            Assert.IsFalse(ThreadPuzzleRewardSelector.IsCandidate(Site("b", PuzzleSiteKind.Reward, rewardDisabled: true), null));
            Assert.IsFalse(ThreadPuzzleRewardSelector.IsCandidate(Site("c", PuzzleSiteKind.Gate, rewardDisabled: true), null), "a gate site disabled for the reward role");
            Assert.IsTrue(ThreadPuzzleRewardSelector.IsCandidate(Site("d", PuzzleSiteKind.Reward), null), "control");

            var offSite = Site("curated-off", PuzzleSiteKind.Reward, disabled: true);
            var off = new ThreadPuzzlePick(offSite, PuzzleGateType.Sigil, 4, 1, true);
            Assert.IsNull(ThreadPuzzleRewardSelector.Select(new[] { offSite }, null, off, 6, null), "a disabled curated pick is not the fallback");

            var onSite = Site("curated-on", PuzzleSiteKind.Reward);
            var on = new ThreadPuzzlePick(onSite, PuzzleGateType.Sigil, 4, 1, true);
            var choice = ThreadPuzzleRewardSelector.Select(new[] { onSite }, null, on, 6, null);
            Assert.IsNotNull(choice, "control");
            Assert.IsTrue(choice.CuratedFallback);
        }

        [TestMethod]
        public void The_curated_fallback_reads_its_site_from_the_current_list_so_a_reload_disable_holds()
        {
            // Populate picked "r" while it was enabled; a /dd reload since replaced the site objects.
            var atPopulate = Site("r", PuzzleSiteKind.Reward);
            var curated = new ThreadPuzzlePick(atPopulate, PuzzleGateType.Sigil, 4, 1, true);

            var reloadedOff = Site("r", PuzzleSiteKind.Reward, disabled: true);
            Assert.IsNull(ThreadPuzzleRewardSelector.Select(new[] { reloadedOff }, null, curated, 6, null), "disabled by the reload: the stale pick's own site still says enabled");

            var reloadedRewardOff = Site("r", PuzzleSiteKind.Reward, rewardDisabled: true);
            Assert.IsNull(ThreadPuzzleRewardSelector.Select(new[] { reloadedRewardOff }, null, curated, 6, null), "reward-disabled by the reload");

            Assert.IsNull(ThreadPuzzleRewardSelector.Select(new[] { Site("other", PuzzleSiteKind.Reward) }, null, curated, 6, null), "the id is gone from the file: not usable");

            var reloadedOn = Site("r", PuzzleSiteKind.Reward);
            var choice = ThreadPuzzleRewardSelector.Select(new[] { reloadedOn }, null, curated, 6, null);
            Assert.IsNotNull(choice, "control: still enabled after the reload");
            Assert.AreSame(reloadedOn, choice.Pick.Site, "the fallback carries the CURRENT site object");
            Assert.AreEqual(PuzzleGateType.Sigil, choice.Pick.Type, "and the pick as planned");
        }

        [TestMethod]
        public void The_tour_tags_switched_off_sites_with_their_reason()
        {
            Assert.AreEqual("", ThreadPuzzleSiteTour.DisabledTag(Site("x")));
            Assert.AreEqual(" DISABLED (sweep)", ThreadPuzzleSiteTour.DisabledTag(Site("y", disabled: true)));
            Assert.AreEqual(" REWARD-DISABLED (sweep)", ThreadPuzzleSiteTour.DisabledTag(Site("z", rewardDisabled: true)));
            StringAssert.Contains(ThreadPuzzleSiteTour.Describe(0, Site("y2", disabled: true)), "DISABLED (sweep)");
        }

        // ---- loader -------------------------------------------------------------------------------------------

        private const string Index = "{\"version\":1,\"dungeons\":[{\"id\":\"filos_doom\",\"landblock\":\"0x0150\",\"name\":\"Filos\",\"minLevel\":1,\"maxLevel\":400,\"families\":[],\"creatureTypes\":[],\"exitPortalWcid\":1003601,\"enabled\":true}]}";

        private const string SpawnFile = "{\"version\":1,\"landblock\":\"0x0150\",\"entry\":\"0x0150018A 30 0 0.005 1 0 0 0\",\"points\":[{\"cell\":22020490,\"x\":30,\"y\":12,\"z\":0.005,\"clearance\":4.0,\"depth\":1,\"curated\":true}],\"bossAnchor\":{\"cell\":22020491,\"x\":31,\"y\":12,\"z\":0.005,\"clearance\":4.0,\"depth\":2,\"curated\":true}}";

        private static ThreadDungeonStore Build(string puzzleJson)
            => ThreadDungeonStore.Parse(Index, "{\"bosses\":[]}", "{\"modifiers\":[],\"xpLadder\":[]}",
                new Dictionary<string, string> { ["0x0150"] = SpawnFile }, puzzleGatesJson: puzzleJson);

        /// <summary>puzzle-gates.json as the tool writes it: 2-space indentation, CRLF, "id" first in each site.</summary>
        private static string FileText(string nl = "\r\n", params (string Dungeon, string[] Sites)[] dungeons)
        {
            if (dungeons.Length == 0)
                dungeons = new[] { ("filos_doom", new[] { "s1", "s2" }) };

            var lines = new List<string> { "{", "  \"version\": 1,", "  \"dungeons\": {" };

            for (var d = 0; d < dungeons.Length; d++)
            {
                lines.Add($"    \"{dungeons[d].Dungeon}\": {{");
                lines.Add("      \"sites\": [");

                for (var s = 0; s < dungeons[d].Sites.Length; s++)
                {
                    lines.Add("        {");
                    lines.Add($"          \"id\": \"{dungeons[d].Sites[s]}\",");
                    lines.Add("          \"kind\": \"gate\",");
                    lines.Add("          \"anchor\": {");
                    lines.Add("            \"cell\": \"0x0150018A\",");
                    lines.Add(FormattableString.Invariant($"            \"x\": {1.5 + 40 * s},"));
                    lines.Add("            \"y\": 2.5,");
                    lines.Add("            \"z\": 0.005");
                    lines.Add("          },");
                    lines.Add("          \"yaw\": 90,");
                    lines.Add("          \"doorway\": { \"width\": 3.0, \"height\": 3.2, \"ceiling\": 6.0 },");
                    lines.Add("          \"maxN\": 5,");
                    lines.Add("          \"types\": [ \"sigil\", \"beam\" ],");
                    lines.Add("          \"gateModel\": { \"kind\": \"door\", \"wcid\": 1006850, \"scale\": 1.0, \"panels\": 1, \"residentGuid\": null },");
                    lines.Add("          \"shuffleSpots\": [],");
                    lines.Add("          \"toolVersion\": \"puzzlesites/1\"");
                    lines.Add(s == dungeons[d].Sites.Length - 1 ? "        }" : "        },");
                }

                lines.Add("      ]");
                lines.Add(d == dungeons.Length - 1 ? "    }" : "    },");
            }

            lines.Add("  }");
            lines.Add("}");
            return string.Join(nl, lines) + nl;
        }

        [TestMethod]
        public void The_loader_keeps_a_disabled_site_with_its_switches_and_reason()
        {
            Assert.IsTrue(PuzzleSiteKillSwitch.TryApply(FileText(), "filos_doom", "s1", false, "bad lever", out var text));
            Assert.IsTrue(PuzzleSiteKillSwitch.TryApply(text, "filos_doom", "s2", true, "bad reward", out text));

            var store = Build(text);
            var sites = store.GetPuzzleSites("filos_doom");

            Assert.AreEqual(2, sites.Count, string.Join("\n", store.Diagnostics));
            Assert.IsTrue(sites[0].Disabled);
            Assert.IsFalse(sites[0].RewardDisabled);
            Assert.AreEqual("bad lever", sites[0].DisabledReason);
            Assert.IsFalse(sites[1].Disabled);
            Assert.IsTrue(sites[1].RewardDisabled);
            Assert.AreEqual("bad reward", sites[1].DisabledReason);
            Assert.IsFalse(sites[0].UsableAsGate);
            Assert.IsTrue(sites[1].UsableAsGate);
            Assert.IsFalse(sites[1].UsableAsReward);

            var control = Build(FileText()).GetPuzzleSites("filos_doom");
            Assert.IsTrue(control.All(s => !s.Disabled && !s.RewardDisabled && s.UsableAsGate && s.UsableAsReward), "control: absent switches = in use");
        }

        // =============================================================================================
        // PuzzleSiteKillSwitch: the text edit
        // =============================================================================================

        /// <summary>The text with every line the edit inserted taken back out.</summary>
        private static string WithoutInserted(string text)
            => Regex.Replace(text, "^[ \\t]*\"(disabled|rewardDisabled|disabledReason)\":[^\\r\\n]*\\r?\\n", "", RegexOptions.Multiline);

        [TestMethod]
        public void The_edit_inserts_only_the_switch_lines_and_every_other_byte_is_unchanged()
        {
            foreach (var nl in new[] { "\r\n", "\n" })
            {
                var original = FileText(nl);

                Assert.IsTrue(PuzzleSiteKillSwitch.TryApply(original, "filos_doom", "s2", false, "check b: lever 1 moved", out var edited));

                Assert.AreNotEqual(original, edited);
                Assert.AreEqual(original, WithoutInserted(edited), "removing the inserted lines gives the original back, byte for byte");
                Assert.AreEqual(original.Length + ($"          \"disabled\": true,{nl}".Length + $"          \"disabledReason\": \"check b: lever 1 moved\",{nl}".Length), edited.Length);
                StringAssert.Contains(edited, $"          \"disabled\": true,{nl}          \"disabledReason\": \"check b: lever 1 moved\",{nl}          \"id\": \"s2\",");

                if (nl == "\n")
                    Assert.IsFalse(edited.Contains('\r'), "the file's own line ending is kept");

                // s1 is untouched: its block is identical before and after.
                var s1 = original.Substring(original.IndexOf("\"id\": \"s1\"", StringComparison.Ordinal), 200);
                StringAssert.Contains(edited, s1);
                Assert.AreEqual(1, Regex.Matches(edited, "\"disabled\"").Count);
            }
        }

        [TestMethod]
        public void The_edit_is_idempotent_and_does_not_repeat_a_reason()
        {
            Assert.IsTrue(PuzzleSiteKillSwitch.TryApply(FileText(), "filos_doom", "s1", true, "first", out var once));
            Assert.IsFalse(PuzzleSiteKillSwitch.TryApply(once, "filos_doom", "s1", true, "second", out var twice));
            Assert.AreEqual(once, twice);

            // Escalating to a full disable adds the switch but keeps the one reason.
            Assert.IsTrue(PuzzleSiteKillSwitch.TryApply(once, "filos_doom", "s1", false, "third", out var full));
            Assert.AreEqual(1, Regex.Matches(full, "\"disabledReason\"").Count);
            Assert.AreEqual(1, Regex.Matches(full, "\"disabled\": true").Count);
            Assert.AreEqual(1, Regex.Matches(full, "\"rewardDisabled\": true").Count);
        }

        [TestMethod]
        public void An_existing_switch_key_is_set_in_place_never_duplicated()
        {
            foreach (var nl in new[] { "\r\n", "\n" })
            {
                // s1 already carries "disabled": false and "rewardDisabled": false (hand-written, or a switch turned back off).
                var original = FileText(nl).Replace($"          \"id\": \"s1\",{nl}", $"          \"id\": \"s1\",{nl}          \"disabled\": false,{nl}          \"rewardDisabled\": false,{nl}");

                Assert.IsTrue(PuzzleSiteKillSwitch.TryApply(original, "filos_doom", "s1", false, "bad lever", out var full));
                Assert.AreEqual(1, Regex.Matches(full, "\"disabled\"").Count, full);
                Assert.AreEqual(1, Regex.Matches(full, "\"disabled\": true").Count, full);
                Assert.AreEqual(1, Regex.Matches(full, "\"disabledReason\": \"bad lever\"").Count, full);
                Assert.AreEqual(original.Replace("\"disabled\": false", "\"disabled\": true"), WithoutReason(full), "only the value changed, plus the reason line");

                Assert.IsTrue(PuzzleSiteKillSwitch.TryApply(original, "filos_doom", "s1", true, "bad reward", out var reward));
                Assert.AreEqual(1, Regex.Matches(reward, "\"rewardDisabled\"").Count, reward);
                Assert.AreEqual(1, Regex.Matches(reward, "\"rewardDisabled\": true").Count, reward);
                Assert.AreEqual(1, Regex.Matches(reward, "\"disabled\": false").Count, "the other switch is left as it was");

                var site = Build(full).GetPuzzleSites("filos_doom")[0];
                Assert.IsTrue(site.Disabled, "the loader reads the one key as true");
                Assert.AreEqual("bad lever", site.DisabledReason);
            }
        }

        private static string WithoutReason(string text)
            => Regex.Replace(text, "^[ \\t]*\"disabledReason\":[^\\r\\n]*\\r?\\n", "", RegexOptions.Multiline);

        [TestMethod]
        public void The_edit_is_scoped_to_the_named_dungeon_when_two_dungeons_share_a_site_id()
        {
            var original = FileText("\r\n", ("filos_doom", new[] { "s1" }), ("tiny_hive", new[] { "s1" }));

            Assert.IsTrue(PuzzleSiteKillSwitch.TryApply(original, "tiny_hive", "s1", false, "x", out var edited));

            var hive = edited.IndexOf("\"tiny_hive\"", StringComparison.Ordinal);
            var flag = edited.IndexOf("\"disabled\": true", StringComparison.Ordinal);
            Assert.IsTrue(flag > hive, "the switch landed in tiny_hive, not filos_doom");
            Assert.AreEqual(original, WithoutInserted(edited));

            Assert.IsTrue(PuzzleSiteKillSwitch.TryApply(original, "filos_doom", "s1", false, "x", out var other));
            Assert.IsTrue(other.IndexOf("\"disabled\": true", StringComparison.Ordinal) < other.IndexOf("\"tiny_hive\"", StringComparison.Ordinal), "control: filos_doom's lands before tiny_hive");
        }

        [TestMethod]
        public void The_edit_refuses_an_unknown_dungeon_or_site_and_escapes_the_reason()
        {
            Assert.ThrowsExactly<FormatException>(() => PuzzleSiteKillSwitch.TryApply(FileText(), "nowhere", "s1", false, "x", out _));
            Assert.ThrowsExactly<FormatException>(() => PuzzleSiteKillSwitch.TryApply(FileText(), "filos_doom", "s9", false, "x", out _));
            Assert.ThrowsExactly<FormatException>(() => PuzzleSiteKillSwitch.TryApply(FileText(), "filos_doom", "s", false, "x", out _), "a prefix of an id is not the id");

            Assert.AreEqual("\"a \\\"b\\\" c\\\\d ?\"", PuzzleSiteKillSwitch.JsonString("a \"b\" c\\d \u00e9"));

            Assert.IsTrue(PuzzleSiteKillSwitch.TryApply(FileText(), "filos_doom", "s1", false, "say \"hi\"\tnow", out var text));
            Assert.AreEqual("say \"hi\"?now", Build(text).GetPuzzleSites("filos_doom")[0].DisabledReason, "the escaped reason parses back");
        }

        // =============================================================================================
        // PuzzleSiteSweep.Evaluate: checks a-e
        // =============================================================================================

        private const uint A = 0x01500101, B = 0x01500102, C = 0x01500103;

        private static readonly Vector3 GateAt = new Vector3(0, 7, 0);

        private static IEnumerable<DungeonWalkGraph.Portal> Door(uint a, uint b, float x, float y)
        {
            yield return new DungeonWalkGraph.Portal(a, b, new Vector3(x, y, 0));
            yield return new DungeonWalkGraph.Portal(b, a, new Vector3(x, y, 0));
        }

        /// <summary>A (the approach, anchor at the origin) and B (beyond the gate), joined only by the gate's doorway.</summary>
        private static DungeonWalkGraph OneWay() => new DungeonWalkGraph(new[] { A, B }, Door(A, B, GateAt.X, GateAt.Y));

        /// <summary>OneWay plus a side cell C off A on the approach side (the gate still closes the way to B).</summary>
        private static DungeonWalkGraph WithC() => new DungeonWalkGraph(new[] { A, B, C }, Door(A, B, GateAt.X, GateAt.Y).Concat(Door(A, C, -5, 0)));

        /// <summary>The same, plus a way round through C: the gate no longer closes anything.</summary>
        private static DungeonWalkGraph WayRound() => new DungeonWalkGraph(new[] { A, B, C }, Door(A, B, GateAt.X, GateAt.Y).Concat(Door(A, C, 10, 0)).Concat(Door(C, B, 10, 10)));

        private static SweepObject Lever(int slot, Vector3 at, uint cell = A, Vector3? final = null, bool entered = true, uint? physicsCell = null, bool? plannedInCell = null)
            => new SweepObject
            {
                Role = PuzzleRole.Candidate, Slot = slot, Planned = at, ExpectedCell = cell, Entered = entered,
                Cell = entered ? physicsCell ?? cell : 0, Final = entered ? final ?? at : null, FinalRotation = entered ? Quaternion.Identity : null,
                PlannedInCell = entered ? plannedInCell ?? true : null,
            };

        private static SweepObject GatePart(bool entered = true)
            => new SweepObject { Role = PuzzleRole.Gate, Slot = 0, Planned = GateAt, ExpectedCell = A, Entered = entered, Cell = entered ? A : 0, Final = entered ? GateAt : null, FinalRotation = Quaternion.Identity };

        private static SweepRecord Record(string role = "gate", IEnumerable<SweepObject> round = null, IEnumerable<SweepObject> gate = null, PuzzleGateType type = PuzzleGateType.Odd,
            IReadOnlyList<int> beamTargets = null, IReadOnlyList<SweepPoint> extra = null, string spawnError = null, string skip = null, int? plannedRound = null)
        {
            var roundList = (round ?? new[] { Lever(0, new Vector3(-1, 3, 0)), Lever(1, new Vector3(1, 3, 0)) }).ToList();
            var gateList = (gate ?? new[] { GatePart() }).ToList();

            return new SweepRecord
            {
                Dungeon = "filos_doom", SiteId = "s1", SiteKind = "gate", Role = role, Type = type, N = 2,
                PlannedGateParts = 1, PlannedRoundObjects = plannedRound ?? roundList.Count,
                Objects = gateList.Concat(roundList).ToList(),
                ExtraLeverPoints = extra ?? Array.Empty<SweepPoint>(),
                BeamTargets = beamTargets,
                Axis = PuzzleBeamAxis.Down,
                AnchorCell = A, Anchor = Vector3.Zero, GatePoint = GateAt,
                SpawnError = spawnError, SkipReason = skip,
                Probe = new[] { new ProbeLeg("lever 1", "out", ProbeOutcome.Pass, Vector3.Zero, null), new ProbeLeg("lever 1", "back", ProbeOutcome.Pass, Vector3.Zero, null) },
            };
        }

        [TestMethod]
        public void A_clean_gate_placement_passes_every_check()
        {
            var o = PuzzleSiteSweep.Evaluate(Record(), OneWay());

            Assert.AreEqual(PuzzleSiteSweep.Pass, o.Result, o.Detail);
            Assert.AreEqual("-", o.FailingCheck);
            StringAssert.Contains(o.Detail, "c: walk probe");
        }

        [TestMethod]
        public void Check_a_fails_a_spawn_error_a_missing_object_and_a_short_count()
        {
            var spawn = PuzzleSiteSweep.Evaluate(Record(spawnError: "lever slot 2: outside every cell"), OneWay());
            Assert.AreEqual(("fail", "a"), (spawn.Result, spawn.FailingCheck));

            var gone = PuzzleSiteSweep.Evaluate(Record(gate: new[] { GatePart(entered: false) }), OneWay());
            Assert.AreEqual(("fail", "a"), (gone.Result, gone.FailingCheck));
            StringAssert.Contains(gone.Detail, "gate part 1 not in the world");

            var shortCount = PuzzleSiteSweep.Evaluate(Record(plannedRound: 3), OneWay());
            Assert.AreEqual(("fail", "a"), (shortCount.Result, shortCount.FailingCheck));
            StringAssert.Contains(shortCount.Detail, "round objects 2/3");
        }

        private static ProbeLeg[] Legs(string target, ProbeOutcome back)
            => new[] { new ProbeLeg(target, "out", ProbeOutcome.Pass, Vector3.Zero, null), new ProbeLeg(target, "back", back, Vector3.Zero, back == ProbeOutcome.Blocked ? "stuck" : null) };

        /// <summary>
        /// Drift past 0.25 m across / 0.5 m up but within 1.0 m is a note while the walk probe (aimed at the lever where it
        /// stands) reaches it, and a b failure when that probe is blocked (control). Past 1.0 m fails whatever the probe.
        /// </summary>
        [TestMethod]
        public void Check_b_fails_drift_only_when_the_probe_to_the_lever_fails_or_it_passes_1_m()
        {
            var planned = new Vector3(-1, 3, 0);
            SweepRecord Slid(Vector3 by, ProbeOutcome back) => Record(round: new[] { Lever(0, planned, final: planned + by), Lever(1, new Vector3(1, 3, 0)) }) with { Probe = Legs("lever 1", back) };

            var inside = PuzzleSiteSweep.Evaluate(Slid(new Vector3(0.2f, 0, 0.4f), ProbeOutcome.Pass), OneWay());
            Assert.AreEqual(PuzzleSiteSweep.Pass, inside.Result, inside.Detail);
            Assert.IsFalse(inside.Detail.Contains("moved"), "inside 0.25 / 0.5 is not even a note: " + inside.Detail);

            var reached = PuzzleSiteSweep.Evaluate(Slid(new Vector3(0.4f, 0, 0), ProbeOutcome.Pass), OneWay());
            Assert.AreEqual(PuzzleSiteSweep.Pass, reached.Result, reached.Detail);
            StringAssert.Contains(reached.Detail, "lever 1 moved 0.40 m across, 0.00 m up/down from its planned point (walk probe reaches it)");

            // Control: the same 0.40 m with the probe to that lever blocked.
            var blocked = PuzzleSiteSweep.Evaluate(Slid(new Vector3(0.4f, 0, 0), ProbeOutcome.Blocked), OneWay());
            Assert.AreEqual(("fail", "b"), (blocked.Result, blocked.FailingCheck), blocked.Detail);
            StringAssert.Contains(blocked.Detail, "b: lever 1 moved 0.40 m across, 0.00 m up/down from its planned point, and the walk probe to it is blocked");

            // A blocked leg to ANOTHER lever does not make lever 1's drift a b failure (it is c's).
            var other = PuzzleSiteSweep.Evaluate(Record(round: new[] { Lever(0, planned, final: planned + new Vector3(0.4f, 0, 0)), Lever(1, new Vector3(1, 3, 0)) })
                with { Probe = Legs("lever 1", ProbeOutcome.Pass).Concat(Legs("lever 2", ProbeOutcome.Blocked)).ToArray() }, OneWay());
            Assert.AreEqual(("fail", "c"), (other.Result, other.FailingCheck), other.Detail);

            // Hard limit: 1.2 m across, and 1.1 m up, fail with the probe passing.
            var far = PuzzleSiteSweep.Evaluate(Slid(new Vector3(1.2f, 0, 0), ProbeOutcome.Pass), OneWay());
            Assert.AreEqual(("fail", "b"), (far.Result, far.FailingCheck), far.Detail);
            StringAssert.Contains(far.Detail, "(1.20 m in 3D, limit 1.0 m)");

            var up = PuzzleSiteSweep.Evaluate(Slid(new Vector3(0, 0, 1.1f), ProbeOutcome.Pass), OneWay());
            Assert.AreEqual(("fail", "b"), (up.Result, up.FailingCheck), up.Detail);

            // 3D: 0.9 m across and 0.9 m up is under 1.0 m on each axis but 1.27 m in 3D: fails whatever the probe says.
            var diagonal = PuzzleSiteSweep.Evaluate(Slid(new Vector3(0.9f, 0, 0.9f), ProbeOutcome.Pass), OneWay());
            Assert.AreEqual(("fail", "b"), (diagonal.Result, diagonal.FailingCheck), diagonal.Detail);
            StringAssert.Contains(diagonal.Detail, "(1.27 m in 3D, limit 1.0 m)");

            // 0.6 m up (past 0.5, inside 1.0) with the probe passing: a note, not a failure.
            var upNote = PuzzleSiteSweep.Evaluate(Slid(new Vector3(0, 0, 0.6f), ProbeOutcome.Pass), OneWay());
            Assert.AreEqual(PuzzleSiteSweep.Pass, upNote.Result, upNote.Detail);

            // No probe leg for the lever: unchecked, never passed.
            var noLeg = PuzzleSiteSweep.Evaluate(Slid(new Vector3(0.4f, 0, 0), ProbeOutcome.Pass) with { Probe = Legs("lever 2", ProbeOutcome.Pass) }, OneWay());
            Assert.AreEqual((PuzzleSiteSweep.Unchecked, "b"), (noLeg.Result, noLeg.FailingCheck), noLeg.Detail);
        }

        [TestMethod]
        public void Check_c_fails_a_lever_point_with_no_cell_or_a_lever_in_the_wrong_cell()
        {
            var noCell = PuzzleSiteSweep.Evaluate(Record(round: new[] { Lever(0, new Vector3(-1, 3, 0), cell: 0, entered: false), Lever(1, new Vector3(1, 3, 0)) }), OneWay());
            Assert.IsTrue(noCell.Detail.Contains("c: lever 1: no cell at its planned point"), noCell.Detail);

            var wrongCell = PuzzleSiteSweep.Evaluate(Record(round: new[] { Lever(0, new Vector3(-1, 3, 0), physicsCell: C, plannedInCell: false), Lever(1, new Vector3(1, 3, 0)) }), WithC());
            Assert.AreEqual(("fail", "c"), (wrongCell.Result, wrongCell.FailingCheck), wrongCell.Detail);
            StringAssert.Contains(wrongCell.Detail, "lever 1: physics cell 0x01500103 contains neither its planned point nor where it stands (accepted drift), planned point resolves to 0x01500101");

            // Overlapping cells: the physics cell differs from AdjustCell's lowest-numbered pick but ALSO contains the
            // planned point. Same record as wrongCell except the containment answer (the discriminating control).
            var overlap = PuzzleSiteSweep.Evaluate(Record(round: new[] { Lever(0, new Vector3(-1, 3, 0), physicsCell: C, plannedInCell: true), Lever(1, new Vector3(1, 3, 0)) }), WithC());
            Assert.AreEqual(PuzzleSiteSweep.Pass, overlap.Result, overlap.Detail);
            StringAssert.Contains(overlap.Detail, "lever 1: physics cell 0x01500103 also contains its planned point (overlaps 0x01500101)");

            // Containment unknown (null) is not taken as a pass.
            var unknown = PuzzleSiteSweep.Evaluate(Record(round: new[] { new SweepObject { Role = PuzzleRole.Candidate, Slot = 0, Planned = new Vector3(-1, 3, 0), ExpectedCell = A, Entered = true, Cell = C, Final = new Vector3(-1, 3, 0), FinalRotation = Quaternion.Identity, PlannedInCell = null }, Lever(1, new Vector3(1, 3, 0)) }), OneWay());
            Assert.AreEqual(("fail", "c"), (unknown.Result, unknown.FailingCheck), unknown.Detail);

            var badSpot = PuzzleSiteSweep.Evaluate(Record(extra: new[] { new SweepPoint(1, new Vector3(5, 5, 0), 0) }), OneWay());
            Assert.AreEqual(("fail", "c"), (badSpot.Result, badSpot.FailingCheck), badSpot.Detail);
            StringAssert.Contains(badSpot.Detail, "shuffle spot 2");
        }

        /// <summary>
        /// The planned point is not in the lever's physics cell, but where the lever stands is: that passes only when b
        /// accepts the lever's drift. Each case below differs from its neighbour in one input.
        /// </summary>
        [TestMethod]
        public void Check_c_accepts_a_physics_cell_holding_the_final_position_only_when_the_drift_is_accepted()
        {
            var planned = new Vector3(-1, 3, 0);
            SweepObject L0(Vector3 by, bool? finalIn) => new SweepObject
            {
                Role = PuzzleRole.Candidate, Slot = 0, Planned = planned, ExpectedCell = A, Entered = true, Cell = C,
                Final = planned + by, FinalRotation = Quaternion.Identity, PlannedInCell = false, FinalInCell = finalIn,
            };
            SweepRecord Rec(Vector3 by, bool? finalIn, ProbeOutcome back) => Record(round: new[] { L0(by, finalIn), Lever(1, new Vector3(1, 3, 0)) }) with { Probe = Legs("lever 1", back) };

            // Within the 0.25 m tolerance, final position in the cell: pass with a note.
            var small = PuzzleSiteSweep.Evaluate(Rec(new Vector3(0.1f, 0, 0), true, ProbeOutcome.Pass), WithC());
            Assert.AreEqual(PuzzleSiteSweep.Pass, small.Result, small.Detail);
            StringAssert.Contains(small.Detail, "lever 1: physics cell 0x01500103 contains where it stands (drift accepted");

            // Control: the same, final position NOT in the cell either: fail.
            var neither = PuzzleSiteSweep.Evaluate(Rec(new Vector3(0.1f, 0, 0), false, ProbeOutcome.Pass), WithC());
            Assert.AreEqual(("fail", "c"), (neither.Result, neither.FailingCheck), neither.Detail);

            // 0.4 m drift the probe reaches (accepted): final position counts.
            var accepted = PuzzleSiteSweep.Evaluate(Rec(new Vector3(0.4f, 0, 0), true, ProbeOutcome.Pass), WithC());
            Assert.AreEqual(PuzzleSiteSweep.Pass, accepted.Result, accepted.Detail);

            // Control: the same 0.4 m with the probe blocked (drift NOT accepted): the final position does not count.
            var rejected = PuzzleSiteSweep.Evaluate(Rec(new Vector3(0.4f, 0, 0), true, ProbeOutcome.Blocked), WithC());
            StringAssert.Contains(rejected.Detail, "c: lever 1: physics cell 0x01500103 contains neither");

            // Past the hard limit: never accepted.
            var far = PuzzleSiteSweep.Evaluate(Rec(new Vector3(1.2f, 0, 0), true, ProbeOutcome.Pass), WithC());
            StringAssert.Contains(far.Detail, "c: lever 1: physics cell 0x01500103 contains neither");
        }

        /// <summary>
        /// A start may stand in the region cell or a cell the walk graph reaches from it with the gate doorway closed;
        /// no graph (or a region outside it) is no proof at all (null), so the sweep leaves such a start unchecked.
        /// </summary>
        [TestMethod]
        public void Start_reach_is_the_region_plus_what_the_graph_reaches_with_the_gate_closed()
        {
            var gate = new[] { DungeonWalkGraph.Pair(A, B) };

            var closed = PuzzleSiteSweep.StartReach(OneWay(), A, Vector3.Zero, gate);
            Assert.IsTrue(closed(A));
            Assert.IsFalse(closed(B), "beyond the closed gate doorway");

            var open = PuzzleSiteSweep.StartReach(OneWay(), A, Vector3.Zero, null);
            Assert.IsTrue(open(B), "control: the same graph with the doorway open reaches B");

            Assert.IsTrue(PuzzleSiteSweep.StartReach(WithC(), A, Vector3.Zero, gate)(C), "a side cell on the approach side");
            Assert.IsFalse(PuzzleSiteSweep.StartReach(WithC(), A, Vector3.Zero, gate)(0x01509999), "a cell not in the graph is not proven");

            Assert.IsNull(PuzzleSiteSweep.StartReach(null, A, Vector3.Zero, gate), "no graph: no proof");
            Assert.IsNull(PuzzleSiteSweep.StartReach(OneWay(), 0x01509999, Vector3.Zero, gate), "region not in the graph: no proof");
        }

        /// <summary>
        /// A gate row's region must be reached from the declared anchor cell with the gate doorway closed; a region beyond
        /// the gate is not proven. Nothing to prove for a reward row or a region equal to the anchor cell. With no graph,
        /// or the anchor cell outside it, a different region is NOT proven (a reason, never a pass).
        /// </summary>
        [TestMethod]
        public void A_gate_region_must_be_proven_on_the_approach_side()
        {
            var gate = new[] { DungeonWalkGraph.Pair(A, B) };

            Assert.IsNotNull(PuzzleSiteSweep.RegionNotProven(OneWay(), true, A, Vector3.Zero, B, gate), "beyond the closed gate");
            Assert.IsNull(PuzzleSiteSweep.RegionNotProven(OneWay(), true, A, Vector3.Zero, B, null), "control: the doorway open");
            Assert.IsNull(PuzzleSiteSweep.RegionNotProven(WithC(), true, A, Vector3.Zero, C, gate), "a side cell on the approach side");
            Assert.IsNull(PuzzleSiteSweep.RegionNotProven(OneWay(), false, A, Vector3.Zero, B, gate), "a reward row has no gate");
            Assert.IsNull(PuzzleSiteSweep.RegionNotProven(OneWay(), true, A, Vector3.Zero, A, gate), "the anchor cell itself");

            StringAssert.Contains(PuzzleSiteSweep.RegionNotProven(OneWay(), true, 0x01509999, Vector3.Zero, B, gate), "the anchor cell 0x01509999 is not in the walk graph");
            StringAssert.Contains(PuzzleSiteSweep.RegionNotProven(null, true, A, Vector3.Zero, B, gate), "no walk graph");
            Assert.IsNull(PuzzleSiteSweep.RegionNotProven(null, true, A, Vector3.Zero, A, gate), "no graph, but the region is the anchor cell: nothing to prove");
        }

        /// <summary>
        /// The runner's whole gate decision (GateGuardFor, the only call RunProbe makes for it): a reward row runs
        /// unrestricted; a gate row with no doorway or no graph is unchecked (its legs would run with nothing closed); a
        /// gate row whose region is not proven on the approach side is unchecked; otherwise its legs are confined to the
        /// gate field (B, beyond the closed doorway, is refused).
        /// </summary>
        [TestMethod]
        public void The_gate_guard_confines_gate_legs_or_leaves_the_row_unchecked()
        {
            var gate = new[] { DungeonWalkGraph.Pair(A, B) };

            var reward = PuzzleSiteSweep.GateGuardFor(OneWay(), false, A, Vector3.Zero, A, Vector3.Zero, null);
            Assert.IsNull(reward.Allowed, "a reward row is unrestricted");
            Assert.IsNull(reward.UncheckedReason);

            var noDoor = PuzzleSiteSweep.GateGuardFor(OneWay(), true, A, Vector3.Zero, A, Vector3.Zero, null);
            Assert.IsNull(noDoor.Allowed);
            StringAssert.Contains(noDoor.UncheckedReason, "no gate doorway");

            var emptyDoor = PuzzleSiteSweep.GateGuardFor(OneWay(), true, A, Vector3.Zero, A, Vector3.Zero, new (uint, uint)[0]);
            StringAssert.Contains(emptyDoor.UncheckedReason, "no gate doorway");

            var noGraph = PuzzleSiteSweep.GateGuardFor(null, true, A, Vector3.Zero, A, Vector3.Zero, gate);
            StringAssert.Contains(noGraph.UncheckedReason, "no walk graph");

            var beyond = PuzzleSiteSweep.GateGuardFor(OneWay(), true, A, Vector3.Zero, B, Vector3.Zero, gate);
            Assert.IsNull(beyond.Allowed);
            StringAssert.Contains(beyond.UncheckedReason, "region 0x01500102 not proven on the approach side");

            var unknownAnchor = PuzzleSiteSweep.GateGuardFor(OneWay(), true, 0x01509999, Vector3.Zero, A, Vector3.Zero, gate);
            StringAssert.Contains(unknownAnchor.UncheckedReason, "is not in the walk graph");

            var proven = PuzzleSiteSweep.GateGuardFor(OneWay(), true, A, Vector3.Zero, A, Vector3.Zero, gate);
            Assert.IsNull(proven.UncheckedReason, proven.UncheckedReason);
            Assert.IsNotNull(proven.Allowed);
            Assert.IsTrue(proven.Allowed(A));
            Assert.IsFalse(proven.Allowed(B), "the legs stay on the approach side of the closed doorway");
        }

        /// <summary>
        /// The gate field comes from the region; with the region outside the graph it falls back to the declared anchor
        /// cell; with neither in the graph there is none (the row's start is then unchecked).
        /// </summary>
        [TestMethod]
        public void The_gate_field_falls_back_to_the_anchor_cell_and_is_null_without_either()
        {
            var gate = new[] { DungeonWalkGraph.Pair(A, B) };

            var fromRegion = PuzzleSiteSweep.GateField(OneWay(), A, Vector3.Zero, A, Vector3.Zero, gate, out var why);
            Assert.IsNull(why);
            Assert.IsFalse(fromRegion(B));

            var fallback = PuzzleSiteSweep.GateField(OneWay(), 0x01509999, Vector3.Zero, A, Vector3.Zero, gate, out why);
            Assert.IsNotNull(fallback, why);
            Assert.IsTrue(fallback(A));
            Assert.IsFalse(fallback(B), "the anchor cell's field still keeps the body out of B");
            Assert.IsTrue(fallback(0x01509999), "a cell outside the graph stays allowed");

            Assert.IsNull(PuzzleSiteSweep.GateField(OneWay(), 0x01509999, Vector3.Zero, 0x01509998, Vector3.Zero, gate, out why));
            StringAssert.Contains(why, "no walk field for the gate check");
        }

        /// <summary>A blocked start leg fails c; an unproven start (unchecked leg) leaves the row unchecked, never passed.</summary>
        [TestMethod]
        public void A_blocked_start_fails_c_and_an_unproven_start_is_unchecked()
        {
            var blocked = PuzzleSiteSweep.Evaluate(Record() with { Probe = new[] { ProbeLeg.Blocked("anchor", "start", Vector3.Zero, "no standing room") } }, OneWay());
            Assert.AreEqual(("fail", "c"), (blocked.Result, blocked.FailingCheck), blocked.Detail);

            var unproven = PuzzleSiteSweep.Evaluate(Record() with { Probe = new[] { ProbeLeg.Unchecked("anchor", "start", "no walk graph proves it reachable") } }, OneWay());
            Assert.AreEqual((PuzzleSiteSweep.Unchecked, "c"), (unproven.Result, unproven.FailingCheck), unproven.Detail);
        }

        /// <summary>disabledReason carries up to 300 characters of detail; longer detail is cut and ends with "...".</summary>
        [TestMethod]
        public void A_long_disable_reason_is_cut_at_300_with_an_ellipsis()
        {
            var longDetail = new string('x', 400);
            var reason = PuzzleSiteSweep.Reason("t", (Record(), new SweepOutcome("fail", "c", longDetail)));
            StringAssert.EndsWith(reason, new string('x', 297) + "...");
            Assert.IsFalse(reason.Contains(new string('x', 298)));

            var shortReason = PuzzleSiteSweep.Reason("t", (Record(), new SweepOutcome("fail", "c", new string('y', 300))));
            StringAssert.EndsWith(shortReason, new string('y', 300), "exactly 300 is kept whole");
        }

        [TestMethod]
        public void Body_lift_is_half_the_physics_height_or_half_a_metre()
        {
            Assert.AreEqual(0.75f, PuzzleSiteSweep.BodyLift(1.5f));
            Assert.AreEqual(0.5f, PuzzleSiteSweep.BodyLift(0f));
        }

        [TestMethod]
        public void Check_d_needs_the_levers_on_the_approach_side_and_the_gate_to_close_the_way()
        {
            // A lever beyond the gate: unreachable once the doorway is blocked.
            var beyond = PuzzleSiteSweep.Evaluate(Record(round: new[] { Lever(0, new Vector3(-1, 3, 0)), Lever(1, new Vector3(0, 12, 0), cell: B) }), OneWay());
            Assert.AreEqual(("fail", "d"), (beyond.Result, beyond.FailingCheck), beyond.Detail);
            StringAssert.Contains(beyond.Detail, "lever 2 in 0x01500102 is not reachable from the anchor without passing the gate");

            // The same lever for a REWARD host: nothing is blocked, so it is reachable (control for the blocking).
            var reward = PuzzleSiteSweep.Evaluate(Record(role: "reward", round: new[] { Lever(0, new Vector3(-1, 3, 0)), Lever(1, new Vector3(0, 12, 0), cell: B) }), OneWay());
            Assert.AreEqual(PuzzleSiteSweep.Pass, reward.Result, reward.Detail);

            // A way round the gate: the gate closes nothing.
            var round = PuzzleSiteSweep.Evaluate(Record(), WayRound());
            Assert.AreEqual(("fail", "d"), (round.Result, round.FailingCheck), round.Detail);
            StringAssert.Contains(round.Detail, "does not close the way");

            // No doorway portal near the gate point at all.
            var noDoor = PuzzleSiteSweep.Evaluate(Record(), new DungeonWalkGraph(new[] { A, B }, Door(A, B, 40, 40)));
            StringAssert.Contains(noDoor.Detail, "no doorway portal at the gate point");

            // No graph: d is unchecked, and that is the row's result when nothing else failed.
            var none = PuzzleSiteSweep.Evaluate(Record(), null);
            Assert.AreEqual((PuzzleSiteSweep.Unchecked, "d"), (none.Result, none.FailingCheck));
        }

        [TestMethod]
        public void Check_e_compares_where_each_beam_points_with_the_planned_target()
        {
            var levers = new[] { Lever(0, new Vector3(-2, 3, 0)), Lever(1, new Vector3(2, 3, 0)) };
            var hostAt = new Vector3(0, 6, 3);
            var aimAtFirst = PuzzleBeamAim.Aim(new Vector3(-2, 3, PuzzleGateTunables.BeamTargetHeight) - hostAt, PuzzleBeamAxis.Down);
            var light = new SweepObject { Role = PuzzleRole.Light, Slot = 0, Planned = hostAt, ExpectedCell = A, Entered = true, Cell = A, Final = hostAt, FinalRotation = aimAtFirst };

            var right = PuzzleSiteSweep.Evaluate(Record(type: PuzzleGateType.Beam, round: levers.Append(light), beamTargets: new[] { 0 }), OneWay());
            Assert.AreEqual(PuzzleSiteSweep.Pass, right.Result, right.Detail);

            var wrong = PuzzleSiteSweep.Evaluate(Record(type: PuzzleGateType.Beam, round: levers.Append(light), beamTargets: new[] { 1 }), OneWay());
            Assert.AreEqual(("fail", "e"), (wrong.Result, wrong.FailingCheck), wrong.Detail);
            StringAssert.Contains(wrong.Detail, "beam 1 points at lever 1, planned lever 2");
        }

        [TestMethod]
        public void A_skipped_row_is_unchecked_and_never_a_failure()
        {
            var o = PuzzleSiteSweep.Evaluate(Record(skip: "no placeable gate model"), OneWay());
            Assert.AreEqual((PuzzleSiteSweep.Unchecked, "-"), (o.Result, o.FailingCheck));
        }

        /// <summary>
        /// A load timeout, a spawn timeout or an uncleared previous row is the sweep's own trouble: the row is unchecked
        /// ("harness: ...") and --write-disabled switches nothing off. Control: a real spawn failure (SpawnError) on the
        /// same site fails check a and is disabled.
        /// </summary>
        [TestMethod]
        public void Harness_timeouts_are_unchecked_rows_and_disable_nothing()
        {
            var dungeon = new DungeonEntryDef { Id = "filos_doom", Landblock = 0x0150 };
            var site = Site("h1");
            var rows = new[] { PuzzleSiteSweepRunner.LoadTimeoutWhy, PuzzleSiteSweepRunner.SpawnTimeoutWhy, PuzzleSiteSweepRunner.NotClearedWhy }
                .Select(why => PuzzleSiteSweepRunner.Harness(dungeon, site, "gate", PuzzleGateType.Sigil, 5, 7, why))
                .Select(r => (r, PuzzleSiteSweep.Evaluate(r, OneWay())))
                .ToList();

            foreach (var (r, o) in rows)
            {
                Assert.AreEqual(PuzzleSiteSweep.Unchecked, o.Result, o.Detail);
                StringAssert.StartsWith(o.Detail, PuzzleSiteSweepRunner.HarnessPrefix);
                Assert.IsNull(r.SpawnError, "a harness row never carries a spawn error");
                StringAssert.Contains(PuzzleSiteSweep.TsvRow(r, o), "seed=7; harness: ", "the seed reproduces it");
            }

            StringAssert.Contains(rows[0].Item2.Detail, "did not finish loading within 60 s");
            StringAssert.Contains(rows[1].Item2.Detail, "the queued spawn did not run within 20 s");
            Assert.AreEqual(0, PuzzleSiteSweep.Decide(rows, "t").Count, "no harness row switches a site off");

            // Control: the same site with a real host-reported spawn failure is check a, and is disabled.
            var real = new SweepRecord { Dungeon = "filos_doom", SiteId = "h1", SiteKind = "gate", Role = "gate", Type = PuzzleGateType.Sigil, N = 5, SpawnError = "gate part 1 refused its cell" };
            var realOutcome = PuzzleSiteSweep.Evaluate(real, OneWay());
            Assert.AreEqual(("fail", "a"), (realOutcome.Result, realOutcome.FailingCheck), realOutcome.Detail);
            Assert.AreEqual(1, PuzzleSiteSweep.Decide(new[] { (real, realOutcome) }, "t").Count);
        }


        // =============================================================================================
        // Check c: the walk probe (pure parts)
        // =============================================================================================

        [TestMethod]
        public void A_blocked_probe_leg_fails_c_and_names_where_the_body_stopped()
        {
            var blocked = Record() with
            {
                Probe = new[]
                {
                    new ProbeLeg("lever 1", "out", ProbeOutcome.Pass, Vector3.Zero, null),
                    ProbeLeg.Blocked("lever 2", "back", new Vector3(1.04f, 2.96f, -3f), "fell with no floor within 12 m") with { Path = "straight" },
                },
            };

            var o = PuzzleSiteSweep.Evaluate(blocked, OneWay());

            Assert.AreEqual(("fail", "c"), (o.Result, o.FailingCheck), o.Detail);
            StringAssert.Contains(o.Detail, "probe lever 2 back: blocked-at (1.0, 3.0, -3.0) fell with no floor within 12 m [straight]");
            Assert.AreEqual(1, PuzzleSiteSweep.Decide(new[] { (blocked, o) }, "t").Count, "a gate-role probe failure disables the site");
        }

        [TestMethod]
        public void A_probe_that_did_not_run_or_skipped_a_leg_leaves_c_unchecked_never_passed()
        {
            var notRun = PuzzleSiteSweep.Evaluate(Record() with { Probe = null, ProbeNote = "the probe body did not load" }, OneWay());
            Assert.AreEqual((PuzzleSiteSweep.Unchecked, "c"), (notRun.Result, notRun.FailingCheck), notRun.Detail);
            StringAssert.Contains(notRun.Detail, "the probe body did not load");

            var skipped = PuzzleSiteSweep.Evaluate(Record() with { Probe = new[] { ProbeLeg.Unchecked("lever 1", "out", "no body") } }, OneWay());
            Assert.AreEqual((PuzzleSiteSweep.Unchecked, "c"), (skipped.Result, skipped.FailingCheck));

            // Both c and d unchecked: the row names the first (c), and the detail carries both.
            var both = PuzzleSiteSweep.Evaluate(Record() with { Probe = null }, null);
            Assert.AreEqual("c", both.FailingCheck);
            StringAssert.Contains(both.Detail, "d: no walk graph");

            // A real failure elsewhere still wins over an unchecked c.
            var failed = PuzzleSiteSweep.Evaluate(Record(plannedRound: 3) with { Probe = null }, OneWay());
            Assert.AreEqual(("fail", "a"), (failed.Result, failed.FailingCheck));
        }

        [TestMethod]
        public void Arrival_needs_the_body_within_half_a_metre_on_a_walkable_floor()
        {
            var target = new Vector3(10, 10, 0);

            Assert.AreEqual(ProbeOutcome.Pass, PuzzleSiteSweep.ClassifyArrival(target + new Vector3(0.3f, 0.3f, 0.2f), true, target).Outcome, "0.47 m");
            Assert.AreEqual(ProbeOutcome.Blocked, PuzzleSiteSweep.ClassifyArrival(target + new Vector3(0.4f, 0.4f, 0f), true, target).Outcome, "0.57 m");

            var below = PuzzleSiteSweep.ClassifyArrival(target + new Vector3(0, 0, -3f), true, target);
            Assert.AreEqual(ProbeOutcome.Blocked, below.Outcome, "under a balcony");
            StringAssert.Contains(below.Reason, "-3.00 m up/down");

            var steep = PuzzleSiteSweep.ClassifyArrival(target, false, target);
            Assert.AreEqual(ProbeOutcome.Blocked, steep.Outcome);
            StringAssert.Contains(steep.Reason, "not on a walkable surface");
        }

        [TestMethod]
        public void Start_candidates_are_strictly_nearest_first_within_1_5_m()
        {
            var p = new Vector3(10, 10, -12);
            var approach = new Vector3(-1, 0, 0);
            var c = PuzzleWalkProbe.StartCandidates(p, approach).ToList();

            Assert.AreEqual(1.5f, PuzzleWalkProbe.StartRadius);
            Assert.AreEqual(p, c[0], "the curated point first");
            Assert.AreEqual(1 + 6 * 36, c.Count, "6 rings (0.25 m steps to 1.5 m), 36 points each (every 10 degrees)");
            Assert.IsTrue(c.All(x => Vector3.Distance(x, p) <= PuzzleWalkProbe.StartRadius + 1e-4f && x.Z == p.Z));
            Assert.AreEqual(36, c.Skip(1).Count(x => Math.Abs(Vector3.Distance(x, p) - 1.5f) < 1e-4f), "the outer ring is 1.5 m out");

            // 10 degree spacing: on the 0.25 m ring no two points are closer than the 10 degree chord, and 36 are distinct.
            var ring = c.Skip(1).Where(x => Math.Abs(Vector3.Distance(x, p) - 0.25f) < 1e-4f).ToList();
            var chord = 2f * 0.25f * MathF.Sin(5f * MathF.PI / 180f);
            Assert.AreEqual(36, ring.Count);
            Assert.IsTrue(ring.SelectMany((x, i) => ring.Skip(i + 1).Select(y => Vector3.Distance(x, y))).Min() > chord - 1e-4f);
            Assert.IsTrue(Vector3.Distance(c[1], p + new Vector3(-0.25f, 0, 0)) < 1e-4f, "the nearest ring, straight toward the approach");

            // Strictly by distance: a far-side point of a nearer ring comes before any point of a farther ring (the
            // approach only orders points WITHIN a ring). The old order put the whole approach half-plane first.
            var distances = c.Select(x => Vector3.Distance(x, p)).ToList();
            Assert.IsTrue(distances.Zip(distances.Skip(1), (a, b) => b >= a - 1e-4f).All(ok => ok), "never a farther point before a nearer one");
            var farSide025 = c.FindIndex(x => x.X > p.X + 0.2f && Math.Abs(Vector3.Distance(x, p) - 0.25f) < 1e-4f);
            var nearSide050 = c.FindIndex(x => x.X < p.X - 0.4f && Math.Abs(Vector3.Distance(x, p) - 0.5f) < 1e-4f);
            Assert.IsTrue(farSide025 < nearSide050, $"0.25 m far side ({farSide025}) before 0.5 m approach side ({nearSide050})");

            // Control: no approach direction = rings all around, nearest first.
            var all = PuzzleWalkProbe.StartCandidates(p, null).ToList();
            Assert.AreEqual(c.Count, all.Count);
            Assert.IsTrue(all.Skip(1).Take(36).All(x => Math.Abs(Vector3.Distance(x, p) - 0.25f) < 1e-4f));
            Assert.IsTrue(Math.Abs(Vector3.Distance(all[37], p) - 0.5f) < 1e-4f, "then the 0.5 m ring");
        }

        [TestMethod]
        public void Each_routed_doorway_gets_a_soft_push_through_point_1_m_beyond_it()
        {
            var start = new Vector3(0, 0, -12);
            var goals = PuzzleWalkProbe.Goals(start, new[] { new Vector3(0, 4, -10.5f), new Vector3(3, 4, -10.5f) }, new Vector3(3, 8, -12));

            Assert.AreEqual(5, goals.Count);
            Assert.AreEqual((new Vector3(0, 4, -10.5f), false, 1), goals[0]);
            Assert.AreEqual((new Vector3(0, 5, -10.5f), true, 1), goals[1], "1 m on, along the line in from the start");
            Assert.AreEqual((new Vector3(3, 4, -10.5f), false, 2), goals[2]);
            Assert.AreEqual((new Vector3(4, 4, -10.5f), true, 2), goals[3], "1 m on, along the line in from doorway 1");
            Assert.AreEqual((new Vector3(3, 8, -12), false, 0), goals[4], "the target last, hard");

            // Control: no route = the target alone.
            Assert.AreEqual(1, PuzzleWalkProbe.Goals(start, Array.Empty<Vector3>(), new Vector3(1, 1, 0)).Count);
        }

        [TestMethod]
        public void Use_arrival_is_the_server_cylinder_distance_within_the_use_radius_on_a_walkable_floor()
        {
            var lever = new UseReach(0.2f, 1.5f, 2.5f, "lever wcid 1006851");
            Assert.AreEqual(0.15f, PuzzleSiteSweep.UseMargin);

            var inside = PuzzleSiteSweep.UseArrival(2.34f, lever, true, true);
            Assert.IsTrue(inside.Reached, inside.Measure);
            Assert.AreEqual("use distance 2.34 m (server cylinder distance) vs UseRadius 2.50 m less 0.15 m margin of lever wcid 1006851", inside.Measure);

            Assert.IsFalse(PuzzleSiteSweep.UseArrival(2.36f, lever, true, true).Reached, "inside the UseRadius but inside the 0.15 m margin");
            Assert.IsFalse(PuzzleSiteSweep.UseArrival(1.0f, lever, false, true).Reached, "in range but not standing on a walkable floor");

            // In range and on a floor, but not in sight: not reached (control: the same with sight is, above).
            var unseen = PuzzleSiteSweep.UseArrival(1.0f, lever, true, false);
            Assert.IsFalse(unseen.Reached);
            StringAssert.Contains(unseen.Measure, "not in sight from eye height");
            Assert.IsTrue(PuzzleSiteSweep.UseArrival(1.0f, lever, true, true).Reached);
            Assert.IsFalse(PuzzleSiteSweep.UseArrival(1.0f, lever, true, null).Reached, "sight not looked for is never a pass");

            Assert.IsTrue(PuzzleSiteSweep.ReturnArrival(0.99f, 0f, 0.6f, true).Reached);
            Assert.IsFalse(PuzzleSiteSweep.ReturnArrival(1.01f, 0f, 0.6f, true).Reached, "more than 1.0 m from the start");
            StringAssert.Contains(PuzzleSiteSweep.ReturnArrival(0.5f, 0f, 0.6f, true).Measure, "0.50 m from the start point (3D)");

            // Within 1.0 m in 3D but more than a step below the start (a ledge): not returned. Control: within a step.
            var ledge = PuzzleSiteSweep.ReturnArrival(0.95f, -0.9f, 0.6f, true);
            Assert.IsFalse(ledge.Reached);
            StringAssert.Contains(ledge.Measure, "more than the 0.60 m step-up height");
            Assert.IsTrue(PuzzleSiteSweep.ReturnArrival(0.95f, -0.55f, 0.6f, true).Reached);

            // The start rise cap: up to the step-up height above, step-up + 0.1 m below.
            Assert.IsTrue(PuzzleWalkProbe.StartRiseOk(0.6f, 0.6f));
            Assert.IsFalse(PuzzleWalkProbe.StartRiseOk(0.65f, 0.6f), "no margin upward");
            Assert.IsTrue(PuzzleWalkProbe.StartRiseOk(-0.65f, 0.6f), "the margin is downward only");
            Assert.IsFalse(PuzzleWalkProbe.StartRiseOk(-0.75f, 0.6f));

            // UseDistance itself is checked in PuzzleWalkProbeDatTests: it reaches LandDefs, whose static constructor
            // reads the portal dat once per process, so calling it here before the dats load would poison every dat test.
        }

        [TestMethod]
        public void The_probe_targets_are_every_lever_every_further_spot_and_the_focal_approach()
        {
            var focal = new ProbeTarget("focal approach", A, PuzzleSiteSweep.FocalApproach(Vector3.Zero, 0f, 7f));
            Assert.AreEqual(new Vector3(0, 5, 0), focal.Point, "2 m short of a 7 m gate point, straight ahead at yaw 0");

            var targets = PuzzleSiteSweep.ProbeTargets(
                new[] { (1, new Vector3(1, 3, 0), A), (0, new Vector3(-1, 3, 0), A), (2, new Vector3(3, 3, 0), 0u) },
                new[] { new SweepPoint(1, new Vector3(5, 5, 0), A), new SweepPoint(2, new Vector3(6, 6, 0), 0) },
                focal);

            CollectionAssert.AreEqual(new[] { "lever 1", "lever 2", "shuffle spot 2", "focal approach" }, targets.Select(t => t.Label).ToList(),
                "slot order, and a point with no cell is left to the cell check");

            Assert.AreEqual(0, PuzzleSiteSweep.ProbeTargets(Array.Empty<(int, Vector3, uint)>(), null, null).Count);
        }

        [TestMethod]
        public void Doors_the_probe_walked_through_are_named_in_the_detail_of_a_passing_row()
        {
            var through = new ProbeLeg("lever 1", "out", ProbeOutcome.Pass, Vector3.Zero, null) { DoorsPassed = new[] { "wcid 412" } };
            var back = new ProbeLeg("lever 1", "back", ProbeOutcome.Pass, Vector3.Zero, null) { DoorsPassed = new[] { "wcid 412", "wcid 7210" } };

            var o = PuzzleSiteSweep.Evaluate(Record() with { Probe = new[] { through, back } }, OneWay());

            Assert.AreEqual(PuzzleSiteSweep.Pass, o.Result, o.Detail);
            Assert.AreEqual("a-e ok (c: walk probe); doors passed: wcid 412 x2, wcid 7210 x1", o.Detail);
            Assert.IsNull(PuzzleSiteSweep.DoorsPassedNote(Record().Probe), "control: no doors, no note");
            StringAssert.Contains(through.Describe(), "pass through door wcid 412");
        }

        [TestMethod]
        public void The_probe_column_reports_blocked_before_unchecked_before_pass()
        {
            var pass = new ProbeLeg("l", "out", ProbeOutcome.Pass, Vector3.Zero, null);
            var skip = ProbeLeg.Unchecked("l", "out", "x");
            var block = ProbeLeg.Blocked("l", "out", Vector3.Zero, "x");

            Assert.AreEqual("-", PuzzleSiteSweep.ProbeSummary(null));
            Assert.AreEqual("pass 2/2", PuzzleSiteSweep.ProbeSummary(new[] { pass, pass }));
            Assert.AreEqual("unchecked 1/2", PuzzleSiteSweep.ProbeSummary(new[] { pass, skip }));
            Assert.AreEqual("blocked 1/3", PuzzleSiteSweep.ProbeSummary(new[] { pass, skip, block }));
        }

        [TestMethod]
        public void The_route_follows_the_doorways_and_goes_round_a_blocked_one()
        {
            var open = WayRound().Route(A, Vector3.Zero, B, new Vector3(0, 12, 0));
            CollectionAssert.AreEqual(new[] { GateAt }, open, "straight through the gate's doorway");

            var blocked = new HashSet<(uint, uint)> { DungeonWalkGraph.Pair(A, B) };
            var round = WayRound().Route(A, Vector3.Zero, B, new Vector3(0, 12, 0), blocked);
            CollectionAssert.AreEqual(new[] { new Vector3(10, 0, 0), new Vector3(10, 10, 0) }, round, "round through C");

            Assert.IsNull(OneWay().Route(A, Vector3.Zero, B, new Vector3(0, 12, 0), blocked), "no way round");
            Assert.AreEqual(0, OneWay().Route(A, Vector3.Zero, A, new Vector3(1, 1, 0)).Count, "same cell: no doorway");
            Assert.IsNull(OneWay().Route(A, Vector3.Zero, 0x01509999, Vector3.Zero), "an unknown cell");

            var field = OneWay().From(A, Vector3.Zero, blocked);
            Assert.IsTrue(field.Reaches(A));
            Assert.IsFalse(field.Reaches(B), "beyond the closed gate");
            Assert.IsTrue(OneWay().From(A, Vector3.Zero).Reaches(B), "control: open");
        }
        // =============================================================================================
        // TSV, summary, decisions, arguments
        // =============================================================================================

        [TestMethod]
        public void The_tsv_row_has_the_eleven_columns_in_header_order_and_no_stray_tabs()
        {
            var record = Record();
            var row = PuzzleSiteSweep.TsvRow(record, new SweepOutcome("fail", "b", "lever 1\tmoved\nfar"));
            var cells = row.Split('\t');

            Assert.AreEqual(11, PuzzleSiteSweep.TsvHeader.Split('\t').Length);
            Assert.AreEqual(PuzzleSiteSweep.TsvHeader.Split('\t').Length, cells.Length);
            CollectionAssert.AreEqual(new[] { "filos_doom", "s1", "gate", "gate", "odd", "2", "fail", "b", "pass 2/2 (0 ms)", "0", "lever 1 moved far" }, cells);

            var seeded = PuzzleSiteSweep.TsvRow(record with { Seed = 42, ProbeMs = 17 }, new SweepOutcome("pass", "-", "ok")).Split('\t');
            Assert.AreEqual("pass 2/2 (17 ms)", seeded[8], "probe landblock-thread ms per row");
            Assert.AreEqual("seed=42; ok", seeded[10], "a swept seed leads the detail");
            StringAssert.StartsWith(PuzzleSiteSweep.TsvNote, "# each row = one seed", "the header note says each row is one seed at max N");

            var disabledRow = PuzzleSiteSweep.TsvRow(new SweepRecord { Dungeon = "d", SiteId = "s", SiteKind = "reward", Role = "reward", SiteState = "disabled", Type = PuzzleGateType.Sigil, N = 4 }, new SweepOutcome("pass", "-", "ok"));
            Assert.AreEqual("reward,disabled", disabledRow.Split('\t')[2], "a disabled site is reported as such");
        }

        [TestMethod]
        public void The_summary_counts_per_dungeon_and_in_total()
        {
            var rows = new[]
            {
                (Record() with { ElapsedMs = 0 }, new SweepOutcome("pass", "-", "")),
                (Record(), new SweepOutcome("fail", "b", "")),
                (Record(role: "reward"), new SweepOutcome("fail", "d", "")),
                (Record(), new SweepOutcome("unchecked", "d", "")),
            };

            var lines = PuzzleSiteSweep.Summary(rows);

            Assert.AreEqual(2, lines.Count);
            Assert.AreEqual("filos_doom: 4 rows, 1 pass, 2 fail (1 gate-role), 1 unchecked; fails by check: b=1 d=1; elapsed 0 ms (0 ms/row, probe 0 ms on the landblock thread, start search 0 ms, worst action 0 ms)", lines[0]);
            StringAssert.StartsWith(lines[1], "TOTAL: 4 rows");
        }

        private static SweepRecord Row(string site, string kind, string role, string state = "")
            => new SweepRecord { Dungeon = "filos_doom", SiteId = site, SiteKind = kind, Role = role, SiteState = state, Type = PuzzleGateType.Sigil, N = 4 };

        [TestMethod]
        public void A_gate_failure_disables_the_site_and_a_reward_host_failure_disables_only_that_role_on_a_gate_site()
        {
            var fail = new SweepOutcome("fail", "b", "lever 1 moved");
            var pass = new SweepOutcome("pass", "-", "");

            var decisions = PuzzleSiteSweep.Decide(new[]
            {
                (Row("g1", "gate", "gate"), fail), (Row("g1", "gate", "reward"), fail),     // gate fail wins: whole site
                (Row("g2", "gate", "gate"), pass), (Row("g2", "gate", "reward"), fail),     // reward-only on a gate site
                (Row("r1", "reward", "reward"), fail),                                    // a reward site: whole site
                (Row("g3", "gate", "gate", "disabled"), fail),                            // already off: nothing
                (Row("g4", "gate", "reward", "reward-disabled"), fail),                   // already reward-off: nothing
                (Row("g5", "gate", "gate"), new SweepOutcome("unchecked", "d", "")),      // unchecked: nothing
            }, "2026-10-07T00:00:00Z");

            var byId = decisions.ToDictionary(d => d.SiteId);

            CollectionAssert.AreEquivalent(new[] { "g1", "g2", "r1" }, byId.Keys.ToList());
            Assert.IsFalse(byId["g1"].RewardOnly);
            Assert.IsTrue(byId["g2"].RewardOnly);
            Assert.IsFalse(byId["r1"].RewardOnly);
            StringAssert.StartsWith(byId["g1"].Reason, "puzzlegate-sweep 2026-10-07T00:00:00Z: gate sigil check b: lever 1 moved");
        }

        [TestMethod]
        public void Sweep_arguments_parse_and_refuse()
        {
            Assert.IsTrue(SweepArgs.TryParse(new[] { "all" }, out var all, out _));
            Assert.IsNull(all.DungeonId);
            Assert.IsNull(all.Types);
            Assert.IsFalse(all.WriteDisabled);

            Assert.IsTrue(SweepArgs.TryParse(new[] { "filos_doom", "types=beam,odd,beam", "out=C:\\x.tsv", "--write-disabled", "json=C:\\p.json" }, out var one, out _));
            Assert.AreEqual("filos_doom", one.DungeonId);
            CollectionAssert.AreEqual(new[] { PuzzleGateType.Beam, PuzzleGateType.Odd }, one.Types.ToList());
            Assert.AreEqual("C:\\x.tsv", one.OutPath);
            Assert.IsTrue(one.WriteDisabled);
            Assert.AreEqual("C:\\p.json", one.JsonPath);

            Assert.IsTrue(SweepArgs.TryParse(Array.Empty<string>(), out var none, out _));
            Assert.IsNull(none.DungeonId, "no argument = all");

            Assert.IsFalse(SweepArgs.TryParse(new[] { "types=laser" }, out _, out _));
            Assert.IsFalse(SweepArgs.TryParse(new[] { "a", "b" }, out _, out _));
            Assert.IsFalse(SweepArgs.TryParse(new[] { "speed=3" }, out _, out _));

            // seeds= (default 1) and --force.
            Assert.AreEqual(1, all.Seeds);
            Assert.IsFalse(all.Force);
            Assert.IsTrue(SweepArgs.TryParse(new[] { "filos_doom", "seeds=3", "--force" }, out var seeded, out _));
            Assert.AreEqual(3, seeded.Seeds);
            Assert.IsTrue(seeded.Force);
            Assert.IsTrue(SweepArgs.TryParse(new[] { $"seeds={SweepArgs.MaxSeeds}" }, out _, out _));

            foreach (var bad in new[] { "seeds=0", $"seeds={SweepArgs.MaxSeeds + 1}", "seeds=x", "seeds=" })
                Assert.IsFalse(SweepArgs.TryParse(new[] { bad }, out _, out var why), bad);

            // Each extra seed is a different, stable seed; k = 0 keeps the one-seed sweep's value.
            var k0 = PuzzleSiteSweepRunner.SweepSeed("filos_doom", "s1", "gate", PuzzleGateType.Sigil);
            Assert.AreEqual(k0, PuzzleSiteSweepRunner.SweepSeed("filos_doom", "s1", "gate", PuzzleGateType.Sigil, 0));
            Assert.AreNotEqual(k0, PuzzleSiteSweepRunner.SweepSeed("filos_doom", "s1", "gate", PuzzleGateType.Sigil, 1));
            Assert.AreEqual(PuzzleSiteSweepRunner.SweepSeed("filos_doom", "s1", "gate", PuzzleGateType.Sigil, 2), PuzzleSiteSweepRunner.SweepSeed("filos_doom", "s1", "gate", PuzzleGateType.Sigil, 2));
        }

        [TestMethod]
        public void The_sweep_places_each_type_at_the_most_levers_a_run_could_give_the_site()
        {
            Assert.AreEqual(5, PuzzleSiteSweepRunner.SweepN(PuzzleGateType.Sigil, 5));
            Assert.AreEqual(4, PuzzleSiteSweepRunner.SweepN(PuzzleGateType.Odd, 4));
            Assert.AreEqual(ThreadPuzzleSitePicker.LeverCount(PuzzleGateType.Beam, 5, 0), PuzzleSiteSweepRunner.SweepN(PuzzleGateType.Beam, 5));
            Assert.AreEqual(1, PuzzleSiteSweepRunner.SweepN(PuzzleGateType.Shuffle, 5));

            // Every N a run's roll can produce is at most the sweep's.
            for (uint roll = 0; roll < 20; roll++)
                foreach (var t in AllTypes)
                    Assert.IsTrue(ThreadPuzzleSitePicker.LeverCount(t, 5, roll) <= PuzzleSiteSweepRunner.SweepN(t, 5));
        }

        [TestMethod]
        public void A_gate_site_is_swept_as_a_gate_and_as_a_reward_host_a_reward_site_only_as_a_reward()
        {
            var gate = PuzzleSiteSweepRunner.Plan(Site("g"), null);
            Assert.AreEqual(8, gate.Count);
            CollectionAssert.AreEqual(new[] { "gate", "reward" }, gate.Select(r => r.Role).Distinct().ToList());

            var reward = PuzzleSiteSweepRunner.Plan(Site("r", PuzzleSiteKind.Reward), new[] { PuzzleGateType.Beam });
            Assert.AreEqual(1, reward.Count);
            Assert.AreEqual(("reward", PuzzleGateType.Beam), (reward[0].Role, reward[0].Type));

            // A disabled site is still swept (and reported as disabled).
            Assert.AreEqual(8, PuzzleSiteSweepRunner.Plan(Site("off", disabled: true), null).Count);

            Assert.AreEqual(PuzzleSiteSweepRunner.SweepSeed("d", "s", "gate", PuzzleGateType.Odd), PuzzleSiteSweepRunner.SweepSeed("d", "s", "gate", PuzzleGateType.Odd));
            Assert.AreNotEqual(PuzzleSiteSweepRunner.SweepSeed("d", "s", "gate", PuzzleGateType.Odd), PuzzleSiteSweepRunner.SweepSeed("d", "s", "reward", PuzzleGateType.Odd));
            Assert.IsTrue(PuzzleSiteSweepRunner.SweepSeed("d", "s", "gate", PuzzleGateType.Odd) > 0);
        }
    }
}
