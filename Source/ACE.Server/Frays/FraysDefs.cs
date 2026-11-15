using System.Collections.Generic;
using System.Linq;
using System.Numerics;

namespace ACE.Server.Frays
{
    /// <summary>One spawn point: a retail creature position that was in the room (cell + landblock-local position).</summary>
    public readonly struct FraysSpawnPoint
    {
        public readonly ushort Cell;
        public readonly Vector3 Pos;

        public FraysSpawnPoint(ushort cell, float x, float y, float z)
        {
            Cell = cell;
            Pos = new Vector3(x, y, z);
        }
    }

    /// <summary>One creature of a room's mix: the weenie, its weight, and the highest Heat it still spawns at (-1 = any).</summary>
    public readonly struct FraysMixEntry
    {
        public readonly uint Wcid;
        public readonly int Weight;
        public readonly int MaxHeat;

        public FraysMixEntry(uint wcid, int weight, int maxHeat = -1)
        {
            Wcid = wcid;
            Weight = weight;
            MaxHeat = maxHeat;
        }
    }

    /// <summary>A campfire: where it stands, its room's cells, its spawn points and its creature mix.</summary>
    public sealed class FraysFireDef
    {
        /// <summary>Short id used by commands and the per-fire overrides ("eo1").</summary>
        public string Id { get; init; }

        /// <summary>The room's name, shown on the fire.</summary>
        public string RoomName { get; init; }

        public ushort Landblock { get; init; }

        /// <summary>The Fray this fire belongs to (its fellowship, family, name and shared bars). 0 = its own landblock.</summary>
        public ushort Fray { get; init; }

        public ushort FrayKey => Fray != 0 ? Fray : Landblock;

        /// <summary>An outdoor zone: the whole landblock is the zone (no room cells); spawn points burn out per camper.</summary>
        public bool Outdoor { get; init; }

        /// <summary>The fire's cell and landblock-local position (on the floor).</summary>
        public ushort FireCell { get; init; }
        public Vector3 FirePos { get; init; }

        /// <summary>The room's own EnvCells (the leash zone before the doorway margin).</summary>
        public IReadOnlyList<ushort> RoomCells { get; init; }

        public IReadOnlyList<FraysSpawnPoint> SpawnPoints { get; init; }

        public IReadOnlyList<FraysMixEntry> Mix { get; init; }

        /// <summary>The mix entries that may spawn at <paramref name="heat"/>.</summary>
        public IEnumerable<FraysMixEntry> MixAt(int heat) => Mix.Where(m => m.Weight > 0 && (m.MaxHeat < 0 || heat <= m.MaxHeat));

        /// <summary>A group camp (the Sky Fray): a fellowship holds the fire and shares one Heat (FraysFire, group part).</summary>
        public bool Group { get; init; }

        /// <summary>A big group camp (the Sky Fray's middle): needs group_min_size_middle members, not group_min_size.</summary>
        public bool BigGroup { get; init; }

        /// <summary>Outdoors: the zone circle's centre (landblock-local x, y); null = the middle of the landblock.</summary>
        public Vector2? ZoneCentre { get; init; }

        /// <summary>Outdoors: the zone circle's radius in metres; 0 = the zone_radius setting.</summary>
        public float ZoneRadius { get; init; }

        /// <summary>
        /// Outdoors on a floor above the ground (the temple's floating plateau): positions keep their own height instead of
        /// being put on the terrain, and the zone holds only what stands above this height. Null = on the terrain.
        /// </summary>
        public float? FloorAbove { get; init; }

        /// <summary>
        /// Outdoors: the owner's wisps stand in a straight line from A to B instead of round the zone's circle (a plateau
        /// arm: a line across the arm where it meets the middle square). Null = the circle.
        /// </summary>
        public (Vector2 A, Vector2 B)? WispLine { get; init; }
    }

    /// <summary>
    /// The Frays' campfires: Vissidal Island's coast and heights (solo camps) and the Sky Fray (group camps on the
    /// temple's plateau). The Egg Orchard and Elemental Caverns dungeon Frays were removed.
    /// </summary>
    public static class FraysDefs
    {

        /// <summary>Vissidal Island's two Frays (Fray keys, not landblocks): the low coast and the heights.</summary>
        public const ushort VissidalCoast = 0xC8E2;
        public const ushort VissidalHeights = 0xD5DF;

        /// <summary>
        /// The Sky Fray (fellowship-based group campfires): four group
        /// camps on the Temple of Xik Minru's floating plateau (0xD5DA, floor at z 116), one on each arm. The Sky Fray portal
        /// (2000122) lands you on the plateau's middle square, which stays a safe meeting spot. Its Fray key is the
        /// plateau's landblock.
        /// </summary>
        public const ushort VissidalSky = 0xD5DA;

        /// <summary>The plateau's walkable floor height, and the height a position must stand above to count as on it.</summary>
        public const float PlateauFloor = 116f;
        public const float PlateauAbove = 100f;

        /// <summary>An arm camp's zone radius: the arm and its two side pods, clear of the middle square.</summary>
        public const float PlateauCampRadius = 30f;

        /// <summary>
        /// Each arm's wisps: a line across the arm this far out from the plateau's middle (108, 108), this far either side of
        /// the arm's middle line (set on site at the north arm's inner end, 32.7 m
        /// out). The arms' floors run about 27 m either side (their spawn points).
        /// </summary>
        public const float PlateauWispLineOut = 32.7f;
        public const float PlateauWispLineHalf = 28f;
        public static readonly Vector2 PlateauMiddle = new Vector2(108f, 108f);

        /// <summary>How far out from the middle each wing's fire stands: the North fire at the spire foot (108, 191.2), the others turned to match.</summary>
        public const float WingFireOut = 83.2f;

        public const uint CampfireWcid = 2000029;
        public const uint ColdCampfireWcid = 2000033;
        public const uint HearthKeeperWcid = 2000030;
        public const uint QuartermasterWcid = 2000031;
        public const uint EmberWcid = 2000032;

        // Village NPCs: three shopkeepers copied from Marketplace vendors, and two explainers.
        public const uint ApothecaryWcid = 2000159;   // a copy of Yamira bint Sabra the Apothecary (1001850)
        public const uint ArchmageWcid = 2000160;     // a copy of Aun Amanaualuan the Elder Shaman (24594)
        public const uint BowyerWcid = 2000161;       // a copy of Laurana (44076)
        public const uint BoonwrightWcid = 2000162;
        public const uint StewardWcid = 2000163;

        /// <summary>The Fray-Warden and the Ember Trader in the Fray village.</summary>
        public static readonly IReadOnlyList<(uint Wcid, ushort Landblock, ushort Cell, Vector3 Pos, Quaternion Facing)> EntranceNpcs = new[]
        {
            // Vissidal: just outside the Temple of Xik Minru's door (0xD5DA 179.8, 167.3), facing it. The island portal drops you
            // inside the temple facing south; you walk out to them.
            // NPCs are spread round the village: the Hearth-Keeper outside the Great Hall's porch, the
            // Quartermaster outside the Cottage's front door, both facing out (south-west, toward the temple).
            (HearthKeeperWcid, V, (ushort)0x30, new Vector3(132.12f, 186.87f, 0.0f), new Quaternion(0, 0, 0.9238795f, 0.3826834f)),
            (QuartermasterWcid, (ushort)0xD6DA, (ushort)0x07, new Vector3(7.8f, 154.5f, 0.0f), new Quaternion(0, 0, 0.9238795f, 0.3826834f)),

            // The Fray Guide sign (an in-game sign object like the two at the Marketplace), between them.
            (GuideSignWcid, V, (ushort)0x3F, new Vector3(180.0f, 158.5f, 0.0f), new Quaternion(0, 0, 1, 0)),   // the sign model reads from its back: turned to face the door
        };

        /// <summary>The landblock of Vissidal's arrival: the Temple of Xik Minru (the island portal drops you inside it at 179.7, 178.3).</summary>
        public const ushort V = 0xD5DA;

        /// <summary>The Fray Guide sign (dreamweave-content 2000095, a copy of the Marketplace sign).</summary>
        public const uint GuideSignWcid = 2000095;

        /// <summary>The Sky Fray portal (dreamweave-content 2000122): up to the temple's floating platform, where the group Frays go.</summary>
        public const uint SkyFrayPortal = 2000122;

        /// <summary>Retail's Life Stone, in the Vissidal village.</summary>
        public const uint LifeStone = 509;

        /// <summary>A brazier's flame (dreamweave-content 2000147), and how high above the ground it sits in the brazier's bowl.</summary>
        public const uint BrazierFlame = 2000147;
        public const float BrazierFlameHeight = 1.95f;

        // The Fray village decor (dreamweave-content 2000096-2000106: retail pieces in retail colours for now, Stuck).
        public const uint EmberCookfire = 2000096, EmberBrazier = 2000097, EmberTorch = 2000098, AshenWell = 2000099, FrayBanner = 2000100,
            CharredBarrel = 2000101, CharredCrate = 2000102, AshenBench = 2000103, EmberTable = 2000104, CharredCart = 2000105, EmberBedroll = 2000106;

        // The town's buildings (dreamweave-content 2000107-2000121: Arwic's building models on placeable objects, full size, solid).
        public const uint ArwicCottage = 2000107, ArwicHouse = 2000108, ArwicManor = 2000109, ArwicShop = 2000110, ArwicHall = 2000111,
            ArwicTower = 2000112, ArwicWall = 2000113, ArwicGateA = 2000114, ArwicGateB = 2000115;
        public const uint ArwicGreatHall = 2000116, ArwicChapel = 2000117, ArwicWallB = 2000118, ArwicWallC = 2000119, ArwicGatehouse = 2000120, ArwicWallD = 2000121;

        /// <summary>
        /// The Vissidal village round the Temple of Xik Minru (a walled ring round the temple with
        /// the arrival camp inside, three exits, four buildings and a lifestone). Laid out
        /// in a frame centred on the temple (D5DA 180, 180): u runs north-east, up toward the hills; v runs south-east, along the
        /// beach. Arwic's own wall and watchtower: an eight-sided ring of 24 m wall sections with a watchtower on every corner
        /// (the towers hide the angled joins). Exits: the water gate in front of the temple's door (the Sky Fray portal stands in
        /// the shallows outside it) and a tower-flanked gate at each end of the beach (east-south-east and north-west).
        /// </summary>

        /// <summary>The ring's corners (u, v), in order round it.</summary>
        public static readonly (float U, float V)[] RingCorners =
        {
            // Corners 1 and 2 are global (144, 142) and (216, 142): the south wall runs east-west, square to the temple's door,
            // with the water gate in its middle on the temple's centre line.
            // Corner 0 is a water-side /loc taken on site.
            // Corners 4 to 6 keep off the north-east hill (the walls
            // there ran up ground 3 to 10 m high and sank into it); every side now stands on ground 1.4 m high or less. Corners 6 to 8 push the north-west out over the flat beach.
            (-52.326f, -22.586f), (-52.326f, 1.414f), (-1.414f, 52.326f), (20f, 53f), (36f, 14f), (30f, -14f), (14f, -64f), (-10f, -62f), (-35.2f, -46.7f),
        };

        /// <summary>Per side (corner i to i + 1): how many 24 m sections, and which are left out (gates).</summary>
        private static readonly (int Sections, int[] Gaps)[] RingSides =
        {
            (1, System.Array.Empty<int>()),   // the beach, west of the gate
            (3, new[] { 1 }),            // the south wall: the water gate in its middle, straight out from the temple's door
            (1, new[] { 0 }),            // the east-south-east gate, between two towers
            (2, System.Array.Empty<int>()),
            (1, System.Array.Empty<int>()),   // the east side, just below the hill (the lifestone stands on the rise inside it)
            (2, System.Array.Empty<int>()),
            (1, new[] { 0 }),            // the north-west gate, between two towers
            (1, System.Array.Empty<int>()),   // the north-west beach, pushed out 8 m so the Great Hall clears the rocks
            (1, System.Array.Empty<int>()),   // back to the beach corner
        };

        public static readonly IReadOnlyList<(uint Wcid, ushort Landblock, float X, float Y, float Facing)> TownPieces = Town();
        /// <summary>Temple-frame (u, v) to D5DA-relative global x, y.</summary>
        public static (float X, float Y) TempleToGlobal(float u, float v)
        {
            var r = (float)System.Math.Sqrt(2);
            return (180 + (u + v) / r, 180 + (u - v) / r);
        }

        /// <summary>A piece at a D5DA-relative global spot, with its landblock and local spot worked out.</summary>
        public static (uint Wcid, ushort Landblock, float X, float Y, float Facing) AtGlobal(uint wcid, float gx, float gy, float facing)
        {
            int bx = (int)System.Math.Floor(gx / 192), by = (int)System.Math.Floor(gy / 192);
            return (wcid, (ushort)(((0xD5 + bx) << 8) | (0xDA + by)), gx - 192 * bx, gy - 192 * by, facing);
        }

        /// <summary>A village piece at (u, v) in the temple's frame, with its facing in degrees.</summary>
        public static (uint Wcid, ushort Landblock, float X, float Y, float Facing) AtTemple(uint wcid, float u, float v, float facing)
        {
            var (gx, gy) = TempleToGlobal(u, v);
            return AtGlobal(wcid, gx, gy, facing);
        }

        /// <summary>Arwic's wall: its centre line is 8.15 m off its origin (local -y); its parapet faces local +y (out).</summary>
        public const float WallLineOffset = -8.15f;

        /// <summary>Arwic's watchtower: its middle is at local (-8.5, -8.5).</summary>
        public const float TowerOffset = -8.5f;

        private static (uint, ushort, float, float, float)[] Town()
        {
            uint House(int n) => TownHouseFirst + (uint)n - 1;
            const float NE = -45f;   // facing that squares a building to the temple frame (local x along v, local y along u)

            var list = new List<(uint, ushort, float, float, float)>
            {
                // The Great Hall sits west-
                // south-west of the temple beside the arrival camp, on dead flat ground clear of the landscape rocks, its porch
                // (local +y) toward the temple. Houses 5 and 6 moved to the north-west spot it left.
                // It sits 9 m further south-west (2.5 m off the
                // ring wall) and turned a quarter (facing 45) so its one outside door, on the back corner, faces east toward the temple.
                AtTemple(ArwicGreatHall, -37f, -11f, 45f),
                AtTemple(ArwicCottage, 8.5f, 32f, NE),
                // Town House 5 (the pagoda) and Town House 6 (the stone cottage) side by side, first in the beach corner fronts facing
                // east-north-east toward the temple, the pagoda on the left as you face them (placed on site at
                // 0xD5DA002F 133.7, 167.5, in place of town house 2; moved 6 m in off the wall).
                // Houses 5 and 6 now stand against the north-east wall, facing south-west: 5 backs onto the north-east wall over
                // the two big rocks there (hidden inside it); 6 in the corner where that wall meets the north one. The lifestone
                // stays in the 5 m gap between them. Both doors face south-west into the village.
                AtTemple(House(5), 25.4f, 2.5f, NE),
                AtTemple(House(6), 19f, -13.5f, NE),
            };

            var g = RingCorners.Select(c => TempleToGlobal(c.U, c.V)).ToArray();
            float cx = g.Average(p => p.X), cy = g.Average(p => p.Y);

            for (var i = 0; i < g.Length; i++)
            {
                var a = g[i];
                var b = g[(i + 1) % g.Length];
                float dx = b.X - a.X, dy = b.Y - a.Y, len = (float)System.Math.Sqrt(dx * dx + dy * dy);
                float ux = dx / len, uy = dy / len;

                // Local +y (the parapet) must face out of the ring.
                float mx = (a.X + b.X) / 2 - cx, my = (a.Y + b.Y) / 2 - cy;
                var flip = (-uy * mx + ux * my) < 0;
                float fx = flip ? -ux : ux, fy = flip ? -uy : uy;
                var f = (float)(System.Math.Atan2(fy, fx) * 180 / System.Math.PI);
                float s = (float)System.Math.Sin(f * System.Math.PI / 180), c = (float)System.Math.Cos(f * System.Math.PI / 180);

                var (n, gaps) = RingSides[i];
                var start = (len - n * 24) / 2;

                (uint, ushort, float, float, float) TowerAt(float px, float py) =>
                    AtGlobal(ArwicTower, px - (TowerOffset * c - TowerOffset * s), py - (TowerOffset * s + TowerOffset * c), f);

                for (var k = 0; k < n; k++)
                {
                    if (gaps.Contains(k))
                    {
                        // A gate partway along a side: a watchtower on each open wall end, so it is flanked like the others.
                        foreach (var edge in new[] { start + 24 * k, start + 24 * (k + 1) })
                            if (edge > 4 && edge < len - 4)
                                list.Add(TowerAt(a.X + ux * edge, a.Y + uy * edge));
                        continue;
                    }

                    var t = start + 12 + 24 * k;
                    float px = a.X + ux * t, py = a.Y + uy * t;
                    // origin = centre - R(f) * (0, WallLineOffset)
                    list.Add(AtGlobal(ArwicWall, px + WallLineOffset * s, py - WallLineOffset * c, f));
                }

                // The watchtower on corner i, square to the side leaving it.
                list.Add(TowerAt(a.X, a.Y));
            }

            return list.ToArray();
        }

        private const uint TownHouseFirst = 2000124;   // FraysHouses.FirstWcid (Town House 1)

        /// <summary>The Fray town's door (dreamweave-content 2000123): Arwic's door, locked shut.</summary>
        public const uint TownDoor = 2000123;

        /// <summary>
        /// Loose town doors. None now: every building's doorways are covered by FraysHouses, which places the
        /// covers with the building.
        /// </summary>
        public static readonly IReadOnlyList<(uint Wcid, ushort Landblock, float X, float Y, float Z, float Facing)> TownDoors =
            System.Array.Empty<(uint, ushort, float, float, float, float)>();

        /// <summary>The village's Fray Tapestry (dreamweave-content 2000173: Stuck, no gravity, so it stays up).</summary>
        public const uint VillageTapestry = 2000173;

        /// <summary>
        /// Things hung on the village walls: Z is the height above the ground.
        /// The Fray Tapestry hangs on the inside of the south wall (its line is y 142), west of
        /// the water gate, facing north into the village.
        /// </summary>
        public static readonly IReadOnlyList<(uint Wcid, ushort Landblock, float X, float Y, float Z, float Facing)> Hangings = new[]
        {
            (VillageTapestry, V, 150.5f, 145.0f, 2.5f, 0f),
        };
        /// <summary>
        /// The little camp round each arrival point's NPCs.
        /// Cell 0 means outdoors: the cell comes from the position. Facing is in degrees.
        /// </summary>
        public static readonly IReadOnlyList<(uint Wcid, ushort Landblock, ushort Cell, Vector3 Pos, float Facing)> Village = new[]
        {
            // Vissidal: the square outside the temple's door, and the Sky Fray portal on the shore outside the town (placed
            // on site: 0xD5DA0036 157.1, 129.5).
            (EmberBrazier,  V, (ushort)0, new Vector3(170f, 160f, 0f), 0f),
            (EmberBrazier,  V, (ushort)0, new Vector3(190f, 160f, 0f), 0f),
            // A flame in each brazier's bowl; Z is the height above the ground.
            (BrazierFlame,  V, (ushort)0, new Vector3(170f, 160f, BrazierFlameHeight), 0f),
            (BrazierFlame,  V, (ushort)0, new Vector3(190f, 160f, BrazierFlameHeight), 0f),
            (AshenBench,    V, (ushort)0, new Vector3(176f, 148f, 0f), 90f),
            (AshenBench,    V, (ushort)0, new Vector3(184f, 148f, 0f), -90f),
            (EmberTorch,    V, (ushort)0, new Vector3(172f, 152f, 0f), 0f),
            (EmberTorch,    V, (ushort)0, new Vector3(188f, 152f, 0f), 0f),
            (FrayBanner,    V, (ushort)0, new Vector3(175f, 165f, 0f), 0f),
            (FrayBanner,    V, (ushort)0, new Vector3(185f, 165f, 0f), 0f),
            (SkyFrayPortal, V, (ushort)0, new Vector3(180.8f, 103.9f, 0f), 4f),   // out in the water through the gate (placed on site, 0xD5DA003D 180.8, 103.9)
            (LifeStone, (ushort)0xD6DB, (ushort)0, new Vector3(3.56f, 9.21f, 0f), 0f),   // on the rise where the Great Hall stood, inside the east wall (u 26, v -4)
        };

        public const uint OlthoiRipper = 31005;
        public const uint OlthoiSlasher = 31007;
        public const uint OlthoiLacerator = 31008;

        // the Golem family's DreamWeave creatures: the fixed mix when families are off
        public const uint DriftRubble = 2000063;
        public const uint DriftGolem = 2000064;
        public const uint DriftGolemViceroy = 2000065;

        private static ushort[] Cells(params ushort[] c) => c;

        public static readonly IReadOnlyList<FraysFireDef> All = new List<FraysFireDef>
        {
            new FraysFireDef
            {
                Id = "vc1", RoomName = "Ember Landing", Landblock = 0xC9E2, Fray = VissidalCoast, Outdoor = true,
                FireCell = 0x22, FirePos = new Vector3(96.0f, 44.0f, 2.0f),   // moved south toward the Western Shore landing, about 58 m off, so shore fires sit fairly close together; was 0x1C 84, 84
                RoomCells = Cells(),
                SpawnPoints = new[]
                {
                    new FraysSpawnPoint(0x40, 174.0f, 174.0f, 4.0f),
                    new FraysSpawnPoint(0x08, 18.0f, 174.0f, 4.0f),
                    new FraysSpawnPoint(0x39, 174.0f, 18.0f, 1.0f),
                    new FraysSpawnPoint(0x01, 18.0f, 18.0f, 0.0f),
                    new FraysSpawnPoint(0x27, 108.0f, 156.0f, 4.0f),
                    new FraysSpawnPoint(0x04, 18.0f, 84.0f, 3.0f),
                    new FraysSpawnPoint(0x19, 84.0f, 18.0f, 0.0f),
                    new FraysSpawnPoint(0x34, 156.0f, 84.0f, 4.0f),
                    new FraysSpawnPoint(0x0E, 36.0f, 132.0f, 4.0f),
                    new FraysSpawnPoint(0x2A, 132.0f, 36.0f, 2.0f),
                    new FraysSpawnPoint(0x18, 60.0f, 174.0f, 4.0f),
                    new FraysSpawnPoint(0x36, 156.0f, 132.0f, 4.0f),
                    new FraysSpawnPoint(0x0A, 36.0f, 36.0f, 0.0f),
                    new FraysSpawnPoint(0x13, 60.0f, 60.0f, 3.0f),
                    new FraysSpawnPoint(0x15, 60.0f, 108.0f, 4.0f),
                    new FraysSpawnPoint(0x1E, 84.0f, 132.0f, 4.0f),
                    new FraysSpawnPoint(0x23, 108.0f, 60.0f, 4.0f),
                    new FraysSpawnPoint(0x25, 108.0f, 108.0f, 4.0f),
                    new FraysSpawnPoint(0x30, 132.0f, 174.0f, 4.0f),
                    new FraysSpawnPoint(0x3B, 174.0f, 60.0f, 4.0f),
                },
                Mix = new[] { new FraysMixEntry(DriftGolem, 2), new FraysMixEntry(DriftRubble, 2), new FraysMixEntry(DriftGolemViceroy, 1) },
            },
            new FraysFireDef
            {
                Id = "vc2", RoomName = "Driftwood Flats", Landblock = 0xCAE2, Fray = VissidalCoast, Outdoor = true,
                FireCell = 0x1C, FirePos = new Vector3(84.0f, 84.0f, 4.0f),
                RoomCells = Cells(),
                SpawnPoints = new[]
                {
                    new FraysSpawnPoint(0x40, 174.0f, 174.0f, 4.0f),
                    new FraysSpawnPoint(0x08, 18.0f, 174.0f, 4.0f),
                    new FraysSpawnPoint(0x39, 174.0f, 18.0f, 4.0f),
                    new FraysSpawnPoint(0x01, 18.0f, 18.0f, 2.0f),
                    new FraysSpawnPoint(0x27, 108.0f, 156.0f, 4.0f),
                    new FraysSpawnPoint(0x04, 18.0f, 84.0f, 4.0f),
                    new FraysSpawnPoint(0x19, 84.0f, 18.0f, 4.0f),
                    new FraysSpawnPoint(0x34, 156.0f, 84.0f, 4.0f),
                    new FraysSpawnPoint(0x0E, 36.0f, 132.0f, 4.0f),
                    new FraysSpawnPoint(0x2A, 132.0f, 36.0f, 4.0f),
                    new FraysSpawnPoint(0x18, 60.0f, 174.0f, 4.0f),
                    new FraysSpawnPoint(0x36, 156.0f, 132.0f, 4.0f),
                    new FraysSpawnPoint(0x0A, 36.0f, 36.0f, 4.0f),
                    new FraysSpawnPoint(0x13, 60.0f, 60.0f, 4.0f),
                    new FraysSpawnPoint(0x15, 60.0f, 108.0f, 4.0f),
                    new FraysSpawnPoint(0x1E, 84.0f, 132.0f, 4.0f),
                    new FraysSpawnPoint(0x23, 108.0f, 60.0f, 4.0f),
                    new FraysSpawnPoint(0x25, 108.0f, 108.0f, 4.0f),
                    new FraysSpawnPoint(0x30, 132.0f, 174.0f, 4.0f),
                    new FraysSpawnPoint(0x3B, 174.0f, 60.0f, 4.0f),
                },
                Mix = new[] { new FraysMixEntry(DriftGolem, 2), new FraysMixEntry(DriftRubble, 2), new FraysMixEntry(DriftGolemViceroy, 1) },
            },
            new FraysFireDef
            {
                Id = "vc3", RoomName = "Saltglass Shore", Landblock = 0xCCE0, Fray = VissidalCoast, Outdoor = true,
                FireCell = 0x1C, FirePos = new Vector3(84.0f, 84.0f, 4.0f),
                RoomCells = Cells(),
                SpawnPoints = new[]
                {
                    new FraysSpawnPoint(0x40, 174.0f, 174.0f, 0.0f),
                    new FraysSpawnPoint(0x08, 18.0f, 174.0f, 4.0f),
                    new FraysSpawnPoint(0x39, 174.0f, 18.0f, 1.5f),
                    new FraysSpawnPoint(0x01, 18.0f, 18.0f, 1.0f),
                    new FraysSpawnPoint(0x27, 108.0f, 156.0f, 4.0f),
                    new FraysSpawnPoint(0x04, 18.0f, 84.0f, 4.0f),
                    new FraysSpawnPoint(0x19, 84.0f, 18.0f, 4.0f),
                    new FraysSpawnPoint(0x34, 156.0f, 84.0f, 4.0f),
                    new FraysSpawnPoint(0x0E, 36.0f, 132.0f, 4.0f),
                    new FraysSpawnPoint(0x2A, 132.0f, 36.0f, 4.0f),
                    new FraysSpawnPoint(0x18, 60.0f, 174.0f, 4.0f),
                    new FraysSpawnPoint(0x36, 156.0f, 132.0f, 3.5f),
                    new FraysSpawnPoint(0x0A, 36.0f, 36.0f, 4.0f),
                    new FraysSpawnPoint(0x13, 60.0f, 60.0f, 4.0f),
                    new FraysSpawnPoint(0x15, 60.0f, 108.0f, 4.0f),
                    new FraysSpawnPoint(0x1E, 84.0f, 132.0f, 4.0f),
                    new FraysSpawnPoint(0x23, 108.0f, 60.0f, 4.0f),
                    new FraysSpawnPoint(0x25, 108.0f, 108.0f, 4.0f),
                    new FraysSpawnPoint(0x30, 132.0f, 174.0f, 4.0f),
                    new FraysSpawnPoint(0x3B, 174.0f, 60.0f, 2.5f),
                },
                Mix = new[] { new FraysMixEntry(DriftGolem, 2), new FraysMixEntry(DriftRubble, 2), new FraysMixEntry(DriftGolemViceroy, 1) },
            },
            new FraysFireDef
            {
                Id = "vc4", RoomName = "Kelp Hollow", Landblock = 0xD2DE, Fray = VissidalCoast, Outdoor = true,
                FireCell = 0x1C, FirePos = new Vector3(84.0f, 84.0f, 4.0f),
                RoomCells = Cells(),
                SpawnPoints = new[]
                {
                    new FraysSpawnPoint(0x08, 18.0f, 174.0f, 4.0f),
                    new FraysSpawnPoint(0x38, 156.0f, 174.0f, 4.0f),
                    new FraysSpawnPoint(0x39, 174.0f, 18.0f, 4.0f),
                    new FraysSpawnPoint(0x01, 18.0f, 18.0f, 0.0f),
                    new FraysSpawnPoint(0x3D, 174.0f, 108.0f, 4.0f),
                    new FraysSpawnPoint(0x04, 18.0f, 84.0f, 3.0f),
                    new FraysSpawnPoint(0x19, 84.0f, 18.0f, 2.0f),
                    new FraysSpawnPoint(0x1F, 84.0f, 156.0f, 4.0f),
                    new FraysSpawnPoint(0x0E, 36.0f, 132.0f, 4.0f),
                    new FraysSpawnPoint(0x2A, 132.0f, 36.0f, 4.0f),
                    new FraysSpawnPoint(0x2E, 132.0f, 132.0f, 4.0f),
                    new FraysSpawnPoint(0x2C, 132.0f, 84.0f, 4.0f),
                    new FraysSpawnPoint(0x3B, 174.0f, 60.0f, 4.0f),
                    new FraysSpawnPoint(0x0A, 36.0f, 36.0f, 1.0f),
                    new FraysSpawnPoint(0x13, 60.0f, 60.0f, 4.0f),
                    new FraysSpawnPoint(0x15, 60.0f, 108.0f, 4.0f),
                    new FraysSpawnPoint(0x18, 60.0f, 174.0f, 4.0f),
                    new FraysSpawnPoint(0x23, 108.0f, 60.0f, 4.0f),
                    new FraysSpawnPoint(0x25, 108.0f, 108.0f, 4.0f),
                    new FraysSpawnPoint(0x28, 108.0f, 174.0f, 4.0f),
                },
                Mix = new[] { new FraysMixEntry(DriftGolem, 2), new FraysMixEntry(DriftRubble, 2), new FraysMixEntry(DriftGolemViceroy, 1) },
            },
            new FraysFireDef
            {
                Id = "vc5", RoomName = "Tidewatch", Landblock = 0xD0DF, Fray = VissidalCoast, Outdoor = true,
                FireCell = 0x25, FirePos = new Vector3(108.0f, 108.0f, 2.0f),
                RoomCells = Cells(),
                SpawnPoints = new[]
                {
                    new FraysSpawnPoint(0x01, 18.0f, 18.0f, 0.0f),
                    new FraysSpawnPoint(0x08, 18.0f, 174.0f, 4.0f),
                    new FraysSpawnPoint(0x39, 174.0f, 18.0f, 0.0f),
                    new FraysSpawnPoint(0x40, 174.0f, 174.0f, 4.0f),
                    new FraysSpawnPoint(0x0C, 36.0f, 84.0f, 0.0f),
                    new FraysSpawnPoint(0x20, 84.0f, 174.0f, 4.0f),
                    new FraysSpawnPoint(0x21, 108.0f, 18.0f, 0.0f),
                    new FraysSpawnPoint(0x3C, 174.0f, 84.0f, 3.0f),
                    new FraysSpawnPoint(0x12, 60.0f, 36.0f, 0.0f),
                    new FraysSpawnPoint(0x16, 60.0f, 132.0f, 4.0f),
                    new FraysSpawnPoint(0x2B, 132.0f, 60.0f, 0.0f),
                    new FraysSpawnPoint(0x2F, 132.0f, 156.0f, 4.0f),
                    new FraysSpawnPoint(0x06, 18.0f, 132.0f, 4.0f),
                    new FraysSpawnPoint(0x3E, 174.0f, 132.0f, 4.0f),
                    new FraysSpawnPoint(0x03, 18.0f, 60.0f, 0.0f),
                    new FraysSpawnPoint(0x0F, 36.0f, 156.0f, 4.0f),
                    new FraysSpawnPoint(0x1B, 84.0f, 60.0f, 0.0f),
                    new FraysSpawnPoint(0x32, 156.0f, 36.0f, 0.0f),
                    new FraysSpawnPoint(0x35, 156.0f, 108.0f, 4.0f),
                    new FraysSpawnPoint(0x02, 18.0f, 36.0f, 0.0f),
                },
                Mix = new[] { new FraysMixEntry(DriftGolem, 2), new FraysMixEntry(DriftRubble, 2), new FraysMixEntry(DriftGolemViceroy, 1) },
            },
            new FraysFireDef
            {
                Id = "vc6", RoomName = "Gull Point", Landblock = 0xD3DD, Fray = VissidalCoast, Outdoor = true,
                FireCell = 0x25, FirePos = new Vector3(108.0f, 108.0f, 1.0f),
                RoomCells = Cells(),
                SpawnPoints = new[]
                {
                    new FraysSpawnPoint(0x08, 18.0f, 174.0f, 4.0f),
                    new FraysSpawnPoint(0x39, 174.0f, 18.0f, 0.0f),
                    new FraysSpawnPoint(0x40, 174.0f, 174.0f, 4.0f),
                    new FraysSpawnPoint(0x04, 18.0f, 84.0f, 0.0f),
                    new FraysSpawnPoint(0x19, 84.0f, 18.0f, 0.0f),
                    new FraysSpawnPoint(0x20, 84.0f, 174.0f, 4.0f),
                    new FraysSpawnPoint(0x3C, 174.0f, 84.0f, 4.0f),
                    new FraysSpawnPoint(0x0E, 36.0f, 132.0f, 0.0f),
                    new FraysSpawnPoint(0x13, 60.0f, 60.0f, 0.0f),
                    new FraysSpawnPoint(0x2A, 132.0f, 36.0f, 0.0f),
                    new FraysSpawnPoint(0x2F, 132.0f, 156.0f, 4.0f),
                    new FraysSpawnPoint(0x3E, 174.0f, 132.0f, 4.0f),
                    new FraysSpawnPoint(0x15, 60.0f, 108.0f, 0.0f),
                    new FraysSpawnPoint(0x17, 60.0f, 156.0f, 3.0f),
                    new FraysSpawnPoint(0x1C, 84.0f, 84.0f, 0.0f),
                    new FraysSpawnPoint(0x1E, 84.0f, 132.0f, 2.0f),
                    new FraysSpawnPoint(0x23, 108.0f, 60.0f, 0.0f),
                    new FraysSpawnPoint(0x2C, 132.0f, 84.0f, 2.0f),
                    new FraysSpawnPoint(0x33, 156.0f, 60.0f, 2.0f),
                    new FraysSpawnPoint(0x35, 156.0f, 108.0f, 4.0f),
                },
                Mix = new[] { new FraysMixEntry(DriftGolem, 2), new FraysMixEntry(DriftRubble, 2), new FraysMixEntry(DriftGolemViceroy, 1) },
            },
            new FraysFireDef
            {
                Id = "vc7", RoomName = "Wrack Line", Landblock = 0xD4DC, Fray = VissidalCoast, Outdoor = true,
                FireCell = 0x1C, FirePos = new Vector3(84.0f, 84.0f, 2.0f),
                RoomCells = Cells(),
                SpawnPoints = new[]
                {
                    new FraysSpawnPoint(0x08, 18.0f, 174.0f, 0.0f),
                    new FraysSpawnPoint(0x39, 174.0f, 18.0f, 0.0f),
                    new FraysSpawnPoint(0x30, 132.0f, 174.0f, 4.0f),
                    new FraysSpawnPoint(0x02, 18.0f, 36.0f, 0.0f),
                    new FraysSpawnPoint(0x35, 156.0f, 108.0f, 4.0f),
                    new FraysSpawnPoint(0x05, 18.0f, 108.0f, 0.0f),
                    new FraysSpawnPoint(0x19, 84.0f, 18.0f, 0.0f),
                    new FraysSpawnPoint(0x16, 60.0f, 132.0f, 2.0f),
                    new FraysSpawnPoint(0x2A, 132.0f, 36.0f, 0.0f),
                    new FraysSpawnPoint(0x18, 60.0f, 174.0f, 4.0f),
                    new FraysSpawnPoint(0x26, 108.0f, 132.0f, 4.0f),
                    new FraysSpawnPoint(0x3B, 174.0f, 60.0f, 2.0f),
                    new FraysSpawnPoint(0x09, 36.0f, 18.0f, 0.0f),
                    new FraysSpawnPoint(0x0B, 36.0f, 60.0f, 0.0f),
                    new FraysSpawnPoint(0x0F, 36.0f, 156.0f, 1.0f),
                    new FraysSpawnPoint(0x12, 60.0f, 36.0f, 0.0f),
                    new FraysSpawnPoint(0x1F, 84.0f, 156.0f, 4.0f),
                    new FraysSpawnPoint(0x23, 108.0f, 60.0f, 0.0f),
                    new FraysSpawnPoint(0x2C, 132.0f, 84.0f, 3.0f),
                    new FraysSpawnPoint(0x03, 18.0f, 60.0f, 0.0f),
                },
                Mix = new[] { new FraysMixEntry(DriftGolem, 2), new FraysMixEntry(DriftRubble, 2), new FraysMixEntry(DriftGolemViceroy, 1) },
            },
            new FraysFireDef
            {
                Id = "vc8", RoomName = "Shell Strand", Landblock = 0xCBE1, Fray = VissidalCoast, Outdoor = true,
                FireCell = 0x1C, FirePos = new Vector3(84.0f, 84.0f, 3.0f),
                RoomCells = Cells(),
                SpawnPoints = new[]
                {
                    new FraysSpawnPoint(0x08, 18.0f, 174.0f, 4.0f),
                    new FraysSpawnPoint(0x39, 174.0f, 18.0f, 4.0f),
                    new FraysSpawnPoint(0x30, 132.0f, 174.0f, 4.0f),
                    new FraysSpawnPoint(0x02, 18.0f, 36.0f, 0.0f),
                    new FraysSpawnPoint(0x35, 156.0f, 108.0f, 4.0f),
                    new FraysSpawnPoint(0x05, 18.0f, 108.0f, 0.0f),
                    new FraysSpawnPoint(0x19, 84.0f, 18.0f, 0.0f),
                    new FraysSpawnPoint(0x16, 60.0f, 132.0f, 4.0f),
                    new FraysSpawnPoint(0x2A, 132.0f, 36.0f, 4.0f),
                    new FraysSpawnPoint(0x18, 60.0f, 174.0f, 4.0f),
                    new FraysSpawnPoint(0x26, 108.0f, 132.0f, 4.0f),
                    new FraysSpawnPoint(0x3B, 174.0f, 60.0f, 4.0f),
                    new FraysSpawnPoint(0x09, 36.0f, 18.0f, 0.0f),
                    new FraysSpawnPoint(0x0B, 36.0f, 60.0f, 0.0f),
                    new FraysSpawnPoint(0x0F, 36.0f, 156.0f, 4.0f),
                    new FraysSpawnPoint(0x12, 60.0f, 36.0f, 0.0f),
                    new FraysSpawnPoint(0x1F, 84.0f, 156.0f, 4.0f),
                    new FraysSpawnPoint(0x23, 108.0f, 60.0f, 3.0f),
                    new FraysSpawnPoint(0x2C, 132.0f, 84.0f, 4.0f),
                    new FraysSpawnPoint(0x03, 18.0f, 60.0f, 0.0f),
                },
                Mix = new[] { new FraysMixEntry(DriftGolem, 2), new FraysMixEntry(DriftRubble, 2), new FraysMixEntry(DriftGolemViceroy, 1) },
            },
            new FraysFireDef
            {
                Id = "vc9", RoomName = "Brine Reach", Landblock = 0xD1DF, Fray = VissidalCoast, Outdoor = true,
                FireCell = 0x1C, FirePos = new Vector3(84.0f, 84.0f, 4.0f),
                RoomCells = Cells(),
                SpawnPoints = new[]
                {
                    new FraysSpawnPoint(0x08, 18.0f, 174.0f, 4.0f),
                    new FraysSpawnPoint(0x39, 174.0f, 18.0f, 4.0f),
                    new FraysSpawnPoint(0x01, 18.0f, 18.0f, 0.0f),
                    new FraysSpawnPoint(0x3D, 174.0f, 108.0f, 4.0f),
                    new FraysSpawnPoint(0x04, 18.0f, 84.0f, 4.0f),
                    new FraysSpawnPoint(0x19, 84.0f, 18.0f, 3.0f),
                    new FraysSpawnPoint(0x1F, 84.0f, 156.0f, 4.0f),
                    new FraysSpawnPoint(0x0E, 36.0f, 132.0f, 4.0f),
                    new FraysSpawnPoint(0x2A, 132.0f, 36.0f, 4.0f),
                    new FraysSpawnPoint(0x2C, 132.0f, 84.0f, 4.0f),
                    new FraysSpawnPoint(0x3B, 174.0f, 60.0f, 4.0f),
                    new FraysSpawnPoint(0x0A, 36.0f, 36.0f, 1.0f),
                    new FraysSpawnPoint(0x13, 60.0f, 60.0f, 4.0f),
                    new FraysSpawnPoint(0x15, 60.0f, 108.0f, 4.0f),
                    new FraysSpawnPoint(0x18, 60.0f, 174.0f, 4.0f),
                    new FraysSpawnPoint(0x23, 108.0f, 60.0f, 4.0f),
                    new FraysSpawnPoint(0x25, 108.0f, 108.0f, 4.0f),
                    new FraysSpawnPoint(0x02, 18.0f, 36.0f, 0.0f),
                    new FraysSpawnPoint(0x03, 18.0f, 60.0f, 2.0f),
                    new FraysSpawnPoint(0x05, 18.0f, 108.0f, 4.0f),
                },
                Mix = new[] { new FraysMixEntry(DriftGolem, 2), new FraysMixEntry(DriftRubble, 2), new FraysMixEntry(DriftGolemViceroy, 1) },
            },
            new FraysFireDef
            {
                Id = "vc10", RoomName = "Foam Steps", Landblock = 0xCFDF, Fray = VissidalCoast, Outdoor = true,
                FireCell = 0x23, FirePos = new Vector3(108.0f, 60.0f, 4.0f),
                RoomCells = Cells(),
                SpawnPoints = new[]
                {
                    new FraysSpawnPoint(0x40, 174.0f, 174.0f, 4.0f),
                    new FraysSpawnPoint(0x01, 18.0f, 18.0f, 4.5f),
                    new FraysSpawnPoint(0x39, 174.0f, 18.0f, 0.0f),
                    new FraysSpawnPoint(0x3C, 174.0f, 84.0f, 1.0f),
                    new FraysSpawnPoint(0x0B, 36.0f, 60.0f, 5.0f),
                    new FraysSpawnPoint(0x19, 84.0f, 18.0f, 1.0f),
                    new FraysSpawnPoint(0x2D, 132.0f, 108.0f, 5.0f),
                    new FraysSpawnPoint(0x29, 132.0f, 18.0f, 0.0f),
                    new FraysSpawnPoint(0x3E, 174.0f, 132.0f, 4.0f),
                    new FraysSpawnPoint(0x12, 60.0f, 36.0f, 4.0f),
                    new FraysSpawnPoint(0x32, 156.0f, 36.0f, 0.0f),
                    new FraysSpawnPoint(0x37, 156.0f, 156.0f, 5.5f),
                    new FraysSpawnPoint(0x02, 18.0f, 36.0f, 6.0f),
                    new FraysSpawnPoint(0x03, 18.0f, 60.0f, 7.0f),
                    new FraysSpawnPoint(0x09, 36.0f, 18.0f, 4.0f),
                    new FraysSpawnPoint(0x0A, 36.0f, 36.0f, 4.5f),
                    new FraysSpawnPoint(0x11, 60.0f, 18.0f, 3.0f),
                    new FraysSpawnPoint(0x13, 60.0f, 60.0f, 4.0f),
                    new FraysSpawnPoint(0x1A, 84.0f, 36.0f, 3.0f),
                    new FraysSpawnPoint(0x1B, 84.0f, 60.0f, 4.0f),
                },
                Mix = new[] { new FraysMixEntry(DriftGolem, 2), new FraysMixEntry(DriftRubble, 2), new FraysMixEntry(DriftGolemViceroy, 1) },
            },
            new FraysFireDef
            {
                Id = "vc11", RoomName = "Sandbar Rise", Landblock = 0xD3DE, Fray = VissidalCoast, Outdoor = true,
                FireCell = 0x1B, FirePos = new Vector3(84.0f, 60.0f, 4.0f),
                RoomCells = Cells(),
                SpawnPoints = new[]
                {
                    new FraysSpawnPoint(0x07, 18.0f, 156.0f, 4.0f),
                    new FraysSpawnPoint(0x39, 174.0f, 18.0f, 4.0f),
                    new FraysSpawnPoint(0x01, 18.0f, 18.0f, 4.0f),
                    new FraysSpawnPoint(0x04, 18.0f, 84.0f, 4.0f),
                    new FraysSpawnPoint(0x15, 60.0f, 108.0f, 4.0f),
                    new FraysSpawnPoint(0x21, 108.0f, 18.0f, 4.0f),
                    new FraysSpawnPoint(0x2C, 132.0f, 84.0f, 4.0f),
                    new FraysSpawnPoint(0x11, 60.0f, 18.0f, 4.0f),
                    new FraysSpawnPoint(0x0A, 36.0f, 36.0f, 4.0f),
                    new FraysSpawnPoint(0x0E, 36.0f, 132.0f, 4.0f),
                    new FraysSpawnPoint(0x2A, 132.0f, 36.0f, 4.0f),
                    new FraysSpawnPoint(0x33, 156.0f, 60.0f, 4.0f),
                    new FraysSpawnPoint(0x02, 18.0f, 36.0f, 4.0f),
                    new FraysSpawnPoint(0x03, 18.0f, 60.0f, 4.0f),
                    new FraysSpawnPoint(0x05, 18.0f, 108.0f, 4.0f),
                    new FraysSpawnPoint(0x06, 18.0f, 132.0f, 4.0f),
                    new FraysSpawnPoint(0x09, 36.0f, 18.0f, 4.0f),
                    new FraysSpawnPoint(0x0B, 36.0f, 60.0f, 4.0f),
                    new FraysSpawnPoint(0x0C, 36.0f, 84.0f, 4.0f),
                    new FraysSpawnPoint(0x0D, 36.0f, 108.0f, 4.0f),
                },
                Mix = new[] { new FraysMixEntry(DriftGolem, 2), new FraysMixEntry(DriftRubble, 2), new FraysMixEntry(DriftGolemViceroy, 1) },
            },
            new FraysFireDef
            {
                Id = "vc12", RoomName = "Low Ferry", Landblock = 0xCFE1, Fray = VissidalCoast, Outdoor = true,
                FireCell = 0x13, FirePos = new Vector3(60.0f, 60.0f, 1.0f),
                RoomCells = Cells(),
                SpawnPoints = new[]
                {
                    new FraysSpawnPoint(0x08, 18.0f, 174.0f, 1.5f),
                    new FraysSpawnPoint(0x31, 156.0f, 18.0f, 0.0f),
                    new FraysSpawnPoint(0x1E, 84.0f, 132.0f, 0.0f),
                    new FraysSpawnPoint(0x01, 18.0f, 18.0f, 0.0f),
                    new FraysSpawnPoint(0x04, 18.0f, 84.0f, 0.0f),
                    new FraysSpawnPoint(0x19, 84.0f, 18.0f, 0.0f),
                    new FraysSpawnPoint(0x24, 108.0f, 84.0f, 2.5f),
                    new FraysSpawnPoint(0x0A, 36.0f, 36.0f, 0.0f),
                    new FraysSpawnPoint(0x0D, 36.0f, 108.0f, 0.0f),
                    new FraysSpawnPoint(0x0F, 36.0f, 156.0f, 0.0f),
                    new FraysSpawnPoint(0x22, 108.0f, 36.0f, 1.0f),
                    new FraysSpawnPoint(0x2B, 132.0f, 60.0f, 1.5f),
                    new FraysSpawnPoint(0x02, 18.0f, 36.0f, 0.0f),
                    new FraysSpawnPoint(0x03, 18.0f, 60.0f, 0.0f),
                    new FraysSpawnPoint(0x07, 18.0f, 156.0f, 0.0f),
                    new FraysSpawnPoint(0x09, 36.0f, 18.0f, 0.0f),
                    new FraysSpawnPoint(0x0B, 36.0f, 60.0f, 0.0f),
                    new FraysSpawnPoint(0x0C, 36.0f, 84.0f, 0.0f),
                    new FraysSpawnPoint(0x11, 60.0f, 18.0f, 0.0f),
                    new FraysSpawnPoint(0x12, 60.0f, 36.0f, 0.5f),
                },
                Mix = new[] { new FraysMixEntry(DriftGolem, 2), new FraysMixEntry(DriftRubble, 2), new FraysMixEntry(DriftGolemViceroy, 1) },
            },
            new FraysFireDef
            {
                Id = "vc13", RoomName = "West Tip", Landblock = 0xC7E3, Fray = VissidalCoast, Outdoor = true,
                FireCell = 0x24, FirePos = new Vector3(108.0f, 84.0f, 2.0f),
                RoomCells = Cells(),
                SpawnPoints = new[]
                {
                    new FraysSpawnPoint(0x38, 156.0f, 174.0f, 0.0f),
                    new FraysSpawnPoint(0x09, 36.0f, 18.0f, 0.0f),
                    new FraysSpawnPoint(0x39, 174.0f, 18.0f, 4.0f),
                    new FraysSpawnPoint(0x0E, 36.0f, 132.0f, 0.0f),
                    new FraysSpawnPoint(0x21, 108.0f, 18.0f, 2.0f),
                    new FraysSpawnPoint(0x20, 84.0f, 174.0f, 0.0f),
                    new FraysSpawnPoint(0x13, 60.0f, 60.0f, 0.0f),
                    new FraysSpawnPoint(0x2E, 132.0f, 132.0f, 1.0f),
                    new FraysSpawnPoint(0x33, 156.0f, 60.0f, 4.0f),
                    new FraysSpawnPoint(0x1E, 84.0f, 132.0f, 0.0f),
                    new FraysSpawnPoint(0x0C, 36.0f, 84.0f, 0.0f),
                    new FraysSpawnPoint(0x15, 60.0f, 108.0f, 0.0f),
                    new FraysSpawnPoint(0x17, 60.0f, 156.0f, 0.0f),
                    new FraysSpawnPoint(0x1A, 84.0f, 36.0f, 0.0f),
                    new FraysSpawnPoint(0x27, 108.0f, 156.0f, 0.0f),
                    new FraysSpawnPoint(0x2A, 132.0f, 36.0f, 4.0f),
                    new FraysSpawnPoint(0x0A, 36.0f, 36.0f, 0.0f),
                    new FraysSpawnPoint(0x0B, 36.0f, 60.0f, 0.0f),
                    new FraysSpawnPoint(0x0D, 36.0f, 108.0f, 0.0f),
                    new FraysSpawnPoint(0x11, 60.0f, 18.0f, 0.0f),
                },
                Mix = new[] { new FraysMixEntry(DriftGolem, 2), new FraysMixEntry(DriftRubble, 2), new FraysMixEntry(DriftGolemViceroy, 1) },
            },
            new FraysFireDef
            {
                Id = "vc14", RoomName = "Spray Meadow", Landblock = 0xD1DE, Fray = VissidalCoast, Outdoor = true,
                FireCell = 0x25, FirePos = new Vector3(108.0f, 108.0f, 0.0f),
                RoomCells = Cells(),
                SpawnPoints = new[]
                {
                    new FraysSpawnPoint(0x08, 18.0f, 174.0f, 0.0f),
                    new FraysSpawnPoint(0x39, 174.0f, 18.0f, 0.0f),
                    new FraysSpawnPoint(0x40, 174.0f, 174.0f, 4.0f),
                    new FraysSpawnPoint(0x20, 84.0f, 174.0f, 1.0f),
                    new FraysSpawnPoint(0x3C, 174.0f, 84.0f, 1.0f),
                    new FraysSpawnPoint(0x0E, 36.0f, 132.0f, 0.0f),
                    new FraysSpawnPoint(0x2A, 132.0f, 36.0f, 0.0f),
                    new FraysSpawnPoint(0x2F, 132.0f, 156.0f, 3.0f),
                    new FraysSpawnPoint(0x3E, 174.0f, 132.0f, 4.0f),
                    new FraysSpawnPoint(0x15, 60.0f, 108.0f, 0.0f),
                    new FraysSpawnPoint(0x17, 60.0f, 156.0f, 0.0f),
                    new FraysSpawnPoint(0x1C, 84.0f, 84.0f, 0.0f),
                    new FraysSpawnPoint(0x1E, 84.0f, 132.0f, 0.0f),
                    new FraysSpawnPoint(0x23, 108.0f, 60.0f, 0.0f),
                    new FraysSpawnPoint(0x2C, 132.0f, 84.0f, 0.0f),
                    new FraysSpawnPoint(0x33, 156.0f, 60.0f, 0.0f),
                    new FraysSpawnPoint(0x35, 156.0f, 108.0f, 1.0f),
                    new FraysSpawnPoint(0x07, 18.0f, 156.0f, 0.0f),
                    new FraysSpawnPoint(0x0F, 36.0f, 156.0f, 0.0f),
                    new FraysSpawnPoint(0x10, 36.0f, 174.0f, 0.0f),
                },
                Mix = new[] { new FraysMixEntry(DriftGolem, 2), new FraysMixEntry(DriftRubble, 2), new FraysMixEntry(DriftGolemViceroy, 1) },
            },
            new FraysFireDef
            {
                Id = "vc15", RoomName = "Pebble Cove", Landblock = 0xCAE1, Fray = VissidalCoast, Outdoor = true,
                FireCell = 0x10, FirePos = new Vector3(24.0f, 168.0f, 1.0f),   // moved west toward the Western Shore landing, about 118 m off; was 0x26 108, 132
                RoomCells = Cells(),
                SpawnPoints = new[]
                {
                    new FraysSpawnPoint(0x08, 18.0f, 174.0f, 0.0f),
                    new FraysSpawnPoint(0x3B, 174.0f, 60.0f, 0.0f),
                    new FraysSpawnPoint(0x40, 174.0f, 174.0f, 4.0f),
                    new FraysSpawnPoint(0x0D, 36.0f, 108.0f, 0.0f),
                    new FraysSpawnPoint(0x23, 108.0f, 60.0f, 0.0f),
                    new FraysSpawnPoint(0x17, 60.0f, 156.0f, 0.0f),
                    new FraysSpawnPoint(0x35, 156.0f, 108.0f, 0.0f),
                    new FraysSpawnPoint(0x28, 108.0f, 174.0f, 4.0f),
                    new FraysSpawnPoint(0x06, 18.0f, 132.0f, 0.0f),
                    new FraysSpawnPoint(0x14, 60.0f, 84.0f, 0.0f),
                    new FraysSpawnPoint(0x1D, 84.0f, 108.0f, 0.0f),
                    new FraysSpawnPoint(0x2C, 132.0f, 84.0f, 0.0f),
                    new FraysSpawnPoint(0x2F, 132.0f, 156.0f, 3.0f),
                    new FraysSpawnPoint(0x3E, 174.0f, 132.0f, 2.0f),
                    new FraysSpawnPoint(0x07, 18.0f, 156.0f, 0.0f),
                    new FraysSpawnPoint(0x0E, 36.0f, 132.0f, 0.0f),
                    new FraysSpawnPoint(0x0F, 36.0f, 156.0f, 0.0f),
                    new FraysSpawnPoint(0x10, 36.0f, 174.0f, 0.0f),
                    new FraysSpawnPoint(0x15, 60.0f, 108.0f, 0.0f),
                    new FraysSpawnPoint(0x16, 60.0f, 132.0f, 0.0f),
                },
                Mix = new[] { new FraysMixEntry(DriftGolem, 2), new FraysMixEntry(DriftRubble, 2), new FraysMixEntry(DriftGolemViceroy, 1) },
            },
            new FraysFireDef
            {
                Id = "vc16", RoomName = "Grey Shallows", Landblock = 0xCEDF, Fray = VissidalCoast, Outdoor = true,
                FireCell = 0x24, FirePos = new Vector3(108.0f, 84.0f, 0.0f),
                RoomCells = Cells(),
                SpawnPoints = new[]
                {
                    new FraysSpawnPoint(0x40, 174.0f, 174.0f, 3.5f),
                    new FraysSpawnPoint(0x18, 60.0f, 174.0f, 0.0f),
                    new FraysSpawnPoint(0x39, 174.0f, 18.0f, 5.0f),
                    new FraysSpawnPoint(0x11, 60.0f, 18.0f, 0.0f),
                    new FraysSpawnPoint(0x1E, 84.0f, 132.0f, 0.0f),
                    new FraysSpawnPoint(0x2A, 132.0f, 36.0f, 2.0f),
                    new FraysSpawnPoint(0x2F, 132.0f, 156.0f, 4.0f),
                    new FraysSpawnPoint(0x35, 156.0f, 108.0f, 4.0f),
                    new FraysSpawnPoint(0x13, 60.0f, 60.0f, 0.0f),
                    new FraysSpawnPoint(0x3B, 174.0f, 60.0f, 6.0f),
                    new FraysSpawnPoint(0x1A, 84.0f, 36.0f, 0.0f),
                    new FraysSpawnPoint(0x21, 108.0f, 18.0f, 0.0f),
                    new FraysSpawnPoint(0x28, 108.0f, 174.0f, 2.0f),
                    new FraysSpawnPoint(0x12, 60.0f, 36.0f, 0.0f),
                    new FraysSpawnPoint(0x14, 60.0f, 84.0f, 0.0f),
                    new FraysSpawnPoint(0x19, 84.0f, 18.0f, 0.0f),
                    new FraysSpawnPoint(0x1B, 84.0f, 60.0f, 0.0f),
                    new FraysSpawnPoint(0x1C, 84.0f, 84.0f, 0.0f),
                    new FraysSpawnPoint(0x1D, 84.0f, 108.0f, 0.0f),
                    new FraysSpawnPoint(0x1F, 84.0f, 156.0f, 0.0f),
                },
                Mix = new[] { new FraysMixEntry(DriftGolem, 2), new FraysMixEntry(DriftRubble, 2), new FraysMixEntry(DriftGolemViceroy, 1) },
            },
            new FraysFireDef
            {
                Id = "vc17", RoomName = "Ash Beach", Landblock = 0xD4DD, Fray = VissidalCoast, Outdoor = true,
                FireCell = 0x14, FirePos = new Vector3(60.0f, 84.0f, 4.0f),
                RoomCells = Cells(),
                SpawnPoints = new[]
                {
                    new FraysSpawnPoint(0x08, 18.0f, 174.0f, 4.0f),
                    new FraysSpawnPoint(0x29, 132.0f, 18.0f, 4.0f),
                    new FraysSpawnPoint(0x01, 18.0f, 18.0f, 1.0f),
                    new FraysSpawnPoint(0x27, 108.0f, 156.0f, 4.0f),
                    new FraysSpawnPoint(0x05, 18.0f, 108.0f, 4.0f),
                    new FraysSpawnPoint(0x1A, 84.0f, 36.0f, 4.0f),
                    new FraysSpawnPoint(0x03, 18.0f, 60.0f, 4.0f),
                    new FraysSpawnPoint(0x16, 60.0f, 132.0f, 4.0f),
                    new FraysSpawnPoint(0x18, 60.0f, 174.0f, 4.0f),
                    new FraysSpawnPoint(0x24, 108.0f, 84.0f, 4.0f),
                    new FraysSpawnPoint(0x0A, 36.0f, 36.0f, 4.0f),
                    new FraysSpawnPoint(0x0F, 36.0f, 156.0f, 4.0f),
                    new FraysSpawnPoint(0x11, 60.0f, 18.0f, 4.0f),
                    new FraysSpawnPoint(0x1D, 84.0f, 108.0f, 4.0f),
                    new FraysSpawnPoint(0x2E, 132.0f, 132.0f, 5.0f),
                    new FraysSpawnPoint(0x02, 18.0f, 36.0f, 3.0f),
                    new FraysSpawnPoint(0x04, 18.0f, 84.0f, 4.0f),
                    new FraysSpawnPoint(0x06, 18.0f, 132.0f, 4.0f),
                    new FraysSpawnPoint(0x07, 18.0f, 156.0f, 4.0f),
                    new FraysSpawnPoint(0x09, 36.0f, 18.0f, 3.0f),
                },
                Mix = new[] { new FraysMixEntry(DriftGolem, 2), new FraysMixEntry(DriftRubble, 2), new FraysMixEntry(DriftGolemViceroy, 1) },
            },
            new FraysFireDef
            {
                Id = "vc18", RoomName = "Lantern Shore", Landblock = 0xCBE0, Fray = VissidalCoast, Outdoor = true,
                FireCell = 0x2D, FirePos = new Vector3(132.0f, 108.0f, 0.0f),
                RoomCells = Cells(),
                SpawnPoints = new[]
                {
                    new FraysSpawnPoint(0x10, 36.0f, 174.0f, 0.0f),
                    new FraysSpawnPoint(0x39, 174.0f, 18.0f, 0.0f),
                    new FraysSpawnPoint(0x40, 174.0f, 174.0f, 4.0f),
                    new FraysSpawnPoint(0x22, 108.0f, 36.0f, 0.0f),
                    new FraysSpawnPoint(0x28, 108.0f, 174.0f, 0.0f),
                    new FraysSpawnPoint(0x16, 60.0f, 132.0f, 0.0f),
                    new FraysSpawnPoint(0x1C, 84.0f, 84.0f, 0.0f),
                    new FraysSpawnPoint(0x33, 156.0f, 60.0f, 0.0f),
                    new FraysSpawnPoint(0x3D, 174.0f, 108.0f, 3.0f),
                    new FraysSpawnPoint(0x1F, 84.0f, 156.0f, 0.0f),
                    new FraysSpawnPoint(0x26, 108.0f, 132.0f, 0.0f),
                    new FraysSpawnPoint(0x29, 132.0f, 18.0f, 0.0f),
                    new FraysSpawnPoint(0x2F, 132.0f, 156.0f, 1.0f),
                    new FraysSpawnPoint(0x36, 156.0f, 132.0f, 2.0f),
                    new FraysSpawnPoint(0x17, 60.0f, 156.0f, 0.0f),
                    new FraysSpawnPoint(0x18, 60.0f, 174.0f, 0.0f),
                    new FraysSpawnPoint(0x1D, 84.0f, 108.0f, 0.0f),
                    new FraysSpawnPoint(0x1E, 84.0f, 132.0f, 0.0f),
                    new FraysSpawnPoint(0x20, 84.0f, 174.0f, 0.0f),
                    new FraysSpawnPoint(0x23, 108.0f, 60.0f, 0.0f),
                },
                Mix = new[] { new FraysMixEntry(DriftGolem, 2), new FraysMixEntry(DriftRubble, 2), new FraysMixEntry(DriftGolemViceroy, 1) },
            },
            new FraysFireDef
            {
                Id = "vh1", RoomName = "Cinder Bowl", Landblock = 0xD3DF, Fray = VissidalHeights, Outdoor = true,
                FireCell = 0x1C, FirePos = new Vector3(84.0f, 84.0f, 20.0f),
                RoomCells = Cells(),
                SpawnPoints = new[]
                {
                    new FraysSpawnPoint(0x40, 174.0f, 174.0f, 20.0f),
                    new FraysSpawnPoint(0x08, 18.0f, 174.0f, 20.0f),
                    new FraysSpawnPoint(0x39, 174.0f, 18.0f, 20.0f),
                    new FraysSpawnPoint(0x01, 18.0f, 18.0f, 20.0f),
                    new FraysSpawnPoint(0x27, 108.0f, 156.0f, 20.0f),
                    new FraysSpawnPoint(0x04, 18.0f, 84.0f, 20.0f),
                    new FraysSpawnPoint(0x19, 84.0f, 18.0f, 20.0f),
                    new FraysSpawnPoint(0x34, 156.0f, 84.0f, 20.0f),
                    new FraysSpawnPoint(0x0E, 36.0f, 132.0f, 20.0f),
                    new FraysSpawnPoint(0x2A, 132.0f, 36.0f, 20.0f),
                    new FraysSpawnPoint(0x18, 60.0f, 174.0f, 20.0f),
                    new FraysSpawnPoint(0x36, 156.0f, 132.0f, 20.0f),
                    new FraysSpawnPoint(0x0A, 36.0f, 36.0f, 20.0f),
                    new FraysSpawnPoint(0x13, 60.0f, 60.0f, 20.0f),
                    new FraysSpawnPoint(0x15, 60.0f, 108.0f, 20.0f),
                    new FraysSpawnPoint(0x1E, 84.0f, 132.0f, 20.0f),
                    new FraysSpawnPoint(0x23, 108.0f, 60.0f, 20.0f),
                    new FraysSpawnPoint(0x25, 108.0f, 108.0f, 20.0f),
                    new FraysSpawnPoint(0x30, 132.0f, 174.0f, 20.0f),
                    new FraysSpawnPoint(0x3B, 174.0f, 60.0f, 20.0f),
                },
                Mix = new[] { new FraysMixEntry(DriftGolem, 2), new FraysMixEntry(DriftRubble, 2), new FraysMixEntry(DriftGolemViceroy, 1) },
            },
            new FraysFireDef
            {
                Id = "vh2", RoomName = "High Kiln", Landblock = 0xD5E0, Fray = VissidalHeights, Outdoor = true,
                FireCell = 0x1C, FirePos = new Vector3(84.0f, 84.0f, 41.0f),
                RoomCells = Cells(),
                SpawnPoints = new[]
                {
                    new FraysSpawnPoint(0x40, 174.0f, 174.0f, 55.5f),
                    new FraysSpawnPoint(0x08, 18.0f, 174.0f, 50.5f),
                    new FraysSpawnPoint(0x39, 174.0f, 18.0f, 40.0f),
                    new FraysSpawnPoint(0x01, 18.0f, 18.0f, 36.5f),
                    new FraysSpawnPoint(0x27, 108.0f, 156.0f, 46.5f),
                    new FraysSpawnPoint(0x04, 18.0f, 84.0f, 41.5f),
                    new FraysSpawnPoint(0x19, 84.0f, 18.0f, 40.0f),
                    new FraysSpawnPoint(0x34, 156.0f, 84.0f, 41.0f),
                    new FraysSpawnPoint(0x0E, 36.0f, 132.0f, 48.5f),
                    new FraysSpawnPoint(0x2A, 132.0f, 36.0f, 40.0f),
                    new FraysSpawnPoint(0x18, 60.0f, 174.0f, 50.5f),
                    new FraysSpawnPoint(0x36, 156.0f, 132.0f, 47.0f),
                    new FraysSpawnPoint(0x0A, 36.0f, 36.0f, 39.5f),
                    new FraysSpawnPoint(0x13, 60.0f, 60.0f, 40.5f),
                    new FraysSpawnPoint(0x15, 60.0f, 108.0f, 45.0f),
                    new FraysSpawnPoint(0x1E, 84.0f, 132.0f, 46.0f),
                    new FraysSpawnPoint(0x23, 108.0f, 60.0f, 40.0f),
                    new FraysSpawnPoint(0x25, 108.0f, 108.0f, 42.0f),
                    new FraysSpawnPoint(0x30, 132.0f, 174.0f, 50.0f),
                    new FraysSpawnPoint(0x3B, 174.0f, 60.0f, 40.0f),
                },
                Mix = new[] { new FraysMixEntry(DriftGolem, 2), new FraysMixEntry(DriftRubble, 2), new FraysMixEntry(DriftGolemViceroy, 1) },
            },
            new FraysFireDef
            {
                Id = "vh3", RoomName = "Ridge Crown", Landblock = 0xD6DD, Fray = VissidalHeights, Outdoor = true,
                FireCell = 0x1C, FirePos = new Vector3(84.0f, 84.0f, 55.5f),
                RoomCells = Cells(),
                SpawnPoints = new[]
                {
                    new FraysSpawnPoint(0x40, 174.0f, 174.0f, 61.0f),
                    new FraysSpawnPoint(0x08, 18.0f, 174.0f, 43.5f),
                    new FraysSpawnPoint(0x39, 174.0f, 18.0f, 64.5f),
                    new FraysSpawnPoint(0x01, 18.0f, 18.0f, 56.0f),
                    new FraysSpawnPoint(0x27, 108.0f, 156.0f, 53.0f),
                    new FraysSpawnPoint(0x04, 18.0f, 84.0f, 55.0f),
                    new FraysSpawnPoint(0x19, 84.0f, 18.0f, 54.5f),
                    new FraysSpawnPoint(0x34, 156.0f, 84.0f, 58.0f),
                    new FraysSpawnPoint(0x0E, 36.0f, 132.0f, 51.5f),
                    new FraysSpawnPoint(0x2A, 132.0f, 36.0f, 57.5f),
                    new FraysSpawnPoint(0x18, 60.0f, 174.0f, 46.5f),
                    new FraysSpawnPoint(0x36, 156.0f, 132.0f, 58.0f),
                    new FraysSpawnPoint(0x0A, 36.0f, 36.0f, 56.0f),
                    new FraysSpawnPoint(0x13, 60.0f, 60.0f, 55.5f),
                    new FraysSpawnPoint(0x15, 60.0f, 108.0f, 55.5f),
                    new FraysSpawnPoint(0x1E, 84.0f, 132.0f, 54.5f),
                    new FraysSpawnPoint(0x23, 108.0f, 60.0f, 55.0f),
                    new FraysSpawnPoint(0x25, 108.0f, 108.0f, 56.0f),
                    new FraysSpawnPoint(0x30, 132.0f, 174.0f, 55.0f),
                    new FraysSpawnPoint(0x3B, 174.0f, 60.0f, 60.0f),
                },
                Mix = new[] { new FraysMixEntry(DriftGolem, 2), new FraysMixEntry(DriftRubble, 2), new FraysMixEntry(DriftGolemViceroy, 1) },
            },
            new FraysFireDef
            {
                Id = "vh4", RoomName = "Smoke Basin", Landblock = 0xD3E0, Fray = VissidalHeights, Outdoor = true,
                FireCell = 0x24, FirePos = new Vector3(108.0f, 84.0f, 20.0f),
                RoomCells = Cells(),
                SpawnPoints = new[]
                {
                    new FraysSpawnPoint(0x01, 18.0f, 18.0f, 20.0f),
                    new FraysSpawnPoint(0x07, 18.0f, 156.0f, 24.0f),
                    new FraysSpawnPoint(0x40, 174.0f, 174.0f, 29.0f),
                    new FraysSpawnPoint(0x39, 174.0f, 18.0f, 20.0f),
                    new FraysSpawnPoint(0x20, 84.0f, 174.0f, 24.0f),
                    new FraysSpawnPoint(0x04, 18.0f, 84.0f, 20.0f),
                    new FraysSpawnPoint(0x19, 84.0f, 18.0f, 20.0f),
                    new FraysSpawnPoint(0x3C, 174.0f, 84.0f, 20.0f),
                    new FraysSpawnPoint(0x13, 60.0f, 60.0f, 20.0f),
                    new FraysSpawnPoint(0x16, 60.0f, 132.0f, 20.0f),
                    new FraysSpawnPoint(0x2A, 132.0f, 36.0f, 20.0f),
                    new FraysSpawnPoint(0x2E, 132.0f, 132.0f, 20.5f),
                    new FraysSpawnPoint(0x30, 132.0f, 174.0f, 25.0f),
                    new FraysSpawnPoint(0x3E, 174.0f, 132.0f, 22.0f),
                    new FraysSpawnPoint(0x0A, 36.0f, 36.0f, 20.0f),
                    new FraysSpawnPoint(0x0D, 36.0f, 108.0f, 20.0f),
                    new FraysSpawnPoint(0x10, 36.0f, 174.0f, 24.0f),
                    new FraysSpawnPoint(0x1D, 84.0f, 108.0f, 20.0f),
                    new FraysSpawnPoint(0x27, 108.0f, 156.0f, 21.0f),
                    new FraysSpawnPoint(0x33, 156.0f, 60.0f, 20.0f),
                },
                Mix = new[] { new FraysMixEntry(DriftGolem, 2), new FraysMixEntry(DriftRubble, 2), new FraysMixEntry(DriftGolemViceroy, 1) },
            },
            new FraysFireDef
            {
                Id = "vh5", RoomName = "Ember Terrace", Landblock = 0xD6DC, Fray = VissidalHeights, Outdoor = true,
                FireCell = 0x1C, FirePos = new Vector3(84.0f, 84.0f, 55.5f),
                RoomCells = Cells(),
                SpawnPoints = new[]
                {
                    new FraysSpawnPoint(0x40, 174.0f, 174.0f, 67.0f),
                    new FraysSpawnPoint(0x08, 18.0f, 174.0f, 55.5f),
                    new FraysSpawnPoint(0x39, 174.0f, 18.0f, 52.5f),
                    new FraysSpawnPoint(0x01, 18.0f, 18.0f, 42.0f),
                    new FraysSpawnPoint(0x27, 108.0f, 156.0f, 57.0f),
                    new FraysSpawnPoint(0x04, 18.0f, 84.0f, 48.5f),
                    new FraysSpawnPoint(0x19, 84.0f, 18.0f, 47.0f),
                    new FraysSpawnPoint(0x34, 156.0f, 84.0f, 60.0f),
                    new FraysSpawnPoint(0x0E, 36.0f, 132.0f, 55.5f),
                    new FraysSpawnPoint(0x2A, 132.0f, 36.0f, 53.0f),
                    new FraysSpawnPoint(0x18, 60.0f, 174.0f, 55.5f),
                    new FraysSpawnPoint(0x3E, 174.0f, 132.0f, 69.5f),
                    new FraysSpawnPoint(0x0A, 36.0f, 36.0f, 45.5f),
                    new FraysSpawnPoint(0x13, 60.0f, 60.0f, 51.5f),
                    new FraysSpawnPoint(0x15, 60.0f, 108.0f, 55.5f),
                    new FraysSpawnPoint(0x1E, 84.0f, 132.0f, 56.0f),
                    new FraysSpawnPoint(0x23, 108.0f, 60.0f, 55.0f),
                    new FraysSpawnPoint(0x25, 108.0f, 108.0f, 57.0f),
                    new FraysSpawnPoint(0x2E, 132.0f, 132.0f, 60.0f),
                    new FraysSpawnPoint(0x30, 132.0f, 174.0f, 60.5f),
                },
                Mix = new[] { new FraysMixEntry(DriftGolem, 2), new FraysMixEntry(DriftRubble, 2), new FraysMixEntry(DriftGolemViceroy, 1) },
            },
            new FraysFireDef
            {
                Id = "vh6", RoomName = "Ashen Rise", Landblock = 0xD5DD, Fray = VissidalHeights, Outdoor = true,
                FireCell = 0x24, FirePos = new Vector3(108.0f, 84.0f, 42.0f),
                RoomCells = Cells(),
                SpawnPoints = new[]
                {
                    new FraysSpawnPoint(0x08, 18.0f, 174.0f, 38.5f),
                    new FraysSpawnPoint(0x01, 18.0f, 18.0f, 39.5f),
                    new FraysSpawnPoint(0x40, 174.0f, 174.0f, 42.0f),
                    new FraysSpawnPoint(0x39, 174.0f, 18.0f, 54.0f),
                    new FraysSpawnPoint(0x0D, 36.0f, 108.0f, 39.5f),
                    new FraysSpawnPoint(0x19, 84.0f, 18.0f, 41.0f),
                    new FraysSpawnPoint(0x20, 84.0f, 174.0f, 40.0f),
                    new FraysSpawnPoint(0x3C, 174.0f, 84.0f, 52.5f),
                    new FraysSpawnPoint(0x13, 60.0f, 60.0f, 40.0f),
                    new FraysSpawnPoint(0x2A, 132.0f, 36.0f, 46.0f),
                    new FraysSpawnPoint(0x2E, 132.0f, 132.0f, 43.0f),
                    new FraysSpawnPoint(0x1E, 84.0f, 132.0f, 40.0f),
                    new FraysSpawnPoint(0x30, 132.0f, 174.0f, 40.5f),
                    new FraysSpawnPoint(0x3E, 174.0f, 132.0f, 46.5f),
                    new FraysSpawnPoint(0x0A, 36.0f, 36.0f, 39.5f),
                    new FraysSpawnPoint(0x0F, 36.0f, 156.0f, 40.0f),
                    new FraysSpawnPoint(0x27, 108.0f, 156.0f, 40.5f),
                    new FraysSpawnPoint(0x33, 156.0f, 60.0f, 50.0f),
                    new FraysSpawnPoint(0x35, 156.0f, 108.0f, 46.5f),
                    new FraysSpawnPoint(0x37, 156.0f, 156.0f, 43.0f),
                },
                Mix = new[] { new FraysMixEntry(DriftGolem, 2), new FraysMixEntry(DriftRubble, 2), new FraysMixEntry(DriftGolemViceroy, 1) },
            },
            new FraysFireDef
            {
                Id = "vh7", RoomName = "Basin Ford", Landblock = 0xD4DF, Fray = VissidalHeights, Outdoor = true,
                FireCell = 0x1C, FirePos = new Vector3(84.0f, 84.0f, 20.0f),
                RoomCells = Cells(),
                SpawnPoints = new[]
                {
                    new FraysSpawnPoint(0x08, 18.0f, 174.0f, 20.0f),
                    new FraysSpawnPoint(0x39, 174.0f, 18.0f, 20.0f),
                    new FraysSpawnPoint(0x30, 132.0f, 174.0f, 20.0f),
                    new FraysSpawnPoint(0x01, 18.0f, 18.0f, 20.0f),
                    new FraysSpawnPoint(0x04, 18.0f, 84.0f, 20.0f),
                    new FraysSpawnPoint(0x19, 84.0f, 18.0f, 20.0f),
                    new FraysSpawnPoint(0x34, 156.0f, 84.0f, 20.0f),
                    new FraysSpawnPoint(0x0E, 36.0f, 132.0f, 20.0f),
                    new FraysSpawnPoint(0x1F, 84.0f, 156.0f, 20.0f),
                    new FraysSpawnPoint(0x2A, 132.0f, 36.0f, 20.0f),
                    new FraysSpawnPoint(0x2E, 132.0f, 132.0f, 20.0f),
                    new FraysSpawnPoint(0x0A, 36.0f, 36.0f, 20.0f),
                    new FraysSpawnPoint(0x13, 60.0f, 60.0f, 20.0f),
                    new FraysSpawnPoint(0x15, 60.0f, 108.0f, 20.0f),
                    new FraysSpawnPoint(0x18, 60.0f, 174.0f, 20.0f),
                    new FraysSpawnPoint(0x23, 108.0f, 60.0f, 20.0f),
                    new FraysSpawnPoint(0x25, 108.0f, 108.0f, 20.0f),
                    new FraysSpawnPoint(0x37, 156.0f, 156.0f, 20.5f),
                    new FraysSpawnPoint(0x3B, 174.0f, 60.0f, 20.0f),
                    new FraysSpawnPoint(0x02, 18.0f, 36.0f, 20.0f),
                },
                Mix = new[] { new FraysMixEntry(DriftGolem, 2), new FraysMixEntry(DriftRubble, 2), new FraysMixEntry(DriftGolemViceroy, 1) },
            },
            new FraysFireDef
            {
                Id = "vh8", RoomName = "North Ledge", Landblock = 0xCDE3, Fray = VissidalHeights, Outdoor = true,
                FireCell = 0x1D, FirePos = new Vector3(84.0f, 108.0f, 43.5f),
                RoomCells = Cells(),
                SpawnPoints = new[]
                {
                    new FraysSpawnPoint(0x01, 18.0f, 18.0f, 40.5f),
                    new FraysSpawnPoint(0x3A, 174.0f, 36.0f, 40.0f),
                    new FraysSpawnPoint(0x40, 174.0f, 174.0f, 53.0f),
                    new FraysSpawnPoint(0x07, 18.0f, 156.0f, 53.5f),
                    new FraysSpawnPoint(0x04, 18.0f, 84.0f, 46.0f),
                    new FraysSpawnPoint(0x1A, 84.0f, 36.0f, 40.0f),
                    new FraysSpawnPoint(0x20, 84.0f, 174.0f, 54.0f),
                    new FraysSpawnPoint(0x35, 156.0f, 108.0f, 41.0f),
                    new FraysSpawnPoint(0x2B, 132.0f, 60.0f, 40.0f),
                    new FraysSpawnPoint(0x2F, 132.0f, 156.0f, 49.0f),
                    new FraysSpawnPoint(0x0A, 36.0f, 36.0f, 40.5f),
                    new FraysSpawnPoint(0x0E, 36.0f, 132.0f, 52.0f),
                    new FraysSpawnPoint(0x10, 36.0f, 174.0f, 53.5f),
                    new FraysSpawnPoint(0x11, 60.0f, 18.0f, 38.0f),
                    new FraysSpawnPoint(0x13, 60.0f, 60.0f, 40.5f),
                    new FraysSpawnPoint(0x17, 60.0f, 156.0f, 53.5f),
                    new FraysSpawnPoint(0x24, 108.0f, 84.0f, 40.5f),
                    new FraysSpawnPoint(0x26, 108.0f, 132.0f, 45.0f),
                    new FraysSpawnPoint(0x3C, 174.0f, 84.0f, 40.0f),
                    new FraysSpawnPoint(0x3E, 174.0f, 132.0f, 44.0f),
                },
                Mix = new[] { new FraysMixEntry(DriftGolem, 2), new FraysMixEntry(DriftRubble, 2), new FraysMixEntry(DriftGolemViceroy, 1) },
            },
            new FraysFireDef
            {
                Id = "vh9", RoomName = "Hearth Vale", Landblock = 0xD0E1, Fray = VissidalHeights, Outdoor = true,
                FireCell = 0x1C, FirePos = new Vector3(84.0f, 84.0f, 20.0f),
                RoomCells = Cells(),
                SpawnPoints = new[]
                {
                    new FraysSpawnPoint(0x08, 18.0f, 174.0f, 20.0f),
                    new FraysSpawnPoint(0x39, 174.0f, 18.0f, 20.5f),
                    new FraysSpawnPoint(0x01, 18.0f, 18.0f, 20.0f),
                    new FraysSpawnPoint(0x04, 18.0f, 84.0f, 20.0f),
                    new FraysSpawnPoint(0x19, 84.0f, 18.0f, 20.0f),
                    new FraysSpawnPoint(0x1F, 84.0f, 156.0f, 21.5f),
                    new FraysSpawnPoint(0x34, 156.0f, 84.0f, 21.0f),
                    new FraysSpawnPoint(0x0E, 36.0f, 132.0f, 20.0f),
                    new FraysSpawnPoint(0x2A, 132.0f, 36.0f, 20.0f),
                    new FraysSpawnPoint(0x0A, 36.0f, 36.0f, 20.0f),
                    new FraysSpawnPoint(0x13, 60.0f, 60.0f, 20.0f),
                    new FraysSpawnPoint(0x15, 60.0f, 108.0f, 20.0f),
                    new FraysSpawnPoint(0x18, 60.0f, 174.0f, 21.5f),
                    new FraysSpawnPoint(0x23, 108.0f, 60.0f, 20.0f),
                    new FraysSpawnPoint(0x25, 108.0f, 108.0f, 20.5f),
                    new FraysSpawnPoint(0x3B, 174.0f, 60.0f, 22.0f),
                    new FraysSpawnPoint(0x02, 18.0f, 36.0f, 20.0f),
                    new FraysSpawnPoint(0x03, 18.0f, 60.0f, 20.0f),
                    new FraysSpawnPoint(0x05, 18.0f, 108.0f, 20.0f),
                    new FraysSpawnPoint(0x06, 18.0f, 132.0f, 20.0f),
                },
                Mix = new[] { new FraysMixEntry(DriftGolem, 2), new FraysMixEntry(DriftRubble, 2), new FraysMixEntry(DriftGolemViceroy, 1) },
            },
            new FraysFireDef
            {
                Id = "vh10", RoomName = "Coal Ridge", Landblock = 0xD6DE, Fray = VissidalHeights, Outdoor = true,
                FireCell = 0x1C, FirePos = new Vector3(84.0f, 84.0f, 43.0f),
                RoomCells = Cells(),
                SpawnPoints = new[]
                {
                    new FraysSpawnPoint(0x08, 18.0f, 174.0f, 40.0f),
                    new FraysSpawnPoint(0x39, 174.0f, 18.0f, 61.5f),
                    new FraysSpawnPoint(0x30, 132.0f, 174.0f, 41.5f),
                    new FraysSpawnPoint(0x01, 18.0f, 18.0f, 41.0f),
                    new FraysSpawnPoint(0x35, 156.0f, 108.0f, 47.5f),
                    new FraysSpawnPoint(0x04, 18.0f, 84.0f, 40.0f),
                    new FraysSpawnPoint(0x19, 84.0f, 18.0f, 47.0f),
                    new FraysSpawnPoint(0x0E, 36.0f, 132.0f, 40.0f),
                    new FraysSpawnPoint(0x1F, 84.0f, 156.0f, 40.0f),
                    new FraysSpawnPoint(0x29, 132.0f, 18.0f, 55.0f),
                    new FraysSpawnPoint(0x0A, 36.0f, 36.0f, 40.5f),
                    new FraysSpawnPoint(0x13, 60.0f, 60.0f, 41.0f),
                    new FraysSpawnPoint(0x15, 60.0f, 108.0f, 40.5f),
                    new FraysSpawnPoint(0x18, 60.0f, 174.0f, 40.0f),
                    new FraysSpawnPoint(0x22, 108.0f, 36.0f, 49.0f),
                    new FraysSpawnPoint(0x25, 108.0f, 108.0f, 43.0f),
                    new FraysSpawnPoint(0x2C, 132.0f, 84.0f, 47.5f),
                    new FraysSpawnPoint(0x2E, 132.0f, 132.0f, 42.5f),
                    new FraysSpawnPoint(0x32, 156.0f, 36.0f, 58.0f),
                    new FraysSpawnPoint(0x37, 156.0f, 156.0f, 47.0f),
                },
                Mix = new[] { new FraysMixEntry(DriftGolem, 2), new FraysMixEntry(DriftRubble, 2), new FraysMixEntry(DriftGolemViceroy, 1) },
            },
            new FraysFireDef
            {
                Id = "vh11", RoomName = "Hidden Bowl", Landblock = 0xD0E0, Fray = VissidalHeights, Outdoor = true,
                FireCell = 0x26, FirePos = new Vector3(108.0f, 132.0f, 20.0f),
                RoomCells = Cells(),
                SpawnPoints = new[]
                {
                    new FraysSpawnPoint(0x08, 18.0f, 174.0f, 20.0f),
                    new FraysSpawnPoint(0x3B, 174.0f, 60.0f, 19.5f),
                    new FraysSpawnPoint(0x40, 174.0f, 174.0f, 20.0f),
                    new FraysSpawnPoint(0x0D, 36.0f, 108.0f, 19.5f),
                    new FraysSpawnPoint(0x17, 60.0f, 156.0f, 20.0f),
                    new FraysSpawnPoint(0x35, 156.0f, 108.0f, 20.0f),
                    new FraysSpawnPoint(0x28, 108.0f, 174.0f, 20.0f),
                    new FraysSpawnPoint(0x06, 18.0f, 132.0f, 18.0f),
                    new FraysSpawnPoint(0x1D, 84.0f, 108.0f, 20.0f),
                    new FraysSpawnPoint(0x2F, 132.0f, 156.0f, 20.0f),
                    new FraysSpawnPoint(0x3E, 174.0f, 132.0f, 20.0f),
                    new FraysSpawnPoint(0x05, 18.0f, 108.0f, 17.0f),
                    new FraysSpawnPoint(0x07, 18.0f, 156.0f, 19.0f),
                    new FraysSpawnPoint(0x0E, 36.0f, 132.0f, 20.0f),
                    new FraysSpawnPoint(0x0F, 36.0f, 156.0f, 20.0f),
                    new FraysSpawnPoint(0x10, 36.0f, 174.0f, 20.0f),
                    new FraysSpawnPoint(0x15, 60.0f, 108.0f, 20.0f),
                    new FraysSpawnPoint(0x16, 60.0f, 132.0f, 20.0f),
                    new FraysSpawnPoint(0x18, 60.0f, 174.0f, 20.0f),
                    new FraysSpawnPoint(0x1E, 84.0f, 132.0f, 20.0f),
                },
                Mix = new[] { new FraysMixEntry(DriftGolem, 2), new FraysMixEntry(DriftRubble, 2), new FraysMixEntry(DriftGolemViceroy, 1) },
            },
            new FraysFireDef
            {
                Id = "vh12", RoomName = "Sunset Mesa", Landblock = 0xD4E1, Fray = VissidalHeights, Outdoor = true,
                FireCell = 0x1C, FirePos = new Vector3(84.0f, 84.0f, 40.5f),
                RoomCells = Cells(),
                SpawnPoints = new[]
                {
                    new FraysSpawnPoint(0x08, 18.0f, 174.0f, 54.0f),
                    new FraysSpawnPoint(0x39, 174.0f, 18.0f, 46.5f),
                    new FraysSpawnPoint(0x3F, 174.0f, 156.0f, 53.0f),
                    new FraysSpawnPoint(0x02, 18.0f, 36.0f, 40.0f),
                    new FraysSpawnPoint(0x28, 108.0f, 174.0f, 59.0f),
                    new FraysSpawnPoint(0x05, 18.0f, 108.0f, 44.0f),
                    new FraysSpawnPoint(0x19, 84.0f, 18.0f, 40.0f),
                    new FraysSpawnPoint(0x34, 156.0f, 84.0f, 42.0f),
                    new FraysSpawnPoint(0x16, 60.0f, 132.0f, 46.5f),
                    new FraysSpawnPoint(0x2A, 132.0f, 36.0f, 40.5f),
                    new FraysSpawnPoint(0x2E, 132.0f, 132.0f, 47.0f),
                    new FraysSpawnPoint(0x18, 60.0f, 174.0f, 58.0f),
                    new FraysSpawnPoint(0x09, 36.0f, 18.0f, 37.5f),
                    new FraysSpawnPoint(0x0B, 36.0f, 60.0f, 40.0f),
                    new FraysSpawnPoint(0x12, 60.0f, 36.0f, 40.0f),
                    new FraysSpawnPoint(0x23, 108.0f, 60.0f, 40.0f),
                    new FraysSpawnPoint(0x25, 108.0f, 108.0f, 42.5f),
                    new FraysSpawnPoint(0x3B, 174.0f, 60.0f, 44.5f),
                    new FraysSpawnPoint(0x3D, 174.0f, 108.0f, 47.0f),
                    new FraysSpawnPoint(0x03, 18.0f, 60.0f, 40.0f),
                },
                Mix = new[] { new FraysMixEntry(DriftGolem, 2), new FraysMixEntry(DriftRubble, 2), new FraysMixEntry(DriftGolemViceroy, 1) },
            },
            new FraysFireDef
            {
                Id = "vh13", RoomName = "Burnt Dell", Landblock = 0xCDE2, Fray = VissidalHeights, Outdoor = true,
                FireCell = 0x25, FirePos = new Vector3(108.0f, 108.0f, 20.0f),
                RoomCells = Cells(),
                SpawnPoints = new[]
                {
                    new FraysSpawnPoint(0x01, 18.0f, 18.0f, 18.5f),
                    new FraysSpawnPoint(0x40, 174.0f, 174.0f, 22.0f),
                    new FraysSpawnPoint(0x06, 18.0f, 132.0f, 20.5f),
                    new FraysSpawnPoint(0x21, 108.0f, 18.0f, 17.5f),
                    new FraysSpawnPoint(0x3B, 174.0f, 60.0f, 20.0f),
                    new FraysSpawnPoint(0x28, 108.0f, 174.0f, 23.0f),
                    new FraysSpawnPoint(0x13, 60.0f, 60.0f, 20.0f),
                    new FraysSpawnPoint(0x17, 60.0f, 156.0f, 22.5f),
                    new FraysSpawnPoint(0x36, 156.0f, 132.0f, 20.0f),
                    new FraysSpawnPoint(0x03, 18.0f, 60.0f, 20.0f),
                    new FraysSpawnPoint(0x15, 60.0f, 108.0f, 20.0f),
                    new FraysSpawnPoint(0x23, 108.0f, 60.0f, 20.0f),
                    new FraysSpawnPoint(0x0A, 36.0f, 36.0f, 20.0f),
                    new FraysSpawnPoint(0x0C, 36.0f, 84.0f, 20.0f),
                    new FraysSpawnPoint(0x1A, 84.0f, 36.0f, 20.0f),
                    new FraysSpawnPoint(0x1C, 84.0f, 84.0f, 20.0f),
                    new FraysSpawnPoint(0x1E, 84.0f, 132.0f, 20.0f),
                    new FraysSpawnPoint(0x2A, 132.0f, 36.0f, 20.0f),
                    new FraysSpawnPoint(0x2C, 132.0f, 84.0f, 20.0f),
                    new FraysSpawnPoint(0x2F, 132.0f, 156.0f, 20.0f),
                },
                Mix = new[] { new FraysMixEntry(DriftGolem, 2), new FraysMixEntry(DriftRubble, 2), new FraysMixEntry(DriftGolemViceroy, 1) },
            },
            new FraysFireDef
            {
                Id = "vh14", RoomName = "Glow Basin", Landblock = 0xD4DE, Fray = VissidalHeights, Outdoor = true,
                FireCell = 0x25, FirePos = new Vector3(108.0f, 108.0f, 20.0f),
                RoomCells = Cells(),
                SpawnPoints = new[]
                {
                    new FraysSpawnPoint(0x08, 18.0f, 174.0f, 20.0f),
                    new FraysSpawnPoint(0x40, 174.0f, 174.0f, 20.0f),
                    new FraysSpawnPoint(0x29, 132.0f, 18.0f, 19.5f),
                    new FraysSpawnPoint(0x04, 18.0f, 84.0f, 19.5f),
                    new FraysSpawnPoint(0x3C, 174.0f, 84.0f, 22.0f),
                    new FraysSpawnPoint(0x20, 84.0f, 174.0f, 20.0f),
                    new FraysSpawnPoint(0x0E, 36.0f, 132.0f, 20.0f),
                    new FraysSpawnPoint(0x13, 60.0f, 60.0f, 19.5f),
                    new FraysSpawnPoint(0x2F, 132.0f, 156.0f, 20.0f),
                    new FraysSpawnPoint(0x23, 108.0f, 60.0f, 20.0f),
                    new FraysSpawnPoint(0x3E, 174.0f, 132.0f, 20.0f),
                    new FraysSpawnPoint(0x15, 60.0f, 108.0f, 20.0f),
                    new FraysSpawnPoint(0x17, 60.0f, 156.0f, 20.0f),
                    new FraysSpawnPoint(0x1A, 84.0f, 36.0f, 18.5f),
                    new FraysSpawnPoint(0x1C, 84.0f, 84.0f, 20.0f),
                    new FraysSpawnPoint(0x1E, 84.0f, 132.0f, 20.0f),
                    new FraysSpawnPoint(0x2C, 132.0f, 84.0f, 20.0f),
                    new FraysSpawnPoint(0x33, 156.0f, 60.0f, 20.5f),
                    new FraysSpawnPoint(0x35, 156.0f, 108.0f, 20.0f),
                    new FraysSpawnPoint(0x05, 18.0f, 108.0f, 20.0f),
                },
                Mix = new[] { new FraysMixEntry(DriftGolem, 2), new FraysMixEntry(DriftRubble, 2), new FraysMixEntry(DriftGolemViceroy, 1) },
            },
            new FraysFireDef
            {
                Id = "vh15", RoomName = "South Crag", Landblock = 0xD6DB, Fray = VissidalHeights, Outdoor = true,
                FireCell = 0x25, FirePos = new Vector3(108.0f, 108.0f, 40.0f),
                RoomCells = Cells(),
                SpawnPoints = new[]
                {
                    new FraysSpawnPoint(0x08, 18.0f, 174.0f, 40.5f),
                    new FraysSpawnPoint(0x39, 174.0f, 18.0f, 40.0f),
                    new FraysSpawnPoint(0x40, 174.0f, 174.0f, 49.5f),
                    new FraysSpawnPoint(0x05, 18.0f, 108.0f, 39.0f),
                    new FraysSpawnPoint(0x20, 84.0f, 174.0f, 43.5f),
                    new FraysSpawnPoint(0x21, 108.0f, 18.0f, 38.5f),
                    new FraysSpawnPoint(0x3C, 174.0f, 84.0f, 40.5f),
                    new FraysSpawnPoint(0x14, 60.0f, 84.0f, 39.0f),
                    new FraysSpawnPoint(0x2B, 132.0f, 60.0f, 40.0f),
                    new FraysSpawnPoint(0x2F, 132.0f, 156.0f, 43.0f),
                    new FraysSpawnPoint(0x16, 60.0f, 132.0f, 40.0f),
                    new FraysSpawnPoint(0x3E, 174.0f, 132.0f, 44.5f),
                    new FraysSpawnPoint(0x0F, 36.0f, 156.0f, 40.0f),
                    new FraysSpawnPoint(0x1B, 84.0f, 60.0f, 38.5f),
                    new FraysSpawnPoint(0x32, 156.0f, 36.0f, 40.0f),
                    new FraysSpawnPoint(0x35, 156.0f, 108.0f, 40.5f),
                    new FraysSpawnPoint(0x06, 18.0f, 132.0f, 40.0f),
                    new FraysSpawnPoint(0x07, 18.0f, 156.0f, 40.0f),
                    new FraysSpawnPoint(0x0D, 36.0f, 108.0f, 40.0f),
                    new FraysSpawnPoint(0x0E, 36.0f, 132.0f, 40.0f),
                },
                Mix = new[] { new FraysMixEntry(DriftGolem, 2), new FraysMixEntry(DriftRubble, 2), new FraysMixEntry(DriftGolemViceroy, 1) },
            },
            new FraysFireDef
            {
                Id = "vh16", RoomName = "Spark Ledge", Landblock = 0xD5E1, Fray = VissidalHeights, Outdoor = true,
                FireCell = 0x1B, FirePos = new Vector3(84.0f, 60.0f, 54.5f),   // back on the ledge (at 168, 120, toward the Eastern Heights landing, it sat at the cliff foot, height 0, below its own spawn points)
                RoomCells = Cells(),
                SpawnPoints = new[]
                {
                    new FraysSpawnPoint(0x07, 18.0f, 156.0f, 53.0f),
                    new FraysSpawnPoint(0x39, 174.0f, 18.0f, 56.0f),
                    new FraysSpawnPoint(0x01, 18.0f, 18.0f, 50.5f),
                    new FraysSpawnPoint(0x04, 18.0f, 84.0f, 49.5f),
                    new FraysSpawnPoint(0x15, 60.0f, 108.0f, 55.0f),
                    new FraysSpawnPoint(0x21, 108.0f, 18.0f, 50.0f),
                    new FraysSpawnPoint(0x2C, 132.0f, 84.0f, 53.5f),
                    new FraysSpawnPoint(0x11, 60.0f, 18.0f, 51.5f),
                    new FraysSpawnPoint(0x0A, 36.0f, 36.0f, 52.0f),
                    new FraysSpawnPoint(0x0E, 36.0f, 132.0f, 53.5f),
                    new FraysSpawnPoint(0x2A, 132.0f, 36.0f, 53.0f),
                    new FraysSpawnPoint(0x33, 156.0f, 60.0f, 55.0f),
                    new FraysSpawnPoint(0x02, 18.0f, 36.0f, 49.0f),
                    new FraysSpawnPoint(0x03, 18.0f, 60.0f, 48.5f),
                    new FraysSpawnPoint(0x05, 18.0f, 108.0f, 50.5f),
                    new FraysSpawnPoint(0x06, 18.0f, 132.0f, 52.0f),
                    new FraysSpawnPoint(0x09, 36.0f, 18.0f, 52.5f),
                    new FraysSpawnPoint(0x0B, 36.0f, 60.0f, 52.0f),
                    new FraysSpawnPoint(0x0C, 36.0f, 84.0f, 53.5f),
                    new FraysSpawnPoint(0x0D, 36.0f, 108.0f, 53.5f),
                },
                Mix = new[] { new FraysMixEntry(DriftGolem, 2), new FraysMixEntry(DriftRubble, 2), new FraysMixEntry(DriftGolemViceroy, 1) },
            },
            new FraysFireDef
            {
                Id = "vh17", RoomName = "Windward Mesa", Landblock = 0xD6DF, Fray = VissidalHeights, Outdoor = true,
                FireCell = 0x1C, FirePos = new Vector3(84.0f, 84.0f, 40.0f),
                RoomCells = Cells(),
                SpawnPoints = new[]
                {
                    new FraysSpawnPoint(0x08, 18.0f, 174.0f, 40.0f),
                    new FraysSpawnPoint(0x01, 18.0f, 18.0f, 40.0f),
                    new FraysSpawnPoint(0x31, 156.0f, 18.0f, 44.5f),
                    new FraysSpawnPoint(0x36, 156.0f, 132.0f, 45.5f),
                    new FraysSpawnPoint(0x04, 18.0f, 84.0f, 40.0f),
                    new FraysSpawnPoint(0x19, 84.0f, 18.0f, 40.0f),
                    new FraysSpawnPoint(0x1F, 84.0f, 156.0f, 42.0f),
                    new FraysSpawnPoint(0x0E, 36.0f, 132.0f, 40.0f),
                    new FraysSpawnPoint(0x2B, 132.0f, 60.0f, 40.0f),
                    new FraysSpawnPoint(0x0A, 36.0f, 36.0f, 40.0f),
                    new FraysSpawnPoint(0x13, 60.0f, 60.0f, 40.0f),
                    new FraysSpawnPoint(0x15, 60.0f, 108.0f, 40.0f),
                    new FraysSpawnPoint(0x18, 60.0f, 174.0f, 42.0f),
                    new FraysSpawnPoint(0x22, 108.0f, 36.0f, 40.0f),
                    new FraysSpawnPoint(0x25, 108.0f, 108.0f, 40.0f),
                    new FraysSpawnPoint(0x34, 156.0f, 84.0f, 42.5f),
                    new FraysSpawnPoint(0x02, 18.0f, 36.0f, 40.0f),
                    new FraysSpawnPoint(0x03, 18.0f, 60.0f, 40.0f),
                    new FraysSpawnPoint(0x05, 18.0f, 108.0f, 40.0f),
                    new FraysSpawnPoint(0x06, 18.0f, 132.0f, 40.0f),
                },
                Mix = new[] { new FraysMixEntry(DriftGolem, 2), new FraysMixEntry(DriftRubble, 2), new FraysMixEntry(DriftGolemViceroy, 1) },
            },
            new FraysFireDef
            {
                Id = "vh18", RoomName = "Iron Shelf", Landblock = 0xCCE2, Fray = VissidalHeights, Outdoor = true,
                FireCell = 0x15, FirePos = new Vector3(60.0f, 108.0f, 47.0f),
                RoomCells = Cells(),
                SpawnPoints = new[]
                {
                    new FraysSpawnPoint(0x40, 174.0f, 174.0f, 40.5f),
                    new FraysSpawnPoint(0x01, 18.0f, 18.0f, 50.5f),
                    new FraysSpawnPoint(0x08, 18.0f, 174.0f, 50.0f),
                    new FraysSpawnPoint(0x20, 84.0f, 174.0f, 44.5f),
                    new FraysSpawnPoint(0x2E, 132.0f, 132.0f, 39.0f),
                    new FraysSpawnPoint(0x04, 18.0f, 84.0f, 50.5f),
                    new FraysSpawnPoint(0x12, 60.0f, 36.0f, 45.5f),
                    new FraysSpawnPoint(0x06, 18.0f, 132.0f, 54.5f),
                    new FraysSpawnPoint(0x30, 132.0f, 174.0f, 42.5f),
                    new FraysSpawnPoint(0x0B, 36.0f, 60.0f, 49.0f),
                    new FraysSpawnPoint(0x0F, 36.0f, 156.0f, 52.0f),
                    new FraysSpawnPoint(0x1B, 84.0f, 60.0f, 42.5f),
                    new FraysSpawnPoint(0x1E, 84.0f, 132.0f, 44.0f),
                    new FraysSpawnPoint(0x25, 108.0f, 108.0f, 40.5f),
                    new FraysSpawnPoint(0x27, 108.0f, 156.0f, 41.5f),
                    new FraysSpawnPoint(0x02, 18.0f, 36.0f, 49.5f),
                    new FraysSpawnPoint(0x03, 18.0f, 60.0f, 49.0f),
                    new FraysSpawnPoint(0x05, 18.0f, 108.0f, 53.5f),
                    new FraysSpawnPoint(0x07, 18.0f, 156.0f, 53.0f),
                    new FraysSpawnPoint(0x09, 36.0f, 18.0f, 50.0f),
                },
                Mix = new[] { new FraysMixEntry(DriftGolem, 2), new FraysMixEntry(DriftRubble, 2), new FraysMixEntry(DriftGolemViceroy, 1) },
            },
            new FraysFireDef
            {
                Id = "vh19", RoomName = "Quiet Basin", Landblock = 0xCFE2, Fray = VissidalHeights, Outdoor = true,
                FireCell = 0x1B, FirePos = new Vector3(84.0f, 60.0f, 20.5f),
                RoomCells = Cells(),
                SpawnPoints = new[]
                {
                    new FraysSpawnPoint(0x39, 174.0f, 18.0f, 20.0f),
                    new FraysSpawnPoint(0x05, 18.0f, 108.0f, 21.0f),
                    new FraysSpawnPoint(0x02, 18.0f, 36.0f, 17.5f),
                    new FraysSpawnPoint(0x11, 60.0f, 18.0f, 18.5f),
                    new FraysSpawnPoint(0x2A, 132.0f, 36.0f, 20.0f),
                    new FraysSpawnPoint(0x3B, 174.0f, 60.0f, 23.0f),
                    new FraysSpawnPoint(0x0B, 36.0f, 60.0f, 20.0f),
                    new FraysSpawnPoint(0x14, 60.0f, 84.0f, 21.5f),
                    new FraysSpawnPoint(0x21, 108.0f, 18.0f, 20.0f),
                    new FraysSpawnPoint(0x24, 108.0f, 84.0f, 23.0f),
                    new FraysSpawnPoint(0x03, 18.0f, 60.0f, 20.0f),
                    new FraysSpawnPoint(0x04, 18.0f, 84.0f, 20.0f),
                    new FraysSpawnPoint(0x0A, 36.0f, 36.0f, 19.0f),
                    new FraysSpawnPoint(0x0C, 36.0f, 84.0f, 20.5f),
                    new FraysSpawnPoint(0x12, 60.0f, 36.0f, 20.0f),
                    new FraysSpawnPoint(0x13, 60.0f, 60.0f, 20.0f),
                    new FraysSpawnPoint(0x19, 84.0f, 18.0f, 20.0f),
                    new FraysSpawnPoint(0x1A, 84.0f, 36.0f, 20.0f),
                    new FraysSpawnPoint(0x1C, 84.0f, 84.0f, 22.5f),
                    new FraysSpawnPoint(0x22, 108.0f, 36.0f, 20.0f),
                },
                Mix = new[] { new FraysMixEntry(DriftGolem, 2), new FraysMixEntry(DriftRubble, 2), new FraysMixEntry(DriftGolemViceroy, 1) },
            },
            new FraysFireDef
            {
                Id = "vh20", RoomName = "Torch Rise", Landblock = 0xD5DE, Fray = VissidalHeights, Outdoor = true,
                FireCell = 0x2C, FirePos = new Vector3(132.0f, 84.0f, 40.0f),
                RoomCells = Cells(),
                SpawnPoints = new[]
                {
                    new FraysSpawnPoint(0x01, 18.0f, 18.0f, 38.5f),
                    new FraysSpawnPoint(0x40, 174.0f, 174.0f, 40.0f),
                    new FraysSpawnPoint(0x39, 174.0f, 18.0f, 40.5f),
                    new FraysSpawnPoint(0x27, 108.0f, 156.0f, 38.0f),
                    new FraysSpawnPoint(0x14, 60.0f, 84.0f, 35.5f),
                    new FraysSpawnPoint(0x19, 84.0f, 18.0f, 40.0f),
                    new FraysSpawnPoint(0x36, 156.0f, 132.0f, 40.0f),
                    new FraysSpawnPoint(0x29, 132.0f, 18.0f, 40.0f),
                    new FraysSpawnPoint(0x3B, 174.0f, 60.0f, 40.0f),
                    new FraysSpawnPoint(0x0A, 36.0f, 36.0f, 39.5f),
                    new FraysSpawnPoint(0x1B, 84.0f, 60.0f, 39.0f),
                    new FraysSpawnPoint(0x1D, 84.0f, 108.0f, 35.5f),
                    new FraysSpawnPoint(0x22, 108.0f, 36.0f, 40.0f),
                    new FraysSpawnPoint(0x30, 132.0f, 174.0f, 40.0f),
                    new FraysSpawnPoint(0x32, 156.0f, 36.0f, 40.0f),
                    new FraysSpawnPoint(0x3D, 174.0f, 108.0f, 40.0f),
                    new FraysSpawnPoint(0x09, 36.0f, 18.0f, 40.0f),
                    new FraysSpawnPoint(0x11, 60.0f, 18.0f, 40.0f),
                    new FraysSpawnPoint(0x12, 60.0f, 36.0f, 40.0f),
                    new FraysSpawnPoint(0x13, 60.0f, 60.0f, 38.5f),
                },
                Mix = new[] { new FraysMixEntry(DriftGolem, 2), new FraysMixEntry(DriftRubble, 2), new FraysMixEntry(DriftGolemViceroy, 1) },
            },

            // The Sky Fray: the plateau's four arms. Fires and spawn points sit on flat floor at z 116, read from
            // the plateau's own floor polygons (the cell dat); points keep 8 m or more from each other and from the middle square.
            SkyCamp("vs1", "Western Spire", 36f, 108f, 0x0D, 108f - WingFireOut, 108f, new[]   // the same spot as the North fire, turned
            {
                new FraysSpawnPoint(0x15, 63.0f, 115.0f, PlateauFloor),
                new FraysSpawnPoint(0x05, 11.0f, 97.0f, PlateauFloor),
                new FraysSpawnPoint(0x0E, 29.0f, 135.0f, PlateauFloor),
                new FraysSpawnPoint(0x0C, 45.0f, 83.0f, PlateauFloor),
                new FraysSpawnPoint(0x05, 13.0f, 119.0f, PlateauFloor),
                new FraysSpawnPoint(0x16, 49.0f, 131.0f, PlateauFloor),
                new FraysSpawnPoint(0x0C, 25.0f, 83.0f, PlateauFloor),
                new FraysSpawnPoint(0x14, 59.0f, 93.0f, PlateauFloor),
                new FraysSpawnPoint(0x0C, 35.0f, 95.0f, PlateauFloor),
                new FraysSpawnPoint(0x0E, 37.0f, 121.0f, PlateauFloor),
                new FraysSpawnPoint(0x0E, 25.0f, 123.0f, PlateauFloor),
                new FraysSpawnPoint(0x04, 21.0f, 93.0f, PlateauFloor),
            }),
            SkyCamp("vs2", "Eastern Spire", 172f, 108f, 0x3D, 108f + WingFireOut, 108f, new[]
            {
                new FraysSpawnPoint(0x36, 165.0f, 135.0f, PlateauFloor),
                new FraysSpawnPoint(0x3C, 179.0f, 81.0f, PlateauFloor),
                new FraysSpawnPoint(0x3E, 189.0f, 123.0f, PlateauFloor),
                new FraysSpawnPoint(0x34, 159.0f, 85.0f, PlateauFloor),
                new FraysSpawnPoint(0x35, 153.0f, 119.0f, PlateauFloor),
                new FraysSpawnPoint(0x3C, 189.0f, 93.0f, PlateauFloor),
                new FraysSpawnPoint(0x3E, 179.0f, 135.0f, PlateauFloor),
                new FraysSpawnPoint(0x3E, 171.0f, 123.0f, PlateauFloor),
                new FraysSpawnPoint(0x34, 167.0f, 93.0f, PlateauFloor),
                new FraysSpawnPoint(0x3C, 169.0f, 83.0f, PlateauFloor),
            }),
            SkyCamp("vs3", "Southern Spire", 108f, 36f, 0x22, 108f, 108f - WingFireOut, new[]
            {
                new FraysSpawnPoint(0x23, 115.0f, 63.0f, PlateauFloor),
                new FraysSpawnPoint(0x21, 97.0f, 11.0f, PlateauFloor),
                new FraysSpawnPoint(0x2A, 135.0f, 29.0f, PlateauFloor),
                new FraysSpawnPoint(0x1A, 81.0f, 43.0f, PlateauFloor),
                new FraysSpawnPoint(0x21, 119.0f, 13.0f, PlateauFloor),
                new FraysSpawnPoint(0x2B, 131.0f, 49.0f, PlateauFloor),
                new FraysSpawnPoint(0x1B, 93.0f, 59.0f, PlateauFloor),
                new FraysSpawnPoint(0x1A, 85.0f, 25.0f, PlateauFloor),
                new FraysSpawnPoint(0x2A, 123.0f, 25.0f, PlateauFloor),
                new FraysSpawnPoint(0x1A, 93.0f, 47.0f, PlateauFloor),
                new FraysSpawnPoint(0x23, 103.0f, 63.0f, PlateauFloor),
                new FraysSpawnPoint(0x19, 95.0f, 21.0f, PlateauFloor),
            }),
            // Its fire at the foot of the north spire, placed on site;
            // 0.8 m inside the landblock edge.
            SkyCamp("vs4", "Northern Spire", 108f, 172f, 0x28, 108f, 108f + WingFireOut, new[]
            {
                new FraysSpawnPoint(0x30, 135.0f, 179.0f, PlateauFloor),
                new FraysSpawnPoint(0x1F, 81.0f, 165.0f, PlateauFloor),
                new FraysSpawnPoint(0x20, 93.0f, 189.0f, PlateauFloor),
                new FraysSpawnPoint(0x2F, 131.0f, 159.0f, PlateauFloor),
                new FraysSpawnPoint(0x27, 97.0f, 153.0f, PlateauFloor),
                new FraysSpawnPoint(0x30, 123.0f, 189.0f, PlateauFloor),
                new FraysSpawnPoint(0x30, 123.0f, 171.0f, PlateauFloor),
                new FraysSpawnPoint(0x20, 81.0f, 179.0f, PlateauFloor),
                new FraysSpawnPoint(0x30, 133.0f, 169.0f, PlateauFloor),
                new FraysSpawnPoint(0x1F, 87.0f, 157.0f, PlateauFloor),
            }),
            // The fifth camp, on the middle square round the Sky Fray portal's arrival (109.7, 110).
            // The fire stands 8 m north-west of the
            // arrival and 11 m from the return portal (about 109.7, 106); no spawn point is within 14 m of the arrival.
            SkyCamp("vs5", "Sky Hearth", 108f, 108f, 0x25, 103f, 115f, new[]
            {
                new FraysSpawnPoint(0x24, 101.0f, 81.0f, PlateauFloor),
                new FraysSpawnPoint(0x26, 115.0f, 135.0f, PlateauFloor),
                new FraysSpawnPoint(0x1D, 81.0f, 115.0f, PlateauFloor),
                new FraysSpawnPoint(0x2D, 135.0f, 101.0f, PlateauFloor),
                new FraysSpawnPoint(0x1C, 85.0f, 95.0f, PlateauFloor),
                new FraysSpawnPoint(0x1E, 95.0f, 131.0f, PlateauFloor),
                new FraysSpawnPoint(0x2C, 121.0f, 85.0f, PlateauFloor),
                new FraysSpawnPoint(0x2E, 131.0f, 121.0f, PlateauFloor),
                new FraysSpawnPoint(0x25, 101.0f, 97.0f, PlateauFloor),
                new FraysSpawnPoint(0x2D, 121.0f, 99.0f, PlateauFloor),
                new FraysSpawnPoint(0x1D, 93.0f, 109.0f, PlateauFloor),
                new FraysSpawnPoint(0x26, 107.0f, 125.0f, PlateauFloor),
            }),
        };

        /// <summary>A group camp on one of the plateau's arms: its zone circle round the arm, the fire on flat floor.</summary>
        private static FraysFireDef SkyCamp(string id, string name, float cx, float cy, ushort fireCell, float fx, float fy, FraysSpawnPoint[] points) => new FraysFireDef
        {
            Id = id, RoomName = name, Landblock = VissidalSky, Fray = VissidalSky, Outdoor = true, Group = true, BigGroup = id == "vs5",
            ZoneCentre = new Vector2(cx, cy), ZoneRadius = PlateauCampRadius, FloorAbove = PlateauAbove,
            FireCell = fireCell, FirePos = new Vector3(fx, fy, PlateauFloor),
            RoomCells = Cells(),
            SpawnPoints = points,
            WispLine = ArmWispLine(new Vector2(cx, cy)),
            Mix = new[] { new FraysMixEntry(DriftGolem, 2), new FraysMixEntry(DriftRubble, 2), new FraysMixEntry(DriftGolemViceroy, 1) },
        };

        /// <summary>An arm's wisp line (PlateauWispLineOut), or null for the middle square (it keeps its circle).</summary>
        private static (Vector2 A, Vector2 B)? ArmWispLine(Vector2 armCentre)
        {
            var along = armCentre - PlateauMiddle;

            if (along.Length() < 1f)
                return null;

            along = Vector2.Normalize(along);
            var across = new Vector2(-along.Y, along.X);
            var mid = PlateauMiddle + along * PlateauWispLineOut;
            return (mid - across * PlateauWispLineHalf, mid + across * PlateauWispLineHalf);
        }


        /// <summary>
        /// Every landblock with Vissidal Island ground (from its terrain), minus the three with the island's town and
        /// camp buildings (0xD5DA, 0xD5DB, 0xD6DA). While a Vissidal Fray is active their retail monsters and
        /// generators are left out, so the island holds only campfire creatures; NPCs stay.
        /// </summary>
        public static readonly IReadOnlyCollection<ushort> VissidalIsland = new HashSet<ushort>
        {
            0xC7E2, 0xC7E3, 0xC7E4, 0xC8E1, 0xC8E2, 0xC8E3, 0xC8E4, 0xC9E1, 0xC9E2, 0xC9E3, 0xC9E4, 0xCAE1, 
            0xCAE2, 0xCAE3, 0xCAE4, 0xCBDF, 0xCBE0, 0xCBE1, 0xCBE2, 0xCBE3, 0xCBE4, 0xCCDF, 0xCCE0, 0xCCE1, 
            0xCCE2, 0xCCE3, 0xCCE4, 0xCDDF, 0xCDE0, 0xCDE1, 0xCDE2, 0xCDE3, 0xCDE4, 0xCEDE, 0xCEDF, 0xCEE0, 
            0xCEE1, 0xCEE2, 0xCEE3, 0xCEE4, 0xCFDE, 0xCFDF, 0xCFE0, 0xCFE1, 0xCFE2, 0xCFE3, 0xCFE4, 0xD0DE, 
            0xD0DF, 0xD0E0, 0xD0E1, 0xD0E2, 0xD0E3, 0xD1DD, 0xD1DE, 0xD1DF, 0xD1E0, 0xD1E1, 0xD1E2, 0xD1E3, 
            0xD2DD, 0xD2DE, 0xD2DF, 0xD2E0, 0xD2E1, 0xD2E2, 0xD3DC, 0xD3DD, 0xD3DE, 0xD3DF, 0xD3E0, 0xD3E1, 
            0xD3E2, 0xD4DB, 0xD4DC, 0xD4DD, 0xD4DE, 0xD4DF, 0xD4E0, 0xD4E1, 0xD4E2, 0xD5DC, 0xD5DD, 0xD5DE, 
            0xD5DF, 0xD5E0, 0xD5E1, 0xD5E2, 0xD6D9, 0xD6DB, 0xD6DC, 0xD6DD, 0xD6DE, 0xD6DF, 0xD6E0, 0xD6E1, 
            0xD7D9, 0xD7DA, 0xD7DB, 0xD7DC, 0xD7DD, 0xD7DE, 0xD7DF, 0xD7E0, 0xD7E1, 0xD8DA, 0xD8DB, 0xD8DC, 
            0xD8DD, 0xD8DE, 0xD8DF, 0xD8E0, 
        };
        public static IEnumerable<FraysFireDef> ForLandblock(ushort landblock) => All.Where(f => f.Landblock == landblock);

        /// <summary>The Fray a landblock belongs to (its fires' Fray), or the landblock itself when it holds none.</summary>
        public static ushort FrayOfLandblock(ushort landblock) => All.FirstOrDefault(f => f.Landblock == landblock)?.FrayKey ?? landblock;

        public static FraysFireDef Find(string id) => All.FirstOrDefault(f => string.Equals(f.Id, id, System.StringComparison.OrdinalIgnoreCase));
    }
}
