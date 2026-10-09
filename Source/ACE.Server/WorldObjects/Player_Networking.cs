using System;

using ACE.Common;
using ACE.Common.Extensions;
using ACE.Database;
using ACE.Database.Models.Shard;
using ACE.Entity;
using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Server.Entity;
using ACE.Server.Entity.Actions;
using ACE.Server.Managers;
using ACE.Server.Network.Enum;
using ACE.Server.Network.GameEvent.Events;
using ACE.Server.Network.GameMessages.Messages;
using ACE.Server.Network.Sequence;
using ACE.Server.Network.Structure;

namespace ACE.Server.WorldObjects
{
    partial class Player
    {
        public void PlayerEnterWorld()
        {
            PlayerManager.SwitchPlayerFromOfflineToOnline(this);
            Teleporting = true;

            // Save the LoginTimestamp
            var lastLoginTimestamp = Time.GetUnixTime();

            LoginTimestamp = lastLoginTimestamp;
            LastTeleportStartTimestamp = lastLoginTimestamp;

            // pending-teleport diagnostics: a login waits on the same LoginComplete a teleport does (see Player_TeleportWatch).
            // Opened here, next to the stamp; the baseline is taken after SendSelf() below.
            OpenTeleportWatch(TeleportWatchRules.ClassifyKind(lastLoginTimestamp, LastPortalTeleportTimestamp, LoginTimestamp), false,
                Location?.Cell ?? 0, Location?.Instance ?? 0);

            // The /lph window opens at login and is reset by /lph start.
            ResetLumRateWindow();

            Character.LastLoginTimestamp = lastLoginTimestamp;
            Character.TotalLogins++;
            CharacterChangesDetected = true;

            // Bank offline bonus time for however long this character was logged out (reads the prior
            // session's LogoffTimestamp, which is not reset until this session's logout), and anchor the
            // online drain clock. Must run before LogoffTimestamp is touched again this session.
            AccrueOfflineBonus();

            // Cache the alt character bonus catch-up target (highest enlightenment+level on this account) for
            // the duration of this login. Only one character per account is online at a time, so the target is
            // fixed until logout - only this character's own progression changes as it levels. See
            // Player_AltCharacterBonus.
            InitAltCharacterBonus();

            // Backfill this character's quest stamp count if it predates the feature, and compute the
            // account-wide total for the session. Same one-character-per-account assumption as above: the
            // other characters' counts are frozen until this one logs out. See Player_QuestStamps.
            InitQuestStamps();

            // Warm this account's pooled pyreal balance and fold in any legacy per-character balance
            // this character still carries. MUST run before SendSelf() below: GameEventPlayerDescription
            // snapshots GetSpendableCoinValue, which reads BankedPyreals, so a cold pool would put the
            // session's first coin figure at 0. See Player_Bank.InitAccountBank.
            InitAccountBank();

            // Warm AccountCapacityUpgradeManager's cache for both capacity upgrade kinds so the FIRST
            // /mule upgrade or /market upgrade this session runs does not pay a cold-cache shard read.
            // GetCount is safe to call speculatively - a failed read is logged and simply retried on the
            // next call, never cached as 0 (see AccountCapacityUpgradeManager's own remarks).
            var accountId = Account?.AccountId ?? 0;

            if (accountId != 0)
            {
                AccountCapacityUpgradeManager.GetCount(accountId, CapacityUpgradeKind.MuleVault);
                AccountCapacityUpgradeManager.GetCount(accountId, CapacityUpgradeKind.MarketListings);
            }

            Sequences.SetSequence(SequenceType.ObjectInstance, new UShortSequence((ushort)Character.TotalLogins));

            if (BarberActive)
                BarberActive = false;

            if (AllegianceNode != null)
                AllegianceRank = (int)AllegianceNode.Rank;
            else
                AllegianceRank = null;

            if (!Account15Days)
            {
                var accountAge = DateTime.UtcNow - Account.CreateTime;

                if (accountAge.TotalDays >= 15)
                    Account15Days = true;

                ManageAccount15Days_HousePurchaseTimestamp();

                if (!Account15Days && IsOlthoiPlayer)
                    Session.Network.EnqueueSend(new GameMessageSystemChat("You may not leave Olthoi Island until your account and this character have been active on this game world for 15 days.", ChatMessageType.Broadcast));
            }

            if (PlayerKillerStatus == PlayerKillerStatus.PKLite && !PropertyManager.GetBool("pkl_server").Item)
            {
                PlayerKillerStatus = PlayerKillerStatus.NPK;

                var actionChain = new ActionChain();
                actionChain.AddDelaySeconds(3.0f);
                actionChain.AddAction(this, () =>
                {
                    Session.Network.EnqueueSend(new GameEventWeenieError(Session, WeenieError.YouAreNonPKAgain));
                });
                actionChain.EnqueueChain();
            }

            // Facets: apply the PK facet rule before the character enters the world (Player_FacetPk.cs).
            FacetPkLoginCheck();

            HandlePreOrderItems();

            // SendSelf will trigger the entrance into portal space
            SendSelf();

            // pending-teleport diagnostics: baseline after the enter-world sends, so only later self-position sends count
            // as extra. Keyed on the login stamp, so a teleport started during enter-world keeps its own baseline.
            BaselineTeleportWatch(lastLoginTimestamp, false, 0, 0);

            // Update or override certain properties sent to client.

            // bugged: do not send this here, or else a freshly loaded acclient will overrwrite the values
            // wait until first enter world is completed

            //SendPropertyUpdatesAndOverrides();

            if (PropertyManager.GetBool("use_turbine_chat").Item)
            {
                // Init the client with the chat channel ID's, and then notify the player that they've joined the associated channels.
                Session.Network.EnqueueSend(new GameEventWeenieError(Session, WeenieError.TurbineChatIsEnabled));

                if (IsOlthoiPlayer)
                {
                    JoinTurbineChatChannel("Olthoi");
                }
                else
                {
                    if (GetCharacterOption(CharacterOption.ListenToAllegianceChat) && Allegiance != null)
                        JoinTurbineChatChannel("Allegiance");
                    if (GetCharacterOption(CharacterOption.ListenToGeneralChat))
                        JoinTurbineChatChannel("General");
                    if (GetCharacterOption(CharacterOption.ListenToTradeChat))
                        JoinTurbineChatChannel("Trade");
                    if (GetCharacterOption(CharacterOption.ListenToLFGChat))
                        JoinTurbineChatChannel("LFG");
                    if (GetCharacterOption(CharacterOption.ListenToRoleplayChat))
                        JoinTurbineChatChannel("Roleplay");
                    if (GetCharacterOption(CharacterOption.ListenToSocietyChat) && Society != FactionBits.None)
                        JoinTurbineChatChannel("Society");
                }
            }

            // check if vassals earned XP while offline
            HandleAllegianceOnLogin();
            HandleHouseOnLogin();

            // let the player know if they have offline bonus time banked from being logged out
            if (PropertyManager.GetBool("offline_bonus_enabled").Item && GetOfflineExperienceBonusRemaining() > 0)
            {
                var actionChain = new ActionChain();
                actionChain.AddDelaySeconds(3.0f);
                actionChain.AddAction(this, () => ShowOfflineExperienceBonusStatus());
                actionChain.EnqueueChain();
            }

            // retail appeared to send the squelch list very early,
            // even before the CreatePlayer, but doing it here
            if (SquelchManager.HasSquelches)
                SquelchManager.SendSquelchDB();

            AuditItemSpells();
            AuditEquippedItems();

            HandleMissingXp();

            // pay out any level-milestone class ability points not yet granted (retroactive for existing
            // characters, immune to missed level-ups) - DESIGN.md sec 2a
            GrantMilestoneClassAbilityPoints();

            // catch up the facet slot-2 unlock notice for a character who was already eligible before
            // this login (retroactive, same idempotent shape as the milestone grant above)
            SendFacetUnlockNoticeIfDue();

            // catch up the one-time Thread-Guide notice for a character already at level 50 or more
            SendThreadGuideNoticeIfDue();

            // once per login: point a player left without a guide fragment back to the Thread-Guide
            SendThreadGuideLoginReminderIfDue();

            // queue (never await) the stored facet rows for the out-of-reach warning, so it works from the
            // first spend after login without a blocking shard read on the raise path
            BeginFacetStoredSummaryLoad();

            // pay out any enlightenment-milestone class ability points not yet granted. Enlightenment is
            // RETIRED, so this entitlement is frozen rather than growing - it stays only to finish paying
            // characters who were mid-catch-up when the system was retired. DESIGN.md sec 2c.
            GrantEnlightenmentClassAbilityPoints();

            // return the experience the retired enlightenment system consumed, as levels and unassigned xp
            // (Docs/ClassAbilities/XP-LANE-SPEC.md sec 5.2). Idempotent floor; no-op for the unenlightened.
            GrantEnlightenmentRetirementCredit();

            // refund class ability points orphaned by a since-retired ability (e.g. Advanced Weaponry,
            // Questionable Tactics) and clean up the dead quest registry row - runs regardless of
            // class_abilities_enabled, unlike the two grants above
            SweepRetiredClassAbilities();

            // Class Ability overhaul deployment migration: the one-shot forced full respec. Runs AFTER
            // SweepRetiredClassAbilities, which owns the quest rows this one deliberately will not
            // touch (the unresolvable ones it prices from the retired table), and BEFORE the audit
            // below, which has to measure state that has stopped changing. Per-character one-shot
            // guard (PropertyInt 9067) plus a server-wide kill switch, so a mid-deployment abort is
            // benign and the next login continues.
            ApplyClassAbilityOverhaulRespec();

            // CAP audit ledger, round 3: detect a ledger/counter mismatch at login. Must run AFTER the
            // three calls above - they mutate the CAP counters and/or the quest-registry rows this
            // audit reads, so auditing before them would measure a state about to change. Read-only;
            // completes asynchronously (queued shard read) and never blocks login.
            AuditClassAbilityPointLedger();

            HandleSkillCreditRefund();
            HandleSkillTemplesReset();
            HandleSkillSpecCreditRefund();
            HandleFreeSkillResetRenewal();
            HandleFreeAttributeResetRenewal();
            HandleFreeMasteryResetRenewal();

            HandleDBUpdates();

            // ML Relaria repeat-kill aura (Player_RelariaAura.cs): re-arms the pulse if this character has
            // earned it and has not opted out. The pulse itself is never persisted - only the quest stamp
            // and the on/off preference are - so every login needs to re-start it from scratch.
            ArmRelariaAuraPulseIfEligible();

            // Tally of the Unburied's 100-charge glow (Player_RelariaTallyGlow.cs): same reasoning,
            // independent pulse, no opt-out preference.
            ArmRelariaTallyGlowPulseIfEligible();

            if (ServerManager.ShutdownInitiated)
            {
                var actionChain = new ActionChain();
                actionChain.AddDelaySeconds(10.0f);
                actionChain.AddAction(this, () =>
                {
                    SendMessage(ServerManager.ShutdownNoticeText(), ChatMessageType.WorldBroadcast);
                });
                actionChain.EnqueueChain();
            }

            log.DebugFormat("[LOGIN] Account {0} entered the world with character {1} (0x{2}) at {3}.", Account.AccountName, Name, Guid, DateTime.Now.ToCommonString());
        }

        public void SendTurbineChatChannels(bool breakAllegiance = false)
        {
            var allegianceChannel = Allegiance != null && !breakAllegiance ? Allegiance.Biota.Id : 0u;

            var societyChannel = Society switch
            {
                FactionBits.CelestialHand => TurbineChatChannel.SocietyCelestialHand,
                FactionBits.EldrytchWeb => TurbineChatChannel.SocietyEldrytchWeb,
                FactionBits.RadiantBlood => TurbineChatChannel.SocietyRadiantBlood,
                _ => 0u
            };

            Session.Network.EnqueueSend(new GameEventSetTurbineChatChannels(Session, allegianceChannel, societyChannel));
        }

        public void JoinTurbineChatChannel(string channelName)
        {
            if (channelName == "Allegiance" && Allegiance == null)
                return;
            else if (channelName == "Society")
            {
                if (Society == FactionBits.None)
                    return;

                channelName = Society switch
                {
                    FactionBits.CelestialHand => "Celestial Hand",
                    FactionBits.EldrytchWeb => "Eldrytch Web",
                    FactionBits.RadiantBlood => "Radiant Blood",
                    _ => channelName
                };
            }
            else if (channelName == "Olthoi" && !IsOlthoiPlayer)
                return;

            Session.Network.EnqueueSend(new GameEventWeenieErrorWithString(Session, WeenieErrorWithString.YouHaveEnteredThe_Channel, channelName));

            SendTurbineChatChannels();
        }

        public void LeaveTurbineChatChannel(string channelName, bool breakAllegiance = false)
        {
            if (channelName == "Allegiance" && !breakAllegiance && Allegiance == null)
                return;
            else if (channelName == "Society")
            {
                if (Society == FactionBits.None)
                    return;

                channelName = Society switch
                {
                    FactionBits.CelestialHand => "Celestial Hand",
                    FactionBits.EldrytchWeb => "Eldrytch Web",
                    FactionBits.RadiantBlood => "Radiant Blood",
                    _ => channelName
                };
            }
            else if (channelName == "Olthoi" && (!IsOlthoiPlayer || !IsAdmin))
                return;
            else if (IsOlthoiPlayer && !IsAdmin && channelName != "Olthoi")
                return;

            Session.Network.EnqueueSend(new GameEventWeenieErrorWithString(Session, WeenieErrorWithString.YouHaveLeftThe_Channel, channelName));

            SendTurbineChatChannels(breakAllegiance);
        }

        private void SendSelf()
        {
            var player = new GameEventPlayerDescription(Session);
            var title = new GameEventCharacterTitle(Session);
            var friends = new GameEventFriendsListUpdate(Session);

            Session.Network.EnqueueSend(player, title, friends);

            // Player objects don't get a placement
            Placement = null;
            Session.Network.EnqueueSend(new GameMessagePlayerCreate(Guid), new GameMessageCreateObject(this));

            // Relog is the case the probe exists to fix: the client rebuilds everything here and any
            // previously-sent particle script is gone unless it is re-emitted. See VisualEffectManager.
            VisualEffectManager.SendTo(Session, this);

            SendInventoryAndWieldedItems();

            SendContractTrackerTable();
        }

        public void SendPropertyUpdatesAndOverrides()
        {
            if (!PropertyManager.GetBool("require_spell_comps").Item)
                Session.Network.EnqueueSend(new GameMessagePrivateUpdatePropertyBool(this, PropertyBool.SpellComponentsRequired, false));
        }

        /// <summary>
        /// This method iterates through your main pack, any packs and finds all the items contained
        /// It also iterates over your wielded items - it sends create object messages needed by the login process
        /// it is called from SendSelf as part of the login message traffic.   Og II
        /// </summary>
        public void SendInventoryAndWieldedItems()
        {
            // Pack contents get no visual effect: they are not drawn, so a script sent here would
            // attach to nothing AND consume the once-per-lifetime send that equipping needs.
            foreach (var item in Inventory.Values)
            {
                Session.Network.EnqueueSend(new GameMessageCreateObject(item));

                // Was the item I just send a container? If so, we need to send the items in the container as well. Og II
                if (item is Container container)
                {
                    Session.Network.EnqueueSend(new GameEventViewContents(Session, container));

                    foreach (var itemsInContainer in container.Inventory.Values)
                        Session.Network.EnqueueSend(new GameMessageCreateObject(itemsInContainer));
                }
            }

            // Equipped items ARE drawn, so these do get their effect.
            foreach (var item in EquippedObjects.Values)
            {
                item.Wielder = this;
                Session.Network.EnqueueSend(new GameMessageCreateObject(item));
                VisualEffectManager.SendTo(Session, item);
            }
        }

        public void SendContractTrackerTable()
        {
            if (Character.GetContractsCount(CharacterDatabaseLock) > 0)
                Session.Network.EnqueueSend(new GameEventSendClientContractTrackerTable(Session));
        }

        /// <summary>
        /// Will send out GameEventFriendsListUpdate packets to everyone online that has this player as a friend.
        /// </summary>
        public void SendFriendStatusUpdates(bool previouslyOnline, bool isOnline)
        {
            var appearOffline = GetAppearOffline();
            var previouslyOnlineAndIsNowOffline = previouslyOnline && !isOnline;
            var previouslyOfflineAndIsNowOnline = !previouslyOnline && isOnline;
            var previouslyOfflineAndAppearOffline = !previouslyOnline && appearOffline;

            if ((previouslyOfflineAndIsNowOnline && !previouslyOfflineAndAppearOffline) || previouslyOnlineAndIsNowOffline)
            {
                var msg = $"{Name} has {(isOnline ? "come on" : "gone off")}line.";

                var inverseFriends = PlayerManager.GetOnlineInverseFriends(Guid);

                foreach (var friend in inverseFriends)
                {
                    var playerFriend = new CharacterPropertiesFriendList { CharacterId = friend.Guid.Full, FriendId = Guid.Full };
                    friend.Session.Network.EnqueueSend(new GameEventFriendsListUpdate(friend.Session, GameEventFriendsListUpdate.FriendsUpdateTypeFlag.FriendStatusChanged, playerFriend, true, isOnline));
                    friend.SendMessage(msg);
                }
            }
        }


        /// <summary>
        /// Records where the client thinks we are, for use by physics engine later
        /// </summary>
        public void SetRequestedLocation(Position pos, bool broadcast = true)
        {
            RequestedLocation = pos;
            RequestedLocationBroadcast = broadcast;
        }

        public MotionCommand LastSoulEmote;
        public DateTime LastSoulEmoteEndTime;

        public void BroadcastMovement(MoveToState moveToState)
        {
            var state = moveToState.RawMotionState;

            // update current style
            if ((state.Flags & RawMotionFlags.CurrentStyle) != 0)
            {
                // this lowercase stance field in Player doesn't really seem to be used anywhere
                stance = state.CurrentStyle;
            }

            // update CurrentMotionState here for substates?
            if ((state.Flags & RawMotionFlags.ForwardCommand) != 0)
            {
                if (((uint)state.ForwardCommand & (uint)CommandMask.SubState) != 0)
                    CurrentMotionState.SetForwardCommand(state.ForwardCommand);
            }
            else
                CurrentMotionState.SetForwardCommand(MotionCommand.Ready);

            if (state.CommandListLength > 0)
            {
                if (((uint)state.Commands[0].MotionCommand & (uint)CommandMask.SubState) != 0)
                    CurrentMotionState.SetForwardCommand(state.Commands[0].MotionCommand);
            }

            if (state.HasSoulEmote(false))
            {
                // prevent soul emote spam / bug where client sends multiples
                var soulEmote = state.Commands[0].MotionCommand;
                if (soulEmote == LastSoulEmote && DateTime.UtcNow < LastSoulEmoteEndTime)
                {
                    state.Commands.Clear();
                    state.CommandListLength = 0;
                }
                else
                {
                    var animLength = Physics.Animation.MotionTable.GetAnimationLength(MotionTableId, CurrentMotionState.Stance, soulEmote, state.Commands[0].Speed);

                    LastSoulEmote = soulEmote;
                    LastSoulEmoteEndTime = DateTime.UtcNow + TimeSpan.FromSeconds(animLength);
                }
            }

            var movementData = new MovementData(this, moveToState);

            // copy some fields to CurrentMotionState?
            // this is a mess, fix this whole architecture.
            CurrentMotionState.MotionState.ForwardCommand = movementData.Invalid.State.ForwardCommand;
            CurrentMotionState.MotionState.ForwardSpeed = movementData.Invalid.State.ForwardSpeed;
            CurrentMotionState.MotionState.TurnCommand = movementData.Invalid.State.TurnCommand;
            CurrentMotionState.MotionState.TurnSpeed = movementData.Invalid.State.TurnSpeed;
            CurrentMotionState.MotionState.SidestepCommand = movementData.Invalid.State.SidestepCommand;
            CurrentMotionState.MotionState.SidestepSpeed = movementData.Invalid.State.SidestepSpeed;

            var movementEvent = new GameMessageUpdateMotion(this, movementData);
            EnqueueBroadcast(true, movementEvent);    // shouldn't need to go to originating player?

            // TODO: use real motion / animation system from physics
            //CurrentMotionCommand = movementData.Invalid.State.ForwardCommand;
            CurrentMovementData = movementData;
        }

        private EnvironChangeType? currentFogColor;

        public void SetFogColor(EnvironChangeType fogColor)
        {
            if (fogColor == EnvironChangeType.Clear && !currentFogColor.HasValue)
                return;                

            if (LandblockManager.GlobalFogColor.HasValue && currentFogColor != fogColor)
            {
                currentFogColor = LandblockManager.GlobalFogColor;
                SendEnvironChange(currentFogColor.Value);
            }
            else if (currentFogColor != fogColor)
            {
                currentFogColor = fogColor;
                SendEnvironChange(currentFogColor.Value);
            }

            if (currentFogColor == EnvironChangeType.Clear)
                currentFogColor = null;
        }

        public void ClearFogColor()
        {
            SetFogColor(EnvironChangeType.Clear);
        }

        public void SendEnvironChange(EnvironChangeType environChangeType)
        {
            Session.Network.EnqueueSend(new GameMessageAdminEnvirons(Session, environChangeType));
        }

        public void SetPlayerKillerStatus(PlayerKillerStatus playerKillerStatus, bool broadcast = false)
        {
            switch (playerKillerStatus)
            {
                case PlayerKillerStatus.NPK:
                case PlayerKillerStatus.PK:
                case PlayerKillerStatus.PKLite:
                    PlayerKillerStatus = PlayerKillerStatus.NPK;
                    MinimumTimeSincePk = 0;
                    break;
                case PlayerKillerStatus.Free:
                    PlayerKillerStatus = PlayerKillerStatus.Free;
                    break;
            }

            if (broadcast)
                EnqueueBroadcast(new GameMessagePublicUpdatePropertyInt(this, PropertyInt.PlayerKillerStatus, (int)PlayerKillerStatus));
        }

        public void SendWeenieError(WeenieError error)
        {
            Session.Network.EnqueueSend(new GameEventWeenieError(Session, error));
        }

        public void SendWeenieErrorWithString(WeenieErrorWithString error, string str)
        {
            Session.Network.EnqueueSend(new GameEventWeenieErrorWithString(Session, error, str));
        }

        public void SendTransientError(string msg)
        {
            Session.Network.EnqueueSend(new GameEventCommunicationTransientString(Session, msg));
        }

        public void HandleActionSetAFKMode(bool afkStatus)
        {
            IsAfk = afkStatus;

            Session.Network.EnqueueSend(new GameMessagePrivateUpdatePropertyBool(this, PropertyBool.Afk, IsAfk));
        }

        public static string DefaultAFKMessage => "I am currently away from the keyboard."; // client default (/afk msg)

        public void HandleActionSetAFKMessage(string afkMessage)
        {
            if (string.IsNullOrWhiteSpace(afkMessage))
                afkMessage = DefaultAFKMessage; // client default

            AfkMessage = afkMessage;
        }

        public void HandlePreOrderItems()
        {
            var subscriptionStatus = (SubscriptionStatus)PropertyManager.GetLong("default_subscription_level").Item;

            string status;
            bool success;
            switch (subscriptionStatus)
            {
                default:
                    status = "purchasing";
                    success = TryCreatePreOrderItem(PropertyBool.ActdReceivedItems, ACE.Entity.Enum.WeenieClassName.W_GEMACTDPURCHASEREWARDARMOR_CLASS);
                    break;
                case SubscriptionStatus.ThroneOfDestiny_Preordered:
                    status = "pre-ordering";
                    TryCreatePreOrderItem(PropertyBool.ActdReceivedItems, ACE.Entity.Enum.WeenieClassName.W_GEMACTDPURCHASEREWARDARMOR_CLASS); // pcaps show this actually didn't occur on retail. odd
                    success = TryCreatePreOrderItem(PropertyBool.ActdPreorderReceivedItems, ACE.Entity.Enum.WeenieClassName.W_GEMACTDPURCHASEREWARDHEALTH_CLASS);
                    break;
            }

            var msg = $"Thank you for {status} the Throne of Destiny expansion! A special gift has been placed in your backpack.";

            if (PropertyManager.GetBool("show_first_login_gift").Item && success)
                Session.Network.EnqueueSend(new GameMessageSystemChat(msg, ChatMessageType.Magic));

            AccountRequirements = subscriptionStatus;
        }

        private bool TryCreatePreOrderItem(PropertyBool propertyBool, WeenieClassName weenieClassName)
        {
            var rcvdBlackmoorsFavor = GetProperty(propertyBool) ?? false;
            if (!rcvdBlackmoorsFavor)
            {
                if (GetInventoryItemsOfWCID((uint)weenieClassName).Count == 0)
                {
                    var cachedWeenie = Database.DatabaseManager.World.GetCachedWeenie((uint)weenieClassName);
                    if (cachedWeenie == null)
                        return false;

                    var wo = Factories.WorldObjectFactory.CreateNewWorldObject(cachedWeenie);
                    if (wo == null)
                        return false;

                    if (TryAddToInventory(wo))
                    {
                        SetProperty(propertyBool, true);
                        return true;
                    }
                }
                else
                    SetProperty(propertyBool, true); // already had the item, set the property to reflect item was received
            }

            return false;
        }
    }
}
