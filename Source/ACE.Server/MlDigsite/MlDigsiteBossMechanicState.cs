using System;
using System.Collections.Generic;

using ACE.Server.WorldObjects;

using Position = ACE.Entity.Position;

namespace ACE.Server.MlDigsite
{
    /// <summary>
    /// ALL mutable state the Boss Rush mechanic driver holds for one encounter, and the only place any of it
    /// lives. The five mechanic modules are STATELESS SINGLETONS - the same rule
    /// MonsterEffectHooks.cs states for effect handlers, and for the same reason: a module is shared by every
    /// live encounter at once, so anything it remembered would be remembered for all of them.
    ///
    /// LOCKING. Every field is guarded by <see cref="sync"/> and every accessor takes it, because this object
    /// is touched from two threads: the WORLD thread (MlDigsiteManager.Tick -> the driver) and whichever
    /// LANDBLOCK thread killed an add (Creature.Die -> OnEncounterCreatureDied). The immune phase's add-guid
    /// set is the concrete case - the driver fills it and the death hook empties it - and getting it wrong
    /// would be silent: a phase that never ends, or one that ends early.
    ///
    /// NOTHING HERE IS PERSISTED. It dies with the process, which is correct: a restart ends the encounter
    /// anyway, and MlDigsiteOrphanFilter sweeps anything the encounter had placed.
    ///
    /// THE STEP QUEUE IS THE ONLY SCHEDULER. Every delayed action a mechanic wants - a drum beat, a fuse, a
    /// telegraph resolving, a marker being cleared - is a (due time, action) pair evaluated on the digsite's
    /// existing 1 s tick. Deliberately NOT an ActionChain and NOT Landblock.EnqueueAction with a delay: both
    /// would outlive an encounter that has already destroyed the objects the step names, and EnqueueAction
    /// escapes the caller's try/catch. Keeping every step here means one guarded loop owns all of them and
    /// <see cref="ClearSteps"/> is a complete stop.
    ///
    /// GRANULARITY IS 1 SECOND and that is a design decision, not an accident (owner ruling 2026-09-20): a
    /// second, faster clock would be real per-tick cost across every live encounter for marginal feel, so
    /// every cadence is designed around a 1 s beat.
    /// </summary>
    public sealed class MlDigsiteBossMechanicState
    {
        private readonly object sync = new object();

        public MlDigsiteBossMechanicState(MlDigsiteMechanicSet set, DateTime startedUtc)
        {
            Set = set;

            mainDueUtc = startedUtc;
            secondaryDueUtc = startedUtc;
        }

        /// <summary>
        /// Which set this encounter rolled. Immutable for the life of the encounter - the roll happens once,
        /// in MlDigsiteManager.OpenEncounter, before the boss can have been hit.
        /// </summary>
        public MlDigsiteMechanicSet Set { get; }

        // ---- the step queue ------------------------------------------------------------------------------

        private readonly List<PendingStep> steps = new List<PendingStep>();

        private readonly struct PendingStep
        {
            public PendingStep(DateTime dueUtc, Action step)
            {
                DueUtc = dueUtc;
                Step = step;
            }

            public DateTime DueUtc { get; }

            public Action Step { get; }
        }

        /// <summary>
        /// Queues one delayed action. Ignored once the queue has been cleared (the encounter is finishing), so
        /// a step scheduled by another step on the same tick cannot resurrect a stopped driver.
        /// </summary>
        public void Schedule(DateTime dueUtc, Action step)
        {
            if (step == null)
                return;

            lock (sync)
            {
                if (stopped)
                    return;

                steps.Add(new PendingStep(dueUtc, step));
            }
        }

        /// <summary>
        /// Every step now due, IN THE ORDER THEY WERE SCHEDULED, removed from the queue. Returns null - not an
        /// empty list - when nothing is due, which is the common case on every tick of every encounter and is
        /// why this allocates nothing at all there.
        ///
        /// Order is load bearing: the drum cadence schedules beat 1, beat 2 and the resolve up front, and a
        /// tick that catches two of them at once must still run them in that order.
        /// </summary>
        public List<Action> TakeDueSteps(DateTime now)
        {
            lock (sync)
            {
                if (steps.Count == 0)
                    return null;

                List<Action> due = null;
                var keep = 0;

                for (var i = 0; i < steps.Count; i++)
                {
                    if (steps[i].DueUtc <= now)
                    {
                        due = due ?? new List<Action>();
                        due.Add(steps[i].Step);
                    }
                    else
                    {
                        steps[keep++] = steps[i];
                    }
                }

                if (due != null)
                    steps.RemoveRange(keep, steps.Count - keep);

                return due;
            }
        }

        private bool stopped;

        /// <summary>
        /// Drops every pending step and refuses any further one. Called from MlDigsiteManager's cleanup, so a
        /// step that would have detonated at a dead add's position, or hit players on behalf of a boss that
        /// has already been destroyed, simply never runs. Returns how many were dropped, for the log line.
        /// </summary>
        public int ClearSteps()
        {
            lock (sync)
            {
                stopped = true;

                var dropped = steps.Count;

                steps.Clear();

                return dropped;
            }
        }

        public int PendingStepCount
        {
            get { lock (sync) return steps.Count; }
        }

        // ---- per-slot cadence ----------------------------------------------------------------------------

        private DateTime mainDueUtc;
        private DateTime secondaryDueUtc;

        /// <summary>
        /// Whether this slot's mechanic is due to run, and arms the next one <paramref name="seconds"/> out
        /// from NOW rather than from the due time - so a driver that was starved (a landblock stall, a long
        /// reap) does not immediately fire a backlog of cycles.
        ///
        /// The FIRST claim for a new encounter always returns false and only arms the clock: a mechanic firing
        /// in the same instant the boss climbs out of the hole gives a player no time to read the opening
        /// line, let alone the mechanic.
        /// </summary>
        public bool TryClaimCadence(MlDigsiteMechanicSlot slot, DateTime now, double seconds)
        {
            if (!double.IsFinite(seconds) || seconds < 1.0)
                seconds = 1.0;

            lock (sync)
            {
                var due = slot == MlDigsiteMechanicSlot.Secondary ? secondaryDueUtc : mainDueUtc;

                if (now < due)
                    return false;

                var next = now + TimeSpan.FromSeconds(seconds);

                if (slot == MlDigsiteMechanicSlot.Secondary)
                {
                    var first = !secondaryArmed;
                    secondaryArmed = true;
                    secondaryDueUtc = next;

                    return !first;
                }

                var firstMain = !mainArmed;
                mainArmed = true;
                mainDueUtc = next;

                return !firstMain;
            }
        }

        private bool mainArmed;
        private bool secondaryArmed;

        // ---- the immune phase ----------------------------------------------------------------------------
        //
        // ONE phase state per encounter, not one per slot. No shipped set authors an immune mechanic in both
        // slots (set 3 is immunephases/drums, set 5 is safezones/immune50), and a set that did would be one
        // boss with two overlapping shells - which reads as a bug whichever way it resolves. Sharing the state
        // makes the second slot's threshold simply join the first's list instead.

        private readonly HashSet<int> immuneThresholdsFired = new HashSet<int>();
        private readonly HashSet<uint> phaseAddGuids = new HashSet<uint>();
        private bool phaseActive;
        private DateTime phaseDeadlineUtc;

        /// <summary>
        /// Latches one immune threshold. True the first time each one is crossed and false ever after, so a
        /// boss healed back over 50% (by its own leech, or by the tether heal) does not go immune at 50% twice.
        /// Keyed on the threshold rounded to whole percent, so 0.5 and 0.50 are the same latch.
        /// </summary>
        public bool TryLatchImmuneThreshold(double threshold)
        {
            lock (sync)
                return immuneThresholdsFired.Add((int)Math.Round(threshold * 100.0));
        }

        /// <summary>True while a phase is running, whichever slot started it.</summary>
        public bool ImmunePhaseActive
        {
            get { lock (sync) return phaseActive; }
        }

        /// <summary>
        /// Opens a phase over <paramref name="addGuids"/>, with a failsafe deadline. An EMPTY add set opens
        /// nothing and returns false: a phase whose adds all failed to spawn would be an unbreakable immunity
        /// with nothing to kill, which is strictly worse than no phase at all.
        /// </summary>
        public bool BeginImmunePhase(IReadOnlyList<uint> addGuids, DateTime deadlineUtc)
        {
            if (addGuids == null || addGuids.Count == 0)
                return false;

            lock (sync)
            {
                phaseAddGuids.Clear();

                foreach (var guid in addGuids)
                    phaseAddGuids.Add(guid);

                phaseActive = true;
                phaseDeadlineUtc = deadlineUtc;

                return true;
            }
        }

        /// <summary>
        /// Reports one add's death to the running phase. <paramref name="phaseCleared"/> is true only on the
        /// death that empties the set - so the "shell cracks" line and the flag clear happen exactly once.
        /// Returns false for an add that was not part of the phase (a volatile add, or one from a phase that
        /// already timed out), which the caller still handles as an ordinary add death.
        /// </summary>
        public bool NoteImmunePhaseAddDeath(uint guid, out bool phaseCleared)
        {
            lock (sync)
            {
                phaseCleared = false;

                if (!phaseActive || !phaseAddGuids.Remove(guid))
                    return false;

                if (phaseAddGuids.Count == 0)
                {
                    phaseActive = false;
                    phaseCleared = true;
                }

                return true;
            }
        }

        /// <summary>
        /// Whether the running phase has outlived its failsafe. Closes it as a side effect, so this can only
        /// report a timeout once. Without this, one add lost to terrain or to a landblock unload would hold
        /// the boss immortal until the encounter's own TTL.
        /// </summary>
        public bool TryTimeOutImmunePhase(DateTime now)
        {
            lock (sync)
            {
                if (!phaseActive || now < phaseDeadlineUtc)
                    return false;

                phaseActive = false;
                phaseAddGuids.Clear();

                return true;
            }
        }

        // ---- the interrupt window ------------------------------------------------------------------------

        private WorldObject interruptObject;
        private DateTime interruptDeadlineUtc;
        private bool interruptOpen;
        private readonly HashSet<int> interruptWarningsSent = new HashSet<int>();

        /// <summary>
        /// Opens an interrupt window. Returns false when one is already open, so a cadence that fires while
        /// the previous windup is still running is skipped rather than stacking two unavoidable hits.
        /// </summary>
        public bool TryOpenInterrupt(DateTime deadlineUtc)
        {
            lock (sync)
            {
                if (interruptOpen)
                    return false;

                interruptOpen = true;
                interruptDeadlineUtc = deadlineUtc;
                interruptObject = null;
                interruptWarningsSent.Clear();
                nextInterruptPulseUtc = null;

                return true;
            }
        }

        /// <summary>Records the object that was actually placed, once its landblock build has run.</summary>
        public void SetInterruptObject(WorldObject wo)
        {
            lock (sync)
            {
                if (interruptOpen)
                    interruptObject = wo;
            }
        }

        public bool InterruptOpen
        {
            get { lock (sync) return interruptOpen; }
        }

        public WorldObject InterruptObject
        {
            get { lock (sync) return interruptObject; }
        }

        /// <summary>
        /// Latches one countdown reminder. True once per mark per window, so the 6 s and 3 s lines are sent
        /// once each and not on every one of the ticks they are due on.
        /// </summary>
        public bool TryLatchInterruptWarning(int secondsLeft)
        {
            lock (sync)
                return interruptOpen && interruptWarningsSent.Add(secondsLeft);
        }

        /// <summary>
        /// Closes the window and reports whether THIS caller is the one that closed it, plus the object that
        /// was standing. The single latch is what makes "used" and "expired" mutually exclusive: a player
        /// using the object on the same tick the window runs out produces exactly one outcome.
        /// </summary>
        public bool TryCloseInterrupt(out WorldObject placed)
        {
            lock (sync)
            {
                placed = interruptObject;

                if (!interruptOpen)
                    return false;

                interruptOpen = false;
                interruptObject = null;

                return true;
            }
        }

        private DateTime? nextInterruptPulseUtc;

        /// <summary>
        /// Round 17: whether the open window's visibility flare is due, and arms the next one
        /// <paramref name="seconds"/> out. Due on the FIRST call of a window (the drum has just gone up) and
        /// then once per interval; false with no window open, no drum built yet, or a non-positive interval.
        /// Re-armed for every window by <see cref="TryOpenInterrupt"/>.
        /// </summary>
        public bool TryClaimInterruptPulse(DateTime now, double seconds)
        {
            lock (sync)
            {
                if (!interruptOpen || interruptObject == null || !(seconds > 0.0) || !double.IsFinite(seconds))
                    return false;

                if (nextInterruptPulseUtc != null && now < nextInterruptPulseUtc.Value)
                    return false;

                nextInterruptPulseUtc = now + TimeSpan.FromSeconds(seconds);

                return true;
            }
        }

        /// <summary>Whether the open window has run out. A pure read; the caller closes it.</summary>
        public bool InterruptExpired(DateTime now)
        {
            lock (sync)
                return interruptOpen && now >= interruptDeadlineUtc;
        }

        /// <summary>Whole seconds left on the open window, or -1 when none is open.</summary>
        public int InterruptSecondsLeft(DateTime now)
        {
            lock (sync)
            {
                if (!interruptOpen)
                    return -1;

                var left = (interruptDeadlineUtc - now).TotalSeconds;

                return left <= 0 ? 0 : (int)Math.Ceiling(left);
            }
        }

        // ---- safe-zone misses ----------------------------------------------------------------------------

        private readonly Dictionary<uint, MissRecord> misses = new Dictionary<uint, MissRecord>();

        private struct MissRecord
        {
            public int Count;
            public DateTime LastMissUtc;
        }

        /// <summary>
        /// Records one safe-zone miss for a player and returns how many they had accrued BEFORE it - which is
        /// what MlDigsiteBossMechanicRules.MissIsLethal takes, so the first miss of a fight arrives as 0.
        ///
        /// Decay is applied on READ rather than on a timer: one stack is removed for each whole
        /// <paramref name="decaySeconds"/> since the last miss, so a ten-minute fight does not accumulate a
        /// death sentence out of three unrelated mistakes, and nothing has to be ticked to make that true.
        ///
        /// NOT PERSISTED, and so not proof against a relog. Clearing two misses by logging out and back in is
        /// an exploit in the strict sense and a trivial one - relogging costs far more time than the decay
        /// window - and persisting it would mean a shard write per miss.
        /// </summary>
        public int NoteSafeZoneMiss(uint playerGuid, DateTime now, double decaySeconds)
        {
            lock (sync)
            {
                var before = 0;

                if (misses.TryGetValue(playerGuid, out var record))
                {
                    before = record.Count;

                    if (decaySeconds > 0.0)
                    {
                        var elapsed = (now - record.LastMissUtc).TotalSeconds;

                        if (elapsed > 0.0)
                            before = Math.Max(0, before - (int)(elapsed / decaySeconds));
                    }
                }

                misses[playerGuid] = new MissRecord { Count = before + 1, LastMissUtc = now };

                return before;
            }
        }

        // ---- telegraph markers ---------------------------------------------------------------------------

        private readonly List<WorldObject> markers = new List<WorldObject>();

        /// <summary>
        /// Adopts a placed telegraph marker so a later cycle can take it back out of the world. These are ALSO
        /// in the encounter's held list (MlDigsiteProps stamps and adopts every prop), so this list is about
        /// clearing the PREVIOUS cycle's lights, never about cleanup - cleanup is the encounter's, whatever
        /// happens here.
        /// </summary>
        public void AddMarker(WorldObject wo)
        {
            if (wo == null)
                return;

            lock (sync)
                markers.Add(wo);
        }

        /// <summary>Every marker currently adopted, removed from the list. Never null.</summary>
        public List<WorldObject> TakeMarkers()
        {
            lock (sync)
            {
                if (markers.Count == 0)
                    return EmptyMarkers;

                var taken = new List<WorldObject>(markers);

                markers.Clear();

                return taken;
            }
        }

        private static readonly List<WorldObject> EmptyMarkers = new List<WorldObject>();

        // ---- marked points -------------------------------------------------------------------------------

        private List<Position> markedPoints;

        /// <summary>
        /// Records the POSITIONS a telegraph marked, for a resolve that must land where the marks were drawn
        /// rather than where the players now are. That distinction is the whole of the drum volley: a mark
        /// that followed its target could not be stepped off.
        ///
        /// Stored as copies by the caller. Overwrites whatever the previous cycle left, so a cycle that never
        /// reached its resolve cannot leak marks into the next one.
        /// </summary>
        public void SetMarkedPoints(IReadOnlyList<Position> points)
        {
            lock (sync)
                markedPoints = points == null || points.Count == 0 ? null : new List<Position>(points);
        }

        /// <summary>The marked positions, cleared as they are handed out. Never null.</summary>
        public List<Position> TakeMarkedPoints()
        {
            lock (sync)
            {
                if (markedPoints == null)
                    return EmptyPoints;

                var taken = markedPoints;

                markedPoints = null;

                return taken;
            }
        }

        private static readonly List<Position> EmptyPoints = new List<Position>();
    }
}
