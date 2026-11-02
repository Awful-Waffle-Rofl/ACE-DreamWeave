using System;
using System.Collections.Generic;
using System.Threading;

using ACE.Entity;
using ACE.Entity.Enum;
using ACE.Server.Entity;
using ACE.Server.Managers;
using ACE.Server.Network.GameMessages;
using ACE.Server.Network.GameMessages.Messages;
using ACE.Server.Physics;

namespace ACE.Server.WorldObjects
{
    /// <summary>
    /// The teleport flush switch: teleport_flush_scope (0 off, 1 legacy, 2 ledger), teleport_flush_delete_pacing and
    /// teleport_flush_delete_rate, plus the [TELEPORT] / [TELEPORT_DRAIN] diagnostics.
    ///
    /// SHIPS AS A NO-OP. Under the defaults (scope 1, pacing off) Teleport runs the literal legacy two lines, the
    /// NetworkSession hook only does bookkeeping, every other arm here is gated off, and no client-visible message
    /// or ordering changes. Nothing here ever holds or delays a message other than the paced flush's own deletes.
    /// The one exception on every scope: on an instance change, the residual broadcast-link cut
    /// (CutResidualBroadcastLinks) runs after the scope's own flush.
    /// </summary>
    partial class Player
    {
        /// <summary>Per-teleport counts for the [TELEPORT] line.</summary>
        public struct TeleportFlushCounts
        {
            /// <summary>Known objects whose own-client delete went out during the teleport.</summary>
            public int DeletedNow;

            /// <summary>Known objects whose own-client delete was queued for paced release.</summary>
            public int Deferred;

            /// <summary>Observers told to delete the teleporter.</summary>
            public int ObserverDeletes;

            /// <summary>Known objects whose tracking was cut silently (no client delete).</summary>
            public int Purged;
        }

        private ClientObjectBook clientObjectBook;

        /// <summary>
        /// This player's client object ledger and paced-delete queue. Created on first use, from any thread (the
        /// NetworkSession hook runs on many); exactly one instance is ever published. Read on every create, delete and
        /// pickup, so the fast path is a plain field read with no allocation.
        /// </summary>
        public ClientObjectBook ClientObjectBook => clientObjectBook ?? CreateClientObjectBook();

        private ClientObjectBook CreateClientObjectBook()
        {
            var created = new ClientObjectBook(Guid.Full);

            return Interlocked.CompareExchange(ref clientObjectBook, created, null) ?? created;
        }

        // cached once per player so the per-message paths below allocate no delegates
        private Func<uint, bool> ownsClientObject;
        private Action<uint> forgetEffectsByGuid;

        private Func<uint, bool> OwnsClientObjectCheck => ownsClientObject ??= OwnsClientObject;

        private Action<uint> ForgetEffectsByGuid => forgetEffectsByGuid ??= guid => VisualEffectManager.Forget(Session, guid);

        private void SendToOwnClient(GameMessage message) => Session.Network.EnqueueSend(message);

        /// <summary>
        /// The three settings, read once. Never throws: a failed read (a unit test has no shard config) is the
        /// shipped defaults, which is the legacy behaviour.
        /// </summary>
        public static TeleportFlushSettings ReadTeleportFlushSettings()
        {
            try
            {
                return new TeleportFlushSettings
                {
                    Scope = TeleportFlushPolicy.Normalize(PropertyManager.GetLong("teleport_flush_scope", TeleportFlushPolicy.DefaultScope).Item),
                    Pacing = PropertyManager.GetBool("teleport_flush_delete_pacing", TeleportFlushPolicy.DefaultPacing).Item,
                    Rate = TeleportFlushPolicy.NormalizeRate(PropertyManager.GetLong("teleport_flush_delete_rate", TeleportFlushPolicy.DefaultRate).Item),
                };
            }
            catch (Exception)
            {
                return TeleportFlushSettings.Defaults;
            }
        }

        /// <summary>teleport_flush_scope alone, for the per-object paths. Same never-throws seam.</summary>
        public static TeleportFlushScope ReadTeleportFlushScope()
        {
            try
            {
                return TeleportFlushPolicy.Normalize(PropertyManager.GetLong("teleport_flush_scope", TeleportFlushPolicy.DefaultScope).Item);
            }
            catch (Exception)
            {
                return TeleportFlushPolicy.Normalize(TeleportFlushPolicy.DefaultScope);
            }
        }

        /// <summary>
        /// Every arm of the teleport flush EXCEPT scope 1 with pacing off, which Teleport runs as the literal legacy
        /// lines. Runs where the legacy flush runs: after HandlePreTeleportVisibility, before UpdatePlayerPosition.
        /// </summary>
        private TeleportFlushCounts RunTeleportFlushVariant(TeleportFlushAction action, ulong originInstancedLandblock, ulong destinationInstancedLandblock, out int ledgerFired)
        {
            var counts = default(TeleportFlushCounts);
            ledgerFired = 0;

            var destinationLandblock = TeleportFlushPolicy.LandblockOf(destinationInstancedLandblock);
            var destinationInstance = TeleportFlushPolicy.InstanceOf(destinationInstancedLandblock);

            if ((action & TeleportFlushAction.LegacyFlush) != 0)
                counts = FlushKnownObjectsForInstanceChange();

            if ((action & TeleportFlushAction.DeferredFlush) != 0)
                counts = DeferredFlushKnownObjects(originInstancedLandblock, destinationLandblock, destinationInstance);

            if ((action & TeleportFlushAction.SilentPurge) != 0)
                counts = SilentPurgeKnownObjects((action & TeleportFlushAction.ObserverDeletes) != 0);

            if ((action & TeleportFlushAction.LedgerFire) != 0)
                ledgerFired = FireClientObjectLedger(destinationLandblock, destinationInstance);

            return counts;
        }

        /// <summary>
        /// What a scope-2 teleport to this destination would delete from the ledger, without taking anything. For
        /// the [TELEPORT] line's ledger=shadow:N under scopes 0 and 1, so the arms can be compared on one log.
        /// </summary>
        private int ClientObjectLedgerShadow(ulong destinationInstancedLandblock)
        {
            var book = ClientObjectBook;
            var neighbourhood = TeleportFlushPolicy.Neighbourhood(TeleportFlushPolicy.LandblockOf(destinationInstancedLandblock));

            lock (book.SyncRoot)
                return book.Ledger.CountOtherCopies(neighbourhood, TeleportFlushPolicy.InstanceOf(destinationInstancedLandblock));
        }

        private int PendingTeleportDeletes()
        {
            var book = clientObjectBook;
            if (book == null)
                return 0;

            lock (book.SyncRoot)
                return book.PendingDeletes.Count;
        }

        /// <summary>
        /// Scope 1 with pacing on: FlushKnownObjectsForInstanceChange in the same iteration order, except that each
        /// object's own-client deletes are queued for paced release instead of sent. An object from another copy of
        /// the destination 3x3 (a collision) still takes the legacy path at once, as do waiting deletes from an
        /// earlier teleport that collide with this destination. Observer deletes are never paced.
        /// </summary>
        private TeleportFlushCounts DeferredFlushKnownObjects(ulong originInstancedLandblock, ushort destinationLandblock, uint destinationInstance)
        {
            var counts = default(TeleportFlushCounts);

            if (PhysicsObj?.ObjMaint == null)
                return counts;

            var observerDeletes = 0;

            TeleportFlushSteps.DeferredFlush(ClientObjectBook, GetKnownObjects(), TeleportFlushPolicy.Neighbourhood(destinationLandblock), destinationInstance,
                TeleportFlushPolicy.LandblockOf(originInstancedLandblock), TeleportFlushPolicy.InstanceOf(originInstancedLandblock), DateTime.UtcNow.Ticks,
                ClientObjectPlacement.TryGet,
                BuildFlushDeletes,
                knownObj =>
                {
                    // a collision: exactly the legacy per-object body
                    if (knownObj.PhysicsObj != null)
                    {
                        knownObj.PhysicsObj.ObjMaint.RemoveObject(PhysicsObj);
                        ObjMaint.RemoveObject(knownObj.PhysicsObj);
                    }

                    if (knownObj is Player collidingPlayer)
                    {
                        collidingPlayer.RemoveTrackedObject(this, false);
                        observerDeletes++;
                    }

                    RemoveTrackedObject(knownObj, false);
                },
                knownObj =>
                {
                    // deferred: its deletes are already queued, so cut the tracking now
                    if (knownObj.PhysicsObj != null)
                    {
                        knownObj.PhysicsObj.ObjMaint.RemoveObject(PhysicsObj);
                        ObjMaint.RemoveObject(knownObj.PhysicsObj);
                    }

                    if (knownObj is Player knownPlayer)
                    {
                        knownPlayer.RemoveTrackedObject(this, false);
                        observerDeletes++;
                    }

                    // the delete is as good as sent: a later create re-sends the effects whether or not it cancels it
                    ForgetObjectAndEquipped(knownObj);
                },
                SendToOwnClient,
                out counts.DeletedNow, out counts.Deferred);

            counts.ObserverDeletes = observerDeletes;
            return counts;
        }

        /// <summary>
        /// The same messages RemoveTrackedObject would send for <paramref name="knownObj"/>, built by the same
        /// constructor, keyed by guid: the object, then its trackable equipped items.
        /// </summary>
        private IEnumerable<KeyValuePair<uint, GameMessage>> BuildFlushDeletes(WorldObject knownObj)
        {
            var deletes = new List<KeyValuePair<uint, GameMessage>>
            {
                new KeyValuePair<uint, GameMessage>(knownObj.Guid.Full, new GameMessageDeleteObject(knownObj)),
            };

            if (knownObj is Creature creature)
            {
                foreach (var wieldedItem in creature.EquippedObjects.Values)
                {
                    if (SendsEquippedDelete(creature, wieldedItem))
                        deletes.Add(new KeyValuePair<uint, GameMessage>(wieldedItem.Guid.Full, new GameMessageDeleteObject(wieldedItem)));
                }
            }

            return deletes;
        }
        /// <summary>
        /// Scopes 0 and 2 on an instance change: cut the server-side tracking both ways and reset replayed-effect
        /// bookkeeping, sending no client deletes. Without the cut, ObjectMaint (keyed by bare guid) would refuse
        /// the destination copy's same-guid objects. With <paramref name="observerDeletes"/> (scope 2) every
        /// observer is still told to delete the teleporter, as the legacy flush does: the instance guard is
        /// insert-only, so an old-copy observer would otherwise keep a ghost of the teleporter.
        /// </summary>
        private TeleportFlushCounts SilentPurgeKnownObjects(bool observerDeletes)
        {
            var counts = default(TeleportFlushCounts);

            if (PhysicsObj?.ObjMaint == null)
                return counts;

            foreach (var knownObj in GetKnownObjects())
            {
                if (knownObj.PhysicsObj != null)
                {
                    knownObj.PhysicsObj.ObjMaint.RemoveObject(PhysicsObj);
                    ObjMaint.RemoveObject(knownObj.PhysicsObj);
                }

                if (knownObj is Player knownPlayer)
                {
                    if (observerDeletes)
                    {
                        knownPlayer.RemoveTrackedObject(this, false);
                        counts.ObserverDeletes++;
                    }
                    else
                        knownPlayer.ForgetObjectAndEquipped(this);
                }

                ForgetObjectAndEquipped(knownObj);
                counts.Purged++;
            }

            return counts;
        }

        /// <summary>
        /// Scope 2, every teleport: delete whatever this client still holds from another copy of the destination
        /// landblock or its 8 neighbours, before the arrival creates. Waiting paced deletes for those landblocks go
        /// first. Never deletes the player itself or anything it carries or wields.
        /// </summary>
        private int FireClientObjectLedger(ushort destinationLandblock, uint destinationInstance)
        {
            var book = ClientObjectBook;
            var neighbourhood = TeleportFlushPolicy.Neighbourhood(destinationLandblock);

            TeleportFlushSteps.SendCollidingPending(book, neighbourhood, destinationInstance, SendToOwnClient);

            return TeleportFlushSteps.FireLedger(book, neighbourhood, destinationInstance, OwnsClientObjectCheck, SendToOwnClient, ForgetEffectsByGuid);
        }

        /// <summary>
        /// Scope 2, from TrackObject before the CreateObject: walking (or being carried) into a landblock this client
        /// holds from another copy deletes that copy's objects first. Under scopes 0 and 1 it only reads the ledger.
        ///
        /// Reached from the parallel physics phase, so it must never stop the create that follows it: ownership checks
        /// are fail-safe (TeleportFlushSteps.OwnsFailSafe) and anything else that throws is logged and swallowed.
        /// </summary>
        private void DeleteOtherCopyBeforeCreate(WorldObject worldObject)
        {
            try
            {
                if (!ClientObjectPlacement.TryGet(worldObject, out var landblock, out var instance))
                    return;

                var book = ClientObjectBook;

                lock (book.SyncRoot)
                {
                    if (!book.Ledger.HoldsOtherCopy(landblock, instance))
                        return;
                }

                // read only on a conflict, never per create
                if (ReadTeleportFlushScope() != TeleportFlushScope.Ledger)
                    return;

                var fired = TeleportFlushSteps.FireLedger(book, new[] { landblock }, instance, OwnsClientObjectCheck, SendToOwnClient, ForgetEffectsByGuid);

                if (fired > 0)
                    log.Debug($"[TELEPORT_LEDGER] {Name} (0x{Guid.Full:X8}) entering landblock 0x{landblock:X4} instance 0x{instance:X8}: deleted {fired} object(s) held from another copy");
            }
            catch (Exception ex)
            {
                log.Error($"[TELEPORT_LEDGER] {Name} (0x{Guid.Full:X8}) other-copy check failed; the create still goes out", ex);
            }
        }

        /// <summary>
        /// Every scope, on an instance change, after the scope's own flush and before the relocate: cuts the
        /// broadcast links the flush cannot see because they are not in KnownObjects (ObjectMaint.CutResidualObservers;
        /// chiefly the pairs ObjectMaint.ExpireObject keeps after a one-way expiry), so nothing the player left behind
        /// keeps receiving its broadcasts. Only the DeleteObject to a cut observer that still draws the player follows
        /// the scope (TeleportFlushPolicy.ResidualObserverDeletes); it is sent at once, as observer deletes always are.
        /// Returns how many links were cut, for the [TELEPORT] line.
        /// </summary>
        private int CutResidualBroadcastLinks(TeleportFlushScope scope, bool pacing, ulong originInstancedLandblock, ulong destinationInstancedLandblock)
        {
            if (PhysicsObj?.ObjMaint == null || !TeleportFlushPolicy.CutsResidualObservers(originInstancedLandblock, destinationInstancedLandblock))
                return 0;

            var cut = PhysicsObj.ObjMaint.CutResidualObservers();

            if (cut.Count == 0)
                return 0;

            TeleportFlushSteps.ApplyResidualObservers(cut, TeleportFlushPolicy.ResidualObserverDeletes(scope, pacing, originInstancedLandblock, destinationInstancedLandblock),
                observer =>
                {
                    if (observer.WeenieObj?.WorldObject is Player observerPlayer)
                        observerPlayer.RemoveTrackedObject(this, false);
                },
                observer =>
                {
                    if (observer.WeenieObj?.WorldObject is Player observerPlayer)
                        observerPlayer.ForgetObjectAndEquipped(this);
                });

            return cut.Count;
        }

        /// <summary>
        /// The 25s server-side cull (ObjectMaint.DestroyObjects) mirrors the client destroying objects that stayed out
        /// of view, which drops their emitters, so their effects must be re-sent when they come back. Gated off under
        /// scope 1, which keeps the legacy behaviour exactly.
        /// </summary>
        public void OnClientObjectsCulled(List<PhysicsObj> expiredObjs)
        {
            if (expiredObjs == null || expiredObjs.Count == 0)
                return;

            if (ReadTeleportFlushScope() == TeleportFlushScope.Legacy)
                return;

            foreach (var obj in expiredObjs)
            {
                var wo = obj?.WeenieObj?.WorldObject;
                if (wo != null)
                    ForgetObjectAndEquipped(wo);
            }
        }

        /// <summary>
        /// Player tick, right after the queued actions run (so creates queued in the same tick cancel first): release
        /// paced flush deletes. Pacing off releases everything at once, even mid-teleport; pacing on waits for the
        /// teleport to complete, then releases teleport_flush_delete_rate per tick.
        /// </summary>
        public void DrainTeleportDeletes()
        {
            var book = clientObjectBook;
            if (book == null || !book.DrainCycleOpen)
                return;

            var settings = ReadTeleportFlushSettings();

            var end = TeleportFlushSteps.Drain(book, settings.Pacing, Teleporting, settings.Rate, SendToOwnClient, out var ended);

            if (end != null)
                LogTeleportDrain(ended, end, settings.Rate);
        }

        /// <summary>Logout: waiting deletes are dropped, never sent.</summary>
        public void DiscardTeleportDeletes()
        {
            var book = clientObjectBook;
            if (book == null || !book.DrainCycleOpen)
                return;

            if (TeleportFlushSteps.Discard(book, out var ended))
                LogTeleportDrain(ended, "logout", ReadTeleportFlushSettings().Rate);
        }
        private void LogTeleportDrain(TeleportDeleteCycle cycle, string end, int rate)
        {
            var durationMs = cycle.StartTicks == 0 ? 0 : (long)TimeSpan.FromTicks(DateTime.UtcNow.Ticks - cycle.StartTicks).TotalMilliseconds;

            log.Info($"[TELEPORT_DRAIN] {Name} (0x{Guid.Full:X8}) end={end} sent={cycle.Sent} cancelled={cycle.Cancelled} " +
                     $"deferred={cycle.Deferred} teleports={cycle.Teleports} durationMs={durationMs} rate={rate}");
        }

        /// <summary>The player itself, or anything it carries or wields: never deleted from its own client by the ledger.
        /// Callers go through TeleportFlushSteps.OwnsFailSafe, which counts a throw as owned.</summary>
        private bool OwnsClientObject(uint guid)
        {
            if (guid == Guid.Full)
                return true;

            // direct lookups first: no enumeration, so cheap and the least exposed to a concurrent write
            if (EquippedObjects.ContainsKey(new ObjectGuid(guid)) || Inventory.ContainsKey(new ObjectGuid(guid)))
                return true;

            // side packs (enumerates Inventory; may throw from a foreign thread, which callers count as owned)
            return GetInventoryItem(guid) != null;
        }
        /// <summary>
        /// The RemoveTrackedEquippedObject filter: the equipped items whose delete this player's client is sent when
        /// <paramref name="wielder"/> is deleted.
        /// </summary>
        private bool SendsEquippedDelete(Creature wielder, WorldObject wieldedItem)
        {
            if (((wieldedItem.ValidLocations ?? 0) & EquipMask.SelectablePlusAmmo) == 0)
                return false;

            return wielder != this;
        }

        /// <summary>VisualEffectManager.Forget for an object and, when it is a creature, its trackable equipped items.</summary>
        private void ForgetObjectAndEquipped(WorldObject wo)
        {
            VisualEffectManager.Forget(Session, wo);

            if (wo is Creature creature)
            {
                foreach (var wieldedItem in creature.EquippedObjects.Values)
                {
                    if (SendsEquippedDelete(creature, wieldedItem))
                        VisualEffectManager.Forget(Session, wieldedItem);
                }
            }
        }

        /// <summary>The book's numbers for Session.DropSession's line: waiting paced deletes and ledger evictions.</summary>
        public void GetClientObjectBookDiagnostics(out int pendingDeletes, out long ledgerEvictions)
        {
            pendingDeletes = 0;
            ledgerEvictions = 0;

            var book = clientObjectBook;
            if (book == null)
                return;

            lock (book.SyncRoot)
            {
                pendingDeletes = book.PendingDeletes.Count;
                ledgerEvictions = book.Ledger.Evictions;
            }
        }
    }
}
