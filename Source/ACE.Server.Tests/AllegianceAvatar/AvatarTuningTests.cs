using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Server.Avatars;
using ACE.Server.Managers;

namespace ACE.Server.Tests.Avatars
{
    [TestClass]
    public class AvatarTuningTests
    {
        private const string Prefix = "allegiance_avatar_";

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

        private static List<string> AvatarKeys<T>(IEnumerable<KeyValuePair<string, T>> d) =>
            d.Select(kv => kv.Key).Where(k => k.StartsWith(Prefix, StringComparison.Ordinal)).ToList();

        [TestMethod]
        public void Defaults_MatchPropertyManagerDefaults()
        {
            var d = AvatarTuning.Defaults;
            foreach (var p in typeof(AvatarTuning).GetProperties(BindingFlags.Public | BindingFlags.Instance))
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
        public void EveryAvatarKeyIsInTheRecord()
        {
            var props = typeof(AvatarTuning).GetProperties(BindingFlags.Public | BindingFlags.Instance);
            Assert.AreEqual(41, props.Length);

            var bools = AvatarKeys(DefaultPropertyManager.DefaultBooleanProperties);
            var longs = AvatarKeys(DefaultPropertyManager.DefaultLongProperties);
            var doubles = AvatarKeys(DefaultPropertyManager.DefaultDoubleProperties);
            var strings = AvatarKeys(DefaultPropertyManager.DefaultStringProperties);
            Assert.AreEqual(41, bools.Count + longs.Count + doubles.Count + strings.Count);

            var all = bools.Concat(longs).Concat(doubles).Concat(strings).ToList();
            foreach (var p in props)
                Assert.AreEqual(1, all.Count(k => k == Key(p.Name)), p.Name);
        }

        [TestMethod]
        public void FromPropertyManager_ReturnsTheLiveValues()
        {
            // Pin the consumer: the generator must read the PropertyManager values, not the literals.
            // Every key is seeded (reads of an unseeded key would hit the database), then restored to its default.
            var bools = AvatarKeys(DefaultPropertyManager.DefaultBooleanProperties);
            var longs = AvatarKeys(DefaultPropertyManager.DefaultLongProperties);
            var doubles = AvatarKeys(DefaultPropertyManager.DefaultDoubleProperties);
            var strings = AvatarKeys(DefaultPropertyManager.DefaultStringProperties);
            try
            {
                foreach (var k in strings)
                    Assert.IsTrue(PropertyManager.ModifyString(k, DefaultPropertyManager.DefaultStringProperties[k].Item));
                foreach (var k in bools)
                    Assert.IsTrue(PropertyManager.ModifyBool(k, DefaultPropertyManager.DefaultBooleanProperties[k].Item));
                foreach (var k in longs)
                    Assert.IsTrue(PropertyManager.ModifyLong(k, DefaultPropertyManager.DefaultLongProperties[k].Item));
                foreach (var k in doubles)
                    Assert.IsTrue(PropertyManager.ModifyDouble(k, DefaultPropertyManager.DefaultDoubleProperties[k].Item, init: true));

                // Distinct value per key so a swap between two keys that share a default is caught. Every value is inside
                // the clamps (level_xp_fraction within [0, 1]), so each is read back unchanged.
                var expected = new Dictionary<string, object>();
                for (int i = 0; i < longs.Count; i++)
                {
                    PropertyManager.ModifyLong(longs[i], 100 + i);
                    expected[longs[i]] = (long)(100 + i);
                }
                for (int i = 0; i < doubles.Count; i++)
                {
                    var v = 0.25 + i * 0.001;
                    PropertyManager.ModifyDouble(doubles[i], v, init: true);
                    expected[doubles[i]] = v;
                }
                for (int i = 0; i < strings.Count; i++)
                {
                    var v = "Test/Zone_" + i;
                    PropertyManager.ModifyString(strings[i], v);
                    expected[strings[i]] = v;
                }
                foreach (var k in bools)
                {
                    var flipped = !DefaultPropertyManager.DefaultBooleanProperties[k].Item;
                    PropertyManager.ModifyBool(k, flipped);
                    expected[k] = flipped;
                }

                var live = AvatarTuning.FromPropertyManager();
                var props = typeof(AvatarTuning).GetProperties(BindingFlags.Public | BindingFlags.Instance);
                Assert.AreEqual(expected.Count, props.Length);
                foreach (var p in props)
                    Assert.AreEqual(expected[Key(p.Name)], p.GetValue(live), Key(p.Name));
            }
            finally
            {
                RestoreAll();
            }
        }

        // ---- clamps ---------------------------------------------------------------------------------------------

        /// <summary>Seeds every Avatar key with its default (an unseeded read would hit the database).</summary>
        private static void SeedAll()
        {
            foreach (var k in AvatarKeys(DefaultPropertyManager.DefaultBooleanProperties))
                Assert.IsTrue(PropertyManager.ModifyBool(k, DefaultPropertyManager.DefaultBooleanProperties[k].Item));
            foreach (var k in AvatarKeys(DefaultPropertyManager.DefaultLongProperties))
                Assert.IsTrue(PropertyManager.ModifyLong(k, DefaultPropertyManager.DefaultLongProperties[k].Item));
            foreach (var k in AvatarKeys(DefaultPropertyManager.DefaultDoubleProperties))
                Assert.IsTrue(PropertyManager.ModifyDouble(k, DefaultPropertyManager.DefaultDoubleProperties[k].Item, init: true));
            foreach (var k in AvatarKeys(DefaultPropertyManager.DefaultStringProperties))
                Assert.IsTrue(PropertyManager.ModifyString(k, DefaultPropertyManager.DefaultStringProperties[k].Item));
        }

        private static void RestoreAll()
        {
            foreach (var k in AvatarKeys(DefaultPropertyManager.DefaultBooleanProperties))
                PropertyManager.ModifyBool(k, DefaultPropertyManager.DefaultBooleanProperties[k].Item);
            foreach (var k in AvatarKeys(DefaultPropertyManager.DefaultLongProperties))
                PropertyManager.ModifyLong(k, DefaultPropertyManager.DefaultLongProperties[k].Item);
            foreach (var k in AvatarKeys(DefaultPropertyManager.DefaultDoubleProperties))
                PropertyManager.ModifyDouble(k, DefaultPropertyManager.DefaultDoubleProperties[k].Item, init: true);
            foreach (var k in AvatarKeys(DefaultPropertyManager.DefaultStringProperties))
                PropertyManager.ModifyString(k, DefaultPropertyManager.DefaultStringProperties[k].Item);
        }

        [TestMethod]
        public void FromPropertyManager_LevelXpFraction_NaN_FallsBackToTheDefault()
        {
            try
            {
                SeedAll();
                PropertyManager.ModifyDouble(Prefix + "level_xp_fraction", double.NaN, init: true);

                Assert.AreEqual(0.01, AvatarTuning.FromPropertyManager().LevelXpFraction);
            }
            finally
            {
                RestoreAll();
            }
        }

        [TestMethod]
        public void FromPropertyManager_LevelXpFraction_Negative_ClampsToZero()
        {
            try
            {
                SeedAll();
                PropertyManager.ModifyDouble(Prefix + "level_xp_fraction", -1.0, init: true);

                Assert.AreEqual(0.0, AvatarTuning.FromPropertyManager().LevelXpFraction);
            }
            finally
            {
                RestoreAll();
            }
        }

        [TestMethod]
        public void FromPropertyManager_LevelXpFraction_AboveOne_ClampsToOne()
        {
            try
            {
                SeedAll();
                PropertyManager.ModifyDouble(Prefix + "level_xp_fraction", 2.0, init: true);

                Assert.AreEqual(1.0, AvatarTuning.FromPropertyManager().LevelXpFraction);
            }
            finally
            {
                RestoreAll();
            }
        }

        [TestMethod]
        public void Sanitized_Defaults_AreUnchanged()
        {
            Assert.AreEqual(AvatarTuning.Defaults, AvatarTuning.Sanitized(AvatarTuning.Defaults));
        }

        [TestMethod]
        public void Defaults_PinTheOwnerApprovedGates()
        {
            Assert.AreEqual(0.01, AvatarTuning.Defaults.LevelXpFraction);
            Assert.AreEqual(40L, AvatarTuning.Defaults.EventMinLevel);
        }

        /// <summary>Owner ruling 2026-11-25: one private World Event per Avatar per day, the day turning over at 12:00 AM US Eastern.</summary>
        [TestMethod]
        public void Defaults_PinTheOwnerApprovedDailyLimit()
        {
            Assert.AreEqual(1L, AvatarTuning.Defaults.EventDailyLimit);
            Assert.AreEqual(0L, AvatarTuning.Defaults.DayResetHour);
            Assert.AreEqual("America/New_York", AvatarTuning.Defaults.DayResetTimezone);
        }

        /// <summary>Owner ruling 2026-11-25: the Blessing costs 250 + 100 per online member, at most 20000, and adds one hour to each bank.</summary>
        [TestMethod]
        public void Defaults_PinTheOwnerApprovedBlessing()
        {
            Assert.AreEqual(250L, AvatarTuning.Defaults.BlessingPriceBase);
            Assert.AreEqual(100L, AvatarTuning.Defaults.BlessingPricePerMember);
            Assert.AreEqual(20000L, AvatarTuning.Defaults.BlessingPriceCap);
            Assert.AreEqual(3600L, AvatarTuning.Defaults.BlessingBankSeconds);
        }
    }
}
