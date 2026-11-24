using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

using ACE.Server.BalanceTrials;
using ACE.Server.ThreadDungeons;
using ACE.Server.ThreadDungeons.Defs;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ACE.Server.Tests.BalanceTrials
{
    /// <summary>Pins wave selection and re-pointing, and the source shape of BuildPlan. Reads no PropertyManager key.</summary>
    [TestClass]
    public class TrialWavePlanTests
    {
        private static DungeonSpawnPointDef Pt(float x) => new DungeonSpawnPointDef { Cell = 100, X = x };

        private static DungeonSpawnPlan Plan(bool bossNoCandidate = false, params (uint Wcid, DungeonRole Role)[] entries)
        {
            var plan = new DungeonSpawnPlan { BossNoCandidate = bossNoCandidate };
            foreach (var e in entries)
                plan.Entries.Add(new DungeonSpawnPlanEntry(e.Wcid, e.Role, Pt(e.Wcid)));
            return plan;
        }

        private static TrialRoom Room(int points)
            => new TrialRoom("r", 0x0150, Pt(1), Pt(999), Enumerable.Range(0, points).Select(i => Pt(500 + i)).ToList(), null);

        [TestMethod]
        public void Select_takes_the_first_N_per_role_in_plan_order()
        {
            var plan = Plan(false,
                (1, DungeonRole.Trash), (2, DungeonRole.Elite), (3, DungeonRole.Trash), (4, DungeonRole.Trash), (5, DungeonRole.Boss), (6, DungeonRole.Elite));

            var waves = TrialWavePlan.Select(plan,
                new[] { new TrialWave(DungeonRole.Trash, 2), new TrialWave(DungeonRole.Elite, 1), new TrialWave(DungeonRole.Boss, 1) }, out var shortfalls);

            Assert.AreEqual(0, shortfalls.Count);
            Assert.AreEqual(3, waves.Count);
            CollectionAssert.AreEqual(new uint[] { 1, 3 }, waves[0].Select(e => e.Wcid).ToArray());
            CollectionAssert.AreEqual(new uint[] { 2 }, waves[1].Select(e => e.Wcid).ToArray());
            CollectionAssert.AreEqual(new uint[] { 5 }, waves[2].Select(e => e.Wcid).ToArray());
        }

        [TestMethod]
        public void Select_reports_a_short_role_and_a_boss_with_no_candidate()
        {
            var plan = Plan(true, (1, DungeonRole.Trash), (2, DungeonRole.Elite));

            var waves = TrialWavePlan.Select(plan,
                new[] { new TrialWave(DungeonRole.Trash, 3), new TrialWave(DungeonRole.Elite, 1), new TrialWave(DungeonRole.Boss, 1) }, out var shortfalls);

            Assert.AreEqual(2, shortfalls.Count, string.Join(" | ", shortfalls));
            StringAssert.Contains(shortfalls[0], "Trash");
            StringAssert.Contains(shortfalls[0], "wanted 3");
            StringAssert.Contains(shortfalls[0], "got 1");
            StringAssert.Contains(shortfalls[1], "Boss");
            Assert.AreEqual(1, waves[0].Count);
            Assert.AreEqual(0, waves[2].Count);
        }

        [TestMethod]
        public void Repoint_keeps_wcid_role_uplift_and_stamp()
        {
            var wave = new List<DungeonSpawnPlanEntry>
            {
                new DungeonSpawnPlanEntry(10, DungeonRole.Trash, Pt(1), upliftLevel: 400),
                new DungeonSpawnPlanEntry(11, DungeonRole.Elite, Pt(2), stampLevel: 380),
                new DungeonSpawnPlanEntry(12, DungeonRole.Trash, Pt(3), upliftLevel: 390, stretched: true),
                new DungeonSpawnPlanEntry(13, DungeonRole.Boss, Pt(4)),
            };
            var room = Room(3);

            var moved = TrialWavePlan.Repoint(wave, room);

            Assert.AreEqual(4, moved.Count);
            CollectionAssert.AreEqual(new uint[] { 10, 11, 12, 13 }, moved.Select(e => e.Wcid).ToArray());
            CollectionAssert.AreEqual(wave.Select(e => e.Role).ToArray(), moved.Select(e => e.Role).ToArray());
            CollectionAssert.AreEqual(new[] { 400, 0, 390, 0 }, moved.Select(e => e.UpliftLevel).ToArray());
            CollectionAssert.AreEqual(new[] { 0, 380, 0, 0 }, moved.Select(e => e.StampLevel).ToArray());
            Assert.IsTrue(moved[2].Stretched);
            Assert.AreSame(room.Points[0], moved[0].Point);
            Assert.AreSame(room.Points[1], moved[1].Point);
            Assert.AreSame(room.Points[2], moved[2].Point);
            Assert.AreSame(room.BossPoint, moved[3].Point);
        }

        [TestMethod]
        public void Repoint_refuses_a_wave_with_more_entries_than_room_points()
        {
            var wave = new List<DungeonSpawnPlanEntry>
            {
                new DungeonSpawnPlanEntry(10, DungeonRole.Trash, Pt(1)),
                new DungeonSpawnPlanEntry(11, DungeonRole.Trash, Pt(2)),
            };

            Assert.ThrowsExactly<TrialArchetypeException>(() => TrialWavePlan.Repoint(wave, Room(1)));
        }

        private static string SourceOf(string file)
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);

            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "ACE.Server", "BalanceTrials", "TrialScenario.cs")))
                dir = dir.Parent;

            Assert.IsNotNull(dir, $"Could not find ACE.Server/BalanceTrials/TrialScenario.cs by walking up from {AppContext.BaseDirectory}");

            return File.ReadAllText(Path.Combine(dir.FullName, "ACE.Server", "BalanceTrials", file));
        }

        [TestMethod]
        public void BuildPlan_calls_BuildLivePlan_with_solo_group()
        {
            var src = SourceOf("TrialWavePlan.cs");

            StringAssert.Contains(src, "ThreadDungeonSpawner.BuildLivePlan(spec, dungeon, GroupScaling.Solo, sources,");
            Assert.IsFalse(System.Text.RegularExpressions.Regex.IsMatch(src, @"with\s*\{\s*Store"), "never copy LivePlanSources with a replaced Store: FitOf keeps the live store");
        }
    }
}
