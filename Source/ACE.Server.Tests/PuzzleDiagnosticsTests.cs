using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

using log4net;
using log4net.Appender;
using log4net.Core;
using log4net.Repository.Hierarchy;

using ACE.Server.Managers;
using ACE.Server.PuzzleGates;
using ACE.Server.Tests.ThreadDungeons;
using ACE.Server.ThreadDungeons;
using ACE.Server.ThreadDungeons.Defs;
using ACE.Server.WorldObjects;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using Position = ACE.Entity.Position;

namespace ACE.Server.Tests
{
    /// <summary>
    /// The puzzle-gate diagnostics: the per-lever cue string, the per-pull log line, the puzzle fields on the fail
    /// policy's lines, and the pure parts of /puzzlegate site. No world and no Player.
    /// </summary>
    [TestClass]
    public class PuzzleDiagnosticsTests
    {
        private static readonly DateTime T0 = new DateTime(2026, 10, 6, 12, 0, 0, DateTimeKind.Utc);

        private static uint Red => PuzzleGateColours.ByIndex(0).Script;
        private static uint Blue => PuzzleGateColours.ByIndex(1).Script;
        private static uint Green => PuzzleGateColours.ByIndex(2).Script;
        private static uint Purple => PuzzleGateColours.ByIndex(3).Script;

        private static PuzzleObjectSpec Spec(PuzzleRole role, int slot, float scale = 1f, float yawDeg = 0f, uint script = 0)
            => new PuzzleObjectSpec(role, slot, Vector3.Zero, Vector3.Zero, PuzzleGateGenerator.YawQuaternion(yawDeg), scale, script);

        // ---- cue strings ----

        [TestMethod]
        public void Sigil_cues_name_each_lever_colour_star_the_answer_and_give_the_gate_colour()
        {
            var plan = new PuzzlePlan
            {
                Type = PuzzleGateType.Sigil,
                AnswerSlot = 1,
                AnswerColourIndex = 1,
                Specs = new[]
                {
                    Spec(PuzzleRole.Candidate, 0), Spec(PuzzleRole.Candidate, 1), Spec(PuzzleRole.Candidate, 2), Spec(PuzzleRole.Candidate, 3),
                    Spec(PuzzleRole.Light, 0, script: Red), Spec(PuzzleRole.Light, 1, script: Blue), Spec(PuzzleRole.Light, 2, script: Green), Spec(PuzzleRole.Light, 3, script: Purple),
                    Spec(PuzzleRole.Indicator, 0, script: Blue),
                },
            };

            Assert.AreEqual("red,blue*,green,purple gate=blue", PuzzleGateCues.Describe(plan));
        }

        [TestMethod]
        public void Beam_cues_say_where_each_beam_points_and_a_single_beam_is_just_the_target()
        {
            var multi = new PuzzlePlan
            {
                Type = PuzzleGateType.Beam,
                AnswerSlot = 0,
                AnswerColourIndex = 1,
                BeamTargets = new[] { 2, 0, 3 },
                Specs = new[] { Spec(PuzzleRole.Light, 0, script: Red), Spec(PuzzleRole.Light, 1, script: Blue), Spec(PuzzleRole.Light, 2, script: Green) },
            };

            Assert.AreEqual("red->3,blue->1*,green->4 gate=blue", PuzzleGateCues.Describe(multi));

            var single = new PuzzlePlan
            {
                Type = PuzzleGateType.Beam,
                AnswerSlot = 2,
                AnswerColourIndex = 0,
                BeamTargets = new[] { 2 },
                Specs = new[] { Spec(PuzzleRole.Light, 0, script: Red) },
            };

            Assert.AreEqual("beam->3*", PuzzleGateCues.Describe(single));
        }

        [TestMethod]
        public void Odd_cues_show_the_differing_channel_per_lever_with_the_answer_starred()
        {
            var gateFacing = Spec(PuzzleRole.Gate, 0, yawDeg: 180f);

            var scale = new PuzzlePlan
            {
                Type = PuzzleGateType.Odd, OddChannel = PuzzleOddChannel.Scale, AnswerSlot = 2, Gate = gateFacing,
                Specs = new[] { Spec(PuzzleRole.Candidate, 0, scale: 1f), Spec(PuzzleRole.Candidate, 1, scale: 1f), Spec(PuzzleRole.Candidate, 2, scale: 1.4f), Spec(PuzzleRole.Candidate, 3, scale: 1f) },
            };

            Assert.AreEqual("scale:1,1,1.4*,1", PuzzleGateCues.Describe(scale));

            var yaw = new PuzzlePlan
            {
                Type = PuzzleGateType.Odd, OddChannel = PuzzleOddChannel.Yaw, AnswerSlot = 2, Gate = gateFacing,
                Specs = new[] { Spec(PuzzleRole.Candidate, 0, yawDeg: 180f), Spec(PuzzleRole.Candidate, 1, yawDeg: 180f), Spec(PuzzleRole.Candidate, 2, yawDeg: 270f), Spec(PuzzleRole.Candidate, 3, yawDeg: 180f) },
            };

            Assert.AreEqual("yaw:0,0,90*,0", PuzzleGateCues.Describe(yaw));

            var glow = new PuzzlePlan
            {
                Type = PuzzleGateType.Odd, OddChannel = PuzzleOddChannel.Glow, AnswerSlot = 2, Gate = gateFacing,
                Specs = new[] { Spec(PuzzleRole.Candidate, 0), Spec(PuzzleRole.Candidate, 1), Spec(PuzzleRole.Candidate, 2, script: PuzzleGateColours.GlowScript), Spec(PuzzleRole.Candidate, 3) },
            };

            Assert.AreEqual("glow:-,-,on*,-", PuzzleGateCues.Describe(glow));
        }

        [TestMethod]
        public void Shuffle_cue_is_the_one_based_spot_starred()
        {
            var plan = new PuzzlePlan { Type = PuzzleGateType.Shuffle, AnswerSlot = 1, Specs = new[] { Spec(PuzzleRole.Candidate, 1) } };
            Assert.AreEqual("spot2*", PuzzleGateCues.Describe(plan));
        }

        /// <summary>
        /// The same cues from REAL generated plans: exactly one star per string, and it sits on the lever the plan
        /// calls correct (for sigil and beam, the starred colour is the gate colour). Catches a star on the wrong
        /// slot or a plan field (BeamTargets) the generator forgot to fill.
        /// </summary>
        [TestMethod]
        public void Generated_plans_star_exactly_the_answer_lever()
        {
            for (var seed = 1; seed <= 60; seed++)
            {
                foreach (var type in new[] { "sigil", "beam", "odd" })
                {
                    var args = new List<string> { type };

                    if (type == "beam")
                        args.AddRange(new[] { "n=5", "beams=" + (2 + seed % 3) });

                    Assert.IsTrue(PuzzleGateOptions.TryParse(args, out var options, out var perr), perr);
                    var input = new PuzzleGateInput { AnchorPosition = new Vector3(96, 96, 10), AnchorYawDeg = 35f, Options = options, Seed = seed };
                    Assert.IsTrue(PuzzleGateGenerator.TryCreate(input, out var gen, out var error), error);

                    for (var round = 0; round < 3; round++)
                    {
                        var plan = gen.Next();
                        var cues = PuzzleGateCues.Describe(plan);
                        var where = $"{type} seed={seed} round={round}: {cues}";

                        Assert.AreEqual(1, cues.Count(ch => ch == '*'), where);

                        var list = cues.Split(' ')[0].Split(',');

                        if (type == "odd")
                        {
                            var starred = Array.FindIndex(list, s => s.Contains('*'));
                            Assert.AreEqual(plan.AnswerSlot, starred, where);
                        }
                        else if (type == "sigil")
                        {
                            var starred = Array.FindIndex(list, s => s.Contains('*'));
                            Assert.AreEqual(plan.AnswerSlot, starred, where);
                            Assert.AreEqual(PuzzleGateColours.ByIndex(plan.AnswerColourIndex).Name, list[starred].TrimEnd('*'), where);
                        }
                        else
                        {
                            var starred = list.Single(s => s.Contains('*'));
                            Assert.AreEqual(plan.AnswerSlot + 1, int.Parse(starred.Split("->")[1].TrimEnd('*')), where);
                            Assert.AreEqual(PuzzleGateColours.ByIndex(plan.AnswerColourIndex).Name, starred.Split("->")[0], where);
                        }
                    }
                }
            }
        }

        // ---- the per-pull line ----

        private static PuzzleGatePlacement NewPlacement(string siteId, IPuzzleGateHost host = null)
        {
            Assert.IsTrue(PuzzleGateOptions.TryParse(new[] { "sigil", "n=4" }, out var options, out var error), error);
            var anchor = new Position(0xA9B40001, 96f, 96f, 10f, 0f, 0f, 0f, 1f, 0);
            var p = new PuzzleGatePlacement(7, options, 1234, anchor, 0f, null, 0, null, T0, host) { SiteId = siteId };

            var plan = new PuzzlePlan
            {
                Type = PuzzleGateType.Sigil,
                RoundIndex = 2,
                AnswerSlot = 1,
                AnswerColourIndex = 1,
                Specs = new[]
                {
                    Spec(PuzzleRole.Light, 0, script: Red), Spec(PuzzleRole.Light, 1, script: Blue), Spec(PuzzleRole.Light, 2, script: Green), Spec(PuzzleRole.Light, 3, script: Purple),
                },
            };

            p.BeginRound(plan, new[] { (0x7F000001u, 0), (0x7F000002u, 1) }, T0);
            return p;
        }

        [TestMethod]
        public void The_pull_line_says_what_was_pulled_what_was_expected_and_what_each_lever_looked_like()
        {
            var p = NewPlacement("filos_doom_a", new ThreadPuzzleRunHost(ThreadLootTestFixtures.NewRun(), false));

            var line = PuzzleGateManager.ActivationLine(p, "Tester", 0, false, PuzzleActivation.Wrong, 4200);

            Assert.AreEqual(
                "[PUZZLE_GATE] id=7 activation player=Tester type=sigil run=" + 0x80001234u + " round=2 expected=2 pulled=1 correct=False result=Wrong ms_since_reshuffle=4200 cues=red,blue*,green,purple gate=blue",
                line);
            Assert.IsFalse(line.Contains('\n'), "one line");
        }

        [TestMethod]
        public void An_admin_pull_line_carries_run_zero()
        {
            var line = PuzzleGateManager.ActivationLine(NewPlacement(null), "Admin", 1, true, PuzzleActivation.Solved, 10);
            StringAssert.Contains(line, "run=0 ");
            StringAssert.Contains(line, "expected=2 pulled=2 correct=True");
        }

        // ---- fail / lockout lines ----

        private MemoryAppender appender;
        private Hierarchy hierarchy;
        private Level priorLevel;
        private bool priorConfigured;
        private IThreadPuzzleFailSink savedSink;

        [TestInitialize]
        public void Setup()
        {
            appender = new MemoryAppender();
            hierarchy = (Hierarchy)LogManager.GetRepository(typeof(PropertyManager).Assembly);
            priorLevel = hierarchy.Root.Level;
            priorConfigured = hierarchy.Configured;
            hierarchy.Root.AddAppender(appender);
            hierarchy.Root.Level = Level.All;
            hierarchy.Configured = true;
            savedSink = ThreadPuzzleRunHost.FailSink;
        }

        [TestCleanup]
        public void Cleanup()
        {
            hierarchy.Root.RemoveAppender(appender);
            hierarchy.Root.Level = priorLevel;
            hierarchy.Configured = priorConfigured;
            ThreadPuzzleRunHost.FailSink = savedSink;
        }

        private List<string> Policy() => appender.GetEvents().Select(e => e.RenderedMessage).Where(m => m.Contains("[PUZZLE_POLICY]")).ToList();

        [TestMethod]
        public void Fail_lockout_and_locked_pull_lines_carry_placement_site_type_and_slot()
        {
            var p = NewPlacement("steaming_hovel");
            p.LastPullSlot = 2;
            var tag = p.LogTag();
            Assert.AreEqual("placement=7 site=steaming_hovel type=sigil slot=3", tag);

            var config = ThreadPuzzlePolicyConfig.Create(true, 2, 60, 120);
            var ledger = new ThreadPuzzleIpLedger(null, ThreadPuzzleIpLedger.DefaultRetention);
            var actor = new PuzzleActor(0x50000001u, "Pat", 9, "203.0.113.7");

            ThreadPuzzleFailPolicy.RecordScoredWrong("k", actor, null, T0, config, ledger, tag);
            ThreadPuzzleFailPolicy.RecordScoredWrong("k", actor, null, T0.AddSeconds(1), config, ledger, tag);
            ThreadPuzzleFailPolicy.RecordScoredWrong("k", actor, null, T0.AddSeconds(2), config, ledger, tag);

            var lines = Policy();
            Assert.AreEqual(3, lines.Count, string.Join("\n", lines));
            StringAssert.Contains(lines[0], "[PUZZLE_POLICY] fail ");
            StringAssert.Contains(lines[1], "[PUZZLE_POLICY] LOCKOUT ");
            StringAssert.Contains(lines[2], "[PUZZLE_POLICY] pull while locked ");

            foreach (var line in lines)
                StringAssert.EndsWith(line, " " + tag, line);
        }

        [TestMethod]
        public void A_fail_without_a_puzzle_tag_prints_the_old_line_unchanged()
        {
            var config = ThreadPuzzlePolicyConfig.Create(true, 5, 60, 120);
            var ledger = new ThreadPuzzleIpLedger(null, ThreadPuzzleIpLedger.DefaultRetention);

            ThreadPuzzleFailPolicy.RecordScoredWrong("k2", new PuzzleActor(0x50000002u, "Sam", 3, "203.0.113.8"), null, T0, config, ledger);

            var line = Policy().Single();
            StringAssert.EndsWith(line, "run=");
        }

        private sealed class CapturingSink : IThreadPuzzleFailSink
        {
            public readonly List<string> Tags = new List<string>();

            public void OnScoredWrong(ThreadDungeonRun run, Player player, PuzzleGatePlacement placement) => Tags.Add(placement.LogTag());
        }

        [TestMethod]
        public void The_run_host_hands_the_sink_a_placement_that_names_its_site_type_and_slot()
        {
            var sink = new CapturingSink();
            ThreadPuzzleRunHost.FailSink = sink;
            var host = new ThreadPuzzleRunHost(ThreadLootTestFixtures.NewRun(), false);
            var p = NewPlacement("filos_doom_a", host);
            p.LastPullSlot = 0;

            host.OnWrong(p, null, true);
            host.OnWrong(p, null, false);

            Assert.AreEqual(1, sink.Tags.Count, "only the scored wrong reaches the sink");
            Assert.AreEqual("placement=7 site=filos_doom_a type=sigil slot=1", sink.Tags[0]);
        }

        [TestMethod]
        public void A_plain_admin_placement_prints_a_dash_for_site_and_slot_before_any_pull()
        {
            var p = NewPlacement(null);
            p.LastPullSlot = -1;
            Assert.AreEqual("placement=7 site=- type=sigil slot=-", p.LogTag());
        }

        // ---- /puzzlegate site: pure parts ----

        private static IReadOnlyList<string> T(params string[] tokens) => tokens;

        [TestMethod]
        public void Site_args_parse_list_place_and_step_forms()
        {
            Assert.IsTrue(ThreadPuzzleSiteTour.TryParse(T("filos_doom"), out var list, out _));
            Assert.AreEqual(PuzzleSiteAction.List, list.Action);
            Assert.AreEqual("filos_doom", list.DungeonId);
            Assert.AreEqual(1, list.Page);

            Assert.IsTrue(ThreadPuzzleSiteTour.TryParse(T("filos_doom", "page=3"), out var paged, out _));
            Assert.AreEqual(PuzzleSiteAction.List, paged.Action);
            Assert.AreEqual(3, paged.Page);

            Assert.IsTrue(ThreadPuzzleSiteTour.TryParse(T("filos_doom", "gate_a", "type=beam", "n=3", "seed=42"), out var place, out _));
            Assert.AreEqual(PuzzleSiteAction.Place, place.Action);
            Assert.AreEqual("gate_a", place.SiteId);
            Assert.AreEqual(PuzzleGateType.Beam, place.Type);
            Assert.AreEqual(3, place.N);
            Assert.AreEqual(42, place.Seed);

            Assert.IsTrue(ThreadPuzzleSiteTour.TryParse(T("NEXT"), out var next, out _));
            Assert.AreEqual(PuzzleSiteAction.Step, next.Action);
            Assert.AreEqual(1, next.Delta);

            Assert.IsTrue(ThreadPuzzleSiteTour.TryParse(T("prev"), out var prev, out _));
            Assert.AreEqual(-1, prev.Delta);
        }

        [TestMethod]
        public void Site_args_reject_bad_input()
        {
            Assert.IsFalse(ThreadPuzzleSiteTour.TryParse(T(), out _, out var e1)); Assert.IsNotNull(e1);
            Assert.IsFalse(ThreadPuzzleSiteTour.TryParse(T("d", "s", "type=cube"), out _, out _));
            Assert.IsFalse(ThreadPuzzleSiteTour.TryParse(T("d", "s", "n=zero"), out _, out _));
            Assert.IsFalse(ThreadPuzzleSiteTour.TryParse(T("d", "type=beam"), out _, out _), "options without a site");
            Assert.IsFalse(ThreadPuzzleSiteTour.TryParse(T("next", "extra"), out _, out _));
            Assert.IsFalse(ThreadPuzzleSiteTour.TryParse(T("d", "s", "bogus=1"), out _, out _));
        }

        [TestMethod]
        public void The_cursor_wraps_both_ways_and_a_fresh_cursor_starts_at_either_end()
        {
            Assert.AreEqual(0, ThreadPuzzleSiteTour.Step(-1, 5, 1));
            Assert.AreEqual(4, ThreadPuzzleSiteTour.Step(-1, 5, -1));
            Assert.AreEqual(3, ThreadPuzzleSiteTour.Step(2, 5, 1));
            Assert.AreEqual(0, ThreadPuzzleSiteTour.Step(4, 5, 1), "wraps forward");
            Assert.AreEqual(4, ThreadPuzzleSiteTour.Step(0, 5, -1), "wraps back");
            Assert.AreEqual(0, ThreadPuzzleSiteTour.Step(0, 1, 1));
            Assert.AreEqual(-1, ThreadPuzzleSiteTour.Step(0, 0, 1), "no sites");
        }

        private static PuzzleSiteDef Site(string id, int maxN, string kind = "gate", params PuzzleGateType[] types)
            => new PuzzleSiteDef
            {
                Id = id,
                KindName = kind,
                Kind = kind == "reward" ? PuzzleSiteKind.Reward : PuzzleSiteKind.Gate,
                MaxN = maxN,
                Types = types.ToList(),
                TypeNames = types.Select(t => t.ToString().ToLowerInvariant()).ToList(),
                Anchor = new PuzzleSitePointDef { Cell = 0x01500100, X = 10, Y = 10, Z = 0 },
                Doorway = new PuzzleDoorwayDef { Width = 3.5f, Height = 4f },
                GateModel = new PuzzleGateModelDef { KindName = "door", Kind = PuzzleGateModelKind.Door, Wcid = 1001, Scale = 1f, Panels = 1 },
                ShuffleSpots = new List<PuzzleSitePointDef>(),
            };

        [TestMethod]
        public void The_pick_defaults_to_the_first_eligible_type_and_the_site_max_n()
        {
            var site = Site("s1", 5, "gate", PuzzleGateType.Shuffle, PuzzleGateType.Beam, PuzzleGateType.Sigil);

            Assert.IsTrue(ThreadPuzzleSiteTour.TryBuildPick(site, new PuzzleSiteArgs(), 9, out var pick, out var error), error);
            Assert.AreEqual(PuzzleGateType.Beam, pick.Type, "shuffle is listed first but has no spots");
            Assert.AreEqual(ThreadPuzzleSitePicker.BeamN, pick.N, "a beam site gets the picker's beam count (capped at BeamN), not maxN");
            Assert.AreEqual(9, pick.Seed);
            Assert.IsFalse(pick.IsReward);

            Assert.IsTrue(ThreadPuzzleSiteTour.TryBuildPick(site, new PuzzleSiteArgs { Type = PuzzleGateType.Sigil, N = 4 }, 9, out pick, out error), error);
            Assert.AreEqual(PuzzleGateType.Sigil, pick.Type);
            Assert.AreEqual(4, pick.N);

            Assert.IsFalse(ThreadPuzzleSiteTour.TryBuildPick(site, new PuzzleSiteArgs { Type = PuzzleGateType.Shuffle }, 9, out _, out error));
            StringAssert.Contains(error, "Eligible");
            Assert.IsFalse(ThreadPuzzleSiteTour.TryBuildPick(site, new PuzzleSiteArgs { N = 6 }, 9, out _, out _), "above the site's maxN");

            Assert.IsTrue(ThreadPuzzleSiteTour.TryBuildPick(Site("r1", 4, "reward", PuzzleGateType.Odd), new PuzzleSiteArgs(), 1, out pick, out _));
            Assert.IsTrue(pick.IsReward, "a reward site picks as a reward (the focal model)");
            Assert.AreEqual(4, pick.N);
        }

        [TestMethod]
        public void The_default_lever_count_is_the_run_pickers_rule_for_every_type_and_max_n()
        {
            foreach (var maxN in new[] { 2, 3, 4, 5 })
            {
                foreach (var seed in new[] { 1, 2, 3, 40, 123457 })
                {
                    foreach (var type in new[] { PuzzleGateType.Sigil, PuzzleGateType.Odd, PuzzleGateType.Beam })
                    {
                        var site = Site("s", maxN, "gate", type);

                        if (ThreadPuzzleSitePicker.EligibleTypes(site).Count == 0)
                            continue;

                        Assert.IsTrue(ThreadPuzzleSiteTour.TryBuildPick(site, new PuzzleSiteArgs { Type = type }, seed, out var pick, out var error), error);
                        Assert.AreEqual(ThreadPuzzleSitePicker.LeverCount(type, maxN, (uint)seed), pick.N, $"{type} maxN={maxN} seed={seed}");
                    }
                }
            }

            var spots = Site("sh", 3, "gate", PuzzleGateType.Shuffle);
            spots.ShuffleSpots = new List<PuzzleSitePointDef> { new PuzzleSitePointDef(), new PuzzleSitePointDef() };
            Assert.IsTrue(ThreadPuzzleSiteTour.TryBuildPick(spots, new PuzzleSiteArgs(), 5, out var shuffle, out _));
            Assert.AreEqual(ThreadPuzzleSitePicker.LeverCount(PuzzleGateType.Shuffle, 3, 5), shuffle.N);
        }

        [TestMethod]
        public void The_arrival_decision_places_waits_times_out_and_yields_to_a_newer_command()
        {
            const double limit = 30;

            Assert.AreEqual(SiteArrival.Place, ThreadPuzzleSiteTour.DecideArrival(false, true, true, 0.3, limit));
            Assert.AreEqual(SiteArrival.Wait, ThreadPuzzleSiteTour.DecideArrival(false, true, false, 1, limit), "arrived but still populating");
            Assert.AreEqual(SiteArrival.Wait, ThreadPuzzleSiteTour.DecideArrival(false, false, true, 1, limit), "the teleport has not landed yet (fog delay)");
            Assert.AreEqual(SiteArrival.TimedOut, ThreadPuzzleSiteTour.DecideArrival(false, false, false, 31, limit));
            Assert.AreEqual(SiteArrival.Place, ThreadPuzzleSiteTour.DecideArrival(false, true, true, 31, limit), "a ready arrival places even past the bound");
            Assert.AreEqual(SiteArrival.Superseded, ThreadPuzzleSiteTour.DecideArrival(true, true, true, 0.3, limit), "an old poll never places over a newer command");
            Assert.AreEqual(SiteArrival.Superseded, ThreadPuzzleSiteTour.DecideArrival(true, false, false, 99, limit), "and never reports a timeout either");
        }

        [TestMethod]
        public void A_site_placement_never_ambushes_but_the_admin_tool_still_manages_it()
        {
            Assert.IsFalse(SiteTourPuzzleGateHost.Instance.AllowAmbush);
            Assert.AreEqual(0u, SiteTourPuzzleGateHost.Instance.RunId);
            Assert.IsTrue(AdminPuzzleGateHost.Instance.AllowAmbush, "control: the plain admin host still ambushes");

            var p = NewPlacement("s", SiteTourPuzzleGateHost.Instance);
            Assert.IsTrue(PuzzleGateManager.IsAdminManaged(p));
        }

        [TestMethod]
        public void Only_a_live_admin_managed_placement_is_cleared_by_the_next_site_command()
        {
            var site = NewPlacement("s", SiteTourPuzzleGateHost.Instance);
            Assert.IsTrue(PuzzleGateManager.ShouldClearPrevious(site));

            Assert.IsFalse(PuzzleGateManager.ShouldClearPrevious(null));
            Assert.IsFalse(PuzzleGateManager.ShouldClearPrevious(NewPlacement("s", new ThreadPuzzleRunHost(ThreadLootTestFixtures.NewRun(), false))), "a run's placement is never cleared by the tool");

            var gone = NewPlacement("s", SiteTourPuzzleGateHost.Instance);
            gone.MarkCleared();
            Assert.IsFalse(PuzzleGateManager.ShouldClearPrevious(gone), "already cleared");
        }

        [TestMethod]
        public void A_single_beam_from_a_real_plan_stars_its_one_target()
        {
            for (var seed = 1; seed <= 40; seed++)
            {
                Assert.IsTrue(PuzzleGateOptions.TryParse(new[] { "beam", "n=4", "beams=1" }, out var options, out var perr), perr);
                var input = new PuzzleGateInput { AnchorPosition = new Vector3(96, 96, 10), AnchorYawDeg = 0f, Options = options, Seed = seed };
                Assert.IsTrue(PuzzleGateGenerator.TryCreate(input, out var gen, out var error), error);

                var plan = gen.Next();
                Assert.AreEqual($"beam->{plan.AnswerSlot + 1}*", PuzzleGateCues.Describe(plan), $"seed={seed}");
            }
        }

        [TestMethod]
        public void A_big_dungeon_lists_in_pages_with_a_pointer_to_the_next()
        {
            var sites = Enumerable.Range(1, 56).Select(i => Site($"site_{i:00}", 4, "gate", PuzzleGateType.Sigil)).ToList();

            var first = ThreadPuzzleSiteTour.ListPage("big", sites, 1);
            Assert.AreEqual(1 + ThreadPuzzleSiteTour.PageSize + 1, first.Count);
            StringAssert.Contains(first[0], "56 puzzle site(s), page 1/5");
            StringAssert.Contains(first[1], "site_01");
            StringAssert.Contains(first[1], "gate=door wcid=1001");
            StringAssert.Contains(first[1], "doorway=3.5x4");
            StringAssert.Contains(first[^1], "page=2");

            var last = ThreadPuzzleSiteTour.ListPage("big", sites, 5);
            Assert.AreEqual(1 + 8, last.Count, "56 = 4 * 12 + 8, and no pointer after the last page");

            var clamped = ThreadPuzzleSiteTour.ListPage("big", sites, 99);
            StringAssert.Contains(clamped[0], "page 5/5");
        }

        [TestMethod]
        public void Site_ids_resolve_case_insensitively_and_the_valid_list_truncates()
        {
            var sites = Enumerable.Range(1, 40).Select(i => Site($"Site_{i:00}", 4, "gate", PuzzleGateType.Sigil)).ToList();

            Assert.AreEqual(2, ThreadPuzzleSiteTour.IndexOf(sites, "site_03"));
            Assert.AreEqual(-1, ThreadPuzzleSiteTour.IndexOf(sites, "nope"));
            StringAssert.Contains(ThreadPuzzleSiteTour.ValidSites(sites, 30), "(+10 more");
        }
    }
}
