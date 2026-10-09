using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

using ACE.Server.ThreadDungeons;
using ACE.Server.WorldEvents;
using ACE.Server.WorldEvents.Defs;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ACE.Server.Tests
{
    /// <summary>
    /// World Events band uplift (world_events_band_uplift): per-slot uplift levels out of PickWave and
    /// PickChampion, the thin-pool widening, the WE-owned band-standard memo, and the spawn-site ordering.
    /// A live Creature cannot be built in this harness, so what runs on a Creature (BandUplift.Apply, the
    /// health floor write) is covered by its pure halves plus source-order pins.
    /// </summary>
    [TestClass]
    public class WorldEventBandUpliftTests
    {
        private Func<WorldEventRosterSelector.UpliftDials> originalUplift;
        private Func<BandDials> originalDials;

        [TestInitialize]
        public void Pin()
        {
            originalUplift = WorldEventRosterSelector.UpliftDialSource;
            originalDials = WorldEventRosterSelector.DialSource;

            // Fixed dials so no PropertyManager read is involved: trash band 1.0-1.5, floor 0.60, pool 6.
            WorldEventRosterSelector.DialSource = () => BandDials.Defaults;
            WorldEventRosterSelector.UpliftDialSource = () => WorldEventRosterSelector.UpliftDials.Defaults;

            WorldEventBandMemo.Clear();
        }

        [TestCleanup]
        public void Restore()
        {
            WorldEventRosterSelector.UpliftDialSource = originalUplift;
            WorldEventRosterSelector.DialSource = originalDials;
            WorldEventBandMemo.Clear();
        }

        private static FamilyMember Member(uint wcid, int level, int role = 0)
            => new FamilyMember { Wcid = wcid, Name = $"m{wcid}", Level = level, Role = role };

        private static FamilyDef Family(params FamilyMember[] members)
            => new FamilyDef { Id = "fam", DisplayName = "fam", Members = members.ToList() };

        private static SourceThemeDef Theme()
            => new SourceThemeDef { Id = "ambush", MaxAlive = 24, WaveCount = new ScaledCount { Base = 8, PerParticipant = 0, Cap = 8 } };

        /// <summary>One level-275 player: every slot samples L = 275, whose natural trash band is [275, 275].</summary>
        private static AudienceEstimate Top() => new AudienceEstimate(1, 275, 275, new[] { 275 });

        // ---- the headline case ----------------------------------------------------------------------------

        [TestMethod]
        public void RosterToppingAt180_WithL275_UpliftsEverySlotTo275()
        {
            // 170 and 180 are the only members inside the widest band the 0.60 floor allows ([165, 275]).
            var family = Family(Member(1, 150), Member(2, 160), Member(3, 170), Member(4, 180));

            var pick = WorldEventRosterSelector.PickWave(family, Top(), Theme(), 0, 0, new Random(7));

            Assert.AreEqual(8, pick.Trash.Count);
            Assert.IsTrue(pick.Trash.All(w => w == 3 || w == 4), "the widened pool is the two members inside [165, 275]");
            Assert.AreEqual(2, pick.Trash.Distinct().Count(), "both pool members are drawn: without the widening every slot is the single nearest member (wcid 4)");
            Assert.IsTrue(pick.TrashUplift.All(u => u == 275), "both sit below the natural low edge 275, so each slot is uplifted to L = 275");
            Assert.AreEqual(pick.Trash.Count, pick.TrashUplift.Count);
        }

        [TestMethod]
        public void InBandMember_IsNotUplifted()
        {
            // Level 275 is exactly the natural low edge: in band, spawned as authored.
            var family = Family(Member(1, 275), Member(2, 275), Member(3, 275), Member(4, 275), Member(5, 275), Member(6, 275));

            var pick = WorldEventRosterSelector.PickWave(family, Top(), Theme(), 0, 0, new Random(7));

            Assert.IsTrue(pick.TrashUplift.All(u => u == 0), "members at or above the low edge are not uplifted");
        }

        [TestMethod]
        public void UpliftLevelFor_IsStrictlyBelowTheNaturalLowEdge()
        {
            Assert.AreEqual(275, WorldEventRosterSelector.UpliftLevelFor(274, 275, 275));
            Assert.AreEqual(0, WorldEventRosterSelector.UpliftLevelFor(275, 275, 275));
            Assert.AreEqual(0, WorldEventRosterSelector.UpliftLevelFor(300, 275, 275));
            Assert.AreEqual(0, WorldEventRosterSelector.UpliftLevelFor(10, 0, 0), "no slot level means no uplift");
        }

        // ---- widening -------------------------------------------------------------------------------------

        [TestMethod]
        public void Widening_StopsAtTheFloor_WhenThePoolNeverFills()
        {
            // One member at every 10 levels from 100: far fewer than 6 inside [165, 275], so the search ends at the floor.
            var members = Enumerable.Range(0, 5).Select(i => Member((uint)(10 + i), 100 + 10 * i)).ToList();

            var band = WorldEventRosterSelector.WidenTrashBand(members, 275, 1.0, 1.5, lowFloor: 0.60, minPool: 6);

            Assert.AreEqual(165, band.Low, "floor(275 * 0.60) - never lower, even though members exist below it");
            Assert.AreEqual(275, band.High, "the high edge never moves and stays capped at 275");
        }

        [TestMethod]
        public void Widening_StopsAtTheFirstBandThatFillsThePool()
        {
            // Six distinct wcids at 255..260. Ladder: 1.00 -> 275, 0.95 -> 261 (still empty), 0.90 -> 247 (all six in).
            var members = Enumerable.Range(0, 6).Select(i => Member((uint)(20 + i), 255 + i)).ToList();

            var band = WorldEventRosterSelector.WidenTrashBand(members, 275, 1.0, 1.5, lowFloor: 0.60, minPool: 6);

            Assert.AreEqual(247, band.Low, "stops at the first ratio that holds six distinct wcids, not at the floor");
        }

        [TestMethod]
        public void Widening_ADeepNaturalPool_ReturnsTheNaturalBandUntouched()
        {
            var members = Enumerable.Range(0, 6).Select(i => Member((uint)(30 + i), 100)).ToList();

            var band = WorldEventRosterSelector.WidenTrashBand(members, 100, 1.0, 1.5, 0.60, 6);

            Assert.AreEqual(WorldEventRosterSelector.TrashBand(100, 1.0, 1.5).Low, band.Low);
            Assert.AreEqual(WorldEventRosterSelector.TrashBand(100, 1.0, 1.5).High, band.High);
        }

        [TestMethod]
        public void Widening_FloorAtOrAboveTheLowRatio_IsANoOp()
        {
            var members = new List<FamilyMember> { Member(40, 150) };

            var band = WorldEventRosterSelector.WidenTrashBand(members, 200, 1.0, 1.5, lowFloor: 1.0, minPool: 6);

            Assert.AreEqual(200, band.Low);
        }

        // ---- flag off -------------------------------------------------------------------------------------

        [TestMethod]
        public void FlagOff_PicksAreIdenticalToTheNonThinCase_AndNothingIsUplifted()
        {
            // A DEEP in-band family: the widening never triggers, so On and Off must draw identically for one seed.
            var deep = Family(Enumerable.Range(0, 8).Select(i => Member((uint)(50 + i), 275)).ToArray());

            WorldEventRosterSelector.UpliftDialSource = () => WorldEventRosterSelector.UpliftDials.Off;
            var off = WorldEventRosterSelector.PickWave(deep, Top(), Theme(), 2, 0, new Random(99));

            WorldEventRosterSelector.UpliftDialSource = () => WorldEventRosterSelector.UpliftDials.Defaults;
            var on = WorldEventRosterSelector.PickWave(deep, Top(), Theme(), 2, 0, new Random(99));

            CollectionAssert.AreEqual(off.Trash.ToList(), on.Trash.ToList());
            CollectionAssert.AreEqual(off.Champions.ToList(), on.Champions.ToList());
            Assert.AreEqual(off.EliteIndex, on.EliteIndex);
            Assert.IsTrue(off.TrashUplift.All(u => u == 0));
        }

        [TestMethod]
        public void FlagOff_ThinPool_FallsBackToTheNearestMemberUnscaled()
        {
            var family = Family(Member(1, 150), Member(2, 160), Member(3, 170), Member(4, 180));

            WorldEventRosterSelector.UpliftDialSource = () => WorldEventRosterSelector.UpliftDials.Off;
            var off = WorldEventRosterSelector.PickWave(family, Top(), Theme(), 0, 0, new Random(7));

            Assert.IsTrue(off.Trash.All(w => w == 4), "pre-uplift rule: the single member nearest the band centre");
            Assert.IsTrue(off.TrashUplift.All(u => u == 0), "off means no uplift level on any slot");
        }

        [TestMethod]
        public void NearestMemberFallback_BelowBand_IsUpliftedToo()
        {
            // Floor 1.0 disables the widening, so the pool is empty at L = 275 and SelectPool falls back to the
            // nearest member (180) - which sits below the band and must still carry an uplift level.
            var family = Family(Member(1, 150), Member(2, 160), Member(3, 170), Member(4, 180));

            WorldEventRosterSelector.UpliftDialSource = () => new WorldEventRosterSelector.UpliftDials(true, 6, 1.0);
            var pick = WorldEventRosterSelector.PickWave(family, Top(), Theme(), 0, 0, new Random(7));

            Assert.IsTrue(pick.Trash.All(w => w == 4));
            Assert.IsTrue(pick.TrashUplift.All(u => u == 275));
        }

        // ---- family champion ------------------------------------------------------------------------------

        [TestMethod]
        public void FamilyChampion_PromotedFromBelowTheBand_CarriesAnUpliftLevel()
        {
            // p90 275: the elite band collapses onto the trash band [275, 275]; no real role-1/2 is there, so the
            // nearest role-0 member (180) is promoted - below band, so it is uplifted to 275.
            var family = Family(Member(1, 150), Member(2, 180));

            var pick = WorldEventRosterSelector.PickChampion(family, Top());

            Assert.AreEqual(2u, pick.Wcid);
            Assert.IsTrue(pick.Synthetic);
            Assert.AreEqual(275, pick.UpliftLevel);
        }

        [TestMethod]
        public void FamilyChampion_FlagOff_HasNoUpliftLevel()
        {
            var family = Family(Member(1, 150), Member(2, 180));

            WorldEventRosterSelector.UpliftDialSource = () => WorldEventRosterSelector.UpliftDials.Off;

            Assert.AreEqual(0, WorldEventRosterSelector.PickChampion(family, Top()).UpliftLevel);
        }

        // ---- the memo -------------------------------------------------------------------------------------

        private static Dictionary<string, SpeciesTableDef> Tables(params (uint Wcid, int Level)[] members)
            => new Dictionary<string, SpeciesTableDef>
            {
                ["t"] = new SpeciesTableDef
                {
                    Id = "t",
                    Members = members.Select(m => new SpeciesMemberDef { Wcid = m.Wcid, Level = m.Level, Role = 0 }).ToList(),
                },
            };

        private static DungeonStatProfile Profile(int level, uint health, uint dmg)
            => new DungeonStatProfile(level, health, new Dictionary<ACE.Entity.Enum.Skill, uint> { [ACE.Entity.Enum.Skill.MeleeDefense] = 200u + (uint)level },
                dmg, dmg * 2);

        [TestMethod]
        public void Memo_ColdAndWarm_ReturnTheSameStandard_AndWarmReadsNothing()
        {
            var tables = Tables((1, 275), (2, 275), (3, 275), (4, 275), (5, 275), (6, 275));
            var reads = 0;

            DungeonStatProfile Resolve(uint wcid)
            {
                reads++;
                return Profile(275, 10000, 300);
            }

            var cold = WorldEventBandMemo.StandardFor(tables, 275, 1.0, 1.5, 0.60, Resolve);
            var coldReads = reads;
            var warm = WorldEventBandMemo.StandardFor(tables, 275, 1.0, 1.5, 0.60, Resolve);

            Assert.IsFalse(cold.IsEmpty);
            Assert.AreEqual(6, coldReads, "one profile read per distinct wcid on a cold call");
            Assert.AreSame(cold, warm);
            Assert.AreEqual(coldReads, reads, "a warm call resolves nothing");
            Assert.AreEqual(300u, warm.MaxBodyDamage);

            var health = WorldEventBandMemo.MedianHealthFor(tables, 275, 1.0, 1.5, Resolve);
            Assert.AreEqual(10000u, health);
            Assert.AreEqual(coldReads, reads, "the health median reuses the profile map");
        }

        [TestMethod]
        public void Memo_DifferentLevelOrFloor_IsADifferentEntry()
        {
            var tables = Tables((1, 275), (2, 275), (3, 275), (4, 275), (5, 275), (6, 275));

            DungeonStatProfile Resolve(uint w) => Profile(275, 10000, 300);

            var a = WorldEventBandMemo.StandardFor(tables, 275, 1.0, 1.5, 0.60, Resolve);
            var b = WorldEventBandMemo.StandardFor(tables, 275, 1.0, 1.5, 0.80, Resolve);
            var c = WorldEventBandMemo.StandardFor(tables, 250, 1.0, 1.5, 0.60, Resolve);

            Assert.AreNotSame(a, b);
            Assert.AreNotSame(a, c);
            Assert.AreEqual(3, WorldEventBandMemo.Counts.Standards);
        }

        [TestMethod]
        public void Memo_Clear_ForcesARecompute()
        {
            var tables = Tables((1, 275), (2, 275), (3, 275), (4, 275), (5, 275), (6, 275));
            var reads = 0;

            DungeonStatProfile Resolve(uint w)
            {
                reads++;
                return Profile(275, 10000, 300);
            }

            var first = WorldEventBandMemo.StandardFor(tables, 275, 1.0, 1.5, 0.60, Resolve);
            var readsBefore = reads;

            WorldEventBandMemo.Clear();

            Assert.AreEqual((0, 0, 0), WorldEventBandMemo.Counts);

            var second = WorldEventBandMemo.StandardFor(tables, 275, 1.0, 1.5, 0.60, Resolve);

            Assert.AreNotSame(first, second, "a cleared memo rebuilds the standard");
            Assert.AreEqual(readsBefore * 2, reads, "and re-reads every profile");
        }

        [TestMethod]
        public void Memo_IsBounded()
        {
            var tables = Tables((1, 275), (2, 275), (3, 275), (4, 275), (5, 275), (6, 275));

            DungeonStatProfile Resolve(uint w) => Profile(275, 10000, 300);

            for (var level = 1; level <= WorldEventBandMemo.MaxEntries + 20; level++)
                WorldEventBandMemo.StandardFor(tables, level, 1.0, 1.5, 0.60, Resolve);

            Assert.IsTrue(WorldEventBandMemo.Counts.Standards <= WorldEventBandMemo.MaxEntries);
        }

        [TestMethod]
        public void Memo_StandardIsMeasuredOverTheCappedBand()
        {
            // L = 275 caps the sample band at [275, 275]. A member at level 300 would be in Threads' unclamped
            // [275, 413] but must NOT enter WE's standard: with only the level-300 creatures in range and the
            // floor at 1.0 (no widening) the sample is empty.
            var tables = Tables((1, 300), (2, 300), (3, 300), (4, 300), (5, 300), (6, 300));

            DungeonStatProfile Resolve(uint w) => Profile(300, 99999, 999);

            var standard = WorldEventBandMemo.StandardFor(tables, 275, 1.0, 1.5, 1.0, Resolve);

            Assert.IsTrue(standard.IsEmpty, "a level-300 member lies above the 275 cap and must not feed the standard");
        }

        [TestMethod]
        public void Memo_DifferentHighEdgeCaps_AreDistinctEntries_WithDistinctResults()
        {
            // Six level-300 members: inside the UNCAPPED band [275, 413] for L = 275, outside the 275-capped one.
            var tables = Tables((1, 300), (2, 300), (3, 300), (4, 300), (5, 300), (6, 300));

            DungeonStatProfile Resolve(uint w) => Profile(300, 99999, 999);

            var capped = WorldEventBandMemo.StandardFor(tables, 275, 1.0, 1.5, 1.0, Resolve, maxHighEdge: 275);
            var uncapped = WorldEventBandMemo.StandardFor(tables, 275, 1.0, 1.5, 1.0, Resolve, maxHighEdge: int.MaxValue);

            Assert.IsTrue(capped.IsEmpty);
            Assert.IsFalse(uncapped.IsEmpty, "a cap must not be served from an uncapped entry, or vice versa");
            Assert.AreEqual(2, WorldEventBandMemo.Counts.Standards);

            var cappedHealth = WorldEventBandMemo.MedianHealthFor(tables, 275, 1.0, 1.5, Resolve, maxHighEdge: 275);
            var uncappedHealth = WorldEventBandMemo.MedianHealthFor(tables, 275, 1.0, 1.5, Resolve, maxHighEdge: int.MaxValue);

            Assert.AreEqual(0u, cappedHealth);
            Assert.AreEqual(99999u, uncappedHealth);
            Assert.AreEqual(2, WorldEventBandMemo.Counts.Health);
        }

        [TestMethod]
        public void SlotLevelFor_StampsNoSlotLevel_OnAPickThatRecordedNone()
        {
            var bare = new WavePick(new List<uint> { 1, 2 }, null);
            Assert.IsFalse(bare.HasSlotLevels);
            Assert.AreEqual(WorldEventSpawner.NoSlotLevel, WorldEventSpawner.SlotLevelFor(bare, bare.TrashSlotLevelAt(0)));

            var family = Family(Member(1, 275), Member(2, 275), Member(3, 275), Member(4, 275), Member(5, 275), Member(6, 275));
            var real = WorldEventRosterSelector.PickWave(family, Top(), Theme(), 0, 0, new Random(3));

            Assert.IsTrue(real.HasSlotLevels);
            Assert.AreEqual(275, WorldEventSpawner.SlotLevelFor(real, real.TrashSlotLevelAt(0)));
        }

        [TestMethod]
        public void NoSpawnBossOverload_PassesALiteralZeroSlotLevel()
        {
            var lines = File.ReadAllLines(Path.Combine(FindSourceRoot(), "WorldEvents", "WorldEventSpawner.cs"));
            var calls = 0;

            foreach (var raw in lines)
            {
                var code = raw.Trim();

                // The chain's forwarding calls: SpawnBoss(evt, wcid, anchor, ..., <slotLevel>);
                if (!code.StartsWith("SpawnBoss(evt, wcid, anchor") || !code.EndsWith(");"))
                    continue;

                calls++;

                Assert.IsFalse(code.EndsWith(", 0);") && code.Count(c => c == ',') >= 6,
                    $"a forwarding call passes a literal 0 slot level (it must pass NoSlotLevel): {code}");
            }

            Assert.IsTrue(calls >= 4, "the overload chain moved; update this pin");
            Assert.IsTrue(string.Join("\n", lines).Contains("SpawnBoss(evt, wcid, anchor, healthMult, synthetic, upliftLevel, NoSlotLevel);"));
        }
        [TestMethod]
        public void WcidListSpawnWave_IsDocumentedAsSkippingTheGuard_AndTheGuardHonoursIt()
        {
            var text = File.ReadAllText(Path.Combine(FindSourceRoot(), "WorldEvents", "WorldEventSpawner.cs"));

            Assert.IsTrue(text.Contains("if (creature == null || slotLevel < 0)"), "ApplyCombatGuard must skip an entry with no slot level");
            Assert.IsTrue(text.Contains("the combat-trait guard is deliberately SKIPPED"), "the overload's remarks must say so");
        }

        // ---- health floor ---------------------------------------------------------------------------------

        [TestMethod]
        public void HealthFloor_IsUpwardOnly()
        {
            Assert.AreEqual(5000u, WorldEventSpawner.HealthFloorTarget(2000, 5000));
            Assert.AreEqual(0u, WorldEventSpawner.HealthFloorTarget(8000, 5000), "already above the median: leave it");
            Assert.AreEqual(0u, WorldEventSpawner.HealthFloorTarget(2000, 0), "no data: leave it");
        }

        // ---- source pins ----------------------------------------------------------------------------------

        [TestMethod]
        public void Spawn_AppliesTheUplift_AfterHostility_AndBeforeTheHealthMultiplier()
        {
            var lines = File.ReadAllLines(Path.Combine(FindSourceRoot(), "WorldEvents", "WorldEventSpawner.cs"));

            int Find(string code) => Array.FindIndex(lines, l => l.Trim() == code);

            var hostility = Find("SpawnedCreatureHostility.MakeHostileToPlayers(creature);");
            var uplift = Find("ApplyBandUplift(evt, creature, entry.UpliftLevel, kind);");
            var capture = Find("candidate.AuthoredStartingValue = creature.Health.StartingValue;");
            var multiplier = Find("ApplyHealthMultiplier(evt, creature, healthMult);");

            Assert.IsTrue(hostility > 0 && uplift > 0 && capture > 0 && multiplier > 0, "all four anchor lines must exist exactly once");
            Assert.IsTrue(hostility < uplift, "uplift after the hostility sanitize");
            Assert.IsTrue(uplift < capture && uplift < multiplier, "uplift (and its health floor) before the authored-health capture and the multiplier");
        }

        [TestMethod]
        public void EveryThreadPlanCacheInvalidate_InTheWorldEventAndDeveloperPaths_IsPairedWithAMemoClear()
        {
            var root = FindSourceRoot();

            foreach (var rel in new[] { Path.Combine("WorldEvents", "WorldEventManager.cs"), Path.Combine("Command", "Handlers", "DeveloperContentCommands.cs") })
            {
                var lines = File.ReadAllLines(Path.Combine(root, rel));
                var invalidates = 0;

                for (var i = 0; i < lines.Length; i++)
                {
                    if (!lines[i].Contains("ThreadPlanCache.Invalidate();") || lines[i].TrimStart().StartsWith("//"))
                        continue;

                    invalidates++;

                    var window = string.Join("\n", lines.Skip(i).Take(6));
                    Assert.IsTrue(window.Contains("WorldEventBandMemo.Clear();"), $"{rel}:{i + 1} invalidates the Threads cache without clearing the World Events memo");
                }

                Assert.IsTrue(invalidates > 0, $"{rel} lost its Invalidate call; update this pin");
            }
        }

        [TestMethod]
        public void NamedBoss_IsNeverHandedAnUpliftLevel()
        {
            var lines = File.ReadAllLines(Path.Combine(FindSourceRoot(), "WorldEvents", "WorldEvent.cs"));
            var text = string.Join("\n", lines);

            // The family champion's level is the only one forwarded; the Named branch leaves the local at 0.
            Assert.IsTrue(text.Contains("var championUplift = 0;"));
            Assert.IsTrue(text.Contains("championUplift = pick.UpliftLevel;"));
            Assert.IsTrue(text.Contains("Spawner.SpawnBoss(this, wcid, anchor, healthMult, synthetic, championUplift, championSlotLevel);"));
            Assert.AreEqual(1, System.Text.RegularExpressions.Regex.Matches(text, "championUplift = pick\\.UpliftLevel;").Count, "assigned only in the family-champion branch");
        }

        private static string FindSourceRoot()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);

            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "ACE.Server", "WorldEvents", "WorldEventBandMemo.cs")))
                dir = dir.Parent;

            Assert.IsNotNull(dir, $"Could not find ACE.Server/WorldEvents/WorldEventBandMemo.cs by walking up from {AppContext.BaseDirectory}");

            return Path.Combine(dir.FullName, "ACE.Server");
        }
    }
}
