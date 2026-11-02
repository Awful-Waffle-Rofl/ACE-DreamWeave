using System;
using System.Collections.Generic;
using System.Linq;

using ACE.Server.WorldEvents;
using ACE.Server.WorldEvents.Defs;
using ACE.Server.WorldEvents.Hold;
using ACE.Server.WorldObjects;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using K = ACE.Server.WorldEvents.HoldKeys;

namespace ACE.Server.Tests
{
    /// <summary>
    /// World Events Hold H4a, the engine's reach and damage side. The pure rules in WorldEventHoldRules.Combat.cs (ported
    /// in intent from the contributor's WorldEventHoldSurvivalTests / WorldEventHoldWardTests /
    /// WorldEventHoldSettingsKnights / WorldEventHoldSettingsMonsters), then the creature-level paths the hooks forward to,
    /// driven over real tagged Creatures and a hand-built run view (no WorldEvent, no landblock, no PropertyManager).
    /// </summary>
    [TestClass]
    public class WorldEventHoldCombatTests
    {
        private const double T0 = 1_000_000.0;

        // ---- cross-thread damage state (review F2, F3) -----------------------------------------------------------

        [TestMethod]
        public void ParallelHits_OnOneKnight_NoThrow_CapAndPacingStayConsistent()
        {
            var run = new FakeHoldRunView(9)
            {
                Tuning = WorldEventHoldTuning.Defaults with { UndefendedSeconds = 0, MinDeathSeconds = 30, CapBurstSeconds = 2, KnightDamageTakenMult = 1.0 },
            };

            var knight = HoldFixtures.Tagged(run, HoldUnitKind.Knight);
            var a = HoldFixtures.Tagged(run, HoldUnitKind.Monster);
            var b = HoldFixtures.Tagged(run, HoldUnitKind.Monster);
            var bucket = knight.Health.MaxValue / 30.0 * 2;

            const int hits = 20000;
            double totalA = 0, totalB = 0;

            // One fixed instant, so the bucket never refills: however the hits interleave, together they may take exactly
            // the burst and no more. An unlocked bucket double-spends under this race.
            var ta = System.Threading.Tasks.Task.Run(() => { for (var i = 0; i < hits; i++) totalA += knight.ScaleHoldKnightHit(a, 3f, T0); });
            var tb = System.Threading.Tasks.Task.Run(() => { for (var i = 0; i < hits; i++) totalB += knight.ScaleHoldKnightHit(b, 3f, T0); });
            System.Threading.Tasks.Task.WaitAll(ta, tb);

            Assert.AreEqual(bucket, totalA + totalB, 1.0, "the two attackers shared one burst, exactly");
            Assert.AreEqual(2, knight.HoldPacedAttackerCount);
            Assert.AreEqual(T0, knight.HoldLastMonsterHitAt);
        }

        [TestMethod]
        public void HitPacing_ForgetsAttackersIdlePastTheWindow()
        {
            var run = new FakeHoldRunView(9) { Tuning = WorldEventHoldTuning.Defaults with { HitIntervalSeconds = 2, HitIntervalMaxSeconds = 4 } };
            var knight = HoldFixtures.Tagged(run, HoldUnitKind.Knight);
            var a = HoldFixtures.Tagged(run, HoldUnitKind.Monster);
            var b = HoldFixtures.Tagged(run, HoldUnitKind.Monster);

            knight.ScaleHoldKnightHit(a, 10f, T0);
            knight.ScaleHoldKnightHit(b, 10f, T0 + 1);
            Assert.AreEqual(2, knight.HoldPacedAttackerCount);

            knight.ScaleHoldKnightHit(b, 10f, T0 + 5);
            Assert.AreEqual(2, knight.HoldPacedAttackerCount, "a, idle 5 s, is still remembered (retention is HoldHitMemorySeconds, not the interval window)");

            knight.ScaleHoldKnightHit(b, 10f, T0 + 61);
            Assert.AreEqual(1, knight.HoldPacedAttackerCount, "a, idle 61 s past the 60 s retention, is pruned");
        }

        [TestMethod]
        public void HitPacing_IdleAttacker_SameSliceAlone_OrAfterAnotherHit_PastRetentionIsAFirstHit()
        {
            var tuning = WorldEventHoldTuning.Defaults with { HitIntervalSeconds = 2, HitIntervalMaxSeconds = 4, UndefendedSeconds = 60, MinDeathSeconds = 0, KnightDamageTakenMult = 1.0 };

            float Hit(Creature knight, Creature attacker, double at) => knight.ScaleHoldKnightHit(attacker, 10f, at);

            // A idles 10 s alone, then hits.
            var run1 = new FakeHoldRunView(9) { Tuning = tuning };
            var k1 = HoldFixtures.Tagged(run1, HoldUnitKind.Knight);
            var a1 = HoldFixtures.Tagged(run1, HoldUnitKind.Monster);
            var first = Hit(k1, a1, T0);
            var alone = Hit(k1, a1, T0 + 10);

            // The same, with B hitting in between.
            var run2 = new FakeHoldRunView(9) { Tuning = tuning };
            var k2 = HoldFixtures.Tagged(run2, HoldUnitKind.Knight);
            var a2 = HoldFixtures.Tagged(run2, HoldUnitKind.Monster);
            var b2 = HoldFixtures.Tagged(run2, HoldUnitKind.Monster);
            Hit(k2, a2, T0);
            Hit(k2, b2, T0 + 9);
            var afterB = Hit(k2, a2, T0 + 10);

            // A idles 61 s, past the retention: a first hit again (whether or not B hit in between).
            var run3 = new FakeHoldRunView(9) { Tuning = tuning };
            var k3 = HoldFixtures.Tagged(run3, HoldUnitKind.Knight);
            var a3 = HoldFixtures.Tagged(run3, HoldUnitKind.Monster);
            var b3 = HoldFixtures.Tagged(run3, HoldUnitKind.Monster);
            Hit(k3, a3, T0);
            Hit(k3, b3, T0 + 30);
            var pastRetention = Hit(k3, a3, T0 + 61);

            var max = k1.Health.MaxValue;
            Assert.AreEqual(WorldEventHoldRules.ProtectedHitDamage(max, 2, 60), first, "sanity: a first hit is the 2 s slice");
            Assert.AreEqual(WorldEventHoldRules.ProtectedHitDamage(max, 4, 60), alone, "a 10 s gap is clamped to the 4 s cap");
            Assert.AreEqual(alone, afterB, "another attacker's hit in between changes nothing");
            Assert.AreEqual(first, pastRetention, "idle past HoldHitMemorySeconds: a first hit");
        }

        [TestMethod]
        public void BossDamageLog_KeepsRecentPlayerDamage_Bounded()
        {
            var run = new FakeHoldRunView(9) { Tuning = WorldEventHoldTuning.Defaults with { BossDamageKeepSeconds = 30 } };
            var boss = HoldFixtures.Tagged(run, HoldUnitKind.FinaleBoss);

            boss.NoteHoldBossDamage(0x50000001u, 100, T0);
            boss.NoteHoldBossDamage(0x50000002u, 0, T0);
            boss.NoteHoldBossDamage(0u, 100, T0);
            boss.NoteHoldBossDamage(0x50000002u, 250, T0 + 10);

            var snap = boss.HoldBossDamageSnapshot(T0 + 10);
            Assert.AreEqual(2, snap.Count, "zero amounts and guid 0 are not logged");
            Assert.AreEqual((T0, 0x50000001u, 100.0), snap[0]);
            Assert.AreEqual(1, boss.HoldBossDamageSnapshot(T0 + 35).Count, "older than boss_damage_keep_seconds drops out of the snapshot");
        }

        [TestMethod]
        public void BossDamageLog_HighHitRate_CoalescedPerSecond_NothingUndercounted()
        {
            var run = new FakeHoldRunView(9) { Tuning = WorldEventHoldTuning.Defaults with { BossDamageKeepSeconds = 30 } };
            var boss = HoldFixtures.Tagged(run, HoldUnitKind.FinaleBoss);

            // 600 hits in 8 s from two players.
            double sum = 0;

            for (var i = 0; i < 600; i++)
            {
                var amount = (uint)(1 + i % 7);
                boss.NoteHoldBossDamage(i % 2 == 0 ? 0x50000001u : 0x50000002u, amount, T0 + i * (8.0 / 600));
                sum += amount;
            }

            var snap = boss.HoldBossDamageSnapshot(T0 + 8);
            Assert.AreEqual(sum, snap.Sum(e => e.Amount), "every hit is counted");
            Assert.IsTrue(snap.Count <= 2 * 9, $"bounded by players x seconds, got {snap.Count}");
            Assert.AreEqual(2, snap.Select(e => e.Player).Distinct().Count());
        }

        // ---- pure rules -----------------------------------------------------------------------------------------

        [TestMethod]
        public void PhysicsRunIds_KnightAndMonsterSides_ZeroForNoTag()
        {
            Assert.AreEqual((0u, 0u), WorldEventHoldRules.PhysicsRunIds(null, 7));
            Assert.AreEqual((0u, 0u), WorldEventHoldRules.PhysicsRunIds(HoldUnitKind.Knight, 0));
            Assert.AreEqual((7u, 0u), WorldEventHoldRules.PhysicsRunIds(HoldUnitKind.Knight, 7));
            Assert.AreEqual((0u, 7u), WorldEventHoldRules.PhysicsRunIds(HoldUnitKind.Monster, 7));
            Assert.AreEqual((0u, 7u), WorldEventHoldRules.PhysicsRunIds(HoldUnitKind.FinaleBoss, 7));

            Assert.IsTrue(WorldEventHoldRules.PhysicsIsKnightTargetFor(7, true, 7));
            Assert.IsFalse(WorldEventHoldRules.PhysicsIsKnightTargetFor(0, true, 0), "no knight, no route - even with matching zeros");
            Assert.IsFalse(WorldEventHoldRules.PhysicsIsKnightTargetFor(7, false, 7), "a non-monster never targets a knight");
            Assert.IsFalse(WorldEventHoldRules.PhysicsIsKnightTargetFor(7, true, 8), "another run's monster");
        }

        [TestMethod]
        public void MayDamageKnight_OnlyItsOwnActiveRunsMonsters()
        {
            Assert.IsTrue(WorldEventHoldRules.MayDamageKnight(7, false, HoldUnitKind.Monster, 7, true));
            Assert.IsTrue(WorldEventHoldRules.MayDamageKnight(7, false, HoldUnitKind.FinaleBoss, 7, true));
            Assert.IsFalse(WorldEventHoldRules.MayDamageKnight(7, true, HoldUnitKind.Monster, 7, true), "a pet or player");
            Assert.IsFalse(WorldEventHoldRules.MayDamageKnight(7, false, null, 0, true), "an untagged creature");
            Assert.IsFalse(WorldEventHoldRules.MayDamageKnight(7, false, HoldUnitKind.Knight, 7, true), "a knight never hits a knight");
            Assert.IsFalse(WorldEventHoldRules.MayDamageKnight(7, false, HoldUnitKind.Monster, 8, true), "another run");
            Assert.IsFalse(WorldEventHoldRules.MayDamageKnight(7, false, HoldUnitKind.Monster, 7, false), "a finished run");
            Assert.IsFalse(WorldEventHoldRules.MayDamageKnight(0, false, HoldUnitKind.Monster, 0, true), "no knight run");
        }

        [TestMethod]
        public void HitInterval_FirstHitDefault_ThenTheGap_Clamped()
        {
            Assert.AreEqual(2.0, WorldEventHoldRules.HitInterval(null, T0, 2, 4));
            Assert.AreEqual(1.5, WorldEventHoldRules.HitInterval(T0 - 1.5, T0, 2, 4));
            Assert.AreEqual(4.0, WorldEventHoldRules.HitInterval(T0 - 30, T0, 2, 4), "a long gap is not banked");
            Assert.AreEqual(0.0, WorldEventHoldRules.HitInterval(T0 + 5, T0, 2, 4), "a clock step back is 0, never negative");
            Assert.AreEqual(3.0, WorldEventHoldRules.HitInterval(double.NaN, T0, 3, 4), "an unusable last hit is a first hit");
        }

        [TestMethod]
        public void ProtectedHitDamage_SizedToUndefendedSeconds_RunnersFaster()
        {
            Assert.AreEqual(500f, WorldEventHoldRules.ProtectedHitDamage(15000, 2, 60));
            Assert.AreEqual(1000f, WorldEventHoldRules.ProtectedHitDamage(15000, 2, 60, 2.0), "twice the swing speed, twice the slice");
            Assert.AreEqual(500f, WorldEventHoldRules.ProtectedHitDamage(15000, 2, 60, 0.5), "below 1 is the plain slice");
            Assert.AreEqual(1f, WorldEventHoldRules.ProtectedHitDamage(10, 0.01, 60), "at least 1 for a positive interval");
            Assert.AreEqual(0f, WorldEventHoldRules.ProtectedHitDamage(15000, 0, 60), "two hits in one instant share the slice");
            Assert.AreEqual(0f, WorldEventHoldRules.ProtectedHitDamage(15000, 2, 0));
            Assert.AreEqual(0f, WorldEventHoldRules.ProtectedHitDamage(0, 2, 60));
        }

        [TestMethod]
        public void CapProtectedHit_HoldsAPileOnToMinDeathSeconds()
        {
            double tokens = 0, lastAt = 0;

            // 15000 health over 30 s = 500/s, with a 2 s burst = 1000 in the bucket.
            Assert.AreEqual(600f, WorldEventHoldRules.CapProtectedHit(600, 15000, 30, 2, ref tokens, ref lastAt, T0));
            Assert.AreEqual(400f, WorldEventHoldRules.CapProtectedHit(600, 15000, 30, 2, ref tokens, ref lastAt, T0), "what is left of the burst");
            Assert.AreEqual(0f, WorldEventHoldRules.CapProtectedHit(600, 15000, 30, 2, ref tokens, ref lastAt, T0), "empty");
            Assert.AreEqual(500f, WorldEventHoldRules.CapProtectedHit(600, 15000, 30, 2, ref tokens, ref lastAt, T0 + 1), "one second refills the rate");

            double t2 = 0, l2 = 0;
            Assert.AreEqual(99999f, WorldEventHoldRules.CapProtectedHit(99999, 15000, 0, 2, ref t2, ref l2, T0), "0 = no cap");
        }

        [TestMethod]
        public void TakeFromBucket_StartsFull_RefillsToCapacity()
        {
            double tokens = 0, lastAt = 0;

            Assert.AreEqual(10.0, WorldEventHoldRules.TakeFromBucket(10, 1, 10, ref tokens, ref lastAt, T0));
            Assert.AreEqual(0.0, WorldEventHoldRules.TakeFromBucket(5, 1, 10, ref tokens, ref lastAt, T0));
            Assert.AreEqual(10.0, WorldEventHoldRules.TakeFromBucket(50, 1, 10, ref tokens, ref lastAt, T0 + 100), "never above capacity");
            Assert.AreEqual(0.0, WorldEventHoldRules.TakeFromBucket(double.NaN, 1, 10, ref tokens, ref lastAt, T0));
            Assert.AreEqual(-3.0, WorldEventHoldRules.TakeFromBucket(-3, 1, 10, ref tokens, ref lastAt, T0), "a non-positive request passes through");
        }

        [TestMethod]
        public void EmpowerFactor_SelfInsideAuraOutside()
        {
            Assert.AreEqual(2.0, WorldEventHoldRules.EmpowerFactor(true, null, 12, 1.5, 2.0));
            Assert.AreEqual(1.5, WorldEventHoldRules.EmpowerFactor(false, 11.9, 12, 1.5, 2.0));
            Assert.AreEqual(1.5, WorldEventHoldRules.EmpowerFactor(false, 12, 12, 1.5, 2.0), "the edge counts");
            Assert.AreEqual(1.0, WorldEventHoldRules.EmpowerFactor(false, 12.1, 12, 1.5, 2.0));
            Assert.AreEqual(1.0, WorldEventHoldRules.EmpowerFactor(false, null, 12, 1.5, 2.0));
            Assert.AreEqual(1.0, WorldEventHoldRules.EmpowerFactor(true, null, 12, 1.5, 0.5), "a weakening self mult is ignored");
        }

        [TestMethod]
        public void WardHit_CutsMissileAndMagic_MeleeShatters()
        {
            var reduces = WorldEventCombatKinds.Missile | WorldEventCombatKinds.Magic;

            Assert.AreEqual(25f, WorldEventHoldRules.WardHit(100, WorldEventCombatKinds.Missile, true, 0.75, reduces, WorldEventCombatKinds.Melee, out var o1));
            Assert.AreEqual(WorldEventHoldRules.WardOutcome.Reduced, o1);
            Assert.AreEqual(25f, WorldEventHoldRules.WardHit(100, WorldEventCombatKinds.Magic, true, 0.75, reduces, WorldEventCombatKinds.Melee, out _));
            Assert.AreEqual(100f, WorldEventHoldRules.WardHit(100, WorldEventCombatKinds.Melee, true, 0.75, reduces, WorldEventCombatKinds.Melee, out var o3));
            Assert.AreEqual(WorldEventHoldRules.WardOutcome.Shattered, o3);
            Assert.AreEqual(100f, WorldEventHoldRules.WardHit(100, WorldEventCombatKinds.Missile, false, 0.75, reduces, WorldEventCombatKinds.Melee, out var o4), "down: full");
            Assert.AreEqual(WorldEventHoldRules.WardOutcome.None, o4);
            Assert.AreEqual(0f, WorldEventHoldRules.WardHit(0, WorldEventCombatKinds.Melee, true, 0.75, reduces, WorldEventCombatKinds.Melee, out var o5), "a miss never shatters");
            Assert.AreEqual(WorldEventHoldRules.WardOutcome.None, o5);
            Assert.AreEqual(1.0, WorldEventHoldRules.WardDownSeconds(0.2));
            Assert.AreEqual(120.0, WorldEventHoldRules.WardDownSeconds(500));
        }

        [TestMethod]
        public void TargetPicks_SpreadAvoidHealthiestNearest()
        {
            Assert.AreEqual(1, WorldEventHoldRules.PickLeastAttacked(new[] { 2, 0, 0 }, new[] { 1.0, 9.0, 16.0 }, 100), "fewest attackers, nearest among ties");
            Assert.AreEqual(-1, WorldEventHoldRules.PickLeastAttacked(new[] { 0 }, new[] { 400.0 }, 100), "out of reach");

            var counts = WorldEventHoldRules.AvoidTarget(new[] { 0, 3 }, new uint[] { 0xA, 0xB }, 0xA);
            CollectionAssert.AreEqual(new[] { WorldEventHoldRules.AvoidedTargetPenalty, 3 }, counts);
            Assert.AreEqual(1, WorldEventHoldRules.PickLeastAttacked(counts, new[] { 1.0, 50.0 }, 100), "the avoided knight is a last resort");
            CollectionAssert.AreEqual(new[] { 0, 3 }, WorldEventHoldRules.AvoidTarget(new[] { 0, 3 }, new uint[] { 0xA, 0xB }, 0));

            Assert.AreEqual(1, WorldEventHoldRules.PickHealthiest(new (uint, uint)[] { (50, 100), (900, 1000), (0, 0) }));
            Assert.AreEqual(-1, WorldEventHoldRules.PickHealthiest(Array.Empty<(uint, uint)>()));

            Assert.AreEqual(2, WorldEventHoldRules.NearestWithin(new[] { 50.0, double.NaN, 4.0 }, 100));
            Assert.AreEqual(-1, WorldEventHoldRules.NearestWithin(new[] { 500.0 }, 100));

            Assert.IsTrue(WorldEventHoldRules.SeekerStrayed(true, false, true));
            Assert.IsFalse(WorldEventHoldRules.SeekerStrayed(true, true, true));
            Assert.IsFalse(WorldEventHoldRules.SeekerStrayed(true, false, false), "nothing standing to go back to");
            Assert.IsFalse(WorldEventHoldRules.SeekerStrayed(false, false, true));
        }

        [TestMethod]
        public void RangedHold_KeepsItsDistanceWithSlack()
        {
            Assert.IsTrue(WorldEventHoldRules.HoldsRange(15, true, false));
            Assert.IsTrue(WorldEventHoldRules.HoldsRange(15, false, true));
            Assert.IsFalse(WorldEventHoldRules.HoldsRange(15, false, false), "melee-only closes in");
            Assert.IsFalse(WorldEventHoldRules.HoldsRange(0, true, true));

            Assert.IsFalse(WorldEventHoldRules.RangedHoldShouldApproach(16.4, 15, 1.5));
            Assert.IsTrue(WorldEventHoldRules.RangedHoldShouldApproach(16.6, 15, 1.5));
            Assert.IsTrue(WorldEventHoldRules.RangedHoldShouldApproach(15.1, 15, -3), "a negative slack is 0");
        }

        // ---- the knight hit, end to end over tagged creatures -----------------------------------------------------

        private static FakeHoldRunView Run(uint id, IDictionary<string, string> props, GoalDef goal = null)
            => new FakeHoldRunView(id) { Tuning = HoldFixtures.Tuning(props, goal) };

        [TestMethod]
        public void KnightHit_UndefendedPacing_OneAttackerEmptiesItInUndefendedSeconds()
        {
            var run = Run(41, new Dictionary<string, string> { [K.UndefendedSeconds] = "60" });
            var knight = HoldFixtures.Tagged(run, HoldUnitKind.Knight);
            var monster = HoldFixtures.Tagged(run, HoldUnitKind.Monster);
            var max = knight.Health.MaxValue;

            Assert.AreEqual((float)Math.Round(max * 2.0 / 60), knight.ScaleHoldKnightHit(monster, 7f, T0), "first hit: 2 s of attacking");
            Assert.AreEqual((float)Math.Round(max * 1.0 / 60), knight.ScaleHoldKnightHit(monster, 7f, T0 + 1), "then the gap");
            Assert.AreEqual((float)Math.Round(max * 4.0 / 60), knight.ScaleHoldKnightHit(monster, 7f, T0 + 60), "a long gap is clamped to 4 s");
            Assert.AreEqual(T0 + 60, knight.HoldLastMonsterHitAt, "the heal reduction is timed from the last hit");

            var runner = HoldFixtures.Tagged(run, HoldUnitKind.Monster, role: HoldDamageRole.Runner, attackSpeedMult: 2f);
            Assert.AreEqual((float)Math.Round(max * 2.0 / 30), knight.ScaleHoldKnightHit(runner, 7f, T0 + 61), "a runner swinging twice as fast");
        }

        [TestMethod]
        public void KnightHit_NoUndefendedSeconds_KeepsTheWeaponDamage_ThenTheMultipliers()
        {
            var run = Run(42, new Dictionary<string, string>
            {
                [K.KnightDamageTakenMult] = "2",
                [K.Role("melee", K.RoleNpcDamageMult)] = "1.5",
            });

            var knight = HoldFixtures.Tagged(run, HoldUnitKind.Knight);
            var monster = HoldFixtures.Tagged(run, HoldUnitKind.Monster);

            Assert.AreEqual(300f, knight.ScaleHoldKnightHit(monster, 100f, T0), "100 x knight_damage_taken_mult 2 x melee_npc_damage_mult 1.5");
        }

        [TestMethod]
        public void KnightHit_MinDeathSeconds_CapsAPileOn()
        {
            var run = Run(43, new Dictionary<string, string> { [K.UndefendedSeconds] = "60", [K.MinDeathSeconds] = "60", [K.CapBurstSeconds] = "2" });
            var knight = HoldFixtures.Tagged(run, HoldUnitKind.Knight);
            var max = knight.Health.MaxValue;
            var slice = (float)Math.Round(max * 2.0 / 60);   // each attacker's first hit
            var bucket = max / 60.0 * 2;                       // 2 s of the max / 60 rate

            var dealt = 0.0;

            for (var i = 0; i < 5; i++)
                dealt += knight.ScaleHoldKnightHit(HoldFixtures.Tagged(run, HoldUnitKind.Monster), 7f, T0);

            Assert.IsTrue(slice * 5 > bucket, "sanity: five slices ask for more than the burst holds");
            Assert.AreEqual(bucket, dealt, 0.01, "five attackers in one instant get only the burst");
        }

        [TestMethod]
        public void KnightHit_Corrupted_SelfAndAura()
        {
            var run = Run(44, new Dictionary<string, string> { [K.CorruptedSelfMult] = "2", [K.CorruptedEmpowerMult] = "1.5" });
            var knight = HoldFixtures.Tagged(run, HoldUnitKind.Knight);
            var corrupted = HoldFixtures.Tagged(run, HoldUnitKind.Monster, role: HoldDamageRole.Corrupted, corrupted: true);
            var near = HoldFixtures.Tagged(run, HoldUnitKind.Monster);
            var far = HoldFixtures.Tagged(run, HoldUnitKind.Monster);

            corrupted.Location = HoldFixtures.At(50, 50);
            near.Location = HoldFixtures.At(55, 50);
            far.Location = HoldFixtures.At(50, 90);
            run.WaveMonsters = new[] { corrupted, near, far };

            Assert.IsTrue(run.Tuning.CorruptedEmpowerRadius > 5 && run.Tuning.CorruptedEmpowerRadius < 40, "sanity: the default aura covers 5 m, not 40 m");

            Assert.AreEqual(200f, knight.ScaleHoldKnightHit(corrupted, 100f, T0));
            Assert.AreEqual(150f, knight.ScaleHoldKnightHit(near, 100f, T0));
            Assert.AreEqual(100f, knight.ScaleHoldKnightHit(far, 100f, T0));

            corrupted.Health.Current = 0;
            Assert.AreEqual(100f, knight.ScaleHoldKnightHit(near, 100f, T0 + 1), "a dead corrupted monster empowers nobody");
        }

        [TestMethod]
        public void KnightHit_FinaleBoss_OwnSliceAndBucket_Enraged_Noted()
        {
            var run = Run(45, new Dictionary<string, string>
            {
                [K.FinaleTarget] = ProtectDef.FinaleTargetNpcs,
                [K.FinaleMinKillSeconds] = "50",
                [K.UndefendedSeconds] = "60",
            });
            run.EnrageMult = 1.5;

            var knight = HoldFixtures.Tagged(run, HoldUnitKind.Knight);
            var boss = HoldFixtures.Tagged(run, HoldUnitKind.FinaleBoss);
            var max = knight.Health.MaxValue;

            Assert.IsTrue(run.Tuning.FinaleTargetsKnights);
            Assert.AreEqual((float)(Math.Round(max * 2.0 / 50) * 1.5), knight.ScaleHoldKnightHit(boss, 7f, T0), 0.5, "its own 50 s slice, enraged x1.5");
            Assert.AreEqual(1, run.FinaleHits, "the run is told");

            var players = Run(46, new Dictionary<string, string> { [K.FinaleTarget] = ProtectDef.FinaleTargetPlayers, [K.UndefendedSeconds] = "60" });
            Assert.IsFalse(players.Tuning.FinaleTargetsKnights);
        }

        [TestMethod]
        public void KnightHit_Unchanged_ForEverythingElse()
        {
            var run = Run(47, new Dictionary<string, string> { [K.UndefendedSeconds] = "60", [K.KnightDamageTakenMult] = "3" });
            var knight = HoldFixtures.Tagged(run, HoldUnitKind.Knight);
            var monster = HoldFixtures.Tagged(run, HoldUnitKind.Monster);

            Assert.AreEqual(123f, knight.ScaleHoldKnightHit(HoldFixtures.MakeCreature(), 123f, T0), "an untagged attacker");
            Assert.AreEqual(123f, knight.ScaleHoldKnightHit(HoldFixtures.Tagged(Run(99, null), HoldUnitKind.Monster), 123f, T0), "another run's monster");
            Assert.AreEqual(123f, knight.ScaleHoldKnightHit(null, 123f, T0), "a sourceless DoT tick");
            Assert.AreEqual(123f, monster.ScaleHoldKnightHit(knight, 123f, T0), "a wave monster is not a knight");
            Assert.AreEqual(123f, HoldFixtures.MakeCreature().ScaleHoldKnightHit(monster, 123f, T0), "an untagged target");
            Assert.AreEqual(-5f, knight.ScaleHoldKnightHit(monster, -5f, T0), "a heal through TakeDamage is never touched");

            run.IsActive = false;
            Assert.AreEqual(123f, knight.ScaleHoldKnightHit(monster, 123f, T0), "a finished run");
        }

        // ---- may damage, player multiplier, ward --------------------------------------------------------------------

        [TestMethod]
        public void CanDamage_KnightOnlyByItsOwnRun()
        {
            var run = Run(51, null);
            var knight = HoldFixtures.Tagged(run, HoldUnitKind.Knight);
            var monster = HoldFixtures.Tagged(run, HoldUnitKind.Monster);
            var stranger = HoldFixtures.MakeCreature();

            Assert.IsTrue(monster.CanDamage(knight));
            Assert.IsFalse(stranger.CanDamage(knight), "an outside monster never");
            Assert.IsFalse(HoldFixtures.Tagged(Run(52, null), HoldUnitKind.Monster).CanDamage(knight), "another run's monster never");
            Assert.IsFalse(knight.CanDamage(knight));

            run.IsActive = false;
            Assert.IsFalse(monster.CanDamage(knight), "a finished run's monster never");
        }

        [TestMethod]
        public void PlayerDamageMult_RoleThenFinale_OneForEverythingElse()
        {
            var run = Run(53, new Dictionary<string, string>
            {
                [K.Role("melee", K.RolePlayerDamageMult)] = "1.5",
                [K.FinalePlayerDamageMult] = "2",
            });
            run.EnrageMult = 1.5;

            Assert.AreEqual(1.5, HoldFixtures.Tagged(run, HoldUnitKind.Monster).HoldPlayerDamageMult(T0));
            Assert.AreEqual(1.0, HoldFixtures.Tagged(run, HoldUnitKind.Monster, role: HoldDamageRole.Ranged).HoldPlayerDamageMult(T0), "a role with no setting");
            Assert.AreEqual(3.0, HoldFixtures.Tagged(run, HoldUnitKind.FinaleBoss).HoldPlayerDamageMult(T0), 1e-9, "finale 2 x enrage 1.5");
            Assert.AreEqual(1.0, HoldFixtures.Tagged(run, HoldUnitKind.Knight).HoldPlayerDamageMult(T0));
            Assert.AreEqual(1.0, HoldFixtures.MakeCreature().HoldPlayerDamageMult(T0));

            run.IsActive = false;
            Assert.AreEqual(1.0, HoldFixtures.Tagged(run, HoldUnitKind.Monster).HoldPlayerDamageMult(T0), "a finished run");
        }

        [TestMethod]
        public void Ward_CutsMissile_MeleeShattersForItsTime_ThenItIsBack()
        {
            var goal = new GoalDef { Protect = new ProtectDef { Ward = new WardDef() } };
            var run = Run(54, null, goal);
            var ranged = HoldFixtures.Tagged(run, HoldUnitKind.Monster, role: HoldDamageRole.Ranged, warded: true);

            Assert.IsTrue(run.Tuning.WardEnabled, "sanity: the goal carries a ward");

            Assert.AreEqual(25f, ranged.ApplyHoldWard(WorldEventCombatKinds.Missile, 100f, T0));
            Assert.AreEqual(25f, ranged.ApplyHoldWard(WorldEventCombatKinds.Magic, 100f, T0));
            Assert.AreEqual(100f, ranged.ApplyHoldWard(WorldEventCombatKinds.Melee, 100f, T0), "melee shatters and lands in full");
            Assert.AreEqual(100f, ranged.ApplyHoldWard(WorldEventCombatKinds.Missile, 100f, T0 + 9), "down for 10 s");
            Assert.AreEqual(25f, ranged.ApplyHoldWard(WorldEventCombatKinds.Missile, 100f, T0 + 10), "back");

            var plain = HoldFixtures.Tagged(run, HoldUnitKind.Monster, role: HoldDamageRole.Ranged);
            Assert.AreEqual(100f, plain.ApplyHoldWard(WorldEventCombatKinds.Missile, 100f, T0), "only a warded monster");

            var noWardGoal = HoldFixtures.Tagged(Run(55, null), HoldUnitKind.Monster, warded: true);
            Assert.AreEqual(100f, noWardGoal.ApplyHoldWard(WorldEventCombatKinds.Missile, 100f, T0), "a goal with no ward block");
        }
    }
}
