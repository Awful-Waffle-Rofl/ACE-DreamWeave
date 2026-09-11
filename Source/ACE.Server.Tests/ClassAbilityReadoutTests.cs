using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Server.ClassAbilities;

namespace ACE.Server.Tests
{
    /// <summary>
    /// Tests for the pure /abilities list line formatter (ClassAbilityCommands.FormatReadoutLine and Num).
    /// These are the only parts of the readout display that don't need a live Player - the three exemplar
    /// abilities' GetReadout implementations (SavageBlowsAbility, AcidProcAbility, FrenzyAbility) call
    /// Player/PropertyManager and are exercised in-game instead (see the project's Player-static-initializer
    /// test constraint).
    ///
    /// FormatReadoutLine and Num are private on ClassAbilityCommands, so these tests reach them via
    /// reflection rather than widening the handler's public surface just for testability.
    /// </summary>
    [TestClass]
    public class ClassAbilityReadoutTests
    {
        private static readonly System.Reflection.MethodInfo FormatReadoutLineMethod =
            typeof(ACE.Server.Command.Handlers.ClassAbilityCommands).GetMethod(
                "FormatReadoutLine", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);

        private static readonly System.Reflection.MethodInfo NumMethod =
            typeof(ACE.Server.Command.Handlers.ClassAbilityCommands).GetMethod(
                "Num", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);

        private static string FormatReadoutLine(ClassAbilityDefinition definition, int rank, ClassAbilityReadout readout) =>
            (string)FormatReadoutLineMethod.Invoke(null, new object[] { definition, rank, readout });

        private static string Num(double value) =>
            (string)NumMethod.Invoke(null, new object[] { value });

        private static ClassAbilityDefinition Def(string displayName, int maxRank) => new ClassAbilityDefinition
        {
            Id = ClassAbilityId.SavageBlows, // arbitrary - DisplayName/MaxRank are all FormatReadoutLine reads
            DisplayName = displayName,
            MaxRank = maxRank,
        };

        [TestMethod]
        public void FormatReadoutLine_NoCap_RendersHeadlineAndTriple()
        {
            var def = Def("Savage Blows", 3);
            var readout = new ClassAbilityReadout
            {
                HasValue = true,
                Skill = 18,
                Affinity = 16,
                Gear = 2.7,
                Effective = 36.7,
                Unit = "%",
                Label = "melee dmg",
                Per = null,
                CapNote = null,
            };

            var line = FormatReadoutLine(def, 3, readout);

            Assert.AreEqual("  Savage Blows 3/3  36.7% melee dmg  [18/16/2.7]", line);
        }

        [TestMethod]
        public void FormatReadoutLine_Capped_UsesEffectiveAsHeadlineAndAppendsCapNote()
        {
            var def = Def("Acid Proc", 3);
            var readout = new ClassAbilityReadout
            {
                HasValue = true,
                Skill = 20,
                Affinity = 20,
                Gear = 30.9,
                Effective = 70.9,
                Unit = "%",
                Label = "proc",
                Per = null,
                CapNote = "affinity cap",
            };

            var line = FormatReadoutLine(def, 3, readout);

            Assert.AreEqual("  Acid Proc 3/3  70.9% proc  [20/20/30.9] capped: affinity cap", line);
            Assert.IsTrue(readout.Capped);
        }

        [TestMethod]
        public void FormatReadoutLine_PerStack_InsertsPerBeforeLabel()
        {
            var def = Def("Frenzy", 3);
            var readout = new ClassAbilityReadout
            {
                HasValue = true,
                Skill = 5,
                Affinity = 0.1,
                Gear = 0.3,
                Effective = 5.4,
                Unit = "%",
                Label = "atk speed",
                Per = "/stack",
                CapNote = null,
            };

            var line = FormatReadoutLine(def, 3, readout);

            Assert.AreEqual("  Frenzy 3/3  5.4%/stack atk speed  [5/0.1/0.3]", line);
        }

        [TestMethod]
        public void FormatReadoutLine_NoReadout_RendersRankOnly()
        {
            var def = Def("Whirlwind", 1);
            var readout = default(ClassAbilityReadout); // HasValue defaults to false

            var line = FormatReadoutLine(def, 1, readout);

            Assert.AreEqual("  Whirlwind 1/1", line);
        }

        [TestMethod]
        public void Num_SuppressesTrailingZeroAndRoundsToOneDecimal()
        {
            Assert.AreEqual("18", Num(18.0));
            Assert.AreEqual("2.7", Num(2.7));
            Assert.AreEqual("36.7", Num(36.66));
            Assert.AreEqual("0", Num(0.0));
            Assert.AreEqual("5.4", Num(5.36));
        }
    }
}
