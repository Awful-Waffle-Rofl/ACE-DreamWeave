using System;
using System.Linq;
using System.Reflection;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Server.PuzzleGates;

namespace ACE.Server.Tests
{
    [TestClass]
    public class PuzzleGateRulesTests
    {
        private static readonly DateTime T0 = new DateTime(2026, 10, 6, 12, 0, 0, DateTimeKind.Utc);

        [TestMethod]
        public void ThreeRounds_SolveOnTheThirdCorrect()
        {
            var r = new PuzzleGateRules(3, 5);
            Assert.AreEqual(PuzzleActivation.RoundAdvanced, r.Activate(true, T0));
            Assert.AreEqual(1, r.RoundsCompleted(T0));
            Assert.IsFalse(r.Solved);
            Assert.AreEqual(PuzzleActivation.RoundAdvanced, r.Activate(true, T0));
            Assert.AreEqual(2, r.RoundsCompleted(T0));
            Assert.IsFalse(r.Solved);
            Assert.AreEqual(PuzzleActivation.Solved, r.Activate(true, T0));
            Assert.IsTrue(r.Solved);
        }

        [TestMethod]
        public void OneRound_SolvesOnFirstCorrect()
        {
            var r = new PuzzleGateRules(1, 5);
            Assert.AreEqual(PuzzleActivation.Solved, r.Activate(true, T0));
        }

        [TestMethod]
        public void Wrong_ResetsProgress_AndNeedsAFullRunAfterwards()
        {
            var r = new PuzzleGateRules(3, 5);
            r.Activate(true, T0);
            r.Activate(true, T0);
            Assert.AreEqual(PuzzleActivation.Wrong, r.Activate(false, T0));
            Assert.AreEqual(0, r.RoundsCompleted(T0));
            Assert.AreEqual(1, r.WrongCount);

            var t = T0.AddSeconds(10);
            Assert.AreEqual(PuzzleActivation.RoundAdvanced, r.Activate(true, t));
            Assert.AreEqual(PuzzleActivation.RoundAdvanced, r.Activate(true, t));
            Assert.IsFalse(r.Solved, "two corrects after a reset must not solve a 3-round gate");
            Assert.AreEqual(PuzzleActivation.Solved, r.Activate(true, t));
        }

        [TestMethod]
        public void WrongAfterLatch_IsANoOp()
        {
            var r = new PuzzleGateRules(2, 5);
            r.Activate(true, T0);
            Assert.AreEqual(PuzzleActivation.Solved, r.Activate(true, T0));

            Assert.AreEqual(PuzzleActivation.AlreadySolved, r.Activate(false, T0.AddSeconds(1)));
            Assert.IsTrue(r.Solved);
            Assert.AreEqual(0, r.WrongCount);
            Assert.IsFalse(r.IsLocked(T0.AddSeconds(1)));
            Assert.AreEqual(2, r.RoundsCompleted(T0.AddSeconds(1)));
        }

        [TestMethod]
        public void Lockout_RefusesUntilItExpires_ThenAccepts()
        {
            var r = new PuzzleGateRules(3, 5);
            r.Activate(false, T0);

            Assert.IsTrue(r.IsLocked(T0.AddSeconds(4.9)));
            Assert.AreEqual(PuzzleActivation.Refused, r.Activate(true, T0.AddSeconds(4.9)));
            Assert.AreEqual(PuzzleActivation.Refused, r.Activate(false, T0.AddSeconds(1)));
            Assert.AreEqual(1, r.WrongCount, "a refused wrong must not count or extend the lockout");
            Assert.AreEqual(0, r.RoundsCompleted(T0.AddSeconds(4.9)));

            Assert.IsFalse(r.IsLocked(T0.AddSeconds(5)));
            Assert.AreEqual(PuzzleActivation.RoundAdvanced, r.Activate(true, T0.AddSeconds(5)));
        }

        [TestMethod]
        public void ZeroLockout_NeverRefuses()
        {
            var r = new PuzzleGateRules(3, 0);
            r.Activate(false, T0);
            Assert.AreEqual(PuzzleActivation.RoundAdvanced, r.Activate(true, T0));
        }

        [TestMethod]
        public void Text_NeverNamesASlotOrColour_AndIsAscii()
        {
            var lines = typeof(PuzzleGateText)
                .GetFields(BindingFlags.Public | BindingFlags.Static)
                .Where(f => f.FieldType == typeof(string))
                .Select(f => (string)f.GetRawConstantValue())
                .ToList();

            Assert.IsTrue(lines.Count >= 9, "the five approach prompts are included");

            // The rule is "no line names a SPECIFIC colour or slot". Generic words ("color", "lever", "beam")
            // are allowed; every palette colour name (plus the dropped white) and every slot word is not.
            var banned = PuzzleGateColours.Palette.Select(c => c.Name)
                .Concat(new[] { "white", "slot", "first", "second", "third", "fourth", "fifth", "last", "left", "right", "middle", "centre", "center" })
                .ToList();

            foreach (var line in lines)
            {
                Assert.IsTrue(line.All(c => c < 128), "non-ASCII in: " + line);

                var words = System.Text.RegularExpressions.Regex.Split(line.ToLowerInvariant(), "[^a-z]+");

                foreach (var word in banned)
                    Assert.IsFalse(words.Contains(word), $"'{line}' names '{word}'");

                Assert.IsFalse(line.Any(char.IsDigit), "digits in: " + line);
            }
        }
    }
}