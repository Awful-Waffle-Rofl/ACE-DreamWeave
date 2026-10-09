using System;
using System.Collections.Generic;

using log4net;

using ACE.Entity.Models;
using ACE.Server.Managers;

namespace ACE.Server.Entity
{
    /// <summary>
    /// The pure decision half of the NPC turn-in reward pre-flight (Player_Inventory.PreflightEmoteGive).
    ///
    /// An NPC give consumes the given item and stamps the quest BEFORE any reward-give emote row runs, so a
    /// player who is over-burdened or out of slots used to lose the turn-in and never receive the reward.
    /// The gate combines <see cref="EmoteRewardForecast"/> (what the emote set could hand out) with
    /// <see cref="ItemsToReceive"/> (what the player can actually hold) and refuses the give up front.
    ///
    /// Everything here is free of Player/WorldObject state so it can be unit tested: the flag read goes
    /// through a seam, and path selection and message wording operate on plain numbers the caller measured.
    /// </summary>
    public static class EmoteGivePreflight
    {
        private static readonly ILog log = LogManager.GetLogger(System.Reflection.MethodBase.GetCurrentMethod().DeclaringType);

        /// <summary>
        /// The server property that arms the gate. OFF by default: while it is off the gate returns before
        /// it builds a forecast, and the give path behaves exactly as it did before the gate existed.
        /// </summary>
        public const string PropertyKey = "emote_give_preflight";

        /// <summary>
        /// A CreateTreasure emote row rolls its reward at execution time, so a forecast can only reserve the
        /// slot it will land in - never its burden or its wcid. One row, one inventory slot, zero burden.
        /// </summary>
        public const int TreasureInventorySlots = 1;

        /// <summary>
        /// Test seam for the flag read. Defaults to <see cref="ReadEnabledFromProperties"/> (this field is
        /// never actually null in production, but a test may reassign it and must restore it afterward).
        /// </summary>
        internal static Func<bool> EnabledSource = ReadEnabledFromProperties;

        /// <summary>
        /// True when the pre-flight gate is armed. Any failure to read the flag - a unit test has no shard
        /// config at all - reads as OFF, which is the only safe direction here: OFF is the pre-existing
        /// behaviour, ON changes what a player-visible give does.
        /// </summary>
        public static bool Enabled => (EnabledSource ?? ReadEnabledFromProperties)();

        /// <summary>
        /// The production flag read. PropertyManager.GetBool dereferences DatabaseManager.ShardConfig on a
        /// cache miss (Source/ACE.Server/Managers/PropertyManager.cs:104), which is null wherever the shard
        /// config was never loaded - a unit test host - so an uncached read there throws
        /// NullReferenceException. It is wrapped: an unreadable flag degrades to OFF rather than throwing
        /// out of a player-visible give.
        /// </summary>
        internal static bool ReadEnabledFromProperties()
        {
            try
            {
                return PropertyManager.GetBool(PropertyKey).Item;
            }
            catch (Exception ex)
            {
                log.Error($"[GIVE PREFLIGHT] could not read the {PropertyKey} property; treating the gate as off", ex);

                return false;
            }
        }

        /// <summary>
        /// The whole gate decision, minus the live Player: forecast <paramref name="emoteSet"/>, price every
        /// path it could take through <paramref name="costPath"/>, and say whether the give may proceed.
        ///
        /// Returns TRUE to allow the give and FALSE to refuse it, with <paramref name="worst"/> carrying the
        /// offending path for the caller's message and log line.
        ///
        /// This method is the gate's exception boundary, and that is the point of it existing separately
        /// from the Player method that calls it. The gate runs inside a CreateMoveToChain callback with no
        /// catch anywhere in the ActionChain dispatch path, and everything below is capable of throwing -
        /// EmoteRewardForecast.Build walks arbitrary shard emote data, and <paramref name="costPath"/> is a
        /// live inventory measurement that reaches the world database for every reward wcid. A throw here
        /// would abort every turn-in server-wide, with no player feedback, for as long as the flag was on.
        /// So it fails OPEN exactly as the flag read does: the give proceeds unchecked, which is the
        /// behaviour that existed before this gate, and the exception is logged.
        ///
        /// <paramref name="context"/> is caller-supplied text identifying the NPC and item, so a thrown
        /// pre-flight names what it was looking at.
        /// </summary>
        public static bool Decide(PropertiesEmote emoteSet, IEnumerable<PropertiesEmote> allSets, Func<IReadOnlyList<ForecastedReward>, PreflightPathCost> costPath, string context, out PreflightPathCost worst)
        {
            worst = null;

            if (emoteSet == null || costPath == null)
                return true;

            try
            {
                var forecast = EmoteRewardForecast.Build(emoteSet, allSets);

                // a truncated walk may hide worse paths than it reported - never refuse on a partial picture
                if (forecast.Truncated)
                    return true;

                if (TotalRewardCount(forecast) == 0)
                    return true;

                var costs = new List<PreflightPathCost>(forecast.Paths.Count);

                foreach (var path in forecast.Paths)
                    costs.Add(costPath(path));

                var candidate = SelectWorst(costs);

                if (candidate == null || !candidate.ExceedsLimits)
                    return true;

                worst = candidate;

                return false;
            }
            catch (Exception ex)
            {
                log.Error($"[GIVE PREFLIGHT] {context}: the reward pre-flight threw; allowing the give through unchecked", ex);

                worst = null;

                return true;
            }
        }

        /// <summary>
        /// The total number of forecasted rewards across every path. Zero means the emote set hands out
        /// nothing this forecaster can see, so there is nothing to gate on and the give is allowed through.
        /// </summary>
        public static int TotalRewardCount(EmoteRewardForecast forecast)
        {
            if (forecast?.Paths == null)
                return 0;

            var total = 0;

            foreach (var path in forecast.Paths)
                total += path?.Count ?? 0;

            return total;
        }

        /// <summary>
        /// Picks the path the refusal should be reported against: a path that exceeds the player's limits
        /// always outranks one that does not, and among equals the one demanding the most slots, then the
        /// most burden, wins. Returns null for an empty list.
        ///
        /// The gate refuses when ANY path exceeds limits, so this is only ever used to word the refusal and
        /// the log line - never to decide it. A caller still asks <see cref="PreflightPathCost.ExceedsLimits"/>
        /// on the returned cost.
        /// </summary>
        public static PreflightPathCost SelectWorst(IReadOnlyList<PreflightPathCost> costs)
        {
            if (costs == null || costs.Count == 0)
                return null;

            PreflightPathCost worst = null;

            foreach (var cost in costs)
            {
                if (cost == null)
                    continue;

                if (worst == null || IsWorse(cost, worst))
                    worst = cost;
            }

            return worst;
        }

        private static bool IsWorse(PreflightPathCost candidate, PreflightPathCost incumbent)
        {
            if (candidate.ExceedsLimits != incumbent.ExceedsLimits)
                return candidate.ExceedsLimits;

            if (candidate.RequiredSlots != incumbent.RequiredSlots)
                return candidate.RequiredSlots > incumbent.RequiredSlots;

            return candidate.RequiredBurden > incumbent.RequiredBurden;
        }

        /// <summary>
        /// The player-facing refusal line, picked the same three ways Vendor.ValidateItemsForPurchase does
        /// (Source/ACE.Server/WorldObjects/Vendor.cs:516-526): burden first, then pack space, then container
        /// slots. Returns null when the cost does not actually exceed anything.
        /// </summary>
        public static string RefusalMessage(PreflightPathCost worst, string targetName)
        {
            if (worst == null || !worst.ExceedsLimits)
                return null;

            if (worst.ExceedsBurden)
                return $"You are too encumbered to accept what {targetName} would give you in return!";

            if (worst.OutOfInventorySlots)
                return $"You do not have enough pack space to accept what {targetName} would give you in return!";

            return $"You do not have enough container slots to accept what {targetName} would give you in return!";
        }
    }

    /// <summary>
    /// What one forecasted reward path would cost the player, net of the space the outgoing turn-in item
    /// frees. A plain snapshot of an <see cref="ItemsToReceive"/> tally, so the selection and wording rules
    /// above can be exercised without a live Player.
    /// </summary>
    public class PreflightPathCost
    {
        public int RequiredInventorySlots { get; }
        public int RequiredContainerSlots { get; }
        public int RequiredBurden { get; }

        public bool OutOfInventorySlots { get; }
        public bool OutOfContainerSlots { get; }
        public bool ExceedsBurden { get; }

        /// <summary>Total slots demanded. Can be negative when the turn-in frees more than the reward needs.</summary>
        public int RequiredSlots => RequiredInventorySlots + RequiredContainerSlots;

        public bool ExceedsLimits => OutOfInventorySlots || OutOfContainerSlots || ExceedsBurden;

        public PreflightPathCost(int requiredInventorySlots, int requiredContainerSlots, int requiredBurden, bool outOfInventorySlots, bool outOfContainerSlots, bool exceedsBurden)
        {
            RequiredInventorySlots = requiredInventorySlots;
            RequiredContainerSlots = requiredContainerSlots;
            RequiredBurden = requiredBurden;

            OutOfInventorySlots = outOfInventorySlots;
            OutOfContainerSlots = outOfContainerSlots;
            ExceedsBurden = exceedsBurden;
        }

        /// <summary>Snapshots a finished <see cref="ItemsToReceive"/> tally.</summary>
        public static PreflightPathCost From(ItemsToReceive items)
        {
            if (items == null)
                return null;

            return new PreflightPathCost(
                items.RequiredInventorySlots,
                items.RequiredContainerSlots,
                items.RequiredBurden,
                items.PlayerOutOfInventorySlots,
                items.PlayerOutOfContainerSlots,
                items.PlayerExceedsAvailableBurden);
        }

        public override string ToString() =>
            $"inventorySlots={RequiredInventorySlots} containerSlots={RequiredContainerSlots} burden={RequiredBurden} exceeds={ExceedsLimits}";
    }
}
