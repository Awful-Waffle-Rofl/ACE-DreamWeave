using System;
using System.Collections.Generic;
using System.Numerics;
using System.Linq;

using ACE.Common;
using ACE.Database;
using ACE.DatLoader;
using ACE.DatLoader.FileTypes;
using ACE.Entity;
using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Entity.Models;
using ACE.Server.Entity;
using ACE.Server.Entity.Facets;
using ACE.Server.Entity.Actions;
using ACE.Server.Network.GameEvent.Events;
using ACE.Server.Network.GameMessages.Messages;
using ACE.Server.Managers;
using ACE.Server.Realms;

namespace ACE.Server.WorldObjects
{
    partial class Player
    {

        /// <summary>
        /// Teleports the player to position
        /// </summary>
        /// <param name="positionType">PositionType to be teleported to</param>
        /// <returns>true on success (position is set) false otherwise</returns>
        public bool TeleToPosition(PositionType positionType)
        {
            var position = GetPosition(positionType);

            if (position != null)
            {
                var teleportDest = new Position(position);
                AdjustDungeon(teleportDest);

                Teleport(teleportDest);
                return true;
            }

            return false;
        }

        private static readonly Motion motionLifestoneRecall = new Motion(MotionStance.NonCombat, MotionCommand.LifestoneRecall);

        private static readonly Motion motionHouseRecall = new Motion(MotionStance.NonCombat, MotionCommand.HouseRecall);

        public static float RecallMoveThreshold = 8.0f;
        public static float RecallMoveThresholdSq = RecallMoveThreshold * RecallMoveThreshold;

        public bool TooBusyToRecall
        {
            get => IsBusy || suicideInProgress;     // recalls could be started from portal space?
        }

        public void HandleActionTeleToHouse()
        {
            if (IsOlthoiPlayer)
            {
                Session.Network.EnqueueSend(new GameEventWeenieError(Session, WeenieError.OlthoiCanOnlyRecallToLifestone));
                return;
            }

            if (PKTimerActive)
            {
                Session.Network.EnqueueSend(new GameEventWeenieError(Session, WeenieError.YouHaveBeenInPKBattleTooRecently));
                return;
            }

            if (RecallsDisabled)
            {
                Session.Network.EnqueueSend(new GameEventWeenieError(Session, WeenieError.ExitTrainingAcademyToUseCommand));
                return;
            }

            if (TooBusyToRecall)
            {
                Session.Network.EnqueueSend(new GameEventWeenieError(Session, WeenieError.YoureTooBusy));
                return;
            }

            var house = House ?? GetAccountHouse();

            if (house == null)
            {
                Session.Network.EnqueueSend(new GameEventWeenieError(Session, WeenieError.YouMustOwnHouseToUseCommand));
                return;
            }

            // Threads pooled loot: a recall out of a Cleared run with loot still pooled asks first (spec section 7).
            if (ACE.Server.ThreadDungeons.ThreadExitGuard.TryHoldExit(this, HandleActionTeleToHouse))
                return;

            if (CombatMode != CombatMode.NonCombat)
            {
                // this should be handled by a different thing, probably a function that forces player into peacemode
                var updateCombatMode = new GameMessagePrivateUpdatePropertyInt(this, PropertyInt.CombatMode, (int)CombatMode.NonCombat);
                SetCombatMode(CombatMode.NonCombat);
                Session.Network.EnqueueSend(updateCombatMode);
            }

            EnqueueBroadcast(new GameMessageSystemChat($"{Name} is recalling home.", ChatMessageType.Recall), LocalBroadcastRange, ChatMessageType.Recall);

            SendMotionAsCommands(MotionCommand.HouseRecall, MotionStance.NonCombat);

            var startPos = new Position(Location);

            // Wait for animation
            var actionChain = new ActionChain();

            // Then do teleport
            var animLength = DatManager.PortalDat.ReadFromDat<MotionTable>(MotionTableId).GetAnimationLength(MotionCommand.HouseRecall);
            actionChain.AddDelaySeconds(animLength);
            IsBusy = true;
            actionChain.AddAction(this, () =>
            {
                IsBusy = false;
                var endPos = new Position(Location);
                if (startPos.SquaredDistanceTo(endPos) > RecallMoveThresholdSq)
                {
                    Session.Network.EnqueueSend(new GameEventWeenieError(Session, WeenieError.YouHaveMovedTooFar));
                    return;
                }
                Teleport(house.SlumLord.Location);
            });

            actionChain.EnqueueChain();
        }

        /// <summary>
        /// Handles teleporting a player to the lifestone (/ls or /lifestone command)
        /// </summary>
        public void HandleActionTeleToLifestone()
        {
            if (PKTimerActive)
            {
                Session.Network.EnqueueSend(new GameEventWeenieError(Session, WeenieError.YouHaveBeenInPKBattleTooRecently));
                return;
            }

            if (RecallsDisabled)
            {
                Session.Network.EnqueueSend(new GameEventWeenieError(Session, WeenieError.ExitTrainingAcademyToUseCommand));
                return;
            }

            if (TooBusyToRecall)
            {
                Session.Network.EnqueueSend(new GameEventWeenieError(Session, WeenieError.YoureTooBusy));
                return;
            }

            if (Sanctuary == null)
            {
                Session.Network.EnqueueSend(new GameMessageSystemChat("Your spirit has not been attuned to a sanctuary location.", ChatMessageType.Broadcast));
                return;
            }

            // A lifestone in one realm copy of the landblock the player is standing in (the Marketplace lifestone
            // in realm 1's Aerfalle Keep, seen from realm 0's) would be a same-landblock cross-instance hop, which
            // renders as a blend of both copies. See InstanceRouting.IsPersistentSameLandblockRealmHop.
            if (InstanceRouting.IsPersistentSameLandblockRealmHop(Location, Sanctuary))
            {
                Session.Network.EnqueueSend(new GameMessageSystemChat(InstanceRouting.SameLandblockRealmHopRefusal, ChatMessageType.Broadcast));
                return;
            }

            // Threads pooled loot: a recall out of a Cleared run with loot still pooled asks first (spec section 7).
            if (ACE.Server.ThreadDungeons.ThreadExitGuard.TryHoldExit(this, HandleActionTeleToLifestone))
                return;

            // FIXME(ddevec): I should probably make a better interface for this
            UpdateVital(Mana, Mana.Current / 2);

            if (CombatMode != CombatMode.NonCombat)
            {
                // this should be handled by a different thing, probably a function that forces player into peacemode
                var updateCombatMode = new GameMessagePrivateUpdatePropertyInt(this, PropertyInt.CombatMode, (int)CombatMode.NonCombat);
                SetCombatMode(CombatMode.NonCombat);
                Session.Network.EnqueueSend(updateCombatMode);
            }

            EnqueueBroadcast(new GameMessageSystemChat($"{Name} is recalling to the lifestone.", ChatMessageType.Recall), LocalBroadcastRange, ChatMessageType.Recall);

            SendMotionAsCommands(MotionCommand.LifestoneRecall, MotionStance.NonCombat);

            var startPos = new Position(Location);

            // Wait for animation
            ActionChain lifestoneChain = new ActionChain();

            // Then do teleport
            IsBusy = true;
            lifestoneChain.AddDelaySeconds(DatManager.PortalDat.ReadFromDat<MotionTable>(MotionTableId).GetAnimationLength(MotionCommand.LifestoneRecall));
            lifestoneChain.AddAction(this, () =>
            {
                IsBusy = false;
                var endPos = new Position(Location);
                if (startPos.SquaredDistanceTo(endPos) > RecallMoveThresholdSq)
                {
                    Session.Network.EnqueueSend(new GameEventWeenieError(Session, WeenieError.YouHaveMovedTooFar));
                    return;
                }

                Teleport(Sanctuary);
            });

            lifestoneChain.EnqueueChain();
        }

        private static readonly Motion motionMarketplaceRecall = new Motion(MotionStance.NonCombat, MotionCommand.MarketplaceRecall);

        public void HandleActionTeleToMarketPlace()
        {
            if (IsOlthoiPlayer)
            {
                Session.Network.EnqueueSend(new GameEventWeenieError(Session, WeenieError.OlthoiCanOnlyRecallToLifestone));
                return;
            }

            if (PKTimerActive)
            {
                Session.Network.EnqueueSend(new GameEventWeenieError(Session, WeenieError.YouHaveBeenInPKBattleTooRecently));
                return;
            }

            if (RecallsDisabled)
            {
                Session.Network.EnqueueSend(new GameEventWeenieError(Session, WeenieError.ExitTrainingAcademyToUseCommand));
                return;
            }

            if (TooBusyToRecall)
            {
                Session.Network.EnqueueSend(new GameEventWeenieError(Session, WeenieError.YoureTooBusy));
                return;
            }

            // Resolved per recall, AFTER the player-state guards so a busy / PK-locked / Olthoi player still sees
            // their own refusal first. Fails closed: never falls back to realm 0 (see MarketplaceRecall).
            if (!MarketplaceRecall.TryResolve(out var marketplaceDrop, out var marketplaceFailure))
            {
                log.Error($"{Name} (0x{Guid.Full:X8}) Marketplace recall refused: {marketplaceFailure}.");
                Session.Network.EnqueueSend(new GameMessageSystemChat(MarketplaceRecall.UnavailableMessage, ChatMessageType.Broadcast));
                return;
            }

            // Recalling from realm 0's Aerfalle Keep into realm 1's Marketplace (same landblock 0x01F5) would be a
            // same-landblock cross-instance hop - see InstanceRouting.IsPersistentSameLandblockRealmHop.
            if (InstanceRouting.IsPersistentSameLandblockRealmHop(Location, marketplaceDrop))
            {
                Session.Network.EnqueueSend(new GameMessageSystemChat(InstanceRouting.SameLandblockRealmHopRefusal, ChatMessageType.Broadcast));
                return;
            }

            // Threads pooled loot: a recall out of a Cleared run with loot still pooled asks first (spec section 7).
            if (ACE.Server.ThreadDungeons.ThreadExitGuard.TryHoldExit(this, HandleActionTeleToMarketPlace))
                return;

            if (CombatMode != CombatMode.NonCombat)
            {
                // this should be handled by a different thing, probably a function that forces player into peacemode
                var updateCombatMode = new GameMessagePrivateUpdatePropertyInt(this, PropertyInt.CombatMode, (int)CombatMode.NonCombat);
                SetCombatMode(CombatMode.NonCombat);
                Session.Network.EnqueueSend(updateCombatMode);
            }

            EnqueueBroadcast(new GameMessageSystemChat($"{Name} is recalling to the marketplace.", ChatMessageType.Recall), LocalBroadcastRange, ChatMessageType.Recall);

            SendMotionAsCommands(MotionCommand.MarketplaceRecall, MotionStance.NonCombat);

            var startPos = new Position(Location);

            // TODO: (OptimShi): Actual animation length is longer than in retail. 18.4s
            // float mpAnimationLength = MotionTable.GetAnimationLength((uint)MotionTableId, MotionCommand.MarketplaceRecall);
            // mpChain.AddDelaySeconds(mpAnimationLength);
            ActionChain mpChain = new ActionChain();
            mpChain.AddDelaySeconds(14);

            // Then do teleport
            IsBusy = true;
            mpChain.AddAction(this, () =>
            {
                IsBusy = false;
                var endPos = new Position(Location);
                if (startPos.SquaredDistanceTo(endPos) > RecallMoveThresholdSq)
                {
                    Session.Network.EnqueueSend(new GameEventWeenieError(Session, WeenieError.YouHaveMovedTooFar));
                    return;
                }

                Teleport(marketplaceDrop);
            });

            // Set the chain to run
            mpChain.EnqueueChain();
        }

        /// <summary>
        /// The Drift Network arrival point: inside the main room (the crossing, landblock 0x0007) but
        /// toward its SOUTH end, facing south down the garrisoned hallway.
        /// NOT the dead centre (70,-70) - there is a fountain there and arrivals landed inside it.
        /// (70,-85) is open floor in cell 0x00070145, ~15m clear of the fountain and ~5m short of where
        /// the south wing begins, so the player arrives in the hall looking down the corridor.
        /// The instance is left at 0 - it is re-bound to realm 1's default instance at teleport time,
        /// because the realm registry is not populated when this static initialiser runs.
        /// </summary>
        private static readonly Position DriftNetworkDrop = new Position(0x00070145, 70f, -85f, 0.005f, 0, 0, 1f, 0, 0);

        /// <summary>Realm 1, "Weave Content 1" - the realm the Drift Network's content lives in.</summary>
        private const ushort DriftNetworkRealmId = 1;

        /// <summary>
        /// The traditional (retail) Town Network arrival point: the same hall, in the same landblock as
        /// the Drift Network, but in realm 0. (70,-80) facing north is where retail's own
        /// "portal to town network" weenies drop players, so it is known-clear of the centre fountain.
        /// The instance is left at 0 for the same reason as DriftNetworkDrop - it is re-bound to the
        /// base realm's default instance at teleport time.
        /// </summary>
        private static readonly Position TownNetworkDrop = new Position(0x00070145, 70f, -80f, 0.005f, 0, 0, 0, 1f, 0);

        /// <summary>
        /// "/dn" - recall to the Drift Network, modelled on HandleActionTeleToMarketPlace: same guards,
        /// same MarketplaceRecall animation and 14s cast, same move-too-far abort.
        /// </summary>
        public void HandleActionTeleToDriftNetwork()
        {
            // resolved (and possibly null) here, but reported from inside the helper, so that an Olthoi /
            // PK-locked / academy / busy player still gets their own error rather than a realm message
            HandleActionTeleToNetworkHub("the Drift Network", DriftNetworkDrop,
                RealmManager.GetRealm(DriftNetworkRealmId), "The Drift Network is not available on this world.");
        }

        /// <summary>
        /// "/tn" - recall to the traditional Town Network. Identical to /dn apart from the arrival point
        /// and the realm it binds to: realm 0, the base world, which always exists, so there is no
        /// "not available on this world" case to guard.
        /// </summary>
        public void HandleActionTeleToTownNetwork()
        {
            HandleActionTeleToNetworkHub("the Town Network", TownNetworkDrop, RealmManager.BaseRealm, null);
        }

        /// <summary>
        /// The shared body of /dn and /tn: the marketplace recall's guards, its MarketplaceRecall
        /// animation and 14s cast, and its move-too-far abort - landing the player at <paramref name="drop"/>
        /// re-bound to <paramref name="realm"/>'s default instance.
        /// A null <paramref name="realm"/> means that network is not registered on this world; it is
        /// reported with <paramref name="unavailableMessage"/>, and deliberately AFTER the player-state
        /// guards, so an unavailable network never masks the more specific "you are busy / in PK / an
        /// Olthoi" refusal the player actually needs to see.
        /// </summary>
        private void HandleActionTeleToNetworkHub(string networkName, Position drop, WorldRealm realm, string unavailableMessage)
        {
            if (IsOlthoiPlayer)
            {
                Session.Network.EnqueueSend(new GameEventWeenieError(Session, WeenieError.OlthoiCanOnlyRecallToLifestone));
                return;
            }

            if (PKTimerActive)
            {
                Session.Network.EnqueueSend(new GameEventWeenieError(Session, WeenieError.YouHaveBeenInPKBattleTooRecently));
                return;
            }

            if (RecallsDisabled)
            {
                Session.Network.EnqueueSend(new GameEventWeenieError(Session, WeenieError.ExitTrainingAcademyToUseCommand));
                return;
            }

            if (TooBusyToRecall)
            {
                Session.Network.EnqueueSend(new GameEventWeenieError(Session, WeenieError.YoureTooBusy));
                return;
            }

            if (realm == null)
            {
                Session.Network.EnqueueSend(new GameMessageSystemChat(unavailableMessage, ChatMessageType.Broadcast));
                return;
            }

            // Guard against the instance-blind visibility bug: teleporting between two instances of the
            // SAME landblock leaves the client showing a blend of both realms (Physics/Common/ObjectMaint.cs
            // has no concept of Instance). Recalling from anywhere else is an ordinary cross-landblock
            // teleport and is fine. Same reason the entry portal lives in the Marketplace, not the hub.
            // NB the guard is on the LANDBLOCK, which is shared by the retail Town Network (realm 0) and
            // the Drift Network (realm 1) - so this fires in either hall, for either command, and the
            // wording must not assume which one the player is standing in.
            if (Location.LandblockId.Landblock == drop.LandblockId.Landblock)
            {
                Session.Network.EnqueueSend(new GameMessageSystemChat("You must leave the network you are standing in before you can call for a way into another.", ChatMessageType.Broadcast));
                return;
            }

            // Threads pooled loot: a recall out of a Cleared run with loot still pooled asks first (spec section 7).
            if (ACE.Server.ThreadDungeons.ThreadExitGuard.TryHoldExit(this, () => HandleActionTeleToNetworkHub(networkName, drop, realm, unavailableMessage)))
                return;

            EnqueueBroadcast(new GameMessageSystemChat($"{Name} is recalling to {networkName}.", ChatMessageType.Recall), LocalBroadcastRange, ChatMessageType.Recall);

            SendMotionAsCommands(MotionCommand.MarketplaceRecall, MotionStance.NonCombat);

            var startPos = new Position(Location);

            var hubChain = new ActionChain();
            hubChain.AddDelaySeconds(14);

            IsBusy = true;
            hubChain.AddAction(this, () =>
            {
                IsBusy = false;
                var endPos = new Position(Location);
                if (startPos.SquaredDistanceTo(endPos) > RecallMoveThresholdSq)
                {
                    Session.Network.EnqueueSend(new GameEventWeenieError(Session, WeenieError.YouHaveMovedTooFar));
                    return;
                }

                // bind the drop point to the target realm's default instance - same mechanism the realm
                // portal uses via PropertyInt.PortalRealm, and what @telerealm does by hand
                Teleport(new Position(drop, realm.DefaultInstanceID));
            });

            hubChain.EnqueueChain();
        }

        private static readonly Motion motionAllegianceHometownRecall = new Motion(MotionStance.NonCombat, MotionCommand.AllegianceHometownRecall);

        public void HandleActionRecallAllegianceHometown()
        {
            //Console.WriteLine($"{Name}.HandleActionRecallAllegianceHometown()");

            if (IsOlthoiPlayer)
            {
                Session.Network.EnqueueSend(new GameEventWeenieError(Session, WeenieError.OlthoiCanOnlyRecallToLifestone));
                return;
            }

            if (PKTimerActive)
            {
                Session.Network.EnqueueSend(new GameEventWeenieError(Session, WeenieError.YouHaveBeenInPKBattleTooRecently));
                return;
            }

            if (RecallsDisabled)
            {
                Session.Network.EnqueueSend(new GameEventWeenieError(Session, WeenieError.ExitTrainingAcademyToUseCommand));
                return;
            }

            if (TooBusyToRecall)
            {
                Session.Network.EnqueueSend(new GameEventWeenieError(Session, WeenieError.YoureTooBusy));
                return;
            }

            // check if player is in an allegiance
            if (!VerifyRecallAllegianceHometown())
                return;

            // Threads pooled loot: a recall out of a Cleared run with loot still pooled asks first (spec section 7).
            if (ACE.Server.ThreadDungeons.ThreadExitGuard.TryHoldExit(this, HandleActionRecallAllegianceHometown))
                return;

            if (CombatMode != CombatMode.NonCombat)
            {
                // this should be handled by a different thing, probably a function that forces player into peacemode
                var updateCombatMode = new GameMessagePrivateUpdatePropertyInt(this, PropertyInt.CombatMode, (int)CombatMode.NonCombat);
                SetCombatMode(CombatMode.NonCombat);
                Session.Network.EnqueueSend(updateCombatMode);
            }

            EnqueueBroadcast(new GameMessageSystemChat($"{Name} is going to the Allegiance hometown.", ChatMessageType.Recall), LocalBroadcastRange, ChatMessageType.Recall);

            SendMotionAsCommands(MotionCommand.AllegianceHometownRecall, MotionStance.NonCombat);

            var startPos = new Position(Location);

            // Wait for animation
            var actionChain = new ActionChain();

            // Then do teleport
            IsBusy = true;
            var animLength = DatManager.PortalDat.ReadFromDat<MotionTable>(MotionTableId).GetAnimationLength(MotionCommand.AllegianceHometownRecall);
            actionChain.AddDelaySeconds(animLength);
            actionChain.AddAction(this, () =>
            {
                IsBusy = false;
                var endPos = new Position(Location);
                if (startPos.SquaredDistanceTo(endPos) > RecallMoveThresholdSq)
                {
                    Session.Network.EnqueueSend(new GameEventWeenieError(Session, WeenieError.YouHaveMovedTooFar));
                    return;
                }

                // re-verify
                if (!VerifyRecallAllegianceHometown())
                    return;

                Teleport(Allegiance.Sanctuary);
            });

            actionChain.EnqueueChain();
        }

        private bool VerifyRecallAllegianceHometown()
        {
            if (Allegiance == null)
            {
                Session.Network.EnqueueSend(new GameEventWeenieError(Session, WeenieError.YouAreNotInAllegiance));
                return false;
            }

            if (Allegiance.Sanctuary == null)
            {
                Session.Network.EnqueueSend(new GameEventWeenieError(Session, WeenieError.YourAllegianceDoesNotHaveHometown));
                return false;
            }

            return true;
        }

        /// <summary>
        /// Recalls you to your allegiance's Mansion or Villa
        /// </summary>
        public void HandleActionTeleToMansion()
        {
            //Console.WriteLine($"{Name}.HandleActionTeleToMansion()");

            if (IsOlthoiPlayer)
            {
                Session.Network.EnqueueSend(new GameEventWeenieError(Session, WeenieError.OlthoiCanOnlyRecallToLifestone));
                return;
            }

            if (PKTimerActive)
            {
                Session.Network.EnqueueSend(new GameEventWeenieError(Session, WeenieError.YouHaveBeenInPKBattleTooRecently));
                return;
            }

            if (RecallsDisabled)
            {
                Session.Network.EnqueueSend(new GameEventWeenieError(Session, WeenieError.ExitTrainingAcademyToUseCommand));
                return;
            }

            if (TooBusyToRecall)
            {
                Session.Network.EnqueueSend(new GameEventWeenieError(Session, WeenieError.YoureTooBusy));
                return;
            }

            var allegianceHouse = VerifyTeleToMansion();

            if (allegianceHouse == null)
                return;

            // Threads pooled loot: a recall out of a Cleared run with loot still pooled asks first (spec section 7).
            if (ACE.Server.ThreadDungeons.ThreadExitGuard.TryHoldExit(this, HandleActionTeleToMansion))
                return;

            if (CombatMode != CombatMode.NonCombat)
            {
                // this should be handled by a different thing, probably a function that forces player into peacemode
                var updateCombatMode = new GameMessagePrivateUpdatePropertyInt(this, PropertyInt.CombatMode, (int)CombatMode.NonCombat);
                SetCombatMode(CombatMode.NonCombat);
                Session.Network.EnqueueSend(updateCombatMode);
            }

            EnqueueBroadcast(new GameMessageSystemChat($"{Name} is recalling to the Allegiance housing.", ChatMessageType.Recall), LocalBroadcastRange, ChatMessageType.Recall);

            SendMotionAsCommands(MotionCommand.HouseRecall, MotionStance.NonCombat);

            var startPos = new Position(Location);

            // Wait for animation
            var actionChain = new ActionChain();

            // Then do teleport
            var animLength = DatManager.PortalDat.ReadFromDat<MotionTable>(MotionTableId).GetAnimationLength(MotionCommand.HouseRecall);
            actionChain.AddDelaySeconds(animLength);

            IsBusy = true;
            actionChain.AddAction(this, () =>
            {
                IsBusy = false;
                var endPos = new Position(Location);
                if (startPos.SquaredDistanceTo(endPos) > RecallMoveThresholdSq)
                {
                    Session.Network.EnqueueSend(new GameEventWeenieError(Session, WeenieError.YouHaveMovedTooFar));
                    return;
                }

                // re-verify
                allegianceHouse = VerifyTeleToMansion();

                if (allegianceHouse == null)
                    return;

                Teleport(allegianceHouse.SlumLord.Location);
            }); 

            actionChain.EnqueueChain();
        }

        private House VerifyTeleToMansion()
        {
            // check if player is in an allegiance
            if (Allegiance == null)
            {
                Session.Network.EnqueueSend(new GameEventWeenieError(Session, WeenieError.YouAreNotInAllegiance));
                return null;
            }

            var allegianceHouse = Allegiance.GetHouse();

            if (allegianceHouse == null)
            {
                Session.Network.EnqueueSend(new GameEventWeenieError(Session, WeenieError.YourMonarchDoesNotOwnAMansionOrVilla));
                return null;
            }

            if (allegianceHouse.HouseType != HouseType.Villa && allegianceHouse.HouseType != HouseType.Mansion)
            {
                Session.Network.EnqueueSend(new GameEventWeenieError(Session, WeenieError.YourMonarchsHouseIsNotAMansionOrVilla));
                return null;
            }

            // ensure allegiance housing has allegiance permissions enabled
            if (allegianceHouse.MonarchId == null)
            {
                Session.Network.EnqueueSend(new GameEventWeenieError(Session, WeenieError.YourMonarchHasClosedTheMansion));
                return null;
            }

            return allegianceHouse;
        }

        private static readonly Motion motionPkArenaRecall = new Motion(MotionStance.NonCombat, MotionCommand.PKArenaRecall);

        private static List<Position> pkArenaLocs = new List<Position>()
        {
            new Position(DatabaseManager.World.GetCachedWeenie("portalpkarenanew1")?.GetPosition(PositionType.Destination) ?? new Position(0x00660117, 30, -50, 0.005f, 0, 0,  0.000000f,  1.000000f, 0)),
            new Position(DatabaseManager.World.GetCachedWeenie("portalpkarenanew2")?.GetPosition(PositionType.Destination) ?? new Position(0x00660106, 10,   0, 0.005f, 0, 0, -0.947071f,  0.321023f, 0)),
            new Position(DatabaseManager.World.GetCachedWeenie("portalpkarenanew3")?.GetPosition(PositionType.Destination) ?? new Position(0x00660103, 30, -30, 0.005f, 0, 0, -0.699713f,  0.714424f, 0)),
            new Position(DatabaseManager.World.GetCachedWeenie("portalpkarenanew4")?.GetPosition(PositionType.Destination) ?? new Position(0x0066011E, 50,   0, 0.005f, 0, 0, -0.961021f, -0.276474f, 0)),
            new Position(DatabaseManager.World.GetCachedWeenie("portalpkarenanew5")?.GetPosition(PositionType.Destination) ?? new Position(0x00660127, 60, -30, 0.005f, 0, 0,  0.681639f,  0.731689f, 0)),
        };

        public void HandleActionTeleToPkArena()
        {
            //Console.WriteLine($"{Name}.HandleActionTeleToPkArena()");

            if (PlayerKillerStatus != PlayerKillerStatus.PK)
            {
                Session.Network.EnqueueSend(new GameEventWeenieError(Session, WeenieError.OnlyPKsMayUseCommand));
                return;
            }

            if (PKTimerActive)
            {
                Session.Network.EnqueueSend(new GameEventWeenieError(Session, WeenieError.YouHaveBeenInPKBattleTooRecently));
                return;
            }

            if (RecallsDisabled)
            {
                Session.Network.EnqueueSend(new GameEventWeenieError(Session, WeenieError.ExitTrainingAcademyToUseCommand));
                return;
            }

            if (TooBusyToRecall)
            {
                Session.Network.EnqueueSend(new GameEventWeenieError(Session, WeenieError.YoureTooBusy));
                return;
            }

            // Threads pooled loot: a recall out of a Cleared run with loot still pooled asks first (spec section 7).
            if (ACE.Server.ThreadDungeons.ThreadExitGuard.TryHoldExit(this, HandleActionTeleToPkArena))
                return;

            if (CombatMode != CombatMode.NonCombat)
            {
                // this should be handled by a different thing, probably a function that forces player into peacemode
                var updateCombatMode = new GameMessagePrivateUpdatePropertyInt(this, PropertyInt.CombatMode, (int)CombatMode.NonCombat);
                SetCombatMode(CombatMode.NonCombat);
                Session.Network.EnqueueSend(updateCombatMode);
            }

            EnqueueBroadcast(new GameMessageSystemChat($"{Name} is going to the PK Arena.", ChatMessageType.Recall), LocalBroadcastRange, ChatMessageType.Recall);

            SendMotionAsCommands(MotionCommand.PKArenaRecall, MotionStance.NonCombat);

            var startPos = new Position(Location);

            // Wait for animation
            var actionChain = new ActionChain();

            // Then do teleport
            var animLength = DatManager.PortalDat.ReadFromDat<MotionTable>(MotionTableId).GetAnimationLength(MotionCommand.PKArenaRecall);
            actionChain.AddDelaySeconds(animLength);

            IsBusy = true;
            actionChain.AddAction(this, () =>
            {
                IsBusy = false;
                var endPos = new Position(Location);
                if (startPos.SquaredDistanceTo(endPos) > RecallMoveThresholdSq)
                {
                    Session.Network.EnqueueSend(new GameEventWeenieError(Session, WeenieError.YouHaveMovedTooFar));
                    return;
                }

                var rng = ThreadSafeRandom.Next(0, pkArenaLocs.Count - 1);
                var loc = pkArenaLocs[rng];

                Teleport(loc);
            });

            actionChain.EnqueueChain();
        }

        private static List<Position> pklArenaLocs = new List<Position>()
        {
            new Position(DatabaseManager.World.GetCachedWeenie("portalpklarenanew1")?.GetPosition(PositionType.Destination) ?? new Position(0x00670117, 30, -50, 0.005f, 0, 0,  0.000000f,  1.000000f, 0)),
            new Position(DatabaseManager.World.GetCachedWeenie("portalpklarenanew2")?.GetPosition(PositionType.Destination) ?? new Position(0x00670106, 10,   0, 0.005f, 0, 0, -0.947071f,  0.321023f, 0)),
            new Position(DatabaseManager.World.GetCachedWeenie("portalpklarenanew3")?.GetPosition(PositionType.Destination) ?? new Position(0x00670103, 30, -30, 0.005f, 0, 0, -0.699713f,  0.714424f, 0)),
            new Position(DatabaseManager.World.GetCachedWeenie("portalpklarenanew4")?.GetPosition(PositionType.Destination) ?? new Position(0x0067011E, 50,   0, 0.005f, 0, 0, -0.961021f, -0.276474f, 0)),
            new Position(DatabaseManager.World.GetCachedWeenie("portalpklarenanew5")?.GetPosition(PositionType.Destination) ?? new Position(0x00670127, 60, -30, 0.005f, 0, 0,  0.681639f,  0.731689f, 0)),
        };

        public void HandleActionTeleToPklArena()
        {
            //Console.WriteLine($"{Name}.HandleActionTeleToPkLiteArena()");

            if (IsOlthoiPlayer)
            {
                Session.Network.EnqueueSend(new GameEventWeenieError(Session, WeenieError.OlthoiCanOnlyRecallToLifestone));
                return;
            }

            if (PlayerKillerStatus != PlayerKillerStatus.PKLite)
            {
                Session.Network.EnqueueSend(new GameEventWeenieError(Session, WeenieError.OnlyPKLiteMayUseCommand));
                return;
            }

            if (PKTimerActive)
            {
                Session.Network.EnqueueSend(new GameEventWeenieError(Session, WeenieError.YouHaveBeenInPKBattleTooRecently));
                return;
            }

            if (RecallsDisabled)
            {
                Session.Network.EnqueueSend(new GameEventWeenieError(Session, WeenieError.ExitTrainingAcademyToUseCommand));
                return;
            }

            if (TooBusyToRecall)
            {
                Session.Network.EnqueueSend(new GameEventWeenieError(Session, WeenieError.YoureTooBusy));
                return;
            }

            // Threads pooled loot: a recall out of a Cleared run with loot still pooled asks first (spec section 7).
            if (ACE.Server.ThreadDungeons.ThreadExitGuard.TryHoldExit(this, HandleActionTeleToPklArena))
                return;

            if (CombatMode != CombatMode.NonCombat)
            {
                // this should be handled by a different thing, probably a function that forces player into peacemode
                var updateCombatMode = new GameMessagePrivateUpdatePropertyInt(this, PropertyInt.CombatMode, (int)CombatMode.NonCombat);
                SetCombatMode(CombatMode.NonCombat);
                Session.Network.EnqueueSend(updateCombatMode);
            }

            EnqueueBroadcast(new GameMessageSystemChat($"{Name} is going to the PKL Arena.", ChatMessageType.Recall), LocalBroadcastRange, ChatMessageType.Recall);

            SendMotionAsCommands(MotionCommand.PKArenaRecall, MotionStance.NonCombat);

            var startPos = new Position(Location);

            // Wait for animation
            var actionChain = new ActionChain();

            // Then do teleport
            var animLength = DatManager.PortalDat.ReadFromDat<MotionTable>(MotionTableId).GetAnimationLength(MotionCommand.PKArenaRecall);
            actionChain.AddDelaySeconds(animLength);

            IsBusy = true;
            actionChain.AddAction(this, () =>
            {
                IsBusy = false;
                var endPos = new Position(Location);
                if (startPos.SquaredDistanceTo(endPos) > RecallMoveThresholdSq)
                {
                    Session.Network.EnqueueSend(new GameEventWeenieError(Session, WeenieError.YouHaveMovedTooFar));
                    return;
                }

                var rng = ThreadSafeRandom.Next(0, pklArenaLocs.Count - 1);
                var loc = pklArenaLocs[rng];

                Teleport(loc);
            });

            actionChain.EnqueueChain();
        }

        public void SendMotionAsCommands(MotionCommand motionCommand, MotionStance motionStance)
        {
            if (FastTick)
            {
                var actionChain = new ActionChain();
                EnqueueMotionAction(actionChain, new List<MotionCommand>() { motionCommand }, 1.0f, motionStance);
                actionChain.EnqueueChain();
            }
            else
            {
                var motion = new Motion(motionStance, MotionCommand.Ready);
                motion.MotionState.AddCommand(this, motionCommand);
                EnqueueBroadcastMotion(motion);
            }
        }

        public DateTime LastTeleportTime;

        /// <summary>
        /// This is not thread-safe. Consider using WorldManager.ThreadSafeTeleport() instead if you're calling this from a multi-threaded subsection.
        /// </summary>
        public void Teleport(Position _newPosition, bool fromPortal = false)
        {
            // single choke point for instance safety: every teleport destination must
            // land in a registered realm, and ephemeral instances must be live and
            // accept this player - otherwise reroute to the home realm's default
            var validated = _newPosition.ValidateInstanceDestination(this, out var rejection);

            // ...except for an EPHEMERAL destination, where the reroute is worse than not moving at all.
            // The reroute keeps the coordinates and swaps the instance, so a refused private instance
            // becomes the shared-world copy of that landblock - and for every landblock that is only ever
            // entered privately (the Proving Grounds arenas, the Loom, a Thread) that copy holds no
            // content: no monsters, no NPCs, no exit portal. A player put there has to /die to get out,
            // which is exactly the prod report this guard exists for.
            //
            // Refusing is safe here in a way it is NOT on the login path (WorldManager.DoPlayerEnterWorld,
            // which must keep accepting the reroute): a refused teleport simply leaves the player standing
            // where they already are, which is by definition a valid, loaded location.
            if (rejection != InstanceRejection.None && _newPosition.IsEphemeralRealm)
            {
                log.Error($"Player.Teleport: refusing to teleport {Name} (0x{Guid.Full:X8}) to landblock 0x{(_newPosition.Cell >> 16):X4} " +
                          $"in ephemeral instance 0x{_newPosition.Instance:X8} - {rejection}. Rerouting would have dropped them into " +
                          $"instance 0x{validated.Instance:X8}, a different copy of that landblock which may hold no content.");

                Session?.Network?.EnqueueSend(new GameMessageSystemChat(
                    "That private instance is no longer available, so you have not been moved. Please try again.",
                    ChatMessageType.System));

                // A caller may already have armed a challenge run ahead of this teleport (Portal.ActOnUse
                // persists DpsChallengeActive / SurvivalChallengeActive / WaveChallengeActive /
                // SpeedChallengeActive BEFORE issuing it, so a mid-run logout is caught at next login). For
                // a completed teleport OnTeleportComplete reconciles that arming, but it will never run now,
                // so the reconcilers are invoked here instead. Each is a no-op when nothing is armed, and
                // each clears an active-but-unbound run silently - precisely the state a refused entry
                // leaves behind.
                //
                // All FOUR are called here, unlike OnTeleportComplete, which wires only the last three.
                // CheckDpsChallengeInstanceExit was added for this path: nothing else would ever clear a DPS
                // flag armed by a portal whose teleport was then refused, and it would sit persisted until
                // the player's next login (WorldManager.DoPlayerEnterWorld's login clear). That
                // OnTeleportComplete does not call it is a separate, pre-existing gap and is left alone.
                CheckDpsChallengeInstanceExit();
                CheckSurvivalChallengeInstanceExit();
                CheckWaveChallengeInstanceExit();
                CheckSpeedChallengeInstanceExit();

                // the queued portal teleport (if this was one) has now been decided - refused - so the
                // player may use a portal again at once rather than waiting out the pending-marker bound
                PendingPortalTeleportTime = null;

                return;
            }

            var newPosition = new Position(validated);
            //newPosition.PositionZ += 0.005f;
            newPosition.PositionZ += 0.005f * (ObjScale ?? 1.0f);

            //Console.WriteLine($"{Name}.Teleport() - Sending to {newPosition.ToLOCString()}");

            // Check currentFogColor set for player. If LandblockManager.GlobalFogColor is set, don't bother checking, dungeons didn't clear like this on retail worlds.
            // if not clear, reset to clear before portaling in case portaling to dungeon (no current way to fast check unloaded landblock for IsDungeon or current FogColor)
            // client doesn't respond to any change inside dungeons, and only queues for change if in dungeon, executing change upon next teleport
            // so if we delay teleport long enough to ensure clear arrives before teleport, we don't get fog carrying over into dungeon.

            if (currentFogColor.HasValue && currentFogColor != EnvironChangeType.Clear && !LandblockManager.GlobalFogColor.HasValue)
            {
                var delayTelport = new ActionChain();
                delayTelport.AddAction(this, () => ClearFogColor());
                delayTelport.AddDelaySeconds(1);
                delayTelport.AddAction(this, () => WorldManager.ThreadSafeTeleport(this, _newPosition));

                delayTelport.EnqueueChain();

                return;
            }

            // pending-teleport diagnostics: was a previous teleport still waiting on LoginComplete (see Player_TeleportWatch)
            var teleportingBefore = Teleporting;

            Teleporting = true;

            // Teleporting (and, for a portal, LastPortalTeleportTimestamp) now carries the portal double-use
            // guard from here on, so the pending marker Portal.ActOnUse stamped has done its job. Cleared only
            // HERE and on the refusal above - not at the top of this method - because the fog-color branch
            // just above re-queues this teleport a second later without starting it, and the marker must keep
            // covering that gap.
            PendingPortalTeleportTime = null;

            LastTeleportTime = DateTime.UtcNow;
            LastTeleportStartTimestamp = Time.GetUnixTime();

            if (fromPortal)
                LastPortalTeleportTimestamp = LastTeleportStartTimestamp;

            // pending-teleport diagnostics: open the window now, before the first send, so it is counted and watched
            // even if anything below throws. Its baseline is taken as this method's last statement.
            // The stamp is held locally because a nested Teleport() below (see the "double update path" note in
            // UpdatePlayerPosition) opens its own window, which this call's baseline must not overwrite.
            var watchStamp = LastTeleportStartTimestamp;
            OpenTeleportWatch(fromPortal ? TeleportKind.Portal : TeleportKind.Teleport, teleportingBefore, newPosition.Cell, newPosition.Instance);

            Session.Network.EnqueueSend(new GameMessagePlayerTeleport(this));

            // load quickly, but player can load into landblock before server is finished loading

            // send a "fake" update position to get the client to start loading asap,
            // also might fix some decal bugs
            var prevLoc = Location;
            Location = newPosition;
            SendUpdatePosition();
            Location = prevLoc;

            DoTeleportPhysicsStateChanges();

            // force out of hotspots
            PhysicsObj.report_collision_end(true);

            if (UnderLifestoneProtection)
                LifestoneProtectionDispel();

            HandlePreTeleportVisibility(newPosition);

            // capture the origin landblock instance BEFORE UpdatePlayerPosition relocates us:
            // for command teleports (InUpdate == false) that call relocates CurrentLandblock to
            // the destination, so it can no longer tell us where we came from
            var originInstancedLandblock = Location.InstancedLandblock;
            var originCell = Location.Cell;
            var originInstance = Location.Instance;

            // Tell the client to delete the origin landblock's objects on any teleport that leaves it,
            // and do it BEFORE the relocate below. Required because a landblock's per-realm instance
            // copies share object guids and client cell ids: without an explicit delete, the origin
            // instance's objects re-render as un-interactable ghosts when a different instance of the
            // same landblock id is later entered.
            //
            // This MUST run before UpdatePlayerPosition. That call performs the arrival visibility pass
            // (update_object_server -> set_current_pos -> change_cell_server -> enter_cell_server ->
            // handle_visible_cells -> enqueue_objs) which adds the DESTINATION landblock's objects to
            // ObjMaint and enqueues their creates. Flushing AFTER it (the previous ordering) iterated
            // those just-added destination objects, removed them from ObjMaint, and sent DeleteObject
            // for them - so a player who stood still after a cross-instance teleport (e.g. returning
            // from an ephemeral arena to the Marketplace) saw an empty destination until a cell change
            // re-ran the visibility pass. Flushing FIRST operates on the still-current ORIGIN known
            // objects (Location is still the origin here), leaving the arrival pass as the single
            // authority for the destination - the same way an ordinary, non-instance-changing teleport
            // already behaves. Uses newPosition (the validated destination) for the destination side,
            // since Location is not relocated until UpdatePlayerPosition runs.
            if (TeleportRequiresClientObjectFlush(originInstancedLandblock, newPosition.InstancedLandblock))
                FlushKnownObjectsForInstanceChange();

            var landblockUpdate = UpdatePlayerPosition(new Position(newPosition), true);

            // cross-instance teleports must transfer landblock membership immediately:
            // the usual deferred transfer (a client position ack arriving while
            // Teleporting is still set) can race with OnTeleportComplete, and the
            // client's ack can never signal an instance change on its own.
            // (Command teleports already relocated inside UpdatePlayerPosition, so
            // CurrentLandblock == destination here and this is a no-op for them.)
            if (landblockUpdate && CurrentLandblock != null && CurrentLandblock.Instance != Location.Instance)
                LandblockManager.RelocateObjectForPhysics(this, true);

            // Permanent forensic record for the "empty world after a teleport" class of report, which is
            // intermittent and never reproducible on demand. Everything needed to tell the three failure
            // shapes apart is on one line: a destination whose instance was rerouted (requested vs
            // validated differ), an arrival that did not transfer landblock membership (landed instance
            // does not match the validated one), and a landblock that was pulled out from under the player
            // afterwards (landed reads none). One INFO line per completed teleport, built only from values
            // already in hand - no lookups, no allocation beyond the string itself.
            var landed = CurrentLandblock == null
                ? "(none)"
                : $"0x{CurrentLandblock.Id.Landblock:X4} instance 0x{CurrentLandblock.Instance:X8}";

            log.Info($"[TELEPORT] {Name} (0x{Guid.Full:X8}) from 0x{originCell:X8} instance 0x{originInstance:X8} " +
                     $"- requested 0x{_newPosition.Cell:X8} instance 0x{_newPosition.Instance:X8}, " +
                     $"validated 0x{newPosition.Cell:X8} instance 0x{newPosition.Instance:X8}, " +
                     $"landed {landed} (landblockUpdate {landblockUpdate})");

            // LAST, so the teleport message, the fake position and UpdatePlayerPosition's own send above are all
            // inside the H3 baseline and only later sends count as extra
            BaselineTeleportWatch(watchStamp, true, originCell, originInstance);
        }

        public void DoPreTeleportHide()
        {
            if (Teleporting) return;
            PlayParticleEffect(PlayScript.Hide, Guid);
        }

        public void DoTeleportPhysicsStateChanges()
        {
            var broadcastUpdate = false;

            var oldHidden = Hidden.Value;
            var oldIgnore = IgnoreCollisions.Value;
            var oldReport = ReportCollisions.Value;

            Hidden = true;
            IgnoreCollisions = true;
            ReportCollisions = false;

            if (Hidden != oldHidden || IgnoreCollisions != oldIgnore || ReportCollisions != oldReport)
                broadcastUpdate = true;

            if (broadcastUpdate)
                EnqueueBroadcastPhysicsState();
        }

        /// <summary>
        /// Prevent message spam
        /// </summary>
        public double? LastPortalTeleportTimestampError;

        /// <summary>
        /// Transient, never persisted: unix time at which Portal.ActOnUse accepted a portal use and queued its
        /// teleport, null when none is pending. Read through Portal.IsPortalTeleportPending, which bounds it
        /// to the ordinary 3.5 s portal cooldown. See that method for why it exists.
        /// </summary>
        public double? PendingPortalTeleportTime;

        /// <summary>
        /// Transient: the portals this player was already overlapping when they last materialised
        /// (OnTeleportComplete, which also runs at login). Empty in the normal case.
        /// <para/>
        /// Why it exists: portal collisions are not edge-triggered. Every client position report that moves the
        /// player runs a physics transition and calls PhysicsObj.track_object_collision for each object it
        /// touches, which reports the collision again whether or not it was already being touched
        /// (PhysicsObj.cs track_object_collision -> report_object_collision -> WeenieObject.DoCollision ->
        /// Player.OnCollideObject -> Portal.OnCollideObject -> OnActivate). So a player who lands ON a portal is
        /// activated by it on their first step after materialising - Teleporting is false by then, and the
        /// 3.5 s portal cooldown only covers portal teleports. That is the 2026-09-30 report: an arena exit
        /// returned a player to an EphemeralRealmExitTo stamp taken while he stood inside the Attack portal, and
        /// his first step re-entered a new arena.
        /// <para/>
        /// A held portal does not fire on COLLISION until the player has walked clear of it (checked on every
        /// moving position report); an explicit use is unaffected. The hold/release rules live in the pure
        /// <see cref="ArrivalOverlapSet"/>; the methods below only feed it distances.
        /// </summary>
        private readonly ArrivalOverlapSet arrivalOverlapPortals = new ArrivalOverlapSet();

        /// <summary>
        /// Records every portal the player is standing in at the moment of materialising, across the current
        /// landblock AND its adjacents: collision is per object, so a portal owned by the neighbouring landblock
        /// at a landblock edge collides just the same. Called from the end of OnTeleportComplete; one portal
        /// filter per landblock per teleport or login, never per tick.
        /// </summary>
        private void CaptureArrivalPortalOverlaps()
        {
            arrivalOverlapPortals.Capture(ArrivalPortalDistances());
        }

        private System.Collections.Generic.IEnumerable<(uint guid, double distance)> ArrivalPortalDistances()
        {
            var landblock = CurrentLandblock;

            if (landblock == null || PhysicsObj == null)
                yield break;

            foreach (var portal in landblock.GetPortals())
            {
                if (portal.PhysicsObj != null)
                    yield return (portal.Guid.Full, GetCylinderDistance(portal));
            }

            foreach (var adjacent in landblock.Adjacents)
            {
                if (adjacent == null)
                    continue;

                foreach (var portal in adjacent.GetPortals())
                {
                    if (portal.PhysicsObj != null)
                        yield return (portal.Guid.Full, GetCylinderDistance(portal));
                }
            }
        }

        /// <summary>
        /// True while <paramref name="portal"/> is held by an arrival overlap and must not fire on collision.
        /// </summary>
        public bool IsArrivalOverlapPortal(Portal portal)
        {
            return arrivalOverlapPortals.IsHeld(portal.Guid.Full);
        }

        /// <summary>
        /// Releases each arrival-held portal the player has walked clear of, or that is gone. Called from
        /// UpdatePlayerPosition after the physics update of every moving position report, so the report that
        /// steps back ONTO a still-held portal is refused, and the first report that carries the player clear
        /// releases it. The lookup searches adjacent landblocks (GetObject's default), matching the capture
        /// scope. The IsEmpty test comes first so the normal case allocates no closure on this hot path.
        /// </summary>
        private void ReleaseArrivalPortalOverlaps()
        {
            if (arrivalOverlapPortals.IsEmpty)
                return;

            arrivalOverlapPortals.Release(guid =>
            {
                var portal = CurrentLandblock?.GetObject(new ObjectGuid(guid), searchAdjacents: true) as Portal;
                return portal?.PhysicsObj == null ? (double?)null : GetCylinderDistance(portal);
            });
        }

        /// <summary>
        /// How long <see cref="OnTeleportComplete"/> will hold a player in the pre-materialize
        /// "pink bubble" state waiting on Landblock.CreateWorldObjectsCompleted before giving up and
        /// materializing them anyway.
        ///
        /// The wait exists so a player cannot walk through a door that has not spawned yet, so the bound
        /// has to sit far above any healthy load: a landblock's population is a cached world-db read, a
        /// shard static read and object construction, all of which complete in well under a second even
        /// on a loaded server. 30s leaves that untouched while still resolving inside the 5 minute
        /// MaximumTeleportTime backstop in Player_Tick, which does not rescue the player - it logs them
        /// off, and a relog into the same broken landblock simply repeats the wait. Materializing into a
        /// landblock that never populated is strictly better than that loop: the player can move, recall
        /// and use commands, and the ERROR line below says why the world around them is empty.
        /// </summary>
        private static readonly TimeSpan MaxPreMaterializeWait = TimeSpan.FromSeconds(30);

        // Retry state for the wait above. Keyed on the teleport (its start timestamp) and the landblock
        // being waited on, not on the player, so a later teleport - or a login, which stamps
        // LastTeleportStartTimestamp too - always starts a fresh clock instead of inheriting the elapsed
        // wait of an earlier arrival. preMaterializeWaitStart == DateTime.MinValue means "no wait in
        // progress"; it is what makes the seeding independent of the stamp, see OnTeleportComplete.
        private double? preMaterializeWaitStamp;
        private ulong preMaterializeWaitLongId;
        private DateTime preMaterializeWaitStart;

        // Not persisted: a fresh login always re-announces, which is what we want, since the
        // client never carries the realm line across a reconnect either.
        private uint? lastAnnouncedRealmInstance;

        /// <summary>
        /// Pushes the [REALM] chat line (see RealmLine) to this player, if realm_announce_enabled
        /// is on and the player's instance actually changed since the last push. Called from
        /// OnTeleportComplete, which also covers login (GameActionLoginComplete calls it directly).
        /// </summary>
        public void SendRealmLine()
        {
            if (Location == null)
                return;

            // Checked before the dedupe latch below, not after: re-enabling the tunable mid-session
            // must not leave a player permanently un-announced because they were skipped while it
            // was off.
            if (!PropertyManager.GetBool("realm_announce_enabled").Item)
                return;

            if (lastAnnouncedRealmInstance == Location.Instance)
                return;

            lastAnnouncedRealmInstance = Location.Instance;

            Session.Network.EnqueueSend(new GameMessageSystemChat(RealmLine.ForPosition(Location), ChatMessageType.Broadcast));
        }

        /// <summary>How long after materializing the second stance re-send fires (covers arrival while still airborne).</summary>
        private const double StanceResyncDelaySeconds = 1.5;

        /// <summary>
        /// Re-sends the server's CURRENT stance and CombatMode to this player's own client only. Changes no server
        /// state, broadcasts to nobody, starts no animation and does not touch NextUseTime. Sent in every mode,
        /// NonCombat included: a peace-stance motion plus CombatMode=NonCombat is how the server pulls a client out
        /// of the missile-bar-in-peace-stance trap.
        /// </summary>
        public void ResyncStanceToSelf(string reason)
        {
            var stance = CurrentMotionState.Stance;
            var mode = CombatMode;

            var line = $"[STANCE_RESYNC] {Name} (0x{Guid}) stance={stance} mode={mode} reason={reason}";

            // A combat mode with a peace stance is the inconsistent pair this resync exists to repair; Warn so
            // prod shows whether the server itself ever holds it. The normal case is Debug (fires on every teleport).
            if (mode != CombatMode.NonCombat && stance == MotionStance.NonCombat)
                log.Warn(line + " MISMATCH");
            else if (log.IsDebugEnabled)
                log.Debug(line);

            // animOnly=true: do not write CombatMode back, the server value is the source of truth here
            ResendStanceToSelf(this, mode, stance, true);
        }

        /// <summary>
        /// One delayed <see cref="ResyncStanceToSelf"/>. Re-reads current state at fire time and is skipped if the
        /// player is teleporting again, has no network, or is logging out.
        /// </summary>
        private void ScheduleDelayedStanceResync()
        {
            var chain = new ActionChain();
            chain.AddDelaySeconds(StanceResyncDelaySeconds);
            chain.AddAction(this, () =>
            {
                if (Teleporting || Session?.Network == null || IsLoggingOut)
                    return;

                // Never disturb a player who is doing something: a fresh stance motion carries ForwardCommand=Ready and
                // is non-autonomous, so on the player's own client it can stop a run or cancel a MoveTo auto-walk.
                // Movement signal: the last client MoveToState (set for every report in either movement formula, and
                // cleared of commands when the key is released) plus a server-driven MoveTo in progress.
                if (IsDead || IsInDeathProcess)
                    return;

                if (DateTime.UtcNow < NextUseTime || MagicState.IsCasting || Attacking)
                    return;

                if (MoveToParams != null || IsPlayerMovingTo || LastMoveToState?.RawMotionState?.HasMovement() == true)
                    return;

                ResyncStanceToSelf("teleport-complete-delayed");
            });
            chain.EnqueueChain();
        }

        public void OnTeleportComplete()
        {
            if (CurrentLandblock != null && !CurrentLandblock.CreateWorldObjectsCompleted)
            {
                var waitStamp = LastTeleportStartTimestamp;
                var waitLongId = CurrentLandblock.LongId;

                // The MinValue clause is the one that must not be dropped. LastTeleportStartTimestamp is
                // stamped by every teleport (Player_Location.cs, Teleport) and every login
                // (Player_Networking.cs), and nothing nulls it, so today waitStamp always has a value -
                // but the stamp comparison ALONE would silently fail if that ever stopped holding:
                // null != null is false, the start would never be seeded, and the very first check would
                // read an enormous elapsed time and take the expired branch immediately, materializing
                // with a zero-length wait and defeating the bound entirely. Seeding on "no wait in
                // progress" does not depend on the stamp at all.
                if (preMaterializeWaitStart == DateTime.MinValue || preMaterializeWaitStamp != waitStamp || preMaterializeWaitLongId != waitLongId)
                {
                    preMaterializeWaitStamp = waitStamp;
                    preMaterializeWaitLongId = waitLongId;
                    preMaterializeWaitStart = DateTime.UtcNow;
                }

                var waited = DateTime.UtcNow - preMaterializeWaitStart;

                if (waited < MaxPreMaterializeWait)
                {
                    // If the critical landblock resources haven't been loaded yet, we keep the player in the pink bubble state
                    // We'll check periodically to see when it's safe to let them materialize in
                    var actionChain = new ActionChain();
                    actionChain.AddDelaySeconds(0.1);
                    actionChain.AddAction(this, OnTeleportComplete);
                    actionChain.EnqueueChain();
                    return;
                }

                // Past the bound: the landblock's population task has almost certainly faulted (see the
                // faulted-only continuation on the Task.Run in Landblock.Init, which logs the exception).
                // Fall through and materialize rather than retrying forever - a player stuck here cannot
                // act at all, and cannot even /die, because Player_Death refuses while Teleporting is set.
                log.Error($"{Name} (0x{Guid}): landblock 0x{CurrentLandblock.Id.Landblock:X4} instance 0x{CurrentLandblock.Instance:X8} has not set CreateWorldObjectsCompleted after {waited.TotalSeconds:0.#}s - materializing anyway instead of holding the player pre-materialize. This landblock is very likely empty; check for a preceding population task error on it.");
            }

            // Whatever wait there was is over - either the landblock finished loading, or the bound
            // expired above. Clearing the start is what lets the NEXT arrival seed a fresh clock even in
            // the degenerate case where the stamp cannot tell two arrivals apart; leaving it set would
            // hand that arrival an already-expired clock and a zero-length wait.
            preMaterializeWaitStart = DateTime.MinValue;

            // set materialize physics state
            // this takes the player from pink bubbles -> fully materialized
            if (CloakStatus != CloakStatus.On)
                ReportCollisions = true;

            IgnoreCollisions = false;
            Hidden = false;

            // pending-teleport diagnostics: reads Teleporting, so before it is cleared
            TeleportWatchOnComplete();

            Teleporting = false;

            // Re-send the server's current stance + CombatMode to our own client. A client that lost the stance
            // motion in portal space can show the missile bar while its character stands in peace stance, and
            // cannot break out of that on its own. Once now, and once more shortly after in case we arrived
            // while still falling (the client may refuse the motion until it has ground contact).
            ResyncStanceToSelf("teleport-complete");
            ScheduleDelayedStanceResync();

            // survival challenge (WaffleACE): reconcile the run flag if a teleport moved us out of the arena
            // instance by any path other than death / exit portal / login-clear (recalls, /hometown, admin tp)
            CheckSurvivalChallengeInstanceExit();

            // wave challenge (WaffleACE): same reconciliation for the wave gauntlet - any teleport out of the
            // arena instance other than death / exit portal / login-clear abandons the run
            CheckWaveChallengeInstanceExit();

            // speed challenge (WaffleACE): same reconciliation for the timed run - ANY teleport out of the
            // season instance other than death or a completion (recall, portal, the exit portal) forfeits it
            CheckSpeedChallengeInstanceExit();

            // Threads (WaffleACE): we are past the pink-bubble retry above, so the player is really
            // in this instance now. Latch that on the run, if this instance is one.
            NoteThreadDungeonArrival();

            CheckMonsters();
            CheckHouse();

            EnqueueBroadcastPhysicsState();

            // hijacking this for both start/end on portal teleport
            if (LastTeleportStartTimestamp == LastPortalTeleportTimestamp)
                LastPortalTeleportTimestamp = Time.GetUnixTime();

            // Decal plugin support: publish the realm/instance the client itself never sees
            // (only Cell goes over the wire). Covers login too, since GameActionLoginComplete
            // calls OnTeleportComplete directly.
            SendRealmLine();

            // Collisions report again from the next moving position report: hold any portal the player has
            // landed inside so that report cannot activate it (see arrivalOverlapPortals). LAST in this method,
            // and guarded, so a failure here can never skip the materialise broadcast or the realm line above -
            // nothing between materialising and here can process a position report, so running it last opens
            // no gap. A failure leaves the set empty, which is exactly the behaviour before this guard existed.
            try
            {
                CaptureArrivalPortalOverlaps();
            }
            catch (Exception ex)
            {
                log.Error($"{Name} (0x{Guid}): CaptureArrivalPortalOverlaps failed after teleport to 0x{Location?.Cell:X8} instance 0x{Location?.Instance:X8} - a portal the player landed inside may fire on their first step.", ex);
            }
        }

        /// <summary>
        /// Records that a player has actually materialised inside a Thread run's private copy
        /// (WaffleACE). ThreadDungeonManager's cleared-and-empty reap refuses to fire until this has
        /// happened at least once, so that a run whose owner is still in transit is never torn down as
        /// "empty" - see ThreadDungeonRun.PlayerEverObserved for why the reap's own probe cannot tell
        /// "nobody came" from "nobody has arrived yet".
        ///
        /// This is the right place for it rather than the gem handler's teleport call: the follow-up action
        /// WorldManager.ThreadSafeTeleport takes runs as soon as Player.Teleport RETURNS, which can be
        /// before the player has materialised, while OnTeleportComplete only reaches this line after the
        /// CreateWorldObjectsCompleted retry chain above has let go.
        ///
        /// One O(1) dictionary probe per completed teleport, and no scan: GetRun is a ConcurrentDictionary
        /// lookup keyed by the instance id, and an ordinary landblock's instance (0) simply misses.
        ///
        /// Group Threads: ThreadDungeonManager.OnPlayerArrived does the latch above and, for a roster
        /// member, also latches their Entered flag and asks for a held pile's delivery (ruling R1).
        /// </summary>
        private void NoteThreadDungeonArrival()
        {
            var instance = Location?.Instance ?? 0;
            if (instance == 0)
                return;

            ACE.Server.ThreadDungeons.ThreadDungeonManager.OnPlayerArrived(this, instance);
        }

        public void SendTeleportedViaMagicMessage(WorldObject itemCaster, Spell spell)
        {
            if (itemCaster == null || itemCaster is Gem)
                Session.Network.EnqueueSend(new GameMessageSystemChat($"You have been teleported.", ChatMessageType.Magic));
            else if (this != itemCaster && !(itemCaster is Gem) && !(itemCaster is Switch) && !(itemCaster.GetProperty(PropertyBool.NpcInteractsSilently) ?? false))
                Session.Network.EnqueueSend(new GameMessageSystemChat($"{itemCaster.Name} teleports you with {spell.Name}.", ChatMessageType.Magic));
            //else if (itemCaster is Gem)
            //    Session.Network.EnqueueSend(new GameEventWeenieError(Session, WeenieError.ITeleported));
        }

        public void NotifyLandblocks()
        {
            // the original implementations of this were done on landblock heartbeat,
            // with checks for players in the current landblock, as well as adjacent outdoor landblocks

            // for performance reasons, this is being reimplemented in the reverse manner,
            // with players notifying landblocks of their activity

            // notify current landblock of player activity
            if (CurrentLandblock != null)
                CurrentLandblock?.SetActive();
        }

        public const float RunFactor = 1.5f;

        /// <summary>
        /// WaffleACE: speed multiplier for server-initiated player turns (the auto-face before a cast,
        /// attack or use), scaled by buffed Quickness - see <see cref="QuicknessTurnSpeed"/>. Sent to the
        /// client in the TurnTo params, used by the server's own turn, and divided into GetRotateDelay, so
        /// all three agree. Manual turning is client-simulated and unaffected.
        ///
        /// PK facet, while the facet PK rule is active (IsPkFacetRuleActive): exactly 1.0, the unboosted turn. This property is the one source for all three uses -
        /// the TurnTo params (Creature_Navigation, both TurnTo overloads, and Player_Move2.GetTurnToParams)
        /// and GetRotateDelay below - so suppressing it here suppresses the bonus everywhere at once.
        /// The PvP arena turn-speed mask joins through the combined predicate (TurnSpeedSuppressed = facet rule OR
        /// arena mask, Docs/Pvp/DESIGN.md H12).
        /// </summary>
        public float TurnToSpeed => (float)FacetPk.TurnSpeed(TurnSpeedSuppressed, QuicknessTurnSpeed.Compute(
            Quickness.Current,
            PropertyManager.GetDouble("player_turnto_speed_per_quickness").Item,
            PropertyManager.GetDouble("player_turnto_speed_bonus_max").Item));

        /// <summary>
        /// Returns the amount of time for player to rotate by the # of degrees
        /// from the input angle, using the omega speed from its MotionTable
        /// </summary>
        public override float GetRotateDelay(float angle)
        {
            return base.GetRotateDelay(angle) / (RunFactor * TurnToSpeed);
        }

        /// <summary>
        /// Called when a player first logs in - see <see cref="NoLogLandblock"/>.
        /// </summary>
        public static void HandleNoLogLandblock(Biota biota, out bool playerWasMovedFromNoLogLandblock)
        {
            NoLogLandblock.Apply(biota, out playerWasMovedFromNoLogLandblock);
        }
    }
}
