using System;
using System.Collections.Generic;
using System.Linq;

using ACE.Entity.Enum.Properties;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ACE.Server.Tests
{
    /// <summary>
    /// Guards the property-id allocations the Proving Grounds (Wave) engine depends on. These ids are the
    /// contract between the engine and the content SQL that ships with it, so a silent renumber or a collision
    /// with a later custom property would strand live characters' persisted scores and quietly break wave
    /// rosters. Pure enum reflection - no database, no world.
    /// </summary>
    [TestClass]
    public class WaveChallengePropertyTests
    {
        [TestMethod]
        public void WaveChallenge_PropertyIds_MatchTheShippedContract()
        {
            Assert.AreEqual(9020, (int)PropertyBool.WaveChallengeCreature);
            Assert.AreEqual(9021, (int)PropertyBool.WaveChallengeActive);

            Assert.AreEqual(9029, (int)PropertyInt.WaveChallengeWaves);
            Assert.AreEqual(9030, (int)PropertyInt.WaveChallengeWaveIndex);
            Assert.AreEqual(9031, (int)PropertyInt.WaveChallengeRosterBaseWcid);

            Assert.AreEqual(9004, (int)PropertyFloat.WaveChallengeInterWaveDelay);
            Assert.AreEqual(9005, (int)PropertyFloat.WaveChallengeStallTimeout);
            Assert.AreEqual(9006, (int)PropertyFloat.WaveChallengeWaveTimeLimit);

            Assert.AreEqual(9021, (int)PropertyInt64.BestWaveScore);
        }

        /// <summary>
        /// The absolute per-wave deadline. It replaced an ACCIDENTAL 5 minute limit - wave creatures are spawned
        /// outside a generator, so the landblock decay sweep was destroying them at DefaultTimeToRot with a bare
        /// Destroy() that never ran Die(), leaving the wave permanently unclearable. The engine now opts those
        /// creatures out of decay and owns the limit explicitly, which makes this id part of the shipped contract
        /// with the portal weenie's SQL (Content/sql/weenies/1001550 Proving Grounds (Wave).sql writes 9006).
        /// Reuses PropertyRegistryTests' parser rather than reading the TSV a second way.
        /// </summary>
        [TestMethod]
        public void WaveChallengeWaveTimeLimit_EnumAndRegistryAgree()
        {
            var row = PropertyRegistryTests.FindSingleIdRow("PropertyFloat", 9006);

            Assert.IsNotNull(row, "PropertyFloat 9006 has no single-id row in Source/property-registry.tsv.");
            Assert.AreEqual(nameof(PropertyFloat.WaveChallengeWaveTimeLimit), row.Name,
                $"Source/property-registry.tsv line {row.LineNumber} registers PropertyFloat 9006 under a different name than the enum member.");
            Assert.AreEqual("active", row.Status,
                $"Source/property-registry.tsv line {row.LineNumber} must be 'active' - the enum member and the content SQL both exist.");
            Assert.AreEqual("proving-grounds-wave", row.Owner,
                $"Source/property-registry.tsv line {row.LineNumber} must be owned by the wave arena unit.");
        }

        [TestMethod]
        public void PropertyFloat_9003_StaysUnallocated()
        {
            // 9003 was allocated and then retired; stale rows may still exist in shipped content, so it must
            // never be handed to a new property.
            // PropertyFloat's underlying type is ushort, and Enum.IsDefined demands the exact underlying type
            Assert.IsFalse(Enum.IsDefined(typeof(PropertyFloat), (ushort)9003),
                "PropertyFloat 9003 is a retired id and must not be reused.");
        }

        /// <summary>
        /// The weapon-mod half of the same rule. 8136-8140 held WeaponModWarding, WeaponModCritWard,
        /// WeaponModResolute, WeaponModVigor and WeaponModMending until 2026-07-30, when the Tier A pool was cut
        /// to damage-oriented modifiers and those five rows were removed outright.
        ///
        /// The members are gone rather than renamed, but weapons on dev shards are carrying records at those ids
        /// right now. Handing one of them to a new property would make an existing stale record read as that
        /// property's value, silently, on every weapon that still holds one.
        /// </summary>
        [TestMethod]
        public void PropertyFloat_8136_To_8140_StayUnallocated()
        {
            var wasCalled = new Dictionary<int, string>
            {
                { 8136, "WeaponModWarding" },
                { 8137, "WeaponModCritWard" },
                { 8138, "WeaponModResolute" },
                { 8139, "WeaponModVigor" },
                { 8140, "WeaponModMending" },
            };

            foreach (var kvp in wasCalled)
            {
                // PropertyFloat's underlying type is ushort, and Enum.IsDefined demands the exact underlying type
                Assert.IsFalse(Enum.IsDefined(typeof(PropertyFloat), (ushort)kvp.Key),
                    $"PropertyFloat {kvp.Key} (was {kvp.Value}) is a retired id and must not be reused.");
            }
        }

        [TestMethod]
        public void CustomPropertyBands_HaveNoDuplicateIds()
        {
            AssertNoDuplicates<PropertyBool>();
            AssertNoDuplicates<PropertyInt>();
            AssertNoDuplicates<PropertyInt64>();
            AssertNoDuplicates<PropertyFloat>();
        }

        /// <summary>
        /// Two enum members sharing one numeric value compile fine and read as one property at runtime - the
        /// exact failure mode this suite exists to catch.
        /// </summary>
        private static void AssertNoDuplicates<T>() where T : Enum
        {
            var byValue = new Dictionary<long, string>();
            var collisions = new List<string>();

            foreach (var name in Enum.GetNames(typeof(T)))
            {
                var value = Convert.ToInt64(Enum.Parse(typeof(T), name));

                if (byValue.TryGetValue(value, out var existing))
                    collisions.Add($"{typeof(T).Name} {value} is shared by {existing} and {name}");
                else
                    byValue[value] = name;
            }

            Assert.AreEqual(0, collisions.Count, string.Join("; ", collisions));
        }
    }
}
