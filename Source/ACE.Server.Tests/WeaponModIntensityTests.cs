using System;
using System.Collections.Generic;
using System.Linq;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Entity;
using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Entity.Models;
using ACE.Server.Managers;
using ACE.Server.WeaponMods;
using ACE.Server.WorldObjects;

namespace ACE.Server.Tests
{
    /// <summary>
    /// The intensity percentage added to the appraisal panel and the craft lines on 2026-08-07 (repo-owner
    /// request): "Quickening [83%]: +20% attack speed", where the figure is the applied magnitude as a
    /// percentage of what that modifier reaches on a PERFECT roll on a WORKMANSHIP 10 weapon.
    ///
    /// The denominator being fixed at workmanship 10 rather than at this weapon's own workmanship is the
    /// design, not an approximation: it folds both axes the roll varies on into one comparable number, so a
    /// workmanship 5 weapon reads about half however well it rolls. Tested from both directions below -
    /// potency at fixed workmanship, and workmanship at fixed potency.
    ///
    /// WHY THE ROUNDED MAGNITUDE IS THE NUMERATOR. The percentage is derived from the stored magnitude, never
    /// from the potency that produced it, so it always agrees with the number printed beside it on the same
    /// line. On the integer rows that is visible: a Devastation rolled at potency 0.62 stores +4 (rounded
    /// from 3.72) and must read 67% - four sixths of the ceiling the player can see - rather than the 62% its
    /// potency would suggest for a magnitude that is not what the weapon carries.
    /// </summary>
    [TestClass]
    public class WeaponModIntensityTests
    {
        private static uint nextGuid = 0x7E200000;
        private static uint nextWcid = 994000;

        [ClassInitialize]
        public static void TestSetup(TestContext context)
        {
            DefaultPropertyManager.LoadDefaultProperties();
        }

        /// <summary>Bare in-memory weapon, same shape as WeaponModCatalogV3Tests.MakeWeapon.</summary>
        private static WorldObject MakeWeapon()
        {
            var weenie = new Weenie
            {
                WeenieClassId = nextWcid++,
                WeenieType = WeenieType.Clothing,
                PropertiesInt = new Dictionary<PropertyInt, int>
                {
                    { PropertyInt.ItemType, (int)ItemType.MeleeWeapon },
                    { PropertyInt.ItemWorkmanship, 10 },
                    { PropertyInt.CombatUse, (int)CombatUse.Melee },
                },
                PropertiesString = new Dictionary<PropertyString, string> { { PropertyString.Name, "Test Weapon" } },
            };

            return new Clothing(weenie, new ObjectGuid(nextGuid++));
        }

        // ---------------- the two axes ----------------

        /// <summary>
        /// A perfect roll on a workmanship 10 weapon reads exactly 100 for EVERY row in the catalog. This is
        /// the definition of the scale, so it is asserted registry-wide rather than on a sample: a row added
        /// later with a MaxRoll the percentage cannot represent fails here rather than shipping a panel that
        /// tops out at 97%.
        /// </summary>
        [TestMethod]
        public void Intensity_APerfectWorkmanshipTenRoll_ReadsExactlyOneHundred()
        {
            foreach (var definition in WeaponModRegistry.AllMods)
            {
                var magnitude = WeaponModValue.Resolve(definition, 1.0, 10.0, 1.0);

                Assert.AreEqual(100, WeaponModDisplay.IntensityPercent(definition, magnitude, 1.0),
                    $"{definition.Id}: a perfect roll on a workmanship 10 weapon must read 100%");
            }
        }

        /// <summary>
        /// Potency axis, at fixed workmanship 10. The reported figure tracks the potency that produced the
        /// magnitude, and every roll the roller can actually produce lands inside the band the potency floor
        /// implies - never 0, never above 100.
        ///
        /// The floor is DefaultMinPotency (0.60), so the band is 60-100 and the assertion is written against
        /// the constant rather than a literal, so a retune moves the test with the design. Integer rows can
        /// read slightly ABOVE their potency once rounding lifts them (a 0.60 potency Devastation stores +4,
        /// which is 67%), so the band is a floor test, not an equality test.
        /// </summary>
        [TestMethod]
        public void Intensity_EveryRollableMagnitude_LandsInsideThePotencyBand()
        {
            var floor = (int)Math.Floor(WeaponModDefinition.DefaultMinPotency * 100.0);

            foreach (var definition in WeaponModRegistry.AllMods)
            {
                for (var i = 0; i < 200; i++)
                {
                    var magnitude = WeaponModValue.Roll(definition, 10.0);

                    if (!WeaponModValue.IsLiveMagnitude(definition, magnitude))
                        continue;

                    var percent = WeaponModDisplay.IntensityPercent(definition, magnitude, WeaponModValue.MagnitudeScale());

                    Assert.IsTrue(percent >= floor && percent <= 100,
                        $"{definition.Id}: a live workmanship 10 roll read {percent}%, outside the {floor}-100 band its potency floor implies");
                }
            }
        }

        /// <summary>
        /// Workmanship axis, at fixed perfect potency. HALF THE WORKMANSHIP READS HALF THE INTENSITY, which is
        /// the property that makes the number comparable between two weapons.
        ///
        /// Asserted on the FLOAT rows only, and that exclusion is the point rather than a convenience: an
        /// integer row quantizes before it is ever displayed, so a perfect workmanship 5 WeakPoint stores
        /// round(3 x 0.5) = 2 against a ceiling of 3 and reads 67%, not 50%. Both are honest - 2 really is two
        /// thirds of 3 - and the alternative, reporting a 50% that no arithmetic on the visible numbers can
        /// reproduce, is worse. The integer rows are covered by the band test above.
        /// </summary>
        [TestMethod]
        public void Intensity_HalfWorkmanship_ReadsHalfIntensityOnTheFloatRows()
        {
            var covered = 0;

            foreach (var definition in WeaponModRegistry.AllMods.Where(m => !m.IsInteger && !m.Binary))
            {
                var magnitude = WeaponModValue.Resolve(definition, 1.0, 5.0, 1.0);

                Assert.AreEqual(50, WeaponModDisplay.IntensityPercent(definition, magnitude, 1.0),
                    $"{definition.Id}: a perfect roll on a workmanship 5 weapon must read 50%");

                covered++;
            }

            Assert.IsTrue(covered > 0, "no float-valued row was exercised - this test passed vacuously");
        }

        /// <summary>
        /// The scale is 1-100 with BOTH ends clamped, and neither clamp is decoration.
        ///
        /// The floor: a magnitude too small to round up to a whole percent reads 1%, never 0%. A workmanship 1
        /// weapon can reach exactly that, and 0% would read as "this modifier does nothing" on a modifier that
        /// is applied and working.
        ///
        /// The ceiling: a magnitude ABOVE its own ceiling reads 100%, never more. That state is reachable in
        /// one direction only - a weapon rolled before weapon_mod_magnitude_scale was lowered keeps the
        /// magnitude it was rolled with while the denominator moves under it - and it is why the result is
        /// clamped rather than trusted.
        /// </summary>
        [TestMethod]
        public void Intensity_IsClampedToOneThroughOneHundred()
        {
            var quickening = WeaponModRegistry.AllMods.Single(m => m.Id == WeaponModId.Quickening);

            Assert.AreEqual(1, WeaponModDisplay.IntensityPercent(quickening, quickening.MaxRoll * 0.0001, 1.0),
                "a magnitude below half a percent of its ceiling must floor at 1%, not report 0%");

            Assert.AreEqual(100, WeaponModDisplay.IntensityPercent(quickening, quickening.MaxRoll * 4.0, 1.0),
                "a magnitude rolled at a scale since lowered must clamp at 100%, not report 400%");

            // the reachable version of the same case: rolled at scale 2, read back at scale 1
            var rolledHot = WeaponModValue.Resolve(quickening, 1.0, 10.0, 2.0);

            Assert.AreEqual(100, WeaponModDisplay.IntensityPercent(quickening, rolledHot, 1.0),
                "a magnitude that outlives a scale reduction must clamp rather than exceed 100%");
        }

        // ---------------- the unreportable cases ----------------

        /// <summary>
        /// Everything that cannot produce an honest figure returns 0, which is the caller's signal to omit the
        /// bracket entirely. A percentage is dropped rather than guessed: the alternative to this branch is a
        /// division that lands on infinity or NaN and then clamps to a confident-looking 100%.
        ///
        /// The muted-scale case is the one that matters operationally. weapon_mod_magnitude_scale = 0 drives
        /// every ceiling to zero, and a weapon rolled BEFORE the mute still carries live magnitudes - so this
        /// is a state a player can appraise, not a hypothetical.
        /// </summary>
        [TestMethod]
        public void Intensity_UnreportableInputs_ReturnZeroRatherThanAFigure()
        {
            var devastation = WeaponModRegistry.AllMods.Single(m => m.Id == WeaponModId.Devastation);

            Assert.AreEqual(0, WeaponModDisplay.IntensityPercent(null, 4.0, 1.0), "a null definition has no ceiling");
            Assert.AreEqual(0, WeaponModDisplay.IntensityPercent(devastation, 0.0, 1.0), "a zero magnitude has no intensity");
            Assert.AreEqual(0, WeaponModDisplay.IntensityPercent(devastation, -4.0, 1.0), "a negative magnitude must not clamp up to 1%");
            Assert.AreEqual(0, WeaponModDisplay.IntensityPercent(devastation, double.NaN, 1.0), "NaN must not divide into a figure");
            Assert.AreEqual(0, WeaponModDisplay.IntensityPercent(devastation, 4.0, 0.0), "a muted scale leaves no ceiling to measure against");
            Assert.AreEqual(0, WeaponModDisplay.IntensityPercent(devastation, 4.0, double.NaN), "a NaN scale leaves no ceiling to measure against");
        }

        /// <summary>
        /// A line that cannot compute an honest figure loses the FIGURE and keeps the MODIFIER. The panel
        /// still names the modifier and still prints its magnitude - only the bracket is dropped - because a
        /// player holding a weapon under a muted scale needs to know what it carries more than they need a
        /// percentage of nothing.
        /// </summary>
        [TestMethod]
        public void Describe_DropsTheBracketWhenTheFigureIsUnreportable()
        {
            var devastation = WeaponModRegistry.AllMods.Single(m => m.Id == WeaponModId.Devastation);

            var muted = WeaponModDisplay.Describe(devastation, 4.0, 0.0);

            Assert.AreEqual("Devastation: +4 critical damage rating", muted,
                "a muted scale must drop the bracket, not print an empty or zero one");

            Assert.IsFalse(muted.Contains('['), $"a bracket survived an unreportable figure: \"{muted}\"");
            Assert.IsTrue(muted.Contains(devastation.DisplayName), "the modifier itself must survive an unreportable figure");
            Assert.AreEqual(string.Empty, WeaponModDisplay.Describe(null, 1.0, 1.0), "a null definition still describes as empty");
        }

        // ---------------- the rendered line ----------------

        /// <summary>
        /// The exact player-facing strings, pinned. These are the format itself - name, bracket, colon,
        /// magnitude - so a change to any part of it is a deliberate edit here rather than a silent shift in
        /// what every player reads.
        ///
        /// Devastation is the integer case at a rounded 4 of 6, and Quickening the float case at three
        /// quarters of its ceiling.
        /// </summary>
        [TestMethod]
        public void Describe_RendersTheIntensityAheadOfTheMagnitude()
        {
            var devastation = WeaponModRegistry.AllMods.Single(m => m.Id == WeaponModId.Devastation);
            var quickening = WeaponModRegistry.AllMods.Single(m => m.Id == WeaponModId.Quickening);

            Assert.AreEqual("Devastation [67%]: +4 critical damage rating",
                WeaponModDisplay.Describe(devastation, 4.0, 1.0));

            Assert.AreEqual("Devastation [100%]: +6 critical damage rating",
                WeaponModDisplay.Describe(devastation, 6.0, 1.0));

            Assert.AreEqual("Quickening [75%]: +18% attack speed",
                WeaponModDisplay.Describe(quickening, quickening.MaxRoll * 0.75, 1.0));
        }

        /// <summary>
        /// The figure reaches the appraisal panel, which is the surface that was actually asked for. Asserted
        /// through GetAppraisalLines - the method AppraiseInfo.BuildProfile calls - rather than through
        /// Describe alone, so the wiring between the two is covered and not just the formatter.
        ///
        /// The tinker line is deliberately excluded: it describes a composition, not a rolled magnitude, and
        /// has no intensity to report.
        /// </summary>
        [TestMethod]
        public void AppraisalLines_CarryTheIntensityForEveryHeldSpecial()
        {
            var weapon = MakeWeapon();
            var held = new List<WeaponModDefinition>();

            foreach (var definition in WeaponModRegistry.AllMods.Take(4))
            {
                WeaponModTinkerSet.ApplySpecial(weapon, definition, WeaponModValue.Resolve(definition, 0.8, 10.0, WeaponModValue.MagnitudeScale()));
                held.Add(definition);
            }

            var lines = WeaponModDisplay.GetAppraisalLines(weapon);

            foreach (var definition in held)
            {
                var line = lines.SingleOrDefault(l => l.Contains(definition.DisplayName, StringComparison.Ordinal));

                Assert.IsNotNull(line, $"{definition.Id} is held but does not appear on the appraisal panel");

                var percent = WeaponModDisplay.IntensityPercent(definition, weapon.GetProperty(definition.Record) ?? 0.0, WeaponModValue.MagnitudeScale());

                Assert.IsTrue(line.Contains($"[{percent}%]", StringComparison.Ordinal),
                    $"{definition.Id}: the panel line carries no intensity bracket - \"{line}\"");

                Assert.IsTrue(line.IndexOf('[') < line.IndexOf(':'),
                    $"{definition.Id}: the bracket must precede the magnitude - \"{line}\"");
            }
        }
    }
}
