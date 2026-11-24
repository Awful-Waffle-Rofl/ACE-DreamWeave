using System;

using ACE.Server.ThreadDungeons;
using ACE.Server.WorldEvents;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ACE.Server.Tests
{
    /// <summary>
    /// The World Events stat-uplift dials. Every read goes through the DialSource seam (or the natural
    /// no-config fallback), so no PropertyManager key is seeded or shared with another test class.
    /// </summary>
    [TestClass]
    public class WorldEventUpliftDialsTests
    {
        private Func<WorldEventUpliftDials> original;

        [TestInitialize]
        public void Save() => original = WorldEventUplift.DialSource;

        [TestCleanup]
        public void Restore() => WorldEventUplift.DialSource = original;

        [TestMethod]
        public void Defaults_AreOn_AndTheCapIsIndependentOfTheThreadsCap()
        {
            var d = WorldEventUpliftDials.Defaults;

            Assert.IsTrue(d.StatCurve);
            Assert.IsTrue(d.ThreadsUplift);
            Assert.AreEqual(508.0, d.MagicAttackSkillCap);
            Assert.AreEqual(508.0, WorldEventUpliftDials.DefaultMagicAttackSkillCap);
        }

        [TestMethod]
        public void Current_WithDefaultSource_ReturnsDefaults()
        {
            WorldEventUplift.DialSource = () => WorldEventUpliftDials.Defaults;

            var d = WorldEventUplift.Current();

            Assert.IsTrue(d.StatCurve);
            Assert.IsTrue(d.ThreadsUplift);
            Assert.AreEqual(508.0, d.MagicAttackSkillCap);
        }

        [TestMethod]
        public void ReadFromProperties_WithNoConfig_FallsBackToDefaults()
        {
            // No shard config exists in a unit test, so the PropertyManager reads throw and are caught.
            var d = WorldEventUplift.ReadFromProperties();

            Assert.IsTrue(d.StatCurve);
            Assert.IsTrue(d.ThreadsUplift);
            Assert.AreEqual(508.0, d.MagicAttackSkillCap);
        }

        [TestMethod]
        public void Current_PassesFlagsThrough()
        {
            WorldEventUplift.DialSource = () => new WorldEventUpliftDials(false, false, 300);

            var d = WorldEventUplift.Current();

            Assert.IsFalse(d.StatCurve);
            Assert.IsFalse(d.ThreadsUplift);
            Assert.AreEqual(300.0, d.MagicAttackSkillCap);
        }

        [TestMethod]
        public void Current_SanitizesTheCap()
        {
            WorldEventUplift.DialSource = () => new WorldEventUpliftDials(true, true, double.NaN);
            Assert.AreEqual(508.0, WorldEventUplift.Current().MagicAttackSkillCap, "NaN reads as the default");

            WorldEventUplift.DialSource = () => new WorldEventUpliftDials(true, true, double.PositiveInfinity);
            Assert.AreEqual(508.0, WorldEventUplift.Current().MagicAttackSkillCap, "Infinity reads as the default");

            WorldEventUplift.DialSource = () => new WorldEventUpliftDials(true, true, double.NegativeInfinity);
            Assert.AreEqual(508.0, WorldEventUplift.Current().MagicAttackSkillCap, "-Infinity reads as the default");

            WorldEventUplift.DialSource = () => new WorldEventUpliftDials(true, true, -5);
            Assert.AreEqual(0.0, WorldEventUplift.Current().MagicAttackSkillCap, "negative reads as off");

            WorldEventUplift.DialSource = () => new WorldEventUpliftDials(true, true, 0);
            Assert.AreEqual(0.0, WorldEventUplift.Current().MagicAttackSkillCap, "0 is off");

            WorldEventUplift.DialSource = () => new WorldEventUpliftDials(true, true, 400.4);
            Assert.AreEqual(400.0, WorldEventUplift.Current().MagicAttackSkillCap, "rounded to a whole point");
        }
    }
}