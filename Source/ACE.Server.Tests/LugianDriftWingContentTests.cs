using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ACE.Server.Tests
{
    /// <summary>
    /// Content pins for the Drift north wing of the level 300/400 lugian dungeons: the two kill-tally
    /// clerks (1008470 Citadel, 1008472 Excavations). One data row per dungeon over shared assertions,
    /// so a divergence between the two dungeons fails a test. Pure file + regex parsing of the
    /// committed SQL; no database or dat files.
    /// </summary>
    [TestClass]
    public class LugianDriftWingContentTests
    {
        private const int AwardNoShareXp = 62;
        private const int AwardLuminance = 113;
        private const int Give = 3;
        private const int StampQuest = 22;
        private const int EraseQuest = 31;
        private const int InqQuestSolves = 30;
        private const int SetQuestCompletions = 70;
        private const int LevelProportionalXp = 49;
        private const int ObsidianBag = 1001752;
        private const int TradeNotes = 20630;

        private const int CategoryQuestSuccess = 12;
        private const int CategoryQuestFailure = 13;
        private const int IntMax = 2147483647;

        private sealed class Clerk
        {
            public string Dungeon;
            public int Wcid;
            public long Xp;
            public long Lum;
            public override string ToString() => Dungeon;
        }

        private static readonly Clerk[] Clerks =
        {
            new Clerk { Dungeon = "Citadel", Wcid = 1008470, Xp = 1400000000L, Lum = 350000L },
            new Clerk { Dungeon = "Excavations", Wcid = 1008472, Xp = 2200000000L, Lum = 550000L },
        };

        // Arch name, tally threshold.
        private static readonly (string Arch, int Done)[] Arches =
        {
            ("Brute", 500), ("Hurler", 250), ("Boss", 1),
        };

        private sealed class Action
        {
            public int Order;
            public int Type;
            public string Message;
            public long? Min;
            public long? Max;
            public long? Amount;
            public long? Amount64;
            public long? WeenieClassId;
            public long? StackSize;
            public double Delay;
        }

        private sealed class Emote
        {
            public int Category;
            public string Quest;
            public long? ItemWcid;
            public List<Action> Actions = new List<Action>();
        }

        [TestMethod]
        public void Clerk_DoneThresholdsAre500_250_125_1()
        {
            foreach (var clerk in Clerks)
            {
                var emotes = LoadEmotes(clerk.Wcid);

                foreach (var (arch, done) in Arches)
                {
                    var q = $"Lugian{clerk.Dungeon}{arch}";
                    var solves = Single(emotes, CategoryQuestFailure, q).Actions.Single();

                    Assert.AreEqual(InqQuestSolves, solves.Type, $"{clerk}/{arch}: not an InqQuestSolves.");
                    Assert.AreEqual(q + "Kills@done", solves.Message, $"{clerk}/{arch}");
                    Assert.AreEqual((long)done, solves.Min, $"{clerk}/{arch}: @done min.");
                    Assert.AreEqual((long)IntMax, solves.Max, $"{clerk}/{arch}: @done max.");
                }
            }
        }

        [TestMethod]
        public void Clerk_MidRangesStopOneShortOfDone()
        {
            foreach (var clerk in Clerks)
            {
                var emotes = LoadEmotes(clerk.Wcid);

                foreach (var (arch, done) in Arches)
                {
                    var q = $"Lugian{clerk.Dungeon}{arch}";
                    var solves = Single(emotes, CategoryQuestFailure, q + "Kills@done").Actions.Single();

                    Assert.AreEqual(InqQuestSolves, solves.Type, $"{clerk}/{arch}");
                    Assert.AreEqual(q + "Kills@mid", solves.Message, $"{clerk}/{arch}");
                    Assert.AreEqual(1L, solves.Min, $"{clerk}/{arch}: @mid min.");
                    Assert.AreEqual((long)(done - 1), solves.Max, $"{clerk}/{arch}: @mid max stops one short of done (boss 0).");
                }
            }
        }

        [TestMethod]
        public void Clerk_PaysXpLuminanceAndOneBagPerTally()
        {
            foreach (var clerk in Clerks)
            {
                var emotes = LoadEmotes(clerk.Wcid);

                foreach (var (arch, _) in Arches)
                {
                    var done = Single(emotes, CategoryTestSuccess, $"Pay-{arch}");
                    var where = $"{clerk}/{arch}";

                    var xp = done.Actions.Single(a => a.Type == AwardNoShareXp);
                    Assert.AreEqual(clerk.Xp, xp.Amount64, $"{where}: AwardNoShareXP amount_64.");

                    var lum = done.Actions.Single(a => a.Type == AwardLuminance);
                    Assert.AreEqual(clerk.Lum, lum.Amount64, $"{where}: AwardLuminance amount_64.");

                    var give = done.Actions.Single(a => a.Type == Give);
                    Assert.AreEqual((long)ObsidianBag, give.WeenieClassId, $"{where}: Give wcid.");
                    Assert.AreEqual(1L, give.StackSize, $"{where}: Give stack.");

                    Assert.IsFalse(done.Actions.Any(a => a.Type == LevelProportionalXp), $"{where}: AwardLevelProportionalXP (49) must be gone.");
                    Assert.IsFalse(done.Actions.Any(a => a.Type == Give && a.WeenieClassId == TradeNotes), $"{where}: Trade Notes (20630) must be gone.");
                    Assert.AreEqual(1, done.Actions.Count(a => a.Type == Give), $"{where}: exactly one Give.");
                }
            }
        }

        [TestMethod]
        public void Clerk_PaysOnlyThroughAPackSpaceCheck()
        {
            foreach (var clerk in Clerks)
            {
                var emotes = LoadEmotes(clerk.Wcid);

                // Every Give of the bag sits in a TestSuccess Pay-* set and nowhere else.
                var holders = emotes.Where(e => e.Actions.Any(a => a.Type == Give && a.WeenieClassId == ObsidianBag)).ToList();
                Assert.AreEqual(Arches.Length, holders.Count, $"{clerk}: one bag Give per archetype.");
                foreach (var h in holders)
                {
                    Assert.AreEqual(CategoryTestSuccess, h.Category, $"{clerk}: a bag Give outside TestSuccess ({h.Quest}).");
                    StringAssert.StartsWith(h.Quest, "Pay-", $"{clerk}: bag Give in a non Pay-* set.");
                }

                foreach (var (arch, _) in Arches)
                {
                    var q = $"Lugian{clerk.Dungeon}{arch}";

                    // @done only asks for pack space (four tallies can pay in one Use).
                    var gate = Single(emotes, CategoryQuestSuccess, q + "Kills@done").Actions.Single();
                    Assert.AreEqual(InqPackSpace, gate.Type, $"{clerk}/{arch}: @done must be a lone InqPackSpace.");
                    Assert.AreEqual($"Pay-{arch}", gate.Message, $"{clerk}/{arch}: InqPackSpace message.");
                    Assert.AreEqual(4L, gate.Amount, $"{clerk}/{arch}: InqPackSpace amount.");

                    // The failure set tells and does nothing else, so the Kills row stays full.
                    var fail = Single(emotes, CategoryTestFailure, $"Pay-{arch}").Actions.Single();
                    Assert.AreEqual(Tell, fail.Type, $"{clerk}/{arch}: TestFailure must be a lone Tell.");
                }
            }
        }

        [TestMethod]
        public void Clerk_StampsTheCooldownAndErasesTheKillsRow()
        {
            foreach (var clerk in Clerks)
            {
                var emotes = LoadEmotes(clerk.Wcid);

                foreach (var (arch, _) in Arches)
                {
                    var q = $"Lugian{clerk.Dungeon}{arch}";
                    var done = Single(emotes, CategoryTestSuccess, $"Pay-{arch}");
                    var where = $"{clerk}/{arch}";

                    Assert.AreEqual(q, done.Actions.Single(a => a.Type == StampQuest).Message, $"{where}: StampQuest cooldown row.");
                    Assert.AreEqual(q + "Kills", done.Actions.Single(a => a.Type == EraseQuest).Message, $"{where}: EraseQuest Kills row.");

                    var stamp = done.Actions.Single(a => a.Type == StampQuest).Order;
                    var erase = done.Actions.Single(a => a.Type == EraseQuest).Order;
                    var give = done.Actions.Single(a => a.Type == Give).Order;
                    var xp = done.Actions.Single(a => a.Type == AwardNoShareXp).Order;
                    var lum = done.Actions.Single(a => a.Type == AwardLuminance).Order;

                    Assert.IsTrue(xp < lum && lum < stamp && stamp < erase && erase < give, $"{where}: action order must be XP, luminance, stamp, erase, give.");

                    // The cooldown row is told when the player returns inside the 20 h window.
                    Single(emotes, CategoryQuestSuccess, q);
                }
            }
        }

        [TestMethod]
        public void Clerk_ArmsWithZeroCompletionsOnFirstTalk()
        {
            foreach (var clerk in Clerks)
            {
                var emotes = LoadEmotes(clerk.Wcid);

                foreach (var (arch, _) in Arches)
                {
                    var q = $"Lugian{clerk.Dungeon}{arch}";
                    var arm = Single(emotes, CategoryQuestFailure, q + "Kills@mid");
                    var set = arm.Actions.Single(a => a.Type == SetQuestCompletions);

                    Assert.AreEqual(q + "Kills", set.Message, $"{clerk}/{arch}: SetQuestCompletions row.");
                    Assert.AreEqual(0L, set.Amount, $"{clerk}/{arch}: SetQuestCompletions amount.");
                    Assert.IsTrue(arm.Actions.Any(a => a.Type == 10), $"{clerk}/{arch}: arming emote must also Tell.");
                }
            }
        }

        [TestMethod]
        public void Clerk_NoPlaceholderMarkersRemain()
        {
            foreach (var clerk in Clerks)
            {
                var text = File.ReadAllText(FindWeenieFile(clerk.Wcid));

                Assert.IsTrue(text.Contains("/* Name */"), $"{clerk}: Name line missing.");
                Assert.IsFalse(text.Contains("COPY-" + "PLACEHOLDER"), $"{clerk}: placeholder marker left in the file.");
            }
        }

        [TestMethod]
        public void Clerk_GreetingsExplainKillsCountOnlyAfterTheTally()
        {
            foreach (var clerk in Clerks)
            {
                var use = LoadEmotes(clerk.Wcid).Single(e => e.Category == 7);
                var greeting = use.Actions.Single(a => a.Type == 13).Message;

                Assert.IsTrue(greeting.IndexOf("only once", StringComparison.OrdinalIgnoreCase) >= 0 && greeting.IndexOf("tally", StringComparison.OrdinalIgnoreCase) >= 0,
                    $"{clerk}: greeting must say kills count only after the tally is taken.");
                CollectionAssert.AreEqual(
                    Arches.Select(a => $"Lugian{clerk.Dungeon}{a.Arch}").ToList(),
                    use.Actions.Where(a => a.Type == 21).Select(a => a.Message).ToList(),
                    $"{clerk}: Use must InqQuest the three cooldown rows in Brute, Hurler, Boss order.");
            }
        }

        [TestMethod]
        public void Clerk_HasNoChanterBranchOrShamanQuest()
        {
            // Owner ruling 2026-11-23: the Chanters and their kill-tally quest were removed.
            foreach (var clerk in Clerks)
            {
                var text = File.ReadAllText(FindWeenieFile(clerk.Wcid));
                Assert.IsFalse(text.Contains("Shaman"), $"{clerk}: no Shaman quest key or Pay-Shaman branch may remain.");
                Assert.IsFalse(text.Contains("Chanter"), $"{clerk}: no Chanter text may remain.");
                Assert.IsFalse(LoadEmotes(clerk.Wcid).Any(e => e.Quest != null && e.Quest.Contains("Shaman")), $"{clerk}: no emote may be keyed on a Shaman quest.");
            }
        }

        // ---- Quartermasters (Task 13) ----

        private sealed class Quartermaster
        {
            public string Dungeon;
            public int Wcid;
            public int Item;
            public int OtherItem;
            public override string ToString() => Dungeon + " quartermaster";
        }

        private static readonly Quartermaster[] Quartermasters =
        {
            new Quartermaster { Dungeon = "Citadel", Wcid = 1008471, Item = 1008460, OtherItem = 1008461 },
            new Quartermaster { Dungeon = "Excavations", Wcid = 1008474, Item = 1008461, OtherItem = 1008460 },
        };

        private const int CategoryRefuse = 1;
        private const int CategoryGive = 6;
        private const int CategoryUse = 7;
        private const int CategoryTestSuccess = 22;
        private const int CategoryTestFailure = 23;
        private const int TakeItems = 74;
        private const int InqOwnsItems = 76;
        private const int InqPackSpace = 89;
        private const int Tell = 10;

        // The two TestSuccess sets that exchange: the drag path and the use path.
        private static IEnumerable<string> ExchangeKeys(Quartermaster q) => new[] { $"OwnsItem-{q.Item}", $"UsePack-{q.Item}" };

        [TestMethod]
        public void Quartermaster_DispatchesOnRefuseNeverGive()
        {
            foreach (var q in Quartermasters)
            {
                var emotes = LoadEmotes(q.Wcid);

                Assert.IsFalse(emotes.Any(e => e.Category == CategoryGive), $"{q}: no Give-category (6) emote may exist.");

                var own = emotes.Single(e => e.Category == CategoryRefuse && e.ItemWcid == q.Item);
                Assert.AreEqual(12, own.Actions[0].Type, $"{q}: own-item Refuse must start with TurnToTarget.");
                var inq = own.Actions[1];
                Assert.AreEqual(InqPackSpace, inq.Type, $"{q}");
                Assert.AreEqual($"DragPack-{q.Item}", inq.Message, $"{q}");
            }
        }

        [TestMethod]
        public void Quartermaster_ProvesTenThenTakesTenWithNothingYieldingBetween()
        {
            foreach (var q in Quartermasters)
            {
                var emotes = LoadEmotes(q.Wcid);

                var drag = Single(emotes, CategoryTestSuccess, $"DragPack-{q.Item}").Actions.Single();
                Assert.AreEqual(InqOwnsItems, drag.Type, $"{q}");
                Assert.AreEqual($"OwnsItem-{q.Item}", drag.Message, $"{q}");

                var use = Single(emotes, CategoryUse, null);
                var has = use.Actions.Single(a => a.Type == InqOwnsItems);
                Assert.AreEqual($"UseHas-{q.Item}", has.Message, $"{q}");
                Assert.AreEqual(10L, has.StackSize, $"{q}: UseHas count.");
                Assert.AreEqual($"UsePack-{q.Item}", Single(emotes, CategoryTestSuccess, $"UseHas-{q.Item}").Actions.Single().Message, $"{q}");

                foreach (var key in ExchangeKeys(q))
                {
                    var set = Single(emotes, CategoryTestSuccess, key).Actions;
                    var firstNonTell = set.First(a => a.Type != Tell);

                    Assert.AreEqual(TakeItems, firstNonTell.Type, $"{q}/{key}: first non-Tell action must be TakeItems.");
                    Assert.AreEqual((long)q.Item, firstNonTell.WeenieClassId, $"{q}/{key}: TakeItems wcid.");
                    Assert.AreEqual(10L, firstNonTell.StackSize, $"{q}/{key}: TakeItems stack.");
                }

                Assert.AreEqual(10L, Single(emotes, CategoryTestSuccess, $"DragPack-{q.Item}").Actions.Single().StackSize, $"{q}: OwnsItem count.");

                // A non-zero delay yields the emote chain, so nothing in the exchange sets may carry one.
                foreach (var e in emotes.Where(e => e.Category == CategoryRefuse || e.Category == CategoryUse || e.Category == CategoryTestSuccess))
                    foreach (var a in e.Actions)
                        Assert.AreEqual(0.0, a.Delay, $"{q}/{e.Category}/{e.Quest}: action {a.Order} (type {a.Type}) must have delay 0.");
            }
        }

        [TestMethod]
        public void Quartermaster_PaysOneFullObsidianBag()
        {
            foreach (var q in Quartermasters)
            {
                var emotes = LoadEmotes(q.Wcid);

                foreach (var key in ExchangeKeys(q))
                {
                    var set = Single(emotes, CategoryTestSuccess, key).Actions;
                    var give = set.Single(a => a.Type == Give);

                    Assert.AreEqual((long)ObsidianBag, give.WeenieClassId, $"{q}/{key}: Give wcid.");
                    Assert.AreEqual(1L, give.StackSize, $"{q}/{key}: Give stack.");
                    Assert.IsTrue(set.First(a => a.Type == TakeItems).Order < give.Order, $"{q}/{key}: TakeItems must precede Give.");
                }
            }
        }

        [TestMethod]
        public void Quartermaster_RefusesTheOtherDungeonsItemWithoutTakingIt()
        {
            foreach (var q in Quartermasters)
            {
                var emotes = LoadEmotes(q.Wcid);
                var other = emotes.Single(e => e.Category == CategoryRefuse && e.ItemWcid == q.OtherItem);

                Assert.IsTrue(other.Actions.Count > 0 && other.Actions.All(a => a.Type == Tell), $"{q}: refusing the other dungeon's item must be Tell only.");
                Assert.AreEqual(2, emotes.Count(e => e.Category == CategoryRefuse), $"{q}: exactly two Refuse sets (own, other).");
            }
        }

        [TestMethod]
        public void Quartermaster_FailureBranchesOnlyTell()
        {
            foreach (var q in Quartermasters)
            {
                foreach (var e in LoadEmotes(q.Wcid).Where(e => e.Category == CategoryTestFailure))
                    Assert.IsTrue(e.Actions.All(a => a.Type == Tell), $"{q}/{e.Quest}: TestFailure may only Tell.");
            }
        }

        [TestMethod]
        public void Quartermaster_NoPlaceholderMarkersRemain()
        {
            foreach (var q in Quartermasters)
            {
                var text = File.ReadAllText(FindWeenieFile(q.Wcid));

                Assert.IsTrue(text.Contains("/* Name */"), $"{q}: Name line missing.");
                Assert.IsFalse(text.Contains("COPY-" + "PLACEHOLDER"), $"{q}: placeholder marker left in the file.");
            }
        }

        // ---- Portals, wing placements, security split (Task 14) ----

        private const long WingBandLow = 0x70007E87;
        private const long WingBandHigh = 0x70007E96;
        private const long BayBandLow = 0x70007E97;
        private const long BayBandHigh = 0x70007EAF;

        private sealed class PortalRow
        {
            public string Dungeon;
            public int Wcid;
            public int MinLevel;
            public long Cell;
            public double X, Y, Z;
            public int ClerkWcid, QuartermasterWcid;
            public long NpcCell;
            public override string ToString() => Dungeon + " portal";
        }

        private static readonly PortalRow[] Portals =
        {
            new PortalRow { Dungeon = "Citadel", Wcid = 1008466, MinLevel = 250, Cell = 0x02F00152, X = 140, Y = -130, Z = 0, ClerkWcid = 1008470, QuartermasterWcid = 1008471, NpcCell = 0x00070131 },
            new PortalRow { Dungeon = "Excavations", Wcid = 1008467, MinLevel = 350, Cell = 0x02E70285, X = 10, Y = -550, Z = 18, ClerkWcid = 1008472, QuartermasterWcid = 1008474, NpcCell = 0x00070130 },
        };

        private sealed class Placement
        {
            public long Guid;
            public int Wcid;
            public long Cell;
            public double X, Y, Z;
        }

        private static string FindRealmFile(string name)
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);

            while (dir != null)
            {
                var f = Path.Combine(dir.FullName, "Content", "realms", name);
                if (File.Exists(f)) return f;
                dir = dir.Parent;
            }

            Assert.Fail($"Could not locate Content/realms/{name} by walking up from {AppContext.BaseDirectory}.");
            return null;
        }

        private static List<Placement> LoadPlacements(string realmFile)
        {
            var text = File.ReadAllText(FindRealmFile(realmFile));
            // Strip comments so rows quoted inside prose never count.
            var noBlock = Regex.Replace(text, @"/\*.*?\*/", "", RegexOptions.Singleline);
            var rows = new List<Placement>();

            foreach (Match m in Regex.Matches(noBlock, @"^\s*\(1,\s*(0x[0-9A-Fa-f]+),\s*(\d+),\s*(0x[0-9A-Fa-f]+),\s*(-?[\d.]+),\s*(-?[\d.]+),\s*(-?[\d.]+),", RegexOptions.Multiline))
            {
                rows.Add(new Placement
                {
                    Guid = Convert.ToInt64(m.Groups[1].Value, 16),
                    Wcid = int.Parse(m.Groups[2].Value),
                    Cell = Convert.ToInt64(m.Groups[3].Value, 16),
                    X = double.Parse(m.Groups[4].Value, System.Globalization.CultureInfo.InvariantCulture),
                    Y = double.Parse(m.Groups[5].Value, System.Globalization.CultureInfo.InvariantCulture),
                    Z = double.Parse(m.Groups[6].Value, System.Globalization.CultureInfo.InvariantCulture),
                });
            }

            return rows;
        }

        [TestMethod]
        public void Portals_GateAtLevel250And350_IntoRealmOne_AtTheDropPoints()
        {
            foreach (var p in Portals)
            {
                var text = File.ReadAllText(FindWeenieFile(p.Wcid));

                Assert.AreEqual(p.MinLevel, int.Parse(Regex.Match(text, $@"\({p.Wcid},\s+86,\s+(\d+)\)").Groups[1].Value), $"{p}: MinLevel (int 86).");
                Assert.AreEqual(1, int.Parse(Regex.Match(text, $@"\({p.Wcid},\s+9022,\s+(\d+)\)").Groups[1].Value), $"{p}: PortalRealm (int 9022).");
                Assert.AreEqual(65536, int.Parse(Regex.Match(text, $@"\({p.Wcid},\s+1,\s+(\d+)\)\s*/\* ItemType").Groups[1].Value), $"{p}: ItemType Portal.");

                var d = Regex.Match(text, $@"VALUES \({p.Wcid}, 2 /\* Destination \*/, (0x[0-9A-Fa-f]+), (-?[\d.]+), (-?[\d.]+), (-?[\d.]+),");
                Assert.IsTrue(d.Success, $"{p}: Destination position row missing.");
                Assert.AreEqual(p.Cell, Convert.ToInt64(d.Groups[1].Value, 16), $"{p}: destination cell.");
                Assert.AreEqual(p.X, double.Parse(d.Groups[2].Value, System.Globalization.CultureInfo.InvariantCulture), $"{p}: destination x.");
                Assert.AreEqual(p.Y, double.Parse(d.Groups[3].Value, System.Globalization.CultureInfo.InvariantCulture), $"{p}: destination y.");
                Assert.AreEqual(p.Z, double.Parse(d.Groups[4].Value, System.Globalization.CultureInfo.InvariantCulture), $"{p}: destination z.");

                StringAssert.Contains(text, "/* Name */", $"{p}: Name line.");
                Assert.IsFalse(text.Contains("COPY-" + "PLACEHOLDER"), $"{p}: placeholder marker left in the file.");
            }
        }

        [TestMethod]
        public void Wing_PlacesSixteenRowsInsideItsGuidBand()
        {
            var rows = LoadPlacements("drift_lugian_wing.sql");

            Assert.AreEqual(16, rows.Count, "wing row count.");
            Assert.AreEqual(16, rows.Select(r => r.Guid).Distinct().Count(), "wing guids must be distinct.");
            Assert.IsTrue(rows.All(r => r.Guid >= WingBandLow && r.Guid <= WingBandHigh), "every wing guid must sit in 0x70007E87-0x70007E96.");

            foreach (var p in Portals)
            {
                Assert.AreEqual(1, rows.Count(r => r.Wcid == p.Wcid), $"{p}: placed once.");
                Assert.AreEqual(1, rows.Count(r => r.Wcid == p.ClerkWcid && r.Cell == p.NpcCell), $"{p}: clerk in hall cell {p.NpcCell:X8}.");
                Assert.AreEqual(1, rows.Count(r => r.Wcid == p.QuartermasterWcid && r.Cell == p.NpcCell), $"{p}: quartermaster in {p.NpcCell:X8}.");
            }

            Assert.AreEqual(2, rows.Count(r => r.Wcid == 1001076), "two portal frames.");
            Assert.AreEqual(0x00070124L, rows.Single(r => r.Wcid == 1008466).Cell, "Citadel portal alcove (moved 2026-11-19).");
            Assert.AreEqual(0x00070123L, rows.Single(r => r.Wcid == 1008467).Cell, "Excavations portal alcove.");

            var text = File.ReadAllText(FindRealmFile("drift_lugian_wing.sql"));
            StringAssert.Contains(text, "-- @owns: guid:1:0x70007E87-0x70007E96");
            StringAssert.Contains(text, "`guid` BETWEEN 0x70007E87 AND 0x70007E96");
            StringAssert.Contains(text, "lint-ok: wipe-guid-range");
            Assert.AreEqual(1, Regex.Matches(text, @"DELETE FROM `landblock_instance_realm`").Count, "one DELETE.");
        }

        [TestMethod]
        public void Security_TotalsStayNineHonorGuardsAndSixWarriorsAcrossBayAndWing()
        {
            var bay = LoadPlacements("drift_marae_lassel_bay.sql");
            var wing = LoadPlacements("drift_lugian_wing.sql");
            bool Honor(Placement r) => r.Wcid == 1001440 || r.Wcid == 1001013;
            bool Warrior(Placement r) => r.Wcid == 1001441;

            Assert.AreEqual(5, bay.Count(Honor), "bay keeps 5 honor guards.");
            Assert.AreEqual(2, bay.Count(Warrior), "bay keeps 2 warriors.");
            Assert.AreEqual(4, wing.Count(Honor), "wing has 4 honor guards.");
            Assert.AreEqual(4, wing.Count(Warrior), "wing has 4 warriors.");
            Assert.AreEqual(9, bay.Concat(wing).Count(Honor), "9 honor guards in total.");
            Assert.AreEqual(6, bay.Concat(wing).Count(Warrior), "6 warriors in total.");

            // The eight rows the split removed from the bay must not come back, and no guid may exist in both units.
            foreach (var g in new long[] { 0x70007EA5, 0x70007EA4, 0x70007E9E, 0x70007E9D, 0x70007E9C, 0x70007E9A, 0x70007E99, 0x70007E97 })
                Assert.IsFalse(bay.Any(r => r.Guid == g), $"bay must no longer place 0x{g:X8}.");

            Assert.IsFalse(bay.Select(r => r.Guid).Intersect(wing.Select(r => r.Guid)).Any(), "bay and wing must not share a guid.");
            Assert.IsTrue(bay.All(r => r.Guid >= BayBandLow && r.Guid <= BayBandHigh), "bay guids stay in its band.");
        }

        [TestMethod]
        public void Wing_Layout_PortalsInSouthernAlcoves_NpcsFlankTheMouthInTheHall()
        {
            var rows = LoadPlacements("drift_lugian_wing.sql");
            Placement P(int wcid) => rows.Single(r => r.Wcid == wcid);

            // Portals centred in their alcoves (mouth plane x = 55, alcove centre y -40 / -30), the Citadel one moved 2026-11-19.
            Assert.AreEqual(0x70007E96L, P(1008466).Guid);
            Assert.AreEqual((0x00070124L, 53.5, -40.0), (P(1008466).Cell, P(1008466).X, P(1008466).Y), "Citadel portal centred in alcove 0x00070124.");
            Assert.AreEqual((0x00070123L, 53.436, -30.01), (P(1008467).Cell, P(1008467).X, P(1008467).Y), "Excavations portal unchanged.");

            var frames = rows.Where(r => r.Wcid == 1001076).OrderBy(r => r.Y).ToList();
            Assert.AreEqual((0x00070124L, 55.0, -40.09), (frames[0].Cell, frames[0].X, frames[0].Y), "Citadel frame follows its portal.");
            Assert.AreEqual((0x00070123L, 55.0, -30.09), (frames[1].Cell, frames[1].X, frames[1].Y), "Excavations frame unchanged.");

            // NPCs: against the west wall (0.46 m off the face at x = 55), 2.1425 m either side of the alcove centre, clerk north of the quartermaster.
            foreach (var p in Portals)
            {
                var portal = P(p.Wcid);
                var clerk = P(p.ClerkWcid);
                var qm = P(p.QuartermasterWcid);
                Assert.AreEqual(55.46, clerk.X, 1e-6, $"{p}: clerk x.");
                Assert.AreEqual(55.46, qm.X, 1e-6, $"{p}: quartermaster x.");
                Assert.AreEqual(Math.Round(portal.Y, 0) + 2.1425, clerk.Y, 0.02, $"{p}: clerk 2.1425 m north of the alcove centre.");
                Assert.AreEqual(Math.Round(portal.Y, 0) - 2.1425, qm.Y, 0.02, $"{p}: quartermaster 2.1425 m south of the alcove centre.");
                Assert.IsTrue(clerk.X > 55.0 && qm.X > 55.0, $"{p}: NPCs stand in the hall, not in the alcove.");
            }

            // Alcoves 0x00070121 and 0x00070122 are empty.
            Assert.IsFalse(rows.Any(r => r.Cell == 0x00070121 || r.Cell == 0x00070122), "alcoves 0x00070121/0x00070122 hold nothing.");
        }

        [TestMethod]
        public void ExitPortals_LandInFrontOfTheirOwnDungeonsAlcove()
        {
            var rows = LoadPlacements("drift_lugian_wing.sql");
            var exits = new[] { (Exit: 1008468, Portal: 1008466, Cell: 0x00070131L), (Exit: 1008469, Portal: 1008467, Cell: 0x00070130L) };

            foreach (var e in exits)
            {
                var text = File.ReadAllText(FindWeenieFile(e.Exit));
                var d = Regex.Match(text, $@"VALUES \({e.Exit}, 2, (0x[0-9A-Fa-f]+), (-?[\d.]+), (-?[\d.]+), (-?[\d.]+),");
                Assert.IsTrue(d.Success, $"{e.Exit}: Destination position row missing.");
                var inv = System.Globalization.CultureInfo.InvariantCulture;
                var portal = rows.Single(r => r.Wcid == e.Portal);

                Assert.AreEqual(e.Cell, Convert.ToInt64(d.Groups[1].Value, 16), $"{e.Exit}: destination hall cell.");
                Assert.AreEqual(Math.Round(portal.Y), double.Parse(d.Groups[3].Value, inv), 1e-9, $"{e.Exit}: lands level with its own portal's alcove centre (y).");
                Assert.IsTrue(double.Parse(d.Groups[2].Value, inv) > 55.0, $"{e.Exit}: lands in the hall in front of the alcove mouth (x > 55).");
                StringAssert.Contains(text, $"in front of the {(e.Portal == 1008466 ? "Citadel" : "Excavations")} alcove 0x{portal.Cell:X8}", $"{e.Exit}: comment names the portal's alcove.");
            }
        }

        [TestMethod]
        public void Wing_CitadelGuards_FaceTheMovedPortal()
        {
            var rows = LoadPlacements("drift_lugian_wing.sql");
            var text = File.ReadAllText(FindRealmFile("drift_lugian_wing.sql"));
            var portal = rows.Single(r => r.Wcid == 1008466);

            // Positions pinned to the owner-feedback layout.
            var expected = new (long Guid, double X, double Y)[]
            {
                (0x70007E8E, 59.0, -41.5), (0x70007E8D, 59.0, -38.5), (0x70007E8C, 57.5, -43.5), (0x70007E8B, 57.5, -36.5),
            };
            foreach (var (guid, x, y) in expected)
            {
                var g = rows.Single(r => r.Guid == guid);
                Assert.AreEqual((x, y), (g.X, g.Y), $"0x{guid:X8} position");
                Assert.AreEqual(0x00070131L, g.Cell, $"0x{guid:X8} hall cell");

                // Facing = the bay's formula toward the portal: theta = atan2(dx, dy), quat (cos(theta/2), 0, 0, -sin(theta/2)).
                var theta = Math.Atan2(portal.X - g.X, portal.Y - g.Y);
                var row = Regex.Match(text, @"\(1,\s*0x" + guid.ToString("X8") + @",[^\n]*?,\s*([\d.]+),\s*0,\s*0,\s*(-?[\d.]+),\s*0\)");
                Assert.IsTrue(row.Success, $"0x{guid:X8} row");
                Assert.AreEqual(Math.Cos(theta / 2), double.Parse(row.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture), 2e-6, $"0x{guid:X8} W");
                Assert.AreEqual(-Math.Sin(theta / 2), double.Parse(row.Groups[2].Value, System.Globalization.CultureInfo.InvariantCulture), 2e-6, $"0x{guid:X8} Z");
            }
        }

        [TestMethod]
        public void Portals_AreNamedDriftHillsCitadelAndDriftLugianExcavation()
        {
            StringAssert.Contains(File.ReadAllText(FindWeenieFile(1008466)), "'Drift Hills Citadel') /* Name */");
            StringAssert.Contains(File.ReadAllText(FindWeenieFile(1008467)), "'Drift Lugian Excavation') /* Name */");
        }

        [TestMethod]
        public void NorthRoute_HubOctagonNorthSealAndBayFalseWall_AreGone()
        {
            var seals = LoadPlacements("driftnetwork_hub_seals.sql");
            Assert.IsFalse(seals.Any(r => r.Guid == 0x70007EDB), "the octagon's north seal wall is gone.");
            Assert.AreEqual(4, seals.Count, "west wall + barrier, east wall + barrier remain.");
            var sealText = File.ReadAllText(FindRealmFile("driftnetwork_hub_seals.sql"));
            StringAssert.Contains(sealText, "0x70007EDC, 0x70007EDB)", "the unit's DELETE still names the removed guid so stale rows are swept.");

            var bay = LoadPlacements("drift_marae_lassel_bay.sql");
            Assert.AreEqual(16, bay.Count, "positive control: the bay still parses to its 16 rows, so the absence asserts below cannot pass on an empty parse.");
            Assert.AreEqual(4, seals.Count, "positive control (seals).");
            Assert.IsFalse(bay.Any(r => r.Guid == 0x70007EAC), "the bay's false wall is gone.");
            Assert.IsFalse(bay.Any(r => r.Wcid == 1001087), "the bay places no wall at all.");
            StringAssert.Contains(File.ReadAllText(FindRealmFile("drift_marae_lassel_bay.sql")), "BETWEEN 0x70007E97 AND 0x70007EAF");
        }

        [TestMethod]
        public void LugianWeapons_AreOpaque_NoTranslucencyRow()
        {
            foreach (var w in new[] { 1008458, 1008459, 1008475, 1008476, 1008477, 1008478 })
            {
                var text = File.ReadAllText(FindWeenieFile(w));
                Assert.IsFalse(Regex.IsMatch(text, $@"\({w},\s+76,"), $"{w}: no Translucency (float 76) row.");
                Assert.IsFalse(text.Contains("/* Translucency */"), $"{w}: no Translucency row.");
                Assert.IsTrue(Regex.IsMatch(text, @"\*/;\s*\n\s*\nINSERT INTO `weenie_properties_string`"), $"{w}: the float table still closes with a semicolon.");
            }
        }

        [TestMethod]
        public void Wing_EachPortalHasOneMageOneArcherTwoWarriors()
        {
            var rows = LoadPlacements("drift_lugian_wing.sql");

            foreach (var p in Portals)
            {
                var portal = rows.Single(r => r.Wcid == p.Wcid);
                var guards = rows.Where(r => r.Wcid == 1001440 || r.Wcid == 1001013 || r.Wcid == 1001441)
                    .Where(r => rows.Where(o => Portals.Any(pp => pp.Wcid == o.Wcid)).OrderBy(o => Math.Sqrt(Math.Pow(o.X - r.X, 2) + Math.Pow(o.Y - r.Y, 2))).First().Wcid == p.Wcid)
                    .ToList();

                Assert.AreEqual(1, guards.Count(g => g.Wcid == 1001440), $"{p}: one mage.");
                Assert.AreEqual(1, guards.Count(g => g.Wcid == 1001013), $"{p}: one archer.");
                Assert.AreEqual(2, guards.Count(g => g.Wcid == 1001441), $"{p}: two warriors.");
                Assert.IsTrue(guards.All(g => Math.Sqrt(Math.Pow(g.X - portal.X, 2) + Math.Pow(g.Y - portal.Y, 2)) < 8), $"{p}: guards stand within 8 m of their portal.");
            }
        }


        private static Emote Single(List<Emote> emotes, int category, string quest)
        {
            var hits = emotes.Where(e => e.Category == category && e.Quest == quest).ToList();
            Assert.AreEqual(1, hits.Count, $"Expected exactly one emote (category {category}, quest '{quest}'), found {hits.Count}.");
            return hits[0];
        }

        private static List<Emote> LoadEmotes(int wcid)
        {
            var text = File.ReadAllText(FindWeenieFile(wcid)).Replace("\r\n", "\n");

            var actionCols = Regex.Match(text, @"weenie_properties_emote_action` \((.*?)\)\n").Groups[1].Value
                .Split(',').Select(c => c.Trim().Trim('`')).ToList();
            int Col(string n) => actionCols.IndexOf(n);

            var result = new List<Emote>();
            var blocks = text.Split(new[] { "INSERT INTO `weenie_properties_emote` (" }, StringSplitOptions.None).Skip(1);

            foreach (var block in blocks)
            {
                // The final block runs on into the create_list insert; cut it off.
                var cut = block.IndexOf("INSERT INTO `weenie_properties_create_list`", StringComparison.Ordinal);
                var body = cut >= 0 ? block.Substring(0, cut) : block;

                var emote = new Emote();
                var rows = body.Split('\n').Where(l => l.StartsWith("VALUES (") || l.StartsWith("     , (")).ToList();

                var headerFields = SplitFields(rows[0]);
                emote.Category = int.Parse(Regex.Match(headerFields[1], @"\d+").Value);
                emote.Quest = Unquote(headerFields[6]);
                emote.ItemWcid = Num(headerFields[3]);

                foreach (var line in rows.Skip(1))
                {
                    var f = SplitFields(line);
                    emote.Actions.Add(new Action
                    {
                        Order = int.Parse(f[Col("order")]),
                        Type = int.Parse(Regex.Match(f[Col("type")], @"\d+").Value),
                        Message = Unquote(f[Col("message")]),
                        Min = Num(f[Col("min")]),
                        Max = Num(f[Col("max")]),
                        Amount = Num(f[Col("amount")]),
                        Amount64 = Num(f[Col("amount_64")]),
                        WeenieClassId = Num(f[Col("weenie_Class_Id")]),
                        StackSize = Num(f[Col("stack_Size")]),
                        Delay = double.Parse(Regex.Replace(f[Col("delay")], @"/\*.*?\*/", "").Trim(), System.Globalization.CultureInfo.InvariantCulture),
                    });
                }

                result.Add(emote);
            }

            return result;
        }

        private static long? Num(string s)
        {
            s = Regex.Replace(s, @"/\*.*?\*/", "").Trim();
            return s == "NULL" || s.Length == 0 ? (long?)null : long.Parse(s);
        }

        private static string Unquote(string s)
        {
            s = Regex.Replace(s, @"/\*.*?\*/", "").Trim();
            if (s == "NULL") return null;
            return s.Substring(1, s.Length - 2).Replace("''", "'");
        }

        /// <summary>Splits one VALUES tuple row into raw field text, honouring quotes and comments.</summary>
        private static List<string> SplitFields(string row)
        {
            var start = row.IndexOf('(');
            var fields = new List<string>();
            var cur = new System.Text.StringBuilder();
            var inQuote = false;
            var depth = 0;

            for (var i = start + 1; i < row.Length; i++)
            {
                var c = row[i];

                if (inQuote)
                {
                    cur.Append(c);
                    if (c == '\'')
                    {
                        if (i + 1 < row.Length && row[i + 1] == '\'') { cur.Append('\''); i++; }
                        else inQuote = false;
                    }
                    continue;
                }

                if (c == '/' && i + 1 < row.Length && row[i + 1] == '*')
                {
                    var end = row.IndexOf("*/", i + 2, StringComparison.Ordinal);
                    cur.Append(row, i, end + 2 - i);
                    i = end + 1;
                    continue;
                }

                if (c == '\'') { inQuote = true; cur.Append(c); continue; }
                if (c == '(') depth++;
                if (c == ')')
                {
                    if (depth == 0) { fields.Add(cur.ToString().Trim()); return fields; }
                    depth--;
                }

                if (c == ',' && depth == 0) { fields.Add(cur.ToString().Trim()); cur.Clear(); continue; }
                cur.Append(c);
            }

            fields.Add(cur.ToString().Trim());
            return fields;
        }

        private static string FindWeenieFile(int wcid)
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);

            while (dir != null)
            {
                var weenies = Path.Combine(dir.FullName, "Content", "sql", "weenies");

                if (Directory.Exists(weenies))
                {
                    var file = Directory.EnumerateFiles(weenies, wcid + " *.sql", SearchOption.TopDirectoryOnly)
                        .OrderBy(f => f, StringComparer.Ordinal).FirstOrDefault();
                    Assert.IsNotNull(file, $"{wcid}: no weenie file found in {weenies}.");
                    return file;
                }

                dir = dir.Parent;
            }

            Assert.Fail($"Could not locate Content/sql/weenies by walking up from {AppContext.BaseDirectory}.");
            return null;
        }
    }
}
