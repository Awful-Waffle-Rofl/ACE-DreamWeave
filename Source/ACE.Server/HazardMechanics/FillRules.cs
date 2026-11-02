using System;
using System.Collections.Generic;
using System.Globalization;
using System.Numerics;

using ACE.Entity.Enum;

namespace ACE.Server.HazardMechanics
{
    /// <summary>
    /// The fill mechanic's numbers (<c>/hazard fill</c> args), each read with a literal key, a default and a clamp.
    /// Everything z is absolute (the region's frame). Built by <see cref="From"/>; <see cref="FillRules.Validate"/> is
    /// the pre-flight that refuses a combination the sheet cannot honour.
    /// </summary>
    public sealed class FillSettings
    {
        public static readonly string[] NumberKeys =
        {
            "floor_z", "ground_z", "max_z", "start_z", "rate", "tick", "damage", "damage_per_m", "damage_max", "damage_scale",
            "submerged_damage", "submerged_per_m", "submerged_max", "head_height", "panel_size", "opacity", "wisp_scale", "resnap", "duration", "sheet_margin", "under_margin",
        };

        public static readonly string[] WholeKeys = { "wisps", "wisp_wcid" };

        /// <summary>acid | lava.</summary>
        public string Element { get; private set; }

        /// <summary>The panel colour word (acid|lava|smoke|poison).</summary>
        public string Colour { get; private set; }

        public DamageType DamageType { get; private set; }

        public double GroundZ { get; private set; }

        public double FloorZ { get; private set; }

        public double MaxZ { get; private set; }

        public double StartZ { get; private set; }

        public double Rate { get; private set; }

        public double Tick { get; private set; }

        public double Damage { get; private set; }

        public double DamagePerM { get; private set; }

        public double DamageMax { get; private set; }

        public double DamageScale { get; private set; }

        public double HeadHeight { get; private set; }

        /// <summary>
        /// The submerged tier's flat damage at depth == <see cref="HeadHeight"/> (acid over the player's head); 0 = tier off.
        /// </summary>
        public double SubmergedDamage { get; private set; }

        /// <summary>Added per metre of depth beyond <see cref="HeadHeight"/> in the submerged tier.</summary>
        public double SubmergedPerM { get; private set; }

        /// <summary>The submerged tier's cap; 0 means "same as <see cref="DamageMax"/>".</summary>
        public double SubmergedMax { get; private set; }

        public float PanelSize { get; private set; }

        public double Opacity { get; private set; }

        /// <summary>Every surface tile is a pair, an up-facing panel plus a down-facing one at the same centre (the surface shows from below).</summary>
        public bool Doubled { get; private set; }

        public int Wisps { get; private set; }

        public float WispScale { get; private set; }

        public uint WispWcid { get; private set; }

        public double Resnap { get; private set; }

        /// <summary>0 = forever.</summary>
        public double Duration { get; private set; }

        public float SheetMargin { get; private set; }

        public double UnderMargin { get; private set; }

        public static FillSettings From(HazardArgs args, HazardRegion region)
        {
            args ??= HazardArgs.Empty;

            var element = args.GetChoice("element", "acid", "acid", "lava");
            var lava = element == "lava";

            var colour = args.GetString("color");
            colour = HazardPanels.TryWcid(colour, out _) ? colour.Trim().ToLowerInvariant() : (lava ? "lava" : "acid");

            var damageType = HazardContext.TryParseDamageType(args.GetString("damage_type")) ?? (lava ? DamageType.Fire : DamageType.Acid);

            var ground = args.GetDouble("ground_z", region != null ? region.GroundZ(region.CenterX, region.CenterY) : 0.0, -100000.0, 100000.0);
            var floor = args.GetDouble("floor_z", ground + 0.05, -100000.0, 100000.0);
            var defaultMax = region != null && region.CeilingZ.HasValue ? region.CeilingZ.Value - 0.3 : floor + 1.0;
            var max = args.GetDouble("max_z", defaultMax, -100000.0, 100000.0);

            return new FillSettings
            {
                Element = element,
                Colour = colour,
                DamageType = damageType,
                GroundZ = ground,
                FloorZ = floor,
                MaxZ = max,
                StartZ = args.GetDouble("start_z", floor, -100000.0, 100000.0),
                Rate = args.GetDouble("rate", 0.05, -2.0, 2.0),
                Tick = args.GetDouble("tick", 2.0, 0.5, 30.0),
                Damage = args.GetDouble("damage", 40.0, 0.0, 10000.0),
                DamagePerM = args.GetDouble("damage_per_m", 25.0, 0.0, 10000.0),
                DamageMax = args.GetDouble("damage_max", 400.0, 0.0, 100000.0),
                DamageScale = args.GetDouble("damage_scale", 1.0, 0.0, 100.0),
                HeadHeight = args.GetDouble("head_height", 2.0, 0.5, 10.0),
                SubmergedDamage = args.GetDouble("submerged_damage", 0.0, 0.0, 10000.0),
                SubmergedPerM = args.GetDouble("submerged_per_m", 0.0, 0.0, 10000.0),
                SubmergedMax = args.GetDouble("submerged_max", 0.0, 0.0, 100000.0),
                PanelSize = (float)args.GetDouble("panel_size", 10.4, 1.0, 50.0),
                Opacity = args.GetDouble("opacity", 0.5, 0.0, 1.0),
                Doubled = args.GetBool("doubled", true),
                Wisps = args.GetInt("wisps", 16, 0, 64),
                WispScale = (float)args.GetDouble("wisp_scale", 0.5, 0.1, 3.0),
                WispWcid = (uint)args.GetInt("wisp_wcid", (int)(lava ? HazardDefaults.FillLavaWispWcid : HazardDefaults.FillAcidWispWcid), 1, int.MaxValue),
                Resnap = args.GetDouble("resnap", 10.0, 1.0, 60.0),
                Duration = args.GetDouble("duration", 0.0, 0.0, 86400.0),
                SheetMargin = (float)args.GetDouble("sheet_margin", 0.3, 0.0, 2.0),
                UnderMargin = args.GetDouble("under_margin", 0.1, 0.0, 1.0),
            };
        }

        /// <summary>The panel weenie for <see cref="Colour"/>.</summary>
        public uint PanelWcid => HazardPanels.TryWcid(Colour, out var wcid) ? wcid : HazardDefaults.PanelAcidWcid;
    }

    /// <summary>
    /// Every decision of the fill mechanic that is not a world call (shared invariant 6): the pre-flight, the sheet
    /// tiling, the damage formula, the wisp height clamp, the under-sheet hysteresis and the leg planner. Tested
    /// without a world; <see cref="FillDirector{THandle, TPlayer}"/> runs them against a port.
    ///
    /// INVARIANT 8 EXCEPTION (decided by the orchestrator 2026-10-26, see Docs/Hazards/2026-10-26-fill-and-fog-plan.md,
    /// "Resolved open questions"). <see cref="Validate"/> does NOT refuse a run whose surface can reach max_z: fill is
    /// accepted as host-escaped - the host that starts it owns the drain and the stop through /hazard control (rate,
    /// target, set_z, stop). A fill left alone rises to the top and stays there.
    /// </summary>
    public static class FillRules
    {
        /// <summary>The most surface panels one sheet may use (tiles x 2 when doubled; the under sheet is not counted: it adds one more panel per tile while it shows, so a doubled sheet with the under sheet up is 3 x tiles).</summary>
        public const int MaxSheetTiles = 64;

        /// <summary>Null when the run can start, else one line.</summary>
        public static string Validate(HazardRegion region, HazardArgs args)
        {
            var refusal = HazardPanels.RegionRefusal("fill", region);

            if (refusal != null)
                return refusal;

            args ??= HazardArgs.Empty;

            refusal = args.FirstNonNumeric(FillSettings.NumberKeys, FillSettings.WholeKeys);

            if (refusal != null)
                return refusal;

            refusal = HazardContext.BadDamageType(args);

            if (refusal != null)
                return refusal;

            var element = args.GetString("element");

            if (element != null && !string.Equals(element, "acid", StringComparison.OrdinalIgnoreCase) && !string.Equals(element, "lava", StringComparison.OrdinalIgnoreCase))
                return $"element='{element}' is not one of acid|lava";

            var colour = args.GetString("color");

            if (colour != null && !HazardPanels.TryWcid(colour, out _))
                return $"color='{colour}' is not one of {string.Join("|", HazardPanels.Colours)}";

            if (!region.CeilingZ.HasValue && !args.Has("max_z"))
                return "max_z is required when the region has no ceiling";

            var s = FillSettings.From(args, region);

            if (region.CeilingZ.HasValue && s.MaxZ > region.CeilingZ.Value - 0.05)
                return string.Format(CultureInfo.InvariantCulture, "max_z {0:0.##} is above the ceiling {1:0.##} less 0.05", s.MaxZ, region.CeilingZ.Value);

            if (s.FloorZ >= s.MaxZ - 0.5)
                return string.Format(CultureInfo.InvariantCulture, "floor_z {0:0.##} must be at least 0.5 below max_z {1:0.##}", s.FloorZ, s.MaxZ);

            if (s.StartZ < s.FloorZ || s.StartZ > s.MaxZ)
                return string.Format(CultureInfo.InvariantCulture, "start_z {0:0.##} is outside floor_z {1:0.##} .. max_z {2:0.##}", s.StartZ, s.FloorZ, s.MaxZ);

            var tiles = SheetTiles(region, s.PanelSize, s.SheetMargin).Count;
            var panels = tiles * (s.Doubled ? 2 : 1);

            if (panels > MaxSheetTiles)
            {
                return s.Doubled
                    ? $"fill sheet needs {panels} surface panels ({tiles} tiles x 2 for doubled=1); at most {MaxSheetTiles} (use doubled=0 or a larger panel_size)"
                    : $"fill sheet needs {panels} surface panels; at most {MaxSheetTiles}";
            }

            return null;
        }

        /// <summary>
        /// The tile centres of one sheet (<see cref="Sheet"/>); the panel edge that goes with them is
        /// <see cref="SheetEdge"/>. Every panel of the sheet - both faces of a doubled tile and the under sheet - is
        /// drawn from this one plan.
        /// </summary>
        public static IReadOnlyList<(Vector2 Centre, float ZOffset)> SheetTiles(HazardRegion region, float panelSize, float margin)
            => Sheet(region, panelSize, margin).Tiles;

        /// <summary>The square panel edge the sheet's tiles are drawn at (<see cref="Sheet"/>); 0 for no sheet.</summary>
        public static float SheetEdge(HazardRegion region, float panelSize, float margin)
            => Sheet(region, panelSize, margin).Edge;

        /// <summary>
        /// One flush sheet, EDGE TO EDGE (design doc "Surface sheet"): the region rect grown by <paramref name="margin"/>
        /// on every side is covered by nx = ceil(width / panel_size) by ny = ceil(height / panel_size) square tiles
        /// whose edge is the PITCH, max(width / nx, height / ny) - never more than panel_size - so neighbours abut
        /// with no overlap and no gap. The axis that sets the edge is covered exactly; the other overhangs its margined
        /// edges equally by (n x edge - extent) / 2 (Matron 0.06 m, Grael 0.025 m: a square tile cannot match two
        /// pitches). ZOffset is 0 for every tile: the whole sheet is ONE plane.
        ///
        /// BUG 2026-10-27 (Matron playtest 3, light and dark seam lines): ZOffset used to alternate 0 / 0.01 m in a
        /// checkerboard, against z-fighting where the old tiles OVERLAPPED. Edge to edge nothing overlaps, so all it did
        /// was put a 1 cm step on every shared edge; which side was higher flipped at every crossing seam, so one seam
        /// line read light along one tile and dark along the next, offset at the crossing.
        ///
        /// BUG 2026-10-27 (Matron playtest 2, dark bands at opacity 0.5): the tiles were drawn at panel_size but spread
        /// first-to-last flush to the edges, a pitch of (extent - panel_size) / (n - 1): Matron's 12 m panels sat 9.65 m
        /// apart, 2.35 m of doubled translucency on every seam.
        /// </summary>
        public static (float Edge, IReadOnlyList<(Vector2 Centre, float ZOffset)> Tiles) Sheet(HazardRegion region, float panelSize, float margin)
        {
            var result = new List<(Vector2, float)>();

            if (region == null || !(panelSize > 0f))
                return (0f, result);

            var minX = region.MinX - margin;
            var minY = region.MinY - margin;
            var width = region.MaxX - region.MinX + 2f * margin;
            var height = region.MaxY - region.MinY + 2f * margin;

            var nx = Math.Max(1, (int)Math.Ceiling(width / panelSize - 1e-6));
            var ny = Math.Max(1, (int)Math.Ceiling(height / panelSize - 1e-6));

            var edge = Math.Max(width / nx, height / ny);

            // centred: the overhang of the axis whose pitch is smaller is split evenly between its two sides
            var x0 = minX + (width - nx * edge) / 2f;
            var y0 = minY + (height - ny * edge) / 2f;

            for (var j = 0; j < ny; j++)
            {
                for (var i = 0; i < nx; i++)
                {
                    var x = x0 + (i + 0.5f) * edge;
                    var y = y0 + (j + 0.5f) * edge;

                    result.Add((new Vector2(x, y), 0f));
                }
            }

            return (edge, result);
        }

        /// <summary>The step <see cref="LowestFaceDownZ"/> climbs in.</summary>
        public const float FaceDownLiftStep = 0.01f;

        /// <summary>
        /// The lowest z, from <paramref name="z"/> up, at which a FACE-DOWN panel centred on <paramref name="xy"/> rests
        /// exactly where it is put: the first z whose point <see cref="HazardPanels.FaceClearance"/> below is in a cell
        /// (<paramref name="hasCell"/>, live: HazardContext.HasCell), climbing in <see cref="FaceDownLiftStep"/> steps.
        /// At most FaceClearance above <paramref name="z"/>, since that probe is <paramref name="z"/> itself.
        /// <paramref name="z"/> unchanged when the point itself is in no cell (the spawn then refuses as before, outside
        /// the room) or there is no lookup. A face-down panel placed lower is refused or pushed up off the floor
        /// (HazardPanels.FaceClearance), which aborted the Matron fill at its floor_z -59.95 over the floor -60.0.
        /// PURE.
        /// </summary>
        public static float LowestFaceDownZ(Vector2 xy, float z, Func<Vector3, bool> hasCell)
        {
            if (hasCell == null || !float.IsFinite(z) || !hasCell(new Vector3(xy.X, xy.Y, z)))
                return z;

            var steps = (int)Math.Ceiling(HazardPanels.FaceClearance / FaceDownLiftStep - 1e-4);

            for (var i = 0; i <= steps; i++)
            {
                var c = z + Math.Min(HazardPanels.FaceClearance, i * FaceDownLiftStep);

                if (hasCell(new Vector3(xy.X, xy.Y, c - HazardPanels.FaceClearance)))
                    return c;
            }

            return z + HazardPanels.FaceClearance;
        }

        /// <summary>
        /// The flat damage of one hit: min(damage + damage_per_m x depth, damage_max) x damageScale; 0 when the
        /// player stands at or above the surface (depth &lt;= 0) or depth is not finite. The optional submerged tier
        /// (submerged_damage &gt; 0) applies once depth &gt;= head_height: the result is then
        /// max(that curve, min(submerged_damage + submerged_per_m x (depth - head_height), cap)) x damageScale, where
        /// cap is submerged_max, or damage_max when submerged_max is 0. Tier off (the default) is the plain curve.
        /// </summary>
        public static double FlatDamage(double depth, FillSettings s, double damageScale)
        {
            if (!double.IsFinite(depth) || depth <= 0.0)
                return 0.0;

            var flat = Math.Min(s.Damage + s.DamagePerM * depth, s.DamageMax);

            if (s.SubmergedDamage > 0.0 && depth >= s.HeadHeight)
            {
                var cap = s.SubmergedMax > 0.0 ? s.SubmergedMax : s.DamageMax;
                var submerged = Math.Min(s.SubmergedDamage + s.SubmergedPerM * (depth - s.HeadHeight), cap);

                flat = Math.Max(flat, submerged);
            }

            return flat * damageScale;
        }

        /// <summary>
        /// Where a wisp's origin (at its feet) stands for a surface at <paramref name="surfaceZ"/>: half its height
        /// under the surface, never into the floor plates (ground + 0.05) and never into the ceiling.
        /// </summary>
        public static float WispZ(double surfaceZ, double groundZ, double? ceilingZ, float wispScale)
        {
            var top = HazardDefaults.WispHeightAtScale1 * wispScale;
            var z = surfaceZ - top / 2.0;

            if (ceilingZ.HasValue)
                z = Math.Min(z, ceilingZ.Value - top - HazardDefaults.WispCeilingMargin);

            z = Math.Max(z, groundZ + 0.05);

            return (float)z;
        }

        /// <summary>
        /// Hysteresis for the under-sheet (the ceiling-facing copy a player sees from below): shown once the surface
        /// reaches underZ + margin, hidden again below underZ, otherwise unchanged.
        /// </summary>
        public static bool UnderSheetShown(bool shownNow, double surfaceZ, double underZ, double margin)
        {
            if (surfaceZ >= underZ + margin)
                return true;

            if (surfaceZ < underZ)
                return false;

            return shownNow;
        }

        /// <summary>
        /// The next leg to schedule: at most <paramref name="resnap"/> seconds long, ending at the surface z then.
        /// Null when the surface has no event ahead (not moving, or already at its bound).
        /// </summary>
        public static (double EndZ, double Seconds)? NextLeg(FillSurface surface, double now, double resnap)
        {
            var event0 = surface.SecondsToNextEvent(now);

            if (!event0.HasValue)
                return null;

            var seconds = Math.Min(resnap, event0.Value);

            return (surface.ZAt(now + seconds), seconds);
        }
    }
}
