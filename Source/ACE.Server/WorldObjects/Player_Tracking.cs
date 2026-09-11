using System;
using System.Collections.Generic;
using System.Linq;

using ACE.Entity;
using ACE.Entity.Enum;
using ACE.Server.Entity;
using ACE.Server.Entity.Actions;
using ACE.Server.Managers;
using ACE.Server.Network.GameMessages.Messages;
using ACE.Server.Physics.Common;

namespace ACE.Server.WorldObjects
{
    partial class Player
    {
        /// <summary>
        /// ObjectId of the currently selected Target (only players and creatures)
        /// </summary>
        private WorldObjectInfo selectedTarget { get; set; }

        /// <summary>
        /// This dictionary is used to keep track of the last use of any item that implemented shared cooldown.
        /// It is session specific.   I think (could be wrong) cooldowns reset if you logged out and back in.
        /// This is a different mechanic than quest repeat timers and rare item use timers.
        /// example - contacts have a shared cooldown key value 100 so each time a player uses an item that has
        /// a shared cooldown we just add to the dictionary 100, datetime.now()   The check becomes trivial at that
        /// point if on a subsequent use, now() minus the last use value from the dictionary
        /// is greater than or equal to the cooldown, we can do the use - if not you must wait message.   Og II
        /// </summary>
        public Dictionary<int, DateTime> LastUseTracker { get; set; }

        /// <summary>
        /// The link to this player's Object Maintenance
        /// This is the system from client physics that tracks known objects, visible objects,
        /// and objects that have been occluded for less than 25s that are pending destruction
        /// </summary>
        public ObjectMaint ObjMaint => PhysicsObj.ObjMaint;

        /// <summary>
        /// GUIDs of objects for which this client already has a persistent visual effect running.
        /// Lives on Player, so it resets naturally on relog - which is correct, because a relog
        /// rebuilds every object client-side and drops their emitters. See VisualEffectManager.
        /// </summary>
        public HashSet<uint> VisualEffectsSent { get; } = new HashSet<uint>();

        /// <summary>
        /// Returns the list of WorldObjects this Player this player currently knows about
        /// </summary>
        public List<WorldObject> GetKnownObjects()
        {
            return ObjMaint.GetKnownObjectsValuesWhere(i => i?.WeenieObj.WorldObject != null).Select(o => o.WeenieObj.WorldObject).ToList();
        }

        /// <summary>
        /// Sends a network message to player for CreateObject, if applicable
        /// </summary>
        /// <param name="sharedCreate">
        /// Optional CreateObject message already serialized once for a whole broadcast fan-out
        /// (see <see cref="WorldObject.NotifyPlayers"/>). GameMessage payloads are built in the
        /// constructor and NetworkSession.EnqueueSend only stores the reference, so one message
        /// object can be handed to every session - this is the same sharing EnqueueBroadcast does.
        /// It is only used for recipients with Adminvision == false, because the adminvision /
        /// adminnodraw flags change the serialized payload; an Adminvision recipient still builds
        /// its own message.
        /// </param>
        /// <param name="resend">
        /// True when the caller is re-sending CreateObject for an object the client still holds, with
        /// no delete in between (/fi). That path skips RemoveTrackedObject, so nothing calls
        /// VisualEffectManager.Forget, and without this flag the rebuild would silently strip every
        /// particle script the object had for the rest of the session.
        /// </param>
        public void TrackObject(WorldObject worldObject, bool delay = false, GameMessageCreateObject sharedCreate = null, bool resend = false)
        {
            //Console.WriteLine($"TrackObject({worldObject.Name}, {delay})");

            if (worldObject == null || worldObject.Guid == Guid)
                return;

            // If Visibility is true, do not send object to client, object is meant for server side only, unless Adminvision is true.
            if (worldObject.Visibility && !Adminvision)
                return;

            Session.Network.EnqueueSend(sharedCreate != null && !Adminvision
                ? sharedCreate
                : new GameMessageCreateObject(worldObject, Adminvision, Adminvision));

            // A client drops every attached particle script when it rebuilds an object, so re-emit
            // them right behind the create. See VisualEffectManager.
            VisualEffectManager.SendTo(Session, worldObject, resend);

            //Console.WriteLine($"Player {Name} - TrackObject({worldObject.Name})");

            // add creature equipped objects / wielded items
            if (worldObject is Creature creature)
            {
                foreach (var wieldedItem in creature.EquippedObjects.Values)
                    if (IsInChildLocation(wieldedItem))
                        TrackEquippedObject(creature, wieldedItem, resend);

                if (creature.IsMoving)
                    creature.BroadcastMoveTo(this);
            }
        }

        /// <param name="sharedCreate">see <see cref="TrackObject"/> - optional CreateObject serialized once for a whole fan-out</param>
        public bool AddTrackedObject(WorldObject worldObject, GameMessageCreateObject sharedCreate = null)
        {
            // does this work for equipped objects?
            if (ObjMaint.KnownObjectsContainsValue(worldObject.PhysicsObj))
            {
                //Console.WriteLine($"Player {Name} - AddTrackedObject({worldObject.Name}) skipped, already tracked");
                return false;
            }

            ObjMaint.AddKnownObject(worldObject.PhysicsObj);
            ObjMaint.AddVisibleObject(worldObject.PhysicsObj);

            TrackObject(worldObject, false, sharedCreate);
            return true;
        }

        /// <param name="sharedDelete">
        /// Optional DeleteObject message already serialized once for a whole broadcast fan-out
        /// (see Landblock.RemoveWorldObjectInternal). Unlike CreateObject this payload has no
        /// recipient-dependent component at all - it is the object guid plus the object's
        /// ObjectInstance sequence - so the one message is valid for every recipient, admin or not.
        /// </param>
        public void RemoveTrackedObject(WorldObject wo, bool fromPickup, GameMessageDeleteObject sharedDelete = null)
        {
            //log.Info($"{Name}.RemoveTrackedObject({wo.Name} ({wo.Guid}), {fromPickup})");

            if (fromPickup)
            {
                Session.Network.EnqueueSend(new GameMessagePickupEvent(wo));

                if (wo.WielderId != null && (wo.ParentLocation ?? 0) != 0)
                    Session.Network.EnqueueSend(new GameMessageParentEvent(wo.Wielder, wo));
            }
            else
                Session.Network.EnqueueSend(sharedDelete ?? new GameMessageDeleteObject(wo));

            // The client drops this object's emitters when it destroys the object, so allow a re-send
            // the next time it comes into view.
            VisualEffectManager.Forget(Session, wo);

            if (wo is Creature creature)
            {
                foreach (var wieldedItem in creature.EquippedObjects.Values)
                    RemoveTrackedEquippedObject(creature, wieldedItem);
            }
        }


        /// <param name="resend">see <see cref="TrackObject"/> - re-send with no preceding delete</param>
        public void TrackEquippedObject(Creature wielder, WorldObject wieldedItem, bool resend = false)
        {
            //Console.WriteLine($"Player {Name} - TrackEquippedObject({wieldedItem.Name}) on Wielder {wielder.Name}");

            // We make sure the item is actually wielded and selectable
            if (((wieldedItem.CurrentWieldedLocation ?? 0) & EquipMask.SelectablePlusAmmo) == 0)
                return;

            // The wielder already knows about this object
            if (wielder == this)
                return;

            Session.Network.EnqueueSend(new GameMessageCreateObject(wieldedItem));

            // This is the path that carries ANOTHER player's wielded weapon into your client, so it is
            // the one that decides whether other people see the effect on your bow.
            VisualEffectManager.SendTo(Session, wieldedItem, resend);
        }

        public void RemoveTrackedEquippedObject(Creature formerWielder, WorldObject worldObject)
        {
            //Console.WriteLine($"Player {Name} - RemoveTrackedEquippedObject({worldObject.Name}) on Former Wielder {formerWielder.Name}");

            // We don't need to remove objects that couldn't have been tracked in the first place
            if (((worldObject.ValidLocations ?? 0) & EquipMask.SelectablePlusAmmo) == 0)
                return;

            // The former wielder already knows about this object was removed
            if (formerWielder == this)
                return;

            // intended for cloaked objects, as DO's should not be sent for them
            // but this breaks regular players, as the state of worldObject has already changed, and is never in a ChildLocation
            //if (!IsInChildLocation(worldObject))
                //return;

            // todo: Until we can fix the tracking system better, sending the PickupEvent like retail causes weapon dissapearing bugs on relog
            //Session.Network.EnqueueSend(new GameMessagePickupEvent(worldObject));

            Session.Network.EnqueueSend(new GameMessageDeleteObject(worldObject));

            VisualEffectManager.Forget(Session, worldObject);
        }

        public void DeCloak()
        {
            if (CloakStatus == CloakStatus.Off)
                return;

            var actionChain = new ActionChain();

            actionChain.AddAction(this, () =>
            {
                EnqueueBroadcast(false, new GameMessageDeleteObject(this));
            });
            actionChain.AddAction(this, () =>
            {
                NoDraw = true;
                EnqueueBroadcastPhysicsState();
                Visibility = false;
            });
            actionChain.AddDelaySeconds(.5);
            actionChain.AddAction(this, () =>
            {
                EnqueueBroadcast(false, new GameMessageCreateObject(this));
            });
            actionChain.AddDelaySeconds(.5);
            actionChain.AddAction(this, () =>
            {
                Cloaked = false;
                Ethereal = false;
                NoDraw = false;
                ReportCollisions = true;
                EnqueueBroadcastPhysicsState();
            });

            actionChain.EnqueueChain();
        }

        public void HandleCloak()
        {
            if (CloakStatus == CloakStatus.On)
                return;

            var actionChain = new ActionChain();

            actionChain.AddAction(this, () =>
            {
                Cloaked = true;
                Ethereal = true;
                NoDraw = true;
                ReportCollisions = false;
                EnqueueBroadcastPhysicsState();
            });
            actionChain.AddAction(this, () =>
            {
                EnqueueBroadcast(false, new GameMessageDeleteObject(this));
            });
            actionChain.AddDelaySeconds(.5);
            actionChain.AddAction(this, () =>
            {
                Visibility = true;
            });
            actionChain.AddDelaySeconds(.5);
            actionChain.AddAction(this, () =>
            {
                EnqueueBroadcast(false, new GameMessageCreateObject(this, true, true));
            });

            actionChain.EnqueueChain();
        }

        /// <summary>
        /// ACRealms port: a landblock and its per-realm instance copies share 32-bit object guids
        /// and client-side cell ids. A teleport that leaves a landblock must tell the client to
        /// delete that origin landblock's objects (see <see cref="FlushKnownObjectsForInstanceChange"/>);
        /// otherwise entering a *different instance* of the same landblock id later re-renders the
        /// origin instance's objects as un-interactable ghosts. Returns true when the origin and
        /// destination are different server-side landblock instances - i.e. the teleport changes the
        /// 64-bit (instance, landblock) pair.
        ///
        /// The decision must be made from the origin instanced-landblock captured *before* physics
        /// relocation, never from CurrentLandblock: a command-initiated teleport (lifestone / house /
        /// marketplace / Drift Network recall, /teleto, /telepoi) runs with <see cref="InUpdate"/> == false,
        /// so UpdatePlayerPosition has already relocated CurrentLandblock to the destination by the time
        /// the caller evaluates this - making a CurrentLandblock-vs-destination instance compare falsely
        /// equal and skipping the flush. That skip is what let realm-specific objects linger on the client
        /// as ghosts. The client's own 25s occlusion cull does not cover it either: an intermediate hop
        /// through another landblock (a lifestone recall between the two visits) drops the objects from the
        /// origin's PVS well before the eventual cross-instance arrival, so nothing ever deletes them.
        /// </summary>
        public static bool TeleportRequiresClientObjectFlush(ulong originInstancedLandblock, ulong destinationInstancedLandblock)
        {
            return originInstancedLandblock != destinationInstancedLandblock;
        }

        /// <summary>
        /// ACRealms port: a landblock and its instance copies share 32-bit object guids,
        /// so when a player moves between instances the client must fully forget the old
        /// instance's objects before the new instance's (same-guid) objects can be
        /// created for it. Clears both sides of the object tracking tables and sends
        /// DeleteObject for everything known.
        /// </summary>
        public void FlushKnownObjectsForInstanceChange()
        {
            if (PhysicsObj?.ObjMaint == null)
                return;

            foreach (var knownObj in GetKnownObjects())
            {
                if (knownObj.PhysicsObj != null)
                {
                    knownObj.PhysicsObj.ObjMaint.RemoveObject(PhysicsObj);
                    ObjMaint.RemoveObject(knownObj.PhysicsObj);
                }

                if (knownObj is Player knownPlayer)
                    knownPlayer.RemoveTrackedObject(this, false);

                RemoveTrackedObject(knownObj, false);
            }
        }

        public void HandlePreTeleportVisibility(ACE.Entity.Position newPosition)
        {
            // repro steps without this function:

            // - /teleloc 0x8A0201C2 [59.822445 -59.574703 0.005000] 0.999998 0.000000 0.000000 -0.002014 for 2 players
            // - click minimap to teleport player 1 elsewhere in world
            // - /teleto <player 1 name> for player 2
            // - use facility hub portal gem for player 2 (49563)
            // - player 2 runs down the stairs, into the room with the torch
            // - player 1 waits 25s+ -- /knownplayers from player 1 to confirm once player 2 has exited the destruction queue
            // - player 1 uses facility hub portal gem, runs to player 2
            // - expected: consistent visibility for both players on each others screens
            // - actual: player 2's dot will be on radar for player 1, but they will be invisible in 3d game world, floating weapon if they are wielding

            // after analyzing this bug from many different perspectives, i believe this is some kind of odd client bug
            // even resending the CO does nothing, as player 1's client does indeed know about player 2
            // a DO and then a CO is the only thing that fixes this issue (/objsend can help with this)
            // this part probably deviates from retail a bit, but is the equivalent automated fix

            var fixLevel = PropertyManager.GetLong("teleport_visibility_fix").Item;

            // disabled by default
            if (fixLevel < 1) return;

            if (Location.Cell == newPosition.Cell)
                return;

            var knownObjs = GetKnownObjects();

            if (fixLevel == 1)
            {
                // filter to players only
                knownObjs = knownObjs.Where(i => i is Player).ToList();
            }
            else if (fixLevel == 2)
            {
                // filter to creatures only
                knownObjs = knownObjs.Where(i => i is Creature).ToList();
            }

            foreach (var knownObj in knownObjs)
            {
                knownObj.PhysicsObj.ObjMaint.RemoveObject(PhysicsObj);

                if (knownObj is Player knownPlayer)
                    knownPlayer.RemoveTrackedObject(this, false);

                ObjMaint.RemoveObject(knownObj.PhysicsObj);
                RemoveTrackedObject(knownObj, false);
            }
        }
    }
}
