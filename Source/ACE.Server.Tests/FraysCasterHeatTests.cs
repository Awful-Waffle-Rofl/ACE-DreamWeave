using System.Collections.Generic;
using System.Linq;

using ACE.Server.Frays;
using ACE.Server.ThreadDungeons;
using ACE.Server.WorldEvents.Defs;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ACE.Server.Tests
{
    /// <summary>Casters only from caster_min_heat (so maggots do not cast void spells at Heat 0).</summary>
    [TestClass]
    public class FraysCasterHeatTests
    {
        [TestMethod]
        public void CasterMinHeat_DefaultsTo17()
        {
            Assert.AreEqual(17, (int)FraysSettings.All.Single(s => s.Key == FraysSettings.CasterMinHeat).Default);
        }

        [TestMethod]
        public void CastersAllowed_OnlyFromTheMinimumHeat()
        {
            Assert.IsFalse(FraysRules.CastersAllowed(0, 17));
            Assert.IsFalse(FraysRules.CastersAllowed(16, 17));
            Assert.IsTrue(FraysRules.CastersAllowed(17, 17));
            Assert.IsTrue(FraysRules.CastersAllowed(30, 17));
            Assert.IsTrue(FraysRules.CastersAllowed(0, 0), "0 = casters at every Heat");
        }

        [TestMethod]
        public void SpellBook_EmptiedBelowTheMinimumHeat_KeptFromIt()
        {
            // A retail Giant Maggot's book (46410): Nether Arc VII, Festering Curse VII, Weakening Curse VII, Corruption VII.
            Dictionary<int, float> Maggot() => new() { [5367] = 2.2f, [5377] = 2.25f, [5385] = 2.333f, [5401] = 2.5f };

            var low = Maggot();
            Assert.AreEqual(4, FraysRules.StripSpellsBelowCasterHeat(low, 0, 17));
            Assert.AreEqual(0, low.Count, "nothing casts at Heat 0");

            var edge = Maggot();
            Assert.AreEqual(4, FraysRules.StripSpellsBelowCasterHeat(edge, 16, 17));
            Assert.AreEqual(0, edge.Count);

            var high = Maggot();
            Assert.AreEqual(0, FraysRules.StripSpellsBelowCasterHeat(high, 17, 17));
            Assert.AreEqual(4, high.Count, "Heat 17 keeps its spells");

            var off = Maggot();
            Assert.AreEqual(0, FraysRules.StripSpellsBelowCasterHeat(off, 0, 0));
            Assert.AreEqual(4, off.Count);

            Assert.AreEqual(0, FraysRules.StripSpellsBelowCasterHeat(null, 0, 17));
        }

        private static FraysFamilyMember M(uint wcid, FraysFightKind kind, int level = 300) =>
            new FraysFamilyMember { Wcid = wcid, Name = $"m{wcid}", Level = level, Kind = kind, Weight = kind == FraysFightKind.Melee ? 1 : 0.15 };

        [TestMethod]
        public void FamilyPick_LeavesCastersOutBelowTheMinimumHeat()
        {
            var family = new FraysFamily
            {
                Key = "mixed",
                Table = new SpeciesTableDef { Id = "mixed" },
                Trash = new List<FraysFamilyMember> { M(1, FraysFightKind.Melee), M(2, FraysFightKind.Caster), M(3, FraysFightKind.Archer) },
                Elites = new List<FraysFamilyMember> { M(10, FraysFightKind.Caster, 400), M(11, FraysFightKind.Melee, 350) },
            };

            for (var i = 0; i < 300; i++)
            {
                Assert.AreNotEqual(2u, family.WcidFor(FraysRole.Standard, castersAllowed: false));
                Assert.AreNotEqual(2u, family.WcidFor(FraysRole.Swarm, castersAllowed: false));
            }

            Assert.AreEqual(11u, family.WcidFor(FraysRole.Snarl, castersAllowed: false), "the Snarl is the best non-caster elite");
            Assert.AreEqual(10u, family.WcidFor(FraysRole.Snarl, castersAllowed: true), "from the minimum Heat the caster elite is back");

            Assert.IsTrue(family.IsCaster(2) && family.IsCaster(10));
            Assert.IsFalse(family.IsCaster(1) || family.IsCaster(3));
        }

        [TestMethod]
        public void FamilyPick_AllCasters_StillFieldsSomething()
        {
            var family = new FraysFamily
            {
                Key = "casters",
                Table = new SpeciesTableDef { Id = "casters" },
                Trash = new List<FraysFamilyMember> { M(5, FraysFightKind.Caster) },
            };

            Assert.AreEqual(5u, family.WcidFor(FraysRole.Standard, castersAllowed: false), "its book is emptied at spawn instead");

            // as knathtead or gear_knight: every role still fields the family's own creature
            var knath = new FraysFamily
            {
                Key = "knathtead",
                Table = new SpeciesTableDef { Id = "knathtead" },
                Trash = new List<FraysFamilyMember> { M(5, FraysFightKind.Caster), M(6, FraysFightKind.Caster) },
                Elites = new List<FraysFamilyMember> { M(9, FraysFightKind.Caster, 400) },
            };

            Assert.IsTrue(knath.CasterOnlyPacks);
            foreach (var role in new[] { FraysRole.Standard, FraysRole.Swarm, FraysRole.Elite, FraysRole.Snarl })
                Assert.AreNotEqual(0u, knath.WcidFor(role, castersAllowed: false), role.ToString());
            Assert.AreEqual(9u, knath.WcidFor(FraysRole.Snarl, castersAllowed: false), "no creature that does not cast: its own Snarl, book emptied");
        }

        [TestMethod]
        public void ThreadsPick_LeavesCastersOut()
        {
            var melee = new DungeonSpawnPlanEntry(1, DungeonRole.Trash, null);
            var caster = new DungeonSpawnPlanEntry(2, DungeonRole.Trash, null);
            var entries = new List<DungeonSpawnPlanEntry> { melee, caster };

            CollectionAssert.AreEqual(new[] { 1u }, FraysThreadsShape.WithoutLeftOut(entries, w => w == 2).Select(e => e.Wcid).ToArray());
            Assert.AreEqual(2, FraysThreadsShape.WithoutLeftOut(entries, null).Count);
            Assert.AreEqual(0, FraysThreadsShape.WithoutLeftOut(new List<DungeonSpawnPlanEntry> { caster }, w => w == 2).Count, "all casters: the family's own pick is used");
        }

        // ---- an elite pool of casters only; family_exclude ----

        private static FraysFamily F(string key, IEnumerable<FraysFamilyMember> trash, IEnumerable<FraysFamilyMember> elites = null) => new FraysFamily
        {
            Key = key,
            Table = new SpeciesTableDef { Id = key },
            Trash = trash.ToList(),
            Elites = (elites ?? Enumerable.Empty<FraysFamilyMember>()).ToList(),
        };

        [TestMethod]
        public void ElitePick_CasterElitesOnly_APackCreatureThatDoesNotCastStepsUp()
        {
            var family = F("x", new[] { M(1, FraysFightKind.Melee, 200), M(2, FraysFightKind.Caster) }, new[] { M(10, FraysFightKind.Caster, 400) });

            for (var i = 0; i < 100; i++)
                Assert.AreEqual(1u, family.WcidFor(FraysRole.Elite, castersAllowed: false));

            Assert.AreEqual(1u, family.WcidFor(FraysRole.Snarl, castersAllowed: false));
        }

        [TestMethod]
        public void Excluding_LeavesOutByKeyOrCreatureType_NeverEmpty()
        {
            var larvae = new FraysFamily { Key = "olthoi_larvae", Type = ACE.Entity.Enum.CreatureType.OlthoiLarvae, Table = new SpeciesTableDef { Id = "olthoi_larvae" } };
            var olthoi = new FraysFamily { Key = "olthoi", Type = ACE.Entity.Enum.CreatureType.Olthoi, Table = new SpeciesTableDef { Id = "olthoi" } };
            var list = new List<FraysFamily> { larvae, olthoi };

            CollectionAssert.AreEqual(new[] { olthoi }, FraysFamilies.Excluding(list, "olthoi_larvae").ToArray());
            CollectionAssert.AreEqual(new[] { olthoi }, FraysFamilies.Excluding(list, " OlthoiLarvae , nothing").ToArray(), "a creature type works too");
            CollectionAssert.AreEqual(list, FraysFamilies.Excluding(list, "").ToList());
            CollectionAssert.AreEqual(list, FraysFamilies.Excluding(list, "olthoi_larvae,olthoi").ToList(), "never every family");
            Assert.AreEqual("olthoi_larvae", FraysSettings.All.Single(s => s.Key == FraysSettings.FamilyExclude).TextDefault);
        }

        [TestMethod]
        public void WeekRotation_NeverPicksAnExcludedFamily_OneFamilyEveryWeek()
        {
            var previousTables = FraysFamilies.TablesSource;
            var previousKind = FraysFamilies.KindOf;

            try
            {
                SpeciesMemberDef Mem(uint w) => new SpeciesMemberDef { Wcid = w, Name = $"m{w}", Level = 200, Role = 0 };
                SpeciesTableDef T(string id, string type, uint w) => new SpeciesTableDef { Id = id, DisplayName = id, CreatureType = type, Members = { Mem(w) } };

                var tables = new Dictionary<string, SpeciesTableDef>
                {
                    ["olthoi_larvae"] = T("olthoi_larvae", "OlthoiLarvae", 1),
                    ["drudge"] = T("drudge", "Drudge", 2),
                    ["lugian"] = T("lugian", "Lugian", 3),
                    ["tusker"] = T("tusker", "Tusker", 4),
                };

                FraysFamilies.TablesSource = () => tables;
                FraysFamilies.KindOf = w => FraysFightKind.Melee;

                Assert.AreEqual(3, FraysFamilies.All.Count, "the default family_exclude leaves the larvae out");
                Assert.IsFalse(FraysFamilies.All.Any(f => f.Key == "olthoi_larvae"));
                var larvaeWeeks = 0;

                for (var week = 0; week < 400; week++)
                {
                    var f = FraysFamilies.ForWeek(week, 0);
                    Assert.IsNotNull(f, $"week {week}");
                    Assert.AreNotEqual("olthoi_larvae", f.Key, $"week {week}");

                    // every other week keeps the family it had before the exclusion
                    var pool = FraysFamilies.ThreadsFamilies();
                    var at = new System.Random(unchecked(week * 7919 + 104729)).NextDouble() * pool.Sum(x => x.Weight);
                    var before = pool[^1];
                    foreach (var x in pool) { at -= x.Weight; if (at < 0) { before = x; break; } }
                    if (before.Key != "olthoi_larvae")
                        Assert.AreSame(before, f, $"week {week} keeps its family");
                    else
                        larvaeWeeks++;
                }

                Assert.IsTrue(larvaeWeeks > 0, "some weeks drew the larvae and drew again");
            }
            finally
            {
                FraysFamilies.TablesSource = previousTables;
                FraysFamilies.KindOf = previousKind;
            }
        }
    }
}
