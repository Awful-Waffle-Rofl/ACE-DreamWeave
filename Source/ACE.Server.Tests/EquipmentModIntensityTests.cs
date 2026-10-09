using System.Linq;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Server.EquipmentMods;
using ACE.Server.Managers;

namespace ACE.Server.Tests
{
    /// <summary>
    /// The intensity percentage on the equipment-mod panel (2026-08-07), carried over from the weapon-mod
    /// side so both systems read the same way: "Deadeye [83%]: +2.32% missile damage".
    ///
    /// SIMPLER HERE THAN ON WEAPONS, because this system already stored what the weapon side had to be
    /// changed to store. An equipment mod persists a POTENCY and resolves the magnitude live
    /// (EquipmentModValue.Resolve is linear in potency), so the figure is the stored potency and is already
    /// retune-invariant: both MaxMagnitude and equipment_mod_potency_scale cancel out of the ratio. No storage
    /// change was needed and none was made.
    ///
    /// TWO WAYS IT DIFFERS FROM THE WEAPON BRACKET, both inherent:
    ///   - no workmanship term, because equipment mods do not scale with workmanship at all;
    ///   - a lower and per-row floor (0.10 default, 0.25 on some rows) against the weapons' shared 0.60.
    /// So armour brackets span roughly 10-100 where weapon brackets span 60-100, and the two are NOT directly
    /// comparable. That is a property of the roll ranges, not of the display.
    /// </summary>
    [TestClass]
    public class EquipmentModIntensityTests
    {
        [ClassInitialize]
        public static void TestSetup(TestContext context)
        {
            DefaultPropertyManager.LoadDefaultProperties();
        }

        /// <summary>
        /// A perfect roll reads exactly 100 for EVERY row in the catalog, and the floor roll reads the floor.
        /// Asserted registry-wide rather than on a sample: a row added later whose ceiling the scale cannot
        /// represent fails here rather than shipping a panel that tops out at 97%.
        /// </summary>
        [TestMethod]
        public void Intensity_APerfectRollReadsOneHundredForEveryRow()
        {
            foreach (var mod in EquipmentModRegistry.AllMods)
            {
                Assert.AreEqual(100, EquipmentModDisplay.IntensityPercent(mod, 1.0),
                    $"{mod.Id}: a perfect roll must read 100%");

                var floor = EquipmentModRoller.MinPotency(mod);

                Assert.AreEqual((int)System.Math.Round(floor * 100.0, System.MidpointRounding.AwayFromZero),
                    EquipmentModDisplay.IntensityPercent(mod, floor),
                    $"{mod.Id}: the weakest legal roll must read its own potency floor");
            }
        }

        /// <summary>
        /// The figure tracks potency linearly, which is the whole contract - a player reading 50% has half the
        /// available strength of that mod, whatever its units or magnitude happen to be.
        /// </summary>
        [TestMethod]
        public void Intensity_TracksPotencyLinearly()
        {
            var deadeye = EquipmentModRegistry.Get(EquipmentModId.Deadeye);

            Assert.AreEqual(25, EquipmentModDisplay.IntensityPercent(deadeye, 0.25));
            Assert.AreEqual(50, EquipmentModDisplay.IntensityPercent(deadeye, 0.50));
            Assert.AreEqual(83, EquipmentModDisplay.IntensityPercent(deadeye, 0.83));
            Assert.AreEqual(100, EquipmentModDisplay.IntensityPercent(deadeye, 1.0));
        }

        /// <summary>
        /// RETUNE-INVARIANT, which is the property the weapon side needed a storage change to gain and this
        /// side has for free. Moving equipment_mod_potency_scale changes every magnitude in the catalog and
        /// must move no percentage at all: the scale appears in both the magnitude and the ceiling it is
        /// measured against, so it cancels.
        /// </summary>
        [TestMethod]
        public void Intensity_DoesNotMoveWhenTheGlobalScaleIsRetuned()
        {
            var prior = PropertyManager.GetDouble("equipment_mod_potency_scale").Item;

            try
            {
                var deadeye = EquipmentModRegistry.Get(EquipmentModId.Deadeye);

                foreach (var scale in new[] { 0.25, 1.0, 4.0 })
                {
                    PropertyManager.ModifyDouble("equipment_mod_potency_scale", scale);

                    Assert.AreEqual(70, EquipmentModDisplay.IntensityPercent(deadeye, 0.70),
                        $"scale {scale}: the reported intensity must describe the ROLL, not the catalog");
                }

                // ... while the magnitude beside it genuinely does move, so the invariance above is not vacuous
                PropertyManager.ModifyDouble("equipment_mod_potency_scale", 1.0);
                var atOne = EquipmentModValue.Resolve(deadeye, 0.70);

                PropertyManager.ModifyDouble("equipment_mod_potency_scale", 2.0);
                var atTwo = EquipmentModValue.Resolve(deadeye, 0.70);

                Assert.AreEqual(atOne * 2.0, atTwo, 1e-12, "control: the magnitude must double when the scale doubles");
            }
            finally
            {
                PropertyManager.ModifyDouble("equipment_mod_potency_scale", prior);
            }
        }

        /// <summary>
        /// Clamped to 1-100 at both ends. The floor keeps a working mod off 0%; the ceiling covers a
        /// contaminated shard row, which Resolve already clamps - the display must not disagree with it.
        /// </summary>
        [TestMethod]
        public void Intensity_IsClampedToOneThroughOneHundred()
        {
            var deadeye = EquipmentModRegistry.Get(EquipmentModId.Deadeye);

            Assert.AreEqual(1, EquipmentModDisplay.IntensityPercent(deadeye, 0.0001),
                "a potency below half a percent must floor at 1%, not report 0%");

            Assert.AreEqual(100, EquipmentModDisplay.IntensityPercent(deadeye, 7.0),
                "a contaminated potency must clamp at 100%, matching the clamp Resolve already applies");
        }

        /// <summary>
        /// Everything that cannot produce an honest figure returns 0, which is the caller's signal to omit the
        /// bracket. The muted-scale case is the operational one: equipment_mod_potency_scale = 0 drives every
        /// ceiling to zero while items still carry live potencies, so it is a state a player can appraise.
        /// </summary>
        [TestMethod]
        public void Intensity_UnreportableInputsReturnZeroRatherThanAFigure()
        {
            var deadeye = EquipmentModRegistry.Get(EquipmentModId.Deadeye);

            Assert.AreEqual(0, EquipmentModDisplay.IntensityPercent(null, 1.0), "a null definition has no ceiling");
            Assert.AreEqual(0, EquipmentModDisplay.IntensityPercent(deadeye, 0.0), "a zero potency has no intensity");
            Assert.AreEqual(0, EquipmentModDisplay.IntensityPercent(deadeye, -1.0), "a negative potency must not clamp up to 1%");
            Assert.AreEqual(0, EquipmentModDisplay.IntensityPercent(deadeye, double.NaN), "NaN must not divide into a figure");

            var prior = PropertyManager.GetDouble("equipment_mod_potency_scale").Item;

            try
            {
                PropertyManager.ModifyDouble("equipment_mod_potency_scale", 0.0);

                Assert.AreEqual(0, EquipmentModDisplay.IntensityPercent(deadeye, 0.70),
                    "a muted scale leaves no ceiling to measure against");

                Assert.AreEqual("Deadeye: +0% missile damage", EquipmentModDisplay.Describe(deadeye, 0.70),
                    "a muted scale must drop the bracket and keep the mod, not print an empty or zero one");
            }
            finally
            {
                PropertyManager.ModifyDouble("equipment_mod_potency_scale", prior);
            }
        }

        /// <summary>
        /// Every rollable potency lands inside the band its floor implies, for every row - never 0, never
        /// above 100. The uniform roll is now the only way the flow stamps a potency; the low-tier (Tiger Eye)
        /// fixed potency it also used to cover was removed when tiger eye became an armor tinker.
        /// </summary>
        [TestMethod]
        public void Intensity_EveryRollablePotencyLandsInsideItsBand()
        {
            foreach (var mod in EquipmentModRegistry.AllMods)
            {
                var floorPercent = (int)System.Math.Floor(EquipmentModRoller.MinPotency(mod) * 100.0);

                for (var i = 0; i < 100; i++)
                {
                    var percent = EquipmentModDisplay.IntensityPercent(mod, EquipmentModRoller.RollPotency(mod));

                    Assert.IsTrue(percent >= floorPercent && percent <= 100,
                        $"{mod.Id}: a rolled potency read {percent}%, outside the {floorPercent}-100 band its floor implies");
                }

                // the floor itself is the band's lower edge and must read inside it
                var atFloor = EquipmentModDisplay.IntensityPercent(mod, EquipmentModRoller.MinPotency(mod));

                Assert.IsTrue(atFloor >= floorPercent && atFloor <= 100,
                    $"{mod.Id}: the floor itself read {atFloor}%, outside its band");
            }
        }

        /// <summary>
        /// The exact rendered strings, pinned - name, bracket, colon, magnitude. A change to any part of the
        /// format is then a deliberate edit here rather than a silent shift in what every player reads.
        /// </summary>
        [TestMethod]
        public void Describe_RendersTheIntensityAheadOfTheMagnitude()
        {
            var deadeye = EquipmentModRegistry.Get(EquipmentModId.Deadeye);

            Assert.AreEqual("Deadeye [100%]: +2.8% missile damage", EquipmentModDisplay.Describe(deadeye, 1.0));
            Assert.AreEqual("Deadeye [50%]: +1.4% missile damage", EquipmentModDisplay.Describe(deadeye, 0.5));

            Assert.IsTrue(EquipmentModDisplay.GetMods(null).Count == 0, "a null item carries nothing");
        }

        // ---------------- the workmanship term (2026-08-07) ----------------

        /// <summary>
        /// An equipment mod now stores a ROLL FRACTION - potency scaled by the item's workmanship - exactly
        /// as a weapon mod does. This pins the arithmetic itself.
        ///
        /// A workmanship 10 item is unchanged from the previous behaviour, which is what makes the change
        /// close to invisible in practice: the 963 modded items on stage averaged workmanship 9.99 the day it
        /// landed. What it closes is modding a LOW-workmanship piece for the price of a perfect one.
        /// </summary>
        [TestMethod]
        public void RollFraction_ScalesPotencyByWorkmanship()
        {
            Assert.AreEqual(1.0, EquipmentModValue.RollFraction(1.0, 10.0), 1e-12, "a perfect roll on the best item is 1.0");
            Assert.AreEqual(0.5, EquipmentModValue.RollFraction(1.0, 5.0), 1e-12, "half the workmanship is half the fraction");
            Assert.AreEqual(0.5, EquipmentModValue.RollFraction(0.5, 10.0), 1e-12, "half the potency is half the fraction");
            Assert.AreEqual(0.35, EquipmentModValue.RollFraction(0.5, 7.0), 1e-12, "the two axes multiply");

            // clamped at both ends rather than trusted
            Assert.AreEqual(1.0, EquipmentModValue.RollFraction(4.0, 40.0), 1e-12, "out-of-range inputs clamp to a full roll, never above");
            Assert.AreEqual(0.0, EquipmentModValue.RollFraction(-1.0, 10.0), 1e-12, "a negative potency floors at zero");
            Assert.AreEqual(0.0, EquipmentModValue.RollFraction(1.0, double.NaN), 1e-12, "NaN workmanship reads as no roll rather than a full one");

            // NO WORKMANSHIP IS ZERO, WHICH IS EXACTLY WHY THE FLOW REFUSES IT UPSTREAM. Asserted here so the
            // refusal's reason is visible at the arithmetic rather than only in the manager: without it, a
            // workmanship-less item would take a mod worth nothing and look modded.
            Assert.AreEqual(0.0, EquipmentModValue.RollFraction(1.0, 0.0), 1e-12, "no workmanship is no roll");
        }

        /// <summary>
        /// The bracket now means the same thing on armour as on a weapon: a percentage of the best possible
        /// roll ON A WORKMANSHIP 10 ITEM. So a lesser item cannot reach 100 however well it rolls, and the
        /// figure answers "how good is this mod" rather than "how lucky was this roll".
        /// </summary>
        [TestMethod]
        public void Intensity_FoldsWorkmanshipInJustAsTheWeaponBracketDoes()
        {
            var deadeye = EquipmentModRegistry.Get(EquipmentModId.Deadeye);

            Assert.AreEqual(100, EquipmentModDisplay.IntensityPercent(deadeye, EquipmentModValue.RollFraction(1.0, 10.0)),
                "a perfect roll on a workmanship 10 item reads 100%");

            Assert.AreEqual(50, EquipmentModDisplay.IntensityPercent(deadeye, EquipmentModValue.RollFraction(1.0, 5.0)),
                "a perfect roll on a workmanship 5 item reads 50% - it cannot reach 100 however well it rolls");

            Assert.AreEqual(35, EquipmentModDisplay.IntensityPercent(deadeye, EquipmentModValue.RollFraction(0.5, 7.0)),
                "both axes reach the bracket");
        }

        /// <summary>
        /// Every catalog row renders a bracket at a normal roll, and the bracket always precedes the
        /// magnitude. Registry-wide, so a row whose DisplayFormat somehow swallowed the prefix is caught.
        /// </summary>
        [TestMethod]
        public void Describe_EveryRowCarriesABracketAheadOfItsMagnitude()
        {
            foreach (var mod in EquipmentModRegistry.AllMods)
            {
                var line = EquipmentModDisplay.Describe(mod, 0.5);

                Assert.IsTrue(line.Contains($"{mod.DisplayName} ["), $"{mod.Id}: no bracket follows the name - \"{line}\"");
                Assert.IsTrue(line.IndexOf('[') < line.IndexOf(':'), $"{mod.Id}: the bracket must precede the magnitude - \"{line}\"");
                Assert.IsTrue(line.Contains($"[{EquipmentModDisplay.IntensityPercent(mod, 0.5)}%]"), $"{mod.Id}: bracket disagrees with IntensityPercent - \"{line}\"");
            }
        }
    }
}
