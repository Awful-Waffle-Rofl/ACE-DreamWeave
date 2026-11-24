using System;
using System.IO;
using System.Numerics;
using System.Text.RegularExpressions;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Server.Entity;
using ACE.Server.Managers;
using ACE.Server.WorldObjects;

namespace ACE.Server.Tests
{
    /// <summary>
    /// The 2026-11-07 follow-teleport reliability pass and /telesummon: the retry cadence after a failed
    /// teleport (PetFollowRules.AfterTeleportAttempt), the warning rate limit, the /telesummon cooldown, its chat
    /// lines, and source pins for the wiring no test here can drive (a live Player and CombatPet are out of reach
    /// in this project - see SummonFollowAssistTests' scope note).
    ///
    /// PropertyManager: Settings_TeleSummonCooldown_Read seeds pet_telesummon_cooldown_seconds and restores it in
    /// a finally. Nothing else here reads PropertyManager.
    /// </summary>
    [TestClass]
    public class PetFollowTeleportRetryTests
    {
        private static readonly Vector3 PetAt = Vector3.Zero;
        private static readonly Vector3 OwnerAt = new Vector3(20.0f, 0.0f, 0.0f);

        // ---------------------------------------------------------------- retry cadence

        /// <summary>
        /// The bug this fixes: a stuck pet whose teleport FAILS must be retried every TeleportRetryIntervalSeconds,
        /// not after a fresh stuck window. The tracker reports stuck at t=3, the attempt fails, and on the very next
        /// sample after the retry interval the tracker still reports stuck - so the trigger fires again at t=4.
        /// </summary>
        [TestMethod]
        public void FailedTeleport_KeepsTheStuckTriggerArmed_AndRetriesAfterTheInterval()
        {
            var tracker = new PetFollowProgressTracker();
            var followStart = 0.0;

            tracker.Update(0.0, PetAt, OwnerAt, 3.0);
            Assert.IsTrue(tracker.Update(3.0, PetAt, OwnerAt, 3.0), "setup: stuck at 3 s");

            var next = PetFollowRules.AfterTeleportAttempt(false, 3.0, tracker, ref followStart);

            Assert.AreEqual(3.0 + PetFollowRules.TeleportRetryIntervalSeconds, next, 1e-9, "the next attempt waits only the retry interval");
            Assert.IsTrue(tracker.IsTracking, "a failed attempt must not reset the stuck window");
            Assert.AreEqual(0.0, followStart, "nor restart the max-time clock");

            Assert.IsTrue(tracker.Update(next, PetAt, OwnerAt, 3.0), "the stuck trigger is still firing when the retry comes due");
        }

        /// <summary>
        /// Control for the test above: the OLD behaviour (reset on every attempt) would have made the stuck
        /// trigger wait a whole new window. Shown by doing exactly that reset by hand - so the test above
        /// discriminates the fix rather than passing on any tracker.
        /// </summary>
        [TestMethod]
        public void Control_ResettingOnAFailedAttempt_WouldDelayTheRetryByAWholeWindow()
        {
            var tracker = new PetFollowProgressTracker();

            tracker.Update(0.0, PetAt, OwnerAt, 3.0);
            Assert.IsTrue(tracker.Update(3.0, PetAt, OwnerAt, 3.0));

            tracker.Reset();   // what TryTeleportToOwner did before 2026-11-07
            tracker.Update(3.0, PetAt, OwnerAt, 3.0);

            Assert.IsFalse(tracker.Update(4.0, PetAt, OwnerAt, 3.0), "with the reset the trigger is silent at the retry time");
            Assert.IsTrue(tracker.Update(6.0, PetAt, OwnerAt, 3.0), "and only fires a full window later");
        }

        [TestMethod]
        public void FailedTeleport_KeepsTheMaxTimeTriggerArmed()
        {
            var tracker = new PetFollowProgressTracker();
            var followStart = 10.0;

            Assert.IsTrue(PetFollowRules.FollowTimedOut(followStart, 25.0, 15.0), "setup: timed out");

            var next = PetFollowRules.AfterTeleportAttempt(false, 25.0, tracker, ref followStart);

            Assert.AreEqual(10.0, followStart, "the follow start is untouched by a failed attempt");
            Assert.IsTrue(PetFollowRules.FollowTimedOut(followStart, next, 15.0), "so the max-time trigger is still firing at the retry");
        }

        [TestMethod]
        public void SuccessfulTeleport_RestartsTheWindowAndTheClock()
        {
            var tracker = new PetFollowProgressTracker();
            var followStart = 10.0;

            tracker.Update(10.0, PetAt, OwnerAt, 3.0);
            Assert.IsTrue(tracker.Update(25.0, PetAt, OwnerAt, 3.0));

            var next = PetFollowRules.AfterTeleportAttempt(true, 25.0, tracker, ref followStart);

            Assert.AreEqual(26.0, next, 1e-9);
            Assert.IsFalse(tracker.IsTracking, "a placed pet starts a fresh stuck window");
            Assert.AreEqual(25.0, followStart, "and a fresh max-time clock");
            Assert.IsFalse(PetFollowRules.FollowTimedOut(followStart, next, 15.0));
        }

        // ---------------------------------------------------------------- warning rate limit

        [TestMethod]
        public void TeleportFailureWarning_IsRateLimited()
        {
            Assert.IsTrue(PetFollowRules.ShouldLogTeleportFailure(null, 5.0), "the first failure always logs");
            Assert.IsFalse(PetFollowRules.ShouldLogTeleportFailure(5.0, 6.0), "a retry a second later does not");
            Assert.IsFalse(PetFollowRules.ShouldLogTeleportFailure(5.0, 34.9));
            Assert.IsTrue(PetFollowRules.ShouldLogTeleportFailure(5.0, 35.0), "30 s later it logs again");
            Assert.AreEqual(30.0, PetFollowRules.TeleportFailureLogIntervalSeconds);
        }

        // ---------------------------------------------------------------- /telesummon cooldown

        [TestMethod]
        public void TeleSummonCooldown_CountsDownFromTheLastUse()
        {
            Assert.AreEqual(0.0, PetFollowRules.TeleSummonCooldownRemaining(null, 100.0, 10.0), "never used: ready");
            Assert.AreEqual(10.0, PetFollowRules.TeleSummonCooldownRemaining(100.0, 100.0, 10.0), 1e-9);
            Assert.AreEqual(2.5, PetFollowRules.TeleSummonCooldownRemaining(100.0, 107.5, 10.0), 1e-9);
            Assert.AreEqual(0.0, PetFollowRules.TeleSummonCooldownRemaining(100.0, 110.0, 10.0), "ready exactly at the end");
            Assert.AreEqual(0.0, PetFollowRules.TeleSummonCooldownRemaining(100.0, 500.0, 10.0));
        }

        [TestMethod]
        public void TeleSummonCooldown_ZeroOrNegativeDisablesIt()
        {
            Assert.AreEqual(0.0, PetFollowRules.TeleSummonCooldownRemaining(100.0, 100.0, 0.0));
            Assert.AreEqual(0.0, PetFollowRules.TeleSummonCooldownRemaining(100.0, 100.0, -5.0));
            Assert.AreEqual(0.0, PetFollowRules.TeleSummonCooldownRemaining(100.0, 100.0, double.NaN));
        }

        [TestMethod]
        public void Settings_TeleSummonCooldown_DefaultsAndRead()
        {
            Assert.AreEqual(10.0, PetFollowSettings.DefaultTeleSummonCooldownSeconds);
            Assert.AreEqual(PetFollowSettings.DefaultTeleSummonCooldownSeconds,
                DefaultPropertyManager.DefaultDoubleProperties[PetFollowSettings.TeleSummonCooldownSecondsKey].Item,
                "the compiled default and the PropertyManager default must agree");

            try
            {
                Assert.IsTrue(PropertyManager.ModifyDouble(PetFollowSettings.TeleSummonCooldownSecondsKey, 4.0, true));
                Assert.AreEqual(4.0, PetFollowSettings.ReadTeleSummonCooldownSeconds());
            }
            finally
            {
                PropertyManager.ModifyDouble(PetFollowSettings.TeleSummonCooldownSecondsKey,
                    DefaultPropertyManager.DefaultDoubleProperties[PetFollowSettings.TeleSummonCooldownSecondsKey].Item, true);
            }
        }

        // ---------------------------------------------------------------- /telesummon chat lines

        [TestMethod]
        public void TeleSummonLines_AreShortPlainAscii()
        {
            var longName = new string('A', 64) + "'s " + new string('B', 64);

            foreach (Pet.TeleSummonOutcome outcome in Enum.GetValues(typeof(Pet.TeleSummonOutcome)))
            {
                var line = Pet.FormatTeleSummonOutcome(longName, outcome);

                Assert.IsTrue(line.Length < 255, $"{outcome}: {line.Length} chars");
                Assert.IsTrue(Regex.IsMatch(line, @"^[\x20-\x7E]+$"), $"{outcome}: not plain ASCII: {line}");
                StringAssert.Contains(line, longName, $"{outcome}: names the pet");
            }

            Assert.AreEqual("Bob's Wisp returns to your side.", Pet.FormatTeleSummonOutcome("Bob's Wisp", Pet.TeleSummonOutcome.Teleported));
        }

        // ---------------------------------------------------------------- review fixes (pure)

        /// <summary>F1: a dropped target engaging the owner or the pet is not ignored; anything else is.</summary>
        [TestMethod]
        public void TeleSummonDroppedTarget_IgnoredOnlyWhenNotEngagingOurSide()
        {
            Assert.IsTrue(PetFollowRules.ShouldIgnoreTeleSummonDroppedTarget(false, false, false, false), "an unengaged chase target is ignored");

            Assert.IsFalse(PetFollowRules.ShouldIgnoreTeleSummonDroppedTarget(true, false, false, false), "attacking the owner");
            Assert.IsFalse(PetFollowRules.ShouldIgnoreTeleSummonDroppedTarget(false, true, false, false), "attacking the pet");
            Assert.IsFalse(PetFollowRules.ShouldIgnoreTeleSummonDroppedTarget(false, false, true, false), "retaliating against the owner");
            Assert.IsFalse(PetFollowRules.ShouldIgnoreTeleSummonDroppedTarget(false, false, false, true), "retaliating against the pet");
        }

        /// <summary>F3: a pet put back where it stood counts as moved only if an earlier attempt moved it.</summary>
        [TestMethod]
        public void RejectedLift_RestoredPetIsNotAMove_ParkedPetIs()
        {
            Assert.IsFalse(PetFollowRules.MovedAfterRejectedLift(false, true), "restored, nothing earlier moved it: no broadcast");
            Assert.IsTrue(PetFollowRules.MovedAfterRejectedLift(true, true), "restored, but the in-front attempt had moved it");
            Assert.IsTrue(PetFollowRules.MovedAfterRejectedLift(false, false), "not restored: it is somewhere new and must be synced");
            Assert.IsTrue(PetFollowRules.MovedAfterRejectedLift(true, false));
        }

        /// <summary>F2: the move-back accepts the original spot or its lift, and nothing farther.</summary>
        [TestMethod]
        public void MoveBack_AcceptsTheOriginalSpotOrItsLift_Only()
        {
            const uint cell = 0x00AE0149;
            var before = new Vector3(84.75f, -59.84f, -17.99f);

            Assert.IsTrue(PetFollowRules.AcceptMoveBack(cell, before, cell, before, true));
            Assert.IsTrue(PetFollowRules.AcceptMoveBack(cell, before, cell, before + new Vector3(0, 0, PetFollowRules.OwnerSpotBumpHeight), true), "the lifted retry");
            Assert.IsFalse(PetFollowRules.AcceptMoveBack(cell, before, cell, before + new Vector3(0.5f, 0, 0), true), "half a metre off is not back");
            Assert.IsFalse(PetFollowRules.AcceptMoveBack(cell, before, cell + 1, before, true), "another cell");
            Assert.IsFalse(PetFollowRules.AcceptMoveBack(cell, before, cell, before, false), "no physics cell");
        }

        /// <summary>
        /// F4: while the follow MoveTo cannot go out (the pet is not MoveReady), the stuck window is reset every
        /// tick so a 5 s pin or attack recovery is never counted as stuck under the 3 s default - but the
        /// max-time trigger, which does not use the tracker, still fires.
        /// </summary>
        [TestMethod]
        public void NotMoveReady_IsNeverStuck_ButStillTimesOut()
        {
            Assert.IsTrue(PetFollowRules.ShouldResetStuckWindow(false));
            Assert.IsFalse(PetFollowRules.ShouldResetStuckWindow(true));

            var tracker = new PetFollowProgressTracker();
            var stuckAt = -1.0;
            var timedOutAt = -1.0;

            // a pinned pet, ticked every 0.2 s for 20 s, never able to issue its MoveTo
            for (var i = 0; i <= 100; i++)
            {
                var t = i * 0.2;

                if (tracker.Update(t, PetAt, OwnerAt, 3.0) && stuckAt < 0)
                    stuckAt = t;

                if (PetFollowRules.FollowTimedOut(0.0, t, 15.0) && timedOutAt < 0)
                    timedOutAt = t;

                if (PetFollowRules.ShouldResetStuckWindow(false))
                    tracker.Reset();
            }

            Assert.AreEqual(-1.0, stuckAt, "a pet that may not move must never report stuck");
            Assert.AreEqual(15.0, timedOutAt, 0.1, "the max-time trigger still ends it");
        }

        /// <summary>Control for the test above: without the per-tick reset the same pinned pet is stuck at 3 s.</summary>
        [TestMethod]
        public void Control_WithoutTheReset_APinnedPetIsStuckAtThreeSeconds()
        {
            var tracker = new PetFollowProgressTracker();
            var stuckAt = -1.0;

            for (var i = 0; i <= 100 && stuckAt < 0; i++)
            {
                var t = i * 0.2;
                if (tracker.Update(t, PetAt, OwnerAt, 3.0))
                    stuckAt = t;
            }

            Assert.AreEqual(3.0, stuckAt, 0.01);
        }

        // ---------------------------------------------------------------- source pins

        /// <summary>F1 pin: the ignore in TeleportToOwnerOnCommand is gated on ShouldIgnoreTeleSummonDroppedTarget with all four engagement reads.</summary>
        [TestMethod]
        public void TeleSummonIgnore_IsGatedOnTheEngagementRule()
        {
            var code = Collapse(Source(FollowPath));
            var method = Region(code, "public override TeleSummonOutcome TeleportToOwnerOnCommand(Player owner)", "protected override void StopMovingForTeleport()");

            // the exact condition head, so a widened gate (`true || ...`, `||` instead of `&&`) fails here
            var gate = method.IndexOf("if (AttackTarget is Creature target && PetFollowRules.ShouldIgnoreTeleSummonDroppedTarget(", StringComparison.Ordinal);
            var ignore = method.IndexOf("unreachableTargets.Ignore(", StringComparison.Ordinal);

            Assert.IsTrue(gate >= 0 && ignore > gate, "the ignore must be inside the engagement gate");
            var args = method.Substring(gate, ignore - gate);
            StringAssert.Contains(args, "ReferenceEquals(target.AttackTarget, owner)");
            StringAssert.Contains(args, "ReferenceEquals(target.AttackTarget, this)");
            StringAssert.Contains(args, "target.HasRetaliateTarget(owner)");
            StringAssert.Contains(args, "target.HasRetaliateTarget(this)");
        }

        /// <summary>F5 pin: the target drop and ignore only follow a successful teleport.</summary>
        [TestMethod]
        public void TeleSummonTargetDrop_IsGatedOnSuccess()
        {
            var code = Collapse(Source(FollowPath));
            var method = Region(code, "public override TeleSummonOutcome TeleportToOwnerOnCommand(Player owner)", "protected override void StopMovingForTeleport()");

            var gate = method.IndexOf("if (outcome != TeleSummonOutcome.Teleported) return outcome;", StringComparison.Ordinal);
            var ignore = method.IndexOf("unreachableTargets.Ignore(", StringComparison.Ordinal);
            var drop = method.IndexOf("DropAttackTarget();", StringComparison.Ordinal);

            Assert.IsTrue(gate >= 0, "a failed teleport must return before touching the target");
            Assert.IsTrue(ignore > gate && drop > gate, "the ignore and the drop come after the success gate");
        }

        /// <summary>F5 pins: HandleTeleSummon's refusals, cooldown and cooldown start.</summary>
        [TestMethod]
        public void HandleTeleSummon_RefusesAndRunsTheCooldownInOrder()
        {
            var code = Collapse(Source(TeleSummonPath));

            var dead = code.IndexOf("if (IsDead) { Msg(\"You cannot call your summons while dead.\"); return; }", StringComparison.Ordinal);
            var teleporting = code.IndexOf("if (Teleporting) { Msg(\"You cannot call your summons while teleporting.\"); return; }", StringComparison.Ordinal);
            var cooldown = code.IndexOf("if (remaining > 0.0) {", StringComparison.Ordinal);
            var cooldownReturn = code.IndexOf("return; }", cooldown < 0 ? 0 : cooldown, StringComparison.Ordinal);
            var enqueue = code.IndexOf("pet.EnqueueAction(", StringComparison.Ordinal);
            var stamp = code.IndexOf("if (sent > 0) lastTeleSummonTime = now;", StringComparison.Ordinal);

            Assert.IsTrue(dead >= 0, "a dead player is refused");
            Assert.IsTrue(teleporting >= 0, "a teleporting player is refused");
            Assert.IsTrue(dead < enqueue && teleporting < enqueue, "both refusals come before any pet is sent");
            Assert.IsTrue(cooldown >= 0 && cooldownReturn > cooldown && cooldownReturn < enqueue, "the cooldown refuses before the enqueue");
            StringAssert.Contains(code, "PetFollowRules.TeleSummonCooldownRemaining(lastTeleSummonTime, now, PetFollowSettings.ReadTeleSummonCooldownSeconds())");
            Assert.IsTrue(stamp > enqueue, "the cooldown starts only after a pet was sent");
            Assert.AreEqual(1, Regex.Matches(code, @"lastTeleSummonTime = ").Count, "and nowhere else");
        }

        /// <summary>F4 pin: both follow-MoveTo issue sites reset the stuck window when the MoveTo did not go out.</summary>
        [TestMethod]
        public void FollowIssueSites_ResetTheStuckWindowWhenNotIssued()
        {
            var code = Collapse(Source(FollowPath));
            var tick = Region(code, "public virtual void TickFollowOwner()", "private bool CanFollowOwner(Player owner)");

            StringAssert.Contains(tick, "if (PetFollowRules.ShouldResetStuckWindow(IssueFollowMove(owner))) followProgress.Reset();",
                "the window-opening issue must reset the stuck window when it fails");
            StringAssert.Contains(tick, "if (!IsMoving && now >= nextFollowIssueTime && PetFollowRules.ShouldResetStuckWindow(IssueFollowMove(owner))) followProgress.Reset();",
                "and so must the re-issue");
            Assert.AreEqual(2, Regex.Matches(tick, @"IssueFollowMove\(owner\)").Count, "no other issue site in the tick");
        }

        private const string FollowPath = "Source/ACE.Server/WorldObjects/CombatPet_Follow.cs";
        private const string TeleSummonPath = "Source/ACE.Server/WorldObjects/Player_TeleSummon.cs";

        private static string Collapse(string code) => Regex.Replace(code, @"\s+", " ");

        private static string Region(string code, string start, string end)
        {
            var s = code.IndexOf(start, StringComparison.Ordinal);
            Assert.IsTrue(s >= 0, $"source no longer contains: {start}");
            var e = code.IndexOf(end, s, StringComparison.Ordinal);
            Assert.IsTrue(e > s, $"source no longer contains, after it: {end}");
            return code.Substring(s, e - s);
        }

        /// <summary>
        /// The command never moves a pet from the command thread: every TeleportToOwnerOnCommand call in
        /// Player_TeleSummon.cs sits inside an action enqueued on that pet.
        /// </summary>
        [TestMethod]
        public void TeleSummon_MovesEachPetOnlyFromAnActionEnqueuedOnThatPet()
        {
            var code = Source("Source/ACE.Server/WorldObjects/Player_TeleSummon.cs");

            var enqueue = code.IndexOf("pet.EnqueueAction(new ActionEventDelegate(", StringComparison.Ordinal);
            var call = code.IndexOf("TeleportToOwnerOnCommand(", StringComparison.Ordinal);

            Assert.IsTrue(enqueue >= 0, "the pet move must be enqueued on the pet");
            Assert.IsTrue(call > enqueue, "and TeleportToOwnerOnCommand must be called inside that action");
            Assert.AreEqual(-1, code.IndexOf("TeleportToOwnerOnCommand(", call + 1, StringComparison.Ordinal), "and nowhere else");
            Assert.IsFalse(code.Contains("PlaceBesideOwner(") || code.Contains("PhysicsTeleport("), "the command itself must never touch pet physics");
        }

        /// <summary>
        /// The action re-checks reach on the pet's thread before it moves anything: TeleportToOwnerOnCommand runs
        /// CheckTeleSummonReach (which holds the CanTeleportToOwner / SharesTickThread rule) before PlaceBesideOwner.
        /// </summary>
        [TestMethod]
        public void TeleportToOwnerOnCommand_RechecksReachBeforePlacing()
        {
            var code = Source("Source/ACE.Server/WorldObjects/Pet.cs");

            var method = code.IndexOf("TeleSummonOutcome TeleportToOwnerOnCommand(Player owner)", StringComparison.Ordinal);
            Assert.IsTrue(method >= 0);

            var check = code.IndexOf("CheckTeleSummonReach(owner)", method, StringComparison.Ordinal);
            var place = code.IndexOf("PlaceBesideOwner(owner", method, StringComparison.Ordinal);

            Assert.IsTrue(check > method && place > check, "reach must be re-checked before the pet is placed");

            var reach = code.IndexOf("TeleSummonOutcome? CheckTeleSummonReach(Player owner)", StringComparison.Ordinal);
            var reachEnd = code.IndexOf("return null;", reach, StringComparison.Ordinal);
            var reachBody = code.Substring(reach, reachEnd - reach);

            StringAssert.Contains(reachBody, "PetFollowRules.SharesTickThread(", "the cross-thread rule must be part of the re-check");
            StringAssert.Contains(reachBody, "PetFollowRules.CanTeleportToOwner(", "and so must the owner/instance rule");
        }

        /// <summary>
        /// H3 fix: the follow window (and with it the stuck and max-time triggers) opens whenever the pet wants to
        /// follow, not only when IssueFollowMove succeeded - `ShouldStartFollow(distance) &amp;&amp; IssueFollowMove`
        /// left a not-MoveReady pet with no trigger armed.
        /// </summary>
        [TestMethod]
        public void FollowWindow_OpensWhetherOrNotTheMoveToWentOut()
        {
            var code = Regex.Replace(Source("Source/ACE.Server/WorldObjects/CombatPet_Follow.cs"), @"\s+", " ");

            Assert.IsFalse(code.Contains("ShouldStartFollow(distance) && IssueFollowMove(owner)"),
                "the follow window must not depend on the MoveTo having been issued");
            StringAssert.Contains(code, "if (PetFollowRules.ShouldStartFollow(distance)) { isFollowingOwner = true;");
        }

        private static string Source(string repoPath)
        {
            var native = repoPath.Replace('/', Path.DirectorySeparatorChar);

            for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
            {
                var candidate = Path.Combine(dir.FullName, native);
                if (File.Exists(candidate))
                    return StripLineComments(File.ReadAllText(candidate));
            }

            Assert.Fail($"Could not find {repoPath} by walking up from {AppContext.BaseDirectory} - this suite must run in-tree");
            return null;
        }

        /// <summary>Drops // comments (and /// doc comments) so prose naming these symbols cannot satisfy a pin.</summary>
        private static string StripLineComments(string code)
        {
            return Regex.Replace(code, @"^\s*//.*$", "", RegexOptions.Multiline);
        }
    }
}
