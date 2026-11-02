using ACE.Entity.Enum.Properties;
using ACE.Server.WorldObjects;

namespace ACE.Server.Knots
{
    /// <summary>
    /// The values PropertyInt.KnotItemKind takes. Seeded on the weenie row of each Knot-system item.
    /// </summary>
    public static class KnotItemKind
    {
        public const int None = 0;
        public const int Fiber = 1;
        public const int Knot = 2;
        public const int Pattern = 3;
        public const int Pouch = 4;
    }

    /// <summary>
    /// Shared vocabulary of the Knots system (Docs/Knots/KNOTS-DESIGN.md): the item-kind predicates, the wcid
    /// constants, and every player-visible line and name format. Every Knots track reads its text from here so
    /// the wording lives in one place and the final copy is a one-file edit.
    ///
    /// INVARIANT: an item is a Fiber, Knot, Pattern or Pouch only by the PropertyInt.KnotItemKind it carries,
    /// never by its wcid. The wcid constants below exist for creating and placing the items, not for
    /// recognising them.
    /// </summary>
    public static partial class KnotItems
    {
        // ---- wcids (reservation: Content/wcid-registry.d/wcid-1007000.tsv, owner "knots") ----

        public const uint FiberWcid = 1007000;
        public const uint KnotWcid = 1007001;
        public const uint PouchWcid = 1007002;
        public const uint PatternWcid = 1007003;

        /// <summary>The Thread Cache variant that spawns when the cache holds a Fiber or a Pattern.</summary>
        public const uint KnottedThreadCacheWcid = 1007004;

        /// <summary>The pinnacle completion reward chest: the Knotted Thread Cache's model at 2x scale, a separate weenie so the Threads cache stays 1x.</summary>
        public const uint PinnacleCacheWcid = 1007005;

        // ---- numbers ----

        /// <summary>Fibers that weave into one Knot, and the capacity of the Knotting Pouch.</summary>
        public const int FibersPerKnot = 10;

        /// <summary>Entries a Knot carries when its pinnacle definition does not say otherwise.</summary>
        public const int DefaultEntries = 3;

        // ---- predicates: the item kind property ONLY ----

        public static int KindOf(WorldObject item) => item?.GetProperty(PropertyInt.KnotItemKind) ?? KnotItemKind.None;

        public static bool IsFiber(WorldObject item) => KindOf(item) == KnotItemKind.Fiber;
        public static bool IsKnot(WorldObject item) => KindOf(item) == KnotItemKind.Knot;
        public static bool IsPattern(WorldObject item) => KindOf(item) == KnotItemKind.Pattern;
        public static bool IsPouch(WorldObject item) => KindOf(item) == KnotItemKind.Pouch;

        /// <summary>A Fiber or a Pattern: the two things that make a Thread Cache a Knotted Thread Cache.</summary>
        public static bool IsKnotDrop(WorldObject item) => IsKnotDrop(KindOf(item));

        public static bool IsFiber(int kind) => kind == KnotItemKind.Fiber;
        public static bool IsKnot(int kind) => kind == KnotItemKind.Knot;
        public static bool IsPattern(int kind) => kind == KnotItemKind.Pattern;
        public static bool IsPouch(int kind) => kind == KnotItemKind.Pouch;
        public static bool IsKnotDrop(int kind) => kind == KnotItemKind.Fiber || kind == KnotItemKind.Pattern;

        /// <summary>
        /// Fiber, Pattern or Knot: the front group of a Thread cache's placement order (owner ruling 2026-10-30). Wider
        /// than <see cref="IsKnotDrop(WorldObject)"/>, which decides Knotted-cache formation and is deliberately unchanged.
        /// </summary>
        public static bool IsKnotFamily(WorldObject item)
        {
            var kind = KindOf(item);
            return kind == KnotItemKind.Fiber || kind == KnotItemKind.Pattern || kind == KnotItemKind.Knot;
        }

        // ---- names ----

        public static string FiberName(int level) => $"Fiber (Level {level})";

        public static string KnotName(int level) => $"Knot (Level {level})";

        public static string BoundKnotName(string pinnacleName, int level) => $"Knot of the {pinnacleName} (Level {level})";

        public static string PatternName(string pinnacleName) => $"Pattern: {pinnacleName}";

        /// <summary>A Pattern that has taken Fibers: "Pattern: {name} (3/10, Level 120)". With no Fibers taken it is the bare <see cref="PatternName(string)"/>.</summary>
        public static string PatternName(string pinnacleName, int fiberCount, int level)
            => fiberCount <= 0 ? PatternName(pinnacleName) : $"Pattern: {pinnacleName} ({fiberCount}/{FibersPerKnot}, Level {level})";

        // ---- chat: drops ----

        /// <summary>
        /// A Fiber landed in the owner's cache. <paramref name="held"/> is the Fibers in the character's
        /// inventory (side packs and the Pouch included) plus the one just landed; the display caps at 10.
        /// </summary>
        public static string FiberLanded(int held)
        {
            if (held > FibersPerKnot) held = FibersPerKnot;
            if (held < 1) held = 1;
            return $"A Fiber has caught in your Thread Cache. (Fiber {held} of {FibersPerKnot})";
        }

        public static string PatternLanded(string pinnacleName) => $"A Pattern has caught in your Thread Cache: {pinnacleName}.";

        // ---- chat: the Pouch ----

        public const string PouchAcceptsFibersOnly = "The Knotting Pouch only holds Fibers.";

        // ---- chat: Patterns and Knots ----

        public const string NoPinnacleAvailable = "No pinnacle answers this Knot right now.";

        public const string PinnacleDisabled = "That pinnacle is not open right now.";

        public const string KnotResumeHeld = "Your Knot pulls you back into the fight you left.";

        public const string KnotEnterArrival = "The Knot unravels around you, and you stand at the threshold of the pinnacle.";

        public const string KnotReturnToCache = "Your Knot pulls you back to the reward you left behind.";

        public const string KnotNoEntriesTrialLost = "Your Knot has no entries left to carry you back. The trial is lost, and the Knot comes apart in your hands.";

        /// <summary>A return into a running or held fight spent one entry (owner ruling R1).</summary>
        public static string KnotResumeSpent(int entriesLeft)
            => $"Your Knot pulls you back into the fight you left. One entry is spent; {entriesLeft} {(entriesLeft == 1 ? "entry remains" : "entries remain")}.";

        /// <summary>
        /// The line after an admitted use of an encounter that charges itself (SeamChargesEntry false).
        /// <paramref name="spent"/>: the use took an entry (the Knot's count went down across TryEnter).
        /// </summary>
        public static string EncounterEntryLine(KnotResume resume, bool spent, int entriesLeft)
        {
            if (spent)
                return KnotResumeSpent(entriesLeft);

            switch (resume)
            {
                case KnotResume.Free:
                case KnotResume.Owed: return KnotReturnToCache;
                case KnotResume.Paid: return KnotResumeHeld;
                default: return KnotEnterArrival;
            }
        }
    }
}
