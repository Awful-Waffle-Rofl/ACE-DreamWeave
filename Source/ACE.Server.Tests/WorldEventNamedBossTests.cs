using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

using ACE.Server.WorldEvents;
using ACE.Server.WorldEvents.Defs;
using ACE.Server.WorldEvents.Objectives;
using ACE.Server.WorldObjects;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ACE.Server.Tests
{
    /// <summary>
    /// Unit coverage for the named-boss engine leg (Docs/WorldEvents/BOSS-STANDARD.md): the bosses.json
    /// axis and its validation split, the composer's family gate, role 3 in the catalog and the roster
    /// selector, the power-weighted ratcheting health curve, and the fail-line pool.
    ///
    /// Everything here is a pure static or a plain object - no database, no landblock, no Creature and no
    /// Player (TECH-DESIGN D6).
    /// </summary>
    [TestClass]
    public class WorldEventNamedBossTests
    {
        // ---- (a) the shipped bosses.json ---------------------------------------------------------------

        private static string FindAxesDir()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);

            while (dir != null)
            {
                var candidate = Path.Combine(dir.FullName, "Content", "events", "axes");

                if (Directory.Exists(candidate) && File.Exists(Path.Combine(candidate, "bosses.json")))
                    return candidate;

                dir = dir.Parent;
            }

            return null;
        }

        private static string ReadShippedBosses()
        {
            var dir = FindAxesDir();

            if (dir == null)
                Assert.Inconclusive("Could not locate Content/events/axes by walking up from the test assembly -- skipping.");

            return File.ReadAllText(Path.Combine(dir, "bosses.json"));
        }

        /// <summary>The ten bosses in BOSS-STANDARD.md section 4 order, with their reserved wcids.</summary>
        private static readonly (string id, uint wcid, string displayName)[] ShippedBosses =
        {
            ("vhaleth", 1002619u, "Vhaleth, Matron of the Black Clutch"),
            ("pale_idol", 1002620u, "The Pale Idol of Bak't"),
            ("aurex", 1002621u, "Aurex, the Gilded Dynamo"),
            ("red_reaping", 1002622u, "The Red Reaping"),
            ("vessaryn", 1002623u, "Legate Vessaryn of the Violet Ranks"),
            ("ssethaun", 1002624u, "Ssethaun, Pale Hierophant of the Correction"),
            ("ghurrak", 1002625u, "Ghurrak Bonehide"),
            ("black_draught", 1002626u, "The Black Draught"),
            ("violet_wake", 1002627u, "The Violet Wake"),
            ("blackwing_eclipse", 1002628u, "The Blackwing Eclipse")
        };

        /// <summary>
        /// Each shipped Named boss's natural family binding (owner decision 2026-08-16, BOSS-STANDARD.md
        /// section 5): every boss belongs to the family it was written for, so "--boss auto" can resolve
        /// deterministically for those families instead of falling back to a random pick.
        /// </summary>
        private static readonly Dictionary<string, string> ShippedBossFamilies = new Dictionary<string, string>
        {
            ["vhaleth"] = "olthoi",
            ["pale_idol"] = "anekshay",
            ["aurex"] = "gear_knight",
            ["red_reaping"] = "grievver",
            ["vessaryn"] = "skeleton",
            ["ssethaun"] = "sclavus",
            ["ghurrak"] = "tusker",
            ["black_draught"] = "sleech",
            ["violet_wake"] = "remoran",
            ["blackwing_eclipse"] = "margul"
        };

        [TestMethod]
        public void ShippedBossesJson_ParsesCleanly_TenNamedBossesPlusTheTwoBuiltIns()
        {
            var store = WorldEventAxisStore.Parse(null, null, null, null, null, ReadShippedBosses());

            CollectionAssert.AreEqual(Array.Empty<string>(), store.Diagnostics.ToArray(),
                "the shipped bosses.json must validate clean");

            Assert.AreEqual(12, store.Bosses.Count, "two built-ins plus ten named bosses");

            Assert.AreEqual(BossKind.None, store.Bosses["none"].Kind);
            Assert.AreEqual(BossKind.FamilyChampion, store.Bosses["family_champion"].Kind);

            foreach (var expected in ShippedBosses)
            {
                Assert.IsTrue(store.Bosses.ContainsKey(expected.id), $"bosses.json is missing '{expected.id}'");

                var boss = store.Bosses[expected.id];

                Assert.AreEqual(BossKind.Named, boss.Kind, expected.id);
                Assert.AreEqual(expected.wcid, boss.NamedWcid, expected.id);
                Assert.AreEqual(expected.displayName, boss.DisplayName, expected.id);

                // Each boss is bound to its natural family (owner decision 2026-08-16).
                Assert.AreEqual(ShippedBossFamilies[expected.id], boss.FamilyId, $"{expected.id} familyId");

                // The weenie is the authority on base health; the override is an escape hatch nothing uses.
                Assert.AreEqual(0u, boss.BaseHealth, $"{expected.id} baseHealth");

                Assert.AreEqual(BossDef.DefaultPerPlayer, boss.PerPlayer, 0.0000001, $"{expected.id} perPlayer");
                Assert.AreEqual(BossDef.DefaultHealthCap, boss.HealthCap, 0.0000001, $"{expected.id} cap");

                // C16: every shipped boss carries the throughput dials explicitly, floor included - the
                // floor's C# default is 0 ("no throughput scaling"), so shipping it is what makes the
                // measured path reachable at all once the flag is turned on.
                Assert.AreEqual(100000u, boss.ThroughputFloorHealth, $"{expected.id} throughputFloorHealth");
                Assert.AreEqual(BossDef.DefaultThroughputCapHealth, boss.ThroughputCapHealth, $"{expected.id} throughputCapHealth");
                Assert.AreEqual(BossDef.DefaultTargetKillSeconds, boss.TargetKillSeconds, 0.0000001, $"{expected.id} targetKillSeconds");
                Assert.AreEqual(BossDef.DefaultThroughputCalibration, boss.ThroughputCalibration, 0.0000001, $"{expected.id} throughputCalibration");
                Assert.AreEqual(BossDef.DefaultMinSampleSeconds, boss.MinSampleSeconds, 0.0000001, $"{expected.id} minSampleSeconds");

                Assert.AreEqual(3, boss.FailLines.Count, $"{expected.id} ships three fail lines");

                foreach (var line in boss.FailLines)
                {
                    Assert.IsFalse(string.IsNullOrWhiteSpace(line), $"{expected.id} has a blank fail line");
                    Assert.IsFalse(line.Contains("**LV**"), $"{expected.id} fail line still carries the LV marker");
                }
            }
        }

        [TestMethod]
        public void BossDefCSharpDefaults_MatchTheShippedJsonValues()
        {
            var fresh = new BossDef();

            Assert.AreEqual(0.15, fresh.PerPlayer, 0.0000001);
            Assert.AreEqual(8.0, fresh.HealthCap, 0.0000001);
            Assert.AreEqual(0u, fresh.BaseHealth);
            Assert.IsNotNull(fresh.FailLines);
            Assert.AreEqual(0, fresh.FailLines.Count);

            // C16 dials. Four of the five match the shipped JSON exactly; throughputFloorHealth is the one
            // deliberate exception - 0 means "no throughput scaling for this boss", so an entry that omits
            // the key keeps the legacy power curve rather than inheriting a health rebase it never asked for.
            Assert.AreEqual(0u, fresh.ThroughputFloorHealth);
            Assert.AreEqual(6400000u, fresh.ThroughputCapHealth);
            Assert.AreEqual(300.0, fresh.TargetKillSeconds, 0.0000001);
            Assert.AreEqual(1.0, fresh.ThroughputCalibration, 0.0000001);
            Assert.AreEqual(120.0, fresh.MinSampleSeconds, 0.0000001);
        }

        [TestMethod]
        public void BossDef_ThroughputDials_RoundTripThroughJson()
        {
            var store = ParseBosses(@"{ ""bosses"": [ { ""id"": ""dialled"", ""displayName"": ""Dialled"", ""wcid"": 1002619, " +
                                    @"""throughputFloorHealth"": 250000, ""throughputCapHealth"": 5000000, " +
                                    @"""targetKillSeconds"": 240.0, ""throughputCalibration"": 0.8, ""minSampleSeconds"": 90.0 } ] }");

            CollectionAssert.AreEqual(Array.Empty<string>(), store.Diagnostics.ToArray(), "every dial is in range");

            var boss = store.Bosses["dialled"];

            Assert.AreEqual(250000u, boss.ThroughputFloorHealth);
            Assert.AreEqual(5000000u, boss.ThroughputCapHealth);
            Assert.AreEqual(240.0, boss.TargetKillSeconds, 0.0000001);
            Assert.AreEqual(0.8, boss.ThroughputCalibration, 0.0000001);
            Assert.AreEqual(90.0, boss.MinSampleSeconds, 0.0000001);
        }

        // ---- (b) entry defects DROP -------------------------------------------------------------------

        private static WorldEventAxisStore ParseBosses(string bossesJson)
        {
            return WorldEventAxisStore.Parse(null, null, null, null, null, bossesJson);
        }

        private const string GoodBoss =
            @"{ ""id"": ""ok_boss"", ""displayName"": ""Ok Boss"", ""wcid"": 1002619 }";

        [TestMethod]
        public void Bosses_MissingOrInvalidId_IsDropped()
        {
            var store = ParseBosses(@"{ ""bosses"": [ { ""displayName"": ""No Id"", ""wcid"": 1 }, " +
                                    @"{ ""id"": ""Bad Id"", ""displayName"": ""Bad"", ""wcid"": 1 }, " + GoodBoss + " ] }");

            Assert.AreEqual(3, store.Bosses.Count, "only the good entry joins the two built-ins");
            Assert.IsTrue(store.Bosses.ContainsKey("ok_boss"));
            Assert.AreEqual(2, store.Diagnostics.Count(d => d.Contains("invalid or missing id")));
        }

        [TestMethod]
        public void Bosses_IdCollidingWithABuiltIn_IsDropped()
        {
            var store = ParseBosses(@"{ ""bosses"": [ " +
                                    @"{ ""id"": ""none"", ""displayName"": ""Impostor"", ""wcid"": 1 }, " +
                                    @"{ ""id"": ""family_champion"", ""displayName"": ""Impostor"", ""wcid"": 1 } ] }");

            Assert.AreEqual(2, store.Bosses.Count, "the built-ins survive and neither entry is added");
            Assert.AreEqual(BossKind.None, store.Bosses["none"].Kind, "the built-in was not overwritten");
            Assert.AreEqual(BossKind.FamilyChampion, store.Bosses["family_champion"].Kind);
            Assert.AreEqual(2, store.Diagnostics.Count(d => d.Contains("collides with an existing boss id, dropped")));
        }

        [TestMethod]
        public void Bosses_DuplicateNamedId_LaterEntryIsDropped()
        {
            var store = ParseBosses(@"{ ""bosses"": [ " + GoodBoss + ", " +
                                    @"{ ""id"": ""ok_boss"", ""displayName"": ""Second"", ""wcid"": 99 } ] }");

            Assert.AreEqual(3, store.Bosses.Count);
            Assert.AreEqual(1002619u, store.Bosses["ok_boss"].NamedWcid, "the first entry wins");
            Assert.AreEqual(1, store.Diagnostics.Count(d => d.Contains("collides with an existing boss id, dropped")));
        }

        [TestMethod]
        public void Bosses_MissingDisplayName_IsDropped()
        {
            var store = ParseBosses(@"{ ""bosses"": [ { ""id"": ""nameless"", ""wcid"": 1002619 } ] }");

            Assert.AreEqual(2, store.Bosses.Count);
            Assert.IsTrue(store.Diagnostics.Any(d => d.Contains("boss 'nameless' missing displayName, dropped")));
        }

        [TestMethod]
        public void Bosses_ZeroWcid_IsDropped()
        {
            var store = ParseBosses(@"{ ""bosses"": [ { ""id"": ""ghost"", ""displayName"": ""Ghost"", ""wcid"": 0 } ] }");

            Assert.AreEqual(2, store.Bosses.Count);
            Assert.IsTrue(store.Diagnostics.Any(d => d.Contains("boss 'ghost' has a zero wcid, dropped")));
        }

        [TestMethod]
        public void Bosses_MalformedFamilyId_IsDropped_ButAnEmptyOneIsFine()
        {
            var store = ParseBosses(@"{ ""bosses"": [ " +
                                    @"{ ""id"": ""bound"", ""displayName"": ""Bound"", ""wcid"": 1, ""familyId"": ""Not An Id"" }, " +
                                    @"{ ""id"": ""free"", ""displayName"": ""Free"", ""wcid"": 2, ""familyId"": """" } ] }");

            Assert.AreEqual(3, store.Bosses.Count);
            Assert.IsFalse(store.Bosses.ContainsKey("bound"));
            Assert.IsTrue(store.Bosses.ContainsKey("free"));
            Assert.IsTrue(store.Diagnostics.Any(d => d.Contains("boss 'bound' has an invalid familyId 'Not An Id', dropped")));
        }

        [TestMethod]
        public void Bosses_MalformedJson_LeavesTheBuiltInsIntact()
        {
            var store = ParseBosses("{ not valid json ");

            Assert.AreEqual(2, store.Bosses.Count);
            Assert.IsTrue(store.Diagnostics.Any(d => d.Contains("bosses.json") && d.Contains("malformed")));
        }

        // ---- (c) dial defects REPAIR ------------------------------------------------------------------

        [TestMethod]
        public void Bosses_NegativeOrNonFinitePerPlayer_IsRepairedAndTheBossIsKept()
        {
            var store = ParseBosses(@"{ ""bosses"": [ " +
                                    @"{ ""id"": ""neg"", ""displayName"": ""Neg"", ""wcid"": 1, ""perPlayer"": -0.5 }, " +
                                    @"{ ""id"": ""nan"", ""displayName"": ""Nan"", ""wcid"": 2, ""perPlayer"": ""NaN"" } ] }");

            Assert.AreEqual(4, store.Bosses.Count, "both bosses are KEPT - a bad dial never drops an entry");
            Assert.AreEqual(BossDef.DefaultPerPlayer, store.Bosses["neg"].PerPlayer, 0.0000001);
            Assert.AreEqual(BossDef.DefaultPerPlayer, store.Bosses["nan"].PerPlayer, 0.0000001);
            Assert.AreEqual(2, store.Diagnostics.Count(d => d.Contains("invalid perPlayer, using the default")));
        }

        [TestMethod]
        public void Bosses_CapBelowOneOrNonFinite_IsRepairedAndTheBossIsKept()
        {
            var store = ParseBosses(@"{ ""bosses"": [ " +
                                    @"{ ""id"": ""low"", ""displayName"": ""Low"", ""wcid"": 1, ""cap"": 0.5 }, " +
                                    @"{ ""id"": ""inf"", ""displayName"": ""Inf"", ""wcid"": 2, ""cap"": ""Infinity"" } ] }");

            Assert.AreEqual(4, store.Bosses.Count);
            Assert.AreEqual(BossDef.DefaultHealthCap, store.Bosses["low"].HealthCap, 0.0000001);
            Assert.AreEqual(BossDef.DefaultHealthCap, store.Bosses["inf"].HealthCap, 0.0000001);
            Assert.AreEqual(2, store.Diagnostics.Count(d => d.Contains("cap below 1.0, using the default")));
        }

        [TestMethod]
        public void Bosses_BadThroughputDials_AreRepairedAndTheBossIsKept()
        {
            var store = ParseBosses(@"{ ""bosses"": [ " +
                                    @"{ ""id"": ""t"", ""displayName"": ""T"", ""wcid"": 1, ""targetKillSeconds"": 0 }, " +
                                    @"{ ""id"": ""c"", ""displayName"": ""C"", ""wcid"": 2, ""throughputCalibration"": -1.0 }, " +
                                    @"{ ""id"": ""m"", ""displayName"": ""M"", ""wcid"": 3, ""minSampleSeconds"": ""NaN"" }, " +
                                    @"{ ""id"": ""k"", ""displayName"": ""K"", ""wcid"": 4, ""throughputFloorHealth"": 200000, ""throughputCapHealth"": 100000 } ] }");

            Assert.AreEqual(6, store.Bosses.Count, "all four are KEPT - a bad dial never drops an entry");

            Assert.AreEqual(BossDef.DefaultTargetKillSeconds, store.Bosses["t"].TargetKillSeconds, 0.0000001);
            Assert.AreEqual(BossDef.DefaultThroughputCalibration, store.Bosses["c"].ThroughputCalibration, 0.0000001);
            Assert.AreEqual(BossDef.DefaultMinSampleSeconds, store.Bosses["m"].MinSampleSeconds, 0.0000001);
            Assert.AreEqual(BossDef.DefaultThroughputCapHealth, store.Bosses["k"].ThroughputCapHealth);
            Assert.AreEqual(200000u, store.Bosses["k"].ThroughputFloorHealth, "the floor itself is left alone");

            Assert.AreEqual(1, store.Diagnostics.Count(d => d.Contains("invalid targetKillSeconds, using the default")));
            Assert.AreEqual(1, store.Diagnostics.Count(d => d.Contains("invalid throughputCalibration, using the default")));
            Assert.AreEqual(1, store.Diagnostics.Count(d => d.Contains("invalid minSampleSeconds, using the default")));
            Assert.AreEqual(1, store.Diagnostics.Count(d => d.Contains("throughputCapHealth at or below its throughputFloorHealth, using the default")));
        }

        [TestMethod]
        public void Bosses_AZeroThroughputFloor_IsNotADefect()
        {
            // 0 is the C# default and means "this boss keeps the power curve" - it must not produce a
            // diagnostic, and it must not drag the cap check in behind it either.
            var store = ParseBosses(@"{ ""bosses"": [ { ""id"": ""legacy"", ""displayName"": ""Legacy"", ""wcid"": 1, " +
                                    @"""throughputFloorHealth"": 0, ""throughputCapHealth"": 0 } ] }");

            CollectionAssert.AreEqual(Array.Empty<string>(), store.Diagnostics.ToArray());
            Assert.AreEqual(0u, store.Bosses["legacy"].ThroughputFloorHealth);
        }

        [TestMethod]
        public void Bosses_NullFailLines_BecomesAnEmptyList()
        {
            var store = ParseBosses(@"{ ""bosses"": [ { ""id"": ""quiet"", ""displayName"": ""Quiet"", ""wcid"": 1, ""failLines"": null } ] }");

            Assert.AreEqual(3, store.Bosses.Count);
            Assert.IsNotNull(store.Bosses["quiet"].FailLines);
            Assert.AreEqual(0, store.Bosses["quiet"].FailLines.Count);
            CollectionAssert.AreEqual(Array.Empty<string>(), store.Diagnostics.ToArray());
        }

        [TestMethod]
        public void Bosses_OmittedDials_TakeTheCSharpDefaults()
        {
            var store = ParseBosses(@"{ ""bosses"": [ " + GoodBoss + " ] }");

            var boss = store.Bosses["ok_boss"];

            Assert.AreEqual(BossDef.DefaultPerPlayer, boss.PerPlayer, 0.0000001);
            Assert.AreEqual(BossDef.DefaultHealthCap, boss.HealthCap, 0.0000001);
            Assert.AreEqual("", boss.FamilyId ?? "");
            CollectionAssert.AreEqual(Array.Empty<string>(), store.Diagnostics.ToArray());
        }

        // ---- (d) the composer's family gate ------------------------------------------------------------

        private static (string sources, string families, string goals, string rewards, string anchors) ReadShippedAxes()
        {
            var dir = FindAxesDir();

            if (dir == null)
                Assert.Inconclusive("Could not locate Content/events/axes by walking up from the test assembly -- skipping.");

            return (
                File.ReadAllText(Path.Combine(dir, "sources.json")),
                File.ReadAllText(Path.Combine(dir, "families.json")),
                File.ReadAllText(Path.Combine(dir, "goals.json")),
                File.ReadAllText(Path.Combine(dir, "rewards.json")),
                File.ReadAllText(Path.Combine(dir, "anchors.json"))
            );
        }

        /// <summary>The shipped axes with a bosses.json of the test's own choosing.</summary>
        private static WorldEventAxisStore StoreWithBosses(string bossesJson)
        {
            var (sources, families, goals, rewards, anchors) = ReadShippedAxes();

            return WorldEventAxisStore.Parse(sources, families, goals, rewards, anchors, bossesJson);
        }

        /// <summary>A catalog holding two families, so a family gate has something to be wrong about.</summary>
        private static WorldEventCatalog TwoFamilyCatalog()
        {
            var families = new Dictionary<string, IReadOnlyList<FamilyMember>>
            {
                ["olthoi"] = new List<FamilyMember>
                {
                    new FamilyMember { Wcid = 9001, Name = "Drone", Level = 100, Role = 0 }
                },
                ["tusker"] = new List<FamilyMember>
                {
                    new FamilyMember { Wcid = 9002, Name = "Tusker", Level = 100, Role = 0 }
                }
            };

            return new WorldEventCatalog(families, new List<string>());
        }

        /// <summary>
        /// A catalog covering the families exercised by the "--boss auto" tests: "olthoi" (vhaleth's
        /// natural family, from the shipped bosses.json), "tusker" (ghurrak's, and reused for the
        /// wrong-family refusal case) and "testclone" (a real families.json id with no bound boss, so
        /// auto must fall back to a random Named boss).
        /// </summary>
        private static WorldEventCatalog AutoBossCatalog()
        {
            var families = new Dictionary<string, IReadOnlyList<FamilyMember>>
            {
                ["olthoi"] = new List<FamilyMember>
                {
                    new FamilyMember { Wcid = 9001, Name = "Drone", Level = 100, Role = 0 }
                },
                ["tusker"] = new List<FamilyMember>
                {
                    new FamilyMember { Wcid = 9002, Name = "Tusker", Level = 100, Role = 0 }
                },
                ["testclone"] = new List<FamilyMember>
                {
                    new FamilyMember { Wcid = 9003, Name = "Test Clone", Level = 100, Role = 0 }
                }
            };

            return new WorldEventCatalog(families, new List<string>());
        }

        private static WorldEventRequest Request(string familyId, string bossId, string goalId = "kill_count",
            string sourceId = "ambush")
        {
            return new WorldEventRequest
            {
                SourceId = sourceId,
                FamilyId = familyId,
                GoalId = goalId,
                BossId = bossId,
                AnchorPosition = new ACE.Entity.Position(0x016C019E, 10f, 10f, 0f, 0f, 0f, 0f, 1f, 0),
                AnchorLabel = "the test anchor",
                Invoker = "test"
            };
        }

        private const string BoundBoss =
            @"{ ""bosses"": [ { ""id"": ""bound"", ""displayName"": ""Bound"", ""wcid"": 1002619, ""familyId"": ""olthoi"" }, " +
            @"{ ""id"": ""free"", ""displayName"": ""Free"", ""wcid"": 1002620, ""familyId"": """" } ] }";

        [TestMethod]
        public void Compose_NamedBossBoundToAnotherFamily_IsRefused()
        {
            var store = StoreWithBosses(BoundBoss);

            var ok = WorldEventComposer.TryCompose(store, TwoFamilyCatalog(), Request("tusker", "bound"),
                out var composition, out var error);

            Assert.IsFalse(ok);
            Assert.IsNull(composition);
            StringAssert.Contains(error, "boss 'bound' is not compatible with family 'tusker'");
            StringAssert.Contains(error, "compatible: olthoi");
        }

        [TestMethod]
        public void Compose_NamedBossBoundToTheComposedFamily_Succeeds()
        {
            var store = StoreWithBosses(BoundBoss);

            var ok = WorldEventComposer.TryCompose(store, TwoFamilyCatalog(), Request("olthoi", "bound"),
                out var composition, out var error);

            Assert.IsTrue(ok, error);
            Assert.AreEqual(BossKind.Named, composition.Boss.Kind);
            Assert.AreEqual(1002619u, composition.Boss.NamedWcid);
        }

        [TestMethod]
        public void Compose_NamedBossBoundToTheSECONDComposedFamily_Succeeds()
        {
            // Two-family composition (2026-08-29): the gate accepts a binding that matches EITHER composed
            // family. "bound" is bound to olthoi, which here is slot B - the boss stands over a crowd drawn
            // from the union of both, so binding to the second half is as valid as binding to the first.
            var store = StoreWithBosses(BoundBoss);

            var request = Request("tusker", "bound");
            request.FamilyIds = new List<string> { "tusker", "olthoi" };

            var ok = WorldEventComposer.TryCompose(store, TwoFamilyCatalog(), request, out var composition, out var error);

            Assert.IsTrue(ok, error);
            Assert.AreEqual("bound", composition.Boss.Id);
            CollectionAssert.AreEqual(new[] { "tusker", "olthoi" }, composition.Families.Select(f => f.Id).ToList());
        }

        [TestMethod]
        public void Compose_NamedBossBoundToNeitherComposedFamily_IsRefusedAndNamesBoth()
        {
            var store = StoreWithBosses(BoundBoss);

            var catalog = new WorldEventCatalog(new Dictionary<string, IReadOnlyList<FamilyMember>>
            {
                ["tusker"] = new List<FamilyMember> { new FamilyMember { Wcid = 9002, Name = "Tusker", Level = 100, Role = 0 } },
                ["testclone"] = new List<FamilyMember> { new FamilyMember { Wcid = 9003, Name = "Test Clone", Level = 100, Role = 0 } }
            }, new List<string>());

            var request = Request("tusker", "bound");
            request.FamilyIds = new List<string> { "tusker", "testclone" };

            var ok = WorldEventComposer.TryCompose(store, catalog, request, out var composition, out var error);

            Assert.IsFalse(ok);
            Assert.IsNull(composition);
            StringAssert.Contains(error, "boss 'bound' is not compatible with family 'tusker+testclone'");
            StringAssert.Contains(error, "compatible: olthoi");
        }

        [TestMethod]
        public void Compose_AutoBoss_DrawsFromEitherComposedFamilysBoundBosses()
        {
            // One boss bound to olthoi, one to tusker, one unbound. With both families composed, "auto"
            // must be able to land on EITHER bound boss and must never fall back to the unbound one, which
            // is only reached when neither family has a binding at all.
            const string bosses =
                @"{ ""bosses"": [ { ""id"": ""olthoi_boss"", ""displayName"": ""Olthoi Boss"", ""wcid"": 1002619, ""familyId"": ""olthoi"" }, " +
                @"{ ""id"": ""tusker_boss"", ""displayName"": ""Tusker Boss"", ""wcid"": 1002620, ""familyId"": ""tusker"" }, " +
                @"{ ""id"": ""unbound_boss"", ""displayName"": ""Unbound Boss"", ""wcid"": 1002621, ""familyId"": """" } ] }";

            var store = StoreWithBosses(bosses);
            var catalog = TwoFamilyCatalog();

            var picked = new HashSet<string>();

            for (var seed = 0; seed < 40; seed++)
            {
                var request = Request("olthoi", "auto");
                request.FamilyIds = new List<string> { "olthoi", "tusker" };

                var ok = WorldEventComposer.TryCompose(store, catalog, request, out var composition, out var error, new Random(seed));

                Assert.IsTrue(ok, error);

                picked.Add(composition.Boss.Id);
            }

            Assert.IsFalse(picked.Contains("unbound_boss"),
                "auto must stay inside the bound pool while either composed family has a binding");

            CollectionAssert.AreEquivalent(new[] { "olthoi_boss", "tusker_boss" }, picked.ToList(),
                "auto must be able to draw a boss bound to either composed family");
        }

        [TestMethod]
        public void Compose_NamedBossWithAnEmptyFamilyId_ComposesWithAnyFamily()
        {
            var store = StoreWithBosses(BoundBoss);

            foreach (var familyId in new[] { "olthoi", "tusker" })
            {
                var ok = WorldEventComposer.TryCompose(store, TwoFamilyCatalog(), Request(familyId, "free"),
                    out var composition, out var error);

                Assert.IsTrue(ok, $"{familyId}: {error}");
                Assert.AreEqual("free", composition.Boss.Id);
            }
        }

        [TestMethod]
        public void Compose_TheTwoBuiltInBosses_AreNeverFamilyGated()
        {
            var store = StoreWithBosses(BoundBoss);

            foreach (var bossId in new[] { "none", "family_champion" })
            {
                var ok = WorldEventComposer.TryCompose(store, TwoFamilyCatalog(), Request("tusker", bossId),
                    out _, out var error);

                Assert.IsTrue(ok, $"{bossId}: {error}");
            }
        }

        // ---- (d2) "--boss auto" (BOSS-STANDARD.md section 5, owner decision 2026-08-16) ---------------

        [TestMethod]
        public void Compose_BossAutoWithABoundFamily_ResolvesTheBoundNamedBoss()
        {
            var store = StoreWithBosses(ReadShippedBosses());

            var ok = WorldEventComposer.TryCompose(store, AutoBossCatalog(), Request("olthoi", "auto"),
                out var composition, out var error);

            Assert.IsTrue(ok, error);
            Assert.AreEqual(BossKind.Named, composition.Boss.Kind);
            Assert.AreEqual("vhaleth", composition.Boss.Id);
        }

        [TestMethod]
        public void Compose_BossAutoWithNoBoundBoss_PicksARandomNamedBoss_DeterministicallyPerSeed()
        {
            var store = StoreWithBosses(ReadShippedBosses());

            // "testclone" is a real families.json id (see AutoBossCatalog) that none of the shipped
            // bosses are bound to, so auto must fall back to a random pick among all ten Named bosses.
            var ok1 = WorldEventComposer.TryCompose(store, AutoBossCatalog(), Request("testclone", "auto"),
                out var composition1, out var error1, new Random(42));

            var ok2 = WorldEventComposer.TryCompose(store, AutoBossCatalog(), Request("testclone", "auto"),
                out var composition2, out var error2, new Random(42));

            Assert.IsTrue(ok1, error1);
            Assert.IsTrue(ok2, error2);
            Assert.AreEqual(BossKind.Named, composition1.Boss.Kind);
            Assert.AreEqual(composition1.Boss.Id, composition2.Boss.Id, "the same seed must pick the same boss");
        }

        [TestMethod]
        public void Compose_BossAutoWithNoNamedBossesInTheStore_Refuses()
        {
            var store = StoreWithBosses(@"{ ""bosses"": [] }");

            var ok = WorldEventComposer.TryCompose(store, AutoBossCatalog(), Request("olthoi", "auto"),
                out var composition, out var error);

            Assert.IsFalse(ok);
            Assert.IsNull(composition);
            Assert.AreEqual("boss 'auto' needs at least one named boss in bosses.json", error);
        }

        [TestMethod]
        public void Compose_ExplicitNamedBoss_IsStillRefusedForAWrongFamily_WithAutoAvailable()
        {
            var store = StoreWithBosses(ReadShippedBosses());

            var ok = WorldEventComposer.TryCompose(store, AutoBossCatalog(), Request("tusker", "vhaleth"),
                out var composition, out var error);

            Assert.IsFalse(ok);
            Assert.IsNull(composition);
            StringAssert.Contains(error, "boss 'vhaleth' is not compatible with family 'tusker'");
            StringAssert.Contains(error, "compatible: olthoi");
        }

        [TestMethod]
        public void ObjectiveFactory_KillBossWithANamedBoss_BuildsTheObjective()
        {
            var store = StoreWithBosses(BoundBoss);

            Assert.IsTrue(WorldEventComposer.TryCompose(store, TwoFamilyCatalog(),
                Request("olthoi", "bound", "kill_boss", "sky_rift"), out var composition, out var composeError), composeError);

            var evt = new WorldEvent(1, composition, Request("olthoi", "bound", "kill_boss", "sky_rift"), null,
                () => new AudienceEstimate(3, 100, 150), () => 1_700_000_000d);

            var objective = WorldEventObjectiveFactory.Create(evt, out var error);

            Assert.IsNotNull(objective, error);
            Assert.IsNull(error);
        }

        /// <summary>
        /// 2026-08-16: the sibling of the placement-retry fix - kill_boss with no boss goal is refused at
        /// COMPOSE time now (a run that can never place a champion is unwinnable from the start), so this
        /// case no longer reaches TryCompose successfully. See Compose_KillBossWithBossNone_IsRefused below
        /// for that refusal, and the case immediately after for the factory's own defense-in-depth path
        /// that still exists for a composition built any other way.
        /// </summary>
        [TestMethod]
        public void Compose_KillBossWithBossNone_IsRefused()
        {
            var store = StoreWithBosses(BoundBoss);

            var ok = WorldEventComposer.TryCompose(store, TwoFamilyCatalog(),
                Request("olthoi", "none", "kill_boss", "sky_rift"), out var composition, out var error);

            Assert.IsFalse(ok);
            Assert.IsNull(composition);
            Assert.AreEqual("goal 'kill_boss' needs --boss (family_champion, auto, or a named boss id)", error);
        }

        [TestMethod]
        public void Compose_KillBossWithFamilyChampion_StillComposes()
        {
            var store = StoreWithBosses(BoundBoss);

            var ok = WorldEventComposer.TryCompose(store, TwoFamilyCatalog(),
                Request("olthoi", "family_champion", "kill_boss", "sky_rift"), out var composition, out var error);

            Assert.IsTrue(ok, error);
            Assert.AreEqual(BossKind.FamilyChampion, composition.Boss.Kind);
        }

        [TestMethod]
        public void ObjectiveFactory_KillBossWithBossNone_RefusesAndNamesTheRealRule()
        {
            // Constructed directly rather than through the composer: TryCompose now refuses kill_boss +
            // boss none at compose time (see Compose_KillBossWithBossNone_IsRefused above), so this
            // exercises the factory's own defense-in-depth refusal for a composition built any other way.
            var composition = new WorldEventComposition(WaveTheme(),
                new[] { Family(new FamilyMember { Wcid = 1002604, Name = "Drone", Level = 100, Role = 0 }) },
                BossDef.None,
                new GoalDef
                {
                    Id = "kill_boss",
                    DisplayName = "Slay the Champion",
                    Type = "KillBoss",
                    TypeKind = GoalType.KillBoss,
                    MvpRule = "killingBlowAndTopDamage",
                    RuleKind = MvpRule.KillingBlowAndTopDamage,
                    ProgressTemplate = "The champion still stands."
                },
                new RewardDef
                {
                    Id = "standard",
                    DisplayName = "Crate",
                    SuccessCrateWcid = 1002600,
                    ConsolationCrateWcid = 1002601,
                    CacheWcid = 1002602,
                    ParticipantsPerCache = 8,
                    ClaimWindowSeconds = 120
                },
                new AnchorDef { Id = "here", DisplayName = "the test anchor", CellId = 0x016C019E, BiomeTags = new List<string>() },
                new ACE.Entity.Position(0x016C019E, 10f, 10f, 0f, 0f, 0f, 0f, 1f, 0),
                WorldEventAxisStore.Empty);

            var evt = new WorldEvent(1, composition, Request("olthoi", "none", "kill_boss", "sky_rift"), null,
                () => new AudienceEstimate(3, 100, 150), () => 1_700_000_000d);

            var objective = WorldEventObjectiveFactory.Create(evt, out var error);

            Assert.IsNull(objective);
            Assert.AreEqual("goal kill_boss needs a --boss that is not 'none' (try --boss auto)", error);
        }

        // ---- (e) WorldEventRole 3 --------------------------------------------------------------------

        private static ACE.Entity.Models.Weenie BossWeenie(uint wcid, string family, int level, int? role)
        {
            var weenie = new ACE.Entity.Models.Weenie
            {
                WeenieClassId = wcid,
                ClassName = $"testboss{wcid}",
                WeenieType = ACE.Entity.Enum.WeenieType.Creature,
                PropertiesBool = new Dictionary<ACE.Entity.Enum.Properties.PropertyBool, bool>(),
                PropertiesInt = new Dictionary<ACE.Entity.Enum.Properties.PropertyInt, int>(),
                PropertiesString = new Dictionary<ACE.Entity.Enum.Properties.PropertyString, string>()
            };

            weenie.PropertiesBool[ACE.Entity.Enum.Properties.PropertyBool.WorldEventCreature] = true;
            weenie.PropertiesBool[ACE.Entity.Enum.Properties.PropertyBool.Attackable] = true;
            weenie.PropertiesString[ACE.Entity.Enum.Properties.PropertyString.WorldEventFamily] = family;
            weenie.PropertiesString[ACE.Entity.Enum.Properties.PropertyString.Name] = $"Test {wcid}";
            weenie.PropertiesInt[ACE.Entity.Enum.Properties.PropertyInt.Level] = level;

            if (role != null)
                weenie.PropertiesInt[ACE.Entity.Enum.Properties.PropertyInt.WorldEventRole] = role.Value;

            return weenie;
        }

        [TestMethod]
        public void Catalog_Role3IsKeptSilently_Role4IsDropped()
        {
            var catalog = WorldEventCatalogBuilder.Build(new List<ACE.Entity.Models.Weenie>
            {
                BossWeenie(1002619, "olthoi", 400, 3),
                BossWeenie(1002620, "olthoi", 400, 4)
            }, out var diagnostics);

            Assert.AreEqual(1, catalog.MemberCount, "role 3 joins the catalog, role 4 does not");
            Assert.AreEqual(1002619u, catalog.Families["olthoi"][0].Wcid);
            Assert.AreEqual(3, catalog.Families["olthoi"][0].Role);

            Assert.AreEqual(1, diagnostics.Count);
            StringAssert.Contains(diagnostics[0], "outside 0..3 (0 trash / 1 elite / 2 champion / 3 named boss)");
        }

        [TestMethod]
        public void Catalog_DescribeCoverage_ReportsNamedBosses()
        {
            var catalog = WorldEventCatalogBuilder.Build(new List<ACE.Entity.Models.Weenie>
            {
                BossWeenie(1002604, "olthoi", 100, 0),
                BossWeenie(1002619, "olthoi", 400, 3)
            }, out _);

            Assert.AreEqual("olthoi: 2 members, levels 100-400, trash 1 elite 0 champion 0 named 1, flagged 2 table 0, casters 0, minLevel 100",
                catalog.DescribeCoverage("olthoi"));
        }

        private static FamilyDef Family(params FamilyMember[] members)
        {
            return new FamilyDef
            {
                Id = "olthoi",
                DisplayName = "the Black Clutch",
                Members = new List<FamilyMember>(members)
            };
        }

        private static SourceThemeDef WaveTheme()
        {
            return new SourceThemeDef
            {
                Id = "ambush",
                DisplayName = "Ambush",
                Geometry = "edges",
                GeometryKind = SourceGeometry.Edges,
                GeometryRadius = 45f,
                GeometryPoints = 3,
                WaveIntervalSeconds = 45,
                MaxAlive = 24,
                WaveCount = new ScaledCount { Base = 4, PerParticipant = 0, Cap = 18 },
                RewardRadius = 60f
            };
        }

        [TestMethod]
        public void PickWave_AFamilyOfOnlyNamedBosses_ProducesNothing()
        {
            var family = Family(new FamilyMember { Wcid = 1002619, Name = "Vhaleth", Level = 400, Role = 3 });

            var pick = WorldEventRosterSelector.PickWave(family, new AudienceEstimate(5, 100, 150),
                WaveTheme(), waveIndex: 0, currentAlive: 0, new Random(1));

            Assert.AreEqual(0, pick.Total, "the any-role fallback must not reach a named boss");
        }

        [TestMethod]
        public void PickChampion_AFamilyOfOnlyNamedBosses_ReturnsZero()
        {
            var family = Family(new FamilyMember { Wcid = 1002619, Name = "Vhaleth", Level = 400, Role = 3 });

            // PickChampion now answers a ChampionPick (#629): None is the "nothing in band and nothing to
            // promote" verdict the caller declines on, and a family of nothing but named bosses is exactly it.
            Assert.IsTrue(WorldEventRosterSelector.PickChampion(family, new AudienceEstimate(5, 100, 150)).IsNone);
        }

        [TestMethod]
        public void PickChampion_NeverPromotesANamedBossOverARealMember()
        {
            var family = Family(
                new FamilyMember { Wcid = 1002604, Name = "Drone", Level = 100, Role = 0 },
                new FamilyMember { Wcid = 1002619, Name = "Vhaleth", Level = 400, Role = 3 });

            // #629: with no real role-1/role-2 member the role-0 Drone is PROMOTED into the champion slot
            // (synthetic), which is still the level-400 named boss being skipped - the point of this test.
            var pick = WorldEventRosterSelector.PickChampion(family, new AudienceEstimate(5, 100, 150));

            Assert.AreEqual(1002604u, pick.Wcid,
                "the level-400 named boss is the highest member and must still be skipped");
        }

        [TestMethod]
        public void PickWave_TrashPlusANamedBoss_NeverDrawsTheBossOver200Draws()
        {
            var family = Family(
                new FamilyMember { Wcid = 1002604, Name = "Drone", Level = 100, Role = 0 },
                new FamilyMember { Wcid = 1002619, Name = "Vhaleth", Level = 400, Role = 3 });

            var theme = WaveTheme();
            var est = new AudienceEstimate(5, 100, 150);

            var drawn = 0;

            for (var seed = 0; seed < 200; seed++)
            {
                var pick = WorldEventRosterSelector.PickWave(family, est, theme, waveIndex: seed,
                    currentAlive: 0, new Random(seed));

                Assert.IsTrue(pick.Total > 0, $"seed {seed} produced an empty wave");

                drawn += pick.Trash.Count(w => w == 1002619u) + pick.Champions.Count(w => w == 1002619u);
            }

            Assert.AreEqual(0, drawn, "a named boss must never be drawn into a wave");
        }

        // ---- (f) power-weighted boss health ------------------------------------------------------------

        [TestMethod]
        public void PowerSum_IsTheSquaredShareOf275()
        {
            Assert.AreEqual(0.0, WorldEventRosterSelector.PowerSum(null), 0.0000001);
            Assert.AreEqual(0.0, WorldEventRosterSelector.PowerSum(new List<int>()), 0.0000001);
            Assert.AreEqual(0.0, WorldEventRosterSelector.PowerSum(new List<int> { 0 }), 0.0000001);

            Assert.AreEqual(1.0, WorldEventRosterSelector.PowerSum(new List<int> { 275 }), 0.0000001);
            Assert.AreEqual(1.0, WorldEventRosterSelector.PowerSum(new List<int> { 500 }), 0.0000001,
                "levels above the reference are clamped, never worth more than one whole player");

            Assert.AreEqual(0.1322314, WorldEventRosterSelector.PowerSum(new List<int> { 100 }), 0.0000001);

            Assert.AreEqual(0.0, WorldEventRosterSelector.PowerSum(new List<int> { -50 }), 0.0000001);

            Assert.AreEqual(2.1322314, WorldEventRosterSelector.PowerSum(new List<int> { 275, 275, 100 }), 0.0000001,
                "the sum is additive over the sampled players");
        }

        [TestMethod]
        public void EstimateFromLevels_CarriesThePowerSum()
        {
            var est = WorldEventRosterSelector.EstimateFromLevels(new List<int> { 275, 100 });

            Assert.AreEqual(2, est.Count);
            Assert.AreEqual(1.1322314, est.PowerSum, 0.0000001);

            Assert.AreEqual(0.0, WorldEventRosterSelector.EstimateFromLevels(new List<int>()).PowerSum, 0.0000001);
        }

        [TestMethod]
        public void AudienceEstimate_PowerSumDefaultsToZero()
        {
            Assert.AreEqual(0.0, new AudienceEstimate(5, 100, 150).PowerSum, 0.0000001,
                "the three-argument form every existing call site uses must still compile and mean power 0");
        }

        [TestMethod]
        public void ResolveHealthMult_FloorsAtOne_CapsAtEight_AndRepairsBadDials()
        {
            var boss = new BossDef();

            Assert.AreEqual(1.0, boss.ResolveHealthMult(0), 0.0000001, "an empty field leaves the weenie alone");
            Assert.AreEqual(1.15, boss.ResolveHealthMult(1.0), 0.0000001, "one level-275 adds 15 percent");
            Assert.AreEqual(1.75, boss.ResolveHealthMult(5.0), 0.0000001);

            Assert.AreEqual(8.0, boss.ResolveHealthMult(1000.0), 0.0000001, "the cap holds");

            Assert.AreEqual(1.0, boss.ResolveHealthMult(double.NaN), 0.0000001);
            Assert.AreEqual(1.0, boss.ResolveHealthMult(double.NegativeInfinity), 0.0000001);
            Assert.AreEqual(1.0, boss.ResolveHealthMult(-5.0), 0.0000001);

            // The dial-free form repairs its own inputs the same way.
            Assert.AreEqual(1.0, BossDef.ResolveHealthMult(double.NaN, 8.0, 0.0), 0.0000001);
            Assert.AreEqual(1.0, BossDef.ResolveHealthMult(0.15, 0.5, 100.0), 0.0000001,
                "a cap below 1.0 turns the curve off rather than shrinking the boss");
        }

        [TestMethod]
        public void ScaledStartingValue_IsIdempotentForTheSameMultiplier()
        {
            const uint authoredStarting = 40000u;
            const uint authoredMax = 50000u;

            var once = WorldEventSpawner.ScaledStartingValue(authoredStarting, authoredMax, 2.0);

            Assert.AreEqual(90000u, once, "40000 + (100000 - 50000)");

            Assert.AreEqual(once, WorldEventSpawner.ScaledStartingValue(authoredStarting, authoredMax, 2.0),
                "recomputing from the AUTHORED pair must not compound");

            Assert.AreEqual(authoredStarting, WorldEventSpawner.ScaledStartingValue(authoredStarting, authoredMax, 1.0),
                "a multiplier of 1 is a no-op");
            Assert.AreEqual(authoredStarting, WorldEventSpawner.ScaledStartingValue(authoredStarting, authoredMax, 0.5),
                "a multiplier below 1 never lowers the value");
        }

        // ---- (g) the whole spawn path, through the boss-spawn seam -------------------------------------

        private sealed class OpenObjective : IWorldEventObjective
        {
            public void OnCreatureDied(Creature creature, ACE.Server.Entity.DamageHistoryInfo lastDamager,
                ACE.Server.Entity.DamageHistoryInfo topDamager)
            {
            }

            public void Tick(double now)
            {
            }

            public bool IsComplete => false;

            public string ProgressText => "the champion has not yet appeared";

            public WorldEventMvp Mvp() => WorldEventMvp.None;
        }

        private static BossDef NamedBossDef(uint wcid = 1002619)
        {
            return new BossDef
            {
                Id = "vhaleth",
                DisplayName = "Vhaleth, Matron of the Black Clutch",
                Kind = BossKind.Named,
                NamedWcid = wcid,
                FamilyId = ""
            };
        }

        private const double T0 = 1_700_000_000d;
        private const int TestMinDuration = 300;

        /// <summary>
        /// A run wired for the boss path only: a mutable clock, a mutable audience, and the boss-spawn
        /// seam in place of a landblock. Nothing here constructs a Creature, a Player or a Landblock.
        ///
        /// <paramref name="placementSucceeds"/> is the 2026-08-16 placement-retry seam: every invocation of
        /// the boss spawner is still recorded in <paramref name="spawns"/> (the WHEN/WHAT the pre-existing
        /// tests check), but the seam's own return value - what SpawnChampion reads to decide Placed vs.
        /// RetryTransient - comes from this delegate. Null (every pre-existing test) means "always succeeds",
        /// matching the seam's old always-succeeds Action shape exactly.
        /// </summary>
        private static WorldEvent BossRun(Func<double> clock, Func<AudienceEstimate> audience,
            List<(uint Wcid, double Mult)> spawns, BossDef boss = null, Func<bool> placementSucceeds = null,
            Func<bool> throughputFlag = null)
        {
            var composition = new WorldEventComposition(WaveTheme(),
                new[] { Family(new FamilyMember { Wcid = 1002604, Name = "Drone", Level = 100, Role = 0 }) },
                boss ?? NamedBossDef(),
                new GoalDef
                {
                    Id = "kill_boss",
                    DisplayName = "Slay the Champion",
                    Type = "KillBoss",
                    TypeKind = GoalType.KillBoss,
                    MvpRule = "killingBlowAndTopDamage",
                    RuleKind = MvpRule.KillingBlowAndTopDamage,
                    ProgressTemplate = "The champion still stands."
                },
                new RewardDef
                {
                    Id = "standard",
                    DisplayName = "Crate",
                    SuccessCrateWcid = 1002600,
                    ConsolationCrateWcid = 1002601,
                    CacheWcid = 1002602,
                    ParticipantsPerCache = 8,
                    ClaimWindowSeconds = 120
                },
                new AnchorDef { Id = "here", DisplayName = "the test anchor", CellId = 0x016C019E, BiomeTags = new List<string>() },
                new ACE.Entity.Position(0x016C019E, 10f, 10f, 0f, 0f, 0f, 0f, 1f, 0),
                WorldEventAxisStore.Empty);

            var request = new WorldEventRequest
            {
                SourceId = "ambush",
                FamilyId = "olthoi",
                GoalId = "kill_boss",
                AnnounceLeadSeconds = 0,
                MaxDurationSeconds = 100000,
                MinDurationSeconds = TestMinDuration,
                AbandonAfterSeconds = 100000,
                WipeGraceSeconds = 100000,
                Invoker = "test"
            };

            return new WorldEvent(1, composition, request, new OpenObjective(), audience, clock,
                null, (wcid, mult) =>
                {
                    spawns.Add((wcid, mult));
                    return placementSucceeds == null || placementSucceeds();
                },
                // C16: null is the production read of world_events_boss_throughput_scaling_enabled, which a
                // unit test has no shard config for. Every pre-existing test leaves it null and therefore
                // takes the legacy path, exactly as the shipped default does.
                throughputFlag);
        }

        [TestMethod]
        public void NamedBoss_SpawnsOnceAtTheMinimumDurationMark_AndNotBefore()
        {
            var now = T0;
            var spawns = new List<(uint Wcid, double Mult)>();

            var evt = BossRun(() => now, () => WorldEventRosterSelector.FloorEstimate, spawns);

            evt.Stage(now);
            evt.Tick(now);

            Assert.AreEqual(WorldEventState.Active, evt.State);
            Assert.AreEqual(0, spawns.Count, "nothing at the moment the run goes Active");

            now = T0 + TestMinDuration - 0.1;
            evt.Tick(now);

            Assert.AreEqual(0, spawns.Count, "still nothing one tick before the minimum");

            now = T0 + TestMinDuration;
            evt.Tick(now);

            Assert.AreEqual(1, spawns.Count, "the boss lands on the first tick past the minimum");
            Assert.AreEqual(1002619u, spawns[0].Wcid);

            for (var i = 1; i <= 10; i++)
            {
                now = T0 + TestMinDuration + i;
                evt.Tick(now);
            }

            Assert.AreEqual(1, spawns.Count, "and exactly once, however many ticks follow");
        }

        [TestMethod]
        public void ChampionDue_IsBothNonNoneKinds_AndNeverBeforeTheMinimum()
        {
            Assert.IsFalse(WorldEvent.ChampionDue(BossKind.None, minDurationElapsed: true));
            Assert.IsFalse(WorldEvent.ChampionDue(BossKind.Named, minDurationElapsed: false));
            Assert.IsFalse(WorldEvent.ChampionDue(BossKind.FamilyChampion, minDurationElapsed: false));

            Assert.IsTrue(WorldEvent.ChampionDue(BossKind.Named, minDurationElapsed: true));
            Assert.IsTrue(WorldEvent.ChampionDue(BossKind.FamilyChampion, minDurationElapsed: true));
        }

        [TestMethod]
        public void NamedBoss_HealthMultiplier_IsThePowerCurveAlone()
        {
            var now = T0;
            var spawns = new List<(uint Wcid, double Mult)>();

            // Five level-275 players: powerSum 5.0, so 1 + 0.15 * 5 = 1.75.
            var evt = BossRun(() => now, () => new AudienceEstimate(5, 275, 275, 5.0), spawns);

            evt.Stage(now);
            evt.Tick(now);

            now = T0 + TestMinDuration;
            evt.Tick(now);

            Assert.AreEqual(1, spawns.Count);
            Assert.AreEqual(1.75, spawns[0].Mult, 0.0000001,
                "crowdHealth and the pace health step must not appear in a named boss's multiplier");
            Assert.AreEqual(1.75, evt.BossHealthMult, 0.0000001);
        }

        [TestMethod]
        public void RatchetMult_KeepsTheHighWaterMark()
        {
            // The BOSS-STANDARD sequence: 1.0 -> 1.6 -> 1.3 must leave 1.6.
            var mult = 1.0;

            mult = WorldEvent.RatchetMult(mult, 1.6);
            Assert.AreEqual(1.6, mult, 0.0000001);

            mult = WorldEvent.RatchetMult(mult, 1.3);
            Assert.AreEqual(1.6, mult, 0.0000001, "a shrinking crowd must never shrink the health bar");

            mult = WorldEvent.RatchetMult(mult, 2.4);
            Assert.AreEqual(2.4, mult, 0.0000001);

            Assert.AreEqual(2.4, WorldEvent.RatchetMult(mult, double.NaN), 0.0000001);
            Assert.AreEqual(2.4, WorldEvent.RatchetMult(mult, double.PositiveInfinity), 0.0000001);
        }

        [TestMethod]
        public void BossHealthMult_TracksTheAudienceSampleAndTheBossSpawnsAtIt()
        {
            var now = T0;
            var spawns = new List<(uint Wcid, double Mult)>();

            // powerSum 4.0 at the Stage sample: 1 + 0.15 * 4 = 1.6.
            var evt = BossRun(() => now, () => new AudienceEstimate(4, 275, 275, 4.0), spawns);

            evt.Stage(now);

            Assert.AreEqual(1.6, evt.BossHealthMult, 0.0000001, "the Stage sample already moves it");

            evt.Tick(now);

            now = T0 + TestMinDuration;
            evt.Tick(now);

            Assert.AreEqual(1, spawns.Count);
            Assert.AreEqual(1.6, spawns[0].Mult, 0.0000001, "the boss spawns at the current multiplier");
        }

        /// <summary>
        /// An empty field is floored by SetAudience to one level-50 participant, so the multiplier is the
        /// floor's worth and not a flat 1.0: PowerSum = (50/275)^2 = 4/121, and BossDef.DefaultPerPlayer is
        /// 0.15, so 1 + 0.15 * 4/121 = 1 + 0.6/121 = 1.004958677685950...
        /// </summary>
        [TestMethod]
        public void BossHealthMult_ForAnEmptyFieldIsTheScalingFloorsWorth()
        {
            var now = T0;
            var spawns = new List<(uint Wcid, double Mult)>();

            var evt = BossRun(() => now, () => new AudienceEstimate(0, 0, 0), spawns);

            evt.Stage(now);
            evt.Tick(now);

            now = T0 + TestMinDuration;
            evt.Tick(now);

            var floorPower = WorldEventRosterSelector.FloorEstimate.PowerSum;

            var floorMult = 1.0 + (BossDef.DefaultPerPlayer * floorPower);

            Assert.AreEqual(4d / 121d, floorPower, 0.0000000001, "(50/275)^2");
            Assert.AreEqual(1.0049586776859504, floorMult, 0.0000000001, "the derivation above, spelled out");

            Assert.AreEqual(floorMult, evt.BossHealthMult, 0.0000001);
            Assert.AreEqual(1, spawns.Count);
            Assert.AreEqual(floorMult, spawns[0].Mult, 0.0000001,
                "an empty field is sized for the one floor participant, barely above the authored health");
        }

        [TestMethod]
        public void BossHealthMult_StaysAtOneForAFamilyChampion()
        {
            var now = T0;
            var spawns = new List<(uint Wcid, double Mult)>();

            var evt = BossRun(() => now, () => new AudienceEstimate(5, 275, 275, 5.0), spawns,
                BossDef.FamilyChampion);

            evt.Stage(now);
            evt.Tick(now);

            now = T0 + TestMinDuration;
            evt.Tick(now);

            Assert.AreEqual(1.0, evt.BossHealthMult, 0.0000001,
                "the power curve is named-boss only; a family champion keeps crowd x pace");

            Assert.AreEqual(1, spawns.Count);
            Assert.AreEqual(1002604u, spawns[0].Wcid, "the family champion still comes from the roster");
        }

        // ---- (g1) throughput-scaled boss health (C16, 2026-08-18) --------------------------------------

        /// <summary>The shipped dial set: floor 100000, cap 6400000, target 300 s, calibration 1.0, minSample 120 s.</summary>
        private static BossDef ThroughputBossDef()
        {
            var boss = NamedBossDef();

            boss.ThroughputFloorHealth = 100000;
            boss.ThroughputCapHealth = 6400000;
            boss.TargetKillSeconds = 300.0;
            boss.ThroughputCalibration = 1.0;
            boss.MinSampleSeconds = 120.0;

            return boss;
        }

        /// <summary>
        /// The wave phase, as the metric sees it: <paramref name="hp"/> total health cleared. Fed straight
        /// into the accumulator rather than through a Creature, because nothing here may construct one (D6);
        /// what a real death adds is exactly this - the dead creature's Health.MaxValue.
        /// </summary>
        private static void ClearWavePhase(WorldEvent evt, double hp)
        {
            var chunk = (uint)(hp / 10);

            for (var i = 0; i < 10; i++)
                evt.Throughput.NoteCleared(chunk);
        }

        [TestMethod]
        public void ThroughputScaling_FlagOff_TakesTheLegacyPowerCurve()
        {
            var now = T0;
            var spawns = new List<(uint Wcid, double Mult)>();

            // powerSum 5.0 -> the power curve says 1.75. The measured rate would say 30x, so if the flag
            // leaked at all this assertion could not pass.
            var evt = BossRun(() => now, () => new AudienceEstimate(5, 275, 275, 5.0), spawns,
                ThroughputBossDef(), throughputFlag: () => false);

            evt.Stage(now);
            evt.Tick(now);

            // 3,000,000 HP over the 300 s wave phase = 10,000 HP/s.
            ClearWavePhase(evt, 3000000);

            now = T0 + TestMinDuration;
            evt.Tick(now);

            Assert.AreEqual(1, spawns.Count);
            Assert.AreEqual(1.75, spawns[0].Mult, 0.0000001, "the power curve alone, exactly as before C16");
            Assert.AreEqual(1.75, evt.BossHealthMult, 0.0000001);

            Assert.IsFalse(evt.BossThroughputActive);
            Assert.AreEqual(0u, evt.BossRebaseHealth, "the legacy rebase is bosses.json's baseHealth, which is 0");
            Assert.AreEqual(0.0, evt.BossThroughputHps, 0.0000001);
        }

        [TestMethod]
        public void ThroughputScaling_FlagOn_SizesTheBossFromTheMeasuredRate()
        {
            var now = T0;
            var spawns = new List<(uint Wcid, double Mult)>();

            var evt = BossRun(() => now, () => new AudienceEstimate(5, 275, 275, 5.0), spawns,
                ThroughputBossDef(), throughputFlag: () => true);

            evt.Stage(now);
            evt.Tick(now);

            ClearWavePhase(evt, 3000000);

            now = T0 + TestMinDuration;
            evt.Tick(now);

            Assert.AreEqual(1, spawns.Count);

            // 10,000 HP/s x 1.0 x 300 s = 3,000,000 HP wanted, over a 100,000 floor.
            Assert.AreEqual(30.0, spawns[0].Mult, 0.0000001);
            Assert.AreEqual(30.0, evt.BossHealthMult, 0.0000001,
                "the measured size REPLACES the power curve at spawn, even though it is far above it here");

            Assert.IsTrue(evt.BossThroughputActive);
            Assert.AreEqual(10000.0, evt.BossThroughputHps, 0.0000001);
            Assert.AreEqual(5, evt.BossThroughputCountAtSpawn);
            Assert.AreEqual(100000u, evt.BossRebaseHealth, "the spawner rebases to the floor, not to baseHealth");
        }

        [TestMethod]
        public void ThroughputScaling_FlagOn_CanSizeTheBossBELOWThePowerCurve()
        {
            var now = T0;
            var spawns = new List<(uint Wcid, double Mult)>();

            var evt = BossRun(() => now, () => new AudienceEstimate(5, 275, 275, 5.0), spawns,
                ThroughputBossDef(), throughputFlag: () => true);

            evt.Stage(now);
            evt.Tick(now);

            // 150,000 HP over 300 s = 500 HP/s -> 150,000 HP wanted -> 1.5x the floor, against a power curve
            // that had already ratcheted to 1.75. The ratchet guards a STANDING boss, not the spawn size.
            ClearWavePhase(evt, 150000);

            Assert.AreEqual(1.75, evt.BossHealthMult, 0.0000001, "the power curve moved at the Stage sample");

            now = T0 + TestMinDuration;
            evt.Tick(now);

            Assert.AreEqual(1, spawns.Count);
            Assert.AreEqual(1.5, spawns[0].Mult, 0.0000001, "sized down to what the group actually demonstrated");
            Assert.IsTrue(evt.BossThroughputActive);
        }

        [TestMethod]
        public void ThroughputScaling_FlagOn_ButNoSample_FallsBackToThePowerCurve()
        {
            var now = T0;
            var spawns = new List<(uint Wcid, double Mult)>();

            var evt = BossRun(() => now, () => new AudienceEstimate(5, 275, 275, 5.0), spawns,
                ThroughputBossDef(), throughputFlag: () => true);

            evt.Stage(now);
            evt.Tick(now);

            // Nothing died all run: there is elapsed time but no rate to trust.
            now = T0 + TestMinDuration;
            evt.Tick(now);

            Assert.AreEqual(1, spawns.Count);
            Assert.AreEqual(1.75, spawns[0].Mult, 0.0000001);
            Assert.IsFalse(evt.BossThroughputActive);
            Assert.AreEqual(0u, evt.BossRebaseHealth);
        }

        [TestMethod]
        public void ThroughputScaling_FlagOn_ButTheBossHasNoFloor_FallsBackToThePowerCurve()
        {
            var now = T0;
            var spawns = new List<(uint Wcid, double Mult)>();

            // floor 0 is the C# default: this boss opts out of throughput scaling entirely.
            var evt = BossRun(() => now, () => new AudienceEstimate(5, 275, 275, 5.0), spawns,
                NamedBossDef(), throughputFlag: () => true);

            evt.Stage(now);
            evt.Tick(now);

            ClearWavePhase(evt, 3000000);

            now = T0 + TestMinDuration;
            evt.Tick(now);

            Assert.AreEqual(1, spawns.Count);
            Assert.AreEqual(1.75, spawns[0].Mult, 0.0000001);
            Assert.IsFalse(evt.BossThroughputActive);
        }

        [TestMethod]
        public void ThroughputScaling_FlagOn_ShortWavePhase_FallsBackToThePowerCurve()
        {
            var now = T0;
            var spawns = new List<(uint Wcid, double Mult)>();

            var boss = ThroughputBossDef();

            // A minimum sample longer than the wave phase this run gets: 400 s of window against 300 s of
            // Active time before the boss is due.
            boss.MinSampleSeconds = 400.0;

            var evt = BossRun(() => now, () => new AudienceEstimate(5, 275, 275, 5.0), spawns, boss,
                throughputFlag: () => true);

            evt.Stage(now);
            evt.Tick(now);

            ClearWavePhase(evt, 3000000);

            now = T0 + TestMinDuration;
            evt.Tick(now);

            Assert.AreEqual(1, spawns.Count);
            Assert.AreEqual(1.75, spawns[0].Mult, 0.0000001, "too short a measurement is no measurement");
            Assert.IsFalse(evt.BossThroughputActive);
        }

        [TestMethod]
        public void ThroughputCandidateMult_ScalesWithTheCrowd_AndStaysUnderTheCap()
        {
            var boss = ThroughputBossDef();

            // The re-sample candidate, evaluated in WorldEvent.SetAudience once the boss is standing. The
            // whole run path is not exercised here on purpose: SetAudience is only reached from a wave pick,
            // and TickWaves returns immediately without a held landblock, which a D6 test cannot supply.
            Assert.AreEqual(30.0, WorldEvent.ThroughputCandidateMult(boss, 10000, 5, 5), 0.0000001,
                "the same crowd re-proposes exactly the size it was given");

            Assert.AreEqual(60.0, WorldEvent.ThroughputCandidateMult(boss, 10000, 10, 5), 0.0000001,
                "twice the crowd proposes twice the boss");

            Assert.AreEqual(12.0, WorldEvent.ThroughputCandidateMult(boss, 10000, 2, 5), 0.0000001,
                "a thinner crowd proposes less - RatchetMult is what refuses to apply it");

            // 5,000 HP/s sizes at 15x; a hundred times the crowd would propose 1500x, but the ceiling is
            // 6400000 / 100000 = 64.
            Assert.AreEqual(64.0, WorldEvent.ThroughputCandidateMult(boss, 5000, 100, 1), 0.0000001,
                "throughputCapHealth bounds the late-arrival term too");

            Assert.AreEqual(1.0, WorldEvent.ThroughputCandidateMult(null, 10000, 5, 5), 0.0000001);
        }

        [TestMethod]
        public void RatchetMult_OverTheThroughputCandidate_KeepsTheHighWaterMark()
        {
            var boss = ThroughputBossDef();

            var mult = WorldEvent.ThroughputCandidateMult(boss, 10000, 5, 5);

            Assert.AreEqual(30.0, mult, 0.0000001);

            mult = WorldEvent.RatchetMult(mult, WorldEvent.ThroughputCandidateMult(boss, 10000, 10, 5));

            Assert.AreEqual(60.0, mult, 0.0000001, "the crowd doubled and the boss grew with it");

            mult = WorldEvent.RatchetMult(mult, WorldEvent.ThroughputCandidateMult(boss, 10000, 2, 5));

            Assert.AreEqual(60.0, mult, 0.0000001, "and never shrinks back when the crowd thins");
        }

        // ---- (g2) placement retry, grace expiry, SuccessBossAbsent (2026-08-16) ------------------------

        [TestMethod]
        public void SpawnChampion_FailedPlacement_DoesNotLatch_RetriesOnTheNextTick()
        {
            var now = T0;
            var spawns = new List<(uint Wcid, double Mult)>();

            // Every attempt is refused by the seam - the run never places the boss, so the retry loop must
            // keep calling the seam every Active tick rather than latching after the first failure.
            var evt = BossRun(() => now, () => WorldEventRosterSelector.FloorEstimate, spawns, placementSucceeds: () => false);

            evt.Stage(now);
            evt.Tick(now);

            now = T0 + TestMinDuration;
            evt.Tick(now);

            Assert.AreEqual(1, spawns.Count, "the first attempt at the minimum duration mark");

            now = T0 + TestMinDuration + 1;
            evt.Tick(now);

            Assert.AreEqual(2, spawns.Count, "a second attempt on the very next tick - no latch");

            now = T0 + TestMinDuration + 2;
            evt.Tick(now);

            Assert.AreEqual(3, spawns.Count, "and a third - the seam is invoked again every tick it fails");
            Assert.AreEqual(WorldEventState.Active, evt.State, "still trying; the grace window has not expired");
        }

        [TestMethod]
        public void SpawnChampion_SucceedsAfterRetrying_LatchesAndStopsCalling()
        {
            var now = T0;
            var spawns = new List<(uint Wcid, double Mult)>();
            var attempt = 0;

            // Fails twice, then succeeds - the retry must stop calling the seam once it does.
            var evt = BossRun(() => now, () => WorldEventRosterSelector.FloorEstimate, spawns,
                placementSucceeds: () => ++attempt > 2);

            evt.Stage(now);
            evt.Tick(now);

            now = T0 + TestMinDuration;
            evt.Tick(now);
            now += 1;
            evt.Tick(now);
            now += 1;
            evt.Tick(now);

            Assert.AreEqual(3, spawns.Count, "two failures then a success");

            now += 1;
            evt.Tick(now);

            Assert.AreEqual(3, spawns.Count, "latched - the seam is not called again once placed");
        }

        [TestMethod]
        public void SpawnChampion_GraceExpires_FinishesAsSuccessBossAbsent_PaysTheSuccessCrate()
        {
            var now = T0;
            var spawns = new List<(uint Wcid, double Mult)>();

            var evt = BossRun(() => now, () => WorldEventRosterSelector.FloorEstimate, spawns, placementSucceeds: () => false);

            evt.Stage(now);
            evt.Tick(now);

            now = T0 + TestMinDuration;
            evt.Tick(now);

            Assert.AreEqual(WorldEventState.Active, evt.State, "still retrying at the moment the champion becomes due");

            // Grace runs from championDueAt (== the first due tick, T0 + TestMinDuration), not from now.
            now = T0 + TestMinDuration + WorldEvent.BossPlacementGraceSeconds;
            evt.Tick(now);

            Assert.AreEqual(WorldEventState.Rewarding, evt.State, "the run finishes and opens the claim window");
            Assert.AreEqual(WorldEventOutcome.SuccessBossAbsent, evt.Outcome);

            Assert.IsFalse(WorldEvent.BossWins(evt.Outcome), "SuccessBossAbsent is not a boss win");
            Assert.IsTrue(WorldEvent.PaysSuccessCrate(evt.Outcome), "pays exactly like Success");
        }

        [TestMethod]
        public void SpawnChampion_NoSpawnAnchors_EndsImmediately_NoGraceWait()
        {
            var now = T0;
            var spawns = new List<(uint Wcid, double Mult)>();

            // A null AnchorPosition makes ComputeSpawnAnchors leave spawnAnchors empty (WorldEvent.cs:
            // "anchor == null || theme == null" early-out) regardless of the landblock bridge - a hard
            // failure the retry can never resolve, whether or not the seam is wired.
            var composition = new WorldEventComposition(WaveTheme(),
                new[] { Family(new FamilyMember { Wcid = 1002604, Name = "Drone", Level = 100, Role = 0 }) },
                NamedBossDef(),
                new GoalDef
                {
                    Id = "kill_boss",
                    DisplayName = "Slay the Champion",
                    Type = "KillBoss",
                    TypeKind = GoalType.KillBoss,
                    MvpRule = "killingBlowAndTopDamage",
                    RuleKind = MvpRule.KillingBlowAndTopDamage,
                    ProgressTemplate = "The champion still stands."
                },
                new RewardDef
                {
                    Id = "standard",
                    DisplayName = "Crate",
                    SuccessCrateWcid = 1002600,
                    ConsolationCrateWcid = 1002601,
                    CacheWcid = 1002602,
                    ParticipantsPerCache = 8,
                    ClaimWindowSeconds = 120
                },
                new AnchorDef { Id = "here", DisplayName = "the test anchor", CellId = 0x016C019E, BiomeTags = new List<string>() },
                null,
                WorldEventAxisStore.Empty);

            var request = new WorldEventRequest
            {
                SourceId = "ambush",
                FamilyId = "olthoi",
                GoalId = "kill_boss",
                AnnounceLeadSeconds = 0,
                MaxDurationSeconds = 100000,
                MinDurationSeconds = TestMinDuration,
                AbandonAfterSeconds = 100000,
                WipeGraceSeconds = 100000,
                Invoker = "test"
            };

            var evt = new WorldEvent(1, composition, request, new OpenObjective(),
                () => WorldEventRosterSelector.FloorEstimate, () => now, null, (wcid, mult) =>
                {
                    spawns.Add((wcid, mult));
                    return true;
                });

            evt.Stage(now);
            evt.Tick(now);

            now = T0 + TestMinDuration;
            evt.Tick(now);

            Assert.AreEqual(0, spawns.Count, "the seam is never reached - SpawnChampion returns HardFailure first");
            Assert.AreEqual(WorldEventState.Rewarding, evt.State, "ends on the SAME tick it becomes due, no grace wait");
            Assert.AreEqual(WorldEventOutcome.SuccessBossAbsent, evt.Outcome);
        }

        [TestMethod]
        public void BossPlacementGraceExpired_EdgeCases()
        {
            Assert.IsFalse(WorldEvent.BossPlacementGraceExpired(100, 10, 0), "a zero grace never expires");
            Assert.IsFalse(WorldEvent.BossPlacementGraceExpired(100, 10, -5), "a negative grace never expires");
            Assert.IsFalse(WorldEvent.BossPlacementGraceExpired(100, 0, 60), "dueAt 0 means never actually due");

            Assert.IsFalse(WorldEvent.BossPlacementGraceExpired(69, 10, 60), "one second short");
            Assert.IsTrue(WorldEvent.BossPlacementGraceExpired(70, 10, 60), "exactly at the boundary");
            Assert.IsTrue(WorldEvent.BossPlacementGraceExpired(1000, 10, 60), "long past it");
        }

        [TestMethod]
        public void PaysSuccessCrate_IsSuccessAndSuccessBossAbsentOnly()
        {
            Assert.IsTrue(WorldEvent.PaysSuccessCrate(WorldEventOutcome.Success));
            Assert.IsTrue(WorldEvent.PaysSuccessCrate(WorldEventOutcome.SuccessBossAbsent));

            Assert.IsFalse(WorldEvent.PaysSuccessCrate(WorldEventOutcome.FailedTimeout));
            Assert.IsFalse(WorldEvent.PaysSuccessCrate(WorldEventOutcome.FailedWipe));
            Assert.IsFalse(WorldEvent.PaysSuccessCrate(WorldEventOutcome.FailedNoParticipants));
            Assert.IsFalse(WorldEvent.PaysSuccessCrate(WorldEventOutcome.AbortedAdmin));
            Assert.IsFalse(WorldEvent.PaysSuccessCrate(WorldEventOutcome.AbortedShutdown));
            Assert.IsFalse(WorldEvent.PaysSuccessCrate(WorldEventOutcome.AbortedError));
            Assert.IsFalse(WorldEvent.PaysSuccessCrate(WorldEventOutcome.None));
        }

        [TestMethod]
        public void BossWins_And_IsAborted_ExcludeSuccessBossAbsent()
        {
            Assert.IsFalse(WorldEvent.BossWins(WorldEventOutcome.SuccessBossAbsent));
        }

        [TestMethod]
        public void BossRetryJitterMetres_GrowsByAttempt()
        {
            Assert.AreEqual(2.0f, WorldEventSpawner.BossRetryJitterMetres(0), 0.0000001f);
            Assert.AreEqual(4.0f, WorldEventSpawner.BossRetryJitterMetres(1), 0.0000001f);
            Assert.AreEqual(6.0f, WorldEventSpawner.BossRetryJitterMetres(2), 0.0000001f);
            Assert.AreEqual(8.0f, WorldEventSpawner.BossRetryJitterMetres(3), 0.0000001f);
        }

        [TestMethod]
        public void OutcomeLine_SuccessBossAbsent_NamesTheBossAndNeverShowed()
        {
            var goal = new GoalDef { Id = "kill_boss", DisplayName = "Slay the Champion" };

            var line = WorldEventAnnouncer.OutcomeLine(WorldEventOutcome.SuccessBossAbsent, goal, "the test anchor",
                WorldEventMvp.None, null, 3, "Vhaleth, Matron of the Black Clutch");

            StringAssert.Contains(line, "never showed");
            StringAssert.Contains(line, "Vhaleth, Matron of the Black Clutch");
            StringAssert.Contains(line, "3 defenders took part");
        }

        [TestMethod]
        public void OutcomeLine_SuccessBossAbsent_FallsBackToTheChampionWhenNoBossName()
        {
            var goal = new GoalDef { Id = "kill_boss", DisplayName = "Slay the Champion" };

            var line = WorldEventAnnouncer.OutcomeLine(WorldEventOutcome.SuccessBossAbsent, goal, "the test anchor",
                WorldEventMvp.None, null, 0);

            StringAssert.Contains(line, "the champion never showed");
        }

        // ---- (g3) DecideChampionTick and the async-placement guard (2026-08-16 review fix) -------------

        [TestMethod]
        public void DecideChampionTick_NotDue_WhenKindIsNoneOrTheMinimumHasNotElapsed()
        {
            // Every other input varied - NotDue must win regardless.
            Assert.AreEqual(WorldEvent.ChampionTickDecision.NotDue,
                WorldEvent.DecideChampionTick(bossGuid: 0, placementPending: false, attemptsIssued: 0,
                    minElapsed: true, graceExpired: false, kind: BossKind.None));

            Assert.AreEqual(WorldEvent.ChampionTickDecision.NotDue,
                WorldEvent.DecideChampionTick(bossGuid: 1, placementPending: true, attemptsIssued: 5,
                    minElapsed: true, graceExpired: true, kind: BossKind.None));

            Assert.AreEqual(WorldEvent.ChampionTickDecision.NotDue,
                WorldEvent.DecideChampionTick(bossGuid: 0, placementPending: false, attemptsIssued: 0,
                    minElapsed: false, graceExpired: false, kind: BossKind.Named));

            Assert.AreEqual(WorldEvent.ChampionTickDecision.NotDue,
                WorldEvent.DecideChampionTick(bossGuid: 1, placementPending: true, attemptsIssued: 5,
                    minElapsed: false, graceExpired: true, kind: BossKind.FamilyChampion));
        }

        [TestMethod]
        public void DecideChampionTick_Placed_WheneverBossGuidIsSet_EvenOverPendingOrExpiredGrace()
        {
            // Plain case.
            Assert.AreEqual(WorldEvent.ChampionTickDecision.Placed,
                WorldEvent.DecideChampionTick(bossGuid: 0x80000001, placementPending: false, attemptsIssued: 1,
                    minElapsed: true, graceExpired: false, kind: BossKind.Named));

            // bossGuid != 0 wins over placementPending (the spawner's own pending flag and BossGuid are read
            // in the same tick, not atomically - a boss that is already standing is Placed regardless).
            Assert.AreEqual(WorldEvent.ChampionTickDecision.Placed,
                WorldEvent.DecideChampionTick(bossGuid: 0x80000001, placementPending: true, attemptsIssued: 1,
                    minElapsed: true, graceExpired: false, kind: BossKind.Named));

            // bossGuid != 0 wins over an already-expired grace too.
            Assert.AreEqual(WorldEvent.ChampionTickDecision.Placed,
                WorldEvent.DecideChampionTick(bossGuid: 0x80000001, placementPending: false, attemptsIssued: 9,
                    minElapsed: true, graceExpired: true, kind: BossKind.FamilyChampion));

            // And both together.
            Assert.AreEqual(WorldEvent.ChampionTickDecision.Placed,
                WorldEvent.DecideChampionTick(bossGuid: 0x80000001, placementPending: true, attemptsIssued: 9,
                    minElapsed: true, graceExpired: true, kind: BossKind.Named));
        }

        [TestMethod]
        public void DecideChampionTick_WaitPending_EvenWhenGraceHasExpired()
        {
            // The core review-fix rule: an in-flight attempt is waited out, never abandoned mid-flight, no
            // matter how long the grace window has been open - the queue drains within a tick or two.
            Assert.AreEqual(WorldEvent.ChampionTickDecision.WaitPending,
                WorldEvent.DecideChampionTick(bossGuid: 0, placementPending: true, attemptsIssued: 3,
                    minElapsed: true, graceExpired: false, kind: BossKind.Named));

            Assert.AreEqual(WorldEvent.ChampionTickDecision.WaitPending,
                WorldEvent.DecideChampionTick(bossGuid: 0, placementPending: true, attemptsIssued: 3,
                    minElapsed: true, graceExpired: true, kind: BossKind.FamilyChampion));
        }

        [TestMethod]
        public void DecideChampionTick_GiveUp_OnlyWhenGraceExpiredWithNothingStandingOrInFlight()
        {
            Assert.AreEqual(WorldEvent.ChampionTickDecision.GiveUp,
                WorldEvent.DecideChampionTick(bossGuid: 0, placementPending: false, attemptsIssued: 7,
                    minElapsed: true, graceExpired: true, kind: BossKind.Named));
        }

        [TestMethod]
        public void DecideChampionTick_Retry_WhenNothingIsStandingOrInFlightAndGraceHasNotExpired()
        {
            Assert.AreEqual(WorldEvent.ChampionTickDecision.Retry,
                WorldEvent.DecideChampionTick(bossGuid: 0, placementPending: false, attemptsIssued: 0,
                    minElapsed: true, graceExpired: false, kind: BossKind.Named));

            Assert.AreEqual(WorldEvent.ChampionTickDecision.Retry,
                WorldEvent.DecideChampionTick(bossGuid: 0, placementPending: false, attemptsIssued: 4,
                    minElapsed: true, graceExpired: false, kind: BossKind.FamilyChampion));
        }

        [TestMethod]
        public void AcceptsBossAdopt_TrueOnlyWhenNoBossHasBeenAdoptedYet()
        {
            Assert.IsTrue(WorldEventSpawner.AcceptsBossAdopt(existingBossGuid: 0));

            Assert.IsFalse(WorldEventSpawner.AcceptsBossAdopt(existingBossGuid: 0x80000001));
            Assert.IsFalse(WorldEventSpawner.AcceptsBossAdopt(existingBossGuid: 1));
        }

        /// <summary>
        /// WorldEventSpawner.Adopt (the "second boss adopt refused" guard) cannot be exercised directly: it
        /// is only reachable through SpawnBoss -> Spawn -> landblock.EnqueueAction on a REAL Landblock, and
        /// D6 forbids constructing one in a unit test. AcceptsBossAdopt above is the pure rule Adopt defers
        /// to, and is exhaustively covered there; this only checks the spawner's OWN state - reachable
        /// without a landblock - starts clean, so a fresh run never reads a stale pending/refusal count from
        /// a previous test or a static default.
        /// </summary>
        [TestMethod]
        public void WorldEventSpawner_BossPlacementPendingAndRefusals_StartFalseAndZero()
        {
            var spawner = new WorldEventSpawner(() => 0d);

            Assert.IsFalse(spawner.BossPlacementPending);
            Assert.AreEqual(0, spawner.BossPlacementRefusals);
            Assert.AreEqual(0u, spawner.BossGuid);
        }

        // ---- (h) the progress line names a named boss --------------------------------------------------

        [TestMethod]
        public void KillBossProgress_WithNoName_IsExactlyWhatItAlwaysWas()
        {
            var guid = 0u;

            var objective = new KillBossObjective(null, () => guid);

            Assert.AreEqual("the champion has not yet appeared", objective.ProgressText);

            guid = 0x8000_0001u;

            Assert.AreEqual("The champion still stands.", objective.ProgressText);

            objective.OnDeathCore(guid, "Killer", "Killer");

            Assert.AreEqual("The champion has fallen.", objective.ProgressText);
        }

        [TestMethod]
        public void KillBossProgress_WithANamedBoss_UsesItsName()
        {
            var guid = 0u;

            var objective = new KillBossObjective(null, () => guid, "Vhaleth, Matron of the Black Clutch");

            Assert.AreEqual("Vhaleth, Matron of the Black Clutch has not yet appeared", objective.ProgressText);

            guid = 0x8000_0001u;

            Assert.AreEqual("Vhaleth, Matron of the Black Clutch still stands.", objective.ProgressText);

            objective.OnDeathCore(guid, "Killer", "Killer");

            Assert.AreEqual("Vhaleth, Matron of the Black Clutch has fallen.", objective.ProgressText);
        }

        [TestMethod]
        public void KillBossProgress_ABlankNameFallsBackToTheGenericWording()
        {
            var objective = new KillBossObjective(null, () => 0x8000_0001u, "   ");

            Assert.AreEqual("The champion still stands.", objective.ProgressText);
        }

        // ---- (i) the boss fail line --------------------------------------------------------------------

        private static BossDef BossWithLines(params string[] lines)
        {
            var boss = NamedBossDef();
            boss.FailLines = new List<string>(lines);
            return boss;
        }

        [TestMethod]
        public void BossFailLine_IsNullForEverythingThatHasNothingToSay()
        {
            Assert.IsNull(WorldEventAnnouncer.BossFailLine(null, new Random(1)));
            Assert.IsNull(WorldEventAnnouncer.BossFailLine(BossDef.None, new Random(1)));
            Assert.IsNull(WorldEventAnnouncer.BossFailLine(BossDef.FamilyChampion, new Random(1)));

            Assert.IsNull(WorldEventAnnouncer.BossFailLine(BossWithLines(), new Random(1)),
                "an empty pool says nothing");

            Assert.IsNull(WorldEventAnnouncer.BossFailLine(BossWithLines("", "   ", null), new Random(1)),
                "an all-blank pool says nothing");

            var noList = NamedBossDef();
            noList.FailLines = null;

            Assert.IsNull(WorldEventAnnouncer.BossFailLine(noList, new Random(1)));
        }

        [TestMethod]
        public void BossFailLine_DrawsFromThePool_PrefixedExactlyOnce()
        {
            var pool = new[] { "Line one.", "Line two.", "Line three." };

            var boss = BossWithLines(pool);

            for (var seed = 0; seed < 50; seed++)
            {
                var line = WorldEventAnnouncer.BossFailLine(boss, new Random(seed));

                Assert.IsNotNull(line);
                StringAssert.StartsWith(line, WorldEventAnnouncer.AnnouncePrefix);

                var body = line.Substring(WorldEventAnnouncer.AnnouncePrefix.Length);

                CollectionAssert.Contains(pool, body);
                Assert.IsFalse(body.Contains(WorldEventAnnouncer.AnnouncePrefix), "the prefix must be applied once");
            }
        }

        [TestMethod]
        public void BossFailLine_IsDeterministicForASeededRng()
        {
            var boss = BossWithLines("Line one.", "Line two.", "Line three.");

            for (var seed = 0; seed < 20; seed++)
            {
                Assert.AreEqual(WorldEventAnnouncer.BossFailLine(boss, new Random(seed)),
                    WorldEventAnnouncer.BossFailLine(boss, new Random(seed)), $"seed {seed}");
            }
        }

        [TestMethod]
        public void BossFailLine_SkipsBlanksInAnOtherwiseUsablePool()
        {
            var boss = BossWithLines("", "   ", "The only real line.", null);

            for (var seed = 0; seed < 20; seed++)
            {
                Assert.AreEqual(WorldEventAnnouncer.AnnouncePrefix + "The only real line.",
                    WorldEventAnnouncer.BossFailLine(boss, new Random(seed)));
            }
        }

        [TestMethod]
        public void BossWins_IsTheThreeFailedOutcomesOnly()
        {
            Assert.IsTrue(WorldEvent.BossWins(WorldEventOutcome.FailedTimeout));
            Assert.IsTrue(WorldEvent.BossWins(WorldEventOutcome.FailedWipe));
            Assert.IsTrue(WorldEvent.BossWins(WorldEventOutcome.FailedNoParticipants));

            Assert.IsFalse(WorldEvent.BossWins(WorldEventOutcome.Success), "the boss lost; it says nothing");
            Assert.IsFalse(WorldEvent.BossWins(WorldEventOutcome.None));

            Assert.IsFalse(WorldEvent.BossWins(WorldEventOutcome.AbortedAdmin));
            Assert.IsFalse(WorldEvent.BossWins(WorldEventOutcome.AbortedShutdown));
            Assert.IsFalse(WorldEvent.BossWins(WorldEventOutcome.AbortedError));
        }

        [TestMethod]
        public void EveryShippedBoss_ProducesAFailLine()
        {
            var store = WorldEventAxisStore.Parse(null, null, null, null, null, ReadShippedBosses());

            foreach (var expected in ShippedBosses)
            {
                var line = WorldEventAnnouncer.BossFailLine(store.Bosses[expected.id], new Random(7));

                Assert.IsNotNull(line, expected.id);
                StringAssert.StartsWith(line, WorldEventAnnouncer.AnnouncePrefix, expected.id);
            }
        }

        // ---- (j) the ratchet's apply/skip decision -----------------------------------------------------

        [TestMethod]
        public void BossRatchetReady_RefusesWithoutALandblock()
        {
            // The review case: a boss mid adjacency transfer is ALIVE and has no CurrentLandblock. The
            // answer must be "skip", not "apply inline" - an off-queue vital write is never acceptable.
            Assert.IsFalse(WorldEventSpawner.BossRatchetReady(50000u, 1.6, deadOrDestroyed: false, hasLandblock: false));

            Assert.IsTrue(WorldEventSpawner.BossRatchetReady(50000u, 1.6, deadOrDestroyed: false, hasLandblock: true));
        }

        [TestMethod]
        public void BossRatchetReady_RefusesEveryOtherWayItCanBeUnready()
        {
            Assert.IsFalse(WorldEventSpawner.BossRatchetReady(0u, 1.6, false, true), "no authored base to recompute from");

            Assert.IsFalse(WorldEventSpawner.BossRatchetReady(50000u, 1.0, false, true), "not an increase");
            Assert.IsFalse(WorldEventSpawner.BossRatchetReady(50000u, 0.5, false, true), "a decrease is never applied");
            Assert.IsFalse(WorldEventSpawner.BossRatchetReady(50000u, double.NaN, false, true));
            Assert.IsFalse(WorldEventSpawner.BossRatchetReady(50000u, double.PositiveInfinity, false, true));

            Assert.IsFalse(WorldEventSpawner.BossRatchetReady(50000u, 1.6, deadOrDestroyed: true, hasLandblock: true));
        }

        [TestMethod]
        public void RatchetBossHealth_WithNoAdoptedBoss_IsASilentNoOp()
        {
            // A spawner that has never adopted a boss has nothing to mutate and must not throw. This is
            // as far as a pure test can drive the method itself - the remaining branches all need a live
            // Creature, which D6 forbids a test from building, so the decision they turn on is factored
            // into BossRatchetReady above.
            new WorldEventSpawner(() => 0d).RatchetBossHealth(null, 4.0);
        }

        [TestMethod]
        public void ClampVitalDelta_PinsAtIntMaxValue()
        {
            Assert.AreEqual(0u, WorldEventSpawner.ClampVitalDelta(0u));
            Assert.AreEqual(1000u, WorldEventSpawner.ClampVitalDelta(1000u));

            Assert.AreEqual((uint)int.MaxValue, WorldEventSpawner.ClampVitalDelta((uint)int.MaxValue));

            // Unclamped, these narrow to int.MinValue and -1 and would REMOVE health.
            Assert.AreEqual((uint)int.MaxValue, WorldEventSpawner.ClampVitalDelta(2147483648u));
            Assert.AreEqual((uint)int.MaxValue, WorldEventSpawner.ClampVitalDelta(uint.MaxValue));

            Assert.IsTrue((int)WorldEventSpawner.ClampVitalDelta(uint.MaxValue) > 0,
                "the clamped value must still be positive once it narrows to the int overload");
        }

        // ---- (k) the baseHealth override ---------------------------------------------------------------

        [TestMethod]
        public void RebasedStartingValue_MakesMaxValueEqualBaseHealth()
        {
            // A constructed creature: StartingValue 100, MaxValue 250, so 150 of the total is Ranks + attr
            // and is not ours to set.
            const uint startingValue = 100u;
            const uint maxValue = 250u;
            const uint fixedPart = maxValue - startingValue;

            const uint baseHealth = 800000u;

            var rebased = WorldEventSpawner.RebasedStartingValue(startingValue, maxValue, baseHealth);

            Assert.AreEqual(baseHealth - fixedPart, rebased);
            Assert.AreEqual(baseHealth, rebased + fixedPart, "the resulting MaxValue is exactly baseHealth");
        }

        [TestMethod]
        public void RebasedStartingValue_ZeroMeansNoOverride()
        {
            Assert.AreEqual(100u, WorldEventSpawner.RebasedStartingValue(100u, 250u, 0u));
        }

        [TestMethod]
        public void RebasedStartingValue_FloorsAtZeroWhenTheFixedPartAlreadyExceedsTheTarget()
        {
            // Ranks + attr alone are 150; a baseHealth of 100 cannot be reached from below.
            Assert.AreEqual(0u, WorldEventSpawner.RebasedStartingValue(100u, 250u, 100u));
            Assert.AreEqual(0u, WorldEventSpawner.RebasedStartingValue(100u, 250u, 150u));

            Assert.AreEqual(1u, WorldEventSpawner.RebasedStartingValue(100u, 250u, 151u));
        }

        [TestMethod]
        public void RebasedStartingValue_ToleratesAMaxValueBelowTheStartingValue()
        {
            // Not a shape a constructed creature produces, but the subtraction must not wrap if it ever does.
            Assert.AreEqual(800000u, WorldEventSpawner.RebasedStartingValue(300u, 100u, 800000u));
        }

        [TestMethod]
        public void RebasedStartingValue_ThenScaled_IsBaseHealthTimesTheMultiplier()
        {
            // The two transforms compose in the order Spawn applies them: rebase to the authored base,
            // capture that pair, then multiply.
            const uint startingValue = 100u;
            const uint maxValue = 250u;
            const uint fixedPart = maxValue - startingValue;
            const uint baseHealth = 800000u;

            var rebased = WorldEventSpawner.RebasedStartingValue(startingValue, maxValue, baseHealth);

            // What Spawn captures as the AUTHORED pair after the override.
            var authoredStarting = rebased;
            var authoredMax = rebased + fixedPart;

            var scaled = WorldEventSpawner.ScaledStartingValue(authoredStarting, authoredMax, 1.75);

            Assert.AreEqual(1400000u, scaled + fixedPart, "800000 x 1.75");
        }

        [TestMethod]
        public void Bosses_NonZeroBaseHealth_Parses()
        {
            var store = ParseBosses(@"{ ""bosses"": [ { ""id"": ""heavy"", ""displayName"": ""Heavy"", " +
                                    @"""wcid"": 1002619, ""baseHealth"": 800000 } ] }");

            Assert.AreEqual(3, store.Bosses.Count);
            Assert.AreEqual(800000u, store.Bosses["heavy"].BaseHealth);
            CollectionAssert.AreEqual(Array.Empty<string>(), store.Diagnostics.ToArray());
        }

        [TestMethod]
        public void ShippedBosses_AllLeaveBaseHealthToTheWeenie()
        {
            var store = WorldEventAxisStore.Parse(null, null, null, null, null, ReadShippedBosses());

            foreach (var expected in ShippedBosses)
                Assert.AreEqual(0u, store.Bosses[expected.id].BaseHealth, expected.id);
        }
    }
}
