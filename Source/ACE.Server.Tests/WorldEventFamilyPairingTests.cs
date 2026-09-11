using System;
using System.Collections.Generic;
using System.Linq;

using ACE.Server.WorldEvents;
using ACE.Server.WorldEvents.Defs;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ACE.Server.Tests
{
    /// <summary>
    /// Unit coverage for WorldEventFamilyPairing (two-family composition, 2026-08-29): which ordered family
    /// pairs a random composition may draw, and how the rule degrades when the catalog cannot satisfy it.
    /// Everything here is pure (TECH-DESIGN D6) - hand-built catalogs, no database, no store.
    /// </summary>
    [TestClass]
    public class WorldEventFamilyPairingTests
    {
        private const int Floor = 50;

        private static FamilyMember Member(uint wcid, int level, int role = 0, bool caster = false, string familyId = null)
        {
            return new FamilyMember
            {
                Wcid = wcid,
                Name = $"Test Creature {wcid}",
                Level = level,
                Role = role,
                Caster = caster,
                FamilyId = familyId
            };
        }

        private static WorldEventCatalog Catalog(params (string id, FamilyMember[] members)[] families)
        {
            var dict = new Dictionary<string, IReadOnlyList<FamilyMember>>();

            foreach (var (id, members) in families)
                dict[id] = members.ToList();

            return new WorldEventCatalog(dict, new List<string>());
        }

        // ---- (a) the rule as designed ----------------------------------------------------------------

        [TestMethod]
        public void CandidatePairs_SlotAAlwaysReachesTheFloor()
        {
            // "low" reaches the floor, "high" does not. Both have a caster, so the caster half of the rule
            // is satisfied whichever way round the pair lands and cannot mask the floor half.
            var catalog = Catalog(
                ("low", new[] { Member(1, 20, caster: true) }),
                ("high", new[] { Member(2, 200, caster: true) }));

            var pairs = WorldEventFamilyPairing.CandidatePairs(catalog, Floor, out var tier);

            Assert.AreEqual(WorldEventFamilyPairing.PairTier.FloorAndCaster, tier);
            CollectionAssert.AreEqual(new[] { ("low", "high") }, pairs);
        }

        [TestMethod]
        public void CandidatePairs_TheCasterMayBeInEitherSlot()
        {
            // Both reach the floor; only "b" casts. (a, b) is legal because B brings the caster, and
            // (b, a) is legal because A does - the caster requirement is on the PAIR, not on slot A.
            var catalog = Catalog(
                ("a", new[] { Member(1, 20) }),
                ("b", new[] { Member(2, 30, caster: true) }));

            var pairs = WorldEventFamilyPairing.CandidatePairs(catalog, Floor, out var tier);

            Assert.AreEqual(WorldEventFamilyPairing.PairTier.FloorAndCaster, tier);
            CollectionAssert.AreEquivalent(new[] { ("a", "b"), ("b", "a") }, pairs);
        }

        [TestMethod]
        public void CandidatePairs_NeverPairsAFamilyWithItself()
        {
            var catalog = Catalog(
                ("a", new[] { Member(1, 20, caster: true) }),
                ("b", new[] { Member(2, 30) }),
                ("c", new[] { Member(3, 40) }));

            var pairs = WorldEventFamilyPairing.CandidatePairs(catalog, Floor, out _);

            Assert.IsTrue(pairs.Count > 0);
            Assert.IsFalse(pairs.Any(p => p.a == p.b), "an ordered pair must name two different families");
        }

        // ---- (b) the degrade ladder ------------------------------------------------------------------

        [TestMethod]
        public void CandidatePairs_NoCasterAnywhere_DegradesToFloorOnly()
        {
            var catalog = Catalog(
                ("a", new[] { Member(1, 20) }),
                ("b", new[] { Member(2, 30) }));

            var pairs = WorldEventFamilyPairing.CandidatePairs(catalog, Floor, out var tier);

            Assert.AreEqual(WorldEventFamilyPairing.PairTier.FloorOnly, tier);
            CollectionAssert.AreEquivalent(new[] { ("a", "b"), ("b", "a") }, pairs);
        }

        [TestMethod]
        public void CandidatePairs_NothingUnderTheFloor_DegradesToUnconstrained()
        {
            // This is the margul case: every family starts well above the floor, so there is no legal A.
            // Unconstrained is the honest answer - the run still composes, and the log line says the rule
            // could not be honoured.
            var catalog = Catalog(
                ("margul", new[] { Member(1, 135, caster: true) }),
                ("high", new[] { Member(2, 200) }));

            var pairs = WorldEventFamilyPairing.CandidatePairs(catalog, Floor, out var tier);

            Assert.AreEqual(WorldEventFamilyPairing.PairTier.Unconstrained, tier);
            CollectionAssert.AreEquivalent(new[] { ("margul", "high"), ("high", "margul") }, pairs);
        }

        [TestMethod]
        public void CandidatePairs_OneFamilyOnly_IsSingleWithNoPartner()
        {
            var catalog = Catalog(("only", new[] { Member(1, 20, caster: true) }));

            var pairs = WorldEventFamilyPairing.CandidatePairs(catalog, Floor, out var tier);

            Assert.AreEqual(WorldEventFamilyPairing.PairTier.Single, tier);
            Assert.AreEqual(1, pairs.Count);
            Assert.AreEqual("only", pairs[0].a);
            Assert.IsNull(pairs[0].b);
        }

        [TestMethod]
        public void CandidatePairs_NoFamilies_IsSingleAndEmpty()
        {
            var pairs = WorldEventFamilyPairing.CandidatePairs(WorldEventCatalog.Empty, Floor, out var tier);

            Assert.AreEqual(WorldEventFamilyPairing.PairTier.Single, tier);
            Assert.AreEqual(0, pairs.Count);

            var nullPairs = WorldEventFamilyPairing.CandidatePairs(null, Floor, out var nullTier);

            Assert.AreEqual(WorldEventFamilyPairing.PairTier.Single, nullTier);
            Assert.AreEqual(0, nullPairs.Count);
        }

        [TestMethod]
        public void CandidatePairs_AFamilyOfNamedBossesOnly_IsNotEligible()
        {
            // Role 3 members are never drawn into a wave or the champion slot, so a family made only of
            // them cannot fill half a roster - and its profile MinLevel of 0 would otherwise let it pass
            // the floor rule for free.
            var catalog = Catalog(
                ("real", new[] { Member(1, 20, caster: true) }),
                ("bosses", new[] { Member(2, 400, role: WorldEventRosterSelector.NamedBossRole) }));

            var pairs = WorldEventFamilyPairing.CandidatePairs(catalog, Floor, out var tier);

            Assert.AreEqual(WorldEventFamilyPairing.PairTier.Single, tier);
            Assert.AreEqual(1, pairs.Count);
            Assert.AreEqual("real", pairs[0].a);
        }

        // ---- (c) determinism --------------------------------------------------------------------------

        [TestMethod]
        public void CandidatePairs_AreEnumeratedInOrdinalIdOrder_AOuterBInner()
        {
            var catalog = Catalog(
                ("c", new[] { Member(3, 20, caster: true) }),
                ("a", new[] { Member(1, 20) }),
                ("b", new[] { Member(2, 20) }));

            var first = WorldEventFamilyPairing.CandidatePairs(catalog, Floor, out _);
            var second = WorldEventFamilyPairing.CandidatePairs(catalog, Floor, out _);

            CollectionAssert.AreEqual(first, second, "the same catalog must always enumerate identically");

            // Every pair with a caster on one side, A outer and B inner, ordinal - "a" before "b" before "c".
            CollectionAssert.AreEqual(
                new[] { ("a", "c"), ("b", "c"), ("c", "a"), ("c", "b") },
                first);
        }

        // ---- (d) the floor level seam -----------------------------------------------------------------

        [TestMethod]
        public void FloorLevel_NonPositiveConfiguredValue_FallsBackToTheBuiltInDefault()
        {
            var original = WorldEventFamilyPairing.ConfiguredFloorLevelSource;

            try
            {
                WorldEventFamilyPairing.ConfiguredFloorLevelSource = () => 0;
                Assert.AreEqual(WorldEventFamilyPairing.DefaultFloorLevel, WorldEventFamilyPairing.FloorLevel());

                WorldEventFamilyPairing.ConfiguredFloorLevelSource = () => -25;
                Assert.AreEqual(WorldEventFamilyPairing.DefaultFloorLevel, WorldEventFamilyPairing.FloorLevel());

                WorldEventFamilyPairing.ConfiguredFloorLevelSource = () => 120;
                Assert.AreEqual(120, WorldEventFamilyPairing.FloorLevel());

                WorldEventFamilyPairing.ConfiguredFloorLevelSource = () => throw new InvalidOperationException("no shard config");
                Assert.AreEqual(WorldEventFamilyPairing.DefaultFloorLevel, WorldEventFamilyPairing.FloorLevel());
            }
            finally
            {
                WorldEventFamilyPairing.ConfiguredFloorLevelSource = original;
            }
        }

        [TestMethod]
        public void TheServerPropertyDefault_MatchesTheBuiltInDefault()
        {
            // world_events_family_floor_level is a REAL default (50), not a "0 means use the JSON value"
            // override dial - so the property row and the constant the code falls back to must agree, or an
            // operator reading "/worldevent properties" is told the wrong floor.
            var row = ACE.Server.Managers.PropertyManager
                .EnumerateWithPrefix("world_events_family_floor_level")
                .Single();

            Assert.AreEqual(WorldEventFamilyPairing.DefaultFloorLevel.ToString(), row.@default);
        }
    }
}
