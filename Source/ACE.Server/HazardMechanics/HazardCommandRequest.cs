using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Numerics;

namespace ACE.Server.HazardMechanics
{
    /// <summary>
    /// The pure parse of "/hazard &lt;kind&gt; [pattern] [preset=.. | region=..] [hz=..] [seed=..] [k=v ...]" and the
    /// region it resolves to for a caller. The command handler is a thin shell over this, so the grammar is tested.
    ///
    /// Reserved keys (taken out, never passed to the mechanic): preset, region, hz, seed, pattern, at (at=me becomes cx/cy via ArgsFor). Everything else
    /// becomes the run's caller args, laid over the settings string by HazardManager.Start.
    /// </summary>
    public sealed class HazardCommandRequest
    {
        public string Kind { get; private set; }

        public string Pattern { get; private set; }

        public string Preset { get; private set; }

        public HazardShapeSpec? Shape { get; private set; }

        /// <summary>Hazard height above the ground for an ad-hoc region (default 0).</summary>
        public float Hz { get; private set; }

        public int? Seed { get; private set; }

        public HazardArgs Args { get; private set; } = HazardArgs.Empty;

        /// <summary>at=me: the run's centre (cx/cy) is the caller's own position, in landblock-local metres. Only "me" is accepted.</summary>
        public bool AtCaller { get; private set; }

        private static readonly string[] Reserved = { "preset", "region", "hz", "seed", "pattern", "at" };

        /// <summary>
        /// The run's args for a caller standing at <paramref name="caller"/> (landblock-local): <see cref="Args"/>
        /// itself, or with cx/cy set to the caller's x/y when at=me was given. Pure.
        /// </summary>
        public HazardArgs ArgsFor(Vector3 caller)
            => AtCaller
                ? Args.With("cx", caller.X.ToString("0.###", CultureInfo.InvariantCulture)).With("cy", caller.Y.ToString("0.###", CultureInfo.InvariantCulture))
                : Args;

        public static bool TryParse(IReadOnlyList<string> tokens, out HazardCommandRequest request, out string error)
        {
            request = null;
            error = null;

            if (tokens == null || tokens.Count == 0 || string.IsNullOrWhiteSpace(tokens[0]))
            {
                error = "name a hazard";
                return false;
            }

            var r = new HazardCommandRequest { Kind = tokens[0].Trim().ToLowerInvariant() };
            var rest = tokens.Skip(1).Where(t => !string.IsNullOrWhiteSpace(t)).ToList();

            if (rest.Count > 0 && rest[0].IndexOf('=') < 0)
            {
                r.Pattern = rest[0].Trim().ToLowerInvariant();
                rest.RemoveAt(0);
            }

            var stray = rest.FirstOrDefault(t => t.IndexOf('=') <= 0);

            if (stray != null)
            {
                error = $"'{stray}' is not key=value";
                return false;
            }

            var errors = new List<string>();
            var all = HazardArgs.FromTokens(rest, errors);

            if (errors.Count > 0)
            {
                error = errors[0];
                return false;
            }

            if (all.Has("pattern"))
            {
                if (r.Pattern != null)
                {
                    error = "pattern given twice";
                    return false;
                }

                r.Pattern = all.GetString("pattern").ToLowerInvariant();
            }

            r.Preset = all.GetString("preset");

            if (all.Has("region"))
            {
                if (r.Preset != null)
                {
                    error = "give preset= or region=, not both";
                    return false;
                }

                if (!HazardShapeSpec.TryParse(all.GetString("region"), out var shape, out error))
                    return false;

                r.Shape = shape;
            }

            if (all.Has("hz"))
            {
                if (!float.TryParse(all.GetString("hz"), NumberStyles.Float, CultureInfo.InvariantCulture, out var hz) || !float.IsFinite(hz) || Math.Abs(hz) > 50f)
                {
                    error = "hz must be a number of metres in -50..50";
                    return false;
                }

                r.Hz = hz;
            }

            if (all.Has("seed"))
            {
                if (!int.TryParse(all.GetString("seed"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var seed))
                {
                    error = "seed must be an integer";
                    return false;
                }

                r.Seed = seed;
            }

            if (all.Has("at"))
            {
                if (!string.Equals(all.GetString("at"), "me", StringComparison.OrdinalIgnoreCase))
                {
                    error = "at must be 'me' (the run's centre is your position)";
                    return false;
                }

                if (all.Has("cx") || all.Has("cy"))
                {
                    error = "give at=me or cx/cy, not both";
                    return false;
                }

                r.AtCaller = true;
            }

            r.Args = HazardArgs.FromTokens(rest.Where(t => !Reserved.Contains(t.Substring(0, t.IndexOf('=')).Trim(), StringComparer.OrdinalIgnoreCase)));

            request = r;
            return true;
        }

        /// <summary>
        /// The region for a caller at <paramref name="caller"/> on (<paramref name="landblock"/>, <paramref name="instance"/>):
        /// preset= (refused on any other landblock), else region= ad hoc around the caller, else the landblock's own
        /// preset if it has one, else an ad-hoc circle of 15 m. The result is validated (one landblock, sane shape).
        /// </summary>
        public bool TryResolveRegion(ushort landblock, uint instance, bool outdoor, Vector3 caller, out HazardRegion region, out string error)
        {
            region = null;

            var preset = Preset;

            if (preset == null && Shape == null)
                preset = HazardRegionPresets.ForLandblock(landblock);

            if (preset != null)
            {
                if (!HazardRegionPresets.TryGet(preset, instance, out region))
                {
                    error = $"no preset '{preset}'. Presets: {string.Join(", ", HazardRegionPresets.Names)}.";
                    return false;
                }

                if (region.Landblock != landblock)
                {
                    error = $"preset '{preset}' is on landblock 0x{region.Landblock:X4}; you are on 0x{landblock:X4}.";
                    region = null;
                    return false;
                }
            }
            else
            {
                region = HazardRegion.AdHoc(landblock, instance, outdoor, caller, Shape ?? HazardShapeSpec.Circle(HazardDefaults.AdHocCircleRadius), Hz, out error);

                if (region == null)
                    return false;
            }

            error = region.Validate();

            if (error != null)
            {
                region = null;
                return false;
            }

            return true;
        }
    }
}
