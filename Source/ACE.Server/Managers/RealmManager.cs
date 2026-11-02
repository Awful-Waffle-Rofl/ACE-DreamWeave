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
        /// Creates and loads a fresh single-landblock ephemeral instance of a landblock, owned by a player. Every
        /// caller passes a dungeon (indoor) landblock: those have no adjacents, and a single-landblock instance may
        /// not request adjacents (LandblockManager.GetLandblock throws). It is NOT true that an ephemeral instance
        /// never loads adjacents - an outdoor landblock loaded under an ephemeral id gets its neighbours loaded by
        /// RelocateObjectForPhysics on arrival (unless its instance uses the lazy buffer ring, where they load only on
        /// demand, EphemeralInstanceGroup.LoadsAdjacents) - which is why those neighbours are classified against the
        /// instance's footprint (EphemeralInstanceGroup.Classify) and load as bare terrain unless they are footprint members.
        /// For a multi-landblock outdoor copy see <see cref="GetNewEphemeralGroupLandblock"/>.
        /// <paramref name="excludedRows"/>, when not null, is the box of landblock_instance rows this copy drops at load
        /// (see <see cref="EphemeralRowExclusion"/>); it rides on the EphemeralRealm, which exists before the landblock.
        /// </summary>
        public static Entity.Landblock GetNewEphemeralLandblock(ACE.Entity.LandblockId landblockId, WorldObjects.Player owner, ushort? realmId = null, bool openToFellowship = true, EphemeralRowExclusion excludedRows = null)
        {
            var instance = LandblockManager.RequestNewEphemeralInstanceIDv1(realmId ?? owner.HomeRealm);

            var ephemeralRealm = new EphemeralRealm(owner) { OpenToFellowship = openToFellowship, ExcludedRows = excludedRows };

            var landblock = LandblockManager.GetLandblock(landblockId, instance, loadAdjacents: false, permaload: false, ephemeralRealm: ephemeralRealm);

            return landblock;
        }

        /// <summary>
        /// Open World Threads P0 seam: creates a fresh ephemeral instance whose footprint is the square of landblocks
        /// within <paramref name="radius"/> of <paramref name="primary"/>, and loads the primary (with its adjacents,
        /// which are footprint members or the buffer ring). Members load on demand, inheriting this instance's realm;
        /// landblocks around the footprint load as bare terrain. No Thread, PvP or portal path calls this yet - their
        /// IsDungeon refusals stay until P1 - so its only caller is the Developer-gated /enter-instance-group.
        /// </summary>
        internal static Entity.Landblock GetNewEphemeralGroupLandblock(ACE.Entity.LandblockId primary, int radius, WorldObjects.Player owner, ushort? realmId = null, bool openToFellowship = true)
        {
            var instance = LandblockManager.RequestNewEphemeralInstanceIDv1(realmId ?? owner.HomeRealm);

            var ephemeralRealm = new EphemeralRealm(owner)
            {
                OpenToFellowship = openToFellowship,
                Footprint = EphemeralFootprint.Square(primary.Landblock, radius),
            };

            return LandblockManager.GetLandblock(primary, instance, loadAdjacents: true, permaload: false, ephemeralRealm: ephemeralRealm);
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
        /// Open World Threads (R22): a private realm-0 ephemeral GROUP for an outdoor Thread run. The footprint is the
        /// site's (<paramref name="site"/>.Footprint, primary included) and the primary is the site's primary, which is
        /// also the entry's landblock. The run is attached to the EphemeralRealm before the primary is constructed,
        /// exactly as <see cref="GetNewThreadDungeonLandblock"/> does, so the Thread content filter sees it; every member
        /// inherits that realm (EphemeralInstanceGroup.InheritsRealm) and with it the run and its filter.
        ///
        /// The primary loads with its adjacents (members or the buffer ring, per #1753). Under the lazy buffer ring
        /// (run.LazyBufferRing, recorded by TryStart from dynamic_dungeons_outdoor_lazy_buffer_ring) LandblockManager.GetLandblock
        /// skips those adjacency loads for every footprint landblock (EphemeralInstanceGroup.LoadsAdjacents), so only the
        /// footprint loads here and a ring landblock loads later, on demand, still as a bare Buffer. Then EVERY other footprint
        /// member is loaded explicitly, with adjacents, because a member not adjacent to the primary is not reached by
        /// the primary's adjacency load, and the run's population waits for every member
        /// (ThreadRunGeometry.AllCreateCompleted). Members are held for the run's lifetime by the run itself
        /// (EphemeralRealm.HoldsFootprintMembers), not here.
        ///
        /// Returns the primary, or null when it failed to load. When the primary load did not register this realm (an id
        /// collision, logged by GetLandblock) no member is loaded and the primary is returned for the caller to refuse.
        /// </summary>
        public static Entity.Landblock GetNewThreadOutdoorLandblock(ACE.Server.ThreadDungeons.Defs.OutdoorSiteDef site, WorldObjects.Player owner, ACE.Server.ThreadDungeons.ThreadDungeonRun run, uint instance)
        {
            if (site == null)
                return null;

            var ephemeralRealm = new EphemeralRealm(owner)
            {
                OpenToFellowship = false,
                Run = run,
                Footprint = EphemeralFootprint.Create(site.Primary, site.Footprint),
            };

            // ANY load that throws - the primary's own GetLandblock (its Init and its adjacency loads included) or a member's - must
            // not escape into the gem's action chain with a registered, half-built group behind it: it is logged and null is
            // returned, so TryStart takes its refusal path. That path ends the run (releasing the member hold), finds a primary
            // that did register through GetEphemeralLandblock(instance) and watches it down (ThreadDungeonManager.DecideRefusedCopy),
            // and never reaches EndRun/OnRunEnded, so the owner's gem is not destroyed.
            return LoadOutdoorGroup(
                () => LandblockManager.GetLandblock(new ACE.Entity.LandblockId((uint)site.Primary << 16), instance, loadAdjacents: true, permaload: false, ephemeralRealm: ephemeralRealm),
                primary => ReferenceEquals(primary.InnerRealmInfo, ephemeralRealm),
                ephemeralRealm.Footprint.Landblocks, site.Primary,
                member => LandblockManager.GetLandblock(new ACE.Entity.LandblockId((uint)member << 16), instance, loadAdjacents: true),
                (what, ex) => log.Error($"RealmManager: loading {what} of outdoor run instance {instance:X8} threw; refusing the run", ex));
        }

        /// <summary>
        /// The load sequence of <see cref="GetNewThreadOutdoorLandblock"/>, over delegates so its failure paths are testable
        /// without a Landblock. Loads the primary; a throw there is reported through <paramref name="onError"/> and returns null.
        /// A null primary, or one that is not this run's (<paramref name="isThisRunsPrimary"/> false: an id collision), is
        /// returned as is with no member loaded. Otherwise every other member is loaded (<see cref="TryLoadMembers"/>); a throw
        /// there is reported and returns null. Returns the primary when everything loaded.
        /// </summary>
        internal static T LoadOutdoorGroup<T>(System.Func<T> loadPrimary, System.Func<T, bool> isThisRunsPrimary, IEnumerable<ushort> footprint, ushort primaryId,
            System.Action<ushort> loadMember, System.Action<string, System.Exception> onError) where T : class
        {
            T primary;

            try
            {
                primary = loadPrimary();
            }
            catch (System.Exception ex)
            {
                onError?.Invoke($"primary 0x{primaryId:X4}", ex);
                return null;
            }

            if (primary == null || !isThisRunsPrimary(primary))
                return primary;

            if (!TryLoadMembers(footprint, primaryId, loadMember, (member, ex) => onError?.Invoke($"footprint member 0x{member:X4}", ex)))
                return null;

            return primary;
        }

        /// <summary>
        /// Loads every footprint member other than <paramref name="primary"/>, in ascending landblock order, through
        /// <paramref name="load"/>. Stops at the first throw, reports it through <paramref name="onError"/> and returns false;
        /// true when every member loaded. Separated from <see cref="GetNewThreadOutdoorLandblock"/> so the failure path is
        /// testable without a Landblock.
        /// </summary>
        internal static bool TryLoadMembers(IEnumerable<ushort> footprint, ushort primary, System.Action<ushort> load, System.Action<ushort, System.Exception> onError)
        {
            foreach (var member in footprint.OrderBy(lb => lb))
            {
                if (member == primary)
                    continue;

                try
                {
                    load(member);
                }
                catch (System.Exception ex)
                {
                    onError?.Invoke(member, ex);
                    return false;
                }
            }

            return true;
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
