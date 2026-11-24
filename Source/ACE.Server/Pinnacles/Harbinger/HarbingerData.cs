using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

using ACE.Entity.Enum;
using ACE.Server.HazardMechanics;
using ACE.Server.Realms;
using ACE.Server.WorldEvents;

namespace ACE.Server.Pinnacles.Harbinger
{
    /// <summary>The four elements, in fight order: P1 Acid, P2 Lightning, P3 Frost, P4 Fire.</summary>
    public enum HarbingerElement { Acid, Lightning, Frost, Fire }

    /// <summary>
    /// Everything that differs between the four elements, as data (fan-out invariant: the script has one code path).
    /// <see cref="Tag"/> names the element's hazard run; the other tags name its spawns and prop. Spells are Incantation
    /// war spells (projectile, arc, streak) and the element's Vulnerability Other. <see cref="AttuneSpell"/> is the
    /// element's Legendary Ward, granted to the owner when the element's crystal breaks (round 2 B1, the attunement).
    /// </summary>
    public sealed record HarbingerElementDef(
        HarbingerElement Element,
        string Tag,
        string SummonTag,
        string GuardTag,
        string CrystalTag,
        string PortalTag,
        IReadOnlyList<uint> WarSpells,
        uint VulnSpell,
        uint AttuneSpell,
        uint CrystalWcid,
        uint GuardianWcid,
        uint SummonWcid,
        DamageType DamageType,
        WorldEventHazardVisuals Visuals);

    /// <summary>
    /// One of the four alcoves, high above the chamber (design section 3): the points the owner, the crystal and the two
    /// guardians use, and the floor portal that takes the owner up. Derived from <see cref="Centre"/> and
    /// <see cref="Outward"/> alone by <see cref="HarbingerLayout"/>. Every alcove point is in <see cref="Cell"/>.
    /// <see cref="WallCells"/> is the arm well's cell stack under the alcove's doorway, one per <see cref="HarbingerLayout.WallCellLayer"/>
    /// metres from the chamber floor up (measured), which <see cref="HarbingerLayout.WallStrip"/> places the wall strip in.
    /// </summary>
    public sealed record HarbingerAlcove(
        HarbingerElement Element,
        string Name,
        Vector2 Outward,
        Vector2 Centre,
        uint Cell,
        uint PortalWcid,
        PinnaclePoint Stand,
        PinnaclePoint Crystal,
        IReadOnlyList<PinnaclePoint> Guards,
        PinnaclePoint Portal,
        IReadOnlyList<uint> WallCells);

    /// <summary>
    /// The Harbinger's Lair, waves and wcids in one place (landblock 0x00BC, the Knot copy). Points are MEASURED with
    /// `ACE.Content.Tools cells --landblock=0x00BC --at=x,y,z` (2026-11-15); HarbingerRoomGeometryTests proves every one
    /// against the real dat geometry. Yaw is degrees counter-clockwise from north (HarbingerLayout.Point).
    /// </summary>
    public static class HarbingerData
    {
        public const string Kind = "harbinger";

        public const ushort Landblock = 0x00BC;

        // ---- wcids (block 1008250-1008299, owner pinnacle-harbinger) ----
        public const uint HeadWcid = 1008250;

        /// <summary>The frost hail's small ball (1008263).</summary>
        public const uint HailstoneWcid = 1008263;

        /// <summary>The acid alcove's drip ball (1008281, round 3): the hailstone's shape in the acid Rolling Death's green, smaller.</summary>
        public const uint AcidDripWcid = 1008281;

        /// <summary>Reused hazard weenies: the lightning Storm Wisp (1006963) and the fire ball (1006966).</summary>
        public const uint StormWispWcid = 1006963;

        public const uint FireBallWcid = 1006966;

        public const PinnacleTier HeadTier = PinnacleTier.Boss;

        /// <summary>Crystal, guardians and summons are Elite (design 4.4, 4.5).</summary>
        public const PinnacleTier AddTier = PinnacleTier.Elite;

        /// <summary>
        /// The head casts every <see cref="CastEvery"/> casts the element's vulnerability: casts 1, 6, 11 ... (Grael's
        /// cadence, GraelData.HeadVulnEvery); the rest draw from its war spells.
        /// </summary>
        public const int CastEvery = 5;

        // ---- the element table ----
        public static readonly IReadOnlyList<HarbingerElementDef> Elements = new[]
        {
            // attune: Legendary Acid Ward 6080, Legendary Storm Ward 6079, Legendary Frost Ward 6083, Legendary Flame Ward 6082
            // (ace_world.spell, 2026-11-18; each a Float-mod enchantment of the element's resist; PinnacleOwnerBuffGrantTests.HarbingerAttuneSpells_* pins the dat)
            Def(HarbingerElement.Acid, "acid", new uint[] { 4433, 4421, 4432 }, 4473, 6080, 1008251, 1008255, 1008259, WorldEventHazardElement.Acid),
            Def(HarbingerElement.Lightning, "lightning", new uint[] { 4451, 4426, 4452 }, 4483, 6079, 1008252, 1008256, 1008260, WorldEventHazardElement.Lightning),
            Def(HarbingerElement.Frost, "frost", new uint[] { 4447, 4425, 4448 }, 4479, 6083, 1008253, 1008257, 1008261, WorldEventHazardElement.Frost),
            Def(HarbingerElement.Fire, "fire", new uint[] { 4439, 4423, 4440 }, 4481, 6082, 1008254, 1008258, 1008262, WorldEventHazardElement.Fire),
        };

        private static HarbingerElementDef Def(HarbingerElement element, string tag, uint[] war, uint vuln, uint attune, uint crystal, uint guardian, uint summon, WorldEventHazardElement visuals)
        {
            var v = WorldEventHazardElements.For(visuals);

            return new HarbingerElementDef(element, tag, "summon-" + tag, "guard-" + tag, "crystal-" + tag, "portal-" + tag, war, vuln, attune, crystal, guardian, summon, v.DamageType, v);
        }

        public static HarbingerElementDef Element(HarbingerElement element)
        {
            foreach (var e in Elements)
                if (e.Element == element)
                    return e;

            throw new ArgumentOutOfRangeException(nameof(element), element, "no such Harbinger element");
        }

        // ---- the room ----

        /// <summary>The fight entry and arrival: the south-east of the octagon floor, facing the Harbinger (45 degrees CCW from north, towards north-west).</summary>
        public static readonly PinnaclePoint ArrivalPoint = HarbingerLayout.Point(0x00BC0108, 28.5f, -28.5f, HarbingerLayout.FloorZ,
            HarbingerLayout.YawDegFacing(HarbingerLayout.ChamberCentre - new Vector2(28.5f, -28.5f)));

        public static readonly PinnaclePoint FightEntryPoint = ArrivalPoint;

        /// <summary>
        /// The Harbinger's spot: retail landblock_instance guid 1879818242 (20, -20, -35.985) in cell 0x00BC0104. Design choice: he faces the
        /// arrival point (yaw 225, from (ArrivalPoint - head) with the same YawDegFacing the stand yaws use); the retail row is yaw 0.
        /// Declared after ArrivalPoint, which its yaw reads.
        /// </summary>
        public static readonly PinnaclePoint HeadPoint = HarbingerLayout.Point(0x00BC0104, 20f, -20f, HarbingerLayout.HeadZ,
            HarbingerLayout.YawDegFacing(new Vector2(ArrivalPoint.X - 20f, ArrivalPoint.Y - -20f)));

        /// <summary>The cells of <see cref="SummonPoints"/>, measured: east, then counter-clockwise in 45 degree steps.</summary>
        private static readonly uint[] summonCells =
        {
            0x00BC0107, 0x00BC0106, 0x00BC0103, 0x00BC0100, 0x00BC0101, 0x00BC0102, 0x00BC0105, 0x00BC0108,
        };

        /// <summary>The summon ring: 8 floor points at 10 m round the Harbinger, 45 degrees apart from due east.</summary>
        public static readonly IReadOnlyList<PinnaclePoint> SummonPoints = Enumerable.Range(0, HarbingerLayout.SummonRingCount)
            .Select(i =>
            {
                var xy = HarbingerLayout.SummonXY(i);

                return new PinnaclePoint(summonCells[i], xy.X, xy.Y, HarbingerLayout.FloorZ);
            })
            .ToArray();

        /// <summary>
        /// Landblock_instance rows the Knot copy does not load: the retail Harbinger (36178, guid 1879818242) and the 10 minute
        /// linkable generator (7925, guid 1879818241), both at (20, -20, -35.985). The box takes the whole lair, alcoves
        /// included, and nothing else is placed on this landblock.
        /// </summary>
        public static readonly EphemeralRowExclusion ExcludeRows = new EphemeralRowExclusion(0x00BC, -10f, 50f, -50f, 10f, -40f, 10f);

        // ---- the alcoves (owner mapping 2026-11-15: Acid W, Lightning E, Frost S, Fire N) ----

        /// <summary>
        /// Element, name, outward, the alcove's cell (design section 3), the floor portal's cell (measured) and its wcid
        /// (1008264 W, 1008265 N, 1008266 S, 1008267 E), and the wall cells under the doorway, bottom layer first (z -36..-30,
        /// -30..-24, -24..-18, -18..-12, -12..-6, -6..0). MEASURED 2026-11-18 by a dat scan on the doorway's centre line and
        /// 1.7 m to either side (HarbingerWallStripGeometryTests): each layer is one cell from the chamber to the wall plane.
        /// </summary>
        private static readonly (HarbingerElement Element, string Name, Vector2 Outward, uint Cell, uint PortalCell, uint PortalWcid, uint[] WallCells)[] alcoveRows =
        {
            (HarbingerElement.Acid, "W", new Vector2(-1f, 0f), 0x00BC012D, 0x00BC0101, 1008264, new uint[] { 0x00BC0101, 0x00BC010A, 0x00BC0113, 0x00BC011C, 0x00BC0124, 0x00BC0129 }),
            (HarbingerElement.Lightning, "E", new Vector2(1f, 0f), 0x00BC0137, 0x00BC0107, 1008267, new uint[] { 0x00BC0107, 0x00BC0110, 0x00BC0119, 0x00BC0122, 0x00BC0128, 0x00BC012C }),
            (HarbingerElement.Frost, "S", new Vector2(0f, -1f), 0x00BC0134, 0x00BC0105, 1008266, new uint[] { 0x00BC0105, 0x00BC010E, 0x00BC0117, 0x00BC0120, 0x00BC0127, 0x00BC012B }),
            (HarbingerElement.Fire, "N", new Vector2(0f, 1f), 0x00BC0130, 0x00BC0103, 1008265, new uint[] { 0x00BC0103, 0x00BC010C, 0x00BC0115, 0x00BC011E, 0x00BC0125, 0x00BC012A }),
        };

        /// <summary>The four alcoves in element order (Acid, Lightning, Frost, Fire).</summary>
        public static readonly IReadOnlyList<HarbingerAlcove> Alcoves = alcoveRows.Select(Build).ToArray();

        private static HarbingerAlcove Build((HarbingerElement Element, string Name, Vector2 Outward, uint Cell, uint PortalCell, uint PortalWcid, uint[] WallCells) r)
        {
            var o = r.Outward;
            var stand = HarbingerLayout.StandXY(o);
            var crystal = HarbingerLayout.CrystalXY(o);
            var g0 = HarbingerLayout.GuardXY(o, 0);
            var g1 = HarbingerLayout.GuardXY(o, 1);
            var portal = HarbingerLayout.PortalXY(o);

            return new HarbingerAlcove(
                r.Element,
                r.Name,
                o,
                HarbingerLayout.Centre(o),
                r.Cell,
                r.PortalWcid,
                HarbingerLayout.Point(r.Cell, stand.X, stand.Y, HarbingerLayout.AlcoveZ, HarbingerLayout.YawDegFacing(-o)),
                new PinnaclePoint(r.Cell, crystal.X, crystal.Y, HarbingerLayout.AlcoveZ),
                new[]
                {
                    new PinnaclePoint(r.Cell, g0.X, g0.Y, HarbingerLayout.AlcoveZ),
                    new PinnaclePoint(r.Cell, g1.X, g1.Y, HarbingerLayout.AlcoveZ),
                },
                HarbingerLayout.Point(r.PortalCell, portal.X, portal.Y, HarbingerLayout.FloorZ, HarbingerLayout.YawDegFacing(-o)),
                r.WallCells);
        }

        public static HarbingerAlcove AlcoveOf(HarbingerElement element)
        {
            foreach (var a in Alcoves)
                if (a.Element == element)
                    return a;

            throw new ArgumentOutOfRangeException(nameof(element), element, "no such Harbinger element");
        }

        /// <summary>
        /// Every wcid the script spawns besides the head (the entry readiness check): the crystals, guardians and summons
        /// (1008251-1008262), the hailstone (1008263), the four alcove portals (1008264-1008267), the acid drip (1008281), the Storm Wisp, the fire ball
        /// the effect host, the acid fill's panel and wisp (the fill's wisps arg defaults to 16) and the Matron's acid flow panel
        /// (the wall strips, <see cref="HarbingerLayout.WallStrip"/>).
        /// </summary>
        public static IReadOnlyList<uint> RequiredWcids => Enumerable.Range(1008251, 17).Select(w => (uint)w)
            .Concat(new[] { AcidDripWcid, StormWispWcid, FireBallWcid, HazardDefaults.EffectHostWcid, HazardDefaults.PanelAcidWcid, HazardDefaults.FillAcidWispWcid, Matron.MatronData.AcidFlowWcid }).ToArray();

        /// <summary>
        /// The health multipliers of round 2 A7, split in round 4: <paramref name="multiplier"/> on the four crystals and
        /// <paramref name="headMultiplier"/> on the head alone, and nothing else listed (guardians, summons and every prop spawn at
        /// 1.0, PinnacleScriptData.HealthMultiplierFor).
        /// </summary>
        public static IReadOnlyDictionary<uint, double> HealthMultipliers(double multiplier, double headMultiplier)
        {
            var map = new Dictionary<uint, double> { [HeadWcid] = headMultiplier };

            foreach (var e in Elements)
                map[e.CrystalWcid] = multiplier;

            return map;
        }

        /// <summary>
        /// What the core spawns for the Harbinger: the head and nothing else. No seal (the copy is sealed, reached only by
        /// teleport, design section 3), so SealPoints stays empty. The fight's floor is a long drop from the alcoves, so the
        /// fall damage is waived inside this copy. <paramref name="healthMultiplier"/> is laid over the tier health of the
        /// four crystals and <paramref name="headMultiplier"/> over the head's (<see cref="HealthMultipliers"/>); every other spawn stays 1.0.
        /// </summary>
        public static PinnacleScriptData Script(double healthMultiplier, double headMultiplier) => new PinnacleScriptData
        {
            HealthMultipliers = HealthMultipliers(healthMultiplier, headMultiplier),
            Landblock = Landblock,
            ArrivalPoint = ArrivalPoint,
            FightEntryPoint = FightEntryPoint,
            HeadWcid = HeadWcid,
            RequiredWcids = RequiredWcids,
            HeadPoint = HeadPoint,
            HeadTier = HeadTier,
            SealPoints = Array.Empty<PinnaclePoint>(),
            HazardRegionPreset = HazardRegionPresets.HarbingerName,
            ExcludeRows = ExcludeRows,
            WaiveFallDamage = true,
        };
    }
}
