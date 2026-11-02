using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

using ACE.Server.WorldEvents;
using ACE.Server.WorldEvents.Defs;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ACE.Server.Tests
{
    /// <summary>
    /// The World Events Hold DEFINITIONS and their goals.json parse (ProtectDef, the wave and shrine defs, the load-time
    /// checks in WorldEventAxisStore). Pure data: no runtime, no PropertyManager, no database. Ported from the parse
    /// halves of DreamWeave #17's WorldEventHoldTests / WorldEventHoldSurvivalTests, with the wcid literals moved to the
    /// reserved block 1007650-1007699 (knights 1007650-1007652, shrine 1007653).
    /// </summary>
    [TestClass]
    public class WorldEventHoldDefsTests
    {
        private const uint KnightWcid = 1007651;
        private const uint ShrineWcid = 1007653;

        // ---- builders ----------------------------------------------------------------------------------------

        private static ProtectDef BuildProtect(int minSurvivors = 1, double seekShare = 0.3, int countBase = 1,
            double countPerParticipant = 0, int countCap = 3)
        {
            return new ProtectDef
            {
                Wcid = 1007650,
                Count = new ScaledCount { Base = countBase, PerParticipant = countPerParticipant, Cap = countCap },
                Health = new ScaledCount { Base = 20000, PerParticipant = 4000, Cap = 200000 },
                Radius = 6.0,
                MinSurvivors = minSurvivors,
                SeekShare = seekShare,
                Noun = "ward",
                NounPlural = "wards"
            };
        }

        private const string ValidProtectJson =
            @"""protect"": { ""wcid"": 1007650, ""count"": { ""base"": 1, ""perParticipant"": 0.2, ""cap"": 3 },
                ""health"": { ""base"": 20000, ""perParticipant"": 4000, ""cap"": 200000 }, ""radius"": 6.0,
                ""minSurvivors"": 1, ""seekShare"": 0.3, ""healthMilestones"": true, ""noun"": ""ward"", ""nounPlural"": ""wards"" }";

        private const string SampleProtectJson =
            @"""protect"": { ""wcid"": 1007651, ""count"": { ""base"": 3, ""perParticipant"": 0, ""cap"": 3 },
                ""radius"": 6.0, ""spawnRadius"": 25.0, ""undefendedSeconds"": 120.0, ""minDeathSeconds"": 45.0,
                ""minSurvivors"": 1, ""seekShare"": 0.8, ""healthMilestones"": true,
                ""noun"": ""envoy"", ""nounPlural"": ""envoys"" }";

        private static string HoldGoalsJson(string protectJson, int holdSeconds = 420)
        {
            var protectPart = protectJson == null ? "" : ", " + protectJson;

            return @"{ ""goals"": [ { ""id"": ""hold_wards"", ""displayName"": ""Defend the Wards"", ""type"": ""Hold"",
                ""holdSeconds"": " + holdSeconds + @", ""mvpRule"": ""mostKills"",
                ""progressTemplate"": ""{alive} of {total} {nounPlural} stand. Hold for {remaining}.""" + protectPart + " } ] }";
        }

        private static WorldEventAxisStore ParseHold(string protectJson, int holdSeconds = 420)
        {
            return WorldEventAxisStore.Parse(null, null, HoldGoalsJson(protectJson, holdSeconds), null, null);
        }

        private static void AssertAscii(string text)
        {
            Assert.IsNotNull(text);
            Assert.IsFalse(text.Contains((char)0x2013), $"en dash in player-facing text: {text}");
            Assert.IsFalse(text.Contains((char)0x2014), $"em dash in player-facing text: {text}");
            Assert.IsTrue(text.All(c => c < 128), $"non-ASCII character in player-facing text: {text}");
        }

        // ---- ProtectDef as plain data -------------------------------------------------------------------------

        [TestMethod]
        public void ProtectDef_CountScalesWithPlayers_WithinOneToThree()
        {
            var protect = BuildProtect(countBase: 1, countPerParticipant: 0.2, countCap: 3);

            Assert.AreEqual(1, protect.ResolveCount(0));
            Assert.AreEqual(2, protect.ResolveCount(5));
            Assert.AreEqual(3, protect.ResolveCount(10));
            Assert.AreEqual(3, protect.ResolveCount(100), "capped");
            Assert.IsTrue(protect.ResolveCount(-4) >= 1);
        }

        [TestMethod]
        public void ProtectDef_HealthScalesWithPlayers()
        {
            var protect = BuildProtect();

            Assert.AreEqual(20000, protect.ResolveHealth(0));
            Assert.AreEqual(60000, protect.ResolveHealth(10));
            Assert.AreEqual(200000, protect.ResolveHealth(1000), "capped");
        }

        [TestMethod]
        public void ProtectDef_Defaults_AreTheContributorsDefaults()
        {
            var protect = new ProtectDef();

            Assert.AreEqual("ward", protect.EffectiveNoun);
            Assert.AreEqual("wards", protect.EffectiveNounPlural);
            Assert.AreEqual(1, protect.MinSurvivors);
            Assert.AreEqual(0.3, protect.SeekShare, 1e-9);
            Assert.IsTrue(protect.HealthMilestones);
            Assert.IsTrue(protect.ShowHealthBar);
            Assert.AreEqual(180.0, protect.MinSeconds, 1e-9);
            Assert.AreEqual(0.10, protect.MaxHealPerTenSeconds, 1e-9);
            Assert.AreEqual(2, protect.SpecialWaveFromWave);
            Assert.IsFalse(protect.IsWaveBased);
            Assert.IsFalse(protect.IsTimeScaled);
            Assert.AreEqual(ProtectDef.TimeScaledMaxHealth, new ProtectDef { UndefendedSeconds = 60 }.EffectiveMaxHealth(5));
        }

        [TestMethod]
        public void ProtectDef_SurvivalMechanics_AreOffByDefault()
        {
            var p = new ProtectDef();

            Assert.AreEqual(0, p.CorruptedKillHeal);
            Assert.AreEqual(0, p.RescueHeal);
            Assert.AreEqual(0, p.ClearRegenPerSecond);
            Assert.AreEqual(0, p.HealBudgetPerWave, "no budget unless the goal sets one");
            Assert.IsNull(p.Shrine);
            Assert.IsNull(p.DefendPose);
            Assert.IsNull(p.Waves);
            Assert.IsFalse(p.NextWaveRequiresBoth);
            Assert.IsFalse(p.BossTargetsProtected);
            Assert.AreEqual(0, p.UndefendedSecondsPerParticipant);
            Assert.AreEqual(0.15, p.ShrineHeal, 1e-9);
            Assert.AreEqual(45.0, p.ShrineCooldownSeconds, 1e-9);
        }

        [TestMethod]
        public void ProtectDef_WcidFor_CyclesThroughTheList_ElseUsesWcid()
        {
            var protect = new ProtectDef { Wcid = 1007650, Wcids = new List<uint> { 1007650, 1007651, 1007652 } };

            CollectionAssert.AreEqual(new uint[] { 1007650, 1007651, 1007652, 1007650 },
                Enumerable.Range(0, 4).Select(protect.WcidFor).ToArray());

            Assert.AreEqual(1007650u, new ProtectDef { Wcid = 1007650 }.WcidFor(5), "no list: every target is wcid");
            Assert.AreEqual(1007651u, new ProtectDef { Wcids = new List<uint> { 1007651 } }.WcidFor(0), "a list alone is enough");
        }

        [TestMethod]
        public void ProtectDef_ResolveLayout_PicksTheHighestMinPlayersNotAboveTheCrowd()
        {
            var protect = BuildProtect(countBase: 1, countCap: 3);
            protect.CountByParticipants = new List<TargetLayoutDef>
            {
                new TargetLayoutDef { MinPlayers = 0, Count = 1, Radius = 4 },
                new TargetLayoutDef { MinPlayers = 5, Count = 2, Radius = 8 },
                new TargetLayoutDef { MinPlayers = 10, Count = 3, Radius = 12 }
            };

            Assert.AreEqual((1, 4.0), protect.ResolveLayout(1));
            Assert.AreEqual((2, 8.0), protect.ResolveLayout(7));
            Assert.AreEqual((3, 12.0), protect.ResolveLayout(40));
            Assert.AreEqual(12.0, protect.MaxRingRadius, 1e-9);
        }

        [TestMethod]
        public void ScaledCount_PlayerWindow_IsIdentityByDefault_AndShapesGrowthWhenSet()
        {
            var plain = new ScaledCount { Base = 4, PerParticipant = 1, Cap = 100 };

            Assert.AreEqual(0, plain.MapPlayers(0));
            Assert.AreEqual(9, plain.MapPlayers(9));
            Assert.AreEqual(13, plain.Resolve(9), "unchanged: base + perParticipant * players");

            var windowed = new ScaledCount { Base = 4, PerParticipant = 1, Cap = 100, PlayersFrom = 3, PlayersUpTo = 8 };

            Assert.AreEqual(0, windowed.MapPlayers(2), "growth starts after PlayersFrom");
            Assert.AreEqual(5, windowed.MapPlayers(20), "growth stops at PlayersUpTo");
            Assert.AreEqual(9, windowed.Resolve(20));
        }

        [TestMethod]
        public void SourceThemeDef_WithHoldSizing_CopiesOnlyWhenTheGoalSizesWaves()
        {
            var theme = new SourceThemeDef { MaxAlive = 12, WaveCount = new ScaledCount { Base = 5, Cap = 9 } };

            Assert.AreSame(theme, theme.WithHoldSizing(null, 0));

            var sized = theme.WithHoldSizing(new ScaledCount { Base = 8, Cap = 8 }, 20);

            Assert.AreNotSame(theme, sized);
            Assert.AreEqual(8, sized.WaveCount.Base);
            Assert.AreEqual(20, sized.MaxAlive);
            Assert.AreEqual(5, theme.WaveCount.Base, "the original is untouched");
            Assert.AreEqual(12, theme.MaxAlive);
        }

        // ---- the parse helpers ---------------------------------------------------------------------------------

        [TestMethod]
        public void RepairDashes_ReplacesEmAndEnDashes()
        {
            Assert.IsTrue(WorldEventHoldRules.RepairDashes("Hold " + (char)0x2014 + " now " + (char)0x2013 + " go", out var fixedText));
            Assert.AreEqual("Hold - now - go", fixedText);

            Assert.IsFalse(WorldEventHoldRules.RepairDashes("Already - fine", out var same));
            Assert.AreEqual("Already - fine", same);
            Assert.IsFalse(WorldEventHoldRules.RepairDashes(null, out _));
        }

        [TestMethod]
        public void ParseHelpers_AcceptTheDocumentedShapes_AndRefuseTheRest()
        {
            Assert.IsTrue(WorldEventHoldRules.TryParseCombatKinds(new[] { "Melee", " magic " }, out var kinds));
            Assert.AreEqual(WorldEventCombatKinds.Melee | WorldEventCombatKinds.Magic, kinds);
            Assert.IsFalse(WorldEventHoldRules.TryParseCombatKinds(new[] { "fire" }, out _));
            Assert.IsFalse(WorldEventHoldRules.TryParseCombatKinds(new string[0], out _));

            Assert.IsTrue(WorldEventHoldRules.IsValidBossPrefix("Warlord"));
            Assert.IsTrue(WorldEventHoldRules.IsValidBossPrefix(""));
            Assert.IsFalse(WorldEventHoldRules.IsValidBossPrefix("Lord7"));
            Assert.IsFalse(WorldEventHoldRules.IsValidBossPrefix(new string('a', 21)));

            Assert.IsTrue(WorldEventHoldRules.TryParseBossEffect("none", out _));
            Assert.IsTrue(WorldEventHoldRules.TryParseBossEffect("shieldupred", out var script));
            Assert.AreEqual(ACE.Entity.Enum.PlayScript.ShieldUpRed, script);
            Assert.IsFalse(WorldEventHoldRules.TryParseBossEffect("Sparkles", out _));

            Assert.IsTrue(WorldEventHoldRules.TryParseWaveType("Runners", out var type));
            Assert.AreEqual(HoldWaveType.Runners, type);
            Assert.IsFalse(WorldEventHoldRules.TryParseWaveType("normal", out _));
            Assert.IsFalse(WorldEventHoldRules.TryParseWaveType("mixed", out _));

            Assert.IsTrue(WorldEventTint.TryParsePalette("0x04001381", out var palette));
            Assert.AreEqual(0x04001381u, palette);
            Assert.IsTrue(WorldEventTint.TryParsePalette("none", out palette));
            Assert.AreEqual(0u, palette);
            Assert.IsFalse(WorldEventTint.TryParsePalette("0x05000000", out _));
            Assert.IsFalse(WorldEventTint.TryParsePalette("blue", out _));
        }

        // ---- goals.json: the existing goals load exactly as before -------------------------------------------------

        private static string FindAxesDir()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);

            while (dir != null)
            {
                var candidate = Path.Combine(dir.FullName, "Content", "events", "axes");

                if (File.Exists(Path.Combine(candidate, "goals.json")))
                    return candidate;

                dir = dir.Parent;
            }

            return null;
        }

        [TestMethod]
        public void ShippedGoals_NonHoldGoals_LoadUnchanged()
        {
            var axes = FindAxesDir();

            if (axes == null)
                Assert.Fail("Could not locate Content/events/axes by walking up from the test assembly; it is in-tree and this test pins that the shipped goals load unchanged.");

            var store = WorldEventAxisStore.Parse(null, null, File.ReadAllText(Path.Combine(axes, "goals.json")), null, null);

            Assert.AreEqual(0, store.Diagnostics.Count, string.Join(" | ", store.Diagnostics));
            CollectionAssert.AreEquivalent(new[] { "kill_count", "destroy_source", "kill_boss", "defend_envoys" }, store.Goals.Keys.ToArray());
            Assert.AreEqual(1, store.Goals.Values.Count(g => g.TypeKind == GoalType.Hold), "exactly one shipped goal (defend_envoys) is a Hold goal; the others carry no Hold data");

            var killCount = store.Goals["kill_count"];
            Assert.AreEqual(GoalType.KillCount, killCount.TypeKind);
            Assert.AreEqual(MvpRule.MostKills, killCount.RuleKind);
            Assert.AreEqual("Defend Against the Waves", killCount.DisplayName);
            Assert.AreEqual(20, killCount.Count.Base);
            Assert.AreEqual(6.0, killCount.Count.PerParticipant, 1e-9);
            Assert.AreEqual(150, killCount.Count.Cap);
            Assert.AreEqual(0, killCount.HoldSeconds);
            Assert.AreEqual("The waves at {anchor} have been repelled!", killCount.SuccessTemplate);

            var destroy = store.Goals["destroy_source"];
            Assert.AreEqual(GoalType.DestroySource, destroy.TypeKind);
            Assert.AreEqual(MvpRule.KillingBlowAndTopDamage, destroy.RuleKind);
            Assert.AreEqual(1, destroy.Count.Cap);

            var boss = store.Goals["kill_boss"];
            Assert.AreEqual(GoalType.KillBoss, boss.TypeKind);
            Assert.AreEqual("The champion still stands.", boss.ProgressTemplate);

            foreach (var goal in store.Goals.Values.Where(g => g.TypeKind != GoalType.Hold))
            {
                Assert.IsNull(goal.Protect, goal.Id + " has no protect block");
                Assert.AreEqual(0, goal.Count.PlayersFrom, goal.Id);
                Assert.AreEqual(0, goal.Count.PlayersUpTo, goal.Id);
                Assert.IsFalse(goal.Count.OwnsBase, goal.Id);
            }
        }

        // ---- goals.json: Hold goals -----------------------------------------------------------------------------

        [TestMethod]
        public void GoalsJson_ValidHoldGoal_Parses()
        {
            var store = ParseHold(ValidProtectJson);

            Assert.IsTrue(store.Goals.ContainsKey("hold_wards"), string.Join(" | ", store.Diagnostics));

            var goal = store.Goals["hold_wards"];

            Assert.AreEqual(GoalType.Hold, goal.TypeKind);
            Assert.AreEqual(420, goal.HoldSeconds);
            Assert.AreEqual(1007650u, goal.Protect.Wcid);
            Assert.AreEqual(3, goal.Protect.Count.Cap);
            Assert.AreEqual(6.0, goal.Protect.Radius, 1e-9);
            Assert.AreEqual(0.3, goal.Protect.SeekShare, 1e-9);
            Assert.AreEqual("wards", goal.Protect.NounPlural);
            Assert.IsFalse(store.Diagnostics.Any(d => d.Contains("hold_wards")));
        }

        [TestMethod]
        public void GoalsJson_HoldWithoutProtect_IsDropped()
        {
            var store = ParseHold(null);

            Assert.IsFalse(store.Goals.ContainsKey("hold_wards"));
            Assert.IsTrue(store.Diagnostics.Any(d => d.Contains("hold_wards") && d.Contains("no protect block")));
        }

        [TestMethod]
        [DataRow(@"""wcid"": 0", "no wcid")]
        [DataRow(@"""count"": { ""base"": 0, ""perParticipant"": 0, ""cap"": 3 }", "protect.count")]
        [DataRow(@"""count"": { ""base"": 1, ""perParticipant"": 0, ""cap"": 4 }", "protect.count")]
        [DataRow(@"""count"": { ""base"": 3, ""perParticipant"": 0, ""cap"": 2 }", "protect.count")]
        [DataRow(@"""health"": { ""base"": 0, ""perParticipant"": 0, ""cap"": 10 }", "protect.health")]
        [DataRow(@"""radius"": -1", "protect.radius")]
        [DataRow(@"""seekShare"": 1.5", "protect.seekShare")]
        [DataRow(@"""seekShare"": -0.1", "protect.seekShare")]
        [DataRow(@"""minSurvivors"": 0", "protect.minSurvivors")]
        public void GoalsJson_InvalidProtectField_DropsTheGoal(string fieldJson, string expectedDiagnostic)
        {
            var key = fieldJson.Substring(0, fieldJson.IndexOf(':'));
            var fields = new Dictionary<string, string>
            {
                [@"""wcid"""] = @"""wcid"": 1007650",
                [@"""count"""] = @"""count"": { ""base"": 1, ""perParticipant"": 0.2, ""cap"": 3 }",
                [@"""health"""] = @"""health"": { ""base"": 20000, ""perParticipant"": 4000, ""cap"": 200000 }",
                [@"""radius"""] = @"""radius"": 6.0",
                [@"""seekShare"""] = @"""seekShare"": 0.3",
                [@"""minSurvivors"""] = @"""minSurvivors"": 1"
            };

            fields[key] = fieldJson;

            var store = ParseHold(@"""protect"": { " + string.Join(", ", fields.Values) + " }");

            Assert.IsFalse(store.Goals.ContainsKey("hold_wards"), $"{fieldJson} must drop the goal");
            Assert.IsTrue(store.Diagnostics.Any(d => d.Contains("hold_wards") && d.Contains(expectedDiagnostic)),
                "diagnostics: " + string.Join(" | ", store.Diagnostics));
        }

        [TestMethod]
        public void GoalsJson_HoldSecondsZero_IsStillDropped()
        {
            var store = ParseHold(ValidProtectJson, holdSeconds: 0);

            Assert.IsFalse(store.Goals.ContainsKey("hold_wards"));
            Assert.IsTrue(store.Diagnostics.Any(d => d.Contains("hold_wards") && d.Contains("holdSeconds")));
        }

        [TestMethod]
        public void GoalsJson_NonHoldGoalIgnoresAProtectBlock()
        {
            var json = @"{ ""goals"": [ { ""id"": ""kill_count"", ""displayName"": ""Kill Count"", ""type"": ""KillCount"",
                ""count"": { ""base"": 20, ""perParticipant"": 6, ""cap"": 150 }, ""holdSeconds"": 0,
                ""mvpRule"": ""mostKills"", ""progressTemplate"": ""t"", ""protect"": { ""wcid"": 0 } } ] }";

            var store = WorldEventAxisStore.Parse(null, null, json, null, null);

            Assert.IsTrue(store.Goals.ContainsKey("kill_count"), "only a Hold goal validates its protect block");
        }

        [TestMethod]
        public void GoalsJson_SpawnRadius_ParsesAndValidates()
        {
            var withRadius = ValidProtectJson.Replace(@"""radius"": 6.0,", @"""radius"": 6.0, ""spawnRadius"": 25.0,");
            var store = ParseHold(withRadius);

            Assert.AreEqual(25.0, store.Goals["hold_wards"].Protect.SpawnRadius, 1e-9);

            var negative = ValidProtectJson.Replace(@"""radius"": 6.0,", @"""radius"": 6.0, ""spawnRadius"": -1,");
            store = ParseHold(negative);

            Assert.IsFalse(store.Goals.ContainsKey("hold_wards"));
            Assert.IsTrue(store.Diagnostics.Any(d => d.Contains("protect.spawnRadius")));
            AssertAscii(store.Diagnostics.First(d => d.Contains("protect.spawnRadius")));

            store = ParseHold(ValidProtectJson);
            Assert.AreEqual(0.0, store.Goals["hold_wards"].Protect.SpawnRadius, "absent means keep the source's radius");
        }

        [TestMethod]
        public void GoalsJson_UndefendedSeconds_ParsesAndValidates()
        {
            var json = ValidProtectJson.Replace(@"""radius"": 6.0,", @"""radius"": 6.0, ""undefendedSeconds"": 75.0,");
            var store = ParseHold(json);

            Assert.AreEqual(75.0, store.Goals["hold_wards"].Protect.UndefendedSeconds, 1e-9);

            var bad = ValidProtectJson.Replace(@"""radius"": 6.0,", @"""radius"": 6.0, ""undefendedSeconds"": -10,");
            store = ParseHold(bad);

            Assert.IsFalse(store.Goals.ContainsKey("hold_wards"));
            Assert.IsTrue(store.Diagnostics.Any(d => d.Contains("protect.undefendedSeconds")));
            AssertAscii(store.Diagnostics.First(d => d.Contains("protect.undefendedSeconds")));

            store = ParseHold(ValidProtectJson);
            Assert.AreEqual(0.0, store.Goals["hold_wards"].Protect.UndefendedSeconds, "absent means raw damage");
        }

        [TestMethod]
        public void GoalsJson_MinDeathSeconds_Validates()
        {
            var bad = SampleProtectJson.Replace(@"""minDeathSeconds"": 45.0", @"""minDeathSeconds"": -1");
            var store = ParseHold(bad);

            Assert.IsFalse(store.Goals.ContainsKey("hold_wards"));
            Assert.IsTrue(store.Diagnostics.Any(d => d.Contains("protect.minDeathSeconds")));
            AssertAscii(store.Diagnostics.First(d => d.Contains("protect.minDeathSeconds")));
        }

        [TestMethod]
        public void GoalsJson_HealthRequiredOnlyForRawDamage_AndFlaggedWhenDead()
        {
            // Time-based without health: fine, nothing to say.
            var store = ParseHold(SampleProtectJson);
            Assert.IsTrue(store.Goals.ContainsKey("hold_wards"));
            Assert.AreEqual(0, store.Diagnostics.Count);

            // Time-based WITH health: kept, but the dead setting is called out.
            var withHealth = SampleProtectJson.Replace(@"""radius"": 6.0,",
                @"""health"": { ""base"": 20000, ""perParticipant"": 4000, ""cap"": 200000 }, ""radius"": 6.0,");
            store = ParseHold(withHealth);

            Assert.IsTrue(store.Goals.ContainsKey("hold_wards"));
            var note = store.Diagnostics.FirstOrDefault(d => d.Contains("protect.health") && d.Contains("ignored"));
            Assert.IsNotNull(note, string.Join(" | ", store.Diagnostics));
            AssertAscii(note);

            // Raw damage WITHOUT health: still refused, health is what decides those.
            var rawNoHealth = SampleProtectJson.Replace(@"""undefendedSeconds"": 120.0, ", "");
            store = ParseHold(rawNoHealth);

            Assert.IsFalse(store.Goals.ContainsKey("hold_wards"));
            Assert.IsTrue(store.Diagnostics.Any(d => d.Contains("protect.health")));
        }

        [TestMethod]
        public void GoalsJson_Wcids_ParseAndValidate()
        {
            var json = SampleProtectJson.Replace(@"""wcid"": 1007651,", @"""wcids"": [ 1007650, 1007651, 1007652 ],");
            var store = ParseHold(json);

            Assert.IsTrue(store.Goals.ContainsKey("hold_wards"), string.Join(" | ", store.Diagnostics));
            Assert.AreEqual(1007652u, store.Goals["hold_wards"].Protect.WcidFor(2));

            var withZero = SampleProtectJson.Replace(@"""wcid"": 1007651,", @"""wcids"": [ 1007650, 0 ],");
            store = ParseHold(withZero);

            Assert.IsFalse(store.Goals.ContainsKey("hold_wards"));
            Assert.IsTrue(store.Diagnostics.Any(d => d.Contains("protect.wcids")));
        }

        [TestMethod]
        public void GoalsJson_SceneDecor_BadEntriesDropped_GoalKept()
        {
            var json = SampleProtectJson.Replace(@"""radius"": 6.0,",
                @"""decor"": [ { ""wcid"": 4128, ""scale"": 1.0, ""speed"": 1.0, ""snap"": true }, { ""wcid"": 0, ""scale"": 1.0, ""speed"": 1.0 } ],
                  ""targetDecor"": [ { ""wcid"": 28527, ""dy"": -2.8, ""scale"": 1.0, ""speed"": 1.0, ""yaw"": 90.0, ""snap"": true }, { ""wcid"": 147, ""scale"": 0, ""speed"": 1.0 } ],
                  ""radius"": 6.0,");

            var store = ParseHold(json);

            Assert.IsTrue(store.Goals.ContainsKey("hold_wards"), "a bad prop never drops the goal");

            var protect = store.Goals["hold_wards"].Protect;
            Assert.AreEqual(1, protect.Decor.Count);
            Assert.AreEqual(1, protect.TargetDecor.Count);
            Assert.IsFalse(protect.Decor[0].HoldShrine, "HoldShrine is runtime only and never read from JSON");
            Assert.IsTrue(store.Diagnostics.Any(d => d.Contains("protect.decor entry 1")));
            Assert.IsTrue(store.Diagnostics.Any(d => d.Contains("protect.targetDecor entry 1")));
        }

        [TestMethod]
        public void GoalsJson_WaveSettings_ParseAndValidate()
        {
            var json = SampleProtectJson.Replace(@"""radius"": 6.0,",
                @"""radius"": 11.0, ""waves"": { ""base"": 4, ""perParticipant"": 0.2, ""cap"": 10 }, ""minSeconds"": 300.0,
                  ""healBetweenWaves"": 0.15, ""maxHealPerTenSeconds"": 0.10,");

            var store = ParseHold(json, holdSeconds: 480);

            Assert.IsTrue(store.Goals.ContainsKey("hold_wards"), string.Join(" | ", store.Diagnostics));
            var p = store.Goals["hold_wards"].Protect;
            Assert.IsTrue(p.IsWaveBased);
            Assert.AreEqual(10, p.Waves.Cap);
            Assert.AreEqual(300.0, p.MinSeconds, 1e-9);
            Assert.AreEqual(0.15, p.HealBetweenWaves, 1e-9);
            Assert.AreEqual(0.10, p.MaxHealPerTenSeconds, 1e-9);

            foreach (var (bad, key) in new[]
            {
                (@"""waves"": { ""base"": 0, ""perParticipant"": 0, ""cap"": 4 },", "protect.waves"),
                (@"""minSeconds"": -1,", "protect.minSeconds"),
                (@"""healBetweenWaves"": 1.5,", "protect.healBetweenWaves"),
                (@"""maxHealPerTenSeconds"": -0.1,", "protect.maxHealPerTenSeconds")
            })
            {
                var badJson = SampleProtectJson.Replace(@"""radius"": 6.0,", @"""radius"": 6.0, " + bad);
                store = ParseHold(badJson);

                Assert.IsFalse(store.Goals.ContainsKey("hold_wards"), bad);
                Assert.IsTrue(store.Diagnostics.Any(d => d.Contains(key)), key);
            }

            // minSeconds above the safety cap can never be used.
            var tooLong = SampleProtectJson.Replace(@"""radius"": 6.0,",
                @"""radius"": 6.0, ""waves"": { ""base"": 4, ""perParticipant"": 0, ""cap"": 4 }, ""minSeconds"": 600,");
            store = ParseHold(tooLong, holdSeconds: 480);

            Assert.IsFalse(store.Goals.ContainsKey("hold_wards"));
            Assert.IsTrue(store.Diagnostics.Any(d => d.Contains("protect.minSeconds") && d.Contains("holdSeconds")));
        }

        [TestMethod]
        public void GoalsJson_WavesAboveTheCap_AreNoted()
        {
            var json = SampleProtectJson.Replace(@"""radius"": 6.0,",
                @"""radius"": 6.0, ""waves"": { ""base"": 4, ""perParticipant"": 0, ""cap"": 12 }, ""minSeconds"": 100,");
            var store = ParseHold(json, holdSeconds: 480);

            Assert.IsTrue(store.Goals.ContainsKey("hold_wards"), "noted, not dropped");
            Assert.IsTrue(store.Diagnostics.Any(d => d.Contains("protect.waves goes above " + ProtectDef.MaxHoldWaves)),
                string.Join(" | ", store.Diagnostics));
        }

        [TestMethod]
        public void GoalsJson_SurvivalSettings_Validate()
        {
            void Refused(string with, string expected)
            {
                var json = SampleProtectJson.Replace(@"""radius"": 6.0,", @"""radius"": 6.0, " + with);
                var store = ParseHold(json, holdSeconds: 480);

                Assert.IsFalse(store.Goals.ContainsKey("hold_wards"), with);
                Assert.IsTrue(store.Diagnostics.Any(d => d.Contains(expected)), string.Join(" | ", store.Diagnostics));
            }

            Refused(@"""healBudgetPerWave"": 1.5,", "heal share");
            Refused(@"""corruptedKillHeal"": -0.1,", "heal share");
            Refused(@"""clearRegenPerSecond"": 0.5,", "protect.clearRegenPerSecond");
            Refused(@"""clearRadius"": 99,", "protect.clearRadius");
            Refused(@"""shrineCooldownSeconds"": 0,", "protect.shrineCooldownSeconds");
            Refused(@"""shrine"": { ""wcid"": 0 },", "protect.shrine with no wcid");
            Refused(@"""shrine"": { ""name"": ""Shrine of <b>"" },", "protect.shrine.name");
            Refused(@"""shrine"": { ""readyEffect"": ""Sparkles"" },", "protect.shrine effect");
            Refused(@"""undefendedSecondsPerParticipant"": -5,", "protect.undefendedSecondsPerParticipant");
            Refused(@"""maxWaveIntervalSeconds"": -1,", "protect.maxWaveIntervalSeconds");

            var ok = SampleProtectJson.Replace(@"""radius"": 6.0,",
                @"""radius"": 6.0, ""healBudgetPerWave"": 0.25, ""corruptedKillHeal"": 0.1, ""rescueHeal"": 0.01, ""clearRegenPerSecond"": 0.005, ""shrine"": { },");
            var good = ParseHold(ok, holdSeconds: 480);

            Assert.IsTrue(good.Goals.ContainsKey("hold_wards"), string.Join(" | ", good.Diagnostics));
            Assert.AreEqual(ShrineWcid, good.Goals["hold_wards"].Protect.Shrine.Wcid, "an empty shrine block takes the defaults (the fork-reserved shrine wcid)");
        }

        [TestMethod]
        public void GoalsJson_LinesAreRepaired_BlanksDropped_DashesReplaced()
        {
            var json = ValidProtectJson.Replace(@"""radius"": 6.0,", @"""radius"": 6.0, ""lines"": { ""spawn"": [ ""Here come {family}!"", """" ],
                ""health"": [ ""We are down to {pct} percent! DASH"" ] },")
                .Replace("DASH", "\\u2014");

            var store = ParseHold(json);

            Assert.IsTrue(store.Goals.ContainsKey("hold_wards"), string.Join(" | ", store.Diagnostics));

            var lines = store.Goals["hold_wards"].Protect.Lines;
            Assert.AreEqual(1, lines.Spawn.Count, "the blank line is removed");
            Assert.AreEqual("We are down to {pct} percent! -", lines.Health[0], "the em dash is repaired to a hyphen");
            Assert.IsNotNull(lines.Destroyed, "an absent pool reads as empty, not null");
            Assert.IsTrue(store.Diagnostics.Any(d => d.Contains("protect.lines.spawn") && d.Contains("blank")));
            Assert.IsTrue(store.Diagnostics.Any(d => d.Contains("protect.lines.health") && d.Contains("dash")));
            store.Diagnostics.ToList().ForEach(AssertAscii);
        }

        // ---- a whole sample Hold goal: every block parses to the expected fields ------------------------------------

        private const string FullHoldGoalJson = @"{ ""goals"": [ {
            ""id"": ""hold_envoys"", ""displayName"": ""Defend the Envoys"", ""type"": ""hold"", ""holdSeconds"": 600,
            ""mvpRule"": ""mostKills"", ""progressTemplate"": ""{alive} of {total} {nounPlural} stand."",
            ""protect"": {
                ""wcids"": [ 1007650, 1007651, 1007652 ],
                ""count"": { ""base"": 3, ""perParticipant"": 0, ""cap"": 3 },
                ""radius"": 6.0, ""spawnRadius"": 30.0, ""undefendedSeconds"": 150.0, ""undefendedSecondsPerParticipant"": 5,
                ""undefendedSecondsFloor"": 60, ""minDeathSeconds"": 45.0, ""minSurvivors"": 2, ""seekShare"": 0.6,
                ""noun"": ""envoy"", ""nounPlural"": ""envoys"",
                ""waves"": { ""base"": 5, ""perParticipant"": 0.1, ""cap"": 8 }, ""minSeconds"": 300, ""maxAlive"": 40,
                ""waveSize"": { ""base"": 10, ""perParticipant"": 2, ""cap"": 30 }, ""waveIntervalSeconds"": 60,
                ""countByParticipants"": [ { ""minPlayers"": 0, ""count"": 2, ""radius"": 5 }, { ""minPlayers"": 6, ""count"": 3, ""radius"": 9 } ],
                ""specialWaves"": [
                    { ""type"": ""runners"", ""weight"": 2, ""countMult"": 0.5, ""healthMult"": 0.5, ""speedMult"": 2.0, ""attackSpeedMult"": 1.5 },
                    { ""type"": ""ranged"", ""holdDistance"": 20 },
                    { ""type"": ""hunters"", ""hunters"": 3 },
                    { ""type"": ""finale"" },
                    { ""type"": ""bogus"" },
                    { ""type"": ""runners"" }
                ],
                ""specialWaveChance"": 0.3,
                ""waveMix"": { ""breach"": 0.1, ""runners"": 0.2, ""ranged"": 0.3, ""hunters"": 0.1, ""seekers"": 0.2, ""corrupted"": 0.1,
                               ""minRanged"": 2, ""minCorrupted"": 1, ""maxCorrupted"": 3 },
                ""ward"": { ""reduction"": 0.5, ""reduces"": [ ""missile"" ], ""breakOn"": [ ""melee"", ""magic"" ] },
                ""corrupted"": { ""prefix"": ""Tainted"", ""scale"": 1.5, ""healthMult"": 2.0, ""assaultEveryWaves"": 2, ""tintPalette"": ""0x04001381"" },
                ""portals"": { ""count"": 3, ""radius"": 40, ""parts"": [ { ""wcid"": 4128, ""scale"": 1.0, ""speed"": 1.0 } ] },
                ""shrine"": { ""name"": ""Hearth Shrine"", ""useRadius"": 5, ""readyPulseSeconds"": 30 },
                ""shrineHeal"": 0.2, ""shrineCooldownSeconds"": 60, ""healBudgetPerWave"": 0.3,
                ""defendPose"": { ""reach"": 3, ""swingSeconds"": 2, ""swingMaxSeconds"": 4 },
                ""clearArea"": { ""targetClearance"": 5, ""minPortals"": 2 },
                ""bossTargetsProtected"": true, ""finaleBossScale"": 0, ""finaleBossPrefix"": ""Warlord"", ""bossEffect"": ""ShieldUpRed""
            } } ] }";

        [TestMethod]
        public void SampleHoldGoal_EveryBlockParsesToTheExpectedFields()
        {
            var store = WorldEventAxisStore.Parse(null, null, FullHoldGoalJson, null, null);

            Assert.IsTrue(store.Goals.ContainsKey("hold_envoys"), string.Join(" | ", store.Diagnostics));

            var goal = store.Goals["hold_envoys"];
            var p = goal.Protect;

            Assert.AreEqual(GoalType.Hold, goal.TypeKind, "the type parses case-insensitively");
            Assert.AreEqual(600, goal.HoldSeconds);

            // targets and damage
            Assert.AreEqual(1007651u, p.WcidFor(1));
            Assert.AreEqual(2, p.MinSurvivors);
            Assert.AreEqual(0.6, p.SeekShare, 1e-9);
            Assert.AreEqual(150.0, p.UndefendedSeconds, 1e-9);
            Assert.IsTrue(p.IsTimeScaled);
            Assert.AreEqual(145.0, p.EffectiveUndefendedSeconds(2), 1e-9, "5 s off per participant beyond the first");
            Assert.AreEqual(60.0, p.EffectiveUndefendedSeconds(100), 1e-9, "never under the floor");
            Assert.AreEqual((2, 5.0), p.ResolveLayout(3));
            Assert.AreEqual((3, 9.0), p.ResolveLayout(8));

            // waves
            Assert.IsTrue(p.IsWaveBased);
            Assert.AreEqual(8, p.Waves.Cap);
            Assert.AreEqual(40, p.MaxAlive);
            Assert.AreEqual(30, p.WaveSize.Cap);
            Assert.AreEqual(60.0, p.WaveIntervalSeconds, 1e-9);
            Assert.AreEqual(0.25, p.NextWaveAliveShare, 1e-9, "default");

            // special waves: the unknown type and the repeated one are dropped, the rest keep their parsed kind
            CollectionAssert.AreEqual(
                new[] { HoldWaveType.Runners, HoldWaveType.Ranged, HoldWaveType.Hunters, HoldWaveType.Finale },
                p.SpecialWaves.Select(w => w.TypeKind).ToArray());
            Assert.AreEqual(2.0, p.SpecialWave(HoldWaveType.Runners).SpeedMult, 1e-9);
            Assert.AreEqual(20.0, p.SpecialWave(HoldWaveType.Ranged).HoldDistance, 1e-9);
            Assert.AreEqual(3, p.SpecialWave(HoldWaveType.Hunters).Hunters);
            Assert.IsNotNull(p.Finale);
            Assert.IsTrue(store.Diagnostics.Any(d => d.Contains("bogus")));
            Assert.IsTrue(store.Diagnostics.Any(d => d.Contains("repeats a type")));

            // wave mix
            Assert.AreEqual(6, p.WaveMix.Shares.Length);
            Assert.AreEqual(0.3, p.WaveMix.Ranged, 1e-9);
            Assert.AreEqual(2, p.WaveMix.MinRanged);
            Assert.AreEqual(3, p.WaveMix.MaxCorrupted);
            Assert.AreEqual(3, p.WaveMix.Maximums[5]);

            // ward, corrupted, portals, shrine, pose, clear area
            Assert.AreEqual(WorldEventCombatKinds.Missile, p.Ward.ReducesKinds);
            Assert.AreEqual(WorldEventCombatKinds.Melee | WorldEventCombatKinds.Magic, p.Ward.BreakOnKinds);
            Assert.AreEqual("Tainted", p.Corrupted.Prefix);
            Assert.AreEqual(0x04001381u, p.Corrupted.TintPaletteId);
            Assert.IsTrue(p.Corrupted.AssaultMode);
            Assert.AreSame(p.Corrupted, p.EffectiveCorrupted);
            Assert.AreEqual(3, p.Portals.Count);
            Assert.AreEqual(1, p.Portals.Parts.Count);
            Assert.AreEqual("Hearth Shrine", p.Shrine.Name);
            Assert.AreEqual(ShrineWcid, p.Shrine.Wcid);
            Assert.AreEqual(0.2, p.ShrineHeal, 1e-9);
            Assert.AreEqual(3.0, p.DefendPose.Reach, 1e-9);
            Assert.AreEqual(4.0, p.DefendPose.SwingMaxSeconds, 1e-9);
            Assert.AreEqual(5.0, p.ClearArea.TargetClearance, 1e-9);
            Assert.IsTrue(p.BossTargetsProtected);
            Assert.AreEqual(0.0, p.FinaleBossScale, 1e-9);
            Assert.AreEqual("Warlord", p.FinaleBossPrefix);
        }

        [TestMethod]
        public void SourceThemeDef_OwnedWaveCount_SkipsTheGlobalOverrides_AndHonoursThePlayerWindow()
        {
            var theme = new SourceThemeDef
            {
                WaveCount = new ScaledCount
                {
                    Base = 4, PerParticipant = 1, Cap = 100, PlayersFrom = 3, PlayersUpTo = 8,
                    OwnsBase = true, OwnsPerParticipant = true, OwnsCap = true
                }
            };

            // Owned dials are read straight off the ScaledCount, never from world_events_wave_count_* (no PropertyManager here).
            Assert.AreEqual(4, theme.EffectiveWaveCountBase);
            Assert.AreEqual(1.0, theme.EffectiveWaveCountPerParticipant, 1e-9);
            Assert.AreEqual(100, theme.EffectiveWaveCountCap);

            Assert.AreEqual(4, theme.ResolveWaveCount(2), "under PlayersFrom: no growth");
            Assert.AreEqual(6, theme.ResolveWaveCount(5), "5 players, 3 free: 4 + 2");
            Assert.AreEqual(9, theme.ResolveWaveCount(20), "capped at PlayersUpTo 8: 4 + 5");
            Assert.AreEqual(9, theme.ResolveWaveCountRaw(20));
        }
    }
}
