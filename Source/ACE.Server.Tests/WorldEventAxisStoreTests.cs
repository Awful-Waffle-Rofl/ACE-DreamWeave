using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

using ACE.Server.WorldEvents;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ACE.Server.Tests
{
    /// <summary>
    /// Unit coverage for <see cref="WorldEventAxisStore"/> - DTO deserialization against the shipped
    /// Content/events/axes/*.json files, ScaledCount arithmetic, validation-diagnostic behavior, and the
    /// pure ResolveFolder path-resolution order. No database or server bootstrap is touched.
    /// </summary>
    [TestClass]
    public class WorldEventAxisStoreTests
    {
        // ---- locate the shipped axis files -----------------------------------------------------------

        private static string FindAxesDir()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);

            while (dir != null)
            {
                var candidate = Path.Combine(dir.FullName, "Content", "events", "axes");

                if (Directory.Exists(candidate) && File.Exists(Path.Combine(candidate, "sources.json")))
                    return candidate;

                dir = dir.Parent;
            }

            return null;
        }

        private static (string sources, string families, string goals, string rewards, string anchors) ReadShippedFiles()
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

        /// <summary>
        /// The shipped species/*.json files, in the same order Load reads them. Returns an empty list when
        /// the folder is absent, which is a valid shipped state (species tables are additive).
        /// </summary>
        private static List<(string fileName, string json)> ReadShippedSpecies()
        {
            var dir = FindAxesDir();

            if (dir == null)
                Assert.Inconclusive("Could not locate Content/events/axes by walking up from the test assembly -- skipping.");

            var speciesDir = Path.Combine(dir, "species");

            if (!Directory.Exists(speciesDir))
                return new List<(string, string)>();

            return Directory.GetFiles(speciesDir, "*.json")
                .OrderBy(p => p, StringComparer.Ordinal)
                .Select(p => (Path.GetFileName(p), File.ReadAllText(p)))
                .ToList();
        }

        /// <summary>
        /// bosses.json on its own, so the five-file tuple every other test destructures stays as it was.
        /// </summary>
        private static string ReadShippedBosses()
        {
            var dir = FindAxesDir();

            if (dir == null)
                Assert.Inconclusive("Could not locate Content/events/axes by walking up from the test assembly -- skipping.");

            return File.ReadAllText(Path.Combine(dir, "bosses.json"));
        }

        // ---- (a) shipped files parse cleanly -----------------------------------------------------------

        /// <summary>
        /// The boot path (Load) feeds species/*.json into the same parse; ShippedAxisFiles_ParseCleanly
        /// does not, which is why its "shipped files validate clean" assertion passed for weeks while the
        /// live server logged eight axis WARNs on every single boot. This covers what the server actually
        /// parses.
        /// </summary>
        [TestMethod]
        public void ShippedAxisFilesWithSpecies_ProduceNoWarnings()
        {
            var (sources, families, goals, rewards, anchors) = ReadShippedFiles();

            var store = WorldEventAxisStore.Parse(sources, families, goals, rewards, anchors,
                ReadShippedBosses(), speciesFiles: ReadShippedSpecies());

            // Floor first, because every other assertion in this test is satisfied by an EMPTY parse:
            // zero species files means zero diagnostics and zero notes to iterate, and the test would
            // report green while covering nothing. ReadShippedSpecies returns an empty list for a missing
            // folder rather than failing, so a rename or a broken lookup lands here and not on a red test.
            Assert.AreNotEqual(0, store.SpeciesTables.Count,
                "no species tables were parsed - did ReadShippedSpecies find the species folder?");
            Assert.AreNotEqual(0, store.Notes.Count,
                "no families.json override notes were produced - the shipped content has at least eight, "
                + "so zero means the species files never reached the parse.");

            Assert.AreEqual(0, store.Diagnostics.Count,
                "shipped content must not put anything on the WARN channel: " + string.Join(" | ", store.Diagnostics));

            // Notes are allowed and expected here (each is a families.json display-name override), but
            // every one must be that documented case - anything else has been mislabelled as intentional.
            foreach (var note in store.Notes)
                StringAssert.Contains(note, "also declared in families.json; families.json metadata wins", note);
        }

        [TestMethod]
        public void ShippedAxisFiles_ParseCleanly()
        {
            var (sources, families, goals, rewards, anchors) = ReadShippedFiles();

            var store = WorldEventAxisStore.Parse(sources, families, goals, rewards, anchors, ReadShippedBosses());

            Assert.AreEqual(9, store.Sources.Count,
                "sources.json should ship ambush + rift + sky_rift + weave_spiral + the five element_portal_* themes (WP-21)");
            Assert.AreEqual(11, store.Families.Count, "families.json should ship the testclone smoke family plus the ten boss species");
            Assert.AreEqual(12, store.Bosses.Count, "the two built-in entries plus the ten named bosses from bosses.json");
            Assert.AreEqual(3, store.Goals.Count, "goals.json should ship kill_count, destroy_source, kill_boss");
            Assert.AreEqual(1, store.Rewards.Count, "rewards.json should ship the standard reward");
            Assert.AreEqual(0, store.Anchors.Count, "anchors.json is empty in v1");
            CollectionAssert.AreEqual(Array.Empty<string>(), store.Diagnostics.ToArray(), "shipped files should validate clean");
            Assert.IsFalse(store.IsEmpty);

            Assert.IsTrue(store.Sources.ContainsKey("ambush"));
            Assert.IsTrue(store.Sources.ContainsKey("rift"));
            Assert.AreEqual(ACE.Server.WorldEvents.Defs.SourceGeometry.Edges, store.Sources["ambush"].GeometryKind);
            Assert.AreEqual(ACE.Server.WorldEvents.Defs.SourceGeometry.Ring, store.Sources["rift"].GeometryKind);

            Assert.IsTrue(store.Goals.ContainsKey("kill_count"));
            Assert.AreEqual(ACE.Server.WorldEvents.Defs.GoalType.KillCount, store.Goals["kill_count"].TypeKind);
            Assert.AreEqual(ACE.Server.WorldEvents.Defs.MvpRule.MostKills, store.Goals["kill_count"].RuleKind);

            Assert.IsTrue(store.Bosses.ContainsKey("none"));
            Assert.IsTrue(store.Bosses.ContainsKey("family_champion"));

            // C16: every Named boss ships the throughput dials explicitly, and the round trip must keep them.
            foreach (var boss in store.Bosses.Values)
            {
                if (boss.Kind != ACE.Server.WorldEvents.Defs.BossKind.Named)
                    continue;

                Assert.AreEqual(100000u, boss.ThroughputFloorHealth, $"{boss.Id} throughputFloorHealth");
                Assert.AreEqual(6400000u, boss.ThroughputCapHealth, $"{boss.Id} throughputCapHealth");
                Assert.AreEqual(300.0, boss.TargetKillSeconds, 0.0000001, $"{boss.Id} targetKillSeconds");
                Assert.AreEqual(1.0, boss.ThroughputCalibration, 0.0000001, $"{boss.Id} throughputCalibration");
                Assert.AreEqual(120.0, boss.MinSampleSeconds, 0.0000001, $"{boss.Id} minSampleSeconds");
            }

            // WP-03: the rift theme places the Rift objective creature at every geometry anchor; ambush
            // places nothing. 1002603 is the Rift's reserved wcid (Content/wcid-registry.tsv, WP-08).
            Assert.AreEqual(1002603u, store.Sources["rift"].ObjectiveWcid);
            Assert.AreEqual(0u, store.Sources["ambush"].ObjectiveWcid, "ambush places no objective spawn");

            // WP-14: sky_rift is scenery-only decor, so it declares no objectiveWcid.
            Assert.IsTrue(store.Sources.ContainsKey("sky_rift"));

            // WP-16: sky_rift's waves land anywhere under the stack, resampled every wave, rather than on
            // fixed ring points.
            Assert.AreEqual(ACE.Server.WorldEvents.Defs.SourceGeometry.Disc, store.Sources["sky_rift"].GeometryKind);
            Assert.AreEqual(0u, store.Sources["sky_rift"].ObjectiveWcid, "sky_rift's decor is scenery, not a destroyable");

            // WP-15: weave_spiral is the same shape - decor plus attendants, nothing destroyable.
            Assert.IsTrue(store.Sources.ContainsKey("weave_spiral"));

            // WP-16: weave_spiral's waves step out at the spiral centre through the rigs, not on a ring.
            Assert.AreEqual(ACE.Server.WorldEvents.Defs.SourceGeometry.Single, store.Sources["weave_spiral"].GeometryKind);
            Assert.AreEqual(0u, store.Sources["weave_spiral"].ObjectiveWcid, "weave_spiral's spirals are scenery, not a destroyable");

            // WP-21: the five Element Portal themes, one per element, with fixed-offset pillar objectives
            // rather than an anchor-following objectiveWcid.
            //
            // 2026-08-19: destroy_pillars is RETIRED into destroy_source, and each portal now accepts all
            // three goals in this exact order. Under destroy_source the two pillars are the objective; under
            // kill_count/kill_boss the identical creatures go down as inert scenery
            // (WorldEvent.ObjectiveSpawnKind). The per-source flavour overrides are what keep the
            // announcements and the progress line reading "Shatter the Pillars" / "pillars" off the shared
            // destroy_source goal.
            var elementPortalIds = new[]
            {
                "element_portal_fire", "element_portal_acid", "element_portal_frost",
                "element_portal_lightning", "element_portal_earth"
            };

            foreach (var id in elementPortalIds)
            {
                Assert.IsTrue(store.Sources.ContainsKey(id), $"sources.json should ship '{id}'");
                Assert.AreEqual(0u, store.Sources[id].ObjectiveWcid, $"{id} places its objectives via 'objectives', not objectiveWcid");
                Assert.AreEqual(2, store.Sources[id].Objectives.Count, $"{id} should ship its two flanking pillars");

                CollectionAssert.AreEqual(new[] { "destroy_source", "kill_count", "kill_boss" },
                    store.Sources[id].CompatibleGoals.ToArray(), $"{id} compatibleGoals, in order");

                Assert.AreEqual("Shatter the Pillars", store.Sources[id].GoalDisplayName, $"{id} goalDisplayName");
                Assert.AreEqual("pillar", store.Sources[id].ObjectiveNoun, $"{id} objectiveNoun");
                Assert.AreEqual("pillars", store.Sources[id].ObjectiveNounPlural, $"{id} objectiveNounPlural");
            }

            Assert.IsFalse(store.Goals.ContainsKey("destroy_pillars"),
                "destroy_pillars was retired into destroy_source (2026-08-19)");

            // The rift theme keeps its own wording off the same shared goal, and takes the shipped defaults
            // for the nouns rather than declaring them.
            Assert.AreEqual("Destroy the Rifts", store.Sources["rift"].GoalDisplayName);
            Assert.AreEqual("rift", store.Sources["rift"].ObjectiveNoun);
            Assert.AreEqual("rifts", store.Sources["rift"].ObjectiveNounPlural);

            // Every other theme declares no override at all: the goal's own name stands.
            Assert.IsNull(store.Sources["ambush"].GoalDisplayName);
            Assert.AreEqual("rift", store.Sources["ambush"].ObjectiveNoun);
            Assert.AreEqual("rifts", store.Sources["ambush"].ObjectiveNounPlural);
        }

        // ---- (a2) WP-14 decor round-trip ----------------------------------------------------------------

        /// <summary>
        /// The exact Sky Rift stack approved live 2026-08-15: five layers, largest highest, alternating blue
        /// (1002643) and purple (1002642), all inverted. dz is disc height + 0.455 x scale, which is why it
        /// does not scale linearly with scale - see DecorDef.
        /// </summary>
        [TestMethod]
        public void ShippedSkyRift_DecorRoundTrips_InTableOrder()
        {
            var (sources, families, goals, rewards, anchors) = ReadShippedFiles();

            var store = WorldEventAxisStore.Parse(sources, families, goals, rewards, anchors);

            var decor = store.Sources["sky_rift"].Decor;

            Assert.AreEqual(5, decor.Count, "the Sky Rift stack is five layers");

            var expected = new[]
            {
                (wcid: 1002643u, scale: 100f, speed: 0.8f, dz: 67.7f),
                (wcid: 1002642u, scale: 80f, speed: 0.941f, dz: 58.3f),
                (wcid: 1002643u, scale: 60f, speed: 1.143f, dz: 48.9f),
                (wcid: 1002642u, scale: 40f, speed: 1.455f, dz: 39.5f),
                (wcid: 1002643u, scale: 25f, speed: 2.0f, dz: 32.4f)
            };

            for (var i = 0; i < expected.Length; i++)
            {
                Assert.AreEqual(expected[i].wcid, decor[i].Wcid, $"layer {i} wcid");
                Assert.AreEqual(expected[i].scale, decor[i].Scale, 0.0001f, $"layer {i} scale");
                Assert.AreEqual(expected[i].speed, decor[i].Speed, 0.0001f, $"layer {i} speed");
                Assert.AreEqual(expected[i].dz, decor[i].Dz, 0.0001f, $"layer {i} dz");
                Assert.IsTrue(decor[i].Inverted, $"layer {i} must be inverted - the disc hangs BELOW the origin");
            }

            // Largest highest, and every origin strictly above the one below it.
            for (var i = 1; i < decor.Count; i++)
            {
                Assert.IsTrue(decor[i].Scale < decor[i - 1].Scale, $"layer {i} must be smaller than layer {i - 1}");
                Assert.IsTrue(decor[i].Dz < decor[i - 1].Dz, $"layer {i} must sit lower than layer {i - 1}");
                Assert.IsTrue(decor[i].Speed > decor[i - 1].Speed, $"layer {i} must spin faster than layer {i - 1}");
            }
        }

        // ---- (a3) WP-15 weave_spiral round-trip ---------------------------------------------------------

        /// <summary>
        /// The exact Weave Spiral scene approved live 2026-08-16 (Docs/handoffs/2026-08-16-world-events-
        /// weave-spiral-source.md): FOUR copies of one rig at the centre, quarter-turn apart, at deliberately
        /// unequal speeds so the arms never phase-lock. Four, not eight - eight at 45 degrees mushed together.
        /// </summary>
        [TestMethod]
        public void ShippedWeaveSpiral_DecorRoundTrips_InTableOrder()
        {
            var (sources, families, goals, rewards, anchors) = ReadShippedFiles();

            var store = WorldEventAxisStore.Parse(sources, families, goals, rewards, anchors);

            var decor = store.Sources["weave_spiral"].Decor;

            Assert.AreEqual(4, decor.Count, "the Weave Spiral is four rigs, not eight");

            var expected = new[]
            {
                (yaw: 0f, speed: 0.7f),
                (yaw: 90f, speed: 0.85f),
                (yaw: 180f, speed: 1.0f),
                (yaw: 270f, speed: 1.15f)
            };

            for (var i = 0; i < expected.Length; i++)
            {
                Assert.AreEqual(1002638u, decor[i].Wcid, $"arm {i} wcid");
                Assert.AreEqual(3.5f, decor[i].Scale, 0.0001f, $"arm {i} scale");
                Assert.AreEqual(0f, decor[i].Dz, 0.0001f, $"arm {i} sits at ground level");
                Assert.IsFalse(decor[i].Inverted, $"arm {i} is upright - the spiral is not a sky stack");
                Assert.AreEqual(expected[i].yaw, decor[i].Yaw, 0.0001f, $"arm {i} yaw");
                Assert.AreEqual(expected[i].speed, decor[i].Speed, 0.0001f, $"arm {i} speed");
            }

            // The arms must not share a rate, or they phase-lock and read as one object.
            Assert.AreEqual(4, decor.Select(d => d.Speed).Distinct().Count(), "every arm spins at its own rate");
        }

        /// <summary>
        /// The three Loz attendants: a 4.8 m radius triangle around the centre, each facing inward. yaw is
        /// the polar angle plus 90 (see NpcDef), which is why 90 / 210 / 330 pair with 0 / 120 / 240 degrees.
        /// </summary>
        [TestMethod]
        public void ShippedWeaveSpiral_NpcsRoundTrip_InTableOrder()
        {
            var (sources, families, goals, rewards, anchors) = ReadShippedFiles();

            var store = WorldEventAxisStore.Parse(sources, families, goals, rewards, anchors);

            var npcs = store.Sources["weave_spiral"].Npcs;

            Assert.AreEqual(3, npcs.Count, "three Loz attendants");

            var expected = new[]
            {
                (wcid: 1002639u, dx: 4.8f, dy: 0f, yaw: 90f),
                (wcid: 1002640u, dx: -2.4f, dy: 4.157f, yaw: 210f),
                (wcid: 1002641u, dx: -2.4f, dy: -4.157f, yaw: 330f)
            };

            for (var i = 0; i < expected.Length; i++)
            {
                Assert.AreEqual(expected[i].wcid, npcs[i].Wcid, $"attendant {i} wcid");
                Assert.AreEqual(expected[i].dx, npcs[i].Dx, 0.0001f, $"attendant {i} dx");
                Assert.AreEqual(expected[i].dy, npcs[i].Dy, 0.0001f, $"attendant {i} dy");
                Assert.AreEqual(0f, npcs[i].Dz, 0.0001f, $"attendant {i} stands on the ground");
                Assert.AreEqual(expected[i].yaw, npcs[i].Yaw, 0.0001f, $"attendant {i} yaw");

                var radius = Math.Sqrt(npcs[i].Dx * npcs[i].Dx + npcs[i].Dy * npcs[i].Dy);

                Assert.AreEqual(4.8, radius, 0.005, $"attendant {i} stands on the 4.8 m circle");
            }
        }

        /// <summary>
        /// sky_rift ships no "yaw" key at all - the WP-14 file is untouched by WP-15 - so the default 0 has
        /// to carry it, or five inverted layers would suddenly render turned.
        /// </summary>
        [TestMethod]
        public void ShippedSkyRift_HasNoYawKey_AndDefaultsToZero()
        {
            var (sources, families, goals, rewards, anchors) = ReadShippedFiles();

            var weaveSpiralAt = sources.IndexOf("weave_spiral", StringComparison.Ordinal);

            Assert.IsTrue(weaveSpiralAt > 0, "weave_spiral must be the LAST theme in the file for this check to mean anything");

            Assert.IsFalse(sources.Substring(0, weaveSpiralAt).Contains("\"yaw\""),
                "no yaw key may appear before the weave_spiral entry - sky_rift must rely on the default");

            var store = WorldEventAxisStore.Parse(sources, families, goals, rewards, anchors);

            foreach (var entry in store.Sources["sky_rift"].Decor)
                Assert.AreEqual(0f, entry.Yaw, 0.0001f, "an omitted yaw must default to 0");
        }

        [TestMethod]
        public void ShippedThemes_WithoutNpcs_HaveAnEmptyList()
        {
            var (sources, families, goals, rewards, anchors) = ReadShippedFiles();

            var store = WorldEventAxisStore.Parse(sources, families, goals, rewards, anchors);

            Assert.AreEqual(0, store.Sources["ambush"].Npcs.Count, "ambush declares no npcs");
            Assert.AreEqual(0, store.Sources["rift"].Npcs.Count, "rift declares no npcs");
            Assert.AreEqual(0, store.Sources["sky_rift"].Npcs.Count, "sky_rift declares no npcs");
        }

        [TestMethod]
        public void Sources_OmittedNpcs_DefaultsToEmptyList()
        {
            const string sources = @"{ ""sources"": [ {
                ""id"": ""plain"", ""displayName"": ""Plain"", ""geometry"": ""single"",
                ""geometryRadius"": 1.0, ""geometryPoints"": 1, ""waveIntervalSeconds"": 30.0,
                ""maxAlive"": 5, ""waveCount"": { ""base"": 1, ""perParticipant"": 0, ""cap"": 5 },
                ""holdAdjacentLandblocks"": false, ""rewardRadius"": 10.0, ""compatibleGoals"": []
            } ] }";

            var store = WorldEventAxisStore.Parse(sources, null, null, null, null);

            Assert.IsNotNull(store.Sources["plain"].Npcs, "a missing npcs key must never produce null");
            Assert.AreEqual(0, store.Sources["plain"].Npcs.Count);
        }

        [TestMethod]
        public void Sources_NullNpcs_DefaultsToEmptyList()
        {
            const string sources = @"{ ""sources"": [ {
                ""id"": ""plain"", ""displayName"": ""Plain"", ""geometry"": ""single"",
                ""geometryRadius"": 1.0, ""geometryPoints"": 1, ""waveIntervalSeconds"": 30.0,
                ""maxAlive"": 5, ""waveCount"": { ""base"": 1, ""perParticipant"": 0, ""cap"": 5 },
                ""holdAdjacentLandblocks"": false, ""rewardRadius"": 10.0, ""compatibleGoals"": [],
                ""npcs"": null
            } ] }";

            var store = WorldEventAxisStore.Parse(sources, null, null, null, null);

            Assert.IsNotNull(store.Sources["plain"].Npcs, "an explicit null npcs key must never produce null");
            Assert.AreEqual(0, store.Sources["plain"].Npcs.Count);
        }

        [TestMethod]
        public void BadNpcEntry_IsDropped_ThemeKept()
        {
            const string sources = @"{ ""sources"": [ {
                ""id"": ""plain"", ""displayName"": ""Plain"", ""geometry"": ""single"",
                ""geometryRadius"": 1.0, ""geometryPoints"": 1, ""waveIntervalSeconds"": 30.0,
                ""maxAlive"": 5, ""waveCount"": { ""base"": 1, ""perParticipant"": 0, ""cap"": 5 },
                ""holdAdjacentLandblocks"": false, ""rewardRadius"": 10.0, ""compatibleGoals"": [],
                ""npcs"": [
                  { ""wcid"": 0,       ""dx"": 4.8,   ""dy"": 0.0,   ""dz"": 0.0, ""yaw"": 90.0 },
                  { ""wcid"": 1002640, ""dx"": -2.4,  ""dy"": 4.157, ""dz"": 0.0, ""yaw"": ""NaN"" },
                  { ""wcid"": 1002641, ""dx"": -2.4,  ""dy"": -4.157, ""dz"": 0.0, ""yaw"": 330.0 }
                ]
            } ] }";

            var store = WorldEventAxisStore.Parse(sources, null, null, null, null);

            Assert.IsTrue(store.Sources.ContainsKey("plain"), "a theme with a bad npc entry must be KEPT");

            var npcs = store.Sources["plain"].Npcs;

            Assert.AreEqual(1, npcs.Count, "only the valid entry survives");
            Assert.AreEqual(1002641u, npcs[0].Wcid);
            Assert.AreEqual(330f, npcs[0].Yaw, 0.0001f);

            Assert.AreEqual(2, store.Diagnostics.Count(d => d.Contains("plain") && d.Contains("npc")),
                "one diagnostic per dropped entry, each naming the source and the axis field");
        }

        [TestMethod]
        public void BadDecorYaw_DropsTheEntry_ThemeKept()
        {
            const string sources = @"{ ""sources"": [ {
                ""id"": ""plain"", ""displayName"": ""Plain"", ""geometry"": ""single"",
                ""geometryRadius"": 1.0, ""geometryPoints"": 1, ""waveIntervalSeconds"": 30.0,
                ""maxAlive"": 5, ""waveCount"": { ""base"": 1, ""perParticipant"": 0, ""cap"": 5 },
                ""holdAdjacentLandblocks"": false, ""rewardRadius"": 10.0, ""compatibleGoals"": [],
                ""decor"": [
                  { ""wcid"": 1002638, ""scale"": 3.5, ""speed"": 0.7, ""dz"": 0.0, ""yaw"": ""NaN"" },
                  { ""wcid"": 1002638, ""scale"": 3.5, ""speed"": 0.85, ""dz"": 0.0, ""yaw"": -720.0 }
                ]
            } ] }";

            var store = WorldEventAxisStore.Parse(sources, null, null, null, null);

            Assert.IsTrue(store.Sources.ContainsKey("plain"));

            var decor = store.Sources["plain"].Decor;

            Assert.AreEqual(1, decor.Count, "the NaN yaw entry is dropped");
            Assert.AreEqual(-720f, decor[0].Yaw, 0.0001f, "a yaw outside [0, 360) is legal and is NOT normalised");

            Assert.AreEqual(1, store.Diagnostics.Count(d => d.Contains("plain") && d.Contains("decor")));
        }

        [TestMethod]
        public void ShippedThemes_WithoutDecor_HaveAnEmptyList()
        {
            var (sources, families, goals, rewards, anchors) = ReadShippedFiles();

            var store = WorldEventAxisStore.Parse(sources, families, goals, rewards, anchors);

            Assert.AreEqual(0, store.Sources["ambush"].Decor.Count, "ambush declares no decor");
            Assert.AreEqual(0, store.Sources["rift"].Decor.Count, "rift declares no decor");
        }

        [TestMethod]
        public void Sources_OmittedDecor_DefaultsToEmptyList()
        {
            const string sources = @"{ ""sources"": [ {
                ""id"": ""plain"", ""displayName"": ""Plain"", ""geometry"": ""single"",
                ""geometryRadius"": 1.0, ""geometryPoints"": 1, ""waveIntervalSeconds"": 30.0,
                ""maxAlive"": 5, ""waveCount"": { ""base"": 1, ""perParticipant"": 0, ""cap"": 5 },
                ""holdAdjacentLandblocks"": false, ""rewardRadius"": 10.0, ""compatibleGoals"": []
            } ] }";

            var store = WorldEventAxisStore.Parse(sources, null, null, null, null);

            Assert.IsNotNull(store.Sources["plain"].Decor, "a missing decor key must never produce null");
            Assert.AreEqual(0, store.Sources["plain"].Decor.Count);
        }

        [TestMethod]
        public void Sources_NullDecor_DefaultsToEmptyList()
        {
            const string sources = @"{ ""sources"": [ {
                ""id"": ""plain"", ""displayName"": ""Plain"", ""geometry"": ""single"",
                ""geometryRadius"": 1.0, ""geometryPoints"": 1, ""waveIntervalSeconds"": 30.0,
                ""maxAlive"": 5, ""waveCount"": { ""base"": 1, ""perParticipant"": 0, ""cap"": 5 },
                ""holdAdjacentLandblocks"": false, ""rewardRadius"": 10.0, ""compatibleGoals"": [],
                ""decor"": null
            } ] }";

            var store = WorldEventAxisStore.Parse(sources, null, null, null, null);

            Assert.IsNotNull(store.Sources["plain"].Decor, "an explicit null decor key must never produce null");
            Assert.AreEqual(0, store.Sources["plain"].Decor.Count);
        }

        [TestMethod]
        public void BadDecorEntry_IsDropped_ThemeKept()
        {
            const string sources = @"{ ""sources"": [ {
                ""id"": ""plain"", ""displayName"": ""Plain"", ""geometry"": ""single"",
                ""geometryRadius"": 1.0, ""geometryPoints"": 1, ""waveIntervalSeconds"": 30.0,
                ""maxAlive"": 5, ""waveCount"": { ""base"": 1, ""perParticipant"": 0, ""cap"": 5 },
                ""holdAdjacentLandblocks"": false, ""rewardRadius"": 10.0, ""compatibleGoals"": [],
                ""decor"": [
                  { ""wcid"": 0,       ""scale"": 100.0, ""speed"": 0.8, ""dz"": 67.7, ""inverted"": true },
                  { ""wcid"": 1002643, ""scale"": 0.0,   ""speed"": 0.8, ""dz"": 58.3, ""inverted"": true },
                  { ""wcid"": 1002642, ""scale"": 80.0,  ""speed"": 0.0, ""dz"": 48.9, ""inverted"": true },
                  { ""wcid"": 1002643, ""scale"": 25.0,  ""speed"": 2.0, ""dz"": 32.4, ""inverted"": false }
                ]
            } ] }";

            var store = WorldEventAxisStore.Parse(sources, null, null, null, null);

            Assert.IsTrue(store.Sources.ContainsKey("plain"), "a theme with a bad decor entry must be KEPT");

            var decor = store.Sources["plain"].Decor;

            Assert.AreEqual(1, decor.Count, "only the valid entry survives");
            Assert.AreEqual(1002643u, decor[0].Wcid);
            Assert.IsFalse(decor[0].Inverted);

            Assert.AreEqual(3, store.Diagnostics.Count(d => d.Contains("plain") && d.Contains("decor")),
                "one diagnostic per dropped entry, each naming the source and the axis field");
        }

        [TestMethod]
        public void Sources_OmittedObjectiveWcid_DefaultsToZero()
        {
            const string sources = @"{ ""sources"": [ {
                ""id"": ""plain"", ""displayName"": ""Plain"", ""geometry"": ""single"",
                ""geometryRadius"": 1.0, ""geometryPoints"": 1, ""waveIntervalSeconds"": 30.0,
                ""maxAlive"": 5, ""waveCount"": { ""base"": 1, ""perParticipant"": 0, ""cap"": 5 },
                ""holdAdjacentLandblocks"": false, ""rewardRadius"": 10.0, ""compatibleGoals"": []
            } ] }";

            var store = WorldEventAxisStore.Parse(sources, null, null, null, null);

            Assert.AreEqual(1, store.Sources.Count);
            Assert.AreEqual(0u, store.Sources["plain"].ObjectiveWcid,
                "a theme that does not declare objectiveWcid must place no objective spawn");
        }

        // ---- WP-21 decor dx/dy/pitch validation --------------------------------------------------------

        [TestMethod]
        public void BadDecorDxOrPitch_DropsTheEntry_ThemeKept()
        {
            const string sources = @"{ ""sources"": [ {
                ""id"": ""plain"", ""displayName"": ""Plain"", ""geometry"": ""single"",
                ""geometryRadius"": 1.0, ""geometryPoints"": 1, ""waveIntervalSeconds"": 30.0,
                ""maxAlive"": 5, ""waveCount"": { ""base"": 1, ""perParticipant"": 0, ""cap"": 5 },
                ""holdAdjacentLandblocks"": false, ""rewardRadius"": 10.0, ""compatibleGoals"": [],
                ""decor"": [
                  { ""wcid"": 1002638, ""scale"": 3.5, ""speed"": 0.7, ""dz"": 0.0, ""dx"": ""NaN"" },
                  { ""wcid"": 1002638, ""scale"": 3.5, ""speed"": 0.7, ""dz"": 0.0, ""pitch"": ""NaN"" },
                  { ""wcid"": 1002638, ""scale"": 3.5, ""speed"": 0.7, ""dz"": 0.0, ""dx"": 1.5, ""pitch"": 90.0 }
                ]
            } ] }";

            var store = WorldEventAxisStore.Parse(sources, null, null, null, null);

            Assert.IsTrue(store.Sources.ContainsKey("plain"), "a theme with a bad decor entry must be KEPT");

            var decor = store.Sources["plain"].Decor;

            Assert.AreEqual(1, decor.Count, "only the valid entry survives");
            Assert.AreEqual(1.5f, decor[0].Dx, 0.0001f);
            Assert.AreEqual(90f, decor[0].Pitch, 0.0001f);

            Assert.AreEqual(2, store.Diagnostics.Count(d => d.Contains("plain") && d.Contains("decor")),
                "one diagnostic per dropped entry");
        }

        // ---- WP-25 decorStyles validation -----------------------------------------------------------------

        [TestMethod]
        public void ShippedSkyRift_DecorStyles_RoundTrip()
        {
            var (sources, families, goals, rewards, anchors) = ReadShippedFiles();

            var store = WorldEventAxisStore.Parse(sources, families, goals, rewards, anchors, ReadShippedBosses());

            var styles = store.Sources["sky_rift"].DecorStyles;

            Assert.AreEqual(4, styles.Count, "sky_rift ships the shipped Blue/Purple, Green/Yellow, Red/Orange, Grey/Purple styles");

            foreach (var style in styles)
                Assert.AreEqual(2, style.Count, "sky_rift's decor has two distinct colors, so every style must supply two");

            CollectionAssert.AreEqual(new uint[] { 1002643, 1002642 }, styles[0], "style 0 is the shipped Blue/Purple look");
        }

        [TestMethod]
        public void Sources_OmittedDecorStyles_DefaultsToEmptyList()
        {
            const string sources = @"{ ""sources"": [ {
                ""id"": ""plain"", ""displayName"": ""Plain"", ""geometry"": ""single"",
                ""geometryRadius"": 1.0, ""geometryPoints"": 1, ""waveIntervalSeconds"": 30.0,
                ""maxAlive"": 5, ""waveCount"": { ""base"": 1, ""perParticipant"": 0, ""cap"": 5 },
                ""holdAdjacentLandblocks"": false, ""rewardRadius"": 10.0, ""compatibleGoals"": [],
                ""decor"": [
                  { ""wcid"": 1002643, ""scale"": 100.0, ""speed"": 0.8, ""dz"": 67.7, ""inverted"": true },
                  { ""wcid"": 1002642, ""scale"": 80.0,  ""speed"": 0.9, ""dz"": 58.3, ""inverted"": true }
                ]
            } ] }";

            var store = WorldEventAxisStore.Parse(sources, null, null, null, null);

            Assert.IsNotNull(store.Sources["plain"].DecorStyles, "a missing decorStyles key must never produce null");
            Assert.AreEqual(0, store.Sources["plain"].DecorStyles.Count);
        }

        [TestMethod]
        public void BadDecorStylesEntry_WrongLength_IsDropped_ValidSiblingKept_ThemeKept()
        {
            const string sources = @"{ ""sources"": [ {
                ""id"": ""plain"", ""displayName"": ""Plain"", ""geometry"": ""single"",
                ""geometryRadius"": 1.0, ""geometryPoints"": 1, ""waveIntervalSeconds"": 30.0,
                ""maxAlive"": 5, ""waveCount"": { ""base"": 1, ""perParticipant"": 0, ""cap"": 5 },
                ""holdAdjacentLandblocks"": false, ""rewardRadius"": 10.0, ""compatibleGoals"": [],
                ""decor"": [
                  { ""wcid"": 1002643, ""scale"": 100.0, ""speed"": 0.8, ""dz"": 67.7, ""inverted"": true },
                  { ""wcid"": 1002642, ""scale"": 80.0,  ""speed"": 0.9, ""dz"": 58.3, ""inverted"": true }
                ],
                ""decorStyles"": [
                  [1002645, 1002644],
                  [1002647]
                ]
            } ] }";

            var store = WorldEventAxisStore.Parse(sources, null, null, null, null);

            Assert.IsTrue(store.Sources.ContainsKey("plain"), "a theme with a bad decorStyles entry must be KEPT");

            var styles = store.Sources["plain"].DecorStyles;

            Assert.AreEqual(1, styles.Count, "only the correctly-sized style survives");
            CollectionAssert.AreEqual(new uint[] { 1002645, 1002644 }, styles[0]);

            Assert.AreEqual(1, store.Diagnostics.Count(d => d.Contains("plain") && d.Contains("decorStyles")),
                "one diagnostic for the dropped entry");
        }

        [TestMethod]
        public void DecorStylesEntry_ZeroWcid_IsDropped_ThemeKept()
        {
            const string sources = @"{ ""sources"": [ {
                ""id"": ""plain"", ""displayName"": ""Plain"", ""geometry"": ""single"",
                ""geometryRadius"": 1.0, ""geometryPoints"": 1, ""waveIntervalSeconds"": 30.0,
                ""maxAlive"": 5, ""waveCount"": { ""base"": 1, ""perParticipant"": 0, ""cap"": 5 },
                ""holdAdjacentLandblocks"": false, ""rewardRadius"": 10.0, ""compatibleGoals"": [],
                ""decor"": [
                  { ""wcid"": 1002643, ""scale"": 100.0, ""speed"": 0.8, ""dz"": 67.7, ""inverted"": true },
                  { ""wcid"": 1002642, ""scale"": 80.0,  ""speed"": 0.9, ""dz"": 58.3, ""inverted"": true }
                ],
                ""decorStyles"": [
                  [1002645, 0],
                  [1002647, 1002646]
                ]
            } ] }";

            var store = WorldEventAxisStore.Parse(sources, null, null, null, null);

            Assert.IsTrue(store.Sources.ContainsKey("plain"), "a theme with a zero-wcid decorStyles entry must be KEPT");

            var styles = store.Sources["plain"].DecorStyles;

            Assert.AreEqual(1, styles.Count, "only the entry with no 0 wcid survives");
            CollectionAssert.AreEqual(new uint[] { 1002647, 1002646 }, styles[0]);

            Assert.AreEqual(1, store.Diagnostics.Count(d => d.Contains("plain") && d.Contains("decorStyles")),
                "one diagnostic for the dropped entry");
        }

        [TestMethod]
        public void DecorStylesEntry_DuplicateWcidsWithinStyle_IsLegalAndKept()
        {
            const string sources = @"{ ""sources"": [ {
                ""id"": ""plain"", ""displayName"": ""Plain"", ""geometry"": ""single"",
                ""geometryRadius"": 1.0, ""geometryPoints"": 1, ""waveIntervalSeconds"": 30.0,
                ""maxAlive"": 5, ""waveCount"": { ""base"": 1, ""perParticipant"": 0, ""cap"": 5 },
                ""holdAdjacentLandblocks"": false, ""rewardRadius"": 10.0, ""compatibleGoals"": [],
                ""decor"": [
                  { ""wcid"": 1002643, ""scale"": 100.0, ""speed"": 0.8, ""dz"": 67.7, ""inverted"": true },
                  { ""wcid"": 1002642, ""scale"": 80.0,  ""speed"": 0.9, ""dz"": 58.3, ""inverted"": true }
                ],
                ""decorStyles"": [
                  [1002648, 1002648]
                ]
            } ] }";

            var store = WorldEventAxisStore.Parse(sources, null, null, null, null);

            var styles = store.Sources["plain"].DecorStyles;

            Assert.AreEqual(1, styles.Count, "a monochrome style (duplicate wcids within one entry) is legitimate");
            CollectionAssert.AreEqual(new uint[] { 1002648, 1002648 }, styles[0]);
        }

        [TestMethod]
        public void DecorStyles_NoDecor_AllDropped_OneDiagnostic_ThemeKept()
        {
            const string sources = @"{ ""sources"": [ {
                ""id"": ""plain"", ""displayName"": ""Plain"", ""geometry"": ""single"",
                ""geometryRadius"": 1.0, ""geometryPoints"": 1, ""waveIntervalSeconds"": 30.0,
                ""maxAlive"": 5, ""waveCount"": { ""base"": 1, ""perParticipant"": 0, ""cap"": 5 },
                ""holdAdjacentLandblocks"": false, ""rewardRadius"": 10.0, ""compatibleGoals"": [],
                ""decorStyles"": [
                  [1002645, 1002644],
                  [1002647, 1002646]
                ]
            } ] }";

            var store = WorldEventAxisStore.Parse(sources, null, null, null, null);

            Assert.IsTrue(store.Sources.ContainsKey("plain"), "a theme with decorStyles but no decor must be KEPT");

            Assert.AreEqual(0, store.Sources["plain"].DecorStyles.Count, "styles cannot apply to no decor");

            Assert.AreEqual(1, store.Diagnostics.Count(d => d.Contains("plain") && d.Contains("decorStyles")),
                "one diagnostic for the whole dropped list, not one per entry");
        }

        // ---- WP-21 objectives validation ---------------------------------------------------------------

        [TestMethod]
        public void Sources_OmittedObjectives_DefaultsToEmptyList()
        {
            const string sources = @"{ ""sources"": [ {
                ""id"": ""plain"", ""displayName"": ""Plain"", ""geometry"": ""single"",
                ""geometryRadius"": 1.0, ""geometryPoints"": 1, ""waveIntervalSeconds"": 30.0,
                ""maxAlive"": 5, ""waveCount"": { ""base"": 1, ""perParticipant"": 0, ""cap"": 5 },
                ""holdAdjacentLandblocks"": false, ""rewardRadius"": 10.0, ""compatibleGoals"": []
            } ] }";

            var store = WorldEventAxisStore.Parse(sources, null, null, null, null);

            Assert.IsNotNull(store.Sources["plain"].Objectives, "a missing objectives key must never produce null");
            Assert.AreEqual(0, store.Sources["plain"].Objectives.Count);
        }

        [TestMethod]
        public void BadObjectiveEntry_IsDropped_ThemeKept()
        {
            const string sources = @"{ ""sources"": [ {
                ""id"": ""plain"", ""displayName"": ""Plain"", ""geometry"": ""single"",
                ""geometryRadius"": 1.0, ""geometryPoints"": 1, ""waveIntervalSeconds"": 30.0,
                ""maxAlive"": 5, ""waveCount"": { ""base"": 1, ""perParticipant"": 0, ""cap"": 5 },
                ""holdAdjacentLandblocks"": false, ""rewardRadius"": 10.0, ""compatibleGoals"": [],
                ""objectives"": [
                  { ""wcid"": 0,       ""dx"": 4.0,  ""dy"": 0.0, ""dz"": 0.0, ""yaw"": 0.0 },
                  { ""wcid"": 1002700, ""dx"": -4.0, ""dy"": 0.0, ""dz"": 0.0, ""yaw"": ""NaN"" },
                  { ""wcid"": 1002701, ""dx"": 4.0,  ""dy"": 0.0, ""dz"": 0.0, ""yaw"": 180.0 }
                ]
            } ] }";

            var store = WorldEventAxisStore.Parse(sources, null, null, null, null);

            Assert.IsTrue(store.Sources.ContainsKey("plain"), "a theme with a bad objective entry must be KEPT");

            var objectives = store.Sources["plain"].Objectives;

            Assert.AreEqual(1, objectives.Count, "only the valid entry survives");
            Assert.AreEqual(1002701u, objectives[0].Wcid);
            Assert.AreEqual(180f, objectives[0].Yaw, 0.0001f);

            Assert.AreEqual(2, store.Diagnostics.Count(d => d.Contains("plain") && d.Contains("objective entry")),
                "one diagnostic per dropped entry");
        }

        [TestMethod]
        public void ObjectiveWcidAndObjectives_BothDeclared_EmitsDiagnostic_KeepsBoth()
        {
            const string sources = @"{ ""sources"": [ {
                ""id"": ""plain"", ""displayName"": ""Plain"", ""geometry"": ""single"",
                ""geometryRadius"": 1.0, ""geometryPoints"": 1, ""waveIntervalSeconds"": 30.0,
                ""maxAlive"": 5, ""waveCount"": { ""base"": 1, ""perParticipant"": 0, ""cap"": 5 },
                ""holdAdjacentLandblocks"": false, ""rewardRadius"": 10.0, ""compatibleGoals"": [],
                ""objectiveWcid"": 1002603,
                ""objectives"": [
                  { ""wcid"": 1002701, ""dx"": 4.0, ""dy"": 0.0, ""dz"": 0.0, ""yaw"": 180.0 }
                ]
            } ] }";

            var store = WorldEventAxisStore.Parse(sources, null, null, null, null);

            Assert.IsTrue(store.Sources.ContainsKey("plain"));
            Assert.AreEqual(1002603u, store.Sources["plain"].ObjectiveWcid, "objectiveWcid must be kept, not cleared");
            Assert.AreEqual(1, store.Sources["plain"].Objectives.Count, "objectives must be kept, not cleared");

            Assert.IsTrue(store.Diagnostics.Any(d => d.Contains("plain") && d.Contains("objectiveWcid") && d.Contains("objectives")),
                "must warn that both were declared");
        }

        // ---- WP-21 objectiveHealth validation -----------------------------------------------------------

        [TestMethod]
        public void ObjectiveHealth_Omitted_IsNull()
        {
            const string sources = @"{ ""sources"": [ {
                ""id"": ""plain"", ""displayName"": ""Plain"", ""geometry"": ""single"",
                ""geometryRadius"": 1.0, ""geometryPoints"": 1, ""waveIntervalSeconds"": 30.0,
                ""maxAlive"": 5, ""waveCount"": { ""base"": 1, ""perParticipant"": 0, ""cap"": 5 },
                ""holdAdjacentLandblocks"": false, ""rewardRadius"": 10.0, ""compatibleGoals"": []
            } ] }";

            var store = WorldEventAxisStore.Parse(sources, null, null, null, null);

            Assert.IsNull(store.Sources["plain"].ObjectiveHealth);
        }

        [TestMethod]
        public void ObjectiveHealth_CapLessThanBase_IsDropped_ThemeKept()
        {
            const string sources = @"{ ""sources"": [ {
                ""id"": ""plain"", ""displayName"": ""Plain"", ""geometry"": ""single"",
                ""geometryRadius"": 1.0, ""geometryPoints"": 1, ""waveIntervalSeconds"": 30.0,
                ""maxAlive"": 5, ""waveCount"": { ""base"": 1, ""perParticipant"": 0, ""cap"": 5 },
                ""holdAdjacentLandblocks"": false, ""rewardRadius"": 10.0, ""compatibleGoals"": [],
                ""objectiveHealth"": { ""base"": 8000, ""perParticipant"": 2500, ""cap"": 1000 }
            } ] }";

            var store = WorldEventAxisStore.Parse(sources, null, null, null, null);

            Assert.IsTrue(store.Sources.ContainsKey("plain"), "a theme with a bad objectiveHealth must be KEPT");
            Assert.IsNull(store.Sources["plain"].ObjectiveHealth, "cap < base must drop the whole field");
            Assert.IsTrue(store.Diagnostics.Any(d => d.Contains("plain") && d.Contains("objectiveHealth")));
        }

        [TestMethod]
        public void ObjectiveHealth_Valid_RoundTrips()
        {
            const string sources = @"{ ""sources"": [ {
                ""id"": ""plain"", ""displayName"": ""Plain"", ""geometry"": ""single"",
                ""geometryRadius"": 1.0, ""geometryPoints"": 1, ""waveIntervalSeconds"": 30.0,
                ""maxAlive"": 5, ""waveCount"": { ""base"": 1, ""perParticipant"": 0, ""cap"": 5 },
                ""holdAdjacentLandblocks"": false, ""rewardRadius"": 10.0, ""compatibleGoals"": [],
                ""objectiveHealth"": { ""base"": 8000, ""perParticipant"": 2500, ""cap"": 80000 }
            } ] }";

            var store = WorldEventAxisStore.Parse(sources, null, null, null, null);

            var objectiveHealth = store.Sources["plain"].ObjectiveHealth;

            Assert.IsNotNull(objectiveHealth);
            Assert.AreEqual(8000, objectiveHealth.Resolve(0));
            Assert.AreEqual(13000, objectiveHealth.Resolve(2));
            Assert.AreEqual(80000, objectiveHealth.Resolve(100));
        }

        // ---- (b) ScaledCount.Resolve --------------------------------------------------------------------

        [TestMethod]
        public void ScaledCount_Resolve_MatchesHandComputedValues()
        {
            var count = new ACE.Server.WorldEvents.Defs.ScaledCount { Base = 4, PerParticipant = 1.5, Cap = 18 };

            Assert.AreEqual(4, count.Resolve(0));
            Assert.AreEqual(6, count.Resolve(1));
            Assert.AreEqual(18, count.Resolve(10));
            Assert.AreEqual(18, count.Resolve(100));
        }

        [TestMethod]
        public void ScaledCount_Resolve_FloorsAtBase()
        {
            var count = new ACE.Server.WorldEvents.Defs.ScaledCount { Base = 20, PerParticipant = -5, Cap = 150 };

            // even a hypothetical negative scaling term must never resolve below base.
            Assert.AreEqual(20, count.Resolve(1));
        }

        // ---- (c) validation rules -------------------------------------------------------------------

        [TestMethod]
        public void UnknownCompatibleGoal_IsRemoved_SourceKept()
        {
            var sourcesJson = @"{ ""sources"": [ { ""id"": ""ambush"", ""displayName"": ""Ambush"", ""geometry"": ""edges"",
                ""geometryRadius"": 45.0, ""geometryPoints"": 3, ""waveIntervalSeconds"": 45.0, ""maxAlive"": 24,
                ""waveCount"": { ""base"": 4, ""perParticipant"": 1.5, ""cap"": 18 }, ""holdAdjacentLandblocks"": false,
                ""rewardRadius"": 60.0, ""compatibleGoals"": [""kill_count"", ""nonexistent_goal""],
                ""startFlavour"": ""s"", ""waveFlavour"": ""w"" } ] }";

            var goalsJson = @"{ ""goals"": [ { ""id"": ""kill_count"", ""displayName"": ""Kill Count"", ""type"": ""KillCount"",
                ""count"": { ""base"": 20, ""perParticipant"": 6, ""cap"": 150 }, ""holdSeconds"": 0,
                ""mvpRule"": ""mostKills"", ""progressTemplate"": ""t"" } ] }";

            var store = WorldEventAxisStore.Parse(sourcesJson, null, goalsJson, null, null);

            Assert.IsTrue(store.Sources.ContainsKey("ambush"), "source with a partially-bad goal list must be kept");
            CollectionAssert.AreEqual(new[] { "kill_count" }, store.Sources["ambush"].CompatibleGoals.ToArray());
            Assert.IsTrue(store.Diagnostics.Any(d => d.Contains("nonexistent_goal")), "must name the unknown goal id");
            Assert.IsTrue(store.Diagnostics.Any(d => d.Contains("ambush")), "must name the source id");
        }

        [TestMethod]
        public void BadGeometry_DropsSource()
        {
            var sourcesJson = @"{ ""sources"": [ { ""id"": ""ambush"", ""displayName"": ""Ambush"", ""geometry"": ""hexagon"",
                ""geometryRadius"": 45.0, ""geometryPoints"": 3, ""waveIntervalSeconds"": 45.0, ""maxAlive"": 24,
                ""waveCount"": { ""base"": 4, ""perParticipant"": 1.5, ""cap"": 18 }, ""rewardRadius"": 60.0,
                ""compatibleGoals"": [] } ] }";

            var store = WorldEventAxisStore.Parse(sourcesJson, null, null, null, null);

            Assert.AreEqual(0, store.Sources.Count);
            Assert.IsTrue(store.Diagnostics.Any(d => d.Contains("ambush") && d.Contains("geometry")));
        }

        /// <summary>WP-16: "disc" is a fourth valid geometry string, parsed case-insensitively like the rest.</summary>
        [TestMethod]
        public void DiscGeometry_Parses()
        {
            var sourcesJson = @"{ ""sources"": [ { ""id"": ""ambush"", ""displayName"": ""Ambush"", ""geometry"": ""Disc"",
                ""geometryRadius"": 25.0, ""geometryPoints"": 4, ""waveIntervalSeconds"": 45.0, ""maxAlive"": 24,
                ""waveCount"": { ""base"": 4, ""perParticipant"": 1.5, ""cap"": 18 }, ""rewardRadius"": 60.0,
                ""compatibleGoals"": [] } ] }";

            var store = WorldEventAxisStore.Parse(sourcesJson, null, null, null, null);

            Assert.IsTrue(store.Sources.ContainsKey("ambush"));
            Assert.AreEqual(ACE.Server.WorldEvents.Defs.SourceGeometry.Disc, store.Sources["ambush"].GeometryKind);
        }

        [TestMethod]
        public void DuplicateId_DropsLaterEntry()
        {
            var goalsJson = @"{ ""goals"": [
                { ""id"": ""kill_count"", ""displayName"": ""First"", ""type"": ""KillCount"",
                  ""count"": { ""base"": 20, ""perParticipant"": 6, ""cap"": 150 }, ""holdSeconds"": 0,
                  ""mvpRule"": ""mostKills"", ""progressTemplate"": ""t"" },
                { ""id"": ""kill_count"", ""displayName"": ""Second"", ""type"": ""KillCount"",
                  ""count"": { ""base"": 1, ""perParticipant"": 0, ""cap"": 1 }, ""holdSeconds"": 0,
                  ""mvpRule"": ""mostKills"", ""progressTemplate"": ""t"" }
                ] }";

            var store = WorldEventAxisStore.Parse(null, null, goalsJson, null, null);

            Assert.AreEqual(1, store.Goals.Count);
            Assert.AreEqual("First", store.Goals["kill_count"].DisplayName);
            Assert.IsTrue(store.Diagnostics.Any(d => d.Contains("duplicate") && d.Contains("kill_count")));
        }

        [TestMethod]
        public void UppercaseId_IsDropped()
        {
            var goalsJson = @"{ ""goals"": [ { ""id"": ""Kill_Count"", ""displayName"": ""Kill Count"", ""type"": ""KillCount"",
                ""count"": { ""base"": 20, ""perParticipant"": 6, ""cap"": 150 }, ""holdSeconds"": 0,
                ""mvpRule"": ""mostKills"", ""progressTemplate"": ""t"" } ] }";

            var store = WorldEventAxisStore.Parse(null, null, goalsJson, null, null);

            Assert.AreEqual(0, store.Goals.Count);
            Assert.IsTrue(store.Diagnostics.Any(d => d.Contains("Kill_Count")));
        }

        [TestMethod]
        public void ZeroWcid_DropsReward()
        {
            var rewardsJson = @"{ ""rewards"": [ { ""id"": ""standard"", ""displayName"": ""Hammer Crate"",
                ""successCrateWcid"": 0, ""consolationCrateWcid"": 1002601, ""cacheWcid"": 1002602,
                ""participantsPerCache"": 8, ""claimWindowSeconds"": 300, ""gateByCharacter"": true,
                ""gateByAccount"": true, ""gateByIp"": true } ] }";

            var store = WorldEventAxisStore.Parse(null, null, null, rewardsJson, null);

            Assert.AreEqual(0, store.Rewards.Count);
            Assert.IsTrue(store.Diagnostics.Any(d => d.Contains("standard") && d.Contains("wcid")));
        }

        [TestMethod]
        public void MalformedJsonInOneFile_LeavesOthersLoaded()
        {
            var (sources, families, goals, rewards, anchors) = ReadShippedFiles();

            var brokenSources = "{ not valid json ";

            var store = WorldEventAxisStore.Parse(brokenSources, families, goals, rewards, anchors);

            Assert.AreEqual(0, store.Sources.Count, "the malformed axis loads empty");
            Assert.AreEqual(3, store.Goals.Count, "the other axes still load");
            Assert.AreEqual(1, store.Rewards.Count);
            Assert.AreEqual(11, store.Families.Count);
            Assert.IsTrue(store.Diagnostics.Any(d => d.Contains("sources.json") && d.Contains("malformed")));
        }

        // ---- (d) all-null input ------------------------------------------------------------------------

        [TestMethod]
        public void AllNullInput_IsEmptyTrue_NoException()
        {
            var store = WorldEventAxisStore.Parse(null, null, null, null, null);

            Assert.IsTrue(store.IsEmpty);
            Assert.AreEqual(0, store.Sources.Count);
            Assert.AreEqual(0, store.Goals.Count);
            Assert.AreEqual(0, store.Rewards.Count);
            Assert.AreEqual(0, store.Families.Count);
            Assert.AreEqual(0, store.Anchors.Count);
            Assert.AreEqual(2, store.Bosses.Count, "Bosses is always the two built-in entries, even when empty");
        }

        [TestMethod]
        public void Bosses_ThroughputCapAboveIntMaxValue_IsClampedWithADiagnostic()
        {
            // C16: the ceiling has the same int.MaxValue bound baseHealth documents, because a ratchet raise
            // reaches Creature.UpdateVitalDelta as an int. 4000000000 is a valid uint and an invalid cap.
            var store = WorldEventAxisStore.Parse(null, null, null, null, null,
                @"{ ""bosses"": [ { ""id"": ""huge"", ""displayName"": ""Huge"", ""wcid"": 1002619, " +
                @"""throughputFloorHealth"": 100000, ""throughputCapHealth"": 4000000000 } ] }");

            Assert.AreEqual(3, store.Bosses.Count, "the boss is KEPT - a bad dial never drops an entry");
            Assert.AreEqual((uint)int.MaxValue, store.Bosses["huge"].ThroughputCapHealth);
            Assert.AreEqual(100000u, store.Bosses["huge"].ThroughputFloorHealth, "the floor is untouched");

            Assert.AreEqual(1, store.Diagnostics.Count(d =>
                d.Contains("throughputCapHealth above int.MaxValue, clamped to int.MaxValue")),
                "exactly one diagnostic, and the below-floor repair must not also fire");
        }

        [TestMethod]
        public void Empty_IsEmptyTrue()
        {
            Assert.IsTrue(WorldEventAxisStore.Empty.IsEmpty);
        }

        // ---- (e) ResolveFolder order --------------------------------------------------------------------

        [TestMethod]
        public void ResolveFolder_OverrideWins_WhenItExists()
        {
            var existing = new HashSet<string> { @"C:\override", Path.Combine(@"C:\content", "events", "axes") };

            var folder = WorldEventAxisStore.ResolveFolder(
                @"C:\override", @"C:\content", @"C:\exe", p => existing.Contains(p), out var reason);

            Assert.AreEqual(@"C:\override", folder);
            Assert.AreEqual("world_events_axis_folder", reason);
        }

        [TestMethod]
        public void ResolveFolder_FallsToContentFolder_WhenOverrideEmpty()
        {
            // Path.Combine uses the host separator, so the expected value must be built the same way -
            // a literal backslash path only matches on Windows and fails on the Linux CI runner.
            var contentCandidate = Path.Combine(@"C:\content", "events", "axes");
            var existing = new HashSet<string> { contentCandidate };

            var folder = WorldEventAxisStore.ResolveFolder(
                "", @"C:\content", @"C:\exe", p => existing.Contains(p), out var reason);

            Assert.AreEqual(contentCandidate, folder);
            Assert.AreEqual("content_folder", reason);
        }

        [TestMethod]
        public void ResolveFolder_FallsToContentFolder_WhenOverrideMissingOnDisk()
        {
            // Path.Combine uses the host separator, so the expected value must be built the same way -
            // a literal backslash path only matches on Windows and fails on the Linux CI runner.
            var contentCandidate = Path.Combine(@"C:\content", "events", "axes");
            var existing = new HashSet<string> { contentCandidate };

            var folder = WorldEventAxisStore.ResolveFolder(
                @"C:\does-not-exist", @"C:\content", @"C:\exe", p => existing.Contains(p), out var reason);

            Assert.AreEqual(contentCandidate, folder);
            Assert.AreEqual("content_folder", reason);
        }

        [TestMethod]
        public void ResolveFolder_FallsToExeAdjacent_WhenOverrideAndContentMissing()
        {
            var exeCandidate = Path.Combine(@"C:\exe", "Content", "events", "axes");
            var existing = new HashSet<string> { exeCandidate };

            var folder = WorldEventAxisStore.ResolveFolder(
                "", "", @"C:\exe", p => existing.Contains(p), out var reason);

            Assert.AreEqual(exeCandidate, folder);
            Assert.AreEqual("exe-adjacent", reason);
        }

        [TestMethod]
        public void ResolveFolder_ReturnsNull_WhenNothingExists()
        {
            var folder = WorldEventAxisStore.ResolveFolder(
                @"C:\override", @"C:\content", @"C:\exe", p => false, out var reason);

            Assert.IsNull(folder);
            Assert.AreEqual("none", reason);
        }

        [TestMethod]
        public void ResolveFolder_ExpandsLeadingDotContentFolder()
        {
            var cwd = Directory.GetCurrentDirectory() + Path.DirectorySeparatorChar;
            var expandedCandidate = Path.Combine(cwd + "." + Path.DirectorySeparatorChar + "Content", "events", "axes");

            var folder = WorldEventAxisStore.ResolveFolder(
                "", "." + Path.DirectorySeparatorChar + "Content", @"C:\exe",
                p => p == expandedCandidate, out var reason);

            Assert.AreEqual(expandedCandidate, folder);
            Assert.AreEqual("content_folder", reason);
        }
    }
}
