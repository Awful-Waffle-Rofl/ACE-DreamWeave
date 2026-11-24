using System.Collections.Generic;
using System.Linq;

using ACE.Server.WorldObjects;

namespace ACE.Server.ThreadDungeons
{
    /// <summary>
    /// Thread encounters (Docs/Threads/ENCOUNTERS.md): the three settings stamped by TryStart, the populate context the
    /// encounter types build on, the plan, and the placed instances. Every mutable field below lives under stateLock.
    /// </summary>
    public sealed partial class ThreadDungeonRun
    {
        public const int DefaultEncountersMaxPerRun = 5;
        public const double DefaultEncounterSlotChance = 0.5;

        /// <summary>dynamic_dungeons_encounters_max_per_run as TryStart stamps it: clamped to [0, ThreadEncounterPicker.MaxPerRunCeiling]. Pure.</summary>
        public static int ClampEncountersMaxPerRun(long configured)
            => (int)System.Math.Clamp(configured, 0L, ThreadEncounterPicker.MaxPerRunCeiling);

        /// <summary>dynamic_dungeons_encounters_slot_chance as TryStart stamps it: NaN reads as the default, then clamped to [0, 1]. Pure.</summary>
        public static double ClampEncounterSlotChance(double configured)
            => double.IsNaN(configured) ? DefaultEncounterSlotChance : System.Math.Clamp(configured, 0.0, 1.0);

        /// <summary>
        /// dynamic_dungeons_encounters_enabled, read once by TryStart and stamped before publication (the ExitTo
        /// convention). Defaults to false so a run that was never stamped - tests, any other caller - places no
        /// encounter; the production default (true) lives in PropertyManager.
        /// </summary>
        public bool EncountersEnabled { get; set; }

        /// <summary>dynamic_dungeons_encounters_max_per_run, clamped to [0, ThreadEncounterPicker.MaxPerRunCeiling] by TryStart.</summary>
        public int EncountersMaxPerRun { get; set; }

        /// <summary>dynamic_dungeons_encounters_slot_chance, clamped to [0, 1] by TryStart.</summary>
        public double EncounterSlotChance { get; set; }

        /// <summary>
        /// dynamic_dungeons_encounter_prize_enabled, read once by TryStart and stamped before publication: does an encounter
        /// victory grant the shared prize (ThreadEncounterPrize.Grant)? A run nobody stamped (tests) uses the shipped default, true.
        /// </summary>
        public bool EncounterPrizeEnabled { get; set; } = true;

        private ThreadEncounterRunContext encounterContext;
        private ThreadEncounterPlan encounterPlan;
        private bool encounterPassClaimed;
        private bool encountersEnded;
        private int nextEncounterId;
        private readonly List<ThreadEncounterInstance> placedEncounters = new List<ThreadEncounterInstance>();
        private readonly List<string> encounterOutcomes = new List<string>();

        /// <summary>Hard cap on the admin outcome log, so a looping force-place cannot grow it without bound.</summary>
        private const int MaxEncounterOutcomes = 64;

        /// <summary>
        /// The populate chain's context (plan, observed max health, outdoor perimeter, loaded landblocks). Write-once:
        /// stamped by ThreadDungeonSpawner from the populate chain's terminal step, before MarkPopulated. Null until then,
        /// for a run whose plan build threw, and again once the run has ended (the landblock references are released).
        /// </summary>
        public ThreadEncounterRunContext EncounterContext { get { lock (stateLock) return encounterContext; } }

        /// <summary>Records the populate context. False (and ignored) when one is already recorded or the run has ended.</summary>
        public bool MarkEncounterContext(ThreadEncounterRunContext context)
        {
            if (context == null)
                return false;

            lock (stateLock)
            {
                if (encounterContext != null || encountersEnded || state == ThreadDungeonRunState.Ended)
                    return false;

                encounterContext = context;
                return true;
            }
        }

        /// <summary>True for the ONE caller that may run the populate-time encounter pass (OnRunPopulated can be reached twice: populate and a reward unseal).</summary>
        public bool TryClaimEncounterPass()
        {
            lock (stateLock)
            {
                if (encounterPassClaimed)
                    return false;

                encounterPassClaimed = true;
                return true;
            }
        }

        /// <summary>The plan the pass rolled, or null before the pass ran.</summary>
        public ThreadEncounterPlan EncounterPlan { get { lock (stateLock) return encounterPlan; } }

        /// <summary>The pass's plan: write-once, though it replaces a provisional one (<see cref="MarkEncounterPlanProvisional"/>).</summary>
        public void MarkEncounterPlan(ThreadEncounterPlan plan)
        {
            if (plan == null)
                return;

            lock (stateLock)
            {
                if (encounterPlan != null && !encounterPlanProvisional)
                    return;

                encounterPlan = plan;
                encounterPlanProvisional = false;
            }
        }

        /// <summary>
        /// Records why the pass could not run YET (no populate context, run not Active) without claiming the pass, so
        /// /dd encounter list can say so. Recorded once (only while no plan is recorded); a later real plan replaces it.
        /// </summary>
        public void MarkEncounterPlanProvisional(ThreadEncounterPlan plan)
        {
            if (plan == null)
                return;

            lock (stateLock)
            {
                if (encounterPlan != null)
                    return;

                encounterPlan = plan;
                encounterPlanProvisional = true;
            }
        }

        private bool encounterPlanProvisional;

        /// <summary>One admin-facing line per placement attempt and outcome (/dd encounter list).</summary>
        public void RecordEncounterOutcome(string line)
        {
            if (string.IsNullOrEmpty(line))
                return;

            lock (stateLock)
            {
                if (encounterOutcomes.Count < MaxEncounterOutcomes)
                    encounterOutcomes.Add(line);
            }
        }

        public IReadOnlyList<string> EncounterOutcomes { get { lock (stateLock) return encounterOutcomes.ToList(); } }

        /// <summary>A new instance with the next per-run id. Nothing is recorded until <see cref="TryRecordEncounterPlaced"/>.</summary>
        internal ThreadEncounterInstance NewEncounterInstance(ThreadEncounterPick pick, IThreadEncounterType type)
        {
            int id;

            lock (stateLock)
                id = ++nextEncounterId;

            return new ThreadEncounterInstance(id, this, pick, type);
        }

        /// <summary>
        /// Records a placed instance. False when the run's encounters have already been ended: the caller then owes that
        /// instance its own OnRunEnded, because the end sweep has already run and will not see it.
        /// </summary>
        internal bool TryRecordEncounterPlaced(ThreadEncounterInstance instance)
        {
            if (instance == null)
                return false;

            lock (stateLock)
            {
                if (encountersEnded || state == ThreadDungeonRunState.Ended)
                    return false;

                placedEncounters.Add(instance);
                return true;
            }
        }

        /// <summary>A snapshot of the placed instances, in placement order.</summary>
        public IReadOnlyList<ThreadEncounterInstance> PlacedEncounters { get { lock (stateLock) return placedEncounters.ToList(); } }

        /// <summary>
        /// The end sweep: marks the run's encounters ended (so no later placement is recorded), releases the populate
        /// context, and hands the caller every placed instance exactly once. A second call returns an empty list.
        /// </summary>
        internal List<ThreadEncounterInstance> TakeEncountersForEnd()
        {
            lock (stateLock)
            {
                if (encountersEnded)
                    return new List<ThreadEncounterInstance>();

                encountersEnded = true;
                encounterContext = null;

                var taken = placedEncounters.ToList();
                placedEncounters.Clear();

                foreach (var instance in taken)
                    instance.Ended = true;

                return taken;
            }
        }

        /// <summary>Marks one instance ended (a failed Place, or one whose run ended mid-Place): it tracks nothing from now on.</summary>
        internal void MarkEncounterInstanceEnded(ThreadEncounterInstance instance)
        {
            if (instance == null)
                return;

            lock (stateLock)
                instance.Ended = true;
        }

        /// <summary>Has the end sweep run?</summary>
        public bool EncountersEnded { get { lock (stateLock) return encountersEnded; } }

        internal void TrackEncounterObject(ThreadEncounterInstance instance, WorldObject wo)
        {
            if (instance == null || wo == null)
                return;

            lock (stateLock)
            {
                // An ended instance has already been released: tracking now would leave a back-reference nothing clears.
                if (instance.Ended)
                    return;

                if (!instance.TrackedObjects.Contains(wo))
                    instance.TrackedObjects.Add(wo);

                wo.P_ThreadEncounter = instance;
            }
        }

        internal IReadOnlyList<WorldObject> EncounterObjects(ThreadEncounterInstance instance)
        {
            if (instance == null)
                return new List<WorldObject>();

            lock (stateLock)
                return instance.TrackedObjects.ToList();
        }

        /// <summary>Clears every tracked object's back-reference and forgets them (after OnRunEnded, or after a failed Place).</summary>
        internal void ReleaseEncounterObjects(ThreadEncounterInstance instance)
        {
            if (instance == null)
                return;

            List<WorldObject> objects;

            lock (stateLock)
            {
                objects = instance.TrackedObjects.ToList();
                instance.TrackedObjects.Clear();
            }

            foreach (var wo in objects)
            {
                if (ReferenceEquals(wo.P_ThreadEncounter, instance))
                    wo.P_ThreadEncounter = null;
            }
        }
    }
}
