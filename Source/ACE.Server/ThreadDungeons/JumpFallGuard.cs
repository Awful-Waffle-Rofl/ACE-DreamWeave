using System;
using System.Collections.Generic;
using System.Linq;

using ACE.Server.WorldObjects;

namespace ACE.Server.ThreadDungeons
{
    /// <summary>
    /// The Jump encounter's fall-damage exemption (Docs/Threads/ENCOUNTERS.md "Jump"; owner ruling 2026-11-09: a fall
    /// only sets the player back on their own climb). A course tops out around 16 to 24 m and fall damage starts near a
    /// 12.7 m free fall (Player.HandleFallingDamage), so without this a missed jump near the top would hurt, and a
    /// Thread death sends the player out of the run.
    ///
    /// No enchantment, no persisted property: a read-time check in Player.TakeDamage_Falling, the Shrine-blessing way. A
    /// course zone is open while its course runs and for <see cref="GraceSeconds"/> after it despawns, so a player
    /// standing on the top when time runs out lands safely. The zone list is an IMMUTABLE array swapped under a lock on
    /// the rare writes and read lock-free, so a server with no running course pays one volatile read per fall.
    /// </summary>
    public static class JumpFallGuard
    {
        /// <summary>A zone covers the course disc plus this many metres.</summary>
        public const float RadiusMargin = 5f;

        /// <summary>How long a zone stays open after its course despawns.</summary>
        public const double GraceSeconds = 5.0;

        /// <summary>One course's zone, in global outdoor metres. <see cref="ExpiresTicks"/> is long.MaxValue while the course runs.</summary>
        public sealed class Zone
        {
            internal Zone(uint instance, double gx, double gy, float radius)
            {
                Instance = instance;
                Gx = gx;
                Gy = gy;
                Radius = radius;
            }

            public uint Instance { get; }
            public double Gx { get; }
            public double Gy { get; }
            public float Radius { get; }

            private long expiresTicks = long.MaxValue;

            public long ExpiresTicks => System.Threading.Interlocked.Read(ref expiresTicks);

            internal void Expire(long ticks) => System.Threading.Interlocked.Exchange(ref expiresTicks, ticks);
        }

        private static readonly object Gate = new object();
        private static volatile Zone[] zones = Array.Empty<Zone>();

        /// <summary>Opens a zone for a course that has just formed. Prunes expired zones.</summary>
        public static Zone Open(uint instance, double gx, double gy, float courseRadius, DateTime utcNow)
        {
            var zone = new Zone(instance, gx, gy, courseRadius);

            lock (Gate)
                zones = zones.Where(z => z.ExpiresTicks >= utcNow.Ticks).Append(zone).ToArray();

            return zone;
        }

        /// <summary>The course despawned (or its run ended): the zone stays open for the grace, then lapses. Idempotent.</summary>
        public static void Close(Zone zone, DateTime utcNow)
        {
            if (zone == null)
                return;

            var until = utcNow.AddSeconds(GraceSeconds).Ticks;

            lock (Gate)
            {
                if (zone.ExpiresTicks > until)
                    zone.Expire(until);

                zones = zones.Where(z => z.ExpiresTicks >= utcNow.Ticks).ToArray();
            }
        }

        /// <summary>
        /// The decision, pure: inside the same instance, within the course radius plus <see cref="RadiusMargin"/>
        /// horizontally, and not past the zone's expiry.
        /// </summary>
        public static bool Decide(bool sameInstance, double horizontalDistance, float courseRadius, long expiresTicks, long nowTicks)
            => sameInstance && horizontalDistance <= courseRadius + RadiusMargin && nowTicks <= expiresTicks;

        /// <summary>Is a fall at this spot exempt? Lock-free; one volatile read when no course runs.</summary>
        public static bool IsExempt(uint instance, double gx, double gy, DateTime utcNow)
        {
            var current = zones;

            if (current.Length == 0)
                return false;

            var now = utcNow.Ticks;

            foreach (var z in current)
            {
                var dx = gx - z.Gx;
                var dy = gy - z.Gy;

                if (Decide(z.Instance == instance, Math.Sqrt(dx * dx + dy * dy), z.Radius, z.ExpiresTicks, now))
                    return true;
            }

            return false;
        }

        /// <summary>The Player hook (Player.TakeDamage_Falling). Outdoor positions only; anything else is never exempt.</summary>
        public static bool IsExempt(Player player)
        {
            if (zones.Length == 0)
                return false;

            var location = player?.Location;

            if (location == null || location.Indoors)
                return false;

            var lb = location.LandblockId.Landblock;
            return IsExempt(location.Instance, OutdoorSiteGeometry.ToGlobalX(lb, location.PositionX), OutdoorSiteGeometry.ToGlobalY(lb, location.PositionY), DateTime.UtcNow);
        }

        /// <summary>Test seam: the open zones.</summary>
        internal static IReadOnlyList<Zone> Zones => zones;

        /// <summary>Test seam: drops every zone.</summary>
        internal static void Clear()
        {
            lock (Gate)
                zones = Array.Empty<Zone>();
        }
    }
}
