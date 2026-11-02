using System.Collections.Generic;
using System.Reflection;

using ACE.Entity;
using ACE.Server.Entity;
using ACE.Server.Managers;

using log4net;

namespace ACE.Server.WorldEvents
{
    /// <summary>
    /// Why a run holds the anchor's eight adjacent landblocks as well as the anchor itself. Any one reason is
    /// enough; <see cref="None"/> holds the anchor alone.
    /// </summary>
    [System.Flags]
    public enum WorldEventHoldReason
    {
        None = 0,

        /// <summary>The source theme sets holdAdjacentLandblocks.</summary>
        Source = 1,

        /// <summary>The presence ring crosses the anchor block's edge.</summary>
        Ring = 2,

        /// <summary>The hazard region spans more than the anchor block.</summary>
        Hazard = 4,

        /// <summary>The run's objective asks for them (IWorldEventObjective.HoldsAdjacentLandblocks).</summary>
        Objective = 8
    }

    /// <summary>
    /// The pure half of the landblock hold (TECH-DESIGN 2.11, 5.6, R6).
    ///
    /// There is no API in LandblockManager that releases a permaload - release means assigning
    /// Landblock.Permaload back to false - so the ONLY thing that stops an event from clearing a permaload
    /// somebody else owns (a Server.PreloadedLandblocks entry, or another system's hold) is the prior-state
    /// snapshot taken before the hold. This function is that rule, isolated so it can be tested.
    /// </summary>
    public static class WorldEventLandblockHold
    {
        /// <summary>The reasons OR'd together, one flag per true input.</summary>
        public static WorldEventHoldReason ComputeHoldReasons(bool source, bool ring, bool hazard, bool objective)
        {
            var reasons = WorldEventHoldReason.None;

            if (source)
                reasons |= WorldEventHoldReason.Source;

            if (ring)
                reasons |= WorldEventHoldReason.Ring;

            if (hazard)
                reasons |= WorldEventHoldReason.Hazard;

            if (objective)
                reasons |= WorldEventHoldReason.Objective;

            return reasons;
        }

        /// <summary>Whether the adjacents are held: any reason at all.</summary>
        public static bool HoldsAdjacents(WorldEventHoldReason reasons) => reasons != WorldEventHoldReason.None;

        /// <summary>
        /// The suffix the hold log line carries: " reasons=source+objective" (lowercase, '+'-joined, in flag
        /// order) when <see cref="WorldEventHoldReason.Objective"/> is among the reasons, and the EMPTY string
        /// otherwise, so the line for every run whose objective asks for nothing stays exactly as it was.
        /// </summary>
        public static string HoldReasonsLogSuffix(WorldEventHoldReason reasons)
        {
            if ((reasons & WorldEventHoldReason.Objective) == 0)
                return "";

            var parts = new List<string>();

            if ((reasons & WorldEventHoldReason.Source) != 0)
                parts.Add("source");

            if ((reasons & WorldEventHoldReason.Ring) != 0)
                parts.Add("ring");

            if ((reasons & WorldEventHoldReason.Hazard) != 0)
                parts.Add("hazard");

            parts.Add("objective");

            return " reasons=" + string.Join("+", parts);
        }

        /// <summary>
        /// The subset of <paramref name="heldBlockKeys"/> an event may safely un-permaload when it ends:
        /// every key that was NOT already permaloaded when the hold was taken. A key absent from
        /// <paramref name="priorPermaload"/> counts as not-permaloaded (it was not loaded at all, so the
        /// event is what brought it in). Duplicates collapse; input order is preserved.
        /// </summary>
        public static IReadOnlyList<ulong> ComputeRelease(
            IReadOnlyDictionary<ulong, bool> priorPermaload, IReadOnlyList<ulong> heldBlockKeys)
        {
            var release = new List<ulong>();

            if (heldBlockKeys == null || heldBlockKeys.Count == 0)
                return release;

            var seen = new HashSet<ulong>();

            foreach (var key in heldBlockKeys)
            {
                if (!seen.Add(key))
                    continue;

                if (priorPermaload != null && priorPermaload.TryGetValue(key, out var wasPermaloaded) && wasPermaloaded)
                    continue;

                release.Add(key);
            }

            return release;
        }
    }

    /// <summary>
    /// The impure half of the landblock hold, behind an interface so a WorldEvent constructed directly by a
    /// unit test gets no bridge at all and therefore never reaches LandblockManager (D6). Production passes
    /// <see cref="LiveWorldEventLandblockBridge.Instance"/> from WorldEventManager.TryStart; a null bridge
    /// means "no world", and the event skips the hold and every spawn with a log line.
    /// </summary>
    public interface IWorldEventLandblockBridge
    {
        /// <summary>Permaload state of every currently loaded landblock, keyed by its (instance, id) key.</summary>
        IReadOnlyDictionary<ulong, bool> SnapshotPermaload();

        /// <summary>
        /// Loads (if needed), permaloads and WAKES the anchor landblock, plus its eight adjacents when
        /// <paramref name="loadAdjacents"/> is set. Returns null when the anchor could not be resolved.
        /// </summary>
        Landblock Hold(Position anchor, bool loadAdjacents);

        /// <summary>Clears Permaload on exactly the given keys, and nothing else.</summary>
        void ReleasePermaload(IReadOnlyList<ulong> keys);
    }

    /// <summary>
    /// The live bridge. Stateless, so one shared instance is enough.
    /// </summary>
    public sealed class LiveWorldEventLandblockBridge : IWorldEventLandblockBridge
    {
        private static readonly ILog log = LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);

        public static readonly LiveWorldEventLandblockBridge Instance = new LiveWorldEventLandblockBridge();

        private LiveWorldEventLandblockBridge()
        {
        }

        public IReadOnlyDictionary<ulong, bool> SnapshotPermaload()
        {
            var snapshot = new Dictionary<ulong, bool>();

            foreach (var landblock in LandblockManager.GetLoadedLandblocks())
            {
                if (landblock == null)
                    continue;

                snapshot[landblock.LongId] = landblock.Permaload;
            }

            return snapshot;
        }

        public Landblock Hold(Position anchor, bool loadAdjacents)
        {
            if (anchor == null)
                return null;

            var landblock = LandblockManager.GetLandblock(new LandblockId(anchor.LandblockId.Raw), anchor.Instance,
                loadAdjacents, true);

            if (landblock == null)
                return null;

            // REQUIRED, and separate from Permaload (TECH-DESIGN D4/R4): Permaload only stops a block from
            // BECOMING dormant. A block that was already dormant when the event staged stays dormant, and a
            // dormant block skips physics and creature ticks entirely - the event would spawn creatures that
            // never move. SetActive is the only thing that clears IsDormant, and its default overload wakes
            // the adjacents too.
            landblock.SetActive();

            log.Info($"[WORLDEVENT] landblock hold 0x{landblock.Id.Raw:X8} instance={landblock.Instance} adjacents={loadAdjacents} dormant={landblock.IsDormant}");

            return landblock;
        }

        public void ReleasePermaload(IReadOnlyList<ulong> keys)
        {
            if (keys == null || keys.Count == 0)
                return;

            var wanted = new HashSet<ulong>(keys);

            foreach (var landblock in LandblockManager.GetLoadedLandblocks())
            {
                if (landblock == null || !wanted.Contains(landblock.LongId))
                    continue;

                landblock.Permaload = false;
            }
        }
    }
}
