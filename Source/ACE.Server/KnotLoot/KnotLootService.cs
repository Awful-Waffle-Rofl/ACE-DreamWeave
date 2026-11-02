using System.Collections.Generic;

using ACE.Common;
using ACE.Server.Factories;
using ACE.Server.WorldObjects;

using log4net;

namespace ACE.Server.KnotLoot
{
    /// <summary>The live settings: every read goes through <see cref="KnotLootTunables"/>, the only reader of the property store.</summary>
    public sealed class LiveKnotLootSettings : IKnotLootSettings
    {
        public bool TableEnabled(string table) => KnotLootTunables.TableEnabled(table);

        public long Weight(string table, string entryKey, long fallback) => KnotLootTunables.Weight(table, entryKey, fallback);

        public double Chance(string table, string groupKey, double fallback) => KnotLootTunables.Chance(table, groupKey, fallback);

        public (long Min, long Max) Currency(string table, string currencyKey, (long, long) fallback) => KnotLootTunables.Currency(table, currencyKey, fallback);

        public KnotWindowSettings Window(string table) => KnotLootTunables.Window(table);
    }

    public static class KnotLootService
    {
        private static readonly ILog log = LogManager.GetLogger(System.Reflection.MethodBase.GetCurrentMethod().DeclaringType);

        private static readonly LiveKnotLootSettings Settings = new LiveKnotLootSettings();

        /// <summary>
        /// Fresh, unplaced items for a won fight at <paramref name="knotLevel"/>, or an empty list when the table is
        /// unknown, null or switched off. The caller owns the items.
        /// </summary>
        public static List<WorldObject> RollLive(string tableKey, int knotLevel)
        {
            if (!KnotLootTables.TryGet(tableKey, out var table))
            {
                log.Warn($"[KNOTLOOT] unknown or missing reward table '{tableKey}'; the cache will be empty");
                return new List<WorldObject>();
            }

            var drops = KnotLootRoller.Roll(table, knotLevel, Settings, () => ThreadSafeRandom.Next(0.0f, 1.0f));

            return KnotLootMaterializer.Create(drops, table.Key, knotLevel, WorldObjectFactory.CreateNewWorldObject);
        }
    }
}
