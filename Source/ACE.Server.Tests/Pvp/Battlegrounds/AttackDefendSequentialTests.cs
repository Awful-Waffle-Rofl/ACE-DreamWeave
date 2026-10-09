using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Entity;
using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Server.Pvp;
using ACE.Server.Pvp.Battlegrounds;
using ACE.Server.Tests.ThreadDungeons;
using ACE.Server.WorldObjects;

namespace ACE.Server.Tests.Pvp.Battlegrounds
{
    /// <summary>
    /// Sequential crystals and the Attack/Defend start gates (Docs/Pvp/ATTACK-DEFEND.md "Sequential crystals", "Start gates"): the pure
    /// sequence, the plan that builds it, the crystal damage gate that reads it, the tick handler that advances it and speaks, the
    /// per-attacker refusal throttle, and the shipped gate geometry. Each behavioural claim is paired with the revert that breaks it
    /// (named in the test's comment).
    /// </summary>
    [TestClass]
    public class AttackDefendSequentialTests
    {
        private static readonly DateTime Start = FakeObjectiveContext.Start;
        private const int Max = 20000;

        private static BattlegroundLayout Mines => BattlegroundMapCatalog.Bg003c;

        private static BattlegroundDials Dials(int count = 0, bool sequential = true) =>
            BattlegroundTunables.Defaults with { AdCrystalCount = count, AdSequentialCrystals = sequential };

        private static AttackDefendPlan Plan(int count = 0, bool sequential = true) => AttackDefendPlan.Build(Mines, Dials(count, sequential), attackerCount: 4);

        private static (AttackDefendTickHandler Handler, FakeObjectiveContext Ctx, AttackDefendPlan Plan) Live(int count = 0, bool sequential = true)
        {
            var plan = Plan(count, sequential);
            var handler = new AttackDefendTickHandler(Dials(count, sequential), plan);
            var ctx = new FakeObjectiveContext();

            handler.OnLive(ctx);

            return (handler, ctx, plan);
        }

        // ---------------- CrystalSequence ----------------

        /// <summary>Revert: starting the cursor anywhere but the first crystal, or not skipping destroyed ones, fails.</summary>
        [TestMethod]
        public void Sequence_StartsAtTheFirstCrystal_AndWalksInOrder()
        {
            var s = new CrystalSequence(new[] { "A", "B", "C" });

            Assert.AreEqual(0, s.Current);
            Assert.IsTrue(s.IsVulnerable(0));
            Assert.IsFalse(s.IsVulnerable(1));
            Assert.IsFalse(s.IsVulnerable(2));

            Assert.IsTrue(s.MarkDestroyed(0), "the vulnerable crystal fell: the cursor moved");
            Assert.AreEqual(1, s.Current);
            Assert.IsFalse(s.IsVulnerable(0), "a destroyed crystal is no longer the vulnerable one");

            Assert.IsTrue(s.MarkDestroyed(1));
            Assert.AreEqual(2, s.Current);
            Assert.AreEqual("C", s.CurrentName);

            Assert.IsTrue(s.MarkDestroyed(2));
            Assert.AreEqual(-1, s.Current, "none left");
            Assert.IsNull(s.CurrentName);
            Assert.IsFalse(s.IsVulnerable(-1), "-1 is never vulnerable");
        }

        [TestMethod]
        public void Sequence_RepeatsAndStrangers_ChangeNothing_AndOutOfOrderIsSkippedLater()
        {
            var s = new CrystalSequence(new[] { "A", "B", "C" });

            Assert.IsFalse(s.MarkDestroyed(-1));
            Assert.IsFalse(s.MarkDestroyed(3));
            Assert.AreEqual(0, s.Current);

            Assert.IsFalse(s.MarkDestroyed(2), "a later crystal falling does not move the cursor");
            Assert.AreEqual(0, s.Current);
            Assert.IsFalse(s.MarkDestroyed(2), "a repeat");

            Assert.IsTrue(s.MarkDestroyed(0));
            Assert.AreEqual(1, s.Current);
            Assert.IsTrue(s.MarkDestroyed(1));
            Assert.AreEqual(-1, s.Current, "crystal 2 had already fallen, so the cursor skips it");
        }

        // ---------------- the plan ----------------

        /// <summary>Revert: dropping the dial check, or the count-2 floor, fails one of these.</summary>
        [TestMethod]
        public void Plan_BuildsASequence_OnlyWhenTheDialIsOn_AndTwoOrMoreCrystalsPlay()
        {
            Assert.IsNotNull(Plan(count: 0).Sequence, "default count (3), dial on");
            Assert.IsNotNull(Plan(count: 2).Sequence);
            Assert.IsNull(Plan(count: 1).Sequence, "one crystal has no order to keep");
            Assert.IsNull(Plan(count: 0, sequential: false).Sequence, "dial off: any-order play, exactly as before");
            Assert.IsNull(Plan(count: 2, sequential: false).Sequence);
        }

        /// <summary>Revert: ordering the sequence by anything but the site list order, or not taking the first N, fails.</summary>
        [TestMethod]
        public void Plan_SequenceFollowsTheSiteListOrder_FirstNSites()
        {
            CollectionAssert.AreEqual(new[] { "Great Hall", "West Cavern", "Pit Hall" }, Names(Plan(count: 0).Sequence));
            CollectionAssert.AreEqual(new[] { "Great Hall", "West Cavern" }, Names(Plan(count: 2).Sequence), "count 2 uses the first two");
            CollectionAssert.AreEqual(new[] { "Great Hall" }, Plan(count: 1, sequential: true).Crystals.Select(c => c.Site.Name).ToArray(), "count 1 uses the first one");
            Assert.AreEqual(0, Plan(count: 2).Sequence.Current);
        }

        private static string[] Names(CrystalSequence s) => Enumerable.Range(0, s.Count).Select(s.NameOf).ToArray();

        // ---------------- the gate ----------------

        private static readonly DateTime Now = new DateTime(2026, 10, 7, 0, 0, 0, DateTimeKind.Utc);

        private static PvpMatch Match(CrystalSequence sequence)
        {
            var m = new PvpMatch(Guid.NewGuid(), BattlegroundModes.AttackDefendModeKey, new List<PvpTeam>
            {
                new PvpTeam(0, new List<PvpParticipant> { new PvpParticipant(1, 1500) }),
                new PvpTeam(1, new List<PvpParticipant> { new PvpParticipant(2, 1500) }),
            }, Now);

            m.CrystalSequence = sequence;
            return m;
        }

        private static PvpPlayerBinding Bind(PvpMatch m, int team, bool respawning = false) =>
            new PvpPlayerBinding(m, team, PvpMatchState.Live, true, true, true, true, true, respawning: respawning);

        private static ObjectiveGateReason Gate(PvpMatch m, int crystalIndex, int team = 0, bool harmful = true) =>
            BattlegroundObjectiveGate.Evaluate(new BattlegroundObjectiveTag(m.MatchId, crystalIndex, DefendingTeam: 1), Bind(m, team), harmful);

        /// <summary>Revert: removing the sealed branch from the gate makes crystals 1 and 2 Allowed, failing the first two asserts.</summary>
        [TestMethod]
        public void Gate_ALockedCrystalRefusesDamage_TheCurrentOneTakesIt()
        {
            var m = Match(Plan().Sequence);

            Assert.AreEqual(ObjectiveGateReason.Allowed, Gate(m, 0), "the current crystal takes damage");
            Assert.AreEqual(ObjectiveGateReason.CrystalSealed, Gate(m, 1), "the second is sealed");
            Assert.AreEqual(ObjectiveGateReason.CrystalSealed, Gate(m, 2), "and so is the third");
            Assert.IsFalse(BattlegroundObjectiveGate.IsAllowed(Gate(m, 2)));
        }

        /// <summary>Revert: a gate that never reads the cursor again (a cached result) leaves 1 sealed after 0 falls.</summary>
        [TestMethod]
        public void Gate_DestroyingTheCurrentCrystal_UnlocksTheNext()
        {
            var plan = Plan();
            var m = Match(plan.Sequence);

            Assert.AreEqual(ObjectiveGateReason.CrystalSealed, Gate(m, 1));

            var ctx = new FakeObjectiveContext();
            var tick = new AttackDefendTickHandler(Dials(), plan);
            tick.OnLive(ctx);
            Assert.IsTrue(tick.OnCrystalDestroyed(ctx, 0));

            Assert.AreEqual(ObjectiveGateReason.Allowed, Gate(m, 1), "crystal 1 is now the current one");
            Assert.AreEqual(ObjectiveGateReason.CrystalSealed, Gate(m, 2), "crystal 2 is still sealed");

            Assert.IsTrue(tick.OnCrystalDestroyed(ctx, 1));
            Assert.AreEqual(ObjectiveGateReason.Allowed, Gate(m, 2));
        }

        /// <summary>Revert: treating a missing sequence as "locked" (or building one regardless of the dial) fails.</summary>
        [TestMethod]
        public void Gate_SequentialOff_IsAnyOrderPlay()
        {
            var m = Match(Plan(sequential: false).Sequence);

            Assert.IsNull(m.CrystalSequence);

            for (var i = 0; i < 3; i++)
                Assert.AreEqual(ObjectiveGateReason.Allowed, Gate(m, i), $"crystal {i}");
        }

        /// <summary>Count 2: the third site is not in the match, so only two indices exist and the order is Great Hall then West Cavern.</summary>
        [TestMethod]
        public void Gate_CountTwo_UsesTheFirstTwoInOrder()
        {
            var plan = Plan(count: 2);
            var m = Match(plan.Sequence);

            Assert.AreEqual(ObjectiveGateReason.Allowed, Gate(m, 0));
            Assert.AreEqual(ObjectiveGateReason.CrystalSealed, Gate(m, 1));

            plan.Sequence.MarkDestroyed(0);

            Assert.AreEqual(ObjectiveGateReason.Allowed, Gate(m, 1));
        }

        /// <summary>Count 1: no sequence, the lone crystal is always hittable.</summary>
        [TestMethod]
        public void Gate_CountOne_TheLoneCrystalIsAlwaysHittable()
        {
            var m = Match(Plan(count: 1).Sequence);

            Assert.AreEqual(ObjectiveGateReason.Allowed, Gate(m, 0));
        }

        /// <summary>
        /// The earlier refusals still win over the lock, so the line a player reads is the one that is actually true for them (a defender
        /// hitting a sealed crystal is told it is their own crystal, a heal is NotHarmful).
        /// </summary>
        [TestMethod]
        public void Gate_EarlierRefusalsStillComeFirst()
        {
            var m = Match(Plan().Sequence);

            Assert.AreEqual(ObjectiveGateReason.OwnTeam, Gate(m, 1, team: 1));
            Assert.AreEqual(ObjectiveGateReason.NotHarmful, Gate(m, 1, harmful: false));
            Assert.AreEqual(ObjectiveGateReason.ActorRespawning,
                BattlegroundObjectiveGate.Evaluate(new BattlegroundObjectiveTag(m.MatchId, 1, 1), Bind(m, 0, respawning: true), harmful: true));
        }

        // ---------------- the tick handler ----------------

        /// <summary>Revert: deleting the unlock announcement fails the second line.</summary>
        [TestMethod]
        public void Handler_Destroyed_SendsTheDestroyedLine_ThenTheUnlockBroadcast()
        {
            var (h, ctx, _) = Live();

            Assert.IsTrue(h.OnCrystalDestroyed(ctx, 0));

            CollectionAssert.AreEqual(new[]
            {
                "[Battleground] The Great Hall crystal (upper level, south-east of the octagon) has been destroyed! 2 of 3 remain.",
                "[Battleground] The West Cavern crystal (lower level, south from the octagon past the Defender room, then down the stairs) is now vulnerable!",
            }, ctx.Announcements);
            Assert.AreEqual(0, ctx.TeamAnnouncements.Count, "both go to everyone");

            Assert.IsTrue(h.OnCrystalDestroyed(ctx, 1));

            Assert.AreEqual("[Battleground] The Pit Hall crystal (bottom of the spiral stair, south-east of the West Cavern) is now vulnerable!", ctx.Announcements[^1]);
        }

        [TestMethod]
        public void Handler_TheLastCrystal_SendsNoUnlockLine()
        {
            var (h, ctx, _) = Live();

            h.OnCrystalDestroyed(ctx, 0);
            h.OnCrystalDestroyed(ctx, 1);
            ctx.Announcements.Clear();
            h.OnCrystalDestroyed(ctx, 2);

            Assert.AreEqual(1, ctx.Announcements.Count, "only the destroyed line");
            Assert.IsTrue(ctx.Announcements[0].Contains("has been destroyed! 0 of 3 remain."));
        }

        /// <summary>A duplicate report neither advances the cursor nor repeats the unlock line.</summary>
        [TestMethod]
        public void Handler_ADuplicateDestroyed_DoesNotUnlockTwice()
        {
            var (h, ctx, plan) = Live();

            h.OnCrystalDestroyed(ctx, 0);
            Assert.IsFalse(h.OnCrystalDestroyed(ctx, 0));

            Assert.AreEqual(2, ctx.Announcements.Count);
            Assert.AreEqual(1, plan.Sequence.Current);
        }

        /// <summary>Sequential off: the destroyed line only, no unlock line, the old behaviour.</summary>
        [TestMethod]
        public void Handler_SequentialOff_SendsNoUnlockLine()
        {
            var (h, ctx, plan) = Live(sequential: false);

            h.OnCrystalDestroyed(ctx, 0);

            Assert.IsNull(plan.Sequence);
            Assert.AreEqual(1, ctx.Announcements.Count);
            Assert.IsFalse(ctx.Announcements.Any(a => a.Contains("is now vulnerable")));
        }

        /// <summary>Revert: removing the locked-crystal skip in AlertOnDamage makes crystal 1's damage alert the defenders.</summary>
        [TestMethod]
        public void Handler_ALockedCrystal_SendsNoUnderAttackAlert()
        {
            var (h, ctx, _) = Live();

            ctx.SetCrystal(0, Max, Max);
            ctx.SetCrystal(1, Max / 2, Max);
            ctx.SetCrystal(2, Max, Max);

            h.OnTick(ctx, Start.AddSeconds(5));

            Assert.AreEqual(0, ctx.TeamAnnouncements.Count, "a sealed crystal's sampled damage (it cannot be real) raises no alert");
        }

        /// <summary>Control for the test above: the SAME damage on the vulnerable crystal does alert, and on any crystal with the dial off.</summary>
        [TestMethod]
        public void Handler_TheCurrentCrystal_AndAnyCrystalInAnyOrderPlay_StillAlert()
        {
            var (h, ctx, _) = Live();
            ctx.SetCrystal(0, Max / 2, Max);
            ctx.SetCrystal(1, Max, Max);
            ctx.SetCrystal(2, Max, Max);
            h.OnTick(ctx, Start.AddSeconds(5));

            Assert.AreEqual(1, ctx.TeamAnnouncements.Count, "the vulnerable crystal alerts");
            Assert.AreEqual(CrystalWinCondition.DefenderTeam, ctx.TeamAnnouncements[0].Team);

            var (h2, ctx2, _) = Live(sequential: false);
            ctx2.SetCrystal(0, Max, Max);
            ctx2.SetCrystal(1, Max / 2, Max);
            ctx2.SetCrystal(2, Max, Max);
            h2.OnTick(ctx2, Start.AddSeconds(5));

            Assert.AreEqual(1, ctx2.TeamAnnouncements.Count, "any-order play alerts on crystal 1 as before");
        }

        // ---------------- role and status lines ----------------

        private const string OrderedList =
            "in order: 1. Great Hall (upper level, south-east of the octagon), 2. West Cavern (lower level, south from the octagon past the Defender room, then down the stairs), 3. Pit Hall (bottom of the spiral stair, south-east of the West Cavern)";

        [TestMethod]
        public void RoleLines_SequentialMode_ListTheCrystalsInOrder()
        {
            var (h, _, _) = Live();

            Assert.AreEqual("[Battleground] You are an Attacker. Destroy the Warding Crystals " + OrderedList + " before time runs out!", h.RoleLine(CrystalWinCondition.AttackerTeam));
            Assert.AreEqual("[Battleground] You are a Defender. Protect the Warding Crystals " + OrderedList + " until time runs out!", h.RoleLine(CrystalWinCondition.DefenderTeam));
            Assert.IsNull(h.RoleLine(2));
        }

        [TestMethod]
        public void RoleLines_CountTwo_ListTheFirstTwoOnly()
        {
            var (h, _, _) = Live(count: 2);

            Assert.AreEqual(
                "[Battleground] You are an Attacker. Destroy the Warding Crystals in order: 1. Great Hall (upper level, south-east of the octagon), 2. West Cavern (lower level, south from the octagon past the Defender room, then down the stairs) before time runs out!",
                h.RoleLine(CrystalWinCondition.AttackerTeam));
        }

        /// <summary>Sequential off, and a single crystal: the original lines, unchanged.</summary>
        [TestMethod]
        public void RoleLines_AnyOrderAndSingleCrystal_AreTheOriginalLines()
        {
            var (off, _, _) = Live(sequential: false);

            StringAssert.StartsWith(off.RoleLine(CrystalWinCondition.AttackerTeam), "[Battleground] You are an Attacker. Destroy all 3 Warding Crystals before time runs out! Crystals: ");
            StringAssert.StartsWith(off.RoleLine(CrystalWinCondition.DefenderTeam), "[Battleground] You are a Defender. Protect the Warding Crystals until time runs out! Crystals: ");

            var (one, _, _) = Live(count: 1);

            StringAssert.StartsWith(one.RoleLine(CrystalWinCondition.AttackerTeam), "[Battleground] You are an Attacker. Destroy the Warding Crystal before time runs out!");
        }

        /// <summary>Revert: dropping the Vulnerable suffix fails; the name follows the cursor and the suffix is gone once none is left.</summary>
        [TestMethod]
        public void StatusLine_ShowsTheVulnerableCrystal_WhileSequentialModeIsOn()
        {
            var (h, ctx, _) = Live();
            var scores = ctx.ScoreBoard;

            Assert.AreEqual(" Crystals destroyed: 0 of 3. Vulnerable: Great Hall.", h.StatusLine(scores, 3));

            h.OnCrystalDestroyed(ctx, 0);
            Assert.AreEqual(" Crystals destroyed: 1 of 3. Vulnerable: West Cavern.", h.StatusLine(scores, 3));

            h.OnCrystalDestroyed(ctx, 1);
            h.OnCrystalDestroyed(ctx, 2);
            Assert.AreEqual(" Crystals destroyed: 3 of 3.", h.StatusLine(scores, 3), "nothing left to name");
        }

        [TestMethod]
        public void StatusLine_SequentialOff_HasNoVulnerableSuffix()
        {
            var (h, ctx, _) = Live(sequential: false);

            Assert.AreEqual(" Crystals destroyed: 0 of 3.", h.StatusLine(ctx.ScoreBoard, 3));
        }

        // ---------------- review fixes ----------------

        /// <summary>CR-F3: a crystal destroyed out of order is remembered, so the cursor skips it and the unlock names the right one.</summary>
        [TestMethod]
        public void Handler_OutOfOrderDestroy_ThenTheCurrentOne_UnlocksTheWestCavern()
        {
            var (h, ctx, plan) = Live();

            Assert.IsTrue(h.OnCrystalDestroyed(ctx, 2));
            Assert.IsFalse(ctx.Announcements.Any(a => a.Contains("is now vulnerable")), "the third falling moves nothing");
            Assert.AreEqual(0, plan.Sequence.Current);

            Assert.IsTrue(h.OnCrystalDestroyed(ctx, 0));

            Assert.AreEqual(1, plan.Sequence.Current);
            Assert.AreEqual("[Battleground] The West Cavern crystal (lower level, south from the octagon past the Defender room, then down the stairs) is now vulnerable!", ctx.Announcements[^1]);
        }

        /// <summary>CR-F3: a sequential timeout with one of three down says the defenders held two.</summary>
        [TestMethod]
        public void Handler_SequentialTimeout_WithOneDown_SaysHeldTwoOfThree()
        {
            var (h, ctx, _) = Live();
            h.OnCrystalDestroyed(ctx, 0);
            var defenders = new MatchOutcome(new HashSet<int> { 1 }, new[] { 2, 1 }, false, true, EndReason.Timeout);

            Assert.AreEqual("[Battleground] Time is up! The Defenders held 2 of 3 crystals.", h.ResultLine(defenders, ctx.ScoreBoard));
        }

        /// <summary>CR-F2: when the cursor cannot name two different crystals, the plain refusal is sent instead of a wrong sentence.</summary>
        [TestMethod]
        public void SealedLine_FallsBackToThePlainRefusal_WhenTheNamesMatchOrNothingIsLeft()
        {
            var same = new CrystalSequence(new[] { "Twin", "Twin" });

            Assert.AreEqual(BattlegroundText.RefusalCrystal, BattlegroundText.CrystalRefusal(ObjectiveGateReason.CrystalSealed, same, 1), "sealed and current share a name");

            var finished = new CrystalSequence(new[] { "A", "B" });
            finished.MarkDestroyed(0);
            finished.MarkDestroyed(1);

            Assert.AreEqual(BattlegroundText.RefusalCrystal, BattlegroundText.CrystalRefusal(ObjectiveGateReason.CrystalSealed, finished, 1), "the cursor moved to none between the gate and the line");
        }

        /// <summary>
        /// CR-F1: the silent check refuses a sealed crystal and does NOT touch the throttle; the real gate does (positive control), so a
        /// probe scan cannot burn the window or message a crystal the player never aimed at.
        /// </summary>
        [TestMethod]
        public void SilentCheck_RefusesTheSealedCrystal_AndAcquiresNoThrottle()
        {
            var m = Match(Plan().Sequence);
            var attacker = Seeded<Player>(PlayerKillerStatus.PKLite);
            attacker.SetPvpBindingForTests(Bind(m, CrystalWinCondition.AttackerTeam));
            Assert.IsTrue(attacker.IsBattlegroundObjectiveRefused(Crystal(m, 1)), "sealed: skipped by the probe");
            Assert.IsTrue(attacker.IsBattlegroundObjectiveRefused(Crystal(m, 2)));
            Assert.IsFalse(attacker.IsBattlegroundObjectiveRefused(Crystal(m, 0)), "the current crystal is not refused");
            Assert.IsFalse(attacker.IsBattlegroundObjectiveRefused(Seeded<Creature>(PlayerKillerStatus.NPK)), "a creature that is no objective is left to the normal rules");
            Assert.IsFalse(attacker.PvpCrystalRefusalThrottleStarted, "the silent check never touched the throttle");

            Assert.IsNotNull(attacker.CheckPKStatusVsTarget(Crystal(m, 1), null));
            Assert.IsTrue(attacker.PvpCrystalRefusalThrottleStarted, "control: the real gate does acquire it, so the false above means something");
        }

        /// <summary>The defenders' own crystal is refused silently too (no "own team's crystal" line from a scan).</summary>
        [TestMethod]
        public void SilentCheck_AlsoRefusesADefendersOwnCrystal()
        {
            var m = Match(Plan().Sequence);
            var defender = Seeded<Player>(PlayerKillerStatus.PKLite);
            defender.SetPvpBindingForTests(Bind(m, CrystalWinCondition.DefenderTeam));

            Assert.IsTrue(defender.IsBattlegroundObjectiveRefused(Crystal(m, 0)));
            Assert.IsTrue(Player.IsObjectiveProbeRefused(defender, Crystal(m, 0)));
            Assert.IsFalse(Player.IsObjectiveProbeRefused(Seeded<Creature>(PlayerKillerStatus.NPK), Crystal(m, 0)), "a non-player caster is never refused here");
        }

        /// <summary>The probe sites call the silent check before CheckPKStatusVsTarget (a source scan; reverting any one site fails it).</summary>
        [TestMethod]
        public void EveryProbeSite_CallsTheSilentCheckFirst()
        {
            var sites = new[]
            {
                ("Source/ACE.Server/WorldObjects/Creature_Melee.cs", "player.IsBattlegroundObjectiveRefused(creature)", "player.CheckPKStatusVsTarget(creature, null)"),
                ("Source/ACE.Server/WorldObjects/Creature_Missile.cs", "player.IsBattlegroundObjectiveRefused(creature)", "player.CheckPKStatusVsTarget(creature, null)"),
                ("Source/ACE.Server/ClassAbilities/Abilities/SpellAoeAbility.cs", "Player.IsObjectiveProbeRefused(caster, creature)", "caster.CheckPKStatusVsTarget(creature, null)"),
                ("Source/ACE.Server/ClassAbilities/Abilities/CascadeAbility.cs", "Player.IsObjectiveProbeRefused(caster, creature)", "caster.CheckPKStatusVsTarget(creature, null)"),
                ("Source/ACE.Server/ClassAbilities/Abilities/NetherBloomAbility.cs", "Player.IsObjectiveProbeRefused(killer, creature)", "killer.CheckPKStatusVsTarget(creature, null)"),
                ("Source/ACE.Server/WorldObjects/WorldObject_Magic.cs", "Player.IsObjectiveProbeRefused(caster, creature)", "caster.CheckPKStatusVsTarget(creature, null)"),
            };

            foreach (var (file, silent, loud) in sites)
            {
                var text = PooledLootSourceText.Read(file);
                var s = text.IndexOf(silent, StringComparison.Ordinal);
                var l = text.IndexOf(loud, StringComparison.Ordinal);

                Assert.IsTrue(s >= 0 && l >= 0 && s < l, $"{file}: the silent check must come before the loud one");
            }
        }

        /// <summary>PQ-F6: the periodic status line and the respawn line name the vulnerable crystal in sequential mode only.</summary>
        [TestMethod]
        public void PeriodicStatus_AndRespawnLine_NameTheVulnerableCrystal()
        {
            var (h, ctx, _) = Live();
            h.OnTick(ctx, Start.AddSeconds(60));

            Assert.IsTrue(ctx.Announcements.Single().EndsWith(" Vulnerable: Great Hall."), ctx.Announcements.Single());
            Assert.AreEqual("[Battleground] Vulnerable crystal: Great Hall (upper level, south-east of the octagon).", h.VulnerableLine());

            h.OnCrystalDestroyed(ctx, 0);
            StringAssert.StartsWith(h.VulnerableLine(), "[Battleground] Vulnerable crystal: West Cavern (lower level");

            var (off, ctxOff, _) = Live(sequential: false);
            off.OnTick(ctxOff, Start.AddSeconds(60));

            Assert.IsFalse(ctxOff.Announcements.Single().Contains("Vulnerable"));
            Assert.IsNull(off.VulnerableLine());
        }

        // ---------------- text ----------------

        [TestMethod]
        public void SealedLine_IsTheOwnersText_AndFallsBackWhenItCannotNameBoth()
        {
            var s = Plan().Sequence;

            Assert.AreEqual("[Battleground] The West Cavern crystal is sealed. Destroy the Great Hall crystal first.",
                BattlegroundText.CrystalRefusal(ObjectiveGateReason.CrystalSealed, s, 1));
            Assert.AreEqual("[Battleground] The Pit Hall crystal is sealed. Destroy the Great Hall crystal first.",
                BattlegroundText.CrystalRefusal(ObjectiveGateReason.CrystalSealed, s, 2));
            Assert.AreEqual(BattlegroundText.RefusalCrystal, BattlegroundText.CrystalRefusal(ObjectiveGateReason.CrystalSealed, null, 1));
            Assert.AreEqual(BattlegroundText.RefusalOwnCrystal, BattlegroundText.CrystalRefusal(ObjectiveGateReason.OwnTeam, s, 1), "other reasons are unchanged");
        }

        // ---------------- the per-attacker throttle ----------------

        /// <summary>Revert: a throttle shared between players (a static, or one window for everyone) fails the independence asserts.</summary>
        [TestMethod]
        public void Throttle_IsPerAttacker_ThreeSecondsEach()
        {
            var a = new CrystalNoticeThrottle();
            var b = new CrystalNoticeThrottle();
            var interval = PvpArenaHookSettings.FriendlyFireNoticeIntervalSeconds;
            var t0 = Start;

            Assert.AreEqual(3.0, interval, "the window is the three seconds the owner asked for");
            Assert.IsTrue(a.TryAcquire(t0, interval), "the first refusal sends a line");
            Assert.IsFalse(a.TryAcquire(t0.AddSeconds(1), interval), "a second hit inside the window is silent");
            Assert.IsFalse(a.TryAcquire(t0.AddSeconds(2.9), interval));

            Assert.IsTrue(b.TryAcquire(t0.AddSeconds(1), interval), "another attacker's first refusal is not throttled by the first's");
            Assert.IsFalse(b.TryAcquire(t0.AddSeconds(2), interval));

            Assert.IsTrue(a.TryAcquire(t0.AddSeconds(3), interval), "the window has passed for the first attacker");
            Assert.IsFalse(b.TryAcquire(t0.AddSeconds(3.5), interval), "but not for the second, whose window started at 1");
            Assert.IsTrue(b.TryAcquire(t0.AddSeconds(4), interval));
        }

        /// <summary>
        /// The Player owns the throttle per instance and the refusal sender uses it (a static field, or a sender that bypasses it, fails).
        /// </summary>
        [TestMethod]
        public void Player_HoldsItsOwnThrottle_AndTheRefusalSenderUsesIt()
        {
            var field = typeof(Player).GetField("pvpCrystalRefusalThrottle", BindingFlags.NonPublic | BindingFlags.Instance);

            Assert.IsNotNull(field, "an INSTANCE field on Player (a static would be shared by every attacker)");
            Assert.AreEqual(typeof(CrystalNoticeThrottle), field.FieldType);

            var source = PooledLootSourceText.Read("Source/ACE.Server/WorldObjects/Player_BattlegroundObjective.cs");

            StringAssert.Contains(source, "(pvpCrystalRefusalThrottle ??= new CrystalNoticeThrottle()).TryAcquire(DateTime.UtcNow, PvpArenaHookSettings.FriendlyFireNoticeIntervalSeconds)");
            StringAssert.Contains(source, "BattlegroundText.CrystalRefusal(reason, pvpBinding?.Match?.CrystalSequence, crystalIndex)");
        }

        // ---------------- through the real player gate ----------------

        private static void SetInherited(WorldObject wo, string name, object value)
        {
            var field = typeof(WorldObject).GetField(name, BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance);
            Assert.IsNotNull(field, $"WorldObject.{name} was not found by reflection - has it been renamed?");
            field.SetValue(wo, value);
        }

        private static T Seeded<T>(PlayerKillerStatus status) where T : Creature
        {
            var wo = (T)RuntimeHelpers.GetUninitializedObject(typeof(T));

            SetInherited(wo, "<Biota>k__BackingField", new ACE.Entity.Models.Biota());
            SetInherited(wo, "BiotaDatabaseLock", new ReaderWriterLockSlim());
            SetInherited(wo, "positionCache", new Dictionary<PositionType, Position>());
            SetInherited(wo, "ephemeralPositions", new Dictionary<PositionType, Position>());

            wo.PlayerKillerStatus = status;
            wo.Location = new Position { LandblockId = new LandblockId(0x00900100), PositionX = 50f, PositionY = 50f, RotationW = 1f };

            return wo;
        }

        private static Creature Crystal(PvpMatch m, int index)
        {
            var crystal = Seeded<Creature>(PlayerKillerStatus.NPK);
            Assert.IsTrue(crystal.SetBattlegroundObjective(new BattlegroundObjectiveTag(m.MatchId, index, CrystalWinCondition.DefenderTeam)));
            return crystal;
        }

        /// <summary>Through Player.CheckPKStatusVsTarget: the attacker may hit the current crystal and is refused on a sealed one.</summary>
        [TestMethod]
        public void RealPlayer_IsRefusedOnASealedCrystal_AndAllowedOnTheCurrentOne()
        {
            var m = Match(Plan().Sequence);
            var attacker = Seeded<Player>(PlayerKillerStatus.PKLite);
            attacker.SetPvpBindingForTests(Bind(m, CrystalWinCondition.AttackerTeam));

            Assert.IsNull(attacker.CheckPKStatusVsTarget(Crystal(m, 0), null), "the current crystal takes the hit");

            var refused = attacker.CheckPKStatusVsTarget(Crystal(m, 1), null);
            Assert.IsNotNull(refused, "a sealed crystal refuses it");
            Assert.AreEqual(WeenieErrorWithString._IsAnInvalidTarget, refused[0]);

            m.CrystalSequence.MarkDestroyed(0);

            Assert.IsNull(attacker.CheckPKStatusVsTarget(Crystal(m, 1), null), "unlocked once the first has fallen");
            Assert.IsNotNull(attacker.CheckPKStatusVsTarget(Crystal(m, 2), null), "the third is still sealed");
        }

        // ---------------- the Abandoned Mines start gates ----------------

        private static BattlegroundSealPiece AttackerGate => Mines.StartGates.Single(g => g.CellId == 0x003C0227);

        private static BattlegroundSealPiece DefenderGate => Mines.StartGates.Single(g => g.CellId == 0x003C01D7);

        /// <summary>
        /// The doorway centres and widths are the ones `cells --openings` printed (0x003C0227 to 0x021F at (175, -20) and 0x003C01D7 to
        /// 0x01DE at (75, -80), both 3.33 m wide, normals along x). Each gate must be the 10 m barrier turned so its length runs along y,
        /// centred on the doorway, 0.2 m inside the room, and long enough to cover the doorway.
        /// </summary>
        [TestMethod]
        public void StartGates_OneAtEachSpawnRoomDoor_TurnedAndCoveringTheDoorway()
        {
            Assert.AreEqual(2, Mines.StartGates.Count);

            const float barrierLength = 10f;
            const float doorWidth = 3.33f;

            foreach (var (gate, doorX, doorY, insideSign) in new[] { (AttackerGate, 175f, -20f, 1f), (DefenderGate, 75f, -80f, -1f) })
            {
                Assert.AreEqual(1001088u, gate.Wcid, "the barrier weenie");
                Assert.AreEqual(0.707107f, gate.RotationW, 1e-6f, "turned 90 degrees: the 10 m length lies along y, across an x-normal doorway");
                Assert.AreEqual(0.707107f, gate.RotationZ, 1e-6f);
                Assert.AreEqual(-5.995f, gate.Z, 1e-6f, "on the floor like the other lower-level pieces");
                Assert.AreEqual(doorY, gate.Y, 1e-6f, "centred on the doorway");
                Assert.AreEqual(0.2f, Math.Abs(gate.X - doorX), 1e-4f, "0.2 m off the door plane");
                Assert.AreEqual(insideSign, Math.Sign(gate.X - doorX), "on the room's side of the door");
                Assert.IsTrue(barrierLength / 2 >= doorWidth / 2 + Math.Abs(gate.Y - doorY), "the piece reaches past both jambs");
            }
        }

        [TestMethod]
        public void StartGates_NoSpawnPointIsWithinThreeMetresOfAGate()
        {
            foreach (var gate in Mines.StartGates)
            {
                foreach (var spawn in Mines.TeamSpawns.SelectMany(t => t))
                {
                    var d = Math.Sqrt(Math.Pow(spawn.X - gate.X, 2) + Math.Pow(spawn.Y - gate.Y, 2));

                    Assert.IsTrue(d >= 3.0, $"{spawn.Label} is {d:F2} m from the gate at ({gate.X}, {gate.Y})");
                }
            }

            foreach (var pen in Mines.TeamPens)
                foreach (var gate in Mines.StartGates)
                    Assert.IsTrue(Math.Sqrt(Math.Pow(pen.X - gate.X, 2) + Math.Pow(pen.Y - gate.Y, 2)) > 3.0, $"{pen.Label} is clear of the gates");
        }

        /// <summary>Each team's gate stands in that team's own spawn-room cells, so it closes the room the team spawns in and no other.</summary>
        [TestMethod]
        public void StartGates_StandInTheirOwnTeamsSpawnRoom()
        {
            CollectionAssert.Contains(Mines.SpawnRoomCells[0].ToList(), AttackerGate.CellId, "the attacker gate is in an attacker room cell");
            CollectionAssert.Contains(Mines.SpawnRoomCells[1].ToList(), DefenderGate.CellId, "the defender gate is in a defender room cell");
        }

        /// <summary>The pen seals and the pit seal are unchanged: three pieces, in the same cells.</summary>
        [TestMethod]
        public void StartGates_LeaveThePenSealsUnchanged()
        {
            CollectionAssert.AreEqual(
                new uint[] { 0x003C0223, 0x003C01D4, 0x003C013F },
                Mines.Seals.Select(s => s.CellId).ToArray());
        }
    }
}
