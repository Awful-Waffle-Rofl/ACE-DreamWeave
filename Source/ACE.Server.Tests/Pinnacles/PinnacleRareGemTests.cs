using System;
using System.Collections.Generic;

using ACE.Entity.Models;
using ACE.Server.Pinnacles;
using ACE.Server.WorldObjects;
using ACE.Server.Tests.ThreadDungeons;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ACE.Server.Tests.Pinnacles
{
    /// <summary>
    /// Owner ruling 2026-10-22: rare gems are disabled in Knot fights (stripped on entry, refused inside). The decision is
    /// pure; the hooks need a live Player/world, so their presence and order are pinned on source text. No PropertyManager key is read.
    /// </summary>
    [TestClass]
    public class PinnacleRareGemTests
    {
        [TestMethod]
        public void GemUse_RefusesRareGemBeforePromptAndTimer_AndAgainInUseGemAndUseOnTarget()
        {
            var gem = PooledLootSourceText.Read("Source/ACE.Server/WorldObjects/Gem.cs");

            var act = PooledLootSourceText.MethodBody(gem, "public void ActOnUse(WorldObject activator, bool confirmed)");
            var refuse = act.IndexOf("if (RefuseRareInKnot(player))", StringComparison.Ordinal);
            Assert.IsTrue(refuse > 0, "ActOnUse refuses");
            Assert.IsTrue(refuse < act.IndexOf("// handle rare gems", StringComparison.Ordinal), "before the confirmation prompt");
            Assert.IsTrue(refuse < act.IndexOf("if (RareUsesTimer)", StringComparison.Ordinal), "before the rare timer");

            var use = PooledLootSourceText.MethodBody(gem, "public void UseGem(Player player)");
            var useRefuse = use.IndexOf("if (RefuseRareInKnot(player))", StringComparison.Ordinal);
            Assert.IsTrue(useRefuse > 0 && useRefuse < use.IndexOf("TryHandleUse(this, player)", StringComparison.Ordinal), "UseGem refuses before any effect");

            var onTarget = PooledLootSourceText.MethodBody(gem, "public override void HandleActionUseOnTarget(Player player, WorldObject target)");
            var targetRefuse = onTarget.IndexOf("RefuseRareInKnot(player)", StringComparison.Ordinal);
            Assert.IsTrue(targetRefuse > 0 && targetRefuse < onTarget.IndexOf("Tailoring.IsTailoringKit", StringComparison.Ordinal), "use-on-target refuses first");

            var helper = PooledLootSourceText.MethodBody(gem, "private bool RefuseRareInKnot(Player player)");
            StringAssert.Contains(helper, "RareId == null");

            var block = onTarget.Substring(0, onTarget.IndexOf("Tailoring.IsTailoringKit", StringComparison.Ordinal));
            StringAssert.Contains(block, "player.SendUseDoneEvent();");
        }

        [TestMethod]
        public void EntryFollowUp_StripsBeforeOnArrival_ForEntryAndReentry()
        {
            var enc = PooledLootSourceText.Read("Source/ACE.Server/Pinnacles/PinnacleEncounter.cs");
            var tp = PooledLootSourceText.MethodBody(enc, "private static void Teleport(Player player, Position to, Action onArrival = null)");
            var has = tp.IndexOf("player.HasAnyStrippableRareGemBuff(true)", StringComparison.Ordinal);
            var strip = tp.IndexOf("player.StripRareGemBuffs(true)", StringComparison.Ordinal);
            Assert.IsFalse(tp.Contains("Location?.Instance"), "no instance gate: a fog-delayed teleport must still strip");
            var arrive = tp.IndexOf("onArrival?.Invoke();", StringComparison.Ordinal);
            Assert.IsTrue(has > 0 && strip > has && arrive > strip, $"has={has} strip={strip} arrive={arrive}");
            StringAssert.Contains(tp, "PinnacleFightManager.RareGemsStrippedLine");
        }

        [TestMethod]
        public void SelectStrippableItemBuffs_KeepsInnateAuras_RemovesTimedStrippable_KeepsOthers()
        {
            var strippable = new HashSet<uint> { 100, 200 };
            var registry = new List<PropertiesEnchantmentRegistry>
            {
                new PropertiesEnchantmentRegistry { SpellId = 100, Duration = -1 },    // innate aura: kept
                new PropertiesEnchantmentRegistry { SpellId = 200, Duration = 1800 },  // timed rare buff: removed
                new PropertiesEnchantmentRegistry { SpellId = 300, Duration = 1800 },  // not strippable: kept
            };

            var picked = Player.SelectStrippableItemBuffs(registry, strippable);

            Assert.AreEqual(1, picked.Count);
            Assert.AreEqual(200, picked[0].SpellId);
            Assert.AreEqual(0, Player.SelectStrippableItemBuffs(new List<PropertiesEnchantmentRegistry>(), strippable).Count);
        }
    }
}