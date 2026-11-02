using System;
using System.Collections.Generic;
using System.Linq;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Entity.Enum;
using ACE.Server.WorldEvents;
using ACE.Server.WorldEvents.Defs;
using ACE.Server.WorldEvents.Hold;

namespace ACE.Server.Tests
{
    /// <summary>
    /// World Events Hold H5: the pure rules of the spawner side - the wave mix and the corrupted assault, the finale boss's
    /// name, size, fallback and controller, the ring and portal layout, the knights' tag, health and scene, the scene verdict,
    /// the spawner tuning, and the ported effect-table and ranged-member rules. No dat, no database, no PropertyManager.
    /// </summary>
    [TestClass]
    public class WorldEventHoldSpawnRulesTests
    {
        private static readonly double[] DefaultShares = new WaveMixDef().Shares;

        // ---- the wave mix ---------------------------------------------------------------------------------------------

        [TestMethod]
        public void WaveMix_CountsFillTheWave_AndHonourMinimumsAndMaximums()
        {
            var mix = new WaveMixDef();

            foreach (var size in new[] { 1, 3, 6, 10, 25 })
            {
                var counts = WorldEventHoldRules.WaveMixCounts(size, mix.Shares, mix.Minimums, mix.Maximums);

                Assert.AreEqual(size, counts.Sum(), $"size {size}");
                Assert.IsTrue(counts[(int)HoldRole.Corrupted] <= mix.MaxCorrupted, $"size {size}");
            }

            var big = WorldEventHoldRules.WaveMixCounts(25, mix.Shares, mix.Minimums, mix.Maximums);
            Assert.IsTrue(big[(int)HoldRole.Ranged] >= mix.MinRanged);
            Assert.IsTrue(big[(int)HoldRole.Corrupted] >= mix.MinCorrupted);
        }

        [TestMethod]
        public void AssignWaveMix_NeverPutsRangedOnAKeptSlot()
        {
            var shares = new double[] { 0, 0, 0, 1, 0, 0 };

            for (var seed = 0; seed < 30; seed++)
            {
                var roles = WorldEventHoldRules.AssignWaveMix(5, shares, new int[6], new[] { -1, -1, -1, -1, -1, -1 }, new HashSet<int> { 0, 4 }, new Random(seed));

                Assert.AreNotEqual(HoldRole.Ranged, roles[0]);
                Assert.AreNotEqual(HoldRole.Ranged, roles[4]);
            }
        }

        [TestMethod]
        public void Assault_IsEveryNthWaveFromItsFirst_NeverTheFinale_AndSpreadsOverTheKnights()
        {
            Assert.IsFalse(WorldEventHoldRules.IsAssaultWave(1, 2, 2, false));
            Assert.IsTrue(WorldEventHoldRules.IsAssaultWave(2, 2, 2, false));
            Assert.IsFalse(WorldEventHoldRules.IsAssaultWave(3, 2, 2, false));
            Assert.IsTrue(WorldEventHoldRules.IsAssaultWave(4, 2, 2, false));
            Assert.IsFalse(WorldEventHoldRules.IsAssaultWave(4, 2, 2, true), "never the finale");
            Assert.IsFalse(WorldEventHoldRules.IsAssaultWave(4, 0, 2, false), "assaults off");

            var targets = WorldEventHoldRules.AssaultTargets(3, 3, new Random(2));
            CollectionAssert.AreEquivalent(new[] { 0, 1, 2 }, targets, "a different knight each while enough stand");

            var roles = WorldEventHoldRules.AssignAssaultWave(8, 2, DefaultShares, new int[6], new[] { -1, -1, -1, -1, -1, 2 }, new HashSet<int> { 7 }, new Random(4));
            Assert.AreEqual(2, roles.Count(r => r == HoldRole.Corrupted), "exactly the assault, none by share on top");
            Assert.AreNotEqual(HoldRole.Corrupted, roles[7], "never a kept slot");
        }

        // ---- the finale boss --------------------------------------------------------------------------------------------

        [TestMethod]
        public void FinaleBoss_NameScaleAndLookalikes()
        {
            Assert.AreEqual("Warlord Shallows Gorger", WorldEventHoldRules.FinaleBossName("Warlord", "Elite Shallows Gorger"));
            Assert.AreEqual("Warlord Gorger", WorldEventHoldRules.FinaleBossName("Warlord", "Warlord Gorger"));
            Assert.AreEqual("the champion", WorldEventHoldRules.FinaleBossName(null, null));

            Assert.AreEqual(2.5f, WorldEventHoldRules.FinaleBossScale(9, 0.8f));
            Assert.AreEqual(1.0f, WorldEventHoldRules.FinaleBossScale(0.5, 0.8f), "clamped to 1 at the bottom");
            Assert.AreEqual(0.8f, WorldEventHoldRules.FinaleBossScale(0, 0.8f), "0: the World Events boss multiplier");

            var escort = WorldEventHoldRules.WithoutBossLookalikes(new uint[] { 5, 9, 5, 7 }, 5, new Random(1));
            Assert.IsFalse(escort.Contains(5u));
            Assert.AreEqual(4, escort.Count);
        }

        [TestMethod]
        public void FinaleBossFallback_TakesTheMemberNearestTheEliteCentre_NeverANamedBoss()
        {
            var members = new List<FamilyMember>
            {
                new FamilyMember { Wcid = 1, Level = 40, Role = 0 },
                new FamilyMember { Wcid = 2, Level = 98, Role = 1 },
                new FamilyMember { Wcid = 3, Level = 102, Role = 0 },
                new FamilyMember { Wcid = 4, Level = 100, Role = WorldEventRosterSelector.NamedBossRole },
            };

            Assert.AreEqual(3u, WorldEventRosterSelector.FinaleBossFallbackMember(members, 100).Wcid, "98 and 102 tie at 2 levels: the higher one");
            Assert.AreEqual(1u, WorldEventRosterSelector.FinaleBossFallbackMember(members, 10).Wcid);
            Assert.IsNull(WorldEventRosterSelector.FinaleBossFallbackMember(new[] { members[3] }, 100), "a named boss only: none");
            Assert.IsNull(WorldEventRosterSelector.FinaleBossFallbackMember(null, 100));
        }

        [TestMethod]
        public void FinaleBoss_PicksTheHealthiestKnight_SwapsToAnother_AndPullDecides()
        {
            var knights = new List<(uint, uint, uint)> { (1, 50, 100), (2, 90, 100), (3, 70, 100) };

            Assert.AreEqual(2u, WorldEventHoldFinaleBoss.PickTarget(knights, 0, false), "healthiest");
            Assert.AreEqual(3u, WorldEventHoldFinaleBoss.PickTarget(knights, 3, false), "keeps its target while it stands");
            Assert.AreEqual(2u, WorldEventHoldFinaleBoss.PickTarget(knights, 3, true), "a swap goes to the healthiest OTHER");
            Assert.AreEqual(0u, WorldEventHoldFinaleBoss.PickTarget(new List<(uint, uint, uint)> { (1, 0, 100) }, 1, false));

            var (total, top) = WorldEventHoldFinaleBoss.RecentDamage(new[] { (10.0, 7u, 300.0), (11.0, 8u, 100.0), (1.0, 8u, 999.0) }, 12, 5);
            Assert.AreEqual(400.0, total, "the old hit is outside the window");
            Assert.AreEqual(7u, top);

            Assert.IsTrue(WorldEventHoldFinaleBoss.PullDue(600, 10000, 0.06));
            Assert.IsFalse(WorldEventHoldFinaleBoss.PullDue(599, 10000, 0.06));
            Assert.IsFalse(WorldEventHoldFinaleBoss.PullDue(1e9, 10000, 0), "share 0: never pulled");

            var delay = WorldEventHoldFinaleBoss.SwapDelay(new Random(1), 15, 20);
            Assert.IsTrue(delay >= 15 && delay <= 20);
        }

        [TestMethod]
        public void FinaleController_Targets_Pulls_AndTurnsOnPlayersWhenNoKnightStands()
        {
            var tuning = WorldEventHoldTuning.Defaults;
            var c = new WorldEventHoldFinaleController(new Random(1));
            var knights = new List<(uint, uint, uint)> { (1, 50, 100), (2, 90, 100) };

            var first = c.Decide(100, 99, 10000, knights, (0, 0), false, tuning);
            Assert.AreEqual(HoldFinaleDecisionKind.Target, first.Kind);
            Assert.AreEqual(2u, first.TargetGuid);
            Assert.IsTrue(first.Changed);

            Assert.AreEqual(HoldFinaleDecisionKind.None, c.Decide(100.5, 99, 10000, knights, (0, 0), false, tuning).Kind, "once a second");

            var pull = c.Decide(102, 99, 10000, knights, (10000 * Math.Max(0.01, tuning.FinalePullShare), 555), false, tuning);
            if (tuning.FinalePullShare > 0)
            {
                Assert.AreEqual(HoldFinaleDecisionKind.Pull, pull.Kind);
                Assert.AreEqual(555u, pull.PlayerGuid);
                Assert.AreEqual(102 + tuning.FinalePullSeconds, pull.Until, 1e-9);

                var back = c.Decide(104, 99, 10000, knights, (0, 0), false, tuning);
                Assert.AreEqual(HoldFinaleDecisionKind.Target, back.Kind);
                Assert.AreEqual("back from a pull", back.Reason);
                Assert.AreEqual(1u, back.TargetGuid, "back from a pull it turns on a different knight");
            }

            var players = c.Decide(110, 99, 10000, new List<(uint, uint, uint)>(), (0, 0), false, tuning);
            Assert.AreEqual(HoldFinaleDecisionKind.OnPlayers, players.Kind);
            Assert.AreEqual(HoldFinaleDecisionKind.None, c.Decide(111, 99, 10000, new List<(uint, uint, uint)>(), (0, 0), false, tuning).Kind, "said once");

            Assert.AreEqual(HoldFinaleDecisionKind.None, c.Decide(112, 0, 0, knights, (0, 0), false, tuning).Kind, "no boss");
        }

        // ---- layout ---------------------------------------------------------------------------------------------------

        [TestMethod]
        public void Layout_RingFacesOutward_PortalsSitOnTheirRing_SidesAreCompassWords()
        {
            var ring = WorldEventHoldRules.RingOffsets(3, 6);

            Assert.AreEqual(3, ring.Count);
            Assert.AreEqual(0f, ring[0].Dx, 1e-4);
            Assert.AreEqual(6f, ring[0].Dy, 1e-4);
            Assert.AreEqual(0f, ring[0].YawDegrees, 1e-4, "due north faces north");

            Assert.IsTrue(WorldEventHoldRules.RingOffsets(1, 6).All(o => o.Dx == 0 && o.Dy == 0), "one knight stands at the anchor");

            foreach (var p in WorldEventHoldRules.PortalOffsets(4, 30, 45))
                Assert.AreEqual(30.0, Math.Sqrt(p.Dx * p.Dx + p.Dy * p.Dy), 1e-3);

            Assert.AreEqual("east", WorldEventHoldRules.CompassSide(10, 0));
            Assert.AreEqual("southwest", WorldEventHoldRules.CompassSide(-10, -10));
            Assert.AreEqual("center", WorldEventHoldRules.CompassSide(0.1, 0.1));
        }

        [TestMethod]
        public void IndoorSpawn_IsRefusedOnlyForAHoldWave_WithAnOutdoorAnchor()
        {
            Assert.IsTrue(WorldEventHoldRules.RefusesIndoorSpawn(true, true, true, false));
            Assert.IsFalse(WorldEventHoldRules.RefusesIndoorSpawn(false, true, true, false), "not a Hold run");
            Assert.IsFalse(WorldEventHoldRules.RefusesIndoorSpawn(true, false, true, false), "not a wave");
            Assert.IsFalse(WorldEventHoldRules.RefusesIndoorSpawn(true, true, true, true), "an indoor anchor");
            Assert.IsFalse(WorldEventHoldRules.RefusesIndoorSpawn(true, true, false, false));
        }

        [TestMethod]
        public void MovedSlotLevel_ShiftsWithinTheLevelRange_AndLeavesNoSlotLevelAlone()
        {
            Assert.AreEqual(120, WorldEventHoldRules.MovedSlotLevel(100, 20, -1));
            Assert.AreEqual(275, WorldEventHoldRules.MovedSlotLevel(270, 20, -1));
            Assert.AreEqual(1, WorldEventHoldRules.MovedSlotLevel(5, -20, -1));
            Assert.AreEqual(-1, WorldEventHoldRules.MovedSlotLevel(-1, 20, -1));
            Assert.AreEqual(100, WorldEventHoldRules.MovedSlotLevel(100, 0, -1));
        }

        // ---- knights and their scene ------------------------------------------------------------------------------------

        [TestMethod]
        public void KnightTag_IsAKnightOfTheRun_WithNoTuningRole()
        {
            var run = new WorldEventHoldRun(77);
            var tag = WorldEventSpawner.KnightTag(77, run, true);

            Assert.AreEqual(77u, tag.RunId);
            Assert.AreSame(run, tag.Run);
            Assert.AreEqual(HoldUnitKind.Knight, tag.Kind);
            Assert.AreEqual(HoldDamageRole.None, tag.Role);
            Assert.IsTrue(tag.ShowHealthBar);
            Assert.IsFalse(tag.Seeker || tag.Hunter || tag.Corrupted || tag.Warded);
        }

        [TestMethod]
        public void KnightHealth_ByWcidWins_ThenKnightMaxHealth_ThenProtectHealth()
        {
            var protect = new ProtectDef { Wcid = 1007650, Health = new ScaledCount { Base = 9000, PerParticipant = 0, Cap = 9000 } };
            var goal = new GoalDef { Id = "g", TypeKind = GoalType.Hold, Protect = protect };

            var tuning = WorldEventHoldTuning.From(WorldEventHoldSettingsResolver.Resolve(goal,
                k => k == HoldKeys.PropertyPrefix + HoldKeys.KnightMaxHealthByWcid ? "1007650=12000" : null), goal, 3);

            Assert.AreEqual(12000, WorldEventSpawner.KnightHealth(1007650, tuning, protect, 3));

            var other = WorldEventSpawner.KnightHealth(1007651, tuning, protect, 3);
            Assert.AreEqual(tuning.KnightMaxHealth > 0 ? (int)tuning.KnightMaxHealth : protect.EffectiveMaxHealth(3), other);
        }

        [TestMethod]
        public void SpawnerTuning_ReadsItsRows_AndFallsBackToTheGoal()
        {
            var protect = new ProtectDef { Wcid = 1, SeekShare = 0.45, WaveMix = new WaveMixDef { MinRanged = 4 } };
            var goal = new GoalDef { Id = "g", TypeKind = GoalType.Hold, Protect = protect };

            var plain = WorldEventHoldTuning.From(WorldEventHoldSettingsResolver.Resolve(goal, _ => null), goal, 1).Spawner;

            Assert.IsTrue(plain.HasWaveMix);
            Assert.AreEqual(0.45, plain.SeekShare, 1e-9, "protect.seekShare");
            Assert.AreEqual(4, plain.Minimums[(int)HoldRole.Ranged], "waveMix.minRanged");
            Assert.IsNull(plain.KnightCount);

            var set = WorldEventHoldTuning.From(WorldEventHoldSettingsResolver.Resolve(goal, k =>
                k == HoldKeys.PropertyPrefix + HoldKeys.KnightCount ? "2" :
                k == HoldKeys.PropertyPrefix + HoldKeys.SeekShare ? "0.8" : null), goal, 1).Spawner;

            Assert.AreEqual(2, set.KnightCount);
            Assert.AreEqual(0.8, set.SeekShare, 1e-9);

            var copy = set.Shares;
            copy[0] = 99;
            Assert.AreNotEqual(99, set.Shares[0], "the arrays are copies");
        }

        [TestMethod]
        public void HoldScene_PlacesDecor_TargetDecorPerKnight_ShrinesForTheFirstN_AndPortals()
        {
            var protect = new ProtectDef
            {
                Wcid = 1,
                Decor = new List<DecorDef> { new DecorDef { Wcid = 10 } },
                TargetDecor = new List<DecorDef> { new DecorDef { Wcid = 20, Dx = 1 } },
                Shrine = new ShrineDef { Wcid = 30 },
                Portals = new PortalsDef { Count = 4, Radius = 30, Parts = new List<DecorDef> { new DecorDef { Wcid = 40 }, new DecorDef { Wcid = 41 } } },
            };
            var goal = new GoalDef { Id = "g", TypeKind = GoalType.Hold, Protect = protect };
            var ring = WorldEventHoldRules.RingOffsets(3, 6);

            var all = WorldEventSpawner.HoldScene(protect, ring, WorldEventHoldTuning.Defaults, null);

            Assert.AreEqual(1, all.Count(d => d.Wcid == 10));
            Assert.AreEqual(3, all.Count(d => d.Wcid == 20));
            Assert.AreEqual(3, all.Count(d => d.Wcid == 30 && d.HoldShrine));
            Assert.AreEqual(4, all.Count(d => d.Wcid == 40));
            Assert.AreEqual(4, all.Count(d => d.Wcid == 41));

            var one = WorldEventHoldTuning.From(WorldEventHoldSettingsResolver.Resolve(goal,
                k => k == HoldKeys.PropertyPrefix + HoldKeys.ShrineCount ? "1" : null), goal, 1);

            Assert.AreEqual(1, WorldEventSpawner.HoldScene(protect, ring, one, null).Count(d => d.Wcid == 30), "shrine_count 1");

            var plan = new WorldEventAreaPlan { Ok = true };
            plan.Portals.Add(new PortalSpot { Bearing = 0, Radius = 25, AskedBearing = 0, AskedRadius = 30 });

            Assert.AreEqual(1, WorldEventSpawner.HoldScene(protect, ring, WorldEventHoldTuning.Defaults, plan).Count(d => d.Wcid == 40),
                "the start-spot search's spots replace the configured ring");
        }

        [TestMethod]
        public void DecorVerdict_ShrineMustBeGeneric_PropsGenericOrLight_AnythingElseRefused()
        {
            Assert.AreEqual(WorldEventSpawner.DecorUse.HoldShrine, WorldEventSpawner.DecorVerdict(WeenieType.Generic, true));
            Assert.AreEqual(WorldEventSpawner.DecorUse.Refuse, WorldEventSpawner.DecorVerdict(WeenieType.PKModifier, true));
            Assert.AreEqual(WorldEventSpawner.DecorUse.Inert, WorldEventSpawner.DecorVerdict(WeenieType.Generic, false));
            Assert.AreEqual(WorldEventSpawner.DecorUse.Inert, WorldEventSpawner.DecorVerdict(WeenieType.LightSource, false));

            foreach (var t in new[] { WeenieType.Portal, WeenieType.LifeStone, WeenieType.Switch, WeenieType.Door, WeenieType.Chest, WeenieType.Creature })
                Assert.AreEqual(WorldEventSpawner.DecorUse.Refuse, WorldEventSpawner.DecorVerdict(t, false), t.ToString());
        }

        // ---- ported support rules -----------------------------------------------------------------------------------

        [TestMethod]
        public void EffectTables_FallBackOnlyWhenTheOwnTableLacksTheEffect_AndTheFallbackHasIt()
        {
            Func<uint, PlayScript, bool?> has = (table, s) => table == 0x3400002A;

            Assert.IsTrue(WorldEventEffectTables.NeedsFallback(0, 0x3400002A, PlayScript.ShieldUpRed, has), "no table at all");
            Assert.IsTrue(WorldEventEffectTables.NeedsFallback(0x34000099, 0x3400002A, PlayScript.ShieldUpRed, has), "own table lacks it");
            Assert.IsFalse(WorldEventEffectTables.NeedsFallback(0x3400002A, 0x3400002A, PlayScript.ShieldUpRed, has), "already the fallback");
            Assert.IsFalse(WorldEventEffectTables.NeedsFallback(0x34000099, 0x34000098, PlayScript.ShieldUpRed, has), "fallback lacks it too");
            Assert.IsFalse(WorldEventEffectTables.NeedsFallback(0x34000099, 0x3400002A, PlayScript.ShieldUpRed, (t, s) => null), "dats not loaded");
        }

        [TestMethod]
        public void RangedMembers_RealRangedAttack_AmmoFit_AndThePool()
        {
            Assert.IsTrue(WorldEventRangedMembers.HasRealRangedAttack(true, true, false, 0));
            Assert.IsFalse(WorldEventRangedMembers.HasRealRangedAttack(true, false, false, 0), "a bow with no arrows");
            Assert.IsTrue(WorldEventRangedMembers.HasRealRangedAttack(false, false, true, 0), "a thrown weapon");
            Assert.IsTrue(WorldEventRangedMembers.HasRealRangedAttack(false, false, false, 0.2));
            Assert.IsFalse(WorldEventRangedMembers.HasRealRangedAttack(false, false, false, 0.06), "an incidental bolt");

            Assert.AreEqual(0.25, WorldEventRangedMembers.SpellChance(2.25f), 1e-6);
            Assert.AreEqual(0.015, WorldEventRangedMembers.SpellChance(1.5f), 1e-6, "2 or less is a percentage");

            Assert.IsTrue(WorldEventRangedMembers.AmmoFits(1, 1));
            Assert.IsFalse(WorldEventRangedMembers.AmmoFits(1, 2));
            Assert.IsTrue(WorldEventRangedMembers.AmmoFits(null, 2));

            Assert.IsTrue(WorldEventRangedMembers.IsCasterAttack(false, false, 0.3));
            Assert.IsFalse(WorldEventRangedMembers.IsCasterAttack(true, false, 0.3), "an archer that also casts is an archer");

            var members = new List<FamilyMember>
            {
                new FamilyMember { Wcid = 1, Level = 50 },
                new FamilyMember { Wcid = 2, Level = 52 },
                new FamilyMember { Wcid = 3, Level = 90 },
            };

            CollectionAssert.AreEqual(new uint[] { 2 }, WorldEventRangedMembers.Pool(members, new LevelBand(45, 55), m => m.Wcid >= 2));
            CollectionAssert.AreEqual(new uint[] { 3 }, WorldEventRangedMembers.Pool(members, new LevelBand(45, 55), m => m.Wcid == 3), "none in band: every ranged member");
            Assert.AreEqual(0, WorldEventRangedMembers.Pool(members, new LevelBand(45, 55), m => false).Count);
        }
    }
}
