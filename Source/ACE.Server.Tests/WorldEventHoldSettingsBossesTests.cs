using System;
using System.Collections.Generic;
using System.Linq;

using ACE.Server.WorldEvents;
using ACE.Server.WorldEvents.Defs;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ACE.Server.Tests
{
    /// <summary>The finale and champion groups of the Hold settings.</summary>
    public partial class WorldEventHoldSettingsTests
    {
        [TestMethod]
        public void Bosses_WithNothingSet_AreExactlyAsBefore()
        {
            var goal = EnvoysGoal();
            var p = goal.Protect;

            using var scope = new HoldSettingsScope();

            Assert.AreEqual(3, p.FinaleBossHealthMult);
            Assert.AreEqual(0, p.FinaleBossScale);
            Assert.AreEqual(50, p.BossMinKillSeconds);
            Assert.IsTrue(p.BossTargetsProtected);
            Assert.AreEqual(ProtectDef.FinaleTargetNpcs, p.FinaleTargetMode);
            Assert.IsNull(p.FinaleHealth);
            Assert.IsNull(p.FinaleBossWcid);
            Assert.AreEqual(0, p.FinaleLevelOffset);

            Assert.AreEqual(1.0, HoldSettings.ChampionHealthMult(true));
            Assert.IsNull(HoldSettings.ChampionHealth(true));
            Assert.AreEqual(300, HoldSettings.ChampionArrival(300, 600));
            Assert.AreEqual(1.0, HoldSettings.EnrageMult(1000, 99999, HoldKeys.FinaleEnrageSeconds, HoldKeys.FinaleEnrageMult), "no enrage unless set");

            var dials = new BossDamageDials(3.0, -80, 300, 4, 50);
            Assert.AreEqual(3.0, HoldSettings.ChampionDials(dials, true, 1000, 99999).HitsToKill);

            var audience = new AudienceEstimate(3, 150, 160);
            Assert.AreEqual(150, HoldSettings.ShiftAudience(audience, 0).MedianLevel);

            foreach (var group in new[] { HoldSettings.GroupFinale, HoldSettings.GroupChampion })
                foreach (var s in HoldSettings.InGroup(group).Where(s => !s.IsReused))
                    Assert.IsFalse(HoldSettings.Describe(s, goal).Overridden, s.Key);
        }

        [TestMethod]
        public void Finale_SettingsAreLive()
        {
            var goal = EnvoysGoal();
            var p = goal.Protect;

            using var scope = new HoldSettingsScope();

            scope.Set(HoldKeys.FinaleHealthMult, "2");
            scope.Set(HoldKeys.FinaleMinKillSeconds, "0");
            scope.Set(HoldKeys.FinaleSwapSeconds, "30");
            scope.Set(HoldKeys.FinaleHealth, "50000");
            scope.Set(HoldKeys.FinaleBossWcid, "1002619");
            scope.Set(HoldKeys.FinaleLevelOffset, "-20");

            Assert.AreEqual(2, p.FinaleBossHealthMult);
            Assert.AreEqual(0, p.BossMinKillSeconds, "0 on purpose: no cap of its own");
            Assert.AreEqual(30, p.BossSwapSeconds);
            Assert.AreEqual(50000, p.FinaleHealth);
            Assert.AreEqual(1002619u, p.FinaleBossWcid);
            Assert.AreEqual(-20, p.FinaleLevelOffset);

            scope.Set(HoldKeys.FinaleBossWcid, "0");
            Assert.IsNull(p.FinaleBossWcid, "0 = the family champion");
        }

        [TestMethod]
        public void Finale_TargetPreference()
        {
            var goal = EnvoysGoal();
            var p = goal.Protect;

            using var scope = new HoldSettingsScope();

            scope.Set(HoldKeys.FinaleTarget, "players");
            Assert.IsFalse(p.BossTargetsProtected, "players: the knight controller is off");
            Assert.AreEqual(ProtectDef.FinaleTargetPlayers, p.FinaleTargetMode);

            scope.Set(HoldKeys.FinaleTarget, "BOTH");
            Assert.AreEqual(ProtectDef.FinaleTargetBoth, p.FinaleTargetMode);
            Assert.IsFalse(p.BossTargetsProtected);

            scope.Set(HoldKeys.FinaleTarget, "npcs");
            Assert.IsTrue(p.BossTargetsProtected);

            scope.Set(HoldKeys.FinaleTarget, "dragons");
            Assert.IsTrue(p.BossTargetsProtected, "not a choice: ignored");
            Assert.IsTrue(HoldSettings.TryFind(HoldKeys.FinaleTarget, out var s));
            StringAssert.Contains(HoldSettings.Check(s, "dragons"), "not one of");
        }

        [TestMethod]
        public void Bosses_EnrageAfterItsTime()
        {
            using var scope = new HoldSettingsScope();

            scope.Set(HoldKeys.FinaleEnrageSeconds, "60");
            scope.Set(HoldKeys.FinaleEnrageMult, "2");

            Assert.AreEqual(1.0, HoldSettings.EnrageMult(1000, 1059, HoldKeys.FinaleEnrageSeconds, HoldKeys.FinaleEnrageMult));
            Assert.AreEqual(2.0, HoldSettings.EnrageMult(1000, 1060, HoldKeys.FinaleEnrageSeconds, HoldKeys.FinaleEnrageMult));
            Assert.AreEqual(1.0, HoldSettings.EnrageMult(0, 99999, HoldKeys.FinaleEnrageSeconds, HoldKeys.FinaleEnrageMult), "never spawned: no enrage");

            var boss = TestCreatures.CreateAttacker();
            boss.WorldEventFinaleBoss = true;
            boss.WorldEventBossSpawnedAt = ACE.Common.Time.GetUnixTime() - 120;
            scope.Set(HoldKeys.FinalePlayerDamageMult, "1.5");

            Assert.AreEqual(3.0, HoldSettings.PlayerDamageMultFor(boss), 1e-9, "x1.5 to players, x2 enraged");
            Assert.AreEqual(2.0, HoldSettings.NpcDamageMultFor(boss), 1e-9, "its enrage on knights too");
        }

        [TestMethod]
        public void Champion_DamageGoesThroughTheSteeringNotTheGlobalProperties()
        {
            using var scope = new HoldSettingsScope();
            var dials = new BossDamageDials(3.0, -80, 300, 4, 50);

            scope.Set(HoldKeys.ChampionPlayerDamageMult, "2");
            Assert.AreEqual(1.5, HoldSettings.ChampionDials(dials, true, 0, 1000).HitsToKill, "twice the damage: half the hits");
            Assert.AreEqual(3.0, HoldSettings.ChampionDials(dials, false, 0, 1000).HitsToKill, "any other run: unchanged");

            scope.Set(HoldKeys.ChampionEnrageSeconds, "100");
            scope.Set(HoldKeys.ChampionEnrageMult, "3");
            Assert.AreEqual(0.5, HoldSettings.ChampionDials(dials, true, 1000, 1100).HitsToKill, 1e-9, "x2 and x3 once enraged");
            Assert.AreEqual(-80, HoldSettings.ChampionDials(dials, true, 1000, 1100).RatingMin, "the other dials are kept");
        }

        [TestMethod]
        public void Champion_HealthAndArrival()
        {
            using var scope = new HoldSettingsScope();

            scope.Set(HoldKeys.ChampionHealthMult, "0.5");
            scope.Set(HoldKeys.ChampionHealth, "250000");
            Assert.AreEqual(0.5, HoldSettings.ChampionHealthMult(true));
            Assert.AreEqual(1.0, HoldSettings.ChampionHealthMult(false), "not a chained stage: unchanged");
            Assert.AreEqual(250000, HoldSettings.ChampionHealth(true));
            Assert.IsNull(HoldSettings.ChampionHealth(false));

            scope.Set(HoldKeys.ChampionArrivalSeconds, "60");
            Assert.AreEqual(60, HoldSettings.ChampionArrival(300, 600));

            scope.Set(HoldKeys.ChampionArrivalSeconds, "1800");
            Assert.AreEqual(570, HoldSettings.ChampionArrival(300, 600), "kept under the stage's time limit");
        }

        [TestMethod]
        public void Bosses_LevelOffsetsAndAdds()
        {
            var shifted = HoldSettings.ShiftAudience(new AudienceEstimate(2, 150, 160), 20);
            Assert.AreEqual(170, shifted.MedianLevel);
            Assert.AreEqual(180, shifted.P90Level);

            var fromList = HoldSettings.ShiftAudience(WorldEventRosterSelector.EstimateFromLevels(new List<int> { 100, 270 }), 20);
            Assert.IsTrue(fromList.Levels.Max() <= 275, "kept within 275");

            var members = new List<FamilyMember>
            {
                new FamilyMember { Wcid = 1, Level = 100, Role = 0 },
                new FamilyMember { Wcid = 2, Level = 150, Role = 0 },
                new FamilyMember { Wcid = 3, Level = 150, Role = 2 },
                new FamilyMember { Wcid = 4, Level = 155, Role = 0 }
            };

            var adds = WorldEventHoldRules.PickAdds(members, new LevelBand(140, 160), 4, 6, new Random(1));
            Assert.AreEqual(6, adds.Count);
            Assert.IsTrue(adds.All(w => w == 2), "in band, ordinary, never the boss's own monster");

            Assert.AreEqual(0, WorldEventHoldRules.PickAdds(members, null, 0, 0, new Random(1)).Count, "0 adds: none");
            Assert.IsTrue(WorldEventHoldRules.PickAdds(members, new LevelBand(250, 260), 0, 3, new Random(1)).All(w => w == 1 || w == 2 || w == 4),
                "nobody in band: any ordinary member");
        }
    }
}
