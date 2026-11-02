using System;
using System.Collections.Generic;
using System.Linq;

using ACE.Entity.Enum;
using ACE.Entity.Models;
using ACE.Server.Pinnacles;
using ACE.Server.Tests.ThreadDungeons;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ACE.Server.Tests.Pinnacles
{
    [TestClass]
    public class PinnacleDispelTests
    {
        private static PropertiesEnchantmentRegistry Buff(int spellId, double duration = 3600, uint category = 0)
            => new PropertiesEnchantmentRegistry
            {
                SpellId = spellId,
                Duration = duration,
                SpellCategory = (SpellCategory)(category == 0 ? (uint)spellId : category),
                CasterObjectId = 0x50000001,
            };

        private static List<PropertiesEnchantmentRegistry> Buffs(int count)
            => Enumerable.Range(1, count).Select(i => Buff(1000 + i)).ToList();

        [TestMethod]
        public void FormatLines_Empty_IsTheNothingLine()
        {
            CollectionAssert.AreEqual(new[] { "None of your protections could be unravelled." }, PinnacleDispel.FormatLines(new List<string>()));
            CollectionAssert.AreEqual(new[] { "None of your protections could be unravelled." }, PinnacleDispel.FormatLines(null));
        }

        [TestMethod]
        public void FormatLines_ThreeNames_OneLine()
        {
            CollectionAssert.AreEqual(new[] { "Dispelled: A, B, and C." }, PinnacleDispel.FormatLines(new[] { "A", "B", "C" }));
            CollectionAssert.AreEqual(new[] { "Dispelled: A." }, PinnacleDispel.FormatLines(new[] { "A" }));
            CollectionAssert.AreEqual(new[] { "Dispelled: A and B." }, PinnacleDispel.FormatLines(new[] { "A", "B" }));
        }

        [TestMethod]
        public void FormatLines_LongList_WrapsWithoutSplittingAName()
        {
            var names = Enumerable.Range(1, 40).Select(i => $"Spell Name Number {i:00}, Greater").ToList();

            var lines = PinnacleDispel.FormatLines(names);

            Assert.IsTrue(lines.Count > 1, "does not fit one line");
            Assert.IsTrue(lines.All(l => l.Length <= 250), string.Join(" | ", lines.Select(l => l.Length)));
            StringAssert.StartsWith(lines[0], "Dispelled: ");
            StringAssert.EndsWith(lines[lines.Count - 1], ".");

            var joined = string.Join(" ", lines);
            var pos = 0;

            foreach (var name in names)
            {
                var at = joined.IndexOf(name, pos, StringComparison.Ordinal);
                Assert.IsTrue(at >= pos, $"{name} in order");
                Assert.AreEqual(at, joined.IndexOf(name, StringComparison.Ordinal), $"{name} exactly once");
                pos = at + name.Length;
            }

            // a name is never split across lines: every line holds whole names
            foreach (var line in lines)
                Assert.IsTrue(names.Any(n => line.Contains(n)), line);
        }

        [TestMethod]
        public void FormatLines_HonoursASmallerMax()
        {
            var lines = PinnacleDispel.FormatLines(new[] { "Alpha", "Bravo", "Charlie", "Delta" }, 25);

            Assert.IsTrue(lines.All(l => l.Length <= 25), string.Join(" | ", lines));
            Assert.IsTrue(lines.Count > 1);
        }

        [TestMethod]
        public void DispelOwnerBuffs_PlaysDispelAll_AndSendsMagicLines()
        {
            var body = PooledLootSourceText.MethodBody(PooledLootSourceText.Read("Source/ACE.Server/Pinnacles/LivePinnacleWorld.cs"), "public int DispelOwnerBuffs(");

            StringAssert.Contains(body, "PlayScript.DispelAll");
            StringAssert.Contains(body, "ChatMessageType.Magic");
            StringAssert.Contains(body, "PinnacleDispel.FormatLines(");

            // the visual is not inside the per-entry loop: it plays even when nothing was chosen
            Assert.IsTrue(body.IndexOf("PlayScript.DispelAll", StringComparison.Ordinal) > body.IndexOf("foreach (var entry in chosen)", StringComparison.Ordinal));
        }

        [TestMethod]
        public void TakesAtMostN()
        {
            var chosen = PinnacleDispel.Select(Buffs(12), 5, new Random(3));

            Assert.AreEqual(5, chosen.Count);
            Assert.AreEqual(5, chosen.Distinct().Count(), "no entry twice");
            Assert.AreEqual(0, PinnacleDispel.Select(Buffs(12), 0, new Random(3)).Count);
            Assert.AreEqual(0, PinnacleDispel.Select(Buffs(12), -1, new Random(3)).Count);
        }

        [TestMethod]
        public void ExcludesItemSourcedAndPseudoSpells()
        {
            var item = Buff(2001, duration: -1);              // item-sourced or vitae
            var pseudo = Buff(short.MaxValue + 1);             // item/cooldown pseudo-spell
            var real = new[] { Buff(3001), Buff(3002) };
            var all = new List<PropertiesEnchantmentRegistry> { item, real[0], pseudo, null, real[1] };

            var chosen = PinnacleDispel.Select(all, 10, new Random(1));

            CollectionAssert.AreEquivalent(real, chosen);
        }

        [TestMethod]
        public void FewerThanN_TakesAll()
        {
            var three = Buffs(3);
            var chosen = PinnacleDispel.Select(three, 5, new Random(9));

            CollectionAssert.AreEquivalent(three, chosen);
            Assert.AreEqual(0, PinnacleDispel.Select(new List<PropertiesEnchantmentRegistry>(), 5, new Random(9)).Count);
            Assert.AreEqual(0, PinnacleDispel.Select(null, 5, new Random(9)).Count);
        }

        [TestMethod]
        public void SameSeed_SameChoice()
        {
            var pool = Buffs(20);

            var a = PinnacleDispel.Select(pool, 5, new Random(77)).Select(e => e.SpellId).ToList();
            var b = PinnacleDispel.Select(pool, 5, new Random(77)).Select(e => e.SpellId).ToList();
            CollectionAssert.AreEqual(a, b);

            // and the seed actually matters (discriminates a selection that ignores rng)
            var differs = Enumerable.Range(1, 20).Any(seed =>
                !PinnacleDispel.Select(pool, 5, new Random(seed)).Select(e => e.SpellId).SequenceEqual(a));
            Assert.IsTrue(differs);
        }

        [TestMethod]
        public void DoesNotMutateTheInput()
        {
            var pool = Buffs(8);
            var before = pool.Select(e => e.SpellId).ToList();

            PinnacleDispel.Select(pool, 3, new Random(5));

            CollectionAssert.AreEqual(before, pool.Select(e => e.SpellId).ToList());
        }
    }
}
