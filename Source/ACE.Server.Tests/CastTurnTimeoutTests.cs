using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

using ACE.Server.Entity;
using ACE.Server.WorldObjects;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ACE.Server.Tests
{
    /// <summary>
    /// The bound on a FastTick cast waiting to face its target (spellcast_turn_timeout_seconds). Before it, a cast whose
    /// turn kept being cancelled by client MoveToStates, or that waited behind a stuck animation queue or a held turn
    /// key, left the player IsBusy indefinitely (every action answered YoureTooBusy).
    ///
    /// A live Player cannot be built in this harness (its constructor needs a live database), so the live call sites -
    /// Player.UpdateObjectPhysics -> CheckCastTurnTimeout, TurnTo_Magic, DoWindup, DoCastSpell, MagicState.OnCastStart /
    /// OnCastDone - are NOT executed here. They are covered two ways: the pure pieces they are assembled from
    /// (CastTurnTimeout.IsExpired / ClampTimeout, MagicState.BeginTurnWait / EndTurnWait, run for real on a
    /// MagicState with no Player, which those two methods never touch), and source scans that bind each call to the
    /// method it must sit in. Each test names the call site it stands for.
    /// </summary>
    [TestClass]
    public class CastTurnTimeoutTests
    {
        private static readonly DateTime T0 = new DateTime(2026, 10, 10, 15, 37, 29, DateTimeKind.Utc);
        private const double Limit = CastTurnTimeout.DefaultTimeoutSeconds;

        // ---- the decision (Player.CheckCastTurnTimeout -> CastTurnTimeout.IsExpired) ----

        [TestMethod]
        public void CheckCastTurnTimeout_FastTickCastWaitingPastLimit_Expires()
        {
            Assert.IsTrue(CastTurnTimeout.IsExpired(true, true, T0, T0.AddSeconds(Limit), Limit));
            Assert.IsTrue(CastTurnTimeout.IsExpired(true, true, T0, T0.AddSeconds(40.8), Limit), "the 40.8 s incident wait");
        }

        [TestMethod]
        public void CheckCastTurnTimeout_UnderLimit_DoesNotExpire()
        {
            Assert.IsFalse(CastTurnTimeout.IsExpired(true, true, T0, T0.AddSeconds(Limit - 0.05), Limit));
        }

        [TestMethod]
        public void CheckCastTurnTimeout_NonFastTick_NeverExpires()
        {
            // the non-FastTick cast path never waits on facing; its behaviour must not change
            Assert.IsFalse(CastTurnTimeout.IsExpired(false, true, T0, T0.AddSeconds(600), Limit));
        }

        [TestMethod]
        public void CheckCastTurnTimeout_NotCasting_NeverExpires()
        {
            // after FailCast / FinishCast IsCasting is false: the bound cannot fire a second time for the same cast
            Assert.IsFalse(CastTurnTimeout.IsExpired(true, false, T0, T0.AddSeconds(600), Limit));
        }

        [TestMethod]
        public void CheckCastTurnTimeout_CastNotWaiting_NeverExpires()
        {
            // a cast that never waited on a turn (already facing, no movement) is untouched however long it runs
            Assert.IsFalse(CastTurnTimeout.IsExpired(true, true, DateTime.MinValue, T0.AddSeconds(600), Limit));
        }

        [TestMethod]
        public void CheckCastTurnTimeout_Disabled_NeverExpires()
        {
            Assert.IsFalse(CastTurnTimeout.IsExpired(true, true, T0, T0.AddSeconds(600), 0));
            Assert.IsFalse(CastTurnTimeout.IsExpired(true, true, T0, T0.AddSeconds(600), -3));
        }

        // ---- the tunable (CastTurnTimeout.TimeoutSeconds seam -> ClampTimeout) ----

        [TestMethod]
        public void ClampTimeout_Rules()
        {
            Assert.AreEqual(CastTurnTimeout.DefaultTimeoutSeconds, CastTurnTimeout.ClampTimeout(double.NaN));
            Assert.AreEqual(CastTurnTimeout.DefaultTimeoutSeconds, CastTurnTimeout.ClampTimeout(double.PositiveInfinity));
            Assert.AreEqual(0.0, CastTurnTimeout.ClampTimeout(0));
            Assert.AreEqual(0.0, CastTurnTimeout.ClampTimeout(-1));
            Assert.AreEqual(CastTurnTimeout.MinTimeoutSeconds, CastTurnTimeout.ClampTimeout(0.2));
            Assert.AreEqual(7.5, CastTurnTimeout.ClampTimeout(7.5));
        }

        [TestMethod]
        public void Default_IsEnabled()
        {
            Assert.IsTrue(CastTurnTimeout.DefaultTimeoutSeconds >= CastTurnTimeout.MinTimeoutSeconds, "the bound ships ON");
            Assert.AreEqual(CastTurnTimeout.DefaultTimeoutSeconds, CastTurnTimeout.ClampTimeout(CastTurnTimeout.DefaultTimeoutSeconds));
        }

        [TestMethod]
        public void PropertyManagerRow_UsesTheDefaultConstant()
        {
            var pm = StripComments(RepoFile("Source", "ACE.Server", "Managers", "PropertyManager.cs"));
            StringAssert.Contains(pm, "(\"spellcast_turn_timeout_seconds\", new Property<double>(ACE.Server.WorldObjects.CastTurnTimeout.DefaultTimeoutSeconds,");
            Assert.AreEqual("spellcast_turn_timeout_seconds", CastTurnTimeout.TimeoutKey);
        }

        // ---- the clock (MagicState.BeginTurnWait / EndTurnWait, as TurnTo_Magic, DoWindup and DoCastSpell drive it) ----

        [TestMethod]
        public void TurnTo_Magic_ReissuedAfterEveryMoveToState_StillExpiresAtTheBound()
        {
            // The livelock: TurnTo_Magic starts the wait, a MoveToState in the same tick cancels the turn
            // (StopExistingMoveToChains2 -> OnMoveComplete_Magic(ActionCancelled)), the next tick DoWindup re-checks the
            // angle and re-issues the turn, and the next MoveToState cancels it again. The clock must not restart.
            var magic = new MagicState(null);

            magic.BeginTurnWait(T0);                       // cast -> TurnTo_Magic
            for (var t = 0.1; t < Limit; t += 0.25)
                magic.BeginTurnWait(T0.AddSeconds(t));     // DoWindup re-issue after each cancelling MoveToState

            Assert.AreEqual(T0, magic.TurnWaitStart, "a re-issued turn must not restart the wait");
            Assert.IsTrue(CastTurnTimeout.IsExpired(true, true, magic.TurnWaitStart, T0.AddSeconds(Limit), Limit),
                "a cast whose turn every MoveToState cancels must fail within the bound");
        }

        [TestMethod]
        public void TurnTo_Magic_TurnCompletes_WaitEnds_NormalCastUnaffected()
        {
            var magic = new MagicState(null);

            magic.BeginTurnWait(T0);                       // TurnTo_Magic
            magic.EndTurnWait();                           // OnMoveComplete_Magic(None) -> DoWindup -> windup starts

            Assert.AreEqual(DateTime.MinValue, magic.TurnWaitStart);
            Assert.IsFalse(CastTurnTimeout.IsExpired(true, true, magic.TurnWaitStart, T0.AddSeconds(600), Limit),
                "once the windup has started the pre-windup wait no longer counts");
        }

        [TestMethod]
        public void DoCastSpell_PostGestureWait_IsClockedFromItsOwnStart()
        {
            var magic = new MagicState(null);

            magic.BeginTurnWait(T0);                       // pre-windup turn
            magic.EndTurnWait();                           // windup started at T0 + 1
            var postGesture = T0.AddSeconds(3);
            magic.BeginTurnWait(postGesture);              // DoCastSpell: target moved, turn again

            Assert.AreEqual(postGesture, magic.TurnWaitStart);
            Assert.IsFalse(CastTurnTimeout.IsExpired(true, true, magic.TurnWaitStart, postGesture.AddSeconds(Limit - 0.5), Limit));
            Assert.IsTrue(CastTurnTimeout.IsExpired(true, true, magic.TurnWaitStart, postGesture.AddSeconds(Limit), Limit));
        }

        // ---- the live call sites, bound by source scan ----

        [TestMethod]
        public void UpdateObjectPhysics_RunsTheBoundEveryTick_NotOnlyWhileMoving()
        {
            var tick = StripComments(RepoFile("Source", "ACE.Server", "WorldObjects", "Player_Tick.cs"));

            var update = MethodBody(tick, "public override bool UpdateObjectPhysics()");
            Assert.IsTrue(Regex.IsMatch(update, @"if\s*\(\s*MagicState\.TurnWaitStart\s*!=\s*DateTime\.MinValue\s*\)\s*CheckCastTurnTimeout\(\);"),
                "UpdateObjectPhysics must call CheckCastTurnTimeout while a cast waits on its turn");

            var gate = update.IndexOf("if (FastTick && PhysicsObj.IsMovingOrAnimating", StringComparison.Ordinal);
            var call = update.IndexOf("CheckCastTurnTimeout();", StringComparison.Ordinal);
            Assert.IsTrue(gate > 0 && call > 0 && call < gate, "the bound must run before (outside) the moving-or-animating gate");

            var physics = MethodBody(tick, "public void UpdatePlayerPhysics()");
            Assert.IsFalse(physics.Contains("CheckCastTurnTimeout"),
                "UpdatePlayerPhysics only runs while moving or animating; a parked cast may be neither");
        }

        // ---- the outcome (Player.CheckCastTurnTimeout -> CastTurnTimeout.Decide) ----

        private const double StaleGate = MotionStallWatchdog.DefaultThresholdSeconds;

        private static CastTurnTimeout.Decision Decide(double elapsed, bool castMotionDone, bool blocked = false, double queueStale = 0, bool autoClear = true)
        {
            return CastTurnTimeout.Decide(true, true, T0, T0.AddSeconds(elapsed), Limit, castMotionDone, blocked, queueStale, StaleGate, autoClear);
        }

        [TestMethod]
        public void CheckCastTurnTimeout_PreWindupExpiry_Fails()
        {
            var d = Decide(Limit, castMotionDone: false);
            Assert.AreEqual(CastTurnTimeout.Outcome.Fail, d.Outcome, "before the windup nothing is spent: FailCast");
            Assert.IsFalse(d.ClearQueue);
        }

        [TestMethod]
        public void CheckCastTurnTimeout_PostGestureExpiry_Releases()
        {
            var d = Decide(Limit, castMotionDone: true);
            Assert.AreEqual(CastTurnTimeout.Outcome.Release, d.Outcome, "after the launch gesture the spell releases off-angle, as non-FastTick does");
        }

        [TestMethod]
        public void CheckCastTurnTimeout_NotExpired_Waits()
        {
            Assert.AreEqual(CastTurnTimeout.Outcome.Wait, Decide(Limit - 0.05, castMotionDone: true).Outcome);
            Assert.AreEqual(CastTurnTimeout.Outcome.Wait, Decide(Limit - 0.05, castMotionDone: false).Outcome);
        }

        [TestMethod]
        public void CheckCastTurnTimeout_BlockedBehindFreshQueue_DoesNotExpire()
        {
            // a healthy long animation (EnterPKLite 10.3 s) ahead of the turn: queue non-empty 9 s, under the 10 s gate
            var d = Decide(40, castMotionDone: false, blocked: true, queueStale: StaleGate - 1);
            Assert.AreEqual(CastTurnTimeout.Outcome.Wait, d.Outcome);
            Assert.IsFalse(d.ClearQueue);
        }

        [TestMethod]
        public void CheckCastTurnTimeout_BlockedBehindStaleQueue_ExpiresAndClearsTheQueue()
        {
            var d = Decide(Limit, castMotionDone: false, blocked: true, queueStale: StaleGate + 0.1);
            Assert.AreEqual(CastTurnTimeout.Outcome.Fail, d.Outcome);
            Assert.IsTrue(d.ClearQueue, "a dead queue is cleared at the timeout, deterministically (no dwell gate)");

            var post = Decide(Limit, castMotionDone: true, blocked: true, queueStale: StaleGate + 0.1);
            Assert.AreEqual(CastTurnTimeout.Outcome.Release, post.Outcome);
            Assert.IsTrue(post.ClearQueue);
        }

        [TestMethod]
        public void CheckCastTurnTimeout_StaleQueue_KillSwitchOff_NoClear()
        {
            var d = Decide(Limit, castMotionDone: false, blocked: true, queueStale: StaleGate + 5, autoClear: false);
            Assert.AreEqual(CastTurnTimeout.Outcome.Fail, d.Outcome);
            Assert.IsFalse(d.ClearQueue, "motion_stall_autoclear = false still disables every automatic clear");
        }

        [TestMethod]
        public void CheckCastTurnTimeout_NotBlocked_NoClear()
        {
            Assert.IsFalse(Decide(Limit, castMotionDone: false, blocked: false, queueStale: 60).ClearQueue);
        }

        [TestMethod]
        public void MagicState_TickTurnWait_HealthyAnimationTimeIsNotCounted()
        {
            // TurnTo_Magic at T0, then the turn sits 6 s behind a healthy animation, then waits 4 s unblocked:
            // 4 s of counted wait, so not expired; 1 s more and it is.
            var magic = new MagicState(null);
            magic.BeginTurnWait(T0);

            for (var t = 0.05; t <= 6.0001; t += 0.05)
                magic.TickTurnWait(T0.AddSeconds(t), heldByHealthyAnimation: true, maxPauseSeconds: Cap);

            for (var t = 6.05; t <= 10.0001; t += 0.05)
                magic.TickTurnWait(T0.AddSeconds(t), heldByHealthyAnimation: false, maxPauseSeconds: Cap);

            Assert.IsFalse(CastTurnTimeout.IsExpired(true, true, magic.TurnWaitStart, T0.AddSeconds(10), Limit),
                "time held behind a healthy animation must not count against the cast");
            Assert.IsTrue(CastTurnTimeout.IsExpired(true, true, magic.TurnWaitStart, T0.AddSeconds(11.05), Limit));
        }

        private static readonly double Cap = CastTurnTimeout.MaxPauseSeconds(Limit, StaleGate);

        [TestMethod]
        public void MaxPauseSeconds_IsTheLargerOfTwiceTimeoutAndStalePlusTimeout()
        {
            Assert.AreEqual(15.0, CastTurnTimeout.MaxPauseSeconds(5, 10));
            Assert.AreEqual(40.0, CastTurnTimeout.MaxPauseSeconds(20, 10));
        }

        [TestMethod]
        public void MagicState_TickTurnWait_PerpetualHealthyHold_StillExpires()
        {
            // a queue that keeps looking healthy forever (never crosses the stale gate) must not hold the cast forever:
            // the pause is capped at Cap, then the clock runs and Decide stops honouring the hold
            var magic = new MagicState(null);
            magic.BeginTurnWait(T0);

            var expiredAt = DateTime.MinValue;
            for (var t = 0.05; t <= 120.0001; t += 0.05)
            {
                var now = T0.AddSeconds(t);
                magic.TickTurnWait(now, heldByHealthyAnimation: true, maxPauseSeconds: Cap);

                var d = CastTurnTimeout.Decide(true, true, magic.TurnWaitStart, now, Limit, false, true, StaleGate - 1, StaleGate, true,
                    pauseExhausted: magic.TurnWaitPausedSeconds >= Cap);

                if (d.Outcome != CastTurnTimeout.Outcome.Wait)
                {
                    expiredAt = now;
                    Assert.AreEqual(CastTurnTimeout.Outcome.Fail, d.Outcome);
                    Assert.IsFalse(d.ClearQueue, "a queue the watchdog does not call stale is not cleared");
                    break;
                }
            }

            Assert.AreNotEqual(DateTime.MinValue, expiredAt, "a perpetual healthy hold must still expire");
            Assert.AreEqual(Cap, magic.TurnWaitPausedSeconds, 0.001, "the pause stops exactly at the cap");
            Assert.AreEqual(Cap + Limit, (expiredAt - T0).TotalSeconds, 0.06, "expiry = cap + timeout");
        }

        [TestMethod]
        public void CheckCastTurnTimeout_ReleaseIsDeferredToAnActionChainBoundToTheCast()
        {
            var body = CheckCastTurnTimeoutTryBody();

            var release = body.IndexOf("TurnStarted = false;", StringComparison.Ordinal);
            var capture = body.IndexOf("var castNum = MagicState.CastNum;", release, StringComparison.Ordinal);
            var end = body.IndexOf("MagicState.EndTurnWait();", release, StringComparison.Ordinal);
            var chain = body.IndexOf("new ActionChain();", release, StringComparison.Ordinal);
            var delay = body.IndexOf(".AddDelayForOneTick();", release, StringComparison.Ordinal);
            var lambda = body.IndexOf(".AddAction(this, () =>", release, StringComparison.Ordinal);
            var enqueue = body.IndexOf(".EnqueueChain();", release, StringComparison.Ordinal);

            Assert.IsTrue(release > 0 && capture > release && end > release && chain > capture && delay > chain && lambda > delay && enqueue > lambda,
                "Release: capture CastNum, EndTurnWait, then an ActionChain with a one-tick delay");

            var inLambda = body.Substring(lambda, enqueue - lambda);
            Assert.IsTrue(Regex.IsMatch(inLambda, @"\(\)\s*=>\s*\{\s*if\s*\(\s*!MagicState\.IsCasting\s*\|\|\s*MagicState\.CastNum\s*!=\s*castNum\s*\)\s*return;"),
                "the deferred release must first check it still belongs to this cast");
            StringAssert.Contains(inLambda, "DoCastSpell(MagicState, false);", "the release runs inside the deferred action, not on the physics tick");
            StringAssert.Contains(inLambda, "if (!VerifyCastRadius())");

            var outside = body.Substring(0, lambda) + body.Substring(enqueue);
            Assert.IsFalse(outside.Contains("DoCastSpell(MagicState, false);"), "no release call outside the deferred action");
            Assert.IsFalse(outside.Contains("MagicState.OnCastDone();"), "the fallback close must not run before the deferred release");
        }
        [TestMethod]
        public void MagicState_TickTurnWait_NotHeld_ClockRuns()
        {
            var magic = new MagicState(null);
            magic.BeginTurnWait(T0);

            for (var t = 0.05; t <= Limit + 0.0001; t += 0.05)
                magic.TickTurnWait(T0.AddSeconds(t), heldByHealthyAnimation: false, maxPauseSeconds: Cap);

            Assert.AreEqual(T0, magic.TurnWaitStart);
        }

        [TestMethod]
        public void CheckCastTurnTimeout_ReleasePath_RadiusCheckThenOffAngleRelease()
        {
            var body = CheckCastTurnTimeoutTryBody();

            var release = body.IndexOf("TurnStarted = false;", StringComparison.Ordinal);
            var drop = body.IndexOf("StopExistingMoveToChains2();", release, StringComparison.Ordinal);
            var radius = body.IndexOf("if (!VerifyCastRadius())", StringComparison.Ordinal);
            var cast = body.IndexOf("DoCastSpell(MagicState, false);", StringComparison.Ordinal);

            Assert.IsTrue(release > 0 && drop > release && radius > drop && cast > radius,
                "Release: drop the turn (TurnStarted false first), then the PK radius check, then DoCastSpell(MagicState, false)");
        }

        [TestMethod]
        public void CheckCastTurnTimeout_FailPath_FailsOnceThenDropsTheTurn()
        {
            var body = CheckCastTurnTimeoutTryBody();

            var failBranch = body.IndexOf("if (decision.Outcome == CastTurnTimeout.Outcome.Fail)", StringComparison.Ordinal);
            Assert.IsTrue(failBranch > 0);

            Assert.AreEqual(1, Regex.Matches(body, @"\bFailCast\(\)").Count, "exactly one FailCast per expiry");
            Assert.IsFalse(body.Contains("SendUseDoneEvent"), "UseDone comes from FailCast / FinishCast alone, never twice");

            var fail = body.IndexOf("FailCast();", failBranch, StringComparison.Ordinal);
            var drop = body.IndexOf("StopExistingMoveToChains2();", failBranch, StringComparison.Ordinal);
            Assert.IsTrue(fail > failBranch && drop > fail, "the turn is dropped after FailCast, so OnMoveComplete_Magic sees IsCasting false");
        }

        [TestMethod]
        public void CheckCastTurnTimeout_ClearsTheStaleQueueDirectly()
        {
            var body = CheckCastTurnTimeoutTryBody();

            Assert.IsTrue(Regex.IsMatch(body, @"if\s*\(\s*decision\.ClearQueue\s*\)\s*ClearMotionStall\(""cast_timeout"",\s*allowTwinDrainWhileCasting:\s*decision\.Outcome\s*==\s*CastTurnTimeout\.Outcome\.Fail\s*\);"),
                "a decided clear calls ClearMotionStall directly, draining the twin queue only on the Fail outcome (Release keeps the MotionInterp-only clear)");
            Assert.IsFalse(body.Contains("CheckMotionStall("), "the clear must not depend on CheckMotionStall's dwell gate");

            var clear = body.IndexOf("ClearMotionStall(", StringComparison.Ordinal);
            var fail = body.IndexOf("FailCast();", StringComparison.Ordinal);
            var cast = body.IndexOf("DoCastSpell(MagicState, false);", StringComparison.Ordinal);
            Assert.IsTrue(clear < fail && clear < cast, "the queue is cleared before the cast is resolved");
        }

        [TestMethod]
        public void CheckCastTurnTimeout_NeverThrows_EndsTheCastOnException()
        {
            var magic = StripComments(RepoFile("Source", "ACE.Server", "WorldObjects", "Player_Magic.cs"));
            var method = MethodBody(magic, "public void CheckCastTurnTimeout()");

            Assert.IsTrue(Regex.IsMatch(method, @"^\{\s*try\s*\{"), "the whole body sits in try");

            var catchAt = method.IndexOf("catch (Exception ex)", StringComparison.Ordinal);
            Assert.IsTrue(catchAt > 0, "CheckCastTurnTimeout must catch: UpdateObjectPhysics has no catch of its own");

            var handler = method.Substring(catchAt);
            StringAssert.Contains(handler, "MagicState.OnCastDone();");
            StringAssert.Contains(handler, "SendUseDoneEvent();");
            StringAssert.Contains(handler, "castTurnTimeoutErrorGate.TryPass(");
        }

        [TestMethod]
        public void OnMoveComplete_Magic_FollowUpIsBoundToItsCast()
        {
            var magic = StripComments(RepoFile("Source", "ACE.Server", "WorldObjects", "Player_Magic.cs"));
            var body = MethodBody(magic, "public void OnMoveComplete_Magic(WeenieError status)");

            var capture = body.IndexOf("var castNum = MagicState.CastNum;", StringComparison.Ordinal);
            var chain = body.IndexOf("actionChain.AddAction(", StringComparison.Ordinal);
            Assert.IsTrue(capture > 0 && capture < chain, "CastNum must be captured when the follow-up is scheduled");
            Assert.IsTrue(Regex.IsMatch(body.Substring(chain), @"if\s*\(\s*!MagicState\.IsCasting\s*\|\|\s*MagicState\.CastNum\s*!=\s*castNum\s*\)\s*return;"),
                "the follow-up must return when the cast it was scheduled for has ended");
        }

        [TestMethod]
        public void HandleMotionDone_Magic_FollowUpIsBoundToItsCast()
        {
            // a drained cast gesture (motion-stall clear, cast_timeout Fail, a jump) schedules this follow-up; if that
            // cast ends and a new one starts within the tick, the stale follow-up must not cast the new spell early
            var magic = StripComments(RepoFile("Source", "ACE.Server", "WorldObjects", "Player_Magic.cs"));
            var body = MethodBody(magic, "public void HandleMotionDone_Magic(uint motionID, bool success)");

            var capture = body.IndexOf("var castNum = MagicState.CastNum;", StringComparison.Ordinal);
            var chain = body.IndexOf("actionChain.AddAction(", StringComparison.Ordinal);
            Assert.IsTrue(capture > 0 && capture < chain, "CastNum must be captured when the follow-up is scheduled");

            var followUp = body.Substring(chain);
            var guard = Regex.Match(followUp, @"if\s*\(\s*!MagicState\.IsCasting\s*\|\|\s*MagicState\.CastNum\s*!=\s*castNum\s*\)\s*return;");
            Assert.IsTrue(guard.Success, "the follow-up must return when the cast it was scheduled for has ended");
            var alwaysTurn = followUp.IndexOf("MagicState.AlwaysTurn = true;", StringComparison.Ordinal);
            Assert.IsTrue(alwaysTurn > 0 && guard.Index < alwaysTurn, "the guard runs before the follow-up touches MagicState (AlwaysTurn)");
            Assert.IsTrue(guard.Index < followUp.IndexOf("DoCastSpell(MagicState);", StringComparison.Ordinal), "the guard runs before DoCastSpell");
        }

        /// <summary>CheckCastTurnTimeout's try block (everything before its catch).</summary>
        private static string CheckCastTurnTimeoutTryBody()
        {
            var magic = StripComments(RepoFile("Source", "ACE.Server", "WorldObjects", "Player_Magic.cs"));
            var method = MethodBody(magic, "public void CheckCastTurnTimeout()");

            var catchAt = method.IndexOf("catch (Exception ex)", StringComparison.Ordinal);
            Assert.IsTrue(catchAt > 0);

            var body = method.Substring(0, catchAt);
            StringAssert.Contains(body, "CastTurnTimeout.Decide(FastTick, MagicState.IsCasting, MagicState.TurnWaitStart, now, timeoutSeconds,");
            return body;
        }

        [TestMethod]
        public void TurnTo_Magic_StartsTheWait_OnFastTick()
        {
            var magic = StripComments(RepoFile("Source", "ACE.Server", "WorldObjects", "Player_Magic.cs"));
            var body = MethodBody(magic, "public void TurnTo_Magic(WorldObject target)");

            var fastTick = body.IndexOf("if (FastTick)", StringComparison.Ordinal);
            var begin = body.IndexOf("MagicState.BeginTurnWait(DateTime.UtcNow);", StringComparison.Ordinal);
            var turn = body.IndexOf("CreateTurnToChain2(", StringComparison.Ordinal);

            Assert.IsTrue(fastTick >= 0 && begin > fastTick && begin < turn, "TurnTo_Magic must start the wait inside its FastTick branch, before the turn");
        }

        [TestMethod]
        public void DoWindup_PendingTurnReleaseIsClocked_AndWindupEndsTheWait()
        {
            var magic = StripComments(RepoFile("Source", "ACE.Server", "WorldObjects", "Player_Magic.cs"));
            var body = MethodBody(magic, "public void DoWindup(WindupParams windupParams, bool checkAngle)");

            var end = body.IndexOf("MagicState.EndTurnWait();", StringComparison.Ordinal);
            var create = body.IndexOf("CreatePlayerSpell(", StringComparison.Ordinal);
            Assert.IsTrue(end >= 0 && end < create, "DoWindup must end the wait before the windup starts");

            var begin = body.IndexOf("MagicState.BeginTurnWait(DateTime.UtcNow);", StringComparison.Ordinal);
            var pending = body.IndexOf("MagicState.PendingTurnRelease = true;", StringComparison.Ordinal);
            Assert.IsTrue(begin > create && begin < pending, "the re-turn / PendingTurnRelease branch must be clocked");
        }

        [TestMethod]
        public void DoCastSpell_PendingTurnReleaseIsClocked_AndReleaseEndsTheWait()
        {
            var magic = StripComments(RepoFile("Source", "ACE.Server", "WorldObjects", "Player_Magic.cs"));
            var body = MethodBody(magic, "public void DoCastSpell(Spell spell, WorldObject casterItem, uint magicSkill, uint manaUsed, WorldObject target, CastingPreCheckStatus castingPreCheckStatus, bool checkAngle = true)");

            var begin = body.IndexOf("MagicState.BeginTurnWait(DateTime.UtcNow);", StringComparison.Ordinal);
            var pending = body.IndexOf("MagicState.PendingTurnRelease = true;", StringComparison.Ordinal);
            Assert.IsTrue(begin >= 0 && begin < pending, "DoCastSpell's FastTick re-turn / PendingTurnRelease branch must be clocked");

            var end = body.IndexOf("MagicState.EndTurnWait();", StringComparison.Ordinal);
            var inner = body.IndexOf("DoCastSpell_Inner(", StringComparison.Ordinal);
            Assert.IsTrue(end > pending && end < inner, "DoCastSpell must end the wait before the spell releases");
        }

        [TestMethod]
        public void MagicState_CastStartAndDone_ResetTheWait()
        {
            var state = StripComments(RepoFile("Source", "ACE.Server", "Entity", "MagicState.cs"));

            StringAssert.Contains(MethodBody(state, "public void OnCastStart()"), "EndTurnWait();");
            StringAssert.Contains(MethodBody(state, "public void OnCastDone()"), "EndTurnWait();");
        }

        // ---- helpers ----

        private static string RepoFile(params string[] relative)
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);

            while (dir != null && !Directory.Exists(Path.Combine(dir.FullName, "Source", "ACE.Server")))
                dir = dir.Parent;

            Assert.IsNotNull(dir, $"Could not find Source/ACE.Server by walking up from {AppContext.BaseDirectory}");

            return File.ReadAllText(Path.Combine(dir.FullName, Path.Combine(relative))).Replace("\r\n", "\n");
        }

        /// <summary>Drops // line comments (and /// doc lines), so a scan binds code, not prose about it.</summary>
        private static string StripComments(string text)
        {
            return string.Join("\n", text.Split('\n').Select(line =>
            {
                var i = line.IndexOf("//", StringComparison.Ordinal);
                if (i < 0)
                    return line;

                // keep "//" inside a string literal (e.g. a URL) - only strip when no quote precedes it on the line
                return line.Substring(0, i).Contains('"') ? line : line.Substring(0, i);
            }));
        }

        /// <summary>The body of one method, by brace balance from its signature; fails loudly if the signature moved.</summary>
        private static string MethodBody(string text, string signature)
        {
            var start = text.IndexOf(signature, StringComparison.Ordinal);
            Assert.IsTrue(start >= 0, $"'{signature}' was not found - update this test's anchor.");

            var open = text.IndexOf('{', start);
            var depth = 0;

            for (var i = open; i < text.Length; i++)
            {
                if (text[i] == '{') depth++;
                else if (text[i] == '}' && --depth == 0)
                    return text.Substring(open, i - open + 1);
            }

            Assert.Fail($"No closing brace for '{signature}'.");
            return null;
        }
    }
}
