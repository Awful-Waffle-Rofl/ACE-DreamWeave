using System;
using System.Collections.Generic;
using System.Linq;

namespace ACE.Server.Pvp.Battlegrounds
{
    /// <summary>
    /// The battleground maps (Docs/Pvp/BATTLEGROUNDS.md "Map: 0x016C", Docs/Pvp/ATTACK-DEFEND.md "Map: Abandoned Mines, 0x003C"): landblock
    /// 0x016C (King of the Hill) and 0x003C (Attack/Defend), each in the same realm as the arena maps. Kept apart from <see cref="ArenaMapCatalog.All"/>, which tests pin to the arenas.
    ///
    /// <para/>
    /// Every point was measured with the content tool (`cells --landblock=0x016C`, spawncheck and clearance), z =
    /// 0.005 as for the arena maps. Each team spawns in the same room as its pen, outside the sealed pen cell: six
    /// points in a 2 x 3 grid east of the west pen (x 5 and 8, y -57/-60/-63), facing east out of the room, each cleared
    /// with `cells --clearance` and `cells --spawncheck` (human setup). Team 1 is the exact x-mirror (110 - x) of team 0:
    /// same y, same W, negated Z (facing west).
    /// </summary>
    public static class BattlegroundMapCatalog
    {
        public const string Bg016cKey = "bg_016c";

        public const uint Landblock016c = 0x016C;

        public const string Bg003cKey = "bg_003c";

        public const uint Landblock003c = 0x003C;

        /// <summary>Spawn points per team; covers the largest premade, 6v6.</summary>
        public const int SpawnsPerTeam = 6;

        private const float Z = 0.005f;
        private const uint BarrierWcid = 1001088;

        private static PvpSpawnPoint P(string label, ushort cellLow, float x, float y, float w, float z) =>
            new PvpSpawnPoint(label, cellLow, x, y, Z, w, z);

        private static BattlegroundSealPiece S(uint cellId, float x, float y, float w, float z) =>
            new BattlegroundSealPiece(BarrierWcid, cellId, x, y, Z, w, z);

        /// <summary>A start gate piece: the barrier on the floor, unrotated across a y-normal aperture or turned 90 degrees across an x-normal one.</summary>
        private static BattlegroundSealPiece G(uint cellId, float x, float y, bool turned) => G(cellId, x, y, Z, turned);

        private static BattlegroundSealPiece G(uint cellId, float x, float y, float z, bool turned) =>
            new BattlegroundSealPiece(BarrierWcid, cellId, x, y, z, turned ? 0.707107f : 1f, turned ? 0.707107f : 0f);

        private static readonly IReadOnlyList<PvpSpawnPoint> WestSpawns = Array.AsReadOnly(new[]
        {
            P("W1", 0x0155, 8.00f, -60.00f, 0.707107f, -0.707107f),
            P("W2", 0x0135, 5.00f, -60.00f, 0.707107f, -0.707107f),
            P("W3", 0x0155, 8.00f, -57.00f, 0.707107f, -0.707107f),
            P("W4", 0x0135, 5.00f, -57.00f, 0.707107f, -0.707107f),
            P("W5", 0x0155, 8.00f, -63.00f, 0.707107f, -0.707107f),
            P("W6", 0x0135, 5.00f, -63.00f, 0.707107f, -0.707107f),
        });

        private static readonly IReadOnlyList<PvpSpawnPoint> EastSpawns = Array.AsReadOnly(new[]
        {
            P("E1", 0x0239, 102.00f, -60.00f, 0.707107f, 0.707107f),
            P("E2", 0x0239, 105.00f, -60.00f, 0.707107f, 0.707107f),
            P("E3", 0x0239, 102.00f, -57.00f, 0.707107f, 0.707107f),
            P("E4", 0x0239, 105.00f, -57.00f, 0.707107f, 0.707107f),
            P("E5", 0x0239, 102.00f, -63.00f, 0.707107f, 0.707107f),
            P("E6", 0x0239, 105.00f, -63.00f, 0.707107f, 0.707107f),
        });

        public static readonly BattlegroundLayout Bg016c = new BattlegroundLayout(
            Bg016cKey,
            Landblock016c,
            ArenaMapCatalog.ArenaRealmId,
            Array.AsReadOnly(new[] { WestSpawns, EastSpawns }),
            Array.AsReadOnly(new[]
            {
                P("PenW", 0x0134, 0.00f, -60.00f, 0.707107f, -0.707107f),
                P("PenE", 0x0274, 110.00f, -60.00f, 0.707107f, 0.707107f),
            }),
            Array.AsReadOnly(new[]
            {
                S(0x016C0134, -1.72f, -60.00f, 0.707107f, -0.707107f),
                S(0x016C0134, 1.72f, -60.00f, 0.707107f, 0.707107f),
                S(0x016C0274, 108.28f, -60.00f, 0.707107f, -0.707107f),
                S(0x016C0274, 111.72f, -60.00f, 0.707107f, 0.707107f),
            }),
            ZoneX: 55f,
            ZoneY: -35f,
            ZoneZ: Z)
        {
            DisplayName = "Marketplace",
            // START GATES (Docs/Pvp/BATTLEGROUNDS.md "Start gates"): removed when the match goes Live. Surveyed 2026-10-06 with
            // `ACE.Content.Tools cells --landblock=0x016C --openings=<cell>`. The west start room is the open 10 x 10 cell 0x0155
            // (x 5..15, y -65..-55, floor z 0) plus the 3.03 m strip 0x0135 (x 1.97..5) that holds spawn x 5; the pen closet 0x0134
            // behind the strip is already shut by the pen seals at x +-1.72. `--openings=0x016C0155` prints four 10.00 x 6.00 apertures,
            // all with the 6 m barrier's own size: to 0x0135 at (5, -60) (internal), to 0x0154 at (10, -55) normal -y, to 0x0171 at
            // (15, -60) normal -x, to 0x015B at (10, -65) normal +y; plus a zero-size horizontal portal to 0x028E at z 6 (the ceiling
            // plane of the 6 m cell, 0 x 0, not a doorway). `--openings=0x016C0135` prints 3.03 x 6.00 doorways at (3.48, -55) normal
            // -y (to 0x012F) and (3.48, -65) normal +y (to 0x013C), the 8 x 5 portal to the pen 0x0134, the 10 x 6 portal to 0x0155,
            // and the same zero-size ceiling portal. So the room has exactly five exterior openings: 0x0155 north, south and east,
            // 0x0135 north and south. Each piece stands on the floor, 0.2 m INSIDE the room cell like the A/D pen seals; the 10 m
            // barrier (x -5..5, y -0.05..0.10, z 0..6) is unrotated across a y-normal aperture and turned 90 degrees (W = Z = 0.707107)
            // across an x-normal one. The two pieces on one side overlap, which is harmless. Team 1 is the exact x-mirror (110 - x):
            // `--openings=0x016C0239` (the 0x0155 mirror, apertures at x 95/105, y -55/-65, same sizes) and `--openings=0x016C0275`
            // (the 0x0135 mirror: 3.03 x 6.00 doorways at (106.52, -55) and (106.52, -65), 8 x 5 portal to the pen 0x0274) agree.
            // Every one of the ten placements was spawn-checked: `cells --landblock=0x016C --spawncheck=x,y,0.005 --cell=<cell>
            // --setup=0x02000C2E` (the barrier setup) prints `VERDICT: SPAWNS in <cell>` for each.
            // The upper cell 0x028E (floor z 6) is not reachable from the floor of a 6 m room and is left alone.
            StartGates = Array.AsReadOnly(new[]
            {
                // West (team 0): room cell 0x0155, then the strip 0x0135.
                G(0x016C0155, 10.00f, -55.20f, turned: false),
                G(0x016C0155, 10.00f, -64.80f, turned: false),
                G(0x016C0155, 14.80f, -60.00f, turned: true),
                G(0x016C0135, 3.485f, -55.20f, turned: false),
                G(0x016C0135, 3.485f, -64.80f, turned: false),
                // East (team 1): room cell 0x0239, then the strip 0x0275.
                G(0x016C0239, 100.00f, -55.20f, turned: false),
                G(0x016C0239, 100.00f, -64.80f, turned: false),
                G(0x016C0239, 95.20f, -60.00f, turned: true),
                G(0x016C0275, 106.515f, -55.20f, turned: false),
                G(0x016C0275, 106.515f, -64.80f, turned: false),
            }),
            // A cell at the zone centre (55, -35), which sits on a four-cell corner. Only carries the landblock: the live
            // placement resolves each marker's own cell (see BattlegroundLayout.ZoneCellLow).
            ZoneCellLow = 0x01BC,
            // Moving hill (Docs/Pvp/BATTLEGROUNDS.md "Moving hill"): one mirrored pair, the two rooms the spawn rings used to
            // occupy, given by the west point; the east point is the exact x-mirror (110 - x). (15,-15) is the best centre in the old
            // west spawn room: `cells --clearance=15,-15,0.005 --radius=6` PASSES with the nearest wall 13.03 m away. It is over 40 m
            // from every spawn point and both pens.
            ZonePairs = Array.AsReadOnly(new[]
            {
                new KothSitePair(15f, -15f),
            }),
            // Purple "Hill Marker" (wcid 1006803), from the zone-marker content PR #1533 (merged). On a server whose world
            // database does not hold that weenie yet, the wcid does not create and each marker is skipped (best effort).
            ZoneMarkerWcid = 1006803,
            Modes = Array.AsReadOnly(new[] { BattlegroundModes.KothModeKey }),
        };

        // ---------------- bg_003c: Abandoned Mines (Docs/Pvp/ATTACK-DEFEND.md "Map: Abandoned Mines, 0x003C") ----------------
        //
        // Every point was measured with the content tool (`cells --landblock=0x003C`: spawncheck, clearance, openings, room),
        // 2026-10-06/07.
        // Team 0 is the ATTACKER (layout side 0), team 1 the defender. Floors: every spawn and pen is on the lower level, z -6 (attacker
        // room and pen, defender room and pen, the Great Hall crystal site); every spawn and pen point stands 0.005 above its floor like
        // the other maps. Layout re-done to the owner's markup 2026-10-07: attackers spawn in the north-east L room and respawn in closet
        // 0x0223; defenders spawn in the open room 0x01D1/0x01D7 west of the N-S corridor and respawn in closet 0x01D4. The only
        // permanent seals are the two respawn-closet seals and the pit seal; the spawn rooms are open once the match is Live, and are
        // held shut until then by the two START GATES below. The West Cavern and
        // Pit Hall crystal sites stand on their own floors, z -30 and z -42. Rotation is a pure yaw (W, Z) = (cos, sin) of half the
        // heading, heading 0 = +y, counter-clockwise positive.
        //
        // SEALS. The barrier (wcid 1001088) is 10 m wide along its local x (`cells --modelbounds=0x02000C2E`: x -5..5, y -0.05..0.10,
        // z 0..6, origin at the base), so with NO rotation it closes a doorway whose normal runs along y, which every doorway sealed
        // here does (`cells --openings`: 3.33 m wide, normal along y). The piece stands on the FLOOR (the doorway centre z the survey
        // prints is 1.5 m above it) and 0.2 m INSIDE the pen cell, so its 10 m width spans the whole closet. Each placement was
        // spawn-checked with setup 0x02000C2E (SPAWNS).
        //
        // CRYSTAL SITES (owner ruling 2026-10-06): the Great Hall (centre of its room), the West Cavern (the big L-shaped cavern of the
        // west wing) and the Pit Hall (the hall at the foot of the long spiral stair, above the pit). Each was checked with
        // `cells --landblock=0x003C --cell=<cell> --spawncheck=x,y,z --setup=0x02001023` (the Warding Crystal's setup, Content/sql/weenies/
        // 1006805 Warding Crystal.sql) and `--clearance=x,y,z --radius=3`:
        //   Great Hall  0x003C01EE (110, -70, -6)    SPAWNS; clearance PASS, nearest wall 5.00 m (moved 10 m north to the middle of the cell)
        //   West Cavern 0x003C0161 (54.5, -55.4, -30) SPAWNS; clearance PASS, nearest wall 14.76 m
        //   Pit Hall    0x003C013E (89, -164, -42)   SPAWNS; clearance PASS, nearest wall 9.85 m
        // The attackers reach the West Cavern and Pit Hall only by passing through the octagon (now plain transit) and then the corridor
        // cell 0x01DD, which is the cell outside the defender spawn room's doorway (owner-accepted).
        // The old bounding seal at 0x01DC is gone: it was the only thing cutting off the west wing, the spiral stair and the south hall.
        // The pit seal below closes the 10 x 6 m opening 0x013F to 0x0140 at (90, -175, -39), normal (0, +1); beyond it lies a stack of
        // floorless cells and a 30 m fall into the portal ring at z -72. With both pen seals and the pit seal, a flood fill from both
        // spawn rooms over the `cells --graph` join list reaches 284 cells (223 with floor), none of them floored below z -42 (without
        // the pit seal the earlier layout reached 346 cells and 50 floored cells below z -45). Docs/Pvp/ATTACK-DEFEND.md carries the full derivation.

        private const float LowZ = -5.995f;

        private static PvpSpawnPoint P3(string label, ushort cellLow, float x, float y, float z, float w, float rz) =>
            new PvpSpawnPoint(label, cellLow, x, y, z, w, rz);

        private static BattlegroundSealPiece S3(uint cellId, float x, float y, float z) =>
            new BattlegroundSealPiece(BarrierWcid, cellId, x, y, z, 1f, 0f);

        // Room 0x003C0216 (14 cells, one 3.33 m doorway at (175, -20)): lower-floor cell 0x022A, a 2 x 3 grid at 3 m spacing, each point
        // at least 3 m from the nearest wall or edge (`--clearance`, body 0.68 m), facing the doorway. The corridor spawns first surveyed
        // in 0x0222/0x0221 face a defender firing line and are not used.
        private static readonly IReadOnlyList<PvpSpawnPoint> AttackerSpawns003c = Array.AsReadOnly(new[]
        {
            P3("A1", 0x022A, 189f, -13f, LowZ, 0.525731f, 0.850651f),
            P3("A2", 0x022A, 192f, -13f, LowZ, 0.556440f, 0.830888f),
            P3("A3", 0x022A, 189f, -10f, LowZ, 0.457582f, 0.889168f),
            P3("A4", 0x022A, 192f, -10f, LowZ, 0.496478f, 0.868050f),
            P3("A5", 0x022A, 189f, -7f, LowZ, 0.399718f, 0.916638f),
            P3("A6", 0x022A, 192f, -7f, LowZ, 0.443029f, 0.896507f),
        });

        // The defender spawn room (owner layout 2026-10-07, "Defense Spawn"): room 0x01D1, the cells 0x01D1 (x 55..65) and 0x01D7
        // (x 65..75), y -85..-75, floor z -6, 20 x 10 m, one 3.33 m doorway at (75, -80) (0x01D7 to the stub 0x01DE, normal (-1, 0)).
        // The whole room is open: no seal anywhere in it. Six points in two rows at y -82.5 and -77.5 and x 59, 65.5, 68, each facing
        // the doorway centre (75, -80). Every spawn is visible from the corridor at (80, -80); the rows only keep spawns off the door's
        // axis. Two baked statics (0x01000E5F) stand in 0x01D1 at (60.80, -77.38) and (61.90, -82.43), so D2 and D5 sit at x 65.5, clear
        // of them. Each was checked with
        // `cells --landblock=0x003C --cell=<cell> --spawncheck=x,y,-5.995 --setup=0x02000001 --creature` (the human setup; the
        // control A1 passes with it): SPAWNS with the point unmoved, nearest wall or edge 2.5 m (`--clearance`). A 10 x 10 cell
        // cannot give six points the 3 m the attacker grid has. Points at x 60 / 62 / 63 / 67 on one row were moved by the
        // spawncheck, so the x set was picked from exact spawncheck passes, not from `--clearance` (which ignores statics). Each is 47.5 m or
        // more from the defender pen point.
        private static readonly IReadOnlyList<PvpSpawnPoint> DefenderSpawns003c = Array.AsReadOnly(new[]
        {
            P3("D1", 0x01D1, 59f, -82.5f, LowZ, 0.759729f, -0.650240f),
            P3("D2", 0x01D7, 65.5f, -82.5f, LowZ, 0.791989f, -0.610535f),
            P3("D3", 0x01D7, 68f, -82.5f, LowZ, 0.817416f, -0.576048f),
            P3("D4", 0x01D1, 59f, -77.5f, LowZ, 0.650240f, -0.759729f),
            P3("D5", 0x01D7, 65.5f, -77.5f, LowZ, 0.610535f, -0.791989f),
            P3("D6", 0x01D7, 68f, -77.5f, LowZ, 0.576048f, -0.817416f),
        });

        public static readonly BattlegroundLayout Bg003c = new BattlegroundLayout(
            Bg003cKey,
            Landblock003c,
            ArenaMapCatalog.ArenaRealmId,
            Array.AsReadOnly(new[] { AttackerSpawns003c, DefenderSpawns003c }),
            Array.AsReadOnly(new[]
            {
                // Owner layout 2026-10-07 ("Attack Respawn" and "Defense Respawn"). Attacker closet 0x0223 (x 165.5..174.5, y -84.5..-75.5),
                // doorway north at y -75.5, unchanged: the easternmost of the 81 m2 closets, below the x 170 corridor branch, which is where
                // the marked closet sits relative to the corridors (the other two, 0x0213 at (150,-90) and 0x0205 at (140,-100), lie
                // south-west of it). Defender closet 0x01D4 (x 55.5..64.5, y -135..-125.5), one 3.33 m doorway at (60, -125.5) normal (0, -1)
                // to the stub 0x01D6, plus a roof shaft 0x01D5: the same shape as 0x0223, reached from the corridor by the diagonal chain
                // 0x01DB-01D2-01D3-01D6. Each pen faces its doorway (+y). Both pen points SPAWN with setup 0x02000001 --creature
                // (nearest obstruction 4.5 m), and every spawn is far from its own pen point (the respawn confirm radius is 8 m, so a
                // closer spawn could read a refused respawn as confirmed): attackers about 69 m or more, defenders 47.5 m or more.
                P3("PenAttack", 0x0223, 170f, -80f, LowZ, 1f, 0f),
                P3("PenDefend", 0x01D4, 60f, -130f, LowZ, 1f, 0f),
            }),
            Array.AsReadOnly(new[]
            {
                // Pen seals, unrotated (the join normals run along y), 0.2 m inside the pen cell, spawn-checked with 0x02000C2E: SPAWNS.
                S3(0x003C0223, 170f, -75.7f, LowZ),
                S3(0x003C01D4, 60f, -125.7f, LowZ),
                // The pit seal: across the opening from 0x013F to 0x0140 at (90, -175, -39), unrotated (the join's normal runs along y).
                // The floor of 0x013F is z -42, so the piece stands at -41.995 like the other seals. Spawn-checked with setup 0x02000C2E (SPAWNS).
                S3(0x003C013F, 90f, -174.8f, -41.995f),
            }),
            // ZoneX/ZoneY: this map has no King of the Hill zone, so they carry the point the crystal compass words are measured from, the
            // centroid of the three crystal sites: x (110 + 54.5 + 89) / 3, y (-70 - 55.4 - 164) / 3, z (-6 - 30 - 42) / 3.
            ZoneX: 84.5f,
            ZoneY: -96.47f,
            ZoneZ: -26f)
        {
            DisplayName = "Abandoned Mines",
            // START GATES (Docs/Pvp/ATTACK-DEFEND.md "Start gates"; the KotH mechanism, Docs/Pvp/BATTLEGROUNDS.md "Start gates"): one
            // barrier piece across each spawn room's only door, placed at staging and removed when the match goes Live (or resolves, or
            // is canceled or closed). Surveyed 2026-10-07 with `ACE.Content.Tools cells --landblock=0x003C --openings=<cell>`:
            // attackers' room: `--openings=0x003C0227` prints the door to 0x021F at (175.00, -20.00, -4.50) normal (1, 0), 3.33 wide x 3.00
            // high (the other two portals are the 10 x 6 join to 0x022B and a zero-size ceiling portal); defenders' room:
            // `--openings=0x003C01D7` prints the door to 0x01DE at (75.00, -80.00, -4.50) normal (-1, 0), 3.33 x 3.00 (plus the 10 x 6
            // join to 0x01D1 and the same ceiling portal). Both normals run along x, so the 10 m barrier (`cells --modelbounds=0x02000C2E`:
            // x -5..5, y -0.05..0.10, z 0..6) is turned 90 degrees (W = Z = 0.707107), which lays its 10 m length along y across the
            // door; both cells are 10 m wide along y (0x0227 y -25..-15, 0x01D7 y -85..-75), so the piece spans the whole cell and covers
            // the 3.33 m doorway with 3.3 m to spare each side. It stands on the floor (z -5.995) 0.2 m INSIDE the room, like the pen
            // seals. Each placement was spawn-checked with setup 0x02000C2E: `--spawncheck=175.2,-20,-5.995 --cell=0x003C0227` prints
            // `VERDICT: SPAWNS in 0x003C0227 at (175.2, -20, -6)` and `--spawncheck=74.8,-80,-5.995 --cell=0x003C01D7` prints
            // `VERDICT: SPAWNS in 0x003C01D7 at (74.8, -80, -6)`. No spawn point is within 3 m of a gate: the nearest attacker spawn
            // (A1 189, -13) is 15.5 m from the attacker gate and the nearest defender spawn (D3 68, -82.5 / D6 68, -77.5) is 7.2 m from
            // the defender gate. The respawn pens and their seals are separate and unchanged.
            StartGates = Array.AsReadOnly(new[]
            {
                // Attackers (team 0): the doorway cell 0x0227 of the L room.
                G(0x003C0227, 175.2f, -20f, LowZ, turned: true),
                // Defenders (team 1): the east cell 0x01D7 of the defender room.
                G(0x003C01D7, 74.8f, -80f, LowZ, turned: true),
            }),
            // Spawn-room protection (Docs/Pvp/ATTACK-DEFEND.md): the floor cells of each team's own spawn room and nothing beyond it.
            // Attackers: the L room 0x0216, floor cells 0x0216, 0x021E, 0x0226, 0x0227, 0x0229, 0x022A, 0x022B (the doorway cell 0x021F
            // is outside). Defenders: the room 0x01D1 and 0x01D7 only (the doorway stub 0x01DE is outside, so immunity ends at the
            // doorway on both sides). No pen cell and no ceiling cap is listed.
            SpawnRoomCells = Array.AsReadOnly(new IReadOnlyList<uint>[]
            {
                Array.AsReadOnly(new uint[] { 0x003C0216, 0x003C021E, 0x003C0226, 0x003C0229, 0x003C022A, 0x003C022B, 0x003C0227 }),
                Array.AsReadOnly(new uint[] { 0x003C01D1, 0x003C01D7 }),
            }),
            // The footprints the cell test is bounded by (the cell id and the coordinates are both client-reported, so the cell alone is
            // not trusted: see SpawnRoomArea). Derived from `cells --landblock=0x003C --room=0x0216 --cells` and `--room=0x01D1 --cells`:
            // every floor cell but one is 100 m2 (10 x 10) with the listed origin at its centre, so a cell covers origin +-5 in x and y (0x0229 is a 50 m2 triangle: its rectangle is a superset and the missing part has no floor), and the
            // floor_z of the room is -6.00 (the ceiling caps above it are 0x024E-0x0254 and 0x022C/0x0230, floor 0.27-0.40, not listed).
            // Attackers, rectangles merged from the seven cells: x 155..195 y -5..5 (0x0216, 0x021E, 0x0226, 0x0229), x 175..185 y -25..-15
            // (0x0227), x 185..195 y -25..-5 (0x022A, 0x022B). Defenders: x 55..75 y -85..-75 (0x01D1, 0x01D7). The z band -7..0 holds a
            // standing or jumping player on the -6 floor and excludes the upper level.
            SpawnRoomFootprints = Array.AsReadOnly(new[]
            {
                new SpawnRoomFootprint(
                    Array.AsReadOnly(new[]
                    {
                        new SpawnRoomRect(155f, 195f, -5f, 5f),
                        new SpawnRoomRect(175f, 185f, -25f, -15f),
                        new SpawnRoomRect(185f, 195f, -25f, -5f),
                    }), -7f, 0f),
                new SpawnRoomFootprint(
                    Array.AsReadOnly(new[]
                    {
                        new SpawnRoomRect(55f, 75f, -85f, -75f),
                    }), -7f, 0f),
            }),
            Modes = Array.AsReadOnly(new[] { BattlegroundModes.AttackDefendModeKey }),
            DefaultCrystalCount = 3,
            // The Warding Crystal (Content/sql/weenies/1006805 Warding Crystal.sql).
            CrystalWcid = 1006805,
            CrystalSites = Array.AsReadOnly(new[]
            {
                new BattlegroundCrystalSite("Great Hall", 0x01EE, 110f, -70f, -6f, "upper level, south-east of the octagon"),
                new BattlegroundCrystalSite("West Cavern", 0x0161, 54.5f, -55.4f, -30f, "lower level, south from the octagon past the Defender room, then down the stairs"),
                new BattlegroundCrystalSite("Pit Hall", 0x013E, 89f, -164f, -42f, "bottom of the spiral stair, south-east of the West Cavern"),
            }),
        };

        /// <summary>Every battleground map.</summary>
        public static readonly IReadOnlyList<BattlegroundLayout> All = Array.AsReadOnly(new[] { Bg016c, Bg003c });

        /// <summary>
        /// The bridge to the arena's space seam (<see cref="IPvpMatchSpaces.Allocate"/> and <see cref="ArenaSpawnPosition.Build"/>
        /// take an <see cref="ArenaMap"/>): the same key, landblock and realm as the layout, with one spawn set holding
        /// every team spawn and both pens, so the provider's shape check and arrival probe cover each point a player is
        /// ever sent to. NOT in <see cref="ArenaMapCatalog.All"/>; the coordinator finds the layout again by
        /// <see cref="Find"/> on the map key.
        /// </summary>
        public static readonly ArenaMap Bg016cSpaceMap;

        /// <summary>The space-seam map of 0x003C (Abandoned Mines).</summary>
        public static readonly ArenaMap Bg003cSpaceMap;

        /// <summary>The space-seam map for every battleground layout, DERIVED from <see cref="All"/> and in its order.</summary>
        public static readonly IReadOnlyList<ArenaMap> SpaceMaps;

        static BattlegroundMapCatalog()
        {
            SpaceMaps = Array.AsReadOnly(All.Select(SpaceMapFor).ToArray());
            Bg016cSpaceMap = SpaceMaps.First(m => m.MapKey == Bg016cKey);
            Bg003cSpaceMap = SpaceMaps.First(m => m.MapKey == Bg003cKey);
            Landblocks = new HashSet<uint>(All.Select(l => l.LandblockId));
        }

        /// <summary>The space-seam map built for <paramref name="layout"/> (the same instance every time), or null when it is not in <see cref="All"/>.</summary>
        public static ArenaMap SpaceMapOf(BattlegroundLayout layout) =>
            layout == null ? null : SpaceMaps.FirstOrDefault(m => string.Equals(m.MapKey, layout.MapKey, StringComparison.OrdinalIgnoreCase));

        /// <summary>Builds the <see cref="ArenaMap"/> bridge for a layout. Pure.</summary>
        public static ArenaMap SpaceMapFor(BattlegroundLayout layout)
        {
            var points = new List<PvpSpawnPoint>();

            foreach (var team in layout.TeamSpawns)
                points.AddRange(team);

            points.AddRange(layout.TeamPens);

            var sets = new Dictionary<string, IReadOnlyList<PvpSpawnPoint>>(StringComparer.OrdinalIgnoreCase)
            {
                [BattlegroundModes.RoomKey] = points.AsReadOnly(),
            };

            return new ArenaMap(layout.MapKey, layout.LandblockId, layout.RealmId, sets) { DisplayName = layout.DisplayName };
        }

        /// <summary>The 16-bit landblock numbers of every battleground map, for later lookups.</summary>
        public static readonly IReadOnlySet<uint> Landblocks;

        /// <summary>The map with this key, or null.</summary>
        public static BattlegroundLayout Find(string mapKey)
        {
            foreach (var map in All)
            {
                if (string.Equals(map.MapKey, mapKey, StringComparison.OrdinalIgnoreCase))
                    return map;
            }

            return null;
        }

        /// <summary>
        /// Seats a match's participants, or null when the shape does not fit (never throws for a bad shape). Pure.
        /// Teams are keyed by <see cref="PvpTeam.TeamIndex"/> (0 = west, 1 = east), exactly one team per index;
        /// member i of a team takes spawn i of that team's list, so up to <see cref="SpawnsPerTeam"/> members fit.
        /// </summary>
        public static IReadOnlyList<SpawnAssignment> AssignSpawns(BattlegroundLayout map, IReadOnlyList<PvpTeam> teams)
        {
            if (map == null || teams == null || teams.Count != map.TeamSpawns.Count)
                return null;

            var seen = new HashSet<int>();
            var result = new List<SpawnAssignment>();

            foreach (var team in teams)
            {
                if (team?.Members == null || !seen.Add(team.TeamIndex))
                    return null;

                var spawns = map.SpawnsFor(team.TeamIndex);

                if (spawns.Count == 0 || team.Members.Count > spawns.Count)
                    return null;

                for (var m = 0; m < team.Members.Count; m++)
                {
                    if (team.Members[m] == null)
                        return null;

                    result.Add(new SpawnAssignment(team.TeamIndex, team.Members[m], spawns[m]));
                }
            }

            return result;
        }

        /// <summary>
        /// The spawn for a respawn: the team's points in order, round-robin on <paramref name="counter"/> (the
        /// number of respawns the team has issued). Null for an unknown team index. Pure.
        /// </summary>
        public static PvpSpawnPoint RespawnSpawn(BattlegroundLayout map, int teamIndex, int counter)
        {
            var spawns = map?.SpawnsFor(teamIndex);

            if (spawns == null || spawns.Count == 0)
                return null;

            var i = counter % spawns.Count;

            return spawns[i < 0 ? i + spawns.Count : i];
        }
    }
}