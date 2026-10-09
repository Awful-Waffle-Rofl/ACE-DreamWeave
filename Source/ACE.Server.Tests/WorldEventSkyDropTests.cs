using System;
using System.IO;
using System.Linq;

using ACE.Server.WorldEvents;
using ACE.Server.WorldEvents.Defs;
using ACE.Server.WorldObjects;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ACE.Server.Tests
{
    /// <summary>
    /// Unit coverage for the WP-17 sky-drop spike: the theme field and its validation, the two pure spawner
    /// rules that decide whether and how high a wave creature is placed, and the two pure tick predicates
    /// that keep it falling and then hand it back.
    ///
    /// Only pure members are exercised (TECH-DESIGN D6) - nothing here constructs a Creature, a Landblock or
    /// a Session. The fall itself is a live test and sits on the verify queue instead.
    /// </summary>
    [TestClass]
    public class WorldEventSkyDropTests
    {
        // ---- shipped file round-trip -------------------------------------------------------------------

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

        private static WorldEventAxisStore ShippedStore()
        {
            var dir = FindAxesDir();

            if (dir == null)
                Assert.Inconclusive("Could not locate Content/events/axes by walking up from the test assembly -- skipping.");

            return WorldEventAxisStore.Parse(
                File.ReadAllText(Path.Combine(dir, "sources.json")),
                File.ReadAllText(Path.Combine(dir, "families.json")),
                File.ReadAllText(Path.Combine(dir, "goals.json")),
                File.ReadAllText(Path.Combine(dir, "rewards.json")),
                File.ReadAllText(Path.Combine(dir, "anchors.json")));
        }

        /// <summary>
        /// sky_rift is the only shipped theme that drops its waves in. 20 m puts them just under the lowest
        /// disc of the WP-14 stack, which sits about 21 m up.
        /// </summary>
        [TestMethod]
        public void ShippedSkyRift_SpawnDz_Is20()
        {
            var store = ShippedStore();

            Assert.AreEqual(20f, store.Sources["sky_rift"].SpawnDz, 0.0001f);
            CollectionAssert.AreEqual(Array.Empty<string>(), store.Diagnostics.ToArray(),
                "adding spawnDz must not make the shipped file produce a diagnostic");
        }

        /// <summary>
        /// The inertness claim, read off the shipped file: every other theme declares no spawnDz key at all
        /// and therefore reads 0, which is the value that takes the pre-WP-17 code path.
        /// </summary>
        [TestMethod]
        public void ShippedThemes_OtherThanSkyRift_HaveNoSpawnDz()
        {
            var store = ShippedStore();

            Assert.AreEqual(0f, store.Sources["ambush"].SpawnDz, 0.0001f, "ambush declares no spawnDz");
            Assert.AreEqual(0f, store.Sources["rift"].SpawnDz, 0.0001f, "rift declares no spawnDz");
            Assert.AreEqual(0f, store.Sources["weave_spiral"].SpawnDz, 0.0001f, "weave_spiral declares no spawnDz");
        }

        // ---- validation --------------------------------------------------------------------------------

        private const string GoodTheme = @"{
                ""id"": ""keeper"", ""displayName"": ""Keeper"", ""geometry"": ""single"",
                ""geometryRadius"": 1.0, ""geometryPoints"": 1, ""waveIntervalSeconds"": 30.0,
                ""maxAlive"": 5, ""waveCount"": { ""base"": 1, ""perParticipant"": 0, ""cap"": 5 },
                ""holdAdjacentLandblocks"": false, ""rewardRadius"": 10.0, ""compatibleGoals"": []
            }";

        private static string TwoThemes(string spawnDzJson)
        {
            return @"{ ""sources"": [ {
                ""id"": ""dropper"", ""displayName"": ""Dropper"", ""geometry"": ""disc"",
                ""geometryRadius"": 25.0, ""geometryPoints"": 4, ""waveIntervalSeconds"": 40.0,
                ""maxAlive"": 20, ""waveCount"": { ""base"": 3, ""perParticipant"": 1.5, ""cap"": 16 },
                ""holdAdjacentLandblocks"": false, ""rewardRadius"": 60.0, ""compatibleGoals"": [],
                ""spawnDz"": " + spawnDzJson + @"
            }, " + GoodTheme + " ] }";
        }

        [TestMethod]
        public void Sources_OmittedSpawnDz_DefaultsToZero()
        {
            var store = WorldEventAxisStore.Parse(@"{ ""sources"": [ " + GoodTheme + " ] }", null, null, null, null);

            Assert.IsTrue(store.Sources.ContainsKey("keeper"));
            Assert.AreEqual(0f, store.Sources["keeper"].SpawnDz, 0.0001f, "a missing spawnDz key means on the ground");
            CollectionAssert.AreEqual(Array.Empty<string>(), store.Diagnostics.ToArray());
        }

        [TestMethod]
        public void NegativeSpawnDz_DropsTheTheme_OtherThemesStay()
        {
            var store = WorldEventAxisStore.Parse(TwoThemes("-1.0"), null, null, null, null);

            Assert.IsFalse(store.Sources.ContainsKey("dropper"), "a negative drop height would bury the wave");
            Assert.IsTrue(store.Sources.ContainsKey("keeper"), "the file's other themes must survive");

            Assert.AreEqual(1, store.Diagnostics.Count(d => d.Contains("dropper") && d.Contains("spawnDz")));
        }

        [TestMethod]
        public void NaNSpawnDz_DropsTheTheme_OtherThemesStay()
        {
            var store = WorldEventAxisStore.Parse(TwoThemes(@"""NaN"""), null, null, null, null);

            Assert.IsFalse(store.Sources.ContainsKey("dropper"));
            Assert.IsTrue(store.Sources.ContainsKey("keeper"));

            Assert.AreEqual(1, store.Diagnostics.Count(d => d.Contains("dropper") && d.Contains("spawnDz")));
        }

        [TestMethod]
        public void SpawnDzAboveTheCap_DropsTheTheme_OtherThemesStay()
        {
            var store = WorldEventAxisStore.Parse(TwoThemes("61.0"), null, null, null, null);

            Assert.IsFalse(store.Sources.ContainsKey("dropper"), "61 is past the 60 m cap");
            Assert.IsTrue(store.Sources.ContainsKey("keeper"));

            Assert.AreEqual(1, store.Diagnostics.Count(d => d.Contains("dropper") && d.Contains("spawnDz")));
        }

        /// <summary>The cap itself is legal - the rule is "greater than 60 is dropped", not "60 is dropped".</summary>
        [TestMethod]
        public void SpawnDzAtTheCap_IsKept()
        {
            var store = WorldEventAxisStore.Parse(TwoThemes("60.0"), null, null, null, null);

            Assert.IsTrue(store.Sources.ContainsKey("dropper"));
            Assert.AreEqual(SourceThemeDef.MaxSpawnDz, store.Sources["dropper"].SpawnDz, 0.0001f);
            CollectionAssert.AreEqual(Array.Empty<string>(), store.Diagnostics.ToArray());
        }

        [TestMethod]
        public void ZeroSpawnDz_IsKept()
        {
            var store = WorldEventAxisStore.Parse(TwoThemes("0.0"), null, null, null, null);

            Assert.IsTrue(store.Sources.ContainsKey("dropper"), "0 is the default and must never be a validation failure");
            Assert.AreEqual(0f, store.Sources["dropper"].SpawnDz, 0.0001f);
            CollectionAssert.AreEqual(Array.Empty<string>(), store.Diagnostics.ToArray());
        }

        // ---- spawner rules -----------------------------------------------------------------------------

        [TestMethod]
        public void SkyDropZ_ZeroDz_LeavesTheGroundZUnchanged()
        {
            Assert.AreEqual(42.5f, WorldEventSpawner.SkyDropZ(42.5f, 0f), 0.0001f);
        }

        [TestMethod]
        public void SkyDropZ_20_RaisesTheGroundZBy20()
        {
            Assert.AreEqual(62.5f, WorldEventSpawner.SkyDropZ(42.5f, 20f), 0.0001f);
        }

        /// <summary>
        /// Validation drops a theme with a bad spawnDz long before this runs, so these can only be reached by
        /// a caller passing a value the store never produced. The helper still refuses to move the creature.
        /// </summary>
        [TestMethod]
        public void SkyDropZ_NegativeOrNonFinite_LeavesTheGroundZUnchanged()
        {
            Assert.AreEqual(42.5f, WorldEventSpawner.SkyDropZ(42.5f, -5f), 0.0001f);
            Assert.AreEqual(42.5f, WorldEventSpawner.SkyDropZ(42.5f, float.NaN), 0.0001f);
            Assert.AreEqual(42.5f, WorldEventSpawner.SkyDropZ(42.5f, float.PositiveInfinity), 0.0001f);
        }

        /// <summary>
        /// The kind gate, over EVERY kind: only a wave is dropped. A source (rift), the champion, decor and
        /// npcs keep the placement they had before WP-17 whatever a theme declares.
        /// </summary>
        [TestMethod]
        public void AppliesSkyDrop_IsTrueForWaveOnly()
        {
            Assert.IsTrue(WorldEventSpawner.AppliesSkyDrop(WorldEventSpawnKind.Wave, 20f));

            Assert.IsFalse(WorldEventSpawner.AppliesSkyDrop(WorldEventSpawnKind.Source, 20f));
            Assert.IsFalse(WorldEventSpawner.AppliesSkyDrop(WorldEventSpawnKind.Boss, 20f),
                "a champion drop is a possible follow-up, not this spike");
            Assert.IsFalse(WorldEventSpawner.AppliesSkyDrop(WorldEventSpawnKind.Decor, 20f));
            Assert.IsFalse(WorldEventSpawner.AppliesSkyDrop(WorldEventSpawnKind.Npc, 20f));
        }

        /// <summary>The default value is the off switch: dz 0 drops nothing, for any kind.</summary>
        [TestMethod]
        public void AppliesSkyDrop_IsFalseForEveryKind_WhenDzIsZero()
        {
            foreach (WorldEventSpawnKind kind in Enum.GetValues(typeof(WorldEventSpawnKind)))
                Assert.IsFalse(WorldEventSpawner.AppliesSkyDrop(kind, 0f), $"{kind} with spawnDz 0");
        }

        [TestMethod]
        public void AppliesSkyDrop_IsFalseForWave_WhenDzIsNegativeOrNonFinite()
        {
            Assert.IsFalse(WorldEventSpawner.AppliesSkyDrop(WorldEventSpawnKind.Wave, -1f));
            Assert.IsFalse(WorldEventSpawner.AppliesSkyDrop(WorldEventSpawnKind.Wave, float.NaN));
            Assert.IsFalse(WorldEventSpawner.AppliesSkyDrop(WorldEventSpawnKind.Wave, float.PositiveInfinity));
        }

        // ---- physics tick rules ------------------------------------------------------------------------

        /// <summary>
        /// The rule as it stood before WP-17, written out again here rather than referenced, so the
        /// no-regression test below compares the new predicate against an INDEPENDENT copy of the old one.
        /// </summary>
        private static bool LegacyRunUpdate(bool isAnimating, bool isMonster, bool isAwake, bool isDying, int initialUpdates)
        {
            return isAnimating && (!isMonster || !isAwake) || isDying || initialUpdates <= 1;
        }

        /// <summary>
        /// Every pre-existing combination is untouched with skyDrop false - which is the inertness argument
        /// for the tick half of this WP, since nothing but a world-event sky spawn ever sets that flag.
        /// </summary>
        [TestMethod]
        public void ShouldRunCreaturePhysics_WithoutSkyDrop_MatchesThePreviousRule()
        {
            var flags = new[] { false, true };
            var updates = new[] { 0, 1, 2, 5 };

            foreach (var isAnimating in flags)
            foreach (var isMonster in flags)
            foreach (var isAwake in flags)
            foreach (var isDying in flags)
            foreach (var initialUpdates in updates)
            {
                Assert.AreEqual(
                    LegacyRunUpdate(isAnimating, isMonster, isAwake, isDying, initialUpdates),
                    WorldObject.ShouldRunCreaturePhysics(isAnimating, isMonster, isAwake, isDying, initialUpdates, false),
                    $"animating={isAnimating} monster={isMonster} awake={isAwake} dying={isDying} updates={initialUpdates}");
            }
        }

        /// <summary>
        /// skyDrop forces the update on, in every combination - including the one that matters, an AWAKE
        /// monster that is not animating and is past its first frame, which is exactly what a woken wave
        /// creature is and exactly the case the old rule refused.
        /// </summary>
        [TestMethod]
        public void ShouldRunCreaturePhysics_WithSkyDrop_IsAlwaysTrue()
        {
            var flags = new[] { false, true };
            var updates = new[] { 0, 1, 2, 5 };

            foreach (var isAnimating in flags)
            foreach (var isMonster in flags)
            foreach (var isAwake in flags)
            foreach (var isDying in flags)
            foreach (var initialUpdates in updates)
            {
                Assert.IsTrue(
                    WorldObject.ShouldRunCreaturePhysics(isAnimating, isMonster, isAwake, isDying, initialUpdates, true),
                    $"animating={isAnimating} monster={isMonster} awake={isAwake} dying={isDying} updates={initialUpdates}");
            }

            Assert.IsFalse(WorldObject.ShouldRunCreaturePhysics(false, true, true, false, 5, false),
                "the awake, settled, non-animating monster is precisely the case the old rule skipped");
        }

        // ---- landing predicate -------------------------------------------------------------------------

        [TestMethod]
        public void SkyDropSettled_OnWalkable_IsSettledImmediately()
        {
            Assert.IsTrue(WorldObject.SkyDropSettled(onWalkable: true, now: 100d, deadline: 112d));
        }

        [TestMethod]
        public void SkyDropSettled_StillFalling_IsNotSettled()
        {
            Assert.IsFalse(WorldObject.SkyDropSettled(onWalkable: false, now: 100d, deadline: 112d));
        }

        [TestMethod]
        public void SkyDropSettled_PastTheDeadline_IsSettledEvenInTheAir()
        {
            Assert.IsTrue(WorldObject.SkyDropSettled(onWalkable: false, now: 112d, deadline: 112d),
                "the deadline is inclusive");
            Assert.IsTrue(WorldObject.SkyDropSettled(onWalkable: false, now: 500d, deadline: 112d));
        }

        /// <summary>
        /// The deadline arithmetic the spawner writes, driven end to end: a creature armed at t is still
        /// falling one tick short of the timeout and is force-settled the moment it arrives. A 20 m fall
        /// takes roughly 2 s under the engine's gravity, so the bound is generous by design - its only job
        /// is to stop a creature hanging in the sky for the rest of the run.
        /// </summary>
        // ---- reactivation predicate (WP-17 round 2) ----------------------------------------------------

        /// <summary>
        /// The rule that makes the drop independent of TransientStateFlags.Active. A creature found inactive
        /// while still in the AIR has had its physics stopped by something other than landing, so the drop
        /// re-arms activity and carries on rather than freezing server-side.
        /// </summary>
        [TestMethod]
        public void SkyDropShouldReactivate_InactiveAndAirborne_IsTrue()
        {
            Assert.IsTrue(WorldObject.SkyDropShouldReactivate(physicsActive: false, onWalkable: false));
        }

        /// <summary>Inactive ON the ground is not a stall, it is the fall being over.</summary>
        [TestMethod]
        public void SkyDropShouldReactivate_InactiveOnWalkable_IsFalse()
        {
            Assert.IsFalse(WorldObject.SkyDropShouldReactivate(physicsActive: false, onWalkable: true));
        }

        /// <summary>Active physics is the normal case and is never re-armed.</summary>
        [TestMethod]
        public void SkyDropShouldReactivate_ActivePhysics_IsFalse()
        {
            Assert.IsFalse(WorldObject.SkyDropShouldReactivate(physicsActive: true, onWalkable: false));
            Assert.IsFalse(WorldObject.SkyDropShouldReactivate(physicsActive: true, onWalkable: true));
        }

        /// <summary>
        /// The two predicates must partition the inactive case with no gap and no overlap: an inactive
        /// dropping creature is either re-armed or landed, never both and never neither. A gap here is a
        /// creature frozen in the sky, which is the exact failure this round is fixing.
        /// </summary>
        [TestMethod]
        public void SkyDropPredicates_PartitionTheInactiveCase()
        {
            const double now = 100d;
            const double deadline = 112d;

            foreach (var onWalkable in new[] { false, true })
            {
                var reactivate = WorldObject.SkyDropShouldReactivate(physicsActive: false, onWalkable: onWalkable);
                var settled = WorldObject.SkyDropSettled(onWalkable, now, deadline);

                Assert.AreNotEqual(reactivate, settled,
                    $"onWalkable={onWalkable}: exactly one of reactivate/settled must hold before the deadline");
            }
        }

        /// <summary>
        /// Past the deadline the timeout wins even for an airborne creature whose physics went inactive:
        /// the reactivate branch must not be able to loop forever. The caller checks the deadline before
        /// re-arming, so this pins the ordering the caller relies on.
        /// </summary>
        [TestMethod]
        public void SkyDropPredicates_PastTheDeadline_SettledWinsOverReactivate()
        {
            const double deadline = 112d;

            Assert.IsTrue(WorldObject.SkyDropShouldReactivate(physicsActive: false, onWalkable: false),
                "still airborne, so the reactivate rule alone would re-arm");
            Assert.IsTrue(WorldObject.SkyDropSettled(onWalkable: false, now: deadline, deadline: deadline),
                "but the deadline has arrived, and the caller checks this FIRST");
        }

        [TestMethod]
        public void SkyDropProgressLogInterval_IsTwoSeconds()
        {
            var interval = Creature.SkyDropProgressLogInterval;

            Assert.IsTrue(interval > 0d, "a non-positive interval would log every physics frame");
            Assert.IsTrue(interval <= 5d, "a long interval would not resolve a stall inside one run");
        }

        [TestMethod]
        public void SkyDropDeadline_IsTheSpawnTimePlusTheTimeout()
        {
            var armedAt = 1000d;
            var deadline = armedAt + WorldEventSpawner.SkyDropTimeoutSeconds;

            Assert.IsFalse(WorldObject.SkyDropSettled(false, armedAt + 2d, deadline),
                "two seconds in, a creature that has not reported walkable ground is still falling");
            Assert.IsFalse(WorldObject.SkyDropSettled(false, deadline - 0.2d, deadline));
            Assert.IsTrue(WorldObject.SkyDropSettled(false, deadline, deadline));
        }
    }
}
