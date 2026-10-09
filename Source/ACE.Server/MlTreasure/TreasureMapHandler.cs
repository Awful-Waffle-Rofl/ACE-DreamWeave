using System;
using System.Numerics;
using System.Reflection;

using ACE.Common;
using ACE.DatLoader;
using ACE.DatLoader.FileTypes;
using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Server.Entity;
using ACE.Server.Entity.Actions;
using ACE.Server.Factories;
using ACE.Server.Managers;
using ACE.Server.Network.GameMessages.Messages;
using ACE.Server.WorldEvents;
using ACE.Server.WorldObjects;

using log4net;

namespace ACE.Server.MlTreasure
{
    /// <summary>
    /// The ML Treasure Hunt map handler (Docs/Marae-Lassel/TREASURE-HUNT-PLAN.md section 6). Dispatched
    /// from Gem.UseGem behind PropertyBool.TreasureMap, following the MuleSummonHandler /
    /// ThreadDungeonGemHandler precedent - no new WorldObject subclass.
    ///
    /// THERE IS NO SHOVEL (owner decision 11): a map is used on location with no wield requirement, no
    /// shovel weenie, and no equipped-item check anywhere in this method.
    /// </summary>
    public static class TreasureMapHandler
    {
        private static readonly ILog log = LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);

        /// <summary>
        /// The ML treasure currency: wcid 1004101, Marae Lassel Doubloon, authored on branch
        /// content/ml-treasure-vendors together with the two vendors that accept it. THIS IS A
        /// CROSS-BRANCH DEPENDENCY: until that content lands in the target ace_world, a completed dig
        /// logs an error, tells the player nothing was created, and deliberately does NOT consume the
        /// map, so a dig is refused rather than losing the player their item.
        ///
        /// This was 0 until 2026-09-10. Nothing in the original 15-step plan ever built the currency
        /// the whole economy trades in - the gap was found when the vendor step went to specify its
        /// prices and had nothing to denominate them in.
        /// </summary>
        public const uint TreasureCurrencyWcid = 1004101;

        /// <returns>true when this was a treasure map (handled or refused); false when UseGem should continue.</returns>
        public static bool TryHandleUse(WorldObject wo, Player player)
        {
            if (wo.GetProperty(PropertyBool.TreasureMap) != true)
                return false;

            if (!PropertyManager.GetBool("ml_treasure_enabled").Item)
            {
                player.SendTransientError("Treasure maps are not currently enabled on this server.");
                return true;
            }

            // The gate is NOT optional (TREASURE-HUNT-PLAN.md sections 4 and 6): retail Marae Lassel
            // exists in realm 0, and PositionExtensions.GetMapCoords() is realm-agnostic - it reads only
            // pos.ToGlobal(). Without this check, a player standing on the matching physical spot in the
            // realm-0 island gets the same Dig classification and payout without ever entering realm 1.
            // Accessors match the KillFillVessel.Accepts precedent (Source/ACE.Server/Entity/
            // KillFillVessel.cs) for reading realm/landblock off a Position: LandblockId.Landblock and
            // RealmID, not anything invented here.
            if (!MlTreasureLandblock.IsMaraeLassel(player.Location.LandblockId.Landblock, player.Location.RealmID))
            {
                player.Session.Network.EnqueueSend(new GameMessageSystemChat(
                    "The map means nothing here. Whatever it shows, it is not this place.", ChatMessageType.Broadcast));

                return true;
            }

            // Distance is computed ENTIRELY in map-coordinate space - never reconstruct a Position from
            // the stored ns/ew to measure it (GetMapCoords and new Position(Vector2) are not exact
            // inverses; the round trip drifts about 12 m, TREASURE-HUNT-PLAN.md section 6).
            //
            // Checked BEFORE the lazy placement below (round 15): that placement measures from the reader's
            // own position, so an indoor reader - who has no map coordinates - must be refused first.
            var playerMapCoords = player.Location.GetMapCoords();

            if (playerMapCoords == null)
            {
                player.SendTransientError("You cannot read a treasure map indoors.");
                return true;
            }

            // Place and stamp the site on first use of this map instance. Held per-instance on the
            // biota (9010/9011), never re-rolled here - that is what makes the map non-stackable
            // (TREASURE-HUNT-PLAN.md section 2.8).
            if (!EnsureSiteStamped(wo, player.Location))
            {
                log.Warn($"[ML_TREASURE] {player.Name} used a treasure map (0x{wo.Guid.Full:X8}) with no site, and {player.Location.ToLOCString()} is not a point one could be placed from");
                player.SendTransientError("This map is blank. Something is wrong here - tell an admin.");
                return true;
            }

            var ns = wo.GetProperty(PropertyFloat.TreasureMapNorthSouth);
            var ew = wo.GetProperty(PropertyFloat.TreasureMapEastWest);

            // GetProperty(PropertyFloat) is double? in this codebase; the site coordinates themselves
            // are stored as float precision (MlTreasureSiteStore.Site), so narrow back down here.
            var siteNs = (float)ns.Value;
            var siteEw = (float)ew.Value;

            var siteMapCoords = new Vector2(siteEw, siteNs); // X=EastWest, Y=NorthSouth
            var distanceMetres = MlTreasureGeometry.DistanceMetres(playerMapCoords.Value, siteMapCoords);

            var farMetres = (float)PropertyManager.GetDouble("ml_treasure_far_metres").Item;
            var nearMetres = (float)PropertyManager.GetDouble("ml_treasure_near_metres").Item;

            switch (MlTreasureGeometry.Classify(distanceMetres, farMetres, nearMetres))
            {
                case MlTreasureGeometry.Stage.Far:
                {
                    var nearest = WorldEventTownIndex.Nearest(player.Location, WorldEventTownIndex.Towns);
                    var bearing = MlTreasureGeometry.Bearing8(playerMapCoords.Value, siteMapCoords);
                    var landmark = nearest == null ? "your current position" : nearest.Value.Town.Name;

                    player.Session.Network.EnqueueSend(new GameMessageSystemChat(
                        $"You are near {landmark}. The map shows the site lies far off to the {bearing}.", ChatMessageType.Broadcast));

                    return true;
                }

                case MlTreasureGeometry.Stage.Cardinal:
                {
                    var bearing = MlTreasureGeometry.Bearing8(playerMapCoords.Value, siteMapCoords);

                    player.Session.Network.EnqueueSend(new GameMessageSystemChat(
                        $"The map shows the site is to the {bearing}. You are getting close.", ChatMessageType.Broadcast));

                    return true;
                }

                default: // Dig
                    RunDigStep(wo, player);
                    return true;
            }
        }

        /// <summary>
        /// Pure clamp for PropertyInt.TreasureMapDigProgress: never store more than digSteps. FinishDig
        /// can return without consuming the map (the boss branch, the zero-currency-wcid branch, a full
        /// pack) and every one of those is a Dig stage the player can reach again, so without this the
        /// stored counter climbs one higher on every repeated use - harmless in effect (still >= digSteps
        /// on every later check), but not the intended stored value. Code review 2026-09-10, finding 2.
        /// </summary>
        internal static int ClampDigProgress(int rawProgress, int digSteps) => rawProgress > digSteps ? digSteps : rawProgress;

        /// <summary>
        /// Makes sure <paramref name="map"/> carries a dig site, placing one from <paramref name="readerPosition"/>
        /// when it does not. True when the map already had one or one was placed.
        ///
        /// ROUND 15: a map that reaches first use unstamped - one that dropped on an indoor corpse (no outdoor
        /// pickup point exists for it), or an old map that dropped while the catalogue was empty - is placed by
        /// THE SAME rule as a fresh drop (MlTreasureDrop.StampSite: a catalogue site within 720 m, else the
        /// point itself if its cell is diggable terrain, else the nearest catalogue site), measured from the
        /// reader's position. That is the "player's position when no pickup
        /// point was stored" case: an outdoor drop always stamps its site at drop time, so no map ever carries
        /// a pickup point without also carrying the site placed from it, and there is no separate pickup-point
        /// property to read back here. Before round 15 this path drew uniformly across the whole island.
        ///
        /// Never re-places a map that already has both coordinates - the per-instance site is what keeps maps
        /// non-stackable and what /rrtm measures against.
        /// </summary>
        internal static bool EnsureSiteStamped(WorldObject map, ACE.Entity.Position readerPosition,
            MlTreasureSiteStore store = null, float? radiusMapUnits = null, Func<int, int> pick = null)
        {
            if (map.GetProperty(PropertyFloat.TreasureMapNorthSouth) != null && map.GetProperty(PropertyFloat.TreasureMapEastWest) != null)
                return true;

            var isRelaria = (map.GetProperty(PropertyInt.TreasureMapBossWcid) ?? 0) > 0;

            store = store ?? MlTreasureSiteStore.Instance;

            // Zone isolation (owner-approved design): a map already carries its zone (PropertyInt 9076,
            // stamped at drop time) whenever it has one - that is read back and used to filter this lazy
            // placement to the SAME zone the map was found in. A map with no stamped zone at all (dropped
            // before this property existed) falls back to the reader's OWN landblock zone, and that zone is
            // then stamped onto the map so every later lookup (a future re-stamp, though EnsureSiteStamped
            // itself never re-places a stamped map, and /rrtm) agrees with what was actually used here.
            var storedZone = map.GetProperty(PropertyInt.TreasureMapZone);
            var zone = storedZone != null
                ? (MlTreasureZone)storedZone.Value
                : store.ZoneOf(readerPosition.LandblockId.Landblock);

            if (storedZone == null && zone != MlTreasureZone.Unknown)
                map.SetProperty(PropertyInt.TreasureMapZone, (int)zone);

            return MlTreasureDrop.StampSite(map, readerPosition, isRelaria, store,
                radiusMapUnits ?? MlTreasureDrop.SiteRadiusMapUnits(), pick, zone);
        }

        /// <summary>
        /// One dig step: an ActionChain setting IsBusy, holding the player in the Pickup sub-state for
        /// the animation, running the step body, then returning to Ready before clearing IsBusy and
        /// sending use-done. Increments PropertyInt.TreasureMapDigProgress once per call; the final step
        /// pays out (or spawns the boss) via <see cref="FinishDig"/>.
        ///
        /// Pickup (0x40000018) is a held sub-state, not a one-shot command (MotionInterp.motion_allows_jump
        /// groups it with Reload..Pickup among the substates that block jumping), so
        /// SendMotionAsCommands - which wraps the motion as a transient command via
        /// MotionState.AddCommand - never returns the player out of it. The retail pickup path instead
        /// broadcasts Pickup directly, then explicitly broadcasts Motion(stance, Ready) afterward
        /// (Player_Inventory.cs EnqueuePickupDone); this mirrors that, not RecipeManager.
        /// </summary>
        private static void RunDigStep(WorldObject wo, Player player)
        {
            var digSteps = (int)Math.Clamp(PropertyManager.GetLong("ml_treasure_dig_steps").Item, 1, int.MaxValue);
            var rawProgress = (wo.GetProperty(PropertyInt.TreasureMapDigProgress) ?? 0) + 1;
            var progress = ClampDigProgress(rawProgress, digSteps);

            wo.SetProperty(PropertyInt.TreasureMapDigProgress, progress);

            player.IsBusy = true;

            var actionChain = new ActionChain();

            const MotionCommand motion = MotionCommand.Pickup;
            var currentStance = player.CurrentMotionState.Stance;

            var motionTable = DatManager.PortalDat.ReadFromDat<MotionTable>(player.MotionTableId);
            var animLength = motionTable.GetAnimationLength(currentStance, motion, MotionCommand.Ready);

            actionChain.AddAction(player, () => player.EnqueueBroadcastMotion(new Motion(currentStance, motion)));
            actionChain.AddDelaySeconds(animLength);

            actionChain.AddAction(player, () =>
            {
                // The Ready broadcast below re-reads the player's CURRENT stance rather than reusing the
                // captured currentStance above: HandleActionChangeCombatMode carries no IsBusy gate, so a
                // player who switches stance mid-dig would otherwise be snapped back to the stance they
                // were in when the dig step started, not the one they are actually in now.
                //
                // The Ready broadcast, SendUseDoneEvent and IsBusy=false are in a finally so a throw out of
                // FinishDig (payout, boss spawn, encounter open) can never strand the player mid-Pickup with
                // IsBusy stuck true - the pose fix above would otherwise reintroduce the same stuck-pose bug
                // it was meant to close, just through an exception path instead of a missing broadcast.
                try
                {
                    if (progress >= digSteps)
                        FinishDig(wo, player);
                    else
                    {
                        player.Session.Network.EnqueueSend(new GameMessageSystemChat(
                            $"You dig at the site. ({progress}/{digSteps})", ChatMessageType.Broadcast));
                    }
                }
                catch (Exception ex)
                {
                    log.Error($"[ML_TREASURE] {player.Name} dig step threw at progress {progress}/{digSteps}", ex);
                }
                finally
                {
                    player.EnqueueBroadcastMotion(new Motion(player.CurrentMotionState.Stance, MotionCommand.Ready));

                    player.SendUseDoneEvent();
                    player.IsBusy = false;
                }
            });

            actionChain.EnqueueChain();
        }

        /// <summary>
        /// The last dig step. A boss-variant map (PropertyInt.TreasureMapBossWcid > 0) never pays out:
        /// it summons the creature named by 9066 at the dig site instead (TREASURE-HUNT-PLAN.md section 6
        /// "Boss siting", step 10) and currency comes only from an ordinary dig. An ordinary map pays
        /// ThreadSafeRandom.Next(payout_min, payout_max) treasure currency into the pack and is
        /// consumed.
        ///
        /// Both branches consume the map ONLY on success. A refused boss spawn (the catalogue is missing,
        /// the stored site is not boss_ok, the boss weenie is not in the world database) leaves the map in
        /// the pack so the dig can be retried once the server is fixed, exactly as the zero-currency-wcid
        /// and full-pack branches below do.
        /// </summary>
        /// <summary>
        /// Whether a rolled dig payout creates any currency at all. Round 17 owner ruling shipped the payout
        /// at 0 / 0, so a zero roll is now the NORMAL case: it must create no currency object (a zero-size
        /// stack), send no coin line and no full-pack refusal, and still let the dig consume the map and open
        /// the digsite encounter.
        /// </summary>
        internal static bool PaysCurrency(int count) => count > 0;

        private static void FinishDig(WorldObject wo, Player player)
        {
            var bossWcid = wo.GetProperty(PropertyInt.TreasureMapBossWcid) ?? 0;

            // Read off the map BEFORE it is consumed below (the boss branch never consumes it via
            // TryConsumeFromInventoryWithNetworking - it uses this same read, further down; the ordinary
            // branch does, later in this method). /testtreasuremap (MlTreasureTestCommands.cs) stamps these
            // on a test map; an ordinary dropped or rerolled map never carries them.
            var isAdminTest = wo.GetProperty(PropertyBool.TreasureMapAdminTest) == true;

            if (bossWcid > 0)
            {
                // The site pair is read back off the map rather than recomputed: TryHandleUse has already
                // rolled and stamped it (or found it stamped at drop time), and MlRelariaSpawner needs it
                // only to confirm the site is a boss_ok catalogue row. X=EastWest, Y=NorthSouth, the axis
                // order MlTreasureSiteStore.Site.MapCoords uses.
                var siteNorthSouth = wo.GetProperty(PropertyFloat.TreasureMapNorthSouth) ?? 0.0;
                var siteEastWest = wo.GetProperty(PropertyFloat.TreasureMapEastWest) ?? 0.0;

                if (!MlRelariaSpawner.TrySpawn(player, bossWcid, new Vector2((float)siteEastWest, (float)siteNorthSouth), isAdminTest))
                {
                    player.Session.Network.EnqueueSend(new GameMessageSystemChat(
                        "The ground shudders, but nothing answers. Whatever was buried here did not stir.", ChatMessageType.Broadcast));

                    return;
                }

                player.Session.Network.EnqueueSend(new GameMessageSystemChat(
                    "The earth splits. Something long buried claws its way out of your dig.", ChatMessageType.Broadcast));

                // Tally of the Unburied (MlRelariaChargeTrophy): every successfully consumed map adds a
                // charge, digger only. Wrapped so a charge-side failure never costs the player their
                // already-consumed map or the boss they just summoned.
                try
                {
                    if (player.TryConsumeFromInventoryWithNetworking(wo, 1))
                        MlRelariaChargeTrophy.TryAddMapCharge(player);
                }
                catch (Exception ex)
                {
                    log.Error($"[ML_TREASURE] {player.Name}'s Tally of the Unburied charge failed after consuming boss-variant map 0x{wo.Guid.Full:X8}", ex);
                }

                return;
            }

            if (TreasureCurrencyWcid == 0)
            {
                log.Error($"[ML_TREASURE] {player.Name} finished digging map 0x{wo.Guid.Full:X8} but TreasureMapHandler.TreasureCurrencyWcid is still 0 (currency weenie not yet built); payout skipped, map NOT consumed");

                player.Session.Network.EnqueueSend(new GameMessageSystemChat(
                    "You've found something, but there is nowhere to put it yet. (Treasure currency is not yet implemented on this server.)", ChatMessageType.Broadcast));

                return;
            }

            // Explicit fallbacks equal to the registered defaults (round 17 owner ruling: 0 / 0 - a map's
            // doubloons now come only from the digsite chest), so an unseeded read cannot disagree with them.
            var payoutMin = (int)Math.Clamp(PropertyManager.GetLong("ml_treasure_payout_min", 0).Item, 0, int.MaxValue);
            var payoutMax = (int)Math.Clamp(PropertyManager.GetLong("ml_treasure_payout_max", 0).Item, payoutMin, int.MaxValue);

            var count = ThreadSafeRandom.Next(payoutMin, payoutMax);

            // Also read off the map before it is consumed below (the /testtreasuremap forcing props - see
            // isAdminTest above). Absent on every ordinary map, which is what makes DecodeForcedType null
            // and forcedMechanicSetId 0 - MlDigsiteManager.TryStart's own defaults, so an ordinary dig's
            // encounter is rolled exactly as it always was.
            var forcedType = MlDigsite.MlDigsiteRules.DecodeForcedType(wo.GetProperty(PropertyInt.TreasureMapForcedDigsiteType) ?? 0);
            var forcedMechanicSetId = (long)(wo.GetProperty(PropertyInt.TreasureMapForcedMechanicSet) ?? 0);

            // A roll of 0 pays NOTHING and says nothing: no currency object is created (SetStackSize(0) would
            // hand the pack a zero-size stack), no "You unearth 0 treasure coins!" line, and no "pack is too
            // full" refusal for a payout that does not exist. The map is still consumed and the digsite
            // encounter still opens below, exactly as on a paying dig.
            if (PaysCurrency(count))
            {
                var currency = WorldObjectFactory.CreateNewWorldObject(TreasureCurrencyWcid);

                if (currency == null)
                {
                    log.Error($"[ML_TREASURE] {player.Name} finished digging map 0x{wo.Guid.Full:X8} but treasure currency wcid {TreasureCurrencyWcid} failed to create");
                    return;
                }

                currency.SetStackSize(count);

                if (!player.TryCreateInInventoryWithNetworking(currency))
                {
                    currency.Destroy();

                    player.Session.Network.EnqueueSend(new GameMessageSystemChat(
                        "Your pack is too full to hold the treasure. Make room and dig again.", ChatMessageType.Broadcast));

                    return;
                }

                player.Session.Network.EnqueueSend(new GameMessageSystemChat(
                    $"You unearth {count} treasure coins!", ChatMessageType.Broadcast));
            }

            // Tally of the Unburied (MlRelariaChargeTrophy): every successfully consumed map adds a
            // charge, digger only. Wrapped so a charge-side failure never costs the player their
            // already-paid-out, already-consumed map.
            try
            {
                if (player.TryConsumeFromInventoryWithNetworking(wo, 1))
                    MlRelariaChargeTrophy.TryAddMapCharge(player);
            }
            catch (Exception ex)
            {
                log.Error($"[ML_TREASURE] {player.Name}'s Tally of the Unburied charge failed after consuming map 0x{wo.Guid.Full:X8}", ex);
            }

            // ML digsite encounter. STRICTLY ADDITIVE and strictly LAST: the doubloon payout above still
            // happens in full and the map has already been consumed, so a refused encounter - the digger
            // already has one running, another is too close, the server-wide cap is full, or the whole
            // feature is switched off - costs the player neither their map nor their coins. There is nothing
            // to roll back, which is exactly why this sits at the end of the method rather than gating it.
            //
            // The Relaria boss-variant arm above never reaches here and is deliberately untouched: it
            // returns on its own path, and its rarity is tuned by ml_treasure_relaria_chance alone.
            //
            // The refusal reason is reported to the digger ONLY for a /testtreasuremap map
            // (isAdminTest): an ordinary dig stays silent on a refusal, exactly as before, because every
            // refusal here is already free (the payout and the map consumption above are unaffected) and a
            // routine "the cap is full" line on every normal dig would be noise. A tester who forced a
            // specific type needs to know when admission itself - not the type roll - is what refused it.
            //
            // Zone isolation (owner-approved design): the encounter's zone is the map's own stamped zone
            // (PropertyInt 9076) when it has one, else the zone of the site it was just dug at (a legacy map
            // dropped before this property existed) - read here, BEFORE the map's coordinates could matter
            // to anything else, since TryConsumeFromInventoryWithNetworking above has already consumed it.
            var mapZone = wo.GetProperty(PropertyInt.TreasureMapZone);

            if (!MlDigsite.MlDigsiteManager.TryStart(player, forcedType, forcedMechanicSetId, out var startRefusal,
                    mapZone != null
                        ? (MlTreasureZone)mapZone.Value
                        : MlTreasureSiteStore.Instance.ZoneAt(new Vector2(
                            (float)(wo.GetProperty(PropertyFloat.TreasureMapEastWest) ?? 0.0),
                            (float)(wo.GetProperty(PropertyFloat.TreasureMapNorthSouth) ?? 0.0))))
                && isAdminTest && startRefusal != null)
            {
                player.Session.Network.EnqueueSend(new GameMessageSystemChat(
                    $"Test digsite encounter refused: {startRefusal}", ChatMessageType.Broadcast));
            }
        }
    }
}
