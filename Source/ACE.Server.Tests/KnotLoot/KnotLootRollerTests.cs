using System;
using System.Collections.Generic;
using System.Linq;

using ACE.Server.KnotLoot;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ACE.Server.Tests.KnotLoot
{
    /// <summary>Pure: the roller reads only the IKnotLootSettings handed to it, never the property store.</summary>
    [TestClass]
    public class KnotLootRollerTests
    {
        private static readonly KnotWindowSettings W = KnotWindowSettings.Default;

        private int calls;

        private sealed class FakeDefaults : IKnotLootSettings
        {
            public bool Enabled = true;

            public bool TableEnabled(string t) => Enabled;

            public long Weight(string t, string entry, long fb) => fb;

            public double? LiveChance;

            public double Chance(string t, string group, double fb) => LiveChance ?? fb;

            public (long Min, long Max) Currency(string t, string key, (long, long) fb) => fb;

            public KnotWindowSettings Window(string t) => KnotWindowSettings.Default;
        }

        private Func<double> Script(params double[] values)
        {
            calls = 0;

            return () => values[calls++];
        }

        [TestMethod]
        public void Choose_Weights40_30_20_10()
        {
            var w = new long[] { 40, 30, 20, 10 };

            Assert.AreEqual(0, KnotLootRoller.Choose(w, 0.0));
            Assert.AreEqual(0, KnotLootRoller.Choose(w, 0.399));
            Assert.AreEqual(1, KnotLootRoller.Choose(w, 0.4));
            Assert.AreEqual(2, KnotLootRoller.Choose(w, 0.7));
            Assert.AreEqual(3, KnotLootRoller.Choose(w, 0.9));
            Assert.AreEqual(3, KnotLootRoller.Choose(w, 1.0));
            Assert.AreEqual(1, KnotLootRoller.Choose(new long[] { 0, 5, -3 }, 0.99));
            Assert.AreEqual(-1, KnotLootRoller.Choose(new long[] { 0, 0 }, 0.5));
        }

        [TestMethod]
        public void Roll_Grael_DrawOrderAndOutput()
        {
            var d = KnotLootRoller.Roll(KnotLootTables.Grael, 300, new FakeDefaults(), Script(0.5, 0.45, 0.5, 0.5, 1.0));

            Assert.AreEqual(4, d.Count);
            Assert.AreEqual(1007401u, d[0].Wcid);
            Assert.AreEqual(1007451u, d[1].Wcid);
            Assert.AreEqual(KnotTraitId.ShieldTrade, d[1].Trait);
            Assert.AreEqual(0.5, d[1].U, 1e-9);
            Assert.AreEqual(20630u, d[2].Wcid);
            Assert.AreEqual(38, d[2].StackSize);
            Assert.AreEqual(43901u, d[3].Wcid);
            Assert.AreEqual(41, d[3].StackSize);
            Assert.AreEqual(5, calls);
        }

        [TestMethod]
        public void Roll_Matron_SpinesDrops_WhenGateIsBelowChance()
        {
            // infusion r, rare r, spines gate 0.74, spines r, mmd u, note u
            var d = KnotLootRoller.Roll(KnotLootTables.Matron, 300, new FakeDefaults { LiveChance = 0.75 }, Script(0.0, 0.0, 0.74, 0.0, 0.5, 0.5));

            Assert.AreEqual(5, d.Count);
            Assert.AreEqual(1007400u, d[0].Wcid);
            Assert.AreEqual(1007600u, d[1].Wcid);
            Assert.AreEqual(1007602u, d[2].Wcid);
            Assert.AreEqual(KnotLootMutation.UpgradedCloak, d[2].Mutation);
            Assert.AreEqual(20630u, d[3].Wcid);
            Assert.AreEqual(43901u, d[4].Wcid);
            Assert.AreEqual(6, calls);
        }

        [TestMethod]
        public void Roll_Matron_DefaultChance_AlwaysDropsSpines_AndStillDrawsTheGate()
        {
            // infusion r, rare r, spines gate 0.999 (passes at the default chance of 1), spines r, mmd u, note u
            var d = KnotLootRoller.Roll(KnotLootTables.Matron, 300, new FakeDefaults(), Script(0.0, 0.0, 0.999, 0.0, 0.5, 0.5));

            Assert.AreEqual(1007602u, d[2].Wcid);
            Assert.AreEqual(6, calls);
        }

        [TestMethod]
        public void Roll_Matron_SpinesSkipped_WhenGateAtOrAboveChance_AndConsumesExactlyOneDraw()
        {
            foreach (var gate in new[] { 0.75, 0.9 })
            {
                // infusion r, rare r, spines gate, mmd u, note u
                var d = KnotLootRoller.Roll(KnotLootTables.Matron, 300, new FakeDefaults { LiveChance = 0.75 }, Script(0.0, 0.0, gate, 0.5, 0.5));

                Assert.AreEqual(4, d.Count, "gate " + gate);
                Assert.IsFalse(d.Any(x => x.Wcid == 1007602u), "gate " + gate);
                Assert.AreEqual(20630u, d[2].Wcid);
                Assert.AreEqual(43901u, d[3].Wcid);
                Assert.AreEqual(5, calls, "a skipped gate is one draw, so the currencies read draws 4 and 5");
            }
        }

        [TestMethod]
        public void Roll_Matron_BatonRarePick_StillDrawsItsTraitU()
        {
            // infusion r, rare r 0.5 = hive_stinger_baton, trait u 0.3, spines gate 0.9 (skipped), mmd u, note u
            var d = KnotLootRoller.Roll(KnotLootTables.Matron, 300, new FakeDefaults { LiveChance = 0.75 }, Script(0.0, 0.5, 0.3, 0.9, 0.5, 0.5));

            Assert.AreEqual(4, d.Count);
            Assert.AreEqual(1007603u, d[1].Wcid);
            Assert.AreEqual(KnotTraitId.OverpoweringIntent, d[1].Trait);
            Assert.AreEqual(0.3, d[1].U, 1e-9);
            Assert.AreEqual(6, calls);
        }

        [TestMethod]
        public void Roll_Matron_RareGroupAlwaysDropsExactlyOne_QuartersEach()
        {
            var expected = new uint[] { 1007600, 1007601, 1007603, 1007604 };

            for (var i = 0; i < 4; i++)
            {
                var r = (i + 0.5) / 4.0;
                var d = KnotLootRoller.Roll(KnotLootTables.Matron, 300, new FakeDefaults(), Script(0.0, r, 0.3, 0.9, 0.9, 0.5, 0.5));

                Assert.AreEqual(expected[i], d[1].Wcid, "r " + r);
            }
        }

        [TestMethod]
        public void Roll_Matron_LiveChanceZeroSkipsAndOneAlwaysDrops_GateStillDrawn()
        {
            var zero = KnotLootRoller.Roll(KnotLootTables.Matron, 300, new FakeDefaults { LiveChance = 0.0 }, Script(0.0, 0.0, 0.0, 0.5, 0.5));

            Assert.IsFalse(zero.Any(x => x.Wcid == 1007602u));
            Assert.AreEqual(5, calls, "the gate is drawn even at a live chance of 0 so the draw count is stable");

            var one = KnotLootRoller.Roll(KnotLootTables.Matron, 300, new FakeDefaults { LiveChance = 1.0 }, Script(0.0, 0.0, 0.99, 0.0, 0.5, 0.5));

            Assert.IsTrue(one.Any(x => x.Wcid == 1007602u));
            Assert.AreEqual(6, calls);
        }

        [TestMethod]
        public void Roll_Grael_MakesNoGateDraws()
        {
            Assert.IsTrue(KnotLootTables.Grael.Picks.All(g => !g.Gated));

            KnotLootRoller.Roll(KnotLootTables.Grael, 300, new FakeDefaults(), Script(0.5, 0.45, 0.5, 0.5, 1.0));

            Assert.AreEqual(5, calls);
        }

        [TestMethod]
        public void Roll_Currency_IsWindowed()
        {
            Assert.AreEqual(25, KnotLootRoller.Count(25, 50, KnotLootWindow.Position(200, 0.0, W)));
            Assert.AreEqual(50, KnotLootRoller.Count(25, 50, KnotLootWindow.Position(400, 1.0, W)));
        }

        [TestMethod]
        public void Roll_TableDisabled_IsEmpty_NoDraws()
        {
            var d = KnotLootRoller.Roll(KnotLootTables.Grael, 300, new FakeDefaults { Enabled = false }, Script(0.5, 0.45, 0.5, 0.5, 1.0));

            Assert.AreEqual(0, d.Count);
            Assert.AreEqual(0, calls);
        }

        [TestMethod]
        public void Roll_InfusionPick_CarriesNoTraitAndNoStack()
        {
            var d = KnotLootRoller.Roll(KnotLootTables.Grael, 300, new FakeDefaults(), Script(0.0, 0.0, 0.25, 0.5, 0.5));

            Assert.AreEqual(1007400u, d[0].Wcid);
            Assert.IsNull(d[0].Trait);
            Assert.AreEqual(1, d[0].StackSize);
            Assert.AreEqual(1007450u, d[1].Wcid);
            Assert.AreEqual(KnotTraitId.CastSpeed, d[1].Trait);
            Assert.AreEqual(0.25, d[1].U, 1e-9);
        }

        [TestMethod]
        public void Table_LookupIsCaseInsensitive_AndUnknownIsFalse()
        {
            Assert.IsTrue(KnotLootTables.TryGet("GRAEL", out var t));
            Assert.AreSame(KnotLootTables.Grael, t);
            Assert.IsFalse(KnotLootTables.TryGet("nope", out _));
            Assert.IsFalse(KnotLootTables.TryGet(null, out _));
        }

        [TestMethod]
        public void Table_EveryKeyHasARegisteredDefaultEqualToTheTableDefault()
        {
            // reads the DefaultPropertyManager dictionaries only, never the live store
            foreach (var table in new[] { KnotLootTables.Grael, KnotLootTables.Matron })
            {
                foreach (var group in table.Picks)
                    foreach (var entry in group.Entries)
                        Assert.AreEqual(entry.DefaultWeight, ACE.Server.Managers.DefaultPropertyManager.DefaultLongProperties[KnotLootTunables.TableKey(table.Key, "weight_" + entry.Key)].Item, table.Key + " " + entry.Key);

                foreach (var c in table.Currency)
                {
                    Assert.AreEqual(c.DefaultMin, ACE.Server.Managers.DefaultPropertyManager.DefaultLongProperties[KnotLootTunables.TableKey(table.Key, c.Key + "_min")].Item, table.Key + " " + c.Key);
                    Assert.AreEqual(c.DefaultMax, ACE.Server.Managers.DefaultPropertyManager.DefaultLongProperties[KnotLootTunables.TableKey(table.Key, c.Key + "_max")].Item, table.Key + " " + c.Key);
                }
            }
        }

        [TestMethod]
        public void Table_LookupFindsMatron_CaseInsensitive()
        {
            Assert.IsTrue(KnotLootTables.TryGet("matron", out var t));
            Assert.AreSame(KnotLootTables.Matron, t);
            Assert.IsTrue(KnotLootTables.TryGet("MATRON", out var u));
            Assert.AreSame(KnotLootTables.Matron, u);
        }

        [TestMethod]
        public void Table_MatronShape_MatchesTheSpec()
        {
            var t = KnotLootTables.Matron;

            Assert.AreEqual("matron", t.Key);
            Assert.AreEqual(3, t.Picks.Count);

            Assert.AreEqual("infusion", t.Picks[0].Key);
            Assert.AreEqual(1.0, t.Picks[0].DefaultChance);
            Assert.AreEqual(1.0, t.Picks[1].DefaultChance);
            CollectionAssert.AreEqual(new uint[] { 1007400, 1007401, 1007402 }, t.Picks[0].Entries.Select(e => e.Wcid).ToArray());
            CollectionAssert.AreEqual(new long[] { 1, 1, 1 }, t.Picks[0].Entries.Select(e => e.DefaultWeight).ToArray());
            Assert.IsTrue(t.Picks[0].Entries.All(e => e.Trait == null));

            Assert.AreEqual("rare", t.Picks[1].Key);
            CollectionAssert.AreEqual(new[] { "voidclaw", "torc", "hive_stinger_baton", "hive_nether_baton" }, t.Picks[1].Entries.Select(e => e.Key).ToArray());
            CollectionAssert.AreEqual(new uint[] { 1007600, 1007601, 1007603, 1007604 }, t.Picks[1].Entries.Select(e => e.Wcid).ToArray());
            CollectionAssert.AreEqual(new long[] { 1, 1, 1, 1 }, t.Picks[1].Entries.Select(e => e.DefaultWeight).ToArray());
            CollectionAssert.AreEqual(new KnotTraitId?[] { null, null, KnotTraitId.OverpoweringIntent, KnotTraitId.OverpoweringIntent }, t.Picks[1].Entries.Select(e => e.Trait).ToArray(), "only the two batons carry a trait");
            Assert.IsTrue(t.Picks[1].Entries.All(e => e.Mutation == KnotLootMutation.None));
            Assert.IsTrue(t.Picks[0].Entries.All(e => e.Mutation == KnotLootMutation.None));

            Assert.AreEqual("spines", t.Picks[2].Key);
            Assert.AreEqual(1.0, t.Picks[2].DefaultChance);
            Assert.IsTrue(t.Picks[2].Gated, "the spines group stays gated at a default of 1 so its chance remains tunable");
            Assert.IsFalse(t.Picks[0].Gated || t.Picks[1].Gated);
            Assert.AreEqual(1, t.Picks[2].Entries.Count);
            Assert.AreEqual("spines", t.Picks[2].Entries[0].Key);
            Assert.AreEqual(1007602u, t.Picks[2].Entries[0].Wcid);
            Assert.AreEqual(1L, t.Picks[2].Entries[0].DefaultWeight);
            Assert.IsNull(t.Picks[2].Entries[0].Trait);
            Assert.AreEqual(KnotLootMutation.UpgradedCloak, t.Picks[2].Entries[0].Mutation);

            Assert.AreEqual(2, t.Currency.Count);
            Assert.AreEqual(("mmd", 20630u, 25L, 50L), (t.Currency[0].Key, t.Currency[0].Wcid, t.Currency[0].DefaultMin, t.Currency[0].DefaultMax));
            Assert.AreEqual(("note", 43901u, 25L, 50L), (t.Currency[1].Key, t.Currency[1].Wcid, t.Currency[1].DefaultMin, t.Currency[1].DefaultMax));
            CollectionAssert.AreEqual(KnotLootTables.Grael.Currency.Select(c => (c.Key, c.Wcid, c.DefaultMin, c.DefaultMax)).ToArray(), t.Currency.Select(c => (c.Key, c.Wcid, c.DefaultMin, c.DefaultMax)).ToArray(), "currency matches Grael");
        }

        [TestMethod]
        public void Roll_Matron_InfusionVoidclawMmdAndNote()
        {
            // infusion r, rare r, spines gate (skipped), mmd u, note u
            var d = KnotLootRoller.Roll(KnotLootTables.Matron, 300, new FakeDefaults { LiveChance = 0.75 }, Script(0.5, 0.0, 0.9, 0.5, 1.0));

            Assert.AreEqual(4, d.Count);
            Assert.AreEqual(5, calls, "a trait-less rare pick makes no extra draw; the skipped spines gate is one draw");
            Assert.AreEqual(1007401u, d[0].Wcid);
            Assert.AreEqual(1007600u, d[1].Wcid);
            Assert.IsNull(d[1].Trait);
            Assert.AreEqual(KnotLootMutation.None, d[1].Mutation);
            Assert.AreEqual(20630u, d[2].Wcid);
            Assert.IsTrue(d[2].StackSize >= 25 && d[2].StackSize <= 50, d[2].StackSize.ToString());
            Assert.AreEqual(43901u, d[3].Wcid);
            Assert.IsTrue(d[3].StackSize >= 25 && d[3].StackSize <= 50, d[3].StackSize.ToString());
        }

        [TestMethod]
        public void Roll_Matron_SpinesCarryTheMutation_GateAndPickAreTheOnlyDraws()
        {
            // rare r 0.3 = torc (no spines in the rare group), then gate 0.1 passes, spines r 0.0
            var d = KnotLootRoller.Roll(KnotLootTables.Matron, 300, new FakeDefaults(), Script(0.5, 0.3, 0.1, 0.0, 0.5, 0.5));

            Assert.AreEqual(6, calls, "the mutation is copied, never drawn by the roller");
            Assert.AreEqual(5, d.Count);
            Assert.AreEqual(1007601u, d[1].Wcid);
            Assert.AreEqual(KnotLootMutation.None, d[1].Mutation);
            Assert.AreEqual(1007602u, d[2].Wcid);
            Assert.AreEqual(KnotLootMutation.UpgradedCloak, d[2].Mutation);
            Assert.IsNull(d[2].Trait);
            Assert.AreEqual(1, d[2].StackSize);
        }

        [TestMethod]
        public void Roll_Matron_BatonPick_MakesExactlyOneExtraDraw_ForItsTraitU()
        {
            // infusion r, rare r 0.6 = stinger, trait u 0.25, spines gate (skipped), mmd u, note u
            var stinger = KnotLootRoller.Roll(KnotLootTables.Matron, 300, new FakeDefaults { LiveChance = 0.75 }, Script(0.5, 0.6, 0.25, 0.9, 0.5, 0.5));
            Assert.AreEqual(6, calls, "a trait pick draws its u right after r, before the gate and the currencies");
            Assert.AreEqual(4, stinger.Count);
            Assert.AreEqual(1007603u, stinger[1].Wcid);
            Assert.AreEqual(KnotTraitId.OverpoweringIntent, stinger[1].Trait);
            Assert.AreEqual(0.25, stinger[1].U, 1e-9);
            Assert.AreEqual(KnotLootMutation.None, stinger[1].Mutation);
            Assert.AreEqual(1, stinger[1].StackSize);
            Assert.AreEqual(20630u, stinger[2].Wcid);
            Assert.AreEqual(43901u, stinger[3].Wcid);

            var nether = KnotLootRoller.Roll(KnotLootTables.Matron, 300, new FakeDefaults { LiveChance = 0.75 }, Script(0.5, 0.9, 0.75, 0.9, 0.5, 0.5));
            Assert.AreEqual(6, calls);
            Assert.AreEqual(1007604u, nether[1].Wcid);
            Assert.AreEqual(KnotTraitId.OverpoweringIntent, nether[1].Trait);
            Assert.AreEqual(0.75, nether[1].U, 1e-9);
        }

        [TestMethod]
        public void Table_GraelShape_MatchesTheDesign()
        {
            var t = KnotLootTables.Grael;

            Assert.AreEqual("grael", t.Key);
            Assert.AreEqual(2, t.Picks.Count);
            Assert.AreEqual(3, t.Picks[0].Entries.Count);
            Assert.AreEqual(4, t.Picks[1].Entries.Count);
            Assert.AreEqual(40L, t.Picks[1].Entries[0].DefaultWeight);
            Assert.AreEqual(2, t.Currency.Count);
            Assert.AreEqual((20630u, 25L, 50L), (t.Currency[0].Wcid, t.Currency[0].DefaultMin, t.Currency[0].DefaultMax));
            Assert.AreEqual((43901u, 25L, 50L), (t.Currency[1].Wcid, t.Currency[1].DefaultMin, t.Currency[1].DefaultMax));
        }
    }
}
