using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

using ACE.Entity.Enum;
using ACE.Server.Factories.Tables;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ACE.Server.Tests.KnotLoot
{
    /// <summary>The upgraded proc pool (CloakChance.UpgradedSurgeSpells / RollUpgradedProcSpell) and the retail pool it must not disturb.</summary>
    [TestClass]
    public class UpgradedCloakPoolTests
    {
        [TestMethod]
        public void SpellIdEnum_ValuesAreTheRingIIIds()
        {
            // the enum values are implicit (sequential), so pin them to the spell table ids
            Assert.AreEqual(6189, (int)SpellId.SearingDiscII);
            Assert.AreEqual(6190, (int)SpellId.HorizonsBladesII);
            Assert.AreEqual(6191, (int)SpellId.CassiusRingOfFireII);
            Assert.AreEqual(6192, (int)SpellId.NuhmudirasSpinesII);
            Assert.AreEqual(6193, (int)SpellId.HaloOfFrostII);
            Assert.AreEqual(6194, (int)SpellId.EyeOfTheStormII);
            Assert.AreEqual(6195, (int)SpellId.CloudedSoulII);
            Assert.AreEqual(6196, (int)SpellId.TectonicRiftsII);
            Assert.AreEqual(6197, (int)SpellId.EyeOfTheStormIIAlt);
            Assert.AreEqual(5753, (int)SpellId.CloakAllSkill);
        }

        [TestMethod]
        public void Pool_IsTheEightRingsII_Skill_AndThreeShrouds_Without6197()
        {
            var ids = CloakChance.UpgradedSurgeSpells.Select(s => (int)s).ToList();

            Assert.AreEqual(12, ids.Count);
            CollectionAssert.AreEquivalent(new[] { 6189, 6190, 6191, 6192, 6193, 6194, 6195, 6196 }, ids.Take(8).ToArray());
            Assert.IsFalse(ids.Contains(6197), "the duplicate Eye of the Storm II (6197) is excluded");
            Assert.AreEqual(SpellId.CloakAllSkill, CloakChance.UpgradedSurgeSpells[8]);
            CollectionAssert.AreEqual(new[] { SpellId.CloakMagicDLower, SpellId.CloakMeleeDLower, SpellId.CloakMissileDLower }, CloakChance.UpgradedSurgeSpells.Skip(9).ToArray());
            Assert.AreEqual(ids.Count, ids.Distinct().Count());
        }

        [TestMethod]
        public void Roll_ThirteenEqualOutcomes_EachReachable()
        {
            var seen = new HashSet<SpellId>();

            for (var i = 0; i < 13; i++)
            {
                var low = CloakChance.RollUpgradedProcSpell(i / 13.0 + 1e-9);
                var high = CloakChance.RollUpgradedProcSpell((i + 1) / 13.0 - 1e-9);

                Assert.AreEqual(low, high, $"bucket {i} is one outcome");
                seen.Add(low);

                var expected = i < 12 ? CloakChance.UpgradedSurgeSpells[i] : SpellId.Undef;
                Assert.AreEqual(expected, low, $"bucket {i}");
            }

            Assert.AreEqual(13, seen.Count);
        }

        [TestMethod]
        public void Roll_Edges()
        {
            Assert.AreEqual(SpellId.SearingDiscII, CloakChance.RollUpgradedProcSpell(0.0));
            Assert.AreEqual(SpellId.SearingDiscII, CloakChance.RollUpgradedProcSpell(-1.0));
            Assert.AreEqual(SpellId.SearingDiscII, CloakChance.RollUpgradedProcSpell(double.NaN));
            Assert.AreEqual(SpellId.Undef, CloakChance.RollUpgradedProcSpell(1.0), "r at 1 is damage reduction, never out of range");
            Assert.AreEqual(SpellId.Undef, CloakChance.RollUpgradedProcSpell(5.0));
        }

        [TestMethod]
        public void RetailPool_IsUnchanged()
        {
            var text = CloakProcMagnitudeTests.Source("Source/ACE.Server/Factories/Tables/CloakChance.cs");

            var retail = Regex.Match(text, @"private static readonly List<SpellId> surgeSpells = new List<SpellId>\(\)\s*\{(?<body>.*?)\};", RegexOptions.Singleline);
            Assert.IsTrue(retail.Success, "retail surgeSpells list not found");

            var members = Regex.Matches(retail.Groups["body"].Value, @"SpellId\.(\w+)").Select(m => m.Groups[1].Value).ToArray();

            CollectionAssert.AreEqual(new[]
            {
                "AcidRing", "BladeRing", "FlameRing", "ForceRing", "FrostRing", "LightningRing", "ShockwaveRing", "NetherRing",
                "CloakAllSkill", "CloakMagicDLower", "CloakMeleeDLower", "CloakMissileDLower",
            }, members);

            StringAssert.Contains(text, "var rng = ThreadSafeRandom.Next(0, surgeSpells.Count);");
        }
    }
}
