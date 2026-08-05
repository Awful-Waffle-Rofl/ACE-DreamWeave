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
using ACE.Server.Entity.Actions;
using ACE.Server.Network.GameEvent.Events;
using ACE.Server.Network.GameMessages.Messages;
using ACE.Server.Managers;
using ACE.Server.Realms;

namespace ACE.Server.WorldObjects
{
    partial class Player
    {
        private static readonly Position MarketplaceDrop = DatabaseManager.World.GetCachedWeenie("portalmarketplace")?.GetPosition(PositionType.Destination) ?? new Position(0x016C01BC, 49.206f, -31.935f, 0.005f, 0, 0, -0.707107f, 0.707107f, 0);

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

                Teleport(MarketplaceDrop);
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
        /// "/dn" - recall to the Drift Network, modelled on HandleActionTeleToMarketPlace: same guards,
        /// same MarketplaceRecall animation and 14s cast, same move-too-far abort.
        /// </summary>
        public void HandleActionTeleToDriftNetwork()
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

            var realm = RealmManager.GetRealm(DriftNetworkRealmId);
            if (realm == null)
            {
                Session.Network.EnqueueSend(new GameMessageSystemChat("The Drift Network is not available on this world.", ChatMessageType.Broadcast));
                return;
            }

            // Guard against the instance-blind visibility bug: teleporting between two instances of the
            // SAME landblock leaves the client showing a blend of both realms (Physics/Common/ObjectMaint.cs
            // has no concept of Instance). Recalling from anywhere else is an ordinary cross-landblock
            // teleport and is fine. Same reason the entry portal lives in the Marketplace, not the hub.
            // NB the guard is on the LANDBLOCK, which is shared by the retail Town Network (realm 0) and
            // the Drift Network (realm 1) - so this fires in either hall and the wording must not assume
            // the player is already in the Meridian's.
            if (Location.LandblockId.Landblock == (DriftNetworkDrop.LandblockId.Landblock))
            {
                Session.Network.EnqueueSend(new GameMessageSystemChat("You must leave the network you are standing in before you can call for a way into another.", ChatMessageType.Broadcast));
                return;
            }

            EnqueueBroadcast(new GameMessageSystemChat($"{Name} is recalling to the Drift Network.", ChatMessageType.Recall), LocalBroadcastRange, ChatMessageType.Recall);

            SendMotionAsCommands(MotionCommand.MarketplaceRecall, MotionStance.NonCombat);

            var startPos = new Position(Location);

            var dnChain = new ActionChain();
            dnChain.AddDelaySeconds(14);

            IsBusy = true;
            dnChain.AddAction(this, () =>
            {
                IsBusy = false;
                var endPos = new Position(Location);
                if (startPos.SquaredDistanceTo(endPos) > RecallMoveThresholdSq)
                {
                    Session.Network.EnqueueSend(new GameEventWeenieError(Session, WeenieError.YouHaveMovedTooFar));
                    return;
                }

                // bind the drop point to realm 1's default instance - same mechanism the realm portal
                // uses via PropertyInt.PortalRealm, and what @telerealm does by hand
                Teleport(new Position(DriftNetworkDrop, realm.DefaultInstanceID));
            });

            dnChain.EnqueueChain();
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
            var newPosition = new Position(_newPosition.ValidateInstanceDestination(this));
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

            Teleporting = true;
            LastTeleportTime = DateTime.UtcNow;
            LastTeleportStartTimestamp = Time.GetUnixTime();

            if (fromPortal)
                LastPortalTeleportTimestamp = LastTeleportStartTimestamp;

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

        public void OnTeleportComplete()
        {
            if (CurrentLandblock != null && !CurrentLandblock.CreateWorldObjectsCompleted)
            {
                // If the critical landblock resources haven't been loaded yet, we keep the player in the pink bubble state
                // We'll check periodically to see when it's safe to let them materialize in
                var actionChain = new ActionChain();
                actionChain.AddDelaySeconds(0.1);
                actionChain.AddAction(this, OnTeleportComplete);
                actionChain.EnqueueChain();
                return;
            }

            // set materialize physics state
            // this takes the player from pink bubbles -> fully materialized
            if (CloakStatus != CloakStatus.On)
                ReportCollisions = true;

            IgnoreCollisions = false;
            Hidden = false;
            Teleporting = false;

            // survival challenge (WaffleACE): reconcile the run flag if a teleport moved us out of the arena
            // instance by any path other than death / exit portal / login-clear (recalls, /hometown, admin tp)
            CheckSurvivalChallengeInstanceExit();

            // wave challenge (WaffleACE): same reconciliation for the wave gauntlet - any teleport out of the
            // arena instance other than death / exit portal / login-clear abandons the run
            CheckWaveChallengeInstanceExit();

            CheckMonsters();
            CheckHouse();

            EnqueueBroadcastPhysicsState();

            // hijacking this for both start/end on portal teleport
            if (LastTeleportStartTimestamp == LastPortalTeleportTimestamp)
                LastPortalTeleportTimestamp = Time.GetUnixTime();
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
        /// Returns the amount of time for player to rotate by the # of degrees
        /// from the input angle, using the omega speed from its MotionTable
        /// </summary>
        public override float GetRotateDelay(float angle)
        {
            return base.GetRotateDelay(angle) / RunFactor;
        }

        /// <summary>
        /// A list of landblocks the player cannot relog directly into
        /// 
        /// If a regular player logs out in one of these landblocks,
        /// they will be transported back to the lifestone when they log back in.
        /// </summary>
        public static HashSet<ushort> NoLog_Landblocks = new HashSet<ushort>()
        {
            // https://asheron.fandom.com/wiki/Special:Search?query=Lifestone+on+Relog%3A+Yes+
            // https://docs.google.com/spreadsheets/d/122xOw3IKCezaTDjC_hggWSVzYJ_9M_zUUtGEXkwNXfs/edit#gid=846612575

            0x0002,     // Viamontian Garrison
            0x0007,     // Town Network
            0x0056,     // Augmentation Realm Main Level
            0x005F,     // Tanada House of Pancakes (Seasonal)
            0x0067,     // PKL Arena
            0x006D,     // Augmentation Realm Upper Level
            0x007D,     // Augmentation Realm Lower Level
            0x00AB,     // Derethian Combat Arena
            0x00AC,     // Derethian Combat Arena
            0x00C3,     // Blighted Putrid Moarsman Tunnels
            0x00D7,     // Jester's Prison
            0x00EA,     // Mhoire Armory
            0x015D,     // Mountain Cavern
            0x027F,     // East Fork Dam Hive
            0x03A7,     // Mount Elyrii Hive
            0x5764,     // Oubliette of Mhoire Castle
            0x634C,     // Tainted Grotto
            0x6544,     // Greater Battle Dungeon
            0x6651,     // Hoshino Tower
            0x7E04,     // Thug Hideout
            0x8A04,     // Night Club (Seasonal Anniversary)
            0x8B04,     // Frozen Wight Lair
            0x9EE5,     // Northwatch Castle Black Market
            0xB5F0,     // Aerfalle's Sanctum
            0xF92F,     // Freebooter Keep Black Market
            0x00B0,     // Colosseum Arena One
            0x00B1,     // Colosseum Arena Two
            0x00B2,     // Colosseum Arena Three
            0x00B3,     // Colosseum Arena Four
            0x00B4,     // Colosseum Arena Five
            0x00B6,     // Colosseum Arena Mini-Bosses
            0x5960,     // Gauntlet Arena One (Celestial Hand)
            0x5961,     // Gauntlet Arena Two (Celestial Hand)
            0x5962,     // Gauntlet Arena One (Eldritch Web)
            0x5963,     // Gauntlet Arena Two (Eldritch Web)
            0x5964,     // Gauntlet Arena One (Radiant Blood)
            0x5965,     // Gauntlet Arena Two (Radiant Blood)
        };

        /// <summary>
        /// Called when a player first logs in
        /// </summary>
        public static void HandleNoLogLandblock(Biota biota, out bool playerWasMovedFromNoLogLandblock)
        {
            playerWasMovedFromNoLogLandblock = false;

            if (biota.WeenieType == WeenieType.Sentinel || biota.WeenieType == WeenieType.Admin) return;

            if (!biota.PropertiesPosition.TryGetValue(PositionType.Location, out var location))
                return;

            var landblock = (ushort)(location.ObjCellId >> 16);

            if (!NoLog_Landblocks.Contains(landblock))
                return;

            if (!biota.PropertiesPosition.TryGetValue(PositionType.Sanctuary, out var lifestone))
                return;

            location.ObjCellId = lifestone.ObjCellId;
            location.PositionX = lifestone.PositionX;
            location.PositionY = lifestone.PositionY;
            location.PositionZ = lifestone.PositionZ;
            location.RotationX = lifestone.RotationX;
            location.RotationY = lifestone.RotationY;
            location.RotationZ = lifestone.RotationZ;
            location.RotationW = lifestone.RotationW;

            playerWasMovedFromNoLogLandblock = true;

            return;
        }
    }
}
