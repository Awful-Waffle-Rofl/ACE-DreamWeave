using ACE.Entity.Enum.Properties;
using ACE.Server.WorldObjects;

using log4net;

namespace ACE.Server.Knots
{
    /// <summary>
    /// What a pinnacle encounter reports back (Docs/Knots/KNOTS-DESIGN.md section 9). The Knot is found in the
    /// owner inventory by guid; if it is gone (traded away, already destroyed) the call is a logged no-op.
    /// </summary>
    public static class KnotEncounterEvents
    {
        private static readonly ILog log = LogManager.GetLogger(System.Reflection.MethodBase.GetCurrentMethod().DeclaringType);

        /// <summary>Records the phase a fresh copy should start at next entry. Upward only.</summary>
        public static void OnPhaseReached(Player owner, uint knotGuid, int phaseIndex)
        {
            var knot = Find(owner, knotGuid, nameof(OnPhaseReached));
            if (knot == null)
                return;

            var current = knot.GetProperty(PropertyInt.KnotPhase);
            var next = KnotState.PhaseAfter(current, phaseIndex);
            if (current.HasValue && next == current.Value)
                return;

            owner.UpdateProperty(knot, PropertyInt.KnotPhase, next);
        }

        /// <summary>The boss died: one Knot in, one reward out. Destroys the Knot whatever entries remain.</summary>
        public static void OnBossKilled(Player owner, uint knotGuid)
        {
            var knot = Find(owner, knotGuid, nameof(OnBossKilled));
            if (knot == null)
                return;

            owner.TryConsumeFromInventoryWithNetworking(knot);
            KnotTelemetry.RecordKnotSpent(owner, knot, KnotTelemetry.Result.Won);
        }

        /// <summary>
        /// Spends one entry on the Knot and returns the entries remaining, or -1 when the Knot is not in the owner's
        /// possession or the owner has no live session. NEVER consumes the Knot, even at zero: the encounter keeps it
        /// until the attempt ends. Must be called on the owner's thread with a live session; callers marshal via
        /// owner.EnqueueAction.
        /// </summary>
        public static int SpendEntry(Player owner, uint knotGuid)
        {
            if (owner?.Session == null)
            {
                log.Info($"[KNOTS] {nameof(SpendEntry)}: owner has no live session for Knot 0x{knotGuid:X8}; nothing to do");
                return -1;
            }

            var knot = Find(owner, knotGuid, nameof(SpendEntry));
            if (knot == null)
                return -1;

            // Same fallback as KnotUse.Decide: an unset Structure means the full count (MaxStructure is set at bind).
            var current = knot.GetProperty(PropertyInt.Structure) ?? knot.GetProperty(PropertyInt.MaxStructure) ?? 0;
            var left = KnotState.EntriesAfterSpend(current);
            owner.UpdateProperty(knot, PropertyInt.Structure, left);
            KnotTelemetry.RecordEntry(owner, knot, left);
            return left;
        }

        /// <summary>
        /// Gives back ONE entry the encounter spent (a won attempt whose reward was never delivered must not cost the
        /// Knot), capped at the Knot's MaxStructure, and returns the entries now held, or -1 when the Knot is not in the
        /// owner's possession or the owner has no live session. Never consumes. Must be called on the owner's thread with
        /// a live session; callers marshal via owner.EnqueueAction.
        /// </summary>
        public static int RefundEntry(Player owner, uint knotGuid)
        {
            if (owner?.Session == null)
            {
                log.Info($"[KNOTS] {nameof(RefundEntry)}: owner has no live session for Knot 0x{knotGuid:X8}; nothing to do");
                return -1;
            }

            var knot = Find(owner, knotGuid, nameof(RefundEntry));
            if (knot == null)
                return -1;

            var max = knot.GetProperty(PropertyInt.MaxStructure);
            var current = knot.GetProperty(PropertyInt.Structure) ?? max ?? 0;
            var next = KnotState.EntriesAfterRefund(current, max);
            owner.UpdateProperty(knot, PropertyInt.Structure, next);
            return next;
        }

        /// <summary>
        /// The attempt ended without a kill: the Knot is consumed when it has no entries left. A logged no-op when the
        /// Knot is gone or the owner has no live session. Must be called on the owner's thread; callers marshal via
        /// owner.EnqueueAction.
        /// </summary>
        public static void OnAttemptLost(Player owner, uint knotGuid)
        {
            if (owner?.Session == null)
            {
                log.Info($"[KNOTS] {nameof(OnAttemptLost)}: owner has no live session for Knot 0x{knotGuid:X8}; nothing to do");
                return;
            }

            var knot = Find(owner, knotGuid, nameof(OnAttemptLost));
            if (knot == null)
                return;

            if ((knot.GetProperty(PropertyInt.Structure) ?? 0) <= 0)
            {
                owner.TryConsumeFromInventoryWithNetworking(knot);
                KnotTelemetry.RecordKnotSpent(owner, knot, KnotTelemetry.Result.LostAtZero);
            }
        }

        private static WorldObject Find(Player owner, uint knotGuid, string caller)
        {
            var knot = owner?.FindObject(knotGuid, Player.SearchLocations.MyInventory);
            if (knot == null || !KnotItems.IsKnot(knot))
            {
                log.Info($"[KNOTS] {caller}: Knot 0x{knotGuid:X8} is no longer in the inventory of {owner?.Name ?? "an absent owner"}; nothing to do");
                return null;
            }

            return knot;
        }
    }
}
