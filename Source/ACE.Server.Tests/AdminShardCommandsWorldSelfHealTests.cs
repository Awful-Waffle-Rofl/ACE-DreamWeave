using ACE.Server.Command.Handlers;
using ACE.Server.Managers;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ACE.Server.Tests
{
    /// <summary>
    /// AdminShardCommands's "@world" self-heal (code review finding, PR #1451): when WorldStatus already
    /// matches the requested state ("World is already open"/"already closed"), the command must still
    /// persist world_closed if the property disagrees with that state - e.g. it was never written, or a
    /// direct DB edit put it back - so a restart still comes back matching WorldStatus without needing a
    /// second @world command first.
    ///
    /// Both tests seed "world_closed" directly via PropertyCacheSeed's reflection seam, bypassing
    /// PropertyManager.ModifyBool's own live hook (WorldManager.HandleWorldClosedPropertyChanged), which
    /// is exactly what is needed here: it lets the test set up a genuine disagreement between WorldStatus
    /// and the persisted property (the hook, if it ran, would immediately resync them). Called through
    /// AdminShardCommands.HandleHelp with session: null (the console/web path - CommandHandlerHelper.
    /// WriteOutputInfo's null-session branch only logs, and WebCommandContext.TryCapture(null, ...) no-ops
    /// with no active web run), so this reaches the real command handler, not a re-implementation of it.
    /// </summary>
    [TestClass]
    public class AdminShardCommandsWorldSelfHealTests
    {
        [TestMethod]
        public void World_Open_WhileAlreadyOpen_PersistsIfPropertyDisagrees()
        {
            using var logAudit = PropertyCacheSeed.Bool("log_audit", true);
            using var chatLogAudit = PropertyCacheSeed.Bool("chat_log_audit", false);
            using var discordRelayEnabled = PropertyCacheSeed.Bool("discord_relay_enabled", false);
            // Disagreement: property says closed, WorldStatus is (about to be put, and stay) Open.
            using var worldClosedSeed = PropertyCacheSeed.Bool("world_closed", true);

            WorldManager.CompleteStartup();
            WorldManager.Open(null);

            AdminShardCommands.HandleHelp(null, new[] { "open" });

            Assert.AreEqual(WorldManager.WorldStatusState.Open, WorldManager.WorldStatus,
                "\"@world open\" while already open must never change WorldStatus.");
            Assert.IsFalse(PropertyManager.GetBool("world_closed", false).Item,
                "\"@world open\" while already open must still persist world_closed=false when the property disagreed.");
        }

        [TestMethod]
        public void World_Close_WhileAlreadyClosed_PersistsIfPropertyDisagrees()
        {
            using var logAudit = PropertyCacheSeed.Bool("log_audit", true);
            using var chatLogAudit = PropertyCacheSeed.Bool("chat_log_audit", false);
            using var discordRelayEnabled = PropertyCacheSeed.Bool("discord_relay_enabled", false);
            // Disagreement: property says open, WorldStatus is (about to be put) Closed.
            using var worldClosedSeed = PropertyCacheSeed.Bool("world_closed", false);

            WorldManager.CompleteStartup();
            WorldManager.Close(null, bootPlayers: false);

            try
            {
                AdminShardCommands.HandleHelp(null, new[] { "close" });

                Assert.AreEqual(WorldManager.WorldStatusState.Closed, WorldManager.WorldStatus,
                    "\"@world close\" while already closed must never change WorldStatus.");
                Assert.IsTrue(PropertyManager.GetBool("world_closed", false).Item,
                    "\"@world close\" while already closed must still persist world_closed=true when the property disagreed.");
            }
            finally
            {
                // Leave WorldStatus Open for later tests in the process, matching the convention the other
                // world_closed test classes already use.
                WorldManager.Open(null);
            }
        }
    }
}
