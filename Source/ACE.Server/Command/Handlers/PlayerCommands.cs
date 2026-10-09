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
        // rewards against (distinct quests across the account, so an alt repeating a quest adds nothing to it),
        // and which reward tiers this character has already claimed
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

            session.Network.EnqueueSend(new GameMessageSystemChat($"Quest stamps: {own} (account total: {accountTotal} unique quests)", ChatMessageType.Broadcast));

            var next = QuestStamps.NextThreshold(accountTotal);

            if (next != null)
                session.Network.EnqueueSend(new GameMessageSystemChat($"Next reward at {next.Value} ({next.Value - accountTotal} to go).", ChatMessageType.Broadcast));
            else
                session.Network.EnqueueSend(new GameMessageSystemChat("All reward tiers reached.", ChatMessageType.Broadcast));

            var tier1 = session.Player.QuestManager.HasQuest("QuestStampTier1") ? "yes" : "no";
            var tier2 = session.Player.QuestManager.HasQuest("QuestStampTier2") ? "yes" : "no";
            var tier3 = session.Player.QuestManager.HasQuest("QuestStampTier3") ? "yes" : "no";
            var tier4 = session.Player.QuestManager.HasQuest("QuestStampTier4") ? "yes" : "no";
            var tier5 = session.Player.QuestManager.HasQuest("QuestStampTier5") ? "yes" : "no";

            session.Network.EnqueueSend(new GameMessageSystemChat($"Reward tiers claimed: Tier1 {tier1}, Tier2 {tier2}, Tier3 {tier3}, Tier4 {tier4}, Tier5 {tier5}", ChatMessageType.Broadcast));
        }

        // xp progress: the client's own "XP for next level" chart ends at level 275 and shows a large
        // negative number past it (see Player_Xp.cs GetPlayerMaxLevel doc comment for the chart's hard
        // ceiling at 1445) - this command reports the server's own correct numbers over chat instead
        [CommandHandler("xp", AccessLevel.Player, CommandHandlerFlag.RequiresWorld, 0,
            "Shows your experience progress",
            "")]
        public static void HandleXp(Session session, params string[] parameters)
        {
            var player = session.Player;

            var level = player.Level ?? 1;
            var totalExperience = player.TotalExperience ?? 0;
            var availableExperience = player.AvailableExperience ?? 0;

            var maxLevel = player.GetPlayerMaxLevel();

            if (level >= maxLevel)
            {
                session.Network.EnqueueSend(new GameMessageSystemChat($"Level {level} - the maximum level. There is nothing beyond this.", ChatMessageType.Broadcast));
            }
            else
            {
                var remainingXp = player.GetRemainingXP();

                var floorXp = Player.GetTotalXP(level);
                var ceilingXp = Player.GetTotalXP(level + 1);

                var bandWidth = ceilingXp - floorXp;

                // level numbers stay unformatted - the max-level line below reads "Level 1445", and a
                // separator here would render the neighbouring level as "Level 1,444" against it
                var line = $"Level {level} - {remainingXp:N0} experience to level {level + 1}";

                if (bandWidth > 0)
                {
                    var progress = (double)(totalExperience - (long)floorXp) / bandWidth * 100.0;
                    progress = Math.Clamp(progress, 0.0, 100.0);

                    line += $" ({progress:N1}% of the way)";
                }

                line += ".";

                session.Network.EnqueueSend(new GameMessageSystemChat(line, ChatMessageType.Broadcast));
            }

            session.Network.EnqueueSend(new GameMessageSystemChat($"Total experience: {totalExperience:N0}.", ChatMessageType.Broadcast));
            session.Network.EnqueueSend(new GameMessageSystemChat($"Unassigned experience: {availableExperience:N0}.", ChatMessageType.Broadcast));
        }

        // luminance per hour: the accumulator opens fresh at login and is purely in-memory/per-session
        // (see Player_Luminance.cs ResetLumRateWindow / CalcLumPerHour)
        [CommandHandler("lph", AccessLevel.Player, CommandHandlerFlag.RequiresWorld, 0,
            "Shows how much Luminance you have earned per hour since you logged in, or since your last /lph start",
            "[start]")]
        public static void HandleLph(Session session, params string[] parameters)
        {
            var player = session.Player;

            if (parameters.Length == 0)
            {
                var earned = player.LumRateWindowEarned;
                var elapsed = Time.GetUnixTime() - player.LumRateWindowStart;

                var rate = Player.CalcLumPerHour(earned, elapsed);

                if (rate != null)
                {
                    var elapsedText = FormatLphElapsed(elapsed);
                    session.Network.EnqueueSend(new GameMessageSystemChat($"Luminance: {earned:N0} earned over {elapsedText} - {rate.Value:N0} per hour.", ChatMessageType.Broadcast));
                }
                else
                {
                    session.Network.EnqueueSend(new GameMessageSystemChat($"Luminance: {earned:N0} earned. The timer has only been running a few seconds - too short to show a rate.", ChatMessageType.Broadcast));
                }
            }
            else if (parameters.Length == 1 && parameters[0].Trim().Equals("start", StringComparison.OrdinalIgnoreCase))
            {
                player.ResetLumRateWindow();

                session.Network.EnqueueSend(new GameMessageSystemChat("Luminance per hour timer reset.", ChatMessageType.Broadcast));
            }
            else
            {
                session.Network.EnqueueSend(new GameMessageSystemChat("Usage: /lph [start]", ChatMessageType.Broadcast));
                session.Network.EnqueueSend(new GameMessageSystemChat("/lph reports your Luminance-per-hour rate; /lph start restarts the timer.", ChatMessageType.Broadcast));
            }
        }

        /// <summary>
        /// Renders an elapsed duration for /lph as "1h 23m" / "23m" / "45s". Hours are total hours - a
        /// 30-hour session reads "30h 5m", never "1d 6h". A negative elapsed is clamped to zero first.
        ///
        /// Internal rather than private so ACE.Server.Tests can pin these boundaries (InternalsVisibleTo,
        /// ACE.Server.csproj:15).
        /// </summary>
        internal static string FormatLphElapsed(double elapsedSeconds)
        {
            if (elapsedSeconds < 0)
                elapsedSeconds = 0;

            var span = TimeSpan.FromSeconds(elapsedSeconds);

            var totalHours = (int)span.TotalHours;

            if (totalHours >= 1)
                return $"{totalHours}h {span.Minutes}m";

            if (span.TotalMinutes >= 1)
                return $"{(int)span.TotalMinutes}m";

            return $"{(int)span.TotalSeconds}s";
        }

        [CommandHandler("offlinebonus", AccessLevel.Player, CommandHandlerFlag.RequiresWorld, "Shows your offline experience bonus")]
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

        [CommandHandler("pickupspeed", AccessLevel.Player, CommandHandlerFlag.RequiresWorld, "Shows your current pick-up speed bonus, including any permanent bonuses earned from quests")]
        public static void HandlePickupSpeed(Session session, params string[] parameters)
        {
            session.Player.ShowPickupSpeedStatus();
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
        /// Per-character toggle for the outgoing damage-over-time combat message: the line you see
        /// when your own DoT spell ticks on a target. With no argument, reports the current setting
        /// and whether it is your own choice or inherited from the server default.
        /// </summary>
        [CommandHandler("dotdamage", AccessLevel.Player, CommandHandlerFlag.RequiresWorld, 0,
            "Toggle the outgoing damage-over-time combat message (the message you see when your DoT spell ticks on a target).", "on | off")]
        public static void HandleDotDamage(Session session, params string[] parameters)
        {
            var player = session.Player;
            if (player == null)
                return;

            void Msg(string text) => session.Network.EnqueueSend(new GameMessageSystemChat(text, ChatMessageType.Broadcast));

            if (parameters.Length == 0)
            {
                if (player.ShowDotDamageOverride is bool own)
                {
                    Msg($"Damage-over-time damage messages are currently {(own ? "on" : "off")} (your own setting).");
                }
                else
                {
                    var inherited = player.ShowDotDamage;
                    Msg($"Damage-over-time damage messages are currently {(inherited ? "on" : "off")} (server default). Use /dotdamage {(inherited ? "off" : "on")} to turn them {(inherited ? "off" : "on")}.");
                }

                return;
            }

            switch (parameters[0].ToLowerInvariant())
            {
                case "on":
                    player.ShowDotDamage = true;
                    Msg("Damage-over-time damage messages are now on.");
                    break;

                case "off":
                    player.ShowDotDamage = false;
                    Msg("Damage-over-time damage messages are now off.");
                    break;

                default:
                    Msg("Usage: /dotdamage on | off");
                    break;
            }
        }

        /// <summary>
        /// Per-character fast tick opt-in (PropertyBool.FastTickOptIn). With no argument, reports the
        /// effective state and whether it is your own choice, the server default, or forced by PK status.
        /// Refused mid-cast or mid-drink; otherwise applied live.
        /// </summary>
        [CommandHandler("fasttick", AccessLevel.Player, CommandHandlerFlag.RequiresWorld, 0,
            "Opt in or out of fast tick: faster spell release, fast-chug and melee stick-to-target. Some players see quirky movement or rubber-banding; try it and switch back if it misbehaves.",
            "on | off | default | status")]
        public static void HandleFastTick(Session session, params string[] parameters)
        {
            var player = session.Player;
            if (player == null)
                return;

            void Msg(string text) => session.Network.EnqueueSend(new GameMessageSystemChat(text, ChatMessageType.Broadcast));

            var arg = parameters.Length == 0 ? "status" : parameters[0].ToLowerInvariant();

            if (arg == "status")
            {
                var effective = player.FastTick;
                string source;
                if (player.IsPKType)
                    source = "forced on because you are PK or PKLite";
                else if (player.FastTickOptIn.HasValue)
                    source = "your own setting";
                else
                    source = "server default";

                Msg($"Fast tick is currently {(effective ? "on" : "off")} ({source}).");
                if (player.IsPKType && player.FastTickOptIn.HasValue)
                    Msg($"Your saved choice is {(player.FastTickOptIn.Value ? "on" : "off")}, but it has no effect while you are PK or PKLite.");
                Msg("Fast tick gives faster spell release, fast-chug and melee stick-to-target, but some players see quirky movement or rubber-banding. Try it and switch back if it misbehaves. Use /fasttick on | off | default.");
                return;
            }

            if (arg != "on" && arg != "off" && arg != "default")
            {
                Msg("Usage: /fasttick on | off | default | status");
                return;
            }

            var blocker = player.FastTickChangeBlocker(arg == "on" ? true : arg == "off" ? false : (bool?)null);
            if (blocker != null)
            {
                Msg(blocker);
                return;
            }

            switch (arg)
            {
                case "on":
                    player.SetProperty(PropertyBool.FastTickOptIn, true);
                    Msg("Fast tick is now on for you.");
                    break;

                case "off":
                    player.SetProperty(PropertyBool.FastTickOptIn, false);
                    Msg("Fast tick is now off for you.");
                    break;

                default:
                    player.RemoveProperty(PropertyBool.FastTickOptIn);
                    Msg("Fast tick now follows the server default.");
                    break;
            }

            if (player.IsPKType)
                Msg("Your choice is saved, but it has no effect while you are PK or PKLite (fast tick is always on for PK players).");
        }

        /// <summary>
        /// Per-character toggle for summon assist: when on, your combat pets switch to the monster you most
        /// recently hit (CombatPet.HandleFindTarget). On by default. With no argument, reports the current
        /// setting and whether it is your own choice or the default. A change rushes the next save, as
        /// /summondamage does, so an "off" is not lost if the server goes down before the next regular save.
        /// </summary>
        [CommandHandler("summonattackontarget", AccessLevel.Player, CommandHandlerFlag.RequiresWorld, 0,
            "Toggle whether your combat pets attack the monster you most recently hit.", "on | off")]
        public static void HandleSummonAttackOnTarget(Session session, params string[] parameters)
        {
            var player = session.Player;
            if (player == null)
                return;

            void Msg(string text) => session.Network.EnqueueSend(new GameMessageSystemChat(text, ChatMessageType.Broadcast));

            if (parameters.Length == 0)
            {
                if (player.SummonAttackOnTargetOverride is bool own)
                    Msg($"Pet assist is currently {(own ? "on" : "off")} (your own setting). Use /summonattackontarget {(own ? "off" : "on")} to turn it {(own ? "off" : "on")}.");
                else
                    Msg("Pet assist is currently on (default). Use /summonattackontarget off to turn it off.");

                return;
            }

            switch (parameters[0].ToLowerInvariant())
            {
                case "on":
                    player.SummonAttackOnTarget = true;
                    player.RushNextPlayerSave(5);
                    Msg("Pet assist is now on. Your combat pets will attack the monster you most recently hit.");
                    break;

                case "off":
                    player.SummonAttackOnTarget = false;
                    player.RushNextPlayerSave(5);
                    Msg("Pet assist is now off. Your combat pets will pick their own targets.");
                    break;

                default:
                    Msg("Usage: /summonattackontarget on | off");
                    break;
            }
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

        /// <summary>
        /// Resend every monster and NPC this player knows about, without deleting them first.
        ///
        /// Unlike /objsend this emits no GameMessageDeleteObject, so client plugins that hook object
        /// destruction do not see the whole scene churn. It also means the command can only repair the
        /// case where the client dropped an object the server still tracks - it cannot hand the client
        /// knowledge the server never had.
        ///
        /// Players are excluded deliberately: this is aimed at the invisible-monster bug, and leaving
        /// them out keeps the resend small in town. Use /objsend for anything wider.
        /// </summary>
        [CommandHandler("fi", AccessLevel.Player, CommandHandlerFlag.RequiresWorld, "Resends nearby monsters and NPCs to your client, to fix invisible creature bugs. Can be used once per minute.")]
        [CommandHandler("fixinvisible", AccessLevel.Player, CommandHandlerFlag.RequiresWorld, "Resends nearby monsters and NPCs to your client, to fix invisible creature bugs. Can be used once per minute.")]
        public static void HandleFixInvisible(Session session, params string[] parameters)
        {
            // RequiresWorld only checks CurrentLandblock, and Landblock.AddWorldObjectInternal sets
            // that before PhysicsObj is (re)initialized, so ObjMaint can still be unreachable here.
            // Player.OnTalk guards the same ObjMaint call the same way.
            if (session.Player.PhysicsObj == null)
                return;

            if (DateTime.UtcNow - session.Player.PrevFixInvisible < TimeSpan.FromMinutes(1))
            {
                session.Player.SendTransientError("You have used this command too recently!");
                return;
            }

            var resent = 0;

            foreach (var creature in session.Player.ObjMaint.GetKnownObjectsValuesAsCreature())
            {
                if (creature is Player)
                    continue;

                // mirrors TrackObject's own guard, so the count below matches what was actually sent
                if (creature.Visibility && !session.Player.Adminvision)
                    continue;

                session.Player.TrackObject(creature, resend: true);
                resent++;
            }

            session.Player.PrevFixInvisible = DateTime.UtcNow;

            session.Network.EnqueueSend(new GameMessageSystemChat($"Resent {resent} creature{(resent == 1 ? "" : "s")} to your client.", ChatMessageType.Broadcast));
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

        [CommandHandler("top", AccessLevel.Player, CommandHandlerFlag.RequiresWorld, "Show a leaderboard (top 20).", "dps | defense | wave | speed | speed winners | level | bank | cap | stamp | thread | 1v1 | 2v2 | tugak | bg")]
        public static void HandleTop(Session session, params string[] parameters)
        {
            var player = session.Player;
            if (player == null)
                return;

            var sub = parameters.Length > 0 ? parameters[0].ToLowerInvariant() : "";

            switch (sub)
            {
                // Proving Grounds boards: one best-ever score per arena.
                case "dps":
                {
                    var board = LeaderboardBoardsByKey["dps"];
                    SendLeaderboard(session, board.Title, board.Score, board.Format);
                    return;
                }

                case "defense":
                {
                    var board = LeaderboardBoardsByKey["defense"];
                    SendLeaderboard(session, board.Title, board.Score, board.Format);
                    return;
                }

                case "wave":
                {
                    var board = LeaderboardBoardsByKey["wave"];
                    SendLeaderboard(session, board.Title, board.Score, board.Format, board.TieBreaker, detail: board.Detail);
                    return;
                }

                // Speed does NOT go through SendLeaderboard: that method hardcodes OrderByDescending and a
                // score > 0 filter over PlayerManager, so it can neither serve a lower-is-better board nor
                // read the speed board cache at all (DESIGN section 6). Its own renderers below reuse every
                // one of SendLeaderboard's player-visible conventions instead, so the boards look identical.
                case "speed":
                    if (parameters.Length > 1 && string.Equals(parameters[1], "winners", StringComparison.OrdinalIgnoreCase))
                        SendSpeedWinners(session);
                    else
                        SendSpeedLeaderboard(session);
                    return;

                // Character-progress boards. Level ties break on lifetime xp, so characters that share a level
                // are ordered by how far into it they are rather than arbitrarily.
                case "level":
                {
                    var board = LeaderboardBoardsByKey["level"];
                    SendLeaderboard(session, board.Title, board.Score, board.Format, board.TieBreaker);
                    return;
                }

                // The one board that is per ACCOUNT rather than per character: the bank is account-shared,
                // so listing every character would let one account hold the same pile of money in several
                // places on the board at once.
                //
                // The balance is ONE account-wide pool (account_bank, via AccountBankManager), not a
                // per-character property, so every character on an account scores the IDENTICAL number.
                // "The highest-level character's balance", "the account's sum" and "its richest
                // character" are therefore all the same figure here and the collapse is not choosing
                // between them - what it is doing is stopping one account occupying twenty rows with
                // twenty copies of one balance.
                case "bank":
                {
                    // Taken ONCE per render rather than per player inside the score lambda: one shared
                    // snapshot, 30s TTL, so a burst of /top bank costs at most one query.
                    var balances = AccountBankManager.GetAllBalances();

                    // An empty board and a broken database must not look the same to a player.
                    if (balances == null)
                    {
                        session.Network.EnqueueSend(new GameMessageSystemChat("Bank balances are unavailable right now, try again shortly.", ChatMessageType.System));
                        return;
                    }

                    SendLeaderboard(session, "Banked Pyreals",
                        p => balances.TryGetValue(AccountLeaderboard.AccountKeyFor(p), out var banked) ? banked : 0,
                        pyreals => $"{pyreals:N0} pyreals",
                        accountKey: AccountLeaderboard.AccountKeyFor);
                    return;
                }

                case "cap":
                {
                    if (!PropertyManager.GetBool("class_abilities_enabled").Item)
                    {
                        session.Network.EnqueueSend(new GameMessageSystemChat("Class abilities are not currently enabled on this server.", ChatMessageType.System));
                        return;
                    }

                    // lifetime earned, never the unspent pool - spending points must not cost a character its place
                    var board = LeaderboardBoardsByKey["cap"];
                    SendLeaderboard(session, board.Title, board.Score, board.Format);
                    return;
                }

                case "stamp":
                case "stamps":
                {
                    if (!PropertyManager.GetBool("quest_stamps_enabled").Item)
                    {
                        session.Network.EnqueueSend(new GameMessageSystemChat("Quest stamps are not currently enabled on this server.", ChatMessageType.System));
                        return;
                    }

                    // per character, not per account: QuestStampCount is the persisted per-character count of
                    // quests that character has been stamped for, while the account-wide total the Registrar
                    // rewards against counts DISTINCT quests across the account and is ephemeral - it only
                    // exists for a character that is logged in right now
                    var board = LeaderboardBoardsByKey["stamp"];
                    SendLeaderboard(session, board.Title, board.Score, board.Format);
                    return;
                }

                case "thread":
                case "threads":
                {
                    // gate read from the board so the command and the character sheet cannot diverge
                    var board = LeaderboardBoardsByKey["thread"];

                    if (!PropertyManager.GetBool(board.FeatureGate).Item)
                    {
                        session.Network.EnqueueSend(new GameMessageSystemChat("Threads are not currently enabled on this server.", ChatMessageType.System));
                        return;
                    }

                    // rung first, lifetime Thread clears break ties; both are persisted per-character PropertyInts
                    SendLeaderboard(session, board.Title, board.Score, board.Format, board.TieBreaker, detail: board.Detail);
                    return;
                }

                // PvP arena ladders, ranked by rating: the same board /arena top prints, reachable here
                // because /top is where players look for leaderboards. "/top 1v1 [count]" passes through.
                case var ladder when PvpArenaCommands.IsLadderWord(ladder):
                    PvpArenaCommands.HandleArenaTop(player, parameters, PvpArenaCommands.TopCommandUsage);
                    return;
            }

            // Bare /top lists the boards instead of silently defaulting to one of them.
            var usage = "=== Leaderboards ===\n"
                + "/top dps - Attack arena: highest damage dealt in the 60s trial\n"
                + "/top defense - Defense arena: longest survival against the Squall\n"
                + "/top wave - Wave gauntlet: highest wave reached (cleared waves + % of the next wave's health destroyed)\n"
                + "/top speed - Speed trial: fastest run of the current season's dungeon\n"
                + "/top speed winners - Speed trial: the winner of each of the 20 most recent seasons\n"
                + "/top level - Highest character level\n"
                + "/top bank - Most pyreals banked (one entry per account, the highest-level character)\n"
                + "/top cap - Most class ability points earned, lifetime\n"
                + "/top stamp - Most quest stamps earned by a single character\n"
                + "/top thread - Highest Thread-Guide rung won, ties broken by lifetime Thread clears\n"
                + "/top 1v1 | 2v2 | tugak | bg - PvP arena ladder, ranked by rating\n";
            session.Network.EnqueueSend(new GameMessageSystemChat(usage, ChatMessageType.System));
        }

        private static readonly Dictionary<string, LeaderboardBoard> LeaderboardBoardsByKey =
            LeaderboardRanking.Boards.ToDictionary(b => b.Key);

        /// <summary>
        /// Renders one leaderboard: the top <see cref="LeaderboardRanking.LeaderboardSize"/> by <paramref name="score"/>, with the
        /// requesting player's own standing appended when they rank below the cut. Only positive scores are
        /// listed, so a character that has never scored on a board simply does not appear on it.
        ///
        /// Every board is served from PlayerManager's in-memory player list (online Player objects plus the
        /// offlinePlayers dictionary loaded at boot), so none of this reads the shard database - which also means
        /// a raw SQL edit to a scored property stays invisible until the next server restart.
        ///
        /// Staff characters/accounts are filtered out via LeaderboardExemptionManager.IsExempt before ranking, so
        /// they never appear at the top of a board a player is looking at. An exempt requester still has their
        /// own-standing append no-op (FindIndex returns -1 for a filtered-out player), so a line is appended below
        /// telling them why their scores are not listed instead of leaving it looking broken.
        ///
        /// Passing <paramref name="accountKey"/> makes the board one-entry-per-account instead of per-character;
        /// see the call site comment below for the ordering that implies. Leaving it null is the per-character
        /// board every existing board uses, and takes exactly the path it always has.
        /// </summary>
        private static void SendLeaderboard(Session session, string title, Func<IPlayer, long> score, Func<long, string> format, Func<IPlayer, long> tieBreaker = null, Func<IPlayer, uint> accountKey = null, Func<IPlayer, string> detail = null)
        {
            tieBreaker ??= _ => 0;

            var exemptNames = LeaderboardExemptionManager.GetExemptAccountNames();

            // Collapse to one row per account before ranking, so an account cannot take several places on
            // the board with what is really one balance seen from several characters.
            //
            // The score filter runs after the collapse, so the representative is picked by level without
            // reference to the score. On today's only account board that ordering has no observable
            // effect - the balance is one shared pool, so every character on an account scores the same
            // number - and it is kept because it is the ordering that generalises: a per-character score
            // collapsed this way is represented by the account's main rather than by whichever alt
            // happened to score, so the listed name does not shuffle as balances move.
            //
            // Exemption is applied first either way, so an exempt character can never become the
            // representative of an account that would otherwise have been listed.
            var ranked = LeaderboardRanking.Rank(
                PlayerManager.GetAllPlayers(),
                p => LeaderboardExemptionManager.IsExempt(p, exemptNames),
                score,
                tieBreaker,
                accountKey,
                p => p.Level ?? 0,
                p => p.GetProperty(PropertyInt64.TotalExperience) ?? 0,
                p => p.Guid.Full);

            var msg = $"=== {title} Leaderboard (Top {LeaderboardRanking.LeaderboardSize}) ===\n";

            // resolved before the empty-board early return, so an exempt requester sees the notice
            // even on a board nobody has scored on yet, not just once the board has other entries
            var requesterExempt = LeaderboardExemptionManager.IsExempt(session.Player, exemptNames);

            if (ranked.Count == 0)
            {
                msg += "No scores have been recorded yet.\n";
                if (requesterExempt)
                    msg += "You are exempt from the leaderboards, so your own scores are not listed.\n";
                session.Network.EnqueueSend(new GameMessageSystemChat(msg, ChatMessageType.System));
                return;
            }

            for (var i = 0; i < ranked.Count && i < LeaderboardRanking.LeaderboardSize; i++)
                msg += $"{i + 1}. {ranked[i].Name} - {format(score(ranked[i]))}{detail?.Invoke(ranked[i])}\n";

            // if the requesting player has a score but sits outside the top 20, append their own standing.
            // On an account-collapsed board the requester is often NOT their own account's representative,
            // so the row to find is the one carrying their account key, not their own guid - otherwise a
            // player looking at /top bank on an alt would be told they are unranked while their account
            // sits at 40th. That row is labelled "(your account)" rather than "(you)" so the name shown
            // next to the rank is never mistaken for the character who typed the command.
            var myRank = accountKey != null
                ? ranked.FindIndex(p => accountKey(p) == accountKey(session.Player))
                : ranked.FindIndex(p => p.Guid == session.Player.Guid);

            if (myRank >= LeaderboardRanking.LeaderboardSize)
            {
                var isSelf = ranked[myRank].Guid == session.Player.Guid;
                var whose = isSelf ? "(you)" : "(your account)";
                var name = isSelf ? session.Player.Name : ranked[myRank].Name;
                msg += $"...\n{myRank + 1}. {name} {whose} - {format(score(ranked[myRank]))}{detail?.Invoke(ranked[myRank])}\n";
            }

            if (requesterExempt)
                msg += "You are exempt from the leaderboards, so your own scores are not listed.\n";

            session.Network.EnqueueSend(new GameMessageSystemChat(msg, ChatMessageType.System));
        }

        /// <summary>
        /// Renders /top speed: one season's fastest runs, ASCENDING (lower is better).
        ///
        /// Deliberately not SendLeaderboard. That renderer ranks PlayerManager's in-memory players by a biota
        /// property, descending; this board is the opposite on both counts - it is served from
        /// SpeedBoardManager's cache, which is DERIVED FROM the authoritative `character_speed_run` shard
        /// table (DESIGN section 3.5), and lower times rank higher. Every player-visible convention is copied
        /// across so the two boards read identically: the "=== ... Leaderboard (Top N) ===" header, the
        /// LeaderboardSize cut, "{rank}. {Name} - {value}" lines, the "..." own-standing append, the
        /// "No scores have been recorded yet." empty case, and the exempt-requester notice resolved BEFORE
        /// the empty-board early return so it shows even on an empty board.
        ///
        /// A board entry whose character cannot be resolved (deleted since the run) is NOT exempt and stays
        /// on the board, and the SNAPSHOT name recorded at completion time is what renders - never a live
        /// lookup. The table deliberately outlives the character, which is the entire reason it snapshots the
        /// name (CharacterSpeedRunPartial.cs).
        /// </summary>
        private static void SendSpeedLeaderboard(Session session)
        {
            var season = SpeedSeasonManager.GetActiveSeason();

            // A gap between rotations is an entirely normal state (DESIGN section 3.2), so rather than show
            // nothing, fall back to the most recent season and mark the header as ended.
            var seasonIsActive = season != null;

            if (season == null)
                season = SpeedSeasonManager.GetRecentSeasons(1).FirstOrDefault();

            if (season == null)
            {
                session.Network.EnqueueSend(new GameMessageSystemChat("No speed trial season has been run yet.", ChatMessageType.System));
                return;
            }

            var exemptNames = LeaderboardExemptionManager.GetExemptAccountNames();

            var ranked = LeaderboardRanking.RankSpeed(
                SpeedBoardManager.GetSeasonBoard(season.Id),
                e => e.Centiseconds,
                e =>
                {
                    var player = PlayerManager.FindByGuid(e.CharacterId);
                    return player != null && LeaderboardExemptionManager.IsExempt(player, exemptNames);
                });

            var msg = $"=== {LeaderboardRanking.SpeedSeasonTitle(season)} Leaderboard (Top {LeaderboardRanking.LeaderboardSize}) ===\n";

            if (!seasonIsActive)
                msg += "That season has ended. No speed trial season is currently running.\n";

            // resolved before the empty-board early return, so an exempt requester sees the notice
            // even on a board nobody has scored on yet, not just once the board has other entries
            var requesterExempt = LeaderboardExemptionManager.IsExempt(session.Player, exemptNames);

            if (ranked.Count == 0)
            {
                msg += "No scores have been recorded yet.\n";
                if (requesterExempt)
                    msg += "You are exempt from the leaderboards, so your own scores are not listed.\n";
                session.Network.EnqueueSend(new GameMessageSystemChat(msg, ChatMessageType.System));
                return;
            }

            for (var i = 0; i < ranked.Count && i < LeaderboardRanking.LeaderboardSize; i++)
                msg += $"{i + 1}. {ranked[i].CharacterName} - {Player.FormatSpeedRunTime(ranked[i].Centiseconds)}\n";

            // Character.Id is the player's Guid.Full (set in the Player constructor), so this matches the
            // requester against their own board line without a name comparison.
            var myRank = ranked.FindIndex(e => e.CharacterId == session.Player.Guid.Full);
            if (myRank >= LeaderboardRanking.LeaderboardSize)
                msg += $"...\n{myRank + 1}. {session.Player.Name} (you) - {Player.FormatSpeedRunTime(ranked[myRank].Centiseconds)}\n";

            if (requesterExempt)
                msg += "You are exempt from the leaderboards, so your own scores are not listed.\n";

            session.Network.EnqueueSend(new GameMessageSystemChat(msg, ChatMessageType.System));
        }

        /// <summary>
        /// Renders /top speed winners: one line per season for the LeaderboardSize most recent seasons,
        /// newest first, naming that season's winner and time.
        ///
        /// This is a history of SEASONS, not of winners, so a season nobody has completed still gets its
        /// line with the winner shown as unclaimed rather than being skipped - otherwise the list would
        /// silently misrepresent how many seasons have run.
        ///
        /// The winner comes from SpeedBoardManager.GetSeasonLeader, the same method the record-broadcast bar
        /// reads, so the name listed here and the time treated as that season's record can never disagree.
        /// </summary>
        private static void SendSpeedWinners(Session session)
        {
            var seasons = SpeedSeasonManager.GetRecentSeasons(LeaderboardRanking.LeaderboardSize);

            if (seasons.Count == 0)
            {
                session.Network.EnqueueSend(new GameMessageSystemChat("No speed trial season has been run yet.", ChatMessageType.System));
                return;
            }

            var exemptNames = LeaderboardExemptionManager.GetExemptAccountNames();

            var msg = $"=== Speed Trial Winners (Last {LeaderboardRanking.LeaderboardSize} Seasons) ===\n";

            for (var i = 0; i < seasons.Count; i++)
            {
                var season = seasons[i];
                var leader = SpeedBoardManager.GetSeasonLeader(season.Id, exemptNames);

                var winner = leader == null
                    ? "unclaimed"
                    : $"{leader.CharacterName} - {Player.FormatSpeedRunTime(leader.Centiseconds)}";

                msg += $"{i + 1}. {LeaderboardRanking.SpeedSeasonTitle(season)} - {winner}\n";
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
            "/b d pea             - deposit peas (lead through pyreal) as pyreals, at face value\n" +
            "/b w p <amount>      - withdraw pyreals (as 250k notes + coins)\n" +
            "/b w n [value] <cnt> - withdraw trade notes (value defaults to 250000)\n" +
            "/b w k <uses>       - withdraw legendary keys; the amount is in USES, paid as 10-use Durable keys + 1-use Aged keys\n" +
            "/b w pn <amount>     - withdraw promissory notes\n" +
            "/b t p|k|pn <amount> <char> - transfer to another character\n" +
            "/b ad [on|off]       - vendor sale proceeds to the bank (on) or as coins in your pack (off)\n" +
            "/b                   - show balances\n" +
            "Luminance can be deposited but not withdrawn or transferred.")]
        public static void HandleBank(Session session, params string[] parameters)
        {
            var player = session.Player;
            if (player == null)
                return;

            // PvP template (economy lock): no banking while templated.
            if (player.PvpTemplateRefuses(ACE.Server.Pvp.Templates.PvpTemplateAction.Vault))
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
            session.Network.EnqueueSend(new GameMessageSystemChat($"[BANK] Pyreals (shared by every character on this account): {player.BankedPyreals:N0}", ChatMessageType.System));
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
            session.Network.EnqueueSend(new GameMessageSystemChat("/b d pea             - bank peas (lead through pyreal) as pyreals at face value", ChatMessageType.System));
            session.Network.EnqueueSend(new GameMessageSystemChat("/b w p <amount>      - withdraw pyreals (as 250k notes + coins)", ChatMessageType.System));
            session.Network.EnqueueSend(new GameMessageSystemChat("/b w n [value] <cnt> - withdraw trade notes (value defaults to 250000)", ChatMessageType.System));
            session.Network.EnqueueSend(new GameMessageSystemChat("/b w k <uses>       - withdraw legendary keys; the amount is in USES, paid as 10-use Durable keys + 1-use Aged keys", ChatMessageType.System));
            session.Network.EnqueueSend(new GameMessageSystemChat("/b w pn <amount>     - withdraw promissory notes", ChatMessageType.System));
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

        /// <summary>
        /// "/tn" - recall to the traditional Town Network, the retail hall of town portals.
        /// The /dn counterpart, landing in the same hall in realm 0 instead of realm 1.
        /// </summary>
        [CommandHandler("tn", AccessLevel.Player, CommandHandlerFlag.RequiresWorld, 0,
            "Recall to the Town Network.",
            "Begins a recall to the traditional Town Network. Like /mp, this takes a few seconds and is\n"
            + "interrupted if you move too far.")]
        public static void HandleTownNetworkRecall(Session session, params string[] parameters)
        {
            session.Player.HandleActionTeleToTownNetwork();
        }

        /// <summary>
        /// "/summondamage on|off" - the per-character summon damage feed. When on, the player gets one chat
        /// line for every hit their OWN summoned pets land. Other players' summons are never reported: the
        /// message is only ever addressed to the pet's own owner (Pet.NotifyOwnerOfDamage).
        ///
        /// Off by default. Touches no items, so it sits outside any mutating-command gate. A change rushes
        /// the next save, because a toggle that silently reverts in a crash window is worse than one that
        /// never applied - the player has no way to tell the two apart.
        /// </summary>
        [CommandHandler("summondamage", AccessLevel.Player, CommandHandlerFlag.RequiresWorld, 0,
            "Show or hide a chat line for each hit your own summoned pets land.",
            "[on | off]\n"
            + "With no argument, reports the current setting. Off by default. Only your own summons are\n"
            + "reported - you never see damage dealt by anyone else's pets.")]
        public static void HandleSummonDamage(Session session, params string[] parameters)
        {
            var player = session.Player;

            void Msg(string text) => session.Network.EnqueueSend(new GameMessageSystemChat(text, ChatMessageType.Broadcast));

            var arg = parameters.Length > 0 ? parameters[0].ToLowerInvariant() : "";

            switch (arg)
            {
                case "":
                    Msg(player.SummonDamageMessages
                        ? "Summon damage messages are ON. Use /summondamage off to hide them."
                        : "Summon damage messages are OFF. Use /summondamage on to show them.");
                    break;

                case "on":
                case "true":
                case "1":
                    player.SummonDamageMessages = true;
                    player.RushNextPlayerSave(5);
                    Msg("Summon damage messages are now ON. You will see each hit your own summons land.");
                    break;

                case "off":
                case "false":
                case "0":
                    player.SummonDamageMessages = false;
                    player.RushNextPlayerSave(5);
                    Msg("Summon damage messages are now OFF.");
                    break;

                default:
                    Msg("Usage: /summondamage [on|off]");
                    break;
            }
        }
    }
}
