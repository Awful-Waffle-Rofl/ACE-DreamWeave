using System.Linq;
using System.Numerics;

using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Server.Entity;
using ACE.Server.Managers;
using ACE.Server.MlTreasure;
using ACE.Server.Network;
using ACE.Server.Network.GameMessages.Messages;
using ACE.Server.WorldObjects;

namespace ACE.Server.Command.Handlers
{
    /// <summary>
    /// /rerolltreasuremap (alias /rrtm) - Docs/Marae-Lassel/TREASURE-HUNT-PLAN.md section 6 "Reroll",
    /// step 8. The dig-site catalogue is generated offline from a self-declared PROVISIONAL walkable-
    /// slope threshold (section 11's first known risk), so some catalogued sites may sit on ground a
    /// player cannot actually reach. This command is the escape hatch: it snaps the target map's dig
    /// site to the catalogue site nearest wherever the player is currently standing.
    ///
    /// THERE IS NO SHOVEL (owner decision 11): no wield requirement, no equipped-item check anywhere in
    /// this file, matching TreasureMapHandler and the map weenie itself.
    /// </summary>
    public static class TreasureMapRerollCommands
    {
        /// <summary>Per-character cooldown quest name (Managers/QuestManager.cs CanSolve/Stamp
        /// pattern). The row's MinDelta lives in content SQL (Content/sql/quests/MlTreasureReroll.sql)
        /// and is cached for the process lifetime by DatabaseManager.World.GetCachedQuest - changing the
        /// cooldown later needs a server restart, not a content apply (TREASURE-HUNT-PLAN.md section 6).</summary>
        public const string CooldownQuestName = "MlTreasureReroll";

        private const string Usage =
            "Rerolls the dig site of a treasure map to the catalogue site nearest your current position.\n" +
            "Target: your last-appraised map if it is in your own inventory, else the single map you hold.\n" +
            "One reroll per map, a 1-hour cooldown per character, and you must be within range of the map's\n" +
            "current site (this nudges an unreachable site, it does not teleport one to you).";

        [CommandHandler("rerolltreasuremap", AccessLevel.Player, CommandHandlerFlag.RequiresWorld, 0,
            "Rerolls a treasure map's dig site to the nearest catalogue site to your position.", Usage)]
        [CommandHandler("rrtm", AccessLevel.Player, CommandHandlerFlag.RequiresWorld, 0,
            "Rerolls a treasure map's dig site to the nearest catalogue site to your position.", Usage)]
        public static void HandleRerollTreasureMap(Session session, params string[] parameters)
        {
            var player = session.Player;

            var map = ResolveTargetMap(session, player);

            if (map == null)
                return; // ResolveTargetMap has already sent the player a refusal message.

            // Guard order per TREASURE-HUNT-PLAN.md section 6: outdoors, inside Marae Lassel, not
            // already rerolled, cooldown, then distance to the currently stored site.

            var playerMapCoords = player.Location.GetMapCoords();

            if (playerMapCoords == null)
            {
                Msg(player, "You cannot reroll a treasure map indoors.");
                return;
            }

            if (!MlTreasureLandblock.IsMaraeLassel(player.Location.LandblockId.Landblock, player.Location.RealmID))
            {
                Msg(player, "You must be on Marae Lassel to reroll a treasure map.");
                return;
            }

            var alreadyRerolled = map.GetProperty(PropertyBool.TreasureMapRerolled) == true;

            if (MlTreasureRerollGuards.IsAlreadyRerolled(alreadyRerolled))
            {
                Msg(player, "This map has already been rerolled once. It cannot be rerolled again.");
                return;
            }

            if (!player.QuestManager.CanSolve(CooldownQuestName))
            {
                var remaining = player.QuestManager.GetNextSolveTime(CooldownQuestName);
                Msg(player, $"You must wait {(int)remaining.TotalMinutes} more minute(s) before rerolling another treasure map.");
                return;
            }

            var ns = map.GetProperty(PropertyFloat.TreasureMapNorthSouth);
            var ew = map.GetProperty(PropertyFloat.TreasureMapEastWest);

            if (ns == null || ew == null)
            {
                Msg(player, "This map has not revealed a site yet. Use it once before rerolling it.");
                return;
            }

            var siteMapCoords = new Vector2((float)ew.Value, (float)ns.Value); // X=EastWest, Y=NorthSouth
            var distanceMetres = MlTreasureGeometry.DistanceMetres(playerMapCoords.Value, siteMapCoords);
            var maxMetres = (float)PropertyManager.GetLong("ml_treasure_reroll_max_metres").Item;

            if (MlTreasureRerollGuards.IsTooFarToReroll(distanceMetres, maxMetres))
            {
                Msg(player, "You are too far from this map's current site to reroll it. Get closer to the marked location first.");
                return;
            }

            // Zone isolation (owner-approved design): a reroll must stay in the map's own zone - the
            // stamped TreasureMapZone (PropertyInt 9076) when the map carries one, else the zone of its
            // CURRENT site (ZoneAt), so a legacy unstamped map still rerolls within whatever zone it is
            // already sitting in rather than jumping island-wide.
            var storedZone = map.GetProperty(PropertyInt.TreasureMapZone);
            var zone = storedZone != null
                ? (MlTreasureZone)storedZone.Value
                : MlTreasureSiteStore.Instance.ZoneAt(siteMapCoords);

            var rerolled = MlTreasureSiteStore.Instance.Nearest(playerMapCoords.Value, bossOk: false, zone);

            if (rerolled == null)
            {
                Msg(player, "There is nowhere to reroll this map to. Something is wrong here - tell an admin.");
                return;
            }

            map.SetProperty(PropertyFloat.TreasureMapNorthSouth, rerolled.Value.Ns);
            map.SetProperty(PropertyFloat.TreasureMapEastWest, rerolled.Value.Ew);
            map.SetProperty(PropertyInt.TreasureMapDigProgress, 0);
            map.SetProperty(PropertyBool.TreasureMapRerolled, true);

            player.QuestManager.Stamp(CooldownQuestName);

            Msg(player, "The map's site has shifted. It now points to a spot near where you stand.");
        }

        /// <summary>
        /// Target resolution (TREASURE-HUNT-PLAN.md section 6): the last-appraised object if it is a
        /// treasure map in the player's own inventory, else the single treasure map the player holds,
        /// else a refusal. Sends the player a message and returns null on every refusal path.
        /// </summary>
        private static WorldObject ResolveTargetMap(Session session, Player player)
        {
            var appraised = CommandHandlerHelper.GetLastAppraisedObject(session);

            if (appraised != null
                && appraised.GetProperty(PropertyBool.TreasureMap) == true
                && player.FindObject(appraised.Guid.Full, Player.SearchLocations.MyInventory, out _, out _, out _) != null)
            {
                return appraised;
            }

            var maps = player.GetInventoryItemsOfTypeWeenieType(WeenieType.Gem)
                .Where(wo => wo.GetProperty(PropertyBool.TreasureMap) == true)
                .ToList();

            if (maps.Count == 1)
                return maps[0];

            Msg(player, "Examine a treasure map first, then use /rrtm to reroll it.");
            return null;
        }

        private static void Msg(Player player, string text)
        {
            player.Session.Network.EnqueueSend(new GameMessageSystemChat(text, ChatMessageType.Broadcast));
        }
    }
}
