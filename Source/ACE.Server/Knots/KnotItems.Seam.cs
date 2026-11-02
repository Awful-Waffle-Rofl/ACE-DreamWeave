using System.Linq;

namespace ACE.Server.Knots
{
    /// <summary>Player-visible lines of the Pattern-fill and Knot-entry seam (Docs/Knots/KNOTS-PLAN.md Track C).</summary>
    public static partial class KnotItems
    {
        public const string KnotHasNoEntries = "That Knot has no entries left.";

        public const string NoEncounterRegistered = "That pinnacle has not opened its doors yet.";

        public const string FiberNeedsPattern = "Use a Pattern on this Fiber to weave it in.";

        public const string PatternNeedsTarget = "Use this Pattern on a Fiber.";

        public const string PatternNeedsFiber = "A Pattern can only take a Fiber from your pack.";

        public static string PatternFiberAdded(int count, int level)
            => $"The Fiber weaves into the Pattern. ({count} of {FibersPerKnot}, Level {level})";

        public const string PatternNoRoom = "The Pattern is complete, but you have no room for the Knot. Make room, then use the Pattern.";

        public static string PatternCompleted(string pinnacleName, int level)
            => $"The Pattern draws tight. You now hold a Knot of the {pinnacleName} (Level {level}).";

        public static string KnotBoundRandomly(string pinnacleName)
            => $"The Knot unravels toward the {pinnacleName}.";

        public static string KnotEntryUsed(int entriesLeft)
            => entriesLeft == 1 ? "Your Knot has 1 entry left." : $"Your Knot has {entriesLeft} entries left.";

        public const string KnotSpent = "Your Knot has no entries left and comes apart in your hands.";

        public static string KnotInCombat(string knotName) => $"You cannot use the {knotName} while in combat.";

        public const string KnotInsideInstance = "You cannot enter a pinnacle from inside another instance.";

        public const string KnotEncounterRefused = "The pinnacle will not open for you right now.";

        public const string KnotGoneAtAnswer = "Your Knot is no longer in your pack.";

        /// <summary>The start question. The name is capped so the whole line stays well under the 250 character chat limit.</summary>
        public static string UsePrompt(string knotName)
        {
            var name = string.IsNullOrWhiteSpace(knotName) ? "Knot" : knotName.Trim();
            if (name.Length > 80)
                name = name.Substring(0, 80);

            name = new string(name.Select(c => c >= 0x20 && c < 0x7F ? c : '?').ToArray());

            return $"Start a Knot run with {name}? The run begins immediately.";
        }
    }
}
