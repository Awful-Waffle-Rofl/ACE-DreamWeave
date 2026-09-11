using System;

using log4net;

using ACE.Common;
using ACE.Common.Extensions;
using ACE.Database;
using ACE.Server.Factories;
using ACE.Server.Managers;
using ACE.Server.WorldObjects;

namespace ACE.Server.Entity.AccountVault
{
    /// <summary>
    /// What happened when the store asked for one vault container's biota.
    ///
    /// The distinction between <see cref="Absent"/> and <see cref="Failed"/> is the whole reason this
    /// type exists, and collapsing the two back into "null" reintroduces the defect it was added for.
    /// A container biota that is GONE is a dangling index row: the store may drop that one row and
    /// carry on, because a container whose biota does not exist provably holds nothing, and DESIGN 7.2's
    /// orphan purge deletes children whose parent container is missing anyway. A container biota that
    /// could not be READ says nothing about whether it exists, so the store must refuse the whole
    /// account rather than quietly shrink its vault set.
    ///
    /// Treating the two the same in either direction is a real failure. Read as Absent, a transient
    /// shard outage deletes live index rows and hides the player's items. Read as Failed, one missing
    /// biota bricks every OTHER vault on the account permanently, with no admin command and no
    /// self-heal.
    /// </summary>
    public enum VaultContainerLoad
    {
        /// <summary>The biota was read and rehydrated into a Container.</summary>
        Loaded,

        /// <summary>The read succeeded and there is no such biota. The index row is dangling.</summary>
        Absent,

        /// <summary>The read could not be performed, or produced something that is not a Container.</summary>
        Failed,
    }

    /// <summary>
    /// The store's second seam: everything it needs from the world-object layer.
    ///
    /// Split from <see cref="IAccountVaultBackend"/> because the two fail for different reasons and
    /// are faked differently. The backend is four database tables; this one is object construction,
    /// biota persistence and the collapse test, none of which a unit test can reach without a world
    /// database and a running SerializedShardDatabase.
    ///
    /// Nothing here may mention a vendor type. That boundary is what keeps
    /// <see cref="AccountVaultStore"/> testable at all, and Task 7's PersonalVendor depends on it
    /// holding.
    /// </summary>
    public interface IAccountVaultWorldSource
    {
        /// <summary>A brand new object of this wcid with a fresh guid, or null if the wcid is unknown.</summary>
        WorldObject CreateNewWorldObject(uint weenieClassId);

        /// <summary>
        /// Rehydrates one vault container from the shard.
        ///
        /// The return value, NOT the out parameter, is the answer: see <see cref="VaultContainerLoad"/>
        /// for why a missing biota and an unreadable one must never be reported the same way.
        ///
        /// The returned container's inventory load is ASYNC: it reports InventoryLoaded false until it
        /// finishes, which is exactly what risk R4 is about.
        /// </summary>
        VaultContainerLoad LoadContainer(uint containerGuid, out Container container);

        /// <summary>
        /// <see cref="VaultCollapse.IsPristine(WorldObject)"/>. Total by construction: anything it
        /// cannot answer is false, and false keeps the item's biota.
        ///
        /// MUST be called from outside any BiotaDatabaseLock region - see IsPristine's own doc
        /// comment. The store honours that by calling it before it touches any container.
        /// </summary>
        bool IsPristine(WorldObject item);

        /// <summary>
        /// Enqueues a biota save. "Persisted" means the callback fired with true, not that this
        /// returned (DESIGN R5) - the save is a fire-and-forget enqueue onto SerializedShardDatabase's
        /// worker thread, so the call returns long before anything reaches MySQL.
        ///
        /// The callback overload exists for the one caller that cannot proceed on a maybe: registering
        /// an account_vault index row for a container whose biota has not landed leaves the store
        /// permanently unloadable if the process dies in between. Everything else may fire and forget,
        /// because a lost save there costs at worst a re-save.
        ///
        /// <paramref name="callback"/> arrives on the database worker thread, NOT on the caller's.
        /// </summary>
        void SaveBiota(WorldObject worldObject, Action<bool> callback = null);

        /// <summary>Destroys an object and removes its biota. Only ever called on a collapsed deposit.</summary>
        void DestroyItem(WorldObject item);

        /// <summary>
        /// Resolves a character name to its guid, canonical name and owning account. False when no
        /// such character is known.
        /// </summary>
        bool TryResolveCharacter(string characterName, out uint characterGuid, out string canonicalName, out uint accountId);
    }

    /// <summary>
    /// Production world source. Every member is a one-liner over an existing ACE entry point; the
    /// interface exists for the tests, not to add behavior.
    /// </summary>
    public class ShardAccountVaultWorldSource : IAccountVaultWorldSource
    {
        private static readonly ILog log = LogManager.GetLogger(System.Reflection.MethodBase.GetCurrentMethod().DeclaringType);

        public WorldObject CreateNewWorldObject(uint weenieClassId)
        {
            try
            {
                return WorldObjectFactory.CreateNewWorldObject(weenieClassId);
            }
            catch (Exception ex)
            {
                log.Error($"[VAULT] CreateNewWorldObject({weenieClassId}) failed: {ex.GetFullMessage()}");
                return null;
            }
        }

        /// <summary>
        /// Reads the container biota straight off the base ShardDatabase and hands it to
        /// WorldObjectFactory, which is the same shape AllegianceManager uses to rehydrate an
        /// allegiance object (Managers/AllegianceManager.cs:62). The Container constructor then fires
        /// the async inventory load by itself (Container.cs:89-96), so the contents come back for
        /// free and InventoryLoaded is the signal that they have arrived.
        ///
        /// Absent and Failed are separated at exactly the place the two are still distinguishable.
        /// ShardDatabase.GetBiota resolves the row with FirstOrDefault (ShardDatabase.cs:238-241), so a
        /// null return means the row is NOT THERE, while a database that cannot be read throws and is
        /// caught below. Any later layer sees only "null" and can no longer tell them apart.
        ///
        /// The EF biota is handed to WorldObjectFactory.CreateWorldObject unconverted on purpose: that
        /// overload takes ACE.Database.Models.Shard.Biota and converts internally. Converting here
        /// first would be the classic two-Biota-types mistake this codebase keeps making.
        /// </summary>
        public VaultContainerLoad LoadContainer(uint containerGuid, out Container container)
        {
            container = null;

            try
            {
                var biota = DatabaseManager.Shard.BaseDatabase.GetBiota(containerGuid);

                if (biota == null)
                {
                    log.Error($"[VAULT] vault container 0x{containerGuid:X8} has an account_vault row but no biota. Reporting it as ABSENT so the store drops that one index row instead of refusing the whole account.");
                    return VaultContainerLoad.Absent;
                }

                container = WorldObjectFactory.CreateWorldObject(biota) as Container;

                if (container == null)
                {
                    // The biota exists but is not a Container. That is corruption rather than a
                    // dangling row, so it fails closed: deleting the index row here would abandon a
                    // biota that may well still hold the player's items.
                    log.Error($"[VAULT] vault container 0x{containerGuid:X8} has a biota that did not rehydrate as a Container. Refusing rather than dropping the index row.");
                    return VaultContainerLoad.Failed;
                }

                return VaultContainerLoad.Loaded;
            }
            catch (Exception ex)
            {
                log.Error($"[VAULT] LoadContainer(0x{containerGuid:X8}) failed: {ex.GetFullMessage()}");
                return VaultContainerLoad.Failed;
            }
        }

        public bool IsPristine(WorldObject item)
        {
            return VaultCollapse.IsPristine(item);
        }

        /// <summary>
        /// With no callback this is exactly what it always was: WorldObject.SaveBiotaToDatabase, which
        /// flushes the position cache, stamps the save bookkeeping and enqueues
        /// (WorldObject_Database.cs:49-77).
        ///
        /// With a callback it must do the SAME bookkeeping and then enqueue with the caller's
        /// completion signal attached, which is why it calls SaveBiotaToDatabase(enqueueSave: false)
        /// first rather than going straight to the database manager. Skipping that half would leave
        /// ChangesDetected set and the position cache unflushed, so the very next ordinary save would
        /// write a biota this one had already written and the vault's Location (Task 5) could be lost
        /// between the two.
        /// </summary>
        public void SaveBiota(WorldObject worldObject, Action<bool> callback = null)
        {
            if (worldObject == null)
            {
                callback?.Invoke(false);
                return;
            }

            if (callback == null)
            {
                worldObject.SaveBiotaToDatabase();
                return;
            }

            worldObject.SaveBiotaToDatabase(enqueueSave: false);
            worldObject.CheckpointTimestamp = Time.GetUnixTime();

            DatabaseManager.Shard.SaveBiota(worldObject.Biota, worldObject.BiotaDatabaseLock, callback);
        }

        public void DestroyItem(WorldObject item)
        {
            item?.Destroy();
        }

        public bool TryResolveCharacter(string characterName, out uint characterGuid, out string canonicalName, out uint accountId)
        {
            return AccountVaultManager.TryResolveCharacter(characterName, out characterGuid, out canonicalName, out accountId);
        }
    }
}
