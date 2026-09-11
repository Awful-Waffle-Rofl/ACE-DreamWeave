using System;
using System.Collections.Generic;
using System.Linq;

using ACE.Server.ThreadDungeons;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ACE.Server.Tests.ThreadDungeons
{
    /// <summary>
    /// The pure surface of the Fragment Press (PHASE-2-IMPLEMENTATION-PLAN.md task D5): the per-dose log
    /// line, the give gate's decision and its refusals, and the empty-load pressing. The Player branches -
    /// the kill switch, the Trade Note charge, the confirmation, the consume/EndRun ordering - need a live
    /// session and are covered by the live checks in D7b instead.
    ///
    /// Nothing here may reach PropertyManager: its reads throw in unit tests, which is why the press builds
    /// its PressLimits in FragmentPressStation.BuildLimits and every test below constructs one by hand.
    /// </summary>
    [TestClass]
    public class FragmentPressStationTests
    {
        private const string EmptyIndex = "{\"dungeons\":[]}";
        private const string EmptyBosses = "{\"bosses\":[]}";

        private const string ModifiersJson =
            "{\"modifiers\":[" +
            "{\"id\":\"savage\",\"display\":\"Savage\",\"rarity\":\"common\",\"target\":\"monster\",\"minMagnitude\":10,\"maxMagnitude\":40}," +
            "{\"id\":\"precise\",\"display\":\"Precise\",\"rarity\":\"uncommon\",\"target\":\"monster\",\"minMagnitude\":10,\"maxMagnitude\":30}" +
            "],\"xpLadder\":[]}";

        private const string AttunementJson =
            "{\"version\":1,\"colors\":[{\"color\":\"red\",\"modifier\":\"savage\"}],\"components\":[" +
            "{\"wcid\":1650,\"name\":\"Red Taper\",\"type\":\"taper\",\"color\":\"red\",\"dose\":10,\"limit\":4,\"instability\":5," +
            "\"ops\":[{\"op\":\"add_or_raise\",\"modifier\":\"savage\",\"weight\":100}]}" +
            "],\"wild\":[{\"op\":\"nothing\",\"weight\":100}]}";

        private static ThreadDungeonStore Store()
            => ThreadDungeonStore.Parse(EmptyIndex, EmptyBosses, ModifiersJson, new Dictionary<string, string>(), attunementJson: AttunementJson);

        /// <summary>
        /// The shipped defaults, hand-built - PressLimits is the seam that keeps PropertyManager out of the
        /// core. aimChance is pinned at 1.0 rather than left at the shipped 0.5 for the same reason
        /// RawFragmentRulesTests pins it: the press test below asserts which modifier a named component
        /// produces, which is a statement about the aim landing. The 50/50 miss is covered there.
        /// </summary>
        private static PressLimits Limits()
            => new PressLimits(4, DungeonGemSpec.MaxLocks, RawFragmentRules.MaxEntries, DungeonGemSpec.MaxLevel,
                aimChance: 1.0);

        [TestMethod]
        public void ComposeInsufficientFundsMessage_computes_banked_total()
        {
            Assert.AreEqual(
                "You need 1 Trade Note (250,000), in your pack or as 250,000 banked pyreals, to use the Fragment Press.",
                FragmentPressStation.ComposeInsufficientFundsMessage(1));

            var twoNotes = FragmentPressStation.ComposeInsufficientFundsMessage(2);
            StringAssert.Contains(twoNotes, "2 Trade Notes");
            StringAssert.Contains(twoNotes, "500,000");
        }

        /// <summary>
        /// REWRITTEN from ComposePressedMessage_uses_the_steadiness_word, which asserted the four
        /// instability bands of RawFragmentRules.SteadinessWord. That function went with the mechanic (owner
        /// ruling, 2026-09-07), leaving the message with nothing to vary on, so the wording the calm band
        /// produced is now a fixed literal. Kept rather than deleted because the exact sentence is still
        /// player-facing text worth pinning.
        /// </summary>
        [TestMethod]
        public void ComposePressedMessage_is_a_fixed_line()
        {
            Assert.AreEqual("Pressed. It holds.", FragmentPressStation.ComposePressedMessage());
        }

        /// <summary>
        /// The live aim-chance tunable is sanitized before it reaches the pure core, so a shard operator
        /// cannot set it to a value that makes the aim roll meaningless. NaN in particular has to read as the
        /// default rather than being passed through: every comparison against NaN is false, so an unclamped
        /// NaN would silently mean "always miss" - the exact opposite of a disabled dial.
        /// </summary>
        [TestMethod]
        public void The_aim_chance_tunable_is_sanitized_before_it_reaches_the_core()
        {
            Assert.AreEqual(0.5, FragmentPressStation.SanitizeAimChance(0.5), 0.0001);
            Assert.AreEqual(0.0, FragmentPressStation.SanitizeAimChance(0.0), 0.0001, "0 is a legal setting, not a garbled one");
            Assert.AreEqual(1.0, FragmentPressStation.SanitizeAimChance(4.2), 0.0001);
            Assert.AreEqual(0.0, FragmentPressStation.SanitizeAimChance(-1.0), 0.0001);
            Assert.AreEqual(PressLimits.DefaultAimChance, FragmentPressStation.SanitizeAimChance(double.NaN), 0.0001);
        }

        [TestMethod]
        public void Dose_log_line_format()
        {
            var dose = new DoseLogEntry
            {
                Wcid = 1650u,
                Name = "Red Taper",
                Op = "add_or_raise",
                Before = "mods=",
                After = "mods=savage:10",
            };

            Assert.AreEqual("[DYNDUNGEON] press Frostfell Red Taper mods= -> mods=savage:10",
                FragmentPressStation.ComposeDoseLog("Frostfell", dose));
        }

        [TestMethod]
        public void Destroy_of_a_fragment_is_refused()
        {
            // A fragment is refused on its own, specific terms.
            var fragment = new DungeonGemSpec("any", 195, 7, "any", 0, new (string, double)[0], 0, 0);
            Assert.IsFalse(FragmentPressStation.CanDestroy(fragment, 3, 3, out var alreadyRefusal));
            Assert.AreEqual(FragmentPressStation.AlreadyAFragmentMessage, alreadyRefusal);

            // An unbound gem is the case the press actually takes.
            var unbound = new DungeonGemSpec("any", 195, 7, "any", 918273, new (string, double)[0], 0, 0);
            Assert.IsTrue(FragmentPressStation.CanDestroy(unbound, 3, 3, out var none));
            Assert.IsNull(none);
        }

        private const uint Owner = 0x50000001u;
        private const uint Stranger = 0x50000002u;
        private const uint Run = 0x80001234u;

        private static DungeonGemSpec BoundGem(uint ownerGuid)
            => new DungeonGemSpec("filos_doom", 195, 7, "any", 918273, new (string, double)[0], Run, ownerGuid);

        /// <summary>
        /// Someone else's bound gem is refused at the press in the SAME words a use of it gets
        /// (DungeonGemRules.Decide), whether or not the run behind it is still live.
        /// </summary>
        [TestMethod]
        public void Give_of_another_players_bound_gem_is_refused()
        {
            Assert.AreEqual(PressGiveDecision.Refuse,
                FragmentPressStation.DecideGive(BoundGem(Stranger), Owner, runIsLive: true, 2, 3, out var refusal));
            Assert.AreEqual("This gem is bound to another adventurer's dungeon.", refusal);
            Assert.AreEqual(FragmentPressStation.NotYourGemMessage, refusal);

            Assert.AreEqual(PressGiveDecision.Refuse,
                FragmentPressStation.DecideGive(BoundGem(Stranger), Owner, runIsLive: false, 2, 3, out var deadRefusal));
            Assert.AreEqual(FragmentPressStation.NotYourGemMessage, deadRefusal);
        }

        /// <summary>The fix this feature exists for: the owner's own LIVE run can be closed at the press.</summary>
        [TestMethod]
        public void Give_of_your_own_bound_gem_with_a_live_run_offers_the_close()
        {
            Assert.AreEqual(PressGiveDecision.CloseRun,
                FragmentPressStation.DecideGive(BoundGem(Owner), Owner, runIsLive: true, 2, 3, out var refusal));
            Assert.IsNull(refusal);

            // Zero entries left is still a closeable run - that is exactly the stranded case.
            Assert.AreEqual(PressGiveDecision.CloseRun,
                FragmentPressStation.DecideGive(BoundGem(Owner), Owner, runIsLive: true, 0, 3, out _));
        }

        /// <summary>
        /// A bound gem whose run is gone is just a spent gem: it is destroyed outright, rather than being
        /// refused as it was before this gesture existed.
        /// </summary>
        [TestMethod]
        public void Give_of_your_own_bound_gem_whose_run_is_dead_destroys()
        {
            var gem = BoundGem(Owner);

            Assert.AreEqual(PressGiveDecision.Destroy,
                FragmentPressStation.DecideGive(gem, Owner, runIsLive: false, 2, 3, out var refusal));
            Assert.IsNull(refusal);
        }

        /// <summary>
        /// The unbound path: an unbound gem is destroyed, a fragment and a non-gem are refused on their own
        /// terms, and DecideGive hands those two straight to CanDestroy.
        /// </summary>
        [TestMethod]
        public void Give_of_an_unbound_gem_destroys()
        {
            var unbound = new DungeonGemSpec("any", 195, 7, "any", 918273, new (string, double)[0], 0, 0);

            Assert.AreEqual(PressGiveDecision.Destroy,
                FragmentPressStation.DecideGive(unbound, Owner, runIsLive: false, 3, 3, out var none));
            Assert.IsNull(none);

            // runIsLive is meaningless for an unbound gem and must not change the answer.
            Assert.AreEqual(PressGiveDecision.Destroy,
                FragmentPressStation.DecideGive(unbound, Owner, runIsLive: true, 3, 3, out _));

            var fragment = new DungeonGemSpec("any", 195, 7, "any", 0, new (string, double)[0], 0, 0);
            Assert.AreEqual(PressGiveDecision.Refuse,
                FragmentPressStation.DecideGive(fragment, Owner, runIsLive: false, 3, 3, out var alreadyRefusal));
            Assert.AreEqual(FragmentPressStation.AlreadyAFragmentMessage, alreadyRefusal);

            Assert.AreEqual(PressGiveDecision.Refuse,
                FragmentPressStation.DecideGive(null, Owner, runIsLive: false, 0, 0, out var notAGem));
            Assert.AreEqual(FragmentPressStation.NotAGemMessage, notAGem);
        }

        /// <summary>
        /// The two confirmation prompts, since they are the only place the forfeited entries are stated and
        /// the only place "nothing comes back" is promised before the player commits.
        /// </summary>
        [TestMethod]
        public void Confirmation_prompts_name_the_entries_and_promise_nothing_back()
        {
            Assert.AreEqual(
                "End your run in Filo's Doom and destroy the gem? It has 2 of 3 entries left, and nothing comes back.",
                FragmentPressStation.ComposeCloseConfirm("Filo's Doom", 2, 3));

            Assert.AreEqual(
                "Destroy this gem at the press? It has 2 of 3 entries left, and nothing comes back.",
                FragmentPressStation.ComposeDestroyConfirm(2, 3));

            // StringAssert rather than Assert.AreEqual: both sides would be compile-time constants and
            // MSTEST0032 flags that as an assertion whose condition is known at build time.
            StringAssert.Contains(FragmentPressStation.ClosedMessage,
                "The press grinds the gem to dust and the way behind it closes.");
        }

        /// <summary>
        /// Neither success line implies a fragment or gem comes back - that is the whole behavioral change
        /// this feature makes. (Not asserting the two messages differ from each other: both sides would be
        /// compile-time constants, which MSTEST0032 flags as a condition known at build time.)
        /// </summary>
        [TestMethod]
        public void Destroyed_and_closed_messages_promise_nothing_back()
        {
            StringAssert.DoesNotMatch(FragmentPressStation.DestroyedMessage, new System.Text.RegularExpressions.Regex("fragment", System.Text.RegularExpressions.RegexOptions.IgnoreCase));
            StringAssert.DoesNotMatch(FragmentPressStation.ClosedMessage, new System.Text.RegularExpressions.Regex("fragment", System.Text.RegularExpressions.RegexOptions.IgnoreCase));
        }

        /// <summary>
        /// GemEntries feeds the confirmation prompts with the two entry counts read straight off the gem.
        /// The composed description is asserted alongside the pair, because "Entries: 1 of 3" is the whole
        /// point of keeping the two numbers apart.
        /// </summary>
        [TestMethod]
        public void GemEntries_reads_both_counts_off_the_gem()
        {
            var (entries, maxEntries) = FragmentPressStation.GemEntries(1, 3);

            Assert.AreEqual(1, entries);
            Assert.AreEqual(3, maxEntries);

            var gem = new DungeonGemSpec("any", 195, 7, "any", 918273, new (string, double)[0], 0, 0);
            var desc = ThreadDungeonGemHandler.ComposeLongDesc(gem, "any dungeon", entries, maxEntries, null, null);

            StringAssert.Contains(desc, "\nEntries: 1 of 3\n");

            // A gem the press never touched is full, and a nonsense pair still cannot exceed its own ceiling.
            Assert.AreEqual((3, 3), FragmentPressStation.GemEntries(3, 3));
            Assert.AreEqual((2, 2), FragmentPressStation.GemEntries(5, 2));
            Assert.AreEqual((0, 0), FragmentPressStation.GemEntries(null, null));
        }

        /// <summary>
        /// REWRITTEN for press v2 (owner ruling, 2026-09-07). A fragment with nothing loaded used to press
        /// into a bare gem carrying no modifiers at all; under the slot board every slot resolves, so it now
        /// presses into a COMPLETE, fully random gem. That reversal is the whole point of the board - it
        /// removes the failure mode where a lightly loaded fragment produced a thin, boring gem - so the
        /// test is inverted rather than deleted.
        ///
        /// The fixture here carries only tapers, so the four taper slots resolve and the other five are
        /// skipped; the level, tier and entry count are untouched because no scarab is in the store.
        /// </summary>
        [TestMethod]
        public void Pressing_an_empty_fragment_still_fills_every_slot_the_store_can_fill()
        {
            var store = Store();
            var fragment = new DungeonGemSpec("any", 235, 7, "any", 0, new (string, double)[0], 0, 0);

            var pressed = RawFragmentRules.Resolve(new PressState(fragment, 3), store.Attunement, store.Modifiers,
                Limits(), new Random(20260904), out var log);

            Assert.AreEqual(4, log.Count, "four taper slots, all drawn");
            Assert.IsTrue(log.All(d => d.Drawn), "nothing was loaded, so every slot drew");
            Assert.AreNotEqual(0, pressed.Spec.Seed);
            Assert.AreEqual(1, pressed.Spec.Presses);
            Assert.AreEqual(0, pressed.Spec.Load.Count);
            Assert.AreEqual(1, pressed.Spec.Modifiers.Count, "the only taper in the fixture names savage");
            Assert.AreEqual("savage", pressed.Spec.Modifiers[0].Id);
            Assert.AreEqual(235, pressed.Spec.Level, "no scarab in the fixture, so the level is untouched");
            Assert.AreEqual(7, pressed.Spec.Tier);
            Assert.AreEqual(3, pressed.Entries);
        }
    }
}
