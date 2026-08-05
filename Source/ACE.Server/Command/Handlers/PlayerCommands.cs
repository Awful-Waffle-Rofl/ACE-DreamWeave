using System;
using System.Collections.Generic;
using System.Linq;

using log4net;

using ACE.Common;
using ACE.Database;
using ACE.Entity;
using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Server.Entity;
using ACE.Server.Entity.Actions;
using ACE.Server.Managers;
using ACE.Server.Network;
using ACE.Server.Network.GameEvent.Events;
using ACE.Server.Network.GameMessages.Messages;
using ACE.Server.WorldObjects;


namespace ACE.Server.Command.Handlers
{
    public static class PlayerCommands
    {
        private static readonly ILog log = LogManager.GetLogger(System.Reflection.MethodBase.GetCurrentMethod().DeclaringType);

        // enl
        [CommandHandler("enl", AccessLevel.Player, CommandHandlerFlag.RequiresWorld, 0,
            "Attain Enlightenment: reset your level for a permanent stat floor and a higher level cap.",
            "Requires your personal maximum level and enough luminance. A confirmation dialog lists exactly what resets and what is kept.")]
        public static void HandleEnlightenment(Session session, params string[] parameters)
        {
            Enlightenment.HandleEnlightenmentRequest(session.Player, false);
        }

        // pop
        [CommandHandler("pop", AccessLevel.Player, CommandHandlerFlag.None, 0,
            "Show current world population",
            "")]
        public static void HandlePop(Session session, params string[] parameters)
        {
            CommandHandlerHelper.WriteOutputInfo(session, $"Current world population: {PlayerManager.GetOnlineCount():N0}", ChatMessageType.Broadcast);
        }

        // quest info (uses GDLe formatting to match plugin expectations)
        [CommandHandler("myquests", AccessLevel.Player, CommandHandlerFlag.RequiresWorld, "Shows your quest log")]
        public static void HandleQuests(Session session, params string[] parameters)
        {
            if (!PropertyManager.GetBool("quest_info_enabled").Item)
            {
                session.Network.EnqueueSend(new GameMessageSystemChat("The command \"myquests\" is not currently enabled on this server.", ChatMessageType.Broadcast));
                return;
            }

            var quests = session.Player.QuestManager.GetQuests();

            if (quests.Count == 0)
            {
                session.Network.EnqueueSend(new GameMessageSystemChat("Quest list is empty.", ChatMessageType.Broadcast));
                return;
            }

            foreach (var playerQuest in quests)
            {
                var text = "";
                var questName = QuestManager.GetQuestName(playerQuest.QuestName);
                var quest = DatabaseManager.World.GetCachedQuest(questName);
                if (quest == null)
                {
                    //Console.WriteLine($"Couldn't find quest {playerQuest.QuestName}");
                    continue;
                }

                var minDelta = quest.MinDelta;
                if (QuestManager.CanScaleQuestMinDelta(quest))
                    minDelta = (uint)(quest.MinDelta * PropertyManager.GetDouble("quest_mindelta_rate").Item);

                text += $"{playerQuest.QuestName.ToLower()} - {playerQuest.NumTimesCompleted} solves ({playerQuest.LastTimeCompleted})";
                text += $"\"{quest.Message}\" {quest.MaxSolves} {minDelta}";

                session.Network.EnqueueSend(new GameMessageSystemChat(text, ChatMessageType.Broadcast));
            }
        }

        // quest stamp progress: this character's own count, the account-wide pool the Quest Stamp Registrar
        // rewards against, and which reward tiers this character has already claimed
        [CommandHandler("quests", AccessLevel.Player, CommandHandlerFlag.RequiresWorld, 0,
            "Shows your quest stamp progress",
            "")]
        public static void HandleQuestStamps(Session session, params string[] parameters)
        {
            if (!PropertyManager.GetBool("quest_stamps_enabled").Item)
            {
                session.Network.EnqueueSend(new GameMessageSystemChat("Quest stamps are not currently enabled on this server.", ChatMessageType.Broadcast));
                return;
            }

            var own = session.Player.QuestStampCount;
            var accountTotal = session.Player.AccountQuestStampCount;

            session.Network.EnqueueSend(new GameMessageSystemChat($"Quest stamps: {own} (account total: {accountTotal})", ChatMessageType.Broadcast));

            var next = QuestStamps.NextThreshold(accountTotal);

            if (next != null)
                session.Network.EnqueueSend(new GameMessageSystemChat($"Next reward at {next.Value} ({next.Value - accountTotal} to go).", ChatMessageType.Broadcast));
            else
                session.Network.EnqueueSend(new GameMessageSystemChat("All reward tiers reached.", ChatMessageType.Broadcast));

            var tier1 = session.Player.QuestManager.HasQuest("QuestStampTier1") ? "yes" : "no";
            var tier2 = session.Player.QuestManager.HasQuest("QuestStampTier2") ? "yes" : "no";
            var tier3 = session.Player.QuestManager.HasQuest("QuestStampTier3") ? "yes" : "no";
            var tier4 = session.Player.QuestManager.HasQuest("QuestStampTier4") ? "yes" : "no";

            session.Network.EnqueueSend(new GameMessageSystemChat($"Reward tiers claimed: Tier1 {tier1}, Tier2 {tier2}, Tier3 {tier3}, Tier4 {tier4}", ChatMessageType.Broadcast));
        }

        [CommandHandler("offlinebonus", AccessLevel.Player, CommandHandlerFlag.RequiresWorld, "Shows your remaining offline experience bonus time")]
        public static void HandleOfflineBonus(Session session, params string[] parameters)
        {
            session.Player.ShowOfflineExperienceBonusStatus();
        }

        [CommandHandler("altbonus", AccessLevel.Player, CommandHandlerFlag.RequiresWorld, "Shows whether this character is receiving the alt character catch-up experience bonus")]
        public static void HandleAltBonus(Session session, params string[] parameters)
        {
            session.Player.ShowAltCharacterBonusStatus();
        }

        [CommandHandler("bonus", AccessLevel.Player, CommandHandlerFlag.RequiresWorld, "Lists all of your experience bonus sources (gear, augmentation, alternate character, offline) and the combined total")]
        public static void HandleBonus(Session session, params string[] parameters)
        {
            session.Player.ShowExperienceBonusSummary();
        }

        /// <summary>
        /// For characters/accounts who currently own multiple houses, used to select which house they want to keep
        /// </summary>
        [CommandHandler("house-select", AccessLevel.Player, CommandHandlerFlag.RequiresWorld, 1, "For characters/accounts who currently own multiple houses, used to select which house they want to keep")]
        public static void HandleHouseSelect(Session session, params string[] parameters)
        {
            HandleHouseSelect(session, false, parameters);
        }

        public static void HandleHouseSelect(Session session, bool confirmed, params string[] parameters)
        {
            if (!int.TryParse(parameters[0], out var houseIdx))
                return;

            // ensure current multihouse owner
            if (!session.Player.IsMultiHouseOwner(false))
            {
                log.Warn($"{session.Player.Name} tried to /house-select {houseIdx}, but they are not currently a multi-house owner!");
                return;
            }

            // get house info for this index
            var multihouses = session.Player.GetMultiHouses();

            if (houseIdx < 1 || houseIdx > multihouses.Count)
            {
                session.Network.EnqueueSend(new GameMessageSystemChat($"Please enter a number between 1 and {multihouses.Count}.", ChatMessageType.Broadcast));
                return;
            }

            var keepHouse = multihouses[houseIdx - 1];

            // show confirmation popup
            if (!confirmed)
            {
                var houseType = $"{keepHouse.HouseType}".ToLower();
                var loc = HouseManager.GetCoords(keepHouse.SlumLord.Location);

                var msg = $"Are you sure you want to keep the {houseType} at\n{loc}?";
                if (!session.Player.ConfirmationManager.EnqueueSend(new Confirmation_Custom(session.Player.Guid, () => HandleHouseSelect(session, true, parameters)), msg))
                    session.Player.SendWeenieError(WeenieError.ConfirmationInProgress);
                return;
            }

            // house to keep confirmed, abandon the other houses
            var abandonHouses = new List<House>(multihouses);
            abandonHouses.RemoveAt(houseIdx - 1);

            foreach (var abandonHouse in abandonHouses)
            {
                var house = session.Player.GetHouse(abandonHouse.Guid.Full);

                HouseManager.HandleEviction(house, house.HouseOwner ?? 0, true);
            }

            // set player properties for house to keep
            var player = PlayerManager.FindByGuid(keepHouse.HouseOwner ?? 0, out bool isOnline);
            if (player == null)
            {
                log.Error($"{session.Player.Name}.HandleHouseSelect({houseIdx}) - couldn't find HouseOwner {keepHouse.HouseOwner} for {keepHouse.Name} ({keepHouse.Guid})");
                return;
            }

            player.HouseId = keepHouse.HouseId;
            player.HouseInstance = keepHouse.Guid.Full;

            player.SaveBiotaToDatabase();

            // update house panel for current player
            var actionChain = new ActionChain();
            actionChain.AddDelaySeconds(3.0f);  // wait for slumlord inventory biotas above to save
            actionChain.AddAction(session.Player, session.Player.HandleActionQueryHouse);
            actionChain.EnqueueChain();

            Console.WriteLine("OK");
        }

        [CommandHandler("debugcast", AccessLevel.Player, CommandHandlerFlag.RequiresWorld, "Shows debug information about the current magic casting state")]
        public static void HandleDebugCast(Session session, params string[] parameters)
        {
            var physicsObj = session.Player.PhysicsObj;

            var pendingActions = physicsObj.MovementManager.MoveToManager.PendingActions;
            var currAnim = physicsObj.PartArray.Sequence.CurrAnim;

            session.Network.EnqueueSend(new GameMessageSystemChat(session.Player.MagicState.ToString(), ChatMessageType.Broadcast));
            session.Network.EnqueueSend(new GameMessageSystemChat($"IsMovingOrAnimating: {physicsObj.IsMovingOrAnimating}", ChatMessageType.Broadcast));
            session.Network.EnqueueSend(new GameMessageSystemChat($"PendingActions: {pendingActions.Count}", ChatMessageType.Broadcast));
            session.Network.EnqueueSend(new GameMessageSystemChat($"CurrAnim: {currAnim?.Value.Anim.ID:X8}", ChatMessageType.Broadcast));
        }

        [CommandHandler("fixcast", AccessLevel.Player, CommandHandlerFlag.RequiresWorld, "Fixes magic casting if locked up for an extended time")]
        public static void HandleFixCast(Session session, params string[] parameters)
        {
            var magicState = session.Player.MagicState;

            if (magicState.IsCasting && DateTime.UtcNow - magicState.StartTime > TimeSpan.FromSeconds(5))
            {
                session.Network.EnqueueSend(new GameEventCommunicationTransientString(session, "Fixed casting state"));
                session.Player.SendUseDoneEvent();
                magicState.OnCastDone();
            }
        }

        [CommandHandler("castmeter", AccessLevel.Player, CommandHandlerFlag.RequiresWorld, "Shows the fast casting efficiency meter")]
        public static void HandleCastMeter(Session session, params string[] parameters)
        {
            if (parameters.Length == 0)
            {
                session.Player.MagicState.CastMeter = !session.Player.MagicState.CastMeter;
            }
            else
            {
                if (parameters[0].Equals("on", StringComparison.OrdinalIgnoreCase))
                    session.Player.MagicState.CastMeter = true;
                else
                    session.Player.MagicState.CastMeter = false;
            }
            session.Network.EnqueueSend(new GameMessageSystemChat($"Cast efficiency meter {(session.Player.MagicState.CastMeter ? "enabled" : "disabled")}", ChatMessageType.Broadcast));
        }

        private static List<string> configList = new List<string>()
        {
            "Common settings:\nConfirmVolatileRareUse, MainPackPreferred, SalvageMultiple, SideBySideVitals, UseCraftSuccessDialog",
            "Interaction settings:\nAcceptLootPermits, AllowGive, AppearOffline, AutoAcceptFellowRequest, DragItemOnPlayerOpensSecureTrade, FellowshipShareLoot, FellowshipShareXP, IgnoreAllegianceRequests, IgnoreFellowshipRequests, IgnoreTradeRequests, UseDeception",
            "UI settings:\nCoordinatesOnRadar, DisableDistanceFog, DisableHouseRestrictionEffects, DisableMostWeatherEffects, FilterLanguage, LockUI, PersistentAtDay, ShowCloak, ShowHelm, ShowTooltips, SpellDuration, TimeStamp, ToggleRun, UseMouseTurning",
            "Chat settings:\nHearAllegianceChat, HearGeneralChat, HearLFGChat, HearRoleplayChat, HearSocietyChat, HearTradeChat, HearPKDeaths, StayInChatMode",
            "Combat settings:\nAdvancedCombatUI, AutoRepeatAttack, AutoTarget, LeadMissileTargets, UseChargeAttack, UseFastMissiles, ViewCombatTarget, VividTargetingIndicator",
            "Character display settings:\nDisplayAge, DisplayAllegianceLogonNotifications, DisplayChessRank, DisplayDateOfBirth, DisplayFishingSkill, DisplayNumberCharacterTitles, DisplayNumberDeaths"
        };

        /// <summary>
        /// Mapping of GDLE -> ACE CharacterOptions
        /// </summary>
        private static Dictionary<string, string> translateOptions = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            // Common
            { "ConfirmVolatileRareUse", "ConfirmUseOfRareGems" },
            { "MainPackPreferred", "UseMainPackAsDefaultForPickingUpItems" },
            { "SalvageMultiple", "SalvageMultipleMaterialsAtOnce" },
            { "SideBySideVitals", "SideBySideVitals" },
            { "UseCraftSuccessDialog", "UseCraftingChanceOfSuccessDialog" },

            // Interaction
            { "AcceptLootPermits", "AcceptCorpseLootingPermissions" },
            { "AllowGive", "LetOtherPlayersGiveYouItems" },
            { "AppearOffline", "AppearOffline" },
            { "AutoAcceptFellowRequest", "AutomaticallyAcceptFellowshipRequests" },
            { "DragItemOnPlayerOpensSecureTrade", "DragItemToPlayerOpensTrade" },
            { "FellowshipShareLoot", "ShareFellowshipLoot" },
            { "FellowshipShareXP", "ShareFellowshipExpAndLuminance" },
            { "IgnoreAllegianceRequests", "IgnoreAllegianceRequests" },
            { "IgnoreFellowshipRequests", "IgnoreFellowshipRequests" },
            { "IgnoreTradeRequests", "IgnoreAllTradeRequests" },
            { "UseDeception", "AttemptToDeceiveOtherPlayers" },

            // UI
            { "CoordinatesOnRadar", "ShowCoordinatesByTheRadar" },
            { "DisableDistanceFog", "DisableDistanceFog" },
            { "DisableHouseRestrictionEffects", "DisableHouseRestrictionEffects" },
            { "DisableMostWeatherEffects", "DisableMostWeatherEffects" },
            { "FilterLanguage", "FilterLanguage" },
            { "LockUI", "LockUI" },
            { "PersistentAtDay", "AlwaysDaylightOutdoors" },
            { "ShowCloak", "ShowYourCloak" },
            { "ShowHelm", "ShowYourHelmOrHeadGear" },
            { "ShowTooltips", "Display3dTooltips" },
            { "SpellDuration", "DisplaySpellDurations" },
            { "TimeStamp", "DisplayTimestamps" },
            { "ToggleRun", "RunAsDefaultMovement" },
            { "UseMouseTurning", "UseMouseTurning" },

            // Chat
            { "HearAllegianceChat", "ListenToAllegianceChat" },
            { "HearGeneralChat", "ListenToGeneralChat" },
            { "HearLFGChat", "ListenToLFGChat" },
            // The enum member is ListenToRoleplayChat with a capital T. Enum.TryParse below is case sensitive,
            // so the old lowercase spelling made /config HearRoleplayChat report "Unknown character option".
            { "HearRoleplayChat", "ListenToRoleplayChat" },
            { "HearSocietyChat", "ListenToSocietyChat" },
            { "HearTradeChat", "ListenToTradeChat" },
            { "HearPKDeaths", "ListenToPKDeathMessages" },
            { "StayInChatMode", "StayInChatModeAfterSendingMessage" },

            // Combat
            { "AdvancedCombatUI", "AdvancedCombatInterface" },
            { "AutoRepeatAttack", "AutoRepeatAttacks" },
            { "AutoTarget", "AutoTarget" },
            { "LeadMissileTargets", "LeadMissileTargets" },
            { "UseChargeAttack", "UseChargeAttack" },
            { "UseFastMissiles", "UseFastMissiles" },
            { "ViewCombatTarget", "KeepCombatTargetsInView" },
            { "VividTargetingIndicator", "VividTargetingIndicator" },

            // Character Display
            { "DisplayAge", "AllowOthersToSeeYourAge" },
            { "DisplayAllegianceLogonNotifications", "ShowAllegianceLogons" },
            { "DisplayChessRank", "AllowOthersToSeeYourChessRank" },
            { "DisplayDateOfBirth", "AllowOthersToSeeYourDateOfBirth" },
            { "DisplayFishingSkill", "AllowOthersToSeeYourFishingSkill" },
            { "DisplayNumberCharacterTitles", "AllowOthersToSeeYourNumberOfTitles" },
            { "DisplayNumberDeaths", "AllowOthersToSeeYourNumberOfDeaths" },
        };

        /// <summary>
        /// True when the /config argument is the "list" request rather than a setting name.
        /// </summary>
        public static bool IsConfigListRequest(string settingName)
        {
            return "list".Equals(settingName, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Translates a GDLE-style /config setting name into the ACE CharacterOption it maps to.
        /// Lookup of the setting name is case insensitive; the mapped value must match a CharacterOption
        /// member exactly, which is why the table values are covered by a unit test.
        /// </summary>
        public static bool TryResolveConfigOption(string settingName, out CharacterOption characterOption)
        {
            characterOption = default;

            if (string.IsNullOrWhiteSpace(settingName))
                return false;

            if (!translateOptions.TryGetValue(settingName, out var aceOptionName))
                return false;

            return Enum.TryParse(aceOptionName, out characterOption);
        }

        /// <summary>
        /// Resolves the new value for a character option from the /config mode argument.
        /// "on" and "off" are absolute; anything else, including a missing argument, toggles.
        /// </summary>
        public static bool ResolveConfigValue(bool currentValue, string modeArgument)
        {
            if ("on".Equals(modeArgument, StringComparison.OrdinalIgnoreCase))
                return true;

            if ("off".Equals(modeArgument, StringComparison.OrdinalIgnoreCase))
                return false;

            return !currentValue;
        }

        /// <summary>
        /// Manually sets a character option on the server. Use /config list to see a list of settings.
        /// </summary>
        [CommandHandler("config", AccessLevel.Player, CommandHandlerFlag.RequiresWorld, 1, "Manually sets a character option on the server.\nUse /config list to see a list of settings.", "<setting> <on/off>")]
        public static void HandleConfig(Session session, params string[] parameters)
        {
            if (!PropertyManager.GetBool("player_config_command").Item)
            {
                session.Network.EnqueueSend(new GameMessageSystemChat("The command \"config\" is not currently enabled on this server.", ChatMessageType.Broadcast));
                return;
            }

            // /config list - show character options
            if (IsConfigListRequest(parameters[0]))
            {
                foreach (var line in configList)
                    session.Network.EnqueueSend(new GameMessageSystemChat(line, ChatMessageType.Broadcast));

                return;
            }

            // translate GDLE CharacterOptions for existing plugins
            if (!TryResolveConfigOption(parameters[0], out var characterOption))
            {
                session.Network.EnqueueSend(new GameMessageSystemChat($"Unknown character option: {parameters[0]}", ChatMessageType.Broadcast));
                return;
            }

            // modes of operation: on / off / toggle. If no mode is specified, toggle.
            var option = ResolveConfigValue(session.Player.GetCharacterOption(characterOption), parameters.Length > 1 ? parameters[1] : null);

            session.Player.SetCharacterOption(characterOption, option);

            session.Network.EnqueueSend(new GameMessageSystemChat($"Character option {parameters[0]} is now {(option ? "on" : "off")}.", ChatMessageType.Broadcast));

            // This used to unconditionally resend GameEventPlayerDescription, the full character snapshot the
            // server otherwise only ever sends once, inside the login sequence (Player_Networking.SendSelf).
            // Sending it mid session reliably wedges the client: it stops sending anything at all and the
            // session dies of Network Timeout about a minute later, taking any queued commands with it
            // (observed 5 for 5 against a 78 minute no-/config control, with no server side exception).
            // The option itself is already live on the server - it is read from Character.CharacterOptions1/2 -
            // so skipping the resend costs only the client's own Options panel refresh, which the client
            // picks up on its next login. The old behaviour is kept behind a diagnostic tunable so the
            // packet capture that pins down the client side mechanism can be run without a rebuild.
            if (PropertyManager.GetBool("player_config_command_resend_description").Item)
                session.Network.EnqueueSend(new GameEventPlayerDescription(session));
            else
                session.Network.EnqueueSend(new GameMessageSystemChat("The change is active now. Your Options panel will show it after your next login.", ChatMessageType.Broadcast));
        }

        /// <summary>
        /// Force resend of all visible objects known to this player. Can fix rare cases of invisible object bugs.
        /// Can only be used once every 5 mins max.
        /// </summary>
        [CommandHandler("objsend", AccessLevel.Player, CommandHandlerFlag.RequiresWorld, "Force resend of all visible objects known to this player. Can fix rare cases of invisible object bugs. Can only be used once every 5 mins max.")]
        public static void HandleObjSend(Session session, params string[] parameters)
        {
            // a good repro spot for this is the first room after the door in facility hub
            // in the portal drop / staircase room, the VisibleCells do not have the room after the door
            // however, the room after the door *does* have the portal drop / staircase room in its VisibleCells (the inverse relationship is imbalanced)
            // not sure how to fix this atm, seems like it triggers a client bug..

            if (DateTime.UtcNow - session.Player.PrevObjSend < TimeSpan.FromMinutes(5))
            {
                session.Player.SendTransientError("You have used this command too recently!");
                return;
            }

            var creaturesOnly = parameters.Length > 0 && parameters[0].Contains("creature", StringComparison.OrdinalIgnoreCase);

            var knownObjs = session.Player.GetKnownObjects();

            foreach (var knownObj in knownObjs)
            {
                if (creaturesOnly && !(knownObj is Creature))
                    continue;

                session.Player.RemoveTrackedObject(knownObj, false);
                session.Player.TrackObject(knownObj);
            }
            session.Player.PrevObjSend = DateTime.UtcNow;
        }

        // show player ace server versions
        [CommandHandler("aceversion", AccessLevel.Player, CommandHandlerFlag.RequiresWorld, "Shows this server's version data")]
        public static void HandleACEversion(Session session, params string[] parameters)
        {
            if (!PropertyManager.GetBool("version_info_enabled").Item)
            {
                session.Network.EnqueueSend(new GameMessageSystemChat("The command \"aceversion\" is not currently enabled on this server.", ChatMessageType.Broadcast));
                return;
            }

            var msg = ServerBuildInfo.GetVersionInfo();

            session.Network.EnqueueSend(new GameMessageSystemChat(msg, ChatMessageType.WorldBroadcast));
        }

        // reportbug < code | content > < description >
        [CommandHandler("reportbug", AccessLevel.Player, CommandHandlerFlag.RequiresWorld, 2,
            "Generate a Bug Report",
            "<category> <description>\n" +
            "This command generates a URL for you to copy and paste into your web browser to submit for review by server operators and developers.\n" +
            "Category can be the following:\n" +
            "Creature\n" +
            "NPC\n" +
            "Item\n" +
            "Quest\n" +
            "Recipe\n" +
            "Landblock\n" +
            "Mechanic\n" +
            "Code\n" +
            "Other\n" +
            "For the first three options, the bug report will include identifiers for what you currently have selected/targeted.\n" +
            "After category, please include a brief description of the issue, which you can further detail in the report on the website.\n" +
            "Examples:\n" +
            "/reportbug creature Drudge Prowler is over powered\n" +
            "/reportbug npc Ulgrim doesn't know what to do with Sake\n" +
            "/reportbug quest I can't enter the portal to the Lost City of Frore\n" +
            "/reportbug recipe I cannot combine Bundle of Arrowheads with Bundle of Arrowshafts\n" +
            "/reportbug code I was killed by a Non-Player Killer\n"
            )]
        public static void HandleReportbug(Session session, params string[] parameters)
        {
            if (!PropertyManager.GetBool("reportbug_enabled").Item)
            {
                session.Network.EnqueueSend(new GameMessageSystemChat("The command \"reportbug\" is not currently enabled on this server.", ChatMessageType.Broadcast));
                return;
            }

            var category = parameters[0];
            var description = "";

            for (var i = 1; i < parameters.Length; i++)
                description += parameters[i] + " ";

            description.Trim();

            switch (category.ToLower())
            {
                case "creature":
                case "npc":
                case "quest":
                case "item":
                case "recipe":
                case "landblock":
                case "mechanic":
                case "code":
                case "other":
                    break;
                default:
                    category = "Other";
                    break;
            }

            var sn = ConfigManager.Config.Server.WorldName;
            var c = session.Player.Name;

            var st = "ACE";

            //var versions = ServerBuildInfo.GetVersionInfo();
            var databaseVersion = DatabaseManager.World.GetVersion();
            var sv = ServerBuildInfo.FullVersion;
            var pv = databaseVersion.PatchVersion;

            //var ct = PropertyManager.GetString("reportbug_content_type").Item;
            var cg = category.ToLower();

            var w = "";
            var g = "";

            if (cg == "creature" || cg == "npc"|| cg == "item" || cg == "item")
            {
                var objectId = new ObjectGuid();
                if (session.Player.HealthQueryTarget.HasValue || session.Player.ManaQueryTarget.HasValue || session.Player.CurrentAppraisalTarget.HasValue)
                {
                    if (session.Player.HealthQueryTarget.HasValue)
                        objectId = new ObjectGuid((uint)session.Player.HealthQueryTarget);
                    else if (session.Player.ManaQueryTarget.HasValue)
                        objectId = new ObjectGuid((uint)session.Player.ManaQueryTarget);
                    else
                        objectId = new ObjectGuid((uint)session.Player.CurrentAppraisalTarget);

                    //var wo = session.Player.CurrentLandblock?.GetObject(objectId);

                    var wo = session.Player.FindObject(objectId.Full, Player.SearchLocations.Everywhere);

                    if (wo != null)
                    {
                        w = $"{wo.WeenieClassId}";
                        g = $"0x{wo.Guid:X8}";
                    }
                }
            }

            var l = session.Player.Location.ToLOCString();

            var issue = description;

            var urlbase = $"https://www.accpp.net/bug?";

            var url = urlbase;
            if (sn.Length > 0)
                url += $"sn={Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(sn))}";
            if (c.Length > 0)
                url += $"&c={Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(c))}";
            if (st.Length > 0)
                url += $"&st={Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(st))}";
            if (sv.Length > 0)
                url += $"&sv={Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(sv))}";
            if (pv.Length > 0)
                url += $"&pv={Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(pv))}";
            //if (ct.Length > 0)
            //    url += $"&ct={Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(ct))}";
            if (cg.Length > 0)
            {
                if (cg == "npc")
                    cg = cg.ToUpper();
                else
                    cg = char.ToUpper(cg[0]) + cg.Substring(1);
                url += $"&cg={Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(cg))}";
            }
            if (w.Length > 0)
                url += $"&w={Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(w))}";
            if (g.Length > 0)
                url += $"&g={Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(g))}";
            if (l.Length > 0)
                url += $"&l={Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(l))}";
            if (issue.Length > 0)
                url += $"&i={Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(issue))}";

            var msg = "\n\n\n\n";
            msg += "Bug Report - Copy and Paste the following URL into your browser to submit a bug report\n";
            msg += "-=-\n";
            msg += $"{url}\n";
            msg += "-=-\n";
            msg += "\n\n\n\n";

            session.Network.EnqueueSend(new GameMessageSystemChat(msg, ChatMessageType.AdminTell));
        }

        // DPS-challenge duration all scores are reported against (the content portal uses 60s).
        private const double DpsLeaderboardDuration = 60.0;

        [CommandHandler("top", AccessLevel.Player, CommandHandlerFlag.RequiresWorld, "Show a Proving Grounds leaderboard (top 20).", "dps | defense | wave")]
        public static void HandleTop(Session session, params string[] parameters)
        {
            var player = session.Player;
            if (player == null)
                return;

            // Bare /top lists the boards instead of silently defaulting to one of them.
            var sub = parameters.Length > 0 ? parameters[0].ToLowerInvariant() : "";
            if (sub != "dps" && sub != "defense" && sub != "wave")
            {
                var usage = "=== Proving Grounds Leaderboards ===\n"
                    + "/top dps - Attack arena: highest damage dealt in the 60s trial\n"
                    + "/top defense - Defense arena: longest survival against the Squall\n"
                    + "/top wave - Wave gauntlet: highest wave cleared\n";
                session.Network.EnqueueSend(new GameMessageSystemChat(usage, ChatMessageType.System));
                return;
            }

            // Wave leaderboard: highest wave fully cleared in the Proving Grounds (Wave) gauntlet.
            if (sub == "wave")
            {
                var rankedWave = PlayerManager.GetAllPlayers()
                    .Where(p => (p.GetProperty(PropertyInt64.BestWaveScore) ?? 0) > 0)
                    .OrderByDescending(p => p.GetProperty(PropertyInt64.BestWaveScore) ?? 0)
                    .ToList();

                var waveMsg = "=== Wave Challenge Leaderboard (Top 20) ===\n";

                if (rankedWave.Count == 0)
                {
                    waveMsg += "No scores have been recorded yet.\n";
                    session.Network.EnqueueSend(new GameMessageSystemChat(waveMsg, ChatMessageType.System));
                    return;
                }

                for (var i = 0; i < rankedWave.Count && i < 20; i++)
                {
                    var waveScore = rankedWave[i].GetProperty(PropertyInt64.BestWaveScore) ?? 0;
                    waveMsg += $"{i + 1}. {rankedWave[i].Name} - Wave {waveScore:N0}\n";
                }

                // if the requesting player has a score but sits outside the top 20, append their own standing
                var myWaveRank = rankedWave.FindIndex(p => p.Guid == player.Guid);
                if (myWaveRank >= 20)
                {
                    var myScore = rankedWave[myWaveRank].GetProperty(PropertyInt64.BestWaveScore) ?? 0;
                    waveMsg += $"...\n{myWaveRank + 1}. {player.Name} (you) - Wave {myScore:N0}\n";
                }

                session.Network.EnqueueSend(new GameMessageSystemChat(waveMsg, ChatMessageType.System));
                return;
            }

            // Defense (survival) leaderboard: best seconds survived in the Proving Grounds (Defense) arena.
            if (sub == "defense")
            {
                var rankedDefense = PlayerManager.GetAllPlayers()
                    .Where(p => (p.GetProperty(PropertyInt64.BestSurvivalScore) ?? 0) > 0)
                    .OrderByDescending(p => p.GetProperty(PropertyInt64.BestSurvivalScore) ?? 0)
                    .ToList();

                var defenseMsg = "=== Defense Challenge Leaderboard (Top 20) ===\n";

                if (rankedDefense.Count == 0)
                {
                    defenseMsg += "No scores have been recorded yet.\n";
                    session.Network.EnqueueSend(new GameMessageSystemChat(defenseMsg, ChatMessageType.System));
                    return;
                }

                for (var i = 0; i < rankedDefense.Count && i < 20; i++)
                {
                    var survivalScore = rankedDefense[i].GetProperty(PropertyInt64.BestSurvivalScore) ?? 0;
                    defenseMsg += $"{i + 1}. {rankedDefense[i].Name} - {survivalScore:N0}s\n";
                }

                // if the requesting player has a score but sits outside the top 20, append their own standing
                var myDefenseRank = rankedDefense.FindIndex(p => p.Guid == player.Guid);
                if (myDefenseRank >= 20)
                {
                    var myScore = rankedDefense[myDefenseRank].GetProperty(PropertyInt64.BestSurvivalScore) ?? 0;
                    defenseMsg += $"...\n{myDefenseRank + 1}. {player.Name} (you) - {myScore:N0}s\n";
                }

                session.Network.EnqueueSend(new GameMessageSystemChat(defenseMsg, ChatMessageType.System));
                return;
            }

            var ranked = PlayerManager.GetAllPlayers()
                .Where(p => (p.GetProperty(PropertyInt64.BestDpsScore) ?? 0) > 0)
                .OrderByDescending(p => p.GetProperty(PropertyInt64.BestDpsScore) ?? 0)
                .ToList();

            var msg = "=== DPS Challenge Leaderboard (Top 20) ===\n";

            if (ranked.Count == 0)
            {
                msg += "No scores have been recorded yet.\n";
                session.Network.EnqueueSend(new GameMessageSystemChat(msg, ChatMessageType.System));
                return;
            }

            for (var i = 0; i < ranked.Count && i < 20; i++)
            {
                var score = ranked[i].GetProperty(PropertyInt64.BestDpsScore) ?? 0;
                msg += $"{i + 1}. {ranked[i].Name} - {score:N0} damage ({score / DpsLeaderboardDuration:N0} DPS)\n";
            }

            // if the requesting player has a score but sits outside the top 20, append their own standing
            var myRank = ranked.FindIndex(p => p.Guid == player.Guid);
            if (myRank >= 20)
            {
                var myScore = ranked[myRank].GetProperty(PropertyInt64.BestDpsScore) ?? 0;
                msg += $"...\n{myRank + 1}. {player.Name} (you) - {myScore:N0} damage ({myScore / DpsLeaderboardDuration:N0} DPS)\n";
            }

            session.Network.EnqueueSend(new GameMessageSystemChat(msg, ChatMessageType.System));
        }

        [CommandHandler("b", AccessLevel.Player, CommandHandlerFlag.RequiresWorld, "Show bank balances, or shorthand for /bank <args>", "see /bank")]
        public static void HandleBankShort(Session session, params string[] parameters)
        {
            // Bare "/b" shows balances and points to /bank for the full command list.
            if (parameters.Length == 0)
            {
                if (session.Player == null)
                    return;

                ShowBankBalances(session);
                session.Network.EnqueueSend(new GameMessageSystemChat("[BANK] Type /bank for the full list of commands.", ChatMessageType.System));
                return;
            }

            HandleBank(session, parameters);
        }

        [CommandHandler("bank", AccessLevel.Player, CommandHandlerFlag.RequiresWorld, "Deposit, withdraw, transfer, and check banked currency",
            "deposit|withdraw|transfer|balance ...\n" +
            "Currencies: p=pyreals, l=luminance, k=legendary keys, pn=promissory notes, n=trade notes, pea=peas\n" +
            "/b d                 - deposit everything\n" +
            "/b d p [amount]      - deposit pyreals (all, or an amount like 10k)\n" +
            "/b d pea             - deposit silver/gold/pyreal peas as pyreals, at face value\n" +
            "/b w p <amount>      - withdraw pyreals (as 250k notes + coins)\n" +
            "/b w n [value] <cnt> - withdraw trade notes (value defaults to 250000)\n" +
            "/b w k|pn <amount>   - withdraw legendary keys / promissory notes\n" +
            "/b t p|k|pn <amount> <char> - transfer to another character\n" +
            "/b ad [on|off]       - vendor sale proceeds to the bank (on) or as coins in your pack (off)\n" +
            "/b                   - show balances\n" +
            "Luminance can be deposited but not withdrawn or transferred.")]
        public static void HandleBank(Session session, params string[] parameters)
        {
            var player = session.Player;
            if (player == null)
                return;

            // Bare "/bank" shows the command help; balances are on "/b" (or "/bank b").
            var sub = parameters.Length > 0 ? parameters[0].ToLowerInvariant() : "help";

            var isMutating = sub is "d" or "deposit" or "w" or "withdraw" or "t" or "transfer";
            if (isMutating)
            {
                var since = DateTime.UtcNow - player.LastBankCommandTime;
                if (since.TotalSeconds < 1.0)
                {
                    session.Network.EnqueueSend(new GameMessageSystemChat("[BANK] You are using bank commands too quickly.", ChatMessageType.System));
                    return;
                }
                player.LastBankCommandTime = DateTime.UtcNow;

                if (player.IsBusy)
                {
                    session.Network.EnqueueSend(new GameMessageSystemChat("[BANK] You are too busy - finish what you are doing and try again.", ChatMessageType.System));
                    return;
                }

                // Block bank ops mid-trade: otherwise notes/coins committed to the trade window could be
                // deposited to the bank as well (a dupe).
                if (player.IsTrading)
                {
                    session.Network.EnqueueSend(new GameMessageSystemChat("[BANK] You cannot use the bank while trading.", ChatMessageType.System));
                    return;
                }
            }

            // currency token (parameters[1]) if present
            var cur = parameters.Length > 1 ? parameters[1].ToLowerInvariant() : "";

            switch (sub)
            {
                case "d":
                case "deposit":
                    HandleBankDeposit(player, cur, parameters);
                    break;

                case "w":
                case "withdraw":
                    HandleBankWithdraw(player, cur, parameters);
                    break;

                case "t":
                case "transfer":
                    HandleBankTransfer(player, cur, parameters);
                    break;

                case "b":
                case "balance":
                    ShowBankBalances(session);
                    break;

                case "ad":
                case "autodeposit":
                    HandleBankAutoDeposit(player, cur);
                    break;

                case "help":
                case "?":
                default:
                    ShowBankHelp(session);
                    break;
            }
        }

        private static void ShowBankBalances(Session session)
        {
            var player = session.Player;
            session.Network.EnqueueSend(new GameMessageSystemChat("[BANK] Your balances:", ChatMessageType.System));
            session.Network.EnqueueSend(new GameMessageSystemChat($"[BANK] Pyreals: {player.BankedPyreals:N0}", ChatMessageType.System));
            session.Network.EnqueueSend(new GameMessageSystemChat($"[BANK] Luminance: {player.BankedLuminance:N0}", ChatMessageType.System));
            session.Network.EnqueueSend(new GameMessageSystemChat($"[BANK] Legendary Keys: {player.BankedLegendaryKeys:N0}", ChatMessageType.System));
            session.Network.EnqueueSend(new GameMessageSystemChat($"[BANK] Promissory Notes: {player.BankedPromissoryNotes:N0}", ChatMessageType.System));

            if (PropertyManager.GetBool("offline_bonus_enabled").Item)
                session.Network.EnqueueSend(new GameMessageSystemChat($"[BANK] Offline Bonus Time: {player.OfflineExperienceBonusDisplay}", ChatMessageType.System));

            if (PropertyManager.GetBool("alt_character_bonus_enabled").Item && player.IsAltCharacterBonusActive)
            {
                var altBonusPercent = (int)Math.Round(PropertyManager.GetDouble("alt_character_bonus_multiplier").Item * 100);
                session.Network.EnqueueSend(new GameMessageSystemChat($"[BANK] Alternate Character Bonus +{altBonusPercent}% until Enl {player.AltCharacterBonusTargetEnlightenment}, Level {player.AltCharacterBonusTargetLevel}", ChatMessageType.System));
            }
        }

        private static void ShowBankHelp(Session session)
        {
            session.Network.EnqueueSend(new GameMessageSystemChat("[BANK] Bank commands (currencies: p=pyreals, l=luminance, k=legendary keys, pn=promissory notes, n=trade notes, pea=peas):", ChatMessageType.System));
            session.Network.EnqueueSend(new GameMessageSystemChat("/b                   - show balances", ChatMessageType.System));
            session.Network.EnqueueSend(new GameMessageSystemChat("/b d                 - deposit everything", ChatMessageType.System));
            session.Network.EnqueueSend(new GameMessageSystemChat("/b d p|l|k|pn|n|pea [amt] - deposit one currency (amount optional)", ChatMessageType.System));
            session.Network.EnqueueSend(new GameMessageSystemChat("/b d pea             - bank silver/gold/pyreal peas as pyreals at face value", ChatMessageType.System));
            session.Network.EnqueueSend(new GameMessageSystemChat("/b w p <amount>      - withdraw pyreals (as 250k notes + coins)", ChatMessageType.System));
            session.Network.EnqueueSend(new GameMessageSystemChat("/b w n [value] <cnt> - withdraw trade notes (value defaults to 250000)", ChatMessageType.System));
            session.Network.EnqueueSend(new GameMessageSystemChat("/b w k|pn <amount>   - withdraw legendary keys / promissory notes", ChatMessageType.System));
            session.Network.EnqueueSend(new GameMessageSystemChat("/b t p|k|pn <amount> <char> - transfer to another character", ChatMessageType.System));
            session.Network.EnqueueSend(new GameMessageSystemChat("/b ad [on|off]       - vendor sale proceeds to the bank (on) or as coins in your pack (off)", ChatMessageType.System));
            session.Network.EnqueueSend(new GameMessageSystemChat("Luminance can be deposited but not withdrawn or transferred. Amounts accept k/m/b suffixes (e.g. 10k).", ChatMessageType.System));
        }

        private static void HandleBankDeposit(Player player, string cur, string[] parameters)
        {
            // optional amount at parameters[2]
            long amount = -1;
            if (parameters.Length > 2 && !Player.TryParseAmount(parameters[2], out amount))
            {
                player.Session.Network.EnqueueSend(new GameMessageSystemChat("[BANK] Invalid amount.", ChatMessageType.System));
                return;
            }

            switch (cur)
            {
                case "": player.DepositAll(); break;
                case "p": case "pyreals": player.DepositPyreals(amount); break;
                case "l": case "luminance": player.DepositLuminance(amount); break;
                case "k": case "keys": player.DepositLegendaryKeys(); break;
                case "pn": case "promissory": case "promissorynotes": player.DepositPromissoryNotes(); break;
                case "n": case "notes": player.DepositTradeNotes(); break;
                case "pea": case "peas": player.DepositPeas(); break;
                default:
                    player.Session.Network.EnqueueSend(new GameMessageSystemChat("[BANK] Unknown currency. Use p, l, k, pn, n, or pea.", ChatMessageType.System));
                    break;
            }
        }

        /// <summary>
        /// Reports or sets the per-character vendor auto-deposit toggle. Touches no items, so it is deliberately
        /// outside the mutating-command cooldown / IsBusy / IsTrading gate, same as "balance".
        ///
        /// A change rushes the next save rather than waiting for the periodic one: the setting exists so an
        /// external inventory-reading tool sees coin land in the pack, and silently reverting it in a crash
        /// window would put that tool straight back into the sell-to-restock loop this toggle prevents.
        /// </summary>
        private static void HandleBankAutoDeposit(Player player, string cur)
        {
            void Msg(string text) => player.Session.Network.EnqueueSend(new GameMessageSystemChat($"[BANK] {text}", ChatMessageType.System));

            switch (cur)
            {
                case "":
                    Msg(player.BankAutoDeposit
                        ? "Vendor auto-deposit is ON - sale proceeds go straight to your bank. Use /b ad off to be paid in coins instead."
                        : "Vendor auto-deposit is OFF - sale proceeds are paid as coins into your pack. Use /b ad on to bank them instead.");
                    break;

                case "on":
                case "true":
                case "1":
                    player.BankAutoDeposit = true;
                    player.RushNextPlayerSave(5);
                    Msg("Vendor auto-deposit is now ON. Sale proceeds go straight to your bank, so you need no pack space to sell.");
                    break;

                case "off":
                case "false":
                case "0":
                    player.BankAutoDeposit = false;
                    player.RushNextPlayerSave(5);
                    Msg("Vendor auto-deposit is now OFF. Sale proceeds arrive as coins in your pack, so you need free pack space; anything that will not fit is banked instead.");
                    break;

                default:
                    Msg("Usage: /b ad [on|off]");
                    break;
            }
        }

        private static void HandleBankWithdraw(Player player, string cur, string[] parameters)
        {
            switch (cur)
            {
                case "p":
                case "pyreals":
                    if (parameters.Length < 3 || !Player.TryParseAmount(parameters[2], out var pAmt))
                    {
                        player.Session.Network.EnqueueSend(new GameMessageSystemChat("[BANK] Usage: /b w p <amount>", ChatMessageType.System));
                        return;
                    }
                    player.WithdrawPyreals(pAmt);
                    break;

                case "n":
                case "notes":
                    // /b w n <count>  OR  /b w n <value> <count>
                    long denom = 250000, count;
                    if (parameters.Length == 3)
                    {
                        if (!Player.TryParseAmount(parameters[2], out count)) { WithdrawUsage(player); return; }
                    }
                    else if (parameters.Length >= 4)
                    {
                        if (!Player.TryParseAmount(parameters[2], out denom) || !Player.TryParseAmount(parameters[3], out count)) { WithdrawUsage(player); return; }
                    }
                    else { WithdrawUsage(player); return; }
                    player.WithdrawTradeNotes(denom, count);
                    break;

                case "k":
                case "keys":
                    if (parameters.Length < 3 || !Player.TryParseAmount(parameters[2], out var kAmt)) { player.Session.Network.EnqueueSend(new GameMessageSystemChat("[BANK] Usage: /b w k <amount>", ChatMessageType.System)); return; }
                    player.WithdrawLegendaryKeys(kAmt);
                    break;

                case "pn":
                case "promissory":
                case "promissorynotes":
                    if (parameters.Length < 3 || !Player.TryParseAmount(parameters[2], out var pnAmt)) { player.Session.Network.EnqueueSend(new GameMessageSystemChat("[BANK] Usage: /b w pn <amount>", ChatMessageType.System)); return; }
                    player.WithdrawPromissoryNotes(pnAmt);
                    break;

                case "l":
                case "luminance":
                    player.Session.Network.EnqueueSend(new GameMessageSystemChat("[BANK] Luminance cannot be withdrawn.", ChatMessageType.System));
                    break;

                default:
                    player.Session.Network.EnqueueSend(new GameMessageSystemChat("[BANK] Unknown currency. Use p, n, k, or pn.", ChatMessageType.System));
                    break;
            }
        }

        private static void WithdrawUsage(Player player)
        {
            player.Session.Network.EnqueueSend(new GameMessageSystemChat("[BANK] Usage: /b w n <count>  or  /b w n <value> <count>", ChatMessageType.System));
        }

        private static void HandleBankTransfer(Player player, string cur, string[] parameters)
        {
            // /b t <currency> <amount> <char...>
            if (parameters.Length < 4)
            {
                player.Session.Network.EnqueueSend(new GameMessageSystemChat("[BANK] Usage: /b t p|k|pn <amount> <character>", ChatMessageType.System));
                return;
            }

            if (!Player.TryParseAmount(parameters[2], out var amount))
            {
                player.Session.Network.EnqueueSend(new GameMessageSystemChat("[BANK] Invalid amount.", ChatMessageType.System));
                return;
            }

            // support quoted / multi-word names by joining the remaining tokens
            var target = string.Join(" ", parameters, 3, parameters.Length - 3);

            switch (cur)
            {
                case "p": case "pyreals": player.TransferPyreals(amount, target); break;
                case "l": case "luminance":
                    player.Session.Network.EnqueueSend(new GameMessageSystemChat("[BANK] Luminance transfer is currently disabled.", ChatMessageType.System));
                    break;
                case "k": case "keys": player.TransferLegendaryKeys(amount, target); break;
                case "pn": case "promissory": case "promissorynotes": player.TransferPromissoryNotes(amount, target); break;
                default:
                    player.Session.Network.EnqueueSend(new GameMessageSystemChat("[BANK] Unknown currency. Use p, l, k, or pn.", ChatMessageType.System));
                    break;
            }
        }

        /// <summary>
        /// "/dn" - recall to the Drift Network, the Meridian's hall of pinned drift-doors.
        /// The marketplace recall (/mp) is a client GameAction, so this is registered as a text command
        /// instead, but the behaviour it delegates to is modelled directly on it: same guards, the same
        /// MarketplaceRecall animation, a 14s cast, and the same abort-if-you-move check.
        /// </summary>
        [CommandHandler("dn", AccessLevel.Player, CommandHandlerFlag.RequiresWorld, 0,
            "Recall to the Drift Network.",
            "Begins a recall to the Meridian's Drift Network. Like /mp, this takes a few seconds and is\n"
            + "interrupted if you move too far.")]
        public static void HandleDriftNetworkRecall(Session session, params string[] parameters)
        {
            session.Player.HandleActionTeleToDriftNetwork();
        }
    }
}
