using System;
using System.Collections.Generic;
using System.Text;

using ACE.Entity.Enum;
using ACE.Server.Entity;
using ACE.Server.WorldEvents.Defs;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ACE.Server.Tests
{
    /// <summary>
    /// The Bluespire ladder's four pure pieces: the entry decision table
    /// (<see cref="BluespireLadderGate"/>), the death-spawn plan (<see cref="DeathSpawnPlan"/>), the crowd
    /// filter and curve (<see cref="BluespireCrowdScaling"/>), and the quest-name-to-rung table
    /// (<see cref="BluespireLadderRewards.RungForQuestName"/>).
    ///
    /// Everything here is a static call over values. NOTHING in this file touches PropertyManager, a Player,
    /// a landblock or a database - PropertyManager reads throw in unit tests, and the Player constructor
    /// reaches DatabaseManager.Authentication, which is the whole reason these decisions were separated from
    /// their tunable reads in the first place. This class therefore needs no seeding and is order
    /// independent; it passes run alone.
    /// </summary>
    [TestClass]
    public class BluespireLadderGateTests
    {
        // ------------------------------------------------------------------------------------------------
        // The full entry decision table.
        // ------------------------------------------------------------------------------------------------

        /// <summary>
        /// 6 rungs x {prerequisite held, not} x {rung 1 entry quest held, not} x {enabled, disabled} - 48
        /// cases, each with its expectation derived here from the RATIFIED RULES rather than from the
        /// implementation:
        ///
        ///   R1 the master switch is a KILL SWITCH, so disabled means the portal behaves as an ordinary one
        ///      (Allow), never as a sealed one;
        ///   R2 there is no level requirement to enter any depth (owner ruling, 2026-09-24, round 19 A2) -
        ///      only the prerequisite gates a character;
        ///   R3 rung 1's prerequisite is the entry quest stamp, every other rung's is the previous rung's
        ///      clear stamp.
        ///
        /// Every case is evaluated and every mismatch is collected, so a broken clause reports its whole
        /// footprint in one failure instead of one case at a time.
        /// </summary>
        [TestMethod]
        public void DecisionTable_AllRungs_AllPrerequisites_AllSwitchStates()
        {
            const string entryQuest = "BluespireTheFirstDrum";

            var requires = new[] { 0, 0, 1, 2, 3, 4, 5 }; // index 0 unused; 1..6 are the rungs; rung 1 has no rung prerequisite

            var failures = new List<string>();
            var cases = 0;

            for (var rung = 1; rung <= BluespireLadderGate.RungCount; rung++)
            {
                var requiredRung = requires[rung];

                foreach (var prereqHeld in new[] { true, false })
                {
                    foreach (var entryHeld in new[] { true, false })
                    {
                        foreach (var enabled in new[] { true, false })
                        {
                            cases++;

                            BluespireLadderDecision expected;

                            if (!enabled)
                                expected = BluespireLadderDecision.Allow;                          // R1
                            else if (rung == 1 && !entryHeld)
                                expected = BluespireLadderDecision.MissingEntryQuest;              // R3
                            else if (requiredRung != 0 && !prereqHeld)
                                expected = BluespireLadderDecision.MissingPreviousRung;            // R2, R3
                            else
                                expected = BluespireLadderDecision.Allow;

                            var actual = BluespireLadderGate.Decide(rung, requiredRung,
                                prereqHeld, entryQuest, entryHeld, enabled);

                            if (actual != expected)
                                failures.Add($"rung {rung} requires {requiredRung} " +
                                             $"prereqHeld={prereqHeld} entryHeld={entryHeld} enabled={enabled}: " +
                                             $"expected {expected}, got {actual}");
                        }
                    }
                }
            }

            Assert.AreEqual(6 * 2 * 2 * 2, cases, "the decision table did not cover the intended number of cases");

            AssertNoFailures(failures);
        }

        /// <summary>
        /// A handful of the table's rows written out by hand, with literal expectations. A generated table
        /// proves internal consistency; these prove the table was generated from the right rules.
        /// </summary>
        [TestMethod]
        public void DecisionTable_HandWrittenAnchors()
        {
            const string q = "BluespireTheFirstDrum";

            // Rung 1 with the drum: in, whatever the character's level.
            Assert.AreEqual(BluespireLadderDecision.Allow,
                BluespireLadderGate.Decide(1, 0, false, q, true, true));

            // Rung 1, no drum: rung 1's own prerequisite.
            Assert.AreEqual(BluespireLadderDecision.MissingEntryQuest,
                BluespireLadderGate.Decide(1, 0, false, q, false, true));

            // Rung 4, rung 3 not cleared.
            Assert.AreEqual(BluespireLadderDecision.MissingPreviousRung,
                BluespireLadderGate.Decide(4, 3, false, q, false, true));

            // Rung 4, rung 3 cleared. The entry quest is rung 1's alone and is not consulted.
            Assert.AreEqual(BluespireLadderDecision.Allow,
                BluespireLadderGate.Decide(4, 3, true, q, false, true));

            // Rung 6, rung 5 cleared, no level requirement stands in the way (owner ruling, round 19 A2).
            Assert.AreEqual(BluespireLadderDecision.Allow,
                BluespireLadderGate.Decide(6, 5, true, q, true, true));
        }

        /// <summary>
        /// Rung 1 with a BLANK entry quest name: the entry-quest requirement is off and, with no level
        /// requirement on any depth (owner ruling, round 19 A2), the rung is unconditionally open. This is
        /// the documented way to disable the entry-quest requirement without editing content.
        /// </summary>
        [TestMethod]
        public void BlankEntryQuestName_TurnsTheRungOneEntryRequirementOff()
        {
            foreach (var blank in new[] { null, "", "   " })
            {
                Assert.AreEqual(BluespireLadderDecision.Allow,
                    BluespireLadderGate.Decide(1, 0, false, blank, false, true),
                    $"entry quest name '{blank ?? "null"}' should read as no entry-quest requirement");
            }
        }

        /// <summary>
        /// A rung outside 1..6 is a content bug on the portal weenie and is refused. Refusing rather than
        /// clamping is deliberate: clamping down to 1 would let a typo open the whole ladder.
        /// </summary>
        [TestMethod]
        public void MalformedRung_IsRefused_ButOnlyWhileEnabled()
        {
            foreach (var rung in new[] { int.MinValue, -1, 0, 7, 99, int.MaxValue })
            {
                Assert.AreEqual(BluespireLadderDecision.MalformedRung,
                    BluespireLadderGate.Decide(rung, 0, true, "q", true, true),
                    $"rung {rung} is not on the ladder and must be refused");

                Assert.AreEqual(BluespireLadderDecision.Allow,
                    BluespireLadderGate.Decide(rung, 0, true, "q", true, false),
                    $"rung {rung} with the ladder switched off must behave as an ordinary portal");
            }

            for (var rung = 1; rung <= BluespireLadderGate.RungCount; rung++)
                Assert.IsTrue(BluespireLadderGate.IsValidRung(rung), $"rung {rung} should be valid");
        }

        // ------------------------------------------------------------------------------------------------
        // Malformed _requires.
        // ------------------------------------------------------------------------------------------------

        /// <summary>
        /// The four malformed shapes the spec names - 0, the rung's own number, 7 (past the ladder) and a
        /// negative - all read as 0, "no rung prerequisite". Reading them as 0 is what stops a typo either
        /// locking a rung shut forever (a prerequisite that can never be met) or forming a cycle (a chain
        /// that points upward, so no rung can ever be entered first). The level gate is untouched by it.
        /// </summary>
        [TestMethod]
        public void SanitizeRequires_MalformedValuesReadAsNoPrerequisite()
        {
            for (var rung = 1; rung <= BluespireLadderGate.RungCount; rung++)
            {
                Assert.AreEqual(0, BluespireLadderGate.SanitizeRequires(rung, 0), $"rung {rung}: 0 is already 'none'");
                Assert.AreEqual(0, BluespireLadderGate.SanitizeRequires(rung, rung), $"rung {rung}: a rung cannot require itself");
                Assert.AreEqual(0, BluespireLadderGate.SanitizeRequires(rung, 7), $"rung {rung}: 7 is past the ladder");
                Assert.AreEqual(0, BluespireLadderGate.SanitizeRequires(rung, -3), $"rung {rung}: a negative is meaningless");
                Assert.AreEqual(0, BluespireLadderGate.SanitizeRequires(rung, long.MinValue), $"rung {rung}: long.MinValue must not wrap");
                Assert.AreEqual(0, BluespireLadderGate.SanitizeRequires(rung, long.MaxValue), $"rung {rung}: long.MaxValue must not wrap");

                // Anything at or above this rung's own number is unreachable and must be dropped.
                for (var higher = rung; higher <= BluespireLadderGate.RungCount + 2; higher++)
                    Assert.AreEqual(0, BluespireLadderGate.SanitizeRequires(rung, higher),
                        $"rung {rung} must not accept a prerequisite of {higher}");
            }
        }

        /// <summary>The shipped chain, 0/1/2/3/4/5, survives sanitisation untouched.</summary>
        [TestMethod]
        public void SanitizeRequires_ShippedChainIsPreserved()
        {
            var shipped = new[] { 0, 0, 1, 2, 3, 4, 5 };

            for (var rung = 1; rung <= BluespireLadderGate.RungCount; rung++)
                Assert.AreEqual(shipped[rung], BluespireLadderGate.SanitizeRequires(rung, shipped[rung]),
                    $"rung {rung}'s shipped prerequisite must survive sanitisation");

            // Any strictly lower real rung is legal, not just the immediately preceding one - a retune may
            // legitimately shorten the chain.
            Assert.AreEqual(1, BluespireLadderGate.SanitizeRequires(6, 1));
            Assert.AreEqual(4, BluespireLadderGate.SanitizeRequires(5, 4));
        }

        /// <summary>
        /// A sanitised-away prerequisite must not silently admit anyone: rung 1 still refuses without the
        /// drum. This is the "a typo cannot unlock the ladder" half of the rule, and it is the half a
        /// sanitiser alone does not give you.
        /// </summary>
        [TestMethod]
        public void SanitizedAwayPrerequisite_LeavesTheEntryQuestStanding()
        {
            var requiredRung = BluespireLadderGate.SanitizeRequires(4, 9);

            Assert.AreEqual(0, requiredRung);

            Assert.AreEqual(BluespireLadderDecision.Allow,
                BluespireLadderGate.Decide(4, requiredRung, false, "q", false, true));

            Assert.AreEqual(BluespireLadderDecision.MissingEntryQuest,
                BluespireLadderGate.Decide(1, BluespireLadderGate.SanitizeRequires(1, 3), false, "q", false, true));
        }

        // ------------------------------------------------------------------------------------------------
        // Quest names, refusal text.
        // ------------------------------------------------------------------------------------------------

        [TestMethod]
        public void ClearedQuestNames_AreTheSixLadderNames_AndNothingElse()
        {
            for (var rung = 1; rung <= BluespireLadderGate.RungCount; rung++)
                Assert.AreEqual($"BluespireLadderD{rung}Cleared", BluespireLadderGate.ClearedQuestName(rung));

            foreach (var rung in new[] { -1, 0, 7, 100 })
                Assert.IsNull(BluespireLadderGate.ClearedQuestName(rung), $"rung {rung} is not on the ladder");
        }

        /// <summary>
        /// Allow produces no message (the portal simply opens) and every refusal produces one, because a
        /// refusal with no text is a portal that does nothing for no stated reason.
        /// </summary>
        [TestMethod]
        public void RefusalMessage_IsNullOnlyForAllow()
        {
            Assert.IsNull(BluespireLadderGate.RefusalMessage(BluespireLadderDecision.Allow, 1, 0));

            foreach (BluespireLadderDecision decision in Enum.GetValues(typeof(BluespireLadderDecision)))
            {
                if (decision == BluespireLadderDecision.Allow)
                    continue;

                var message = BluespireLadderGate.RefusalMessage(decision, 2, 1);

                Assert.IsFalse(string.IsNullOrWhiteSpace(message), $"{decision} must produce a player-facing refusal");
                Assert.IsFalse(message.IndexOf((char)0x2013) >= 0 || message.IndexOf((char)0x2014) >= 0,
                    $"{decision}: player-facing text must use ASCII hyphens only");
            }

            StringAssert.Contains(BluespireLadderGate.RefusalMessage(BluespireLadderDecision.MissingPreviousRung, 3, 2), "second",
                "the prerequisite refusal must name the depth actually required");
        }

        [TestMethod]
        public void OrdinalWord_CoversTheLadderAndDegradesGracefully()
        {
            Assert.AreEqual("first", BluespireLadderGate.OrdinalWord(1));
            Assert.AreEqual("sixth", BluespireLadderGate.OrdinalWord(6));
            Assert.AreEqual("9", BluespireLadderGate.OrdinalWord(9));
        }

        // ------------------------------------------------------------------------------------------------
        // Reward table (quest name -> rung). Pure; the payout itself needs a Player and is not tested here.
        // ------------------------------------------------------------------------------------------------

        [TestMethod]
        public void RungForQuestName_MapsTheSixClearStamps_CaseInsensitively()
        {
            for (var rung = 1; rung <= BluespireLadderGate.RungCount; rung++)
            {
                var name = BluespireLadderGate.ClearedQuestName(rung);

                Assert.AreEqual(rung, BluespireLadderRewards.RungForQuestName(name));
                Assert.AreEqual(rung, BluespireLadderRewards.RungForQuestName(name.ToLowerInvariant()),
                    "QuestManager compares quest names case-insensitively, so this table must too");
                Assert.AreEqual(rung, BluespireLadderRewards.RungForQuestName(name.ToUpperInvariant()));
            }
        }

        [TestMethod]
        public void RungForQuestName_IsZeroForEveryOtherQuest()
        {
            foreach (var name in new[] { null, "", "   ", "BluespireLadderD0Cleared", "BluespireLadderD7Cleared",
                                         "BluespireTheFirstDrum", "fachubbanderlingcampportal_flag", "BluespireLadderD1" })
            {
                Assert.AreEqual(0, BluespireLadderRewards.RungForQuestName(name),
                    $"'{name ?? "null"}' is not a ladder clear stamp and must pay nothing");
            }
        }

        // ------------------------------------------------------------------------------------------------
        // Kill-task bootstrap: which quests a kill may CREATE a registry row for, rather than only credit.
        //
        // The rules this section pins, derived from the ladder's requirements rather than from the
        // implementation:
        //
        //   B1 a rung's clear row must be creatable BY THE BOSS KILL, because the ladder has no quest-giving
        //      NPC and pre-arming the row is the very row creation that pays the rung - a pre-armed
        //      character would be paid for entering the dungeon;
        //   B2 the bypass must be exactly as wide as the ladder and no wider: every other quest in the game
        //      keeps its HasQuest gate, so an ordinary kill task can still never credit someone who never
        //      took it;
        //   B3 the master switch is a kill switch for this too - a row created while the ladder is off would
        //      be a clear the payout declines to pay and that can never be re-earned, because the row IS the
        //      ledger;
        //   B4 the set of names that may bootstrap and the set of names that get paid must be THE SAME SET,
        //      or a clear exists that nothing recognises.
        // ------------------------------------------------------------------------------------------------

        /// <summary>B1, and B5: authored KillQuest strings are content, so the match is case-insensitive.</summary>
        [TestMethod]
        public void ShouldBootstrapClearRow_IsTrueForEveryRungClearStamp_WhileTheLadderIsOn()
        {
            for (var rung = 1; rung <= BluespireLadderGate.RungCount; rung++)
            {
                var name = BluespireLadderGate.ClearedQuestName(rung);

                Assert.IsTrue(BluespireLadderRewards.ShouldBootstrapClearRow(name, true),
                    $"rung {rung}'s clear row can only ever be created by its boss kill, so '{name}' must bootstrap");

                Assert.IsTrue(BluespireLadderRewards.ShouldBootstrapClearRow(name.ToLowerInvariant(), true),
                    "a KillQuest authored in another case is the same quest to QuestManager, so it must be to this too");
                Assert.IsTrue(BluespireLadderRewards.ShouldBootstrapClearRow(name.ToUpperInvariant(), true));
            }
        }

        /// <summary>
        /// B2. The HasQuest gate exists so an ordinary kill task never credits a player who did not take the
        /// quest; nothing outside the ladder may lose it. The near-miss names matter most here - a prefix or
        /// "starts with Bluespire" test would pass the positive cases above and quietly fail these.
        /// </summary>
        [TestMethod]
        public void ShouldBootstrapClearRow_IsFalseForEveryOtherQuest_WhateverTheSwitch()
        {
            var notLadderQuests = new[]
            {
                null, "", "   ",
                "BluespireLadderD0Cleared", "BluespireLadderD7Cleared", "BluespireLadderD1",
                "BluespireLadderD1Cleared2", "XBluespireLadderD1Cleared",
                "BluespireTheFirstDrum", "BluespireLadderCleared",
                "fachubbanderlingcampportal_flag", "aunhunterkilltask", "Blue"
            };

            foreach (var name in notLadderQuests)
            {
                foreach (var enabled in new[] { true, false })
                {
                    Assert.IsFalse(BluespireLadderRewards.ShouldBootstrapClearRow(name, enabled),
                        $"'{name ?? "null"}' is not a ladder clear stamp; a kill must never be able to create its row " +
                        $"(ladder enabled: {enabled})");
                }
            }
        }

        /// <summary>
        /// B3. Note this is the OPPOSITE polarity to the entry gate, where disabled means Allow: refusing to
        /// stamp is the safe direction here, because the character can simply kill the boss again once the
        /// ladder is switched back on, whereas an unpaid clear row can never be undone.
        /// </summary>
        [TestMethod]
        public void ShouldBootstrapClearRow_IsFalseForEveryRung_WhileTheLadderIsOff()
        {
            for (var rung = 1; rung <= BluespireLadderGate.RungCount; rung++)
            {
                Assert.IsFalse(BluespireLadderRewards.ShouldBootstrapClearRow(BluespireLadderGate.ClearedQuestName(rung), false),
                    $"the master switch is a kill switch: with the ladder off, rung {rung}'s row must not be created, " +
                    "because BluespireLadderRewards.Pay would decline to pay it and the clear could never be re-earned");
            }
        }

        /// <summary>
        /// B4. The invariant that keeps the bypass and the payout from drifting apart: both are the same
        /// table. Checked over every name either side is asked about, including the near misses, so adding a
        /// seventh rung to one and not the other fails here rather than in a dungeon.
        /// </summary>
        [TestMethod]
        public void ShouldBootstrapClearRow_AcceptsExactlyTheNamesTheRewardTablePays()
        {
            var probes = new List<string> { null, "", "   ", "BluespireLadderD0Cleared", "BluespireLadderD7Cleared",
                                            "BluespireTheFirstDrum", "BluespireLadderD1", "aunhunterkilltask" };

            for (var rung = 1; rung <= BluespireLadderGate.RungCount; rung++)
            {
                var name = BluespireLadderGate.ClearedQuestName(rung);

                probes.Add(name);
                probes.Add(name.ToLowerInvariant());
                probes.Add(name.ToUpperInvariant());
            }

            var failures = new List<string>();

            foreach (var name in probes)
            {
                var paid = BluespireLadderRewards.RungForQuestName(name) != 0;
                var bootstraps = BluespireLadderRewards.ShouldBootstrapClearRow(name, true);

                if (paid != bootstraps)
                    failures.Add($"'{name ?? "null"}': paid {paid}, bootstraps {bootstraps} - a clear row must never exist " +
                                 "that the payout does not recognise, and nothing may be paid that a kill cannot create");
            }

            AssertNoFailures(failures);
        }

        /// <summary>
        /// The @comment suffix is stripped by the CALLER (Creature.OnDeath_HandleKillTask calls
        /// QuestManager.GetQuestName first, exactly as HasQuest and KillTask_GetEligibleReceivers do), not by
        /// this table. Pinned so that moving the strip out of the call site fails a test rather than silently
        /// making an authored "BluespireLadderD4Cleared@comment" unclearable.
        /// </summary>
        [TestMethod]
        public void ShouldBootstrapClearRow_DoesNotStripAnAtComment_ThatIsTheCallersJob()
        {
            const string commented = "BluespireLadderD4Cleared@the rung 4 miniboss";

            Assert.IsFalse(BluespireLadderRewards.ShouldBootstrapClearRow(commented, true),
                "this table matches whole names only");

            Assert.IsTrue(BluespireLadderRewards.ShouldBootstrapClearRow(
                ACE.Server.Managers.QuestManager.GetQuestName(commented), true),
                "and the caller must strip the comment before asking, which is what makes an authored comment work");
        }

        // ------------------------------------------------------------------------------------------------
        // Delivered-size readback (BluespireLadderRewards.ResolveDeliveredSize).
        //
        // Pins the playability-review fix (2026-09-18, B2 on PR #1219): Pay must report and accumulate
        // whatever the stack ACTUALLY ended up holding after SetStackSize, never the size it asked for.
        // The regression this guards is exactly the Toa Sigil (1005490) bug - a WeenieType Generic
        // currency makes SetStackSize a no-op (WorldObject_Properties.cs:3196-3200), so the object is
        // stuck at StackSize 1 no matter how large a reward Pay requested.
        // ------------------------------------------------------------------------------------------------

        [TestMethod]
        public void ResolveDeliveredSize_ReturnsTheReadbackSize_WhenSetStackSizeWorkedNormally()
        {
            Assert.AreEqual(26, BluespireLadderRewards.ResolveDeliveredSize(26));
            Assert.AreEqual(1, BluespireLadderRewards.ResolveDeliveredSize(1));
            Assert.AreEqual(int.MaxValue, BluespireLadderRewards.ResolveDeliveredSize(int.MaxValue));
        }

        /// <summary>
        /// The regression case: a caller asked SetStackSize for a large reward (say 26), but the object's
        /// runtime type was not Stackable, so SetStackSize never moved StackSize off 1. The readback must
        /// report that stuck 1 - NEVER the 26 that was requested - or the chat message and running totals
        /// silently overstate what the player actually received.
        /// </summary>
        [TestMethod]
        public void ResolveDeliveredSize_ReportsTheStuckReadback_NotTheRequestedAmount_WhenSetStackSizeWasANoOp()
        {
            const int requested = 26;
            const int stuckReadback = 1;

            var delivered = BluespireLadderRewards.ResolveDeliveredSize(stuckReadback);

            Assert.AreEqual(stuckReadback, delivered);
            Assert.AreNotEqual(requested, delivered,
                "trusting the requested size instead of the readback is exactly how this bug hid");
        }

        /// <summary>
        /// A null readback (the property was never set at all) is treated the same as the floor of 1 - a
        /// stack always holds at least one unit, so there is no legitimate reading below that.
        /// </summary>
        [TestMethod]
        public void ResolveDeliveredSize_FloorsAtOne_ForANullOrNonPositiveReadback()
        {
            Assert.AreEqual(1, BluespireLadderRewards.ResolveDeliveredSize(null));
            Assert.AreEqual(1, BluespireLadderRewards.ResolveDeliveredSize(0));
            Assert.AreEqual(1, BluespireLadderRewards.ResolveDeliveredSize(-5));
        }

        // ------------------------------------------------------------------------------------------------
        // Display-name singular/plural choice (BluespireLadderRewards.ResolveDisplayName).
        //
        // Pins the code-review fix on PR #1327: the singular/plural choice for the reward chat lines is a
        // pure function, extracted next to ResolveDeliveredSize, rather than left inlined and untested.
        // ------------------------------------------------------------------------------------------------

        [TestMethod]
        public void ResolveDisplayName_CountOfOne_ReturnsSingular()
        {
            Assert.AreEqual("Toa Sigil", BluespireLadderRewards.ResolveDisplayName(1, "Toa Sigil", "Toa Sigils"));
        }

        [TestMethod]
        public void ResolveDisplayName_CountOfTwo_ReturnsPlural()
        {
            Assert.AreEqual("Toa Sigils", BluespireLadderRewards.ResolveDisplayName(2, "Toa Sigil", "Toa Sigils"));
        }

        /// <summary>A large count is still just "not 1" - the plural covers every count above one, not
        /// only the smallest one.</summary>
        [TestMethod]
        public void ResolveDisplayName_LargeCount_ReturnsPlural()
        {
            Assert.AreEqual("Toa Sigils", BluespireLadderRewards.ResolveDisplayName(16, "Toa Sigil", "Toa Sigils"));
        }

        // ------------------------------------------------------------------------------------------------
        // Death spawn plan.
        // ------------------------------------------------------------------------------------------------

        /// <summary>An absent - or zero, or negative - wcid is the OFF state: no spawn at all.</summary>
        [TestMethod]
        public void DeathSpawnPlan_AbsentWcidIsANoOp()
        {
            foreach (var wcid in new int?[] { null, 0, -1, int.MinValue })
            {
                var plan = DeathSpawnPlan.Decide(wcid, 5, 4.0);

                Assert.IsFalse(plan.ShouldSpawn, $"wcid {(wcid?.ToString() ?? "null")} must produce no spawn");
                Assert.AreEqual(0u, plan.Wcid);
                Assert.AreEqual(0, plan.Count);
            }

            Assert.IsFalse(DeathSpawnPlan.None.ShouldSpawn);
        }

        /// <summary>
        /// An absent or sub-1 count reads as 1, never 0: a weenie that names something to spawn and forgets
        /// to say how many plainly means one, and reading it as zero would make the feature silently inert
        /// on the most likely authoring mistake.
        /// </summary>
        [TestMethod]
        public void DeathSpawnPlan_CountIsClampedIntoOneToMax()
        {
            foreach (var count in new int?[] { null, 0, -1, int.MinValue })
                Assert.AreEqual(1, DeathSpawnPlan.Decide(1005000, count, null).Count,
                    $"count {(count?.ToString() ?? "null")} must read as 1");

            Assert.AreEqual(1, DeathSpawnPlan.Decide(1005000, 1, null).Count);
            Assert.AreEqual(7, DeathSpawnPlan.Decide(1005000, 7, null).Count);
            Assert.AreEqual(DeathSpawnPlan.MaxCount, DeathSpawnPlan.Decide(1005000, DeathSpawnPlan.MaxCount, null).Count);

            foreach (var count in new int?[] { DeathSpawnPlan.MaxCount + 1, 500, int.MaxValue })
                Assert.AreEqual(DeathSpawnPlan.MaxCount, DeathSpawnPlan.Decide(1005000, count, null).Count,
                    $"count {count} must clamp to {DeathSpawnPlan.MaxCount}");
        }

        /// <summary>
        /// An absent, non-finite or non-positive radius reads as the default, and anything past the ceiling
        /// clamps to it. The ceiling exists because a dungeon room is the unit: a radius large enough to
        /// cross a wall produces adds that either fail to place or appear next door.
        /// </summary>
        [TestMethod]
        public void DeathSpawnPlan_RadiusIsClampedIntoZeroExclusiveToMax()
        {
            foreach (var radius in new double?[] { null, 0.0, -1.0, double.NaN, double.PositiveInfinity, double.NegativeInfinity })
                Assert.AreEqual(DeathSpawnPlan.DefaultRadius, DeathSpawnPlan.Decide(1005000, 1, radius).Radius, 1e-6f,
                    $"radius {(radius?.ToString() ?? "null")} must read as the default");

            Assert.AreEqual(4.5f, DeathSpawnPlan.Decide(1005000, 1, 4.5).Radius, 1e-6f);
            Assert.AreEqual(DeathSpawnPlan.MaxRadius, DeathSpawnPlan.Decide(1005000, 1, DeathSpawnPlan.MaxRadius).Radius, 1e-6f);
            Assert.AreEqual(DeathSpawnPlan.MaxRadius, DeathSpawnPlan.Decide(1005000, 1, 10000.0).Radius, 1e-6f);

            var plan = DeathSpawnPlan.Decide(1005000, 3, 6.0);

            Assert.IsTrue(plan.ShouldSpawn);
            Assert.AreEqual(1005000u, plan.Wcid);
            Assert.AreEqual(3, plan.Count);
            Assert.AreEqual(6.0f, plan.Radius, 1e-6f);
        }

        // ------------------------------------------------------------------------------------------------
        // Crowd scaling: the filter, and both curves.
        // ------------------------------------------------------------------------------------------------

        /// <summary>
        /// Staff excluded, dead excluded, another instance excluded - the three exclusions, each isolated so
        /// a regression names which rule broke.
        /// </summary>
        [TestMethod]
        public void CrowdFilter_ExcludesStaff_Dead_AndOtherInstances()
        {
            const uint here = 42;
            const uint elsewhere = 43;

            // The baseline: an ordinary living player standing in this dungeon copy.
            Assert.IsTrue(BluespireCrowdScaling.CountsTowardCrowd(AccessLevel.Player, false, here, here));

            // Another landblock instance is a different place at identical coordinates.
            Assert.IsFalse(BluespireCrowdScaling.CountsTowardCrowd(AccessLevel.Player, false, elsewhere, here),
                "a player in another realm copy of this dungeon is not in this fight");
            Assert.IsFalse(BluespireCrowdScaling.CountsTowardCrowd(AccessLevel.Player, false, null, here),
                "a player with no location is between places and must not be counted");

            // Dead.
            Assert.IsFalse(BluespireCrowdScaling.CountsTowardCrowd(AccessLevel.Player, true, here, here),
                "a field of corpses is a wipe, not a crowd");

            // Staff is Sentinel and above; Advocate and Player are not staff, and an unknown level counts.
            foreach (var staff in new[] { AccessLevel.Sentinel, AccessLevel.Envoy, AccessLevel.Developer, AccessLevel.Admin })
                Assert.IsFalse(BluespireCrowdScaling.CountsTowardCrowd(staff, false, here, here),
                    $"{staff} is staff and must not make the fight harder for the players in it");

            foreach (var notStaff in new AccessLevel?[] { AccessLevel.Player, AccessLevel.Advocate, null })
                Assert.IsTrue(BluespireCrowdScaling.CountsTowardCrowd(notStaff, false, here, here),
                    $"{notStaff?.ToString() ?? "null"} is not staff and must count");
        }

        /// <summary>
        /// The health curve is CrowdHealthDef.Resolve, reused unchanged: min(cap, 1 + perPlayer * max(0,
        /// count - startAt)), floored at 1.0, with a sub-1.0 cap reading as "off" rather than as "weaker
        /// than authored". Checked against the shipped ladder dials, 1 / 0.15 / 3.0.
        /// </summary>
        [TestMethod]
        public void CrowdHealthCurve_UsesTheShippedDials()
        {
            const int startAt = 1;
            const double perPlayer = 0.15;
            const double cap = 3.0;

            Assert.AreEqual(1.0, CrowdHealthDef.Resolve(startAt, perPlayer, 0, cap), 1e-9, "an empty dungeon is 1.0x");
            Assert.AreEqual(1.0, CrowdHealthDef.Resolve(startAt, perPlayer, 1, cap), 1e-9, "a solo run is exactly what the weenie authored");
            Assert.AreEqual(1.15, CrowdHealthDef.Resolve(startAt, perPlayer, 2, cap), 1e-9);
            Assert.AreEqual(1.75, CrowdHealthDef.Resolve(startAt, perPlayer, 6, cap), 1e-9);

            // The cap binds at 1 + 0.15 * (count - 1) >= 3.0, i.e. count >= 14.33, so 15 players and up.
            Assert.AreEqual(cap, CrowdHealthDef.Resolve(startAt, perPlayer, 15, cap), 1e-9);
            Assert.AreEqual(cap, CrowdHealthDef.Resolve(startAt, perPlayer, 500, cap), 1e-9);

            // A mis-authored sub-1.0 cap turns the axis OFF; it never makes creatures weaker than authored.
            Assert.AreEqual(1.0, CrowdHealthDef.Resolve(startAt, perPlayer, 10, 0.5), 1e-9);
            Assert.AreEqual(1.0, CrowdHealthDef.Resolve(startAt, perPlayer, 10, 0.0), 1e-9);
        }

        /// <summary>
        /// The damage curve: a clamped additive DamageRating, the same shape
        /// ThreadDungeonSpawner.StampedDamageRating uses. Off at or below startAt, off when either dial is
        /// non-positive, and saturating rather than wrapping.
        /// </summary>
        [TestMethod]
        public void CrowdDamageRatingCurve_IsClampedAndIndependentOfTheHealthAxis()
        {
            const int startAt = 1;
            const long perPlayer = 10;
            const long cap = 100;

            Assert.AreEqual(0, BluespireCrowdScaling.DamageRatingAddend(0, startAt, perPlayer, cap));
            Assert.AreEqual(0, BluespireCrowdScaling.DamageRatingAddend(1, startAt, perPlayer, cap), "a solo run gets no addend");
            Assert.AreEqual(10, BluespireCrowdScaling.DamageRatingAddend(2, startAt, perPlayer, cap));
            Assert.AreEqual(50, BluespireCrowdScaling.DamageRatingAddend(6, startAt, perPlayer, cap));
            Assert.AreEqual(100, BluespireCrowdScaling.DamageRatingAddend(11, startAt, perPlayer, cap), "the cap binds at 11 players");
            Assert.AreEqual(100, BluespireCrowdScaling.DamageRatingAddend(1000, startAt, perPlayer, cap));

            // Either dial at or below zero turns the damage axis off, so it can be tuned independently of health.
            Assert.AreEqual(0, BluespireCrowdScaling.DamageRatingAddend(50, startAt, 0, cap));
            Assert.AreEqual(0, BluespireCrowdScaling.DamageRatingAddend(50, startAt, -5, cap));
            Assert.AreEqual(0, BluespireCrowdScaling.DamageRatingAddend(50, startAt, perPlayer, 0));
            Assert.AreEqual(0, BluespireCrowdScaling.DamageRatingAddend(50, startAt, perPlayer, -1));

            // Saturation rather than wrap-around, on both the product and the cap.
            Assert.AreEqual(int.MaxValue,
                BluespireCrowdScaling.DamageRatingAddend(int.MaxValue, 0, long.MaxValue, long.MaxValue));
            Assert.IsTrue(BluespireCrowdScaling.DamageRatingAddend(int.MaxValue, 0, 1, long.MaxValue) > 0,
                "a huge crowd must not produce a negative addend");

            // A start-at above the crowd holds both axes at their off values.
            Assert.AreEqual(0, BluespireCrowdScaling.DamageRatingAddend(5, 8, perPlayer, cap));
            Assert.AreEqual(1.0, CrowdHealthDef.Resolve(8, 0.15, 5, 3.0), 1e-9);
        }

        // ------------------------------------------------------------------------------------------------

        private static void AssertNoFailures(IReadOnlyList<string> failures)
        {
            if (failures.Count == 0)
                return;

            var sb = new StringBuilder();

            sb.AppendLine($"{failures.Count} case(s) decided wrongly:");

            foreach (var failure in failures)
                sb.AppendLine("  " + failure);

            Assert.Fail(sb.ToString());
        }
    }
}
