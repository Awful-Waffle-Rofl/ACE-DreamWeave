using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Entity.Models;
using ACE.Server.WorldEvents;
using ACE.Server.WorldEvents.Defs;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ACE.Server.Tests
{
    /// <summary>
    /// The 2026-10-18 late round, from two more real runs: the shrine as its own weenie (no retail use behavior on any
    /// event prop), no immobile creature in any pick, the Warlord at the standard boss size, and the chain to a second
    /// event. Shares the builders in WorldEventHoldTests.
    /// </summary>
    public partial class WorldEventHoldTests
    {
        // ---- event props carry no retail use behavior ---------------------------------------------------------

        [TestMethod]
        public void DecorVerdict_ShrineMustBeGeneric_OtherPropsInert_InteractiveTypesRefused()
        {
            Assert.AreEqual(WorldEventSpawner.DecorUse.HoldShrine, WorldEventSpawner.DecorVerdict(WeenieType.Generic, true));
            Assert.AreEqual(WorldEventSpawner.DecorUse.Refuse, WorldEventSpawner.DecorVerdict(WeenieType.PKModifier, true),
                "the Altar of Asheron (855) is a PK switch: its client shows the NPK prompt");
            Assert.AreEqual(WorldEventSpawner.DecorUse.Refuse, WorldEventSpawner.DecorVerdict(WeenieType.Creature, true));

            Assert.AreEqual(WorldEventSpawner.DecorUse.Inert, WorldEventSpawner.DecorVerdict(WeenieType.Generic, false), "pennants, portal rings");
            Assert.AreEqual(WorldEventSpawner.DecorUse.Inert, WorldEventSpawner.DecorVerdict(WeenieType.LightSource, false), "the campfire");

            foreach (var type in new[] { WeenieType.PKModifier, WeenieType.Portal, WeenieType.LifeStone, WeenieType.Switch, WeenieType.Door,
                         WeenieType.Chest, WeenieType.Container, WeenieType.PressurePlate, WeenieType.Vendor, WeenieType.Creature, WeenieType.Healer })
                Assert.AreEqual(WorldEventSpawner.DecorUse.Refuse, WorldEventSpawner.DecorVerdict(type, false), $"{type} keeps its own use behavior");
        }

        private static string ContentFile(params string[] parts)
        {
            for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
            {
                var path = Path.Combine(new[] { dir.FullName }.Concat(parts).ToArray());

                if (File.Exists(path))
                    return path;
            }

            return null;
        }

        /// <summary>The rows a weenie SQL file inserts into one property table, as "(id, type, value)" tuples.</summary>
        private static List<(int Type, string Value)> SqlRows(string sql, string table)
        {
            var rows = new List<(int, string)>();
            var block = Regex.Match(sql, $@"INSERT INTO `{table}`[^;]*;", RegexOptions.Singleline);

            if (!block.Success)
                return rows;

            foreach (Match m in Regex.Matches(block.Value, @"\(\s*\d+\s*,\s*(\d+)\s*,\s*([^)]*?)\s*\)"))
                rows.Add((int.Parse(m.Groups[1].Value), m.Groups[2].Value.Trim()));

            return rows;
        }

        [TestMethod]
        public void ShrineWeenie_IsItsOwnGeneric_WithNoRetailUseBehavior_WhenPresent()
        {
            var path = ContentFile("dreamweave-content", "2000008-restoration-shrine.sql");

            if (path == null)
                Assert.Inconclusive("dreamweave-content/2000008-restoration-shrine.sql not found next to the repo");

            var sql = File.ReadAllText(path);

            StringAssert.Contains(sql, "VALUES (2000008, 'dw-restoration-shrine', 1, NOW())", "a Generic (type 1), never a PK switch (27)");

            // No retail rows at all: nothing copied by SELECT, no emotes, actions, create list, generators or spells.
            Assert.IsFalse(Regex.IsMatch(sql, @"SELECT\s+2000008", RegexOptions.IgnoreCase), "nothing copied from a retail weenie");

            foreach (var table in new[] { "weenie_properties_emote", "weenie_properties_emote_action", "weenie_properties_create_list",
                         "weenie_properties_generator", "weenie_properties_spell_book", "weenie_properties_book" })
                Assert.IsFalse(sql.Contains($"`{table}`"), $"no {table} rows");

            var ints = SqlRows(sql, "weenie_properties_int").ToDictionary(r => r.Type, r => r.Value);
            Assert.AreEqual("32", ints[(int)PropertyInt.ItemUseable], "usable from a distance: Remote");
            Assert.AreEqual("128", ints[(int)PropertyInt.ItemType], "Misc");

            var dids = SqlRows(sql, "weenie_properties_d_i_d").ToDictionary(r => r.Type, r => r.Value);
            Assert.AreEqual("0x02001762", dids[(int)PropertyDataId.Setup], "the retail Altar model (1 m, no default script)");
            Assert.AreEqual("0x3400002A", dids[(int)PropertyDataId.PhysicsEffectTable], "a table that can show the ready cue");

            var strings = SqlRows(sql, "weenie_properties_string");
            Assert.IsFalse(strings.Any(s => s.Value.IndexOf("killer", StringComparison.OrdinalIgnoreCase) >= 0), "no PK or NPK text");
            AssertAscii(sql);

            var log = ContentFile("dreamweave-content", "WCID-LOG.md");
            Assert.IsTrue(log != null && File.ReadAllText(log).Contains("| 2000008 | Restoration Shrine | dw-restoration-shrine | In use |"), "the WCID log row");
        }

        [TestMethod]
        public void KnightWeenies_CarryNoEmotesOrUseActions_WhenPresent()
        {
            foreach (var file in new[] { "2000005-sir-draithon.sql", "2000006-sir-donovan.sql", "2000007-sir-adarl.sql" })
            {
                var path = ContentFile("dreamweave-content", file);

                if (path == null)
                    Assert.Inconclusive($"dreamweave-content/{file} not found next to the repo");

                var sql = File.ReadAllText(path);

                Assert.IsFalse(sql.Contains("`weenie_properties_emote`") || sql.Contains("`weenie_properties_emote_action`"), $"{file}: no emotes, so no retail use behavior");
                Assert.IsFalse(sql.Contains("`weenie_properties_generator`"), $"{file}: no generator");
            }
        }

        // ---- no immobile creature in any pick -----------------------------------------------------------------

        private static Weenie CatalogCreature(uint wcid, string family, int level, uint motionTable, bool aiImmobile = false, bool flagged = false)
        {
            var w = new Weenie
            {
                WeenieClassId = wcid,
                ClassName = $"c{wcid}",
                WeenieType = WeenieType.Creature,
                PropertiesBool = new Dictionary<PropertyBool, bool> { [PropertyBool.Attackable] = true },
                PropertiesInt = new Dictionary<PropertyInt, int> { [PropertyInt.Level] = level },
                PropertiesString = new Dictionary<PropertyString, string> { [PropertyString.Name] = $"Creature {wcid}" },
                PropertiesDID = new Dictionary<PropertyDataId, uint> { [PropertyDataId.MotionTable] = motionTable }
            };

            if (aiImmobile)
                w.PropertiesBool[PropertyBool.AiImmobile] = true;

            if (flagged)
            {
                w.PropertiesBool[PropertyBool.WorldEventCreature] = true;
                w.PropertiesString[PropertyString.WorldEventFamily] = family;
                w.PropertiesInt[PropertyInt.WorldEventRole] = 0;
            }

            return w;
        }

        private const uint WalkingTable = 0x09000080, TendrilTable = 0x0900007B;

        [TestMethod]
        public void MotionTableRule_WalkOrRunCycleMoves()
        {
            // Cycle keys as the dats have them: style in the high half, motion in the low half.
            Assert.IsTrue(WorldEventCatalogBuilder.CyclesCanMove(new uint[] { 0x003C0003, 0x003C0005, 0x003C0007 }), "a grievver: walk and run");
            Assert.IsTrue(WorldEventCatalogBuilder.CyclesCanMove(new uint[] { 0x003D0007 }), "run alone");
            Assert.IsFalse(WorldEventCatalogBuilder.CyclesCanMove(new uint[] { 0x00470003, 0x003C0003, 0x003D0003, 0x0047000D, 0x003C0011 }),
                "the T'thuun tendril table: ready, attacks, no walk or run");
            Assert.IsFalse(WorldEventCatalogBuilder.CyclesCanMove(null));
        }

        [TestMethod]
        public void ImmobileCreatures_NeverEnterTheCatalog_SoNoWaveRangedCorruptedOrFinalePick()
        {
            var old = WorldEventCatalogBuilder.MotionTableCanMove;

            try
            {
                WorldEventCatalogBuilder.MotionTableCanMove = t => t == WalkingTable ? true : t == TendrilTable ? false : (bool?)null;

                Assert.IsTrue(WorldEventCatalogBuilder.IsImmobile(CatalogCreature(1, "slithis", 150, TendrilTable)), "a tendril (no flag, no walk)");
                Assert.IsTrue(WorldEventCatalogBuilder.IsImmobile(CatalogCreature(2, "slithis", 150, WalkingTable, aiImmobile: true)), "AiImmobile");
                Assert.IsFalse(WorldEventCatalogBuilder.IsImmobile(CatalogCreature(3, "slithis", 150, WalkingTable)));
                Assert.IsFalse(WorldEventCatalogBuilder.IsImmobile(CatalogCreature(4, "slithis", 150, 0x09999999)), "dats not loaded: mobile");

                var weenies = new List<Weenie>
                {
                    CatalogCreature(38830, "slithis", 180, TendrilTable),          // Tendril of T'thuun
                    CatalogCreature(38823, "slithis", 180, TendrilTable),          // Eyestalk of T'thuun
                    CatalogCreature(4000, "slithis", 180, WalkingTable),
                    CatalogCreature(4001, "slithis", 190, WalkingTable, aiImmobile: true),
                    CatalogCreature(5000, "emberwrought", 150, TendrilTable, flagged: true),
                    CatalogCreature(5001, "emberwrought", 150, WalkingTable, flagged: true),
                };

                var tables = new List<SpeciesTableDef>
                {
                    new SpeciesTableDef { Id = "slithis", DisplayName = "the Slithis",
                        Members = new[] { 38830u, 38823u, 4000u, 4001u }.Select(w => new SpeciesMemberDef { Wcid = w, Role = 0 }).ToList() }
                };

                var catalog = WorldEventCatalogBuilder.Build(weenies, tables, out var diagnostics);

                CollectionAssert.AreEqual(new[] { 4000u }, catalog.Families["slithis"].Select(m => m.Wcid).ToArray(), "only the one that moves");
                CollectionAssert.AreEqual(new[] { 5001u }, catalog.Families["emberwrought"].Select(m => m.Wcid).ToArray(), "flagged families too");
                Assert.AreEqual(4, diagnostics.Count(d => d.Contains("never moves")), string.Join(" | ", diagnostics));
                diagnostics.ForEach(AssertAscii);

                // The borrowed-ranged pool draws from the catalog, so a tendril can no longer stand in a ranged slot.
                var pool = WorldEventRangedMembers.CompatiblePool(new[] { "monouga" }, catalog.Families, new LevelBand(150, 225), out _, _ => true, new Random(1));
                Assert.IsFalse(pool.Contains(38830u) || pool.Contains(38823u), "no tendril or eyestalk");
            }
            finally
            {
                WorldEventCatalogBuilder.MotionTableCanMove = old;
            }
        }

        // ---- the Warlord's size ---------------------------------------------------------------------------------

        [TestMethod]
        public void FinaleBossScale_ZeroMatchesWorldEventBosses()
        {
            Assert.AreEqual(0.67f, WorldEventHoldRules.FinaleBossScale(0, 0.67f), 1e-6, "the same multiplier every World Events boss gets");
            Assert.AreEqual(1.0f, WorldEventHoldRules.FinaleBossScale(0, 0f), 1e-6, "a bad property: unchanged");
            Assert.AreEqual(1.75f, WorldEventHoldRules.FinaleBossScale(1.75, 0.67f), 1e-6, "an explicit size still wins");
            Assert.AreEqual(2.5f, WorldEventHoldRules.FinaleBossScale(9, 0.67f), 1e-6);
            Assert.AreEqual(1.0f, WorldEventHoldRules.FinaleBossScale(double.NaN, 0.67f), 1e-6);

            var ok = WorldEventAxisStore.Parse(null, null, HoldGoalsJson(SampleProtectJson.Replace(@"""radius"": 6.0,", @"""radius"": 6.0, ""finaleBossScale"": 0,"), holdSeconds: 480), null, null);
            Assert.IsTrue(ok.Goals.ContainsKey("hold_wards"), string.Join(" | ", ok.Diagnostics));

            var bad = WorldEventAxisStore.Parse(null, null, HoldGoalsJson(SampleProtectJson.Replace(@"""radius"": 6.0,", @"""radius"": 6.0, ""finaleBossScale"": 0.5,"), holdSeconds: 480), null, null);
            Assert.IsFalse(bad.Goals.ContainsKey("hold_wards"), "between 0 and 1 is neither");
        }

        // ---- the chain to a second event ------------------------------------------------------------------------

        [TestMethod]
        public void Chain_OnlyASuccessChains_AndNeverPastItsLength()
        {
            Assert.AreEqual(WorldEventChain.Decision.Next, WorldEventChain.Decide(WorldEventOutcome.Success, 1, 2));
            Assert.AreEqual(WorldEventChain.Decision.EndedAtMax, WorldEventChain.Decide(WorldEventOutcome.Success, 2, 2), "max length 2: the second stage is the last");
            Assert.AreEqual(WorldEventChain.Decision.Next, WorldEventChain.Decide(WorldEventOutcome.Success, 2, 3));

            foreach (var outcome in new[] { WorldEventOutcome.FailedObjectiveLost, WorldEventOutcome.FailedWipe, WorldEventOutcome.FailedNoParticipants,
                         WorldEventOutcome.AbortedAdmin, WorldEventOutcome.AbortedError, WorldEventOutcome.AbortedShutdown })
            {
                Assert.AreEqual(WorldEventChain.Decision.EndedByOutcome, WorldEventChain.Decide(outcome, 1, 2), $"{outcome} does not chain");
                Assert.AreEqual(WorldEventChain.Decision.EndedByOutcome, WorldEventChain.Decide(outcome, 2, 2), $"{outcome} in the second stage ends the chain");
            }

            Assert.AreEqual(WorldEventChain.Decision.None, WorldEventChain.Decide(WorldEventOutcome.Success, 1, 0), "a goal with no chain");

            Assert.AreEqual(2, WorldEventChain.EffectiveMaxLength(0, new ChainDef()), "the goal's");
            Assert.AreEqual(2, WorldEventChain.EffectiveMaxLength(2, null), "a later stage keeps the first stage's length, whatever its own goal says");
            Assert.AreEqual(0, WorldEventChain.EffectiveMaxLength(0, null));
        }

        [TestMethod]
        public void Chain_CountdownIs120Seconds_TeaserThenAnnounce()
        {
            Assert.AreEqual((90, 30), WorldEventChain.Leads(120, 30), "Active 120 s after the broadcast");
            Assert.AreEqual((0, 20), WorldEventChain.Leads(20, 30));
        }

        [TestMethod]
        public void Chain_NextSpot_IsNearby_NeverTheSameSpot()
        {
            var chain = new ChainDef();
            var rng = new Random(7);

            for (var i = 0; i < 500; i++)
            {
                var (dx, dy) = WorldEventChain.Offset(rng, chain.MinDistance, chain.MaxDistance);
                var d = Math.Sqrt(dx * dx + dy * dy);

                Assert.IsTrue(d >= 250 - 1e-6 && d <= 500 + 1e-6, $"{d:F1} m");
            }
        }

        [TestMethod]
        public void Chain_NextRequest_RollsEverything_CarriesTheTestSettings()
        {
            var first = new WorldEventRequest { SourceId = "ambush", GoalId = "defend_envoys", FamilyIds = new List<string> { "rat" }, TestLevel = 150,
                AbandonAfterSeconds = 0, WipeGraceSeconds = 0, ForcedWaveType = HoldWaveType.Ranged };
            var anchor = new ACE.Entity.Position(0xCE950022, 100, 100, 20, 0, 0, 0, 1, 0);

            // "random" on every axis: the 2026-10-18 behavior (the default now pins the sky rift champion; see
            // Chain_SecondStage_DefaultsToTheSkyRiftChampion_AndRandomStillRolls).
            var rolled = new ChainDef { NextSource = ChainDef.RandomAxis, NextGoal = ChainDef.RandomAxis, NextBoss = ChainDef.RandomAxis };
            var next = WorldEventChain.NextRequest(first, rolled, 2, 2, 41, 41, anchor, "Eastham (17.0N, 63.5E)");

            Assert.IsTrue(next.Random);
            Assert.IsNull(next.SourceId);
            Assert.IsNull(next.GoalId);
            Assert.AreEqual(0, next.FamilyIds.Count, "a random compatible source, family and goal");
            Assert.AreSame(anchor, next.AnchorPosition);
            Assert.AreEqual(150, next.TestLevel, "--level carries over");
            Assert.AreEqual(0, next.AbandonAfterSeconds, "--abandon carries over");
            Assert.IsNull(next.ForcedWaveType, "--wave-type does not");
            Assert.AreEqual(2, next.ChainStage);
            Assert.AreEqual(2, next.ChainMaxLength);
            Assert.AreEqual(41u, next.ChainRootRunId);
            Assert.AreEqual(98, next.TeaserLeadSeconds, "the countdown starts at the announcement, 8 s after the outcome (chain.pacing.announceSeconds)");
            Assert.AreEqual(30, next.AnnounceLeadSeconds);
        }

        [TestMethod]
        public void Chain_Broadcast_NamesTheFamilyAndThePlace()
        {
            Assert.AreEqual("The Monougas and the Ruschks regroup near Eastham (17.0N, 63.5E)! They strike in 120 seconds.",
                WorldEventChain.AnnounceLine(null, "the Monougas and the Ruschks", "Eastham (17.0N, 63.5E)", 120));
            Assert.AreEqual("Scouts report the Rats massing near Holtburg (42.1N, 33.6E).",
                WorldEventChain.AnnounceLine("Scouts report {family} massing near {location}.", "the Rats", "Holtburg (42.1N, 33.6E)", 120));
        }

        [TestMethod]
        public void GoalsJson_Chain_Validates()
        {
            string Goals(string chain) => HoldGoalsJson(SampleProtectJson, holdSeconds: 480).Replace(@"""type"": ""Hold"",", @"""type"": ""Hold"", " + chain);

            var good = WorldEventAxisStore.Parse(null, null, Goals(@"""chain"": { ""maxLength"": 2, ""countdownSeconds"": 120, ""announce"": [ ""{Family} regroup near {location}!"", "" "" ] },"), null, null);
            Assert.IsTrue(good.Goals.ContainsKey("hold_wards"), string.Join(" | ", good.Diagnostics));
            Assert.AreEqual(1, good.Goals["hold_wards"].Chain.Announce.Count, "the blank line is dropped");

            foreach (var (chain, expected) in new[]
                     {
                         (@"""chain"": { ""maxLength"": 1 },", "chain.maxLength"),
                         (@"""chain"": { ""maxLength"": 6 },", "chain.maxLength"),
                         (@"""chain"": { ""countdownSeconds"": 30 },", "chain.countdownSeconds"),
                         (@"""chain"": { ""minDistance"": 50 },", "chain distance"),
                         (@"""chain"": { ""minDistance"": 400, ""maxDistance"": 300 },", "chain distance"),
                     })
            {
                var store = WorldEventAxisStore.Parse(null, null, Goals(chain), null, null);

                Assert.IsFalse(store.Goals.ContainsKey("hold_wards"), chain);
                Assert.IsTrue(store.Diagnostics.Any(d => d.Contains(expected)), $"{chain}: {string.Join(" | ", store.Diagnostics)}");
            }

            Assert.IsNull(WorldEventAxisStore.Parse(null, null, HoldGoalsJson(SampleProtectJson, holdSeconds: 480), null, null).Goals["hold_wards"].Chain, "absent: no chain");
        }

        [TestMethod]
        public void ShippedEnvoys_ChainToASecondEvent_WhenPresent()
        {
            var path = ContentFile("dreamweave-content", "events", "defend_envoys.goal.json");

            if (path == null)
                Assert.Inconclusive("defend_envoys.goal.json not found next to the repo");

            var goal = WorldEventAxisStore.Parse(null, null, File.ReadAllText(path), null, null).Goals["defend_envoys"];
            var chain = goal.Chain;

            Assert.IsNotNull(chain, "the envoys chain into a second event");
            Assert.AreEqual(2, chain.MaxLength);
            Assert.AreEqual(120, chain.CountdownSeconds);
            Assert.IsTrue(chain.MinDistance >= 250, "never the same spot");
            Assert.IsTrue(chain.Announce.Count >= 2);
            Assert.AreEqual(2000008u, goal.Protect.Shrine.Wcid);

            foreach (var template in chain.Announce)
            {
                var line = WorldEventChain.AnnounceLine(template, "the Monougas and the Ruschks", "Eastham (17.0N, 63.5E)", 120);

                AssertAscii(WorldEventAnnouncer.Global(line));
                Assert.IsFalse(line.Contains('{') || line.Contains('}'), line);
                Assert.IsTrue(Regex.Matches(line.Trim(), @"[.!?]+(\s|$)").Count <= 3, line);
                Assert.IsFalse(line.IndexOf("warp", StringComparison.OrdinalIgnoreCase) >= 0, line);
                StringAssert.Contains(line, "Eastham (17.0N, 63.5E)", "the place, so players can travel there");
            }
        }
    }
}
