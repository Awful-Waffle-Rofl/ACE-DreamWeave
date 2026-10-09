using System;
using System.Threading;

using ACE.Common;
using ACE.Database;
using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Entity.Models;
using ACE.Server.Network.GameMessages.Messages;

namespace ACE.Server.WorldObjects
{
    partial class WorldObject
    {
        private readonly bool biotaOriginatedFromDatabase;

        public DateTime LastRequestedDatabaseSave { get; protected set; }

        /// <summary>
        /// This variable is set to true when a change is made, and set to false before a save is requested.<para />
        /// The primary use for this is to trigger save on add/modify/remove of properties.
        /// </summary>
        public bool ChangesDetected { get; set; }

        /// <summary>
        /// Best practice says you should use this lock any time you read/write the Biota.<para />
        /// However, it's only a requirement to do this for properties/collections that will be modified after the initial biota has been created.<para />
        /// There are several properties/collections of the biota that are simply duplicates of the original weenie and are never changed. You wouldn't need to use this lock to read those collections.<para />
        /// <para />
        /// For absolute maximum performance, if you're willing to assume (and risk) the following:<para />
        ///  - that the biota in the database will not be modified (in a way that adds or removes properties) outside of ACE while ACE is running with a reference to that biota<para />
        ///  - that the biota will only be read/modified by a single thread in ACE<para />
        /// You can remove the lock usage for any Get/GetAll Property functions. You would simply use it for Set/Remove Property functions because each of these could end up adding/removing to the collections.<para />
        /// The critical thing is that the collections are not added to or removed from while Entity Framework is iterating over them.<para />
        /// Mag-nus 2018-08-19
        /// </summary>
        public readonly ReaderWriterLockSlim BiotaDatabaseLock = new ReaderWriterLockSlim();

        public bool BiotaOriginatedFromOrHasBeenSavedToDatabase()
        {
            return biotaOriginatedFromDatabase || LastRequestedDatabaseSave != DateTime.MinValue;
        }

        /// <summary>
        /// This will set the LastRequestedDatabaseSave to UtcNow and ChangesDetected to false.<para />
        /// If enqueueSave is set to true, DatabaseManager.Shard.SaveBiota() will be called for the biota.<para />
        /// Set enqueueSave to false if you want to perform all the normal routines for a save but not the actual save. This is useful if you're going to collect biotas in bulk for bulk saving.
        /// </summary>
        public virtual void SaveBiotaToDatabase(bool enqueueSave = true)
        {
            // Make sure all of our positions in the biota are up to date with our current cached values.
            foreach (var kvp in positionCache)
            {
                if (kvp.Value != null)
                    Biota.SetPosition(kvp.Key, kvp.Value, BiotaDatabaseLock);
            }

            LastRequestedDatabaseSave = DateTime.UtcNow;
            ChangesDetected = false;

            if (enqueueSave)
            {
                CheckpointTimestamp = Time.GetUnixTime();
                //DatabaseManager.Shard.SaveBiota(Biota, BiotaDatabaseLock, null);
                DatabaseManager.Shard.SaveBiota(Biota, BiotaDatabaseLock, result =>
                {
                    if (!result)
                    {
                        if (this is Player player)
                        {
                            // This will trigger a boot on next player tick
                            player.BiotaSaveFailed = true;
                        }
                    }
                });
            }
        }

        /// <summary>
        /// This will set the LastRequestedDatabaseSave to MinValue and ChangesDetected to true.<para />
        /// If enqueueRemove is set to true, DatabaseManager.Shard.RemoveBiota() will be called for the biota.<para />
        /// Set enqueueRemove to false if you want to perform all the normal routines for a remove but not the actual removal. This is useful if you're going to collect biotas in bulk for bulk removing.
        /// </summary>
        public void RemoveBiotaFromDatabase(bool enqueueRemove = true)
        {
            // If this entity doesn't exist in the database, let's not queue up work unnecessary database work.
            if (!BiotaOriginatedFromOrHasBeenSavedToDatabase())
            {
                ChangesDetected = true;
                return;
            }

            LastRequestedDatabaseSave = DateTime.MinValue;
            ChangesDetected = true;

            if (enqueueRemove)
                DatabaseManager.Shard.RemoveBiota(Biota.Id, null);
        }

        /// <summary>
        /// A static that should persist to the shard may be a hook with an item, or a house that's been purchased, or a housing chest that isn't empty, etc...<para />
        /// If the world object originated from the database or has been saved to the database, this will also return true.
        /// </summary>
        public bool IsStaticThatShouldPersistToShard()
        {
            if (!Guid.IsStatic())
                return false;

            if (BiotaOriginatedFromOrHasBeenSavedToDatabase())
                return true;

            if (WeenieType == WeenieType.SlumLord && this is SlumLord slumlord)
            {
                if (slumlord.House != null && slumlord.House.HouseOwner.HasValue && slumlord.House.HouseOwner != 0)
                    return true;
            }

            if (WeenieType == WeenieType.House && this is House house)
            {
                if (house.HouseOwner.HasValue && house.HouseOwner != 0)
                    return true;
            }

            if ((WeenieType == WeenieType.Hook || WeenieType == WeenieType.Storage) && this is Container container)
            {
                if (container.Inventory.Count > 0)
                    return true;
            }

            return false;
        }

        /// <summary>
        /// This will filter out the following:<para />
        /// Ammunition and Spell projectiles.<para />
        /// Monster corpses.<para />
        /// Missiles that haven't been saved to the shard yet.<para />
        /// If the world object originated from the database or has been saved to the database, this will also return true.
        /// </summary>
        /// <returns></returns>
        public bool IsDynamicThatShouldPersistToShard()
        {
            if (!Guid.IsDynamic())
                return false;

            // Mule Vendor (WaffleACE, fix round 2/F2): deliberately ABOVE the already-saved
            // short-circuit below, not grouped with the other exclusions after it. A mule is
            // transient by construction - one per summoner, destroyed on logout, on rot, on the
            // leash, and on re-summon - but a shard that ran a build before this exclusion existed
            // can already have mule biotas sitting in `biota` with biotaOriginatedFromDatabase (or
            // LastRequestedDatabaseSave) set. If this check sat below
            // BiotaOriginatedFromOrHasBeenSavedToDatabase(), THAT check would return true first for
            // every one of those pre-existing rows and this exclusion would never run for them: the
            // factory rebuilds them on the next landblock activation (setting
            // biotaOriginatedFromDatabase = true again), Landblock.SaveDB re-saves on every unload,
            // and Landblock.Unload takes the RemoveWorldObjectInternal arm rather than Destroy for a
            // saved object - so the row is never removed and the mule becomes a permanent,
            // store-less zombie vendor. Putting the check first makes it unconditional: a
            // PersonalVendor is excluded regardless of whether it was ever, even accidentally, saved
            // before. This is about the VENDOR object only: vault containers and their contents
            // persist deliberately, by a different path (DESIGN 11.5, risk R9).
            if (this is PersonalVendor)
                return false;

            if (BiotaOriginatedFromOrHasBeenSavedToDatabase())
                return true;

            // Don't save generators, and items that were generated by a generator
            // If the item was generated by a generator and then picked up by a player, the wo.Generator property would be set to null.
            if (IsGenerator || Generator != null)
                return false;

            // WaffleACE world events: everything a running event spawned is transient by construction.
            if (GetProperty(PropertyInt.WorldEventId) != null)
                return false;

            // WaffleACE Threads: run-owned creatures are transient by construction.
            if (GetProperty(PropertyInt.ThreadDungeonRunId) != null)
                return false;

            // WaffleACE ML Treasure Hunt: the Aun Relaria boss dug up by a Relaria-variant treasure map
            // (Docs/Marae-Lassel/TREASURE-HUNT-PLAN.md section 6 "Boss siting"). Stamped by
            // MlRelariaSpawner.TrySpawn before the creature enters the world.
            //
            // Unlike the two exclusions above, this one cannot lean on an ephemeral landblock: Marae Lassel
            // in realm 1 is an ordinary PERSISTENT landblock, so Landblock.SaveDB's IsEphemeral early
            // return never fires there. Without this line a boss left standing at logout would be saved on
            // unload, would then take Landblock.Unload's RemoveWorldObjectInternal arm rather than Destroy,
            // and would respawn from the shard on every later activation of that landblock - a permanent
            // level 240 monster nobody summoned. A player who logs out mid-fight is meant to simply lose it.
            //
            // The `this is Creature` test is LOAD-BEARING, not a tidy-up: PropertyInt 9066 is carried by
            // the treasure MAP as well, where it names the boss to summon, and a map is a Gem. Without the
            // type test this clause would also refuse to persist a Relaria map a player had dropped on the
            // ground (a dropped item IS a top-level landblock object, which is what SaveDB iterates), and
            // the map would vanish on unload. Only the spawned creature is transient.
            if (this is Creature && GetProperty(MlTreasure.MlRelariaSpawner.BossMarker) != null)
                return false;

            // WaffleACE ML digsite encounters: everything a dug-up encounter placed - every creature and the
            // reward chest - is transient by construction. Stamped by MlDigsiteSpawner / MlDigsiteRewards
            // before the object enters the world.
            //
            // This is the PRIMARY guarantee, and it carries the whole weight on its own, because a digsite
            // has neither of the two structural protections the exclusions above can lean on: it is not
            // generator-owned, and it runs on an ordinary PERSISTENT, SHARED outdoor Marae Lassel landblock,
            // so Landblock.SaveDB's IsEphemeral early return never fires the way it does for a Thread copy.
            // Without this line an encounter still standing at unload would be saved, would then take
            // Landblock.Unload's RemoveWorldObjectInternal arm rather than Destroy, and would walk back into
            // the world on every later activation of that landblock - a fight nobody dug up, forever.
            // MlDigsiteOrphanFilter is the backstop for anything a hard kill slips past this.
            //
            // NO `is Creature` narrowing here, unlike the Relaria clause immediately above, and the
            // difference is real rather than an oversight: 9066 is carried by the treasure MAP as well as by
            // the boss, so excluding on the property alone would have stopped a DROPPED map persisting.
            // 9068 has exactly one carrier class - objects the encounter itself placed - and the chest's
            // contents are deliberately left unstamped, so an item looted out of the chest still persists.
            if (GetProperty(PropertyInt.MlDigsiteEncounterId) != null)
                return false;

            // WaffleACE wave encounters: every creature an object-anchored wave encounter spawned is
            // transient by construction, for the same reason as the digsite clause above - the first anchor
            // (the D6 Sounding Drum) stands on a PERSISTENT landblock, the creatures are hand-spawned (no
            // Generator back-reference), and SaveDB's IsEphemeral early return never fires there. Only the
            // spawner stamps 9074, and only on creatures. WaveEncounterOrphanFilter is the backstop.
            if (GetProperty(PropertyInt.WaveEncounterId) != null)
                return false;

            // WaffleACE sky decor: generated in code from a sky_decor_region row on every landblock
            // activation, exactly like an encounter spawn. Nothing about it is worth keeping - it is
            // re-derived identically next time - and saving it would grow the shard by one biota per
            // cloud per load. Encounters get this for free by being generators; a plain Generic decor
            // object does not, so the flag is what stands in for that.
            if (IsSkyDecor)
                return false;

            // WaffleACE death spawns (DeathSpawner, PropertyInt 9070): a creature placed by another
            // creature's death is owned by nobody and is transient by construction. See the field's own
            // remarks in WorldObject.cs for why neither the generator exclusion above nor Landblock.SaveDB's
            // IsEphemeral early return covers it - a Bluespire dungeon is an ordinary persistent landblock.
            if (IsTransientSpawn)
                return false;

            if (WeenieType == WeenieType.Missile || WeenieType == WeenieType.Ammunition || WeenieType == WeenieType.ProjectileSpell || WeenieType == WeenieType.GamePiece
                || WeenieType == WeenieType.Pet || WeenieType == WeenieType.CombatPet)
                return false;

            if (WeenieType == WeenieType.Corpse && this is Corpse corpse && corpse.IsMonster)
                return false;

            if (WeenieType == WeenieType.Portal && this is Portal portal && portal.IsGateway)
                return false;

            // Missiles are unique. The only missiles that are persistable are ones that already exist in the database.
            // TODO: See if we can remove this check by catching the WeenieType above.
            var missile = Missile;
            if (missile.HasValue && missile.Value)
            {
                log.Warn($"Missile: WeenieClassId: {WeenieClassId}, Name: {Name}, WeenieType: {WeenieType}, detected in IsDynamicThatShouldPersistToShard() that wasn't caught by prior check.");
                return false;
            }

            return true;
        }
    }
}
