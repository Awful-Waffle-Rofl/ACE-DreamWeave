using System.Collections.Generic;
using System.Linq;

using ACE.Server.HazardMechanics;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ACE.Server.Tests.HazardMechanics
{
    /// <summary>HazardControlRequest: the /hazard control argument parser.</summary>
    [TestClass]
    public class HazardControlRequestTests
    {
        private static bool Parse(string line, out int id, out List<HazardControl> controls, out string error)
            => HazardControlRequest.TryParse(line.Split(' ', System.StringSplitOptions.RemoveEmptyEntries), out id, out controls, out error);

        [TestMethod]
        public void HashId_AndRepeatedKeys_KeepOrder()
        {
            Assert.IsTrue(Parse("#3 rate=0.1 rate=-0.2", out var id, out var controls, out var error), error);
            Assert.AreEqual(3, id);
            Assert.AreEqual(2, controls.Count);
            Assert.AreEqual("rate", controls[0].Key);
            Assert.AreEqual(0.1, controls[0].Value, 1e-12);
            Assert.AreEqual(-0.2, controls[1].Value, 1e-12);
        }

        [TestMethod]
        public void PlainId_LowerCasesKeys()
        {
            Assert.IsTrue(Parse("12 RATE=1 Set_Z=-44.5", out var id, out var controls, out var error), error);
            Assert.AreEqual(12, id);
            CollectionAssert.AreEqual(new[] { "rate", "set_z" }, controls.Select(c => c.Key).ToList());
            Assert.AreEqual(-44.5, controls[1].Value, 1e-12);
        }

        [TestMethod]
        public void BadInput_IsRefusedInOneLine()
        {
            foreach (var line in new[] { "3", "x rate=1", "3 rate=fast", "3 rate=NaN", "3 rate", "3 rate=Infinity", "3 =4", "3 rate=" })
            {
                Assert.IsFalse(Parse(line, out _, out _, out var error), line);
                Assert.IsFalse(string.IsNullOrEmpty(error), line);
                Assert.IsFalse(error.Contains('\n'), "one line: " + error);
            }

            Assert.IsFalse(HazardControlRequest.TryParse(new string[0], out _, out _, out var empty));
            Assert.IsFalse(string.IsNullOrEmpty(empty));
            Assert.IsFalse(HazardControlRequest.TryParse(null, out _, out _, out empty));
        }
    }
}
