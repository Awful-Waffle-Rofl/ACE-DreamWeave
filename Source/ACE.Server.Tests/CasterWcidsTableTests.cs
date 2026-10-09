using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Server.Factories.Entity;
using ACE.Server.Factories.Enum;
using ACE.Server.Factories.Tables.Wcids;

namespace ACE.Server.Tests
{
    /// <summary>
    /// Structural regression coverage for CasterWcids.cs's per-tier ChanceTables.
    ///
    /// WHY THIS EXISTS: a ChanceTable that does not sum to 1.0 fails SILENTLY. ChanceTable.Roll walks
    /// a cumulative sum against a uniform [0,1) draw and returns the LAST non-zero entry for any draw
    /// the sum never reaches, and ChanceTable.VerifyTable only log.Errors a mismatch rather than
    /// throwing (Source/ACE.Server/Factories/Entity/ChanceTable.cs). So an arithmetic slip while
    /// editing these tables does not crash, does not fail an existing test, and does not stop a
    /// server - it quietly redirects the missing probability mass onto whichever entry happens to be
    /// written last. That is the exact hazard of hand-maintaining nine elements x three shape
    /// families across five tables, which is what the 2026-08-25 Sanguine-as-ninth-element rebalance
    /// turned this file into.
    ///
    /// The sum assertion uses the same decimal accumulation and 1e-7 threshold VerifyTable uses, so a
    /// table that passes here is a table that will not log an error at runtime.
    /// </summary>
    [TestClass]
    public class CasterWcidsTableTests
    {
        private const decimal Threshold = 0.0000001M;

        private static List<ChanceTable<WeenieClassName>> GetTiers()
        {
            var field = typeof(CasterWcids).GetField("casterTiers", BindingFlags.NonPublic | BindingFlags.Static);
            Assert.IsNotNull(field, "CasterWcids.casterTiers not found - the field was renamed or removed.");

            return (List<ChanceTable<WeenieClassName>>)field.GetValue(null);
        }

        [TestMethod]
        public void EveryTierTable_SumsToOne()
        {
            var tiers = GetTiers();

            for (var i = 0; i < tiers.Count; i++)
            {
                var total = tiers[i].Aggregate(0.0M, (acc, entry) => acc + (decimal)entry.chance);

                Assert.IsTrue(Math.Abs(1.0M - total) <= Threshold,
                    $"Tier {i + 1} caster table sums to {total}, expected 1.0. ChanceTable.Roll would " +
                    $"silently hand the {1.0M - total} shortfall to its last entry.");
            }
        }

        [TestMethod]
        public void EveryTierTable_HasNoDuplicateWcids()
        {
            var tiers = GetTiers();

            for (var i = 0; i < tiers.Count; i++)
            {
                var duplicates = tiers[i].GroupBy(entry => entry.result)
                                         .Where(group => group.Count() > 1)
                                         .Select(group => group.Key.ToString())
                                         .ToList();

                Assert.AreEqual(0, duplicates.Count,
                    $"Tier {i + 1} caster table lists {string.Join(", ", duplicates)} more than once - " +
                    "a copy/paste slip that inflates that wcid's real weight without changing the sum.");
            }
        }

        /// <summary>
        /// The Sanguine family is a ninth ELEMENT: its sceptre and baton roll wherever the other eight
        /// elements' sceptres and batons roll (T3-T8), and its staff wherever their staves roll (T7-T8).
        /// Nether is the reference element because it is the most recent retail addition and therefore
        /// the cleanest template for "what a full element looks like in these tables".
        /// </summary>
        [TestMethod]
        public void SanguineMembers_AppearInExactlyTheSameTiersAsNether()
        {
            var tiers = GetTiers();

            var pairs = new[]
            {
                (nether: WeenieClassName.ace43381_nethersceptre, sanguine: WeenieClassName.driftwardensanguinesceptre),
                (nether: WeenieClassName.ace43382_netherbaton,   sanguine: WeenieClassName.driftwardensanguinebaton),
                (nether: WeenieClassName.ace43383_netherstaff,   sanguine: WeenieClassName.driftwardensanguinestaff),
            };

            foreach (var pair in pairs)
            {
                for (var i = 0; i < tiers.Count; i++)
                {
                    var hasNether = tiers[i].Any(entry => entry.result == pair.nether);
                    var hasSanguine = tiers[i].Any(entry => entry.result == pair.sanguine);

                    Assert.AreEqual(hasNether, hasSanguine,
                        $"Tier {i + 1}: {pair.nether} present = {hasNether} but {pair.sanguine} present = " +
                        $"{hasSanguine}. Sanguine rolls at parity with every other element, so the two " +
                        "must appear in exactly the same tables.");
                }
            }
        }

        /// <summary>
        /// And at the same WEIGHT, not merely in the same tables - the defect the 2026-08-25 rebalance
        /// corrected was a half-weight Sanguine sitting alongside full-weight elements.
        /// </summary>
        [TestMethod]
        public void SanguineMembers_CarryTheSameWeightAsNether()
        {
            var tiers = GetTiers();

            var pairs = new[]
            {
                (nether: WeenieClassName.ace43381_nethersceptre, sanguine: WeenieClassName.driftwardensanguinesceptre),
                (nether: WeenieClassName.ace43382_netherbaton,   sanguine: WeenieClassName.driftwardensanguinebaton),
                (nether: WeenieClassName.ace43383_netherstaff,   sanguine: WeenieClassName.driftwardensanguinestaff),
            };

            foreach (var pair in pairs)
            {
                for (var i = 0; i < tiers.Count; i++)
                {
                    var netherEntry = tiers[i].FirstOrDefault(entry => entry.result == pair.nether);

                    if (netherEntry.result != pair.nether)
                        continue;

                    var sanguineEntry = tiers[i].FirstOrDefault(entry => entry.result == pair.sanguine);

                    Assert.AreEqual(netherEntry.chance, sanguineEntry.chance,
                        $"Tier {i + 1}: {pair.nether} weighs {netherEntry.chance} but {pair.sanguine} " +
                        $"weighs {sanguineEntry.chance}.");
                }
            }
        }

        /// <summary>
        /// Retail has no elemental ORB - the orb is one of the four undifferentiated non-elemental
        /// casters - so the Sanguine Orb must not be in any tier table. The weenie still exists, which
        /// is exactly why this needs asserting: re-adding it is a one-line edit that looks harmless.
        /// </summary>
        [TestMethod]
        public void SanguineOrb_IsNotInAnyTierTable()
        {
            Assert.IsFalse(CasterWcids.Contains(WeenieClassName.driftwardensanguineorb),
                "driftwardensanguineorb is back in a caster table. Retail has no elemental orb; the " +
                "orb shape is non-elemental by design.");
        }
    }
}
