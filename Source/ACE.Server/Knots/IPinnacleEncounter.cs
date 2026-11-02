using System.Collections.Generic;

using ACE.Server.WorldObjects;

namespace ACE.Server.Knots
{
    /// <summary>
    /// The encounter a pinnacle destination kind provides (Docs/Knots/KNOTS-DESIGN.md section 9). Registered once
    /// per <see cref="PinnacleDestinationDef.Kind"/>. <see cref="TryEnter"/> admits the player to a fresh solo
    /// copy at the Knot's level and recorded phase. It must not spend the Knot's entry and must not consume the
    /// Knot: for an encounter whose <see cref="SeamChargesEntry"/> is true the seam does both, and only when this
    /// returns true. On false it sets <c>refusal</c> to a player line.
    /// For a <see cref="SeamChargesEntry"/> encounter, on the Knot LAST entry the Knot is consumed at entry, so
    /// <see cref="KnotEncounterEvents.OnPhaseReached"/> and <see cref="KnotEncounterEvents.OnBossKilled"/> may find no
    /// Knot and are then no-ops: such an encounter must not depend on the Knot existing after TryEnter. An encounter
    /// with SeamChargesEntry false spends through <see cref="KnotEncounterEvents.SpendEntry"/> itself (at its own
    /// charge point, and for every paid return) and keeps the Knot until the attempt ends
    /// (<see cref="KnotEncounterEvents.OnAttemptLost"/> / OnBossKilled / RefundEntry).
    /// </summary>
    public interface IPinnacleEncounter
    {
        string Kind { get; }

        bool TryEnter(Player player, WorldObject knot, PinnacleDef def, int knotLevel, int phase, out string refusal);

        /// <summary>True (default): the seam spends one Knot entry per admission. False: the encounter charges itself.</summary>
        bool SeamChargesEntry => true;

        /// <summary>
        /// What a use of this Knot would do to this player's live attempt on it (see <see cref="KnotResume"/>; owner
        /// rulings R1 and C1, 2026-10-19): None (no live attempt), Paid (running or held: the return spends an entry and
        /// is impossible at zero), Owed (won, cache still owed) or Free (won, cache placed and being collected): a return
        /// to a won attempt costs nothing, at zero entries too (owner ruling 2026-10-19). An encounter that can resume
        /// must have SeamChargesEntry = false and spend a Paid return itself, inside TryEnter.
        /// Called on the seam thread before TryEnter, for a bound Knot only. It must be a side-effect-free,
        /// thread-safe snapshot read and must not throw (the fight is owned by another landblock thread).
        /// </summary>
        KnotResume ResumableAttempt(Player player, WorldObject knot) => KnotResume.None;

        /// <summary>
        /// A <see cref="KnotResume.Paid"/> return was refused because the Knot had no entries left: the encounter ends
        /// that attempt as lost (its own normal loss path, so the Knot bookkeeping runs once). Called on the owner's
        /// thread, before the seam consumes the spent Knot. Must not throw.
        /// </summary>
        void ForfeitAttempt(Player player, WorldObject knot) { }
    }

    /// <summary>Thread-safe registry of encounter implementations by destination kind.</summary>
    public static class PinnacleEncounters
    {
        private static readonly object Gate = new object();
        private static readonly Dictionary<string, IPinnacleEncounter> ByKind = new Dictionary<string, IPinnacleEncounter>(System.StringComparer.Ordinal);

        /// <summary>Registers (or replaces) the encounter for <c>impl.Kind</c>. A null impl or blank kind is ignored.</summary>
        public static void Register(IPinnacleEncounter impl)
        {
            if (impl == null || string.IsNullOrWhiteSpace(impl.Kind))
                return;

            lock (Gate)
                ByKind[impl.Kind] = impl;
        }

        public static bool TryGet(string kind, out IPinnacleEncounter impl)
        {
            impl = null;
            if (kind == null)
                return false;

            lock (Gate)
                return ByKind.TryGetValue(kind, out impl);
        }

        /// <summary>Test seam: removes every registration.</summary>
        internal static void Clear()
        {
            lock (Gate)
                ByKind.Clear();
        }
    }
}
