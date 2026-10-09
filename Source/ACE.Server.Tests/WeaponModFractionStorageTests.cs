using System.Linq;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Entity.Enum.Properties;
using ACE.Server.Managers;
using ACE.Server.WeaponMods;
using ACE.Server.WorldObjects;

namespace ACE.Server.Tests
{
    /// <summary>
    /// The Tier B storage split (2026-08-07, repo-owner directive): a Tier B record holds the ROLL FRACTION -
    /// potency scaled by workmanship, in [0, 1] - instead of the applied magnitude, and the magnitude is
    /// recomputed as MaxRoll x fraction x scale at every read.
    ///
    /// WHY IT WAS DONE: to make magnitude retunes a real balance lever. Under magnitude storage, halving a
    /// row's MaxRoll only affected NEW rolls - every weapon already carrying the modifier kept the old value
    /// forever, and the appraisal panel then reported those stale weapons as perfect rolls because their
    /// magnitude exceeded the new ceiling. Under fraction storage the catalog moves under the weapon.
    ///
    /// TIER A CANNOT DO THE SAME AND THIS IS NOT AN OVERSIGHT. Tier A adds its magnitude into a native
    /// property that the loot generator may also have written to, and reversal subtracts that same number back
    /// off. If the stored number were re-derived from a retuned catalog, the subtraction would be wrong and the
    /// native would drift permanently on an item nobody can audit. The asymmetry is asserted below rather than
    /// only described, so a future change that "makes the tiers consistent" fails here.
    /// </summary>
    [TestClass]
    public class WeaponModFractionStorageTests
    {
        [ClassInitialize]
        public static void TestSetup(TestContext context)
        {
            DefaultPropertyManager.LoadDefaultProperties();
        }

        /// <summary>A Tier B row with a MaxRoll this test is free to retune. Nothing here reads the registry.</summary>
        private static WeaponModDefinition SyntheticTierB(double maxRoll) => new WeaponModDefinition
        {
            Id = WeaponModId.Quickening,
            Tier = WeaponModTier.B,
            DisplayName = "Synthetic Quickening",
            Record = PropertyFloat.WeaponModQuickening,
            MaxRoll = maxRoll,
            Classes = WeaponClass.All,
            DisplayFormat = "+{0:0.##}% attack speed",
            DisplayScale = 100.0,
        };

        /// <summary>A Tier A row of the same shape: writes a native, so it stores the magnitude.</summary>
        private static WeaponModDefinition SyntheticTierA(double maxRoll) => new WeaponModDefinition
        {
            Id = WeaponModId.Devastation,
            Tier = WeaponModTier.A,
            DisplayName = "Synthetic Devastation",
            Record = PropertyFloat.WeaponModDevastation,
            NativeInt = PropertyInt.GearCritDamage,
            MaxRoll = maxRoll,
            Classes = WeaponClass.All,
            DisplayFormat = "+{0:0} critical damage rating",
        };

        // ---------------- the substitution invariant ----------------

        /// <summary>
        /// THE INVARIANT THE WHOLE SPLIT RESTS ON: resolving a potency directly and resolving it through a
        /// stored fraction produce the same magnitude, for every row, potency and workmanship.
        ///
        /// If these ever diverge, a weapon that was ROLLED and a weapon that was SEEDED to the same numbers
        /// stop agreeing, and every test that seeds one to reason about the other becomes quietly worthless.
        /// Asserted across the whole registry rather than on a sample, because it must hold for the integer
        /// rows and the Binary short-circuit too, not just for the fractional Tier B rows that use it today.
        /// </summary>
        [TestMethod]
        public void FractionStorage_IsExactlySubstitutableForResolve()
        {
            var potencies = new[] { 0.0, 0.25, 0.6, 0.83, 1.0 };
            var workmanships = new[] { 0.0, 1.0, 3.0, 7.5, 10.0 };
            var scales = new[] { 0.5, 1.0, 2.0 };

            foreach (var definition in WeaponModRegistry.AllMods)
            {
                foreach (var potency in potencies)
                {
                    foreach (var workmanship in workmanships)
                    {
                        foreach (var scale in scales)
                        {
                            var direct = WeaponModValue.Resolve(definition, potency, workmanship, scale);
                            var viaFraction = WeaponModValue.MagnitudeFromFraction(
                                definition, WeaponModValue.RollFraction(potency, workmanship), scale);

                            Assert.AreEqual(direct, viaFraction, 1e-12,
                                $"{definition.Id}: potency {potency}, workmanship {workmanship}, scale {scale} - resolving through a stored fraction diverged from resolving directly");
                        }
                    }
                }
            }
        }

        /// <summary>
        /// The fraction is the ROLL and nothing but the roll: MaxRoll and the scale tunable are absent from it,
        /// which is precisely why they can be retuned afterwards. Also pins the range, since a value outside
        /// [0, 1] would mean a catalog term had leaked into the stored number.
        /// </summary>
        [TestMethod]
        public void RollFraction_CarriesOnlyPotencyAndWorkmanship()
        {
            Assert.AreEqual(1.0, WeaponModValue.RollFraction(1.0, 10.0), 1e-12, "a perfect roll on the best weapon is 1.0");
            Assert.AreEqual(0.5, WeaponModValue.RollFraction(1.0, 5.0), 1e-12, "half the workmanship is half the fraction");
            Assert.AreEqual(0.5, WeaponModValue.RollFraction(0.5, 10.0), 1e-12, "half the potency is half the fraction");
            Assert.AreEqual(0.25, WeaponModValue.RollFraction(0.5, 5.0), 1e-12, "the two axes multiply");

            // clamped at both ends rather than trusted
            Assert.AreEqual(1.0, WeaponModValue.RollFraction(4.0, 40.0), 1e-12, "out-of-range inputs clamp to a full roll, never above it");
            Assert.AreEqual(0.0, WeaponModValue.RollFraction(-1.0, 10.0), 1e-12, "a negative potency floors at zero");
            Assert.AreEqual(0.0, WeaponModValue.RollFraction(1.0, 0.0), 1e-12, "no workmanship is no roll");

            foreach (var definition in WeaponModRegistry.AllMods.Where(m => m.Tier == WeaponModTier.B))
            {
                for (var workmanship = 1; workmanship <= 10; workmanship++)
                {
                    var fraction = WeaponModValue.RollFraction(WeaponModRoller.RollPotency(definition), workmanship);

                    Assert.IsTrue(fraction >= 0.0 && fraction <= 1.0,
                        $"{definition.Id}: a rolled fraction of {fraction} is outside [0, 1], so a catalog term has leaked into the stored value");
                }
            }
        }

        // ---------------- the balance lever ----------------

        /// <summary>
        /// THE POINT OF THE WHOLE CHANGE, in the exact scenario it was requested for: Quickening is found
        /// overpowered, its MaxRoll is halved, and a weapon ALREADY carrying it is halved with it - no reroll,
        /// no migration, no touching the weapon at all.
        ///
        /// The stored record must NOT move: it is the roll, and the roll did not change. What changed is what
        /// the roll is worth.
        /// </summary>
        [TestMethod]
        public void Retune_ReachesWeaponsAlreadyInTheWorld()
        {
            var quickening = SyntheticTierB(0.24);
            var weapon = WeaponModTestKit.MakeWeapon();

            WeaponModTinkerSet.ApplySpecialAtFraction(weapon, quickening, 0.83, 1.0);

            Assert.AreEqual(0.83, weapon.GetProperty(PropertyFloat.WeaponModQuickening).Value, 1e-12,
                "the record must hold the roll fraction");
            Assert.AreEqual(0.1992, WeaponModTinkerSet.ReadMagnitude(weapon, quickening, 1.0), 1e-12,
                "before the retune: 83% of a 0.24 ceiling");

            // the retune - the catalog moves, the weapon is not touched
            quickening.MaxRoll = 0.12;

            Assert.AreEqual(0.0996, WeaponModTinkerSet.ReadMagnitude(weapon, quickening, 1.0), 1e-12,
                "after the retune: the weapon already in the world must be halved too, which is the entire reason this storage split exists");
            Assert.AreEqual(0.83, weapon.GetProperty(PropertyFloat.WeaponModQuickening).Value, 1e-12,
                "the stored record must NOT move under a retune - it is the roll, and the roll did not change");

            // the scale tunable is the same lever applied to the whole layer at once
            Assert.AreEqual(0.0498, WeaponModTinkerSet.ReadMagnitude(weapon, quickening, 0.5), 1e-12,
                "weapon_mod_magnitude_scale must reach existing weapons on the same terms as MaxRoll");
        }

        /// <summary>
        /// The display half of the same promise: the reported intensity is retune-INVARIANT, because MaxRoll
        /// and scale appear in the magnitude and in the workmanship-10 ceiling it is measured against, so both
        /// cancel. A player watching a nerf sees the effect fall while their roll quality stays what it was.
        /// </summary>
        [TestMethod]
        public void Retune_LeavesTheReportedIntensityUnmoved()
        {
            var quickening = SyntheticTierB(0.24);
            var weapon = WeaponModTestKit.MakeWeapon();

            WeaponModTinkerSet.ApplySpecialAtFraction(weapon, quickening, 0.83, 1.0);

            var before = WeaponModDisplay.IntensityPercent(quickening, WeaponModTinkerSet.ReadMagnitude(weapon, quickening, 1.0), 1.0);

            quickening.MaxRoll = 0.12;

            var after = WeaponModDisplay.IntensityPercent(quickening, WeaponModTinkerSet.ReadMagnitude(weapon, quickening, 1.0), 1.0);

            Assert.AreEqual(83, before, "precondition: a 0.83 roll reads 83%");
            Assert.AreEqual(before, after, "the reported intensity must not move under a MaxRoll retune - it describes the roll, not the catalog");

            // and the same under a scale change, for the same reason
            Assert.AreEqual(before, WeaponModDisplay.IntensityPercent(quickening, WeaponModTinkerSet.ReadMagnitude(weapon, quickening, 0.25), 0.25),
                "the reported intensity must not move under a scale retune either");
        }

        /// <summary>
        /// THE ASYMMETRY, ASSERTED. A Tier A row does NOT follow a retune, and must not: its record is what
        /// reversal subtracts back off a native the loot generator may also have contributed to. Re-deriving it
        /// from a retuned catalog would subtract a number that was never added, and the difference would stick
        /// permanently.
        ///
        /// A future change that "makes the tiers consistent" by giving Tier A fraction storage fails here, and
        /// the second half of this test is why: the reversal has to land back exactly where it started.
        /// </summary>
        [TestMethod]
        public void Retune_DoesNotReachTierA_BecauseReversalDependsOnTheStoredMagnitude()
        {
            var devastation = SyntheticTierA(6);
            var weapon = WeaponModTestKit.MakeWeapon();

            // a native value the loot generator already put there - the case that makes this load-bearing
            weapon.SetProperty(PropertyInt.GearCritDamage, 10);

            WeaponModTinkerSet.ApplySpecial(weapon, devastation, 4.0);

            Assert.AreEqual(4.0, weapon.GetProperty(PropertyFloat.WeaponModDevastation).Value, 1e-12,
                "a Tier A record holds the applied magnitude");
            Assert.AreEqual(14, weapon.GetProperty(PropertyInt.GearCritDamage).Value,
                "the magnitude was added to the loot-generated native");

            // the retune - and Tier A deliberately does not follow it
            devastation.MaxRoll = 3;

            Assert.AreEqual(4.0, WeaponModTinkerSet.ReadMagnitude(weapon, devastation, 1.0), 1e-12,
                "a Tier A magnitude must NOT be re-derived from a retuned catalog");

            // the reason: reversal has to give the loot-generated value back exactly
            WeaponModTinkerSet.ReverseSpecial(weapon, devastation);

            Assert.AreEqual(10, weapon.GetProperty(PropertyInt.GearCritDamage).Value,
                "reversal must restore the loot-generated native exactly - this is what fraction storage would break for Tier A");
            Assert.IsNull(weapon.GetProperty(PropertyFloat.WeaponModDevastation), "the record must be removed, never zeroed");
        }

        // ---------------- the seeding path ----------------

        /// <summary>
        /// The magnitude-taking overload still means what it says: apply 15 and the weapon carries 15. It has
        /// to invert through the fraction to get there for a Tier B row, so this pins the round trip for every
        /// row at magnitudes the catalog can actually produce.
        /// </summary>
        [TestMethod]
        public void ApplySpecial_RoundTripsAMagnitudeThroughTheStoredFraction()
        {
            var scale = WeaponModValue.MagnitudeScale();

            foreach (var definition in WeaponModRegistry.AllMods.Where(m => m.Tier == WeaponModTier.B))
            {
                foreach (var share in new[] { 0.25, 0.6, 1.0 })
                {
                    var weapon = WeaponModTestKit.MakeWeapon();
                    var magnitude = definition.MaxRoll * share * scale;

                    WeaponModTinkerSet.ApplySpecial(weapon, definition, magnitude);

                    Assert.AreEqual(magnitude, WeaponModTinkerSet.ReadMagnitude(weapon, definition, scale), 1e-12,
                        $"{definition.Id} at {share} of its ceiling did not round-trip through the stored fraction");

                    var stored = weapon.GetProperty(definition.Record).Value;

                    Assert.IsTrue(stored >= 0.0 && stored <= 1.0,
                        $"{definition.Id}: the stored value {stored} is not a fraction");
                }
            }
        }

        /// <summary>
        /// The lossy edge of that inversion, stated so it is a known cost rather than a surprise: a magnitude
        /// ABOVE the row's current ceiling cannot round-trip, because a fraction has nowhere above 1.0 to put
        /// it. It clamps to a full-strength roll.
        ///
        /// This is exactly why the roll path calls ApplySpecialAtFraction with the fraction it already has,
        /// and never comes through the inversion.
        /// </summary>
        [TestMethod]
        public void ApplySpecial_ClampsAMagnitudeAboveTheCeilingRatherThanStoringItAsGiven()
        {
            var quickening = SyntheticTierB(0.24);
            var weapon = WeaponModTestKit.MakeWeapon();

            WeaponModTinkerSet.ApplySpecial(weapon, quickening, 0.96);

            Assert.AreEqual(1.0, weapon.GetProperty(PropertyFloat.WeaponModQuickening).Value, 1e-12,
                "a magnitude above the ceiling must clamp to a full-strength roll, not store a fraction above 1");
            Assert.AreEqual(0.24, WeaponModTinkerSet.ReadMagnitude(weapon, quickening, 1.0), 1e-12,
                "and it therefore reads back as the ceiling, not as the magnitude that was asked for");
        }

        // ---------------- the combat read ----------------

        /// <summary>
        /// The combat accessor resolves the fraction rather than returning it, and still honours the gate. A
        /// raw read here would hand every hook a fraction where it expects a magnitude - Quickening worth 0.83
        /// instead of 0.20 - which is a silent 4x error rather than a crash, so it is pinned per row.
        /// </summary>
        [TestMethod]
        public void ReadWeaponOnly_ResolvesTheStoredFractionAndStillHonoursTheGate()
        {
            foreach (var definition in WeaponModRegistry.AllMods.Where(m => m.Tier == WeaponModTier.B))
            {
                var weapon = WeaponModTestKit.MakeWeapon();

                weapon.SetProperty(definition.Record, 0.5);

                Assert.AreEqual(definition.MaxRoll * 0.5, WeaponModCombat.ReadWeaponOnly(weapon, definition.Id, true, 1.0), 1e-12,
                    $"{definition.Id}: the combat read must resolve the fraction against MaxRoll, not return it raw");

                Assert.AreEqual(definition.MaxRoll * 0.25, WeaponModCombat.ReadWeaponOnly(weapon, definition.Id, true, 0.5), 1e-12,
                    $"{definition.Id}: the combat read must apply the live scale");

                Assert.AreEqual(0.0, WeaponModCombat.ReadWeaponOnly(weapon, definition.Id, false, 1.0), 1e-12,
                    $"{definition.Id}: the gate still wins over a held record");
            }
        }

        /// <summary>
        /// A rolled weapon and the combat layer agree end to end. The reroll writes fractions; the hooks read
        /// magnitudes; this is the one test that drives both halves against the same weapon rather than
        /// seeding one side by hand.
        /// </summary>
        [TestMethod]
        public void ARolledWeapon_ReadsBackTheMagnitudeItsCraftLineReported()
        {
            var prior = PropertyManager.GetBool("weapon_mods_enabled").Item;

            PropertyManager.ModifyBool("weapon_mods_enabled", true);

            try
            {
                var scale = WeaponModValue.MagnitudeScale();

                for (var attempt = 0; attempt < 40; attempt++)
                {
                    var weapon = WeaponModTestKit.MakeUntinkered(WeaponClass.Melee);

                    WeaponModManager.ApplyReroll(weapon, WeaponClass.Melee, 10.0);

                    foreach (var (definition, magnitude) in WeaponModTinkerSet.ReadSpecials(weapon))
                    {
                        if (definition.Tier != WeaponModTier.B)
                            continue;

                        Assert.AreEqual(magnitude, WeaponModCombat.ReadWeaponOnly(weapon, definition.Id, true, scale), 1e-12,
                            $"{definition.Id}: the appraisal panel and the combat hook disagree about the same weapon");

                        var stored = weapon.GetProperty(definition.Record).Value;

                        Assert.IsTrue(stored > 0.0 && stored <= 1.0,
                            $"{definition.Id}: a rolled record of {stored} is not a fraction in (0, 1]");
                    }
                }
            }
            finally
            {
                PropertyManager.ModifyBool("weapon_mods_enabled", prior);
            }
        }
    }
}
