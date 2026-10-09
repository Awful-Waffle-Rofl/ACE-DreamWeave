using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Reflection;

using ACE.Common;
using ACE.Server.Managers;
using ACE.Server.WorldEvents;

using log4net;

namespace ACE.Server.MlTreasure
{
    /// <summary>
    /// Once-built, immutable store of the ML Treasure Hunt dig-site catalogue
    /// (Docs/Marae-Lassel/TREASURE-HUNT-PLAN.md sections 4-6). Loads
    /// Content/marae-lassel/dig-sites.tsv - emitted offline by
    /// Source/ACE.Content.Tools/Commands/SurfaceDigSitesCommand.cs, whose ToMapCoords is the single
    /// place the map-coordinate formula is written - and exposes a uniform random pick and a nearest
    /// lookup over it.
    ///
    /// Never throws: an unresolved folder, a missing/unreadable file, a malformed row, or an empty
    /// file all log once (WARN for a partial/degraded read, ERROR for an exception) and degrade to
    /// <see cref="Empty"/>, whose <see cref="Roll"/> and <see cref="Nearest"/> both return null. The
    /// server must still boot with this file absent (TREASURE-HUNT-PLAN.md section 6).
    /// </summary>
    public sealed class MlTreasureSiteStore
    {
        private static readonly ILog log = LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);

        /// <summary>One dig-site catalogue row. Only the fields the runtime needs are kept - the
        /// landblock/cell/local-coordinate columns exist in the TSV for offline tooling and human
        /// inspection, not for anything read here.</summary>
        public readonly struct Site
        {
            public float Ns { get; }
            public float Ew { get; }
            public bool BossOk { get; }

            /// <summary>Which of Marae Lassel's three level zones this site belongs to (owner-approved zone
            /// isolation design). Unknown for a catalogue row with no (or an unrecognised) zone column -
            /// the pre-zone 9-column catalogue shape, which every zone-aware lookup treats as unfiltered.</summary>
            public MlTreasureZone Zone { get; }

            public Site(float ns, float ew, bool bossOk, MlTreasureZone zone = MlTreasureZone.Unknown)
            {
                Ns = ns;
                Ew = ew;
                BossOk = bossOk;
                Zone = zone;
            }

            /// <summary>X=EastWest, Y=NorthSouth - the same axis order PositionExtensions.GetMapCoords
            /// returns, so a caller can compare this directly against a live position's map coords.</summary>
            public Vector2 MapCoords => new Vector2(Ew, Ns);
        }

        private static readonly MlTreasureSiteStore empty = new MlTreasureSiteStore(Array.Empty<Site>(), Array.Empty<Site>(), new HashSet<uint>(),
            new Dictionary<ushort, MlTreasureZone>());

        /// <summary>The empty store - every lookup returns null. Also what any load failure degrades to.</summary>
        public static MlTreasureSiteStore Empty => empty;

        /// <summary>The live, process-wide store. Empty until <see cref="Initialize"/> succeeds.</summary>
        public static MlTreasureSiteStore Instance { get; private set; } = empty;

        private readonly IReadOnlyList<Site> allSites;
        private readonly IReadOnlyList<Site> bossOkSites;

        /// <summary>Every catalogued terrain cell, keyed by <see cref="CellKey"/>.</summary>
        private readonly HashSet<uint> cells;

        /// <summary>Every landblock's zone, derived offline (MlTreasureZoneAnnotator) and carried in the
        /// catalogue's optional 10th column. A landblock absent from this map has no zoned row in the
        /// catalogue at all - <see cref="ZoneOf"/> answers Unknown for it, same as an unrecognised column.</summary>
        private readonly IReadOnlyDictionary<ushort, MlTreasureZone> landblockZones;

        /// <summary>Row count actually loaded (0 for an empty/degraded store).</summary>
        public int Count => allSites.Count;

        private MlTreasureSiteStore(IReadOnlyList<Site> allSites, IReadOnlyList<Site> bossOkSites, HashSet<uint> cells,
            IReadOnlyDictionary<ushort, MlTreasureZone> landblockZones)
        {
            this.allSites = allSites;
            this.bossOkSites = bossOkSites;
            this.cells = cells;
            this.landblockZones = landblockZones;
        }

        /// <summary>The zone this landblock's catalogue rows carry, or Unknown if it has none (never
        /// catalogued, or catalogued from a pre-zone 9-column TSV).</summary>
        public MlTreasureZone ZoneOf(ushort landblock) =>
            landblockZones.TryGetValue(landblock, out var zone) ? zone : MlTreasureZone.Unknown;

        /// <summary>The zone of the catalogue site NEAREST <paramref name="mapCoords"/> (X=EastWest,
        /// Y=NorthSouth), unfiltered by boss_ok or zone. Unknown for an empty/degraded store or a site with
        /// no zone data of its own.</summary>
        public MlTreasureZone ZoneAt(Vector2 mapCoords) => Nearest(mapCoords, bossOk: false)?.Zone ?? MlTreasureZone.Unknown;

        /// <summary>A terrain cell's key: the 16-bit landblock in the high half, cx * 8 + cy in the low half.</summary>
        private static uint CellKey(ushort landblock, int cx, int cy) => ((uint)landblock << 16) | (uint)(cx * 8 + cy);

        /// <summary>
        /// Whether the terrain cell (<paramref name="cx"/>, <paramref name="cy"/>) of
        /// <paramref name="landblock"/> is itself a catalogued dig site - i.e. whether it passed the SAME
        /// offline test that built the catalogue (SurfaceDigSitesCommand.IsDigSite: all four corners dry per
        /// SurfaceTerrain.IsWater, slope within the surfacecatalog walkability threshold, no building, and
        /// all four orthogonal neighbours passing too). The catalogue is a complete enumeration of every
        /// passing cell on the island, one row per cell, so membership IS that test; the server re-derives
        /// none of its thresholds and reads no terrain.
        ///
        /// Cell coordinates are the 24 m terrain cells, 0..7 on each axis. Out-of-range coordinates, and an
        /// empty or degraded store, answer false - the conservative direction, since the caller then picks
        /// a catalogued site instead.
        /// </summary>
        public bool IsCatalogueCell(ushort landblock, int cx, int cy)
        {
            if (cx < 0 || cx > 7 || cy < 0 || cy > 7)
                return false;

            return cells.Contains(CellKey(landblock, cx, cy));
        }

        // ---- pure lookups --------------------------------------------------------------------------

        /// <summary>A uniformly random site, restricted to boss_ok=1 rows when <paramref name="bossOk"/>
        /// is true. Null when the relevant list is empty - a degraded/empty store, or (in principle) a
        /// catalogue that happens to carry zero boss_ok rows.</summary>
        public Site? Roll(bool bossOk)
        {
            var list = bossOk ? bossOkSites : allSites;

            if (list.Count == 0)
                return null;

            return list[ThreadSafeRandom.Next(0, list.Count - 1)];
        }

        /// <summary>
        /// The cap on how far a dig site may be from the point the map was picked up (round 15 owner ruling):
        /// three map units, 720 m straight-line. It holds whenever a catalogue site, or a dig-able pickup point,
        /// is available; only a pickup that is itself undiggable with nothing in range falls back past it to the
        /// nearest site (see <see cref="PlaceNear"/>). ml_treasure_site_radius_metres ships at this value;
        /// the constant is the fallback a missing or unseeded key reads.
        /// </summary>
        public const float MaxSiteDistanceMetres = 3f * MlTreasureGeometry.MetresPerMapUnit;

        /// <summary>
        /// A random site drawn from those within <paramref name="radiusMapUnits"/> of
        /// <paramref name="nearMapCoords"/>, restricted to boss_ok rows when <paramref name="bossOk"/> is
        /// true - exactly the same filter <see cref="Roll"/> applies, and applied before the distance test
        /// rather than after, so a Relaria map can still never be pointed at a non-boss cell.
        ///
        /// DELIBERATELY NOT <see cref="Nearest"/>. Snapping to the single closest site would make every map
        /// dropped at one farming spot name the same hole. This draws uniformly from EVERY qualifying site
        /// inside the radius.
        ///
        /// ROUND 15: NO WIDENING AND NO ISLAND-WIDE FALLBACK. The radius is a hard cap (owner ruling: never
        /// more than 720 m from where the map was picked up), so this returns null when nothing qualifies -
        /// including for a non-positive or non-finite radius or reference point, where nothing CAN qualify -
        /// and <see cref="PlaceNear"/> decides what the site is instead. Before round 15 the radius
        /// doubled three times and then fell back to a draw across the whole island, which is how a map could
        /// point several kilometres away.
        ///
        /// <paramref name="pick"/> is the index draw over the candidates, as a seam for tests: it is handed
        /// the candidate COUNT and must return an index in [0, count). Null means the live RNG.
        /// </summary>
        public Site? RollNear(bool bossOk, Vector2 nearMapCoords, float radiusMapUnits, Func<int, int> pick = null,
            MlTreasureZone zone = MlTreasureZone.Unknown)
        {
            var list = bossOk ? bossOkSites : allSites;

            if (list.Count == 0)
                return null;

            if (!float.IsFinite(radiusMapUnits) || radiusMapUnits <= 0f
                || !float.IsFinite(nearMapCoords.X) || !float.IsFinite(nearMapCoords.Y))
            {
                return null;
            }

            pick = pick ?? (count => ThreadSafeRandom.Next(0, count - 1));

            var radiusSq = radiusMapUnits * radiusMapUnits;
            var candidates = 0;

            foreach (var site in list)
            {
                if (InZone(site, zone) && DistanceSq(site, nearMapCoords) <= radiusSq)
                    candidates++;
            }

            if (candidates == 0)
                return null;

            // Clamped rather than trusted: pick is a caller-supplied seam, and an out-of-range index would
            // otherwise walk off the end of the candidate scan and return null from a list that demonstrably
            // has a match in it.
            var index = Math.Clamp(pick(candidates), 0, candidates - 1);

            foreach (var site in list)
            {
                if (!InZone(site, zone) || DistanceSq(site, nearMapCoords) > radiusSq)
                    continue;

                if (index == 0)
                    return site;

                index--;
            }

            return null;
        }

        /// <summary>Zone isolation's own filter: unfiltered (true for every site) when <paramref name="zone"/>
        /// is Unknown - the "no zone data" case, which is what keeps a 9-column catalogue or a zone-blind
        /// caller working exactly as before zone isolation shipped. Otherwise true only for a site whose own
        /// zone matches.</summary>
        private static bool InZone(Site site, MlTreasureZone zone) => zone == MlTreasureZone.Unknown || site.Zone == zone;

        /// <summary>Where a map's dig site ended up, as decided by <see cref="PlaceNear"/>.</summary>
        public readonly struct Placement
        {
            public float Ns { get; }
            public float Ew { get; }

            /// <summary>True when no catalogue site was within range and the site IS the pickup point.</summary>
            public bool AtPickupPoint { get; }

            /// <summary>
            /// True when a boss (Relaria) map had to fall back to the pickup point. The caller must strip the
            /// boss variant: MlRelariaSpawner refuses any site that is not a boss_ok catalogue row
            /// (IsBossSite), so a boss map pointed at a bare pickup point could never be completed.
            /// </summary>
            public bool BossVariantDropped { get; }

            /// <summary>
            /// True when nothing was within range AND the pickup point is not itself a dig-able cell, so the
            /// NEAREST catalogue site was taken whatever its distance. The one case a site may lie beyond the
            /// radius.
            /// </summary>
            public bool BeyondRadius { get; }

            public Placement(float ns, float ew, bool atPickupPoint, bool bossVariantDropped, bool beyondRadius = false)
            {
                Ns = ns;
                Ew = ew;
                AtPickupPoint = atPickupPoint;
                BossVariantDropped = bossVariantDropped;
                BeyondRadius = beyondRadius;
            }

            /// <summary>X=EastWest, Y=NorthSouth - the axis order <see cref="Site.MapCoords"/> uses.</summary>
            public Vector2 MapCoords => new Vector2(Ew, Ns);
        }

        /// <summary>
        /// THE round 15 placement rule, shared by the drop path (MlTreasureDrop) and the lazy first-use path
        /// (TreasureMapHandler) so the two can never disagree. In order:
        ///
        ///   1. A random catalogue site within <paramref name="radiusMapUnits"/> of the pickup point
        ///      (<see cref="RollNear"/>). The radius is a hard cap WHILE such a site exists.
        ///   2. Otherwise the pickup point ITSELF (owner ruling) - but only when
        ///      <paramref name="pickupIsDigSite"/>: the pickup cell passes the same water/slope/building test
        ///      the catalogue was built with (<see cref="IsCatalogueCell"/>). A corpse can lie underwater or on
        ///      a slope no dig site would be accepted on, and that point is never used.
        ///   3. Otherwise the NEAREST catalogue site (boss_ok-filtered for a boss map), whatever its distance,
        ///      flagged <see cref="Placement.BeyondRadius"/>.
        ///
        /// Because the catalogue enumerates every dig-able cell, a dig-able pickup cell normally has a site
        /// within the radius (its own row) and step 1 answers; step 2 is reached in practice only by a boss
        /// map whose dig-able pickup cell is not boss_ok. Step 3 covers a pickup in water, on a steep slope,
        /// by a building, and a degraded (empty) catalogue - where it finds nothing and this returns null.
        ///
        /// Also null for a non-finite pickup point, which no outdoor GetMapCoords result can be.
        /// </summary>
        /// <summary>
        /// <paramref name="zone"/> is the zone isolation filter (owner-approved design): Unknown applies no
        /// filter at all (every step behaves exactly as before zone isolation shipped). With a known zone:
        /// step 1 only draws from IN-ZONE sites within radius; step 2 (the pickup point itself) is taken
        /// only when it is ALSO in zone - <see cref="ZoneAt"/> of the pickup point, since a diggable pickup
        /// cell is itself a catalogue site and so carries its own accurate zone; step 3 falls back to the
        /// NEAREST IN-ZONE site rather than the island-wide nearest. A zone that has no qualifying site at
        /// any step returns null rather than crossing into another zone.
        /// </summary>
        public Placement? PlaceNear(bool bossOk, Vector2 pickupMapCoords, float radiusMapUnits, bool pickupIsDigSite, Func<int, int> pick = null,
            MlTreasureZone zone = MlTreasureZone.Unknown)
        {
            if (!float.IsFinite(pickupMapCoords.X) || !float.IsFinite(pickupMapCoords.Y))
                return null;

            var site = RollNear(bossOk, pickupMapCoords, radiusMapUnits, pick, zone);

            if (site != null)
                return new Placement(site.Value.Ns, site.Value.Ew, atPickupPoint: false, bossVariantDropped: false);

            var pickupInZone = zone == MlTreasureZone.Unknown || ZoneAt(pickupMapCoords) == zone;

            if (pickupIsDigSite && pickupInZone)
                return new Placement(pickupMapCoords.Y, pickupMapCoords.X, atPickupPoint: true, bossVariantDropped: bossOk);

            var nearest = Nearest(pickupMapCoords, bossOk, zone);

            if (nearest == null)
                return null;

            return new Placement(nearest.Value.Ns, nearest.Value.Ew, atPickupPoint: false, bossVariantDropped: false, beyondRadius: true);
        }
        /// <summary>Squared map-unit distance from a site to a reference point. X=EastWest, Y=NorthSouth.</summary>
        private static float DistanceSq(Site site, Vector2 mapCoords)
        {
            var dx = site.Ew - mapCoords.X;
            var dy = site.Ns - mapCoords.Y;

            return dx * dx + dy * dy;
        }

        /// <summary>
        /// Whether the catalogue holds a boss_ok row AT the given map coordinates (X=EastWest,
        /// Y=NorthSouth - see <see cref="Site.MapCoords"/>), within <paramref name="toleranceMapUnits"/> on
        /// each axis. This is a confirmation of an already-rolled site, NOT a nearest lookup: a Relaria map
        /// whose 9010/9011 pair does not name a boss_ok cell must be refused rather than quietly snapped to
        /// the nearest one, because the town exclusion (TREASURE-HUNT-PLAN.md section 6) is the whole point
        /// of the flag.
        ///
        /// Fails closed on an empty or degraded store: no rows means no boss_ok row means false.
        /// </summary>
        public bool IsBossSite(Vector2 mapCoords, float toleranceMapUnits)
        {
            foreach (var site in bossOkSites)
            {
                if (Math.Abs(site.Ew - mapCoords.X) <= toleranceMapUnits && Math.Abs(site.Ns - mapCoords.Y) <= toleranceMapUnits)
                    return true;
            }

            return false;
        }

        /// <summary>The catalogue site nearest the given map coordinates (X=EastWest, Y=NorthSouth - see
        /// <see cref="Site.MapCoords"/>), by straight map-unit distance. Null when the store is empty.</summary>
        public Site? Nearest(Vector2 mapCoords) => Nearest(mapCoords, bossOk: false);

        /// <summary>
        /// <see cref="Nearest(Vector2)"/> restricted to boss_ok rows when <paramref name="bossOk"/> is true,
        /// the same filter <see cref="Roll"/> and <see cref="RollNear"/> apply. Null when that list is empty.
        /// </summary>
        public Site? Nearest(Vector2 mapCoords, bool bossOk, MlTreasureZone zone = MlTreasureZone.Unknown)
        {
            var list = bossOk ? bossOkSites : allSites;

            if (list.Count == 0)
                return null;

            Site? best = null;
            var bestSq = float.MaxValue;

            foreach (var site in list)
            {
                if (!InZone(site, zone))
                    continue;

                var dx = site.Ew - mapCoords.X;
                var dy = site.Ns - mapCoords.Y;
                var sq = dx * dx + dy * dy;

                if (best == null || sq < bestSq)
                {
                    bestSq = sq;
                    best = site;
                }
            }

            return best;
        }

        // ---- load ------------------------------------------------------------------------------------

        /// <summary>Sets <see cref="Instance"/> from server config. Call once at boot (mirrors
        /// WorldEventManager.Initialize / WorldEventAxisStore.LoadFromServerConfig).</summary>
        public static void Initialize()
        {
            Instance = LoadFromServerConfig();
        }

        /// <summary>Resolves the dig-sites folder using the same override -> content_folder ->
        /// exe-adjacent chain as WorldEventAxisStore.ResolveFolder, reads dig-sites.tsv from it, logs
        /// which rule won, and returns the parsed store - or <see cref="Empty"/> when nothing was
        /// found or the read/parse failed.</summary>
        public static MlTreasureSiteStore LoadFromServerConfig()
        {
            try
            {
                var overrideFolder = PropertyManager.GetString("ml_treasure_sites_folder").Item;
                var contentFolder = PropertyManager.GetString("content_folder").Item;
                var exeFolder = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);

                var folder = WorldEventAxisStore.ResolveFolder(overrideFolder, contentFolder, exeFolder,
                    new[] { "marae-lassel" }, Directory.Exists, out var reason);

                if (reason == "override")
                    reason = "ml_treasure_sites_folder";

                if (folder == null)
                {
                    log.Warn("[ML_TREASURE] no dig-sites folder found; store is empty");
                    return Empty;
                }

                var path = Path.Combine(folder, "dig-sites.tsv");

                log.Info($"[ML_TREASURE] dig-sites folder resolved via {reason}: {Path.GetFullPath(folder)}");

                return Load(path);
            }
            catch (Exception ex)
            {
                log.Error("[ML_TREASURE] failed to resolve dig-sites folder; store is empty", ex);
                return Empty;
            }
        }

        /// <summary>Reads and parses one dig-sites.tsv file. Never throws.</summary>
        public static MlTreasureSiteStore Load(string path)
        {
            try
            {
                if (!File.Exists(path))
                {
                    log.Warn($"[ML_TREASURE] dig-sites file not found at {path}; store is empty");
                    return Empty;
                }

                var lines = File.ReadAllLines(path);
                return ParseLines(lines, path);
            }
            catch (Exception ex)
            {
                log.Error($"[ML_TREASURE] failed to load dig-sites file at {path}; store is empty", ex);
                return Empty;
            }
        }

        /// <summary>
        /// Pure parse over already-read lines, exposed directly for unit tests against a fixture. Never
        /// throws: a malformed row (wrong column count, unparseable number) is skipped with a logged
        /// WARN naming the source and 1-based line number; an empty input, a header-only input, or an
        /// input where every data row was rejected all degrade to <see cref="Empty"/>.
        ///
        /// Expected header (not validated by name - only the column COUNT of each data row matters):
        /// landblock, cell_x, cell_y, local_x, local_y, z, map_ns, map_ew, boss_ok
        /// (Source/ACE.Content.Tools/Commands/SurfaceDigSitesCommand.cs).
        /// </summary>
        public static MlTreasureSiteStore ParseLines(IReadOnlyList<string> lines, string sourceForLog = "<fixture>")
        {
            if (lines == null || lines.Count == 0)
            {
                log.Warn($"[ML_TREASURE] dig-sites source {sourceForLog} is empty; store is empty");
                return Empty;
            }

            var all = new List<Site>();
            var bossOk = new List<Site>();
            var cells = new HashSet<uint>();
            var landblockZones = new Dictionary<ushort, MlTreasureZone>();

            // row 0 is the header; data starts at row 1
            for (var i = 1; i < lines.Count; i++)
            {
                var line = lines[i];

                if (string.IsNullOrWhiteSpace(line))
                    continue;

                var cols = line.Split('\t');

                if (cols.Length < 9)
                {
                    log.Warn($"[ML_TREASURE] {sourceForLog}:{i + 1} has {cols.Length} column(s), expected 9; row skipped");
                    continue;
                }

                if (!float.TryParse(cols[6], NumberStyles.Float, CultureInfo.InvariantCulture, out var ns)
                    || !float.TryParse(cols[7], NumberStyles.Float, CultureInfo.InvariantCulture, out var ew)
                    || !int.TryParse(cols[8], NumberStyles.Integer, CultureInfo.InvariantCulture, out var bossOkFlag))
                {
                    log.Warn($"[ML_TREASURE] {sourceForLog}:{i + 1} has an unparseable map_ns/map_ew/boss_ok column; row skipped");
                    continue;
                }

                // Column 10 (zone) is OPTIONAL - a pre-zone 9-column catalogue, or a row whose zone text is
                // missing/unrecognised, parses as Unknown rather than being skipped: it is still a fully
                // usable site, just an unfiltered one (MlTreasureZones.Parse never fails).
                var zone = cols.Length >= 10 ? MlTreasureZones.Parse(cols[9]) : MlTreasureZone.Unknown;

                var site = new Site(ns, ew, bossOkFlag != 0, zone);
                all.Add(site);

                // The cell index is kept only when it parses cleanly; a row whose map coordinates parse but
                // whose landblock/cell columns do not is still a usable site, it just never vouches for a
                // pickup point (IsCatalogueCell answers false, the conservative direction).
                var lbText = cols[0].StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? cols[0].Substring(2) : cols[0];

                if (ushort.TryParse(lbText, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var landblock)
                    && int.TryParse(cols[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var cellX)
                    && int.TryParse(cols[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out var cellY)
                    && cellX >= 0 && cellX <= 7 && cellY >= 0 && cellY <= 7)
                {
                    cells.Add(CellKey(landblock, cellX, cellY));

                    if (zone != MlTreasureZone.Unknown)
                        landblockZones[landblock] = zone;
                }

                if (site.BossOk)
                    bossOk.Add(site);
            }

            if (all.Count == 0)
            {
                log.Warn($"[ML_TREASURE] dig-sites source {sourceForLog} produced zero valid rows; store is empty");
                return Empty;
            }

            return new MlTreasureSiteStore(all, bossOk, cells, landblockZones);
        }
    }
}
