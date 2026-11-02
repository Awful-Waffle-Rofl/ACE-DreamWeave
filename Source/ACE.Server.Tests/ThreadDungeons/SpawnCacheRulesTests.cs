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
            Assert.AreEqual("A Thread Cache is already forming. Try again shortly.", SpawnCacheRules.MessageFor(SpawnCacheRefusal.Forming));
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
        public void Summon_and_Move_gather_placed_caches_Auto_does_not()
        {
            Assert.IsTrue(ThreadCachePlacer.MovesPlacedCachesFirst(CacheRequestMode.Summon, true), "Summon must also move placed caches");
            Assert.IsTrue(ThreadCachePlacer.MovesPlacedCachesFirst(CacheRequestMode.Move, true));
            Assert.IsFalse(ThreadCachePlacer.MovesPlacedCachesFirst(CacheRequestMode.Auto, true), "Auto stays a top-up");
            Assert.IsFalse(ThreadCachePlacer.MovesPlacedCachesFirst(CacheRequestMode.Summon, false), "not inside: nothing moves");
            Assert.IsFalse(ThreadCachePlacer.MovesPlacedCachesFirst(CacheRequestMode.Move, false));
        }

        [TestMethod]
        public void Solo_execute_moves_placed_caches_before_building_the_new_ones()
        {
            var src = PooledLootSourceText.Read("Source/ACE.Server/ThreadDungeons/ThreadCachePlacer.cs");
            var body = PooledLootSourceText.MethodBody(src,
                "private static void Execute(ThreadDungeonRun run, Landblock landblock, CacheRequestMode mode, uint requesterGuid, ThreadLootRollBudget budget)");

            var decision = body.IndexOf("var movesPlaced = MovesPlacedCachesFirst(mode, ownerInside);", StringComparison.Ordinal);
            var move = body.IndexOf("if (mode == CacheRequestMode.Summon && movesPlaced)", StringComparison.Ordinal);
            var build = body.IndexOf("ThreadCacheFiller.BuildPile(run, budget, ledgerSnapshot)", StringComparison.Ordinal);
            var form = body.IndexOf("FormAndFillAtMember(run, landblock, owner", StringComparison.Ordinal);
            var finish = body.IndexOf("FinishSummon(run, landblock, owner, summonMove, pass)", StringComparison.Ordinal);

            Assert.IsTrue(decision >= 0 && decision < move, "one decision feeds both branches");
            Assert.IsTrue(move >= 0, "Summon must move the placed caches");
            Assert.IsTrue(move < build && build < form && form < finish, "move existing first, then build and form new, then finish the Summon");

            Assert.AreEqual(1, Count(body, "SummonMoveGuarded("), "the Summon move call appears exactly once");
            Assert.AreEqual(1, Count(body, "MoveCaches("), "the only other move call is the Move branch's");
            Assert.AreEqual(1, Count(body, "FinishSummon("), "the Summon's exit relocation and single chat line live in one place");
            StringAssert.Contains(body, "finally", "the line and the exit relocation also follow a Summon that returned early");
        }

        [TestMethod]
        public void Solo_summon_move_records_progress_per_cache_so_a_throw_keeps_it()
        {
            var src = PooledLootSourceText.Read("Source/ACE.Server/ThreadDungeons/ThreadCachePlacer.cs");
            var guard = PooledLootSourceText.MethodBody(src, "private static void SummonMoveGuarded(");
            var move = PooledLootSourceText.MethodBody(src, "private static CacheMoveResult MoveCaches(");

            StringAssert.Contains(guard, "MoveCaches(run, landblock, owner, quiet: true, progress)", "the guard hands its own result to the move");
            Assert.IsFalse(guard.Contains("return default"), "a throw must not discard the progress");
            StringAssert.Contains(move, "progress.Moved++", "counted per cache moved, not at the end");
        }
        [TestMethod]
        public void Summon_says_one_line_formed_if_anything_moved_or_formed_no_room_only_if_neither()
        {
            Assert.AreEqual(ThreadCachePlacer.FormedMessage, ThreadCachePlacer.SummonLine(2, 1, false));
            Assert.AreEqual(ThreadCachePlacer.FormedMessage, ThreadCachePlacer.SummonLine(1, 0, true), "moved something: never no-room");
            Assert.AreEqual(ThreadCachePlacer.FormedMessage, ThreadCachePlacer.SummonLine(0, 3, true));
            Assert.AreEqual(ThreadCachePlacer.NoRoomMessage, ThreadCachePlacer.SummonLine(0, 0, true));
            Assert.IsNull(ThreadCachePlacer.SummonLine(0, 0, false));
        }

        private static int Count(string text, string needle)
        {
            var n = 0;
            for (var i = text.IndexOf(needle, StringComparison.Ordinal); i >= 0; i = text.IndexOf(needle, i + 1, StringComparison.Ordinal))
                n++;
            return n;
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
