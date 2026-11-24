using System;
using System.Collections.Generic;
using System.Linq;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Entity;
using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Entity.Models;
using ACE.Server.Entity;
using ACE.Server.Managers;
using ACE.Server.Tests.KnotLoot;
using ACE.Server.WorldObjects;

namespace ACE.Server.Tests
{
    /// <summary>
    /// Cloak Weave Amplifier roll (owner ruling 2026-11-10: +100% to +200%; rolled at APPLY time since 2026-11-11):
    /// the tunable range and its clamps, the whole-percent uniform roll helper, the range / rolled-value text, and that
    /// the gem itself is never mutated. Seeds both roll keys per test and restores them.
    /// </summary>
    [TestClass]
    public class CloakAmplifierRollTests
    {
        private static uint nextGuid = 0x7EC00000;
        private static uint nextWcid = 997800;

        [ClassInitialize]
        public static void TestSetup(TestContext context)
        {
            DefaultPropertyManager.LoadDefaultProperties();
        }

        [TestInitialize]
        public void Seed()
        {
            PropertyManager.ModifyLong(CloakMantleApplicator.RollMinKey, 100);
            PropertyManager.ModifyLong(CloakMantleApplicator.RollMaxKey, 200);
            PropertyManager.ModifyDouble(ImbueGemRisk.ChanceKey, 0.33);
        }

        [TestCleanup]
        public void Restore()
        {
            PropertyManager.ModifyLong(CloakMantleApplicator.RollMinKey, DefaultPropertyManager.DefaultLongProperties[CloakMantleApplicator.RollMinKey].Item);
            PropertyManager.ModifyLong(CloakMantleApplicator.RollMaxKey, DefaultPropertyManager.DefaultLongProperties[CloakMantleApplicator.RollMaxKey].Item);
            PropertyManager.ModifyDouble(ImbueGemRisk.ChanceKey, DefaultPropertyManager.DefaultDoubleProperties[ImbueGemRisk.ChanceKey].Item);
        }

        private static Weenie AmplifierWeenie(double floor = 1.0)
        {
            return new Weenie
            {
                WeenieClassId = nextWcid++,
                WeenieType = WeenieType.Gem,
                PropertiesFloat = new Dictionary<PropertyFloat, double> { { PropertyFloat.CloakProcAmplifier, floor } },
                PropertiesString = new Dictionary<PropertyString, string>
                {
                    { PropertyString.Name, "Amplifier" },
                    { PropertyString.Use, "Use on a cloak to strengthen its proc by 100% to 200%." },
                    { PropertyString.LongDesc, "Use on a cloak to strengthen its proc by 100% to 200%." },
                },
            };
        }

        private static Gem NewGem() => new Gem(AmplifierWeenie(), new ObjectGuid(nextGuid++));

        [TestMethod]
        public void Defaults_Are100And200()
        {
            Assert.AreEqual(100L, DefaultPropertyManager.DefaultLongProperties["cloak_amplifier_roll_min_pct"].Item);
            Assert.AreEqual(200L, DefaultPropertyManager.DefaultLongProperties["cloak_amplifier_roll_max_pct"].Item);
            Assert.AreEqual((100, 200), CloakMantleApplicator.GetRollRangePct());
        }

        [TestMethod]
        public void Roll_LandsInRange_AndHitsBothEnds()
        {
            var seen = new HashSet<int>();

            for (var i = 0; i < 4000; i++)
            {
                var pct = CloakMantleApplicator.RollAmplifierPct();

                Assert.IsTrue(pct >= 100 && pct <= 200, $"rolled {pct}");
                seen.Add(pct);
            }

            Assert.IsTrue(seen.Contains(100) && seen.Contains(200), "inclusive ends are reachable");
            Assert.IsTrue(seen.Count > 80, $"the roll spreads across the range, saw {seen.Count} distinct values");
        }

        [TestMethod]
        public void Roll_UsesTunedRange()
        {
            PropertyManager.ModifyLong(CloakMantleApplicator.RollMinKey, 300);
            PropertyManager.ModifyLong(CloakMantleApplicator.RollMaxKey, 310);

            for (var i = 0; i < 200; i++)
            {
                var pct = CloakMantleApplicator.RollAmplifierPct();
                Assert.IsTrue(pct >= 300 && pct <= 310, $"rolled {pct}");
            }
        }

        [TestMethod]
        public void Range_ClampsTo1To500()
        {
            PropertyManager.ModifyLong(CloakMantleApplicator.RollMinKey, -50);
            PropertyManager.ModifyLong(CloakMantleApplicator.RollMaxKey, 9000);
            Assert.AreEqual((1, 500), CloakMantleApplicator.GetRollRangePct());

            for (var i = 0; i < 500; i++)
            {
                var pct = CloakMantleApplicator.RollAmplifierPct();
                Assert.IsTrue(pct >= 1 && pct <= 500, $"rolled {pct}");
            }

            PropertyManager.ModifyLong(CloakMantleApplicator.RollMinKey, 0);
            PropertyManager.ModifyLong(CloakMantleApplicator.RollMaxKey, 0);
            Assert.AreEqual((1, 1), CloakMantleApplicator.GetRollRangePct());
            Assert.AreEqual(1, CloakMantleApplicator.RollAmplifierPct());
        }

        [TestMethod]
        public void Range_MinAboveMax_UsesMaxForBoth()
        {
            PropertyManager.ModifyLong(CloakMantleApplicator.RollMinKey, 250);
            PropertyManager.ModifyLong(CloakMantleApplicator.RollMaxKey, 120);
            Assert.AreEqual((120, 120), CloakMantleApplicator.GetRollRangePct());

            Assert.AreEqual(120, CloakMantleApplicator.RollAmplifierPct());
        }

        [TestMethod]
        public void ConfirmationText_ShowsTheRange_AndCollapsesWhenMinEqualsMax()
        {
            var cloak = UpgradedCloakTestItems.Item(cloak: true);
            cloak.SetProperty(PropertyString.Name, "Test Cloak");
            var gem = NewGem();

            Assert.AreEqual("Amplify the proc of Test Cloak by 100% to 200%? This cannot be undone.", CloakMantleApplicator.GetConfirmationText(gem, cloak));

            PropertyManager.ModifyLong(CloakMantleApplicator.RollMinKey, 150);
            PropertyManager.ModifyLong(CloakMantleApplicator.RollMaxKey, 150);
            Assert.AreEqual("Amplify the proc of Test Cloak by 150%? This cannot be undone.", CloakMantleApplicator.GetConfirmationText(gem, cloak));

            // the imbue risk suffix is still appended
            StringAssert.StartsWith(CloakMantleApplicator.GetConfirmationText(gem, cloak, 33), "Amplify the proc of Test Cloak by 150%? This cannot be undone.");
            Assert.IsTrue(CloakMantleApplicator.GetConfirmationText(gem, cloak, 33).Length > CloakMantleApplicator.GetConfirmationText(gem, cloak).Length);
        }

        [TestMethod]
        public void SuccessText_UsesTheRolledPercent()
        {
            var cloak = UpgradedCloakTestItems.Item(cloak: true);
            cloak.SetProperty(PropertyString.Name, "Test Cloak");

            Assert.AreEqual("Your Test Cloak's proc is now 147% stronger.", CloakMantleApplicator.AmplifierSuccessText(cloak, 147));
            Assert.AreEqual("Your Test Cloak's proc is now 147% stronger.", CloakMantleApplicator.GetSuccessText(NewGem(), cloak, 147));
        }

        [TestMethod]
        public void RollAndApply_NeverMutateTheGem()
        {
            var gem = NewGem();
            var useBefore = gem.GetProperty(PropertyString.Use);
            var longDescBefore = gem.GetProperty(PropertyString.LongDesc);

            var cloak = UpgradedCloakTestItems.Item(cloak: true);
            cloak.ProcSpell = (uint)SpellId.CloakAllSkill;

            Assert.IsNull(CloakMantleApplicator.Verify(gem, cloak));

            var pct = CloakMantleApplicator.RollAmplifierPct();
            CloakMantleApplicator.ApplyTo(gem, cloak, pct);

            Assert.AreEqual(1.0, gem.GetProperty(PropertyFloat.CloakProcAmplifier).Value, 1e-12, "the gem's marker value is untouched");
            Assert.AreEqual(useBefore, gem.GetProperty(PropertyString.Use));
            Assert.AreEqual(longDescBefore, gem.GetProperty(PropertyString.LongDesc));
            Assert.IsTrue(CloakMantleApplicator.IsAmplifier(gem), "the gem is still an amplifier");

            // the cloak carries the roll, not the marker
            Assert.AreEqual(pct / 100.0, cloak.GetProperty(PropertyFloat.CloakProcAmplifier).Value, 1e-12);
        }

        [TestMethod]
        public void RolledStamp_AppearsOnTheCloak_AndAppraisalShowsIt()
        {
            var gem = NewGem();
            var cloak = UpgradedCloakTestItems.Item(cloak: true);
            cloak.ProcSpell = (uint)SpellId.CloakAllSkill;

            CloakMantleApplicator.ApplyTo(gem, cloak, 147);

            Assert.AreEqual(2.47, CloakMantleMath.AmplifierFactor(cloak, false), 1e-12);
            CollectionAssert.Contains(CloakMantleDisplay.GetAppraisalLines(cloak).ToList(), "Proc Amplifier: +147%");
            Assert.AreEqual(147, CloakMantleApplicator.Pct(cloak.GetProperty(PropertyFloat.CloakProcAmplifier).Value));

            // any further Amplifier is refused, whatever its strength
            Assert.AreEqual(CloakMantleApplicator.RefusalHasAmplifier(cloak), CloakMantleApplicator.Verify(NewGem(), cloak));
        }

        [TestMethod]
        public void Factory_DoesNotRollTheAmplifier()
        {
            // the roll lives in CloakMantleApplicator.Apply only; a factory call would pre-roll the gem again
            var factory = System.IO.File.ReadAllText(System.IO.Path.Combine(
                ClassAbilityAffinityDeclarationTests.RepoRoot(), "Source", "ACE.Server", "Factories", "WorldObjectFactory.cs"));
            var matches = System.Text.RegularExpressions.Regex.Matches(factory, @"CloakMantleApplicator\s*\.\s*RollAmplifier");
            Assert.AreEqual(0, matches.Count, "WorldObjectFactory must not roll the Amplifier; it is rolled when applied");

            var weenieCtor = System.IO.File.ReadAllText(System.IO.Path.Combine(
                ClassAbilityAffinityDeclarationTests.RepoRoot(), "Source", "ACE.Server", "WorldObjects", "Gem.cs"));
            Assert.AreEqual(0, System.Text.RegularExpressions.Regex.Matches(weenieCtor, @"RollAmplifier").Count);

            // and no file under Source/ACE.Server calls RollAmplifierPct( except CloakMantleApplicator.cs (declaration excluded)
            var serverRoot = System.IO.Path.Combine(ClassAbilityAffinityDeclarationTests.RepoRoot(), "Source", "ACE.Server");
            var callers = new List<string>();
            foreach (var file in System.IO.Directory.EnumerateFiles(serverRoot, "*.cs", System.IO.SearchOption.AllDirectories))
            {
                if (file.Contains(System.IO.Path.DirectorySeparatorChar + "obj" + System.IO.Path.DirectorySeparatorChar) || file.Contains(System.IO.Path.DirectorySeparatorChar + "bin" + System.IO.Path.DirectorySeparatorChar))
                    continue;

                var text = System.IO.File.ReadAllText(file);
                var calls = System.Text.RegularExpressions.Regex.Matches(text, @"RollAmplifierPct\(").Count
                    - System.Text.RegularExpressions.Regex.Matches(text, @"static\s+int\s+RollAmplifierPct\(").Count;
                if (calls > 0)
                    callers.Add(System.IO.Path.GetFileName(file));
            }

            CollectionAssert.AreEqual(new[] { "CloakMantleApplicator.cs" }, callers, "RollAmplifierPct is called from Apply only");
        }

        [TestMethod]
        public void ApplyTo_AndGetSuccessText_RefuseAnAmplifierWithoutARolledPercent()
        {
            var cloak = UpgradedCloakTestItems.Item(cloak: true);
            Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => CloakMantleApplicator.ApplyTo(NewGem(), cloak, 0));
            Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => CloakMantleApplicator.GetSuccessText(NewGem(), cloak, 0));
            Assert.IsNull(cloak.GetProperty(PropertyFloat.CloakProcAmplifier), "nothing was stamped");
        }

        [TestMethod]
        public void Apply_RollsOnlyAfterTheDestroyRoll()
        {
            var src = System.IO.File.ReadAllText(System.IO.Path.Combine(
                ClassAbilityAffinityDeclarationTests.RepoRoot(), "Source", "ACE.Server", "Entity", "CloakMantleApplicator.cs"));
            var apply = src.IndexOf("private static void Apply(", StringComparison.Ordinal);
            Assert.IsTrue(apply >= 0);
            var destroy = src.IndexOf("ImbueGemOutcome.Destroy", apply, StringComparison.Ordinal);
            Assert.IsTrue(destroy > apply);
            var destroyReturn = src.IndexOf("return;", destroy, StringComparison.Ordinal);
            var roll = src.IndexOf("RollAmplifierPct()", apply, StringComparison.Ordinal);
            Assert.IsTrue(destroyReturn > destroy && roll > destroyReturn, "the strength is rolled on the success branch, after the destroy block returns");
        }

        [TestMethod]
        public void ShippedWeenie_CarriesMarkerAndRangeText()
        {
            var sql = System.IO.File.ReadAllText(System.IO.Path.Combine(
                ClassAbilityAffinityDeclarationTests.RepoRoot(), "Content", "sql", "weenies", "1007901 Cloak Weave Amplifier.sql"));

            StringAssert.Contains(sql, "(1007901, 9022,       1) /* CloakProcAmplifier */");
            Assert.AreEqual(2, System.Text.RegularExpressions.Regex.Matches(sql, "strengthen its proc by 100% to 200%\\.").Count);
        }

        [TestMethod]
        public void ShippedMantleWeenie_LongDescStatesTheRearGuardStrength()
        {
            var sql = System.IO.File.ReadAllText(System.IO.Path.Combine(
                ClassAbilityAffinityDeclarationTests.RepoRoot(), "Content", "sql", "weenies", "1007900 Rear Guard Mantle.sql"));

            StringAssert.Contains(sql, "It adds 20 rear-arc shield armor per cloak level against monsters.");
            Assert.AreEqual(20.0, DefaultPropertyManager.DefaultDoubleProperties["cloak_rear_guard_armor_per_level"].Item, "the LongDesc number is the tunable's default");
        }
    }
}