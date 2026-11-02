using System.Collections.Generic;

using ACE.Server.HazardMechanics;

namespace ACE.Server.Pinnacles.Grael
{
    /// <summary>
    /// Grael's points, wcids and spell lists, all in one place. MEASURED values come from the arena content files;
    /// the spawn and entry points are proven by PinnacleArenaGeometryTests against the room AND its floor Ward Planes.
    /// </summary>
    public static class GraelData
    {
        public const string Kind = "grael";

        public const ushort Landblock = 0x0069;

        public const uint SealWcid = 1006900;

        public const uint GraelWcid = 1006904;

        public const uint RedWcid = 1006905;

        public const uint GreenWcid = 1006906;

        public const uint BlueWcid = 1006907;

        public const uint HeadWcid = 1006909;

        // ---- spawn tags (also the colour keys of the P3 / P5 variants) ----
        public const string GraelTag = "grael";

        public const string RedTag = "red";

        public const string GreenTag = "green";

        public const string BlueTag = "blue";

        // ---- measured ----
        public static readonly PinnaclePoint ArrivalPoint = new PinnaclePoint(0x0069015A, 41.93f, -52.50f, -6.00f);

        public static readonly PinnaclePoint HeadPoint = new PinnaclePoint(0x00690113, 10.6544f, -20.1610f, -47.1039f, 0.704729f, 0.057939f, 0.057939f, 0.704729f);

        // ---- spawn points: z is the WALKING SURFACE + 0.05 ----
        // The walking surface is NOT the room's own geometry: it is the 4 x 5 grid of floor Ward Planes (wcid 1006900,
        // Content/dungeons/pinnacle_arena.sql, origin z -47.85, top -47.75 nominal; the placement settles a body on it at
        // -47.745). A body whose origin sits exactly at -47.75 starts inside that plate and the placement refuses it
        // (NoValidPosition), so AddPhysicsObj fails: that is why every encounter creature failed to spawn in the
        // 2026-10-19 playtest. `cells --spawncheck` alone passed -47.75, because it loads the dat geometry only and never
        // the landblock's Ward Plane rows. PinnacleArenaGeometryTests places those plates and proves every point below
        // (control: -47.75 is refused with them present). -47.70 spawns and settles onto the plate at -47.745.
        public const float SpawnZ = -47.70f;

        // ---- the floor plates (Content/dungeons/pinnacle_arena.sql, rows 0x7006900B-0x7006901E and the header's BOSS ROOM
        // ---- note): 20 flat Ward Planes, origin z -47.85, each spanning z origin-0.05 to +0.10, over the east block, bridge and
        // ---- west block (tops z -48) and the pit (floor z -54) ----

        /// <summary>The plates' combined extent: x -4.943 to 34.943, y -34.959 to -5.041 (the room is x -5..35, y -5..-35).</summary>
        public const float PlatesMinX = -4.943f;

        public const float PlatesMaxX = 34.943f;

        public const float PlatesMinY = -34.959f;

        public const float PlatesMaxY = -5.041f;

        /// <summary>The plates' bottom (origin -47.85 - 0.05). A body origin below it inside the rectangle is under the floor.</summary>
        public const float PlateBottomZ = -47.90f;

        /// <summary>The lift's lower limit: 1 m under the pit floor (z -54), so a body standing on the pit floor is still lifted.</summary>
        public const float PlateLiftLowestZ = -55.0f;

        /// <summary>Where a body under the plates is put: the spawn z, proven to settle on the plate top (-47.745).</summary>
        public const float PlateLiftZ = SpawnZ;

        /// <summary>
        /// The escalation teleport's z: <see cref="FightEntryZ"/>, past the placement's step-down reach. Not
        /// <see cref="PlateLiftZ"/>: a client placed at -47.70 with no plates loaded is stepped down to -47.995, the stuck z
        /// (PinnacleArenaGeometryTests.StuckZ_*), so a teleport there could recreate the bug.
        /// </summary>
        public const float PlateTeleportZ = FightEntryZ;

        public static readonly PinnaclePlateFloor PlateFloor = new PinnaclePlateFloor(PlatesMinX, PlatesMaxX, PlatesMinY, PlatesMaxY, PlateBottomZ, PlateLiftLowestZ, PlateLiftZ, PlateTeleportZ);

        /// <summary>
        /// The fight entry's z: 1.5 m above <see cref="SpawnZ"/> (accepted 2026-10-22), so a re-entering client is placed in
        /// the air past the placement's step-down reach and falls onto the plates. SpawnZ itself stays: it places the
        /// creatures. Measured 2026-10-22 on the human setup at (32.5, -20) in 0x00690119, with NO plates loaded (dat geometry
        /// only): from z -46.70 the placement still steps the body down onto the east block top (-47.995); from -46.45 up it
        /// keeps its z; so -46.20 is 0.25 m past that boundary.
        /// `cells --landblock=0x0069 --spawncheck=32.5,-20,-46.20 --cell=0x00690119 --setup=0x02000001` reports
        /// "VERDICT: SPAWNS in 0x00690119 at (32.5, -20, -46.2)" (height kept). WITH the 20 plates present, a human dropped
        /// from here settles on the plate top (-47.745) in 0x00690119 (PinnacleArenaGeometryTests
        /// FightEntryPoint_IsInItsCellUnderTheCeiling_AndAHumanDroppedFromItSettlesOnThePlates), and without them it keeps
        /// its z at placement (StuckZ_IsTheBlockTopUnderAPlate_AndTheOldEntryStepsDownToIt_WithoutPlates). The raise only
        /// keeps placement from stepping the body down; gravity still drops it afterwards, so a body that ends up under the
        /// plates anyway is caught by the lift (<see cref="PlateFloor"/>).
        /// </summary>
        public const float FightEntryZ = -46.20f;

        public static readonly PinnaclePoint FightEntryPoint = new PinnaclePoint(0x00690119, 32.5f, -20.0f, FightEntryZ);

        public static readonly PinnaclePoint SealPoint = new PinnaclePoint(0x00690119, 34.85f, -20.0f, -47.62f, 0.707107f, 0f, 0f, 0.707107f);

        public static readonly PinnaclePoint GraelSpawn = new PinnaclePoint(0x00690116, 20.0f, -20.0f, SpawnZ);

        public static readonly PinnaclePoint RedSpawn = new PinnaclePoint(0x00690114, 10.0f, -28.0f, SpawnZ);

        public static readonly PinnaclePoint GreenSpawn = new PinnaclePoint(0x0069011A, 30.0f, -28.0f, SpawnZ);

        public static readonly PinnaclePoint BlueSpawn = new PinnaclePoint(0x00690115, 20.0f, -10.0f, SpawnZ);

        /// <summary>
        /// The reward cache and the Knot Exit, on the head's platform: the west block (x 5 to 15, y -25 to -15, solid top
        /// z -48, Content/dungeons/pinnacle_arena.sql), east of HeadPoint on the side the head faces. Both objects are
        /// ethereal with gravity, so they pass through the floor Ward Planes; anywhere off the three blocks they fell to
        /// the pit floor (z -54) when they were stood at the owner (playtest 2026-10-22). Here they come to rest on the
        /// block top. PinnacleArenaGeometryTests proves both against the real arena geometry.
        /// </summary>
        public static readonly PinnaclePoint CachePoint = new PinnaclePoint(0x00690113, 13.0f, -18.0f, SpawnZ);

        public static readonly PinnaclePoint ReturnPortalPoint = new PinnaclePoint(0x00690113, 13.0f, -22.5f, SpawnZ);

        /// <summary>The three variants in a fixed order, with their wcids and spawn points. Declared AFTER the points it reads (static init is textual).</summary>
        public static readonly IReadOnlyList<(string Tag, uint Wcid, PinnaclePoint At)> Variants = new[]
        {
            (RedTag, RedWcid, RedSpawn),
            (GreenTag, GreenWcid, GreenSpawn),
            (BlueTag, BlueWcid, BlueSpawn),
        };

        /// <summary>
        /// The nine P3 variant points (round 2-4 waves take the first <see cref="P3WaveSize"/>, round 5 all nine), each at
        /// <see cref="SpawnZ"/> over one of the 20 floor Ward Planes (the cell is that plate's), the nearest (RedSpawn) about
        /// 7.9 m from the head's collision column at HeadPoint. PinnacleArenaGeometryTests spawns the real body at every one.
        /// </summary>
        public static readonly IReadOnlyList<PinnaclePoint> VariantPoints = new[]
        {
            RedSpawn,
            GreenSpawn,
            BlueSpawn,
            new PinnaclePoint(0x00690116, 20.0f, -20.0f, SpawnZ),
            new PinnaclePoint(0x0069010F, 0.0f, -11.0f, SpawnZ),
            new PinnaclePoint(0x00690111, 0.0f, -28.0f, SpawnZ),
            new PinnaclePoint(0x00690112, 10.0f, -6.0f, SpawnZ),
            new PinnaclePoint(0x00690117, 20.0f, -28.0f, SpawnZ),
            new PinnaclePoint(0x00690118, 30.0f, -11.0f, SpawnZ),
        };

        /// <summary>P3 rounds 2, 3 and 4 each spawn this many of one colour.</summary>
        public const int P3WaveSize = 5;

        /// <summary>P3 round 5 spawns this many of each colour.</summary>
        public const int P3FinalEach = 3;

        /// <summary>The colour each P3 wave round spawns, rounds 2, 3, 4 in order (owner ruling 2026-10-20: Blue, Red, Green).</summary>
        public static readonly IReadOnlyList<(string Tag, uint Wcid)> P3WaveColours = new[]
        {
            (BlueTag, BlueWcid),
            (RedTag, RedWcid),
            (GreenTag, GreenWcid),
        };

        /// <summary>The P3 spawn tag of the <paramref name="index"/>-th (1-based) variant of a colour: "blue-1".</summary>
        public static string P3Tag(string colour, int index) => $"{colour}-{index}";

        /// <summary>
        /// The head's face is on its local -Y while FaceToward points local +Y at the target, so the face is turned
        /// half a circle (W3 note; the content rotation faces the head +X).
        /// </summary>
        public const float HeadFaceYawOffset = (float)System.Math.PI;

        /// <summary>
        /// The head's cast height (PinnacleScriptData.HeadCastHeight), metres above HeadPoint. Its collision column is
        /// 6.87 m tall in a room whose ceiling is 5.05 m above HeadPoint (-42.05 vs -47.10), and the retail launch puts a
        /// bolt at 2/3 of that height and an arc at all of it: the 2026-10-20 playtest's bolts were spent on the ceiling
        /// at the head (WorldObject.IsProjectileVisible failed against the above-ceiling eye point) and its arcs spawned
        /// outside every cell. 3.6 m launches bolts and streaks at 2.4 m and arcs at 3.6 m, mid face, below the ceiling.
        /// PinnacleArenaGeometryTests.HeadCasts_* proves every head spell on the real arena geometry.
        /// </summary>
        public const float HeadCastHeight = 3.6f;

        // ---- head spells: Incantation of Flame Bolt / Flame Arc / Flame Streak, Cassius' Ring of Fire II (round 3);
        // ---- every phase adds Incantation of Fire Vulnerability Other (round 6, owner ask 2026-10-20) ----
        public static readonly IReadOnlyList<uint> P4Spells = new uint[] { 4439, 4423, 4440, 6191 };

        /// <summary>Incantation of Fire Vulnerability Other. Not a projectile, so the arena launch-geometry test covers only <see cref="P4Spells"/>.</summary>
        public const uint HeadVulnSpell = 4481;

        /// <summary>
        /// One head cast counter runs across the WHOLE fight, in every phase that casts (P1, P3, P4, P5): the first head
        /// cast of the fight and every Nth after it (casts 1, 6, 11, ...) is <see cref="HeadVulnSpell"/>; the rest draw from
        /// <see cref="P4Spells"/>.
        /// </summary>
        public const int HeadVulnEvery = 5;

        // ---- tiers (owner ruling 2026-10-20): the body is the Boss, the variants Elite, the P5 head the Head ----
        public const PinnacleTier GraelTier = PinnacleTier.Boss;

        public const PinnacleTier VariantTier = PinnacleTier.Elite;

        /// <summary>The head is 1.5 x Boss health at Boss damage, so it stays the toughest creature in the room at every Knot level.</summary>
        public const PinnacleTier HeadTier = PinnacleTier.Head;

        public static PinnacleScriptData Script() => new PinnacleScriptData
        {
            Landblock = Landblock,
            ArrivalPoint = ArrivalPoint,
            FightEntryPoint = FightEntryPoint,
            SealWcid = SealWcid,
            SealPoints = new[] { SealPoint },
            HeadWcid = HeadWcid,
            HeadPoint = HeadPoint,
            HeadTier = HeadTier,
            HeadFaceYawOffset = HeadFaceYawOffset,
            HeadCastHeight = HeadCastHeight,
            CachePoint = CachePoint,
            ReturnPortalPoint = ReturnPortalPoint,
            HazardRegionPreset = HazardRegionPresets.PinnacleName,
            PlateFloor = PlateFloor,
        };

        // ---- dialogue ----
        public const string LineStart = "You climbed all this way to hear a dead thing speak. Then hear this: the Unsung do not stay buried.";

        public const string LineP2 = "The embers remember where you stand.";

        public const string LineP3 = "Remember the colours, or be unmade by them.";

        public const string LineMemoryFailed = "Your protections unravel.";

        /// <summary>P3 round 5 (no memory: three of each colour, killable at once).</summary>
        public const string LineP3FinalRound = "No more games. All my colours, all at once.";

        /// <summary>
        /// The script names each memory round itself (the hazard run is player-facing and says no round text of its own).
        /// A failed round repeats under the same number.
        /// </summary>
        public static string LineMemoryRound(int round, int of) => $"Round {round} of {of}.";

        public const string LineP4 = "Run, little thing. Run until you cannot.";

        public const string LineP5 = "Enough. Face me yourself.";

        public const string LineHeadDied = "The song... ends...";
    }
}
