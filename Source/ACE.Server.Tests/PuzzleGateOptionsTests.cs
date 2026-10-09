using System;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Server.PuzzleGates;

namespace ACE.Server.Tests
{
    [TestClass]
    public class PuzzleGateOptionsTests
    {
        private static bool Parse(string line, out PuzzleGateOptions o, out string err)
        {
            return PuzzleGateOptions.TryParse(line.Split(' ', StringSplitOptions.RemoveEmptyEntries), out o, out err);
        }

        private static string Reject(string line)
        {
            Assert.IsFalse(Parse(line, out var o, out var err), "should reject: " + line);
            Assert.IsNull(o);
            Assert.IsFalse(string.IsNullOrEmpty(err));
            return err;
        }

        [TestMethod]
        public void Defaults_AreApplied()
        {
            Assert.IsTrue(Parse("beam", out var o, out var err), err);
            Assert.AreEqual(PuzzleGateType.Beam, o.Type);
            Assert.AreEqual(PuzzleGateTunables.DefaultN, o.N);
            Assert.AreEqual(PuzzleGateTunables.DefaultRounds, o.Rounds);
            Assert.AreEqual(PuzzleGateTunables.DefaultLockoutSeconds, o.LockoutSeconds);
            Assert.AreEqual(2, o.Beams);
            Assert.IsNull(o.Seed);
        }

        [TestMethod]
        public void FullGrammar_Parses()
        {
            Assert.IsTrue(Parse("beam n=5 beams=3 rounds=4 seed=-7 lockout=0 ambush=1234x3 axis=up", out var o, out var err), err);
            Assert.AreEqual(5, o.N);
            Assert.AreEqual(3, o.Beams);
            Assert.AreEqual(4, o.Rounds);
            Assert.AreEqual(-7, o.Seed);
            Assert.AreEqual(0, o.LockoutSeconds);
            Assert.AreEqual(1234u, o.AmbushWcid);
            Assert.AreEqual(3, o.AmbushCount);
            Assert.AreEqual(PuzzleBeamAxis.Up, o.Axis);

            Assert.IsTrue(Parse("odd diff=glow ambush=99", out o, out err), err);
            Assert.AreEqual(PuzzleOddChannel.Glow, o.Diff);
            Assert.AreEqual(1, o.AmbushCount);

            Assert.IsTrue(Parse("shuffle spots=6 radius=12.5", out o, out err), err);
            Assert.AreEqual(6, o.Spots);
            Assert.AreEqual(12.5f, o.Radius);
            Assert.AreEqual(1, o.N);
        }

        [TestMethod]
        public void UnknownKey_IsRejectedByName()
        {
            StringAssert.Contains(Reject("sigil colour=red"), "colour");
        }

        [TestMethod]
        public void UnknownType_AndMissingType_AreRejected()
        {
            StringAssert.Contains(Reject("maze"), "maze");
            Assert.IsFalse(PuzzleGateOptions.TryParse(new string[0], out _, out var err));
            Assert.IsFalse(string.IsNullOrEmpty(err));
        }

        [TestMethod]
        public void NOutOfRange_IsRejected()
        {
            StringAssert.Contains(Reject("sigil n=1"), "n must be");
            StringAssert.Contains(Reject("odd n=6"), "n must be");
            StringAssert.Contains(Reject("sigil n=abc"), "n must be");
            Assert.IsTrue(Parse("sigil n=4", out _, out _));
            Assert.IsTrue(Parse("odd n=5", out _, out _));
            Assert.IsTrue(Parse("beam n=5 beams=4", out _, out _));
            Assert.IsTrue(Parse("beam n=2", out _, out _), "beam keeps 2-5");
        }

        [TestMethod]
        public void PickTypes_AcceptOnlyFourOrFive_AndFlagAnOmittedN()
        {
            foreach (var type in new[] { "sigil", "odd" })
            {
                foreach (var bad in new[] { 2, 3, 6 })
                {
                    var err = Reject($"{type} n={bad}");
                    StringAssert.Contains(err, "n must be 4-5 for " + type);
                    StringAssert.Contains(err, "omit n=");
                }

                Assert.IsTrue(Parse($"{type} n=4", out var o, out var e), e);
                Assert.IsTrue(o.NExplicit);
                Assert.AreEqual(4, o.N);

                Assert.IsTrue(Parse(type, out o, out e), e);
                Assert.IsFalse(o.NExplicit, type + " with n omitted is drawn by the generator");
            }
        }

        [TestMethod]
        public void ColourBoundOverflow_IsRejected()
        {
            Assert.IsTrue(Parse("sigil n=5", out _, out var sigilErr), sigilErr + " (sigil is no longer palette-bound)");
            StringAssert.Contains(Reject("beam n=5 beams=5"), "beams must be 1-4");
            Reject("beam beams=5");
        }

        [TestMethod]
        public void BeamsGreaterThanN_IsRejected()
        {
            StringAssert.Contains(Reject("beam n=3 beams=4"), "beams");
            Assert.IsTrue(Parse("beam n=3 beams=3", out _, out _));
            StringAssert.Contains(Reject("beam beams=6"), "beams must be");
            StringAssert.Contains(Reject("beam beams=0"), "beams must be");
        }

        [TestMethod]
        public void RoundsOutOfRange_IsRejected()
        {
            StringAssert.Contains(Reject("odd rounds=0"), "rounds must be");
            StringAssert.Contains(Reject("odd rounds=6"), "rounds must be");
            Assert.IsTrue(Parse("odd rounds=1", out _, out _));
            Assert.IsTrue(Parse("odd rounds=5", out _, out _));
        }

        [TestMethod]
        public void BadAmbush_IsRejected()
        {
            Reject("odd ambush=0");
            Reject("odd ambush=12x0");
            Reject("odd ambush=12x6");
            Reject("odd ambush=abc");
        }

        [TestMethod]
        public void MalformedTokens_DuplicatesAndInapplicableKeys_AreRejected()
        {
            Reject("sigil n");
            Reject("sigil n=");
            Reject("sigil =3");
            StringAssert.Contains(Reject("sigil n=4 n=5"), "more than once");
            StringAssert.Contains(Reject("sigil beams=2"), "does not apply");
            StringAssert.Contains(Reject("shuffle n=3"), "does not apply");
            StringAssert.Contains(Reject("odd diff=blink"), "diff must be");
            StringAssert.Contains(Reject("beam axis=left"), "axis must be");
            Reject("shuffle radius=1");
            Reject("shuffle radius=nan");
            Reject("shuffle spots=1");
            Reject("sigil lockout=61");
            Reject("sigil seed=1.5");
        }

        [TestMethod]
        public void Axis_AppliesToSigilAndBeam_Only()
        {
            Assert.IsTrue(Parse("sigil axis=up", out var o, out var err), err);
            Assert.AreEqual(PuzzleBeamAxis.Up, o.Axis);

            Assert.IsTrue(Parse("beam axis=up", out o, out err), err);
            Assert.AreEqual(PuzzleBeamAxis.Up, o.Axis);

            StringAssert.Contains(Reject("odd axis=up"), "does not apply");
            StringAssert.Contains(Reject("shuffle axis=up"), "does not apply");
        }
    }
}