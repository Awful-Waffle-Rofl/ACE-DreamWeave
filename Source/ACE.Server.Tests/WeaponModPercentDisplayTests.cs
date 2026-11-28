using System.Linq;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Server.WeaponMods;

namespace ACE.Server.Tests
{
    /// <summary>
    /// A weapon mod whose magnitude is a fraction (0.2 = +20%) must render as a percentage. Attunement,
    /// Tension and Leverage once printed the raw fraction ("+0.2 elemental damage mod") because their rows
    /// carried no DisplayScale, while every other fractional row read as a percent.
    /// </summary>
    [TestClass]
    public class WeaponModPercentDisplayTests
    {
        [TestMethod]
        public void Attunement_renders_its_fraction_as_a_percentage()
        {
            Assert.AreEqual("+20% elemental damage mod", WeaponModRegistry.Get(WeaponModId.Attunement).Format(0.2));
        }

        [TestMethod]
        public void Tension_and_Leverage_render_their_fraction_as_a_percentage()
        {
            Assert.AreEqual("+50% damage mod", WeaponModRegistry.Get(WeaponModId.Tension).Format(0.5));
            Assert.AreEqual("+21.5% damage mod", WeaponModRegistry.Get(WeaponModId.Leverage).Format(0.215));
        }

        /// <summary>
        /// Registry-wide: a non-integer, non-binary row whose ceiling is at most 1 stores a fraction, so it must
        /// scale by 100 and print a percent sign. A new fractional row added without a DisplayScale fails here.
        /// </summary>
        [TestMethod]
        public void Every_fractional_row_displays_as_a_percentage()
        {
            var offenders = WeaponModRegistry.AllMods
                .Where(m => !m.IsInteger && !m.Binary && m.MaxRoll <= 1.0)
                .Where(m => m.DisplayScale != 100.0 || !m.DisplayFormat.Contains("%"))
                .Select(m => $"{m.Id} ({m.DisplayFormat}, scale {m.DisplayScale})")
                .ToList();

            Assert.AreEqual(0, offenders.Count, "fractional rows rendering as raw fractions: " + string.Join("; ", offenders));
        }
    }
}
