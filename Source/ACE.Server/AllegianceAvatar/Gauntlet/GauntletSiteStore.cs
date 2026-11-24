using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;

using log4net;

using ACE.Server.Managers;
using ACE.Server.ThreadDungeons;
using ACE.Server.ThreadDungeons.Defs;
using ACE.Server.WorldEvents;

namespace ACE.Server.Avatars.Gauntlet;

/// <summary>
/// One arena in Content/dungeons/gauntlet/sites.json. Positions use the outdoor files' point shape
/// (<see cref="DungeonSpawnPointDef"/>: a full cell id plus landblock-local x, y, z, and an optional heading quaternion).
/// </summary>
public sealed class GauntletSiteDef
{
    public const double DefaultRadius = 60.0;
    public const int MinEntries = 1;
    public const int MaxEntries = 8;

    /// <summary>An outdoor Threads site id (a "kind": "outdoor" row of Content/dungeons/dynamic/index.json).</summary>
    [JsonPropertyName("id")] public string Id { get; set; }

    /// <summary>The Avatar's anchor and the fight circle's centre.</summary>
    [JsonPropertyName("centre")] public DungeonSpawnPointDef Centre { get; set; }

    /// <summary>The fight circle's radius in metres (horizontal, from <see cref="Centre"/>).</summary>
    [JsonPropertyName("radius")] public double Radius { get; set; } = DefaultRadius;

    /// <summary>Where a dead member waits out the death hold.</summary>
    [JsonPropertyName("pen")] public DungeonSpawnPointDef Pen { get; set; }

    /// <summary>1 to 8 arrival points inside the fight circle; the gate rotates through them.</summary>
    [JsonPropertyName("entries")] public List<DungeonSpawnPointDef> Entries { get; set; } = new List<DungeonSpawnPointDef>();

    /// <summary>The Thread catalog row this site copies (the store fills it after the lint passes). Never in the file.</summary>
    [JsonIgnore] public DungeonEntryDef Outdoor { get; set; }
}

/// <summary>Root of Content/dungeons/gauntlet/sites.json.</summary>
public sealed class GauntletSitesFileDef
{
    [JsonPropertyName("version")] public int Version { get; set; } = 1;
    [JsonPropertyName("sites")] public List<GauntletSiteDef> Sites { get; set; } = new List<GauntletSiteDef>();
}

/// <summary>
/// The Gauntlet sites store: Content/dungeons/gauntlet/sites.json, validated against the Thread catalog's outdoor rows.
///
/// FILE SCHEMA (version 1):
/// <code>
/// {
///   "version": 1,
///   "sites": [
///     {
///       "id": "ow_arwic",                                             // an outdoor Threads site id
///       "centre": { "cell": 3316121621, "x": 96.0, "y": 96.0, "z": 50.0 }, // fight-circle centre and Avatar anchor
///       "radius": 60,                                                 // fight circle, metres (default 60)
///       "pen":    { "cell": 3316121621, "x": 10.0, "y": 10.0, "z": 50.0 }, // death hold position
///       "entries": [ { "cell": 3316121621, "x": 100.0, "y": 96.0, "z": 50.0 } ]  // 1 to 8 arrival points
///     }
///   ]
/// }
/// </code>
/// A point is the outdoor files' shape (cell, x, y, z, optional qw/qx/qy/qz). Distances are horizontal, in global outdoor
/// metres (OutdoorSiteGeometry). Comments and trailing commas are accepted when parsing.
///
/// LINT. A site that breaks any rule is dropped with one diagnostic line (never half-loaded); the rest still load:
/// <list type="bullet">
/// <item><see cref="UnknownSite"/>: no id, a duplicate id, or an id that is not a loaded outdoor row of the Thread catalog.
/// Picked by id on purpose: OutdoorSiteTunables.Candidates drops every row above its maxLevel of 500, and the Gauntlet
/// runs above that.</item>
/// <item><see cref="BadShape"/>: a missing centre or pen, a radius that is not a positive finite number, or an entry count
/// outside 1 to 8.</item>
/// <item><see cref="OffFootprint"/>: the centre, the pen or an entry stands on a landblock outside the site's footprint.</item>
/// <item><see cref="CentreOutsideCircle"/>: the centre lies outside the site's own perimeter circle, or an entry lies outside
/// the fight circle.</item>
/// <item><see cref="EntryNearPortal"/>: an entry is not inside allegiance_gauntlet_portal_min_distance of the centre, or is within
/// <see cref="EntryPortalClearance"/> m of a point that can be picked as a wave portal.</item>
/// <item><see cref="PenTooClose"/>: the pen is closer than allegiance_gauntlet_pen_min_distance, or the death hold's snap-back
/// margin plus 1 m (GauntletDeathHolds.SnapBackMargin), whichever is more, beyond the fight circle's edge.</item>
/// <item><see cref="TooFewPortalPoints"/>: fewer than allegiance_gauntlet_portals_min of the site's curated, non-interior
/// points qualify as portal points around the centre, by the exact rule the waves use
/// (ThreadEncounterPortalWaves.PickPortals with the Gauntlet's portal config).</item>
/// </list>
/// Parse is pure; Load reads a folder; LoadFromServerConfig resolves Content/dungeons/gauntlet like the Thread store.
/// None of them throws.
/// </summary>
public sealed class GauntletSiteStore
{
    private static readonly ILog log = LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);

    public const string FileName = "sites.json";
    public const int SchemaVersion = 1;

    public const string UnknownSite = "unknown site";
    public const string BadShape = "bad shape";
    public const string OffFootprint = "off footprint";
    public const string CentreOutsideCircle = "outside circle";
    public const string PenTooClose = "pen too close";
    public const string TooFewPortalPoints = "too few portal points";
    public const string EntryNearPortal = "entry near portal";

    /// <summary>Metres every entry keeps from every point that can be picked as a wave portal.</summary>
    public const double EntryPortalClearance = 15.0;

    private static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    public static readonly GauntletSiteStore Empty = new GauntletSiteStore(new List<GauntletSiteDef>(), new List<string>());

    /// <summary>The published store; Task 7 loads it at startup. Empty until then.</summary>
    public static GauntletSiteStore Current { get; set; } = Empty;

    private readonly Dictionary<string, GauntletSiteDef> byId;

    private GauntletSiteStore(List<GauntletSiteDef> sites, List<string> diagnostics)
    {
        Sites = sites;
        Diagnostics = diagnostics;
        byId = sites.ToDictionary(s => s.Id, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>The sites that passed the lint, in file order.</summary>
    public IReadOnlyList<GauntletSiteDef> Sites { get; }

    /// <summary>One line per dropped site or file-level problem.</summary>
    public IReadOnlyList<string> Diagnostics { get; }

    public GauntletSiteDef Get(string id) => id != null && byId.TryGetValue(id, out var s) ? s : null;

    /// <summary>
    /// Parses and lints <paramref name="json"/> against <paramref name="catalog"/> (the Thread store's rows by id). An empty or
    /// unparseable file, or an unknown version, is an empty store with a diagnostic.
    /// </summary>
    public static GauntletSiteStore Parse(string json, IReadOnlyDictionary<string, DungeonEntryDef> catalog, GauntletTuning tuning)
    {
        var diagnostics = new List<string>();
        var sites = new List<GauntletSiteDef>();

        if (string.IsNullOrWhiteSpace(json))
        {
            diagnostics.Add($"{FileName}: missing or empty");
            return new GauntletSiteStore(sites, diagnostics);
        }

        GauntletSitesFileDef file;

        try
        {
            file = JsonSerializer.Deserialize<GauntletSitesFileDef>(json, JsonOptions);
        }
        catch (Exception ex)
        {
            diagnostics.Add($"{FileName}: unparseable - {ex.Message}");
            return new GauntletSiteStore(sites, diagnostics);
        }

        if (file == null || file.Version != SchemaVersion)
        {
            diagnostics.Add($"{FileName}: version {file?.Version.ToString(CultureInfo.InvariantCulture) ?? "(none)"} is not {SchemaVersion}");
            return new GauntletSiteStore(sites, diagnostics);
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var t = GauntletTuning.Sanitized(tuning ?? GauntletTuning.Defaults);

        foreach (var site in file.Sites ?? new List<GauntletSiteDef>())
        {
            if (site == null)
            {
                diagnostics.Add($"{FileName}: null site entry dropped");
                continue;
            }

            DungeonEntryDef row = null;
            var refusal = site.Id != null && !seen.Add(site.Id)
                ? $"{UnknownSite}: duplicate id"
                : Lint(site, site.Id != null && catalog != null && catalog.TryGetValue(site.Id, out row) ? row : null, t);

            if (refusal != null)
            {
                diagnostics.Add($"{FileName}: site '{site.Id ?? "(no id)"}' dropped - {refusal}");
                continue;
            }

            site.Outdoor = row;
            sites.Add(site);
        }

        return new GauntletSiteStore(sites, diagnostics);
    }

    /// <summary>
    /// The lint for one site against its Thread catalog row (null when the id resolved to nothing): null when the site may
    /// be used, else the refusal, which starts with one of the public tags. Rules are checked in the class summary's order.
    /// Pure.
    /// </summary>
    public static string Lint(GauntletSiteDef site, DungeonEntryDef row, GauntletTuning tuning)
    {
        if (site == null || string.IsNullOrWhiteSpace(site.Id))
            return $"{UnknownSite}: no id";

        if (row == null || !row.IsOutdoor || row.Outdoor == null)
            return $"{UnknownSite}: '{site.Id}' is not a loaded outdoor Threads site";

        if (site.Centre == null || site.Pen == null)
            return $"{BadShape}: centre and pen are required";

        if (!double.IsFinite(site.Radius) || site.Radius <= 0)
            return $"{BadShape}: radius {site.Radius.ToString(CultureInfo.InvariantCulture)} is not a positive number";

        var entries = site.Entries ?? new List<DungeonSpawnPointDef>();

        if (entries.Count < GauntletSiteDef.MinEntries || entries.Count > GauntletSiteDef.MaxEntries || entries.Any(e => e == null))
            return $"{BadShape}: {entries.Count} entries, {GauntletSiteDef.MinEntries} to {GauntletSiteDef.MaxEntries} needed";

        var outdoor = row.Outdoor;
        var footprint = new HashSet<ushort>(outdoor.Footprint ?? new List<ushort>()) { outdoor.Primary };

        foreach (var (name, p) in Named(site))
            if (!footprint.Contains(LandblockOf(p)))
                return $"{OffFootprint}: {name} is on landblock 0x{LandblockOf(p):X4}, outside the site's footprint";

        var centre = Pos(site.Centre);

        if (outdoor.Circle == null || !OutdoorSiteGeometry.Contains(outdoor.Circle, centre))
            return $"{CentreOutsideCircle}: the centre lies outside the site's perimeter circle";

        for (var i = 0; i < entries.Count; i++)
        {
            var d = OutdoorSiteGeometry.HorizontalDistance(Pos(entries[i]), centre);

            if (d > site.Radius)
                return string.Format(CultureInfo.InvariantCulture, "{0}: entry {1} is {2:0.##} m from the centre, past the {3:0.##} m fight circle",
                    CentreOutsideCircle, i + 1, d, site.Radius);
        }

        var t = GauntletTuning.Sanitized(tuning ?? GauntletTuning.Defaults);

        // Arrivals must not land on a wave portal: every entry inside portal_min_distance of the centre, and clear of every
        // point that can be picked as a portal (a candidate between portal_min_distance and portal_max_distance).
        var portalPoints = PortalCandidates(row)
            .Select(c => c.Point)
            .Where(p => { var d = OutdoorSiteGeometry.HorizontalDistance(Pos(p), centre); return d >= t.PortalMinDistance && d <= t.PortalMaxDistance; })
            .ToList();

        for (var i = 0; i < entries.Count; i++)
        {
            var e = Pos(entries[i]);
            var fromCentre = OutdoorSiteGeometry.HorizontalDistance(e, centre);

            if (fromCentre >= t.PortalMinDistance)
                return string.Format(CultureInfo.InvariantCulture, "{0}: entry {1} is {2:0.##} m from the centre, not inside the {3:0.##} m portal_min_distance",
                    EntryNearPortal, i + 1, fromCentre, t.PortalMinDistance);

            foreach (var p in portalPoints)
            {
                var d = OutdoorSiteGeometry.HorizontalDistance(e, Pos(p));

                if (d < EntryPortalClearance)
                    return string.Format(CultureInfo.InvariantCulture, "{0}: entry {1} is {2:0.##} m from a portal point, under {3:0.##} m",
                        EntryNearPortal, i + 1, d, EntryPortalClearance);
            }
        }

        var penDistance = OutdoorSiteGeometry.HorizontalDistance(Pos(site.Pen), centre);

        // The pen must also lie outside the death hold's snap-back zone (the circle plus SnapBackMargin), or a held player
        // standing in the pen would be snapped back to it every second.
        var penClearance = Math.Max(t.PenMinDistance, GauntletDeathHolds.SnapBackMargin + 1);

        if (penDistance < site.Radius + penClearance)
            return string.Format(CultureInfo.InvariantCulture, "{0}: the pen is {1:0.##} m from the centre, under the {2:0.##} m fight circle plus {3:0.##} m",
                PenTooClose, penDistance, site.Radius, penClearance);

        if (QualifyingPortalPoints(site, row, t, out var portalRefusal) == null)
            return $"{TooFewPortalPoints}: {portalRefusal}";

        return null;
    }

    /// <summary>
    /// The site's portal points around its centre by the waves' own rule: ThreadEncounterPortalWaves.PickPortals over the
    /// row's curated, non-interior points, with the Gauntlet portal config at allegiance_gauntlet_portals_min portals (so it
    /// refuses exactly when fewer qualify). Null, with PickPortals' refusal, when too few qualify. Pure (a fixed seed).
    /// </summary>
    public static List<DungeonSpawnPointDef> QualifyingPortalPoints(GauntletSiteDef site, DungeonEntryDef row, GauntletTuning tuning, out string refusal)
    {
        var t = GauntletTuning.Sanitized(tuning ?? GauntletTuning.Defaults);
        var portals = (int)Math.Min(int.MaxValue, t.PortalsMin);
        var config = new GauntletRules(t).PortalConfig(target: portals, portals: portals);

        var candidates = PortalCandidates(row);

        var anchor = ThreadEncounterGeometry.Global(site.Centre, isOutdoor: true);

        return ThreadEncounterPortalWaves.PickPortals(anchor, LandblockOf(site.Centre), candidates, isOutdoor: true, config,
            new Random(0), "centre", out refusal);
    }

    /// <summary>
    /// The portal candidates of a site: the row's curated points that are not interior, in the outdoor comparison frame
    /// (no walk distance outdoors). The one rule both the lint (<see cref="QualifyingPortalPoints"/>) and the waves
    /// (GauntletWavePlanner) feed to ThreadEncounterPortalWaves.PickPortals, so a site the lint passes is the site the
    /// waves pick from. Never null. Pure.
    /// </summary>
    internal static List<PortalWaveCandidate> PortalCandidates(DungeonEntryDef row)
        => (row?.Points ?? new List<DungeonSpawnPointDef>())
            .Where(p => p != null && p.Curated && !string.Equals(p.Kind, "interior", StringComparison.OrdinalIgnoreCase))
            .Select(p => new PortalWaveCandidate(p, ThreadEncounterGeometry.Global(p, isOutdoor: true), null))
            .ToList();

    private static IEnumerable<(string Name, DungeonSpawnPointDef Point)> Named(GauntletSiteDef site)
    {
        yield return ("the centre", site.Centre);
        yield return ("the pen", site.Pen);

        for (var i = 0; i < site.Entries.Count; i++)
            yield return ($"entry {i + 1}", site.Entries[i]);
    }

    private static ushort LandblockOf(DungeonSpawnPointDef p) => (ushort)(p.Cell >> 16);

    private static OutdoorPos Pos(DungeonSpawnPointDef p) => new OutdoorPos(LandblockOf(p), p.X, p.Y);

    /// <summary>Reads <see cref="FileName"/> from <paramref name="folder"/>. Never throws: an unreadable file is a diagnostic and an empty store.</summary>
    public static GauntletSiteStore Load(string folder, IReadOnlyDictionary<string, DungeonEntryDef> catalog, GauntletTuning tuning)
    {
        string json;

        try
        {
            var path = Path.Combine(folder, FileName);
            json = File.Exists(path) ? File.ReadAllText(path) : "";
        }
        catch (Exception ex)
        {
            return new GauntletSiteStore(new List<GauntletSiteDef>(), new List<string> { $"{FileName}: could not read - {ex.Message}" });
        }

        return Parse(json, catalog, tuning);
    }

    /// <summary>Resolves Content/dungeons/gauntlet (content_folder, then the exe folder) and loads it against the live Thread store.</summary>
    public static GauntletSiteStore LoadFromServerConfig()
    {
        var contentFolder = PropertyManager.GetString("content_folder").Item;
        var exeFolder = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
        var folder = WorldEventAxisStore.ResolveFolder(null, contentFolder, exeFolder, new[] { "dungeons", "gauntlet" }, Directory.Exists, out var reason);

        if (folder == null)
        {
            log.Warn("[GAUNTLET] no sites folder found; no arena is configured");
            return Empty;
        }

        var store = Load(folder, ThreadDungeonManager.Store.Dungeons, GauntletTuning.FromPropertyManager());
        log.Info($"[GAUNTLET] loaded {store.Sites.Count} arena site(s) from {folder} ({reason})");

        foreach (var d in store.Diagnostics)
            log.Warn($"[GAUNTLET] {d}");

        return store;
    }
}
