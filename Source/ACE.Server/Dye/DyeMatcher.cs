using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

using ACE.DatLoader;
using ACE.DatLoader.FileTypes;

using log4net;

namespace ACE.Server.Dye
{
    /// <summary>
    /// Turns a dye colour into a real client palette for one sub-palette range of one item.
    ///
    /// Why not one palette per colour: a scan of every ClothingTable found about 3,800 clothing palettes,
    /// and only about 90 of them keep the same colour across the common ranges; most are a different hue in each
    /// range. So the pick is made per range: among every palette any ClothingTable uses, the one whose colours in
    /// that range are closest to the target colour shaded like the item's own palette there (the method of the
    /// DyeRoom monster recolour tool, ColorTools.MatchCost). The item keeps its light and dark detail.
    ///
    /// Picks are cached by (item palette, range, colour). The candidate pool is built once, in the background at
    /// startup (WarmUp) or on first use; it holds 512 sampled colours per palette, about 6 MB.
    /// </summary>
    public static class DyeMatcher
    {
        private static readonly ILog log = LogManager.GetLogger(System.Reflection.MethodBase.GetCurrentMethod().DeclaringType);

        // Two samples per 8-colour unit (indices 2 and 6), 256 units.
        private const int SamplesPerUnit = 2;
        private const int Samples = 256 * SamplesPerUnit;
        private static int SampleIndex(int s) => (s / SamplesPerUnit) * 8 + (s % SamplesPerUnit == 0 ? 2 : 6);

        private sealed class Candidate
        {
            public uint Id;
            public byte[] Rgb; // Samples * 3
        }

        private static readonly object buildLock = new object();
        private static Candidate[] pool;
        private static Task warm;

        private static readonly ConcurrentDictionary<(uint basePal, int offset, int length, int colour), uint> cache = new();
        private static readonly ConcurrentDictionary<uint, float[]> baseLum = new();

        public static int PoolSize => pool?.Length ?? 0;
        public static int CacheSize => cache.Count;

        /// <summary>Starts building the candidate pool on a background thread. Safe to call more than once.</summary>
        public static void WarmUp()
        {
            lock (buildLock)
                warm ??= Task.Run(() => { try { _ = Pool; } catch (Exception ex) { log.Error("[Dye] candidate pool build failed", ex); } });
        }

        private static Candidate[] Pool
        {
            get
            {
                if (pool != null)
                    return pool;
                lock (buildLock)
                {
                    if (pool == null)
                        pool = Build();
                }
                return pool;
            }
        }

        private static Candidate[] Build()
        {
            var started = DateTime.UtcNow;
            var portal = DatManager.PortalDat;
            var paletteIds = new HashSet<uint>();
            var setIds = new HashSet<uint>();

            foreach (var id in portal.AllFiles.Keys.Where(k => (k >> 24) == 0x10).ToList())
            {
                ClothingTable table;
                try { table = portal.ReadFromDat<ClothingTable>(id); } catch { continue; }
                foreach (var eff in table.ClothingSubPalEffects.Values)
                    foreach (var csp in eff.CloSubPalettes)
                        setIds.Add(csp.PaletteSet);
            }

            foreach (var setId in setIds)
            {
                var set = ReadUncached<PaletteSet>(setId);
                if (set == null) continue;
                foreach (var p in set.PaletteList)
                    paletteIds.Add(p);
            }

            var list = new List<Candidate>(paletteIds.Count);
            foreach (var pid in paletteIds.OrderBy(p => p))
            {
                var pal = ReadUncached<Palette>(pid);
                if (pal == null || pal.Colors.Count < 2048) continue;
                var rgb = new byte[Samples * 3];
                for (int s = 0; s < Samples; s++)
                {
                    var c = pal.Colors[SampleIndex(s)];
                    rgb[s * 3] = (byte)(c >> 16);
                    rgb[s * 3 + 1] = (byte)(c >> 8);
                    rgb[s * 3 + 2] = (byte)c;
                }
                list.Add(new Candidate { Id = pid, Rgb = rgb });
            }

            log.Info($"[Dye] candidate pool: {list.Count} palettes from {setIds.Count} palette sets in {(DateTime.UtcNow - started).TotalMilliseconds:F0} ms");
            return list.ToArray();
        }

        private static T ReadUncached<T>(uint id) where T : FileType, new()
        {
            try
            {
                var dr = DatManager.PortalDat.GetReaderForFile(id);
                if (dr == null) return null;
                var obj = new T();
                using var ms = new MemoryStream(dr.Buffer);
                using var reader = new BinaryReader(ms);
                obj.Unpack(reader);
                return obj;
            }
            catch { return null; }
        }

        private static double Lum(uint c) => 0.3 * ((c >> 16) & 255) + 0.59 * ((c >> 8) & 255) + 0.11 * (c & 255);

        private static float[] BaseLuminance(uint basePaletteId)
        {
            return baseLum.GetOrAdd(basePaletteId, id =>
            {
                var lum = new float[Samples];
                Palette pal = null;
                try { pal = DatManager.PortalDat.ReadFromDat<Palette>(id); } catch { }
                for (int s = 0; s < Samples; s++)
                {
                    var i = SampleIndex(s);
                    lum[s] = pal != null && i < pal.Colors.Count ? (float)Lum(pal.Colors[i]) : 128f;
                }
                return lum;
            });
        }

        /// <summary>
        /// The palette (full 0x04 id) to paint units [unitOffset, unitOffset + unitLength) with so that range reads
        /// as <paramref name="colour"/>. basePaletteId is the palette the item would use there undyed (0x04 id, or
        /// its low 16 bits). A length of 0 means the whole palette.
        /// </summary>
        public static uint Match(uint basePaletteId, int unitOffset, int unitLength, DyeColour colour)
        {
            if ((basePaletteId >> 24) == 0)
                basePaletteId |= 0x04000000;
            if (unitLength <= 0)
                unitLength = 256;

            var key = (basePaletteId, unitOffset, unitLength, DyeColours.IndexOf(colour));
            if (cache.TryGetValue(key, out var hit))
                return hit;

            var picked = Search(basePaletteId, unitOffset, unitLength, colour);
            cache[key] = picked;
            return picked;
        }

        private static uint Search(uint basePaletteId, int unitOffset, int unitLength, DyeColour colour)
        {
            var candidates = Pool;
            if (candidates.Length == 0)
                return basePaletteId;

            int lo = Math.Clamp(unitOffset, 0, 256) * SamplesPerUnit;
            int hi = Math.Clamp(unitOffset + unitLength, 0, 256) * SamplesPerUnit;
            if (hi <= lo)
                return basePaletteId;

            // The target shaded like the item's own palette: k = luminance there / mean luminance of the range.
            var lum = BaseLuminance(basePaletteId);
            double meanL = 0;
            for (int s = lo; s < hi; s++) meanL += lum[s];
            meanL = Math.Max(1, meanL / (hi - lo));

            var n = hi - lo;
            var tr = new double[n]; var tg = new double[n]; var tb = new double[n];
            for (int s = lo; s < hi; s++)
            {
                double k = Math.Clamp(lum[s] / meanL, 0.25, 1.9);
                tr[s - lo] = Math.Min(255, colour.R * k);
                tg[s - lo] = Math.Min(255, colour.G * k);
                tb[s - lo] = Math.Min(255, colour.B * k);
            }

            double best = double.MaxValue;
            uint bestId = basePaletteId;
            foreach (var c in candidates)
            {
                var rgb = c.Rgb;
                double sum = 0;
                for (int s = lo; s < hi; s++)
                {
                    int j = s - lo, o = s * 3;
                    double r = rgb[o], g = rgb[o + 1], b = rgb[o + 2];
                    double rm = (r + tr[j]) / 2, dr = r - tr[j], dg = g - tg[j], db = b - tb[j];
                    sum += (2 + rm / 256) * dr * dr + 4 * dg * dg + (2 + (255 - rm) / 256) * db * db;
                    if (sum >= best * n) break; // already worse than the best
                }
                var cost = sum / n;
                if (cost < best)
                {
                    best = cost;
                    bestId = c.Id;
                }
            }
            return bestId;
        }

        /// <summary>Admin: forget every cached pick (after the colour list or the dats change).</summary>
        public static void ClearCache()
        {
            cache.Clear();
            baseLum.Clear();
        }
    }
}
