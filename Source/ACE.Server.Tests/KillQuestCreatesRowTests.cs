using System;
using System.IO;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Database;
using ACE.Database.Models.World;
using ACE.Entity.Enum.Properties;
using ACE.Server.Entity;
using ACE.Server.Managers;
using ACE.Server.WorldObjects;

namespace ACE.Server.Tests
{
    /// <summary>
    /// ML round 17: a general-purpose, per-creature kill-task registry-row bootstrap
    /// (PropertyBool 9065, KillQuestCreatesRow), for a kill task a player can complete before the NPC that
    /// normally arms its row has had the chance to - the Menhir Drummer being the motivating case. Kanokeh
    /// DOES arm BluespireMenhirDrummer (SetQuestCompletions amount 0), but only at charge ACCEPTANCE
    /// (Content/sql/weenies/1003202 Hea Kanokeh the Drum-Keeper.sql:30-31,460-466); a player who reaches
    /// the ring and kills the Drummer before ever accepting that charge holds no row for HasQuest to find.
    /// This extends the Bluespire ladder's existing bootstrap (BluespireLadderRewards.
    /// ShouldBootstrapClearRow) rather than duplicating it: Creature.OnDeath_HandleKillTask now computes
    /// `killQuestCreatesRow = ShouldBypassHasQuestGate(ladderBootstrap, KillQuestCreatesRow, isPrimarySlot)`
    /// and uses that single combined flag everywhere ladderBootstrap alone used to be read.
    ///
    /// SLOT-1 ONLY FIX (2026-09-25). KillQuestCreatesRow is a per-CREATURE property, but
    /// OnDeath_HandleKillTask runs once per non-null KillQuest/KillQuest2/KillQuest3 slot with the SAME
    /// creature-wide flag value. Originally the flag was OR'd into every slot's bypass equally. Real
    /// incident: the Menhir Drummer (1003262) carries the flag true for its own BluespireMenhirDrummer
    /// bootstrap in KillQuest (slot 1), and briefly also carried an unrelated quest,
    /// 'AunTumerokBountyCount', in KillQuest2 (Content/sql/patches/marae_lassel_killquest_auntumerok.sql) -
    /// which let the same kill bootstrap Hea Piritaha's 50-kill bounty row for anyone, skipping her
    /// HasQuest gate entirely (worked around by excluding 1003262 from that bounty in PR #1370). The fix
    /// extracts the decision into the pure <see cref="Creature.ShouldBypassHasQuestGate"/> and gives it an
    /// `isPrimarySlot` parameter, so the creature's own flag is read ONLY for the KillQuest slot; KillQuest2
    /// and KillQuest3 stay HasQuest-gated regardless of what the creature's flag says.
    ///
    /// Things pinned here:
    ///  - the property itself (WorldObject_Properties.KillQuestCreatesRow) round-trips through
    ///    PropertyBool 9065 exactly like every other fork bool property, defaulting to false/absent;
    ///  - ShouldBypassHasQuestGate's behavior directly (pure function, no live Player/Fellowship needed) -
    ///    this is the discriminating coverage for the slot-1-only fix;
    ///  - Creature_Death.cs's source, by exact text, routes both the killer gate and the fellow gate through
    ///    the SAME combined variable, threads it into TryHandleKillTask's bootstrapRow parameter for both,
    ///    and calls OnDeath_HandleKillTask with the correct isPrimarySlot per slot. This is the same
    ///    source-text-pin technique
    ///    BluespireLadderKillCooldownTests.HandleKillTask_CallsApplyKillStamp_AndGatesRepeatPayoutBehindOutcome
    ///    uses, for the same reason: OnDeath_HandleKillTask needs a live Player with DamageHistory and a
    ///    Fellowship to exercise end-to-end, which (per CLAUDE.md) reaches DatabaseManager.Authentication
    ///    and PropertyManager reads that throw outside a real server - not available to this harness.
    ///
    /// The underlying per-kill bootstrap mechanics themselves (row creation vs. credit-only, killer AND
    /// fellow both bypass HasQuest identically) are already exhaustively covered by
    /// BluespireLadderKillCooldownTests against QuestManager.ApplyKillStamp directly - ApplyKillStamp has
    /// no knowledge of WHERE its bootstrapRow argument came from, so those tests exercise this feature's
    /// mechanism today under the ladder's own true/false literal and will exercise it identically once any
    /// caller (including this property) passes bootstrapRow: true instead.
    ///
    /// NOT covered here: Kanokeh's own arm no longer wiping a pre-kill's row. That guard is #1291's fix
    /// (Content/sql/weenies/1003202 is owned by that branch), which must land with or before this PR for
    /// the out-of-order-kill scenario this property exists for to actually work end to end.
    /// </summary>
    [TestClass]
    public class KillQuestCreatesRowTests
    {
        private const string PlainKillQuest = "TestKillQuestCreatesRowPlainTask"; // MaxSolves=20, MinDelta=0

        // Menhir-Drummer-shaped: MaxSolves=1, MinDelta=0, matches Content/sql/weenies/1003262's world quest
        // row exactly, for the OnCooldown regression below.
        private const string MaxSolvedKillQuest = "TestKillQuestCreatesRowMaxSolvedTask";

        // A REAL ladder rung-clear name (rung 2, distinct from BluespireLadderKillCooldownTests' rung 1
        // fixture, so the two test classes' seeded quests can never collide) - BluespireLadderRewards.
        // RungForQuestName only recognizes the compiled BluespireLadderD{1-6}Cleared table, so this fixture
        // MUST use a genuine name, unlike PlainKillQuest/MaxSolvedKillQuest above which deliberately do not.
        private const string LadderShapedQuest = "BluespireLadderD2Cleared";
        private const uint LadderMinDelta = 72000;

        [TestInitialize]
        public void SeedWorldQuests()
        {
            DatabaseManager.World.AddCachedQuest(new Quest { Name = PlainKillQuest, MaxSolves = 20, MinDelta = 0 });
            DatabaseManager.World.AddCachedQuest(new Quest { Name = MaxSolvedKillQuest, MaxSolves = 1, MinDelta = 0 });
            DatabaseManager.World.AddCachedQuest(new Quest { Name = LadderShapedQuest, MaxSolves = -1, MinDelta = LadderMinDelta });
        }

        [TestCleanup]
        public void ClearWorldQuests()
        {
            DatabaseManager.World.ClearCachedQuest(PlainKillQuest);
            DatabaseManager.World.ClearCachedQuest(MaxSolvedKillQuest);
            DatabaseManager.World.ClearCachedQuest(LadderShapedQuest);
        }

        // ------------------------------------------------------------------------------------------------
        // The property itself: PropertyBool 9065, absent/false by default, round-trips like its siblings.
        // ------------------------------------------------------------------------------------------------

        [TestMethod]
        public void KillQuestCreatesRow_DefaultsFalse_AndRoundTrips()
        {
            var creature = TestCreatures.CreateQuestBearer();

            Assert.IsFalse(creature.KillQuestCreatesRow, "absent must read as false - every creature in the game bar the opt-in ones");
            Assert.IsNull(creature.GetProperty(PropertyBool.KillQuestCreatesRow), "false must not be a stored zero/false row, it must be ABSENT");

            creature.KillQuestCreatesRow = true;
            Assert.IsTrue(creature.KillQuestCreatesRow);
            Assert.AreEqual(true, creature.GetProperty(PropertyBool.KillQuestCreatesRow));

            creature.KillQuestCreatesRow = false;
            Assert.IsFalse(creature.KillQuestCreatesRow);
            Assert.IsNull(creature.GetProperty(PropertyBool.KillQuestCreatesRow), "setting false must remove the row, matching ScaleWieldedToBody/DisableSticky");
        }

        // ------------------------------------------------------------------------------------------------
        // Bootstrap mechanics via ApplyKillStamp directly (the same seam BluespireLadderKillCooldownTests
        // uses) - the "killer" and "fellow" cases are the SAME call shape from ApplyKillStamp's side, since
        // Creature_Death.cs calls TryHandleKillTask -> QuestManager.HandleKillTask -> ApplyKillStamp
        // identically for the damaging player and for each qualifying fellow (Creature_Death.cs:496-520).
        // ------------------------------------------------------------------------------------------------

        [TestMethod]
        public void ApplyKillStamp_KillQuestCreatesRowBootstrap_CreatesRowForKillerWithNoPriorQuest()
        {
            var killerQm = TestCreatures.CreateQuestBearer("killer").QuestManager;

            Assert.IsFalse(killerQm.HasQuest(PlainKillQuest), "the killer must not already hold this quest - that is the whole point of the bootstrap");

            var outcome = killerQm.ApplyKillStamp(PlainKillQuest, bootstrapRow: true);

            Assert.AreEqual(QuestManager.KillStampOutcome.Created, outcome);
            Assert.AreEqual(1, killerQm.GetCurrentSolves(PlainKillQuest));
        }

        [TestMethod]
        public void ApplyKillStamp_KillQuestCreatesRowBootstrap_CreatesRowForFellowWithNoPriorQuest()
        {
            // Fellowship.WithinRange qualification happens in Creature_Death.cs before TryHandleKillTask is
            // ever reached; from ApplyKillStamp's side a qualifying fellow's stamp is indistinguishable from
            // the killer's own - same method, same bootstrapRow: true, same no-prior-row starting state.
            var fellowQm = TestCreatures.CreateQuestBearer("fellow").QuestManager;

            Assert.IsFalse(fellowQm.HasQuest(PlainKillQuest));

            var outcome = fellowQm.ApplyKillStamp(PlainKillQuest, bootstrapRow: true);

            Assert.AreEqual(QuestManager.KillStampOutcome.Created, outcome);
            Assert.AreEqual(1, fellowQm.GetCurrentSolves(PlainKillQuest));
        }

        [TestMethod]
        public void ApplyKillStamp_WithoutBootstrap_StillRefusesAPlayerWhoNeverHeldTheQuest()
        {
            // The no-bootstrap default path, unchanged: every kill task in the game bar the ladder rungs and
            // KillQuestCreatesRow opt-ins.
            var qm = TestCreatures.CreateQuestBearer().QuestManager;

            var outcome = qm.ApplyKillStamp(PlainKillQuest, bootstrapRow: false);

            Assert.AreEqual(QuestManager.KillStampOutcome.NotHeld, outcome);
            Assert.AreEqual(0, qm.GetCurrentSolves(PlainKillQuest));
        }

        /// <summary>
        /// Playability regression (round 17, second review pass): before this fix, ApplyKillStamp's
        /// OnCooldown branch fired for ANY bootstrapRow: true call whose row existed and failed CanSolve -
        /// which made it fire for a repeat Menhir Drummer kill too, since KillQuestCreatesRow also passes
        /// bootstrapRow: true (Creature_Death.cs's combined killQuestCreatesRow flag). BluespireMenhirDrummer
        /// has max_Solves 1 and no MinDelta cooldown at all, so CanSolve returning false on it means "the
        /// solve cap is hit", never "come back later" - yet the old code printed the ladder's "already
        /// cleared this rung today... cleared again in <TimeSpan.MaxValue nonsense>" message for it instead
        /// of "Your task is complete!". The fix scopes OnCooldown to
        /// BluespireLadderRewards.RungForQuestName(questName) != 0, so a non-ladder bootstrap kill on a
        /// maxed row falls through to the same Refused classification (and therefore the same message) a
        /// non-bootstrap repeat kill on a maxed row already gets -
        /// see ApplyKillStamp_BlockedExistingRowUpdate_ReturnsRefused_NotRestamped in
        /// BluespireLadderKillCooldownTests for that pre-existing, still-unchanged behaviour.
        /// </summary>
        [TestMethod]
        public void ApplyKillStamp_KillQuestCreatesRowBootstrap_RepeatKillOnMaxSolvedRow_ReturnsRefused_NotOnCooldown()
        {
            var qm = TestCreatures.CreateQuestBearer().QuestManager;

            // pre-armed at the cap, exactly as a Menhir Drummer kill (Kanokeh's SetQuestCompletions amount 0
            // at charge acceptance, or this feature's own bootstrap) leaves it after one solve
            qm.SetQuestCompletions(MaxSolvedKillQuest, 1);

            var outcome = qm.ApplyKillStamp(MaxSolvedKillQuest, bootstrapRow: true);

            Assert.AreEqual(QuestManager.KillStampOutcome.Refused, outcome,
                "a maxed KillQuestCreatesRow row must be Refused (task complete), never OnCooldown (ladder-only message)");
            Assert.AreEqual(1, qm.GetCurrentSolves(MaxSolvedKillQuest), "a refused repeat kill must not change the solve count");

            Assert.IsFalse(BluespireLadderRewards.ShouldPayOnKillStamp(outcome),
                "a refused repeat kill must never route the ladder's repeat payout");
        }

        /// <summary>
        /// The other half of the same fix: a GENUINE ladder rung name must still gate OnCooldown under
        /// bootstrapRow: true, identically to before this change - this feature widens which non-ladder
        /// quests may pass bootstrapRow: true, it must never narrow what the ladder itself already does.
        /// Uses rung 2 (LadderShapedQuest) rather than duplicating BluespireLadderKillCooldownTests' rung-1
        /// coverage; that class remains the exhaustive source for the ladder's own restamp-after-cooldown
        /// behaviour.
        /// </summary>
        [TestMethod]
        public void ApplyKillStamp_KillQuestCreatesRowBootstrap_LadderQuestName_StillGatesOnCooldown()
        {
            Assert.IsTrue(PropertyManager.ModifyDouble("quest_mindelta_rate", 1.0),
                "quest_mindelta_rate is missing from DefaultDoubleProperties");

            try
            {
                var qm = TestCreatures.CreateQuestBearer().QuestManager;

                var first = qm.ApplyKillStamp(LadderShapedQuest, bootstrapRow: true);
                Assert.AreEqual(QuestManager.KillStampOutcome.Created, first);

                var second = qm.ApplyKillStamp(LadderShapedQuest, bootstrapRow: true);
                Assert.AreEqual(QuestManager.KillStampOutcome.OnCooldown, second,
                    "a real ladder rung name must still gate OnCooldown within its daily window - this fix must not touch that");
            }
            finally
            {
                PropertyManager.ModifyDouble("quest_mindelta_rate", 1.0);
            }
        }

        // ------------------------------------------------------------------------------------------------
        // ShouldBypassHasQuestGate directly: the pure decision, and the discriminating coverage for the
        // slot-1-only fix. This is what fails before the fix (the old formula OR'd the creature-wide flag
        // into every slot) and passes after it.
        // ------------------------------------------------------------------------------------------------

        [TestMethod]
        public void ShouldBypassHasQuestGate_PrimarySlot_CreatureFlagBypasses()
        {
            // Slot 1 (KillQuest): the per-creature flag applies - this is what lets the Menhir Drummer
            // bootstrap its OWN BluespireMenhirDrummer row before Kanokeh ever arms it.
            Assert.IsTrue(Creature.ShouldBypassHasQuestGate(ladderBootstrap: false, creatureKillQuestCreatesRow: true, isPrimarySlot: true),
                "slot 1 must still bootstrap when the creature carries KillQuestCreatesRow");
        }

        [TestMethod]
        public void ShouldBypassHasQuestGate_NonPrimarySlot_CreatureFlagIsIgnored()
        {
            // Slot 2/3: the flag must NOT apply, even though the SAME creature carries it true. This is
            // exactly the Menhir Drummer / AunTumerokBountyCount incident (PR #1370): a KillQuest2 tag for
            // an unrelated quest must stay HasQuest-gated regardless of what slot 1's flag says.
            Assert.IsFalse(Creature.ShouldBypassHasQuestGate(ladderBootstrap: false, creatureKillQuestCreatesRow: true, isPrimarySlot: false),
                "slot 2/3 must stay HasQuest-gated even when the creature's KillQuestCreatesRow flag is true");
        }

        [TestMethod]
        public void ShouldBypassHasQuestGate_LadderBootstrap_AppliesRegardlessOfSlot()
        {
            // ladderBootstrap is already quest-name-scoped by the caller (it is computed per call from the
            // specific quest name passed for THAT slot), so it applies unconditionally here, independent of
            // isPrimarySlot.
            Assert.IsTrue(Creature.ShouldBypassHasQuestGate(ladderBootstrap: true, creatureKillQuestCreatesRow: false, isPrimarySlot: false),
                "ladderBootstrap must bypass regardless of slot");
            Assert.IsTrue(Creature.ShouldBypassHasQuestGate(ladderBootstrap: true, creatureKillQuestCreatesRow: false, isPrimarySlot: true),
                "ladderBootstrap must bypass regardless of slot");
        }

        [TestMethod]
        public void ShouldBypassHasQuestGate_NoBootstrapNoFlag_NeverBypasses()
        {
            Assert.IsFalse(Creature.ShouldBypassHasQuestGate(ladderBootstrap: false, creatureKillQuestCreatesRow: false, isPrimarySlot: true));
            Assert.IsFalse(Creature.ShouldBypassHasQuestGate(ladderBootstrap: false, creatureKillQuestCreatesRow: false, isPrimarySlot: false));
        }

        // ------------------------------------------------------------------------------------------------
        // Source-text pins on Creature_Death.cs: the exact wiring that makes KillQuestCreatesRow reach both
        // the killer gate and the fellow gate through the SAME combined variable ladderBootstrap already
        // used, rather than a second parallel bypass - and that OnDeath_HandleKillTask is actually driven by
        // ShouldBypassHasQuestGate (match code, not comments).
        // ------------------------------------------------------------------------------------------------

        [TestMethod]
        public void OnDeathHandleKillTask_CombinesLadderBootstrapWithKillQuestCreatesRowProperty()
        {
            var body = OnDeathHandleKillTaskBody();

            var ladderIdx = body.IndexOf("var ladderBootstrap = BluespireLadderRewards.ShouldBootstrapClearRow(", StringComparison.Ordinal);
            Assert.IsTrue(ladderIdx >= 0, "the ladder's own bootstrap computation must still be present, unchanged");

            var combinedIdx = body.IndexOf("var killQuestCreatesRow = ShouldBypassHasQuestGate(ladderBootstrap, KillQuestCreatesRow, isPrimarySlot);", StringComparison.Ordinal);
            Assert.IsTrue(combinedIdx >= 0,
                "OnDeath_HandleKillTask must compute the combined flag via ShouldBypassHasQuestGate, passing isPrimarySlot through");

            Assert.IsTrue(combinedIdx > ladderIdx, "the combined flag must be computed AFTER the ladder's own bootstrap decision, not before it");
        }

        [TestMethod]
        public void OnDeath_CallsHandleKillTask_WithCorrectIsPrimarySlot_PerSlot()
        {
            var body = OnDeathBody();

            StringAssert.Contains(body, "OnDeath_HandleKillTask(KillQuest, isPrimarySlot: true);",
                "KillQuest (slot 1) must be called with isPrimarySlot: true");
            StringAssert.Contains(body, "OnDeath_HandleKillTask(KillQuest2, isPrimarySlot: false);",
                "KillQuest2 must be called with isPrimarySlot: false");
            StringAssert.Contains(body, "OnDeath_HandleKillTask(KillQuest3, isPrimarySlot: false);",
                "KillQuest3 must be called with isPrimarySlot: false");
        }

        [TestMethod]
        public void OnDeathHandleKillTask_KillerGate_UsesTheCombinedFlag_NotLadderBootstrapAlone()
        {
            var body = OnDeathHandleKillTaskBody();

            StringAssert.Contains(body, "if (killQuestCreatesRow || playerDamager.QuestManager.HasQuest(killQuest))",
                "the killer gate must read the combined flag");
            StringAssert.Contains(body, "TryHandleKillTask(playerDamager, killQuest, killTaskCredits, cap, killQuestCreatesRow);",
                "the killer's bootstrapRow argument must be the combined flag");

            Assert.IsFalse(body.Contains("if (ladderBootstrap || playerDamager.QuestManager.HasQuest(killQuest))"),
                "the killer gate must no longer test ladderBootstrap alone - that would silently exclude KillQuestCreatesRow");
        }

        [TestMethod]
        public void OnDeathHandleKillTask_FellowGate_UsesTheCombinedFlag_NotLadderBootstrapAlone()
        {
            var body = OnDeathHandleKillTaskBody();

            StringAssert.Contains(body, "if (killQuestCreatesRow || fellow.QuestManager.HasQuest(killQuest))",
                "the fellow gate must read the combined flag, exactly as the killer gate does");
            StringAssert.Contains(body, "TryHandleKillTask(fellow, killQuest, killTaskCredits, cap, killQuestCreatesRow);",
                "the fellow's bootstrapRow argument must be the combined flag");

            Assert.IsFalse(body.Contains("if (ladderBootstrap || fellow.QuestManager.HasQuest(killQuest))"),
                "the fellow gate must no longer test ladderBootstrap alone");
        }

        /// <summary>
        /// Ladder names unchanged: this feature must not touch BluespireLadderRewards.RungForQuestName or
        /// ShouldBootstrapClearRow at all - it only widens the CALLER's combined flag, never the ladder's own
        /// six-name table. BluespireLadderGateTests already pins that table exhaustively (case-insensitive,
        /// near-miss names, on/off switch); this is a narrow source check that the ladder computation itself
        /// - the call this feature sits beside - is byte-identical to before.
        /// </summary>
        [TestMethod]
        public void OnDeathHandleKillTask_LadderBootstrapCall_IsUnchanged()
        {
            var body = OnDeathHandleKillTaskBody();

            StringAssert.Contains(body,
                "var ladderBootstrap = BluespireLadderRewards.ShouldBootstrapClearRow(\n                QuestManager.GetQuestName(killQuest), BluespireLadder.Enabled);",
                "the ladder's own bootstrap decision must be untouched by this change");
        }

        private static string OnDeathHandleKillTaskBody()
        {
            var src = CreatureDeathSource();

            var start = src.IndexOf("public void OnDeath_HandleKillTask(string killQuest, bool isPrimarySlot)", StringComparison.Ordinal);
            Assert.IsTrue(start >= 0, "OnDeath_HandleKillTask is missing");

            var end = src.IndexOf("public static bool ShouldBypassHasQuestGate(", start, StringComparison.Ordinal);
            Assert.IsTrue(end > start, "ShouldBypassHasQuestGate no longer follows OnDeath_HandleKillTask");

            return src.Substring(start, end - start);
        }

        private static string OnDeathBody()
        {
            var src = CreatureDeathSource();

            var start = src.IndexOf("public virtual DeathMessage OnDeath(", StringComparison.Ordinal);
            Assert.IsTrue(start >= 0, "OnDeath is missing");

            var end = src.IndexOf("public DeathMessage GetDeathMessage(", start, StringComparison.Ordinal);
            Assert.IsTrue(end > start, "GetDeathMessage no longer follows OnDeath");

            return src.Substring(start, end - start);
        }

        private static string CreatureDeathSource()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);

            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "Source", "ACE.Server", "WorldObjects", "Creature_Death.cs")))
                dir = dir.Parent;

            Assert.IsNotNull(dir, $"Could not find Creature_Death.cs by walking up from {AppContext.BaseDirectory}");

            return File.ReadAllText(Path.Combine(dir.FullName, "Source", "ACE.Server", "WorldObjects", "Creature_Death.cs")).Replace("\r\n", "\n");
        }
    }
}
