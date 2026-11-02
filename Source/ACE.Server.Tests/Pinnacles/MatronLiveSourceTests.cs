using System;

using ACE.Server.Tests.ThreadDungeons;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ACE.Server.Tests.Pinnacles
{
    /// <summary>
    /// T5 of the Matron plan: the live bodies of the script API (props, doors, motion, movement, health floor, steering)
    /// and the door-use routing. LivePinnacleWorld and Door need a live landblock, so their ORDER and PRESENCE are pinned on
    /// source text, the same way PinnacleLiveSourceTests and PinnacleHookWiringTests pin the rest of the Pinnacle wiring.
    /// Reads no PropertyManager value.
    /// </summary>
    [TestClass]
    public class MatronLiveSourceTests
    {
        private static string Live() => PooledLootSourceText.Read("Source/ACE.Server/Pinnacles/LivePinnacleWorld.cs");

        private static string Body(string signature) => PooledLootSourceText.MethodBody(Live(), signature);

        private static string DoorSource() => PooledLootSourceText.Read("Source/ACE.Server/WorldObjects/Door.cs");

        private static string DoorActOnUse() => PooledLootSourceText.MethodBody(DoorSource(), "public override void ActOnUse(WorldObject worldObject)");

        // ---- door hook ----

        [TestMethod]
        public void DoorActOnUse_RoutesPinnaclePropUseFirst()
        {
            var body = DoorActOnUse();

            var hook = body.IndexOf("PinnacleFightManager.TryHandlePropUse(this, user)", StringComparison.Ordinal);
            var busy = body.IndexOf("if (IsBusy) return;", StringComparison.Ordinal);

            Assert.IsTrue(hook > 0 && busy > hook, $"hook={hook} busy={busy}");
            // exactly whitespace + return; between the hook condition and the next statement (a bare "return;" search would be
            // satisfied by the rest of the method)
            var hookEnd = hook + "PinnacleFightManager.TryHandlePropUse(this, user))".Length;
            var between = body.Substring(hookEnd, busy - hookEnd);
            var flat = System.Text.RegularExpressions.Regex.Replace(between, @"//[^\n]*", "");
            Assert.IsTrue(System.Text.RegularExpressions.Regex.IsMatch(flat, @"^\s*return;\s*$"), "a routed prop use consumes the use: [" + between + "]");
        }

        [TestMethod]
        public void DoorActOnUse_HookIsTheFirstStatement_AndCarriesTheForkMarker()
        {
            var source = DoorSource();
            var sig = source.IndexOf("public override void ActOnUse(WorldObject worldObject)", StringComparison.Ordinal);
            var open = source.IndexOf('{', sig);
            var hook = source.IndexOf("if (worldObject is Player user && ACE.Server.Pinnacles.PinnacleFightManager.TryHandlePropUse(this, user))", open, StringComparison.Ordinal);
            var marker = source.IndexOf("DIVERGES FROM UPSTREAM", open, StringComparison.Ordinal);

            Assert.IsTrue(open > 0 && marker > open && hook > marker, $"open={open} marker={marker} hook={hook}");

            // nothing but whitespace and the marker comment between the opening brace and the hook
            foreach (var line in source.Substring(open + 1, hook - open - 1).Split('\n'))
            {
                var t = line.Trim();
                Assert.IsTrue(t.Length == 0 || t.StartsWith("//"), "only the marker comment may precede the hook: " + t);
            }
        }

        [TestMethod]
        public void DoorActOnUse_SealedShutRefusesBeforeAnyResetTimerIsScheduled()
        {
            // Review Focus 2: the only caller of Reset is the auto-close chain, and that chain is built after the
            // SealedShut refusal returns, so a sealed door never schedules a Reset (and a creature walking into it, which
            // arrives as ActOnUse(creature) through OnCollideObject, is refused the same way).
            var body = DoorActOnUse();

            var sealedIdx = body.IndexOf("if (SealedShut)", StringComparison.Ordinal);
            var chain = body.IndexOf("autoCloseTimer", StringComparison.Ordinal);
            var open = body.IndexOf("Open(worldObject.Guid)", StringComparison.Ordinal);

            Assert.IsTrue(sealedIdx > 0 && chain > sealedIdx && open > sealedIdx, $"sealed={sealedIdx} chain={chain} open={open}");

            var sealedBlock = PooledLootSourceText.MethodBody(body.Substring(sealedIdx), "if (SealedShut)");
            Assert.IsTrue(System.Text.RegularExpressions.Regex.IsMatch(sealedBlock, @"return;\s*}$"), "the SealedShut block ends in its own return");

            var source = DoorSource();
            Assert.AreEqual(1, CountOf(source, "Reset(useTimestamp)"), "Reset is scheduled from exactly one place");
            Assert.AreEqual(1, CountOf(source, "Reset(") - CountOf(source, "private void Reset("), "no other caller of Reset");
        }

        [TestMethod]
        public void DoorOnCollideObject_GoesThroughActOnUse()
        {
            // the creature path the SealedShut refusal relies on
            var body = PooledLootSourceText.MethodBody(DoorSource(), "public override void OnCollideObject(WorldObject target)");
            StringAssert.Contains(body, "ActOnUse(target);");
        }

        [TestMethod]
        public void DoorClose_DoesNotConsultSealedShut()
        {
            // the script's own Close must work on a sealed door
            Assert.IsFalse(PooledLootSourceText.MethodBody(DoorSource(), "public void Close(ObjectGuid closer = new ObjectGuid())").Contains("SealedShut"));
            Assert.IsFalse(PooledLootSourceText.MethodBody(DoorSource(), "public void Open(ObjectGuid opener = new ObjectGuid())").Contains("SealedShut"));
        }

        // ---- manager and handle lookup ----

        [TestMethod]
        public void TryHandlePropUse_MirrorsHeadUse()
        {
            var manager = PooledLootSourceText.Read("Source/ACE.Server/Pinnacles/PinnacleFightManager.cs");
            var body = PooledLootSourceText.MethodBody(manager, "public static bool TryHandlePropUse(WorldObject prop, Player player)");

            StringAssert.Contains(body, "byOwner.TryGetValue(player.Guid.Full, out var lf)");
            StringAssert.Contains(body, "lf.Fight.IsEnded");
            StringAssert.Contains(body, "lf.World.TryGetHandle(prop, out var h)");
            StringAssert.Contains(body, "lf.Landblock.EnqueueAction(");
            StringAssert.Contains(body, "lf.Fight.OnObjectUsed(h)");
            StringAssert.Contains(body, "catch (Exception ex)");

            var enqueue = body.IndexOf("lf.Landblock.EnqueueAction(", StringComparison.Ordinal);
            var ret = body.IndexOf("return true;", enqueue, StringComparison.Ordinal);
            Assert.IsTrue(ret > enqueue, "true is returned only after the use is queued");
        }

        [TestMethod]
        public void TryGetHandle_OnlyAnswersForSpawnedProps()
        {
            var live = Live();
            StringAssert.Contains(live, "internal bool TryGetHandle(WorldObject wo, out PinnacleHandle h)");

            var spawn = Body("public PinnacleHandle SpawnProp(");
            StringAssert.Contains(spawn, "propHandles[");

            var destroy = Body("public void Destroy(PinnacleHandle h)");
            StringAssert.Contains(destroy, "propHandles.TryRemove(");
        }

        // ---- props ----

        [TestMethod]
        public void SpawnProp_SealsEveryDoorShutBeforeEnterWorld()
        {
            var b = Body("public PinnacleHandle SpawnProp(");
            var seal = b.IndexOf("SealedShut = true", StringComparison.Ordinal);
            var enter = b.IndexOf("EnterWorld()", StringComparison.Ordinal);

            Assert.IsTrue(seal > 0 && enter > seal, $"seal={seal} enter={enter}");
            StringAssert.Contains(b, "ForceOpenUnlocked()");

            // sealed unconditionally: the seal is not inside the DoorOpen branch
            var force = b.IndexOf("ForceOpenUnlocked()", StringComparison.Ordinal);
            Assert.IsTrue(seal < force, $"seal={seal} force={force}");
        }

        [TestMethod]
        public void SpawnProp_StampsScaleTranslucencyScriptBeforeEnterWorld()
        {
            var b = Body("public PinnacleHandle SpawnProp(");
            var enter = b.IndexOf("EnterWorld()", StringComparison.Ordinal);

            foreach (var stamp in new[] { "ObjScale", "Translucency", "VisualEffectScript", "TimeToRot = -1", "wo.Location = " })
            {
                var i = b.IndexOf(stamp, StringComparison.Ordinal);
                Assert.IsTrue(i > 0 && i < enter, $"{stamp} at {i}, EnterWorld at {enter}");
            }
        }

        [TestMethod]
        public void SpawnProp_NeverTouchesTheCreatureScalingOrKillRewardPath()
        {
            var b = Body("public PinnacleHandle SpawnProp(");
            Assert.IsFalse(b.Contains("PinnacleScaling.Apply"), "a prop is not a tiered creature");
            Assert.IsFalse(b.Contains("ApplyMonsterEffectOverlay"));
        }

        [TestMethod]
        public void SetDoorOpen_UsesTheDoorsOwnOpenAndClose()
        {
            var b = Body("public void SetDoorOpen(");
            StringAssert.Contains(b, "door.Open()");
            StringAssert.Contains(b, "door.Close()");
        }

        // ---- motion and effects ----

        [TestMethod]
        public void PlayMotion_BroadcastsAndHoldPersistsTheCurrentMotionState()
        {
            var b = Body("public void PlayMotion(");
            var send = b.IndexOf("EnqueueBroadcastMotion(", StringComparison.Ordinal);
            var hold = b.IndexOf("CurrentMotionState = ", StringComparison.Ordinal);

            Assert.IsTrue(send > 0 && hold > 0, $"send={send} hold={hold}");
            Assert.IsTrue(System.Text.RegularExpressions.Regex.IsMatch(b, @"if \(hold\)\s*wo\.CurrentMotionState = m;"), "hold is what sets CurrentMotionState");
            StringAssert.Contains(b, "new Motion(stance, motion)");
        }

        [TestMethod]
        public void PlayEffect_UsesThePlayScriptHelper()
            => StringAssert.Contains(Body("public void PlayEffect("), "MlDigsiteProps.PlayScriptOn(");

        // ---- movement ----

        [TestMethod]
        public void MoveTo_RunsTheCreatureMoveWithSetLoc()
        {
            var b = Body("public void MoveTo(");
            StringAssert.Contains(b, "c.MoveTo(");
            StringAssert.Contains(b, "c.GetRunRate()");
            StringAssert.Contains(b, "c.MoveTo(ToPosition(to), c.GetRunRate(), true);");
        }

        [TestMethod]
        public void Engage_EndsHostile_ClearingNoAttackAfterSetPeaceful()
        {
            var b = Body("public void Engage(");

            var peaceful = b.IndexOf("SetPeaceful(h, false)", StringComparison.Ordinal);
            var clear = b.IndexOf("~Tolerance.NoAttack", StringComparison.Ordinal);
            var target = b.IndexOf("AttackTarget = ", StringComparison.Ordinal);
            var wake = b.IndexOf("WakeUp(false)", StringComparison.Ordinal);

            Assert.IsTrue(peaceful > 0 && clear > peaceful, $"peaceful={peaceful} clear={clear}");
            Assert.IsTrue(target > clear && wake > target, $"target={target} wake={wake}");
        }

        // ---- health floor and kill ----

        [TestMethod]
        public void SetHealthFloor_UsesFloorFor()
            => StringAssert.Contains(Body("public void SetHealthFloor("), "SpeedBossPhaseRules.FloorFor(");

        [TestMethod]
        public void IsAtHealthFloor_RequiresAFloorAndHealthAtOrBelowIt()
        {
            var b = PooledLootSourceText.ExpressionBody(Live(), "public bool IsAtHealthFloor(");
            StringAssert.Contains(b, "P_HealthFloor > 0");
            StringAssert.Contains(b, "Health.Current <= ");
        }

        [TestMethod]
        public void Slay_ClearsFloorAndImmunityBeforeSmite()
        {
            // Creature.Die writes UpdateVital(Health, 0) (Creature_Death.cs), and UpdateVital raises that write to the floor
            // (Creature_Vitals.cs), so a creature still at a floor never reaches Health 0 and never reads as dead.
            var b = Body("public void Slay(");
            var floor = b.IndexOf("P_HealthFloor = 0", StringComparison.Ordinal);
            var immune = b.IndexOf("P_DigsiteImmune = false", StringComparison.Ordinal);
            var smite = b.IndexOf("Smite(", StringComparison.Ordinal);

            Assert.IsTrue(floor > 0 && immune > 0 && smite > floor && smite > immune, $"floor={floor} immune={immune} smite={smite}");
        }

        // ---- hazard steering ----

        [TestMethod]
        public void ControlHazard_RoutesToTheRunsControl()
            => StringAssert.Contains(PooledLootSourceText.ExpressionBody(Live(), "public bool ControlHazard("), ".Control(key, value)");

        [TestMethod]
        public void TryGetHazardLevel_ReadsTheLevelSource()
            => StringAssert.Contains(Body("public bool TryGetHazardLevel("), "IHazardLevelSource");

        private static int CountOf(string text, string needle)
        {
            var n = 0;

            for (var i = text.IndexOf(needle, StringComparison.Ordinal); i >= 0; i = text.IndexOf(needle, i + needle.Length, StringComparison.Ordinal))
                n++;

            return n;
        }
    }
}
