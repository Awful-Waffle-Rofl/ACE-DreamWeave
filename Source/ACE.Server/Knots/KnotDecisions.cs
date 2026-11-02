using System;
using System.Collections.Generic;
using System.Linq;

namespace ACE.Server.Knots
{
    /// <summary>Pure reads of a Knot's stored state. The only definition of "bound" and "used".</summary>
    public static class KnotState
    {
        public static bool IsBound(string pinnacleKey) => !string.IsNullOrEmpty(pinnacleKey);

        /// <summary>Entries left after one is spent; never below zero.</summary>
        public static int EntriesAfterSpend(int entries) => Math.Max(0, entries - 1);

        /// <summary>Entries after one is given back: one more, never above <paramref name="maxEntries"/> when it is set (above zero).</summary>
        public static int EntriesAfterRefund(int entries, int? maxEntries)
        {
            var next = Math.Max(0, entries) + 1;
            return maxEntries.HasValue && maxEntries.Value > 0 ? Math.Min(next, maxEntries.Value) : next;
        }

        /// <summary>The phase to store when <paramref name="reached"/> is reported: upward only.</summary>
        public static int PhaseAfter(int? current, int reached) => Math.Max(current ?? 0, reached);
    }

    public readonly struct PatternFillDecision
    {
        /// <summary>True when the use goes ahead; false carries <see cref="Refusal"/> and changes nothing.</summary>
        public bool Accepted { get; }
        public string Refusal { get; }

        /// <summary>True when this use takes the target Fiber. False for a Pattern that is already full: it only completes.</summary>
        public bool TakesFiber { get; }

        public int NewCount { get; }
        public int NewSum { get; }

        /// <summary>Floor of <see cref="NewSum"/> over <see cref="NewCount"/>: the Pattern's KnotLevel after this use.</summary>
        public int NewAverage { get; }

        /// <summary>The Pattern holds <see cref="KnotItems.FibersPerKnot"/> Fibers after this use, so it turns into a Knot.</summary>
        public bool Completes { get; }

        private PatternFillDecision(bool accepted, string refusal, bool takesFiber, int count, int sum, int average, bool completes)
        { Accepted = accepted; Refusal = refusal; TakesFiber = takesFiber; NewCount = count; NewSum = sum; NewAverage = average; Completes = completes; }

        public static PatternFillDecision Refuse(string line) => new PatternFillDecision(false, line, false, 0, 0, 0, false);
        public static PatternFillDecision Accept(bool takesFiber, int count, int sum, int average, bool completes)
            => new PatternFillDecision(true, null, takesFiber, count, sum, average, completes);
    }

    /// <summary>Decides whether a Pattern takes a Fiber, and whether that completes it. Pure; the glue applies the result.</summary>
    public static class PatternFill
    {
        /// <summary>The refusal that stops a Pattern from becoming a Knot: its pinnacle is missing, disabled, or has no encounter. Null when it may.</summary>
        public static string CompletionRefusal(PinnacleDef def, bool encounterRegistered)
        {
            if (def == null || !def.Enabled)
                return KnotItems.PinnacleDisabled;

            // Not available (enabled but unwired) is a refusal, so a Knot is never bound to a pinnacle it cannot enter.
            if (!encounterRegistered)
                return KnotItems.NoEncounterRegistered;

            return null;
        }

        /// <param name="targetInPack">The target was found in the player's own inventory.</param>
        /// <param name="targetIsFiber">The target carries KnotItemKind Fiber.</param>
        /// <param name="fiberLevel">The target Fiber's KnotLevel (0 when it has none).</param>
        /// <param name="currentCount">Fibers the Pattern has taken so far.</param>
        /// <param name="currentSum">The sum of those Fibers' levels.</param>
        /// <param name="def">The pinnacle the Pattern names (store.GetPinnacle, enabled or not), or null when missing.</param>
        /// <param name="encounterRegistered">An encounter is registered for def destination kind (see <see cref="KnotPinnacles"/>).</param>
        public static PatternFillDecision Decide(bool targetInPack, bool targetIsFiber, int fiberLevel, int currentCount, int currentSum,
            PinnacleDef def, bool encounterRegistered)
        {
            if (!targetInPack || !targetIsFiber)
                return PatternFillDecision.Refuse(KnotItems.PatternNeedsFiber);

            var refusal = CompletionRefusal(def, encounterRegistered);
            if (refusal != null)
                return PatternFillDecision.Refuse(refusal);

            var count = Math.Max(0, currentCount);
            var sum = Math.Max(0, currentSum);

            // A full Pattern never takes another Fiber; it only tries to complete again (the retry after making room).
            if (count >= KnotItems.FibersPerKnot)
                return PatternFillDecision.Accept(false, KnotItems.FibersPerKnot, sum, KnotWeaving.KnotLevelFromSum(sum, KnotItems.FibersPerKnot), true);

            var newCount = count + 1;
            var newSum = sum + Math.Max(0, fiberLevel);

            return PatternFillDecision.Accept(true, newCount, newSum, KnotWeaving.KnotLevelFromSum(newSum, newCount), newCount >= KnotItems.FibersPerKnot);
        }
    }
    public enum KnotGate { Open, InCombat, PkTimer, InsideInstance }

    /// <summary>
    /// What a use of a bound Knot would do to the owner's live attempt on it (owner rulings R1 and C1, 2026-10-19).
    /// Read by the seam from <see cref="IPinnacleEncounter.ResumableAttempt"/> before the decision.
    /// </summary>
    public enum KnotResume
    {
        /// <summary>No live attempt on this Knot: an ordinary entry.</summary>
        None,

        /// <summary>A running or held fight: the return spends one entry; at zero entries it is refused and the attempt is lost.</summary>
        Paid,

        /// <summary>
        /// A won fight whose cache is still owed (not yet delivered): the return is FREE, at zero entries too (owner
        /// ruling 2026-10-19: returning to collect after a win always is). Kept apart from <see cref="Free"/> only so
        /// the log and the entry line can tell the two Won stages apart.
        /// </summary>
        Owed,

        /// <summary>A won fight whose cache was placed and is being collected: the return is free, at zero entries too.</summary>
        Free,
    }

    public readonly struct KnotUseDecision
    {
        public string Refusal { get; }
        public PinnacleDef Def { get; }

        /// <summary>True when the Knot is unbound and Def was chosen at random, so it must be bound on success.</summary>
        public bool NeedsBind { get; }

        /// <summary>Entries the Knot holds before this use is paid for.</summary>
        public int Entries { get; }

        public bool Allowed => Refusal == null;

        /// <summary>
        /// True only for the refusal of a bound Knot that holds no entries and cannot re-enter for free: the seam
        /// consumes the Knot. Never set for a won attempt (Owed or Free re-enters for free).
        /// </summary>
        public bool NoEntries { get; }

        /// <summary>True with <see cref="NoEntries"/> when the refusal also loses a live <see cref="KnotResume.Paid"/> attempt.</summary>
        public bool ForfeitsAttempt { get; }

        /// <summary>The resume kind this decision was made for.</summary>
        public KnotResume Resume { get; }

        private KnotUseDecision(string refusal, PinnacleDef def, bool needsBind, int entries, bool noEntries = false, bool forfeits = false, KnotResume resume = KnotResume.None)
        { Refusal = refusal; Def = def; NeedsBind = needsBind; Entries = entries; NoEntries = noEntries; ForfeitsAttempt = forfeits; Resume = resume; }

        public static KnotUseDecision Refuse(string line) => new KnotUseDecision(line, null, false, 0);
        public static KnotUseDecision RefuseNoEntries(bool forfeitsAttempt = false)
            => new KnotUseDecision(forfeitsAttempt ? KnotItems.KnotNoEntriesTrialLost : KnotItems.KnotHasNoEntries, null, false, 0, true, forfeitsAttempt,
                forfeitsAttempt ? KnotResume.Paid : KnotResume.None);
        public static KnotUseDecision Enter(PinnacleDef def, bool needsBind, int entries, KnotResume resume = KnotResume.None)
            => new KnotUseDecision(null, def, needsBind, entries, resume: resume);
    }

    /// <summary>Decides a Knot use. Pure; the glue applies the result and pays only after the encounter admits.</summary>
    public static class KnotUse
    {
        /// <summary>The Thread gem refusals: combat, PK timer, and being inside another instance.</summary>
        public static KnotGate Gate(bool inCombat, bool pkTimerActive, bool insideEphemeralInstance)
        {
            if (inCombat) return KnotGate.InCombat;
            if (pkTimerActive) return KnotGate.PkTimer;
            if (insideEphemeralInstance) return KnotGate.InsideInstance;
            return KnotGate.Open;
        }

        /// <param name="boundKey">The Knot PinnacleKey, or null when unbound.</param>
        /// <param name="boundDef">store.GetPinnacle(boundKey) (enabled or not), or null.</param>
        /// <param name="enabled">store.EnabledPinnacles.</param>
        /// <param name="pick">Uniform integer in [0, n).</param>
        /// <param name="hasEncounter">Whether an encounter is registered for a destination kind.</param>
        /// <param name="resume">
        /// What the use does to a live attempt on this bound Knot. A won attempt (<see cref="KnotResume.Owed"/> or
        /// <see cref="KnotResume.Free"/>) enters at zero entries. At zero entries <see cref="KnotResume.Paid"/> is
        /// refused AND forfeits the attempt (<see cref="KnotUseDecision.ForfeitsAttempt"/>; the seam consumes the Knot),
        /// and None is the ordinary no-entries refusal.
        /// </param>
        public static KnotUseDecision Decide(string boundKey, int? structure, PinnacleDef boundDef,
            IReadOnlyList<PinnacleDef> enabled, Func<int, int> pick, Func<string, bool> hasEncounter, KnotResume resume = KnotResume.None)
        {
            if (KnotState.IsBound(boundKey))
            {
                if (boundDef == null || !boundDef.Enabled)
                    return KnotUseDecision.Refuse(KnotItems.PinnacleDisabled);

                if (!hasEncounter(boundDef.Destination?.Kind))
                    return KnotUseDecision.Refuse(KnotItems.NoEncounterRegistered);

                var entries = structure ?? boundDef.Entries;

                if (entries <= 0)
                {
                    switch (resume)
                    {
                        // returning to collect after a win is always free (owner ruling 2026-10-19)
                        case KnotResume.Free:
                        case KnotResume.Owed:
                            break;

                        case KnotResume.Paid:
                            return KnotUseDecision.RefuseNoEntries(forfeitsAttempt: true);

                        default:
                            return KnotUseDecision.RefuseNoEntries();
                    }
                }

                return KnotUseDecision.Enter(boundDef, false, entries, resume);
            }

            if (enabled == null || enabled.Count == 0)
                return KnotUseDecision.Refuse(KnotItems.NoPinnacleAvailable);

            // Only a pinnacle that can actually be entered is a candidate, so a pinnacle with no encounter yet
            // never swallows an unbound Knot random pick.
            var pool = KnotPinnacles.Available(enabled, hasEncounter);
            if (pool.Count == 0)
                return KnotUseDecision.Refuse(KnotItems.NoEncounterRegistered);

            var chosen = pool[Math.Clamp(pick(pool.Count), 0, pool.Count - 1)];
            return KnotUseDecision.Enter(chosen, true, chosen.Entries);
        }
    }
}
