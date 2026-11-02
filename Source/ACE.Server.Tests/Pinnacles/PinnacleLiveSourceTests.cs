using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

using ACE.Server.Managers;
using ACE.Server.Pinnacles;
using ACE.Server.Tests.ThreadDungeons;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ACE.Server.Tests.Pinnacles
{
    /// <summary>
    /// Source scans for the live adapter (it needs a live landblock, so the ordering and marshalling rules are pinned
    /// in text), plus the settings checks. Reads no PropertyManager value: every key read here is a default-dictionary
    /// read or a fallback.
    /// </summary>
    [TestClass]
    public class PinnacleLiveSourceTests
    {
        private static string Src(string name) => PooledLootSourceText.Read("Source/ACE.Server/Pinnacles/" + name);

        private static string SrcWorldObjects(string name) => PooledLootSourceText.Read("Source/ACE.Server/WorldObjects/" + name);

        private static int Count(string text, string token) => (text.Length - text.Replace(token, string.Empty).Length) / token.Length;

        // ---- entry ----

        [TestMethod]
        public void TryEnter_PassesRealmZero_AndClosedToFellowship()
        {
            var src = Src("PinnacleEncounter.cs");

            StringAssert.Contains(src, "realmId: 0, openToFellowship: false");
            StringAssert.Contains(src, "public bool SeamChargesEntry => false;");

            var create = PooledLootSourceText.MethodBody(src, "private bool CreateCopy(");
            var validate = create.IndexOf("ValidateInstanceDestination", StringComparison.Ordinal);
            var destroy = create.IndexOf("AddToDestructionQueue", StringComparison.Ordinal);
            var adjust = create.IndexOf("AdjustDungeon", StringComparison.Ordinal);
            var add = create.IndexOf("PinnacleFightManager.Add(", StringComparison.Ordinal);
            var teleport = create.IndexOf("Teleport(player", StringComparison.Ordinal);

            Assert.IsTrue(destroy >= 0 && validate >= 0 && validate < adjust && adjust < add && add < teleport, "validate, adjust, register, then teleport");
            StringAssert.Contains(create, "knotLevel: knotLevel, seed:");
        }

        [TestMethod]
        public void Resume_AdmitsStaleOwnerByGuid()
        {
            var body = PooledLootSourceText.MethodBody(Src("PinnacleEncounter.cs"), "private static bool Reenter(");

            var accepts = body.IndexOf("realm.Accepts(player)", StringComparison.Ordinal);
            var guid = body.IndexOf("lf.Fight.OwnerGuid != player.Guid.Full", StringComparison.Ordinal);
            var admit = body.IndexOf("realm.Admit(player)", StringComparison.Ordinal);
            var teleport = body.IndexOf("Teleport(player", StringComparison.Ordinal);
            var arm = body.IndexOf("lf.Fight.ArmResume()", StringComparison.Ordinal);

            Assert.IsTrue(accepts >= 0 && accepts < guid && guid < admit && admit < teleport, "admit before the teleport");

            // F2: armed inside the teleport follow-up (arrival), never before it: a hold on the next tick would clear it.
            Assert.IsTrue(arm > teleport, "ArmResume sits inside the arrival delegate passed to Teleport");
            Assert.AreEqual(1, (body.Length - body.Replace("ArmResume", string.Empty).Length) / "ArmResume".Length, "the only ArmResume is the one in the delegate");

            var enc = Src("PinnacleEncounter.cs");
            var tele = PooledLootSourceText.MethodBody(enc, "private static void Teleport(");
            var delegateAt = tele.IndexOf("new ActionEventDelegate(", StringComparison.Ordinal);
            StringAssert.Contains(tele, "onArrival?.Invoke()");
            Assert.IsTrue(delegateAt >= 0 && tele.IndexOf("onArrival?.Invoke()", StringComparison.Ordinal) > delegateAt, "the follow-up runs inside the teleport delegate");
        }

        [TestMethod]
        public void ResumableAttempt_IsASnapshotRead_ThatNeverThrows()
        {
            var body = PooledLootSourceText.MethodBody(Src("PinnacleFightManager.cs"), "internal static KnotResume ResumableAttempt(");

            StringAssert.Contains(body, "catch (Exception)");
            StringAssert.Contains(body, "lf.Fight.KnotGuid != knotGuid");
            StringAssert.Contains(body, "lf.IsCopyLive");
            StringAssert.Contains(body, "ResumeKind(lf.Fight.State, lf.Fight.AwaitingDelivery, lf.Fight.Collecting)");
            Assert.IsFalse(body.Contains("Tick("), "no side effects");
        }

        [TestMethod]
        public void ResumeHeld_SpendsAnEntryAfterTheReentry_ReturnToCollectIsFree()
        {
            // R1: every return into a held fight spends one entry; C1: a return to collect is free
            var body = PooledLootSourceText.MethodBody(Src("PinnacleEncounter.cs"), "public bool TryEnter(");

            var resume = body.IndexOf("case PinnacleEntryAction.ResumeHeld:", StringComparison.Ordinal);
            var collect = body.IndexOf("case PinnacleEntryAction.ReturnToCollect:", StringComparison.Ordinal);
            var reenter = body.IndexOf("Reenter(player, lf, lf.Fight.Script.Data.FightEntryPoint, arm: true, out refusal)", resume, StringComparison.Ordinal);
            var spend = body.IndexOf("KnotEncounterEvents.SpendEntry(player, knot.Guid.Full)", resume, StringComparison.Ordinal);

            Assert.IsTrue(resume >= 0 && collect > resume, "both cases present, ResumeHeld first");
            Assert.IsTrue(reenter > resume && spend > reenter && spend < collect, "the paid return spends after a successful re-entry");
            Assert.IsFalse(body.Substring(collect).Contains("SpendEntry"), "the return to collect spends nothing");
        }

        [TestMethod]
        public void CreateCopy_RefusesWhenTheHeadOrCacheWeenieIsMissing()
        {
            // C3: checked before anything is created or registered
            var copy = PooledLootSourceText.MethodBody(Src("PinnacleEncounter.cs"), "private bool CreateCopy(");
            var check = copy.IndexOf("FirstMissingWeenie(data.HeadWcid, wcid => DatabaseManager.World.GetCachedWeenie(wcid) != null, data.RequiredWcids)", StringComparison.Ordinal);
            var refuse = copy.IndexOf("refusal = NotReadyLine;", check, StringComparison.Ordinal);
            var add = copy.IndexOf("PinnacleFightManager.Add(", StringComparison.Ordinal);

            Assert.IsTrue(check >= 0 && refuse > check && add > refuse, "the readiness refusal precedes the registration");
        }

        [TestMethod]
        public void FirstMissingWeenie_HeadThenCache()
        {
            const uint head = 4242u;
            var cache = ACE.Server.Knots.KnotItems.PinnacleCacheWcid;

            Assert.AreEqual(0u, PinnacleEncounter.FirstMissingWeenie(head, _ => true));
            Assert.AreEqual(head, PinnacleEncounter.FirstMissingWeenie(head, _ => false));
            Assert.AreEqual(cache, PinnacleEncounter.FirstMissingWeenie(head, w => w == head));
            Assert.AreEqual(head, PinnacleEncounter.FirstMissingWeenie(head, w => w == cache));
            Assert.AreEqual(0u, PinnacleEncounter.FirstMissingWeenie(0u, w => w == cache), "no head wcid: only the cache is checked");
            Assert.AreEqual(1007005u, PinnacleEncounter.FirstMissingWeenie(0u, w => w == ACE.Server.Knots.KnotItems.KnottedThreadCacheWcid), "the Threads Knotted cache does not stand in for the Pinnacle Cache");
        }

        [TestMethod]
        public void Spawn_ScalesThenOverlaysThenStripsKillRewardsLast_BeforeEnterWorld()
        {
            // R2 and E2: scale (with the tier) -> immune overlay -> StripKillRewards LAST -> EnterWorld
            var body = PooledLootSourceText.MethodBody(Src("LivePinnacleWorld.cs"), "public PinnacleHandle Spawn(");

            var scale = body.IndexOf("PinnacleScaling.Apply(creature, knotLevel, tier)", StringComparison.Ordinal);
            var overlay = body.IndexOf("ApplyMonsterEffectOverlay(ImmuneRecord)", StringComparison.Ordinal);
            var strip = body.IndexOf("StripKillRewards(creature)", StringComparison.Ordinal);
            var enter = body.IndexOf("wo.EnterWorld()", StringComparison.Ordinal);

            Assert.IsTrue(scale >= 0 && overlay > scale && strip > overlay && enter > strip, $"order scale {scale} overlay {overlay} strip {strip} enter {enter}");

            // exactly one of each, so the order above is the only order
            Assert.AreEqual(1, Count(body, "PinnacleScaling.Apply("));
            Assert.AreEqual(1, Count(body, "ApplyMonsterEffectOverlay("));
            Assert.AreEqual(1, Count(body, "StripKillRewards("));

            // nothing writes the creature between the strip and EnterWorld but its Location
            var between = body.Substring(strip, enter - strip);
            Assert.IsFalse(between.Contains("creature."), "no creature write after StripKillRewards");

            // no Threads-only pieces on the Pinnacle path (E2): trait strip, XP pricing, name prefixes
            var scaling = Src("PinnacleScaling.cs");
            Assert.IsFalse(scaling.Contains("StripCombatTraits"));
            Assert.IsFalse(scaling.Contains("XpFor("));
            Assert.IsFalse(scaling.Contains("LuminanceFor("));
            Assert.IsFalse(scaling.Contains("XpOverride"));
            Assert.IsFalse(scaling.Contains("creature.Name"));

            var strips = PooledLootSourceText.MethodBody(Src("LivePinnacleWorld.cs"), "internal static void StripKillRewards(");
            StringAssert.Contains(strips, "creature.XpOverride = 0;");
            StringAssert.Contains(strips, "creature.LuminanceAward = null;");

            // the death-XP path pays exactly these two values
            var death = PooledLootSourceText.Read("Source/ACE.Server/WorldObjects/Creature_Death.cs");
            StringAssert.Contains(death, "XpOverride");
            StringAssert.Contains(death, "LuminanceAward");
        }

        [TestMethod]
        public void SetImmune_PlaysTheGreyShieldCueOnARealToggle()
        {
            // C10
            var body = PooledLootSourceText.MethodBody(Src("LivePinnacleWorld.cs"), "public void SetImmune(");

            var changed = body.IndexOf("var changed = c.P_DigsiteImmune != immune;", StringComparison.Ordinal);
            var play = body.IndexOf("MlDigsiteProps.PlayScriptOn(c, immune ? PlayScript.ShieldUpGrey : PlayScript.ShieldDownGrey)", StringComparison.Ordinal);

            Assert.IsTrue(changed >= 0 && play > changed);
            StringAssert.Contains(body, "if (changed)");
        }

        [TestMethod]
        public void Add_EndsAnUnendedOldFightWhoseCopyIsGone_BeforeReplacingIt()
        {
            // C15: once replaced in the registry the reaper can no longer find it, so it is ticked (and ends Lost) first
            var body = PooledLootSourceText.MethodBody(Src("PinnacleFightManager.cs"), "internal static void Add(");

            var check = body.IndexOf("!old.Fight.IsEnded && !old.IsCopyLive", StringComparison.Ordinal);
            var tick = body.IndexOf("WorldManager.EnqueueAction(new ActionEventDelegate(() => old.Fight.Tick(Now())))", StringComparison.Ordinal);
            var replace = body.IndexOf("byOwner[lf.Fight.OwnerGuid] = lf;", StringComparison.Ordinal);

            Assert.IsTrue(check >= 0 && tick > check && replace > tick, "the old fight is queued to end before the registry slot is replaced");
        }

        [TestMethod]
        public void PinnacleHost_IsPlayerFacing()
        {
            // C12: the boss fight's hazard runs say no "[hazard #N]" developer text
            StringAssert.Contains(Src("LivePinnacleWorld.cs"), "public bool PlayerFacing => true;");
        }

        private static bool Offline(uint ownerGuid) => false;

        [TestMethod]
        public void DeferIfOffline_RecordsOnlyForAnOfflineOwnerWithADeferrableEvent()
        {
            ACE.Server.Knots.KnotPendingEvents.ClearForTest();

            try
            {
                Assert.IsFalse(LivePinnacleWorld.DeferIfOffline(true, 7u, 70u, ACE.Server.Knots.KnotPendingEvents.Kind.Refund), "online: run it now");
                Assert.IsFalse(LivePinnacleWorld.DeferIfOffline(false, 7u, 70u, null, Offline), "offline, nothing deferrable (a charge): skipped");
                Assert.AreEqual(0, ACE.Server.Knots.KnotPendingEvents.Peek(7u).Count);

                Assert.IsTrue(LivePinnacleWorld.DeferIfOffline(false, 7u, 70u, ACE.Server.Knots.KnotPendingEvents.Kind.Refund, Offline));
                Assert.IsTrue(LivePinnacleWorld.DeferIfOffline(false, 7u, 71u, ACE.Server.Knots.KnotPendingEvents.Kind.AttemptLost, Offline));
                Assert.IsTrue(LivePinnacleWorld.DeferIfOffline(false, 7u, 72u, ACE.Server.Knots.KnotPendingEvents.Kind.BossKilled, Offline), "a win is never dropped: the kill consumes the Knot on return");
                Assert.IsFalse(LivePinnacleWorld.DeferIfOffline(true, 7u, 72u, ACE.Server.Knots.KnotPendingEvents.Kind.BossKilled));

                CollectionAssert.AreEqual(new[] { (70u, ACE.Server.Knots.KnotPendingEvents.Kind.Refund), (71u, ACE.Server.Knots.KnotPendingEvents.Kind.AttemptLost), (72u, ACE.Server.Knots.KnotPendingEvents.Kind.BossKilled) },
                    ACE.Server.Knots.KnotPendingEvents.Peek(7u).ToArray());
            }
            finally
            {
                ACE.Server.Knots.KnotPendingEvents.ClearForTest();
            }
        }

        [TestMethod]
        public void DeferIfOffline_OwnerOnlineByTheTimeItIsRecorded_TakesAndDeliversNow()
        {
            // review F1: the owner logged in between the offline read and the Record, after their login check found nothing
            ACE.Server.Knots.KnotPendingEvents.ClearForTest();

            try
            {
                ACE.Server.Knots.KnotPendingEvents.Record(8u, 80u, ACE.Server.Knots.KnotPendingEvents.Kind.Refund);

                var delivered = new List<(uint Owner, (uint KnotGuid, ACE.Server.Knots.KnotPendingEvents.Kind Kind)[] Due)>();
                var looked = new List<uint>();

                Assert.IsTrue(LivePinnacleWorld.DeferIfOffline(false, 8u, 81u, ACE.Server.Knots.KnotPendingEvents.Kind.BossKilled,
                    g => { looked.Add(g); return true; }, (g, due) => delivered.Add((g, due.ToArray()))), "still recorded first: true");

                CollectionAssert.AreEqual(new[] { 8u }, looked, "re-checked after recording, for this owner");
                Assert.AreEqual(1, delivered.Count, "delivered once");
                Assert.AreEqual(8u, delivered[0].Owner);
                CollectionAssert.AreEqual(new[] { (80u, ACE.Server.Knots.KnotPendingEvents.Kind.Refund), (81u, ACE.Server.Knots.KnotPendingEvents.Kind.BossKilled) },
                    delivered[0].Due, "everything pending for the owner, in order, including the one just recorded");
                Assert.AreEqual(0, ACE.Server.Knots.KnotPendingEvents.Peek(8u).Count, "taken, so a later login check cannot apply them twice");

                Assert.IsTrue(LivePinnacleWorld.DeferIfOffline(false, 8u, 82u, ACE.Server.Knots.KnotPendingEvents.Kind.AttemptLost,
                    _ => false, (g, due) => delivered.Add((g, due.ToArray()))));
                Assert.AreEqual(1, delivered.Count, "still offline: nothing delivered");
                Assert.AreEqual(1, ACE.Server.Knots.KnotPendingEvents.Peek(8u).Count, "still offline: left for the login check");
            }
            finally
            {
                ACE.Server.Knots.KnotPendingEvents.ClearForTest();
            }
        }

        [TestMethod]
        public void CreateCopy_RefusesAZeroEntryKnot_TheFreeReturnRace()
        {
            // review item 2: the seam read a Free return against a fight that ended before TryEnter
            var body = PooledLootSourceText.MethodBody(Src("PinnacleEncounter.cs"), "public bool TryEnter(");

            var branch = body.IndexOf("case PinnacleEntryAction.CreateCopy:", StringComparison.Ordinal);
            var check = body.IndexOf("if (NoEntriesLeft(knot.GetProperty(PropertyInt.Structure)))", branch, StringComparison.Ordinal);
            var refusal = body.IndexOf("refusal = KnotItems.KnotHasNoEntries;", check, StringComparison.Ordinal);
            var reset = body.IndexOf("lf.Fight.Reset(", branch, StringComparison.Ordinal);
            var create = body.IndexOf("return CreateCopy(", branch, StringComparison.Ordinal);

            Assert.IsTrue(branch >= 0 && check > branch && refusal > check && reset > refusal && create > reset, "refused before anything is reset or created");

            Assert.IsTrue(PinnacleEncounter.NoEntriesLeft(0));
            Assert.IsTrue(PinnacleEncounter.NoEntriesLeft(-1));
            Assert.IsFalse(PinnacleEncounter.NoEntriesLeft(1));
            Assert.IsFalse(PinnacleEncounter.NoEntriesLeft(null), "an unset Structure is the full definition count");
        }

        [TestMethod]
        public void HeadTag_LivesInPinnacleTypes()
        {
            // C15: the tag the core reports and the scripts match is a type-level constant, not a PinnacleFight member
            Assert.AreEqual("head", PinnacleEvent.HeadTag);
            Assert.IsFalse(Src("PinnacleFight.cs").Contains("const string HeadTag"));
            StringAssert.Contains(Src("PinnacleTypes.cs"), "public const string HeadTag = \"head\";");
        }

        [TestMethod]
        public void LoginLine_PinnacleFallbackSitsBeforeTheExpiredLine()
        {
            // C16
            var wm = PooledLootSourceText.Read("Source/ACE.Server/Managers/WorldManager.cs");
            var pin = wm.IndexOf("ACE.Server.Pinnacles.PinnacleFightManager.GetStillOpenLoginMessage(session.Player, savedInstance)", StringComparison.Ordinal);
            var expired = wm.IndexOf("\"The instance you were in has expired and you have been transported outside!\"", StringComparison.Ordinal);

            Assert.IsTrue(pin >= 0 && expired > pin);
        }

        // ---- live world ----

        [TestMethod]
        public void Spawn_AttachesImmuneRecordAndScalesBeforeEnterWorld()
        {
            var body = PooledLootSourceText.MethodBody(Src("LivePinnacleWorld.cs"), "public PinnacleHandle Spawn(");

            var scale = body.IndexOf("PinnacleScaling.Apply(creature, knotLevel, tier)", StringComparison.Ordinal);
            var overlay = body.IndexOf("ApplyMonsterEffectOverlay(ImmuneRecord)", StringComparison.Ordinal);
            var enter = body.IndexOf("wo.EnterWorld()", StringComparison.Ordinal);

            Assert.IsTrue(scale >= 0 && scale < enter && overlay >= 0 && overlay < enter);
            Assert.AreEqual("immune", LivePinnacleWorld.ImmuneRecord);
        }

        [TestMethod]
        public void HeadFaceYawOffset_FlowsFromTheScriptDataIntoFaceAndNod()
        {
            // review 2026-10-20: the offset must reach the turn, the nod's sign, and the live world's constructor
            var live = Src("LivePinnacleWorld.cs");
            var face = PooledLootSourceText.MethodBody(live, "public void FaceOwner(");
            var bob = PooledLootSourceText.MethodBody(live, "public void Bob(");

            var toward = face.IndexOf("headTurns.BeginTurn(", StringComparison.Ordinal);
            var close = face.IndexOf(");", toward, StringComparison.Ordinal);
            Assert.IsTrue(toward >= 0 && close > toward);
            StringAssert.Contains(face.Substring(toward, close - toward), "headFaceYawOffset", "the turn plan gets the offset");

            // and the plan hands it to both the snap and the client turn
            var begin = PooledLootSourceText.MethodBody(Src("PinnacleHeadTurns.cs"), "public TurnPlan BeginTurn(");
            StringAssert.Contains(begin, "PinnacleFacing.FaceToward(spawn, from, to, faceYawOffset)");
            StringAssert.Contains(begin, "PinnacleFacing.LocalTurnToward(current, from, to, faceYawOffset)");

            StringAssert.Contains(bob, "var pitchSign = NodPitchSign(headFaceYawOffset);");
            StringAssert.Contains(bob, "pitch * pitchSign", "every nod step is multiplied by the sign");

            StringAssert.Contains(live, "this.headFaceYawOffset = headFaceYawOffset;");

            var encounter = PooledLootSourceText.MethodBody(Src("PinnacleEncounter.cs"), "private bool CreateCopy(");
            var ctor = encounter.IndexOf("new LivePinnacleWorld(", StringComparison.Ordinal);
            var end = encounter.IndexOf(");", ctor, StringComparison.Ordinal);
            Assert.IsTrue(ctor >= 0 && end > ctor);
            StringAssert.Contains(encounter.Substring(ctor, end - ctor), "data.HeadFaceYawOffset", "PinnacleEncounter passes the script data's offset");

            Assert.AreEqual(-1f, LivePinnacleWorld.NodPitchSign((float)Math.PI));
            Assert.AreEqual(1f, LivePinnacleWorld.NodPitchSign(0f));
        }

        [TestMethod]
        public void FaceOwner_UsesFaceToward_NotAnAbsoluteYawCompose()
        {
            var body = PooledLootSourceText.MethodBody(Src("LivePinnacleWorld.cs"), "public void FaceOwner(");
            var begin = PooledLootSourceText.MethodBody(Src("PinnacleHeadTurns.cs"), "public TurnPlan BeginTurn(");

            // the snap fallback (a head with no motion-table turn): FaceToward in the plan, a teleport-sequenced update here
            StringAssert.Contains(begin, "PinnacleFacing.FaceToward(");
            Assert.IsFalse(begin.Contains("ComposeFacing("));
            Assert.IsFalse(begin.Contains("YawToward("));
            Assert.IsFalse(body.Contains("ComposeFacing("));
            Assert.IsFalse(body.Contains("YawToward("));
            StringAssert.Contains(body, "ApplyRotation(wo, q)");
            // round 3: a plain update of the floating head is dropped by the client (PinnacleArenaGeometryTests.HeadTurn_*)
            var apply = PooledLootSourceText.MethodBody(Src("LivePinnacleWorld.cs"), "private static void ApplyRotation(");
            StringAssert.Contains(apply, "SendUpdatePosition(adminMove: true)");
            Assert.IsFalse(apply.Contains("SendUpdatePosition()"), "a facing update without a new teleport sequence never reaches the client");
        }

        /// <summary>
        /// 2026-10-20 owner ruling: the head with a motion-table turn is turned by the CLIENT (a TurnRight UpdateMotion,
        /// stopped by a Ready one), booked where the client lands, and the nod waits for the turn. The motion path itself
        /// is proven on the dats by PinnacleArenaGeometryTests.HeadTurn_TheClientAnimatesTheMotionTurn_*, and the
        /// bookkeeping by PinnacleFacingTests.HeadTurns_*; this pins the live world's wiring of them.
        /// Review 2026-10-20: F1 the server rotation is written BEFORE the start motion (a re-send mid-turn carries the
        /// booked rotation); F2 a dead head is not turned or nodded; F3 a superseded nod's rest frame is restored first.
        /// </summary>
        [TestMethod]
        public void FaceOwner_WithAMotionTableTurn_LetsTheClientAnimateIt_AndBobWaitsForIt()
        {
            var live = Src("LivePinnacleWorld.cs");
            var face = PooledLootSourceText.MethodBody(live, "public void FaceOwner(");

            var dead = face.IndexOf("!IsAliveHead(wo)", StringComparison.Ordinal);
            var plan = face.IndexOf("headTurns.BeginTurn(h.Id, spawn, IsAliveHead(wo), from, to, headFaceYawOffset, omegaZ, framerate, HeadTurnSpeed)", StringComparison.Ordinal);
            var snap = face.IndexOf("if (plan.Kind == PinnacleHeadTurns.TurnKind.Snap)", StringComparison.Ordinal);
            var restore = face.IndexOf("ApplyRotation(wo, plan.RestoreRest.Value);", StringComparison.Ordinal);
            var hold = face.IndexOf("if (plan.Kind == PinnacleHeadTurns.TurnKind.Hold)", StringComparison.Ordinal);
            var book = face.IndexOf("SetServerRotation(wo, target);", StringComparison.Ordinal);
            var start = face.IndexOf("start.SetTurnCommand(MotionCommand.TurnRight, plan.CommandSpeed);", StringComparison.Ordinal);
            var send = face.IndexOf("wo.EnqueueBroadcastMotion(start);", StringComparison.Ordinal);
            var wait = face.IndexOf("chain.AddDelaySeconds(plan.Seconds);", StringComparison.Ordinal);
            var take = face.IndexOf("headTurns.TakeStop(h.Id, generation, IsAliveHead(wo))", StringComparison.Ordinal);
            var stop = face.IndexOf("StopTurn(wo, target);", StringComparison.Ordinal);

            Assert.IsTrue(dead >= 0 && plan > dead && snap > plan && restore > snap && hold > restore && book > hold && start > book && send > start && wait > send && take > wait && stop > take,
                $"dead {dead} plan {plan} snap {snap} restore {restore} hold {hold} book {book} start {start} send {send} wait {wait} take {take} stop {stop}");
            Assert.IsTrue(dead < face.IndexOf("return;", StringComparison.Ordinal), "F2: a dead head returns before anything is planned");
            Assert.IsFalse(face.Substring(hold).Contains("ApplyRotation("), "the animated path never teleports the head (only the restore of a cut-off nod does)");

            var stopBody = PooledLootSourceText.MethodBody(live, "private static void StopTurn(");
            StringAssert.Contains(stopBody, "wo.EnqueueBroadcastMotion(new Motion(wo, MotionCommand.Ready));");
            StringAssert.Contains(stopBody, "SetServerRotation(wo, target);");
            Assert.IsFalse(PooledLootSourceText.MethodBody(live, "private static void SetServerRotation(").Contains("SendUpdatePosition"), "the booking sends no position");

            StringAssert.Contains(live, "private static bool IsAliveHead(WorldObject wo) => !(wo is Creature c && c.IsDead);");

            var bob = PooledLootSourceText.MethodBody(live, "public void Bob(");
            var nodPlan = bob.IndexOf("headTurns.BeginNod(h.Id, spawn, IsAliveHead(wo))", StringComparison.Ordinal);
            var refused = bob.IndexOf("plan.Refused", StringComparison.Ordinal);
            var turnWait = bob.IndexOf("chain.AddDelaySeconds(plan.DelaySeconds);", StringComparison.Ordinal);
            var firstStep = bob.IndexOf("for (var i = 0; i < BobSteps.Length; i++)", StringComparison.Ordinal);
            Assert.IsTrue(nodPlan >= 0 && refused > nodPlan && turnWait > refused && firstStep > turnWait, "the nod is planned, refused for a dead head, and waits for the turn before its first frame");
            StringAssert.Contains(bob, "headTurns.TakeNodFrame(h.Id, generation, IsAliveHead(wo), isRest)", "a stale nod frame, or one due on a dead head, is skipped");
        }

        [TestMethod]
        public void HeadCastHeight_FlowsFromTheScriptDataIntoTheCast()
        {
            var live = Src("LivePinnacleWorld.cs");
            var cast = PooledLootSourceText.MethodBody(live, "public void Cast(");

            var set = cast.IndexOf("wo.ProjectileCastHeight = headCastHeight;", StringComparison.Ordinal);
            var fire = cast.IndexOf("wo.TryCastSpell(", StringComparison.Ordinal);
            Assert.IsTrue(set >= 0 && fire > set, "the cast height is on the caster before the spell is cast");
            StringAssert.Contains(live, "this.headCastHeight = headCastHeight;");

            var encounter = PooledLootSourceText.MethodBody(Src("PinnacleEncounter.cs"), "private bool CreateCopy(");
            var ctor = encounter.IndexOf("new LivePinnacleWorld(", StringComparison.Ordinal);
            var end = encounter.IndexOf(");", ctor, StringComparison.Ordinal);
            StringAssert.Contains(encounter.Substring(ctor, end - ctor), "data.HeadCastHeight", "PinnacleEncounter passes the script data's cast height");

            Assert.AreEqual(ACE.Server.Pinnacles.Grael.GraelData.HeadCastHeight, ACE.Server.Pinnacles.Grael.GraelData.Script().HeadCastHeight);
        }

        [TestMethod]
        public void EveryEncounterCreature_IsSpawnedCorpseless_UnlessItOptsIn_AndDieSkipsItsCorpse()
        {
            // round 4 ruling: the head, the Grael body and every variant (no loot, 0 XP; the reward is the cache). The one
            // opt-out is the Spawn parameter, which only PinnacleFight's head spawn passes (HeadLeavesCorpse).
            var live = Src("LivePinnacleWorld.cs");
            var spawn = PooledLootSourceText.MethodBody(live, "public PinnacleHandle Spawn(");

            var creatureBlock = spawn.IndexOf("if (wo is Creature creature)", StringComparison.Ordinal);
            var flag = spawn.IndexOf("creature.PinnacleCorpseless = !leavesCorpse;", StringComparison.Ordinal);
            var strip = spawn.IndexOf("StripKillRewards(creature);", StringComparison.Ordinal);
            var enter = spawn.IndexOf("wo.EnterWorld()", StringComparison.Ordinal);
            Assert.IsTrue(creatureBlock >= 0 && flag > creatureBlock && strip > flag && enter > strip, "every creature is flagged, before the strip and EnterWorld");
            Assert.AreEqual(1, Count(spawn, "PinnacleCorpseless"));

            // the opt-in also clears the weenie's NoCorpse, or CreateCorpse returns before any corpse exists (the Matron's
            // weenie sets it); only the head spawn in PinnacleFight passes the opt-in
            StringAssert.Contains(spawn.Replace("\r\n", "\n"), "if (leavesCorpse)\n                    {\n                        creature.NoCorpse = false;\n                        creature.PinnacleCorpseKept = true;\n                    }");

            // the kept corpse never rots: an empty corpse otherwise goes in 15 s (WorldObject_Decay)
            var createCorpse = PooledLootSourceText.MethodBody(SrcWorldObjects("Creature_Death.cs"), "protected void CreateCorpse(");
            var kept = createCorpse.IndexOf("if (PinnacleCorpseKept)", StringComparison.Ordinal);
            Assert.IsTrue(kept >= 0 && kept < createCorpse.IndexOf("corpse.EnterWorld();", StringComparison.Ordinal), "set before the corpse enters the world");
            StringAssert.Contains(createCorpse.Substring(kept, 80), "corpse.TimeToRot = -1;");
            Assert.AreEqual(1, Count(spawn, "NoCorpse ="), "NoCorpse is only ever written once: cleared, under the opt-in");
            var fight = Src("PinnacleFight.cs");
            Assert.AreEqual(1, Count(fight, "HeadLeavesCorpse"), "only the head spawn passes the opt-in");
            Assert.AreEqual(0, Count(fight, "leavesCorpse"), "no other spawn names the parameter");

            // unconditional inside the creature block: the line before the flag is a comment or the overlay, never a guard
            var lineStart = spawn.LastIndexOf('\n', flag - 1);
            var prevLineStart = spawn.LastIndexOf('\n', lineStart - 1);
            Assert.IsFalse(spawn.Substring(prevLineStart, lineStart - prevLineStart).TrimStart().StartsWith("if ", StringComparison.Ordinal), "no wcid guard on the flag");
            Assert.IsFalse(live.Contains("headWcid"), "the head-only switch is gone");

            var die = PooledLootSourceText.MethodBody(SrcWorldObjects("Creature_Death.cs"), "protected virtual void Die(DamageHistoryInfo lastDamager, DamageHistoryInfo topDamager)");
            var local = die.IndexOf("var suppressCorpse = ", StringComparison.Ordinal);
            var semi = die.IndexOf(";", local, StringComparison.Ordinal);
            Assert.IsTrue(local >= 0 && semi > local);
            StringAssert.Contains(die.Substring(local, semi - local), "IsPinnacleCorpselessDeath(PinnacleCorpseless)", "the head's flag is OR-ed into the one corpse local");

            Assert.IsTrue(ACE.Server.WorldObjects.Creature.IsPinnacleCorpselessDeath(true));
            Assert.IsFalse(ACE.Server.WorldObjects.Creature.IsPinnacleCorpselessDeath(false));
        }

        [TestMethod]
        public void MatronDrainIrisWeenie_IsTheUsableDoor_AnOlthoiDoorMinusItsFrame()
        {
            // the geometry tests build the drain body from constants; this pins the shipped row they copy (the use target)
            var sql = PooledLootSourceText.Read("Content/sql/weenies/1007557 Matron Drain Iris.sql");

            StringAssert.Contains(sql, "(1007557, 'ace1007557-matrondrainiris', 19,", "a Door: Door.ActOnUse is the prop-use hook");
            StringAssert.Contains(sql, "(1007557,   1, 0x020005F2) /* Setup", "the Olthoi door's setup");
            StringAssert.Contains(sql, "(1007557,   2, 0x09000087) /* MotionTable", "retail Olthoi door 4451's motion table");
            StringAssert.Contains(sql, "(1007557, 0, 0x010001EC)", "part 0 (a frame pillar) swapped for the null part");
            StringAssert.Contains(sql, "(1007557, 1, 0x010001EC)", "part 1 (the other pillar)");
            StringAssert.Contains(sql, "(1007557, 2, 0x010001EC)", "part 2 (the lintel)");
            Assert.AreEqual(3, Count(sql, "0x010001EC) /*"), "exactly the three frame parts are swapped; the petals (3 to 12) stay");
            StringAssert.Contains(sql, "(1007557,   1, True ) /* Stuck", "Stuck: SpawnAtExactPosition is refused without it");
            StringAssert.Contains(sql, "(1007557, 9069, True ) /* SpawnAtExactPosition");
            StringAssert.Contains(sql, "(1007557,  16,         32) /* ItemUseable");
            StringAssert.Contains(sql, "(1007557,  54,       2) /* UseRadius");
            StringAssert.Contains(sql, "(1007557,  93,         20) /* PhysicsState - Ethereal, IgnoreCollisions");
            StringAssert.Contains(sql, "(1007557,   1, 'Drain') /* Name");
            Assert.IsFalse(sql.Contains("/* UiHidden"), "the player selects and uses it");
        }

        [TestMethod]
        public void MatronDrainGrateWeenie_IsAnUnselectableStaticClipMapQuad()
        {
            var sql = PooledLootSourceText.Read("Content/sql/weenies/1007552 Matron Drain Grate.sql");

            StringAssert.Contains(sql, "(1007552, 'ace1007552-matrondraingrate', 1,", "a plain Generic prop, not type 19");
            StringAssert.Contains(sql, "(1007552,   1, 0x02000E15) /* Setup", "the Base1ClipMap quad");
            StringAssert.Contains(sql, "(1007552, 0, 0x0500264C, 0x05002795)", "the prison door's bars on the quad's own texture");
            StringAssert.Contains(sql, "(1007552,  24, True ) /* UiHidden", "never selectable: only the iris reads Drain");
            StringAssert.Contains(sql, "(1007552, 9069, True ) /* SpawnAtExactPosition");
            StringAssert.Contains(sql, "(1007552,  16,          0) /* ItemUseable - No");
            StringAssert.Contains(sql, "(1007552,   1, True ) /* Stuck");
            Assert.IsFalse(sql.Contains("'Drain')"), "the grate is not named Drain");
            Assert.IsFalse(sql.Contains("0x02000E6E)"), "no longer the opaque Base1Image panel setup");
            StringAssert.Contains(sql, "0x02001215", "the retail grate fallback is documented");
        }

        [TestMethod]
        public void MatronDrainFloorWeenie_IsAnUnselectableExactPlacedBlackPanel()
        {
            var sql = PooledLootSourceText.Read("Content/sql/weenies/1007553 Matron Drain Floor.sql");

            StringAssert.Contains(sql, "(1007553, 'ace1007553-matrondrainfloor', 1,");
            StringAssert.Contains(sql, "(1007553,   1, 0x02000E15) /* Setup", "the grate's ClipMap quad: its selection sphere is small enough to sit inside the iris's");
            StringAssert.Contains(sql, "(1007553, 0, 0x0500264C, 0x05002B94)", "the quad's own tile texture swapped to the all-black one");
            Assert.IsFalse(sql.Contains("0x02000E6E)"), "no longer the 10 m panel, whose 2.18 m selection sphere enclosed the iris");
            StringAssert.Contains(sql, "(1007553,  24, True ) /* UiHidden");
            StringAssert.Contains(sql, "(1007553, 9069, True ) /* SpawnAtExactPosition", "a face-up panel at a small lift otherwise comes to rest on the floor plane");
            StringAssert.Contains(sql, "(1007553,  16,          0) /* ItemUseable - No");
        }

        [TestMethod]
        public void MatronHiveSentinelWeenie_IsTheRetailSentinelAtTwiceItsScale()
        {
            var sql = PooledLootSourceText.Read("Content/sql/weenies/1007558 Matron Hive Sentinel.sql");

            StringAssert.Contains(sql, "-- @owns: wcid:1007558");
            StringAssert.Contains(sql, "(1007558, 'ace1007558-matronhivesentinel', 10,", "class_Id 1007558, a Creature");
            StringAssert.Contains(sql, "(1007558,  39,     1.6) /* DefaultScale", "twice retail 25341's 0.8");
            StringAssert.Contains(sql, "(1007558,   1, 0x02000FB3) /* Setup", "retail 25341's Olthoi Sentinel body");
            StringAssert.Contains(sql, "(1007558,   1, 'Hive Sentinel') /* Name");
            Assert.AreEqual(1007558u, ACE.Server.Pinnacles.Matron.MatronData.SentinelWcid, "the wcid the final wave spawns");
        }

        [TestMethod]
        public void MatronFlowWeenies_ReplayAFasterScrollScript()
        {
            // PropertyDataId 9000 VisualEffectScript replays a script when a client builds the object, on top of the setup's default
            var lava = PooledLootSourceText.Read("Content/sql/weenies/1007555 Matron Lava Flow.sql");
            var acid = PooledLootSourceText.Read("Content/sql/weenies/1007556 Matron Acid Flow.sql");

            StringAssert.Contains(lava, "(1007555, 9000, 0x33000D34) /* VisualEffectScript", "0.10 UV/s (the dat's fastest) plus a looping sound, as the acid");
            StringAssert.Contains(acid, "(1007556, 9000, 0x33000D34) /* VisualEffectScript", "0.10 UV/s plus a looping sound");
            StringAssert.Contains(lava, "UNVERIFIED in the live client");
            StringAssert.Contains(acid, "UNVERIFIED in the live client");
            Assert.AreEqual(1, Count(lava, ", 9000,"), "one script row");
            Assert.AreEqual(1, Count(acid, ", 9000,"), "one script row");
        }

        [TestMethod]
        public void SetAttackable_SetsTheRadarColourBeforeTheObjectUpdate()
        {
            var body = PooledLootSourceText.MethodBody(Src("LivePinnacleWorld.cs"), "public void SetAttackable(");

            var radar = body.IndexOf("wo.RadarColor = radar;", StringComparison.Ordinal);
            var update = body.IndexOf("new GameMessageUpdateObject(wo)", StringComparison.Ordinal);
            Assert.IsTrue(radar >= 0 && update > radar, "the colour is set before the description is re-sent");

            Assert.AreEqual(ACE.Entity.Enum.RadarColor.Creature, LivePinnacleWorld.RadarFor(true, ACE.Entity.Enum.RadarColor.NPC));
            Assert.AreEqual(ACE.Entity.Enum.RadarColor.Gold, LivePinnacleWorld.RadarFor(true, null), "Creature is Gold");
            Assert.AreEqual(ACE.Entity.Enum.RadarColor.NPC, LivePinnacleWorld.RadarFor(false, ACE.Entity.Enum.RadarColor.NPC), "not attackable: the authored colour");
            Assert.IsNull(LivePinnacleWorld.RadarFor(false, null));
        }

        [TestMethod]
        public void Bob_TakesThreeUpdatesOverAboutFourTenthsAndEndsAtRest()
        {
            var steps = LivePinnacleWorld.BobSteps;

            Assert.AreEqual(3, steps.Length);
            Assert.AreEqual(0.4, steps[steps.Length - 1].DelaySeconds, 1e-9);
            Assert.AreEqual(0f, steps[steps.Length - 1].Pitch, "the last update is the rest pose");
            Assert.IsTrue(steps[0].Pitch != 0f);
        }

        [TestMethod]
        public void HazardEnded_IsMarshalledOntoTheFightLandblock()
        {
            var body = PooledLootSourceText.MethodBody(Src("LivePinnacleWorld.cs"), "void IHazardHost.OnEnded(");

            var enqueue = body.IndexOf("landblock.EnqueueAction(", StringComparison.Ordinal);
            var call = body.IndexOf("OnHazardEnded(", StringComparison.Ordinal);

            Assert.IsTrue(enqueue >= 0 && enqueue < call, "the fight is only called from inside the enqueued action");
        }

        [TestMethod]
        public void StartHazard_UsesThePresetAtTheFightInstance()
        {
            var body = PooledLootSourceText.MethodBody(Src("LivePinnacleWorld.cs"), "public int StartHazard(");

            StringAssert.Contains(body, "HazardRegionPresets.TryGet(hazardPreset, instance, out var region)");
            StringAssert.Contains(body, "HazardManager.Start(region, kind, args, this, null, pattern, landblock, out error)");
        }

        [TestMethod]
        public void KnotBookkeeping_IsMarshalledThroughTheOwner()
        {
            var src = Src("LivePinnacleWorld.cs");
            var onOwner = PooledLootSourceText.MethodBody(src, "private void OnOwner(");

            StringAssert.Contains(onOwner, "PlayerManager.GetOnlinePlayer(ownerGuid)");
            StringAssert.Contains(onOwner, "owner.EnqueueAction(");
            StringAssert.Contains(src, "OnOwner(nameof(ChargeEntry), owner => KnotEncounterEvents.SpendEntry(owner, knotGuid))");
            StringAssert.Contains(src, "OnOwner(nameof(ReportBossKilled), owner => KnotEncounterEvents.OnBossKilled(owner, knotGuid), KnotPendingEvents.Kind.BossKilled)");
            StringAssert.Contains(src, "OnOwner(nameof(ReportAttemptLost), owner => KnotEncounterEvents.OnAttemptLost(owner, knotGuid), KnotPendingEvents.Kind.AttemptLost)");
            StringAssert.Contains(src, "OnOwner(nameof(RefundEntry), owner => KnotEncounterEvents.RefundEntry(owner, knotGuid), KnotPendingEvents.Kind.Refund)");

            // an offline owner: the refund and the loss are held (KnotPendingEvents) and the defer runs before the skip
            var defer = onOwner.IndexOf("if (DeferIfOffline(owner != null, ownerGuid, knotGuid, whenOffline))", StringComparison.Ordinal);
            var skip = onOwner.IndexOf("is offline; skipped", StringComparison.Ordinal);
            Assert.IsTrue(defer >= 0 && skip > defer, "the defer runs before the offline skip");
            StringAssert.Contains(src, "OnOwner(nameof(ChargeEntry), owner => KnotEncounterEvents.SpendEntry(owner, knotGuid))");
            Assert.IsFalse(src.Contains("OnPhaseReached"), "no ReportPhaseReached: every attempt starts at phase 1");
        }

        [TestMethod]
        public void ReleaseInstance_ClearsTheHold_AndDestroysTheCopyOnlyWhenTheOwnerIsOutside()
        {
            var body = PooledLootSourceText.MethodBody(Src("LivePinnacleWorld.cs"), "public void ReleaseInstance(");

            var release = body.IndexOf("PinnacleFightManager.ReleaseHold(landblock)", StringComparison.Ordinal);
            var destroy = body.IndexOf("if (!ownerInside)", StringComparison.Ordinal);

            Assert.IsTrue(release >= 0 && release < destroy);
            StringAssert.Contains(body, "LandblockManager.AddToDestructionQueue(landblock)");
        }

        [TestMethod]
        public void StaleNotStartedFight_IsResetBeforeANewCopy_AndTheBandMemoIsWarmedAtEntry()
        {
            var body = PooledLootSourceText.MethodBody(Src("PinnacleEncounter.cs"), "public bool TryEnter(");
            var reset = body.IndexOf("lf.Fight.Reset(", StringComparison.Ordinal);
            var create = body.IndexOf("return CreateCopy(", StringComparison.Ordinal);

            Assert.IsTrue(reset >= 0 && reset < create);
            StringAssert.Contains(body, "lf.Fight.State == PinnacleFightState.NotStarted");
            StringAssert.Contains(body, "lf.Landblock.EnqueueAction(");

            var copy = PooledLootSourceText.MethodBody(Src("PinnacleEncounter.cs"), "private bool CreateCopy(");
            Assert.IsTrue(copy.IndexOf("PinnacleScaling.Warm(knotLevel)", StringComparison.Ordinal) >= 0);
        }

        [TestMethod]
        public void CachePlacementFailure_IsLoggedOncePerFight()
        {
            var body = PooledLootSourceText.MethodBody(Src("LivePinnacleWorld.cs"), "public bool TryDeliverCache(");

            StringAssert.Contains(body, "logFailure: !cachePlacementFailureLogged");
            StringAssert.Contains(body, "cachePlacementFailureLogged = true;");
            StringAssert.Contains(PooledLootSourceText.MethodBody(Src("PinnacleCache.cs"), "public static bool TryPlace("), "if (logFailure)");
        }

        /// <summary>
        /// Playtest 2026-10-22: the cache and exit stood at the owner and fell through the floor. The fixed points reach the
        /// live world and are used there; a dropped argument defaults to null and silently restores the owner placement.
        /// </summary>
        [TestMethod]
        public void FixedCacheAndExitPoints_FlowFromTheScriptDataIntoThePlacement()
        {
            var copy = PooledLootSourceText.MethodBody(Src("PinnacleEncounter.cs"), "private bool CreateCopy(");
            var ctor = copy.IndexOf("new LivePinnacleWorld(", StringComparison.Ordinal);
            var end = copy.IndexOf(");", ctor, StringComparison.Ordinal);
            Assert.IsTrue(ctor >= 0 && end > ctor);
            StringAssert.Contains(copy.Substring(ctor, end - ctor), "data.CachePoint, data.ReturnPortalPoint");

            var live = Src("LivePinnacleWorld.cs");
            StringAssert.Contains(live, "this.cachePoint = cachePoint;");
            StringAssert.Contains(live, "this.returnPortalPoint = returnPortalPoint;");
            StringAssert.Contains(PooledLootSourceText.MethodBody(live, "public bool TryDeliverCache("), "PinnacleCache.TryPlaceAt(owner, landblock, ToPosition(cachePoint.Value)");
            StringAssert.Contains(PooledLootSourceText.MethodBody(Src("PinnacleCache.cs"), "public static bool TryPlaceAt("), "if (logFailure)");

            Assert.IsNotNull(ACE.Server.Pinnacles.Grael.GraelData.Script().CachePoint, "Grael sets a fixed cache point");
            Assert.IsNotNull(ACE.Server.Pinnacles.Grael.GraelData.Script().ReturnPortalPoint, "Grael sets a fixed exit point");
        }

        // ---- cache ----

        [TestMethod]
        public void CacheFactory_RunOverloadDelegates()
        {
            var spawner = PooledLootSourceText.Read("Source/ACE.Server/ThreadDungeons/ThreadDungeonRewardSpawner.cs");

            StringAssert.Contains(spawner, "internal static Chest CreateCacheChest(ThreadDungeonRun run, string purpose, uint ownerGuid, uint wcid)\n"
                + "            => CreateCacheChest(ownerGuid, ownerGuid == run.OwnerGuid ? run.OwnerName : run.GetMember(ownerGuid)?.Name, wcid, run.RunId, $\"{run} {purpose}\");");
            StringAssert.Contains(spawner, "internal static Chest CreateCacheChest(uint ownerGuid, string ownerName, uint wcid, uint? runId, string purpose)");

            var runFree = PooledLootSourceText.MethodBody(spawner, "internal static Chest CreateCacheChest(uint ownerGuid, string ownerName, uint wcid, uint? runId, string purpose)");
            Assert.IsFalse(runFree.Contains("run."), "the run-free factory names no run");

            var cache = Src("PinnacleCache.cs");
            StringAssert.Contains(cache, "ThreadDungeonRewardSpawner.CreateCacheChest(owner.Guid.Full, owner.Name, KnotItems.PinnacleCacheWcid, null, \"pinnacle cache\")");
            StringAssert.Contains(cache, "ThreadCachePlacement.BuildCandidates(");
        }

        [TestMethod]
        public void Rewards_UnknownOrNullTable_IsEmpty()
        {
            Assert.AreEqual(0, PinnacleRewards.Roll(200, null, null).Count);
            Assert.AreEqual(0, PinnacleRewards.Roll(200, null, "nope").Count);
        }

        [TestMethod]
        public void RewardTable_FlowsFromTheDefIntoTheRoll()
        {
            var enter = PooledLootSourceText.MethodBody(Src("PinnacleEncounter.cs"), "public bool TryEnter(");
            StringAssert.Contains(enter, "CreateCopy(player, knot.Guid.Full, knot, knotLevel, def, adminEntry: false, null, out refusal)");

            var copy = PooledLootSourceText.MethodBody(Src("PinnacleEncounter.cs"), "private bool CreateCopy(");
            StringAssert.Contains(copy, "var rewardTable = def?.RewardTable;", "CreateCopy takes the def (for the telemetry key too) and reads the table off it");
            var ctor = copy.IndexOf("new LivePinnacleWorld(", StringComparison.Ordinal);
            var end = copy.IndexOf(");", ctor, StringComparison.Ordinal);
            Assert.IsTrue(ctor >= 0 && end > ctor);
            StringAssert.Contains(copy.Substring(ctor, end - ctor), "rewardTable", "the reward table reaches the live world");

            var live = Src("LivePinnacleWorld.cs");
            StringAssert.Contains(live, "this.rewardTable = rewardTable;");

            var deliver = PooledLootSourceText.MethodBody(live, "public bool TryDeliverCache(");
            StringAssert.Contains(deliver, "PinnacleRewards.Roll(knotLevel, owner, rewardTable)");
        }

        // ---- settings ----

        [TestMethod]
        public void OnlyPinnacleTunablesReadsPropertyManager()
        {
            var dir = PinnaclesDir();

            foreach (var file in Directory.GetFiles(dir, "*.cs", SearchOption.AllDirectories))
            {
                if (Path.GetFileName(file) == "PinnacleTunables.cs")
                    continue;

                Assert.IsFalse(File.ReadAllText(file).Contains("PropertyManager."), Path.GetFileName(file) + " must not read PropertyManager");
            }
        }

        [TestMethod]
        public void Settings_DefaultsMatchTunablesAndAreOn()
        {
            Assert.IsTrue(DefaultPropertyManager.DefaultBooleanProperties[PinnacleTunables.EnabledKey].Item);
            Assert.AreEqual(600L, DefaultPropertyManager.DefaultLongProperties[PinnacleTunables.HoldSecondsKey].Item);
            Assert.AreEqual(10L, DefaultPropertyManager.DefaultLongProperties[PinnacleTunables.ResumeGraceSecondsKey].Item);

            Assert.AreEqual(PinnacleTunables.DefaultEnabled, DefaultPropertyManager.DefaultBooleanProperties[PinnacleTunables.EnabledKey].Item);
            Assert.AreEqual(PinnacleTunables.DefaultHoldSeconds, DefaultPropertyManager.DefaultLongProperties[PinnacleTunables.HoldSecondsKey].Item);
            Assert.AreEqual(PinnacleTunables.DefaultResumeGraceSeconds, DefaultPropertyManager.DefaultLongProperties[PinnacleTunables.ResumeGraceSecondsKey].Item);
        }

        [TestMethod]
        public void Settings_AreInTheDefaultsAndMetadataSnapshots()
        {
            var defaults = PooledLootSourceText.Read("Source/config-defaults.tsv");
            var meta = PooledLootSourceText.Read("Source/ACE.Server/config-metadata.tsv");

            StringAssert.Contains(defaults, "pinnacle_encounters_enabled\tbool\ttrue\ttrue");
            StringAssert.Contains(defaults, "pinnacle_hold_seconds\tlong\t600\t600");
            StringAssert.Contains(defaults, "pinnacle_resume_grace_seconds\tlong\t10\t5");

            foreach (var key in new[] { PinnacleTunables.EnabledKey, PinnacleTunables.HoldSecondsKey, PinnacleTunables.ResumeGraceSecondsKey })
                Assert.IsTrue(meta.Split('\n').Any(l => l.StartsWith(key + "\t", StringComparison.Ordinal)), key);
        }

        [TestMethod]
        public void Tunables_UnreachableStore_FallsBackToDefaults()
        {
            // PropertyManager reads throw with no database; the readers must answer with the shipped defaults.
            Assert.AreEqual(PinnacleTunables.DefaultEnabled, PinnacleTunables.Enabled);

            var cfg = PinnacleTunables.FightConfig();
            Assert.AreEqual(600.0, cfg.HoldSeconds);
            Assert.AreEqual(10.0, cfg.ResumeGraceSeconds);
        }

        [TestMethod]
        public void Tunables_Sanitize_ClampsGarbage()
        {
            Assert.AreEqual(600L, PinnacleTunables.Sanitize(0, 600, 1));
            Assert.AreEqual(600L, PinnacleTunables.Sanitize(-5, 600, 1));
            Assert.AreEqual(0L, PinnacleTunables.Sanitize(0, 5, 0));
            Assert.AreEqual(PinnacleTunables.MaxSeconds, PinnacleTunables.Sanitize(long.MaxValue, 600, 1));
            Assert.AreEqual(90L, PinnacleTunables.Sanitize(90, 600, 1));
        }

        private static string PinnaclesDir()
        {
            for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
            {
                var candidate = Path.Combine(dir.FullName, "Source", "ACE.Server", "Pinnacles");

                if (Directory.Exists(candidate))
                    return candidate;
            }

            Assert.Fail("Source/ACE.Server/Pinnacles not found walking up from " + AppContext.BaseDirectory);
            return null;
        }
    }
}
