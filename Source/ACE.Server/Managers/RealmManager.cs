using System.Collections.Generic;
using System.Linq;

using log4net;

using ACE.Database;
using ACE.Entity.Enum;
using ACE.Server.Realms;

namespace ACE.Server.Managers
{
    /// <summary>
    /// ACRealms port (rulesets stubbed): the realm registry, loaded once at boot
    /// from the world database's realm table. Realm 0 is the implicit base world
    /// and always exists, with or without a row.
    /// </summary>
    public static class RealmManager
    {
        private static readonly ILog log = LogManager.GetLogger(System.Reflection.MethodBase.GetCurrentMethod().DeclaringType);

        // replaced wholesale by Initialize/Reload (reference swap); readers on world
        // threads always see a complete registry
        private static volatile Dictionary<ushort, WorldRealm> realms = new Dictionary<ushort, WorldRealm>();

        /// <summary>
        /// Realm ids 0x7FE0 and above are reserved (ACRealms compatibility:
        /// hideouts, the realm selector, and NULL). User realms must stay below this.
        /// </summary>
        public const ushort ReservedRealmIdStart = 0x7FE0;

        public static WorldRealm BaseRealm { get; } = new WorldRealm(0, "Base World", RealmType.Realm);

        public static void Initialize()
        {
            var loaded = new Dictionary<ushort, WorldRealm> { [0] = BaseRealm };

            MergeRegistryRows(loaded);

            realms = loaded;

            log.Info($"RealmManager: {realms.Count - 1} realm(s) loaded from the world database (plus the base world).");
        }

        /// <summary>
        /// Re-reads the realm registry from the world database and merges it additively
        /// into the live registry: new realms are added, existing realms pick up
        /// name/type/parent changes. Realms no longer in the database are kept and
        /// reported - removing a realm stays restart-gated, since live instances and
        /// landblocks may still reference it.
        /// </summary>
        public static (int added, int updated, List<ushort> missing) Reload()
        {
            var merged = new Dictionary<ushort, WorldRealm>(realms);

            var (added, updated, seen) = MergeRegistryRows(merged);

            var missing = merged.Keys.Where(id => id != 0 && !seen.Contains(id)).OrderBy(id => id).ToList();

            realms = merged;

            log.Info($"RealmManager: registry reloaded - {added} added, {updated} updated, {missing.Count} missing from database (kept), {merged.Count - 1} total.");

            return (added, updated, missing);
        }

        /// <summary>
        /// Loads the realm table into <paramref name="target"/> (add or replace),
        /// skipping realm 0 and unsupported Ruleset rows.
        /// </summary>
        private static (int added, int updated, HashSet<ushort> seen) MergeRegistryRows(Dictionary<ushort, WorldRealm> target)
        {
            var records = DatabaseManager.World.GetAllRealms();

            var added = 0;
            var updated = 0;
            var seen = new HashSet<ushort>();

            foreach (var record in records)
            {
                if (record.Id == 0)
                    continue;   // realm 0 is always the implicit base world

                var type = (RealmType)record.Type;

                if (type == RealmType.Ruleset)
                {
                    // rulesets are stubbed in this port; registry rows of this type are inert
                    log.Warn($"RealmManager: realm {record.Id} ({record.Name}) has type Ruleset, which is not supported yet - ignoring");
                    continue;
                }

                seen.Add(record.Id);

                if (target.TryGetValue(record.Id, out var existing))
                {
                    if (existing.Name == record.Name && existing.Type == type && existing.ParentRealmId == record.ParentRealmId)
                        continue;

                    updated++;
                }
                else
                    added++;

                target[record.Id] = new WorldRealm(record.Id, record.Name, type, record.ParentRealmId);
            }

            return (added, updated, seen);
        }

        public static WorldRealm GetRealm(ushort realmId)
        {
            realms.TryGetValue(realmId, out var realm);
            return realm;
        }

        public static List<WorldRealm> GetAllRealms()
        {
            return realms.Values.OrderBy(r => r.Id).ToList();
        }

        /// <summary>
        /// Creates and loads a fresh ephemeral instance of a landblock, owned by a player.
        /// Indoor (dungeon) landblocks only - ephemeral instances never load adjacents.
        /// </summary>
        public static Entity.Landblock GetNewEphemeralLandblock(ACE.Entity.LandblockId landblockId, WorldObjects.Player owner, ushort? realmId = null, bool openToFellowship = true)
        {
            var instance = LandblockManager.RequestNewEphemeralInstanceIDv1(realmId ?? owner.HomeRealm);

            var ephemeralRealm = new EphemeralRealm(owner) { OpenToFellowship = openToFellowship };

            var landblock = LandblockManager.GetLandblock(landblockId, instance, loadAdjacents: false, permaload: false, ephemeralRealm: ephemeralRealm);

            return landblock;
        }

        /// <summary>
        /// Threads (WaffleACE): a private realm-0 copy of a curated dungeon for one owner. The run is
        /// attached to the EphemeralRealm BEFORE the landblock is constructed, because Landblock.CreateWorldObjects
        /// reads InnerRealmInfo.Run to filter the copy's content and there is no later moment that is early
        /// enough. Single-player MVP: OpenToFellowship is false. Realm 0 always (PLAN section 11, Q5).
        ///
        /// The instance id is allocated by the caller (ThreadDungeonManager.TryStart, via
        /// LandblockManager.RequestNewEphemeralInstanceIDv1(0)) before the run is constructed, because
        /// run.RunId IS the instance id and the run object has to exist to be attached here.
        /// </summary>
        public static Entity.Landblock GetNewThreadDungeonLandblock(ACE.Entity.LandblockId landblockId, WorldObjects.Player owner, ACE.Server.ThreadDungeons.ThreadDungeonRun run, uint instance)
        {
            var ephemeralRealm = new EphemeralRealm(owner) { OpenToFellowship = false, Run = run };

            return LandblockManager.GetLandblock(landblockId, instance, loadAdjacents: false, permaload: false, ephemeralRealm: ephemeralRealm);
        }

        /// <summary>
        /// puzzlegate-sweep's throwaway copy of a Thread dungeon: a fresh realm-0 ephemeral instance that loads the same
        /// content a run's copy loads (EphemeralRealm.RunlessThreadCopyExitPortalWcid drives the Thread content filter,
        /// no shard statics, no encounters) but with NO run, no owner and nobody admitted (Accepts refuses everyone:
        /// a null Owner and an empty allowed list). Permaloaded so it neither goes dormant nor unloads while the sweep
        /// works; the caller clears Permaload and queues it for destruction when done.
        /// </summary>
        public static Entity.Landblock GetNewThreadSweepLandblock(ACE.Entity.LandblockId landblockId, uint exitPortalWcid, out uint instance)
        {
            instance = LandblockManager.RequestNewEphemeralInstanceIDv1(0);

            var ephemeralRealm = new EphemeralRealm(null) { OpenToFellowship = false, RunlessThreadCopyExitPortalWcid = exitPortalWcid };

            return LandblockManager.GetLandblock(landblockId, instance, loadAdjacents: false, permaload: true, ephemeralRealm: ephemeralRealm);
        }
    }
}
