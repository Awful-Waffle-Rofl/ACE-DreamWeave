using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

namespace ACE.Server.Frays
{
    /// <summary>What a solo camp's spawn point is for, fixed for good (FraysLayout).</summary>
    public enum FraysPointRole
    {
        /// <summary>An ordinary point: its nest, normal or quick pack, as FraysRules.PointKind says.</summary>
        Pack,

        /// <summary>A rich point: a Gilded pack that pays rich_mult times the Embers and key bar, and comes back slowly.</summary>
        Rich,

        /// <summary>The Ember Shrine's spot: no creatures.</summary>
        Shrine,

        /// <summary>Too close to another point to keep it apart, with nowhere free nearby: no creatures.</summary>
        Off,
    }

    /// <summary>A typed pack point (pack_types): brute (1 or 2 tough creatures) or swarm (6 to 8 swarm creatures). None: the mixed packs.</summary>
    public enum FraysPackType { None, Brute, Swarm }

    /// <summary>One solo camp's fixed layout: every point's spot and role.</summary>
    public sealed class FraysCampLayout
    {
        public IReadOnlyList<Vector3> Positions { get; init; }
        public IReadOnlyList<FraysPointRole> Roles { get; init; }
        public int Shrine { get; init; } = -1;
        public IReadOnlyList<int> Rich { get; init; } = Array.Empty<int>();
        public IReadOnlyList<FraysPackType> Types { get; init; } = Array.Empty<FraysPackType>();

        /// <summary>A point's pack type (None with pack_types off, and for the shrine and points switched off).</summary>
        public FraysPackType TypeOf(int index) => index >= 0 && index < Types.Count ? Types[index] : FraysPackType.None;

        public FraysPointRole RoleOf(int index) => index >= 0 && index < Roles.Count ? Roles[index] : FraysPointRole.Off;

        /// <summary>True for a point that never fields a pack (the shrine, or a point switched off).</summary>
        public bool Spawnless(int index) => RoleOf(index) is FraysPointRole.Shrine or FraysPointRole.Off;
    }

    /// <summary>
    /// Solo camp layouts: every camp has a set path, so players can plan a route through its shrine; 2 rich points and
    /// 1 shrine; packs spread far enough apart that a route only pulls what the player wants. Pure: the same layout on every server start, for every player, every week, so one
    /// recorded route keeps working. Each camp's own fire id picks its rich points and shrine, so each camp has its own
    /// best route.
    /// </summary>
    public static class FraysLayout
    {
        /// <summary>The layout's fixed turn of the point kinds (nests, quick points, swarms, hunters): week 0's, for good.</summary>
        public static int FixedShift(string fireId) => FraysRules.WeekShift(fireId, 0);

        private static double Flat(Vector3 a, Vector3 b)
        {
            var dx = a.X - b.X;
            var dy = a.Y - b.Y;
            return Math.Sqrt(dx * dx + dy * dy);
        }

        private static bool Inside(Vector3 p, Vector2 centre, double radius)
        {
            var dx = p.X - centre.X;
            var dy = p.Y - centre.Y;
            return dx * dx + dy * dy <= radius * radius + 0.01;
        }

        /// <summary>
        /// Spreads <paramref name="points"/> so no two stand closer than <paramref name="minGap"/> metres (flat). Taken in
        /// order: a point clear of every point kept so far stays where it is; one too close moves to the nearest free spot
        /// inside the circle (rings of 2 m steps across the whole zone, 48 directions); one with no free spot is switched off (null).
        /// </summary>
        public static List<Vector3?> Spread(IReadOnlyList<Vector3> points, Vector2 centre, double radius, double minGap, IEnumerable<Vector3> taken = null)
        {
            // taken: spots no pack may crowd, such as the fire you respawn at (a pack 6.6 m from the fire would pull on every respawn).
            var blocked = taken?.ToList() ?? new List<Vector3>();
            var kept = new List<Vector3>();
            var result = new List<Vector3?>();

            bool Clear(Vector3 p) => kept.All(k => Flat(k, p) >= minGap) && blocked.All(k => Flat(k, p) >= minGap);

            foreach (var p in points)
            {
                Vector3? at = null;

                if (Clear(p) && Inside(p, centre, radius))
                    at = p;
                else
                {
                    for (var r = 2.0; r <= 2 * radius && at == null; r += 2)
                    {
                        for (var a = 0; a < 48; a++)
                        {
                            var angle = a * Math.PI * 2 / 48;
                            var c = new Vector3(p.X + (float)(r * Math.Cos(angle)), p.Y + (float)(r * Math.Sin(angle)), p.Z);

                            if (Inside(c, centre, radius) && Clear(c))
                            {
                                at = c;
                                break;
                            }
                        }
                    }
                }

                if (at is Vector3 v)
                    kept.Add(v);

                result.Add(at);
            }

            return result;
        }

        /// <summary>A stable hash of a fire id and a salt (FNV-1a), for picking the same points on every start.</summary>
        public static uint Hash(string fireId, string salt)
        {
            unchecked
            {
                uint h = 2166136261;

                foreach (var ch in (fireId ?? "") + "/" + salt)
                    h = (h ^ ch) * 16777619;

                return h;
            }
        }

        /// <summary>
        /// Builds a solo camp's layout: the spread spots, then <paramref name="richCount"/> rich points and one shrine
        /// chosen by the fire's id. Every special point stands at least <paramref name="minGap"/> from the fire; the rich
        /// points far apart (at least 2 gaps when the zone allows), and the shrine clear of both, so a route runs rich,
        /// lesser packs while it comes back, shrine, the other rich.
        /// </summary>
        public static FraysCampLayout Build(string fireId, IReadOnlyList<Vector3> points, Vector3 fire, Vector2 centre, double radius, double minGap, int richCount, bool shrine,
            int maxPoints = 0, bool edges = false, bool packTypes = false)
        {
            var built = BuildRoles(fireId, points, fire, centre, radius, minGap, richCount, shrine, maxPoints, edges);

            if (!packTypes)
                return built;

            // Typed packs: an even count of plain points, so both builds get as many. Odd: keep one
            // more point when there is one to keep, else switch off the most crowded plain point.
            if (Plain(built).Count % 2 == 1)
            {
                var more = maxPoints > 0 ? BuildRoles(fireId, points, fire, centre, radius, minGap, richCount, shrine, maxPoints + 1, edges) : null;

                if (more != null && Plain(more).Count % 2 == 0)
                    built = more;
                else
                {
                    var roles = built.Roles.ToList();
                    var plain = Plain(built);
                    var crowded = plain.OrderBy(i => Math.Round(plain.Where(j => j != i).Select(j => Flat(built.Positions[i], built.Positions[j])).DefaultIfEmpty(999).Min(), 3)).ThenBy(i => i).First();
                    roles[crowded] = FraysPointRole.Off;
                    built = new FraysCampLayout { Positions = built.Positions, Roles = roles, Rich = built.Rich, Shrine = built.Shrine };
                }
            }

            return WithTypes(built, centre);
        }

        private static List<int> Plain(FraysCampLayout l) => Enumerable.Range(0, l.Roles.Count).Where(i => l.Roles[i] == FraysPointRole.Pack).ToList();

        /// <summary>The angle of a point round the centre, 0 to 360 degrees.</summary>
        public static double AngleOf(Vector3 p, Vector2 centre)
        {
            var a = Math.Atan2(p.Y - centre.Y, p.X - centre.X) * 180 / Math.PI;
            return a < 0 ? a + 360 : a;
        }

        /// <summary>
        /// Pure: brute and swarm points alternating round the camp (an alternating ring, so each build has its own loop),
        /// counted from just past the shrine; the first Gilded point a brute pack, the second a swarm pack.
        /// </summary>
        public static FraysCampLayout WithTypes(FraysCampLayout l, Vector2 centre)
        {
            var types = Enumerable.Repeat(FraysPackType.None, l.Roles.Count).ToList();
            var start = l.Shrine >= 0 ? AngleOf(l.Positions[l.Shrine], centre) : 0;
            var loop = Plain(l).OrderBy(i => Math.Round((AngleOf(l.Positions[i], centre) - start + 360) % 360, 4)).ThenBy(i => i).ToList();

            for (var k = 0; k < loop.Count; k++)
                types[loop[k]] = k % 2 == 0 ? FraysPackType.Brute : FraysPackType.Swarm;

            for (var k = 0; k < l.Rich.Count; k++)
                types[l.Rich[k]] = k % 2 == 0 ? FraysPackType.Brute : FraysPackType.Swarm;

            return new FraysCampLayout { Positions = l.Positions, Roles = l.Roles, Rich = l.Rich, Shrine = l.Shrine, Types = types };
        }

        private static FraysCampLayout BuildRoles(string fireId, IReadOnlyList<Vector3> points, Vector3 fire, Vector2 centre, double radius, double minGap, int richCount, bool shrine,
            int maxPoints, bool edges)
        {
            var spread = Spread(points, centre, radius, minGap, new[] { fire });
            var roles = spread.Select(s => s == null ? FraysPointRole.Off : FraysPointRole.Pack).ToList();
            var positions = spread.Select((s, i) => s ?? points[i]).ToList();

            // Fewer, bigger packs (layout_points): keep the best spread maxPoints and switch the rest off.
            if (maxPoints > 0)
                foreach (var i in Thin(positions, roles.Select(r => r == FraysPointRole.Pack).ToList(), fire, maxPoints))
                    roles[i] = FraysPointRole.Off;

            List<int> Candidates(Func<int, bool> ok) => Enumerable.Range(0, positions.Count)
                .Where(i => roles[i] == FraysPointRole.Pack && Flat(positions[i], fire) >= minGap && ok(i)).ToList();

            int Pick(List<int> from, string salt) => from.Count == 0 ? -1 : from[(int)(Hash(fireId, salt) % (uint)from.Count)];

            // With edges on (Gilded points and the shrine at different edges, so routing matters),
            // the special points stand on the zone's rim, the rich points at least 120 degrees apart round the centre and
            // the shrine at least 90 degrees from each, so no one loop takes in all three.
            bool OnRim(int i) => !edges || Flat(positions[i], new Vector3(centre, 0)) >= RimShare * radius;
            double Apart(int a, int b) => Turn(positions[a], positions[b], centre);

            var rich = new List<int>();

            for (var n = 0; n < richCount; n++)
            {
                var salt = "rich" + n;
                var pick = edges ? Pick(Candidates(i => OnRim(i) && rich.All(r => Apart(i, r) >= RichApartDegrees)), salt) : -1;

                if (pick < 0)
                    pick = Pick(Candidates(i => rich.All(r => Flat(positions[i], positions[r]) >= 2 * minGap)), salt);

                if (pick < 0)
                    pick = Pick(Candidates(i => true), salt);

                if (pick < 0)
                    break;

                rich.Add(pick);
                roles[pick] = FraysPointRole.Rich;
            }

            var shrineAt = -1;

            if (shrine)
            {
                shrineAt = edges ? Pick(Candidates(i => OnRim(i) && rich.All(r => Apart(i, r) >= ShrineApartDegrees)), "shrine") : -1;

                // No rim point 90 degrees from both: the rim point farthest round from the nearer Gilded.
                if (shrineAt < 0 && edges && rich.Count > 0)
                    shrineAt = Candidates(OnRim).OrderByDescending(i => Math.Round(rich.Min(r => Apart(i, r)), 3)).ThenBy(i => i).DefaultIfEmpty(-1).First();

                if (shrineAt < 0)
                    shrineAt = Pick(Candidates(i => rich.All(r => Flat(positions[i], positions[r]) >= 1.5 * minGap)), "shrine");

                if (shrineAt < 0)
                    shrineAt = Pick(Candidates(i => true), "shrine");

                if (shrineAt >= 0)
                    roles[shrineAt] = FraysPointRole.Shrine;
            }

            return new FraysCampLayout { Positions = positions, Roles = roles, Rich = rich, Shrine = shrineAt };
        }

        /// <summary>Special points count as on the rim from this share of the radius out.</summary>
        public const double RimShare = 0.5;
        public const double RichApartDegrees = 120;
        public const double ShrineApartDegrees = 90;

        /// <summary>Pure: degrees between two points seen from the centre (0 to 180).</summary>
        public static double Turn(Vector3 a, Vector3 b, Vector2 centre)
        {
            var d = Math.Abs(Math.Atan2(a.Y - centre.Y, a.X - centre.X) - Math.Atan2(b.Y - centre.Y, b.X - centre.X)) * 180 / Math.PI;
            return d > 180 ? 360 - d : d;
        }

        /// <summary>
        /// Pure: the points to switch off so only <paramref name="keep"/> of the usable ones stay, the kept ones spread as
        /// widely as they go (the farthest from the fire first, then each time the one farthest from all kept so far and
        /// the fire). Ties go to the lower index, so it is the same every time.
        /// </summary>
        public static List<int> Thin(IReadOnlyList<Vector3> positions, IReadOnlyList<bool> usable, Vector3 fire, int keep)
        {
            var left = Enumerable.Range(0, positions.Count).Where(i => usable[i]).ToList();

            if (keep <= 0 || left.Count <= keep)
                return new List<int>();

            var kept = new List<int>();

            double Room(int i) => kept.Select(k => Flat(positions[i], positions[k])).Append(Flat(positions[i], fire)).Min();

            while (kept.Count < keep)
            {
                var best = left.OrderByDescending(i => Math.Round(Room(i), 3)).ThenBy(i => i).First();
                kept.Add(best);
                left.Remove(best);
            }

            return left;
        }

        // ---- the Ember Shrine's buff -------------------------------------------------------------------------

        /// <summary>The shrine's buffs: Ember Fury, or one a week with shrine_rotate (the same at every shrine).</summary>
        public enum ShrineBuff { Fury, Ward, Stoke, Mending }

        /// <summary>Ember Fury, or with shrine_rotate on, a weekly turn through the four.</summary>
        public static ShrineBuff BuffForWeek(int week) =>
            FraysSettings.Toggle(FraysSettings.ShrineRotate) ? (ShrineBuff)(((week % 4) + 4) % 4) : ShrineBuff.Fury;

        public static string BuffName(ShrineBuff b) => b switch
        {
            ShrineBuff.Fury => "Ember Fury",
            ShrineBuff.Ward => "Ember Ward",
            ShrineBuff.Stoke => "Ember Stoke",
            _ => "Ember Mending",
        };

        /// <summary>What the buff does, for chat: "+25% damage".</summary>
        public static string BuffText(ShrineBuff b)
        {
            var s = Strength(b);

            return b switch
            {
                ShrineBuff.Fury => $"+{s * 100:0}% damage",
                ShrineBuff.Ward => $"{s * 100:0}% less damage taken",
                ShrineBuff.Stoke => $"+{s * 100:0}% Snarl bar speed",
                _ => $"{s * 100:0.#}% of your health back every 10 seconds",
            };
        }

        public static double Strength(ShrineBuff b) => b switch
        {
            ShrineBuff.Fury => FraysSettings.Number(FraysSettings.ShrineFury),
            ShrineBuff.Ward => FraysSettings.Number(FraysSettings.ShrineWard),
            ShrineBuff.Stoke => FraysSettings.Number(FraysSettings.ShrineStoke),
            _ => FraysSettings.Number(FraysSettings.ShrineMending),
        };

        /// <summary>The buff on a camp's modifiers.</summary>
        public static void Apply(ShrineBuff b, FraysCampMods m)
        {
            var s = Strength(b);

            switch (b)
            {
                case ShrineBuff.Fury: m.BonusDamage += s; break;          // the Frays pools
                case ShrineBuff.Ward: m.BonusReduction += s; break;
                case ShrineBuff.Stoke: m.SnarlBarSpeed *= 1 + s; break;   // within the shared Snarl speed cap (snarl_speed_cap)
                default: m.HealPer10s += s; break;
            }
        }
    }
}
