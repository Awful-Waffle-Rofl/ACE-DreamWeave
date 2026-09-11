using System.Linq;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Entity.Enum.Properties;
using ACE.Server.Managers;
using ACE.Server.WeaponMods;

namespace ACE.Server.Tests
{
    /// <summary>
    /// The v4 weapon-mod catalog expansion (2026-08-17, repo-owner directive): nine new Tier B utility rows -
    /// Efficiency, Recovery, Mana Well, Cleanse, Longevity, Siphon, Quick Refresh, Arcane Defender, Panic
    /// Reload - registered alongside the retirement of five older rows (Swift Flight, Life Leech, Mana Leech,
    /// Stamina Leech, Overload). Mirrors WeaponModCatalogV3Tests.cs's shape.
    ///
    /// THIS PHASE REGISTERS THE NINE ROWS BUT WIRES NO HOOKS. Every row is INERT: reading its magnitude off a
    /// weapon works exactly like any other Tier B row (WeaponModCombat.ReadWeaponOnly resolves the roll
    /// fraction against MaxRoll normally), but nothing in the combat/vital/enchantment path consults it yet.
    /// That means this file covers the REGISTRY SHAPE only - magnitudes, classes, record ids, band membership -
    /// and not any combat hook, unlike WeaponModCatalogV3Tests which has live hooks to exercise. Hook coverage
    /// lands with the hooks themselves in a follow-up phase.
    /// </summary>
    [TestClass]
    public class WeaponModCatalogV4Tests
    {
        [ClassInitialize]
        public static void TestSetup(TestContext context)
        {
            DefaultPropertyManager.LoadDefaultProperties();
        }

        /// <summary>
        /// The nine v4 rows, with the magnitude and class set each must carry. Pinned as a table rather than
        /// read off the registry, so a retune has to move this file deliberately.
        ///
        /// MANA WELL AND ARCANE DEFENDER ARE FLAT, NOT PERCENTAGES - unlike every other row in this table, they
        /// carry no DisplayScale. Mana Well's MaxRoll is mana POINTS restored per equipped item, not a fraction
        /// of that item's own mana pool (repo-owner correction, 2026-08-17: originally specced as a 10%
        /// fraction, revised to a flat 30, then revised again the same day to a flat 5). Arcane Defender's
        /// MaxRoll is a flat armor level (revised from 50 to 40, then again to 25, same day).
        /// </summary>
        private static readonly (WeaponModId Id, string Name, PropertyFloat Record, double MaxRoll, WeaponClass Classes, bool DamageRelevant, bool HasDisplayScale)[] Expected =
        {
            (WeaponModId.Efficiency,     "Efficiency",       PropertyFloat.WeaponModEfficiency,     0.50, WeaponClass.All,    false, true),
            (WeaponModId.Recovery,       "Recovery",         PropertyFloat.WeaponModRecovery,        0.50, WeaponClass.All,    false, true),
            (WeaponModId.ManaWell,       "Mana Well",        PropertyFloat.WeaponModManaWell,        5,    WeaponClass.All,    false, false),
            (WeaponModId.Cleanse,        "Cleanse",          PropertyFloat.WeaponModCleanse,          1.00, WeaponClass.All,    false, true),
            (WeaponModId.Longevity,      "Longevity",        PropertyFloat.WeaponModLongevity,        0.25, WeaponClass.Caster, false, true),
            (WeaponModId.Siphon,         "Siphon",           PropertyFloat.WeaponModSiphon,           0.10, WeaponClass.All,    false, true),
            (WeaponModId.QuickRefresh,   "Quick Refresh",    PropertyFloat.WeaponModQuickRefresh,     1.00, WeaponClass.Caster, false, true),
            (WeaponModId.ArcaneDefender, "Arcane Defender",  PropertyFloat.WeaponModArcaneDefender,  25,    WeaponClass.Caster, false, false),
            (WeaponModId.PanicReload,    "Panic Reload",     PropertyFloat.WeaponModPanicReload,      0.10, WeaponClass.Missile, true, true),
        };

        [TestMethod]
        public void CatalogV4_HoldsExactlyTheseNineRowsAsInertTierB()
        {
            foreach (var (id, name, record, maxRoll, classes, damageRelevant, hasDisplayScale) in Expected)
            {
                Assert.IsTrue(WeaponModRegistry.TryGet(id, out var definition), $"{id}: missing registry row");

                Assert.AreEqual(WeaponModTier.B, definition.Tier, $"{id}: must be Tier B - it writes NO native property");
                Assert.AreEqual(record, definition.Record, $"{id}: reserved record id moved");
                Assert.AreEqual(maxRoll, definition.MaxRoll, 1e-12, $"{id}: MaxRoll must be {maxRoll}");
                Assert.AreEqual(classes, definition.Classes, $"{id}: class set moved");
                Assert.AreEqual(name, definition.DisplayName, $"{id}: renaming changes the appraisal panel, so it must be deliberate");
                Assert.AreEqual(damageRelevant, definition.AffectsSingleTargetDamage, $"{id}: AffectsSingleTargetDamage classification moved");
                Assert.IsFalse(definition.Binary, $"{id}: rolls a magnitude");
                Assert.AreEqual(WeaponModDefinition.DefaultMinPotency, definition.MinPotency, 1e-12, $"{id}: uses the class default potency floor");

                Assert.IsNull(definition.NativeInt, $"{id}: a Tier B row must set no native property");
                Assert.IsNull(definition.NativeFloat, $"{id}: a Tier B row must set no native property");
                Assert.IsFalse(definition.WritesNative, $"{id}: a Tier B row must not write a native property");
                Assert.IsFalse(definition.IsInteger, $"{id}: a Tier B magnitude is never the integer branch");

                if (hasDisplayScale)
                {
                    Assert.AreEqual(100.0, definition.DisplayScale, 1e-12, $"{id}: this row is a percentage and must scale by 100");
                }
                else
                {
                    Assert.AreEqual(1.0, definition.DisplayScale, 1e-12, $"{id}: this row is FLAT, not a percentage, and must not carry a DisplayScale");
                }

                // record id falls in the v4 EXPANSION Tier B band, not the v2 band or the v3 expansion band -
                // all three are disjoint
                var recordId = (int)definition.Record;
                Assert.IsTrue(recordId >= WeaponModRegistry.TierBV4BandStart && recordId <= WeaponModRegistry.TierBV4BandEnd,
                    $"{id}: record {recordId} is outside the v4 expansion band {WeaponModRegistry.TierBV4BandStart}-{WeaponModRegistry.TierBV4BandEnd}");
            }

            Assert.AreEqual(9, Expected.Length, "this table itself must name exactly nine rows");
        }

        /// <summary>
        /// THIS PHASE REGISTERS THE ROWS BUT WIRES NO HOOK. Reading a v4 modifier's magnitude off a weapon must
        /// still resolve normally through the generic Tier B machinery (it is not special-cased as "always
        /// zero") - the inertness is that nothing in the combat/vital/enchantment path calls that read yet, not
        /// that the read itself is broken. That distinction matters: a future hook wiring pass reads live data
        /// that was already being written correctly, rather than discovering the read path itself needs fixing.
        /// </summary>
        [TestMethod]
        public void CatalogV4_MagnitudeResolvesNormallyThroughTheGenericTierBMachinery()
        {
            foreach (var (id, _, _, maxRoll, _, _, _) in Expected)
            {
                var definition = WeaponModRegistry.Get(id);
                var weapon = WeaponModTestKit.MakeWeapon();

                WeaponModTinkerSet.ApplySpecial(weapon, definition, maxRoll);

                Assert.AreEqual(maxRoll, WeaponModTinkerSet.ReadMagnitude(weapon, definition, 1.0), 1e-9,
                    $"{id}: a full-strength roll must resolve back to MaxRoll through the standard Tier B read path");
            }
        }

        [TestMethod]
        public void CatalogV4_NoneOfTheNineWritesAnyNativePropertyAtAll()
        {
            foreach (var (id, _, _, _, _, _, _) in Expected)
            {
                var definition = WeaponModRegistry.Get(id);

                Assert.IsFalse(definition.WritesNative, $"{id}: must write no native property - every v4 row is Tier B by construction");
            }
        }

        // ================= class eligibility =================

        [TestMethod]
        public void CatalogV4_ClassEligibilityMatchesTheTable()
        {
            foreach (var id in new[] { WeaponModId.Efficiency, WeaponModId.Recovery, WeaponModId.ManaWell, WeaponModId.Cleanse, WeaponModId.Siphon })
            {
                var definition = WeaponModRegistry.Get(id);
                Assert.IsTrue(definition.AppliesTo(WeaponClass.Melee), $"{id} must apply to Melee");
                Assert.IsTrue(definition.AppliesTo(WeaponClass.Missile), $"{id} must apply to Missile");
                Assert.IsTrue(definition.AppliesTo(WeaponClass.Caster), $"{id} must apply to Caster");
            }

            foreach (var id in new[] { WeaponModId.Longevity, WeaponModId.QuickRefresh, WeaponModId.ArcaneDefender })
            {
                var definition = WeaponModRegistry.Get(id);
                Assert.IsFalse(definition.AppliesTo(WeaponClass.Melee), $"{id} must not apply to Melee");
                Assert.IsFalse(definition.AppliesTo(WeaponClass.Missile), $"{id} must not apply to Missile");
                Assert.IsTrue(definition.AppliesTo(WeaponClass.Caster), $"{id} must apply to Caster");
            }

            var panicReload = WeaponModRegistry.Get(WeaponModId.PanicReload);
            Assert.IsFalse(panicReload.AppliesTo(WeaponClass.Melee), "Panic Reload must not apply to Melee");
            Assert.IsTrue(panicReload.AppliesTo(WeaponClass.Missile), "Panic Reload must apply to Missile");
            Assert.IsFalse(panicReload.AppliesTo(WeaponClass.Caster), "Panic Reload must not apply to Caster");
        }

        /// <summary>
        /// Pool depth is data-driven off these nine rows' Classes, so it is asserted here as a cross-check
        /// against the counts pinned in WeaponModTests.Registry_PoolDepthMatchesTheDesign: melee +5, missile
        /// +6, caster +8.
        /// </summary>
        [TestMethod]
        public void CatalogV4_PoolContributionMatchesTheClassTable()
        {
            var v4Ids = Expected.Select(e => e.Id).ToHashSet();

            var melee = WeaponModRegistry.Pool(WeaponClass.Melee, true).Count(m => v4Ids.Contains(m.Id));
            var missile = WeaponModRegistry.Pool(WeaponClass.Missile, true).Count(m => v4Ids.Contains(m.Id));
            var caster = WeaponModRegistry.Pool(WeaponClass.Caster, true).Count(m => v4Ids.Contains(m.Id));

            Assert.AreEqual(5, melee, "v4 rows reachable from the melee pool with the gate on");
            Assert.AreEqual(6, missile, "v4 rows reachable from the missile pool with the gate on");
            Assert.AreEqual(8, caster, "v4 rows reachable from the caster pool with the gate on");

            // and none is reachable with the gate off, since every v4 row is Tier B
            foreach (var weaponClass in new[] { WeaponClass.Melee, WeaponClass.Missile, WeaponClass.Caster })
            {
                Assert.IsFalse(WeaponModRegistry.Pool(weaponClass, false).Any(m => v4Ids.Contains(m.Id)),
                    $"a v4 row is reachable from the {weaponClass} pool with the gate off");
            }
        }
    }
}
