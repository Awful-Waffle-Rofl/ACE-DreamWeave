using ACE.Entity.Models;
using ACE.Server.WorldObjects.Managers;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ACE.Server.Tests
{
    [TestClass]
    public class FocusDotDamageTests
    {
        [TestMethod]
        public void Gate_FullTruthTable()
        {
            for (var i = 0; i < 16; i++)
            {
                var src = (i & 1) != 0;
                var tgt = (i & 2) != 0;
                var aeth = (i & 4) != 0;
                var proc = (i & 8) != 0;

                Assert.AreEqual(src && !tgt && !aeth && !proc, EnchantmentManager.ShouldApplyFocusDotMod(src, tgt, aeth, proc), $"case {i}");
            }
        }

        [TestMethod]
        public void Unmarked_IsNotProcSourced()
        {
            Assert.IsFalse(ProcDotMarks.IsProcSourced(new PropertiesEnchantmentRegistry()));
            Assert.IsFalse(ProcDotMarks.IsProcSourced(null));
        }

        [TestMethod]
        public void Mark_IsPerEntryAndOverwrittenByLatestApplication()
        {
            var a = new PropertiesEnchantmentRegistry();
            var b = new PropertiesEnchantmentRegistry();

            ProcDotMarks.Set(a, true);
            Assert.IsTrue(ProcDotMarks.IsProcSourced(a));
            Assert.IsFalse(ProcDotMarks.IsProcSourced(b));

            ProcDotMarks.Set(a, false);
            Assert.IsFalse(ProcDotMarks.IsProcSourced(a));

            ProcDotMarks.Set(a, true);
            Assert.IsTrue(ProcDotMarks.IsProcSourced(a));

            ProcDotMarks.Set(null, true);
        }
    }
}