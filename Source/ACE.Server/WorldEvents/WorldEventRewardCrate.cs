using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Server.Factories;
using ACE.Server.Knots;
using ACE.Server.Network.GameMessages.Messages;
using ACE.Server.ThreadDungeons;
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

        /// <summary>The loaded rewards.json profile named <paramref name="id"/>, or null when the store is not loaded or has no such row.</summary>
        public static RewardDef TryGetProfile(string id)
        {
            var rewards = WorldEventManager.Store?.Rewards;
            return id != null && rewards != null && rewards.TryGetValue(id, out var def) ? def : null;
        }

        /// <summary>True when the axis store (and so rewards.json) is loaded.</summary>
        public static bool ProfileStoreLoaded => WorldEventManager.Store != null;

        /// <summary>
        /// Stamps a crate with the profile and multiplier <see cref="Open"/> reads (PropertyString.WorldEventRewardProfile,
        /// PropertyFloat.WorldEventRewardMultiplier). Stamp BEFORE the crate is given.
        /// </summary>
        public static void Stamp(WorldObject crate, string profile, double multiplier)
        {
            crate.SetProperty(PropertyFloat.WorldEventRewardMultiplier, multiplier);
            crate.SetProperty(PropertyString.WorldEventRewardProfile, profile);
        }

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

        /// <summary>
        /// Effective roll count: m as-is when the profile has no draws (World Events); otherwise draws - 1 + m, so
        /// a crate never pays fewer rolls just past a tier boundary (t2 at m 1.9 = 3.9 rolls, t3 at m 1.0 = 4.0). Pure.
        /// </summary>
        public static double EffectiveRolls(int? draws, double m) => draws == null ? m : draws.Value - 1 + m;

        /// <summary>
        /// Stamps a created object from its roll row. itemMaxLevel rows get ItemMaxLevel and the matching level
        /// overlay icon; knotItem "pattern" rows get a pinnacle picked uniformly from <paramref name="availablePinnacles"/>
        /// with <paramref name="rng"/>; "fiber" rows get <paramref name="playerLevel"/>. Both Knot stamps are the
        /// Thread drop path's (KnotDrops.StampPattern / StampFiber). A plain row is untouched. Returns false when a
        /// Knot row cannot be stamped (no available pinnacle, or no player level): the caller skips that object.
        /// Pure of any live world: every input is a parameter.
        /// </summary>
        public static bool ApplyRollStamps(WorldObject wo, RolledReward row, IReadOnlyList<PinnacleDef> availablePinnacles = null, int? playerLevel = null, Random rng = null)
        {
            if (wo == null || row == null)
                return true;

            if (row.ItemMaxLevel != null)
            {
                var level = row.ItemMaxLevel.Value;

                wo.ItemMaxLevel = level;
                wo.IconOverlayId = LootGenerationFactory.IconOverlay_ItemMaxLevel[level - 1];
            }

            if (row.KnotItem == RewardPoolEntryDef.KnotPattern)
            {
                if (availablePinnacles == null || availablePinnacles.Count == 0)
                    return false;

                KnotDrops.StampPattern(wo, availablePinnacles[(rng ?? new Random()).Next(availablePinnacles.Count)]);
            }
            else if (row.KnotItem == RewardPoolEntryDef.KnotFiber)
            {
                if (playerLevel == null || playerLevel.Value < 1)
                    return false;

                KnotDrops.StampFiber(wo, playerLevel.Value);
            }

            return true;
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

            var rolled = WorldEventRewardRoller.Roll(reward.Pool, EffectiveRolls(reward.Draws, multiplier), new Random());

            if (rolled.Count == 0)
            {
                log.Error($"[WORLDEVENT] crate-open refused for {player.Name}: profile '{profile}' rolled nothing at mult={multiplier:0.##}");
                TrySendError(player, "The Event Crate will not open.");
                return;
            }

            var items = new List<WorldObject>();
            var stampRng = new Random();
            List<PinnacleDef> pinnacles = null;

            try
            {
                foreach (var row in rolled)
                {
                    if (row.KnotItem == RewardPoolEntryDef.KnotPattern && pinnacles == null)
                        pinnacles = KnotPinnacles.Available(ThreadDungeonManager.Store);

                    var built = BuildRowObjects(row, WorldObjectFactory.CreateNewWorldObject, pinnacles, player.Level, stampRng, items.Count);

                    if (built.Failure != null)
                    {
                        log.Error($"[WORLDEVENT] crate-open refused for {player.Name}: {built.Failure}");
                        DestroyAll(items);
                        TrySendError(player, "The Event Crate will not open.");
                        return;
                    }

                    if (built.Skipped > 0)
                        log.Error($"[WORLDEVENT] crate-open for {player.Name}: wcid {row.Wcid} ({row.KnotItem}) could not be stamped (no available pinnacle or no player level); {built.Skipped} object(s) skipped");

                    items.AddRange(built.Objects);
                }
            }
            catch
            {
                DestroyAll(items);
                throw;
            }

            if (items.Count == 0)
            {
                log.Error($"[WORLDEVENT] crate-open refused for {player.Name}: every rolled object was skipped (profile '{profile}', mult={multiplier:0.##}); the crate is kept");
                TrySendError(player, "The Event Crate will not open.");
                return;
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

            log.Info($"[WORLDEVENT] crate-open player={player.Name} mult={multiplier.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture)} items={string.Join(",", rolled.Select(DescribeRow))}");
        }

        /// <summary>Log text for one rolled row: "wcid:qty", plus "(L5)" for an itemMaxLevel row or "(pattern)"/"(fiber)" for a knotItem row.</summary>
        public static string DescribeRow(RolledReward r)
            => $"{r.Wcid}:{r.Qty}" + (r.ItemMaxLevel != null ? $"(L{r.ItemMaxLevel})" : "") + (r.KnotItem != null ? $"({r.KnotItem})" : "");

        /// <summary>The outcome of building one rolled row: the objects to give, how many were skipped, or a Failure that refuses the crate.</summary>
        internal sealed class RowBuild
        {
            public List<WorldObject> Objects { get; } = new List<WorldObject>();
            public int Skipped { get; set; }
            public string Failure { get; set; }
        }

        /// <summary>
        /// Builds the objects for one rolled row: create, split into stacks, stamp, and skip (destroying) an object whose
        /// Knot stamp is refused. Every object of the row is stamped, not just the first. A Failure (a wcid that will not
        /// create, or more than <see cref="MaxObjects"/> objects counting <paramref name="alreadyBuilt"/>) destroys what
        /// this call made and leaves Objects empty. Free of any live world: creation, pinnacles, level and rng are inputs.
        /// </summary>
        internal static RowBuild BuildRowObjects(RolledReward row, Func<uint, WorldObject> create, IReadOnlyList<PinnacleDef> pinnacles, int? playerLevel, Random rng, int alreadyBuilt = 0)
        {
            var result = new RowBuild();
            var first = create(row.Wcid);

            if (first == null)
            {
                result.Failure = $"wcid {row.Wcid} would not create";
                return result;
            }

            var stackable = first is Stackable && (first.MaxStackSize ?? 1) > 1;
            var sizes = SplitStacks(row.Qty, stackable ? first.MaxStackSize ?? 1 : 1);

            if (alreadyBuilt + sizes.Count > MaxObjects)
            {
                first.Destroy();
                result.Failure = $"the roll would create more than {MaxObjects} objects";
                return result;
            }

            for (var i = 0; i < sizes.Count; i++)
            {
                var wo = i == 0 ? first : create(row.Wcid);

                if (wo == null)
                {
                    DestroyAll(result.Objects);
                    result.Failure = $"wcid {row.Wcid} would not create";
                    return result;
                }

                if (stackable)
                    wo.SetStackSize(sizes[i]);

                if (!ApplyRollStamps(wo, row, pinnacles, playerLevel, rng))
                {
                    wo.Destroy();
                    result.Skipped++;
                    continue;
                }

                result.Objects.Add(wo);
            }

            return result;
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
