using System;
using System.Diagnostics;
using System.Text.RegularExpressions;
using System.Threading;

using log4net;

using ACE.Common;
using ACE.Common.Performance;
using ACE.Database;
using ACE.Database.Entity;
using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Entity.Models;
using ACE.Server.Command.Handlers;
using ACE.Server.Entity;
using ACE.Server.Realms;
using ACE.Server.Entity.Actions;
using ACE.Server.WorldObjects;
using ACE.Server.Network;
using ACE.Server.Network.GameEvent.Events;
using ACE.Server.Network.GameMessages.Messages;
using ACE.Server.Network.Managers;
using ACE.Server.Physics;
using ACE.Server.Physics.Common;
using ACE.Server.WorldEvents;

using Character = ACE.Database.Models.Shard.Character;
using Position = ACE.Entity.Position;

namespace ACE.Server.Managers
{
    public static class WorldManager
    {
        private static readonly ILog log = LogManager.GetLogger(System.Reflection.MethodBase.GetCurrentMethod().DeclaringType);

        private static readonly PhysicsEngine Physics;

        public static bool WorldActive { get; private set; }
        private static volatile bool pendingWorldStop;

        /// <summary>
        /// WaffleACE liveness: Environment.TickCount64 as of the top of the most recent world tick.
        /// Written by the world thread only, read by WorldWatchdog on its own thread.
        ///
        /// This exists because a dead or hung world thread is otherwise INVISIBLE from outside the
        /// process: the .NET process stays alive, SocketManager's listener threads keep the UDP port
        /// bound, and a port-probe healthcheck keeps reporting healthy. On 2026-09-01 that combination
        /// hid a world thread that had died to an unhandled exception for 5h22m while every login
        /// failed. See WorldWatchdog for what consumes this.
        ///
        /// Deliberately a monotonic tick count and not DateTime.UtcNow: the write sits at the top of a
        /// loop that iterates at least ~100 times a second, so it must be a single interlocked 8-byte
        /// store with no syscall and no allocation, and it must not move backwards when the wall clock
        /// is adjusted (NTP, DST, a container host clock step).
        /// </summary>
        private static long lastWorldTickTicks;

        /// <summary>
        /// Reader for <see cref="lastWorldTickTicks"/>. Interlocked.Read because a 64-bit field is not
        /// guaranteed to be read atomically on a 32-bit runtime, and a torn read here would look like
        /// an enormous staleness and could trip the watchdog on a perfectly healthy world.
        /// </summary>
        internal static long LastWorldTickTicks => Interlocked.Read(ref lastWorldTickTicks);

        /// <summary>
        /// Latched true by the first world tick and never cleared. It is FALSE for the whole duration
        /// of LandblockManager.PreloadConfigLandblocks(), which on a large permaload list legitimately
        /// runs for minutes - the watchdog must be able to tell "has not started yet" apart from
        /// "started and then stopped ticking", because only the second one is a fault.
        /// </summary>
        internal static volatile bool WorldEverStarted;

        /// <summary>
        /// Set true by the world thread's own catch block when it dies to an unhandled exception. This
        /// is the unambiguous death signal: WorldActive alone cannot distinguish a crash from a normal
        /// shutdown, since both clear it.
        /// </summary>
        internal static volatile bool WorldThreadFaulted;

        public enum WorldStatusState
        {
            Closed,
            Open
        }

        public static WorldStatusState WorldStatus { get; private set; } = WorldStatusState.Closed;

        public static readonly ActionQueue ActionQueue = new ActionQueue();
        public static readonly DelayManager DelayManager = new DelayManager();

        static WorldManager()
        {
            Physics = new PhysicsEngine(new ObjectMaint(), new SmartBox());
            Physics.Server = true;
        }

        public static void Initialize()
        {
            var thread = new Thread(() =>
            {
                try
                {
                    LandblockManager.PreloadConfigLandblocks();
                    UpdateWorld();
                }
                catch (Exception ex)
                {
                    // This delegate runs on a bare background thread, so an escaping exception would
                    // otherwise terminate the process with no log line at all. Log it loudly instead.
                    log.Fatal("World thread terminated by an unhandled exception. The world is stopped.", ex);

                    // WaffleACE liveness: publish the death so WorldWatchdog (on its own thread) can
                    // report it and, if configured, take the process down instead of leaving a hollow
                    // server bound to its port. Nothing else goes in this block: we are on the dying
                    // thread, so anything that could itself throw would skip the finally below, and
                    // the finally is what ServerManager's shutdown waits depend on. In particular do
                    // NOT call Environment.Exit or start a shutdown from here.
                    WorldThreadFaulted = true;
                }
                finally
                {
                    // Reach the same state a clean shutdown reaches, whether or not we got here by an
                    // exception. On the normal path both of these are already set, so this is a no-op:
                    // UpdateWorld only returns once pendingWorldStop is true, and it clears WorldActive
                    // on the way out. On the exception path they matter, because ServerManager's
                    // shutdown waits on WorldActive and would otherwise spin forever.
                    pendingWorldStop = true;
                    WorldActive = false;
                }
            });
            thread.Name = "World Manager";
            thread.Priority = ThreadPriority.AboveNormal;
            thread.Start();
            log.DebugFormat("ServerTime initialized to {0}", Timers.WorldStartLoreTime);
            log.DebugFormat("Current maximum allowed sessions: {0}", ConfigManager.Config.Server.Network.MaximumAllowedSessions);

            log.Info($"World started and is currently {WorldStatus.ToString()}{(PropertyManager.GetBool("world_closed", false).Item ? "" : " and will open automatically when server startup is complete.")}");
            if (WorldStatus == WorldStatusState.Closed)
                log.Info($"To open world to players, use command: world open");
        }

        internal static void Open(Player player)
        {
            WorldStatus = WorldStatusState.Open;
            PlayerManager.BroadcastToAuditChannel(player, "World is now open");
        }

        internal static void Close(Player player, bool bootPlayers = false)
        {
            WorldStatus = WorldStatusState.Closed;
            var msg = "World is now closed";
            if (bootPlayers)
                msg += ", and booting all online players.";

            PlayerManager.BroadcastToAuditChannel(player, msg);

            if (bootPlayers)
                PlayerManager.BootAllPlayers();
        }

        public static void PlayerEnterWorld(Session session, Character character)
        {
            var offlinePlayer = PlayerManager.GetOfflinePlayer(character.Id);

            if (offlinePlayer == null)
            {
                log.Error($"PlayerEnterWorld requested for character.Id 0x{character.Id:X8} not found in PlayerManager OfflinePlayers.");
                return;
            }

            var start = DateTime.UtcNow;
            DatabaseManager.Shard.GetPossessedBiotasInParallel(character.Id, biotas =>
            {
                log.DebugFormat("GetPossessedBiotasInParallel for {0} took {1:N0} ms", character.Name, (DateTime.UtcNow - start).TotalMilliseconds);

                ActionQueue.EnqueueAction(new ActionEventDelegate(() => DoPlayerEnterWorld(session, character, offlinePlayer.Biota, biotas)));
            });
        }

        /// <summary>
        /// Login materialization. Runs deferred on the world-simulation thread (enqueued by
        /// PlayerEnterWorld above), far from the handler that requested the login, and it builds a Player out
        /// of persisted shard state - so any corruption in that state surfaces here. An unhandled exception
        /// would escape to WorldManager's fatal handler, which does NOT crash the process: it STOPS the
        /// world, leaving every session connected to a frozen shard. One corrupted character must cost one
        /// failed login, not the world.
        /// </summary>
        private static void DoPlayerEnterWorld(Session session, Character character, Biota playerBiota, PossessedBiotas possessedBiotas)
        {
            // Whole-body containment rather than piecemeal null checks: the body has many null candidates
            // coming out of DB state (possessedBiotas, the Location reassignments, Instantiation) and
            // hardening them one at a time is a refactor of a login path that is otherwise working.
            try
            {
                DoPlayerEnterWorld_Inner(session, character, playerBiota, possessedBiotas);
            }
            catch (Exception ex)
            {
                log.Error($"WorldManager.DoPlayerEnterWorld: failed to materialize character {character?.Name} (0x{character?.Id:X8}) for account {session?.Account}; aborting this login", ex);

                // Signal the client and end the login cleanly. EnterGameGeneric dismisses with a popup and
                // returns the player to character select, which is the right outcome for "this character
                // could not be brought into the world". AccountSelectCallbackException is the existing
                // termination reason closest to "a login callback threw"; no new reason is added here
                // because SessionTerminationReasonDescriptions is an index-aligned array.
                try
                {
                    session?.Terminate(ACE.Server.Network.Enum.SessionTerminationReason.AccountSelectCallbackException,
                        new GameMessageCharacterError(ACE.Server.Network.Enum.CharacterError.EnterGameGeneric),
                        null,
                        "DoPlayerEnterWorld threw");
                }
                catch (Exception ex2)
                {
                    log.Error($"WorldManager.DoPlayerEnterWorld: could not terminate the session for character {character?.Name} after a failed enter-world", ex2);
                }
            }
        }

        private static void DoPlayerEnterWorld_Inner(Session session, Character character, Biota playerBiota, PossessedBiotas possessedBiotas)
        {
            Player player;

            Player.HandleNoLogLandblock(playerBiota, out var playerLoggedInOnNoLogLandblock);

            var stripAdminProperties = false;
            var addAdminProperties = false;
            var addSentinelProperties = false;
            if (ConfigManager.Config.Server.Accounts.OverrideCharacterPermissions)
            {
                if (session.AccessLevel <= AccessLevel.Advocate) // check for elevated characters
                {
                    if (playerBiota.WeenieType == WeenieType.Admin || playerBiota.WeenieType == WeenieType.Sentinel) // Downgrade weenie
                    {
                        character.IsPlussed = false;
                        playerBiota.WeenieType = WeenieType.Creature;
                        stripAdminProperties = true;
                    }
                }
                else if (session.AccessLevel >= AccessLevel.Sentinel && session.AccessLevel <= AccessLevel.Envoy)
                {
                    if (playerBiota.WeenieType == WeenieType.Creature || playerBiota.WeenieType == WeenieType.Admin) // Up/downgrade weenie
                    {
                        character.IsPlussed = true;
                        playerBiota.WeenieType = WeenieType.Sentinel;
                        addSentinelProperties = true;
                    }
                }
                else // Developers and Admins
                {
                    if (playerBiota.WeenieType == WeenieType.Creature || playerBiota.WeenieType == WeenieType.Sentinel) // Up/downgrade weenie
                    {
                        character.IsPlussed = true;
                        playerBiota.WeenieType = WeenieType.Admin;
                        addAdminProperties = true;
                    }
                }
            }

            if (playerBiota.WeenieType == WeenieType.Admin)
                player = new Admin(playerBiota, possessedBiotas.Inventory, possessedBiotas.WieldedItems, character, session);
            else if (playerBiota.WeenieType == WeenieType.Sentinel)
                player = new Sentinel(playerBiota, possessedBiotas.Inventory, possessedBiotas.WieldedItems, character, session);
            else
                player = new Player(playerBiota, possessedBiotas.Inventory, possessedBiotas.WieldedItems, character, session);

            session.SetPlayer(player);

            if (stripAdminProperties) // continue stripping properties
            {
                player.CloakStatus = CloakStatus.Undef;
                player.Attackable = true;
                player.SetProperty(PropertyBool.DamagedByCollisions, true);
                player.AdvocateLevel = null;
                player.ChannelsActive = null;
                player.ChannelsAllowed = null;
                player.Invincible = false;
                player.Cloaked = null;
                player.IgnoreHouseBarriers = false;
                player.IgnorePortalRestrictions = false;
                player.SafeSpellComponents = false;
                player.ReportCollisions = true;


                player.ChangesDetected = true;
                player.CharacterChangesDetected = true;
            }

            if (addSentinelProperties || addAdminProperties) // continue restoring properties to default
            {
                WorldObject weenie;

                if (addAdminProperties)
                    weenie = Factories.WorldObjectFactory.CreateWorldObject(DatabaseManager.World.GetCachedWeenie("admin"), new ACE.Entity.ObjectGuid(ACE.Entity.ObjectGuid.Invalid.Full));
                else
                    weenie = Factories.WorldObjectFactory.CreateWorldObject(DatabaseManager.World.GetCachedWeenie("sentinel"), new ACE.Entity.ObjectGuid(ACE.Entity.ObjectGuid.Invalid.Full));

                if (weenie != null)
                {
                    player.CloakStatus = CloakStatus.Off;
                    player.Attackable = weenie.Attackable;
                    player.SetProperty(PropertyBool.DamagedByCollisions, false);
                    player.AdvocateLevel = weenie.GetProperty(PropertyInt.AdvocateLevel);
                    player.ChannelsActive = (Channel?)weenie.GetProperty(PropertyInt.ChannelsActive);
                    player.ChannelsAllowed = (Channel?)weenie.GetProperty(PropertyInt.ChannelsAllowed);
                    player.Invincible = false;
                    player.Cloaked = false;


                    player.ChangesDetected = true;
                    player.CharacterChangesDetected = true;
                }
            }

            // If the client is missing a location, we start them off in the starter town they chose
            if (session.Player.Location == null)
            {
                if (session.Player.Instantiation != null)
                    session.Player.Location = new Position(session.Player.Instantiation);
                else
                    session.Player.Location = new Position(0xA9B40019, 84, 7.1f, 94, 0, 0, -0.0784591f, 0.996917f, 0);  // ultimate fallback
            }

            var olthoiPlayerReturnedToLifestone = session.Player.IsOlthoiPlayer && character.TotalLogins >= 1 && session.Player.LoginAtLifestone;
            if (olthoiPlayerReturnedToLifestone)
                session.Player.Location = new Position(session.Player.Sanctuary);

            // DreamWeave opening scene: on a character's first-ever login, route them through the
            // Loom - a per-character-private ephemeral instance of Portal Space (realm 2). Their
            // normal starting Location (the training hall of the starter town they chose) becomes
            // the exit target, stamped as EphemeralRealmExitTo so leaving the Loom lands them
            // there. Nothing else PlayerFactory set is touched: Instantiation still points at the
            // starter town, Sanctuary still points at the hall, and RecallsDisabled is still set,
            // so the hall plays out exactly as it does for a character who never saw the Loom.
            // Safely no-ops if the Loom realm is not registered (loom.sql not applied), or for Olthoi.
            //
            // A player who logs out INSIDE the Loom is resumed into a fresh copy of it rather than
            // dropped at the exit. Their instance is gone by then, and the block below would
            // otherwise read their stamped exit and quietly deposit them in the training hall -
            // which let a character skip the opening scene entirely just by relogging.
            var inLoom = session.Player.Location != null && session.Player.Location.RealmID == LoomRealmId
                && session.Player.GetPosition(PositionType.EphemeralRealmExitTo) != null;

            var routedToLoom = false;

            if ((character.TotalLogins == 0 || inLoom) && !session.Player.IsOlthoiPlayer
                && session.Player.Location != null && RealmManager.GetRealm(LoomRealmId) != null)
            {
                // on a first login the player's Location IS the hall, and becomes the exit target;
                // on a resume their Location is the dead Loom instance and the hall is already stamped
                var exitTo = inLoom
                    ? new Position(session.Player.GetPosition(PositionType.EphemeralRealmExitTo))
                    : new Position(session.Player.Location);

                var loomLandblock = RealmManager.GetNewEphemeralLandblock(new ACE.Entity.LandblockId(0x526AFFFFu), session.Player, LoomRealmId);
                if (loomLandblock != null && loomLandblock.IsDungeon)
                {
                    // authored in-game (@loc): the far end of the chamber, facing the Loomstone,
                    // so the walk toward the figure is the first thing the character does
                    var loomSpawn = new Position(0x526A0293u, 219.699234f, -49.672867f, -23.995001f, 0f, 0f, -0.932604f, 0.360902f, 0u);
                    session.Player.Location = new Position(loomSpawn, loomLandblock.Instance);
                    session.Player.SetPosition(PositionType.EphemeralRealmExitTo, exitTo);
                    routedToLoom = true;
                }
            }

            // DPS challenge (WaffleACE): a player who logged out mid-run forfeits it. The run flag is
            // persisted, so catch it here and re-home them to their lifestone rather than let the exit-stamp
            // logic below quietly deposit them back at the arena entrance. Falls through to the normal
            // validated-location path if they have no lifestone set.
            if (session.Player.DpsChallengeActive)
            {
                session.Player.DpsChallengeActive = false;
                session.Player.SetPosition(PositionType.EphemeralRealmExitTo, null);
                if (session.Player.Sanctuary != null)
                    session.Player.Location = new Position(session.Player.Sanctuary);
            }

            // Survival challenge (WaffleACE): a player who logged out mid-run forfeits it (no score). Same handling
            // as the DPS challenge above - clear the persisted run flag and re-home them to their lifestone rather
            // than let the exit-stamp logic below deposit them back at the (now-gone) arena entrance.
            if (session.Player.SurvivalChallengeActive)
            {
                session.Player.SurvivalChallengeActive = false;
                session.Player.SetPosition(PositionType.EphemeralRealmExitTo, null);
                if (session.Player.Sanctuary != null)
                    session.Player.Location = new Position(session.Player.Sanctuary);
            }

            // Wave challenge (WaffleACE): a player who logged out mid-gauntlet forfeits the run. The waves they
            // actually cleared were already banked into BestWaveScoreCenti as each one cleared, so nothing from a
            // completed wave is lost here - but this path does NOT go through FinishWaveChallengeRun, so any
            // partial progress on the wave that was in flight at logout is NOT banked; the run simply ends at the
            // last full clear. Same handling as the two challenges above - clear the persisted run flag and
            // re-home them to their lifestone rather than deposit them back at the (now-gone) arena entrance.
            if (session.Player.WaveChallengeActive)
            {
                session.Player.WaveChallengeActive = false;
                session.Player.SetPosition(PositionType.EphemeralRealmExitTo, null);
                if (session.Player.Sanctuary != null)
                    session.Player.Location = new Position(session.Player.Sanctuary);
            }

            // Speed challenge (WaffleACE): a player who logged out mid-run forfeits it - no time is recorded and
            // no character_speed_run row is written, because the run only ever files a row at the objective. The
            // ephemeral run state (bound instance, start time, season id) died with the session, which is
            // precisely why the active flag is persisted separately: it is the only thing left to catch here.
            // Same handling as the three challenges above - clear the persisted run flag and re-home them to
            // their lifestone rather than deposit them back at the (now-gone) season instance entrance.
            if (session.Player.SpeedChallengeActive)
            {
                session.Player.SpeedChallengeActive = false;
                session.Player.SetPosition(PositionType.EphemeralRealmExitTo, null);
                if (session.Player.Sanctuary != null)
                    session.Player.Location = new Position(session.Player.Sanctuary);
            }

            // Speed season rollover (WaffleACE): drop a personal-best cache left over from an EARLIER season.
            // TryFinishSpeedChallenge already re-stamps the pair on the first clear of a new season, but that
            // only fires for a player who actually runs it again. Without this, a player who set a qualifying
            // time in season N, never claimed the reward, and never enters season N+1 still carries the season
            // N number on their biota, and the reward NPC - which reads the cached best, not the board - would
            // pay them in N+1 off a time they set in a season that is over. Both properties are removed
            // together for the reason their doc comments give: a BestSpeedRunCenti without its matching
            // SpeedChallengeSeasonId is indistinguishable from a stale one. The decision itself - including
            // why a NULL active season is a scheduling gap that must clear NOTHING rather than a season
            // change - lives in Player.ShouldClearStaleSpeedBest, where it is unit tested.
            //
            // This is only ONE of the three places the staleness has to be caught, and on its own it is the
            // weakest: it fires exclusively on a fresh enter-world. TryFinishSpeedChallenge covers the player
            // who runs the new season, and the InqInt64Stat gate in EmoteManager covers the player who is
            // online across the rollover and never relogs at all. Removing any one of them re-opens a payout.
            if (Player.ShouldClearStaleSpeedBest(session.Player.SpeedChallengeSeasonId, SpeedSeasonManager.GetActiveSeason()?.Id))
            {
                session.Player.BestSpeedRunCenti = null;
                session.Player.SpeedChallengeSeasonId = null;
            }

            // ACRealms port: a saved position may point into an instance that no longer
            // exists (an ephemeral instance from a previous session) or an unregistered
            // realm - relocate to the stamped exit position, or re-home
            var validatedLocation = session.Player.Location.ValidateInstanceDestination(session.Player);
            if (validatedLocation.Instance != session.Player.Location.Instance)
            {
                var exitTo = session.Player.GetPosition(PositionType.EphemeralRealmExitTo);
                if (exitTo != null)
                {
                    session.Network.EnqueueSend(new GameMessageSystemChat("The instance you were in has expired and you have been transported outside!", ChatMessageType.System));
                    session.Player.Location = new Position(exitTo);
                    session.Player.SetPosition(PositionType.EphemeralRealmExitTo, null);
                }
                else
                {
                    log.Info($"WorldManager.DoPlayerEnterWorld: player {session.Player.Name}'s saved instance 0x{session.Player.Location.Instance:X8} is unavailable, relocating to home realm.");
                    session.Player.Location = validatedLocation;
                }
            }

            // Threads cleanup layer (b): destroy gems bound to runs that died while this player was offline
            ACE.Server.ThreadDungeons.ThreadDungeonSweeper.SweepPlayer(session.Player);

            session.Player.PlayerEnterWorld();

            var success = LandblockManager.AddObject(session.Player, true);
            if (!success)
            {
                // send to lifestone, or fallback location
                var fixLoc = session.Player.Sanctuary ?? new Position(0xA9B40019, 84, 7.1f, 94, 0, 0, -0.0784591f, 0.996917f, 0);

                log.Error($"WorldManager.DoPlayerEnterWorld: failed to spawn {session.Player.Name}, relocating to {fixLoc.ToLOCString()}");

                session.Player.Location = new Position(fixLoc);
                LandblockManager.AddObject(session.Player, true);

                var actionChain = new ActionChain();
                actionChain.AddDelaySeconds(5.0f);
                actionChain.AddAction(session.Player, () =>
                {
                    if (session != null && session.Player != null)
                        session.Player.Teleport(fixLoc);
                });
                actionChain.EnqueueChain();
            }

            // These warnings are set by DDD_InterrogationResponse
            if ((session.DatWarnCell || session.DatWarnLanguage || session.DatWarnPortal) && PropertyManager.GetBool("show_dat_warning").Item)
            {
                var msg = PropertyManager.GetString("dat_older_warning_msg").Item;
                var chatMsg = new GameMessageSystemChat(msg, ChatMessageType.System);
                session.Network.EnqueueSend(chatMsg);
            }

            var popup_header = PropertyManager.GetString("popup_header").Item;
            var popup_motd = PropertyManager.GetString("popup_motd").Item;

            if (character.TotalLogins <= 1)
            {
                // the welcome popup sends the player off to find the Society Greeter, who is in the
                // training hall - so it must not arrive while they are still standing in the Loom,
                // where there is no greeter and no training to begin. Hold it until the DreamWeave
                // sets them down in the hall; the exit portal plays it on arrival.
                if (routedToLoom)
                    player.DeferredWelcomePopup = true;
                else
                    SendWelcomePopup(session);
            }
            else if (!string.IsNullOrEmpty(popup_motd))
            {
                session.Network.EnqueueSend(new GameEventPopupString(session, AppendLines(popup_header, popup_motd)));
            }

            var info = "Welcome to Asheron's Call\n  powered by ACEmulator\n\nFor more information on commands supported by this server, type @acehelp\n";
            session.Network.EnqueueSend(new GameMessageSystemChat(info, ChatMessageType.Broadcast));

            var server_motd = PropertyManager.GetString("server_motd").Item;
            if (!string.IsNullOrEmpty(server_motd))
                session.Network.EnqueueSend(new GameMessageSystemChat($"{server_motd}\n", ChatMessageType.Broadcast));

            // Report the offline bonus time banked for however long this character was away (accrued in
            // PlayerEnterWorld above). No-op when the feature is disabled or nothing was banked.
            session.Player.SendOfflineBonusLoginMessage();

            // Let an under-leveled alt know it is receiving the catch-up bonus. No-op when the feature is
            // disabled or this is already the furthest-along character on the account.
            if (session.Player.IsAltCharacterBonusActive)
                session.Player.ShowAltCharacterBonusStatus();

            // Tell alpha testers what this shard lets them give themselves. Null - and so nothing sent -
            // on any server where the self-grant commands are not enabled, which is every server but stage.
            var stageTestMsg = StageTestCommands.GetLoginMessage();
            if (stageTestMsg != null)
                session.Network.EnqueueSend(new GameMessageSystemChat(stageTestMsg, ChatMessageType.Broadcast));

            if (olthoiPlayerReturnedToLifestone)
                session.Network.EnqueueSend(new GameMessageSystemChat("You have returned to the Olthoi Queen to serve the hive.", ChatMessageType.Broadcast));
            else if (playerLoggedInOnNoLogLandblock) // see http://acpedia.org/wiki/Mount_Elyrii_Hive
                session.Network.EnqueueSend(new GameMessageSystemChat("The currents of portal space cannot return you from whence you came. Your previous location forbids login.", ChatMessageType.Broadcast));            
        }

        /// <summary>
        /// The realm holding the DreamWeave opening scene (the Loom). Realm 2 is the
        /// no-combat realm per the realm standard (Content/realms/README.md); the Loom
        /// is its first tenant. Must match the realm id registered by loom.sql.
        /// </summary>
        public const ushort LoomRealmId = 2;

        /// <summary>
        /// The first-login welcome popup. Normally sent as the player enters the world, but a
        /// character routed through the Loom has it held back (Player.DeferredWelcomePopup) until
        /// the DreamWeave delivers them to the training hall the popup is telling them about.
        /// </summary>
        public static void SendWelcomePopup(Session session)
        {
            var popup_header = PropertyManager.GetString("popup_header").Item;
            var popup_motd = PropertyManager.GetString("popup_motd").Item;
            var popup_welcome = session.Player.IsOlthoiPlayer ? PropertyManager.GetString("popup_welcome_olthoi").Item : PropertyManager.GetString("popup_welcome").Item;

            if (session.Player.IsOlthoiPlayer)
                session.Network.EnqueueSend(new GameEventPopupString(session, AppendLines(popup_welcome, popup_motd)));
            else
                session.Network.EnqueueSend(new GameEventPopupString(session, AppendLines(popup_header, popup_motd, popup_welcome)));
        }

        private static string AppendLines(params string[] lines)
        {
            var result = "";
            foreach (var line in lines)
                if (!string.IsNullOrEmpty(line))
                    result += $"{line}\n";

            return Regex.Replace(result, "\n$", "");
        }

        /// <summary>
        /// ACE allows for multi-threading with thread boundaries based on the "LandblockGroup" concept
        /// The risk of moving the player immediately is that the player may move onto another LandblockGroup, and thus, cross thread boundaries
        /// This will enqueue the work onto WorldManager making the teleport thread safe.
        /// Note that this work will be done on the next tick, not immediately, so be careful about your order of operations.
        /// If you must ensure order, pass your follow up work in with the argument actionToFollowUpWith. That work will be enqueued onto the Player.
        /// </summary>
        public static void ThreadSafeTeleport(Player player, Position newPosition, IAction actionToFollowUpWith = null, bool fromPortal = false)
        {
            EnqueueAction(new ActionEventDelegate(() =>
            {
                player.Teleport(newPosition, fromPortal);

                if (actionToFollowUpWith != null)
                    EnqueueAction(actionToFollowUpWith);
            }));
        }

        public static void EnqueueAction(IAction action)
        {
            ActionQueue.EnqueueAction(action);
        }

        private static readonly RateLimiter updateGameWorldRateLimiter = new RateLimiter(60, TimeSpan.FromSeconds(1));

        /// <summary>
        /// Manages updating all entities on the world.
        ///  - Server-side command-line commands are handled in their own thread.
        ///  - Database I/O is handled in its own thread.
        ///  - Network commands come from their own listener threads, and are queued for each sessions which are then processed here.
        ///  - This thread does the rest of the work!
        /// </summary>
        private static void UpdateWorld()
        {
            log.DebugFormat("Starting UpdateWorld thread");

            WorldActive = true;
            var worldTickTimer = new Stopwatch();

            while (!pendingWorldStop)
            {
                // WaffleACE liveness stamp - MUST stay the first statement in this loop body. Everything
                // below it can block (database callbacks, landblock ticks, physics), and the whole point
                // of the stamp is to age while that happens so a hung tick is distinguishable from a
                // healthy one. One interlocked store per iteration at ~100+ iterations/sec is free; do
                // not move it, guard it behind a rate limiter, or replace it with a wall-clock read.
                Interlocked.Exchange(ref lastWorldTickTicks, Environment.TickCount64);
                WorldEverStarted = true;

                /*
                When it comes to thread safety for Landblocks and WorldObjects, ACE makes the following assumptions:

                 * Inbound ClientMessages and GameActions are handled on the main UpdateWorld thread.
                   - These actions may load Landblocks and modify other WorldObjects safely.

                 * PlayerEnterWorld queue is run on the main UpdateWorld thread.
                   - These actions may load Landblocks and modify other WorldObjects safely.

                 * Landblock Groups (calculated by LandblockManager) can be processed in parallel.

                 * Adjacent Landblocks will always be run on the same thread.

                 * Non-adjacent landblocks might be run on different threads.
                   - If two non-adjacent landblocks both touch the same landblock, and that landblock is active, they will be run on the same thread.

                 * Database results are returned from a task spawned in SerializedShardDatabase (via callback).
                   - Minimal processing should be done from the callback. Return as quickly as possible to let the database thread do database work.
                   - The processing of these results should be queued to an ActionQueue

                 * The only cases where it's acceptable for to create a new Task, Thread or Parallel loop are the following:
                   - Every scenario must be one where you don't care about breaking ACE
                   - DeveloperCommand Handlers
                */

                worldTickTimer.Restart();

                ServerPerformanceMonitor.RestartEvent(ServerPerformanceMonitor.MonitorType.PlayerManager_Tick);
                PlayerManager.Tick();
                ServerPerformanceMonitor.RegisterEventEnd(ServerPerformanceMonitor.MonitorType.PlayerManager_Tick);

                // WaffleACE: IP active-player limit re-check sweep. Self-rate-limited to ip_limit_sweep_seconds
                // and returns immediately when the feature is off, so calling it every tick is cheap. This is
                // the ONLY reaction site for the limit after login - see IpLimitManager.Tick.
                IpLimitManager.Tick();

                ServerPerformanceMonitor.RestartEvent(ServerPerformanceMonitor.MonitorType.NetworkManager_InboundClientMessageQueueRun);
                NetworkManager.InboundMessageQueue.RunActions();
                ServerPerformanceMonitor.RegisterEventEnd(ServerPerformanceMonitor.MonitorType.NetworkManager_InboundClientMessageQueueRun);

                // This will consist of PlayerEnterWorld actions, as well as other game world actions that require thread safety
                ServerPerformanceMonitor.RestartEvent(ServerPerformanceMonitor.MonitorType.actionQueue_RunActions);
                ActionQueue.RunActions();
                ServerPerformanceMonitor.RegisterEventEnd(ServerPerformanceMonitor.MonitorType.actionQueue_RunActions);

                ServerPerformanceMonitor.RestartEvent(ServerPerformanceMonitor.MonitorType.DelayManager_RunActions);
                DelayManager.RunActions();
                ServerPerformanceMonitor.RegisterEventEnd(ServerPerformanceMonitor.MonitorType.DelayManager_RunActions);

                var tickStart = worldTickTimer.Elapsed;
                ServerPerformanceMonitor.RestartEvent(ServerPerformanceMonitor.MonitorType.UpdateGameWorld);
                var gameWorldUpdated = UpdateGameWorld();
                ServerPerformanceMonitor.RegisterEventEnd(ServerPerformanceMonitor.MonitorType.UpdateGameWorld);

                // Monitoring: record the real game-tick duration, only when the world actually updated so
                // idle spin-sleeps don't skew it. Exposed as a histogram (ServerMetrics -> dotnet-monitor).
                if (gameWorldUpdated)
                    ServerMetrics.RecordWorldTick((worldTickTimer.Elapsed - tickStart).TotalMilliseconds);

                ServerPerformanceMonitor.RestartEvent(ServerPerformanceMonitor.MonitorType.NetworkManager_DoSessionWork);
                int sessionCount = NetworkManager.DoSessionWork();
                ServerPerformanceMonitor.RegisterEventEnd(ServerPerformanceMonitor.MonitorType.NetworkManager_DoSessionWork);

                ServerPerformanceMonitor.Tick();

                // We only relax the CPU if our game world is able to update at the target rate.
                // We do not sleep if our game world just updated. This is to prevent the scenario where our game world can't keep up. We don't want to add further delays.
                // If our game world is able to keep up, it will not be updated on most ticks. It's on those ticks (between updates) that we will relax the CPU.
                if (!gameWorldUpdated)
                    Thread.Sleep(sessionCount == 0 ? 10 : 1); // Relax the CPU more if no sessions are connected

                Timers.PortalYearTicks += worldTickTimer.Elapsed.TotalSeconds;
            }

            // Any world event still running dies with the world thread, not with the process: its spawned
            // objects are transient and must be destroyed while the landblocks are still up.
            WorldEventManager.OnShutdown();

            // World has finished operations and concedes the thread to garbage collection
            WorldActive = false;
        }

        /// <summary>
        /// Projected to run at a reasonable rate for gameplay (30-60fps)
        /// </summary>
        public static bool UpdateGameWorld()
        {
            if (updateGameWorldRateLimiter.GetSecondsToWaitBeforeNextEvent() > 0)
                return false;

            updateGameWorldRateLimiter.RegisterEvent();

            ServerPerformanceMonitor.RestartCumulativeEvents();
            ServerPerformanceMonitor.RestartEvent(ServerPerformanceMonitor.MonitorType.UpdateGameWorld_Entire);

            LandblockManager.Tick(Timers.PortalYearTicks);

            HouseManager.Tick();

            // The three sweeps below are the tail of the world heartbeat and, unlike LandblockManager.Tick
            // and HouseManager.Tick above, carry no containment of their own. UpdateGameWorld and its caller
            // UpdateWorld have no outer catch either, so a throw from any of them escapes to WorldManager's
            // fatal handler, which does NOT crash the process - it STOPS the world (process up, sessions
            // connected, nothing ticking). That is the exact shape of the 2026-09-01 Portal Recall outage.
            // Each is contained separately so one failing manager does not skip the ones after it; none of
            // them is restartable from here, so on failure we log the manager and move on, and the sweep is
            // simply retried on the next heartbeat.
            try
            {
                FellowshipManager.Tick();
            }
            catch (Exception ex)
            {
                log.Error("WorldManager.UpdateGameWorld(): FellowshipManager.Tick() threw; skipping it this heartbeat", ex);
            }

            try
            {
                WorldEventManager.Tick();
            }
            catch (Exception ex)
            {
                log.Error("WorldManager.UpdateGameWorld(): WorldEventManager.Tick() threw; skipping it this heartbeat", ex);
            }

            try
            {
                // WaffleACE Threads: reaps expired runs and runs whose private landblock has already
                // unloaded. Self-rate-limited to a 15-second sweep and returns immediately otherwise.
                ACE.Server.ThreadDungeons.ThreadDungeonManager.Tick();
            }
            catch (Exception ex)
            {
                log.Error("WorldManager.UpdateGameWorld(): ThreadDungeonManager.Tick() threw; skipping it this heartbeat", ex);
            }

            try
            {
                // WaffleACE Mule Vendor: drops account vault stores that no vendor window is looking at.
                // Self-rate-limited to a one-minute sweep and returns immediately otherwise, so calling it
                // from the heartbeat is cheap.
                AccountVaultManager.Tick(Time.GetUnixTime());
            }
            catch (Exception ex)
            {
                log.Error("WorldManager.UpdateGameWorld(): AccountVaultManager.Tick() threw; skipping it this heartbeat", ex);
            }

            ServerPerformanceMonitor.RegisterEventEnd(ServerPerformanceMonitor.MonitorType.UpdateGameWorld_Entire);
            ServerPerformanceMonitor.RegisterCumulativeEvents();

            return true;
        }

        /// <summary>
        /// Function to begin ending the operations inside of an active world.
        /// </summary>
        public static void StopWorld() { pendingWorldStop = true; }
    }
}
