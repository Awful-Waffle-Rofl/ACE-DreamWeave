using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;

using log4net;

using ACE.DatLoader;

namespace ACE.Server.Entity
{
    /// <summary>
    /// Parses PropertyString.WornModelOverrides: comma-separated "part:0xGFXOBJ" pairs a worn item
    /// applies to its wearer as per-part model swaps. See the property's own doc comment
    /// (Source/ACE.Entity/Enum/Properties/PropertyString.cs) and Creature.CalculateObjDesc for how the
    /// result is used.
    /// </summary>
    public static class WornModelOverrides
    {
        public const uint GfxObjMin = 0x01000000;
        public const uint GfxObjMax = 0x01FFFFFF;

        /// <summary>
        /// Splits value on ',', then each pair on ':'. The part is decimal 0-255; the model id is hex,
        /// with or without a 0x prefix, and must fall in the GfxObj range 0x01000000-0x01FFFFFF.
        /// Whitespace around either half is tolerated. Malformed pairs are ignored, never thrown on.
        /// A part named twice keeps its LAST pair. The result is ordered by part index, so the
        /// ObjDesc built from it is deterministic.
        /// </summary>
        public static SortedDictionary<byte, uint> Parse(string value)
        {
            var result = new SortedDictionary<byte, uint>();

            if (string.IsNullOrWhiteSpace(value))
                return result;

            foreach (var token in value.Split(','))
            {
                var pair = token.Split(':');

                if (pair.Length != 2)
                    continue;

                if (!int.TryParse(pair[0].Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var part) || part < 0 || part > 255)
                    continue;

                var modelText = pair[1].Trim();

                if (modelText.StartsWith("0x") || modelText.StartsWith("0X"))
                    modelText = modelText.Substring(2);

                if (modelText.Length == 0 || !uint.TryParse(modelText, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var model))
                    continue;

                if (model < GfxObjMin || model > GfxObjMax)
                    continue;

                result[(byte)part] = model;
            }

            return result;
        }

        /// <summary>
        /// Returns the pairs that can actually be applied to a wearer: parts not in exclusions and
        /// models for which modelExists is true. Every pair dropped because its model does not exist
        /// is reported through onMissingModel. Pure - the dat lookup is injected - so it is unit
        /// testable without the client dats.
        /// </summary>
        public static SortedDictionary<byte, uint> Applicable(SortedDictionary<byte, uint> overrides, ICollection<byte> exclusions, Func<uint, bool> modelExists, Action<byte, uint> onMissingModel = null)
        {
            var result = new SortedDictionary<byte, uint>();

            if (overrides == null)
                return result;

            foreach (var pair in overrides)
            {
                if (exclusions != null && exclusions.Contains(pair.Key))
                    continue;

                if (!modelExists(pair.Value))
                {
                    onMissingModel?.Invoke(pair.Key, pair.Value);
                    continue;
                }

                result[pair.Key] = pair.Value;
            }

            return result;
        }

        private static readonly ILog log = LogManager.GetLogger(System.Reflection.MethodBase.GetCurrentMethod().DeclaringType);

        /// <summary>(item wcid, model id) pairs already warned about, so each is logged once per process.</summary>
        private static readonly ConcurrentDictionary<(uint Wcid, uint ModelId), byte> warnedMissing = new ConcurrentDictionary<(uint, uint), byte>();

        /// <summary>
        /// Parse plus Applicable against client_portal.dat: what Creature.CalculateObjDesc actually
        /// applies for an equipped item. A model id the dat does not hold is dropped - sending it would
        /// make the client look up a GfxObj that is not there - and warned about once per (wcid, id).
        /// </summary>
        public static SortedDictionary<byte, uint> ParseApplicable(string value, ICollection<byte> exclusions, uint itemWcid)
        {
            return Applicable(Parse(value), exclusions, id => DatManager.PortalDat.AllFiles.ContainsKey(id), (part, id) =>
            {
                if (warnedMissing.TryAdd((itemWcid, id), 0))
                    log.Warn($"WornModelOverrides: wcid {itemWcid} part {part} names GfxObj 0x{id:X8}, which is not in client_portal.dat; that pair is ignored.");
            });
        }
    }
}
