using System;

using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Server.Entity;
using ACE.Server.Physics;
using ACE.Server.Physics.Common;

using Position = ACE.Entity.Position;

namespace ACE.Server.WorldObjects
{
    /// <summary>
    /// World events sky-drop (WaffleACE, WP-17). The runtime state and the landing half of the mechanism
    /// whose two public fields live on Creature.cs: a wave creature placed above the terrain by
    /// WorldEventSpawner falls under gravity, and this is what hands it back to the ordinary monster tick
    /// once it is down.
    ///
    /// Nothing here runs for any creature that was not sky-dropped: WorldEventSkyDrop is only ever set by
    /// <see cref="ArmSkyDrop"/>, which only WorldEventSpawner calls, for Wave spawns of a theme whose
    /// spawnDz is greater than 0.
    ///
    /// The creature is deliberately NOT woken while it falls (see ArmSkyDrop) - the wake happens on landing,
    /// in <see cref="OnSkyDropLanded"/>.
    /// </summary>
    partial class Creature
    {
        /// <summary>Seconds between the per-drop progress lines. Diagnostics only.</summary>
        public const double SkyDropProgressLogInterval = 2.0;

        /// <summary>PhysicsTimer.CurrentTime at which the drop was armed. Used for the elapsed figure.</summary>
        public double WorldEventSkyDropArmedAt;

        /// <summary>
        /// Set by <see cref="OnSkyDropLanded"/>, consumed by the first Monster_Tick afterwards. Target
        /// acquisition is DEFERRED to that tick rather than run from the physics tick - see
        /// <see cref="CompleteSkyDropAcquire"/> for why.
        /// </summary>
        public bool WorldEventSkyDropPendingAcquire;

        private double skyDropNextProgressLog;

        private bool skyDropInactiveLogged;

        private int skyDropReactivations;

        /// <summary>
        /// Arms a sky drop on a creature that has just entered the world above the terrain. Called by
        /// WorldEventSpawner immediately after a successful Adopt, on the landblock thread.
        ///
        /// The creature is NOT woken here, and that is deliberate. WakeUp runs EmoteManager.OnWakeUp and
        /// OnNewEnemy (Monster_Awareness.cs:34-35), which can start an emote or a motion on a creature that
        /// is still in the air; four physics entry points create a MovementManager lazily and CLEAR
        /// TransientStateFlags.Active as they do it (Physics/PhysicsObj.cs:957-962, 978-984, 1603-1608,
        /// 1634-1639), so waking mid-fall puts avoidable ways to stop the fall on the critical path for no
        /// benefit. Nothing in the drop needs the creature awake:
        ///
        ///   * the physics update is forced by WorldObject.ShouldRunCreaturePhysics's skyDrop term, which
        ///     ignores IsAwake entirely;
        ///   * gravity comes from PhysicsObj.calc_acceleration, which reads only TransientState;
        ///   * Monster_Tick returns at its own "not awake" block, ABOVE the sky-drop hold, so an asleep
        ///     dropping creature already does nothing (the hold covers the case where a player wakes it
        ///     mid-fall by attacking it in the air);
        ///   * the cachedVelocityFix that would otherwise fire for an asleep monster is excluded for a
        ///     dropping one in WorldObject_Tick.
        ///
        /// It is woken on landing instead, which is the moment the owner's "force-awake" requirement is
        /// actually about: it must be alert and hostile the instant it can act.
        /// </summary>
        public void ArmSkyDrop(double now, double timeoutSeconds)
        {
            WorldEventSkyDrop = true;
            WorldEventSkyDropArmedAt = now;
            WorldEventSkyDropDeadline = now + timeoutSeconds;
            WorldEventSkyDropPendingAcquire = false;

            skyDropNextProgressLog = now;
            skyDropInactiveLogged = false;
            skyDropReactivations = 0;

            // The drop must not depend on the Active flag having survived EnterWorld, so assert it once here
            // with the engine's own setter (Physics/PhysicsObj.cs:3455) rather than assuming.
            PhysicsObj?.set_active(true);
        }

        /// <summary>
        /// Called from WorldObject_Tick.UpdateObjectPhysics when a creature with the sky-drop flag is found
        /// with its physics inactive, BEFORE the early return that would otherwise skip it. Returns the
        /// value UpdateObjectPhysics should return (always false - no landblock change happened).
        ///
        /// On walkable ground this is a landing. In the air it is a physics path having cleared
        /// TransientStateFlags.Active on an object that is not moving under its own power, which for a
        /// falling creature would freeze it in mid-air server-side while the client carries on simulating
        /// the fall - so activity is re-armed and the drop continues. The first occurrence per creature is
        /// logged; later ones are counted and reported on the landing line instead of spamming.
        /// </summary>
        public bool HandleSkyDropPhysicsInactive()
        {
            if (!WorldEventSkyDrop || PhysicsObj == null)
                return false;

            var onWalkable = (PhysicsObj.TransientState & TransientStateFlags.OnWalkable) != 0;

            if (!WorldObject.SkyDropShouldReactivate(false, onWalkable))
            {
                OnSkyDropLanded(timedOut: false, reason: "inactive-walkable");
                return false;
            }

            skyDropReactivations++;

            if (!skyDropInactiveLogged)
            {
                skyDropInactiveLogged = true;

                log.Info($"[WORLDEVENT] skydrop inactive guid=0x{Guid.Full:X8} z={Location?.PositionZ:F2} " +
                         $"vz={PhysicsObj.Velocity.Z:F2} ts={PhysicsObj.TransientState} awake={IsAwake} " +
                         $"mvmgr={PhysicsObj.MovementManager != null} reactivating");
            }

            // The deadline stays the hard floor: if re-arming does not get the creature down, this is where
            // the timeout fires, because the ordinary settle check below never runs on an inactive object.
            if (PhysicsTimer.CurrentTime >= WorldEventSkyDropDeadline)
            {
                OnSkyDropLanded(timedOut: true, reason: "deadline-inactive");
                return false;
            }

            PhysicsObj.set_active(true);

            return false;
        }

        /// <summary>
        /// One diagnostic line every <see cref="SkyDropProgressLogInterval"/> seconds while a creature is
        /// falling. A frozen z across consecutive lines is the signature of a stalled drop; a falling z that
        /// never produces a landing line is a stalled SETTLE check.
        /// </summary>
        public void LogSkyDropProgress()
        {
            if (!WorldEventSkyDrop || PhysicsObj == null)
                return;

            var now = PhysicsTimer.CurrentTime;

            if (now < skyDropNextProgressLog)
                return;

            skyDropNextProgressLog = now + SkyDropProgressLogInterval;

            log.Info($"[WORLDEVENT] skydrop tick guid=0x{Guid.Full:X8} z={Location?.PositionZ:F2} " +
                     $"vz={PhysicsObj.Velocity.Z:F2} ts={PhysicsObj.TransientState} active={PhysicsObj.is_active()} " +
                     $"awake={IsAwake} t={now - WorldEventSkyDropArmedAt:F1}s");
        }

        /// <summary>
        /// Called from WorldObject_Tick.UpdateObjectPhysics, on the landblock thread, the first time a
        /// dropping creature is seen standing on walkable ground (<paramref name="timedOut"/> false) or its
        /// deadline passes without that ever happening (<paramref name="timedOut"/> true).
        ///
        /// On the timeout path the creature is put on the terrain by force: its Z is snapped with
        /// AdjustMapCoords (the same snap WorldEventSpawner.TryPlace used to find the ground in the first
        /// place) and the move is applied through Creature.FakeTeleport, which is the engine's own
        /// same-landblock reposition - physics SetPosition, SyncLocation, then SendUpdatePosition(true)
        /// (Creature_Navigation.cs:420-449). That broadcast IS the timeout path's resync; the normal path
        /// sends its own.
        ///
        /// Then, in order: the home position is re-stamped, the visible-target set is rebuilt, the creature
        /// is woken, and target acquisition is deferred to its next Monster_Tick.
        /// </summary>
        public void OnSkyDropLanded(bool timedOut, string reason)
        {
            if (!WorldEventSkyDrop)
                return;

            WorldEventSkyDrop = false;

            var elapsed = PhysicsTimer.CurrentTime - WorldEventSkyDropArmedAt;

            if (timedOut)
            {
                ForceSkyDropToGround();
            }
            else
            {
                // Landed on its own. Clients simulated the fall themselves, so tell them where the server
                // says it finished rather than leaving the two copies to drift apart.
                SendUpdatePosition();
            }

            // WorldObject.AddPhysicsObj stamps PositionType.Home from Location as the object enters the
            // world (WorldObject.cs:228), which for a sky drop is the point 20 m UP. Left alone, every later
            // MoveToHome would path the creature back into the sky - including the one FindNextTarget makes
            // when it sees no targets. Re-stamped here so its home is where it actually stands, exactly as
            // it would be for a creature spawned on the ground.
            //
            // NOTE this is hardening, not the diagnosed cause of the first live failure: CheckMissHome only
            // fires past HomeRadius, which defaults to 192 m (Monster_Navigation.cs:432) and is unset on the
            // test wave weenie, so a 20 m offset never tripped it.
            if (Location != null)
                SetPosition(PositionType.Home, new Position(Location));

            // A creature that arrives by FALLING crosses no land cell (outdoor cells are X/Y columns), so
            // the cell-transition path that normally refreshes a monster's visible targets -
            // PhysicsObj.enter_cell_server -> handle_visible_cells_non_player (Physics/PhysicsObj.cs:2408,
            // 2789) - has not run since the spawn point in the air. Re-run the engine's own routine rather
            // than inventing a refresh: for a monster it rebuilds the attack-target set from the current
            // cell.
            PhysicsObj?.handle_visible_cells_non_player();

            // The owner's "force-awake" requirement, applied at the moment it means something. Not done
            // while falling - see ArmSkyDrop.
            if (!IsAwake)
                WakeUp(alertNearby: false);

            // Acquisition itself is deferred - see CompleteSkyDropAcquire.
            WorldEventSkyDropPendingAcquire = true;

            log.Info($"[WORLDEVENT] skydrop landed guid=0x{Guid.Full:X8} reason={reason} timedOut={timedOut} " +
                     $"elapsed={elapsed:F2}s reactivations={skyDropReactivations} loc={Location?.ToLOCString()} " +
                     $"awake={IsAwake} state={MonsterState}");
        }

        /// <summary>
        /// The timeout half of the landing: snap to terrain and reposition through the engine's own
        /// same-landblock teleport. Split out so <see cref="OnSkyDropLanded"/> reads as a sequence.
        /// </summary>
        private void ForceSkyDropToGround()
        {
            if (Location == null || PhysicsObj == null)
                return;

            var ground = new Position(Location);

            try
            {
                if (!ground.Indoors)
                    ground.AdjustMapCoords();
            }
            catch (Exception ex)
            {
                log.Error($"[WORLDEVENT] skydrop timeout guid=0x{Guid.Full:X8} could not snap to terrain at {Location.ToLOCString()}", ex);
                return;
            }

            // FakeTeleport broadcasts the move itself (SendUpdatePosition(true)), so no second resync here.
            FakeTeleport(ground);

            log.Warn($"[WORLDEVENT] skydrop timeout guid=0x{Guid.Full:X8} forced to ground at {Location?.ToLOCString()}");
        }

        /// <summary>
        /// Runs on the first Monster_Tick after a landing, NOT from the physics tick that produced it.
        ///
        /// FindNextTarget is not safe to call from inside UpdateObjectPhysics. Three reasons, all from its
        /// own body (Monster_Awareness.cs:167-190): with no visible target it calls MoveToHome, which issues
        /// MoveTo and PhysicsObj.MoveToPosition (Monster_Navigation.cs:487-494) and would therefore mutate
        /// this same PhysicsObj's MovementManager re-entrantly, from inside its own physics update; it calls
        /// EmoteManager.OnNewEnemy, which enqueues emote chains; and it calls SetNextTargetTime, which pushes
        /// NextFindTarget 5 s into the future and so would SUPPRESS the ordinary HandleFindTarget for the
        /// next 5 s - delaying the very aggro it was meant to produce.
        ///
        /// So the physics tick only raises a flag, and the acquisition happens here, on the monster tick,
        /// where every one of those calls is already the normal thing to do. If it still finds nothing the
        /// ordinary tick takes over unchanged - no special case for sleeping.
        /// </summary>
        public void CompleteSkyDropAcquire()
        {
            WorldEventSkyDropPendingAcquire = false;

            if (!IsAwake)
                WakeUp(alertNearby: false);

            var visible = PhysicsObj?.ObjMaint?.GetVisibleTargetsValuesOfTypeCreature().Count ?? -1;

            var found = FindNextTarget();

            log.Info($"[WORLDEVENT] skydrop acquire guid=0x{Guid.Full:X8} visibleTargets={visible} found={found} " +
                     $"target={AttackTarget?.Name ?? "none"} awake={IsAwake} state={MonsterState}");
        }
    }
}
