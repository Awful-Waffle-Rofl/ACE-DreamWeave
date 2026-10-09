using ACE.Server.Managers;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ACE.Server.Tests
{
    /// <summary>
    /// The live world_closed shard config switch (WorldManager.DecideWorldClosedAction /
    /// HandleWorldClosedPropertyChanged): whether a changed "world_closed" value should open or close the
    /// world right away, with no restart.
    ///
    /// DecideWorldClosedAction is pure (no statics, no side effects), so every branch is pinned directly
    /// here with no running server, matching the pattern WorldWatchdogTests uses for WorldWatchdog.Decide.
    /// </summary>
    [TestClass]
    public class WorldClosedLiveSwitchTests
    {
        [TestMethod]
        public void DuringStartup_DoesNothing_RegardlessOfValuesOrStatus()
        {
            // A resync or @modifybool landing mid-boot must never race Program.cs's own final
            // WorldManager.Open(null) - this must hold for every combination of old/new value and status.
            Assert.AreEqual(WorldManager.WorldClosedAction.None,
                WorldManager.DecideWorldClosedAction(false, true, startupComplete: false, WorldManager.WorldStatusState.Open));
            Assert.AreEqual(WorldManager.WorldClosedAction.None,
                WorldManager.DecideWorldClosedAction(true, false, startupComplete: false, WorldManager.WorldStatusState.Closed));
            Assert.AreEqual(WorldManager.WorldClosedAction.None,
                WorldManager.DecideWorldClosedAction(false, true, startupComplete: false, WorldManager.WorldStatusState.Closed));
        }

        [TestMethod]
        public void NoValueChange_DoesNothing()
        {
            Assert.AreEqual(WorldManager.WorldClosedAction.None,
                WorldManager.DecideWorldClosedAction(true, true, startupComplete: true, WorldManager.WorldStatusState.Closed));
            Assert.AreEqual(WorldManager.WorldClosedAction.None,
                WorldManager.DecideWorldClosedAction(false, false, startupComplete: true, WorldManager.WorldStatusState.Open));
        }

        [TestMethod]
        public void AlreadyOpen_SetFalse_DoesNothing()
        {
            // The world is already open (e.g. @world already opened it and then persisted the property),
            // so the property settling to false must not re-fire WorldManager.Open. This is also the exact
            // no-op that stops @world's own ModifyBool call from looping back into a second Open.
            Assert.AreEqual(WorldManager.WorldClosedAction.None,
                WorldManager.DecideWorldClosedAction(true, false, startupComplete: true, WorldManager.WorldStatusState.Open));
        }

        [TestMethod]
        public void AlreadyClosed_SetTrue_DoesNothing()
        {
            // Symmetric case: the world is already closed, so the property settling to true must not
            // re-fire WorldManager.Close (and must not attempt a redundant boot/broadcast). This is also
            // the no-op that stops @world close's own ModifyBool call from looping back into a second Close.
            Assert.AreEqual(WorldManager.WorldClosedAction.None,
                WorldManager.DecideWorldClosedAction(false, true, startupComplete: true, WorldManager.WorldStatusState.Closed));
        }

        [TestMethod]
        public void SetTrue_WhileOpen_AfterStartup_Closes()
        {
            Assert.AreEqual(WorldManager.WorldClosedAction.Close,
                WorldManager.DecideWorldClosedAction(false, true, startupComplete: true, WorldManager.WorldStatusState.Open));
        }

        [TestMethod]
        public void SetFalse_WhileClosed_AfterStartup_Opens()
        {
            Assert.AreEqual(WorldManager.WorldClosedAction.Open,
                WorldManager.DecideWorldClosedAction(true, false, startupComplete: true, WorldManager.WorldStatusState.Closed));
        }
    }

    /// <summary>
    /// Reaches the real call sites rather than just the pure decision function above:
    /// PropertyManager.ModifyBool detecting a "world_closed" change and calling
    /// WorldManager.HandleWorldClosedPropertyChanged, which in turn calls the real
    /// WorldManager.Open/Close. PropertyManager.DoWork's own detection (a direct DB edit picked up by the
    /// resync timer) is NOT reachable from here: DoWork writes via WriteBoolSnapshotToDB and reads back via
    /// DatabaseManager.ShardConfig.GetAllBools, both of which need a live ShardConfig DB connection that
    /// does not exist in this test process (see PropertyManager reads throw in unit tests) - that half of
    /// the wiring is unverified by this suite.
    ///
    /// StartupComplete is a permanent, never-cleared latch (by design, matching WorldEverStarted), so
    /// calling WorldManager.CompleteStartup() here leaves it true for the rest of the test process; that
    /// is harmless since production never clears it either. "log_audit", "chat_log_audit" and
    /// "discord_relay_enabled" are seeded via PropertyCacheSeed (WebCommandTestFakes.cs) to their own real
    /// defaults (true / false / false) so WorldManager.Open/Close's audit broadcast
    /// (BroadcastToAuditChannel -> BroadcastToChannelFromConsole -> LogBroadcastChatAs, and ->
    /// DiscordRelayManager.QueueAudit -> Enabled) does not hit the no-DB-in-tests NRE on a cache-miss
    /// GetBool - see PlayerManagerAuditLabelTests for the same pattern. All three are restored on Dispose
    /// so this test leaves no static cache pollution behind; "log_audit" specifically NEEDS
    /// PropertyCacheSeed's reflection seam rather than a plain ModifyBool call, because it is not a
    /// DefaultBooleanProperties key at all and ModifyBool refuses it outright.
    /// </summary>
    [TestClass]
    public class WorldClosedPropertyManagerWiringTests
    {
        [TestMethod]
        public void ModifyBool_WorldClosedTrue_WhileOpen_ClosesTheWorldLive()
        {
            using var logAudit = PropertyCacheSeed.Bool("log_audit", true);
            using var chatLogAudit = PropertyCacheSeed.Bool("chat_log_audit", false);
            using var discordRelayEnabled = PropertyCacheSeed.Bool("discord_relay_enabled", false);

            WorldManager.CompleteStartup();

            if (WorldManager.WorldStatus != WorldManager.WorldStatusState.Open)
                WorldManager.Open(null);

            try
            {
                PropertyManager.ModifyBool("world_closed", true);

                Assert.AreEqual(WorldManager.WorldStatusState.Closed, WorldManager.WorldStatus,
                    "ModifyBool(\"world_closed\", true) must close the live world with no restart.");
            }
            finally
            {
                // Restoring via ModifyBool(false) itself re-opens the world through the same live hook
                // (WorldStatus is Closed at this point, so DecideWorldClosedAction returns Open) - this
                // doubles as a same-test assertion that the reverse direction is wired too.
                PropertyManager.ModifyBool("world_closed", false);
                Assert.AreEqual(WorldManager.WorldStatusState.Open, WorldManager.WorldStatus,
                    "ModifyBool(\"world_closed\", false) must re-open the live world.");
            }
        }

        [TestMethod]
        public void ModifyBool_WorldClosedTrue_WhileAlreadyClosed_IsANoOp()
        {
            using var logAudit = PropertyCacheSeed.Bool("log_audit", true);
            using var chatLogAudit = PropertyCacheSeed.Bool("chat_log_audit", false);
            using var discordRelayEnabled = PropertyCacheSeed.Bool("discord_relay_enabled", false);

            WorldManager.CompleteStartup();

            if (WorldManager.WorldStatus != WorldManager.WorldStatusState.Closed)
                WorldManager.Close(null, bootPlayers: false);

            try
            {
                // Force both the cache and WorldStatus into the "already closed" state, then confirm
                // setting it to true again does not throw or otherwise misbehave (WorldStatus stays Closed).
                PropertyManager.ModifyBool("world_closed", true);
                Assert.AreEqual(WorldManager.WorldStatusState.Closed, WorldManager.WorldStatus);

                PropertyManager.ModifyBool("world_closed", true);
                Assert.AreEqual(WorldManager.WorldStatusState.Closed, WorldManager.WorldStatus,
                    "setting world_closed to true while already closed must stay a no-op.");
            }
            finally
            {
                PropertyManager.ModifyBool("world_closed", false);
                if (WorldManager.WorldStatus != WorldManager.WorldStatusState.Open)
                    WorldManager.Open(null);
            }
        }
    }
}
