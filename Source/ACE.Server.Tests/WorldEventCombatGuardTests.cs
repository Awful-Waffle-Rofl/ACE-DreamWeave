using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

using ACE.Entity.Enum;
using ACE.Server.ThreadDungeons;
using ACE.Server.WorldEvents;
using ACE.Server.WorldEvents.Defs;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ACE.Server.Tests
{
    /// <summary>
    /// World Events combat guard (world_events_strip_combat_traits): who is guarded, the WE-owned dials and
    /// their sanitizing, the slot level the standard is measured at, and the spawn-site wiring. The writes on
    /// a Creature are CombatTraitGuard.Apply (shared with Threads; its numbers are covered by
    /// DungeonCombatNormalizerTests); a live Creature cannot be built here, so the live check is deferred.
    /// </summary>
    [TestClass]
    public class WorldEventCombatGuardTests
    {
        private Func<WorldEventCombatGuard.GuardDials> originalGuard;
        private Func<WorldEventRosterSelector.UpliftDials> originalUplift;
        private Func<BandDials> originalDials;

        [TestInitialize]
        public void Pin()
        {
            originalGuard = WorldEventCombatGuard.DialSource;
            originalUplift = WorldEventRosterSelector.UpliftDialSource;
            originalDials = WorldEventRosterSelector.DialSource;

            WorldEventRosterSelector.DialSource = () => BandDials.Defaults;
        }

        [TestCleanup]
        public void Restore()
        {
            WorldEventCombatGuard.DialSource = originalGuard;
            WorldEventRosterSelector.UpliftDialSource = originalUplift;
            WorldEventRosterSelector.DialSource = originalDials;
        }

        // ---- who is guarded -------------------------------------------------------------------------------

        [TestMethod]
        public void WaveKind_IsGuarded()
        {
            Assert.IsTrue(WorldEventCombatGuard.AppliesTo(WorldEventSpawnKind.Wave, isNamedBoss: false));
        }

        [TestMethod]
        public void NamedBoss_IsExempt_ButTheFamilyChampionIsGuarded()
        {
            Assert.IsFalse(WorldEventCombatGuard.AppliesTo(WorldEventSpawnKind.Boss, isNamedBoss: true), "the named boss keeps its authored immunities");
            Assert.IsTrue(WorldEventCombatGuard.AppliesTo(WorldEventSpawnKind.Boss, isNamedBoss: false), "the family champion is a roster monster");
        }

        [TestMethod]
        public void NonMonsterKinds_AreNeverGuarded()
        {
            foreach (var kind in new[] { WorldEventSpawnKind.Source, WorldEventSpawnKind.Decor, WorldEventSpawnKind.Npc, WorldEventSpawnKind.InertObjective, WorldEventSpawnKind.Add })
                Assert.IsFalse(WorldEventCombatGuard.AppliesTo(kind, false), kind.ToString());
        }

        // ---- the dials ------------------------------------------------------------------------------------

        [TestMethod]
        public void Defaults_AreTheOwnerValues_AndTheCategoryDialsMatchThreads()
        {
            var d = WorldEventCombatGuard.GuardDials.Defaults;

            Assert.IsTrue(d.Enabled);
            Assert.AreEqual(50.0, d.DefenseSkillCapOffset, "WE ships a tighter defense ceiling than Threads' 100");
            Assert.AreEqual(DungeonPopulationLimits.DefaultCategoryResistFloor, d.CategoryResistFloor);
            Assert.AreEqual(DungeonPopulationLimits.DefaultCategoryArmorModCeiling, d.CategoryArmorModCeiling);
        }

        [TestMethod]
        public void Sanitize_FollowsTheThreadsRules()
        {
            var bad = WorldEventCombatGuard.Sanitize(new WorldEventCombatGuard.GuardDials(true, double.NaN, -3, double.PositiveInfinity));

            Assert.AreEqual(WorldEventCombatGuard.DefaultCategoryResistFloor, bad.CategoryResistFloor);
            Assert.AreEqual(WorldEventCombatGuard.DefaultCategoryArmorModCeiling, bad.CategoryArmorModCeiling);
            Assert.AreEqual(WorldEventCombatGuard.DefaultDefenseSkillCapOffset, bad.DefenseSkillCapOffset);

            var big = WorldEventCombatGuard.Sanitize(new WorldEventCombatGuard.GuardDials(true, 9, 99, 99999));

            Assert.AreEqual(DungeonPopulationLimits.MaxCategoryResistFloor, big.CategoryResistFloor);
            Assert.AreEqual(DungeonPopulationLimits.MaxCategoryArmorModCeiling, big.CategoryArmorModCeiling);
            Assert.AreEqual(DungeonPopulationLimits.MaxDefenseSkillCapOffset, big.DefenseSkillCapOffset);

            var disabled = WorldEventCombatGuard.Sanitize(new WorldEventCombatGuard.GuardDials(true, 0, 0, -1));

            Assert.AreEqual(0.0, disabled.CategoryResistFloor, "0 is the documented disable for a category dial");
            Assert.AreEqual(-1.0, disabled.DefenseSkillCapOffset, "a negative offset passes through: it disables the cap");
        }

        [TestMethod]
        public void Current_ReadsTheDialSeam_AndForwardsEveryNumberToTheSharedGuard()
        {
            WorldEventCombatGuard.DialSource = () => new WorldEventCombatGuard.GuardDials(true, 0.25, 3.5, 20);

            var combat = WorldEventCombatGuard.Current().ToCombatDials();

            Assert.AreEqual(0.25, combat.CategoryResistFloor);
            Assert.AreEqual(3.5, combat.CategoryArmorModCeiling);
            Assert.AreEqual(20.0, combat.DefenseSkillCapOffset);
        }

        [TestMethod]
        public void DefenseCap_IsTheBandEffectiveMedianPlusTheWeOffset_AndNegativeDisablesIt()
        {
            var standard = DungeonBandStandard.ForTest(new Dictionary<Skill, uint>(), 100, 100,
                effectiveDefenseMedians: new Dictionary<Skill, uint> { [Skill.MeleeDefense] = 400 });

            Assert.AreEqual(450u, WorldEventCombatGuard.DefenseCapFor(standard, Skill.MeleeDefense, WorldEventCombatGuard.GuardDials.Defaults),
                "400 + WE offset 50 (Threads' default offset would give 500)");

            var off = new WorldEventCombatGuard.GuardDials(true, 0.5, 2.0, -1);
            Assert.AreEqual(0u, WorldEventCombatGuard.DefenseCapFor(standard, Skill.MeleeDefense, off), "a negative offset disables the ceiling");

            var zero = new WorldEventCombatGuard.GuardDials(true, 0.5, 2.0, 0);
            Assert.AreEqual(400u, WorldEventCombatGuard.DefenseCapFor(standard, Skill.MeleeDefense, zero), "0 caps exactly at the median");
        }

        // ---- the slot level the standard is measured at -----------------------------------------------------

        private static FamilyMember Member(uint wcid, int level, int role = 0)
            => new FamilyMember { Wcid = wcid, Name = $"m{wcid}", Level = level, Role = role };

        private static SourceThemeDef Theme()
            => new SourceThemeDef { Id = "ambush", MaxAlive = 24, WaveCount = new ScaledCount { Base = 8, PerParticipant = 0, Cap = 8 } };

        [TestMethod]
        public void SlotLevels_AreRecordedForEverySlot_EvenWhenNothingIsUplifted()
        {
            // Deep, in-band family: no slot is uplifted, yet every slot must still carry its L for the guard.
            var family = new FamilyDef { Id = "f", Members = Enumerable.Range(0, 8).Select(i => Member((uint)(1 + i), 200)).ToList() };
            var est = new AudienceEstimate(2, 150, 200, new[] { 100, 200 });

            var pick = WorldEventRosterSelector.PickWave(family, est, Theme(), 2, 0, new Random(5));

            Assert.AreEqual(pick.Trash.Count, pick.TrashSlotLevels.Count);
            Assert.IsTrue(pick.TrashSlotLevels.All(l => l == 100 || l == 200), "each slot's L is one of the sampled participant levels");
            Assert.IsTrue(pick.TrashSlotLevels.Distinct().Count() == 2, "the seeded draw hits both participants");
        }

        [TestMethod]
        public void ChampionPick_CarriesTheP90SlotLevel()
        {
            var family = new FamilyDef { Id = "f", Members = new List<FamilyMember> { Member(1, 150), Member(2, 180) } };
            var est = new AudienceEstimate(1, 275, 275, new[] { 275 });

            Assert.AreEqual(275, WorldEventRosterSelector.PickChampion(family, est).SlotLevel);
        }

        // ---- wiring pins ----------------------------------------------------------------------------------

        [TestMethod]
        public void Spawn_RunsTheGuardAfterTheUplift_WithTheEntrySlotLevel()
        {
            var lines = File.ReadAllLines(Path.Combine(FindSourceRoot(), "WorldEvents", "WorldEventSpawner.cs"));

            int Find(string code) => Array.FindIndex(lines, l => l.Trim() == code);

            var uplift = Find("ApplyBandUplift(evt, creature, entry.UpliftLevel, kind);");
            var guard = Find("ApplyCombatGuard(evt, creature, entry.SlotLevel, kind);");
            var multiplier = Find("ApplyHealthMultiplier(evt, creature, healthMult);");

            Assert.IsTrue(uplift > 0 && guard > 0 && multiplier > 0, "anchor lines must exist exactly once");
            Assert.IsTrue(uplift < guard, "guard after the uplift so the defense ceiling sees the final skills");
            Assert.IsTrue(guard < multiplier, "guard before the health multiplier");
        }

        [TestMethod]
        public void ApplyCombatGuard_ExemptsTheNamedBoss_ThroughTheSharedPredicate()
        {
            var text = File.ReadAllText(Path.Combine(FindSourceRoot(), "WorldEvents", "WorldEventSpawner.cs"));

            Assert.IsTrue(text.Contains("var named = kind == WorldEventSpawnKind.Boss && evt?.Composition?.Boss?.Kind == BossKind.Named;"));
            Assert.IsTrue(text.Contains("if (!WorldEventCombatGuard.AppliesTo(kind, named))"));
            Assert.IsTrue(text.Contains("CombatTraitGuard.Apply(creature, dials.ToCombatDials(), standard, isBoss: false)"));
        }

        [TestMethod]
        public void WorldEvent_ForwardsTheChampionSlotLevel()
        {
            var text = File.ReadAllText(Path.Combine(FindSourceRoot(), "WorldEvents", "WorldEvent.cs"));

            Assert.IsTrue(text.Contains("championSlotLevel = pick.SlotLevel;"));
            Assert.IsTrue(text.Contains("Spawner.SpawnBoss(this, wcid, anchor, healthMult, synthetic, championUplift, championSlotLevel);"));
        }

        [TestMethod]
        public void TheGuardCode_ReadsNoThreadsTunable()
        {
            foreach (var file in new[] { "WorldEventCombatGuard.cs", "WorldEventBandMemo.cs", "WorldEventSpawner.cs", "WorldEventRosterSelector.cs" })
            {
                var lines = File.ReadAllLines(Path.Combine(FindSourceRoot(), "WorldEvents", file));

                for (var i = 0; i < lines.Length; i++)
                {
                    var code = lines[i];
                    var commentAt = code.IndexOf("//", StringComparison.Ordinal);

                    if (commentAt >= 0)
                        code = code.Substring(0, commentAt);

                    Assert.IsFalse(code.Contains("\"dynamic_dungeons_"), $"{file}:{i + 1} reads a Threads tunable; World Events owns its own");
                }
            }
        }

        private static string FindSourceRoot()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);

            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "ACE.Server", "WorldEvents", "WorldEventCombatGuard.cs")))
                dir = dir.Parent;

            Assert.IsNotNull(dir, $"Could not find ACE.Server/WorldEvents/WorldEventCombatGuard.cs by walking up from {AppContext.BaseDirectory}");

            return Path.Combine(dir.FullName, "ACE.Server");
        }
    }
}
