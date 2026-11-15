using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using ACE.Server.Frays;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ACE.Server.Tests
{
    /// <summary>The Snarl Monument and its ring of finders' stones.</summary>
    [TestClass]
    public class FraysMonumentTests
    {
        private static FraysTrophyWall.Entry E(string finder, string family = "Olthoi", int day = 9) =>
            new FraysTrophyWall.Entry { Finder = finder, Family = family, Date = new DateTime(2026, 11, day) };

        [TestMethod]
        public void StoneName_IsFinderFamilyAndDate()
        {
            Assert.AreEqual("Alda, Olthoi Snarl, 9 Nov", FraysMonument.StoneName(E("Alda")));
            Assert.AreEqual("Brom, Fray Snarl, 1 Nov", FraysMonument.StoneName(E("Brom", "", 1)));
            Assert.AreEqual("Unclaimed Snarl Stone", FraysMonument.StoneName(null));
            StringAssert.Contains(FraysMonument.StoneText(E("Alda")), "Finder: Alda");
            StringAssert.Contains(FraysMonument.StoneText(E("Alda")), "9 November 2026");
        }

        [TestMethod]
        public void NewTrophy_TakesTheOldestStone_OthersStay()
        {
            // 13 trophies on 12 stones: trophy 13 takes trophy 1's stone.
            var names = Enumerable.Range(1, 13).Select(i => E("P" + i)).Reverse().ToList();   // newest first
            var before = FraysMonument.Slots(names.Skip(1).ToList(), 12, 12);
            var after = FraysMonument.Slots(names, 13, 12);

            var changed = Enumerable.Range(0, 12).Where(i => before[i]?.Finder != after[i]?.Finder).ToList();
            Assert.AreEqual(1, changed.Count, "only one stone changes");
            Assert.AreEqual("P1", before[changed[0]].Finder, "it was the oldest one shown");
            Assert.AreEqual("P13", after[changed[0]].Finder);

            // Past the wall's kept list the total still counts on, so stones keep their places.
            var kept = Enumerable.Range(1, FraysTrophyWall.Kept + 1).Select(i => E("Q" + i)).Reverse().ToList();
            var a = FraysMonument.Slots(kept.Skip(1).Take(FraysTrophyWall.Kept).ToList(), FraysTrophyWall.Kept, 12);
            var b = FraysMonument.Slots(kept.Take(FraysTrophyWall.Kept).ToList(), FraysTrophyWall.Kept + 1, 12);
            Assert.AreEqual(1, Enumerable.Range(0, 12).Count(i => a[i]?.Finder != b[i]?.Finder));

            // Fewer trophies than stones: the rest are unclaimed.
            Assert.AreEqual(9, FraysMonument.Slots(names.Take(3).ToList(), 3, 12).Count(s => s == null));
        }

        [TestMethod]
        public void WallState_KeepsTheRunningTotal()
        {
            var text = FraysTrophyWall.Format(new[] { E("B"), E("A") }, 57);
            Assert.AreEqual(57, FraysTrophyWall.ParseTotal(text));
            Assert.AreEqual(2, FraysTrophyWall.Parse(text).Count, "the total is not an entry");
            Assert.AreEqual("B", FraysTrophyWall.Parse(text)[0].Finder);
            Assert.AreEqual(2, FraysTrophyWall.ParseTotal(FraysTrophyWall.Format(new[] { E("B"), E("A") })), "older saves count their entries");
            Assert.AreEqual(0, FraysTrophyWall.ParseTotal(""));
        }

        [TestMethod]
        public void Ring_GoesRoundTheStatue()
        {
            var ring = FraysMonument.Ring(new Vector2(100, 100), 4, 12);
            Assert.AreEqual(12, ring.Count);
            Assert.IsTrue(ring.All(r => Math.Abs(Vector2.Distance(r.Pos, new Vector2(100, 100)) - 4) < 1e-3));
            Assert.AreEqual(100, ring[0].Pos.X, 1e-3);
            Assert.AreEqual(104, ring[0].Pos.Y, 1e-3, "the first stone is north");
            Assert.AreEqual(12, ring.Select(r => (Math.Round(r.Pos.X, 2), Math.Round(r.Pos.Y, 2))).Distinct().Count());
        }

        [TestMethod]
        public void MonumentText_ListsTheLatestFinders_InAscii()
        {
            var text = FraysMonument.MonumentText(new[] { E("Alda"), E("Brom", "Lugian", 8) }, 12);
            StringAssert.Contains(text, "9 Nov 2026: Alda, Olthoi Snarl");
            StringAssert.Contains(text, "Brom, Lugian Snarl");
            Assert.IsTrue(text.All(c => c < 128));
            Assert.IsTrue(FraysMonument.MonumentText(new FraysTrophyWall.Entry[0], 12).All(c => c < 128));
        }

        // ---- the Champion's stone ----

        [TestMethod]
        public void Champion_MostTrophies_TieGoesToWhoReachedItFirst()
        {
            var counts = new List<FraysTrophyWall.Tally>();
            var t0 = new DateTime(2026, 11, 10, 12, 0, 0, DateTimeKind.Utc);
            FraysTrophyWall.Counted(counts, "Ann", t0);
            FraysTrophyWall.Counted(counts, "Bob", t0.AddHours(1));
            Assert.AreEqual("Ann", FraysTrophyWall.Champion(counts).Name, "1 each: Ann reached 1 first");

            FraysTrophyWall.Counted(counts, "Bob", t0.AddHours(2));
            Assert.AreEqual("Bob", FraysTrophyWall.Champion(counts).Name, "Bob passes Ann");

            FraysTrophyWall.Counted(counts, "ann", t0.AddHours(3));
            Assert.AreEqual(2, counts.Count, "names match without case");
            Assert.AreEqual("Bob", FraysTrophyWall.Champion(counts).Name, "Ann ties at 2, Bob got there first and keeps it");

            FraysTrophyWall.Counted(counts, "Ann", t0.AddHours(4));
            Assert.AreEqual("Ann", FraysTrophyWall.Champion(counts).Name);
            Assert.AreEqual(3, FraysTrophyWall.Champion(counts).Count);
            Assert.IsNull(FraysTrophyWall.Champion(new List<FraysTrophyWall.Tally>()));
        }

        [TestMethod]
        public void Counts_RoundTrip_AndSeedFromTheWall()
        {
            var counts = FraysTrophyWall.Counted(new List<FraysTrophyWall.Tally>(), "Ann", new DateTime(2026, 11, 10, 1, 2, 3, DateTimeKind.Utc));
            var back = FraysTrophyWall.ParseCounts(FraysTrophyWall.FormatCounts(counts));
            Assert.AreEqual(1, back.Count);
            Assert.AreEqual("Ann", back[0].Name);
            Assert.AreEqual(counts[0].Reached, back[0].Reached);

            // wall newest first: Bob, Ann, Bob, Ann (same day) - 2 each, Ann reached 2 first
            var wall = new List<FraysTrophyWall.Entry> { E("Bob"), E("Ann"), E("Bob"), E("Ann") };
            var seeded = FraysTrophyWall.SeedCounts(wall);
            Assert.AreEqual(2, seeded.Single(t => t.Name == "Bob").Count);
            Assert.AreEqual("Ann", FraysTrophyWall.Champion(seeded).Name);
            Assert.AreEqual(0, FraysTrophyWall.SeedCounts(new List<FraysTrophyWall.Entry>()).Count);
        }

        [TestMethod]
        public void ChampionStone_InFrontOfTheStatue_OthersRoundTheRest()
        {
            var (stones, champ) = FraysMonument.Layout(new Vector2(100, 100), 4, 12, true, 90, 0);
            Assert.AreEqual(12, stones.Count);
            Assert.IsNotNull(champ);
            Assert.AreEqual(96, champ.Value.Pos.X, 1e-3, "facing 90 is west: in front is x - 4");
            Assert.AreEqual(100, champ.Value.Pos.Y, 1e-3);
            Assert.IsTrue(stones.All(s => Vector2.Distance(s.Pos, champ.Value.Pos) > 1.5f), "no finder's stone on the champion's spot");

            var (plain, none) = FraysMonument.Layout(new Vector2(100, 100), 4, 12, false, 90, 0);
            Assert.IsNull(none);
            Assert.AreEqual(12, plain.Count);
        }

        [TestMethod]
        public void ChampionText_SaysChampionAndCount_InAscii()
        {
            var t = new FraysTrophyWall.Tally { Name = "Alda", Count = 7, Reached = new DateTime(2026, 11, 9) };
            Assert.AreEqual("Champion's Stone: Alda, 7 Snarl Trophies", FraysMonument.ChampionName(t));
            Assert.AreEqual("Champion's Stone: Alda, 1 Snarl Trophy", FraysMonument.ChampionName(new FraysTrophyWall.Tally { Name = "Alda", Count = 1 }));
            StringAssert.Contains(FraysMonument.ChampionText(t), "Champion: Alda");
            StringAssert.Contains(FraysMonument.ChampionText(t), "Snarl Trophies found: 7");
            var all = FraysMonument.ChampionName(t) + FraysMonument.ChampionText(t) + FraysMonument.ChampionName(null) + FraysMonument.ChampionText(null);
            Assert.IsTrue(all.All(c => c < 128));
        }
    }
}
