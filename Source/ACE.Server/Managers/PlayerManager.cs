using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using log4net;

using ACE.Common;
using ACE.Database;
using ACE.Database.Models.Shard;
using ACE.Entity;
using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Server.Entity;
using ACE.Server.Network.Enum;
using ACE.Server.Network.GameEvent.Events;
using ACE.Server.Network.GameMessages;
using ACE.Server.Network.GameMessages.Messages;
using ACE.Server.WorldObjects;

using Biota = ACE.Entity.Models.Biota;

namespace ACE.Server.Managers
{
    public static class PlayerManager
    {
        private static readonly ILog log = LogManager.GetLogger(System.Reflection.MethodBase.GetCurrentMethod().DeclaringType);

        private static readonly ReaderWriterLockSlim playersLock = new ReaderWriterLockSlim();
        private static readonly Dictionary<uint, Player> onlinePlayers = new Dictionary<uint, Player>();
        private static readonly Dictionary<uint, OfflinePlayer> offlinePlayers = new Dictionary<uint, OfflinePlayer>();

        // indexed by player name
        private static readonly Dictionary<string, IPlayer> playerNames = new Dictionary<string, IPlayer>(StringComparer.OrdinalIgnoreCase);

        // indexed by account id
        private static readonly Dictionary<uint, Dictionary<uint, IPlayer>> playerAccounts = new Dictionary<uint, Dictionary<uint, IPlayer>>();

        /// <summary>
        /// OfflinePlayers will be saved to the database every 1 hour
        /// </summary>
        private static readonly TimeSpan databaseSaveInterval = TimeSpan.FromHours(1);

        private static DateTime lastDatabaseSave = DateTime.MinValue;

        /// <summary>
        /// This will load all the players from the database into the OfflinePlayers dictionary. It should be called before WorldManager is initialized.
        /// </summary>
        public static void Initialize()
        {
            var results = DatabaseManager.Shard.BaseDatabase.GetAllPlayerBiotasInParallel();

            Parallel.ForEach(results, ConfigManager.Config.Server.Threading.DatabaseParallelOptions, result =>
            {
                var offlinePlayer = new OfflinePlayer(result);

                lock (offlinePlayers)
                    offlinePlayers[offlinePlayer.Guid.Full] = offlinePlayer;

                lock (playerNames)
                    playerNames[offlinePlayer.Name] = offlinePlayer;

                lock (playerAccounts)
                {
                    if (offlinePlayer.Account != null)
                    {
                        if (!playerAccounts.TryGetValue(offlinePlayer.Account.AccountId, out var playerAccountsDict))
                        {
                            playerAccountsDict = new Dictionary<uint, IPlayer>();
                            playerAccounts[offlinePlayer.Account.AccountId] = playerAccountsDict;
                        }
                        playerAccountsDict[offlinePlayer.Guid.Full] = offlinePlayer;
                    }
                    else
                        log.Error($"PlayerManager.Initialize: couldn't find account for player {offlinePlayer.Name} ({offlinePlayer.Guid})");
                }
            });
        }

        private static readonly LinkedList<Player> playersPendingLogoff = new LinkedList<Player>();

        private static readonly LinkedList<Player> playersPendingFinalLogoff = new LinkedList<Player>();

        public static void AddPlayerToLogoffQueue(Player player)
        {
            if (!playersPendingLogoff.Contains(player))
                playersPendingLogoff.AddLast(player);
        }

        private static readonly TimeSpan playerFinalLogoutDuration = TimeSpan.FromMinutes(15);

        public static void AddPlayerToFinalLogoffQueue(Player player)
        {
            if (!playersPendingFinalLogoff.Contains(player))
            {
                player.LogOffFinalizedTime = Time.GetFutureUnixTime(playerFinalLogoutDuration.TotalSeconds);
                playersPendingFinalLogoff.AddLast(player);
            }
        }

        public static void RemovePlayerFromFinalLogoffQueue(Player player)
        {
             playersPendingFinalLogoff.Remove(player);
        }

        public static void Tick()
        {
            // Database Save
            if (lastDatabaseSave + databaseSaveInterval <= DateTime.UtcNow)
                SaveOfflinePlayersWithChanges();

            var currentUnixTime = Time.GetUnixTime();

            while (playersPendingLogoff.Count > 0)
            {
                var first = playersPendingLogoff.First.Value;

                if (first.LogoffTimestamp <= currentUnixTime)
                {
                    playersPendingLogoff.RemoveFirst();

                    // Remove-then-act on the world thread: an escaping throw stops the world (WorldManager's
                    // fatal handler does not crash the process, it stops the tick) and, because the player has
                    // already been dequeued, would leave them online forever - which blocks shutdown's
                    // "waiting for N players to log off" wait. Contain it and log the character.
                    //
                    // We deliberately do NOT re-add to playersPendingLogoff: a permanently failing logoff
                    // would then hot-loop every tick. Instead we make sure the 15-minute final-logoff backstop
                    // is armed (AddPlayerToFinalLogoffQueue is idempotent - it Contains-checks first), because
                    // LogOut_Inner may have thrown before it got to arm it itself.
                    try
                    {
                        first.LogOut_Inner();
                        first.Session.logOffRequestTime = DateTime.UtcNow;
                    }
                    catch (Exception ex)
                    {
                        log.Error($"PlayerManager.Tick(): LogOut_Inner threw for {first.Name} (0x{first.Guid}), dropping from the logoff queue and arming the final-logoff backstop", ex);

                        try
                        {
                            AddPlayerToFinalLogoffQueue(first);
                        }
                        catch (Exception ex2)
                        {
                            log.Error($"PlayerManager.Tick(): could not arm the final-logoff backstop for {first.Name} (0x{first.Guid}); they may stay online until restart", ex2);
                        }
                    }
                }
                else
                {
                    break;
                }
            }

            while (playersPendingFinalLogoff.Count > 0)
            {
                var first = playersPendingFinalLogoff.First.Value;

                if (currentUnixTime >= first.LogOffFinalizedTime)
                {
                    playersPendingFinalLogoff.RemoveFirst();

                    // Same remove-then-act contract as the queue above, but this IS the backstop - nothing
                    // else will ever log this character off. So on failure we do not re-queue (that would
                    // hot-loop a bad logoff every tick); we log, then force them out of the online set
                    // directly, which is the terminal step Player.FinalizeLogout would have performed. Leaving
                    // them online instead would block shutdown's "waiting for N players to log off" wait
                    // indefinitely.
                    try
                    {
                        first.ForcedLogOffRequested = true;
                        first.Session?.Terminate(SessionTerminationReason.AutoForcedLogOff, new GameMessageBootAccount(" because the character was forced to log off by system"));
                        first.ForceLogoff();
                    }
                    catch (Exception ex)
                    {
                        log.Error($"PlayerManager.Tick(): ForceLogoff threw for {first.Name} (0x{first.Guid}), replaying the terminal logout steps by hand", ex);

                        // ForceLogoff routes to Player.FinalizeLogout, which performs six steps in a
                        // deliberate order: dequeue from the final-logoff queue (already done above by
                        // RemoveFirst), remove the object from its landblock, stamp the logout properties,
                        // persist final state, switch the character out of the online set, and only then
                        // release the account bank cache entry. A throw partway through - a DB save fault is
                        // exactly the failure class this sweep contains - leaves the rest of that list unrun,
                        // and each unrun step leaks something distinct: a WorldObject still resident in
                        // Landblock.players whose Player_Tick keeps firing every tick against a terminated
                        // Session, a stale LogoffTimestamp that inflates the next login's offline bonus, an
                        // unpersisted character, and an AccountBankManager entry that has no TTL and is only
                        // ever evicted by ReleaseAccountBank. So replay the steps here, in FinalizeLogout's
                        // order, each one independently contained so a second failure does not block the
                        // steps after it.
                        try
                        {
                            // showError: false, unlike FinalizeLogout's call, precisely because this is a
                            // replay - FinalizeLogout may already have removed the object before it threw, and
                            // a "Couldn't find" warning for the expected case would be noise.
                            first.CurrentLandblock?.RemoveWorldObject(first.Guid, false, false, false);
                        }
                        catch (Exception ex2)
                        {
                            log.Error($"PlayerManager.Tick(): could not remove {first.Name} (0x{first.Guid}) from landblock 0x{first.CurrentLandblock?.Id}; they may keep ticking until restart", ex2);
                        }

                        // Before the save, exactly as FinalizeLogout orders it, and it must not be skipped:
                        // this is what advances LogoffTimestamp. If the replay omitted it, the persisted
                        // timestamp would stay at whatever a PRIOR session's logout wrote, and
                        // Player_OfflineBonus.AccrueOfflineBonus grants (now - LogoffTimestamp) at the next
                        // login - so a stale value inflates that grant (capped by offline_bonus_max_seconds,
                        // but inflated). That is economy math, not just bookkeeping.
                        try
                        {
                            first.SetPropertiesAtLogOut();
                        }
                        catch (Exception ex2)
                        {
                            log.Error($"PlayerManager.Tick(): could not stamp the logout properties for {first.Name} (0x{first.Guid}); their LogoffTimestamp stays at the previous logout, which inflates the next login's offline bonus", ex2);
                        }

                        try
                        {
                            first.SavePlayerToDatabase();
                        }
                        catch (Exception ex2)
                        {
                            log.Error($"PlayerManager.Tick(): could not save {first.Name} (0x{first.Guid}) during forced logoff; their final state is not persisted", ex2);
                        }

                        try
                        {
                            // The most likely failure point in FinalizeLogout is a step AFTER this one, in
                            // which case the character is already offline and calling again would return
                            // false - SwitchPlayerFromOnlineToOffline reports its own "should never happen"
                            // for a failed onlinePlayers.Remove. Checking first keeps that expected replay
                            // case out of the error log, so a real stuck-online player stays visible.
                            if (GetOnlinePlayer(first.Guid) == null)
                                log.Info($"PlayerManager.Tick(): {first.Name} (0x{first.Guid}) was already switched offline before ForceLogoff threw; skipping that replay step");
                            else if (!SwitchPlayerFromOnlineToOffline(first))
                                log.Error($"PlayerManager.Tick(): SwitchPlayerFromOnlineToOffline returned false for {first.Name} (0x{first.Guid})");
                        }
                        catch (Exception ex2)
                        {
                            log.Error($"PlayerManager.Tick(): could not force {first.Name} (0x{first.Guid}) offline; they may stay online until restart and block shutdown", ex2);
                        }

                        // Last, and only after the online/offline switch, exactly as FinalizeLogout orders it:
                        // ReleaseAccountBank consults the account's online snapshot and would decline to
                        // release while this character still counted as online.
                        try
                        {
                            first.ReleaseAccountBank();
                        }
                        catch (Exception ex2)
                        {
                            log.Error($"PlayerManager.Tick(): could not release the account bank cache for {first.Name} (0x{first.Guid}); the entry stays resident until restart", ex2);
                        }
                    }
                }
                else
                {
                    break;
                }
            }
        }

        /// <summary>
        /// This will save any player in the OfflinePlayers dictionary that has ChangesDetected. The biotas are saved in parallel.
        /// </summary>
        public static void SaveOfflinePlayersWithChanges()
        {
            lastDatabaseSave = DateTime.UtcNow;

            var biotas = new Collection<(Biota biota, ReaderWriterLockSlim rwLock)>();

            playersLock.EnterReadLock();
            try
            {
                foreach (var player in offlinePlayers.Values)
                {
                    if (player.ChangesDetected)
                    {
                        player.SaveBiotaToDatabase(false);
                        biotas.Add((player.Biota, player.BiotaDatabaseLock));
                    }
                }
            }
            finally
            {
                playersLock.ExitReadLock();
            }

            DatabaseManager.Shard.SaveBiotasInParallel(biotas, null, true);
        }
        

        /// <summary>
        /// This would be used when a new player is created after the server has started.
        /// When a new Player is created, they're created in an offline state, and then set to online shortly after as the login sequence continues.
        /// </summary>
        public static void AddOfflinePlayer(Player player)
        {
            playersLock.EnterWriteLock();
            try
            {
                var offlinePlayer = new OfflinePlayer(player.Biota);
                offlinePlayers[offlinePlayer.Guid.Full] = offlinePlayer;

                playerNames[offlinePlayer.Name] = offlinePlayer;

                if (!playerAccounts.TryGetValue(offlinePlayer.Account.AccountId, out var playerAccountsDict))
                {
                    playerAccountsDict = new Dictionary<uint, IPlayer>();
                    playerAccounts[offlinePlayer.Account.AccountId] = playerAccountsDict;
                }
                playerAccountsDict[offlinePlayer.Guid.Full] = offlinePlayer;
            }
            finally
            {
                playersLock.ExitWriteLock();
            }
        }

        /// <summary>
        /// This will return null if the player wasn't found.
        /// </summary>
        public static OfflinePlayer GetOfflinePlayer(ObjectGuid guid)
        {
            return GetOfflinePlayer(guid.Full);
        }

        /// <summary>
        /// This will return null if the player wasn't found.
        /// </summary>
        public static OfflinePlayer GetOfflinePlayer(uint guid)
        {
            playersLock.EnterReadLock();
            try
            {
                if (offlinePlayers.TryGetValue(guid, out var value))
                    return value;
            }
            finally
            {
                playersLock.ExitReadLock();
            }

            return null;
        }

        /// <summary>
        /// This will return null of the name was not found.
        /// </summary>
        public static OfflinePlayer GetOfflinePlayer(string name)
        {
            var admin = "+" + name;

            playersLock.EnterReadLock();
            try
            {
                var offlinePlayer = offlinePlayers.Values.FirstOrDefault(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase) || p.Name.Equals(admin, StringComparison.OrdinalIgnoreCase));

                if (offlinePlayer != null)
                    return offlinePlayer;
            }
            finally
            {
                playersLock.ExitReadLock();
            }

            return null;
        }

        public static List<IPlayer> GetAllPlayers()
        {
            var offlinePlayers = GetAllOffline();
            var onlinePlayers = GetAllOnline();

            var allPlayers = new List<IPlayer>();

            allPlayers.AddRange(offlinePlayers);
            allPlayers.AddRange(onlinePlayers);

            return allPlayers;
        }

        public static Dictionary<uint, IPlayer> GetAccountPlayers(uint accountId)
        {
            playersLock.EnterReadLock();
            try
            {
                playerAccounts.TryGetValue(accountId, out var accountPlayers);
                return accountPlayers;
            }
            finally
            {
                playersLock.ExitReadLock();
            }
        }

        /// <summary>
        /// Returns a point-in-time copy of every character on an account (online and offline). Unlike
        /// <see cref="GetAccountPlayers"/>, which hands back the live dictionary, this snapshots under the read
        /// lock so callers can enumerate it safely from any thread (e.g. the off-thread XP grant path) without
        /// racing a concurrent login/logout mutation. Empty list when the account has no known characters.
        /// </summary>
        public static List<IPlayer> GetAccountPlayersSnapshot(uint accountId)
        {
            playersLock.EnterReadLock();
            try
            {
                if (playerAccounts.TryGetValue(accountId, out var accountPlayers))
                    return accountPlayers.Values.ToList();

                return new List<IPlayer>();
            }
            finally
            {
                playersLock.ExitReadLock();
            }
        }

        public static int GetOfflineCount()
        {
            playersLock.EnterReadLock();
            try
            {
                return offlinePlayers.Count;
            }
            finally
            {
                playersLock.ExitReadLock();
            }
        }

        public static List<OfflinePlayer> GetAllOffline()
        {
            var results = new List<OfflinePlayer>();

            playersLock.EnterReadLock();
            try
            {
                foreach (var player in offlinePlayers.Values)
                    results.Add(player);
            }
            finally
            {
                playersLock.ExitReadLock();
            }

            return results;
        }

        public static int GetOnlineCount()
        {
            playersLock.EnterReadLock();
            try
            {
                return onlinePlayers.Count;
            }
            finally
            {
                playersLock.ExitReadLock();
            }
        }

        /// <summary>
        /// This will return null if the player wasn't found.
        /// </summary>
        public static Player GetOnlinePlayer(ObjectGuid guid)
        {
            return GetOnlinePlayer(guid.Full);
        }

        /// <summary>
        /// This will return null if the player wasn't found.
        /// </summary>
        public static Player GetOnlinePlayer(uint guid)
        {
            playersLock.EnterReadLock();
            try
            {
                if (onlinePlayers.TryGetValue(guid, out var value))
                    return value;
            }
            finally
            {
                playersLock.ExitReadLock();
            }

            return null;
        }

        /// <summary>
        /// This will return null of the name was not found.
        /// </summary>
        public static Player GetOnlinePlayer(string name)
        {
            var admin = "+" + name;

            playersLock.EnterReadLock();
            try
            {
                var onlinePlayer = onlinePlayers.Values.FirstOrDefault(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase) || p.Name.Equals(admin, StringComparison.OrdinalIgnoreCase));

                if (onlinePlayer != null)
                    return onlinePlayer;
            }
            finally
            {
                playersLock.ExitReadLock();
            }

            return null;
        }

        public static List<Player> GetAllOnline()
        {
            var results = new List<Player>();

            playersLock.EnterReadLock();
            try
            {
                foreach (var player in onlinePlayers.Values)
                    results.Add(player);
            }
            finally
            {
                playersLock.ExitReadLock();
            }

            return results;
        }


        /// <summary>
        /// This will return true if the player was successfully added.
        /// It will return false if the player was not found in the OfflinePlayers dictionary (which should never happen), or player already exists in the OnlinePlayers dictionary (which should never happen).
        /// This will always be preceded by a call to GetOfflinePlayer()
        /// </summary>
        public static bool SwitchPlayerFromOfflineToOnline(Player player)
        {
            playersLock.EnterWriteLock();
            try
            {
                if (!offlinePlayers.Remove(player.Guid.Full, out var offlinePlayer))
                    return false; // This should never happen

                if (offlinePlayer.ChangesDetected)
                    player.ChangesDetected = true;

                player.Allegiance = offlinePlayer.Allegiance;
                player.AllegianceNode = offlinePlayer.AllegianceNode;

                if (!onlinePlayers.TryAdd(player.Guid.Full, player))
                    return false;

                playerNames[offlinePlayer.Name] = player;

                playerAccounts[offlinePlayer.Account.AccountId][offlinePlayer.Guid.Full] = player;
            }
            finally
            {
                playersLock.ExitWriteLock();
            }

            AllegianceManager.LoadPlayer(player);

            player.SendFriendStatusUpdates(false, !player.GetAppearOffline());

            return true;
        }

        /// <summary>
        /// This will return true if the player was successfully added.
        /// It will return false if the player was not found in the OnlinePlayers dictionary (which should never happen), or player already exists in the OfflinePlayers dictionary (which should never happen).
        /// </summary>
        public static bool SwitchPlayerFromOnlineToOffline(Player player)
        {
            playersLock.EnterWriteLock();
            try
            {
                if (!onlinePlayers.Remove(player.Guid.Full, out _))
                    return false; // This should never happen

                var offlinePlayer = new OfflinePlayer(player.Biota);

                offlinePlayer.Allegiance = player.Allegiance;
                offlinePlayer.AllegianceNode = player.AllegianceNode;

                if (!offlinePlayers.TryAdd(offlinePlayer.Guid.Full, offlinePlayer))
                    return false;

                playerNames[offlinePlayer.Name] = offlinePlayer;

                playerAccounts[offlinePlayer.Account.AccountId][offlinePlayer.Guid.Full] = offlinePlayer;
            }
            finally
            {
                playersLock.ExitWriteLock();
            }

            player.SendFriendStatusUpdates(!player.GetAppearOffline(), false);
            player.HandleAllegianceOnLogout();

            return true;
        }

        /// <summary>
        /// Called when a character is initially deleted on the character select screen
        /// </summary>
        public static void HandlePlayerDelete(uint characterGuid)
        {
            AllegianceManager.HandlePlayerDelete(characterGuid);

            HouseManager.HandlePlayerDelete(characterGuid);
        }

        /// <summary>
        /// This will return true if the player was successfully found and removed from the OfflinePlayers dictionary.
        /// It will return false if the player was not found in the OfflinePlayers dictionary (which should never happen).
        /// </summary>
        public static bool ProcessDeletedPlayer(uint guid)
        {
            playersLock.EnterWriteLock();
            try
            {
                if (!offlinePlayers.Remove(guid, out var offlinePlayer))
                    return false; // This should never happen

                playerNames.Remove(offlinePlayer.Name);

                playerAccounts[offlinePlayer.Account.AccountId].Remove(offlinePlayer.Guid.Full);
            }
            finally
            {
                playersLock.ExitWriteLock();
            }

            return true;
        }


        /// <summary>
        /// This will return null if the name was not found.
        /// </summary>
        public static IPlayer FindByName(string name)
        {
            return FindByName(name, out _);
        }

        /// <summary>
        /// This will return null if the name was not found.
        /// </summary>
        public static IPlayer FindByName(string name, out bool isOnline)
        {
            playersLock.EnterReadLock();
            try
            {
                playerNames.TryGetValue(name.TrimStart('+'), out var player);

                isOnline = player != null && player is Player;

                return player;
            }
            finally
            {
                playersLock.ExitReadLock();
            }
        }

        /// <summary>
        /// This will return null if the guid was not found.
        /// </summary>
        public static IPlayer FindByGuid(ObjectGuid guid)
        {
            return FindByGuid(guid, out _);
        }

        /// <summary>
        /// This will return null if the guid was not found.
        /// </summary>
        public static IPlayer FindByGuid(ObjectGuid guid, out bool isOnline)
        {
            return FindByGuid(guid.Full, out isOnline);
        }

        /// <summary>
        /// This will return null if the guid was not found.
        /// </summary>
        public static IPlayer FindByGuid(uint guid)
        {
            return FindByGuid(guid, out _);
        }

        /// <summary>
        /// This will return null if the guid was not found.
        /// </summary>
        public static IPlayer FindByGuid(uint guid, out bool isOnline)
        {
            playersLock.EnterReadLock();
            try
            {
                if (onlinePlayers.TryGetValue(guid, out var onlinePlayer))
                {
                    isOnline = true;
                    return onlinePlayer;
                }

                isOnline = false;

                if (offlinePlayers.TryGetValue(guid, out var offlinePlayer))
                    return offlinePlayer;
            }
            finally
            {
                playersLock.ExitReadLock();
            }

            return null;
        }


        /// <summary>
        /// Returns a list of all players who are under a monarch
        /// </summary>
        /// <param name="monarch">The monarch of an allegiance</param>
        public static List<IPlayer> FindAllByMonarch(ObjectGuid monarch)
        {
            var results = new List<IPlayer>();

            playersLock.EnterReadLock();
            try
            {
                // this kind of sucks, possibly investigate?
                var onlinePlayersResult = onlinePlayers.Values.Where(p => p.MonarchId == monarch.Full);
                var offlinePlayersResult = offlinePlayers.Values.Where(p => p.MonarchId == monarch.Full);

                results.AddRange(onlinePlayersResult);
                results.AddRange(offlinePlayersResult);
            }
            finally
            {
                playersLock.ExitReadLock();
            }

            return results;
        }


        /// <summary>
        /// This will return a list of Players that have this guid as a friend.
        /// </summary>
        public static List<Player> GetOnlineInverseFriends(ObjectGuid guid)
        {
            var results = new List<Player>();

            playersLock.EnterReadLock();
            try
            {
                foreach (var player in onlinePlayers.Values)
                {
                    if (player.Character.HasAsFriend(guid.Full, player.CharacterDatabaseLock))
                        results.Add(player);
                }
            }
            finally
            {
                playersLock.ExitReadLock();
            }

            return results;
        }


        /// <summary>
        /// Broadcasts GameMessage to all online sessions.
        /// </summary>
        public static void BroadcastToAll(GameMessage msg)
        {
            foreach (var player in GetAllOnline())
                player.Session.Network.EnqueueSend(msg);
        }

        public static void BroadcastToAuditChannel(Player issuer, string message)
        {
            if (issuer != null)
                BroadcastToChannel(Channel.Audit, issuer, message, true, true);
            else
                BroadcastToChannelFromConsole(Channel.Audit, message);

            // Persist a disk-backed record so the audit trail survives beyond the ephemeral in-game
            // Audit channel (which is only seen by online staff who happen to be listening).
            if (PropertyManager.GetBool("log_audit", true).Item)
                log.Info($"[AUDIT] {(issuer != null ? $"{issuer.Name}: " : "")}{message}");

            // Mirror to Discord for off-server monitoring. No-ops unless the relay is enabled and
            // discord_webhook_url_audit is set; never blocks (queued and flushed on a timer).
            DiscordRelayManager.QueueAudit(issuer?.Name, message);

            //LogBroadcastChat(Channel.Audit, issuer, message);
        }

        public static void BroadcastToChannel(Channel channel, Player sender, string message, bool ignoreSquelch = false, bool ignoreActive = false)
        {
            if ((sender.ChannelsActive.HasValue && sender.ChannelsActive.Value.HasFlag(channel)) || ignoreActive)
            {
                foreach (var player in GetAllOnline().Where(p => (p.ChannelsActive ?? 0).HasFlag(channel)))
                {
                    if (!player.SquelchManager.Squelches.Contains(sender) || ignoreSquelch)
                        player.Session.Network.EnqueueSend(new GameEventChannelBroadcast(player.Session, channel, sender.Guid == player.Guid ? "" : sender.Name, message));
                }

                LogBroadcastChat(channel, sender, message);
            }
        }

        public static void LogBroadcastChat(Channel channel, WorldObject sender, string message)
        {
            switch (channel)
            {
                case Channel.Abuse:
                    if (!PropertyManager.GetBool("chat_log_abuse").Item)
                        return;
                    break;
                case Channel.Admin:
                    if (!PropertyManager.GetBool("chat_log_admin").Item)
                        return;
                    break;
                case Channel.AllBroadcast: // using this to sub in for a WorldBroadcast channel which isn't technically a channel
                    if (!PropertyManager.GetBool("chat_log_global").Item)
                        return;
                    break;
                case Channel.Audit:
                    if (!PropertyManager.GetBool("chat_log_audit").Item)
                        return;
                    break;
                case Channel.Advocate1:
                case Channel.Advocate2:
                case Channel.Advocate3:
                    if (!PropertyManager.GetBool("chat_log_advocate").Item)
                        return;
                    break;
                case Channel.Debug:
                    if (!PropertyManager.GetBool("chat_log_debug").Item)
                        return;
                    break;
                case Channel.Fellow:
                case Channel.FellowBroadcast:
                    if (!PropertyManager.GetBool("chat_log_fellow").Item)
                        return;
                    break;
                case Channel.Help:
                    if (!PropertyManager.GetBool("chat_log_help").Item)
                        return;
                    break;
                case Channel.Olthoi:
                    if (!PropertyManager.GetBool("chat_log_olthoi").Item)
                        return;
                    break;
                case Channel.QA1:
                case Channel.QA2:
                    if (!PropertyManager.GetBool("chat_log_qa").Item)
                        return;
                    break;
                case Channel.Sentinel:
                    if (!PropertyManager.GetBool("chat_log_sentinel").Item)
                        return;
                    break;

                case Channel.SocietyCelHanBroadcast:
                case Channel.SocietyEldWebBroadcast:
                case Channel.SocietyRadBloBroadcast:
                    if (!PropertyManager.GetBool("chat_log_society").Item)
                        return;
                    break;

                case Channel.AllegianceBroadcast:
                case Channel.CoVassals:
                case Channel.Monarch:
                case Channel.Patron:
                case Channel.Vassals:
                    if (!PropertyManager.GetBool("chat_log_allegiance").Item)
                        return;
                    break;

                case Channel.AlArqas:
                case Channel.Holtburg:
                case Channel.Lytelthorpe:
                case Channel.Nanto:
                case Channel.Rithwic:
                case Channel.Samsur:
                case Channel.Shoushi:
                case Channel.Yanshi:
                case Channel.Yaraq:
                    if (!PropertyManager.GetBool("chat_log_townchans").Item)
                        return;
                    break;

                default:
                    return;
            }

            if (channel != Channel.AllBroadcast)
                log.Info($"[CHAT][{channel.ToString().ToUpper()}] {(sender != null ? sender.Name : "[SYSTEM]")} says on the {channel} channel, \"{message}\"");
            else
                log.Info($"[CHAT][GLOBAL] {(sender != null ? sender.Name : "[SYSTEM]")} issued a world broadcast, \"{message}\"");
        }

        public static void BroadcastToChannelFromConsole(Channel channel, string message)
        {
            foreach (var player in GetAllOnline().Where(p => (p.ChannelsActive ?? 0).HasFlag(channel)))
                player.Session.Network.EnqueueSend(new GameEventChannelBroadcast(player.Session, channel, "CONSOLE", message));

            LogBroadcastChat(channel, null, message);
        }

        public static void BroadcastToChannelFromEmote(Channel channel, string message)
        {
            foreach (var player in GetAllOnline().Where(p => (p.ChannelsActive ?? 0).HasFlag(channel)))
                player.Session.Network.EnqueueSend(new GameEventChannelBroadcast(player.Session, channel, "EMOTE", message));
        }

        /// <summary>
        /// Gag length applied when @gag is given no duration: five minutes, matching retail.
        /// </summary>
        public const double DefaultGagDurationSeconds = 300;

        /// <summary>
        /// GagDuration value that marks a permanent gag. Player.GagsTick never counts a gag at or above this
        /// value down, so it only ends via @ungag. Finite rather than double.MaxValue because the value
        /// round-trips through a MySQL DOUBLE column in biota_properties_float.
        /// </summary>
        public const double PermanentGagDurationSeconds = 1e12;

        public static bool GagPlayer(Player issuer, string playerName, double durationSeconds = DefaultGagDurationSeconds)
        {
            var player = FindByName(playerName);

            if (player == null)
                return false;

            player.SetProperty(ACE.Entity.Enum.Properties.PropertyBool.IsGagged, true);
            player.SetProperty(ACE.Entity.Enum.Properties.PropertyFloat.GagTimestamp, Common.Time.GetUnixTime());
            player.SetProperty(ACE.Entity.Enum.Properties.PropertyFloat.GagDuration, durationSeconds);

            player.SaveBiotaToDatabase();

            BroadcastToAuditChannel(issuer, $"{issuer.Name} has gagged {player.Name} {DescribeGagDuration(durationSeconds)}.");

            return true;
        }

        /// <summary>
        /// Renders a gag length for chat: "permanently", "for 5 minutes", "for 2 hours", "for 1.5 days".
        /// </summary>
        public static string DescribeGagDuration(double durationSeconds)
        {
            if (durationSeconds >= PermanentGagDurationSeconds)
                return "permanently";

            double amount;
            string unit;

            if (durationSeconds >= 86400)
            {
                amount = Math.Round(durationSeconds / 86400, 1);
                unit = "day";
            }
            else if (durationSeconds >= 3600)
            {
                amount = Math.Round(durationSeconds / 3600, 1);
                unit = "hour";
            }
            else
            {
                amount = Math.Round(durationSeconds / 60, 1);
                unit = "minute";
            }

            return $"for {amount:0.#} {unit}{(amount == 1 ? "" : "s")}";
        }

        public static bool UnGagPlayer(Player issuer, string playerName)
        {
            var player = FindByName(playerName);

            if (player == null)
                return false;

            player.RemoveProperty(ACE.Entity.Enum.Properties.PropertyBool.IsGagged);
            player.RemoveProperty(ACE.Entity.Enum.Properties.PropertyFloat.GagTimestamp);
            player.RemoveProperty(ACE.Entity.Enum.Properties.PropertyFloat.GagDuration);

            player.SaveBiotaToDatabase();

            BroadcastToAuditChannel(issuer, $"{issuer.Name} has ungagged {player.Name}.");

            return true;
        }

        public static void BootAllPlayers()
        {
            foreach (var player in GetAllOnline().Where(p => p.Session.AccessLevel < AccessLevel.Advocate))
                player.Session.Terminate(SessionTerminationReason.WorldClosed, new GameMessageBootAccount(" because the world is now closed"), null, "The world is now closed");
        }

        public static void UpdatePKStatusForAllPlayers(string worldType, bool enabled)
        {
            switch (worldType)
            {
                case "pk_server":
                    if (enabled)
                    {
                        foreach (var player in GetAllOnline())
                            player.SetPlayerKillerStatus(PlayerKillerStatus.PK, true);

                        foreach (var player in GetAllOffline())
                        {
                            player.SetProperty(PropertyInt.PlayerKillerStatus, (int)PlayerKillerStatus.NPK);
                            player.SetProperty(PropertyFloat.MinimumTimeSincePk, 0);
                        }

                        var msg = $"This world has been changed to a Player Killer world. All players will become Player Killers in {PropertyManager.GetDouble("pk_respite_timer").Item} seconds.";
                        BroadcastToAll(new GameMessageSystemChat(msg, ChatMessageType.WorldBroadcast));
                        LogBroadcastChat(Channel.AllBroadcast, null, msg);
                    }
                    else
                    {
                        foreach (var player in GetAllOnline())
                            player.SetPlayerKillerStatus(PlayerKillerStatus.NPK, true);

                        foreach (var player in GetAllOffline())
                        {
                            player.SetProperty(PropertyInt.PlayerKillerStatus, (int)PlayerKillerStatus.NPK);
                            player.SetProperty(PropertyFloat.MinimumTimeSincePk, 0);
                        }

                        var msg = "This world has been changed to a Non Player Killer world. All players are now Non-Player Killers.";
                        BroadcastToAll(new GameMessageSystemChat(msg, ChatMessageType.WorldBroadcast));
                        LogBroadcastChat(Channel.AllBroadcast, null, msg);
                    }
                    break;
                case "pkl_server":
                    if (PropertyManager.GetBool("pk_server").Item)
                        return;
                    if (enabled)
                    {
                        foreach (var player in GetAllOnline())
                            player.SetPlayerKillerStatus(PlayerKillerStatus.PKLite, true);

                        foreach (var player in GetAllOffline())
                        {
                            player.SetProperty(PropertyInt.PlayerKillerStatus, (int)PlayerKillerStatus.NPK);
                            player.SetProperty(PropertyFloat.MinimumTimeSincePk, 0);
                        }

                        var msg = $"This world has been changed to a Player Killer Lite world. All players will become Player Killer Lites in {PropertyManager.GetDouble("pk_respite_timer").Item} seconds.";
                        BroadcastToAll(new GameMessageSystemChat(msg, ChatMessageType.WorldBroadcast));
                        LogBroadcastChat(Channel.AllBroadcast, null, msg);
                    }
                    else
                    {
                        foreach (var player in GetAllOnline())
                            player.SetPlayerKillerStatus(PlayerKillerStatus.NPK, true);

                        foreach (var player in GetAllOffline())
                        {
                            player.SetProperty(PropertyInt.PlayerKillerStatus, (int)PlayerKillerStatus.NPK);
                            player.SetProperty(PropertyFloat.MinimumTimeSincePk, 0);
                        }

                        var msg = "This world has been changed to a Non Player Killer world. All players are now Non-Player Killers.";
                        BroadcastToAll(new GameMessageSystemChat(msg, ChatMessageType.WorldBroadcast));
                        LogBroadcastChat(Channel.AllBroadcast, null, msg);
                    }
                    break;
            }
        }

        public static bool IsAccountAtMaxCharacterSlots(string accountName)
        {
            var slotsAvailable = (int)PropertyManager.GetLong("max_chars_per_account").Item;
            var onlinePlayersTotal = 0;
            var offlinePlayersTotal = 0;

            playersLock.EnterReadLock();
            try
            {
                onlinePlayersTotal = onlinePlayers.Count(a => a.Value.Account.AccountName.Equals(accountName, StringComparison.OrdinalIgnoreCase));
                offlinePlayersTotal = offlinePlayers.Count(a => a.Value.Account.AccountName.Equals(accountName, StringComparison.OrdinalIgnoreCase));
            }
            finally
            {
                playersLock.ExitReadLock();
            }

            return (onlinePlayersTotal + offlinePlayersTotal) >= slotsAvailable;
        }
    }
}
