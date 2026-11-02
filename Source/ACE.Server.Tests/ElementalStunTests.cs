using System;
using System.IO;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Server.Entity;
using ACE.Server.WorldObjects;

namespace ACE.Server.Tests
{
    /// <summary>
    /// Elemental Proficiency Bludgeon stun (Archmage T3): Creature.TryApplyClassAbilityStun. Covers refusal while
    /// stunned or immune, immunity measured from when the stun ENDS, the refusals on ineligible and dead
    /// targets, the movement hold written on landing, and - by source scan, because the AI paths need a live
    /// landblock and the dats - that each normal-AI gate is wired and that the scripted paths are left alone.
    ///
    /// Reads no PropertyManager key: every duration is passed explicitly, so this class needs no seeding and is
    /// safe to run alone or in any order.
    /// </summary>
    [TestClass]
    public class ElementalStunTests
    {
        /// <summary>
        /// A live creature in State.Return, the one state in which the landing's CancelMoveTo is skipped (it
        /// would otherwise need a physics layer this dat-free fixture does not have).
        /// </summary>
        private static Creature Target()
        {
            var target = TestCreatures.CreateDefender(maxHealth: 50);

            target.MonsterState = Creature.State.Return;

            Assert.IsFalse(target.IsDead, "the fixture target must be alive, or a refusal proves nothing");

            return target;
        }

        // ---- timing ----------------------------------------------------------------------------------

        [TestMethod]
        public void Landing_SetsTheStunAndAnImmunityThatStartsWhenTheStunEnds()
        {
            var target = Target();

            Assert.IsTrue(target.TryApplyClassAbilityStunState(3.0, 8.0, 100.0));

            Assert.AreEqual(103.0, target.ClassAbilityStunnedUntil, 1e-9);
            Assert.AreEqual(111.0, target.ClassAbilityStunImmuneUntil, 1e-9, "immunity = stun end + immunity seconds");
        }

        [TestMethod]
        public void WhileStunned_ASecondStunIsRefused_AndDoesNotExtendTheFirst()
        {
            var target = Target();

            Assert.IsTrue(target.TryApplyClassAbilityStunState(3.0, 8.0, 100.0));
            Assert.IsFalse(target.TryApplyClassAbilityStunState(4.0, 8.0, 102.0), "no refresh while stunned");

            Assert.AreEqual(103.0, target.ClassAbilityStunnedUntil, 1e-9);
            Assert.AreEqual(111.0, target.ClassAbilityStunImmuneUntil, 1e-9);
        }

        [TestMethod]
        public void InsideTheImmunityWindow_IsRefused_AtItsEdge_LandsAgain()
        {
            var target = Target();

            Assert.IsTrue(target.TryApplyClassAbilityStunState(3.0, 8.0, 100.0));    // stunned to 103, immune to 111

            Assert.IsFalse(target.TryApplyClassAbilityStunState(3.0, 8.0, 103.0), "immune from the moment the stun ends");
            Assert.IsFalse(target.TryApplyClassAbilityStunState(3.0, 8.0, 110.99), "still immune just before the edge");

            Assert.IsTrue(target.TryApplyClassAbilityStunState(2.0, 8.0, 111.0), "lands again once the immunity has run out");
            Assert.AreEqual(113.0, target.ClassAbilityStunnedUntil, 1e-9);
            Assert.AreEqual(121.0, target.ClassAbilityStunImmuneUntil, 1e-9);
        }

        [TestMethod]
        public void ZeroImmunity_AllowsARestunRightAfterTheStunEnds_NegativeIsClampedToZero()
        {
            var target = Target();

            Assert.IsTrue(target.TryApplyClassAbilityStunState(2.0, -5.0, 100.0));
            Assert.AreEqual(102.0, target.ClassAbilityStunImmuneUntil, 1e-9, "a negative immunity never ends the window early");

            Assert.IsFalse(target.TryApplyClassAbilityStunState(2.0, 0.0, 101.0), "still stunned");
            Assert.IsTrue(target.TryApplyClassAbilityStunState(2.0, 0.0, 102.0));
        }

        // ---- refusals --------------------------------------------------------------------------------

        [TestMethod]
        public void NonPositiveDuration_IsRefused()
        {
            var target = Target();

            Assert.IsFalse(target.TryApplyClassAbilityStun(0.0, 8.0));
            Assert.IsFalse(target.TryApplyClassAbilityStun(-1.0, 8.0));
            Assert.AreEqual(0.0, target.ClassAbilityStunnedUntil, 0.0);
            Assert.AreEqual(0.0, target.ClassAbilityStunImmuneUntil, 0.0, "a refused stun must not arm an immunity either");
        }

        [TestMethod]
        public void DeadCreature_IsRefused()
        {
            var target = Target();
            target.Health.Current = 0;

            Assert.IsTrue(target.IsDead, "control: the fixture is dead");
            Assert.IsFalse(target.TryApplyClassAbilityStun(3.0, 8.0));
            Assert.AreEqual(0.0, target.ClassAbilityStunnedUntil, 0.0);
        }

        [TestMethod]
        public void GamePiece_IsRefused()
        {
            var piece = ElementalProficiencyTestFixtures.LiveGamePiece();

            Assert.IsFalse(piece.TryApplyClassAbilityStun(3.0, 8.0), "IsEligibleTarget refuses a GamePiece");
            Assert.IsFalse(piece.IsClassAbilityStunned);
            Assert.AreEqual(0.0, piece.ClassAbilityStunImmuneUntil, 0.0);
        }

        // ---- landing side effects ---------------------------------------------------------------------

        [TestMethod]
        public void PublicLanding_IsStunned_BlocksMoveReady_AndHoldsNextMoveTime()
        {
            var target = Target();

            var before = Timers.RunningTime;
            Assert.IsTrue(target.TryApplyClassAbilityStun(30.0, 8.0));

            Assert.IsTrue(target.IsClassAbilityStunned);
            Assert.IsTrue(target.ClassAbilityStunnedUntil >= before + 30.0 - 1e-9, "measured on Timers.RunningTime");
            Assert.IsTrue(target.NextMoveTime >= target.ClassAbilityStunnedUntil - 1e-9, "movement held to the stun's end");

            // MoveReady returns before touching the physics layer while stunned, so it is callable on this fixture
            Assert.IsFalse(target.MoveReady(), "a stunned creature cannot start a move or a turn");

            Assert.IsFalse(target.TryApplyClassAbilityStun(30.0, 8.0), "and is not re-stunned while stunned");
        }

        [TestMethod]
        public void PublicLanding_NeverPullsALongerMoveHoldForward()
        {
            var target = Target();
            target.NextMoveTime = Timers.RunningTime + 1000.0;

            var held = target.NextMoveTime;
            Assert.IsTrue(target.TryApplyClassAbilityStun(2.0, 8.0));

            Assert.AreEqual(held, target.NextMoveTime, 1e-9);
        }

        // ---- gates: normal AI blocked, scripted paths untouched --------------------------------------

        private static string WorldObjectsSource(string file)
        {
            var path = Path.Combine(ClassAbilityAffinityDeclarationTests.RepoRoot(), "Source", "ACE.Server", "WorldObjects", file);

            Assert.IsTrue(File.Exists(path), $"could not locate {path}");

            return File.ReadAllText(path);
        }

        [TestMethod]
        public void MonsterTick_ReturnsWhileStunned_AfterTheEmoteBusyReturn_AndBeforeAnyAi()
        {
            var tick = WorldObjectsSource("Monster_Tick.cs");

            var emoteBusy = tick.IndexOf("if (EmoteManager.IsBusy) return;", StringComparison.Ordinal);
            var stunGate = tick.IndexOf("if (IsClassAbilityStunned) return;", StringComparison.Ordinal);
            var findTarget = tick.IndexOf("HandleFindTarget();", StringComparison.Ordinal);
            var slowRestore = tick.IndexOf("TickClassAbilityMoveSlow();", StringComparison.Ordinal);

            Assert.IsTrue(emoteBusy > 0 && stunGate > 0 && findTarget > 0 && slowRestore > 0, "anchors present");
            Assert.IsTrue(slowRestore < stunGate, "the slow restore still runs on a stunned tick");
            Assert.IsTrue(emoteBusy < stunGate && stunGate < findTarget, "the gate sits between the emote return and the AI");
        }

        [TestMethod]
        public void EveryNormalAiAttackPath_IsGated()
        {
            var combat = WorldObjectsSource("Monster_Combat.cs");
            var moveReady = combat.Substring(combat.IndexOf("public bool MoveReady()", StringComparison.Ordinal));
            moveReady = moveReady.Substring(0, moveReady.IndexOf("public bool AttackReady()", StringComparison.Ordinal));
            Assert.IsTrue(moveReady.Contains("if (Timers.RunningTime < ClassAbilityStunnedUntil)"), "MoveReady refuses while stunned");

            var melee = WorldObjectsSource("Monster_Melee.cs");
            Assert.IsTrue(melee.Contains("if (IsClassAbilityStunned) return;"), "a winding-up melee strike is dropped");

            var magic = WorldObjectsSource("Monster_Magic.cs");
            var stunInCast = magic.IndexOf("if (IsClassAbilityStunned)", StringComparison.Ordinal);
            var castCall = magic.IndexOf("CastSpell(spell);", StringComparison.Ordinal);
            Assert.IsTrue(stunInCast > 0 && stunInCast < castCall, "the post-windup cast is gated before CastSpell");

            var missile = WorldObjectsSource("Monster_Missile.cs");
            Assert.IsTrue(missile.Contains("if (IsDead || IsClassAbilityStunned) return;"), "a winding-up shot is never fired");

            var launch = missile.Substring(missile.IndexOf("public void LaunchMissile()", StringComparison.Ordinal));
            Assert.IsTrue(launch.IndexOf("if (IsClassAbilityStunned) return;", StringComparison.Ordinal)
                        < launch.IndexOf("GetEquippedMissileWeapon()", StringComparison.Ordinal), "LaunchMissile refuses to start");

            var stun = WorldObjectsSource("Creature_ClassAbilityStun.cs");
            Assert.IsTrue(stun.Contains("PhysicsObj?.unstick_from_object();"),
                "the landing releases the melee stick-to-target hold, which CancelMoveTo does not");
        }

        /// <summary>
        /// World Events Hold (H4a/H4b) added an attack-and-move path for ranged wave monsters. It must stay behind the
        /// stun gates: it runs only from Monster_Tick after the stun return, its approach goes through MoveReady, and its
        /// attack goes through Attack(), whose melee, missile and magic paths are gated above.
        /// </summary>
        [TestMethod]
        public void HoldRangedKeepDistance_RunsOnlyBehindTheStunGates()
        {
            var tick = WorldObjectsSource("Monster_Tick.cs");
            var stunGate = tick.IndexOf("if (IsClassAbilityStunned) return;", StringComparison.Ordinal);
            var holdCall = tick.IndexOf("WorldEventHooks.TickHoldRangedKeepDistance(this, targetDist)", StringComparison.Ordinal);
            Assert.IsTrue(stunGate > 0 && holdCall > stunGate, "the Hold ranged tick runs after the stun return");

            var hold = WorldObjectsSource("Creature_WorldEventHold.cs");

            // every call site of the keep-distance tick in the server: the hook's one forward and Monster_Tick's one call
            var serverDir = Path.Combine(ClassAbilityAffinityDeclarationTests.RepoRoot(), "Source", "ACE.Server");
            var callers = new System.Collections.Generic.List<string>();
            foreach (var file in Directory.GetFiles(serverDir, "*.cs", SearchOption.AllDirectories))
            {
                foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(File.ReadAllText(file),
                    @"(?<!(bool|static bool) )TickHoldRangedKeepDistance\((?!float)"))
                    callers.Add(Path.GetFileName(file) + "@" + m.Index);
            }
            Assert.AreEqual(2, callers.Count, "callers: " + string.Join(", ", callers));
            Assert.AreEqual(1, callers.FindAll(c => c.StartsWith("Monster_Tick.cs@")).Count, "Monster_Tick is the only caller of the hook");
            Assert.AreEqual(1, callers.FindAll(c => c.StartsWith("WorldEventHooks.cs@")).Count, "the hook is the only caller of the creature tick");

            var approach = MethodCode(hold, "private void StartHoldRangedApproach(");
            Assert.IsTrue(approach.IndexOf("if (!MoveReady())", StringComparison.Ordinal) >= 0
                && approach.IndexOf("if (!MoveReady())", StringComparison.Ordinal) < approach.IndexOf("MoveTo(AttackTarget", StringComparison.Ordinal),
                "the approach asks MoveReady, which refuses while stunned, before moving");

            var keep = MethodCode(hold, "internal bool TickHoldRangedKeepDistance(");
            Assert.IsTrue(keep.Contains("Attack();"), "the attack goes through the stock, gated Attack()");
            Assert.IsFalse(System.Text.RegularExpressions.Regex.IsMatch(keep, @"\b(MeleeAttack|RangeAttack|MagicAttack|LaunchMissile|CastSpell)\("),
                "no direct call that would sidestep the per-path gates");
        }

        // ---- the landing must not disturb scripted behaviour (code-review fix) --------------------------

        /// <summary>
        /// The body of a method named <paramref name="signature"/>, up to its closing brace at member indent,
        /// with comment lines removed (comments explaining why a call is NOT made must not count as the call).
        /// </summary>
        private static string MethodCode(string source, string signature)
        {
            var start = source.IndexOf(signature, StringComparison.Ordinal);
            Assert.IsTrue(start >= 0, $"could not find {signature}");

            var end = System.Text.RegularExpressions.Regex.Match(source.Substring(start), @"\n        }\r?\n");
            Assert.IsTrue(end.Success, $"could not find the end of {signature}");

            var body = source.Substring(start, end.Index);

            var lines = body.Split('\n');
            var code = new System.Text.StringBuilder();
            foreach (var line in lines)
            {
                var trimmed = line.TrimStart();
                if (!trimmed.StartsWith("//"))
                    code.Append(line).Append('\n');
            }

            return code.ToString();
        }

        /// <summary>
        /// TRUE when the code calls something that disturbs scripted behaviour: the full Creature.CancelMoveTo()
        /// (bare call - ResetAttack, FindNextTarget and a cancel of any emote walk), FindNextTarget or ResetAttack.
        /// MoveToManager.CancelMoveTo(WeenieError) is the lightweight stop and is allowed.
        /// </summary>
        private static bool DisturbsScriptedBehaviour(string code)
        {
            return System.Text.RegularExpressions.Regex.IsMatch(code, @"(?<![\w.])CancelMoveTo\(\s*\)")
                || code.Contains("FindNextTarget")
                || code.Contains("ResetAttack");
        }

        [TestMethod]
        public void HaltPath_UsesTheLightweightStop_NeverTheFullCancelMoveTo()
        {
            // NEGATIVE CONTROL: the pre-review halt body, verbatim. The scan must reject it, or the assertions
            // below prove nothing.
            const string oldHalt =
                "        private void HaltForClassAbilityStun()\n" +
                "        {\n" +
                "            if (MonsterState != State.Return && PhysicsObj?.MovementManager?.MoveToManager != null)\n" +
                "                CancelMoveTo();\n" +
                "\n" +
                "            PhysicsObj?.unstick_from_object();\n" +
                "        }\n";
            Assert.IsTrue(DisturbsScriptedBehaviour(MethodCode(oldHalt, "private void HaltForClassAbilityStun()")),
                "control: the scan must flag the old full-CancelMoveTo halt");

            var halt = MethodCode(WorldObjectsSource("Creature_ClassAbilityStun.cs"), "private void HaltForClassAbilityStun()");
            var stop = MethodCode(WorldObjectsSource("Creature_ClassAbilityPin.cs"), "private void ReissueMoveForSpeedChange()");

            Assert.IsTrue(halt.Contains("ReissueMoveForSpeedChange();"), "the halt uses the lightweight stop");
            Assert.IsTrue(halt.Contains("PhysicsObj?.unstick_from_object();"), "and still releases the stick-to-target hold");
            Assert.IsTrue(stop.Contains("moveToManager.CancelMoveTo(WeenieError.ActionCancelled);"), "control: the stop cancels the leg");
            Assert.IsTrue(stop.Contains("MonsterState != State.Awake") && stop.Contains("!IsMoving"), "the stop acts only on an AI move in flight");

            Assert.IsFalse(DisturbsScriptedBehaviour(halt), "the halt must not call CancelMoveTo(), FindNextTarget or ResetAttack");
            Assert.IsFalse(DisturbsScriptedBehaviour(stop), "nor may the lightweight stop it delegates to");
        }

        [TestMethod]
        public void Landing_OnACreatureThatIsNotMoving_LeavesItsAiStateUntouched()
        {
            var target = TestCreatures.CreateDefender(maxHealth: 50);
            var foe = TestCreatures.CreateDefender(maxHealth: 50);

            target.MonsterState = Creature.State.Awake;
            target.AttackTarget = foe;
            target.CurrentAttack = CombatType.Melee;
            target.IsMoving = false;
            target.IsTurning = false;

            Assert.IsTrue(target.TryApplyClassAbilityStun(3.0, 8.0));

            Assert.AreSame(foe, target.AttackTarget, "no FindNextTarget: the target is kept");
            Assert.AreEqual(CombatType.Melee, target.CurrentAttack, "no ResetAttack");
            Assert.AreEqual(Creature.State.Awake, target.MonsterState);
            Assert.IsFalse(target.IsMoving);
        }

        [TestMethod]
        public void Landing_WhileAnEmoteRuns_LeavesTheMoveInFlightAlone()
        {
            var target = TestCreatures.CreateDefender(maxHealth: 50);

            target.MonsterState = Creature.State.Awake;
            target.AttackTarget = TestCreatures.CreateDefender(maxHealth: 50);
            target.IsMoving = true;
            target.EmoteManager.IsBusy = true;

            Assert.IsTrue(target.TryApplyClassAbilityStun(3.0, 8.0));

            Assert.IsTrue(target.IsMoving, "a scripted emote walk may own the MoveToManager; the stun leaves it running");
            Assert.IsTrue(target.IsClassAbilityStunned, "the stun itself still landed");
        }

        [TestMethod]
        public void ScriptedPaths_AreNotGatedByTheStun()
        {
            // owner ruling: emote-driven casts, the monster-effect heartbeat and boss mechanics run as designed
            foreach (var file in new[] { "Creature_Tick.cs", "Creature_MonsterEffects.cs", Path.Combine("Managers", "EmoteManager.cs") })
            {
                var source = WorldObjectsSource(file);

                Assert.IsFalse(source.Contains("ClassAbilityStunned"), $"{file} must not consult the stun");
            }
        }
    }
}
