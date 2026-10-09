using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

using ACE.Entity;
using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Entity.Models;
using ACE.Server.ThreadDungeons;
using ACE.Server.ThreadDungeons.Defs;
using ACE.Server.WorldObjects;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ACE.Server.Tests.ThreadDungeons
{
    /// <summary>
    /// Thread-Guide chunk 3: the game wiring. Every decision is driven through ThreadGuideFlow's pure surface with
    /// a fake IGuideHolder (ACE.Server.Tests cannot build a live Player), the manager seams through their swappable
    /// delegates the way the survey tests do, and the Player-only wiring (the notice's latch order, the EndRun call
    /// site, the ActOnUse dispatch) is pinned on source text.
    /// </summary>
    [TestClass]
    public class ThreadGuideWiringTests
    {
        private const uint Owner = 0x50000010u;
        private const uint Other = 0x50000020u;

        private static uint nextGuid = 0x7D100000;

        private static DungeonGemSpec GuideFragment(int rung, int serial = 3, uint owner = Owner)
            => ThreadGuideFlow.BuildFragmentSpec(new GuideTag(rung, serial, owner));

        private static DungeonGemSpec NormalFragment()
            => new DungeonGemSpec(DungeonGemSpec.Any, 185, 7, DungeonGemSpec.Any, 0, null, 0, 0);

        private static Func<string, int> Doses(params string[] loadedTypes)
            => type => loadedTypes.Count(t => t == type);

        // ---------------------------------------------------------------------------------------------
        // Fake holder
        // ---------------------------------------------------------------------------------------------

        private sealed class FakeHolder : IGuideHolder
        {
            public uint Guid { get; set; } = Owner;
            public int GuideLevel { get; set; }
            public int GrantSerial { get; set; }
            public bool Outstanding;
            public bool Room = true;
            public bool GiveSucceeds = true;
            public readonly List<GuideTag> Given = new List<GuideTag>();
            public readonly List<(double Percent, long Cap)> Xp = new List<(double, long)>();
            public readonly List<string> Events = new List<string>();

            public bool HasOutstandingGuideItem() => Outstanding;
            public bool HasRoomForFragment() => Room;

            public bool TryGiveFragment(GuideTag tag)
            {
                Events.Add("give");
                if (!GiveSucceeds)
                    return false;
                Given.Add(tag);
                return true;
            }

            public void GrantXp(double percent, long cap)
            {
                Events.Add("xp");
                Xp.Add((percent, cap));
            }

            public int Saves;
            public int StaleToRemove;

            public void Save()
            {
                Saves++;
                Events.Add($"save(level={GuideLevel},serial={GrantSerial})");
            }

            public int RemoveStaleGuideItems()
            {
                Events.Add("purge");
                var n = StaleToRemove;
                StaleToRemove = 0;
                return n;
            }
        }

        // ---------------------------------------------------------------------------------------------
        // Press gate
        // ---------------------------------------------------------------------------------------------

        [TestMethod]
        public void Press_refuses_a_guide_fragment_owned_by_someone_else_or_with_an_old_serial()
        {
            var spec = GuideFragment(50, serial: 3).WithLoad(new[] { (691u, 1) });

            var otherOwner = ThreadGuideFlow.DecidePress(spec, Other, 3, Doses(RawFragmentRules.ScarabType), 1);
            Assert.AreEqual(ThreadGuideText.Stale, otherOwner.Refusal);

            var oldSerial = ThreadGuideFlow.DecidePress(spec, Owner, 4, Doses(RawFragmentRules.ScarabType), 1);
            Assert.AreEqual(ThreadGuideText.Stale, oldSerial.Refusal);

            var current = ThreadGuideFlow.DecidePress(spec, Owner, 3, Doses(RawFragmentRules.ScarabType), 1);
            Assert.IsNull(current.Refusal, "control: the same fragment presses for its owner at the current serial");
        }

        [TestMethod]
        public void Press_refuses_a_special_rung_without_a_dose_of_its_required_type()
        {
            foreach (var rung in ThreadGuideLadder.Rungs.Where(r => r.RequiredType != null))
            {
                var spec = GuideFragment(rung.Level);
                var bare = ThreadGuideFlow.DecidePress(spec, Owner, 3, Doses(), 1);
                Assert.AreEqual($"This Guide Fragment needs a {ThreadGuideText.TypeWord(rung.RequiredTypes[0])} before the Press will take it.", bare.Refusal, $"rung {rung.Level}");

                // A dose of some OTHER type does not satisfy it.
                var otherType = rung.RequiredType == RawFragmentRules.HerbType ? RawFragmentRules.PotionType : RawFragmentRules.HerbType;
                Assert.IsNotNull(ThreadGuideFlow.DecidePress(spec, Owner, 3, Doses(otherType), 1).Refusal, $"rung {rung.Level} with only a {otherType}");

                var loaded = ThreadGuideFlow.DecidePress(spec, Owner, 3, Doses(rung.RequiredTypes.ToArray()), 1);
                Assert.IsNull(loaded.Refusal, $"rung {rung.Level} loaded");
            }
        }

        [TestMethod]
        public void Press_waives_the_fee_and_guarantees_the_required_type_for_a_guide_fragment()
        {
            var spec = GuideFragment(75);
            var d = ThreadGuideFlow.DecidePress(spec, Owner, 3, Doses(RawFragmentRules.TaperType), 5);

            Assert.IsNull(d.Refusal);
            Assert.AreEqual(0, d.Fee, "guide fragments press free");
            CollectionAssert.AreEqual(new[] { RawFragmentRules.TaperType }, d.GuaranteedTypes.ToArray());
            Assert.AreEqual(RawFragmentRules.TaperType, d.ExplainType);
            Assert.IsTrue(d.IsGuide);

            // A plain guide rung is free too, but has no required type, guarantee or explanation.
            var plain = ThreadGuideFlow.DecidePress(GuideFragment(225), Owner, 3, Doses(), 5);
            Assert.IsNull(plain.Refusal);
            Assert.AreEqual(0, plain.Fee);
            Assert.AreEqual(0, plain.GuaranteedTypes.Count);
            Assert.IsNull(plain.ExplainType);
        }

        [TestMethod]
        public void Press_leaves_a_normal_fragment_unchanged()
        {
            var d = ThreadGuideFlow.DecidePress(NormalFragment(), Owner, 3, Doses(), 1);

            Assert.IsNull(d.Refusal);
            Assert.AreEqual(1, d.Fee, "a normal fragment is charged the configured fee");
            Assert.AreEqual(0, d.GuaranteedTypes.Count, "and gets no guarantee");
            Assert.IsNull(d.ExplainType);
            Assert.IsFalse(d.IsGuide);
        }

        /// <summary>The Press wires the decider ahead of the room check and the fee, and passes the guarantee on.</summary>
        [TestMethod]
        public void Press_source_runs_the_guide_gate_before_the_room_check_and_charges_its_fee()
        {
            var body = PooledLootSourceText.MethodBody(PooledLootSourceText.Read("Source/ACE.Server/ThreadDungeons/FragmentPressStation.cs"),
                "public static void Press(Player player, Gem fragment, WorldObject press)");

            var gate = body.IndexOf("ThreadGuideFlow.DecidePress(", StringComparison.Ordinal);
            var room = body.IndexOf("HasRoomFor(player", StringComparison.Ordinal);
            var fee = body.IndexOf("RefireStationCommon.TryCharge", StringComparison.Ordinal);

            Assert.IsTrue(gate > 0 && room > gate && fee > room, $"gate={gate} room={room} fee={fee}");
            StringAssert.Contains(body, "var feeNotes = guide.Fee;");
            StringAssert.Contains(body, "BuildLimits(guide.GuaranteedTypes)");
            StringAssert.Contains(body, "ThreadGuideItems.Bind(gem)");
        }

        // ---------------------------------------------------------------------------------------------
        // Gem use
        // ---------------------------------------------------------------------------------------------

        [TestMethod]
        public void Gem_use_guide_gem_goes_solo_with_the_level_50_floor_and_tells_a_fellowed_player()
        {
            var gem = GuideFragment(100).WithSeed(77);

            var fellowed = ThreadGuideFlow.DecideGemUse(gem, Owner, 3, 120, fellowed: true);
            Assert.IsNull(fellowed.Refusal);
            Assert.AreEqual(ThreadGuideRules.MinPlayerLevel, fellowed.MinPlayerLevel, "the guide floor replaces the tunable");
            Assert.IsTrue(fellowed.ForceSolo);
            Assert.IsTrue(fellowed.TellSolo);

            var alone = ThreadGuideFlow.DecideGemUse(gem, Owner, 3, 120, fellowed: false);
            Assert.IsTrue(alone.ForceSolo);
            Assert.IsFalse(alone.TellSolo, "no solo line for a player with no fellowship");

            // The floor is applied by DungeonGemRules.Decide: 50 opens, 49 does not, even though the tunable says 120.
            Assert.AreEqual(GemUseDecision.StartRun, DungeonGemRules.Decide(gem, Owner, 3, false, true, 50, fellowed.MinPlayerLevel, out _));
            Assert.AreEqual(GemUseDecision.Refuse, DungeonGemRules.Decide(gem, Owner, 3, false, true, 49, fellowed.MinPlayerLevel, out _));
        }

        [TestMethod]
        public void Gem_use_refuses_a_stale_guide_gem()
        {
            var gem = GuideFragment(100, serial: 2).WithSeed(77);

            Assert.AreEqual(ThreadGuideText.Stale, ThreadGuideFlow.DecideGemUse(gem, Owner, 3, 120, false).Refusal);
            Assert.AreEqual(ThreadGuideText.Stale, ThreadGuideFlow.DecideGemUse(gem, Other, 2, 120, false).Refusal);
            Assert.IsNull(ThreadGuideFlow.DecideGemUse(gem, Owner, 2, 120, false).Refusal, "control");
        }

        [TestMethod]
        public void Gem_use_normal_gem_is_unchanged()
        {
            var gem = NormalFragment().WithSeed(77);
            var d = ThreadGuideFlow.DecideGemUse(gem, Owner, 0, 120, fellowed: true);

            Assert.IsNull(d.Refusal);
            Assert.AreEqual(120, d.MinPlayerLevel, "the tunable floor stays");
            Assert.IsFalse(d.ForceSolo, "a fellowed normal gem still gets the group offer");
            Assert.IsFalse(d.TellSolo);
        }

        [TestMethod]
        public void Gem_use_source_forces_solo_before_the_group_offer()
        {
            var body = PooledLootSourceText.MethodBody(PooledLootSourceText.Read("Source/ACE.Server/ThreadDungeons/ThreadDungeonGemHandler.cs"),
                "private static bool StartRun(Gem gem, Player player, DungeonGemSpec spec, int minPlayerLevel, GuideGemUseDecision guide)");

            var solo = body.IndexOf("if (guide.ForceSolo)", StringComparison.Ordinal);
            var offer = body.IndexOf("OfferGroupStart(", StringComparison.Ordinal);
            Assert.IsTrue(solo > 0 && offer > solo, $"solo={solo} offer={offer}");
        }

        // ---------------------------------------------------------------------------------------------
        // NPC grant flow
        // ---------------------------------------------------------------------------------------------

        [TestMethod]
        public void Npc_grant_increments_the_serial_only_on_a_successful_give()
        {
            var ok = new FakeHolder { GrantSerial = 4 };
            Assert.AreEqual(GuideIssueOutcome.Given, ThreadGuideFlow.Issue(ok, 50));
            Assert.AreEqual(5, ok.GrantSerial);
            Assert.AreEqual(new GuideTag(50, 5, Owner), ok.Given.Single(), "the fragment carries the NEW serial and the owner");

            var full = new FakeHolder { GrantSerial = 4, Room = false };
            Assert.AreEqual(GuideIssueOutcome.NoRoom, ThreadGuideFlow.Issue(full, 50));
            Assert.AreEqual(4, full.GrantSerial, "a full pack leaves the serial");
            Assert.AreEqual(0, full.Events.Count, "and builds nothing");

            var failed = new FakeHolder { GrantSerial = 4, GiveSucceeds = false };
            Assert.AreEqual(GuideIssueOutcome.GiveFailed, ThreadGuideFlow.Issue(failed, 50));
            Assert.AreEqual(4, failed.GrantSerial, "a failed give leaves the serial");
        }

        [TestMethod]
        public void Npc_use_grants_the_next_rung_and_says_it_after_stamping()
        {
            var holder = new FakeHolder { GuideLevel = 50, GrantSerial = 1 };
            var tells = new List<string>();
            var popups = new List<string>();

            var d = ThreadGuideFlow.HandleNpcUse(holder, true, 80, false, t => { Assert.AreEqual(2, holder.GrantSerial, "stamped before the tell"); tells.Add(t); }, popups.Add);

            Assert.IsTrue(d.Granted);
            Assert.AreEqual(75, d.Rung);
            CollectionAssert.AreEqual(new[] { ThreadGuideText.Grant75 }, tells);
            Assert.AreEqual(ThreadGuideText.ComposePopup(75), popups.Single());
        }

        [TestMethod]
        public void Npc_use_refusals_say_the_matching_line_and_issue_nothing()
        {
            void Check(string expected, bool enabled, int level, int won, bool outstanding, bool liveRun)
            {
                var holder = new FakeHolder { GuideLevel = won, GrantSerial = 1, Outstanding = outstanding };
                var tells = new List<string>();
                var d = ThreadGuideFlow.HandleNpcUse(holder, enabled, level, liveRun, tells.Add, _ => Assert.Fail("no popup on a refusal"));
                Assert.IsFalse(d.Granted);
                CollectionAssert.AreEqual(new[] { expected }, tells);
                Assert.AreEqual(1, holder.GrantSerial);
                Assert.AreEqual(0, holder.Given.Count);
            }

            Check(ThreadGuideText.RefuseDisabled, false, 80, 0, false, false);
            Check(ThreadGuideText.RefuseLevel, true, 49, 0, false, false);
            Check(ThreadGuideText.RefuseComplete, true, 400, 375, false, false);
            Check(ThreadGuideText.RefuseOutstanding, true, 80, 0, true, false);
            Check(ThreadGuideText.RefuseOutstanding, true, 80, 0, false, true);
        }

        [TestMethod]
        public void Npc_use_with_a_full_pack_says_so_and_keeps_the_serial()
        {
            var holder = new FakeHolder { GrantSerial = 1, Room = false };
            var tells = new List<string>();
            ThreadGuideFlow.HandleNpcUse(holder, true, 80, false, tells.Add, _ => Assert.Fail("no popup when nothing was given"));

            CollectionAssert.AreEqual(new[] { ThreadGuideText.RefusePackFull }, tells);
            Assert.AreEqual(1, holder.GrantSerial);
        }

        private static Gem MakeItem(string spec)
        {
            var weenie = new Weenie
            {
                WeenieClassId = 1003615,
                WeenieType = WeenieType.Gem,
                PropertiesInt = new Dictionary<PropertyInt, int> { { PropertyInt.ItemType, (int)ItemType.Gem } },
                PropertiesString = new Dictionary<PropertyString, string> { { PropertyString.Name, "Fragment" } },
            };

            if (spec != null)
                weenie.PropertiesString[PropertyString.DungeonGemSpec] = spec;

            return new Gem(weenie, new ObjectGuid(nextGuid++));
        }

        private static Container MakeSidePack()
        {
            var weenie = new Weenie
            {
                WeenieClassId = 136,
                WeenieType = WeenieType.Container,
                PropertiesInt = new Dictionary<PropertyInt, int> { { PropertyInt.ItemsCapacity, 10 } },
            };

            return new Container(weenie, new ObjectGuid(nextGuid++));
        }

        [TestMethod]
        public void Outstanding_scan_finds_a_current_guide_item_in_a_side_pack_and_ignores_a_stale_one()
        {
            var pack = MakeSidePack();
            Assert.IsTrue(pack.TryAddToInventory(MakeItem(GuideFragment(50, serial: 3).Serialize())));

            var top = new List<WorldObject> { MakeItem(NormalFragment().Serialize()), MakeItem(null), pack };
            Assert.IsTrue(ThreadGuideFlow.HasOutstandingGuideItem(top, Owner, 3), "a current guide fragment inside a side pack counts");

            Assert.IsFalse(ThreadGuideFlow.HasOutstandingGuideItem(top, Owner, 4), "the same item at an older serial is stale and does not count");
            Assert.IsFalse(ThreadGuideFlow.HasOutstandingGuideItem(top, Other, 3), "nor does someone else's");

            // A pressed guide gem counts as well as a fragment.
            var gemTop = new List<WorldObject> { MakeItem(GuideFragment(75, serial: 4).WithSeed(9).Serialize()) };
            Assert.IsTrue(ThreadGuideFlow.HasOutstandingGuideItem(gemTop, Owner, 4));

            Assert.IsFalse(ThreadGuideFlow.HasOutstandingGuideItem(new List<WorldObject> { MakeItem("v2|garbage") }, Owner, 3), "an unreadable spec is ignored");
            Assert.IsFalse(ThreadGuideFlow.HasOutstandingGuideItem(null, Owner, 3));
        }

        [TestMethod]
        public void Live_guide_run_means_a_starting_or_active_guide_run_of_this_owner()
        {
            var dungeon = new DungeonEntryDef { Id = "filos_doom", Name = "Filos Doom", Landblock = 0x0150, ExitPortalWcid = 1003601 };
            ThreadDungeonRun Run(DungeonGemSpec spec, uint owner = Owner)
                => new ThreadDungeonRun(0x80001234u, owner, "Tester", 7, 0x80000099u, spec, dungeon, DateTime.UtcNow, TimeSpan.FromMinutes(180));

            var guideSpec = GuideFragment(50).WithSeed(5);

            var starting = Run(guideSpec);
            Assert.AreEqual(ThreadDungeonRunState.Starting, starting.State);
            Assert.IsTrue(ThreadGuideFlow.HasLiveGuideRun(new[] { starting }, Owner));
            Assert.IsFalse(ThreadGuideFlow.HasLiveGuideRun(new[] { starting }, Other), "someone else's run");
            Assert.IsFalse(ThreadGuideFlow.HasLiveGuideRun(new[] { Run(NormalFragment().WithSeed(5)) }, Owner), "a normal run");

            var active = Run(guideSpec);
            active.MarkPopulated(5, 5, 0);
            Assert.AreEqual(ThreadDungeonRunState.Active, active.State);
            Assert.IsTrue(ThreadGuideFlow.HasLiveGuideRun(new[] { active }, Owner));

            var cleared = Run(guideSpec);
            cleared.MarkPopulated(0, 0, 0);
            Assert.AreEqual(ThreadDungeonRunState.Cleared, cleared.State);
            Assert.IsFalse(ThreadGuideFlow.HasLiveGuideRun(new[] { cleared }, Owner), "a cleared run does not hold the next fragment");
        }

        [TestMethod]
        public void Fragment_spec_takes_rung_tier_and_tag_from_the_ladder()
        {
            foreach (var rung in ThreadGuideLadder.Rungs)
            {
                var spec = GuideFragment(rung.Level, serial: 9);
                Assert.AreEqual(rung.Level, spec.Level);
                Assert.AreEqual(rung.Tier, spec.Tier);
                Assert.AreEqual(0, spec.Seed, "a fragment is unpressed");
                Assert.AreEqual(DungeonGemSpec.Any, spec.DungeonId);
                Assert.AreEqual(new GuideTag(rung.Level, 9, Owner), spec.Guide);
                Assert.IsTrue(DungeonGemSpec.TryParse(spec.Serialize(), out var back, out var error), error);
                Assert.AreEqual(spec.Guide, back.Guide);
            }

            Assert.AreEqual("Guide Fragment (Level 50)", ThreadGuideText.ComposeFragmentName(50));
        }

        [TestMethod]
        public void Npc_dispatch_sits_beside_the_archivist_in_ActOnUse()
        {
            var body = PooledLootSourceText.MethodBody(PooledLootSourceText.Read("Source/ACE.Server/WorldObjects/Creature.cs"),
                "public override void ActOnUse(WorldObject worldObject)");

            var archivist = body.IndexOf("SurveyArchivistStation.TryHandleUse(this, surveyor)", StringComparison.Ordinal);
            var guide = body.IndexOf("ThreadGuideStation.TryHandleUse(this, guided)", StringComparison.Ordinal);
            Assert.IsTrue(archivist > 0 && guide > archivist, $"archivist={archivist} guide={guide}");
        }

        // ---------------------------------------------------------------------------------------------
        // Clear seam
        // ---------------------------------------------------------------------------------------------

        private static IReadOnlyList<ulong> SyntheticChart()
        {
            var totals = new List<ulong> { 0 };
            for (var level = 0; level < 400; level++)
                totals.Add(totals[level] + (ulong)(1000 + 10 * level));
            return totals;
        }

        [TestMethod]
        public void Clear_advances_on_the_matching_rung_and_pays_the_rung_cap()
        {
            var chart = SyntheticChart();
            var holder = new FakeHolder { GuideLevel = 50 };
            var said = new List<string>();

            var advanced = ThreadGuideFlow.ApplyClear(holder, GuideFragment(75).WithSeed(3), 1.0, r => ThreadGuideLadder.RewardCap(r, chart),
                t => { Assert.AreEqual(75, holder.GuideLevel, "the rung is written before the message"); said.Add(t); });

            Assert.IsTrue(advanced);
            Assert.AreEqual(75, holder.GuideLevel);
            Assert.AreEqual((1.0, ThreadGuideLadder.RewardCap(75, chart)), holder.Xp.Single(), "pays percent with the rung's cap");
            Assert.AreEqual(1750L, holder.Xp.Single().Cap, "1000 + 10 * 75");
            CollectionAssert.AreEqual(new[] { "You have cleared your level 75 guide thread. Return to the Thread-Guide for your next fragment." }, said);
        }

        [TestMethod]
        public void Clear_of_an_old_or_skipped_rung_pays_nothing_and_does_not_move_the_ladder()
        {
            var chart = SyntheticChart();

            foreach (var rung in new[] { 50, 75, 125 })
            {
                var holder = new FakeHolder { GuideLevel = 75 };
                var said = new List<string>();

                Assert.IsFalse(ThreadGuideFlow.ApplyClear(holder, GuideFragment(rung).WithSeed(3), 1.0, r => ThreadGuideLadder.RewardCap(r, chart), said.Add), $"rung {rung}");
                Assert.AreEqual(75, holder.GuideLevel, $"rung {rung}");
                Assert.AreEqual(0, holder.Xp.Count, $"rung {rung}: a replayed rung must not pay");
                Assert.AreEqual(0, said.Count);
            }

            // A normal run never touches the ladder.
            var plain = new FakeHolder { GuideLevel = 0 };
            Assert.IsFalse(ThreadGuideFlow.ApplyClear(plain, NormalFragment().WithSeed(3), 1.0, r => 1000, _ => { }));
            Assert.AreEqual(0, plain.Xp.Count);
        }

        [TestMethod]
        public void Clear_with_a_zero_cap_latches_the_rung_but_pays_no_xp()
        {
            var holder = new FakeHolder { GuideLevel = 0 };
            var said = new List<string>();

            Assert.IsTrue(ThreadGuideFlow.ApplyClear(holder, GuideFragment(50).WithSeed(3), 1.0, r => 0, said.Add));
            Assert.AreEqual(50, holder.GuideLevel);
            Assert.AreEqual(0, holder.Xp.Count, "a cap of 0 means UNCAPPED to GrantLevelProportionalXp, so it must never be passed");
            Assert.AreEqual(1, said.Count);

            var zeroPercent = new FakeHolder { GuideLevel = 0 };
            ThreadGuideFlow.ApplyClear(zeroPercent, GuideFragment(50).WithSeed(3), 0.0, r => 500, _ => { });
            Assert.AreEqual(50, zeroPercent.GuideLevel);
            Assert.AreEqual(0, zeroPercent.Xp.Count, "percent 0 pays nothing");
        }

        [TestMethod]
        public void Clear_latches_the_rung_even_when_the_payment_throws()
        {
            var holder = new FakeHolder { GuideLevel = 0 };
            Exception seen = null;

            Assert.IsTrue(ThreadGuideFlow.ApplyClear(holder, GuideFragment(50).WithSeed(3), 1.0, r => throw new InvalidOperationException("boom"), _ => { }, ex => seen = ex));
            Assert.AreEqual(50, holder.GuideLevel);
            Assert.IsNotNull(seen);
        }

        private static ThreadDungeonRun SoloRun(DungeonGemSpec spec)
        {
            var dungeon = new DungeonEntryDef { Id = "filos_doom", Name = "Filos Doom", Landblock = 0x0150, ExitPortalWcid = 1003601 };
            return new ThreadDungeonRun(0x80001234u, Owner, "Tester", 7, 0x80000099u, spec, dungeon, DateTime.UtcNow, TimeSpan.FromMinutes(180));
        }

        [TestMethod]
        public void AnnounceCleared_calls_the_guide_and_counter_seams_once_after_the_survey()
        {
            var run = SoloRun(GuideFragment(50).WithSeed(3));
            run.MarkPopulated(0, 0, 0);

            var order = new List<string>();
            var originalGem = ThreadDungeonManager.GemDestroyer;
            var originalSurvey = ThreadDungeonManager.SurveyRecorder;
            var originalReward = ThreadDungeonManager.ClearRewardHandler;
            var originalGuide = ThreadDungeonManager.GuideRecorder;
            var originalCounter = ThreadDungeonManager.ClearCounter;
            ThreadDungeonManager.GemDestroyer = _ => order.Add("gem");
            ThreadDungeonManager.SurveyRecorder = _ => order.Add("survey");
            ThreadDungeonManager.ClearRewardHandler = (_, __) => { };
            ThreadDungeonManager.GuideRecorder = _ => order.Add("guide");
            ThreadDungeonManager.ClearCounter = _ => order.Add("count");

            try
            {
                ThreadDungeonManager.OnRunPopulated(run);
                ThreadDungeonManager.OnRunPopulated(run);
            }
            finally
            {
                ThreadDungeonManager.GemDestroyer = originalGem;
                ThreadDungeonManager.SurveyRecorder = originalSurvey;
                ThreadDungeonManager.ClearRewardHandler = originalReward;
                ThreadDungeonManager.GuideRecorder = originalGuide;
                ThreadDungeonManager.ClearCounter = originalCounter;
            }

            CollectionAssert.AreEqual(new[] { "gem", "survey", "guide", "count" }, order);
        }

        [TestMethod]
        public void The_production_guide_and_counter_seams_are_safe_with_nobody_online()
        {
            // PlayerManager.GetOnlinePlayer is null for every guid under the harness, so both return before any
            // PropertyManager read (which would throw here).
            var guide = SoloRun(GuideFragment(50).WithSeed(3));
            guide.MarkPopulated(5, 5, 0);
            ThreadGuideStation.RecordGuideClear(guide);
            ThreadDungeonManager.RecordClearCount(guide);

            var normal = SoloRun(NormalFragment().WithSeed(3));
            ThreadGuideStation.RecordGuideClear(normal);
            ThreadGuideStation.RegrantAfterFail(normal);
        }

        // ---------------------------------------------------------------------------------------------
        // Counter seam
        // ---------------------------------------------------------------------------------------------

        private static RosterMemberSnapshot Snap(uint guid, bool isOwner, bool entered)
            => new RosterMemberSnapshot { Guid = guid, Name = $"P{guid:X}", IsOwner = isOwner, Entered = entered };

        [TestMethod]
        public void Counter_counts_every_survey_recipient_and_respects_the_nothing_spawned_gate()
        {
            var roster = new[] { Snap(Owner, true, false), Snap(Other, false, true) };

            CollectionAssert.AreEqual(new[] { Owner, Other }, ThreadGuideFlow.ClearCountRecipients(roster, _ => true, spawned: 12).ToArray());
            CollectionAssert.AreEqual(ThreadDungeonManager.SurveyRecipients(roster, g => g != Other).ToArray(),
                ThreadGuideFlow.ClearCountRecipients(roster, g => g != Other, spawned: 12).ToArray(), "the survey's recipients exactly");
            Assert.AreEqual(0, ThreadGuideFlow.ClearCountRecipients(roster, _ => true, spawned: 0).Count, "nothing spawned counts nothing");
        }

        // ---------------------------------------------------------------------------------------------
        // Fail seam
        // ---------------------------------------------------------------------------------------------

        [TestMethod]
        public void Fail_regrants_a_same_rung_fragment_and_says_so()
        {
            var holder = new FakeHolder { GuideLevel = 50, GrantSerial = 2 };
            var said = new List<string>();

            Assert.AreEqual(GuideIssueOutcome.Given, ThreadGuideFlow.ApplyFailRegrant(holder, 75, false, true, said.Add));
            Assert.AreEqual(3, holder.GrantSerial);
            Assert.AreEqual(new GuideTag(75, 3, Owner), holder.Given.Single());
            CollectionAssert.AreEqual(new[] { "Your guide thread closed before it was cleared. A fresh level 75 Guide Fragment is in your pack. Load and press it as before." }, said);
        }

        [TestMethod]
        public void Fail_with_a_full_pack_or_a_failed_give_sends_the_visit_line()
        {
            foreach (var holder in new[] { new FakeHolder { GrantSerial = 2, Room = false }, new FakeHolder { GrantSerial = 2, GiveSucceeds = false } })
            {
                var said = new List<string>();
                Assert.AreNotEqual(GuideIssueOutcome.Given, ThreadGuideFlow.ApplyFailRegrant(holder, 75, false, true, said.Add));
                Assert.AreEqual(2, holder.GrantSerial);
                CollectionAssert.AreEqual(new[] { ThreadGuideText.FailVisit }, said);
            }
        }

        [TestMethod]
        public void Fail_does_nothing_while_something_guide_related_is_outstanding()
        {
            var item = new FakeHolder { GrantSerial = 2, Outstanding = true };
            var said = new List<string>();
            Assert.IsNull(ThreadGuideFlow.ApplyFailRegrant(item, 75, false, true, said.Add));
            Assert.IsNull(ThreadGuideFlow.ApplyFailRegrant(new FakeHolder { GrantSerial = 2 }, 75, true, true, said.Add));
            Assert.AreEqual(0, said.Count);
            Assert.AreEqual(2, item.GrantSerial);
        }

        [TestMethod]
        public void Fail_regrant_fires_only_for_a_guide_run_that_never_cleared()
        {
            var guide = GuideFragment(75).WithSeed(3);
            var normal = NormalFragment().WithSeed(3);

            Assert.IsTrue(ThreadGuideFlow.ShouldRegrantOnEnd(ThreadDungeonRunState.Active, guide));
            Assert.IsTrue(ThreadGuideFlow.ShouldRegrantOnEnd(ThreadDungeonRunState.Starting, guide));
            Assert.IsFalse(ThreadGuideFlow.ShouldRegrantOnEnd(ThreadDungeonRunState.Cleared, guide), "a cleared run earned its next fragment at the NPC");
            Assert.IsFalse(ThreadGuideFlow.ShouldRegrantOnEnd(ThreadDungeonRunState.Active, normal), "a normal run regrants nothing");
        }

        [TestMethod]
        public void EndRun_calls_the_fail_seam_after_OnRunEnded_on_the_prior_state()
        {
            var body = PooledLootSourceText.MethodBody(PooledLootSourceText.Read("Source/ACE.Server/ThreadDungeons/ThreadDungeonManager.cs"),
                "public static void EndRun(ThreadDungeonRun run, string why)");

            var ended = body.IndexOf("OnRunEnded(run);", StringComparison.Ordinal);
            var gate = body.IndexOf("ThreadGuideFlow.ShouldRegrantOnEnd(priorState, run.Spec)", StringComparison.Ordinal);
            var call = body.IndexOf("GuideFailRegranter(run);", StringComparison.Ordinal);
            Assert.IsTrue(ended > 0 && gate > ended && call > gate, $"ended={ended} gate={gate} call={call}");
        }

        // ---------------------------------------------------------------------------------------------
        // Notice
        // ---------------------------------------------------------------------------------------------

        [TestMethod]
        public void Notice_fires_once_at_level_50_and_never_when_disabled()
        {
            Func<bool> yes = () => true;
            Assert.IsTrue(ThreadGuideFlow.ShouldShowNotice(true, yes, false, 50, 0, 0, false));
            Assert.IsTrue(ThreadGuideFlow.ShouldShowNotice(true, yes, false, 275, 0, 0, false));
            Assert.IsFalse(ThreadGuideFlow.ShouldShowNotice(true, yes, false, 49, 0, 0, false));
            Assert.IsFalse(ThreadGuideFlow.ShouldShowNotice(true, yes, true, 80, 0, 0, false), "latched: shown once only");
            Assert.IsFalse(ThreadGuideFlow.ShouldShowNotice(false, yes, false, 80, 0, 0, false), "disabled means no notice");
            Assert.IsFalse(ThreadGuideFlow.ShouldShowNotice(true, () => false, false, 80, 0, 0, false), "no Thread-Guide weenie, no notice");
            Assert.IsFalse(ThreadGuideFlow.ShouldShowNotice(true, yes, false, 80, 3, 0, false), "a player with Thread clears is a veteran");
            Assert.IsFalse(ThreadGuideFlow.ShouldShowNotice(true, yes, false, 80, 0, 50, false), "a player with a guide rung is a veteran");
            Assert.IsFalse(ThreadGuideFlow.ShouldShowNotice(true, yes, false, 80, 0, 0, true), "a player with any filed survey is a veteran");
        }

        [TestMethod]
        public void Notice_latches_before_sending_and_is_hooked_on_level_up_and_login()
        {
            var body = PooledLootSourceText.MethodBody(PooledLootSourceText.Read("Source/ACE.Server/WorldObjects/Player_ThreadGuide.cs"),
                "public void SendThreadGuideNoticeIfDue()");

            var latch = body.IndexOf("ThreadGuideNoticeShown = true;", StringComparison.Ordinal);
            var save = body.IndexOf("SaveBiotaToDatabase();", StringComparison.Ordinal);
            var popup = body.IndexOf("GameEventPopupString", StringComparison.Ordinal);
            var chat = body.IndexOf("GameMessageSystemChat", StringComparison.Ordinal);
            Assert.IsTrue(latch > 0 && save > latch && popup > save && chat > save, $"latch={latch} save={save} popup={popup} chat={chat}");

            StringAssert.Contains(PooledLootSourceText.Read("Source/ACE.Server/WorldObjects/Player_Xp.cs"), "SendThreadGuideNoticeIfDue();");
            StringAssert.Contains(PooledLootSourceText.Read("Source/ACE.Server/WorldObjects/Player_Networking.cs"), "SendThreadGuideNoticeIfDue();");
        }

        // ---------------------------------------------------------------------------------------------
        // Text: popups, descriptions, CHAMP
        // ---------------------------------------------------------------------------------------------

        [TestMethod]
        public void Each_popup_starts_with_the_title_then_step_one()
        {
            foreach (var rung in ThreadGuideLadder.Rungs)
            {
                var popup = ThreadGuideText.ComposePopup(rung.Level);
                StringAssert.StartsWith(popup, $"Guide Fragment (Level {rung.Level})\n\n1. Buy ", $"rung {rung.Level}");
                StringAssert.Contains(popup, "\n3. Use the fragment on the Fragment Press. Pressing Guide Fragments is free.\n\nThen use the finished gem to enter your thread alone, and clear it for your reward and your next fragment.");
                Assert.IsTrue(popup.All(c => c == '\n' || (c >= 0x20 && c < 0x7F)), $"rung {rung.Level}: ASCII only");
            }

            Assert.AreEqual(
                "Guide Fragment (Level 50)\n\n1. Buy a Lead Scarab from the Reagent-Steward - it is the gentlest; most other Scarabs raise the thread's level.\n2. Use this fragment on the Scarab.\n3. Use the fragment on the Fragment Press. Pressing Guide Fragments is free.\n\nThen use the finished gem to enter your thread alone, and clear it for your reward and your next fragment.",
                ThreadGuideText.ComposePopup(50));
            StringAssert.Contains(ThreadGuideText.ComposePopup(75), "1. Buy a Taper from the Reagent-Steward.\nAny colour except Orange.\n2. Use this fragment on the Taper.\n");
            StringAssert.Contains(ThreadGuideText.ComposePopup(125), "1. Buy a Powder such as Powdered Amber or Powdered Azurite from the Reagent-Steward.\nChoose a named Powder; Agate, Bloodstone, Carnelian and Moonstone pick at random.\n2. Use this fragment on the Powder.\n");
            StringAssert.Contains(ThreadGuideText.ComposePopup(150), "1. Buy a Red Taper and a Hazel Talisman from the Reagent-Steward.\n2. Use this fragment on the Red Taper, then on the Hazel Talisman.\n");
            StringAssert.Contains(ThreadGuideText.ComposePopup(175), "1. Buy a Potion such as Brimstone or Cadmia from the Reagent-Steward.\n2. Use this fragment on the Potion.\n");
            StringAssert.Contains(ThreadGuideText.ComposePopup(100), "1. Buy an Herb such as Hyssop or Mandrake from the Reagent-Steward.\n2. Use this fragment on the Herb.\n");
        }

        private static AttunementDef Defs()
        {
            var components = new Dictionary<uint, ComponentDef>
            {
                [691] = new ComponentDef { Wcid = 691, Name = "Lead Scarab", Type = RawFragmentRules.ScarabType, Limit = 1 },
                [774] = new ComponentDef { Wcid = 774, Name = "Hyssop", Type = RawFragmentRules.HerbType, Limit = 1 },
                [746] = new ComponentDef { Wcid = 746, Name = "Hazel Talisman", Type = RawFragmentRules.TalismanType, Limit = 1 },
                [1650] = new ComponentDef { Wcid = 1650, Name = "Red Taper", Type = RawFragmentRules.TaperType, Limit = 4 },
            };

            return new AttunementDef(components, new Dictionary<string, string>());
        }

        [TestMethod]
        public void Special_guide_fragment_description_starts_with_REQUIRED_and_switches_when_loaded()
        {
            var defs = Defs();
            var fresh = GuideFragment(50);

            var before = ThreadGuideText.ComposeFragmentLongDesc("BASE", fresh, t => RawFragmentRules.DosesOfType(fresh, defs, t));
            Assert.AreEqual("REQUIRED: Use this fragment on a Scarab, then use it on the Fragment Press.\nBASE", before);

            var loaded = RawFragmentRules.Load(fresh, defs.Components[691]);
            var after = ThreadGuideText.ComposeFragmentLongDesc("BASE", loaded, t => RawFragmentRules.DosesOfType(loaded, defs, t));
            Assert.AreEqual("Scarab loaded. Use this fragment on the Fragment Press.\nBASE", after);

            // A dose of another type leaves the REQUIRED line in place.
            var herb = RawFragmentRules.Load(fresh, defs.Components[774]);
            StringAssert.StartsWith(ThreadGuideText.ComposeFragmentLongDesc("BASE", herb, t => RawFragmentRules.DosesOfType(herb, defs, t)), "REQUIRED: ");

            // Rung 150 names the concrete pair, and switches only once BOTH a Taper and a Talisman are in.
            var r150 = GuideFragment(150);
            const string required150 = "REQUIRED: Use this fragment on a Red Taper and a Hazel Talisman, then use it on the Fragment Press.\n";
            StringAssert.StartsWith(ThreadGuideText.ComposeFragmentLongDesc("BASE", r150, Doses()), required150);
            var talismanOnly = RawFragmentRules.Load(r150, defs.Components[746]);
            StringAssert.StartsWith(ThreadGuideText.ComposeFragmentLongDesc("BASE", talismanOnly, t => RawFragmentRules.DosesOfType(talismanOnly, defs, t)), required150, "the Talisman alone does not switch it");
            var taperOnly = RawFragmentRules.Load(r150, defs.Components[1650]);
            StringAssert.StartsWith(ThreadGuideText.ComposeFragmentLongDesc("BASE", taperOnly, t => RawFragmentRules.DosesOfType(taperOnly, defs, t)), required150, "the Taper alone does not switch it");
            var both = RawFragmentRules.Load(taperOnly, defs.Components[746]);
            Assert.AreEqual("Taper and Talisman loaded. Use this fragment on the Fragment Press.\nBASE", ThreadGuideText.ComposeFragmentLongDesc("BASE", both, t => RawFragmentRules.DosesOfType(both, defs, t)));
        }

        [TestMethod]
        public void Plain_guide_fragments_normal_fragments_and_gems_get_no_requirement_line()
        {
            Assert.AreEqual("BASE", ThreadGuideText.ComposeFragmentLongDesc("BASE", GuideFragment(200), Doses()));
            Assert.AreEqual("BASE", ThreadGuideText.ComposeFragmentLongDesc("BASE", NormalFragment(), Doses()));
            Assert.AreEqual("BASE", ThreadGuideText.ComposeFragmentLongDesc("BASE", GuideFragment(50).WithSeed(4), Doses()), "a pressed guide gem");
        }

        [TestMethod]
        public void Fragment_descriptions_are_composed_by_one_helper_at_creation_and_after_a_dose()
        {
            var factory = PooledLootSourceText.Read("Source/ACE.Server/ThreadDungeons/DungeonGemFactory.cs");
            StringAssert.Contains(factory, "fragment.SetProperty(PropertyString.LongDesc, ComposeFragmentLongDesc(spec, entryCount, maxCount));");

            var load = PooledLootSourceText.Read("Source/ACE.Server/ThreadDungeons/RawFragment.cs");
            StringAssert.Contains(load, "DungeonGemFactory.ComposeFragmentLongDesc(loaded, fragment.Structure ?? 0, fragment.MaxStructure ?? 0)");
        }

        [TestMethod]
        public void Grant_tells_use_the_fragment_on_the_component()
        {
            foreach (var rung in new[] { 50, 75, 100, 125, 150, 175 })
            {
                var tell = ThreadGuideText.ComposeGrant(rung);
                Assert.IsTrue(tell.IndexOf("use the fragment on ", StringComparison.OrdinalIgnoreCase) >= 0, $"rung {rung}: {tell}");
                Assert.IsFalse(tell.IndexOf("combine", StringComparison.OrdinalIgnoreCase) >= 0, $"rung {rung}");
            }

            StringAssert.Contains(ThreadGuideText.ComposeGrant(75), "Any colour works except Orange,");
            Assert.AreEqual("Your level 225 Guide Fragment. Load whatever components you like and press it. You know the craft now.", ThreadGuideText.ComposeGrant(225));
        }

        [TestMethod]
        public void Grant_tells_and_required_lines_name_examples_for_herb_powder_and_potion()
        {
            Assert.AreEqual("Level 100. Use the fragment on an Herb such as Hyssop or Mandrake, then press it.", ThreadGuideText.Grant100);
            Assert.AreEqual("Level 125. Use the fragment on a Powder such as Powdered Amber or Powdered Azurite. Choose a named one; Agate, Bloodstone, Carnelian and Moonstone pick at random. Then press it.", ThreadGuideText.Grant125);
            Assert.AreEqual("Level 175. Use the fragment on a Potion such as Brimstone or Cadmia, then press it.", ThreadGuideText.Grant175);

            Assert.AreEqual("REQUIRED: Use this fragment on an Herb such as Hyssop or Mandrake, then use it on the Fragment Press.", ThreadGuideText.ComposeRequirementLine(GuideFragment(100), Doses()));
            Assert.AreEqual("REQUIRED: Use this fragment on a Powder such as Powdered Amber or Powdered Azurite, then use it on the Fragment Press.", ThreadGuideText.ComposeRequirementLine(GuideFragment(125), Doses()));
            Assert.AreEqual("REQUIRED: Use this fragment on a Potion such as Brimstone or Cadmia, then use it on the Fragment Press.", ThreadGuideText.ComposeRequirementLine(GuideFragment(175), Doses()));
            Assert.AreEqual("REQUIRED: Use this fragment on a Taper, then use it on the Fragment Press.", ThreadGuideText.ComposeRequirementLine(GuideFragment(75), Doses()));
            Assert.AreEqual("Potion loaded. Use this fragment on the Fragment Press.", ThreadGuideText.ComposeRequirementLine(GuideFragment(175), Doses(RawFragmentRules.PotionType)));
        }

        /// <summary>
        /// The example rule, checked against the Reagent-Steward's real stock: a type keeps its plain word only when
        /// every one of its stocked item names carries that word, and every example name is a stocked item.
        /// </summary>
        [TestMethod]
        public void Component_examples_are_real_reagent_steward_stock()
        {
            string file = null;
            for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null && file == null; dir = dir.Parent)
            {
                var candidate = Path.Combine(dir.FullName, "Content", "sql", "weenies", "1003602 Reagent-Steward Aya Torimaru.sql");
                if (File.Exists(candidate))
                    file = candidate;
            }

            if (file == null)
                Assert.Inconclusive("Could not locate the Reagent-Steward SQL by walking up from the test assembly.");

            var stock = Regex.Matches(File.ReadAllText(file), @"\(1003602, 4,\s+\d+, -1, 0, 0, False\) /\* (.+?) -\s")
                .Cast<Match>().Select(m => m.Groups[1].Value.Trim()).ToList();
            Assert.IsTrue(stock.Count >= 70, $"parsed {stock.Count} stock rows");

            foreach (var word in new[] { "Scarab", "Taper", "Talisman" })
                Assert.IsTrue(stock.Any(n => n.EndsWith(" " + word)), $"{word}: stocked names carry the word, so it stays plain");

            foreach (var name in new[] { "Hyssop", "Mandrake", "Brimstone", "Cadmia", "Powdered Amber", "Powdered Azurite" })
                CollectionAssert.Contains(stock, name, $"{name} must be a stocked item");

            Assert.IsFalse(stock.Any(n => n.Split(' ').Contains("Herb") || n.Split(' ').Contains("Potion") || n.Split(' ').Contains("Powder")),
                "no stocked name carries Herb, Potion or Powder as a word, which is why those three get examples");
        }

        // ---------------------------------------------------------------------------------------------
        // Review round (owner rulings): kill switch, persistence, stale purge, gates, empty run, rung 150
        // ---------------------------------------------------------------------------------------------

        [TestMethod]
        public void Fail_regrant_with_the_guide_switched_off_issues_nothing_and_says_visit()
        {
            var holder = new FakeHolder { GrantSerial = 2 };
            var said = new List<string>();

            Assert.AreEqual(GuideIssueOutcome.Disabled, ThreadGuideFlow.ApplyFailRegrant(holder, 75, false, enabled: false, said.Add));
            Assert.AreEqual(0, holder.Events.Count, "Issue was not called: no give, no save");
            Assert.AreEqual(2, holder.GrantSerial);
            CollectionAssert.AreEqual(new[] { "Your guide thread closed before it was cleared." }, said, "no NPC pointer: the NPC would refuse too");

            StringAssert.Contains(PooledLootSourceText.MethodBody(PooledLootSourceText.Read("Source/ACE.Server/ThreadDungeons/ThreadGuideStation.cs"),
                "public static void RegrantAfterFail(ThreadDungeonRun run)"), "ReadAvailable()");
        }

        [TestMethod]
        public void A_successful_give_saves_right_after_the_serial_and_before_any_tell()
        {
            var holder = new FakeHolder { GuideLevel = 0, GrantSerial = 1 };
            var tells = new List<string>();

            ThreadGuideFlow.HandleNpcUse(holder, true, 80, false, t => { Assert.AreEqual(1, holder.Saves, "saved before the tell"); tells.Add(t); }, _ => Assert.AreEqual(1, holder.Saves));

            CollectionAssert.AreEqual(new[] { "purge", "give", "save(level=0,serial=2)" }, holder.Events);
            Assert.AreEqual(1, tells.Count);

            var regrant = new FakeHolder { GrantSerial = 4 };
            ThreadGuideFlow.ApplyFailRegrant(regrant, 50, false, true, _ => Assert.AreEqual(1, regrant.Saves, "saved before the regrant line"));
            CollectionAssert.AreEqual(new[] { "give", "save(level=0,serial=5)" }, regrant.Events);

            var failed = new FakeHolder { GrantSerial = 4, GiveSucceeds = false };
            ThreadGuideFlow.Issue(failed, 50);
            Assert.AreEqual(0, failed.Saves, "nothing to save when the give failed");
        }

        [TestMethod]
        public void A_clear_saves_after_the_rung_and_the_xp_and_before_the_tell()
        {
            var holder = new FakeHolder { GuideLevel = 50 };

            ThreadGuideFlow.ApplyClear(holder, GuideFragment(75).WithSeed(3), 1.0, r => 900, _ => Assert.AreEqual(1, holder.Saves, "saved before the tell"));

            CollectionAssert.AreEqual(new[] { "xp", "save(level=75,serial=0)" }, holder.Events, "saved after the XP and before the tell");

            var replay = new FakeHolder { GuideLevel = 75 };
            ThreadGuideFlow.ApplyClear(replay, GuideFragment(75).WithSeed(3), 1.0, r => 900, _ => { });
            Assert.AreEqual(0, replay.Saves, "no advance, no save");
        }

        [TestMethod]
        public void Stale_scan_lists_a_faded_item_in_a_side_pack_and_never_a_current_one()
        {
            var pack = MakeSidePack();
            var stale = MakeItem(GuideFragment(50, serial: 2).Serialize());
            var current = MakeItem(GuideFragment(75, serial: 3).Serialize());
            var normal = MakeItem(NormalFragment().Serialize());
            Assert.IsTrue(pack.TryAddToInventory(stale));

            var found = ThreadGuideFlow.FindStaleGuideItems(new List<WorldObject> { current, normal, MakeItem(null), pack }, Owner, 3);

            CollectionAssert.AreEqual(new WorldObject[] { stale }, found, "only the faded copy, from inside the side pack");
            Assert.AreEqual(0, ThreadGuideFlow.FindStaleGuideItems(new List<WorldObject> { current }, Owner, 3).Count, "a current item is never taken");
        }

        [TestMethod]
        public void Npc_use_takes_back_faded_items_first_and_says_so_once()
        {
            var holder = new FakeHolder { GrantSerial = 3, StaleToRemove = 2 };
            var tells = new List<string>();

            ThreadGuideFlow.HandleNpcUse(holder, true, 80, false, tells.Add, _ => { });

            Assert.AreEqual("purge", holder.Events[0], "the purge runs before the decision");
            Assert.AreEqual(ThreadGuideText.TakesBackFaded, tells[0]);
            Assert.AreEqual(1, tells.Count(t => t == ThreadGuideText.TakesBackFaded), "once per use, however many went");
            Assert.AreEqual("The Thread-Guide takes back a faded Guide Fragment.", ThreadGuideText.TakesBackFaded);

            var clean = new FakeHolder { GrantSerial = 3 };
            var cleanTells = new List<string>();
            ThreadGuideFlow.HandleNpcUse(clean, true, 80, false, cleanTells.Add, _ => { });
            Assert.IsFalse(cleanTells.Contains(ThreadGuideText.TakesBackFaded), "nothing removed, nothing said");
        }

        [TestMethod]
        public void Guide_is_available_only_with_threads_press_and_guide_all_on()
        {
            Assert.IsTrue(ThreadGuideFlow.GuideAvailable(true, true, true));
            Assert.IsFalse(ThreadGuideFlow.GuideAvailable(false, true, true), "Threads off");
            Assert.IsFalse(ThreadGuideFlow.GuideAvailable(true, false, true), "Press off");
            Assert.IsFalse(ThreadGuideFlow.GuideAvailable(true, true, false), "guide off");

            Assert.AreEqual("dynamic_dungeons_press_enabled", FragmentPressStation.EnabledProperty);

            var station = PooledLootSourceText.Read("Source/ACE.Server/ThreadDungeons/ThreadGuideStation.cs");
            StringAssert.Contains(station, "ThreadGuideFlow.HandleNpcUse(holder, ReadAvailable(),");
        }

        [TestMethod]
        public void Notice_does_not_latch_when_any_gate_is_closed()
        {
            void Run(bool expectFired, string why, bool available = true, bool npc = true, bool shown = false, long level = 80, int clears = 0, int guideLevel = 0, bool surveys = false)
            {
                var order = new List<string>();
                var fired = ThreadGuideFlow.SendNoticeIfDue(available, () => npc, shown, level, clears, guideLevel, surveys, () => order.Add("latch"), () => order.Add("send"));

                Assert.AreEqual(expectFired, fired, why);
                CollectionAssert.AreEqual(expectFired ? new[] { "latch", "send" } : new string[0], order, why);
            }

            Run(true, "all gates open: latch BEFORE send");
            Run(false, "guide unavailable (Threads, Press or guide off)", available: false);
            Run(false, "Thread-Guide weenie missing", npc: false);
            Run(false, "already shown", shown: true);
            Run(false, "below 50", level: 49);
            Run(false, "veteran by clears", clears: 1);
            Run(false, "veteran by rung", guideLevel: 50);
            Run(false, "veteran by survey history", surveys: true);
        }

        /// <summary>
        /// The NPC lookup is the costly gate, so it runs LAST and lazily: never when an earlier gate is closed, once
        /// when every other gate is open.
        /// </summary>
        [TestMethod]
        public void Notice_npc_lookup_runs_last_and_only_for_a_candidate()
        {
            var lookups = 0;
            Func<bool> npc = () => { lookups++; return true; };

            ThreadGuideFlow.ShouldShowNotice(false, npc, false, 80, 0, 0, false);
            ThreadGuideFlow.ShouldShowNotice(true, npc, true, 80, 0, 0, false);
            ThreadGuideFlow.ShouldShowNotice(true, npc, false, 49, 0, 0, false);
            ThreadGuideFlow.ShouldShowNotice(true, npc, false, 80, 1, 0, false);
            ThreadGuideFlow.ShouldShowNotice(true, npc, false, 80, 0, 50, false);
            ThreadGuideFlow.ShouldShowNotice(true, npc, false, 80, 0, 0, true);
            Assert.AreEqual(0, lookups, "no lookup for anyone a cheaper gate refuses");

            Assert.IsTrue(ThreadGuideFlow.ShouldShowNotice(true, npc, false, 80, 0, 0, false));
            Assert.AreEqual(1, lookups);

            var body = PooledLootSourceText.MethodBody(PooledLootSourceText.Read("Source/ACE.Server/WorldObjects/Player_ThreadGuide.cs"), "public void SendThreadGuideNoticeIfDue()");
            var available = body.IndexOf("ThreadGuideStation.ReadAvailable()", StringComparison.Ordinal);
            var send = body.IndexOf("ThreadGuideFlow.SendNoticeIfDue(", StringComparison.Ordinal);
            Assert.IsTrue(available > 0 && send > available, "availability is read first");
            StringAssert.Contains(body, "ThreadGuideStation.NpcWeenieExists,");
            StringAssert.Contains(body, "DungeonSurveyLevels");
        }

        [TestMethod]
        public void Presence_cache_keeps_a_hit_forever_and_a_miss_for_five_minutes()
        {
            var t0 = new DateTime(2026, 10, 5, 12, 0, 0, DateTimeKind.Utc);
            var lookups = 0;
            var exists = false;
            Func<bool> lookup = () => { lookups++; return exists; };

            var cache = new ThreadGuideFlow.PresenceCache();
            Assert.IsFalse(cache.Check(lookup, t0));
            Assert.IsFalse(cache.Check(lookup, t0.AddMinutes(4.9)));
            Assert.AreEqual(1, lookups, "a miss is cached for five minutes");

            exists = true;
            Assert.IsTrue(cache.Check(lookup, t0.AddMinutes(5)));
            Assert.AreEqual(2, lookups, "after five minutes it looks again");

            exists = false;
            Assert.IsTrue(cache.Check(lookup, t0.AddDays(30)));
            Assert.AreEqual(2, lookups, "a hit is kept forever");
            Assert.AreEqual(TimeSpan.FromMinutes(5), ThreadGuideFlow.PresenceCache.NegativeTtl);
        }

        [TestMethod]
        public void Notice_reads_every_gate_and_names_the_archivist_as_his_weenie_does()
        {
            var body = PooledLootSourceText.MethodBody(PooledLootSourceText.Read("Source/ACE.Server/WorldObjects/Player_ThreadGuide.cs"),
                "public void SendThreadGuideNoticeIfDue()");
            StringAssert.Contains(body, "ThreadGuideStation.ReadAvailable()");
            StringAssert.Contains(PooledLootSourceText.Read("Source/ACE.Server/ThreadDungeons/ThreadGuideStation.cs"), "GetCachedWeenie(ThreadGuideNpcWcid) != null");
            Assert.AreEqual(1003605u, ThreadGuideStation.ThreadGuideNpcWcid);

            var station = PooledLootSourceText.ExpressionBody(PooledLootSourceText.Read("Source/ACE.Server/ThreadDungeons/ThreadGuideStation.cs"), "public static bool ReadAvailable()");
            StringAssert.Contains(station, "ThreadDungeonManager.IsEnabled");
            StringAssert.Contains(station, "FragmentPressStation.EnabledProperty");
            StringAssert.Contains(station, "PropertyManager.GetBool(EnabledProperty)");

            var teodor = PooledLootSourceText.Read("Content/sql/weenies/1003613 Survey-Archivist Teodor Brannock.sql");
            var name = Regex.Match(teodor, @"\(1003613,\s+1, '([^']+)'\) /\* Name \*/").Groups[1].Value;
            Assert.AreEqual("Survey-Archivist Teodor Brannock", name);
            Assert.AreEqual($"You are ready to run your first Thread. Find the Thread-Guide in the Drift Network, beside {name}.", ThreadGuideText.Notice);
        }

        [TestMethod]
        public void Clear_seam_branches_and_the_empty_run_line()
        {
            var guide = GuideFragment(50).WithSeed(3);

            Assert.AreEqual(ThreadGuideFlow.ClearSeamAction.Apply, ThreadGuideFlow.DecideClearSeam(guide, true, 5));
            Assert.AreEqual(ThreadGuideFlow.ClearSeamAction.Empty, ThreadGuideFlow.DecideClearSeam(guide, true, 0));
            Assert.AreEqual(ThreadGuideFlow.ClearSeamAction.Skip, ThreadGuideFlow.DecideClearSeam(guide, false, 5), "offline owner: no credit");
            Assert.AreEqual(ThreadGuideFlow.ClearSeamAction.Skip, ThreadGuideFlow.DecideClearSeam(NormalFragment().WithSeed(3), true, 5));

            Assert.AreEqual("That thread held nothing to face. Speak with the Thread-Guide for another fragment.", ThreadGuideText.EmptyThread);

            var body = PooledLootSourceText.MethodBody(PooledLootSourceText.Read("Source/ACE.Server/ThreadDungeons/ThreadGuideStation.cs"), "public static void RecordGuideClear(ThreadDungeonRun run)");
            var empty = body.IndexOf("case ThreadGuideFlow.ClearSeamAction.Empty:", StringComparison.Ordinal);
            var say = body.IndexOf("Say(owner, ThreadGuideText.EmptyThread);", StringComparison.Ordinal);
            var apply = body.IndexOf("owner.EnqueueAction(", StringComparison.Ordinal);
            Assert.IsTrue(empty > 0 && say > empty && apply > say, $"empty={empty} say={say} apply={apply}");
        }

        [TestMethod]
        public void Login_reminder_fires_only_for_a_player_left_without_a_fragment()
        {
            Assert.IsTrue(ThreadGuideFlow.ShouldSendLoginReminder(true, 50, 2, false, false));
            Assert.IsFalse(ThreadGuideFlow.ShouldSendLoginReminder(false, 50, 2, false, false), "guide unavailable");
            Assert.IsFalse(ThreadGuideFlow.ShouldSendLoginReminder(true, 375, 2, false, false), "ladder complete");
            Assert.IsFalse(ThreadGuideFlow.ShouldSendLoginReminder(true, 0, 0, false, false), "never had a fragment");
            Assert.IsFalse(ThreadGuideFlow.ShouldSendLoginReminder(true, 50, 2, true, false), "holds a current item");
            Assert.IsFalse(ThreadGuideFlow.ShouldSendLoginReminder(true, 50, 2, false, true), "has a live guide run");
            Assert.AreEqual("Speak with the Thread-Guide in the Drift Network for your next Guide Fragment.", ThreadGuideText.LoginReminder);

            StringAssert.Contains(PooledLootSourceText.Read("Source/ACE.Server/WorldObjects/Player_Networking.cs"), "SendThreadGuideLoginReminderIfDue();");
        }

        [TestMethod]
        public void Rung_150_demands_a_taper_then_a_talisman_and_guarantees_both()
        {
            var spec = GuideFragment(150);
            CollectionAssert.AreEqual(new[] { RawFragmentRules.TaperType, RawFragmentRules.TalismanType }, ThreadGuideLadder.RequiredTypes(150).ToArray());

            Assert.AreEqual("This Guide Fragment needs a Taper before the Press will take it.", ThreadGuideFlow.DecidePress(spec, Owner, 3, Doses(), 1).Refusal);
            Assert.AreEqual("This Guide Fragment needs a Taper before the Press will take it.", ThreadGuideFlow.DecidePress(spec, Owner, 3, Doses(RawFragmentRules.TalismanType), 1).Refusal);
            Assert.AreEqual("This Guide Fragment needs a Talisman before the Press will take it.", ThreadGuideFlow.DecidePress(spec, Owner, 3, Doses(RawFragmentRules.TaperType), 1).Refusal);

            var both = ThreadGuideFlow.DecidePress(spec, Owner, 3, Doses(RawFragmentRules.TaperType, RawFragmentRules.TalismanType), 1);
            Assert.IsNull(both.Refusal);
            CollectionAssert.AreEquivalent(new[] { RawFragmentRules.TaperType, RawFragmentRules.TalismanType }, both.GuaranteedTypes.ToArray());
            Assert.AreEqual(RawFragmentRules.TalismanType, both.ExplainType, "the lesson is still the Talisman's");

            // Every other special rung still demands exactly its one type.
            foreach (var rung in ThreadGuideLadder.Rungs.Where(r => r.RequiredType != null && r.Level != 150))
                CollectionAssert.AreEqual(new[] { rung.RequiredType }, rung.RequiredTypes.ToArray(), $"rung {rung.Level}");
        }

        /// <summary>The rung 150 pair is a real match (the Hazel Talisman sharpens what the Red Taper adds) and both are stocked.</summary>
        [TestMethod]
        public void The_rung_150_pair_matches_in_attunement_json_and_is_stocked()
        {
            var attunement = PooledLootSourceText.Read("Content/dungeons/dynamic/attunement.json");
            var taper = Regex.Match(attunement, "\"name\": \"Red Taper\".*?\"modifier\": \"(\\w+)\"", RegexOptions.Singleline).Groups[1].Value;
            var talisman = Regex.Match(attunement, "\"name\": \"Hazel Talisman\".*?\"op\": \"sharpen\", \"modifier\": \"(\\w+)\"", RegexOptions.Singleline).Groups[1].Value;
            Assert.AreEqual("savage", taper);
            Assert.AreEqual(taper, talisman, "the Hazel Talisman sharpens the Red Taper's modifier");

            var vendor = PooledLootSourceText.Read("Content/sql/weenies/1003602 Reagent-Steward Aya Torimaru.sql");
            StringAssert.Contains(vendor, "/* Red Taper -");
            StringAssert.Contains(vendor, "/* Hazel Talisman -");
            StringAssert.Contains(vendor, "/* Lead Scarab -");

            StringAssert.Contains(ThreadGuideText.Grant150, "a Red Taper, then on a Hazel Talisman");
        }

        [TestMethod]
        public void Rung_50_and_explanation_texts_are_the_owner_wording()
        {
            StringAssert.Contains(ThreadGuideText.Grant50, "Buy a Lead Scarab from the Reagent-Steward - it is the gentlest; most other Scarabs raise the thread's level.");
            Assert.AreEqual("REQUIRED: Use this fragment on a Scarab, then use it on the Fragment Press.", ThreadGuideText.ComposeRequirementLine(GuideFragment(50), Doses()));
            Assert.AreEqual("Potions reroll the weakest modifiers as the gem is pressed. The new roll is usually higher, so the gem gets tougher.", ThreadGuideText.ExplainPotion);
            Assert.AreEqual("Talismans strengthen a modifier on the gem as it is pressed. Pair one with its matching Taper; with nothing to strengthen it picks a modifier at random.", ThreadGuideText.ExplainTalisman);
        }

        /// <summary>{CHAMP} is hard-coded; this pins it against the live attunement table.</summary>
        [TestMethod]
        public void The_champions_taper_colour_matches_attunement_json()
        {
            string file = null;
            for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null && file == null; dir = dir.Parent)
            {
                var candidate = Path.Combine(dir.FullName, "Content", "dungeons", "dynamic", "attunement.json");
                if (File.Exists(candidate))
                    file = candidate;
            }

            if (file == null)
                Assert.Inconclusive("Could not locate Content/dungeons/dynamic/attunement.json by walking up from the test assembly.");

            var m = Regex.Match(File.ReadAllText(file), "\"color\"\\s*:\\s*\"(\\w+)\"\\s*,\\s*\"modifier\"\\s*:\\s*\"champions\"");
            Assert.IsTrue(m.Success, "attunement.json has no colour mapped to champions");
            Assert.AreEqual(m.Groups[1].Value, ThreadGuideText.ChampionsTaperColour, true);
        }

        [TestMethod]
        public void Every_explanation_and_line_is_ascii()
        {
            var lines = new List<string>
            {
                ThreadGuideText.Stale, ThreadGuideText.Solo, ThreadGuideText.FailVisit, ThreadGuideText.RefuseLevel,
                ThreadGuideText.RefuseOutstanding, ThreadGuideText.RefuseComplete, ThreadGuideText.RefuseDisabled,
                ThreadGuideText.RefusePackFull, ThreadGuideText.Notice, ThreadGuideText.ComposeClear(50), ThreadGuideText.ComposeFailRegrant(50),
            };

            foreach (var rung in ThreadGuideLadder.Rungs)
            {
                lines.Add(ThreadGuideText.ComposeGrant(rung.Level));
                if (rung.RequiredType != null)
                {
                    lines.Add(ThreadGuideText.ComposeExplain(rung.RequiredType));
                    lines.Add(ThreadGuideText.ComposeNeeds(rung.RequiredType));
                    Assert.AreEqual(rung.Explanation, ThreadGuideText.ComposeExplain(rung.RequiredType), "the ladder and the Press share one explanation");
                }
            }

            foreach (var line in lines)
                Assert.IsTrue(line.All(c => c >= 0x20 && c < 0x7F), line);
        }
    }
}
