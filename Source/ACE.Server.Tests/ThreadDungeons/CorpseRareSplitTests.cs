using System;

using ACE.Server.WorldObjects;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ACE.Server.Tests.ThreadDungeons
{
    /// <summary>
    /// The rare roll reads PropertyManager (rares_real_time, rares_real_time_v2) and so cannot run under this
    /// harness; the split is pinned structurally instead, and the one pure edge (no killer player) is driven.
    /// </summary>
    [TestClass]
    public class CorpseRareSplitTests
    {
        [TestMethod]
        public void Bookkeeping_with_no_killer_player_is_a_no_op()
        {
            // Driven for both real-time modes and every tier. Under this harness each step past the null guard
            // would throw: rares_real_time true reads PropertyManager, which throws here, and false writes through
            // the null Player. So returning normally IS the observable no-op, and the pin below proves the guard,
            // not a lucky branch, is why.
            foreach (var realTimeRares in new[] { true, false })
            {
                for (var tier = 0; tier <= 7; tier++)
                    Corpse.ApplyRareFoundBookkeeping(null, realTimeRares, timestamp: 1000, tier: tier);
            }

            var body = PooledLootSourceText.MethodBody(PooledLootSourceText.Read("Source/ACE.Server/WorldObjects/Corpse.cs"), "internal static void ApplyRareFoundBookkeeping(");
            var guard = body.IndexOf("if (killerPlayer == null)", StringComparison.Ordinal);
            var early = body.IndexOf("return;", StringComparison.Ordinal);
            var firstRead = body.IndexOf("PropertyManager.", StringComparison.Ordinal);
            var firstWrite = body.IndexOf("killerPlayer.Rares", StringComparison.Ordinal);

            Assert.IsTrue(guard >= 0 && guard < early && early < firstRead && early < firstWrite, "the null guard returns before any PropertyManager read or player write");
        }

        [TestMethod]
        public void Rare_broadcast_text_is_one_shared_constant()
        {
            Assert.AreEqual("Tester has discovered the Pyreal Beetle!", string.Format(Corpse.RareDiscoveredFormat, "Tester", "Pyreal Beetle"));

            var src = PooledLootSourceText.Read("Source/ACE.Server/WorldObjects/Corpse.cs");
            StringAssert.Contains(src, "EnqueueBroadcast(new GameMessageSystemChat(string.Format(RareDiscoveredFormat, killerName, rareGenerated.Name), ChatMessageType.System));");
            Assert.AreEqual(1, src.Split(new[] { "has discovered the" }, StringSplitOptions.None).Length - 1, "the text exists once, in the constant");
        }

        [TestMethod]
        public void TryGenerateRare_is_roll_then_add_then_bookkeeping_on_success_only()
        {
            var src = PooledLootSourceText.Read("Source/ACE.Server/WorldObjects/Corpse.cs");
            var body = PooledLootSourceText.MethodBody(src, "public void TryGenerateRare(DamageHistoryInfo killer)");

            var roll = body.IndexOf("RollRareFor(killer, Name, Guid, out var killerPlayer, out var timestamp, out var realTimeRares, out var tier)", StringComparison.Ordinal);
            var add = body.IndexOf("if (TryAddToInventory(wo))", StringComparison.Ordinal);
            var books = body.IndexOf("ApplyRareFoundBookkeeping(killerPlayer, realTimeRares, timestamp, tier);", StringComparison.Ordinal);
            var failure = body.IndexOf("failed to add to corpse inventory", StringComparison.Ordinal);

            Assert.IsTrue(roll >= 0 && add > roll, "roll first, then the add");
            Assert.IsTrue(books > add && books < failure, "bookkeeping only inside the successful-add branch");
            StringAssert.Contains(body, "CorpseGeneratedRare = true;");
            StringAssert.Contains(body, "TimeToRot = 900;");
        }

        [TestMethod]
        public void RollRareFor_keeps_both_real_time_modes_and_the_second_chance()
        {
            var src = PooledLootSourceText.Read("Source/ACE.Server/WorldObjects/Corpse.cs");
            var body = PooledLootSourceText.MethodBody(src, "internal static WorldObject RollRareFor(");

            StringAssert.Contains(body, "PropertyManager.GetBool(\"rares_real_time\").Item");
            StringAssert.Contains(body, "PropertyManager.GetBool(\"rares_real_time_v2\").Item");
            StringAssert.Contains(body, "if (secondChanceGranted && wo == null && !suppressed)");
            StringAssert.Contains(body, "wo.IconUnderlayId = 0x6005B0C;");
            Assert.IsFalse(body.Contains("TryAddToInventory"), "the roll must not touch any container");
        }
    }
}
