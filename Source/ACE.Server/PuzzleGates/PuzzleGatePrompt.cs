using System.Collections.Generic;

namespace ACE.Server.PuzzleGates
{
    /// <summary>What the approach-prompt scan loop does for a placement in a given state.</summary>
    public enum PuzzlePromptScan
    {
        /// <summary>Not live yet: reschedule, prompt nobody.</summary>
        Wait,

        /// <summary>Live: scan and prompt.</summary>
        Scan,

        /// <summary>Solved, failed or cleared: end the loop for good.</summary>
        Stop,
    }

    /// <summary>
    /// Pure approach-prompt bookkeeping for one placement: who gets the one hint line, and when it re-arms.
    /// <list type="bullet">
    /// <item>A player seen within <see cref="PuzzleGateTunables.PromptRadius"/> who is not already prompted is
    /// prompted once.</item>
    /// <item>A prompted player re-arms only when seen beyond <see cref="PuzzleGateTunables.PromptResetRadius"/>,
    /// or after missing from <see cref="PuzzleGateTunables.PromptUnseenRearmScans"/> consecutive scans (left
    /// the landblock or logged out).</item>
    /// <item>A reroll builds a new placement and with it a new tracker, so it re-arms everyone.</item>
    /// </list>
    /// Allocation-free in steady state: one dictionary and one reusable buffer per placement. Landblock
    /// thread only, like every other mutation of a placement.
    /// </summary>
    public sealed class PuzzlePromptTracker
    {
        /// <summary>Prompted players, valued by the scan number they were last seen in.</summary>
        private readonly Dictionary<uint, int> prompted = new Dictionary<uint, int>();

        private readonly List<uint> expired = new List<uint>();

        private int scan;

        public int PromptedCount => prompted.Count;

        public static PuzzlePromptScan Decide(PuzzlePlacementState state)
        {
            switch (state)
            {
                case PuzzlePlacementState.Spawning: return PuzzlePromptScan.Wait;
                case PuzzlePlacementState.Live: return PuzzlePromptScan.Scan;
                default: return PuzzlePromptScan.Stop;
            }
        }

        public void BeginScan() => scan++;

        /// <summary>One player seen this scan at <paramref name="distance"/> metres. True = send the prompt now.</summary>
        public bool Observe(uint guid, float distance)
        {
            if (prompted.ContainsKey(guid))
            {
                if (distance > PuzzleGateTunables.PromptResetRadius)
                    prompted.Remove(guid);
                else
                    prompted[guid] = scan;

                return false;
            }

            if (distance > PuzzleGateTunables.PromptRadius)
                return false;

            prompted[guid] = scan;
            return true;
        }

        /// <summary>Re-arms prompted players that have been missing for too many scans.</summary>
        public void EndScan()
        {
            if (prompted.Count == 0)
                return;

            expired.Clear();

            foreach (var kvp in prompted)
            {
                if (scan - kvp.Value >= PuzzleGateTunables.PromptUnseenRearmScans)
                    expired.Add(kvp.Key);
            }

            for (var i = 0; i < expired.Count; i++)
                prompted.Remove(expired[i]);
        }
    }
}
