using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Server.Factories;
using ACE.Server.Network.GameMessages.Messages;
using ACE.Server.WorldEvents.Defs;
using ACE.Server.WorldObjects;

using log4net;

namespace ACE.Server.WorldEvents
{
    /// <summary>
    /// The World Events reward crate (wcid 1002600, presence payout). A Gem carrying
    /// PropertyBool.WorldEventRewardCrate is opened here, dispatched from Gem.UseGem ahead of the generic
    /// consume - the TreasureMapHandler marker-dispatch precedent. The crate's contents are rolled IN CODE when
    /// it is opened: the pool of the reward definition named by PropertyString.WorldEventRewardProfile
    /// (missing = "standard"), scaled by PropertyFloat.WorldEventRewardMultiplier (missing = 1.0), through
    /// WorldEventRewardRoller.
    ///
    /// No Use emote set may exist on a weenie carrying this marker: the Use emote category fires before
    /// ActOnUse reaches here, so a leftover emote would pay a second, unscaled reward.
    ///
    /// ALL OR NOTHING. Every rolled item is built first, the pack is checked for room for all of them at
    /// once (Container.CanAddToInventory(List)), and only then is anything given; the crate is consumed only
    /// after the give. A full pack, an empty or missing pool, or an item that will not create refuses and
    /// consumes nothing, so the player can make room and try again.
    /// </summary>
    public static class WorldEventRewardCrate
    {
        private static readonly ILog log = LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);

        /// <summary>The reward profile a crate with no stamp rolls.</summary>
        public const string DefaultProfile = "standard";

        /// <summary>Hard ceiling on the objects one opening may create (m tops out at 2, so a real roll is far below it).</summary>
        public const int MaxObjects = 60;

        /// <returns>true when this was an event reward crate (opened or refused); false when UseGem should continue.</returns>
        public static bool TryHandleUse(WorldObject crate, Player player)
        {
            if (crate == null || player == null || crate.GetProperty(PropertyBool.WorldEventRewardCrate) != true)
                return false;

            try
            {
                Open(crate, player);
            }
            catch (Exception ex)
            {
                log.Error($"[WORLDEVENT] crate-open threw for {player.Name} (0x{player.Guid.Full:X8}) on crate 0x{crate.Guid.Full:X8}", ex);
                TrySendError(player, "The Event Crate will not open.");
            }

            return true;
        }

        /// <summary>
        /// The stamped multiplier, read leniently: missing, NaN, infinite or not above 0 reads as 1.0 (the
        /// unscaled crate); anything above 2 is clamped to 2.
        /// </summary>
        public static double ResolveMultiplier(double? stamped)
        {
            if (stamped == null || double.IsNaN(stamped.Value) || double.IsInfinity(stamped.Value) || stamped.Value <= 0)
                return 1.0;

            return Math.Min(2.0, stamped.Value);
        }

        /// <summary>The stamped profile, or <see cref="DefaultProfile"/> when it is missing or blank.</summary>
        public static string ResolveProfile(string stamped) => string.IsNullOrWhiteSpace(stamped) ? DefaultProfile : stamped.Trim();

        /// <summary>
        /// How one rolled (wcid, qty) becomes objects: stacks of at most <paramref name="maxStack"/> when the
        /// item stacks (maxStack above 1), otherwise qty single objects. Pure.
        /// </summary>
        public static List<int> SplitStacks(int qty, int maxStack)
        {
            var sizes = new List<int>();

            if (qty <= 0)
                return sizes;

            if (maxStack <= 1)
            {
                for (var i = 0; i < qty; i++)
                    sizes.Add(1);

                return sizes;
            }

            var left = qty;

            while (left > 0)
            {
                var take = Math.Min(left, maxStack);
                sizes.Add(take);
                left -= take;
            }

            return sizes;
        }

        private static void Open(WorldObject crate, Player player)
        {
            var multiplier = ResolveMultiplier(crate.GetProperty(PropertyFloat.WorldEventRewardMultiplier));
            var profile = ResolveProfile(crate.GetProperty(PropertyString.WorldEventRewardProfile));

            var rewards = WorldEventManager.Store?.Rewards;
            RewardDef reward = null;

            if (rewards == null || !rewards.TryGetValue(profile, out reward) || reward?.Pool == null || reward.Pool.Count == 0)
            {
                log.Error($"[WORLDEVENT] crate-open refused for {player.Name}: reward profile '{profile}' has no pool (store loaded={rewards != null}, profile found={reward != null})");
                TrySendError(player, "The Event Crate will not open.");
                return;
            }

            var rolled = WorldEventRewardRoller.Roll(reward.Pool, multiplier, new Random());

            if (rolled.Count == 0)
            {
                log.Error($"[WORLDEVENT] crate-open refused for {player.Name}: profile '{profile}' rolled nothing at mult={multiplier:0.##}");
                TrySendError(player, "The Event Crate will not open.");
                return;
            }

            var items = new List<WorldObject>();

            try
            {
                foreach (var (wcid, qty) in rolled)
                {
                    var first = WorldObjectFactory.CreateNewWorldObject(wcid);

                    if (first == null)
                    {
                        log.Error($"[WORLDEVENT] crate-open refused for {player.Name}: wcid {wcid} would not create");
                        DestroyAll(items);
                        TrySendError(player, "The Event Crate will not open.");
                        return;
                    }

                    var stackable = first is Stackable && (first.MaxStackSize ?? 1) > 1;
                    var sizes = SplitStacks(qty, stackable ? first.MaxStackSize ?? 1 : 1);

                    if (items.Count + sizes.Count > MaxObjects)
                    {
                        first.Destroy();
                        log.Error($"[WORLDEVENT] crate-open refused for {player.Name}: the roll would create more than {MaxObjects} objects");
                        DestroyAll(items);
                        TrySendError(player, "The Event Crate will not open.");
                        return;
                    }

                    for (var i = 0; i < sizes.Count; i++)
                    {
                        var wo = i == 0 ? first : WorldObjectFactory.CreateNewWorldObject(wcid);

                        if (wo == null)
                        {
                            log.Error($"[WORLDEVENT] crate-open refused for {player.Name}: wcid {wcid} would not create");
                            DestroyAll(items);
                            TrySendError(player, "The Event Crate will not open.");
                            return;
                        }

                        if (stackable)
                            wo.SetStackSize(sizes[i]);

                        items.Add(wo);
                    }
                }
            }
            catch
            {
                DestroyAll(items);
                throw;
            }

            if (!player.CanAddToInventory(items))
            {
                DestroyAll(items);
                TrySendError(player, "You need more pack space to open the Event Crate.");
                return;
            }

            var given = 0;

            try
            {
                foreach (var item in items)
                {
                    if (player.TryCreateInInventoryWithNetworking(item))
                    {
                        given++;
                        continue;
                    }

                    // CanAddToInventory said there was room, so this is a race (something else filled the pack in
                    // between). Logged; the rest of the roll still goes in where it fits.
                    log.Error($"[WORLDEVENT] crate-open for {player.Name}: {item.Name} (wcid {item.WeenieClassId}) could not be given after the room check; destroyed");
                    item.Destroy();
                }
            }
            finally
            {
                // Consumed only once something was actually given, so a crate is never lost for nothing - and in
                // a finally, so a throw part-way through the give can never leave the items AND a crate that can
                // be opened again.
                if (given > 0 && !player.TryConsumeFromInventoryWithNetworking(crate, 1))
                    log.Error($"[WORLDEVENT] crate-open for {player.Name} (0x{player.Guid.Full:X8}): {given} item(s) given but the crate 0x{crate.Guid.Full:X8} could not be consumed");
            }

            if (crate.UseSound > 0)
                player.Session?.Network?.EnqueueSend(new GameMessageSound(player.Guid, crate.UseSound));

            player.Session?.Network?.EnqueueSend(new GameMessageSystemChat("You break open the Event Crate.", ChatMessageType.Broadcast));

            log.Info($"[WORLDEVENT] crate-open player={player.Name} mult={multiplier.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture)} items={string.Join(",", rolled.Select(r => $"{r.Wcid}:{r.Qty}"))}");
        }

        private static void DestroyAll(List<WorldObject> items)
        {
            foreach (var wo in items)
            {
                try
                {
                    if (wo != null && !wo.IsDestroyed)
                        wo.Destroy();
                }
                catch (Exception ex)
                {
                    log.Debug("[WORLDEVENT] crate-open could not destroy an unused rolled item", ex);
                }
            }

            items.Clear();
        }

        private static void TrySendError(Player player, string message)
        {
            try
            {
                if (player?.Session?.Network != null)
                    player.SendTransientError(message);
            }
            catch (Exception ex)
            {
                log.Debug($"[WORLDEVENT] could not send crate message to {player?.Name}", ex);
            }
        }
    }
}
