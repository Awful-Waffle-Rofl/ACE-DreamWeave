using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;

using log4net;

using ACE.Common;
using ACE.Common.Extensions;
using ACE.Database;
using ACE.DatLoader;
using ACE.Entity;
using ACE.Entity.Enum;
using ACE.Entity.Models;
using ACE.Server.Factories;
using ACE.Server.Managers;
using ACE.Server.Network.Enum;
using ACE.Server.Network.GameMessages;
using ACE.Server.Network.GameMessages.Messages;

namespace ACE.Server.Network.Handlers
{
    public static class CharacterHandler
    {
        private static readonly ILog log = LogManager.GetLogger(System.Reflection.MethodBase.GetCurrentMethod().DeclaringType);

        [GameMessage(GameMessageOpcode.CharacterCreate, SessionState.AuthConnected)]
        public static void CharacterCreate(ClientMessage message, Session session)
        {
            string clientString = message.Payload.ReadString16L();

            if (clientString != session.Account)
                return;

            if (ServerManager.ShutdownInProgress)
            {
                session.SendCharacterError(CharacterError.LogonServerFull);
                return;
            }

            if (WorldManager.WorldStatus == WorldManager.WorldStatusState.Open || session.AccessLevel > AccessLevel.Player)
                CharacterCreateEx(message, session);
            else
                session.SendCharacterError(CharacterError.LogonServerFull);
        }

        private static void CharacterCreateEx(ClientMessage message, Session session)
        {
            var characterCreateInfo = new CharacterCreateInfo();
            characterCreateInfo.Unpack(message.Payload);
            
            if (PropertyManager.GetBool("taboo_table").Item && DatManager.PortalDat.TabooTable.ContainsBadWord(characterCreateInfo.Name.ToLowerInvariant()))
            {
                SendCharacterCreateResponse(session, CharacterGenerationVerificationResponse.NameBanned);
                return;
            }

            if (PropertyManager.GetBool("creature_name_check").Item && DatabaseManager.World.IsCreatureNameInWorldDatabase(characterCreateInfo.Name))
            {
                SendCharacterCreateResponse(session, CharacterGenerationVerificationResponse.NameBanned);
                return;
            }

            DatabaseManager.Shard.IsCharacterNameAvailable(characterCreateInfo.Name, isAvailable =>
            {
                if (!isAvailable)
                {
                    SendCharacterCreateResponse(session, CharacterGenerationVerificationResponse.NameInUse);
                    return;
                }
            });

            if ((characterCreateInfo.Heritage == HeritageGroup.Olthoi || characterCreateInfo.Heritage == HeritageGroup.OlthoiAcid) && PropertyManager.GetBool("olthoi_play_disabled").Item)
            {
                SendCharacterCreateResponse(session, CharacterGenerationVerificationResponse.Pending);
                return;
            }

            Weenie weenie;
            if (ConfigManager.Config.Server.Accounts.OverrideCharacterPermissions)
            {
                if (session.AccessLevel >= AccessLevel.Developer && session.AccessLevel <= AccessLevel.Admin)
                    weenie = DatabaseManager.World.GetCachedWeenie("admin");
                else if (session.AccessLevel >= AccessLevel.Sentinel && session.AccessLevel <= AccessLevel.Envoy)
                    weenie = DatabaseManager.World.GetCachedWeenie("sentinel");
                else
                    weenie = DatabaseManager.World.GetCachedWeenie("human");

                if (characterCreateInfo.Heritage == HeritageGroup.Olthoi && weenie.WeenieType == WeenieType.Admin)
                    weenie = DatabaseManager.World.GetCachedWeenie("olthoiadmin");

                if (characterCreateInfo.Heritage == HeritageGroup.OlthoiAcid && weenie.WeenieType == WeenieType.Admin)
                    weenie = DatabaseManager.World.GetCachedWeenie("olthoiacidadmin");
            }
            else
                weenie = DatabaseManager.World.GetCachedWeenie("human");

            if (characterCreateInfo.Heritage == HeritageGroup.Olthoi && weenie.WeenieType == WeenieType.Creature)
                weenie = DatabaseManager.World.GetCachedWeenie("olthoiplayer");

            if (characterCreateInfo.Heritage == HeritageGroup.OlthoiAcid && weenie.WeenieType == WeenieType.Creature)
                weenie = DatabaseManager.World.GetCachedWeenie("olthoiacidplayer");

            if (characterCreateInfo.IsSentinel && session.AccessLevel >= AccessLevel.Sentinel)
                weenie = DatabaseManager.World.GetCachedWeenie("sentinel");

            if (characterCreateInfo.IsAdmin && session.AccessLevel >= AccessLevel.Developer)
                weenie = DatabaseManager.World.GetCachedWeenie("admin");

            if (weenie == null)
                weenie = DatabaseManager.World.GetCachedWeenie("human"); // Default catch-all

            if (weenie == null) // If it is STILL null after the above catchall, the database is missing critical data and cannot continue with character creation.
            {
                SendCharacterCreateResponse(session, CharacterGenerationVerificationResponse.DatabaseDown);
                log.Error("Database does not contain the weenie for human (1). Characters cannot be created until the missing weenie is restored.");
                return;
            }

            var guid = GuidManager.NewPlayerGuid();

            var weenieType = weenie.WeenieType;

            // If Database didn't have Sentinel/Admin weenies, alter the weenietype coming in.
            if (ConfigManager.Config.Server.Accounts.OverrideCharacterPermissions)
            {
                if (session.AccessLevel >= AccessLevel.Developer && session.AccessLevel <= AccessLevel.Admin && weenieType != WeenieType.Admin)
                    weenieType = WeenieType.Admin;
                else if (session.AccessLevel >= AccessLevel.Sentinel && session.AccessLevel <= AccessLevel.Envoy && weenieType != WeenieType.Sentinel)
                    weenieType = WeenieType.Sentinel;
            }


            var result = PlayerFactory.Create(characterCreateInfo, weenie, guid, session.AccountId, weenieType, out var player);

            if (result != PlayerFactory.CreateResult.Success || player == null)
            {
                if (result == PlayerFactory.CreateResult.ClientServerSkillsMismatch)
                {
                    session.Terminate(SessionTerminationReason.ClientVersionIncorrect, new GameMessageBootAccount(" because your client is not the correct version for this server. Please visit http://play.emu.ac/ to update to latest client"));
                    return;
                }

                SendCharacterCreateResponse(session, CharacterGenerationVerificationResponse.Corrupt);
                return;
            }

            DatabaseManager.Shard.IsCharacterNameAvailable(characterCreateInfo.Name, isAvailable =>
            {
                if (!isAvailable)
                {
                    SendCharacterCreateResponse(session, CharacterGenerationVerificationResponse.NameInUse);
                    return;
                }

                var possessions = player.GetAllPossessions();
                var possessedBiotas = new Collection<(Biota biota, ReaderWriterLockSlim rwLock)>();
                foreach (var possession in possessions)
                    possessedBiotas.Add((possession.Biota, possession.BiotaDatabaseLock));

                // We must await here -- 
                DatabaseManager.Shard.AddCharacterInParallel(player.Biota, player.BiotaDatabaseLock, possessedBiotas, player.Character, player.CharacterDatabaseLock, saveSuccess =>
                {
                    if (!saveSuccess)
                    {
                        SendCharacterCreateResponse(session, CharacterGenerationVerificationResponse.DatabaseDown);
                        return;
                    }

                    PlayerManager.AddOfflinePlayer(player);
                    session.Characters.Add(player.Character);

                    SendCharacterCreateResponse(session, CharacterGenerationVerificationResponse.Ok, player.Guid, characterCreateInfo.Name);
                });
            });
        }

        private static void SendCharacterCreateResponse(Session session, CharacterGenerationVerificationResponse response, ObjectGuid guid = default(ObjectGuid), string charName = "")
        {
            session.Network.EnqueueSend(new GameMessageCharacterCreateResponse(response, guid, charName));
        }


        [GameMessage(GameMessageOpcode.CharacterEnterWorldRequest, SessionState.AuthConnected)]
        public static void CharacterEnterWorldRequest(ClientMessage message, Session session)
        {
            if (ServerManager.ShutdownInProgress)
            {
                session.SendCharacterError(CharacterError.LogonServerFull);
                return;
            }

            if (WorldManager.WorldStatus == WorldManager.WorldStatusState.Open || session.AccessLevel > AccessLevel.Player)
                session.Network.EnqueueSend(new GameMessageCharacterEnterWorldServerReady());
            else
                session.SendCharacterError(CharacterError.LogonServerFull);
        }

        [GameMessage(GameMessageOpcode.CharacterEnterWorld, SessionState.AuthConnected)]
        public static void CharacterEnterWorld(ClientMessage message, Session session)
        {
            var guid = message.Payload.ReadUInt32();

            string clientString = message.Payload.ReadString16L();

            if (ServerManager.ShutdownInProgress)
            {
                session.SendCharacterError(CharacterError.LogonServerFull);
                return;
            }

            if (clientString != session.Account)
            {
                session.SendCharacterError(CharacterError.EnterGameCharacterNotOwned);
                return;
            }

            var character = session.Characters.SingleOrDefault(c => c.Id == guid);
            if (character == null)
            {
                session.SendCharacterError(CharacterError.EnterGameCharacterNotOwned);
                return;
            }

            if (character.IsDeleted || character.DeleteTime > 0)
            {
                session.SendCharacterError(CharacterError.EnterGameCharacterNotOwned);
                return;
            }

            if (PlayerManager.GetOnlinePlayer(guid) != null)
            {
                // If this happens, it could be that the previous session for this Player terminated in a way that didn't transfer the player to offline via PlayerManager properly.
                session.SendCharacterError(CharacterError.EnterGameCharacterInWorld);
                return;
            }

            var offlinePlayer = PlayerManager.GetOfflinePlayer(guid);

            if (offlinePlayer == null)
            {
                // This would likely only happen if the account tried to log in a character that didn't exist.
                session.SendCharacterError(CharacterError.EnterGameGeneric);
                return;
            }

            if (offlinePlayer.IsDeleted || offlinePlayer.IsPendingDeletion)
            {
                session.SendCharacterError(CharacterError.EnterGameCharacterNotOwned);
                return;
            }

            if ((offlinePlayer.Heritage == (int)HeritageGroup.Olthoi || offlinePlayer.Heritage == (int)HeritageGroup.OlthoiAcid) && PropertyManager.GetBool("olthoi_play_disabled").Item)
            {
                session.SendCharacterError(CharacterError.EnterGameCouldntPlaceCharacter);
                return;
            }

            // WaffleACE: IP active-player limit. This is the last gate before the character actually enters
            // the world, and it runs after every existing admission check so a character rejected for any
            // other reason never counts against the IP's budget. The landblock read below is only a
            // PREDICTION of where the character will end up - DoPlayerEnterWorld can still relocate it
            // (no-log landblock handling, first-login routing, dead-instance relocation, spawn-failure
            // fallback) - which is why IpLimitManager.Tick re-checks everyone periodically.
            if (IpLimitManager.Enabled && !IpLimitManager.IsExempt(session))
            {
                if (!HandleIpLimitOnEnterWorld(session, character, offlinePlayer))
                    return;
            }

            session.InitSessionForWorldLogin();

            session.State = SessionState.WorldConnected;

            WorldManager.PlayerEnterWorld(session, character);
        }

        /// <summary>
        /// WaffleACE: IP active-player limit login gate. Returns TRUE if the character may enter the world,
        /// FALSE if it was refused (in which case the client has already been told). Any eviction of an
        /// already-in-world character is started here and is deliberately NOT waited on - the incoming
        /// character is admitted immediately, and the periodic sweep (IpLimitManager.Tick) is what makes the
        /// end state correct if anything about that races.
        ///
        /// Callers must have already checked IpLimitManager.Enabled and IpLimitManager.IsExempt.
        /// </summary>
        private static bool HandleIpLimitOnEnterWorld(Session session, ACE.Database.Models.Shard.Character character, ACE.Server.Entity.OfflinePlayer offlinePlayer)
        {
            // The character's PERSISTED landblock, read straight off the offline biota - the same idiom
            // Player.HandleNoLogLandblock uses (Player_Location.cs). If no persisted location can be read we
            // treat the character as NOT confined, which is the conservative direction: it then counts
            // against the stricter "outside the mule landblocks" cap rather than slipping past it.
            var confined = false;

            var persistedLocation = offlinePlayer.Biota.GetProperty(ACE.Entity.Enum.Properties.PositionType.Location, offlinePlayer.BiotaDatabaseLock);

            if (persistedLocation != null)
                confined = IpLimitManager.GetMuleLandblocks().Contains((ushort)(persistedLocation.ObjCellId >> 16));

            // LoginTimestamp is HARDCODED to double.MaxValue here and must NEVER be read from the character's
            // stored PropertyFloat.LoginTimestamp. That property is only written when a character actually
            // enters the world (Player_Networking.cs, PlayerEnterWorld), so for an offline character it still
            // holds the PREVIOUS session's value. A mule that last logged in days ago would therefore carry an
            // OLDER timestamp than the character already in-world, and would win a level tie that it has to
            // lose. double.MaxValue makes the incoming character unconditionally the most-recently-logged-in
            // one, so on a level tie it is the character that gets refused and the character that has been on
            // longest survives. Do not "clean this up" into a real timestamp - that silently inverts the rule.
            var incoming = new IpLimitCandidate(0, offlinePlayer.Level ?? 0, confined, double.MaxValue);

            // The excludeAccountId argument is load-bearing: a player reconnecting briefly leaves their own
            // previous session still counted as online, and without this exclusion they would evict their own
            // character. Always pass the real account id, never 0.
            var residents = IpLimitManager.GetResidents(session.EndPointC2S?.Address, session.AccountId);

            // Clamped, not bare-cast: see IpLimitManager.ClampCap for why an unchecked long -> int narrowing
            // of these two is a server-wide lockout waiting on one admin typo.
            var maxFree = IpLimitManager.ClampCap(PropertyManager.GetLong("ip_limit_max_free").Item);
            var maxConfined = IpLimitManager.ClampCap(PropertyManager.GetLong("ip_limit_max_confined").Item);

            var decision = IpLimitManager.Evaluate(incoming, residents, maxFree, maxConfined);

            if (decision.Action == IpLimitAction.Refuse)
            {
                // EnterGameCharacterInWorld renders client-side as "One of your characters is still in the
                // world". That wording is imprecise for a refusal caused by a DIFFERENT account on the same
                // address, but the string lives in the client dat and cannot be changed server-side, and it is
                // the closest existing CharacterError to what actually happened.
                session.SendCharacterError(CharacterError.EnterGameCharacterInWorld);

                PlayerManager.BroadcastToAuditChannel(null, $"IP limit: refused world entry for {character.Name} (account {session.Account}, address {session.EndPointC2S?.Address}) - the address is already at its in-world character limit ({maxFree} outside the mule landblocks, {maxFree + maxConfined} total).");

                return false;
            }

            if (decision.Action == IpLimitAction.AdmitAfterEvicting)
            {
                foreach (var evictGuid in decision.Evict)
                {
                    var evictee = PlayerManager.GetOnlinePlayer(evictGuid);

                    if (evictee?.Session == null)
                        continue;

                    evictee.Session.Network.EnqueueSend(new GameMessageSystemChat("You have been logged out because a higher level character from your network address has entered the world.", ChatMessageType.Broadcast));

                    PlayerManager.BroadcastToAuditChannel(null, $"IP limit: logging off {evictee.Name} (level {evictee.Level ?? 0}, account {evictee.Session.Account}, address {session.EndPointC2S?.Address}) because {character.Name} (level {offlinePlayer.Level ?? 0}, account {session.Account}) entered the world from the same address.");

                    // forceImmediate so a PK logout timer cannot stall the eviction. Session.LogOffPlayer, not
                    // Player.ForceLogoff - the latter is documented as system use only.
                    evictee.Session.LogOffPlayer(true);
                }
            }

            return true;
        }


        [GameMessage(GameMessageOpcode.CharacterLogOff, SessionState.WorldConnected)]
        public static void CharacterLogOff(ClientMessage message, Session session)
        {
            session.LogOffPlayer();
        }


        [GameMessage(GameMessageOpcode.CharacterDelete, SessionState.AuthConnected)]
        public static void CharacterDelete(ClientMessage message, Session session)
        {
            string clientString = message.Payload.ReadString16L();
            uint characterSlot = message.Payload.ReadUInt32();

            if (ServerManager.ShutdownInProgress)
            {
                session.SendCharacterError(CharacterError.Delete);
                return;
            }

            if (WorldManager.WorldStatus == WorldManager.WorldStatusState.Closed && session.AccessLevel < AccessLevel.Advocate)
            {
                session.SendCharacterError(CharacterError.LogonServerFull);
                return;
            }

            if (clientString != session.Account)
            {
                session.SendCharacterError(CharacterError.Delete);
                return;
            }

            var character = session.Characters[(int)characterSlot];
            if (character == null)
            {
                session.SendCharacterError(CharacterError.Delete);
                return;
            }

            var offlinePlayer = PlayerManager.GetOfflinePlayer(session.Characters[(int)characterSlot].Id);

            if (offlinePlayer == null || offlinePlayer.IsDeleted || offlinePlayer.IsPendingDeletion)
            {
                session.SendCharacterError(CharacterError.Delete);
                return;
            }

            session.Network.EnqueueSend(new GameMessageCharacterDelete());

            var charRestoreTime = PropertyManager.GetLong("char_delete_time", 3600).Item;
            character.DeleteTime = (ulong)(Time.GetUnixTime() + charRestoreTime);
            character.IsDeleted = false;

            DatabaseManager.Shard.SaveCharacter(character, new ReaderWriterLockSlim(), result =>
            {
                if (result)
                {
                    session.Network.EnqueueSend(new GameMessageCharacterList(session.Characters, session));

                    PlayerManager.HandlePlayerDelete(character.Id);
                }
                else
                    session.SendCharacterError(CharacterError.Delete);
            });
        }

        [GameMessage(GameMessageOpcode.CharacterRestore, SessionState.AuthConnected)]
        public static void CharacterRestore(ClientMessage message, Session session)
        {
            var guid = message.Payload.ReadUInt32();

            if (ServerManager.ShutdownInProgress)
            {
                session.SendCharacterError(CharacterError.EnterGameCouldntPlaceCharacter);
                return;
            }

            if (WorldManager.WorldStatus == WorldManager.WorldStatusState.Closed && session.AccessLevel < AccessLevel.Advocate)
            {
                session.SendCharacterError(CharacterError.LogonServerFull);
                return;
            }

            var character = session.Characters.SingleOrDefault(c => c.Id == guid);
            if (character == null)
                return;

            if (Time.GetUnixTime() > character.DeleteTime || character.IsDeleted)
            {
                session.SendCharacterError(CharacterError.EnterGameCharacterNotOwned);
                return;
            }

            DatabaseManager.Shard.IsCharacterNameAvailable(character.Name, isAvailable =>
            {
                if (!isAvailable)
                {
                    SendCharacterCreateResponse(session, CharacterGenerationVerificationResponse.NameInUse);
                }
                else
                {
                    character.DeleteTime = 0;
                    character.IsDeleted = false;

                    DatabaseManager.Shard.SaveCharacter(character, new ReaderWriterLockSlim(), result =>
                    {
                        var name = character.Name;

                        if (ConfigManager.Config.Server.Accounts.OverrideCharacterPermissions && session.AccessLevel > AccessLevel.Advocate)
                            name = "+" + name;
                        else if (!ConfigManager.Config.Server.Accounts.OverrideCharacterPermissions && character.IsPlussed)
                            name = "+" + name;

                        if (result)
                            session.Network.EnqueueSend(new GameMessageCharacterRestore(guid, name, 0u));
                        else
                            SendCharacterCreateResponse(session, CharacterGenerationVerificationResponse.Corrupt);
                    });
                }
            });
        }
    }
}
