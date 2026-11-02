using System;
using System.Collections.Generic;
using System.IO;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Entity;
using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Entity.Models;
using ACE.Server.Entity;
using ACE.Server.WorldObjects;

namespace ACE.Server.Tests
{
    /// <summary>
    /// CreationRoll: pure parse, grid membership with inclusive ends, all-or-nothing, spec removal. No PropertyManager reads.
    /// </summary>
    [TestClass]
    public class CreationRollSpecTests
    {
        private static uint nextGuid = 0x7D100000;   // static guid range, clear of GuidManager
        private static uint nextWcid = 992000;

        private const string FirstItemSpec = "v1|f29=1.13:1.27:0.01;f62=1.13:1.27:0.01";

        private static WorldObject MakeItem(string spec)
        {
            var weenie = new Weenie
            {
                WeenieClassId = nextWcid++,
                WeenieType = WeenieType.Clothing,
                PropertiesString = new Dictionary<PropertyString, string> { { PropertyString.Name, "Test Item" } },
            };

            if (spec != null)
                weenie.PropertiesString[PropertyString.CreationRollSpec] = spec;

            return new Clothing(weenie, new ObjectGuid(nextGuid++));
        }

        [TestMethod]
        public void EnumId_Is9031()
        {
            Assert.AreEqual(9031, (int)PropertyString.CreationRollSpec);
        }

        [TestMethod]
        public void Parse_ValidSpec_ReturnsEntries()
        {
            var e = CreationRoll.Parse(FirstItemSpec);
            Assert.IsNotNull(e);
            Assert.AreEqual(2, e.Count);
            Assert.IsTrue(e[0].IsFloat);
            Assert.AreEqual(29, e[0].Id);
            Assert.AreEqual(15, e[0].GridCount);   // 1.13 .. 1.27 step 0.01
            Assert.AreEqual(62, e[1].Id);

            var i = CreationRoll.Parse("v1|i44=50:55:1");
            Assert.IsFalse(i[0].IsFloat);
            Assert.AreEqual(6, i[0].GridCount);
        }

        [DataTestMethod]
        [DataRow(null)]
        [DataRow("")]
        [DataRow("v2|f29=1:2:1")]            // wrong version
        [DataRow("f29=1:2:1")]               // no version
        [DataRow("v1|")]                     // empty body
        [DataRow("v1|f29=1:2:1;")]           // empty trailing entry
        [DataRow("v1|f29=1:2")]              // missing step
        [DataRow("v1|f29=1:2:1:3")]          // extra field
        [DataRow("v1|f29=abc:2:1")]          // non-numeric
        [DataRow("v1|f=1:2:1")]              // missing id
        [DataRow("v1|fx=1:2:1")]             // non-numeric id
        [DataRow("v1|f65565=1:2:1")]         // id wraps to 29 in a ushort cast
        [DataRow("v1|i65536=1:2:1")]         // id just past ushort.MaxValue
        [DataRow("v1|f29 1:2:1")]          // missing '='
        [DataRow("v1|f29=2:1:1")]            // min > max
        [DataRow("v1|f29=1:2:0")]            // step zero
        [DataRow("v1|f29=1:2:-0.5")]         // step negative
        [DataRow("v1|x29=1:2:1")]            // unknown type letter
        [DataRow("v1|i44=1.5:3:1")]          // int, non-integer min
        [DataRow("v1|i44=1:3:0.5")]          // int, non-integer step
        public void Parse_Malformed_ReturnsNull(string spec)
        {
            Assert.IsNull(CreationRoll.Parse(spec));
        }

        [TestMethod]
        public void Apply_FloatGrid_HitsBothInclusiveEndsAndNeverLeavesTheGrid()
        {
            var rng = new Random(12345);
            var seen = new HashSet<double>();

            for (var n = 0; n < 5000; n++)
            {
                var wo = MakeItem(FirstItemSpec);
                CreationRoll.Apply(wo, rng);

                var v = wo.GetProperty((PropertyFloat)29).Value;
                seen.Add(v);

                Assert.IsTrue(v >= 1.13 && v <= 1.27, $"{v} outside range");
                Assert.AreEqual(Math.Round(v, 2), v, "value is not on the 0.01 grid");
            }

            Assert.IsTrue(seen.Contains(1.13), "min never rolled");
            Assert.IsTrue(seen.Contains(1.27), "max never rolled");
            Assert.AreEqual(15, seen.Count, "every grid point should be reachable");
        }

        [TestMethod]
        public void Apply_IntGrid_InclusiveAndOnGrid()
        {
            var rng = new Random(7);
            var seen = new HashSet<int>();

            for (var n = 0; n < 2000; n++)
            {
                var wo = MakeItem("v1|i44=50:60:5");
                CreationRoll.Apply(wo, rng);
                seen.Add(wo.GetProperty((PropertyInt)44).Value);
            }

            CollectionAssert.AreEquivalent(new[] { 50, 55, 60 }, new List<int>(seen));
        }

        [TestMethod]
        public void Apply_StepThatDoesNotDivideRange_NeverExceedsMax()
        {
            var rng = new Random(99);
            var seen = new HashSet<double>();

            for (var n = 0; n < 1000; n++)
            {
                var wo = MakeItem("v1|f29=1:2:0.4");   // 1.0, 1.4, 1.8
                CreationRoll.Apply(wo, rng);
                seen.Add(wo.GetProperty((PropertyFloat)29).Value);
            }

            CollectionAssert.AreEquivalent(new[] { 1.0, 1.4, 1.8 }, new List<double>(seen));
        }

        [TestMethod]
        public void Apply_DegenerateRange_YieldsThatValue()
        {
            var wo = MakeItem("v1|f29=1.5:1.5:0.01");
            CreationRoll.Apply(wo, new Random(1));
            Assert.AreEqual(1.5, wo.GetProperty((PropertyFloat)29).Value);
        }

        [TestMethod]
        public void Apply_PartlyBadSpec_AppliesNothing()
        {
            var wo = MakeItem("v1|f29=1.13:1.27:0.01;i44=1.5:2:1");
            CreationRoll.Apply(wo, new Random(1));

            Assert.IsNull(wo.GetProperty((PropertyFloat)29));
            Assert.IsNull(wo.GetProperty((PropertyInt)44));
            Assert.IsNull(wo.GetProperty(PropertyString.CreationRollSpec), "a rejected spec must still be stripped");
        }

        [TestMethod]
        public void Apply_RemovesSpecFromInstance()
        {
            var wo = MakeItem(FirstItemSpec);
            Assert.IsNotNull(wo.GetProperty(PropertyString.CreationRollSpec));

            CreationRoll.Apply(wo, new Random(1));

            Assert.IsNull(wo.GetProperty(PropertyString.CreationRollSpec));
            Assert.IsNotNull(wo.GetProperty((PropertyFloat)29));
            Assert.IsNotNull(wo.GetProperty((PropertyFloat)62));
        }

        [TestMethod]
        public void Apply_NoSpec_ChangesNothing()
        {
            var wo = MakeItem(null);
            CreationRoll.Apply(wo, new Random(1));

            Assert.IsNull(wo.GetProperty((PropertyFloat)29));
            Assert.AreEqual("Test Item", wo.GetProperty(PropertyString.Name));
        }

        [TestMethod]
        public void Apply_ServerRandomOverload_StaysOnGrid()
        {
            for (var n = 0; n < 200; n++)
            {
                var wo = MakeItem("v1|i44=50:52:1");
                CreationRoll.Apply(wo);
                var v = wo.GetProperty((PropertyInt)44).Value;
                Assert.IsTrue(v >= 50 && v <= 52);
            }
        }

        [TestMethod]
        public void Factory_AppliesRollBeforeStampingGearRatings()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            string path = null;
            while (dir != null)
            {
                var candidate = Path.Combine(dir.FullName, "Source", "ACE.Server", "Factories", "WorldObjectFactory.cs");
                if (File.Exists(candidate)) { path = candidate; break; }
                dir = dir.Parent;
            }
            Assert.IsNotNull(path, "could not find WorldObjectFactory.cs by walking up from the test directory");

            var src = File.ReadAllText(path);
            var roll = src.IndexOf("CreationRoll.Apply(worldObject);", StringComparison.Ordinal);
            var stamp = src.IndexOf("EquipmentModManager.StampOriginalGearRatings(worldObject);", StringComparison.Ordinal);

            Assert.IsTrue(roll >= 0, "CreationRoll.Apply call missing from WorldObjectFactory");
            Assert.IsTrue(stamp >= 0, "StampOriginalGearRatings call missing from WorldObjectFactory");
            Assert.IsTrue(roll < stamp, "the creation roll must run BEFORE the born-with gear rating stamp");
        }
    }
}
