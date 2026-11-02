using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Server.SpeedCourses;

namespace ACE.Server.Tests
{
    /// <summary>
    /// The Black Book wings route (owner ruling 2026-10-25, open order) as a graph over the REAL realm rows, weenie SQL and
    /// speed-courses.json: every object is placed into an area by the cell its realm row (or course point) names, every
    /// door separates two areas, a lever opens its key's door from the area it stands in (one on the outer side of every
    /// corner-room door), a Cell Keeper's death opens every door it names (both doors of its room) from inside the room,
    /// and a chute carries the runner one way to its destination cell's area.
    /// <para/>
    /// THE CELL-TO-AREA TABLE is hand data: the connected components of the 0x0089 cell graph
    /// (ACE.Content.Tools cells --landblock=0x0089, 2026-10-25) with every gate's doorway join cut (stub, P1, P2, P3,
    /// the Seal, the three hub doors and the eight corner-room doors). Files only: no dat, no PropertyManager, no database.
    /// </summary>
    [TestClass]
    public class SpeedBlackBookRouteTests
    {
        // Areas: the components between gates.
        private const string West = "W";           // W junction, the NW and SW lever corridors, the Seal's west side
        private const string North = "N";          // N junction, the north arm up to P2, the NE lever corridor
        private const string East = "E";           // E junction, the east arm, the lectern and the east chute
        private const string South = "S";          // S junction, the south arm and terminal, the SE lever corridor
        private const string NorthTerminal = "NT"; // beyond P2: K1 and the north chute
        private const string Hub = "HUB";          // beyond the Seal: the boss
        private const string RoomNW = "R_NW";
        private const string RoomSW = "R_SW";
        private const string RoomNE = "R_NE";
        private const string RoomSE = "R_SE";

        private static readonly Dictionary<uint, string> CellArea = new Dictionary<uint, string>
        {
            [0x0089025D] = West, [0x0089024D] = West, [0x00890251] = West, [0x008902F1] = West,
            [0x0089024C] = RoomNW, [0x00890264] = RoomNW,
            [0x00890252] = RoomSW, [0x00890269] = RoomSW,
            [0x00890288] = North, [0x0089026A] = North, [0x008902A8] = North, [0x0089018A] = North, [0x008902F8] = North,
            [0x008902AA] = RoomNE, [0x008902C1] = RoomNE,
            [0x008902B8] = East, [0x008902C2] = East, [0x008902C6] = East, [0x00890236] = East, [0x00890237] = East, [0x00890303] = East,
            [0x008902AF] = RoomSE, [0x008902C7] = RoomSE,
            [0x0089028D] = South, [0x0089026B] = South, [0x008902A9] = South, [0x00890185] = South, [0x008902FC] = South,
            [0x00890186] = NorthTerminal, [0x00890189] = NorthTerminal,
            [0x008902F3] = Hub, [0x008902F9] = Hub, [0x00890301] = Hub, [0x008902FB] = Hub, [0x008902FA] = Hub,
        };

        /// <summary>Each corner-room door's doorway join: the cell its realm row names and the cell across the doorway (from the cell graph).</summary>
        private static readonly Dictionary<uint, uint> DoorwayAcross = new Dictionary<uint, uint>
        {
            [0x0089024C] = 0x0089024D, // NW in
            [0x00890251] = 0x00890252, // SW in
            [0x008902A8] = 0x008902AA, // NE in
            [0x008902A9] = 0x008902AF, // SE in
            [0x00890264] = 0x0089026A, // NW out
            [0x00890269] = 0x0089026B, // SW out
            [0x008902C1] = 0x008902C2, // NE out
            [0x008902C6] = 0x008902C7, // SE out
        };

        private const uint SealWcid = 1007503;
        private const uint LecternWcid = 1007505;
        private const uint NorthChute = 1007506, EastChute = 1007520, SouthChute = 1007521;

        // ------------------------------------------------------------------------------------------- loading

        private static string RepoRoot()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);

            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "Source", "ACE.sln")))
                dir = dir.Parent;

            Assert.IsNotNull(dir, $"Could not find Source/ACE.sln by walking up from {AppContext.BaseDirectory}");
            return dir.FullName;
        }

        private sealed class Row
        {
            public uint Guid, Wcid, Cell;
            public float X, Y, Z;
        }

        private sealed class Door
        {
            public Row Row;
            public string Key;
            public string SideA, SideB;
            public override string ToString() => $"{Key} (wcid {Row.Wcid}, {SideA}|{SideB})";
        }

        private sealed class Route
        {
            public List<Row> Rows;
            public List<Door> Doors;
            public List<(string Key, string Area)> Levers;
            public List<(string Id, string Area, List<string> Keys)> Guardians;
            public Dictionary<uint, (string From, string To)> Chutes;
            public string SealArea, LecternArea;
            public List<string> OptionalGateKeys;
        }

        private static string Weenie(uint wcid)
        {
            var path = Directory.EnumerateFiles(Path.Combine(RepoRoot(), "Content", "sql", "weenies"), $"{wcid} *.sql").SingleOrDefault();
            Assert.IsNotNull(path, $"no weenie SQL for {wcid}");
            return File.ReadAllText(path);
        }

        /// <summary>
        /// The rows of one property table (`weenie_properties_&lt;table&gt;`): the text from its INSERT to the next INSERT.
        /// Type ids repeat across tables (int 12 vs bool 12, d_i_d 2 vs position 2), so every lookup is scoped.
        /// </summary>
        private static string Section(string sql, string table)
        {
            foreach (var part in sql.Split(new[] { "INSERT INTO" }, StringSplitOptions.None))
                if (part.TrimStart().StartsWith($"`weenie_properties_{table}`", StringComparison.Ordinal))
                    return part;

            return "";
        }

        private static string Prop(string sql, string table, uint wcid, int type)
        {
            var m = Regex.Match(Section(sql, table), $@"\(\s*{wcid}\s*,\s*{type}\s*,\s*('(?<s>[^']*)'|(?<n>[-0-9.]+|True|False))\s*\)");
            return m.Success ? (m.Groups["s"].Success ? m.Groups["s"].Value : m.Groups["n"].Value) : null;
        }

        private static string Area(uint cell, string what)
        {
            Assert.IsTrue(CellArea.TryGetValue(cell, out var area), $"{what}: cell 0x{cell:X8} is not in the area table (moved? re-derive the components)");
            return area;
        }

        private static Route Load()
        {
            var realm = File.ReadAllText(Path.Combine(RepoRoot(), "Content", "realms", "speedrun_black_book.sql"));
            var rows = Regex.Matches(realm, @"\(\s*1\s*,\s*0x(?<g>[0-9A-Fa-f]{8})\s*,\s*(?<w>\d+)\s*,\s*0x(?<c>[0-9A-Fa-f]{8})\s*,\s*(?<x>-?[\d.]+)\s*,\s*(?<y>-?[\d.]+)\s*,\s*(?<z>-?[\d.]+)\s*,")
                .Cast<Match>()
                .Select(m => new Row
                {
                    Guid = Convert.ToUInt32(m.Groups["g"].Value, 16),
                    Wcid = uint.Parse(m.Groups["w"].Value),
                    Cell = Convert.ToUInt32(m.Groups["c"].Value, 16),
                    X = float.Parse(m.Groups["x"].Value, System.Globalization.CultureInfo.InvariantCulture),
                    Y = float.Parse(m.Groups["y"].Value, System.Globalization.CultureInfo.InvariantCulture),
                    Z = float.Parse(m.Groups["z"].Value, System.Globalization.CultureInfo.InvariantCulture),
                })
                .ToList();

            Assert.IsTrue(rows.Count >= 22, $"the realm unit parsed to {rows.Count} rows");

            var route = new Route
            {
                Rows = rows,
                Doors = new List<Door>(),
                Levers = new List<(string, string)>(),
                Guardians = new List<(string, string, List<string>)>(),
                Chutes = new Dictionary<uint, (string, string)>(),
            };

            foreach (var row in rows)
            {
                var sql = Weenie(row.Wcid);
                var key = Prop(sql, "string", row.Wcid, (int)ACE.Entity.Enum.Properties.PropertyString.ObjectiveLockKey);
                var required = Prop(sql, "int", row.Wcid, (int)ACE.Entity.Enum.Properties.PropertyInt.ObjectiveLockRequired);

                if (key == null || row.Wcid == SealWcid || row.Wcid == LecternWcid || key == "bb_stub")
                    continue;

                if (required != null)
                {
                    Assert.IsTrue(DoorwayAcross.TryGetValue(row.Cell, out var across), $"door {key} row cell 0x{row.Cell:X8} is not a known corner-room doorway");
                    route.Doors.Add(new Door { Row = row, Key = key, SideA = Area(row.Cell, key), SideB = Area(across, key) });
                }
                else
                {
                    route.Levers.Add((key, Area(row.Cell, $"lever {key}")));
                }
            }

            var course = SpeedCourseStore.Parse(File.ReadAllText(Path.Combine(RepoRoot(), "Content", "dungeons", "dynamic", SpeedCourseStore.FileName)), null).GetCourse(3);
            Assert.IsNotNull(course, "no parseable season 3 course");

            foreach (var g in course.Guardians)
                route.Guardians.Add((g.Id, Area(g.Position.Cell, g.Id), g.DoorKeys.ToList()));

            route.OptionalGateKeys = course.OptionalGateKeys.ToList();

            foreach (var chute in new[] { NorthChute, EastChute, SouthChute })
            {
                var row = rows.Single(r => r.Wcid == chute);
                var m = Regex.Match(Section(Weenie(chute), "position"), $@"\(\s*{chute}\s*,\s*2\s*,\s*0x(?<c>[0-9A-Fa-f]{{8}})");
                Assert.IsTrue(m.Success, $"chute {chute} has no destination");
                route.Chutes[chute] = (Area(row.Cell, $"chute {chute}"), Area(Convert.ToUInt32(m.Groups["c"].Value, 16), $"chute {chute} destination"));
            }

            // The Seal's row names the hub-side cell of its doorway join 0x008902F1 | 0x008902F3; the runner reaches it from the west side.
            var sealRow = rows.Single(r => r.Wcid == SealWcid);
            Assert.AreEqual(0x008902F3u, sealRow.Cell, "the Seal moved: re-derive its doorway");
            Assert.AreEqual(Hub, Area(sealRow.Cell, "the Seal"));
            route.SealArea = Area(0x008902F1, "the Seal's approach");
            route.LecternArea = Area(rows.Single(r => r.Wcid == LecternWcid).Cell, "the lectern");

            return route;
        }

        // ------------------------------------------------------------------------------------------- the walk

        /// <summary>
        /// Every area the runner can reach from <paramref name="start"/>, opening doors as they go: a lever opens its key's
        /// door from the lever's area, a keeper is killed from its own area and opens EVERY door it names, P2 is solved
        /// from the north arm, and an open door is passable both ways. Chutes are not walked (they are one-way, taken
        /// deliberately). The Seal and the hub doors stay shut. <paramref name="opened"/> carries the doors already open and
        /// receives every door this walk opens. Levers whose key is in <paramref name="skipLevers"/> are never pulled, and
        /// keepers whose id is in <paramref name="skipKeepers"/> are never killed.
        /// </summary>
        private static HashSet<string> Reach(Route r, string start, HashSet<string> opened, ICollection<string> skipLevers = null, ICollection<string> skipKeepers = null)
        {
            var seen = new HashSet<string> { start };
            var changed = true;

            while (changed)
            {
                changed = false;

                foreach (var (key, area) in r.Levers)
                    if (seen.Contains(area) && (skipLevers == null || !skipLevers.Contains(key)) && opened.Add(key))
                        changed = true;

                foreach (var (id, area, keys) in r.Guardians)
                    if (seen.Contains(area) && (skipKeepers == null || !skipKeepers.Contains(id)))
                        foreach (var key in keys)
                            if (opened.Add(key))
                                changed = true;

                if (seen.Contains(North) && seen.Add(NorthTerminal))
                    changed = true;

                foreach (var d in r.Doors.Where(d => opened.Contains(d.Key)))
                {
                    if (seen.Contains(d.SideA) && seen.Add(d.SideB))
                        changed = true;

                    if (seen.Contains(d.SideB) && seen.Add(d.SideA))
                        changed = true;
                }
            }

            return seen;
        }

        private static readonly string[] Wings = { "north", "east", "south" };

        private static string WingGoal(Route r, string wing)
            => wing == "north" ? r.Chutes[NorthChute].From : wing == "east" ? r.LecternArea : r.Chutes[SouthChute].From;

        private static uint WingChute(string wing) => wing == "north" ? NorthChute : wing == "east" ? EastChute : SouthChute;

        private static IEnumerable<string[]> Orders()
        {
            foreach (var a in Wings)
                foreach (var b in Wings.Where(x => x != a))
                    foreach (var c in Wings.Where(x => x != a && x != b))
                        yield return new[] { a, b, c };
        }

        // ------------------------------------------------------------------------------------------- tests

        [TestMethod]
        public void TheRouteObjects_AreWhereTheRulingPutsThem()
        {
            var r = Load();

            Assert.AreEqual(8, r.Doors.Count, string.Join(", ", r.Doors));
            Assert.AreEqual(8, r.Levers.Count, "a lever on the outer side of every corner-room door (owner ruling 2026-10-25)");
            Assert.AreEqual(4, r.Guardians.Count);
            Assert.AreEqual(8, r.Doors.Select(d => d.Key).Distinct().Count(), "one door per key");
            CollectionAssert.AreEquivalent(r.Doors.Select(d => d.Key).ToList(), r.Levers.Select(l => l.Key).ToList(), "exactly one lever per door key");

            string[] Sides(string key)
            {
                var d = r.Doors.Single(x => x.Key == key);
                return new[] { d.SideA, d.SideB }.OrderBy(s => s, StringComparer.Ordinal).ToArray();
            }

            // NW/SW entered from the W junction, exit toward N/S; NE entered from N, SE from S, both exit toward E.
            CollectionAssert.AreEqual(new[] { RoomNW, West }, Sides("bb_nw_in"));
            CollectionAssert.AreEqual(new[] { North, RoomNW }, Sides("bb_nw_out"));
            CollectionAssert.AreEqual(new[] { RoomSW, West }, Sides("bb_sw_in"));
            CollectionAssert.AreEqual(new[] { RoomSW, South }, Sides("bb_sw_out"));
            CollectionAssert.AreEqual(new[] { North, RoomNE }, Sides("bb_ne_in"));
            CollectionAssert.AreEqual(new[] { East, RoomNE }, Sides("bb_ne_out"));
            CollectionAssert.AreEqual(new[] { RoomSE, South }, Sides("bb_se_in"));
            CollectionAssert.AreEqual(new[] { East, RoomSE }, Sides("bb_se_out"));

            // The chutes return the runner to the junction that leads on.
            Assert.AreEqual((NorthTerminal, North), r.Chutes[NorthChute]);
            Assert.AreEqual((East, East), r.Chutes[EastChute]);
            Assert.AreEqual((South, South), r.Chutes[SouthChute]);
            Assert.AreEqual(West, r.SealArea, "the Seal is approached from the W junction side");
        }

        private static readonly string[] Rooms = { RoomNW, RoomSW, RoomNE, RoomSE };

        private static string RoomOf(Door d) => Rooms.Single(x => x == d.SideA || x == d.SideB);

        private static string OuterSide(Door d) => d.SideA == RoomOf(d) ? d.SideB : d.SideA;

        private static List<Door> DoorsOf(Route r, string room) => r.Doors.Where(d => RoomOf(d) == room).ToList();

        /// <summary>
        /// No door can be passed from its locked side without its lever or its keeper: every lever stands on the OUTER side
        /// of its door, never in a room; every keeper stands inside the room whose two doors it names; and with a room's
        /// two levers never pulled, the room cannot be reached from anywhere outside it (its keeper is inside).
        /// </summary>
        [TestMethod]
        public void NoDoorIsPassableFromItsLockedSide_WithoutItsLeverOrKeeper()
        {
            var r = Load();

            foreach (var (key, area) in r.Levers)
            {
                var door = r.Doors.Single(d => d.Key == key);
                Assert.AreEqual(OuterSide(door), area, $"lever {key} stands on the outer side of its door");
                Assert.IsFalse(Rooms.Contains(area), $"lever {key} is not inside any room");
            }

            foreach (var (id, area, keys) in r.Guardians)
            {
                Assert.IsTrue(Rooms.Contains(area), $"{id} stands inside a room");
                CollectionAssert.AreEquivalent(DoorsOf(r, area).Select(d => d.Key).ToList(), keys, $"{id} opens both doors of its own room, and nothing else");
            }

            foreach (var room in Rooms)
            {
                var doors = DoorsOf(r, room);
                Assert.AreEqual(2, doors.Count, room);

                foreach (var outside in doors.Select(OuterSide))
                {
                    var reached = Reach(r, outside, new HashSet<string>(), doors.Select(d => d.Key).ToList());
                    Assert.IsFalse(reached.Contains(room), $"{room} is reachable from {outside} with neither of its levers pulled");
                }
            }
        }

        /// <summary>
        /// Owner ruling 2026-10-25: a room is enterable by lever from EITHER side, and its keeper's death opens BOTH of its
        /// doors. From each door's outer side, with the room's other lever never pulled and no keeper killed, that door's
        /// own lever lets the runner in; and entering by one door then killing the keeper opens the other door too.
        /// </summary>
        [TestMethod]
        public void EveryRoom_IsEnterableByLeverFromEitherSide_AndItsKeeperOpensBothDoors()
        {
            var r = Load();
            var allKeepers = r.Guardians.Select(g => g.Id).ToList();

            foreach (var room in Rooms)
            {
                var doors = DoorsOf(r, room);
                var keeper = r.Guardians.Single(g => g.Area == room);

                foreach (var door in doors)
                {
                    var other = doors.Single(d => d != door);

                    var byLever = Reach(r, OuterSide(door), new HashSet<string>(), r.Levers.Select(l => l.Key).Where(k => k != door.Key).ToList(), allKeepers);
                    Assert.IsTrue(byLever.Contains(room), $"{room}: its {door.Key} lever does not let the runner in from {OuterSide(door)}");

                    // Only this door's lever and this room's keeper: the keeper opens the other door, and the far side is reached.
                    var opened = new HashSet<string>();
                    var withKeeper = Reach(r, OuterSide(door), opened, r.Levers.Select(l => l.Key).Where(k => k != door.Key).ToList(), allKeepers.Where(k => k != keeper.Id).ToList());
                    Assert.IsTrue(opened.Contains(other.Key), $"{room}: {keeper.Id}'s death does not open {other.Key}");
                    Assert.IsTrue(withKeeper.Contains(OuterSide(other)), $"{room}: entering by {door.Key} and killing {keeper.Id} does not lead out to {OuterSide(other)}");
                }
            }
        }

        /// <summary>All six orders of the three wings complete: each wing is reached, its chute taken, and the Seal reached at the end.</summary>
        [TestMethod]
        public void AllSixWingOrders_Complete()
        {
            var r = Load();
            var problems = new List<string>();
            var count = 0;

            foreach (var order in Orders())
            {
                count++;
                var opened = new HashSet<string>();
                var at = West;
                var trail = new List<string> { at };

                foreach (var wing in order)
                {
                    var goal = WingGoal(r, wing);

                    if (!Reach(r, at, opened).Contains(goal))
                    {
                        problems.Add($"{string.Join(">", order)}: {wing} ({goal}) not reachable from {at} with [{string.Join(",", opened.OrderBy(k => k))}] open");
                        break;
                    }

                    var (from, to) = r.Chutes[WingChute(wing)];
                    Assert.AreEqual(goal, from, $"{wing}: its chute stands where the wing ends");
                    at = to;
                    trail.Add($"{wing}->{at}");
                }

                if (!Reach(r, at, opened).Contains(r.SealArea))
                    problems.Add($"{string.Join(">", order)}: the Seal is not reachable from {at} at the end ({string.Join(" ", trail)})");
            }

            Assert.AreEqual(6, count);
            Assert.AreEqual(0, problems.Count, string.Join(Environment.NewLine, problems));
        }

        /// <summary>
        /// A runner who opens only what the route needs: walking needs no action, and every lever pull or keeper kill is
        /// explicit and must be reachable from where the runner stands with the doors opened so far.
        /// </summary>
        private sealed class MinimalWalker
        {
            private readonly Route r;
            private readonly List<string> allLevers;
            private readonly List<string> allKeepers;
            public readonly HashSet<string> Opened = new HashSet<string>();
            public string At = West;

            public MinimalWalker(Route route)
            {
                r = route;
                allLevers = route.Levers.Select(l => l.Key).ToList();
                allKeepers = route.Guardians.Select(g => g.Id).ToList();
            }

            private HashSet<string> Here() => Reach(r, At, new HashSet<string>(Opened), allLevers, allKeepers);

            public void Pull(string key)
            {
                var area = r.Levers.Single(l => l.Key == key).Area;
                Assert.IsTrue(Here().Contains(area), $"lever {key} ({area}) is not reachable from {At} with [{string.Join(",", Opened)}]");
                Opened.Add(key);
            }

            public void Kill(string id)
            {
                var keeper = r.Guardians.Single(g => g.Id == id);
                Assert.IsTrue(Here().Contains(keeper.Area), $"{id} ({keeper.Area}) is not reachable from {At} with [{string.Join(",", Opened)}]");

                foreach (var key in keeper.Keys)
                    Opened.Add(key);
            }

            public void Wing(string wing)
            {
                var goal = WingGoal(r, wing);
                Assert.IsTrue(Here().Contains(goal), $"{wing} ({goal}) is not reachable from {At} with [{string.Join(",", Opened)}]");
                At = r.Chutes[WingChute(wing)].To;
            }

            public void ToTheSeal()
                => Assert.IsTrue(Here().Contains(r.SealArea), $"the Seal is not reachable from {At} with [{string.Join(",", Opened)}]");
        }

        /// <summary>
        /// A legitimate run clears only three corner rooms. Walked with nothing opened that the route does not need, every
        /// door left unlatched at the Book must be one of the course's optionalGateKeys, or the finish backstop forfeits the
        /// run. With levers on both sides of every room, the skipped room can now be any of SE, NE or NW (the north arm is
        /// reached from the east through NE's keeper-side lever).
        /// </summary>
        [TestMethod]
        public void AMinimalThreeRoomRun_LeavesOnlyOptionalGatesUnlatched()
        {
            var r = Load();

            CollectionAssert.AreEquivalent(r.Doors.Select(d => d.Key).ToList(), r.OptionalGateKeys, "every corner-room door is optional, nothing else is");

            // East through NE: north, east, south. SE is skipped.
            var viaNe = new MinimalWalker(r);
            viaNe.Pull("bb_nw_in"); viaNe.Kill("G_NW"); viaNe.Wing("north");
            viaNe.Pull("bb_ne_in"); viaNe.Kill("G_NE"); viaNe.Wing("east");
            viaNe.Pull("bb_sw_in"); viaNe.Kill("G_SW"); viaNe.Wing("south");
            viaNe.ToTheSeal();

            // East through SE: south, east, north. NE is skipped.
            var viaSe = new MinimalWalker(r);
            viaSe.Pull("bb_sw_in"); viaSe.Kill("G_SW"); viaSe.Wing("south");
            viaSe.Pull("bb_se_in"); viaSe.Kill("G_SE"); viaSe.Wing("east");
            viaSe.Pull("bb_nw_in"); viaSe.Kill("G_NW"); viaSe.Wing("north");
            viaSe.ToTheSeal();

            // South, east, then north from the east through NE's keeper-side lever. NW is skipped.
            var reverseNe = new MinimalWalker(r);
            reverseNe.Pull("bb_sw_in"); reverseNe.Kill("G_SW"); reverseNe.Wing("south");
            reverseNe.Pull("bb_se_in"); reverseNe.Kill("G_SE"); reverseNe.Wing("east");
            reverseNe.Pull("bb_ne_out"); reverseNe.Kill("G_NE"); reverseNe.Wing("north");
            reverseNe.ToTheSeal();

            foreach (var (name, walker, skipped) in new[] { ("via NE", viaNe, "se"), ("via SE", viaSe, "ne"), ("NE from the east", reverseNe, "nw") })
            {
                var unlatched = r.Doors.Select(d => d.Key).Where(k => !walker.Opened.Contains(k)).OrderBy(k => k).ToList();

                CollectionAssert.AreEqual(new[] { $"bb_{skipped}_in", $"bb_{skipped}_out" }, unlatched, $"{name}: the skipped room's doors stay shut");

                foreach (var key in unlatched)
                    Assert.IsTrue(r.OptionalGateKeys.Contains(key), $"{name}: {key} is unlatched at the Book but not optional: the finish would forfeit the run");
            }
        }

        /// <summary>
        /// The control: the keepers really gate the wings from the W junction. With no keeper killed, nothing beyond a room
        /// is reachable (the keeper-side levers stand on the far side, out of reach); with only the NW keeper killed the
        /// north arm opens and the east stays shut until a second room is cleared.
        /// </summary>
        [TestMethod]
        public void Control_TheKeepersGateTheWings()
        {
            var r = Load();
            var allKeepers = r.Guardians.Select(g => g.Id).ToList();

            var none = Reach(r, West, new HashSet<string>(), null, allKeepers);
            CollectionAssert.AreEquivalent(new[] { West, RoomNW, RoomSW }, none.ToList(), "levers only: the two west rooms and nothing beyond");

            var nwOnly = Reach(r, West, new HashSet<string>(), null, allKeepers.Where(k => k != "G_NW").ToList());
            Assert.IsTrue(nwOnly.Contains(North) && nwOnly.Contains(NorthTerminal) && nwOnly.Contains(RoomNE));
            Assert.IsFalse(nwOnly.Contains(East), "the east needs the NE or SE keeper");
            Assert.IsFalse(nwOnly.Contains(South), "the south needs the SW keeper");
            Assert.IsFalse(nwOnly.Contains(Hub), "the Seal never opens in the walk");
        }
    }
}
