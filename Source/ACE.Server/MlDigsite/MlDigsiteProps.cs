using System;
using System.Reflection;

using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Server.Entity;
using ACE.Server.Entity.Actions;
using ACE.Server.Factories;
using ACE.Server.Managers;
using ACE.Server.WorldObjects;

using log4net;

using Position = ACE.Entity.Position;

namespace ACE.Server.MlDigsite
{
    /// <summary>
    /// THE one placement helper for everything a digsite encounter puts into the world that is NOT a creature
    /// and NOT a reward chest: the Boss Rush mechanics' telegraph markers, safe-zone lights and the interrupt
    /// object. It is <see cref="MlDigsiteSpawner"/>'s counterpart, and it follows the same fixed order for the
    /// same reasons:
    ///
    ///     create -> fail closed on a null -> terrain snap in try/catch -> ALL stamps -> AddHeld ->
    ///     EnterWorld -> report
    ///
    /// EVERY PROPERTY WRITE HAPPENS BEFORE EnterWorld. No landblock save and no client create packet may ever
    /// see one of these as an ordinary persistable object.
    ///
    /// TimeToRot = -1 IS MANDATORY, exactly as it is for a creature: a prop is owned by its encounter, which
    /// destroys it at Finish, and the landblock decay pass must not take it first. A prop is ALSO registered
    /// through <see cref="MlDigsiteEncounter.AddHeld"/> before it enters the world, so
    /// MlDigsiteManager.DestroyHeld removes it whatever ends the encounter - and it carries
    /// PropertyInt.MlDigsiteEncounterId, so the persistence exclusion keeps it out of the shard and
    /// MlDigsiteOrphanFilter sweeps it if a hard restart ever leaves one behind. A leaked prop standing on a
    /// live, shared, persistent island landblock with TimeToRot = -1 is the failure mode this whole file is
    /// arranged around.
    ///
    /// The reward chest deliberately does NOT go through here: it is the one digsite object that must OUTLIVE
    /// its encounter, so it takes a finite TimeToRot and stays out of the held list (MlDigsiteRewards).
    ///
    /// THREADING. Every method here is called from the mechanic driver, which runs on the WORLD thread, and
    /// the world thread owns no landblock - Landblock.AddWorldObjectInternal detects a foreign-thread entry,
    /// logs it and warns it may still crash. So both the build and the destroy are handed to the anchor
    /// landblock's own action queue, and the WHOLE build is queued rather than just the EnterWorld call, the
    /// same discipline MlDigsiteRewards.SpawnChests applies for the same reason. Placement is therefore
    /// ASYNCHRONOUS: a caller that needs the object back gets it through <paramref name="onPlaced"/>, which
    /// runs on the landblock thread.
    /// </summary>
    public static class MlDigsiteProps
    {
        private static readonly ILog log = LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);

        /// <summary>
        /// Queues one prop for placement at <paramref name="position"/> and reports whether the work was
        /// queued at all (NOT whether the object landed - see the class remarks). A wcid of 0 places nothing
        /// and is how an operator turns markers off without turning the mechanic off.
        ///
        /// <paramref name="scriptId"/> is stamped as PropertyDataId.PhysicsScript when non-zero, so the prop
        /// carries its looping visual in its own CreateObject packet rather than needing a separate send. 0
        /// leaves the weenie's own.
        ///
        /// <paramref name="onPlaced"/> IS ALWAYS INVOKED once the build has run, with the object, or with
        /// NULL when the build failed. A caller that opened something against this object - the interrupt
        /// window is the case - has to be able to close it again, and by the time the build runs the caller's
        /// own call has long since returned true. The concrete failure this covers is the weenie not being
        /// applied to the world database: without it the interrupt window would count down to an unavoidable
        /// hit with nothing in the world for anyone to use.
        /// </summary>
        public static bool TryPlace(MlDigsiteEncounter encounter, uint wcid, Position position, uint scriptId = 0,
            Action<WorldObject> onPlaced = null)
        {
            if (encounter == null || wcid == 0 || position == null || !encounter.RunValid)
                return false;

            var anchor = encounter.Anchor;

            if (anchor == null || !LandblockManager.IsLoaded(anchor.LandblockId, anchor.Instance))
                return false;

            var landblock = LandblockManager.GetLandblock(anchor.LandblockId, anchor.Instance, false);

            if (landblock == null)
            {
                log.Warn($"[ML_DIGSITE] {encounter} could not resolve the anchor landblock; prop wcid {wcid} not placed");
                return false;
            }

            var target = new Position(position);

            landblock.EnqueueAction(new ActionEventDelegate(() =>
            {
                // The driver's own try/catch cannot cover this: the queued work runs after the tick has moved
                // on, so an escape here would land in the landblock tick instead.
                WorldObject placed = null;

                try
                {
                    placed = Build(encounter, wcid, target, scriptId);
                }
                catch (Exception ex)
                {
                    log.Error($"[ML_DIGSITE] {encounter} prop wcid {wcid} build threw on the landblock queue", ex);
                }

                // OUTSIDE the build's catch and unconditional: a caller told to expect this call has to hear
                // about a failure as well as a success, and a throw from the callback must not be reported as
                // a build failure.
                try
                {
                    onPlaced?.Invoke(placed);
                }
                catch (Exception ex)
                {
                    log.Error($"[ML_DIGSITE] {encounter} prop wcid {wcid} placement callback threw", ex);
                }
            }));

            return true;
        }

        /// <summary>
        /// One prop build, which ALWAYS runs on the anchor landblock's own thread - see
        /// <see cref="TryPlace"/>, which is the only caller and the only thing that decides that. Every early
        /// return destroys the object rather than leaving a stamped one stranded outside the world.
        /// </summary>
        private static WorldObject Build(MlDigsiteEncounter encounter, uint wcid, Position position, uint scriptId)
        {
            if (!encounter.RunValid)
                return null;

            var wo = WorldObjectFactory.CreateNewWorldObject(wcid);

            if (wo == null)
            {
                log.Error($"[ML_DIGSITE] {encounter} prop wcid {wcid} failed to create; is its weenie applied to this world database?");
                return null;
            }

            // Fail closed on a Creature. A creature placed through here would carry no tether, would not be
            // tracked by the encounter's creature bookkeeping, and its death would be reported as a kind
            // nothing handles - so it is refused rather than half-placed.
            if (wo is Creature)
            {
                log.Error($"[ML_DIGSITE] {encounter} prop wcid {wcid} is a Creature; props are placed through MlDigsiteSpawner, not here");
                wo.Destroy();
                return null;
            }

            if (position.Indoors)
            {
                // A dig site is an outdoor terrain cell by construction (TreasureMapHandler refuses to read a
                // map indoors), so this is unreachable today. Refusing rather than snapping keeps it that way:
                // AdjustMapCoords has no terrain to consult for an indoor cell.
                log.Warn($"[ML_DIGSITE] {encounter} refusing an indoor prop point at {position.ToLOCString()}");
                wo.Destroy();
                return null;
            }

            try
            {
                position.AdjustMapCoords();
            }
            catch (Exception ex)
            {
                log.Error($"[ML_DIGSITE] {encounter} prop terrain snap failed at {position.ToLOCString()}", ex);
                wo.Destroy();
                return null;
            }

            wo.Location = position;

            // ---- every stamp, before EnterWorld ------------------------------------------------------------

            // The persistence exclusion AND the orphan filter's key, the same stamp every digsite object gets.
            wo.SetProperty(PropertyInt.MlDigsiteEncounterId, (int)encounter.EncounterId);

            // Mandatory - see the class remarks. The encounter owns this object's cleanup.
            wo.TimeToRot = -1;

            if (scriptId != 0)
                wo.DefaultScriptId = scriptId;

            // ---- adoption, then the world ------------------------------------------------------------------

            // BEFORE EnterWorld, so there is no instant at which a live prop stands in the world without the
            // encounter knowing it must destroy it. A false return means the held list is already closed and
            // this object would be orphaned, so it is destroyed here instead.
            if (!encounter.AddHeld(wo))
            {
                log.Debug($"[ML_DIGSITE] {encounter} refused a prop: the held list is closed; destroying wcid {wcid}");
                wo.Destroy();
                return null;
            }

            bool entered;

            try
            {
                entered = wo.EnterWorld();
            }
            catch (Exception ex)
            {
                log.Error($"[ML_DIGSITE] {encounter} prop EnterWorld threw for wcid {wcid} at {position.ToLOCString()}", ex);
                entered = false;
            }

            if (!entered)
            {
                log.Warn($"[ML_DIGSITE] {encounter} prop wcid {wcid} failed to enter the world at {position.ToLOCString()}");
                wo.Destroy();
                return null;
            }

            return wo;
        }

        /// <summary>
        /// Takes one prop back out of the world early - a telegraph marker whose window has passed, an
        /// interrupt object that was used. Destroying it on ITS OWN landblock's queue, for the same reason the
        /// build is queued.
        ///
        /// An object left to <see cref="MlDigsiteManager"/>'s end-of-encounter cleanup instead is not a leak,
        /// only litter until the fight ends: it is already in the held list and already stamped. This exists
        /// so a safe-zone cycle does not leave the previous cycle's lights standing next to the new ones.
        /// </summary>
        public static void Remove(WorldObject wo)
        {
            if (wo == null || wo.IsDestroyed)
                return;

            try
            {
                var landblock = wo.CurrentLandblock;
                var target = wo;

                if (landblock == null)
                {
                    target.Destroy();
                    return;
                }

                landblock.EnqueueAction(new ActionEventDelegate(() =>
                {
                    if (!target.IsDestroyed)
                        target.Destroy();
                }));
            }
            catch (Exception ex)
            {
                log.Error($"[ML_DIGSITE] prop removal threw for 0x{wo.Guid.Full:X8}", ex);
            }
        }

        /// <summary>
        /// A one-shot visual played on an object already in the world - the red flare on a volatile add that
        /// has just died, the shield going up and down on an immune boss. Queued on the object's own landblock
        /// like every other write from the world thread, and best-effort: a missed flare costs a visual cue,
        /// never the mechanic, which always also sends a chat line.
        /// </summary>
        public static void PlayScriptOn(WorldObject wo, PlayScript script)
        {
            if (wo == null || wo.IsDestroyed)
                return;

            try
            {
                var landblock = wo.CurrentLandblock;
                var target = wo;

                if (landblock == null)
                    return;

                landblock.EnqueueAction(new ActionEventDelegate(() =>
                {
                    try
                    {
                        if (!target.IsDestroyed)
                            target.EnqueueBroadcast(new Network.GameMessages.Messages.GameMessageScript(target.Guid, script));
                    }
                    catch (Exception ex)
                    {
                        log.Error($"[ML_DIGSITE] prop script broadcast threw for 0x{target.Guid.Full:X8}", ex);
                    }
                }));
            }
            catch (Exception ex)
            {
                log.Error($"[ML_DIGSITE] prop script queue threw for 0x{wo.Guid.Full:X8}", ex);
            }
        }
    }
}
