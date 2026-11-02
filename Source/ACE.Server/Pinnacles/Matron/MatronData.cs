using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

using ACE.Server.HazardMechanics;
using ACE.Server.PuzzleGates;
using ACE.Server.Realms;

namespace ACE.Server.Pinnacles.Matron
{
    /// <summary>
    /// One of the Matron room's five openings (Docs/Knots/MATRON-ENCOUNTER-DESIGN.md section 3). Centre and Outward are
    /// in landblock x, y; Outward is the unit vector pointing out of the room through the opening, into the niche.
    /// Every door, panel, effect host and platform point is derived from these two by <see cref="MatronLayout"/>
    /// (fan-out invariant: no per-opening logic). Stand is where the Matron plants herself on the floor to open the
    /// flow, facing the opening.
    /// </summary>
    public sealed record MatronOpening(int Id, string Name, Vector2 Centre, Vector2 Outward, uint NicheCell, PinnaclePoint Stand);

    /// <summary>
    /// The Matron's room, waves and wcids in one place (landblock 0x586B, the Queen's Quarters copy). Points are
    /// MEASURED (Docs/Knots/MATRON-ENCOUNTER-DESIGN.md section 3, plan T6); MatronRoomGeometryTests proves every one
    /// against the real dat geometry. Yaw is degrees counter-clockwise from north, turned into a rotation by
    /// <see cref="PuzzleGateGenerator.YawQuaternion"/> (the hazard spawn path's convention, HazardContext.Spawn).
    /// </summary>
    public static class MatronData
    {
        public const string Kind = "matron";

        public const ushort Landblock = 0x586B;

        // ---- wcids (T0 reserved 1007550-1007599, owner pinnacle-matron) ----
        public const uint HeadWcid = 1007550;

        public const uint PlatformWcid = 1007551;

        /// <summary>The drain's grate layer: a plain static ClipMap quad (setup 0x02000E15, the prison-door bars texture), unselectable; spawned as a 2 x 2 grid of tiles.</summary>
        public const uint GrateWcid = 1007552;

        /// <summary>The drain's black base layer: the grate's ClipMap quad (setup 0x02000E15) wearing a black texture, unselectable.</summary>
        public const uint DrainFloorWcid = 1007553;

        /// <summary>
        /// The drain's iris, the use target (named "Drain"): an Olthoi door (setup 0x020005F2) with its frame parts swapped for a null
        /// part, so only the petals remain. Allocated 2026-10-28 with nextwcid --owner=pinnacle-matron.
        /// </summary>
        public const uint DrainIrisWcid = 1007557;

        /// <summary>The Olthoi door (setup 0x020005F2).</summary>
        public const uint DoorWcid = 4451;

        public const uint AcidPanelWcid = HazardDefaults.PanelAcidWcid;

        public const uint LavaPanelWcid = HazardDefaults.PanelLavaWcid;

        public const uint EffectHostWcid = HazardDefaults.EffectHostWcid;

        /// <summary>The fire pillar effect script the pillar hosts play (the memory-pad pillar).</summary>
        public const uint PillarScript = HazardDefaults.PillarScript;

        // ---- tiers: she is fought through all five health bands, so Boss (plan key decision 6); the brood is Elite ----
        public const PinnacleTier HeadTier = PinnacleTier.Boss;

        public const PinnacleTier AddTier = PinnacleTier.Elite;

        /// <summary>The finale's breakable rock in the north opening (an add: its death is the lava trigger).</summary>
        public const uint RockWcid = 1007554;

        /// <summary>The wall strip's scrolling flow panels: retail's flowing lava (setup 0x02000DA8) and acid (0x02000DA9) floors, inert.</summary>
        public const uint LavaFlowWcid = 1007555;

        public const uint AcidFlowWcid = 1007556;

        /// <summary>The final wave's great sentinel: retail 25341 Olthoi Sentinel at twice its DefaultScale (1.6), named Hive Sentinel.</summary>
        public const uint SentinelWcid = 1007558;

        /// <summary>
        /// The rock's fixed tier. PinnacleTier has no tier under Elite (Elite, Boss, Head), so the rock takes the lowest
        /// there is.
        /// </summary>
        public const PinnacleTier RockTier = PinnacleTier.Elite;

        // ---- the room ----
        public const float FloorZ = -59.995f;

        /// <summary>The player's arrival and fight entry: the south nub floor (owner /loc).</summary>
        public static readonly PinnaclePoint ArrivalPoint = new PinnaclePoint(0x586B0114, 219.886154f, -159.602203f, FloorZ);

        public static readonly PinnaclePoint FightEntryPoint = ArrivalPoint;

        /// <summary>Where the Matron stands as the fight opens, facing south (yaw 180).</summary>
        public static readonly PinnaclePoint HeadPoint = Point(0x586B0110, 220.06f, -119.33f, FloorZ, 180f);

        public static readonly PinnaclePoint CachePoint = new PinnaclePoint(0x586B0111, 224f, -128.5f, FloorZ);

        public static readonly PinnaclePoint ReturnPortalPoint = new PinnaclePoint(0x586B0111, 216f, -128.5f, FloorZ);

        /// <summary>The drain's centre (the room centre): the three drain layers (MatronLayout.DrainFloor, DrainGrate, DrainIris) centre on it.</summary>
        public static readonly PinnaclePoint DrainPoint = new PinnaclePoint(0x586B0111, 220f, -128.5f, FloorZ);

        /// <summary>
        /// The finale's spot in the north alcove, where she plays the opening motion. Its rotation faces the arena centre
        /// (<see cref="DrainPoint"/>), so the in-place turn on arrival leaves her looking into the room.
        /// </summary>
        public static readonly PinnaclePoint FinalePoint = Point(0x586B0108, 210.83f, -109.23f, FloorZ,
            YawDegFacing(new Vector2(DrainPoint.X - 210.83f, DrainPoint.Y - (-109.23f))));

        /// <summary>
        /// The ONE fire pillar host, at (<paramref name="x"/>, <paramref name="y"/>) on the floor in the hall cell under it (her own
        /// <paramref name="fallbackCell"/> when the point is off the table). One host: three stacked side by side read in game as
        /// a solid white wall (playtest round 2).
        /// </summary>
        public static PinnaclePoint PillarHostAt(float x, float y, uint fallbackCell)
        {
            var cell = HallCell(x, y, FloorZ);

            return new PinnaclePoint(cell != 0 ? cell : fallbackCell, x, y, FloorZ);
        }

        /// <summary>The pillar host when she stands on <see cref="FinalePoint"/>. A property: the hall cell table is initialised below.</summary>
        public static PinnaclePoint PillarHostPoint => PillarHostAt(FinalePoint.X, FinalePoint.Y, FinalePoint.Cell);

        /// <summary>Observed retail floor spots (ace_world landblock_instance rows), all at z -59.995; a wave spawns on a shuffled subset.</summary>
        public static readonly IReadOnlyList<PinnaclePoint> WaveSpawnPoints = new[]
        {
            new PinnaclePoint(0x586B0109, 209.43f, -116.66f, FloorZ),
            new PinnaclePoint(0x586B0110, 222.27f, -115.44f, FloorZ),
            new PinnaclePoint(0x586B0108, 213.35f, -109.41f, FloorZ),
            new PinnaclePoint(0x586B0115, 229.77f, -109.58f, FloorZ),
            new PinnaclePoint(0x586B0119, 241.45f, -123.22f, FloorZ),
            new PinnaclePoint(0x586B0117, 232.43f, -132.75f, FloorZ),
            new PinnaclePoint(0x586B010A, 212.24f, -125.33f, FloorZ),
            new PinnaclePoint(0x586B010A, 208.24f, -133.04f, FloorZ),
        };

        /// <summary>The three lava pool panels (1006968 face up, scale 1) laid in the north alcove at z -59.8.</summary>
        public static readonly IReadOnlyList<PinnaclePoint> LavaPoolPoints = new[]
        {
            new PinnaclePoint(0x586B0108, 210f, -110f, LavaPoolZ),
            new PinnaclePoint(0x586B010F, 220f, -110f, LavaPoolZ),
            new PinnaclePoint(0x586B0115, 230f, -110f, LavaPoolZ),
        };

        public const float LavaPoolZ = -59.8f;

        /// <summary>
        /// Landblock_instance rows the Knot copy does not load: every retail row inside the room's box (the queen
        /// generators, the hall monsters and the Queen's Treasury portal). Three rows just outside stay.
        /// </summary>
        public static readonly EphemeralRowExclusion ExcludeRows = new EphemeralRowExclusion(0x586B, 194f, 246f, -166f, -100f, -61f, -44f);

        /// <summary>
        /// What the core spawns for the Matron: the head (her body) and nothing else. No seal: the room has no floor-level
        /// exit (design section 3), so SealPoints stays empty. Cache and return portal on the room's real floor.
        /// </summary>
        public static PinnacleScriptData Script() => new PinnacleScriptData
        {
            Landblock = Landblock,
            ArrivalPoint = ArrivalPoint,
            FightEntryPoint = FightEntryPoint,
            HeadWcid = HeadWcid,
            RequiredWcids = RequiredWcids,
            HeadPoint = HeadPoint,
            HeadTier = HeadTier,
            HeadLeavesCorpse = true,
            CachePoint = CachePoint,
            ReturnPortalPoint = ReturnPortalPoint,
            HazardRegionPreset = HazardRegionPresets.MatronName,
            ExcludeRows = ExcludeRows,
        };

        /// <summary>
        /// Every stand point is this far into the room from its opening (Centre - Outward x StandDepth): the least depth at which
        /// her 1.5 m body radius plus 0.3 m clears every wall-flush jump pad. The nearest pad corner is 1 m off the centre line
        /// and 2 m off the wall, so depth d clears it by sqrt(1 + (d - 2)^2), which is 1.8 at d = 3.4967; rounded up to the
        /// centimetre. Cells re-pinned from the dat (MatronRoomGeometryTests.EveryMatronDataPoint_ResolvesItsPinnedCell).
        /// </summary>
        public const float StandDepth = 3.5f;

        // ---- the five openings (design table, plan T6; stand points moved to StandDepth 2026-10-27) ----
        public static readonly IReadOnlyList<MatronOpening> Openings = new[]
        {
            new MatronOpening(1, "NW", new Vector2(200f, -115f), new Vector2(0f, 1f), 0x586B0136, Point(0x586B0103, 200f, -118.5f, FloorZ, 0f)),
            new MatronOpening(2, "SW", new Vector2(205f, -140f), new Vector2(-1f, 0f), 0x586B0139, Point(0x586B010B, 208.5f, -140f, FloorZ, 90f)),
            new MatronOpening(3, "N", new Vector2(220f, -105f), new Vector2(0f, 1f), 0x586B013E, Point(0x586B010F, 220f, -108.5f, FloorZ, 0f)),
            new MatronOpening(4, "SE", new Vector2(235f, -140f), new Vector2(1f, 0f), 0x586B014C, Point(0x586B0118, 231.5f, -140f, FloorZ, 270f)),
            new MatronOpening(5, "NE", new Vector2(240f, -115f), new Vector2(0f, 1f), 0x586B0149, Point(0x586B0119, 240f, -118.5f, FloorZ, 0f)),
        };

        /// <summary>The four acid pits (openings 1, 2, 4, 5); each flows exactly once, in a shuffled order.</summary>
        public static readonly IReadOnlyList<int> PitIds = new[] { 1, 2, 4, 5 };

        /// <summary>The north exit, opened only in the finale.</summary>
        public const int NorthId = 3;

        public static MatronOpening Opening(int id)
        {
            foreach (var o in Openings)
                if (o.Id == id)
                    return o;

            throw new ArgumentOutOfRangeException(nameof(id), id, "no such Matron opening (1-5)");
        }

        /// <summary>Every wcid the script spawns besides the head (the entry readiness check): props, hazard hosts and wave adds.</summary>
        public static IReadOnlyList<uint> RequiredWcids => new[] { PlatformWcid, GrateWcid, DrainFloorWcid, DrainIrisWcid, DoorWcid, AcidPanelWcid, LavaPanelWcid, EffectHostWcid, RockWcid, LavaFlowWcid, AcidFlowWcid, HazardDefaults.FillAcidWispWcid }
            .Concat(Waves.SelectMany(w => w.Select(e => e.Wcid))).Distinct().ToArray();

        // ---- waves (design section 4): (wcid, count) lists, wave k spawns after the k-th seal ----
        // The final wave is the two great sentinels alone (playtest round 6): they guard the finale threshold.
        public static readonly IReadOnlyList<IReadOnlyList<(uint Wcid, int Count)>> Waves = new IReadOnlyList<(uint Wcid, int Count)>[]
        {
            new[] { (213u, 4), (3u, 2) },
            new[] { (214u, 3), (25750u, 2), (24959u, 1) },
            new[] { (212u, 2), (25745u, 2), (23481u, 2), (24960u, 1) },
            new[] { (SentinelWcid, 2) },
        };

        /// <summary>Each wave's tier, by wave: the brood is Elite, the final wave's sentinels Boss (3 x the health curve, the boss level stamp).</summary>
        public static readonly IReadOnlyList<PinnacleTier> WaveTiers = new[] { AddTier, AddTier, AddTier, PinnacleTier.Boss };

        /// <summary>Wave <paramref name="number"/> (1-4).</summary>
        public static IReadOnlyList<(uint Wcid, int Count)> Wave(int number)
        {
            if (number < 1 || number > Waves.Count)
                throw new ArgumentOutOfRangeException(nameof(number), number, "waves are numbered 1-4");

            return Waves[number - 1];
        }

        /// <summary>The tier wave <paramref name="number"/> (1-4) spawns at.</summary>
        public static PinnacleTier WaveTier(int number)
        {
            if (number < 1 || number > WaveTiers.Count)
                throw new ArgumentOutOfRangeException(nameof(number), number, "waves are numbered 1-4");

            return WaveTiers[number - 1];
        }

        // ---- points and rotation ----

        /// <summary>A point turned to face <paramref name="yawDeg"/> degrees counter-clockwise from north.</summary>
        public static PinnaclePoint Point(uint cell, float x, float y, float z, float yawDeg)
        {
            var q = PuzzleGateGenerator.YawQuaternion(yawDeg);

            return new PinnaclePoint(cell, x, y, z, q.W, q.X, q.Y, q.Z);
        }

        /// <summary>The same point with a System.Numerics rotation (X, Y, Z, W) in place of a yaw.</summary>
        public static PinnaclePoint Point(uint cell, float x, float y, float z, Quaternion rotation)
            => new PinnaclePoint(cell, x, y, z, rotation.W, rotation.X, rotation.Y, rotation.Z);

        /// <summary>The yaw (degrees CCW from north) of a body facing <paramref name="direction"/>: north (0, 1) is 0, west (-1, 0) is 90.</summary>
        public static float YawDegFacing(Vector2 direction)
        {
            var yaw = Math.Atan2(-direction.X, direction.Y) * 180.0 / Math.PI;

            return (float)(yaw < -1e-9 ? yaw + 360.0 : yaw);
        }

        // ---- the hall's cell grid ----

        /// <summary>
        /// The room's 70 hall cells (cells --landblock=0x586B --room=0x586B0103 --cells): a 10 m square grid centred on
        /// each cell's own origin, in four stacked layers whose origins sit at z -60, -54, -48 and -42. The five niches
        /// (0x586B0136, 0139, 013E, 0149, 014C) are separate rooms and are not in this table.
        /// </summary>
        private static readonly (uint Id, int X, int Y, int Z)[] hallCells =
        {
            (0x586B0103, 200, -120, -60),
            (0x586B0104, 200, -130, -60),
            (0x586B0108, 210, -110, -60),
            (0x586B0109, 210, -120, -60),
            (0x586B010A, 210, -130, -60),
            (0x586B010B, 210, -140, -60),
            (0x586B010F, 220, -110, -60),
            (0x586B0110, 220, -120, -60),
            (0x586B0111, 220, -130, -60),
            (0x586B0112, 220, -140, -60),
            (0x586B0113, 220, -150, -60),
            (0x586B0114, 220, -160, -60),
            (0x586B0115, 230, -110, -60),
            (0x586B0116, 230, -120, -60),
            (0x586B0117, 230, -130, -60),
            (0x586B0118, 230, -140, -60),
            (0x586B0119, 240, -120, -60),
            (0x586B011A, 240, -130, -60),
            (0x586B011E, 200, -120, -54),
            (0x586B011F, 200, -130, -54),
            (0x586B0123, 210, -110, -54),
            (0x586B0124, 210, -120, -54),
            (0x586B0125, 210, -130, -54),
            (0x586B0126, 210, -140, -54),
            (0x586B012A, 220, -110, -54),
            (0x586B012B, 220, -120, -54),
            (0x586B012C, 220, -130, -54),
            (0x586B012D, 220, -140, -54),
            (0x586B012E, 220, -150, -54),
            (0x586B012F, 220, -160, -54),
            (0x586B0130, 230, -110, -54),
            (0x586B0131, 230, -120, -54),
            (0x586B0132, 230, -130, -54),
            (0x586B0133, 230, -140, -54),
            (0x586B0134, 240, -120, -54),
            (0x586B0135, 240, -130, -54),
            (0x586B0137, 200, -120, -48),
            (0x586B0138, 200, -130, -48),
            (0x586B013A, 210, -110, -48),
            (0x586B013B, 210, -120, -48),
            (0x586B013C, 210, -130, -48),
            (0x586B013D, 210, -140, -48),
            (0x586B013F, 220, -110, -48),
            (0x586B0140, 220, -120, -48),
            (0x586B0141, 220, -130, -48),
            (0x586B0142, 220, -140, -48),
            (0x586B0143, 220, -150, -48),
            (0x586B0144, 220, -160, -48),
            (0x586B0145, 230, -110, -48),
            (0x586B0146, 230, -120, -48),
            (0x586B0147, 230, -130, -48),
            (0x586B0148, 230, -140, -48),
            (0x586B014A, 240, -120, -48),
            (0x586B014B, 240, -130, -48),
            (0x586B014D, 200, -120, -42),
            (0x586B014E, 200, -130, -42),
            (0x586B014F, 210, -110, -42),
            (0x586B0150, 210, -120, -42),
            (0x586B0151, 210, -130, -42),
            (0x586B0152, 210, -140, -42),
            (0x586B0153, 220, -110, -42),
            (0x586B0154, 220, -120, -42),
            (0x586B0155, 220, -130, -42),
            (0x586B0156, 220, -140, -42),
            (0x586B0157, 230, -110, -42),
            (0x586B0158, 230, -120, -42),
            (0x586B0159, 230, -130, -42),
            (0x586B015A, 230, -140, -42),
            (0x586B015B, 240, -120, -42),
            (0x586B015C, 240, -130, -42),
        };

        private static readonly int[] layerOrigins = { -60, -54, -48, -42 };

        /// <summary>
        /// The hall cell containing (x, y, z), or 0 when the point is outside the cross. Pure: it reads the table above,
        /// not the dat (MatronRoomGeometryTests proves the table against the dat). A point exactly on a cell seam
        /// resolves to the cell on its higher-coordinate side after rounding; the layout never places one there.
        /// </summary>
        public static uint HallCell(float x, float y, float z)
        {
            var cx = (int)Math.Round(x / 10.0, MidpointRounding.AwayFromZero) * 10;
            var cy = (int)Math.Round(y / 10.0, MidpointRounding.AwayFromZero) * 10;
            var layer = layerOrigins[0];

            foreach (var origin in layerOrigins)
                if (z >= origin)
                    layer = origin;

            foreach (var c in hallCells)
                if (c.X == cx && c.Y == cy && c.Z == layer)
                    return c.Id;

            return 0;
        }

        /// <summary>True when (x, y) is on the room's floor plan: the cross of core, west and east wings and the south nub.</summary>
        public static bool InFloorPlan(float x, float y)
            => (x >= 205f && x <= 235f && y >= -145f && y <= -105f)
            || (x >= 195f && x <= 245f && y >= -135f && y <= -115f)
            || (x >= 215f && x <= 225f && y >= -165f && y <= -145f);
    }

    /// <summary>
    /// Player-facing lines (orchestrator-written copy). Each constant names its purpose.
    /// </summary>
    public static class MatronLines
    {
        public const string LineStart = "The Drift Olthoi Matron stirs. Her mandibles click, and the hive answers.";

        /// <summary>Taunt said right after the start narration: the Matron tells the intruder they do not belong in the Drift.</summary>
        public const string LineTauntIntrude = "A voice like grinding chitin presses into your mind: \"Soft one. You crossed into the Drift as though it would bear your weight. It will not. This is the hive's realm, and you do not belong to it.\"";

        /// <summary>Taunt said after the intrusion taunt: everything that strays into the hive feeds her children.</summary>
        public const string LineTauntFeed = "\"No matter. Nothing that strays into my hive is wasted. My children are hungry, and you will feed them all the same.\"";

        /// <summary>Taunt said right after the sentinel line, only when the sentinels actually stood.</summary>
        public const string LineTauntSentinels = "The Matron's voice scrapes through your thoughts: \"You break what I make. I make more. The hive is patient, soft one. You are not.\"";

        /// <summary>Taunt said right after the rumble line, as the finale begins.</summary>
        public const string LineTauntBurn = "The Matron's voice is a scream inside your skull: \"If the hive must burn to be rid of you, then let it burn!\"";

        /// <summary>A health threshold is crossed and a flow opens.</summary>
        public const string LineFlowOpens = "The Matron shrieks at a vent above. Acid begins to pour into the chamber!";

        public const string LineSeal = "The door grinds shut. The flow of acid stops.";

        public const string LineDrainUsed = "The grate gives way and the acid rushes down into the dark.";

        /// <summary>The drain is refused because the acid is too shallow (under the minimum depth).</summary>
        public const string LineDrainEmpty = "There is too little acid here to drain.";

        /// <summary>The drain is refused because it was already used this fight.</summary>
        public const string LineDrainSpent = "The drain is choked with sludge and will not open again.";

        /// <summary>A threshold is crossed while her wave adds live: she hardens and holds until they are dead.</summary>
        public const string LineBroodGuards = "The Matron's carapace hardens. While her brood lives, she cannot be harmed.";

        /// <summary>The final wave (the two great sentinels) spawns.</summary>
        public const string LineSentinels = "Two great sentinels tear free of the hive walls to guard their Matron.";

        public const string LineRumble = "The Matron turns on the hive itself and screams. The walls shudder, and every vent tears open at once!";

        public const string LineLavaDoor = "The rock shatters. Fire answers from the deep, and lava floods the northern vent, burning the acid away.";

        public const string LineWin = "The Matron is consumed by the flames. The hive falls silent.";
    }
}
