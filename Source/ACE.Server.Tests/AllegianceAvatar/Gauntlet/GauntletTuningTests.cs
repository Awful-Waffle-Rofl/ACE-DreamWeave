using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Server.Avatars.Gauntlet;
using ACE.Server.Managers;

namespace ACE.Server.Tests.Avatars.Gauntlet
{
    [TestClass]
    public class GauntletTuningTests
    {
        private const string Prefix = "allegiance_gauntlet_";

        private static string Key(string propertyName)
        {
            var sb = new System.Text.StringBuilder();
            for (int i = 0; i < propertyName.Length; i++)
            {
                if (i > 0 && char.IsUpper(propertyName[i]))
                    sb.Append('_');
                sb.Append(char.ToLowerInvariant(propertyName[i]));
            }
            return Prefix + sb;
        }

        private static List<string> GauntletKeys<T>(IEnumerable<KeyValuePair<string, T>> d) =>
            d.Select(kv => kv.Key).Where(k => k.StartsWith(Prefix, StringComparison.Ordinal)).ToList();

        private static PropertyInfo[] RecordProps() =>
            typeof(GauntletTuning).GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Where(p => p.Name != "EqualityContract").ToArray();

        [TestMethod]
        public void Defaults_MatchPropertyManagerDefaults()
        {
            var d = GauntletTuning.Defaults;
            foreach (var p in RecordProps())
            {
                var key = Key(p.Name);
                object expected = p.PropertyType == typeof(bool) ? DefaultPropertyManager.DefaultBooleanProperties[key].Item
                    : p.PropertyType == typeof(long) ? DefaultPropertyManager.DefaultLongProperties[key].Item
                    : p.PropertyType == typeof(string) ? DefaultPropertyManager.DefaultStringProperties[key].Item
                    : (object)DefaultPropertyManager.DefaultDoubleProperties[key].Item;
                Assert.AreEqual(expected, p.GetValue(d), key);
            }
        }

        [TestMethod]
        public void EveryGauntletKeyIsInTheRecord()
        {
            var props = RecordProps();
            var bools = GauntletKeys(DefaultPropertyManager.DefaultBooleanProperties);
            var longs = GauntletKeys(DefaultPropertyManager.DefaultLongProperties);
            var doubles = GauntletKeys(DefaultPropertyManager.DefaultDoubleProperties);
            var strings = GauntletKeys(DefaultPropertyManager.DefaultStringProperties);
            Assert.AreEqual(42, props.Length);
            Assert.AreEqual(42, bools.Count + longs.Count + doubles.Count + strings.Count);

            var all = bools.Concat(longs).Concat(doubles).Concat(strings).ToList();
            foreach (var p in props)
                Assert.AreEqual(1, all.Count(k => k == Key(p.Name)), p.Name);
        }

        [TestMethod]
        public void Defaults_OwnerValues()
        {
            var d = GauntletTuning.Defaults;
            Assert.AreEqual(3000L, d.Price);
            Assert.AreEqual(200L, d.StartLevel);
            Assert.AreEqual(25L, d.LevelStep);
            Assert.AreEqual(5L, d.BossEvery);
            Assert.AreEqual(60L, d.DeathHoldSeconds);
            Assert.AreEqual(1.0, d.KillXpScale, "owner ruling 2026-11-21: kill XP ships on at 1.0");
        }

        private static void SeedAll()
        {
            foreach (var k in GauntletKeys(DefaultPropertyManager.DefaultBooleanProperties))
                Assert.IsTrue(PropertyManager.ModifyBool(k, DefaultPropertyManager.DefaultBooleanProperties[k].Item));
            foreach (var k in GauntletKeys(DefaultPropertyManager.DefaultLongProperties))
                Assert.IsTrue(PropertyManager.ModifyLong(k, DefaultPropertyManager.DefaultLongProperties[k].Item));
            foreach (var k in GauntletKeys(DefaultPropertyManager.DefaultDoubleProperties))
                Assert.IsTrue(PropertyManager.ModifyDouble(k, DefaultPropertyManager.DefaultDoubleProperties[k].Item, init: true));
            foreach (var k in GauntletKeys(DefaultPropertyManager.DefaultStringProperties))
                Assert.IsTrue(PropertyManager.ModifyString(k, DefaultPropertyManager.DefaultStringProperties[k].Item));
        }

        private static void RestoreAll()
        {
            foreach (var k in GauntletKeys(DefaultPropertyManager.DefaultBooleanProperties))
                PropertyManager.ModifyBool(k, DefaultPropertyManager.DefaultBooleanProperties[k].Item);
            foreach (var k in GauntletKeys(DefaultPropertyManager.DefaultLongProperties))
                PropertyManager.ModifyLong(k, DefaultPropertyManager.DefaultLongProperties[k].Item);
            foreach (var k in GauntletKeys(DefaultPropertyManager.DefaultDoubleProperties))
                PropertyManager.ModifyDouble(k, DefaultPropertyManager.DefaultDoubleProperties[k].Item, init: true);
            foreach (var k in GauntletKeys(DefaultPropertyManager.DefaultStringProperties))
                PropertyManager.ModifyString(k, DefaultPropertyManager.DefaultStringProperties[k].Item);
        }

        [TestMethod]
        public void FromPropertyManager_ReturnsTheLiveValues()
        {
            // Pin the consumer: the factory must read the PropertyManager values, not the literals. Every key is seeded (an
            // unseeded read would hit the database), given a distinct value inside the sanitize clamps, then restored.
            try
            {
                SeedAll();

                var expected = new Dictionary<string, object>();
                var longs = GauntletKeys(DefaultPropertyManager.DefaultLongProperties);
                for (int i = 0; i < longs.Count; i++)
                {
                    // headcount_cap and crate_max_tier have ceilings (100 and 6); every other long is a plain at-least clamp.
                    var v = longs[i] == Prefix + "headcount_cap" ? 7 : longs[i] == Prefix + "crate_max_tier" ? 3 : 10 + i;
                    PropertyManager.ModifyLong(longs[i], v);
                    expected[longs[i]] = (long)v;
                }
                var doubles = GauntletKeys(DefaultPropertyManager.DefaultDoubleProperties);
                for (int i = 0; i < doubles.Count; i++)
                {
                    // boss_wave_trash_fraction is clamped to [0, 1]; every other double is an at-least clamp.
                    var v = doubles[i] == Prefix + "boss_wave_trash_fraction" ? 0.25 : 3.0 + i;
                    PropertyManager.ModifyDouble(doubles[i], v, init: true);
                    expected[doubles[i]] = v;
                }
                foreach (var k in GauntletKeys(DefaultPropertyManager.DefaultBooleanProperties))
                {
                    var flipped = !DefaultPropertyManager.DefaultBooleanProperties[k].Item;
                    PropertyManager.ModifyBool(k, flipped);
                    expected[k] = flipped;
                }
                PropertyManager.ModifyString(Prefix + "sites", "alpha,beta");
                expected[Prefix + "sites"] = "alpha,beta";

                // The clamps that tie two keys together: keep each pair ordered so no value is moved.
                PropertyManager.ModifyLong(Prefix + "portals_min", 2);
                PropertyManager.ModifyLong(Prefix + "portals_max", 4);
                expected[Prefix + "portals_min"] = 2L;
                expected[Prefix + "portals_max"] = 4L;
                PropertyManager.ModifyDouble(Prefix + "portal_min_distance", 10.0, init: true);
                PropertyManager.ModifyDouble(Prefix + "portal_max_distance", 50.0, init: true);
                expected[Prefix + "portal_min_distance"] = 10.0;
                expected[Prefix + "portal_max_distance"] = 50.0;
                PropertyManager.ModifyDouble(Prefix + "stagger_min_seconds", 0.5, init: true);
                PropertyManager.ModifyDouble(Prefix + "stagger_max_seconds", 1.5, init: true);
                expected[Prefix + "stagger_min_seconds"] = 0.5;
                expected[Prefix + "stagger_max_seconds"] = 1.5;

                var live = GauntletTuning.FromPropertyManager();
                var props = RecordProps();
                Assert.AreEqual(expected.Count, props.Length);
                foreach (var p in props)
                    Assert.AreEqual(expected[Key(p.Name)], p.GetValue(live), Key(p.Name));
            }
            finally
            {
                RestoreAll();
            }
        }

        // ---- sanitizing (pure, no PropertyManager) --------------------------------------------------------------

        [TestMethod]
        public void Sanitized_Defaults_AreUnchanged()
        {
            Assert.AreEqual(GauntletTuning.Defaults, GauntletTuning.Sanitized(GauntletTuning.Defaults));
        }

        [TestMethod]
        public void Sanitized_GarbledLongs_AreClamped()
        {
            var t = GauntletTuning.Sanitized(GauntletTuning.Defaults with
            {
                Price = -5, MinAvatarLevel = -1, CooldownHours = -1, MaxConcurrent = -1,
                StagingSeconds = -1, IntermissionSeconds = -1, StartLevel = 0, LevelStep = -3,
                BossEvery = 0, WaveSeconds = 0, BossWaveBonusSeconds = -1, WaveBaseCount = -1, MaxAlive = 0,
                HeadcountCap = 5000, StragglerCount = -1, StragglerSeconds = -1,
                HealthCurveTopLevel = 0, MaxCreatureHealth = long.MaxValue, DeathHoldSeconds = -1,
                FighterRespawnSeconds = -1, MinContactWaves = 0, CrateMaxTier = 99,
            });

            Assert.AreEqual(0L, t.Price);
            Assert.AreEqual(0L, t.MinAvatarLevel);
            Assert.AreEqual(0L, t.CooldownHours);
            Assert.AreEqual(0L, t.MaxConcurrent);
            Assert.AreEqual(0L, t.StagingSeconds);
            Assert.AreEqual(0L, t.IntermissionSeconds);
            Assert.AreEqual(1L, t.StartLevel);
            Assert.AreEqual(0L, t.LevelStep);
            Assert.AreEqual(1L, t.BossEvery, "a boss interval of 0 would divide by zero");
            Assert.AreEqual(1L, t.WaveSeconds);
            Assert.AreEqual(0L, t.BossWaveBonusSeconds);
            Assert.AreEqual(0L, t.WaveBaseCount);
            Assert.AreEqual(1L, t.MaxAlive);
            Assert.AreEqual(100L, t.HeadcountCap, "the headcount cap is at most GroupScaling.MaxRosterSize");
            Assert.AreEqual(0L, t.StragglerCount);
            Assert.AreEqual(0L, t.StragglerSeconds);
            Assert.AreEqual(1L, t.HealthCurveTopLevel);
            Assert.AreEqual((long)int.MaxValue, t.MaxCreatureHealth);
            Assert.AreEqual(0L, t.DeathHoldSeconds);
            Assert.AreEqual(0L, t.FighterRespawnSeconds);
            Assert.AreEqual(1L, t.MinContactWaves);
            Assert.AreEqual((long)GauntletTuning.CrateMaxTierCeiling, t.CrateMaxTier);
        }

        [TestMethod]
        public void Sanitized_MaxAliveHuge_IsCappedAt10000()
        {
            Assert.AreEqual(10000L, GauntletTuning.Sanitized(GauntletTuning.Defaults with { MaxAlive = 10_000_000_000 }).MaxAlive);
            Assert.AreEqual(10000L, GauntletTuning.MaxAliveCeiling);
            Assert.AreEqual(120L, GauntletTuning.Sanitized(GauntletTuning.Defaults).MaxAlive);
        }

        [TestMethod]
        public void Sanitized_LevelStartAndStepHuge_AreCappedAtIntMax()
        {
            var t = GauntletTuning.Sanitized(GauntletTuning.Defaults with { StartLevel = long.MaxValue, LevelStep = 4611686018427387903L });
            Assert.AreEqual((long)int.MaxValue, t.StartLevel);
            Assert.AreEqual((long)int.MaxValue, t.LevelStep);
        }

        [TestMethod]
        public void Sanitized_HeadcountCapZero_IsOne()
        {
            Assert.AreEqual(1L, GauntletTuning.Sanitized(GauntletTuning.Defaults with { HeadcountCap = 0 }).HeadcountCap);
        }

        [TestMethod]
        public void Sanitized_NonFiniteDoubles_FallBackToTheDefault()
        {
            var d = GauntletTuning.Defaults;
            var t = GauntletTuning.Sanitized(d with
            {
                WaveCountPerPlayer = double.NaN, BossWaveTrashFraction = double.PositiveInfinity,
                PortalMinDistance = double.NaN, PortalMaxDistance = double.NegativeInfinity,
                StaggerMinSeconds = double.NaN, StaggerMaxSeconds = double.PositiveInfinity,
                KillXpScale = double.NaN, PenMinDistance = double.NaN,
                FighterHealthBase = double.NaN, FighterHealthPerLevel = double.NaN,
                FighterOffenseBase = double.NaN, FighterOffensePerLevel = double.NaN,
                FighterDamageBase = double.NaN, FighterDamagePerLevel = double.NaN,
                FighterMinDeathSeconds = double.NaN, FighterBurstSeconds = double.NaN,
            });

            Assert.AreEqual(d.WaveCountPerPlayer, t.WaveCountPerPlayer);
            Assert.AreEqual(d.BossWaveTrashFraction, t.BossWaveTrashFraction);
            Assert.AreEqual(d.PortalMinDistance, t.PortalMinDistance);
            Assert.AreEqual(d.PortalMaxDistance, t.PortalMaxDistance);
            Assert.AreEqual(d.StaggerMinSeconds, t.StaggerMinSeconds);
            Assert.AreEqual(d.StaggerMaxSeconds, t.StaggerMaxSeconds);
            Assert.AreEqual(d.KillXpScale, t.KillXpScale);
            Assert.AreEqual(d.PenMinDistance, t.PenMinDistance);
            Assert.AreEqual(d.FighterHealthBase, t.FighterHealthBase);
            Assert.AreEqual(d.FighterHealthPerLevel, t.FighterHealthPerLevel);
            Assert.AreEqual(d.FighterOffenseBase, t.FighterOffenseBase);
            Assert.AreEqual(d.FighterOffensePerLevel, t.FighterOffensePerLevel);
            Assert.AreEqual(d.FighterDamageBase, t.FighterDamageBase);
            Assert.AreEqual(d.FighterDamagePerLevel, t.FighterDamagePerLevel);
            Assert.AreEqual(d.FighterMinDeathSeconds, t.FighterMinDeathSeconds);
            Assert.AreEqual(d.FighterBurstSeconds, t.FighterBurstSeconds);
        }

        [TestMethod]
        public void Sanitized_NegativeAndOutOfRangeDoubles_AreClamped()
        {
            var t = GauntletTuning.Sanitized(GauntletTuning.Defaults with
            {
                WaveCountPerPlayer = -1.0, BossWaveTrashFraction = 7.0, KillXpScale = -2.0, PenMinDistance = -1.0,
                FighterHealthBase = -1.0, FighterOffenseBase = -1.0, FighterDamageBase = -1.0,
                FighterMinDeathSeconds = 0.0, FighterBurstSeconds = -1.0,
            });

            Assert.AreEqual(0.0, t.WaveCountPerPlayer);
            Assert.AreEqual(1.0, t.BossWaveTrashFraction);
            Assert.AreEqual(0.0, t.KillXpScale);
            Assert.AreEqual(0.0, t.PenMinDistance);
            Assert.AreEqual(0.0, t.FighterHealthBase);
            Assert.AreEqual(0.0, t.FighterOffenseBase);
            Assert.AreEqual(0.0, t.FighterDamageBase);
            Assert.AreEqual(1.0, t.FighterMinDeathSeconds, "a zero death time would make the intake cap divide by zero");
            Assert.AreEqual(0.0, t.FighterBurstSeconds);
            Assert.AreEqual(0.0, GauntletTuning.Sanitized(GauntletTuning.Defaults with { BossWaveTrashFraction = -1.0 }).BossWaveTrashFraction);
        }

        [TestMethod]
        public void Sanitized_PairedRanges_AreOrdered()
        {
            var t = GauntletTuning.Sanitized(GauntletTuning.Defaults with
            {
                PortalsMin = 0, PortalsMax = -4,
                PortalMinDistance = 60.0, PortalMaxDistance = 10.0,
                StaggerMinSeconds = 5.0, StaggerMaxSeconds = 1.0,
            });

            Assert.AreEqual(1L, t.PortalsMin, "a wave needs at least one portal");
            Assert.AreEqual(1L, t.PortalsMax, "max never below min");
            Assert.AreEqual(60.0, t.PortalMinDistance);
            Assert.AreEqual(60.0, t.PortalMaxDistance, "max never below min");
            Assert.AreEqual(5.0, t.StaggerMinSeconds);
            Assert.AreEqual(5.0, t.StaggerMaxSeconds, "max never below min");
        }

        [TestMethod]
        public void Sanitized_NullSites_IsEmpty()
        {
            Assert.AreEqual("", GauntletTuning.Sanitized(GauntletTuning.Defaults with { Sites = null }).Sites);
        }

        [TestMethod]
        public void FromPropertyManager_GarbledLiveValues_Sanitize()
        {
            // The consumer path end to end: a bad live value is clamped on the way out of PropertyManager.
            try
            {
                SeedAll();
                PropertyManager.ModifyLong(Prefix + "boss_every", 0);
                PropertyManager.ModifyLong(Prefix + "headcount_cap", 100000);
                PropertyManager.ModifyDouble(Prefix + "kill_xp_scale", double.NaN, init: true);

                var t = GauntletTuning.FromPropertyManager();

                Assert.AreEqual(1L, t.BossEvery);
                Assert.AreEqual(100L, t.HeadcountCap);
                Assert.AreEqual(GauntletTuning.Defaults.KillXpScale, t.KillXpScale);
            }
            finally
            {
                RestoreAll();
            }
        }
    }
}
