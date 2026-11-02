using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

namespace ACE.Server.HazardMechanics
{
    /// <summary>The fog mechanic's numbers (<c>/hazard fog</c> args), each read with a literal key, a default and a clamp.</summary>
    public sealed class FogSettings
    {
        public static readonly string[] NumberKeys = { "opacity", "spacing", "height", "base_z", "max_panels", "radar_scan", "duration" };

        public static readonly string[] WholeKeys = { "max_panels" };

        /// <summary>The panel colour word (acid|lava|smoke|poison).</summary>
        public string Colour { get; private set; }

        public double Opacity { get; private set; }

        public float Spacing { get; private set; }

        /// <summary>The panel edge floor and the wall height the lattice covers.</summary>
        public float Height { get; private set; }

        public float BaseZ { get; private set; }

        public bool Doubled { get; private set; }

        public int MaxPanels { get; private set; }

        public bool Radar { get; private set; }

        public double RadarScan { get; private set; }

        /// <summary>0 = forever.</summary>
        public double Duration { get; private set; }

        public static FogSettings From(HazardArgs args, HazardRegion region)
        {
            args ??= HazardArgs.Empty;

            var colour = args.GetString("color");
            colour = HazardPanels.TryWcid(colour, out _) ? colour.Trim().ToLowerInvariant() : "smoke";

            var ground = region != null ? region.GroundZ(region.CenterX, region.CenterY) : 0f;
            var baseZ = (float)args.GetDouble("base_z", ground, -100000.0, 100000.0);
            var defaultHeight = region != null && region.CeilingZ.HasValue ? region.CeilingZ.Value - ground : 5.0;

            return new FogSettings
            {
                Colour = colour,
                Opacity = args.GetDouble("opacity", 0.15, 0.0, 1.0),
                Spacing = (float)args.GetDouble("spacing", 8.0, 1.0, 50.0),
                Height = (float)args.GetDouble("height", defaultHeight, 0.5, 20.0),
                BaseZ = baseZ,
                Doubled = args.GetBool("doubled", true),
                MaxPanels = args.GetInt("max_panels", 160, 1, 400),
                Radar = args.GetBool("radar", true),
                RadarScan = args.GetDouble("radar_scan", 1.0, 0.2, 10.0),
                Duration = args.GetDouble("duration", 0.0, 0.0, 86400.0),
            };
        }

        /// <summary>The panel weenie for <see cref="Colour"/>.</summary>
        public uint PanelWcid => HazardPanels.TryWcid(Colour, out var wcid) ? wcid : HazardDefaults.PanelSmokeWcid;
    }

    /// <summary>One upright fog panel: its centre, its facing and its square edge in metres.</summary>
    public readonly struct FogPanel
    {
        public FogPanel(Vector3 centre, Quaternion rotation, float edge)
        {
            Centre = centre;
            Rotation = rotation;
            Edge = edge;
        }

        public Vector3 Centre { get; }

        public Quaternion Rotation { get; }

        public float Edge { get; }
    }

    /// <summary>
    /// Every decision of the fog mechanic that is not a world call (shared invariant 6): the pre-flight, the lattice of
    /// upright panels, and the radar hide/restore plan. Tested without a world.
    ///
    /// THE LATTICE. Lines of panels at constant y (facing +/-Y) and at constant x (facing +/-X), spaced about
    /// <c>spacing</c> apart across the rect. A line of length L holds n = max(1, floor(L / height)) panels of edge
    /// max(L / n, height), tiled exactly along it, their bottoms at base_z; an edge above <c>height</c> overhangs
    /// upward. <c>doubled</c> adds a partner facing the opposite way to every panel (the panels are single-sided).
    /// </summary>
    public static class FogRules
    {
        /// <summary>Null when the run can start, else one line.</summary>
        public static string Validate(HazardRegion region, HazardArgs args)
        {
            var refusal = HazardPanels.RegionRefusal("fog", region);

            if (refusal != null)
                return refusal;

            args ??= HazardArgs.Empty;

            refusal = args.FirstNonNumeric(FogSettings.NumberKeys, FogSettings.WholeKeys);

            if (refusal != null)
                return refusal;

            var colour = args.GetString("color");

            if (colour != null && !HazardPanels.TryWcid(colour, out _))
                return $"color='{colour}' is not one of {string.Join("|", HazardPanels.Colours)}";

            if (!region.CeilingZ.HasValue && !args.Has("height"))
                return "height is required when the region has no ceiling";

            var s = FogSettings.From(args, region);
            var needed = Lattice(region, s.Spacing, s.Height, s.BaseZ, s.Doubled).Count;

            if (needed > s.MaxPanels)
                return $"fog lattice needs {needed} panels; max_panels is {s.MaxPanels}";

            return null;
        }

        /// <summary>The panels of the lattice (see the class comment). Empty for a null or non-rect region.</summary>
        public static List<FogPanel> Lattice(HazardRegion region, float spacing, float height, float baseZ, bool doubled)
        {
            var panels = new List<FogPanel>();

            if (region == null || region.Shape != HazardShape.Rect || !(spacing > 0f) || !(height > 0f))
                return panels;

            double width = region.MaxX - region.MinX;
            double depth = region.MaxY - region.MinY;

            var lineCountY = Math.Max(1, (int)Math.Round(depth / spacing, MidpointRounding.AwayFromZero));
            var lineCountX = Math.Max(1, (int)Math.Round(width / spacing, MidpointRounding.AwayFromZero));

            for (var j = 0; j < lineCountY; j++)
            {
                var y = region.MinY + (j + 0.5) * depth / lineCountY;

                foreach (var (along, edge) in Tiles(width, height))
                    Add(panels, new Vector3((float)(region.MinX + along), (float)y, baseZ + edge / 2f), HazardPanels.FaceAlongY(true), edge, doubled);
            }

            for (var i = 0; i < lineCountX; i++)
            {
                var x = region.MinX + (i + 0.5) * width / lineCountX;

                foreach (var (along, edge) in Tiles(depth, height))
                    Add(panels, new Vector3((float)x, (float)(region.MinY + along), baseZ + edge / 2f), HazardPanels.FaceAlongX(true), edge, doubled);
            }

            return panels;
        }

        /// <summary>The panel centres (distance from the line start) and edge for a line of <paramref name="length"/>.</summary>
        private static IEnumerable<(double Along, float Edge)> Tiles(double length, float height)
        {
            var n = Math.Max(1, (int)Math.Floor(length / height));
            var edge = (float)Math.Max(length / n, height);

            for (var k = 0; k < n; k++)
                yield return ((k + 0.5) * length / n, edge);
        }

        private static void Add(List<FogPanel> panels, Vector3 centre, Quaternion rotation, float edge, bool doubled)
        {
            panels.Add(new FogPanel(centre, rotation, edge));

            if (doubled)
                panels.Add(new FogPanel(centre, HazardPanels.Flipped(rotation), edge));
        }

        /// <summary>
        /// Which creatures to hide and which to give back. Enabled: hide every creature inside that is not hidden yet,
        /// restore every hidden one that is no longer inside. Disabled: hide none, restore all hidden. Both ascending.
        /// </summary>
        public static (List<uint> Hide, List<uint> Restore) RadarPlan(IReadOnlyCollection<uint> inside, IReadOnlyCollection<uint> hidden, bool enabled)
        {
            inside ??= Array.Empty<uint>();
            hidden ??= Array.Empty<uint>();

            if (!enabled)
                return (new List<uint>(), hidden.Distinct().OrderBy(g => g).ToList());

            var hiddenSet = new HashSet<uint>(hidden);
            var insideSet = new HashSet<uint>(inside);

            return (insideSet.Where(g => !hiddenSet.Contains(g)).OrderBy(g => g).ToList(),
                hiddenSet.Where(g => !insideSet.Contains(g)).OrderBy(g => g).ToList());
        }
    }
}
