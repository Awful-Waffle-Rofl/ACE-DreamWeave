using System;
using System.Collections.Generic;
using System.Linq;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Server.Pvp;

namespace ACE.Server.Tests.Pvp
{
    /// <summary>
    /// PvpDailyProgress against a dictionary-backed character: the HasQuest gate, the target clamp, the distinct-opponent and streak state
    /// machines (including the same-opponent cases and the state's lifetime), the lifetime stamps, and the shared eligibility predicate.
    /// Pure; touches no PropertyManager key.
    /// </summary>
    [TestClass]
    public class PvpDailyProgressTests
    {
        private sealed class FakeTarget : IPvpDailyTarget
        {
            public readonly Dictionary<string, int> Quests = new(StringComparer.OrdinalIgnoreCase);
            public readonly Dictionary<PvpDailyStateSlot, string> State = new();
            public readonly Dictionary<PvpDailyStamp, int> Stamps = new();
            public readonly List<(string Quest, int Value)> Writes = new();

            public FakeTarget Accept(PvpDailyTask task, int value = 0)
            {
                Quests[PvpDailyCatalog.CounterNameOf(task)] = value;
                return this;
            }

            public int Solves(PvpDailyTask task) => Quests[PvpDailyCatalog.CounterNameOf(task)];

            public bool HasQuest(string questName) => Quests.ContainsKey(questName);

            public int GetSolves(string questName) => Quests.TryGetValue(questName, out var v) ? v : 0;

            /// <summary>When set, SetSolves stores at most this (the live max_solves clamp); Writes still records what was written.</summary>
            public int? ClampTo;

            public void SetSolves(string questName, int completions)
            {
                Quests[questName] = ClampTo.HasValue ? Math.Min(completions, ClampTo.Value) : completions;
                Writes.Add((questName, completions));
            }

            public string GetState(PvpDailyStateSlot slot) => State.TryGetValue(slot, out var v) ? v : null;

            public void SetState(PvpDailyStateSlot slot, string value)
            {
                if (value == null)
                    State.Remove(slot);
                else
                    State[slot] = value;
            }

            public int GetStamp(PvpDailyStamp stamp) => Stamps.TryGetValue(stamp, out var v) ? v : 0;

            public void SetStamp(PvpDailyStamp stamp, int value) => Stamps[stamp] = value;
        }

        private const uint OppA = 0xA, OppB = 0xB, OppC = 0xC;

        private static PvpMatchFacts Duel(PvpBloodResult result, uint opponent) =>
            new PvpMatchFacts(Guid.NewGuid(), 0x50000001, PvpDailyMode.OneVOne, 0, result, result == PvpBloodResult.Win ? 1 : 0, 0, 0, 0, 0, opponent, 0);

        private static PvpMatchFacts Pit(long kills, PvpBloodResult result = PvpBloodResult.Loss) =>
            new PvpMatchFacts(Guid.NewGuid(), 0x50000001, PvpDailyMode.Tugak, 0, result, kills, 0, 0, 0, 0, null, 0);

        private static PvpMatchFacts Bg(PvpDailyMode mode, PvpBloodResult result, int team = 0, long hill = 0, int peak = 0) =>
            new PvpMatchFacts(Guid.NewGuid(), 0x50000001, mode, team, result, 0, hill, 0, 0, 0, null, peak);

        // ================= the progress primitive =================

        /// <summary>No quest held: a match that would advance every Tugak counter writes nothing at all. Holding one quest advances only that one.</summary>
        [TestMethod]
        public void NoQuestHeld_NoChange()
        {
            var t = new FakeTarget();

            PvpDailyProgress.Apply(t, Pit(5, PvpBloodResult.Win));

            Assert.AreEqual(0, t.Writes.Count, "nothing is written without the quest");
            Assert.AreEqual(0, t.Quests.Count, "and no quest row is created");

            t.Accept(PvpDailyTask.Carnage);
            PvpDailyProgress.Apply(t, Pit(2));

            Assert.AreEqual(2, t.Solves(PvpDailyTask.Carnage), "control: the held quest advances");
            Assert.AreEqual(1, t.Quests.Count, "and only it exists");
        }

        /// <summary>The target clamp: 4 of 5 plus 3 kills stops at 5; a further match writes nothing; a delta of 0 writes nothing.</summary>
        [TestMethod]
        public void TargetClamp_StopsAtTheTarget_AndWritesNothingOnceThere()
        {
            var t = new FakeTarget().Accept(PvpDailyTask.Carnage, 4);

            PvpDailyProgress.Apply(t, Pit(3));
            Assert.AreEqual(5, t.Solves(PvpDailyTask.Carnage), "min(4 + 3, 5)");

            var writes = t.Writes.Count;
            PvpDailyProgress.Apply(t, Pit(3));
            Assert.AreEqual(5, t.Solves(PvpDailyTask.Carnage));
            Assert.AreEqual(writes, t.Writes.Count, "at the target nothing is written");

            t.Accept(PvpDailyTask.IntoThePit, 1);
            PvpDailyProgress.Apply(t, Bg(PvpDailyMode.Koth, PvpBloodResult.Win));
            Assert.AreEqual(1, t.Solves(PvpDailyTask.IntoThePit), "a KOTH match is no Tugak match");
        }

        /// <summary>A large cumulative fact (hill seconds) is clamped in one step, and the long delta never overflows the int counter.</summary>
        [TestMethod]
        public void LargeDelta_ClampsWithoutOverflow()
        {
            var t = new FakeTarget().Accept(PvpDailyTask.KingsGround, 100);

            PvpDailyProgress.Apply(t, Bg(PvpDailyMode.Koth, PvpBloodResult.Loss, hill: long.MaxValue));

            Assert.AreEqual(180, t.Solves(PvpDailyTask.KingsGround));
        }

        // ================= ProvingGround: distinct opponent accounts =================

        /// <summary>
        /// Wins against A, A again, B, then C: the counter goes 1, 1, 2, 3. A loss never advances it. Counting every win instead of distinct
        /// opponents gives 4 (clamped 3 after the third win, so the repeat-A step is the discriminating one).
        /// </summary>
        [TestMethod]
        public void ProvingGround_CountsDistinctOpponentAccounts()
        {
            var t = new FakeTarget().Accept(PvpDailyTask.ProvingGround);

            PvpDailyProgress.Apply(t, Duel(PvpBloodResult.Win, OppA));
            Assert.AreEqual(1, t.Solves(PvpDailyTask.ProvingGround));

            PvpDailyProgress.Apply(t, Duel(PvpBloodResult.Win, OppA));
            Assert.AreEqual(1, t.Solves(PvpDailyTask.ProvingGround), "the same opponent account does not count twice");

            PvpDailyProgress.Apply(t, Duel(PvpBloodResult.Loss, OppB));
            Assert.AreEqual(1, t.Solves(PvpDailyTask.ProvingGround), "a loss is not a defeated opponent");

            PvpDailyProgress.Apply(t, Duel(PvpBloodResult.Win, OppB));
            Assert.AreEqual(2, t.Solves(PvpDailyTask.ProvingGround));

            PvpDailyProgress.Apply(t, Duel(PvpBloodResult.Win, OppC));
            Assert.AreEqual(3, t.Solves(PvpDailyTask.ProvingGround));

            PvpDailyProgress.Apply(t, Duel(PvpBloodResult.Win, 0xD));
            Assert.AreEqual(3, t.Solves(PvpDailyTask.ProvingGround), "clamped at the target");
        }

        /// <summary>
        /// The state lives only while the quest is held: a fact with the quest absent removes it. A re-accepted quest (counter back at 0)
        /// does not inherit the old opponents even if the state was not cleared: the stored count no longer matches, so it is stale.
        /// </summary>
        [TestMethod]
        public void ProvingGround_StateIsClearedWithoutTheQuest_AndStaleStateIsIgnored()
        {
            var t = new FakeTarget().Accept(PvpDailyTask.ProvingGround);
            PvpDailyProgress.Apply(t, Duel(PvpBloodResult.Win, OppA));
            Assert.IsNotNull(t.GetState(PvpDailyStateSlot.ProvingGround), "fixture: state was written");

            // Re-accepted (counter reset to 0) while the old state is still on the character.
            t.Accept(PvpDailyTask.ProvingGround, 0);
            PvpDailyProgress.Apply(t, Duel(PvpBloodResult.Win, OppA));
            Assert.AreEqual(1, t.Solves(PvpDailyTask.ProvingGround), "the old acceptance's opponent counts again on the new one");

            // The quest turned in (row erased): the next fact drops the state.
            t.Quests.Remove(PvpDailyCatalog.CounterNameOf(PvpDailyTask.ProvingGround));
            PvpDailyProgress.Apply(t, Pit(0));
            Assert.IsNull(t.GetState(PvpDailyStateSlot.ProvingGround), "no quest, no state");
        }

        // ================= GladiatorsStreak =================

        /// <summary>Win A, win B: the streak reaches 2 (the target). Two different opponents in a row is the happy path.</summary>
        [TestMethod]
        public void Streak_TwoDifferentOpponents_Completes()
        {
            var t = new FakeTarget().Accept(PvpDailyTask.GladiatorsStreak);

            PvpDailyProgress.Apply(t, Duel(PvpBloodResult.Win, OppA));
            PvpDailyProgress.Apply(t, Duel(PvpBloodResult.Win, OppB));

            Assert.AreEqual(2, t.Solves(PvpDailyTask.GladiatorsStreak));
        }

        /// <summary>Win A, win A: the second win against the previous win's opponent does not advance; the streak stays at 1 (not reset).</summary>
        [TestMethod]
        public void Streak_SameOpponentTwice_StaysWhereItIs()
        {
            var t = new FakeTarget().Accept(PvpDailyTask.GladiatorsStreak);

            PvpDailyProgress.Apply(t, Duel(PvpBloodResult.Win, OppA));
            PvpDailyProgress.Apply(t, Duel(PvpBloodResult.Win, OppA));
            Assert.AreEqual(1, t.Solves(PvpDailyTask.GladiatorsStreak), "stays, does not advance");

            PvpDailyProgress.Apply(t, Duel(PvpBloodResult.Win, OppB));
            Assert.AreEqual(2, t.Solves(PvpDailyTask.GladiatorsStreak), "and a different opponent then continues it");
        }

        /// <summary>Win A, LOSS, win B: the loss resets to 0, so B is the first win of a new streak (1, not 2).</summary>
        [TestMethod]
        public void Streak_LossResetsToZero()
        {
            var t = new FakeTarget().Accept(PvpDailyTask.GladiatorsStreak);

            PvpDailyProgress.Apply(t, Duel(PvpBloodResult.Win, OppA));
            PvpDailyProgress.Apply(t, Duel(PvpBloodResult.Loss, OppB));
            Assert.AreEqual(0, t.Solves(PvpDailyTask.GladiatorsStreak), "a loss resets");

            PvpDailyProgress.Apply(t, Duel(PvpBloodResult.Win, OppB));
            Assert.AreEqual(1, t.Solves(PvpDailyTask.GladiatorsStreak));
        }

        /// <summary>Win A, DRAW, win A: a draw resets too, and it also forgets the last opponent, so a win against A again starts a new streak at 1.</summary>
        [TestMethod]
        public void Streak_DrawResets_AndForgetsTheLastOpponent()
        {
            var t = new FakeTarget().Accept(PvpDailyTask.GladiatorsStreak);

            PvpDailyProgress.Apply(t, Duel(PvpBloodResult.Win, OppA));
            PvpDailyProgress.Apply(t, Duel(PvpBloodResult.Draw, OppA));
            Assert.AreEqual(0, t.Solves(PvpDailyTask.GladiatorsStreak));

            PvpDailyProgress.Apply(t, Duel(PvpBloodResult.Win, OppA));
            Assert.AreEqual(1, t.Solves(PvpDailyTask.GladiatorsStreak), "after a reset the previous opponent is a fresh start");
        }

        /// <summary>Once the streak reaches the target it is done for this acceptance: a later loss cannot take it back.</summary>
        [TestMethod]
        public void Streak_Completed_IsNotUndoneByALoss()
        {
            var t = new FakeTarget().Accept(PvpDailyTask.GladiatorsStreak);

            PvpDailyProgress.Apply(t, Duel(PvpBloodResult.Win, OppA));
            PvpDailyProgress.Apply(t, Duel(PvpBloodResult.Win, OppB));
            PvpDailyProgress.Apply(t, Duel(PvpBloodResult.Loss, OppC));

            Assert.AreEqual(2, t.Solves(PvpDailyTask.GladiatorsStreak));
        }

        /// <summary>Non-1v1 matches neither advance nor break the streak: a Tugak loss between two 1v1 wins leaves it intact.</summary>
        [TestMethod]
        public void Streak_OtherModes_DoNotBreakIt()
        {
            var t = new FakeTarget().Accept(PvpDailyTask.GladiatorsStreak);

            PvpDailyProgress.Apply(t, Duel(PvpBloodResult.Win, OppA));
            PvpDailyProgress.Apply(t, Pit(0, PvpBloodResult.Loss));
            PvpDailyProgress.Apply(t, Bg(PvpDailyMode.Koth, PvpBloodResult.Loss));
            PvpDailyProgress.Apply(t, Duel(PvpBloodResult.Win, OppB));

            Assert.AreEqual(2, t.Solves(PvpDailyTask.GladiatorsStreak));
        }

        /// <summary>
        /// The forfeit fact resets the streak (1 -> 0, last opponent forgotten) and touches NOTHING else: every 1v1 counter is held at 0 and
        /// stays there, and no stamp is written. Run through the normal path instead, the same Loss would advance BloodInTheSand.
        /// </summary>
        [TestMethod]
        public void ForfeitFact_ResetsTheStreak_AndAdvancesNothingElse()
        {
            var t = new FakeTarget()
                .Accept(PvpDailyTask.GladiatorsStreak)
                .Accept(PvpDailyTask.BloodInTheSand)
                .Accept(PvpDailyTask.FirstBlood)
                .Accept(PvpDailyTask.ProvingGround);

            PvpDailyProgress.Apply(t, Duel(PvpBloodResult.Win, OppA));
            Assert.AreEqual(1, t.Solves(PvpDailyTask.GladiatorsStreak), "fixture: a streak of 1");
            var bloodInTheSand = t.Solves(PvpDailyTask.BloodInTheSand);
            var firstBlood = t.Solves(PvpDailyTask.FirstBlood);
            var provingGround = t.Solves(PvpDailyTask.ProvingGround);
            var stamps = t.Stamps.Count;

            PvpDailyProgress.Apply(t, PvpMatchFacts.ForfeitOf(Guid.NewGuid(), 0x50000001, 0));

            Assert.AreEqual(0, t.Solves(PvpDailyTask.GladiatorsStreak), "the forfeit breaks the streak");
            Assert.AreEqual(bloodInTheSand, t.Solves(PvpDailyTask.BloodInTheSand), "a forfeit is not a completed match");
            Assert.AreEqual(firstBlood, t.Solves(PvpDailyTask.FirstBlood));
            Assert.AreEqual(provingGround, t.Solves(PvpDailyTask.ProvingGround));
            Assert.AreEqual(stamps, t.Stamps.Count, "and no stamp is written");

            PvpDailyProgress.Apply(t, Duel(PvpBloodResult.Win, OppA));
            Assert.AreEqual(1, t.Solves(PvpDailyTask.GladiatorsStreak), "the previous opponent is forgotten: a new streak starts at 1");
        }

        /// <summary>Without the quest the streak keeps no state: a stored state is removed and nothing is counted.</summary>
        [TestMethod]
        public void Streak_NoQuest_ClearsState()
        {
            var t = new FakeTarget();
            t.State[PvpDailyStateSlot.GladiatorsStreak] = new PvpDailyOpponentState(1, new[] { OppA }).Format();

            PvpDailyProgress.Apply(t, Duel(PvpBloodResult.Win, OppB));

            Assert.IsNull(t.GetState(PvpDailyStateSlot.GladiatorsStreak));
            Assert.AreEqual(0, t.Writes.Count);
        }

        // ================= the state text =================

        /// <summary>The state round-trips, and is read as empty when malformed or when its count is not the quest's current count.</summary>
        [TestMethod]
        public void OpponentState_RoundTrips_AndStaleOrMalformedReadsEmpty()
        {
            var s = new PvpDailyOpponentState(2, new[] { OppA, 0xFFFFFFFFu });
            var text = s.Format();

            Assert.AreEqual("v1|2|0000000A,FFFFFFFF", text);
            CollectionAssert.AreEqual(new[] { OppA, 0xFFFFFFFFu }, PvpDailyOpponentState.Parse(text, 2).Accounts.ToArray());
            Assert.AreEqual(0, PvpDailyOpponentState.Parse(text, 0).Accounts.Count, "a count that is not the quest's is stale");
            Assert.AreEqual(0, PvpDailyOpponentState.Parse("garbage", 2).Accounts.Count);
            Assert.AreEqual(0, PvpDailyOpponentState.Parse("v1|2|zz", 2).Accounts.Count);
            Assert.AreEqual(0, PvpDailyOpponentState.Parse(null, 2).Accounts.Count);
        }

        /// <summary>
        /// Bounds: text over 128 characters is rejected even when well formed; more than 16 accounts is rejected; a malformed separator or
        /// version is rejected. Each comes back EMPTY at the current count.
        /// </summary>
        [TestMethod]
        public void OpponentState_OversizedAndMalformed_ReadEmpty()
        {
            var seventeen = string.Join(",", Enumerable.Range(1, 17).Select(i => i.ToString("X")));
            var sixteen = string.Join(",", Enumerable.Range(1, 16).Select(i => i.ToString("X")));
            var longButFewAccounts = "v1|2|" + new string('0', 124) + "A";

            Assert.IsTrue(("v1|2|" + seventeen).Length <= PvpDailyOpponentState.MaxTextLength, "fixture: under the length cap, so the COUNT rule rejects it");
            Assert.AreEqual(0, PvpDailyOpponentState.Parse("v1|2|" + seventeen, 2).Accounts.Count, "17 accounts");
            Assert.AreEqual(16, PvpDailyOpponentState.Parse("v1|2|" + sixteen, 2).Accounts.Count, "control: 16 short accounts parse");

            Assert.IsTrue(longButFewAccounts.Length > PvpDailyOpponentState.MaxTextLength);
            Assert.IsTrue(uint.TryParse(new string('0', 124) + "A", System.Globalization.NumberStyles.HexNumber, null, out _), "fixture: the one token is valid hex");
            Assert.AreEqual(0, PvpDailyOpponentState.Parse(longButFewAccounts, 2).Accounts.Count, "over 128 characters");

            Assert.AreEqual(0, PvpDailyOpponentState.Parse("v2|2|0000000A", 2).Accounts.Count, "unknown version");
            Assert.AreEqual(0, PvpDailyOpponentState.Parse("v1|2|0000000A|x", 2).Accounts.Count, "extra separator");
            Assert.AreEqual(0, PvpDailyOpponentState.Parse("v1|two|0000000A", 2).Accounts.Count, "non-numeric count");
            Assert.AreEqual(0, PvpDailyOpponentState.Parse("v1|2|0000000A,,G", 2).Accounts.Count, "a bad token");
            Assert.AreEqual(0, PvpDailyOpponentState.Parse(PvpDailyProgress.PendingStreakResetMarker, 0).Accounts.Count, "the pending-reset marker is never a state");
        }

        /// <summary>Append keeps only the newest 12 accounts, and a full state of 8-digit accounts fits the length cap and round-trips.</summary>
        [TestMethod]
        public void OpponentState_AppendKeepsTheNewest_AndAFullStateRoundTrips()
        {
            var state = PvpDailyOpponentState.Empty(0);

            for (uint i = 1; i <= 20; i++)
                state = state.Append(0xF0000000u + i, (int)i);

            Assert.AreEqual(PvpDailyOpponentState.KeepAccounts, state.Accounts.Count);
            Assert.AreEqual(0xF0000000u + 9, state.Accounts.First(), "the oldest eight were dropped");
            Assert.AreEqual(0xF0000000u + 20, state.Accounts.Last());

            var wide = new PvpDailyOpponentState(int.MaxValue, state.Accounts).Format();
            Assert.IsTrue(wide.Length <= PvpDailyOpponentState.MaxTextLength, $"a full state is {wide.Length} characters");
            CollectionAssert.AreEqual(state.Accounts.ToArray(), PvpDailyOpponentState.Parse(wide, int.MaxValue).Accounts.ToArray());
        }

        /// <summary>
        /// A WIN whose opponent account is unknown holds the streak: neither advancing nor resetting it. A loss with the same unknown opponent
        /// is the control that still resets.
        /// </summary>
        [TestMethod]
        public void Streak_WinWithUnknownOpponent_HoldsTheStreak()
        {
            var t = new FakeTarget().Accept(PvpDailyTask.GladiatorsStreak);
            PvpDailyProgress.Apply(t, Duel(PvpBloodResult.Win, OppA));
            Assert.AreEqual(1, t.Solves(PvpDailyTask.GladiatorsStreak), "fixture");

            var unknown = Duel(PvpBloodResult.Win, OppB) with { OpponentAccount = null };
            PvpDailyProgress.Apply(t, unknown);
            Assert.AreEqual(1, t.Solves(PvpDailyTask.GladiatorsStreak), "an unjudgeable win holds the streak");

            PvpDailyProgress.Apply(t, Duel(PvpBloodResult.Win, OppB));
            Assert.AreEqual(2, t.Solves(PvpDailyTask.GladiatorsStreak), "and it continues after");

            var t2 = new FakeTarget().Accept(PvpDailyTask.GladiatorsStreak, 1);
            PvpDailyProgress.Apply(t2, Duel(PvpBloodResult.Loss, OppB) with { OpponentAccount = null });
            Assert.AreEqual(0, t2.Solves(PvpDailyTask.GladiatorsStreak), "control: a loss still resets");
        }

        // ================= the player's chat lines =================

        /// <summary>
        /// One [Arena] line per counter that advanced, with the unit after the target for King's Ground (seconds) and Crystal Breaker (damage),
        /// and the completion line naming the giver when a counter reaches its target. Exact text, from the pure result.
        /// </summary>
        [TestMethod]
        public void Messages_ProgressAndCompletionLines_ExactText()
        {
            var t = new FakeTarget().Accept(PvpDailyTask.Carnage).Accept(PvpDailyTask.IntoThePit, 2).Accept(PvpDailyTask.PitVictor);

            var pit = PvpDailyProgress.Apply(t, Pit(2, PvpBloodResult.Win));

            CollectionAssert.AreEquivalent(new[]
            {
                "[Arena] Carnage: 2 of 5.",
                "[Arena] Into the Pit is complete. Return to Black Ferah.",
                "[Arena] Pit Victor is complete. Return to Black Ferah.",
            }, pit.Messages.ToArray());

            var k = new FakeTarget().Accept(PvpDailyTask.KingsGround).Accept(PvpDailyTask.HoldTheCrown, 1);
            var koth = PvpDailyProgress.Apply(k, Bg(PvpDailyMode.Koth, PvpBloodResult.Loss, hill: 75));
            CollectionAssert.AreEquivalent(new[] { "[Arena] King's Ground: 75 of 180 seconds.", "[Arena] Hold the Crown is complete. Return to Ler Rhan." }, koth.Messages.ToArray());

            var a = new FakeTarget().Accept(PvpDailyTask.CrystalBreaker).Accept(PvpDailyTask.WeeklyBattlegrounds, 3);
            var ad = PvpDailyProgress.Apply(a, new PvpMatchFacts(Guid.NewGuid(), 0x50000001, PvpDailyMode.AttackDefend, 0, PvpBloodResult.Loss, 0, 0, 1234, 0, 0, null, 0));
            CollectionAssert.AreEquivalent(new[] { "[Arena] Crystal Breaker: 1234 of 5000 damage.", "[Arena] The Long War: 4 of 20." }, ad.Messages.ToArray());
        }

        /// <summary>
        /// The streak: an advance says so; a RESET says nothing (the log still records it); a counter already at its target, or a quest not
        /// held, says nothing.
        /// </summary>
        [TestMethod]
        public void Messages_StreakResetAndUnchangedCountersSendNothing()
        {
            var t = new FakeTarget().Accept(PvpDailyTask.GladiatorsStreak).Accept(PvpDailyTask.FirstBlood, 1);

            var win = PvpDailyProgress.Apply(t, Duel(PvpBloodResult.Win, OppA));
            CollectionAssert.AreEqual(new[] { "[Arena] Gladiator's Streak: 1 of 2." }, win.Messages.ToArray(), "FirstBlood is already done; nothing else is held");

            var loss = PvpDailyProgress.Apply(t, Duel(PvpBloodResult.Loss, OppB));
            Assert.AreEqual(0, loss.Messages.Count, "a streak reset sends nothing");
            Assert.IsTrue(loss.Log.Any(l => l.Contains("->0")), "but it is logged");

            var forfeit = PvpDailyProgress.Apply(new FakeTarget().Accept(PvpDailyTask.GladiatorsStreak, 1), PvpMatchFacts.ForfeitOf(Guid.NewGuid(), 0x50000001, 0));
            Assert.AreEqual(0, forfeit.Messages.Count, "nor does a forfeit reset");

            Assert.AreEqual(0, PvpDailyProgress.Apply(new FakeTarget(), Pit(5, PvpBloodResult.Win)).Messages.Count, "no quest held, no line");
        }

        /// <summary>
        /// The player line reflects what the quest registry STORED, not what was written: a target that clamps every write to 0 (a missing
        /// quest row reads max_solves 0) gets no line from any of the three write sites (the plain counter, the distinct-opponent counter and
        /// the streak advance), and the log says the write fell short.
        /// </summary>
        [TestMethod]
        public void Messages_ClampedWriteSendsNoLine_AtEveryWriteSite()
        {
            var t = new FakeTarget { ClampTo = 0 }.Accept(PvpDailyTask.Carnage).Accept(PvpDailyTask.ProvingGround).Accept(PvpDailyTask.GladiatorsStreak);

            var pit = PvpDailyProgress.Apply(t, Pit(2, PvpBloodResult.Win));
            Assert.AreEqual(0, pit.Messages.Count, string.Join(" | ", pit.Messages));
            Assert.AreEqual(0, t.Solves(PvpDailyTask.Carnage));
            Assert.IsTrue(pit.Log.Any(l => l.Contains("(stored 0; no player line)")), string.Join(" | ", pit.Log));

            var duel = PvpDailyProgress.Apply(t, Duel(PvpBloodResult.Win, OppA));
            Assert.AreEqual(0, duel.Messages.Count, string.Join(" | ", duel.Messages));
            Assert.IsTrue(duel.Log.Any(l => l.StartsWith(PvpDailyCatalog.CounterNameOf(PvpDailyTask.ProvingGround)) && l.Contains("stored 0")), string.Join(" | ", duel.Log));
            Assert.IsTrue(duel.Log.Any(l => l.StartsWith(PvpDailyCatalog.CounterNameOf(PvpDailyTask.GladiatorsStreak)) && l.Contains("stored 0")), string.Join(" | ", duel.Log));
        }

        /// <summary>A partial clamp below the intended value also sends nothing; an unclamped write still sends the stored count.</summary>
        [TestMethod]
        public void Messages_LineCarriesStoredCount_PartialClampSendsNothing()
        {
            var clamped = new FakeTarget { ClampTo = 1 }.Accept(PvpDailyTask.Carnage);
            Assert.AreEqual(0, PvpDailyProgress.Apply(clamped, Pit(3, PvpBloodResult.Win)).Messages.Count);
            Assert.AreEqual(1, clamped.Solves(PvpDailyTask.Carnage));

            var roomy = new FakeTarget { ClampTo = 5 }.Accept(PvpDailyTask.Carnage);
            CollectionAssert.AreEqual(new[] { "[Arena] Carnage: 3 of 5." }, PvpDailyProgress.Apply(roomy, Pit(3, PvpBloodResult.Win)).Messages.ToArray());
        }

        // ================= the WP-H stamps =================

        /// <summary>
        /// A battleground WIN raises the lifetime battleground wins (KOTH and A/D); a loss and a 1v1 win do not. The peak only ever goes up,
        /// with or without any quest held.
        /// </summary>
        [TestMethod]
        public void Stamps_BattlegroundWins_AndPeakOnlyRises()
        {
            var t = new FakeTarget();

            PvpDailyProgress.Apply(t, Bg(PvpDailyMode.Koth, PvpBloodResult.Win, peak: 1600));
            PvpDailyProgress.Apply(t, Bg(PvpDailyMode.AttackDefend, PvpBloodResult.Win, team: 1, peak: 1550));
            PvpDailyProgress.Apply(t, Bg(PvpDailyMode.Koth, PvpBloodResult.Loss, peak: 1700));
            PvpDailyProgress.Apply(t, Duel(PvpBloodResult.Win, OppA));

            Assert.AreEqual(2, t.GetStamp(PvpDailyStamp.LifetimeBattlegroundWins), "two battleground wins; the loss and the 1v1 win do not count");
            Assert.AreEqual(1700, t.GetStamp(PvpDailyStamp.PeakRating), "the highest peak seen");

            PvpDailyProgress.Apply(t, Bg(PvpDailyMode.Koth, PvpBloodResult.Loss, peak: 1200));
            Assert.AreEqual(1700, t.GetStamp(PvpDailyStamp.PeakRating), "a lower peak never lowers it");
        }

        // ================= the shared eligibility predicate =================

        /// <summary>Each exclusion alone excludes; all clear is eligible; the first failing reason is the one reported.</summary>
        [TestMethod]
        public void Eligibility_EachExclusionAlone_AndTheOrder()
        {
            Assert.AreEqual(PvpRewardExclusion.None, PvpRewardEligibility.Evaluate(true, false, false, false, false));
            Assert.AreEqual(PvpRewardExclusion.NotLive, PvpRewardEligibility.Evaluate(false, false, false, false, false));
            Assert.AreEqual(PvpRewardExclusion.SameIpOpponents, PvpRewardEligibility.Evaluate(true, true, false, false, false));
            Assert.AreEqual(PvpRewardExclusion.Forfeited, PvpRewardEligibility.Evaluate(true, false, true, false, false));
            Assert.AreEqual(PvpRewardExclusion.UnpaidRematch, PvpRewardEligibility.Evaluate(true, false, false, true, false));
            Assert.AreEqual(PvpRewardExclusion.BelowContributionFloor, PvpRewardEligibility.Evaluate(true, false, false, false, true));
            Assert.AreEqual(PvpRewardExclusion.NotLive, PvpRewardEligibility.Evaluate(false, true, true, true, true), "the first reason wins");
            Assert.IsTrue(PvpRewardEligibility.IsEligible(true, false, false, false, false));
            Assert.IsFalse(PvpRewardEligibility.IsEligible(true, false, false, false, true));
        }
    }
}
