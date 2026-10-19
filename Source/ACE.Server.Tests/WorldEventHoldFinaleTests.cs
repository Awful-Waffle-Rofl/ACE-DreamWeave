using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

using ACE.DatLoader;
using ACE.DatLoader.FileTypes;
using ACE.Entity.Enum;
using ACE.Server.WorldEvents;
using ACE.Server.WorldEvents.Defs;
using ACE.Server.WorldObjects;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ACE.Server.Tests
{
    /// <summary>
    /// The 2026-10-19 round: the finale boss as tall as the named bosses and threatening the envoys without deleting one,
    /// the clear spawn area, the corrupted tint, the chain to a different town and the reward for each stage. Shares the
    /// builders in WorldEventHoldTests.
    /// </summary>
    public partial class WorldEventHoldTests
    {
        // ---- the finale boss's size ----------------------------------------------------------------------------

        [TestMethod]
        public void FinaleBossSize_MedianAndHeightScale()
        {
            Assert.AreEqual(5.0, WorldEventFinaleBoss.MedianHeight(new[] { 7.0, 5.0, 3.0 }));
            Assert.AreEqual(4.0, WorldEventFinaleBoss.MedianHeight(new[] { 3.0, 5.0 }));
            Assert.IsNull(WorldEventFinaleBoss.MedianHeight(new[] { 0.0, double.NaN, -1 }));

            Assert.AreEqual(6.0 / 1.8, WorldEventFinaleBoss.HeightScale(6.0, 1.8, 1.0).Value, 1e-5, "a 1.8 m humanoid to 6 m");
            Assert.AreEqual(1.5, WorldEventFinaleBoss.HeightScale(6.0, 2.0, 2.0).Value, 1e-5, "its own ObjScale counts");
            Assert.AreEqual(WorldEventFinaleBoss.MaxHeightScale, WorldEventFinaleBoss.HeightScale(6.3, 0.2, 1.0).Value, "never past the ceiling");
            Assert.AreEqual(WorldEventFinaleBoss.MinHeightScale, WorldEventFinaleBoss.HeightScale(1.0, 9.0, 1.0).Value, "nor below the floor");
            Assert.IsNull(WorldEventFinaleBoss.HeightScale(6.0, 0, 1.0), "no model height: the fallback applies");

            Assert.AreEqual(1.84 * 5.3 * 0.67, WorldEventFinaleBoss.RenderedHeight(1.84, 5.3, 0.67), 1e-9, "Aurex as it stands in game");
            Assert.AreEqual(4.6 * 0.67, WorldEventFinaleBoss.FallbackNamedScale, 0.01, "the named bosses' median ObjScale times the boss multiplier");
        }

        /// <summary>
        /// The ten named bosses in bosses.json, as the world database carried them on 2026-10-18 (setup and ObjScale), measured
        /// against the client DAT: they stand 2.8 to 7.6 m in game (x0.67), median about 6.3 m. That is the finale boss's height.
        /// </summary>
        [TestMethod]
        public void FinaleBossSize_NamedBossesStandAboutSixMetres_WhenTheDatIsPresent()
        {
            if (!TryInitDat(out var why))
                Assert.Inconclusive(why);

            var bosses = new (string Name, uint Setup, double Scale)[]
            {
                ("Vhaleth", 0x02000AAD, 2.5), ("The Pale Idol of Bak't", 0x02001AA4, 5.85), ("Aurex", 0x020006AC, 5.3), ("The Red Reaping", 0x02001445, 3.4),
                ("Legate Vessaryn", 0x02000059, 6.2), ("Ssethaun", 0x02001813, 6.3), ("Ghurrak Bonehide", 0x02000964, 4.2), ("The Black Draught", 0x020014A0, 4.6),
                ("The Violet Wake", 0x02001494, 4.0), ("The Blackwing Eclipse", 0x0200101A, 5.8)
            };

            var heights = bosses.Select(b => WorldEventFinaleBoss.RenderedHeight(DatManager.PortalDat.ReadFromDat<SetupModel>(b.Setup).Height, b.Scale, 0.67)).ToList();
            var median = WorldEventFinaleBoss.MedianHeight(heights).Value;

            Assert.IsTrue(median > 5.5 && median < 7.0, $"median {median:0.00} m ({string.Join(", ", heights.Select(h => h.ToString("0.0")))})");
            Assert.IsTrue(heights.Min() > 2.5 && heights.Max() < 8.0);
        }

        // ---- the finale boss's targets -------------------------------------------------------------------------

        [TestMethod]
        public void FinaleBossTarget_HealthiestFirst_KeepsIt_SwapsToAnother()
        {
            var t = new List<(uint, uint, uint)> { (1, 5000, 10000), (2, 9000, 10000), (3, 7000, 10000) };

            Assert.AreEqual(2u, WorldEventFinaleBoss.PickTarget(t, 0, false), "the healthiest first");
            Assert.AreEqual(3u, WorldEventFinaleBoss.PickTarget(t, 3, false), "it keeps its target while it stands");
            Assert.AreEqual(2u, WorldEventFinaleBoss.PickTarget(t, 3, true), "a swap goes to the healthiest of the others");
            Assert.AreEqual(3u, WorldEventFinaleBoss.PickTarget(t, 2, true), "never the same one");

            var oneLeft = new List<(uint, uint, uint)> { (1, 0, 10000), (2, 4000, 10000), (3, 0, 10000) };
            Assert.AreEqual(2u, WorldEventFinaleBoss.PickTarget(oneLeft, 2, true), "the last one standing stays its target");
            Assert.AreEqual(2u, WorldEventFinaleBoss.PickTarget(oneLeft, 1, false), "its target fell: the one standing");
            Assert.AreEqual(0u, WorldEventFinaleBoss.PickTarget(new List<(uint, uint, uint)> { (1, 0, 10000) }, 1, false), "none stands: the players");

            var rng = new Random(3);

            for (var i = 0; i < 200; i++)
            {
                var d = WorldEventFinaleBoss.SwapDelay(rng, 15, 20);
                Assert.IsTrue(d >= 15 && d <= 20, $"{d}");
            }
        }

        [TestMethod]
        public void FinaleBossPull_EnoughPlayerDamageInTheWindow_TopDamagerHoldsIt()
        {
            var log = new List<(double, uint, double)> { (100, 7, 800), (103, 8, 500), (104, 7, 600), (90, 8, 5000), (110, 9, 1) };

            var (total, top) = WorldEventFinaleBoss.RecentDamage(log, 105, 8);

            Assert.AreEqual(1900, total, "only the last 8 s, and nothing from the future");
            Assert.AreEqual(7u, top, "the top damager in the window");
            Assert.IsTrue(WorldEventFinaleBoss.PullDue(1900, 30000, 0.06), "6% of 30,000 is 1,800");
            Assert.IsFalse(WorldEventFinaleBoss.PullDue(1700, 30000, 0.06));
            Assert.IsFalse(WorldEventFinaleBoss.PullDue(1e9, 30000, 0), "share 0: only a taunt pulls it");
        }

        [TestMethod]
        public void FinaleBossLines_NameTheBossAndTheTarget()
        {
            Assert.AreEqual("Warlord Reaper turns on Sir Donovan!", WorldEventFinaleBoss.TargetLine(null, "Warlord Reaper", "Sir Donovan"));
            Assert.AreEqual("Warlord Reaper turns on Rawzz!", WorldEventFinaleBoss.PulledLine(null, "Warlord Reaper", "Rawzz"));
            Assert.AreEqual("Sir Adarl: Warlord X", WorldEventFinaleBoss.TargetLine("{target}: {boss}", "Warlord X", "Sir Adarl"));
        }

        [TestMethod]
        public void ScaleHit_FinaleBoss_AloneNeedsBossMinKillSeconds_AndTheSharedFloorStillHolds()
        {
            var protect = BuildProtect();
            protect.Health = null;
            protect.UndefendedSeconds = 200;
            protect.MinDeathSeconds = 60;
            protect.BossTargetsProtected = true;
            protect.BossMinKillSeconds = 50;

            var evt = BuildHoldEvent(BuildHoldGoal(protect: protect), null);

            var ward = BuildHoldCreature(ProtectDef.TimeScaledMaxHealth);
            ward.WorldEventProtected = true;
            ward.P_WorldEvent = evt;

            var boss = NewRunMonster(evt);
            boss.WorldEventFinaleBoss = true;

            // The boss alone, swinging every second: a slice sized to 50 s, so after 30 s it has taken about 30/50 of the health.
            var lost = 0.0;

            for (var t = 0; t < 30; t++)
                lost += ward.ScaleWorldEventProtectedHit(boss, 5000f, T0 + t);

            var max = ward.Health.MaxValue;

            Assert.IsTrue(lost <= max * (30 + WorldEventHoldRules.DefaultCapBurstSeconds) / 50.0 + 1, $"lost {lost:F0} of {max} in 30 s");
            Assert.IsTrue(lost >= max * 28 / 60.0, $"and it does threaten: {lost:F0}");

            // With ten ordinary attackers on the same target as well, the shared 60 s floor still holds everything together.
            var ward2 = BuildHoldCreature(ProtectDef.TimeScaledMaxHealth);
            ward2.WorldEventProtected = true;
            ward2.P_WorldEvent = evt;

            var lost2 = 0.0;

            for (var t = 0; t <= 10; t++)
            {
                lost2 += ward2.ScaleWorldEventProtectedHit(boss, 5000f, T0 + 100 + t);

                for (var m = 0; m < 10; m++)
                    lost2 += ward2.ScaleWorldEventProtectedHit(attackersPool[m] ??= NewRunMonster(evt), 30f, T0 + 100 + t);
            }

            Assert.IsTrue(lost2 <= max / 60.0 * (10 + WorldEventHoldRules.DefaultCapBurstSeconds) + 1, $"boss and wave lost {lost2:F0} in 10 s");

            // A goal without the setting: the boss is an ordinary attacker.
            protect.BossTargetsProtected = false;
            var ward3 = BuildHoldCreature(ProtectDef.TimeScaledMaxHealth);
            ward3.WorldEventProtected = true;
            ward3.P_WorldEvent = evt;

            Assert.AreEqual(WorldEventHoldRules.ProtectedHitDamage(max, WorldEventHoldRules.DefaultHitIntervalSeconds, 200), ward3.ScaleWorldEventProtectedHit(boss, 5000f, T0), 1);
        }

        [TestMethod]
        public void SeekerHeldBy_ATauntHoldsItEvenWhenSomeoneElseHits()
        {
            var monster = BuildHoldCreature();
            var taunter = BuildHoldCreature();

            Assert.IsNull(monster.WorldEventSeekerHeldBy(T0));

            monster.TauntTarget = taunter;
            monster.TauntExpiry = ACE.Server.Entity.Timers.RunningTime + 10;

            Assert.AreSame(taunter, monster.WorldEventSeekerHeldBy(T0), "a live taunt holds it");

            monster.TauntExpiry = ACE.Server.Entity.Timers.RunningTime - 1;

            Assert.IsNull(monster.WorldEventSeekerHeldBy(T0), "an expired one does not");
        }

        [TestMethod]
        public void GoalsJson_BossSettings_Validate()
        {
            string With(string extra) => HoldGoalsJson(SampleProtectJson.Replace(@"""radius"": 6.0,", @"""radius"": 6.0, " + extra), holdSeconds: 480);

            var ok = WorldEventAxisStore.Parse(null, null, With(@"""bossTargetsProtected"": true, ""bossMinKillSeconds"": 50, ""bossSwapSeconds"": 15, ""bossSwapMaxSeconds"": 20, ""bossTargetAnnounce"": [ ""{boss} turns on {target}!"", "" "" ],"), null, null);
            Assert.IsTrue(ok.Goals.ContainsKey("hold_wards"), string.Join(" | ", ok.Diagnostics));
            Assert.AreEqual(1, ok.Goals["hold_wards"].Protect.BossTargetAnnounce.Count, "the blank line is dropped");

            foreach (var (extra, expected) in new[]
                     {
                         (@"""bossMinKillSeconds"": 10,", "bossMinKillSeconds"),
                         (@"""bossSwapSeconds"": 30, ""bossSwapMaxSeconds"": 20,", "bossSwapSeconds"),
                         (@"""bossPullShare"": 0.9,", "boss pull"),
                         (@"""clearArea"": { ""minPortals"": 0 },", "protect.clearArea"),
                         (@"""corrupted"": { ""tintPalette"": ""0x05000001"" },", "tintPalette"),
                     })
            {
                var store = WorldEventAxisStore.Parse(null, null, With(extra), null, null);

                Assert.IsFalse(store.Goals.ContainsKey("hold_wards"), extra);
                Assert.IsTrue(store.Diagnostics.Any(d => d.Contains(expected)), $"{extra}: {string.Join(" | ", store.Diagnostics)}");
            }
        }

        // ---- the corrupted tint --------------------------------------------------------------------------------

        [TestMethod]
        public void Tint_ParsesAPaletteId_AndCoversTheWholePalette()
        {
            Assert.IsTrue(WorldEventTint.TryParsePalette("0x04001381", out var p) && p == 0x04001381);
            Assert.IsTrue(WorldEventTint.TryParsePalette("67113857", out p) && p == 0x04001381);
            Assert.IsTrue(WorldEventTint.TryParsePalette("none", out p) && p == 0);
            Assert.IsTrue(WorldEventTint.TryParsePalette("", out p) && p == 0);
            Assert.IsFalse(WorldEventTint.TryParsePalette("0x05001381", out _), "a texture id, not a palette");
            Assert.IsFalse(WorldEventTint.TryParsePalette("cyan", out _));

            var desc = new ACE.Entity.ObjDesc { PaletteID = 0x0400007E };
            desc.SubPalettes.Add(new ACE.Entity.Models.PropertiesPalette { SubPaletteId = 0x04000BEF, Offset = 0x18, Length = 0x8 });

            WorldEventTint.Apply(desc, 0x04001381);

            var last = desc.SubPalettes.Last();
            Assert.AreEqual(0x04001381u, last.SubPaletteId, "added last, so it wins over the creature's own colors");
            Assert.AreEqual(0, (int)last.Offset);
            Assert.AreEqual(0, (int)last.Length, "length 0 is all 2048 colors to the client");
            Assert.AreEqual(0x0400007Eu, desc.PaletteID, "the creature's base palette is kept");

            var bare = new ACE.Entity.ObjDesc();
            WorldEventTint.Apply(bare, 0x04001381);
            Assert.AreEqual(0x04001381u, bare.PaletteID, "a creature with no palette of its own gets the tint as its base");

            var none = new ACE.Entity.ObjDesc();
            WorldEventTint.Apply(none, 0);
            Assert.AreEqual(0, none.SubPalettes.Count, "palette 0: nothing");
        }

        [TestMethod]
        public void Tint_OnlyOnTheCorrupted_InTheirCreateMessage()
        {
            var plain = BuildHoldCreature();
            var corrupted = BuildHoldCreature();
            corrupted.WorldEventTintPalette = 0x04001381;

            Assert.IsFalse(plain.CalculateObjDesc().SubPalettes.Any(s => s.SubPaletteId == 0x04001381), "every other creature is unchanged");
            Assert.IsTrue(corrupted.CalculateObjDesc().SubPalettes.Any(s => s.SubPaletteId == 0x04001381 && s.Length == 0));
        }

        [TestMethod]
        public void Tint_ThePaletteIsACyanRamp_AndShowsOnPalettedModels_WhenTheDatIsPresent()
        {
            if (!TryInitDat(out var why))
                Assert.Inconclusive(why);

            var palette = DatManager.PortalDat.ReadFromDat<Palette>(0x04001381);
            Assert.AreEqual(2048, palette.Colors.Count, "a full palette");

            // Mostly cyan: blue and green well above red, from dark to bright.
            var cyan = palette.Colors.Count(c => ((c >> 8) & 0xFF) > ((c >> 16) & 0xFF) + 20 && (c & 0xFF) > ((c >> 16) & 0xFF) + 20);
            Assert.IsTrue(cyan > 1800, $"{cyan} of 2048 colors are cyan");

            // At least most named-boss bodies (a mix of humans, olthoi, grievvers, drudges) carry palette textures.
            var setups = new uint[] { 0x02000AAD, 0x020006AC, 0x02001445, 0x02000059, 0x02000964, 0x0200101A };
            var shows = setups.Count(WorldEventTint.CanShowOn);
            Assert.IsTrue(shows >= setups.Length / 2, $"{shows} of {setups.Length} show a tint");
        }

        [TestMethod]
        public void ShippedEnvoys_BossAndTintAndClearArea_WhenPresent()
        {
            var path = ContentFile("dreamweave-content", "events", "defend_envoys.goal.json");

            if (path == null)
                Assert.Inconclusive("defend_envoys.goal.json not found next to the repo");

            var store = WorldEventAxisStore.Parse(null, null, File.ReadAllText(path), null, null);
            Assert.IsTrue(store.Goals.ContainsKey("defend_envoys"), string.Join(" | ", store.Diagnostics));

            var p = store.Goals["defend_envoys"].Protect;

            Assert.AreEqual(0, p.FinaleBossScale, "as tall as the named bosses");
            Assert.IsTrue(p.BossTargetsProtected);
            Assert.IsTrue(p.BossMinKillSeconds >= 45 && p.BossMinKillSeconds <= 60, "45 to 60 s alone");
            Assert.AreEqual(15, p.BossSwapSeconds);
            Assert.AreEqual(20, p.BossSwapMaxSeconds);
            Assert.AreEqual(0x04001381u, p.EffectiveCorrupted.TintPaletteId);
            Assert.IsNotNull(p.ClearArea);

            foreach (var template in p.BossTargetAnnounce)
                AssertPlayerLine(WorldEventFinaleBoss.TargetLine(template, "Warlord Reaper", "Sir Donovan"), "Sir Donovan");

            foreach (var template in p.BossPulledAnnounce)
                AssertPlayerLine(WorldEventFinaleBoss.PulledLine(template, "Warlord Reaper", "Rawzz"), "Rawzz");
        }

        private static void AssertPlayerLine(string line, string mustName)
        {
            AssertAscii(WorldEventAnnouncer.Global(line));
            Assert.IsFalse(line.Contains('{') || line.Contains('}'), line);
            Assert.IsTrue(Regex.Matches(line.Trim(), @"[.!?]+(\s|$)").Count <= 3, line);
            Assert.IsFalse(line.IndexOf("warp", StringComparison.OrdinalIgnoreCase) >= 0, line);
            StringAssert.Contains(line, mustName);
        }

        // ---- the clear spawn area ------------------------------------------------------------------------------

        private static readonly List<WorldEventHoldRules.RingOffset> Knights = WorldEventHoldRules.RingOffsets(3, 16);

        private static PortalsDef EnvoyPortals() => new PortalsDef { Count = 4, Radius = 30, RotationDegrees = 45, Inset = 2 };

        /// <summary>Ground that is water at compass bearings <paramref name="from"/> to <paramref name="to"/> degrees, past 20 m.</summary>
        private static Func<double, double, bool> Lake(double from, double to) => (x, y) =>
        {
            var bearing = (Math.Atan2(x, y) * 180 / Math.PI + 360) % 360;

            return !(Math.Sqrt(x * x + y * y) > 20 && bearing >= from && bearing <= to);
        };

        [TestMethod]
        public void SpawnArea_OpenGround_EveryPortalWhereAsked()
        {
            var plan = WorldEventSpawnArea.Plan(Knights, EnvoyPortals(), new ClearAreaDef(), new List<AreaObstacle>(), (x, y) => true);

            Assert.IsTrue(plan.Ok);
            Assert.AreEqual(4, plan.Portals.Count);
            Assert.IsTrue(plan.Portals.All(s => !s.Moved));
            CollectionAssert.AreEqual(new[] { 45.0, 135.0, 225.0, 315.0 }, plan.Portals.Select(s => Math.Round(s.Bearing)).ToArray());
            Assert.AreEqual(0, plan.Notes.Count);
        }

        [TestMethod]
        public void SpawnArea_ATreeOnAPortalsPath_MovesThatPortal_AndSaysSo()
        {
            // A tree halfway along the 45 degree portal's path to its nearest knight (due north, 16 m).
            var (sx, sy) = WorldEventSpawnArea.At(45, 28);
            var obstacles = new List<AreaObstacle> { new AreaObstacle((sx + 0) / 2, (sy + 16) / 2, 0.8, "tree or rock") };

            var plan = WorldEventSpawnArea.Plan(Knights, EnvoyPortals(), new ClearAreaDef(), obstacles, (x, y) => true);

            Assert.IsTrue(plan.Ok);
            Assert.AreEqual(4, plan.Portals.Count);
            Assert.IsTrue(plan.Portals[0].Moved, "the blocked one moved");
            Assert.IsTrue(plan.Portals.Skip(1).All(s => !s.Moved), "the others stay");
            Assert.IsTrue(Math.Abs(WorldEventSpawnArea.AngleDiff(plan.Portals[0].Bearing, 45)) <= 30);
            Assert.IsNull(WorldEventSpawnArea.PortalBlock(plan.Portals[0].Bearing, plan.Portals[0].Radius, 2,
                Knights.Select(k => ((double)k.Dx, (double)k.Dy)).ToList(), new ClearAreaDef(), obstacles, (x, y) => true), "its new path is clear");
            StringAssert.Contains(plan.Notes.Single(), "portal 1 (45 deg, 30 m)");
            StringAssert.Contains(plan.Notes.Single(), "tree or rock");
            AssertAscii(plan.Notes.Single());
        }

        [TestMethod]
        public void SpawnArea_ABuildingAcrossAQuarter_SkipsThatPortal_KeepsTheRest()
        {
            // A lake over the south-east beyond the knights (bearings 100 to 170, past 20 m): no shift gets the 135 degree
            // portal onto dry ground.
            var plan = WorldEventSpawnArea.Plan(Knights, EnvoyPortals(), new ClearAreaDef(), new List<AreaObstacle>(), Lake(100, 170));

            Assert.IsTrue(plan.Ok, plan.Why);
            Assert.AreEqual(1, plan.Skipped);
            Assert.AreEqual(3, plan.Portals.Count);
            Assert.IsTrue(plan.Notes.Any(n => n.Contains("skipped")));
        }

        [TestMethod]
        public void SpawnArea_TooCluttered_Fails_WithTheReason()
        {
            // Water on three of the four quarters beyond the knights: one portal left, fewer than minPortals.
            var plan = WorldEventSpawnArea.Plan(Knights, EnvoyPortals(), new ClearAreaDef(), new List<AreaObstacle>(), Lake(10, 260));

            Assert.IsFalse(plan.Ok);
            StringAssert.Contains(plan.Why, "portals have a clear path");

            // A tree on a knight's spot.
            var onKnight = WorldEventSpawnArea.Plan(Knights, EnvoyPortals(), new ClearAreaDef(),
                new List<AreaObstacle> { new AreaObstacle(Knights[1].Dx + 1, Knights[1].Dy, 0.6, "tree or rock") }, (x, y) => true);

            Assert.IsFalse(onKnight.Ok);
            StringAssert.Contains(onKnight.Why, "knight's spot");

            // Water under a knight.
            var wet = WorldEventSpawnArea.Plan(Knights, EnvoyPortals(), new ClearAreaDef(), new List<AreaObstacle>(), (x, y) => !(Math.Abs(x) < 3 && y > 13));
            Assert.IsFalse(wet.Ok);
            StringAssert.Contains(wet.Why, "water or too steep");
        }

        [TestMethod]
        public void SpawnArea_ShiftsNearestFirst_PortalsKeepTheirDistance_SearchRingsOutward()
        {
            var order = WorldEventSpawnArea.ShiftOrder(30, 8);

            Assert.AreEqual((0.0, 0.0), order[0], "no change first");
            Assert.IsTrue(order.Count == 13 * 9);
            Assert.IsTrue(order.Max(o => Math.Abs(o.Degrees)) <= 30 && order.Max(o => Math.Abs(o.Metres)) <= 8);

            var search = WorldEventSpawnArea.SearchOffsets(40);
            Assert.AreEqual(8 * 12, search.Count);
            Assert.IsTrue(search.Zip(search.Skip(1)).All(p => Math.Sqrt(p.First.X * p.First.X + p.First.Y * p.First.Y) <= Math.Sqrt(p.Second.X * p.Second.X + p.Second.Y * p.Second.Y) + 1e-6), "nearest first");

            var offsets = WorldEventSpawnArea.SpotOffsets(new List<PortalSpot> { new PortalSpot { Bearing = 90, Radius = 30 } });
            Assert.AreEqual(30f, offsets[0].Dx, 1e-3);
            Assert.AreEqual(0f, offsets[0].Dy, 1e-3);
            Assert.AreEqual(WorldEventHoldRules.OutwardYaw(90), offsets[0].YawDegrees, 1e-3);
        }

        // ---- the chain to a different town ---------------------------------------------------------------------

        private static readonly List<WorldEventTown> Towns = new List<WorldEventTown>
        {
            new WorldEventTown("Eastham", 10000, 10000),
            new WorldEventTown("Eastham Outskirts", 10300, 10000),    // within townMinDistance: the same town
            new WorldEventTown("Glenden Wood", 12000, 10500),
            new WorldEventTown("Holtburg", 14000, 9000),
            new WorldEventTown("Rithwic", 9000, 13000),
            new WorldEventTown("Shoushi", 30000, 30000),
        };

        [TestMethod]
        public void ChainTown_NeverTheSameTown_OneOfTheNearest()
        {
            var rng = new Random(5);
            var picked = new HashSet<string>();

            for (var i = 0; i < 300; i++)
            {
                var town = WorldEventChain.PickTown(Towns, 10050, 10020, "Eastham", new ChainDef(), rng).Value;

                Assert.AreNotEqual("Eastham", town.Name, "never the same town");
                Assert.AreNotEqual("Eastham Outskirts", town.Name, "nor one closer than townMinDistance");
                Assert.AreNotEqual("Shoushi", town.Name, "nor one past townMaxDistance while nearer ones exist");
                picked.Add(town.Name);
            }

            CollectionAssert.AreEquivalent(new[] { "Glenden Wood", "Holtburg", "Rithwic" }, picked.ToArray(), "spread over the nearest");

            var far = WorldEventChain.PickTown(Towns.Where(t => t.Name.StartsWith("Eastham") || t.Name == "Shoushi").ToList(), 10050, 10020, "Eastham", new ChainDef(), rng);
            Assert.AreEqual("Shoushi", far.Value.Name, "none in range: the nearest past it");

            Assert.IsNull(WorldEventChain.PickTown(Towns.Take(2).ToList(), 10050, 10020, "Eastham", new ChainDef(), rng), "no other town");

            var excluded = WorldEventChain.PickTown(Towns, 10050, 10020, "Eastham", new ChainDef { TownChoices = 1 }, rng, new List<string> { "Glenden Wood" });
            Assert.AreEqual("Rithwic", excluded.Value.Name, "a town already tried is skipped: the next nearest");
        }

        [TestMethod]
        public void ChainTown_Broadcast_NamesTheTown_AndTheCountdown()
        {
            Assert.AreEqual("The fight moves to Holtburg! The Rats gather near Holtburg (42.1N, 33.6E), and you have 120 seconds to reach them.",
                WorldEventChain.AnnounceLine("The fight moves to {town}! {Family} gather near {location}, and you have {seconds} seconds to reach them.",
                    "the Rats", "Holtburg (42.1N, 33.6E)", 120, "Holtburg"));

            Assert.AreEqual("Your reward for this fight waits in the cache. Claim it before you go to Holtburg!", WorldEventChain.ReminderLine(null, "Holtburg"));
        }

        [TestMethod]
        public void ChainStageRewards_EachStageItsOwnProfile_TheLastOneRepeats()
        {
            var chain = new ChainDef { StageRewards = new List<string> { "standard", "chain_final" } };

            Assert.AreEqual("standard", chain.RewardForStage(1));
            Assert.AreEqual("chain_final", chain.RewardForStage(2));
            Assert.AreEqual("chain_final", chain.RewardForStage(3), "past the list: the last entry");
            Assert.IsNull(new ChainDef().RewardForStage(2), "no list: the default");

            var anchor = new ACE.Entity.Position(0xCE950022, 100, 100, 20, 0, 0, 0, 1, 0);
            var next = WorldEventChain.NextRequest(new WorldEventRequest(), chain, 2, 2, 1, 1, anchor, "Holtburg (42.1N, 33.6E)");

            Assert.AreEqual("chain_final", next.RewardId, "stage 2 pays the final stage's profile");

            CollectionAssert.AreEqual(new[] { "missing" }, WorldEventChain.UnknownStageRewards(new ChainDef { StageRewards = new List<string> { "standard", "missing" } }, new[] { "standard" }));
        }

        [TestMethod]
        public void GoalsJson_ChainTownSettings_Validate()
        {
            string Goals(string chain) => HoldGoalsJson(SampleProtectJson, holdSeconds: 480).Replace(@"""type"": ""Hold"",", @"""type"": ""Hold"", " + chain);

            foreach (var (chain, expected) in new[]
                     {
                         (@"""chain"": { ""townMinDistance"": 100 },", "chain town distance"),
                         (@"""chain"": { ""townMinDistance"": 2000, ""townMaxDistance"": 1000 },", "chain town distance"),
                         (@"""chain"": { ""townChoices"": 0 },", "chain.townChoices"),
                         (@"""chain"": { ""townSpotMinDistance"": 300, ""townSpotMaxDistance"": 200 },", "chain town spot distance"),
                     })
            {
                var store = WorldEventAxisStore.Parse(null, null, Goals(chain), null, null);

                Assert.IsFalse(store.Goals.ContainsKey("hold_wards"), chain);
                Assert.IsTrue(store.Diagnostics.Any(d => d.Contains(expected)), $"{chain}: {string.Join(" | ", store.Diagnostics)}");
            }

            const string Rewards = @"{ ""rewards"": [ { ""id"": ""standard"", ""displayName"": ""Hammer Crate"", ""successCrateWcid"": 1002600, ""consolationCrateWcid"": 1002601,
                ""cacheWcid"": 1002602, ""participantsPerCache"": 8, ""claimWindowSeconds"": 300 } ] }";

            var unknown = WorldEventAxisStore.Parse(null, null, Goals(@"""chain"": { ""stageRewards"": [ ""nope"" ] },"), Rewards, null);
            Assert.IsTrue(unknown.Diagnostics.Any(d => d.Contains("chain.stageRewards names 'nope'")), string.Join(" | ", unknown.Diagnostics));
        }

        [TestMethod]
        public void ShippedEnvoys_ChainToAnotherTown_WithStageRewards_WhenPresent()
        {
            var goalsPath = ContentFile("dreamweave-content", "events", "defend_envoys.goal.json");
            var rewardsPath = ContentFile("ace-content", "events", "axes", "rewards.json");

            if (goalsPath == null || rewardsPath == null)
                Assert.Inconclusive("defend_envoys.goal.json or rewards.json not found next to the repo");

            var store = WorldEventAxisStore.Parse(null, null, File.ReadAllText(goalsPath), File.ReadAllText(rewardsPath), null);
            var chain = store.Goals["defend_envoys"].Chain;

            Assert.IsTrue(chain.ToAnotherTown);
            Assert.AreEqual("standard", chain.RewardForStage(1));
            Assert.AreEqual("chain_final", chain.RewardForStage(2));
            Assert.IsFalse(store.Diagnostics.Any(d => d.Contains("stageRewards")), string.Join(" | ", store.Diagnostics));

            foreach (var template in chain.Announce)
            {
                var line = WorldEventChain.AnnounceLine(template, "the Monougas and the Ruschks", "Holtburg (42.1N, 33.6E)", 120, "Holtburg");

                AssertPlayerLine(line, "Holtburg");
                StringAssert.Contains(line, "120 seconds", "the countdown");
            }

            foreach (var template in chain.ClaimReminder)
                AssertPlayerLine(WorldEventChain.ReminderLine(template, "Holtburg"), "Holtburg");

            var standard = store.Rewards["standard"];
            var final = store.Rewards["chain_final"];

            Assert.AreEqual(0, standard.BonusCrateWcids.Count, "stage 1: the standard Hammer Crate");
            Assert.AreEqual(standard.SuccessCrateWcid, final.SuccessCrateWcid);
            Assert.IsTrue(final.BonusCrateWcids.Count >= 1, "the final stage pays more");
            Assert.AreEqual(standard.CoalWcid, final.CoalWcid, "and still only to players who fought there");
        }

        // ---- shared --------------------------------------------------------------------------------------------

        private static bool TryInitDat(out string why)
        {
            why = null;

            if (DatManager.PortalDat != null)
                return true;

            var dir = new[] { System.Environment.GetEnvironmentVariable("ACE_DAT_PATH"), @"C:\ACE\Dats\" }
                .FirstOrDefault(d => !string.IsNullOrEmpty(d) && File.Exists(Path.Combine(d, "client_portal.dat")));

            if (dir == null)
            {
                why = "client_portal.dat not found (set ACE_DAT_PATH)";
                return false;
            }

            try
            {
                Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
                DatManager.Initialize(dir, true, false);
            }
            catch (Exception ex)
            {
                why = $"the DAT could not be read: {ex.Message}";
                return false;
            }

            return DatManager.PortalDat != null;
        }
    }
}
