using System;
using System.Collections.Generic;

using ACE.Server.WorldObjects;

namespace ACE.Server.Entity
{
    /// <summary>
    /// The pure hold/release state behind the arrival-overlap portal guard (see Player.arrivalOverlapPortals).
    /// A portal the player materialises inside is HELD - its collision must not activate it - until the player
    /// has walked clear of it; a portal the player arrives clear of is never held. Keyed on portal guid and fed
    /// signed edge-to-edge cylinder distances (negative = interpenetrating) by the caller, so it needs no world
    /// and can be unit tested directly. The distance thresholds are Portal.IsArrivalOverlap and
    /// Portal.HasLeftArrivalOverlap.
    /// </summary>
    public sealed class ArrivalOverlapSet
    {
        private HashSet<uint> held;

        /// <summary>True when nothing is held - the normal case, and the hot-path early out.</summary>
        public bool IsEmpty => held == null;

        /// <summary>
        /// Replaces the held set with the portals in <paramref name="portals"/> that the player is overlapping on
        /// arrival. Any previous hold is dropped first, and the new set is only installed once the enumeration
        /// has completed, so a throwing source leaves the set empty rather than half-filled.
        /// </summary>
        public void Capture(IEnumerable<(uint guid, double distance)> portals)
        {
            held = null;

            HashSet<uint> captured = null;

            foreach (var (guid, distance) in portals)
            {
                if (Portal.IsArrivalOverlap(distance))
                {
                    captured ??= new HashSet<uint>();
                    captured.Add(guid);
                }
            }

            held = captured;
        }

        /// <summary>True while the portal <paramref name="guid"/> is held and must not fire on collision.</summary>
        public bool IsHeld(uint guid)
        {
            return held != null && held.Contains(guid);
        }

        /// <summary>
        /// Releases every held portal the player has walked clear of. <paramref name="distanceOrNull"/> returns the
        /// current distance to a held portal, or null when that portal no longer exists (destroyed, or out of
        /// reach), which also releases it.
        /// </summary>
        public void Release(Func<uint, double?> distanceOrNull)
        {
            if (held == null)
                return;

            held.RemoveWhere(guid =>
            {
                var distance = distanceOrNull(guid);
                return distance == null || Portal.HasLeftArrivalOverlap(distance.Value);
            });

            if (held.Count == 0)
                held = null;
        }
    }
}
