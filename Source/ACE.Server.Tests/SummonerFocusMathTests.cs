using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Server.Entity;

namespace ACE.Server.Tests
{
    /// <summary>The pure Summoner casters arithmetic (SummonerFocusMath); no world state, no PropertyManager.</summary>
    [TestClass]
    public class SummonerFocusMathTests
    {
        [TestMethod]
        public void Constants_AreTheDesignValues()
        {
            Assert.AreEqual(1.0, SummonerFocusMath.DefaultPetDamageScale);
            Assert.AreEqual(0.0, SummonerFocusMath.DefaultPetDefenseScale);
            Assert.AreEqual(0.25, SummonerFocusMath.DefaultPetDefenseFloor);
            Assert.AreEqual(0.25, SummonerFocusMath.DefaultWielderDamageMod);
        }

        [TestMethod]
        public void DealtMultiplier()
        {
            Assert.AreEqual(1.70, SummonerFocusMath.DealtMultiplier(1.14, 5.0), 1e-9);
            Assert.AreEqual(1.0, SummonerFocusMath.DealtMultiplier(0.90, 5.0), 1e-9, "a mod below 1 never penalises the pet");
            Assert.AreEqual(1.0, SummonerFocusMath.DealtMultiplier(1.14, 0.0), 1e-9, "scale 0 is off");
        }

        [TestMethod]
        public void TakenMultiplier()
        {
            Assert.AreEqual(0.65, SummonerFocusMath.TakenMultiplier(1.14, 2.5, 0.25), 1e-9);
            Assert.AreEqual(0.25, SummonerFocusMath.TakenMultiplier(1.50, 2.5, 0.25), 1e-9, "clamped at the floor");
            Assert.AreEqual(1.0, SummonerFocusMath.TakenMultiplier(0.90, 2.5, 0.25), 1e-9, "a mod below 1 never makes the pet fragile");
        }

        [TestMethod]
        public void DefaultScales_PassTheBonusOneToOneAndLeavePetsUnchangedOnDefense()
        {
            Assert.AreEqual(1.27, SummonerFocusMath.DealtMultiplier(1.27, SummonerFocusMath.DefaultPetDamageScale), 1e-9, "a 1.27 focus gives +27%");
            Assert.AreEqual(1.0, SummonerFocusMath.TakenMultiplier(1.27, SummonerFocusMath.DefaultPetDefenseScale, SummonerFocusMath.DefaultPetDefenseFloor), 0.0, "defense scale 0 is exactly x1.0");
        }

        [TestMethod]
        public void DefenseScaleZero_IsHonouredNotTreatedAsInvalid()
        {
            Assert.AreEqual(0.0, SummonerFocusMath.SanitizeScale(0.0, 2.5), "0 is a valid scale; only NaN, infinity and negatives fall back");
            Assert.AreEqual(1.0, SummonerFocusMath.TakenMultiplier(1.50, 0.0, 0.25), 0.0);
            Assert.AreEqual(1.0, SummonerFocusMath.TakenMultiplier(1.50, 0.0, 0.0), 0.0);
        }

        [TestMethod]
        public void SanitizeScale()
        {
            Assert.AreEqual(5.0, SummonerFocusMath.SanitizeScale(double.NaN, 5.0));
            Assert.AreEqual(5.0, SummonerFocusMath.SanitizeScale(-1, 5.0));
            Assert.AreEqual(5.0, SummonerFocusMath.SanitizeScale(double.PositiveInfinity, 5.0));
            Assert.AreEqual(3.0, SummonerFocusMath.SanitizeScale(3.0, 5.0));
        }

        [TestMethod]
        public void SanitizeUnit()
        {
            Assert.AreEqual(0.25, SummonerFocusMath.SanitizeUnit(1.5, 0.25));
            Assert.AreEqual(0.25, SummonerFocusMath.SanitizeUnit(-0.1, 0.25));
            Assert.AreEqual(0.25, SummonerFocusMath.SanitizeUnit(double.NaN, 0.25));
            Assert.AreEqual(0.4, SummonerFocusMath.SanitizeUnit(0.4, 0.25));
        }
    }
}
