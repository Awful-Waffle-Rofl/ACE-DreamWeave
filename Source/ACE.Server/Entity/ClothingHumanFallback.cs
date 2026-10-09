using System;
using System.Collections.Concurrent;
using System.Collections.Generic;

using ACE.DatLoader;
using ACE.DatLoader.FileTypes;
using ACE.Entity.Enum;

namespace ACE.Server.Entity
{
    /// <summary>
    /// WaffleACE fork: some ClothingTables only list the human setups. A wearer whose body meshes
    /// (parts 0-15, everything below the head) are identical to a human setup can safely use that
    /// human entry; one whose meshes differ (Umbraen) must not.
    /// </summary>
    public static class ClothingHumanFallback
    {
        public const int BodyPartCount = 16;

        private static readonly ConcurrentDictionary<uint, uint?> Cache = new ConcurrentDictionary<uint, uint?>();

        /// <summary>
        /// Picks the table key to use: SetupTableId, then thisSetupId, then the fallback (only
        /// consulted when neither is present, and only accepted if the table has it). Null if none.
        /// </summary>
        public static uint? PickSetupId(uint setupTableId, uint thisSetupId, Func<uint, bool> tableHas, Func<uint, uint?> fallback)
        {
            if (tableHas(setupTableId))
                return setupTableId;
            if (tableHas(thisSetupId))
                return thisSetupId;

            var fb = fallback(thisSetupId);
            return fb.HasValue && tableHas(fb.Value) ? fb : null;
        }

        /// <summary>
        /// Returns the human setup id (HumanMale or HumanFemale) whose parts 0-15 exactly match the
        /// wearer's, provided the table has that key. Otherwise null.
        /// </summary>
        public static uint? Resolve(uint wearerSetupId, Func<uint, IReadOnlyList<uint>> getParts, Func<uint, bool> tableHasSetup)
        {
            var wearer = getParts(wearerSetupId);
            if (wearer == null || wearer.Count < BodyPartCount)
                return null;

            foreach (var human in new[] { (uint)SetupConst.HumanMale, (uint)SetupConst.HumanFemale })
            {
                if (!tableHasSetup(human))
                    continue;

                var humanParts = getParts(human);
                if (humanParts == null || humanParts.Count < BodyPartCount)
                    continue;

                var match = true;
                for (var i = 0; i < BodyPartCount; i++)
                {
                    if (wearer[i] != humanParts[i])
                    {
                        match = false;
                        break;
                    }
                }

                if (match)
                    return human;
            }

            return null;
        }

        /// <summary>
        /// Dat-backed entry point. The wearer-vs-human mesh comparison is cached per wearer setup id;
        /// the per-table "does it contain that key" check is applied after the cached lookup.
        /// </summary>
        public static uint? Resolve(uint wearerSetupId, Func<uint, bool> tableHasSetup)
        {
            var human = Cache.GetOrAdd(wearerSetupId, id => Resolve(id, ReadParts, _ => true));
            if (human.HasValue && tableHasSetup(human.Value))
                return human;

            // The cached best match may be absent from this table while the other human setup
            // (also mesh-identical) is present; the two human setups never share parts 0-15 in practice,
            // so a miss here is a real miss.
            return null;
        }

        private static IReadOnlyList<uint> ReadParts(uint setupId)
        {
            var setup = DatManager.PortalDat.ReadFromDat<SetupModel>(setupId);
            return setup?.Parts;
        }
    }
}
