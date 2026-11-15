using System;
using System.Collections.Generic;
using System.Linq;

using ACE.Entity.Enum;
using ACE.Server.Frays;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ACE.Server.Tests
{
    /// <summary>The Frays' systems: camp modifiers, the week, families, the Ember cap.</summary>
    [TestClass]
    public class FraysSystemsTests
    {
        [TestInitialize]
        public void Init() => FraysSettings.ResetCacheForTest();

        // ---- Loot counts ----

        [TestMethod]
        public void LootRoll_DropsItsAverage()
        {
            Assert.AreEqual((0, 0, 0), FraysRules.LootRoll(0));
            Assert.AreEqual((40, 1, 1), FraysRules.LootRoll(0.4));   // a swarm creature's share of one
            Assert.AreEqual((100, 1, 1), FraysRules.LootRoll(1));
            Assert.AreEqual((100, 1, 3), FraysRules.LootRoll(2));    // a standard creature's magic items
            Assert.AreEqual((100, 2, 8), FraysRules.LootRoll(5));    // an elite's (2.5 times)

            foreach (var per in new[] { 1.0, 1.5, 2, 2.5, 3, 5, 7.5 })
            {
                var (_, min, max) = FraysRules.LootRoll(per);
                Assert.AreEqual(per, (min + max) / 2.0, 0.51, $"average at {per}");
            }
        }

        [TestMethod]
        public void GreatSnarl_IsAnotherCreatureOfTheSameFamily()
        {
            // The Lugians' only elite (Tiatus Smithy) is the Snarl; the Great Snarl is their top other member.
            var lugians = new FraysFamily
            {
                Key = "lugian", Name = "Lugians", Table = new ACE.Server.WorldEvents.Defs.SpeciesTableDef(),
                Trash = new List<FraysFamilyMember> { new FraysFamilyMember { Wcid = 38954, Level = 200 }, new FraysFamilyMember { Wcid = 51909, Level = 240 } },
                Elites = new List<FraysFamilyMember> { new FraysFamilyMember { Wcid = 47179, Level = 190 } },
            };

            Assert.AreEqual(47179u, lugians.WcidFor(FraysRole.Snarl));
            Assert.AreEqual(51909u, lugians.GreatSnarlWcid());
            Assert.IsTrue(lugians.Contains(lugians.GreatSnarlWcid()), "the same family");

            var lone = new FraysFamily { Key = "x", Name = "X", Table = new ACE.Server.WorldEvents.Defs.SpeciesTableDef(),
                Elites = new List<FraysFamilyMember> { new FraysFamilyMember { Wcid = 7, Level = 10 } } };
            Assert.AreEqual(7u, lone.GreatSnarlWcid(), "with nothing else it shares the Snarl's");
        }

        [TestMethod]
        public void Village_LayoutRoundTripsAndFacingsMatch()
        {
            // The built-in layout is every arrival NPC and village piece; the tool saves it as JSON and reads it back.
            var all = FraysVillage.Defaults();
            Assert.AreEqual(FraysDefs.EntranceNpcs.Count + FraysDefs.Village.Count + FraysDefs.TownPieces.Count + FraysDefs.TownDoors.Count + FraysDefs.Hangings.Count, all.Count);
            Assert.IsTrue(all.Where(p => p.Sink).All(p => p.Size == 1), "the town is full size (the client ignores a smaller scale on these pieces)");
            Assert.IsTrue(FraysDefs.TownPieces.All(t => t.X >= 0 && t.X < 192 && t.Y >= 0 && t.Y < 192), "every town piece sits inside its landblock");

            var text = System.Text.Json.JsonSerializer.Serialize(all);
            var back = System.Text.Json.JsonSerializer.Deserialize<List<FraysPiece>>(text);
            Assert.AreEqual(all.Count, back.Count);
            Assert.AreEqual(all[5].Wcid, back[5].Wcid);
            Assert.AreEqual(all[5].X, back[5].X, 1e-4);

            foreach (var deg in new[] { 0f, 90f, 180f, -90f, 45f })
                Assert.AreEqual(Math.Cos(deg * Math.PI / 180), Math.Cos(FraysVillage.Degrees(FraysVillage.Facing(deg)) * Math.PI / 180), 1e-4, $"{deg} degrees");
        }

        // ---- camp modifiers ----

        private static FraysCampMods Build(FraysAffix shift = FraysAffix.None) =>
            FraysCampMods.Build(null, SnarlPact.Normal, shift, null);

        [TestMethod]
        public void Mods_Affixes()
        {
            var m = Build(FraysAffix.HeavyThread);

            Assert.AreEqual(1.0, m.Embers, 1e-9, "no Shift pays more");
            Assert.AreEqual(1.4, m.MonsterHealth, 1e-9);
        }

        [TestMethod]
        public void Mods_IncomingProofAndWeft()
        {
            var m = Build();
            Assert.AreEqual(1.0, m.IncomingMult(DamageType.Slash, 0), 1e-9);
            Assert.AreEqual(1.0, m.IncomingMult(DamageType.Fire, 0), 1e-9);

            m.ProofReduction[DamageType.Fire] = FraysCampMods.ProofFor(4);
            Assert.AreEqual(0.80, m.IncomingMult(DamageType.Fire, 0), 1e-9);
        }

        [TestMethod]
        public void ProofSets_Bands()
        {
            Assert.AreEqual(0, FraysCampMods.ProofFor(1), 1e-9);
            Assert.AreEqual(0.10, FraysCampMods.ProofFor(2), 1e-9);
            Assert.AreEqual(0.15, FraysCampMods.ProofFor(3), 1e-9);
            Assert.AreEqual(0.25, FraysCampMods.ProofFor(7), 1e-9);
        }

        // ---- the week ----

        [TestMethod]
        public void Week_CountsFromTheResetDay()
        {
            // Tuesday 00:00 UTC is a reset; the moment before belongs to the previous week.
            var reset = new DateTime(2026, 10, 27, 0, 0, 0, DateTimeKind.Utc);
            var w = FraysWeek.WeekAt(reset, 2, 0);
            Assert.AreEqual(w - 1, FraysWeek.WeekAt(reset.AddSeconds(-1), 2, 0));
            Assert.AreEqual(w, FraysWeek.WeekAt(reset.AddDays(6.9), 2, 0));
            Assert.AreEqual(w + 1, FraysWeek.WeekAt(reset.AddDays(7), 2, 0));
        }

        [TestMethod]
        public void Week_AffixesRotateThroughEveryOne()
        {
            var n = FraysWeek.Shifts.Length;
            var shifts = Enumerable.Range(0, n).Select(FraysWeek.ShiftFor).Distinct().Count();
            Assert.AreEqual(22, n, "22 weekly affixes");
            foreach (var gone in new[] { "Bursting", "Bolstering", "Sparking", "Sanguine" })
                Assert.IsFalse(Enum.IsDefined(typeof(FraysAffix), gone), $"{gone} is removed");
            Assert.AreEqual(n, shifts, "one Shift a week, every one comes round");
            Assert.AreEqual(FraysWeek.ShiftFor(n - 5), FraysWeek.ShiftFor(-5), "negative weeks wrap");
        }

        [TestMethod]
        public void Families_OneAWeekAndEveryOneComesRound()
        {
            var seen = new HashSet<string>();

            for (var w = 0; w < FraysFamilies.All.Count; w++)
            {
                var a = FraysFamilies.ForWeek(w, 0);
                Assert.AreNotEqual(a.Key, FraysFamilies.ForWeek(w + 1, 0).Key, $"week {w} and the next differ");
                seen.Add(a.Key);
            }

            Assert.AreEqual(FraysFamilies.All.Count, seen.Count);
            Assert.AreEqual(20, FraysFamilies.All.SelectMany(f => new[] { f.Swarm, f.Standard, f.Elite, f.Snarl }).Distinct().Count());
        }

        // ---- rules ----

        [TestMethod]
        public void EmberCap_FullThenQuarter()
        {
            Assert.AreEqual(100, FraysRules.CappedEmbers(100, 0, 12000, 0.25), 1e-9);
            Assert.AreEqual(50 + 12.5, FraysRules.CappedEmbers(100, 11950, 12000, 0.25), 1e-9);
            Assert.AreEqual(25, FraysRules.CappedEmbers(100, 20000, 12000, 0.25), 1e-9);
            Assert.AreEqual(100, FraysRules.CappedEmbers(100, 20000, 0, 0.25), 1e-9, "cap 0 = no cap");
        }

        [TestMethod]
        public void HeatSteps()
        {
            Assert.AreEqual(1.0, FraysRules.StepMult(4, 0.1), 1e-9);
            Assert.AreEqual(1.1, FraysRules.StepMult(5, 0.1), 1e-9);
            Assert.AreEqual(1.4, FraysRules.StepMult(20, 0.1), 1e-9);
            Assert.AreEqual(1.4, FraysRules.StepMult(30, 0.1), 1e-9, "four steps at most");
        }

        [TestMethod]
        public void Bias_ShiftsKindNeverCount()
        {
            Assert.AreEqual(0, FraysRules.BiasProfile(new List<string>(), 0.1, 0));
            Assert.AreEqual(0, FraysRules.BiasProfile(new List<string> { "weapons" }, 0.6, 0), "half the time, the creature's own");
            Assert.AreEqual(1, FraysRules.BiasProfile(new List<string> { "weapons" }, 0.2, 7));
            Assert.AreEqual(5, FraysRules.BiasProfile(new List<string> { "weapons", "jewelry" }, 0.2, 1));
        }

        [TestMethod]
        public void Report_HeatLineIsShort()
        {
            var log = Enumerable.Range(0, 121).Select(m => ((double)m, m / 10)).ToList();
            var line = FraysReport.HeatLine(log);
            Assert.IsTrue(line.StartsWith("0m 0"), line);
            Assert.IsTrue(line.EndsWith("120m 12"), line);
            Assert.IsTrue(line.Split(',').Length <= 14, line);
        }

        // ---- long-term systems ----

        [TestMethod]
        public void Companion_LevelsCapAt10()
        {
            Assert.AreEqual(0, FraysLongTerm.CompanionLevel(0));
            Assert.AreEqual(1, FraysLongTerm.CompanionLevel(100));
            Assert.AreEqual(2, FraysLongTerm.CompanionLevel(400));
            Assert.AreEqual(10, FraysLongTerm.CompanionLevel(1_000_000));
        }

        [TestMethod]
        public void Bounties_ThreeDistinctAndStablePerDay()
        {
            var a = FraysLongTerm.BountiesFor(1000).Select(b => b.Key).ToList();
            var b2 = FraysLongTerm.BountiesFor(1000).Select(b => b.Key).ToList();
            Assert.AreEqual(3, a.Distinct().Count());
            CollectionAssert.AreEqual(a, b2, "everyone gets the same three");
            Assert.IsTrue(FraysLongTerm.BountyPool.All(p => p.Text.All(c => c < 128)));
        }

        [TestMethod]
        public void Bane_RollNeverRepeatsTheOldSlayer()
        {
            var rec = new FraysPlayerRecord();

            for (var i = 0; i < 200; i++)
                Assert.AreNotEqual(CreatureType.Olthoi, FraysLongTerm.RollSlayer(rec, CreatureType.Olthoi));

            var seen = Enumerable.Range(0, 400).Select(_ => FraysLongTerm.RollSlayer(rec, null)).Distinct().Count();
            Assert.AreEqual(FraysFamilies.All.Select(f => f.Type).Where(t => t != CreatureType.Invalid).Distinct().Count(), seen, "every family can come up");
        }

        [TestMethod]
        public void Record_LongTermFieldsRoundTrip()
        {
            var r = new FraysPlayerRecord { CompanionRole = "warder", CompanionXp = 900, FireTier = 2, Ascensions = 1 };
            r.Claims.Add(2000085);

            var back = FraysPlayerRecord.Parse(r.ToJson());
            Assert.AreEqual("warder", back.CompanionRole);
            Assert.AreEqual(2, back.FireTier);
            Assert.AreEqual(2000085u, back.Claims[0]);
            Assert.IsTrue(back.RollWeek(5) && !back.RollWeek(5));
        }

        [TestMethod]
        public void Defs_IdsAreUnique()
        {
            Assert.AreEqual(FraysDefs.All.Count, FraysDefs.All.Select(f => f.Id).Distinct().Count(), "ids are unique");

            foreach (var spot in FraysFrayWide.GreatSnarlSpots)
                Assert.IsFalse(FraysDefs.All.Any(f => f.Landblock == spot.Key && f.RoomCells.Contains(spot.Value.Cell)), "the Great Snarl's room has no campfire");
        }

        // ---- version 1 ----

        [TestMethod]
        public void LevelPay_FollowsTheCurveMoreSlowly()
        {
            Assert.AreEqual(3.5e9, FraysRules.LevelPay(3.5e9, 100, 100, 0.85), 1);
            var doubleCost = FraysRules.LevelPay(3.5e9, 200, 100, 0.85);
            Assert.IsTrue(doubleCost > 3.5e9 && doubleCost < 7e9, "pays more at a dearer level, but less than the cost grows");
            Assert.AreEqual(7e9, FraysRules.LevelPay(3.5e9, 200, 100, 1.0), 1);
        }

        [TestMethod]
        public void HeatIncome_CapsAtTheMax()
        {
            Assert.AreEqual(1.0, FraysRules.HeatIncome(0, 0.025, 0.5), 1e-9);
            Assert.AreEqual(1.25, FraysRules.HeatIncome(10, 0.025, 0.5), 1e-9);
            Assert.AreEqual(1.5, FraysRules.HeatIncome(40, 0.025, 0.5), 1e-9);
        }

        [TestMethod]
        public void LevelScale_TargetAndHealth()
        {
            Assert.AreEqual(345, FraysLevelScale.TargetLevel(300, 15, 3));
            Assert.AreEqual(300, FraysLevelScale.TargetLevel(300, 0, 3));
        }

        [TestMethod]
        public void Shapes_KeepTheSameHealthAnHour()
        {
            foreach (FraysShape s in Enum.GetValues(typeof(FraysShape)))
            {
                // health per spawn x creatures per spawn / interval = the same for every shape
                var perHour = FraysRules.ShapeHealthMult(s) * FraysRules.ShapePackMult(s) / FraysRules.ShapeIntervalMult(s);
                Assert.AreEqual(1.0, perHour, 1e-9, s.ToString());
            }

            Assert.AreEqual(0, FraysRules.ShapeSwarmShare(FraysShape.Heavy, 0.5), 1e-9);
            Assert.IsTrue(FraysRules.ShapeSwarmShare(FraysShape.Light, 0.5) > 0.5);
        }

        [TestMethod]
        public void Tasks_DailyAndWeeklyAreSeparate()
        {
            Assert.IsTrue(FraysLongTerm.BountiesFor(9).All(b => !b.Weekly));
            // One weekly, and a kill task.
            for (var w = 0; w < 20; w++)
            {
                Assert.AreEqual(3, FraysLongTerm.WeeklyFor(w).Count, "one rolled kill task, Kill the Great Snarl and the group camp task");
                Assert.IsTrue(FraysLongTerm.WeeklyFor(w).Any(b => b.Key == "w_group"));
                Assert.IsTrue(FraysLongTerm.WeeklyFor(w).Any(b => b.Key == "w_great"));
                Assert.IsTrue(FraysLongTerm.WeeklyFor(w).All(b => b.Weekly && (FraysLongTerm.WeeklyKillKeys.Contains(b.Key) || FraysLongTerm.WeeklyAlways.Contains(b.Key))));
            }

            Assert.AreEqual(2, Enumerable.Range(0, 20).Select(w => FraysLongTerm.WeeklyFor(w)[0].Key).Distinct().Count(), "both weekly kill tasks come up");
        }

        [TestMethod]
        public void HealthHit_SizedToTheCamper_ArmorCutsAtMostItsShare()
        {
            // 400 health, a 10% share, a middle roll: 40 with no armor, 20 when armor stops nearly everything (half at most).
            Assert.AreEqual(40, FraysRules.HealthHit(400, 0.10, 1, 0.5, 0.5), 1e-9);
            Assert.AreEqual(20, FraysRules.HealthHit(400, 0.10, 0.004, 0.5, 0.5), 0.1, "a level 444's armor (0.4% through) halves it, no more");
            Assert.AreEqual(30, FraysRules.HealthHit(400, 0.10, 0.5, 0.5, 0.5), 1e-9, "half through: a quarter off");
            Assert.AreEqual(32, FraysRules.HealthHit(400, 0.10, 1, 0.5, 0), 1e-9, "the low roll: -20%");
            Assert.AreEqual(48, FraysRules.HealthHit(400, 0.10, 1, 0.5, 1), 1e-9, "the high roll: +20%");
            Assert.AreEqual(40, FraysRules.HealthHit(400, 0.10, 0, 0, 0.5), 1e-9, "armor off: the full share");
        }

        [TestMethod]
        public void DownlevelHealth_ShrinksByTheHeatsMultiplier_KeepsTheShareLeft()
        {
            // A Heat 15 Snarl add at half health, its camper cooled to a Heat with 2/3 the health multiplier
            Assert.AreEqual((2000u, 1000u), FraysRules.DownlevelHealth(3000, 1500, 3.0, 2.0));
            Assert.AreEqual((3000u, 1500u), FraysRules.DownlevelHealth(3000, 1500, 2.0, 3.0), "never raised");
            Assert.AreEqual((300u, 1u), FraysRules.DownlevelHealth(3000, 1, 10.0, 1.0), "a creature nearly dead stays alive");
            Assert.AreEqual((3000u, 1500u), FraysRules.DownlevelHealth(3000, 1500, 0, 1.0), "no multiplier known: unchanged");
        }

        [TestMethod]
        public void SpellHit_HeldToAWeaponHitsShare_NeverRaised()
        {
            // A Heat 15 lightning bolt that would take 153 of a camper's 361 health (cap 32.5%, cuts x1.07)
            var bolt = 361 * 0.325 * 1.07;
            var sized = FraysRules.SpellHit(bolt, 361, 0.12, 1.0, 1.07, 0.325, 0.5);
            Assert.AreEqual(361 * 0.12 * 1.07, sized, 1e-9, "a middle roll: the Heat 15 share, then the cuts");
            Assert.IsTrue(sized / 361 < 0.14, "about an eighth of health, not a third");

            Assert.AreEqual(20, FraysRules.SpellHit(20, 361, 0.12, 1.0, 1.0, 0.325, 0.5), 1e-9, "a weak spell keeps its own hit");
            Assert.AreEqual(bolt, FraysRules.SpellHit(bolt, 361, 0, 1.0, 1.07, 0.325, 0.5), 1e-9, "share 0: off");
            Assert.AreEqual(361 * 0.325, FraysRules.SpellHit(1000, 361, 0.12, 5.0, 1.0, 0.325, 1), 1e-9, "elite boosts stay under the cap");
        }

        [TestMethod]
        public void RerollStone_FavoursThisWeeksFamily_NeverKeepsTheOld()
        {
            // reroll_week_chance 0.25 by default: a low roll lands on the week's family, a high one on another.
            Assert.AreEqual(CreatureType.Golem, FraysLongTerm.RollReroll(CreatureType.Olthoi, CreatureType.Golem, 0.1));

            for (var i = 0; i < 50; i++)
            {
                var t = FraysLongTerm.RollReroll(CreatureType.Olthoi, CreatureType.Golem, 0.9);
                Assert.AreNotEqual(CreatureType.Olthoi, t, "never the slayer it has");
                Assert.AreNotEqual(CreatureType.Golem, t, "a high roll is not the week's family");
            }

            // Already slays the week's family: any other one.
            Assert.AreNotEqual(CreatureType.Golem, FraysLongTerm.RollReroll(CreatureType.Golem, CreatureType.Golem, 0.1));
        }

        [TestMethod]
        public void TaskReady_SurvivesSaveAndLoad()
        {
            // Finished tasks wait for hand-in at their task-giver, so they must keep through a save.
            var rec = new FraysPlayerRecord();
            rec.TaskReady.Add(new FraysTaskReady { Giver = FraysHunts.HeatWardenWcid, Text = "Climb to Heat 15", Keys = 2 });
            rec.TaskReady.Add(new FraysTaskReady { Giver = FraysHunts.WeekWardenWcid, Text = "Kill 15 Snarls", Embers = 1500 });

            var back = FraysPlayerRecord.Parse(rec.ToJson());

            Assert.AreEqual(2, back.TaskReady.Count);
            Assert.AreEqual(FraysHunts.HeatWardenWcid, back.TaskReady[0].Giver);
            Assert.AreEqual(2, back.TaskReady[0].Keys);
            Assert.AreEqual(1500, back.TaskReady[1].Embers);
            Assert.AreEqual("Kill 15 Snarls", back.TaskReady[1].Text);
            Assert.AreEqual(0, FraysPlayerRecord.Parse("{}").TaskReady.Count, "an old record has none waiting");
        }

        [TestMethod]
        public void TaskGivers_SplitTheBoard()
        {
            FraysHunts.TaskEntry T(string key, bool hunt = false, bool weekly = false) => new FraysHunts.TaskEntry { Key = key, IsHunt = hunt, Weekly = weekly };

            Assert.AreEqual(FraysHunts.HuntmasterWcid, FraysHunts.GiverOf(T(FraysHunts.AnyKey, hunt: true)));
            Assert.AreEqual(FraysHunts.HuntmasterWcid, FraysHunts.GiverOf(T(FraysHunts.EliteKey, hunt: true)));
            Assert.AreEqual(FraysHunts.HuntmasterWcid, FraysHunts.GiverOf(T("c2000059", hunt: true)));
            Assert.AreEqual(FraysHunts.HeatWardenWcid, FraysHunts.GiverOf(T("heat15", hunt: true)));
            Assert.AreEqual(FraysHunts.HeatWardenWcid, FraysHunts.GiverOf(T("heat12")));
            Assert.AreEqual(FraysHunts.SnarlTrackerWcid, FraysHunts.GiverOf(T("snarls")));
            Assert.AreEqual(FraysHunts.SnarlTrackerWcid, FraysHunts.GiverOf(T("health")));
            Assert.AreEqual(FraysHunts.WeekWardenWcid, FraysHunts.GiverOf(T("w_kills", weekly: true)));

            foreach (var b in FraysLongTerm.BountyPool)
                CollectionAssert.Contains(FraysHunts.Givers, FraysHunts.GiverOf(T(b.Key, weekly: b.Weekly)));
        }

        [TestMethod]
        public void SlayerStones_RollOnlyFrayFamilies()
        {
            var rec = new FraysPlayerRecord();
            var types = FraysFamilies.All.Select(f => f.Type).ToHashSet();

            for (var i = 0; i < 200; i++)
                Assert.IsTrue(types.Contains(FraysLongTerm.RollSlayer(rec, null)));
        }

        // ---- Vissidal zones ----

        [TestMethod]
        public void Vissidal_ThirtyEightOneLandblockZones()
        {
            var zones = FraysDefs.All.Where(f => f.Outdoor && !f.Group).ToList();
            Assert.AreEqual(38, zones.Count);
            Assert.AreEqual(38, zones.Select(z => z.Landblock).Distinct().Count(), "one zone per landblock");
            Assert.AreEqual(18, zones.Count(z => z.FrayKey == FraysDefs.VissidalCoast));
            Assert.AreEqual(20, zones.Count(z => z.FrayKey == FraysDefs.VissidalHeights), "a Fray's fellowship holds at most 20");

            foreach (var z in zones)
            {
                Assert.IsTrue(z.SpawnPoints.Count >= 8, $"{z.Id}: {z.SpawnPoints.Count} spawn points");
                Assert.IsTrue(z.RoomName.Length > 0 && z.RoomName.All(c => c < 128), z.Id);

                foreach (var p in z.SpawnPoints.Append(new FraysSpawnPoint(z.FireCell, z.FirePos.X, z.FirePos.Y, z.FirePos.Z)))
                {
                    Assert.IsTrue(p.Pos.X >= 0 && p.Pos.X < 192 && p.Pos.Y >= 0 && p.Pos.Y < 192, $"{z.Id}: a point outside its landblock");
                    Assert.AreEqual(FraysRules.OutdoorCell(true, 0, p.Pos), p.Cell, $"{z.Id}: cell id of {p.Pos}");
                }
            }

            foreach (var spot in FraysFrayWide.GreatSnarlSpots.Where(s => s.Key == FraysDefs.VissidalCoast || s.Key == FraysDefs.VissidalHeights))
                Assert.IsFalse(zones.Any(z => z.Landblock == spot.Value.Landblock), "the Great Snarl lands where no camper holds a fire");
        }

        [TestMethod]
        public void RestedPoints_NearAndRested()
        {
            var pts = new List<System.Numerics.Vector3> { new(10, 10, 0), new(100, 100, 0), new(20, 10, 0) };
            var ready = new Dictionary<int, double>();

            Assert.AreEqual(0, FraysRules.PickRestedPoint(pts, new(12, 12, 0), ready, 100, 60, 0));
            ready[0] = 200;
            Assert.AreEqual(2, FraysRules.PickRestedPoint(pts, new(12, 12, 0), ready, 100, 60, 0), "the rested neighbour, not the far one");
            ready[2] = double.MaxValue;
            Assert.AreEqual(-1, FraysRules.PickRestedPoint(pts, new(12, 12, 0), ready, 100, 60, 0), "nothing near is rested: move on");
            Assert.AreEqual(0, FraysRules.PickRestedPoint(pts, new(12, 12, 0), ready, 250, 60, 0), "rest over");
        }

        [TestMethod]
        public void FrayOfLandblock_GroupsTheZones()
        {
            Assert.AreEqual(FraysDefs.VissidalCoast, FraysDefs.FrayOfLandblock(0xC9E2));
            Assert.AreEqual(FraysDefs.VissidalHeights, FraysDefs.FrayOfLandblock(0xD5E0));
            Assert.AreEqual((ushort)0x1234, FraysDefs.FrayOfLandblock(0x1234));
        }

        // ---- outdoor routing ----

        [TestMethod]
        public void PointKinds_SpreadAndRest()
        {
            var kinds = Enumerable.Range(0, 20).Select(FraysRules.PointKind).ToList();
            Assert.AreEqual(4, kinds.Count(k => k == FraysPointKind.Nest));
            Assert.IsTrue(kinds.Count(k => k == FraysPointKind.Quick) >= 5);
            Assert.IsTrue(FraysRules.PointRest(FraysPointKind.Nest, false) > FraysRules.PointRest(FraysPointKind.Normal, false));
            Assert.IsTrue(FraysRules.PointRest(FraysPointKind.Quick, false) < FraysRules.PointRest(FraysPointKind.Normal, false));
            Assert.AreEqual(FraysRules.PointRest(FraysPointKind.Normal, false) / 2, FraysRules.PointRest(FraysPointKind.Normal, true), 1e-9);
        }

        [TestMethod]
        public void HotQuadrant_MovesAndNeverRepeats()
        {
            var seen = new HashSet<int>();
            var last = -1;

            for (var step = 0; step < 40; step++)
            {
                var q = FraysRules.HotQuadrant(step * 600 + 1, "vc1", 10);
                Assert.IsTrue(q >= 0 && q < 4);
                Assert.AreNotEqual(last, q, "never the same quarter twice running");
                seen.Add(q);
                last = q;
            }

            Assert.AreEqual(4, seen.Count);
            Assert.AreEqual(-1, FraysRules.HotQuadrant(1000, "vc1", 0));
            Assert.AreEqual(3, FraysRules.Quadrant(new System.Numerics.Vector3(150, 150, 0)));
        }

        [TestMethod]
        public void RestedPoints_ZeroRadiusMeansAnywhere()
        {
            var pts = new List<System.Numerics.Vector3> { new(10, 10, 0), new(180, 180, 0) };
            Assert.AreEqual(1, FraysRules.PickRestedPoint(pts, new(10, 10, 0), new Dictionary<int, double> { [0] = 999 }, 1, 0, 0));
        }

        [TestMethod]
        public void Vissidal_PointsStayInsideTheEdge()
        {
            foreach (var z in FraysDefs.All.Where(f => f.Outdoor && !f.Group))
            {
                foreach (var p in z.SpawnPoints)
                    Assert.IsTrue(p.Pos.X >= 18 && p.Pos.X <= 174 && p.Pos.Y >= 18 && p.Pos.Y <= 174, $"{z.Id}: point {p.Pos} within 18 m of the landblock edge");
            }
        }

        // ---- pack templates and the island ----

        [TestMethod]
        public void PackTemplates_ShapeTheMixNotTheHealth()
        {
            var quick = FraysRules.PackTemplate(FraysPointKind.Quick, 0, FraysShape.Medium, 0, false);
            Assert.AreEqual(1, quick.Count);

            var nest = FraysRules.PackTemplate(FraysPointKind.Nest, 2, FraysShape.Medium, 0, false);
            Assert.AreEqual(1, nest.Count(r => r == FraysRole.Standard), "a standard creature leads a nest");
            Assert.AreEqual(3, nest.Count(r => r == FraysRole.Swarm));

            var swarm = FraysRules.PackTemplate(FraysPointKind.Normal, 4, FraysShape.Medium, 0, false);
            var hunters = FraysRules.PackTemplate(FraysPointKind.Normal, 1, FraysShape.Medium, 0, false);
            Assert.IsTrue(swarm.All(r => r == FraysRole.Swarm) && swarm.Count == 3);
            Assert.IsTrue(hunters.All(r => r == FraysRole.Standard) && hunters.Count == 2);

            Assert.IsTrue(FraysRules.PackTemplate(FraysPointKind.Normal, 4, FraysShape.Heavy, 0, false).All(r => r == FraysRole.Standard), "heavy: no swarms");
            Assert.IsTrue(FraysRules.PackTemplate(FraysPointKind.Normal, 1, FraysShape.Light, 0, false).All(r => r == FraysRole.Swarm), "light: no hunters");

            Assert.AreEqual(3 + 4, FraysRules.PackTemplate(FraysPointKind.Normal, 4, FraysShape.Medium, 2, false).Count, "Heat grows a swarm by two a step");
            Assert.AreEqual(3, FraysRules.PackTemplate(FraysPointKind.Normal, 1, FraysShape.Medium, 0, true).Count, "a flaring quarter adds one");
        }

        [TestMethod]
        public void Island_CoversEveryZoneButNotTheTown()
        {
            foreach (var z in FraysDefs.All.Where(f => f.Outdoor && !f.Group))
                Assert.IsTrue(FraysDefs.VissidalIsland.Contains(z.Landblock), z.Id);

            Assert.IsFalse(FraysDefs.VissidalIsland.Contains((ushort)0xD5DA), "the town keeps its own");
        }

        [TestMethod]
        public void Wisps_RingTheZoneCircle()
        {
            var ring = FraysRules.RingSpots(90, 6);
            Assert.IsTrue(ring.Count >= 90, "close enough to read as one line");

            foreach (var (x, y) in ring)
                Assert.AreEqual(90, Math.Sqrt((x - 96) * (x - 96) + (y - 96) * (y - 96)), 0.01);

            for (var i = 0; i < ring.Count; i++)
            {
                var (ax, ay) = ring[i];
                var (bx, by) = ring[(i + 1) % ring.Count];
                Assert.IsTrue(Math.Sqrt((ax - bx) * (ax - bx) + (ay - by) * (ay - by)) <= 6.01, "no gaps");
            }
        }

        [TestMethod]
        public void Zone_IsACircleAndPointsSitInsideIt()
        {
            Assert.IsTrue(FraysRules.InCircle(new System.Numerics.Vector3(96, 186, 0), 90));
            Assert.IsFalse(FraysRules.InCircle(new System.Numerics.Vector3(18, 18, 0), 90), "the landblock's corners are outside");

            var pulled = FraysRules.InsideCircle(new System.Numerics.Vector3(18, 18, 5), 72);
            Assert.AreEqual(72, Math.Sqrt((pulled.X - 96) * (pulled.X - 96) + (pulled.Y - 96) * (pulled.Y - 96)), 0.01);
            Assert.AreEqual(5, pulled.Z, 1e-6, "height kept");

            var inside = new System.Numerics.Vector3(100, 90, 0);
            Assert.AreEqual(inside, FraysRules.InsideCircle(inside, 72), "a point already inside stays put");
        }

        [TestMethod]
        public void Pacing_HitsTheTargetAtEveryHeatAndShape()
        {
            foreach (var shape in new[] { FraysShape.Medium, FraysShape.Heavy, FraysShape.Light })
            {
                foreach (var extra in new[] { 0, 2, 4 })
                {
                    foreach (var hot in new[] { false, true })
                    {
                        var scale = FraysRules.RestScale(20, shape, extra, hot, 600);
                        Assert.AreEqual(600, FraysRules.SupplyPerHour(20, shape, extra, hot) / scale, 1, $"{shape} +{extra} hot {hot}");
                    }
                }
            }

            // Bigger packs at higher Heat rest longer, so the count an hour stays put.
            Assert.IsTrue(FraysRules.RestScale(20, FraysShape.Medium, 4, true, 600) > FraysRules.RestScale(20, FraysShape.Medium, 0, true, 600));
            Assert.AreEqual(1, FraysRules.RestScale(20, FraysShape.Medium, 0, true, 0), 1e-9, "0 = the rests as set");
        }

        [TestMethod]
        public void CampShapes_FieldTheSameWorthAnHour()
        {
            // Heavy, medium and light all get the same rates and rewards. Health, Heat, Embers and
            // loot follow worth, so equal worth an hour is equal pay; light fields more creatures for it, heavy fewer.
            foreach (var extra in new[] { 0, 2, 4 })
            {
                double Creatures(FraysShape shape)
                {
                    var scale = FraysRules.RestScale(20, shape, extra, true, 600);
                    var count = 0.0;

                    for (var i = 0; i < 20; i++)
                    {
                        var kind = FraysRules.PointKind(i);
                        count += 0.75 * FraysRules.PackTemplate(kind, i, shape, extra, false).Count / FraysRules.PointRest(kind, false)
                            + 0.25 * FraysRules.PackTemplate(kind, i, shape, extra, true).Count / FraysRules.PointRest(kind, true);
                    }

                    return count * 3600 / scale;
                }

                foreach (var shape in new[] { FraysShape.Heavy, FraysShape.Medium, FraysShape.Light })
                    Assert.AreEqual(600, FraysRules.SupplyPerHour(20, shape, extra, true) / FraysRules.RestScale(20, shape, extra, true, 600), 1, $"{shape} +{extra}: 600 worth an hour");

                Assert.IsTrue(Creatures(FraysShape.Light) > Creatures(FraysShape.Medium) && Creatures(FraysShape.Medium) > Creatures(FraysShape.Heavy),
                    $"+{extra}: more creatures for the same worth as packs get lighter ({Creatures(FraysShape.Heavy):0}, {Creatures(FraysShape.Medium):0}, {Creatures(FraysShape.Light):0})");
            }
        }

        [TestMethod]
        public void ThreadsFamilies_FavourChasersAndLeaveOutWhatCannotBeKilled()
        {
            var previousTables = FraysFamilies.TablesSource;
            var previousKind = FraysFamilies.KindOf;

            try
            {
                var kinds = new Dictionary<uint, FraysFightKind?>
                {
                    [1] = FraysFightKind.Melee, [2] = FraysFightKind.Melee, [3] = FraysFightKind.Archer, [4] = FraysFightKind.Caster,
                    [5] = null, // stands still or summons
                    [6] = FraysFightKind.Melee,
                    [11] = FraysFightKind.Archer, [12] = FraysFightKind.Archer, [13] = FraysFightKind.Melee,
                };

                ACE.Server.WorldEvents.Defs.SpeciesMemberDef M(uint w, int level, int role = 0) => new ACE.Server.WorldEvents.Defs.SpeciesMemberDef { Wcid = w, Name = $"m{w}", Level = level, Role = role };

                var brutes = new ACE.Server.WorldEvents.Defs.SpeciesTableDef { Id = "brute", DisplayName = "the Brutes", CreatureType = "Lugian",
                    Members = { M(1, 300), M(2, 280), M(3, 290), M(4, 300), M(5, 300), M(6, 310, 1) } };
                var archers = new ACE.Server.WorldEvents.Defs.SpeciesTableDef { Id = "archer", DisplayName = "the Archers", CreatureType = "Tumerok",
                    Members = { M(11, 300), M(12, 300), M(13, 300) } };
                var tables = new Dictionary<string, ACE.Server.WorldEvents.Defs.SpeciesTableDef> { ["brute"] = brutes, ["archer"] = archers };

                FraysFamilies.TablesSource = () => tables;
                FraysFamilies.KindOf = w => kinds.TryGetValue(w, out var k) ? k : null;

                var all = FraysFamilies.ThreadsFamilies();
                Assert.AreEqual(2, all.Count);

                var brute = all.Single(f => f.Key == "brute");
                Assert.AreEqual("Brutes", brute.Name);
                Assert.AreEqual(CreatureType.Lugian, brute.Type);
                Assert.IsFalse(brute.Contains(5), "a creature that stands still or summons is left out");
                Assert.IsTrue(brute.Contains(1) && brute.Contains(3) && brute.Contains(4));
                Assert.IsTrue(brute.Weight > all.Single(f => f.Key == "archer").Weight, "an archer-heavy family comes up less");

                // Melee creatures come up far more than the archer and the caster in the same family.
                var counts = new Dictionary<uint, int>();

                for (var i = 0; i < 1000; i++)
                {
                    var w = FraysFamilies.PickWeighted(brute.Trash, i / 1000.0);
                    counts[w] = counts.TryGetValue(w, out var n) ? n + 1 : 1;
                }

                Assert.IsTrue(counts[1] + counts[2] > 8 * ((counts.TryGetValue(3, out var a) ? a : 0) + (counts.TryGetValue(4, out var c) ? c : 0)));
                Assert.AreEqual(6u, brute.WcidFor(FraysRole.Snarl), "the Snarl is the family's best elite");
            }
            finally
            {
                FraysFamilies.TablesSource = previousTables;
                FraysFamilies.KindOf = previousKind;
            }
        }
        [TestMethod]
        public void FrayRealm_OnlyTheDriftCopy()
        {
            Assert.IsTrue(FraysManager.InFrayRealm(ACE.Entity.Position.InstanceIDFromVars(1, 0, false)), "realm 1, the Drift copy");
            Assert.IsFalse(FraysManager.InFrayRealm(0), "the base world stays retail");
            Assert.IsFalse(FraysManager.InFrayRealm(ACE.Entity.Position.InstanceIDFromVars(1, 5, true)), "never an ephemeral copy");
        }

        [TestMethod]
        public void RateTables_BelowThreads()
        {
            var xp = FraysSettings.Text(FraysSettings.XpByLevel);
            Assert.AreEqual(1.632e9, FraysRules.TableRate(xp, 310), 1);
            Assert.AreEqual(0.358e9, FraysRules.TableRate(xp, 150), 1, "below the table: its first value");
            var mid = FraysRules.TableRate(xp, 320);
            Assert.IsTrue(mid > 1.632e9 && mid < 2.9e9);
            Assert.AreEqual(6.18e9, FraysRules.TableRate(xp, 450), 1, "past the table: its last value");
            Assert.AreEqual(6.18e9, FraysRules.TableRate(xp, 999), 1, "a level 999 earns no more than 410 (it once got 42 billion XP in minutes)");
            Assert.AreEqual(-1, FraysRules.TableRate("", 300), 1e-9);
        }

        [TestMethod]
        public void HeatCurves_DefaultAcceleratesAndCarriesOn()
        {
            Assert.AreEqual(0, FraysCurve.LevelsAdded(0), 1e-9);
            Assert.AreEqual(25, FraysCurve.LevelsAdded(10), 1e-9);
            Assert.AreEqual(16, FraysCurve.LevelsAdded(7), 1e-9, "straight lines between points");
            Assert.AreEqual(95, FraysCurve.LevelsAdded(25), 1e-9, "past the table, the last stretch carries on");

            // Each step of 5 adds more than the last: a curve, not a line.
            var d1 = FraysCurve.LevelsAdded(5) - FraysCurve.LevelsAdded(0);
            var d4 = FraysCurve.LevelsAdded(20) - FraysCurve.LevelsAdded(15);
            Assert.IsTrue(d4 > d1);

            Assert.AreEqual(0.7, FraysCurve.Health(0), 1e-9, "Heat 0 easier than a Thread");
            Assert.AreEqual(3.0, FraysCurve.Health(20), 1e-9, "extra health on top of the levels, x3 at Heat 20 (was x6)");
            Assert.AreEqual(2.4, FraysCurve.Damage(20), 1e-9, "x2.4 damage at Heat 20");

            // Health is Threads' band-standard curve: 890 at 185, 13100 at 375, rising with level.
            Assert.AreEqual(890, FraysLevelScale.CurveHealth(185), 1);
            Assert.AreEqual(13100, FraysLevelScale.CurveHealth(375), 1);
            Assert.IsTrue(FraysLevelScale.CurveHealth(370) > FraysLevelScale.CurveHealth(300));

            // Without a cap a level 440 camper meets 33,000-health creatures at Heat 0 (the curve extrapolates past its data).
            // The camper's own part of the health level stops at the cap; Heat's added levels still count on top.
            Assert.AreEqual(375, FraysLevelScale.HealthLevel(440, 440, 0, 375));
            Assert.AreEqual(FraysLevelScale.TargetLevel(375, 20), FraysLevelScale.HealthLevel(FraysLevelScale.TargetLevel(440, 20), 440, 20, 375));
            Assert.IsTrue(FraysLevelScale.HealthLevel(FraysLevelScale.TargetLevel(440, 20), 440, 20, 375) > 375, "Heat still raises health past the cap");
            Assert.AreEqual(300, FraysLevelScale.HealthLevel(300, 300, 0, 375), "a camper at or under the cap is unchanged");
            Assert.AreEqual(440, FraysLevelScale.HealthLevel(440, 440, 0, 0), "0 = no cap");
            Assert.AreEqual(100, FraysLevelScale.HealthLevel(100, 440, 0, 375), "never above the creature's own level (level scaling off)");
            Assert.IsTrue(FraysLevelScale.CurveHealth(FraysLevelScale.HealthLevel(440, 440, 0, 375)) < 14000);
            Assert.AreEqual(1.64, FraysCurve.Income(20), 1e-9);
            Assert.AreEqual(1.435, FraysCurve.Income(0), 1e-9);
            Assert.AreEqual(370, FraysLevelScale.TargetLevel(300, 20));
        }

        [TestMethod]
        public void HeatCurves_ParseAnyTable()
        {
            Assert.IsNull(FraysCurve.At("", 5));
            Assert.AreEqual(3, FraysCurve.At("0:1,10:5", 5).Value, 1e-9);
            Assert.AreEqual(1, FraysCurve.At("10:5,0:1", -3).Value, 1e-9, "sorted, and the first value below the table");
            Assert.AreEqual(2, FraysCurve.At("junk,4:2", 9).Value, 1e-9, "one good point holds everywhere");
            Assert.IsTrue(FraysCurve.Describe(300).Contains("level 370"));
        }

        [TestMethod]
        public void TownHouses_EveryOpeningIsCovered()
        {
            Assert.AreEqual(17, FraysHouses.Wcids.Count());

            foreach (var wcid in FraysHouses.Wcids)
            {
                Assert.IsTrue(FraysHouses.TryFootprint(wcid, out _), $"house {wcid} has a footprint");
                var openings = FraysHouses.Openings(wcid);

                // City walls (12 to 17) get no covers; every house has openings.
                Assert.AreEqual(FraysHouses.IsWall(wcid), openings.Count == 0, $"house {FraysHouses.Number(wcid)}");

                foreach (var o in openings)
                {
                    var covers = FraysHouses.CoversFor(o);
                    Assert.IsTrue(covers.Count > 0);
                    var want = FraysHouses.IsDoorway(o) ? FraysDefs.TownDoor : FraysHouses.WindowCover;   // no style given: Aluvian
                    Assert.IsTrue(covers.All(c => c.Wcid == want));
                    // Together the covers more than span the opening (no gaps at the edges).
                    Assert.IsTrue(1.92f * covers[0].Scale * covers.Count >= o.Width + 0.1f, $"house {wcid}: covers {o.Width}");
                    Assert.IsTrue(2.5f * covers[0].Scale >= o.Height + 0.05f);
                }
            }

            Assert.AreEqual(12, FraysHouses.FirstWall);
            Assert.IsTrue(FraysHouses.IsWall(FraysHouses.FirstWcid + 11));
            Assert.IsFalse(FraysHouses.IsWall(FraysHouses.FirstWcid + 10));

            // House 4's tall opening a step up from the floor is a window; a balcony's high one is a door.
            Assert.IsFalse(FraysHouses.IsDoorway(new FraysHouses.Opening(0, 0, 1.0f, 1.9f, 2.55f, 0)));
            Assert.IsTrue(FraysHouses.IsDoorway(new FraysHouses.Opening(0, 0, 8.8f, 1.9f, 2.5f, 0)));
            Assert.IsTrue(FraysHouses.IsDoorway(new FraysHouses.Opening(0, 0, 0, 1.9f, 2.5f, 0)));

            // A wide open front (10.8 m by 2.9 m) is shut with a row of doors, not one huge door.
            var wide = FraysHouses.CoversFor(new FraysHouses.Opening(5.6f, 2.1f, 0, 10.82f, 2.9f, 0));
            Assert.AreEqual(5, wide.Count);
            Assert.IsTrue(wide.All(c => c.Scale < 1.3f));

            // A window facing east (out = +x): one lattice window centred on it, its depth running west into the wall, its
            // face just outside the opening.
            var win = FraysHouses.CoversFor(new FraysHouses.Opening(2, 0, 1, 1.1f, 1.5f, -90));
            Assert.AreEqual(1, win.Count);
            Assert.AreEqual(FraysHouses.WindowCover, win[0].Wcid);
            Assert.AreEqual(90f, win[0].Face, 0.01f);
            Assert.AreEqual(2 + FraysHouses.OutsideBy, win[0].Local.X, 0.001f);
            Assert.AreEqual(1.75f, win[0].Local.Z - FraysHouses.AluvianDrop * win[0].Scale + 1.245f * win[0].Scale, 0.01f);

            // Hand fixes: pushed in, flipped to the other side, or taken away.
            var pushed = FraysHouses.CoversFor(new FraysHouses.Opening(2, 0, 1, 1.1f, 1.5f, -90), new FraysHouses.Fix { Out = -0.1f });
            Assert.AreEqual(2 + FraysHouses.OutsideBy - 0.1f, pushed[0].Local.X, 0.001f);
            var flipped = FraysHouses.CoversFor(new FraysHouses.Opening(2, 0, 1, 1.1f, 1.5f, -90), new FraysHouses.Fix { Flip = true });
            Assert.AreEqual(-90f, flipped[0].Face, 0.01f);
            Assert.AreEqual(0, FraysHouses.CoversFor(new FraysHouses.Opening(2, 0, 1, 1.1f, 1.5f, -90), new FraysHouses.Fix { Kind = "none" }).Count);
            Assert.AreEqual(FraysDefs.TownDoor, FraysHouses.CoversFor(new FraysHouses.Opening(2, 0, 1, 1.1f, 1.5f, -90), new FraysHouses.Fix { Kind = "door" })[0].Wcid);

            // Door styles: house 9 is Gharu, 11 is Sho, the rest Aluvian; a hand fix can pick one. A Sho door (0.4 deep,
            // centred on its origin) has its origin half its depth in from the opening, so its outer face is just outside.
            Assert.AreEqual(FraysHouses.Gharu, FraysHouses.StyleOf(9));
            Assert.AreEqual(FraysHouses.Sho, FraysHouses.StyleOf(11));
            Assert.AreEqual(FraysHouses.Aluvian, FraysHouses.StyleOf(1));
            Assert.AreEqual(FraysHouses.Aluvian, FraysHouses.StyleOf(19));   // the Great Hall
            Assert.AreEqual(FraysHouses.Stone, FraysHouses.StyleOf(21));      // the watchtower
            Assert.AreEqual(FraysHouses.Stone, FraysHouses.StyleOf(FraysHouses.FirstWall));
            var doorway = new FraysHouses.Opening(2, 0, 0, 2.0f, 2.5f, -90);

            // The stone slab is one piece, wider than tall: a doorway gets one, its outer face (0.16 m before its origin along
            // its own -y) just outside the opening.
            var stone = FraysHouses.CoversFor(doorway, null, FraysHouses.Stone);
            Assert.AreEqual(1, stone.Count);
            Assert.AreEqual(2000148u, stone[0].Wcid);
            Assert.AreEqual(2 + FraysHouses.OutsideBy, stone[0].Local.X + 0.16f * stone[0].Scale, 0.001f);
            var sho = FraysHouses.CoversFor(doorway, null, FraysHouses.Sho);
            Assert.AreEqual(2000142u, sho[0].Wcid);
            Assert.AreEqual(2 + FraysHouses.OutsideBy - 0.2f * sho[0].Scale, sho[0].Local.X, 0.001f);
            Assert.AreEqual(2000143u, FraysHouses.CoversFor(doorway, new FraysHouses.Fix { Kind = "gharu" }, FraysHouses.Sho)[0].Wcid);
            Assert.AreEqual(FraysHouses.WindowCover, FraysHouses.CoversFor(doorway, new FraysHouses.Fix { Kind = "window" }, FraysHouses.Sho)[0].Wcid);

            // Retail's own doors on houses 3 and 4: house 3's three doorways get
            // retail's Aluvian door at full size where retail puts it, and no fitted cover; a hand fix swaps one back to fitted.
            var h3 = FraysHouses.FirstWcid + 2;
            var all = FraysHouses.AllCovers(h3, _ => null);
            var doors = all.Where(c => c.Cover.Wcid == FraysDefs.TownDoor).ToList();
            Assert.AreEqual(3, doors.Count);
            Assert.IsTrue(doors.All(d => d.Cover.Scale >= 1 && d.Opening >= 0));
            Assert.AreEqual(2, all.Count(c => c.Cover.Wcid == FraysHouses.WindowCover));   // its two windows, fitted
            var fixedOne = FraysHouses.AllCovers(h3, i => i == doors.First(d => d.Opening != 4).Opening ? new FraysHouses.Fix { Out = -0.05f } : null);
            Assert.AreEqual(1, fixedOne.Count(c => c.Opening == doors.First(d => d.Opening != 4).Opening));
            Assert.AreNotEqual(1f, fixedOne.First(c => c.Opening == doors.First(d => d.Opening != 4).Opening).Cover.Scale);
            // House 11: retail's Sho doors in both doorways, and nothing on its side walls (no opening there).
            var h11 = FraysHouses.AllCovers(FraysHouses.FirstWcid + 10, _ => null);
            Assert.AreEqual(2, h11.Count(c => c.Cover.Wcid == 2000144u && c.Opening >= 0));
            Assert.IsTrue(h11.All(c => c.Opening >= 0));
            Assert.AreEqual(0, h11.Count(c => c.Cover.Wcid == 2000142u));
            // House 4 ("missing door"): retail's door sits behind the doorway's black panel; it is moved out just in front.
            var h4door = FraysHouses.AllCovers(FraysHouses.FirstWcid + 3, _ => null).Single(c => c.Cover.Wcid == FraysDefs.TownDoor);

            Assert.AreEqual(0f, h4door.Cover.Face, 0.5f);   // square to its wall
            Assert.AreEqual(-6f, h4door.Cover.Tilt, 0.01f);   // leans in with its wall, its foot just in front of the panel's
            Assert.IsTrue(h4door.Cover.Local.Y < -8.61f && h4door.Cover.Local.Y > -8.65f, $"house 4 door foot y {h4door.Cover.Local.Y}");
            Assert.IsTrue(h4door.Cover.Scale > 1.05f);       // overlaps its frame
            // A door sticking out of its opening goes back flush ("house 3 - door is too far forward").
            var front = new FraysHouses.Opening(0, 0, 0, 2, 2.5f, 180);   // outward is -y
            Assert.AreEqual(-0.01f, FraysHouses.InFrontOf(front, FraysDefs.TownDoor, new System.Numerics.Vector2(0, -0.3f), 0).Y, 0.001f);
            // House 3's corner door is exactly where it was placed in ACE Creator.
            var h3door5 = FraysHouses.AllCovers(h3, _ => null).Single(c => c.Opening == 4).Cover;
            Assert.AreEqual(FraysDefs.TownDoor, h3door5.Wcid);
            Assert.AreEqual(-5.489f, h3door5.Local.X, 0.001f);
            Assert.AreEqual(2.637f, h3door5.Local.Y, 0.001f);
            Assert.AreEqual(178.87f, h3door5.Face, 0.01f);
            Assert.AreEqual(1f, h3door5.Scale, 0.001f);
            // House 9: Gharu doors in its four doorways, and none where there is no opening.
            var h9 = FraysHouses.AllCovers(FraysHouses.FirstWcid + 8, i => FraysHouses.FixFor(FraysHouses.FirstWcid + 8, i));
            Assert.AreEqual(4, FraysHouses.Openings(FraysHouses.FirstWcid + 8).Count);
            Assert.IsTrue(h9.All(c => c.Opening >= 0), "house 9: covers in openings only");
            Assert.AreEqual(4, h9.Count(c => c.Cover.Wcid == 2000143u));
            // House 7: both doors are town doors placed by hand in ACE Creator (its ground door was retail's).
            var h7 = FraysHouses.AllCovers(FraysHouses.FirstWcid + 6, _ => null);
            Assert.AreEqual(0, h7.Count(c => c.Cover.Wcid == 2000145u));
            Assert.AreEqual(2, h7.Count(c => c.Cover.Wcid == FraysDefs.TownDoor));
            Assert.IsTrue(h7.Any(c => c.Cover.Wcid == FraysDefs.TownDoor && c.Cover.Local.Z > 8));
            // House 7's 3rd floor: 6 plank tiles fill its 4 by 6 m hole, tops flush with the floor (15.2 m), inside the hole.
            var f7 = FraysHouses.Floors(FraysHouses.FirstWcid + 6);
            Assert.AreEqual(6, f7.Count);
            Assert.IsTrue(f7.All(t => t.Wcid == FraysHouses.FloorTile));
            Assert.IsTrue(f7.All(t => Math.Abs(t.Local.Z + 0.05f * t.Scale - 15.2f) < 0.001f), "tops flush with the floor");
            Assert.IsTrue(f7.All(t => Math.Abs(t.Local.X) + t.Scale <= 2.05f && t.Local.Y - t.Scale >= -2.1f && t.Local.Y + t.Scale <= 4.05f), "inside the hole");
            Assert.AreEqual(0, FraysHouses.Floors(FraysHouses.FirstWcid).Count);

            // Arwic's Cottage, Great Hall and Chapel get covers too, numbered after the city walls. The Great Hall's 6th to 9th
            // openings are added by hand (its two inside doorways, square to the hall, and both sides of its corner entrance), and the
            // Cottage's 4th (its upstairs door at the gable doorway).
            foreach (var (wcid, n, openings) in new[] { (FraysDefs.ArwicCottage, 18, 4), (FraysDefs.ArwicGreatHall, 19, 9), (FraysDefs.ArwicChapel, 20, 2) })
            {
                Assert.IsTrue(FraysHouses.IsHouse(wcid) && !FraysHouses.IsWall(wcid));
                Assert.AreEqual(n, FraysHouses.Number(wcid));
                Assert.AreEqual(wcid, FraysHouses.WcidOf(n));
                Assert.AreEqual(openings, FraysHouses.Openings(wcid).Count);
                Assert.IsTrue(FraysHouses.AllCovers(wcid, _ => null).Count >= openings);
            }
            var hallOpening6 = FraysHouses.Openings(FraysDefs.ArwicGreatHall)[5];
            Assert.AreEqual(-90f, hallOpening6.Face);
            var flush = FraysHouses.AllCovers(FraysDefs.ArwicGreatHall, i => i == 5 ? new FraysHouses.Fix { Kind = "flush" } : null);
            Assert.AreEqual(1, flush.Count(c => c.Cover.Wcid == FraysHouses.Flush.Wcid && c.Opening == 5));
            Assert.AreEqual("Chapel", FraysHouses.Label(FraysDefs.ArwicChapel));
            Assert.AreEqual("Town House 3", FraysHouses.Label(FraysHouses.FirstWcid + 2));
            Assert.AreEqual(FraysHouses.FirstWcid + 4, FraysHouses.WcidOf(5));

            // The village: no loose doors, no old Arwic walls, gates or towers, and every piece on its landblock.
            Assert.AreEqual(0, FraysDefs.TownDoors.Count);
            Assert.IsFalse(FraysDefs.TownPieces.Any(t => t.Wcid == FraysDefs.ArwicGateA || t.Wcid == FraysDefs.ArwicGateB || t.Wcid >= FraysDefs.ArwicWallB && t.Wcid <= FraysDefs.ArwicWallD));

            // The ring: a watchtower on each corner, and every wall section's two ends tucked inside a tower (no gaps at joins).
            (float X, float Y) Global(ushort lb, float x, float y) => (((lb >> 8) - 0xD5) * 192 + x, ((lb & 0xFF) - 0xDA) * 192 + y);
            (float X, float Y) Turn(float f, float x, float y)
            {
                var r = f * Math.PI / 180;
                return ((float)(x * Math.Cos(r) - y * Math.Sin(r)), (float)(x * Math.Sin(r) + y * Math.Cos(r)));
            }
            var towers = FraysDefs.TownPieces.Where(t => t.Wcid == FraysDefs.ArwicTower).Select(t =>
            {
                var o = Global(t.Landblock, t.X, t.Y);
                var d = Turn(t.Facing, FraysDefs.TowerOffset, FraysDefs.TowerOffset);
                return (X: o.X + d.X, Y: o.Y + d.Y);
            }).ToList();
            Assert.AreEqual(FraysDefs.RingCorners.Length + 2, towers.Count);   // plus one each side of the water gate
            foreach (var (cu, cv) in FraysDefs.RingCorners)
            {
                var c = FraysDefs.TempleToGlobal(cu, cv);
                Assert.IsTrue(towers.Any(t => Math.Abs(t.X - c.X) < 0.01f && Math.Abs(t.Y - c.Y) < 0.01f), $"a tower on corner ({cu}, {cv})");
            }
            var walls = FraysDefs.TownPieces.Where(t => t.Wcid == FraysDefs.ArwicWall).ToList();
            Assert.AreEqual(10, walls.Count);
            foreach (var w in walls)
            {
                var o = Global(w.Landblock, w.X, w.Y);
                var line = Turn(w.Facing, 0, FraysDefs.WallLineOffset);
                var half = Turn(w.Facing, 12, 0);
                var mid = (X: o.X + line.X, Y: o.Y + line.Y);
                foreach (var end in new[] { (X: mid.X + half.X, Y: mid.Y + half.Y), (X: mid.X - half.X, Y: mid.Y - half.Y) })
                {
                    var near = towers.Min(t => Math.Sqrt((t.X - end.X) * (t.X - end.X) + (t.Y - end.Y) * (t.Y - end.Y)));
                    var nextWall = walls.Where(x => x != w).Any(x =>
                    {
                        var xo = Global(x.Landblock, x.X, x.Y);
                        var xl = Turn(x.Facing, 0, FraysDefs.WallLineOffset);
                        var xh = Turn(x.Facing, 12, 0);
                        var xm = (X: xo.X + xl.X, Y: xo.Y + xl.Y);
                        return new[] { (xm.X + xh.X, xm.Y + xh.Y), (xm.X - xh.X, xm.Y - xh.Y) }.Any(e => Math.Abs(e.Item1 - end.X) < 0.01f && Math.Abs(e.Item2 - end.Y) < 0.01f);
                    });
                    Assert.IsTrue(near <= 3.5 || nextWall, $"a wall end at ({end.X:0.0}, {end.Y:0.0}) is {near:0.0} m from the nearest tower");
                }

                // Its parapet (local +y) faces out of the ring, away from the temple.
                var outward = Turn(w.Facing, 0, 1);
                Assert.IsTrue(outward.X * (mid.X - 180) + outward.Y * (mid.Y - 180) > 0, "parapet faces out");
            }

            // The arrival camp stays inside the ring: its sign is nearer the temple than the beach wall is.
            Assert.IsTrue(FraysDefs.RingCorners.All(c => Math.Abs(c.U) + Math.Abs(c.V) > 30));
            var temple = FraysDefs.AtTemple(1, 0, 0, 0);
            Assert.AreEqual((ushort)0xD5DA, temple.Landblock);
            Assert.AreEqual(180f, temple.X, 0.001f);
            // Every house's doorways are covered, by retail or fitted.
            foreach (var w in FraysHouses.Wcids.Where(w => !FraysHouses.IsWall(w)))
            {
                var covers = FraysHouses.AllCovers(w, _ => null);
                var openings = FraysHouses.Openings(w);
                for (var i = 0; i < openings.Count; i++)
                    Assert.IsTrue(covers.Any(c => c.Opening == i), $"house {FraysHouses.Number(w)} opening {i + 1}");
            }
        }

        [TestMethod]
        public void Village_BuildingsStandInsideTheRingClearOfWallsTempleAndEachOther()
        {
            // Some buildings are moved (Great Hall onto the flat, Town Houses 5 and 6 in the beach corner); every
            // building's footprint stays 2.5 m inside the wall line, off the temple and the arrival camp, and off the others.
            var r2 = (float)Math.Sqrt(2);
            (float X, float Y) Global(ushort lb, float x, float y) => (((lb >> 8) - 0xD5) * 192 + x, ((lb & 0xFF) - 0xDA) * 192 + y);
            float Inside(float gx, float gy)
            {
                float u = (gx - 180 + gy - 180) / r2, v = (gx - 180 - (gy - 180)) / r2, worst = float.MaxValue;
                var c = FraysDefs.RingCorners;
                for (var i = 0; i < c.Length; i++)
                {
                    var a = c[i]; var b = c[(i + 1) % c.Length];
                    float du = b.U - a.U, dv = b.V - a.V, len = (float)Math.Sqrt(du * du + dv * dv);
                    worst = Math.Min(worst, -(du * (v - a.V) - dv * (u - a.U)) / len);
                }
                return worst;
            }

            var buildings = FraysDefs.TownPieces.Where(t => FraysHouses.IsHouse(t.Wcid) && t.Wcid != FraysDefs.ArwicTower).Select(t =>
            {
                Assert.IsTrue(FraysHouses.TryFootprint(t.Wcid, out var f), FraysHouses.Label(t.Wcid));
                var o = Global(t.Landblock, t.X, t.Y);
                var a = t.Facing * Math.PI / 180;
                float c = (float)Math.Cos(a), s = (float)Math.Sin(a);
                var pts = new List<(float X, float Y)>();
                for (var i = 0; i <= 4; i++)
                    for (var j = 0; j <= 4; j++)
                    {
                        float lx = f.MinX + (f.MaxX - f.MinX) * i / 4, ly = f.MinY + (f.MaxY - f.MinY) * j / 4;
                        pts.Add((o.X + lx * c - ly * s, o.Y + lx * s + ly * c));
                    }
                return (Name: FraysHouses.Label(t.Wcid), O: o, C: c, S: s, F: f, Pts: pts);
            }).ToList();
            Assert.AreEqual(4, buildings.Count);

            foreach (var b in buildings)
            {
                foreach (var p in b.Pts)
                {
                    Assert.IsTrue(Inside(p.X, p.Y) >= 2.5f, $"{b.Name} reaches the wall at ({p.X:0.0}, {p.Y:0.0})");
                    Assert.IsTrue(Math.Abs(p.X - 180) > 12 || Math.Abs(p.Y - 180) > 12, $"{b.Name} reaches the temple");
                    Assert.IsFalse(p.X > 168 && p.X < 192 && p.Y > 146 && p.Y < 168, $"{b.Name} reaches the arrival camp");
                }

                foreach (var other in buildings.Where(x => x.Name != b.Name))
                    foreach (var p in b.Pts)
                    {
                        float dx = p.X - other.O.X, dy = p.Y - other.O.Y, lx = dx * other.C + dy * other.S, ly = -dx * other.S + dy * other.C;
                        Assert.IsFalse(lx > other.F.MinX - 1 && lx < other.F.MaxX + 1 && ly > other.F.MinY - 1 && ly < other.F.MaxY + 1, $"{b.Name} runs into {other.Name}");
                    }
            }

            // The lifestone, the NPCs and the Sky Fray portal: the first two inside the ring, the portal out through the water gate.
            var stone = FraysDefs.Village.Single(v => v.Wcid == FraysDefs.LifeStone);
            var sg = Global(stone.Landblock, stone.Pos.X, stone.Pos.Y);
            Assert.IsTrue(Inside(sg.X, sg.Y) > 2.5f);
            foreach (var n in FraysDefs.EntranceNpcs.Where(n => n.Landblock >> 8 >= 0xD5 && n.Landblock >> 8 <= 0xD6 && (n.Landblock & 0xFF) >= 0xDA))
            {
                var ng = Global(n.Landblock, n.Pos.X, n.Pos.Y);
                Assert.IsTrue(Inside(ng.X, ng.Y) > 2.5f, $"NPC {n.Wcid} inside the ring");
                Assert.AreEqual((ushort)((int)(n.Pos.X / 24) * 8 + (int)(n.Pos.Y / 24) + 1), n.Cell, $"NPC {n.Wcid} outdoor cell");
            }
            var portal = FraysDefs.Village.Single(v => v.Wcid == FraysDefs.SkyFrayPortal);
            Assert.IsTrue(Inside(portal.Pos.X, portal.Pos.Y) < 0 && Math.Abs(portal.Pos.X - 180) < 2, "the portal is out through the gate");
            Assert.IsFalse(FraysDefs.Village.Any(v => v.Wcid == FraysDefs.EmberCookfire && v.Landblock == FraysDefs.V));
            Assert.AreEqual(2, FraysDefs.Village.Count(v => v.Wcid == FraysDefs.BrazierFlame));
        }

        [TestMethod]
        public void DeathRespawn_AtFireIsOnByDefault_NoPlayerGoesToLifestone()
        {
            Assert.IsTrue(FraysSettings.TryFind(FraysSettings.DeathAtFire, out var s));
            Assert.AreEqual(FraysSettingKind.Toggle, s.Kind);
            Assert.AreEqual(1, s.Default);
            Assert.IsNull(FraysLongTerm.DeathRespawn(null));
        }

        [TestMethod]
        public void KillBudget_HoldsKillPayToTheCapAnHour()
        {
            // a fast killer: 14 times the pace (164 an hour), one kill every 1.57 s, for an hour at cap 1.25
            const double cap = 1.25, pace = 164;
            var bucket = cap / 6;
            var paid = 0.0;
            var gap = 3600.0 / (pace * 14);

            for (var t = gap; t <= 3600; t += gap)
            {
                bucket = FraysRules.RefillKillBudget(bucket, gap, cap);
                var (share, left) = FraysRules.SpendKillBudget(bucket, 1 / pace, cap);
                bucket = left;
                paid += share / pace;
            }

            // never more than the cap an hour plus the 10 minutes banked
            Assert.IsTrue(paid <= cap * 7 / 6 + 1e-6, $"paid {paid}");
            Assert.IsTrue(paid >= cap - 0.01, $"paid {paid}");

            // a kill at the pace pays in full; no cap pays in full
            Assert.AreEqual(1.0, FraysRules.SpendKillBudget(cap / 6, 1 / pace, cap).Paid, 1e-9);
            Assert.AreEqual(1.0, FraysRules.SpendKillBudget(0, 1 / pace, 0).Paid, 1e-9);
            Assert.AreEqual(0.0, FraysRules.SpendKillBudget(0, 1 / pace, cap).Paid, 1e-9);
            Assert.AreEqual(cap / 6, FraysRules.RefillKillBudget(0, 99999, cap), 1e-9);
        }

        [TestMethod]
        public void HitCap_HoldsOneHitToAShareOfMaxHealth_RisingWithHeat()
        {
            // Heals should matter: no one hit takes most of a camper's health at low Heat.
            Assert.AreEqual(0.15, FraysCurve.HitCap(0).Value, 1e-9);
            Assert.AreEqual(0.25, FraysCurve.HitCap(10).Value, 1e-9);
            Assert.AreEqual(0.40, FraysCurve.HitCap(20).Value, 1e-9);
            Assert.IsTrue(FraysCurve.HitCap(5) > FraysCurve.HitCap(0) && FraysCurve.HitCap(15) > FraysCurve.HitCap(10));

            Assert.AreEqual(67.5, FraysRules.CapHit(400, 450, 0.15), 1e-9, "a 400 hit on a 450-health camper at Heat 0");
            Assert.AreEqual(50, FraysRules.CapHit(50, 450, 0.15), 1e-9, "a small hit is untouched");
            Assert.AreEqual(400, FraysRules.CapHit(400, 450, null), 1e-9, "no cap set");
            Assert.AreEqual(400, FraysRules.CapHit(400, 0, 0.15), 1e-9, "no max health known");

            // The cap is taken after armor and resists (capping the raw hit first counted armor twice,
            // so hits at Heat 17 did almost nothing), and the camper's own damage cuts scale the capped hit (Vanguards stay useful).
            // 450 health, 15% cap = 67.5.
            Assert.AreEqual(67.5, FraysRules.CapHitBeforeMitigation(800, 800, 1, 1, 450, 0.15), 1e-9, "no mitigation: the full cap");
            Assert.AreEqual(67.5, FraysRules.CapHitBeforeMitigation(400, 800, 1, 1, 450, 0.15), 1e-9, "armor let half through, still above the cap");
            Assert.AreEqual(33.75, FraysRules.CapHitBeforeMitigation(400, 800, 1, 0.5, 450, 0.15), 1e-9, "a 50% damage cut (Bulwark) halves the capped hit");
            Assert.AreEqual(40, FraysRules.CapHitBeforeMitigation(40, 2000, 1, 1, 450, 0.15), 1e-9, "heavy armor: what gets through lands in full, not shrunk again");
            Assert.AreEqual(60, FraysRules.CapHitBeforeMitigation(40, 2000, 1.5, 1, 450, 0.15), 1e-9, "Heat's boost on what gets through");
            Assert.AreEqual(20, FraysRules.CapHitBeforeMitigation(20, 40, 1, 1, 450, 0.15), 1e-9, "a small hit is untouched");
            Assert.AreEqual(67.5, FraysRules.CapHitBeforeMitigation(400, 0, 1, 1, 450, 0.15), 1e-9, "spells: capped as it lands");
        }

        [TestMethod]
        public void DeathClearsPack_OnByDefault_FadesOnlyCreaturesAboveTheCooledHeat()
        {
            Assert.IsTrue(FraysSettings.TryFind(FraysSettings.HeatDeathClearsPack, out var s));
            Assert.AreEqual(FraysSettingKind.Toggle, s.Kind);
            Assert.AreEqual(1, s.Default);

            Assert.IsTrue(FraysRules.FadesOnDeath(13, 11, false));    // built at Heat 13, the fire cooled to 11
            Assert.IsFalse(FraysRules.FadesOnDeath(11, 11, false));   // at the new Heat already: stays
            Assert.IsFalse(FraysRules.FadesOnDeath(9, 11, false));
            Assert.IsFalse(FraysRules.FadesOnDeath(13, 11, true));    // the Snarl stays
        }

        [TestMethod]
        public void RetakeCooldown_AfterQuitting_NotAfterStaffOrUnload()
        {
            Assert.AreEqual(300, FraysSettings.Number(FraysSettings.RetakeCooldownSeconds));
            Assert.AreEqual(300, FraysRules.RetakeWait(1000, 300, 1000), 1e-9);
            Assert.AreEqual(100, FraysRules.RetakeWait(1000, 300, 1200), 1e-9);
            Assert.AreEqual(0, FraysRules.RetakeWait(1000, 300, 1400), 1e-9, "the wait is over");
            Assert.AreEqual(0, FraysRules.RetakeWait(1000, 0, 1000), 1e-9, "0 = no cooldown");

            Assert.IsTrue(FraysRules.CountsForRetake("you gave up the fire"));
            Assert.IsTrue(FraysRules.CountsForRetake("you left the fellowship"));
            Assert.IsFalse(FraysRules.CountsForRetake("put out by staff"));
            Assert.IsFalse(FraysRules.CountsForRetake("the dungeon unloaded"));
        }

        [TestMethod]
        public void RichPointSettings_Defaults()
        {
            Assert.IsTrue(FraysSettings.Toggle(FraysSettings.FillOnTake), "every pack at once when a camp is taken");
            Assert.AreEqual(3, FraysSettings.Number(FraysSettings.SnarlBarGildedMult), "a Gilded kill fills the Snarl bar three times as fast");
        }

        [TestMethod]
        public void ThreadsShaped_BuiltAtTheCampersOwnLevel_UnlessHeatLevelsIsOn()
        {
            Assert.IsTrue(FraysSettings.TryFind(FraysSettings.ThreadsShapeHeatLevels, out var s));
            Assert.AreEqual(FraysSettingKind.Toggle, s.Kind);
            Assert.AreEqual(0, s.Default);

            Assert.AreEqual(440, FraysLevelScale.ShapedLevel(440, 13, false));   // a Thread you would run yourself
            Assert.AreEqual(440, FraysLevelScale.ShapedLevel(440, 0, false));
            Assert.AreEqual(FraysLevelScale.TargetLevel(440, 13), FraysLevelScale.ShapedLevel(440, 13, true));
            Assert.IsTrue(FraysLevelScale.ShapedLevel(440, 13, true) > 440);

            // Never above the highest Thread a player can run (threads_shape_max_level, 410).
            Assert.IsTrue(FraysSettings.TryFind(FraysSettings.ThreadsShapeMaxLevel, out var cap));
            Assert.AreEqual(410, cap.Default);
            Assert.AreEqual(410, FraysLevelScale.ShapedLevel(440, 13, false, 410));
            Assert.AreEqual(410, FraysLevelScale.ShapedLevel(400, 13, true, 410));
            Assert.AreEqual(300, FraysLevelScale.ShapedLevel(300, 13, false, 410));
        }
    }
}
