using System;
using System.Collections.Generic;
using System.IO;

using ACE.Entity.Enum;
using ACE.Entity.Models;
using ACE.Server.Entity.Actions;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ACE.Server.Tests
{
    /// <summary>
    /// Coverage for the world-thread exception containment added by the tick-hardening sweep.
    ///
    /// An unhandled exception on the world-simulation thread does NOT crash the process: WorldManager's
    /// fatal handler stops the world instead, leaving every session connected to a shard where nothing
    /// ticks. The containment under test turns that into "one bad action fails alone".
    ///
    /// Only the pieces that are genuinely unit-testable are here. ActionQueue and DelayManager work purely
    /// against the IAction/IActor interfaces, so a fake action that throws needs no database, network,
    /// landblock or dat file. EmoteManager needs a WorldObject for its identity logging, which
    /// TestCreatures builds in-memory, and nothing else.
    ///
    /// Every test below is written so that "the exception was swallowed and the state correctly unwound"
    /// and "the exception never happened at all" produce DIFFERENT observable results - a containment test
    /// that cannot tell those apart passes just as happily against code that does nothing.
    /// </summary>
    [TestClass]
    public class TickContainmentTests
    {
        private const string DeliberateFailure = "TickContainmentTests: deliberate Act() failure";

        #region fakes

        /// <summary>An IAction whose Act() always throws. ActionEventBase gives us the IAction plumbing.</summary>
        private sealed class ThrowingAction : ActionEventBase
        {
            public int ActCalls;

            public override Tuple<IActor, IAction> Act()
            {
                ActCalls++;
                throw new InvalidOperationException(DeliberateFailure);
            }
        }

        /// <summary>An IAction that records that it ran and otherwise behaves normally.</summary>
        private sealed class RecordingAction : ActionEventBase
        {
            public int ActCalls;

            public override Tuple<IActor, IAction> Act()
            {
                ActCalls++;
                return base.Act();
            }
        }

        /// <summary>DelayManager.EnqueueAction rejects anything that is not a DelayAction, so the delay-side fakes derive from it.</summary>
        private sealed class ThrowingDelayAction : DelayAction
        {
            public int ActCalls;

            public ThrowingDelayAction() : base(0.0) { }

            public override Tuple<IActor, IAction> Act()
            {
                ActCalls++;
                throw new InvalidOperationException(DeliberateFailure);
            }
        }

        private sealed class RecordingDelayAction : DelayAction
        {
            public int ActCalls;

            public RecordingDelayAction() : base(0.0) { }

            public override Tuple<IActor, IAction> Act()
            {
                ActCalls++;
                return base.Act();
            }
        }

        /// <summary>Records what a completed action asked to be enqueued next, so follow-up propagation is observable.</summary>
        private sealed class RecordingActor : IActor
        {
            public readonly List<IAction> Enqueued = new List<IAction>();

            public void EnqueueAction(IAction action)
            {
                Enqueued.Add(action);
            }
        }

        #endregion

        #region ActionQueue

        /// <summary>
        /// One throwing action must cost one action, not the rest of the batch. Three distinct outcomes are
        /// separated here: an escaping throw fails the test outright, a catch that stopped draining leaves
        /// the later actions at zero calls, and correct containment runs all three.
        /// </summary>
        [TestMethod]
        public void ActionQueue_RunActions_DrainsPastAThrowingAction()
        {
            var queue = new ActionQueue();

            var bad = new ThrowingAction();
            var afterA = new RecordingAction();
            var afterB = new RecordingAction();

            queue.EnqueueAction(bad);
            queue.EnqueueAction(afterA);
            queue.EnqueueAction(afterB);

            queue.RunActions();

            Assert.AreEqual(1, bad.ActCalls, "the throwing action should have been dequeued and attempted exactly once");
            Assert.AreEqual(1, afterA.ActCalls, "the action queued immediately after the failure must still run in the same pass");
            Assert.AreEqual(1, afterB.ActCalls, "every remaining action in the batch must still run in the same pass");

            // The batch must have been drained, not re-queued: a failing action that goes back on the queue
            // would be retried on every world tick forever.
            queue.RunActions();

            Assert.AreEqual(1, bad.ActCalls, "the failed action must not be retried on the next pass");
            Assert.AreEqual(1, afterA.ActCalls, "the queue must have been drained, not re-queued");
            Assert.AreEqual(1, afterB.ActCalls, "the queue must have been drained, not re-queued");
        }

        /// <summary>
        /// The queue itself must survive the failure: an action enqueued after a throwing one has already
        /// been drained still runs on the next pass. If the throw had escaped RunActions, the actor holding
        /// this queue would never reach a second pass at all.
        /// </summary>
        [TestMethod]
        public void ActionQueue_QueueIsStillUsableAfterAnActionThrows()
        {
            var queue = new ActionQueue();

            var bad = new ThrowingAction();
            queue.EnqueueAction(bad);
            queue.RunActions();

            var later = new RecordingAction();
            queue.EnqueueAction(later);
            queue.RunActions();

            Assert.AreEqual(1, bad.ActCalls, "the throwing action should have run once, on the first pass");
            Assert.AreEqual(1, later.ActCalls, "an action enqueued after a failure must run normally");
        }

        /// <summary>
        /// A failed Act() returned nothing, so it has no follow-up to propagate - the containment must not
        /// invent one. The second half is the control run: the identical wiring on an action that does NOT
        /// throw does propagate, so a zero on the failure side means "suppressed", not "this test could
        /// never have observed a follow-up in the first place".
        /// </summary>
        [TestMethod]
        public void ActionQueue_ThrowingActionDoesNotPropagateAFollowUp()
        {
            var queue = new ActionQueue();

            var failedFollowUpActor = new RecordingActor();
            var bad = new ThrowingAction();
            bad.RunOnFinish(failedFollowUpActor, new RecordingAction());

            queue.EnqueueAction(bad);
            queue.RunActions();

            Assert.AreEqual(0, failedFollowUpActor.Enqueued.Count, "a follow-up must not be enqueued for an action whose Act() threw");

            // Control: same wiring, an action that completes normally.
            var okFollowUpActor = new RecordingActor();
            var good = new RecordingAction();
            good.RunOnFinish(okFollowUpActor, new RecordingAction());

            queue.EnqueueAction(good);
            queue.RunActions();

            Assert.AreEqual(1, okFollowUpActor.Enqueued.Count, "control: a normally completing action's follow-up IS propagated, so the zero above is meaningful");
        }

        #endregion

        #region DelayManager

        /// <summary>
        /// DelayManager pulls a whole batch of due actions out of its heap before running any of them, so an
        /// escaping throw silently drops every remaining action in that batch as well as stopping the world.
        /// Both zero-wait actions below are due immediately, and DelayAction's tie-break on insertion
        /// sequence keeps the throwing one first.
        /// </summary>
        [TestMethod]
        public void DelayManager_RunActions_DrainsPastAThrowingAction()
        {
            var delayManager = new DelayManager();

            var bad = new ThrowingDelayAction();
            var after = new RecordingDelayAction();

            delayManager.EnqueueAction(bad);
            delayManager.EnqueueAction(after);

            delayManager.RunActions();

            Assert.AreEqual(1, bad.ActCalls, "the throwing delayed action should have been attempted exactly once");
            Assert.AreEqual(1, after.ActCalls, "the rest of the due batch must still run after one of them throws");

            // Drained, not re-queued.
            delayManager.RunActions();

            Assert.AreEqual(1, bad.ActCalls, "the failed delayed action must not be retried on the next pass");
            Assert.AreEqual(1, after.ActCalls, "the due batch must have been drained, not re-queued");
        }

        /// <summary>
        /// The manager must still accept and run work after a failure rather than being wedged by it.
        /// </summary>
        [TestMethod]
        public void DelayManager_IsStillUsableAfterAnActionThrows()
        {
            var delayManager = new DelayManager();

            var bad = new ThrowingDelayAction();
            delayManager.EnqueueAction(bad);
            delayManager.RunActions();

            var later = new RecordingDelayAction();
            delayManager.EnqueueAction(later);
            delayManager.RunActions();

            Assert.AreEqual(1, bad.ActCalls, "the throwing delayed action should have run once, on the first pass");
            Assert.AreEqual(1, later.ActCalls, "a delayed action enqueued after a failure must run normally");
        }

        /// <summary>
        /// Same suppression contract as the ActionQueue case, with the same control run so the zero is
        /// discriminating rather than vacuous.
        /// </summary>
        [TestMethod]
        public void DelayManager_ThrowingActionDoesNotPropagateAFollowUp()
        {
            var delayManager = new DelayManager();

            var failedFollowUpActor = new RecordingActor();
            var bad = new ThrowingDelayAction();
            bad.RunOnFinish(failedFollowUpActor, new RecordingAction());

            delayManager.EnqueueAction(bad);
            delayManager.RunActions();

            Assert.AreEqual(0, failedFollowUpActor.Enqueued.Count, "a follow-up must not be enqueued for a delayed action whose Act() threw");

            // Control: same wiring, an action that completes normally.
            var okFollowUpActor = new RecordingActor();
            var good = new RecordingDelayAction();
            good.RunOnFinish(okFollowUpActor, new RecordingAction());

            delayManager.EnqueueAction(good);
            delayManager.RunActions();

            Assert.AreEqual(1, okFollowUpActor.Enqueued.Count, "control: a normally completing delayed action's follow-up IS propagated, so the zero above is meaningful");
        }

        #endregion

        #region EmoteManager

        /// <summary>
        /// A ForceMotion emote row with no Motion set. EmoteManager.ExecuteEmote's ForceMotion case reads
        /// <c>emote.Motion.Value</c> with no HasValue guard, which makes this the cheapest deterministic
        /// ExecuteEmote failure available: it needs no landblock, session, dat file or database.
        /// </summary>
        private static PropertiesEmoteAction ThrowingEmoteAction()
        {
            return new PropertiesEmoteAction { Type = (uint)EmoteType.ForceMotion, Motion = null };
        }

        /// <summary>
        /// The precondition the containment test depends on, asserted rather than assumed. If ForceMotion
        /// ever grows a null guard this fails loudly and names the reason, instead of letting the test below
        /// silently degrade into one that exercises the success path and passes anyway.
        /// </summary>
        [TestMethod]
        public void EmoteManager_ForceMotionWithNoMotion_IsADeterministicExecuteEmoteFailure()
        {
            var npc = TestCreatures.CreateQuestBearer("Containment Probe NPC");

            var emoteSet = new PropertiesEmote { Category = EmoteCategory.Use, Quest = "TickContainmentProbe" };

            Assert.ThrowsExactly<InvalidOperationException>(() => npc.EmoteManager.ExecuteEmote(emoteSet, ThrowingEmoteAction()),
                "this test's failure injection relies on ForceMotion dereferencing a null Motion; if that changed, re-point the containment test below at another deterministic ExecuteEmote failure");
        }

        /// <summary>
        /// A throwing emote must cost one emote chain and nothing more: the exception is contained, and
        /// Nested/IsBusy are unwound rather than stranded (a stranded IsBusy wedges this NPC's emote system
        /// for the life of the process, since ExecuteEmoteSet refuses a non-nested set while IsBusy).
        ///
        /// The second emote row carries a non-zero pre-delay, and that is what makes the assertions
        /// discriminating. On the contained path DoEnqueue returns immediately, the second row never runs,
        /// and the unwind happens inline: Nested 0, IsBusy false. Had ExecuteEmote NOT thrown, DoEnqueue
        /// would have chained into that second row and, because its Delay is non-zero, deferred the unwind
        /// onto an ActionChain - leaving Nested at 1 and IsBusy true at the moment we assert. So
        /// "swallowed and correctly unwound" and "never threw" land on different values here.
        /// </summary>
        [TestMethod]
        public void EmoteManager_ExecuteEmoteThrows_UnwindsNestedAndIsBusy()
        {
            var npc = TestCreatures.CreateQuestBearer("Containment Test NPC");
            var emoteManager = npc.EmoteManager;

            var emoteSet = new PropertiesEmote { Category = EmoteCategory.Use, Quest = "TickContainmentProbe" };
            emoteSet.PropertiesEmoteAction.Add(ThrowingEmoteAction());
            emoteSet.PropertiesEmoteAction.Add(new PropertiesEmoteAction { Type = (uint)EmoteType.Say, Message = "not reached", Delay = 5.0f });

            Assert.IsTrue(emoteManager.IsBusy == false && emoteManager.Nested == 0, "guard: a fresh EmoteManager starts idle");

            // An escaping throw fails the test here.
            Assert.IsTrue(emoteManager.ExecuteEmoteSet(emoteSet), "the emote set should have been accepted for execution");

            Assert.AreEqual(0, emoteManager.Nested, "Nested must be unwound after a contained ExecuteEmote failure, not leaked");
            Assert.IsFalse(emoteManager.IsBusy, "IsBusy must be released after a contained ExecuteEmote failure, or this NPC's emote system is wedged permanently");

            // Not wedged: the NPC accepts another set. ExecuteEmoteSet returns false for a non-nested set
            // while IsBusy is still true, so this is a second, independent read of the same unwind.
            Assert.IsTrue(emoteManager.ExecuteEmoteSet(emoteSet), "a later emote set must still be accepted after a contained failure");
            Assert.AreEqual(0, emoteManager.Nested, "the second failure must unwind the same way");
            Assert.IsFalse(emoteManager.IsBusy, "the second failure must unwind the same way");
        }

        #endregion

        #region final-logoff replay (call-site guard)

        /// <summary>
        /// PlayerManager.Tick's final-logoff fallback replays, by hand, the terminal steps that
        /// Player.FinalizeLogout would have performed had ForceLogoff not thrown partway through. The order
        /// is load-bearing and one of the steps is easy to lose in a later edit, so it is pinned here.
        ///
        /// This is a source-text guard, not a behavioral one, and that is a deliberate limit rather than an
        /// oversight: driving the real path needs a live Session, an active landblock, DatabaseManager.Shard
        /// for SavePlayerToDatabase, and PropertyManager (SetPropertiesAtLogOut -> UpdateOfflineBonus reads
        /// offline_bonus_enabled, and PropertyManager reads throw on a cache miss under unit tests). None of
        /// that stands up in this project. The repo has precedent for call-site guards of exactly this shape
        /// - see MuleSummonTests.CallSite_TrySummon_ChecksTheCooldownAfterTheGate.
        ///
        /// It still discriminates in the way that matters: deleting the SetPropertiesAtLogOut replay step, or
        /// moving it after the save, fails this test. Comments are stripped first, so prose that merely
        /// mentions a step does not satisfy it.
        ///
        /// Why the stamping step in particular is worth pinning: it is what advances LogoffTimestamp, which
        /// Player_OfflineBonus.AccrueOfflineBonus reads at the next login to grant (now - LogoffTimestamp) of
        /// banked bonus. Losing the step leaves a PRIOR session's timestamp in place and inflates that grant.
        /// </summary>
        [TestMethod]
        public void PlayerManager_FinalLogoffReplay_RunsFinalizeLogoutsStepsInOrder()
        {
            var path = Path.Combine(RepoRoot(), "Source", "ACE.Server", "Managers", "PlayerManager.cs");
            var code = StripComments(File.ReadAllText(path));

            var iCatch = code.IndexOf("ForceLogoff threw for", StringComparison.Ordinal);
            Assert.IsTrue(iCatch >= 0, "could not find the final-logoff fallback catch in PlayerManager.cs; this guard needs re-pointing");

            var iRemove = code.IndexOf("first.CurrentLandblock?.RemoveWorldObject(", iCatch, StringComparison.Ordinal);
            var iStamp = code.IndexOf("first.SetPropertiesAtLogOut()", iCatch, StringComparison.Ordinal);
            var iSave = code.IndexOf("first.SavePlayerToDatabase()", iCatch, StringComparison.Ordinal);
            var iSwitch = code.IndexOf("SwitchPlayerFromOnlineToOffline(first)", iCatch, StringComparison.Ordinal);
            var iRelease = code.IndexOf("first.ReleaseAccountBank()", iCatch, StringComparison.Ordinal);

            Assert.IsTrue(iRemove >= 0, "the replay must remove the player's WorldObject from its landblock, or it keeps ticking against a terminated Session");
            Assert.IsTrue(iStamp >= 0, "the replay must stamp the logout properties, or LogoffTimestamp stays at the previous logout and inflates the next login's offline bonus");
            Assert.IsTrue(iSave >= 0, "the replay must persist the character, or its final state is lost");
            Assert.IsTrue(iSwitch >= 0, "the replay must switch the character offline, or it stays online until restart and blocks shutdown");
            Assert.IsTrue(iRelease >= 0, "the replay must release the account bank cache entry, which has no TTL and is evicted nowhere else");

            Assert.IsTrue(iRemove < iStamp, "landblock removal comes before the logout stamp, matching FinalizeLogout");
            Assert.IsTrue(iStamp < iSave, "the logout stamp must come BEFORE the save, or the advanced LogoffTimestamp is never persisted");
            Assert.IsTrue(iSave < iSwitch, "the save comes before the online/offline switch, matching FinalizeLogout");
            Assert.IsTrue(iSwitch < iRelease, "ReleaseAccountBank must come LAST: it consults the account's online snapshot and declines to release while this character still counts as online");
        }

        /// <summary>Walks up from the test assembly to the repo root. Mirrors MuleSummonTests.RepoRoot.</summary>
        private static string RepoRoot()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);

            while (dir != null && !Directory.Exists(Path.Combine(dir.FullName, "Source", "ACE.Server")))
                dir = dir.Parent;

            Assert.IsNotNull(dir, $"Could not find Source/ACE.Server by walking up from {AppContext.BaseDirectory}");

            return dir.FullName;
        }

        /// <summary>Removes block and line comments, leaving string literals alone. Mirrors MuleSummonTests.StripComments.</summary>
        private static string StripComments(string source)
        {
            var withoutBlocks = System.Text.RegularExpressions.Regex.Replace(source, @"/\*.*?\*/", string.Empty, System.Text.RegularExpressions.RegexOptions.Singleline);

            var result = new System.Text.StringBuilder();

            foreach (var line in withoutBlocks.Split('\n'))
            {
                result.Append(StripLineComment(line));
                result.Append('\n');
            }

            return result.ToString();
        }

        private static string StripLineComment(string line)
        {
            var inString = false;

            for (var i = 0; i < line.Length; i++)
            {
                if (line[i] == '"' && (i == 0 || line[i - 1] != '\\'))
                    inString = !inString;
                else if (!inString && i + 1 < line.Length && line[i] == '/' && line[i + 1] == '/')
                    return line.Substring(0, i);
            }

            return line;
        }

        #endregion
    }
}
