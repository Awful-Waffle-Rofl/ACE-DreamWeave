using System;
using System.Collections.Generic;
using System.Linq;

namespace ACE.Server.Pvp.Battlegrounds
{
    /// <summary>
    /// One invisible barrier piece that seals a pen doorway (Docs/Pvp/BATTLEGROUNDS.md "Pen seals").
    /// <see cref="CellId"/> is the FULL cell id (landblock in the high 16 bits); X, Y, Z are landblock-local;
    /// the rotation is a pure yaw carried as (W, Z).
    /// </summary>
    public sealed record BattlegroundSealPiece(uint Wcid, uint CellId, float X, float Y, float Z, float RotationW, float RotationZ);

    /// <summary>
    /// One place an Attack/Defend crystal can stand (Docs/Pvp/ATTACK-DEFEND.md "Crystals"). <see cref="CellLow"/> is the LOW 16 bits
    /// of the cell id (the landblock supplies the high bits), X, Y, Z are landblock-local, and <see cref="Name"/> is the player-facing
    /// room name alerts quote ("Great Hall"). <see cref="Hint"/> is an optional short owner-facing description of where the room is;
    /// when set, the role, status and alert lines use it in place of the compass word (<see cref="BattlegroundLayout.CompassFor"/> stays
    /// the fallback for a site without one).
    /// </summary>
    public sealed record BattlegroundCrystalSite(string Name, ushort CellLow, float X, float Y, float Z, string Hint = null);

    /// <summary>
    /// A battleground map's authored layout: per-team spawn lists, per-team pen, pen seal pieces and the KOTH
    /// zone centre. Team index 0 is the west side, 1 the east. Reuses <see cref="PvpSpawnPoint"/> for spawns and
    /// pens so <see cref="ArenaSpawnPosition"/>-style position building applies unchanged. The zone radius and
    /// height tolerance come from settings, not from here.
    /// </summary>
    public sealed record BattlegroundLayout(
        string MapKey,
        uint LandblockId,
        ushort RealmId,
        IReadOnlyList<IReadOnlyList<PvpSpawnPoint>> TeamSpawns,
        IReadOnlyList<PvpSpawnPoint> TeamPens,
        IReadOnlyList<BattlegroundSealPiece> Seals,
        float ZoneX,
        float ZoneY,
        float ZoneZ)
    {
        /// <summary>The player-facing name. Falls back to <see cref="MapKey"/> when not set.</summary>
        public string DisplayName
        {
            get => displayName ?? MapKey;
            init => displayName = value;
        }

        private readonly string displayName;
        /// <summary>
        /// Start gates (Docs/Pvp/BATTLEGROUNDS.md "Start gates"): barrier pieces that close each team's start room for the
        /// Countdown. Placed at staging with the pen seals (the match does not dispatch until they are in) and REMOVED when the match
        /// goes Live, or on any teardown, whichever comes first. Kept apart from <see cref="Seals"/>, which stay for the whole match.
        /// Empty means players are not held in the start room (any map that does not declare them). King of the Hill declares five per room; Attack/Defend one at each spawn room's only door.
        /// </summary>
        public IReadOnlyList<BattlegroundSealPiece> StartGates { get; init; } = Array.Empty<BattlegroundSealPiece>();

        /// <summary>
        /// The wcid of the King of the Hill zone marker (Docs/Pvp/BATTLEGROUNDS.md "Zone markers"); 0 means the map
        /// places no markers. v1 rings the zone in this one colour. It is the NEUTRAL colour by design: a later
        /// per-team or per-capture-state colour adds sibling wcids beside it (one init property each, 0 = fall back to
        /// this one) and passes the chosen wcid to <see cref="BattlegroundZoneMarkers.Plan(BattlegroundLayout, KothZone, double, uint, double)"/>,
        /// so neither this property nor the planner changes shape.
        /// </summary>
        public uint ZoneMarkerWcid { get; init; }

        /// <summary>
        /// The low 16 bits of a cell near the KOTH zone, carried in each planned marker's CellId as
        /// (<see cref="LandblockId"/> &lt;&lt; 16) | this. It is NOT the cell every marker stands in: on bg_016c the zone
        /// centre sits on a corner shared by four cells and most ring points fall in neighbours of this one. The live
        /// placement re-resolves each marker's own cell from its point (AdjustCell), so in practice this field only
        /// carries the landblock; it must still name a real cell of the landblock.
        /// </summary>
        public ushort ZoneCellLow { get; init; }

        /// <summary>
        /// The mirrored hill-site pairs a moving hill can go to (Docs/Pvp/BATTLEGROUNDS.md "Moving hill"), pair 1 first, each
        /// given by its west member; the east member is derived by <see cref="SiteFor"/> as the exact x-mirror about
        /// <see cref="ZoneX"/>, so the two are equidistant from the two team spawns by construction. Empty means the hill never moves.
        /// </summary>
        public IReadOnlyList<KothSitePair> ZonePairs { get; init; } = Array.Empty<KothSitePair>();

        /// <summary>
        /// The mode keys (<see cref="BattlegroundModes"/>) this map can host. A mode's map pool is DERIVED from this list
        /// (<see cref="BattlegroundModes.MapPoolFor"/>), never hand-listed, so a map joins a mode by naming it here. Empty means no mode.
        /// </summary>
        public IReadOnlyList<string> Modes { get; init; } = Array.Empty<string>();

        /// <summary>
        /// Where an Attack/Defend crystal may stand, in the order the count setting takes them (the first N are used). Empty for a map
        /// that does not host Attack/Defend. A site's alert compass word is taken from <see cref="CompassFor"/>.
        /// </summary>
        public IReadOnlyList<BattlegroundCrystalSite> CrystalSites { get; init; } = Array.Empty<BattlegroundCrystalSite>();

        /// <summary>How many crystals a match plays with when pvp_bg_ad_crystal_count is 0 (the map default); never above <see cref="CrystalSites"/>.Count.</summary>
        public int DefaultCrystalCount { get; init; }

        /// <summary>
        /// The wcid of the Attack/Defend crystal placed on every planned site (Docs/Pvp/ATTACK-DEFEND.md "Crystals"); 0 for a map that
        /// does not host Attack/Defend. Placement is strict, so an A/D match on a map whose crystal wcid is 0 cancels before it starts.
        /// </summary>
        public uint CrystalWcid { get; init; }

        /// <summary>
        /// Per team index (the same order as <see cref="TeamSpawns"/>), the FULL cell ids (landblock in the high 16 bits) of the floor cells of
        /// that team's spawn room. Protection a player is granted (match start, respawn) does not expire while they stand in one of their own
        /// team's cells, and starts its window when they leave (Docs/Pvp/BATTLEGROUNDS.md "Spawn protection"). Empty (or a missing or empty
        /// entry for a team) means plain N seconds, exactly as before. List standable floor cells only, never a ceiling cap.
        /// </summary>
        public IReadOnlyList<IReadOnlyList<uint>> SpawnRoomCells { get; init; } = Array.Empty<IReadOnlyList<uint>>();

        /// <summary>
        /// Per team index (the same order as <see cref="SpawnRoomCells"/>), the footprint of that team's room: the x/y rectangles its floor cells
        /// cover and the z band a standing player's feet are in. Room membership requires the reported cell AND this footprint, because both
        /// are client-reported (see <see cref="SpawnRoomArea"/>). A missing or empty entry means the cell alone decides.
        /// </summary>
        public IReadOnlyList<SpawnRoomFootprint> SpawnRoomFootprints { get; init; } = Array.Empty<SpawnRoomFootprint>();

        /// <summary>The team's room as the protection rule uses it (cells plus footprint), or null when the team has no room. Built per call.</summary>
        public SpawnRoomArea SpawnRoomAreaFor(int teamIndex)
        {
            var cells = SpawnRoomSetFor(teamIndex);

            if (cells == null)
                return null;

            var footprint = teamIndex >= 0 && teamIndex < SpawnRoomFootprints.Count ? SpawnRoomFootprints[teamIndex] : null;

            return footprint == null || footprint.Rects == null || footprint.Rects.Count == 0
                ? new SpawnRoomArea(cells)
                : new SpawnRoomArea(cells, footprint.Rects, footprint.ZMin, footprint.ZMax);
        }

        /// <summary>
        /// The spawn-room cells of <paramref name="teamIndex"/> as a new set, or null when that team has none. Built per call (a handful of
        /// cells, once per binding publish) rather than cached on the record, because a record's equality covers every field.
        /// </summary>
        public IReadOnlySet<uint> SpawnRoomSetFor(int teamIndex)
        {
            var cells = teamIndex >= 0 && teamIndex < SpawnRoomCells.Count ? SpawnRoomCells[teamIndex] : null;

            return cells == null || cells.Count == 0 ? null : new HashSet<uint>(cells);
        }


        /// <summary>
        /// The compass word for a crystal site: <see cref="KothHillSchedule.Compass"/> of the site's offset from (<see cref="ZoneX"/>, <see cref="ZoneY"/>),
        /// the map centre the KOTH hill moves already name their directions from. A map with no King of the Hill zone sets ZoneX and ZoneY
        /// to the point its crystal compass words are measured from (bg_003c: the centroid of its three sites).
        /// </summary>
        public string CompassFor(BattlegroundCrystalSite site) => KothHillSchedule.Compass(site.X - ZoneX, site.Y - ZoneY);

        /// <summary>
        /// The (x, y) of a hill site: the centre (ZoneX, ZoneY) for pair 0, else pair k's west point, or its east mirror
        /// (2 x ZoneX - west x, same y). Pure; an out-of-range pair falls back to the centre.
        /// </summary>
        public (float X, float Y) SiteFor(KothSiteChoice site)
        {
            if (site.Pair < 1 || site.Pair > ZonePairs.Count || site.Side == KothSide.Centre)
                return (ZoneX, ZoneY);

            var pair = ZonePairs[site.Pair - 1];

            return site.Side == KothSide.West ? (pair.WestX, pair.Y) : (2f * ZoneX - pair.WestX, pair.Y);
        }

        /// <summary>The spawn list for a team index, or an empty list when the index is out of range. Never null.</summary>
        public IReadOnlyList<PvpSpawnPoint> SpawnsFor(int teamIndex)
            => teamIndex >= 0 && teamIndex < TeamSpawns.Count ? TeamSpawns[teamIndex] : Array.Empty<PvpSpawnPoint>();

        /// <summary>The pen for a team index, or null when the index is out of range.</summary>
        public PvpSpawnPoint PenFor(int teamIndex)
            => teamIndex >= 0 && teamIndex < TeamPens.Count ? TeamPens[teamIndex] : null;
    }
}