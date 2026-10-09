using System;
using System.Reflection;

using ACE.Common;
using ACE.Entity.Enum.Properties;
using ACE.Server.Entity;
using ACE.Server.Factories;
using ACE.Server.Managers;
using ACE.Server.WorldObjects;

using log4net;

using Position = ACE.Entity.Position;

namespace ACE.Server.MlTreasure
{
    /// <summary>
    /// The ML Treasure Hunt drop hook (Docs/Marae-Lassel/TREASURE-HUNT-PLAN.md section 6 "Drop", step 7).
    /// A creature killed by a player on Marae Lassel rarely leaves a treasure map on its corpse.
    ///
    /// THIS RUNS ON THE LANDBLOCK TICK PATH FOR EVERY CREATURE DEATH SERVER-WIDE, FOREVER. The plan calls
    /// it the single highest-risk line in the feature, so the cost of the path that does NOT drop is the
    /// primary design constraint here:
    ///
    ///  - the call site in Creature_Death.CreateCorpse runs MlTreasureLandblock.IsMaraeLassel INLINE and
    ///    calls nothing in this class unless it passes. A non-Marae-Lassel death therefore never enters
    ///    this file at all;
    ///  - inside <see cref="TryDropTreasureMap"/> the order is: killed-by-a-player test (field reads only),
    ///    then the ml_treasure_enabled master switch, then ml_treasure_drop_chance, then the roll, and only
    ///    then the variant roll / weenie creation / site roll. No PropertyManager lookup, no RNG draw and no
    ///    allocation happens before the test above it has already passed.
    ///
    /// <see cref="ShouldDrop"/> is the whole decision as a pure function over already-read values, and it is
    /// what the runtime path actually calls - it is not a parallel copy of the rules that tests could pin
    /// while production diverged. The early returns above it are strict duplicates of its own clauses,
    /// present only so that the expensive reads never happen; each one is annotated with the clause it
    /// short-circuits.
    /// </summary>
    public static class MlTreasureDrop
    {
        private static readonly ILog log = LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);

        /// <summary>
        /// Content/sql/weenies/1004100 ML Treasure Map.sql - a WeenieType.Gem carrying PropertyBool 9060
        /// TreasureMap, which is what Gem.UseGem dispatches on. This constant only NAMES the weenie to
        /// create; nothing in this feature branches on a wcid (TREASURE-HUNT-PLAN.md invariant 4).
        /// </summary>
        public const uint TreasureMapWcid = 1004100;

        /// <summary>
        /// Content/sql/weenies/1004120 Aun Relaria the Unburied.sql - the level 240 CreatureType 57 boss a
        /// Relaria-variant map digs up (TREASURE-HUNT-PLAN.md section 10 steps 9 and 10). Stamped into
        /// PropertyInt 9066 on the map at drop time and answered by MlRelariaSpawner.TrySpawn at the final
        /// dig step.
        ///
        /// Held at 0 until BOTH the boss weenie (step 9) and its spawn (step 10) existed, so the feature
        /// could never ship a map that digs up nothing: 9066 is defined as "> 0 makes this the Relaria
        /// variant and names the boss", and <see cref="IsRelariaVariant"/> still returns false for 0. This
        /// constant only NAMES the weenie to stamp; nothing in this feature branches on a wcid
        /// (TREASURE-HUNT-PLAN.md invariant 4).
        /// </summary>
        public const uint RelariaBossWcid = 1004120;

        // ---- pure decisions ------------------------------------------------------------------------

        /// <summary>
        /// The complete drop decision over already-read values, so every branch is testable without a live
        /// Player, a Corpse, or a PropertyManager (both of the latter are unavailable under unit test -
        /// PropertyManager reads throw). Ordered exactly as TREASURE-HUNT-PLAN.md section 6 specifies:
        /// realm and box first, then the master switch, then the chance.
        /// </summary>
        /// <param name="landblock">raw 16-bit landblock id of the corpse position</param>
        /// <param name="realm">realm id of the corpse position; ML is realm 1 only</param>
        /// <param name="killedByPlayer">true only for a non-Olthoi player killer</param>
        /// <param name="enabled">ml_treasure_enabled</param>
        /// <param name="dropChance">ml_treasure_drop_chance, 0.008 by default (0.8 percent)</param>
        /// <param name="roll">a uniform draw in [0, 1) - ThreadSafeRandom.Next(0.0f, 1.0f)</param>
        public static bool ShouldDrop(ushort landblock, ushort realm, bool killedByPlayer, bool enabled, double dropChance, double roll)
        {
            if (!MlTreasureLandblock.IsMaraeLassel(landblock, realm))
                return false;

            if (!killedByPlayer)
                return false;

            if (!enabled)
                return false;

            // A chance of 0 must never drop, whatever the draw returns - and a roll of exactly 0.0 is a
            // value ThreadSafeRandom.Next(0.0f, 1.0f) can return, so `roll < dropChance` alone is not
            // enough to guarantee that.
            if (dropChance <= 0.0)
                return false;

            return roll < dropChance;
        }

        /// <summary>
        /// Whether a map that is being created should be the rare Aun Relaria boss variant (PropertyInt 9066).
        /// False whenever <paramref name="bossWcid"/> is 0, because 9066 is defined as "> 0 makes this the
        /// Relaria variant and names the boss" (TREASURE-HUNT-PLAN.md section 3) - there is no way to mark a
        /// map as the variant without also naming a real boss.
        /// </summary>
        /// <param name="bossWcid">the boss weenie to stamp, or 0 when none exists yet</param>
        /// <param name="relariaChance">ml_treasure_relaria_chance</param>
        /// <param name="roll">a uniform draw in [0, 1) - ThreadSafeRandom.Next(0.0f, 1.0f)</param>
        public static bool IsRelariaVariant(uint bossWcid, double relariaChance, double roll)
        {
            if (bossWcid == 0)
                return false;

            if (relariaChance <= 0.0)
                return false;

            return roll < relariaChance;
        }

        // ---- runtime -------------------------------------------------------------------------------

        /// <summary>
        /// Rolls for a treasure map and, on a hit, puts one on the corpse. Called from
        /// Creature_Death.CreateCorpse ONLY when MlTreasureLandblock.IsMaraeLassel has already passed at the
        /// call site - see the class remarks for why that gate is inline there rather than here.
        ///
        /// <paramref name="killer"/> is CreateCorpse's lootKiller: DamageHistory.TopDamager (see
        /// Creature_Death.cs:250, Die -> CreateCorpse(topDamager)), resolved through
        /// DamageHistoryInfo.ResolvePetOwnerAsKiller so a CombatPet's kill reads as its owner's. That is the
        /// same player the corpse's own looting rights and its CanGenerateRare branch are keyed to, so the
        /// map lands on a corpse the crediting player may actually open. It is deliberately NOT the
        /// killing-blow player that OnDeath's class-ability / kill-fill hooks use: those are per-player
        /// rewards, this is loot.
        /// </summary>
        /// <param name="corpse">the corpse being built; the map is added to its inventory</param>
        /// <param name="killer">CreateCorpse's lootKiller (top damager, pet kills resolved to their owner)</param>
        /// <param name="landblock">raw landblock id already read at the call site</param>
        /// <param name="realm">realm id already read at the call site</param>
        /// <param name="creatureLevel">the dying creature's own authored/current level (Creature.Level),
        /// used ONLY as the zone fallback for an indoor corpse - see <see cref="ResolveDropZone"/></param>
        public static void TryDropTreasureMap(Corpse corpse, DamageHistoryInfo killer, ushort landblock, ushort realm, int creatureLevel)
        {
            // "Killed by a player" matches the CanGenerateRare test a few lines below the call site
            // (Creature_Death.cs, in this same else branch): killer non-null, killer.IsPlayer, and not an
            // Olthoi player. The Olthoi exclusion is what the rest of CreateCorpse already does with loot -
            // an Olthoi player's kill takes GenerateTreasure_Olthoi (slag) instead of ordinary treasure, and
            // a treasure map is ordinary treasure. All three are field/property reads, no lookup.
            // Short-circuits ShouldDrop's killedByPlayer clause.
            var killedByPlayer = killer != null && killer.IsPlayer && !killer.IsOlthoiPlayer;

            if (!killedByPlayer)
                return;

            // First PropertyManager touch of the whole path, and it is already behind the realm/box gate and
            // the killer test. Short-circuits ShouldDrop's enabled clause.
            var enabled = PropertyManager.GetBool("ml_treasure_enabled").Item;

            if (!enabled)
                return;

            var dropChance = PropertyManager.GetDouble("ml_treasure_drop_chance").Item;

            // Short-circuits ShouldDrop's dropChance clause, so a server that has switched the drop off by
            // setting the chance to 0 never draws from the RNG either.
            if (dropChance <= 0.0)
                return;

            var roll = ThreadSafeRandom.Next(0.0f, 1.0f);

            if (!ShouldDrop(landblock, realm, killedByPlayer, enabled, dropChance, roll))
                return;

            var map = WorldObjectFactory.CreateNewWorldObject(TreasureMapWcid);

            if (map == null)
            {
                log.Error($"[ML_TREASURE] treasure map wcid {TreasureMapWcid} failed to create for the corpse of {corpse.Name} (0x{corpse.Guid.Full:X8}); no map dropped. Is Content/sql/weenies/1004100 ML Treasure Map.sql applied to this world database?");
                return;
            }

            var isRelaria = IsRelariaVariant(RelariaBossWcid,
                PropertyManager.GetDouble("ml_treasure_relaria_chance").Item,
                ThreadSafeRandom.Next(0.0f, 1.0f));

            // Roll the dig site now rather than leaving it to first Use, so the map carries its own site from
            // the moment it exists and two maps on two corpses are independent even if neither is ever used.
            // bossOk is passed so a Relaria map only ever gets a site the boss may legally spawn on
            // (TREASURE-HUNT-PLAN.md section 6 "Boss siting").
            //
            // ROUND 15 (owner ruling): the site is AT MOST ml_treasure_site_radius_metres (720 m, three map
            // units) straight-line from this corpse - the point the map was picked up - whenever a catalogue
            // site is that close. When none is, the site is the corpse position itself if its terrain cell
            // passes the catalogue's own dig-site test, and otherwise the nearest catalogue site at any
            // distance - a corpse in water or on a cliff is never a dig site (StampSite / PlaceNear).
            //
            // A corpse that is not outdoors on Marae Lassel (indoors - GetMapCoords is null for a dungeon
            // cell) leaves the map UNSTAMPED: there is no outdoor pickup point to measure from, so
            // TreasureMapHandler places the site on first use from the reader's own position, by the same rule.
            var pickup = corpse.Location;
            var store = MlTreasureSiteStore.Instance;

            // Zone isolation (owner-approved design): the map's zone is decided once, here, and carried on
            // the map (PropertyInt 9076) so every later placement - the lazy first-use path, /rrtm, and the
            // digsite encounter the completed dig opens - stays confined to it. An outdoor pickup takes its
            // landblock's own catalogue zone (falling back to the nearest catalogue site's zone when the
            // landblock carries no zoned row); an indoor pickup has no outdoor point to measure from at all,
            // so it falls back to the creature's own level band.
            var zone = ResolveDropZone(pickup, creatureLevel, store);

            if (zone != MlTreasureZone.Unknown)
                map.SetProperty(PropertyInt.TreasureMapZone, (int)zone);

            if (!StampSite(map, pickup, isRelaria, store, SiteRadiusMapUnits(), zone: zone))
            {
                // Still a Relaria map: the lazy first-use placement keeps or demotes it by the same rule.
                if (isRelaria)
                    map.SetProperty(PropertyInt.TreasureMapBossWcid, (int)RelariaBossWcid);

                log.Info($"[ML_TREASURE] dropped a treasure map (0x{map.Guid.Full:X8}) unstamped: the corpse at {pickup?.ToLOCString() ?? "<no location>"} is not an outdoor Marae Lassel point, or no site could be placed from it. TreasureMapHandler will place the site on first use.");
            }

            // The real corpse-add pattern in this codebase (Creature_Death.GenerateTreasure). There is no
            // TryAddToCorpse here; the reference design's call by that name does not exist
            // (TREASURE-HUNT-PLAN.md section 5).
            if (!corpse.TryAddToInventory(map))
            {
                log.Warn($"[ML_TREASURE] could not add treasure map 0x{map.Guid.Full:X8} to the corpse of {corpse.Name} (0x{corpse.Guid.Full:X8}); map destroyed");
                map.Destroy();
            }
        }

        /// <summary>
        /// ml_treasure_site_radius_metres converted to the map units the catalogue is stored in. Read in
        /// METRES because every other player-facing distance tunable in this feature is (ml_treasure_far_metres,
        /// ml_treasure_near_metres, ml_treasure_reroll_max_metres). The fallback is the shipped default, so an
        /// unseeded key reads 720 m rather than 0 - which, since round 15, would put every site at its pickup
        /// point rather than restore an island-wide draw.
        /// </summary>
        internal static float SiteRadiusMapUnits()
        {
            var radiusMetres = PropertyManager.GetDouble("ml_treasure_site_radius_metres", MlTreasureSiteStore.MaxSiteDistanceMetres).Item;

            return (float)(radiusMetres / MlTreasureGeometry.MetresPerMapUnit);
        }

        /// <summary>
        /// Whether a position is somewhere a map can be MEASURED from at all: on Marae Lassel in realm 1
        /// (MlTreasureLandblock.IsMaraeLassel) and outdoors (a landblock-surface cell, low word below 0x100,
        /// which is exactly when GetMapCoords returns a value) - the same two tests
        /// TreasureMapHandler.TryHandleUse makes of a reader. This is NOT a terrain test: whether the point
        /// itself may BE a dig site (dry, walkable, clear of buildings) is
        /// <see cref="PickupCellIsDigSite"/>.
        /// </summary>
        public static bool IsOutdoorMaraeLassel(ushort landblock, ushort realm, uint cell)
        {
            if ((cell & 0xFFFF) >= 0x100)
                return false;

            return MlTreasureLandblock.IsMaraeLassel(landblock, realm);
        }

        /// <summary>
        /// Whether the 24 m terrain cell under <paramref name="pickup"/> may itself be a dig site: it is a
        /// catalogued cell (MlTreasureSiteStore.IsCatalogueCell), i.e. it passed the offline water, slope,
        /// building and neighbour test the catalogue was generated with (SurfaceDigSitesCommand.IsDigSite).
        /// A corpse in water, on a too-steep slope or against a building fails. The cell is derived from the
        /// landblock-local X/Y with the same floor-and-clamp SurfaceTerrain.CellId uses.
        /// </summary>
        internal static bool PickupCellIsDigSite(Position pickup, MlTreasureSiteStore store)
        {
            if (pickup == null || store == null)
                return false;

            var cx = Math.Clamp((int)(pickup.PositionX / 24.0f), 0, 7);
            var cy = Math.Clamp((int)(pickup.PositionY / 24.0f), 0, 7);

            return store.IsCatalogueCell(pickup.LandblockId.Landblock, cx, cy);
        }

        /// <summary>
        /// Zone isolation's drop-time zone decision (owner-approved design). An outdoor Marae Lassel pickup
        /// takes its own landblock's catalogue zone (<see cref="MlTreasureSiteStore.ZoneOf"/>); if the
        /// landblock carries no zoned row (a 9-column legacy catalogue, or a landblock the annotator never
        /// reached), it falls back to the zone of the NEAREST catalogue site
        /// (<see cref="MlTreasureSiteStore.ZoneAt"/>). An indoor pickup - or one that is not on Marae Lassel
        /// at all - has no outdoor point to measure from, so it falls back to the dying creature's own
        /// level band (<see cref="MlTreasureZones.FromLevel"/>), which is always a real zone, never Unknown.
        /// </summary>
        internal static MlTreasureZone ResolveDropZone(Position pickup, int creatureLevel, MlTreasureSiteStore store)
        {
            store = store ?? MlTreasureSiteStore.Empty;

            if (pickup != null && IsOutdoorMaraeLassel(pickup.LandblockId.Landblock, pickup.RealmID, pickup.Cell))
            {
                var zone = store.ZoneOf(pickup.LandblockId.Landblock);

                if (zone != MlTreasureZone.Unknown)
                    return zone;

                var mapCoords = pickup.GetMapCoords();

                if (mapCoords != null)
                    return store.ZoneAt(mapCoords.Value);
            }

            return MlTreasureZones.FromLevel(creatureLevel);
        }

        /// <summary>
        /// Stamps a dig site onto <paramref name="map"/> (PropertyFloat 9010/9011) from the pickup point
        /// <paramref name="pickup"/>, by MlTreasureSiteStore.PlaceNear, and stamps or strips the Relaria boss
        /// wcid (PropertyInt 9066) to match. THE shared delivery half of the round 15 rule: the drop hook calls
        /// it with the corpse position, and TreasureMapHandler's lazy first-use path calls it with the reader's
        /// position, so both write the map the same way.
        ///
        /// The site is a catalogue site within the radius when one exists; otherwise the pickup point itself,
        /// but ONLY when its cell passes the catalogue's own terrain test (<see cref="PickupCellIsDigSite"/>);
        /// otherwise the nearest catalogue site at any distance (logged). A corpse underwater or on a cliff
        /// therefore never becomes a dig site.
        ///
        /// A boss map that lands on its pickup point is DEMOTED to an ordinary map (9066 removed, logged):
        /// MlRelariaSpawner refuses any site that is not a boss_ok catalogue row, so leaving it a boss map
        /// would hand the player a map that can never be completed and is never consumed. A boss map that
        /// takes the nearest-site fallback keeps its variant, since that fallback only picks boss_ok rows.
        ///
        /// Returns false, having written nothing, when <paramref name="pickup"/> is not an outdoor realm-1
        /// Marae Lassel point (<see cref="IsOutdoorMaraeLassel"/>), or when nothing at all can be placed (an
        /// empty catalogue and an undiggable pickup cell).
        /// </summary>
        internal static bool StampSite(WorldObject map, Position pickup, bool isRelaria, MlTreasureSiteStore store,
            float radiusMapUnits, Func<int, int> pick = null, MlTreasureZone zone = MlTreasureZone.Unknown)
        {
            if (map == null || pickup == null || !IsOutdoorMaraeLassel(pickup.LandblockId.Landblock, pickup.RealmID, pickup.Cell))
                return false;

            var pickupMapCoords = pickup.GetMapCoords();

            if (pickupMapCoords == null)
                return false;

            store = store ?? MlTreasureSiteStore.Empty;

            var placement = store.PlaceNear(isRelaria, pickupMapCoords.Value, radiusMapUnits, PickupCellIsDigSite(pickup, store), pick, zone);

            if (placement == null)
                return false;

            var placed = placement.Value;

            map.SetProperty(PropertyFloat.TreasureMapNorthSouth, placed.Ns);
            map.SetProperty(PropertyFloat.TreasureMapEastWest, placed.Ew);

            if (isRelaria && !placed.BossVariantDropped)
            {
                map.SetProperty(PropertyInt.TreasureMapBossWcid, (int)RelariaBossWcid);
            }
            else if (placed.BossVariantDropped)
            {
                map.RemoveProperty(PropertyInt.TreasureMapBossWcid);
                log.Warn($"[ML_TREASURE] treasure map 0x{map.Guid.Full:X8}: no boss_ok site within {radiusMapUnits * MlTreasureGeometry.MetresPerMapUnit:0} m of {pickup.ToLOCString()}; the Relaria variant was dropped and the site placed at the pickup point");
            }

            if (placed.AtPickupPoint)
                log.Info($"[ML_TREASURE] treasure map 0x{map.Guid.Full:X8}: no catalogue site within {radiusMapUnits * MlTreasureGeometry.MetresPerMapUnit:0} m of {pickup.ToLOCString()}; the dig site is the pickup point itself");

            if (placed.BeyondRadius)
                log.Warn($"[ML_TREASURE] treasure map 0x{map.Guid.Full:X8}: no catalogue site within {radiusMapUnits * MlTreasureGeometry.MetresPerMapUnit:0} m of {pickup.ToLOCString()} and that point is not diggable terrain; the nearest catalogue site was used ({MlTreasureGeometry.DistanceMetres(pickupMapCoords.Value, placed.MapCoords):0} m away)");

            return true;
        }
    }
}