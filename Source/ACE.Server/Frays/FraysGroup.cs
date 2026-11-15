using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

namespace ACE.Server.Frays
{
    /// <summary>
    /// Group camps (the Sky Fray): pure rules, unit tested. A fellowship of group_min_size or more holds the
    /// fire and shares one Heat; each member present makes the creatures tougher, the packs bigger and the points rest less.
    /// Also the zone geometry for a zone whose circle is not round the middle of its landblock (the plateau's arms).
    /// </summary>
    public static class FraysGroup
    {
        // ---- zone geometry ------------------------------------------------------------------------------------

        /// <summary>A fire's zone centre: its own (the plateau's arms) or the middle of the landblock.</summary>
        public static Vector2 CentreOf(FraysFireDef def) => def?.ZoneCentre ?? new Vector2(FraysRules.ZoneCentre, FraysRules.ZoneCentre);

        /// <summary>A fire's zone radius: its own, else <paramref name="setting"/> (zone_radius).</summary>
        public static double RadiusOf(FraysFireDef def, double setting) => def != null && def.ZoneRadius > 0 ? def.ZoneRadius : setting;

        /// <summary>True within <paramref name="radius"/> metres (flat) of <paramref name="centre"/>; always for 0 or less.</summary>
        public static bool InCircle(Vector3 pos, Vector2 centre, double radius)
        {
            if (radius <= 0)
                return true;

            var dx = pos.X - centre.X;
            var dy = pos.Y - centre.Y;
            return dx * dx + dy * dy <= radius * radius;
        }

        /// <summary><paramref name="pos"/> pulled straight in towards <paramref name="centre"/> until within <paramref name="radius"/>.</summary>
        public static Vector3 InsideCircle(Vector3 pos, Vector2 centre, double radius)
        {
            if (radius <= 0)
                return pos;

            var dx = pos.X - centre.X;
            var dy = pos.Y - centre.Y;
            var d = Math.Sqrt(dx * dx + dy * dy);

            if (d <= radius)
                return pos;

            var k = (float)(radius / d);
            return new Vector3(centre.X + dx * k, centre.Y + dy * k, pos.Z);
        }

        /// <summary>
        /// A spot in a landblock next to <paramref name="home"/> (or in it), in home's own frame: 192 m per landblock step.
        /// False for one further away. Landblock ids are x in the high byte, y in the low.
        /// </summary>
        public static bool TryLocal(ushort home, ushort landblock, Vector3 pos, out Vector3 local)
        {
            var dx = (landblock >> 8) - (home >> 8);
            var dy = (landblock & 0xFF) - (home & 0xFF);
            local = new Vector3(pos.X + dx * 192f, pos.Y + dy * 192f, pos.Z);
            return Math.Abs(dx) <= 1 && Math.Abs(dy) <= 1;
        }

        /// <summary>
        /// A plateau arm's zone: everything past its wisp line,
        /// on the far side from the plateau's middle. The arm's circle stopped short of the line, so an owner standing
        /// well inside the wisps was out of the zone and no monster would take them.
        /// </summary>
        public static bool BeyondLine((Vector2 A, Vector2 B) line, Vector2 middle, Vector3 pos)
        {
            var mid = (line.A + line.B) / 2;
            return Vector2.Dot(new Vector2(pos.X, pos.Y) - mid, mid - middle) >= 0;
        }

        /// <summary>Evenly spaced spots along a straight line from a to b, ends included, about <paramref name="spacing"/> metres apart, inside the landblock.</summary>
        public static List<(float X, float Y)> LineSpots(Vector2 a, Vector2 b, double spacing)
        {
            var spots = new List<(float X, float Y)>();
            var count = Math.Max(2, (int)Math.Ceiling(Vector2.Distance(a, b) / Math.Max(1, spacing)) + 1);

            for (var i = 0; i < count; i++)
            {
                var p = Vector2.Lerp(a, b, i / (float)(count - 1));

                if (p.X > 0.5f && p.X < 191.5f && p.Y > 0.5f && p.Y < 191.5f)
                    spots.Add((p.X, p.Y));
            }

            return spots;
        }

        /// <summary>Evenly spaced spots round a circle, about <paramref name="spacing"/> metres apart (at least 8), inside the landblock.</summary>
        public static List<(float X, float Y)> RingSpots(Vector2 centre, double radius, double spacing)
        {
            var spots = new List<(float X, float Y)>();
            radius = Math.Clamp(radius, 1, 95);
            var count = Math.Max(8, (int)Math.Ceiling(2 * Math.PI * radius / Math.Max(1, spacing)));

            for (var i = 0; i < count; i++)
            {
                var a = 2 * Math.PI * i / count;
                var x = (float)(centre.X + radius * Math.Cos(a));
                var y = (float)(centre.Y + radius * Math.Sin(a));

                if (x > 0.5f && x < 191.5f && y > 0.5f && y < 191.5f)
                    spots.Add((x, y));
            }

            return spots;
        }

        /// <summary>The quarter of a zone a spot is in, round the zone's own centre (0 south-west, 1 south-east, 2 north-west, 3 north-east).</summary>
        public static int Quadrant(Vector3 pos, Vector2 centre) => (pos.X >= centre.X ? 1 : 0) + (pos.Y >= centre.Y ? 2 : 0);

        /// <summary>True when <paramref name="z"/> stands on a raised floor (or the zone has none).</summary>
        public static bool OnFloor(float? floorAbove, float z) => floorAbove == null || z >= floorAbove.Value;

        // ---- the group -----------------------------------------------------------------------------------------

        /// <summary>
        /// Whether a fellowship may light a group fire: at least <paramref name="minSize"/> members online, and, with one
        /// fire per player on (<paramref name="onePerIp"/>), at least that many different connections, so one player's
        /// characters do not make a group.
        /// </summary>
        public static bool MeetsMinSize(int membersOnline, int distinctConnections, int minSize, bool onePerIp) =>
            membersOnline >= minSize && (!onePerIp || distinctConnections >= minSize);

        /// <summary>The group's starting Heat: the lowest of the members' own starts, so nobody is dragged in over their head.</summary>
        public static int StartLevel(IEnumerable<int> memberStarts)
        {
            var list = memberStarts?.ToList() ?? new List<int>();
            return list.Count == 0 ? 0 : Math.Max(0, list.Min());
        }

        /// <summary>Creature health for <paramref name="present"/> members: +<paramref name="perMember"/> for each one past the first.</summary>
        public static double HealthMult(int present, double perMember) => 1 + Math.Max(0, perMember) * Math.Max(0, present - 1);

        /// <summary>Extra creatures in each pack for <paramref name="present"/> members (whole creatures, rounded down).</summary>
        public static int PackExtra(int present, double perMember) => (int)Math.Floor(Math.Max(0, perMember) * Math.Max(0, present - 1));

        /// <summary>How much faster points refill and how many more creatures may stand: 1 + rate for each member past the first.</summary>
        public static double RateMult(int present, double perMember) => 1 + Math.Max(0, perMember) * Math.Max(0, present - 1);

        /// <summary>Levels of shared Heat a member's death costs the group (never below 0).</summary>
        public static int DeathDrop(int setting) => Math.Max(0, setting);
    }
}
