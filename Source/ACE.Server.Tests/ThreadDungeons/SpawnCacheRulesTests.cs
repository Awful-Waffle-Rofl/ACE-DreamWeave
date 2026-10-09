using System;

using ACE.Server.ThreadDungeons;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ACE.Server.Tests.ThreadDungeons
{
    [TestClass]
    public class SpawnCacheRulesTests
    {
        private static SpawnCacheRefusal R(bool owns = true, bool inside = true, ThreadDungeonRunState state = ThreadDungeonRunState.Cleared,
            bool cooldown = true, bool forming = false, bool unclaimed = true, bool holds = false)
            => SpawnCacheRules.FirstRefusal(owns, inside, state, cooldown, forming, unclaimed, holds);

        [TestMethod]
        public void Refusals_are_checked_in_spec_order()
        {
            Assert.AreEqual(SpawnCacheRefusal.None, R());
            Assert.AreEqual(SpawnCacheRefusal.None, R(unclaimed: false, holds: true), "a placed cache with items can be moved");

            Assert.AreEqual(SpawnCacheRefusal.NotInside, R(owns: false, state: ThreadDungeonRunState.Active, cooldown: false, forming: true, unclaimed: false));
            Assert.AreEqual(SpawnCacheRefusal.NotInside, R(inside: false, state: ThreadDungeonRunState.Active, cooldown: false, forming: true, unclaimed: false));
            Assert.AreEqual(SpawnCacheRefusal.NotComplete, R(state: ThreadDungeonRunState.Active, cooldown: false, forming: true, unclaimed: false));
            Assert.AreEqual(SpawnCacheRefusal.NotComplete, R(state: ThreadDungeonRunState.Starting));
            Assert.AreEqual(SpawnCacheRefusal.Cooldown, R(cooldown: false, forming: true, unclaimed: false));
            Assert.AreEqual(SpawnCacheRefusal.Forming, R(forming: true, unclaimed: false));
            Assert.AreEqual(SpawnCacheRefusal.Empty, R(unclaimed: false, holds: false));
        }

        [TestMethod]
        public void Cooldown_is_stamped_once_checks_one_to_three_pass()
        {
            Assert.IsFalse(SpawnCacheRules.StampsCooldown(SpawnCacheRefusal.NotInside));
            Assert.IsFalse(SpawnCacheRules.StampsCooldown(SpawnCacheRefusal.NotComplete));
            Assert.IsFalse(SpawnCacheRules.StampsCooldown(SpawnCacheRefusal.Cooldown));
            Assert.IsTrue(SpawnCacheRules.StampsCooldown(SpawnCacheRefusal.Forming));
            Assert.IsTrue(SpawnCacheRules.StampsCooldown(SpawnCacheRefusal.Empty));
            Assert.IsTrue(SpawnCacheRules.StampsCooldown(SpawnCacheRefusal.None));
        }

        [TestMethod]
        public void Messages_are_the_spec_text()
        {
            Assert.AreEqual("You must be inside your Thread to use this.", SpawnCacheRules.MessageFor(SpawnCacheRefusal.NotInside));
            Assert.AreEqual("Your Thread is not complete yet.", SpawnCacheRules.MessageFor(SpawnCacheRefusal.NotComplete));
            Assert.AreEqual("Please wait before using /spawncache again.", SpawnCacheRules.MessageFor(SpawnCacheRefusal.Cooldown));
            Assert.AreEqual("Your Thread Cache is already forming.", SpawnCacheRules.MessageFor(SpawnCacheRefusal.Forming));
            Assert.AreEqual("Your Thread Cache is empty.", SpawnCacheRules.MessageFor(SpawnCacheRefusal.Empty));
            Assert.IsNull(SpawnCacheRules.MessageFor(SpawnCacheRefusal.None));
        }

        [TestMethod]
        public void Unclaimed_loot_forms_a_cache_otherwise_the_caches_move()
        {
            Assert.AreEqual(CacheRequestMode.Summon, SpawnCacheRules.ModeFor(true));
            Assert.AreEqual(CacheRequestMode.Move, SpawnCacheRules.ModeFor(false));
        }

        [TestMethod]
        public void Command_is_player_access_and_stamps_before_replying()
        {
            var src = PooledLootSourceText.Read("Source/ACE.Server/Command/Handlers/ThreadDungeonCommands.cs");

            StringAssert.Contains(src, "[CommandHandler(\"spawncache\", AccessLevel.Player, CommandHandlerFlag.RequiresWorld, 0,");

            var body = PooledLootSourceText.MethodBody(src, "public static void HandleSpawnCache(Session session, params string[] parameters)");
            var stamp = body.IndexOf("SpawnCacheRules.StampsCooldown(refusal)", StringComparison.Ordinal);
            var reply = body.IndexOf("Reply(session, SpawnCacheRules.MessageFor(refusal));", StringComparison.Ordinal);
            var request = body.IndexOf("ThreadCachePlacer.RequestPlacement(run, SpawnCacheRules.ModeFor(hasUnclaimed), player.Guid.Full)", StringComparison.Ordinal);

            Assert.IsTrue(stamp >= 0 && stamp < reply && reply < request);
            StringAssert.Contains(body, "ThreadCachePlacer.IsOwnerInside(run, player)", "one inside rule, shared with delivery and the exit guard");
        }
    }
}
