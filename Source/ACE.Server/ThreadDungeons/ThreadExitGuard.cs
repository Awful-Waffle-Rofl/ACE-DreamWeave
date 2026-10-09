using System;
using System.Collections.Concurrent;

using ACE.Entity;
using ACE.Entity.Enum;
using ACE.Server.Entity;
using ACE.Server.Entity.Actions;
using ACE.Server.Managers;
using ACE.Server.Network.GameMessages.Messages;
using ACE.Server.WorldObjects;

namespace ACE.Server.ThreadDungeons
{
    public enum ExitPromptSend
    {
        Sent,
        AlreadyPendingOurs,
        BlockedByOtherPrompt,
    }

    /// <summary>The exit-confirmation rules (Docs/Threads/POOLED-LOOT-CACHE-DESIGN.md section 7), pure.</summary>
    public static class ThreadExitRules
    {
        public const string PromptText = "This Thread is complete and you will not be able to return. Your Thread Cache still holds loot. Leave anyway?";
        public const string AnswerOpenConfirmationMessage = "Answer your open confirmation first.";

        /// <summary>How long a Yes lets the re-issued exit through without asking again.</summary>
        public static readonly TimeSpan BypassWindow = TimeSpan.FromSeconds(10);

        public static bool ShouldPromptExit(bool pooledLoot, ThreadDungeonRunState state, bool isOwner, bool insideRunInstance,
            bool hasUnclaimedLoot, bool anyCacheHoldsItems, bool bypassActive)
            => pooledLoot && state == ThreadDungeonRunState.Cleared && isOwner && insideRunInstance && !bypassActive
               && (hasUnclaimedLoot || anyCacheHoldsItems);

        public static ExitPromptSend ClassifySend(bool sent, Confirmation pending)
        {
            if (sent)
                return ExitPromptSend.Sent;

            return pending is Confirmation_ThreadExit ? ExitPromptSend.AlreadyPendingOurs : ExitPromptSend.BlockedByOtherPrompt;
        }
    }

    /// <summary>A marker subclass so a pending exit prompt can be recognised; behaviour is Confirmation_Custom's.</summary>
    public class Confirmation_ThreadExit : Confirmation_Custom
    {
        public Confirmation_ThreadExit(ObjectGuid playerGuid, Action action) : base(playerGuid, action)
        {
        }
    }

    /// <summary>
    /// Opt-in exit gate. Called by each voluntary exit (portals, recall commands, recall spells,
    /// portal gems, /exitinstance) after that exit's own refusals, immediately before its teleport or first
    /// effect, and never from Player.Teleport, so death, logout, EndRun eviction,
    /// landblock-unload eviction and admin teleports are never held (spec section 7).
    ///
    /// Threading. Exit sites run on the player's landblock thread (portal use, spell casts) or the world thread
    /// (GameActions, commands); both only read the run under its lock and the caches of the landblock they are
    /// on. The Yes response arrives on the world thread and re-issues the exit on the player's own queue.
    ///
    /// The bypass lives in one ConcurrentDictionary keyed by player guid. GrantBypass is a single atomic
    /// indexer write and ConsumeBypass a single atomic TryRemove, so a grant on the player's queue and a
    /// consume on either thread cannot double-spend it: exactly one TryHoldExit call removes a given grant.
    /// Every consume removes the entry whether or not it was still in its window, so a grant clears on the
    /// first gated exit after it (normally the re-issue itself).
    /// </summary>
    public static class ThreadExitGuard
    {
        private static readonly ConcurrentDictionary<uint, DateTime> bypassUntil = new ConcurrentDictionary<uint, DateTime>();

        internal static void GrantBypass(uint playerGuid, DateTime nowUtc) => bypassUntil[playerGuid] = nowUtc + ThreadExitRules.BypassWindow;

        internal static bool ConsumeBypass(uint playerGuid, DateTime nowUtc)
            => bypassUntil.TryRemove(playerGuid, out var until) && nowUtc <= until;

        /// <summary>Reads a grant without spending it: a pre-check that must leave the grant for the exit's own gate.</summary>
        internal static bool PeekBypass(uint playerGuid, DateTime nowUtc) => bypassUntil.TryGetValue(playerGuid, out var until) && nowUtc <= until;

        /// <summary>True when a voluntary exit by this player would be held right now. Consumes nothing and sends nothing.</summary>
        public static bool WouldHoldExit(Player player)
        {
            // Group Threads (Task 11): the run the player actually stands in, not the run they own - an owner
            // holding a held cleared run alongside a newer live one must resolve to the run under their feet.
            var run = player?.Location == null ? null : ThreadDungeonManager.GetRunForMember(player.Guid.Full, player.Location.Instance);

            if (run == null)
                return false;

            var guid = player.Guid.Full;

            // isOwner's meaning becomes "may be prompted": true for any roster member, not just the owner.
            var isOwner = run.IsRosterMember(guid);
            var hasUnclaimed = run.IsGroup ? run.HasUnclaimedLootFor(guid) : run.HasUnclaimedLoot;
            var anyCacheHoldsItems = run.IsGroup ? ThreadLootPool.AnyCacheHoldsItemsFor(run, guid) : ThreadLootPool.AnyCacheHoldsItems(run);

            return ThreadExitRules.ShouldPromptExit(run.PooledLoot, run.State, isOwner,
                ThreadCachePlacer.IsOwnerInside(run, player), hasUnclaimed, anyCacheHoldsItems,
                PeekBypass(guid, DateTime.UtcNow));
        }

        /// <summary>
        /// Returns true when the exit is HELD (a prompt was sent, is already open, or another prompt blocks it);
        /// the caller must return without teleporting. <paramref name="reissue"/> is run once on Yes, on the
        /// player's queue, with a one-shot bypass, and should repeat the exact exit that was refused.
        /// </summary>
        public static bool TryHoldExit(Player player, Action reissue)
        {
            if (player?.Location == null || reissue == null)
                return false;

            // The player's own live run, the same lookup /spawncache uses; the inside check below confines it to this copy.
            var run = ThreadDungeonManager.GetRunForMember(player.Guid.Full, player.Location.Instance);

            if (run == null)
                return false;

            var now = DateTime.UtcNow;
            var playerGuid = player.Guid.Full;

            // isOwner's meaning becomes "may be prompted": true for any roster member, not just the owner.
            var isOwner = run.IsRosterMember(playerGuid);
            var bypass = isOwner && ConsumeBypass(playerGuid, now);
            var hasUnclaimed = run.IsGroup ? run.HasUnclaimedLootFor(playerGuid) : run.HasUnclaimedLoot;
            var anyCacheHoldsItems = run.IsGroup ? ThreadLootPool.AnyCacheHoldsItemsFor(run, playerGuid) : ThreadLootPool.AnyCacheHoldsItems(run);

            if (!ThreadExitRules.ShouldPromptExit(run.PooledLoot, run.State, isOwner, ThreadCachePlacer.IsOwnerInside(run, player),
                    hasUnclaimed, anyCacheHoldsItems, bypass))
                return false;

            var guid = player.Guid;

            var confirmation = new Confirmation_ThreadExit(guid, () =>
            {
                // Same Player object only: a relog makes a new one, and the captured exit belongs to the old.
                var current = PlayerManager.GetOnlinePlayer(guid);

                if (current == null || !ReferenceEquals(current, player))
                    return;

                current.EnqueueAction(new ActionEventDelegate(() =>
                {
                    // Re-checked on the player's own queue at Yes time: a player who already left the copy (a forced
                    // exit, the run ending) has nothing to re-issue. Loot claimed in the meantime just proceeds.
                    if (!ThreadCachePlacer.IsOwnerInside(run, current))
                        return;

                    GrantBypass(current.Guid.Full, DateTime.UtcNow);
                    reissue();
                }));
            });

            var sent = player.ConfirmationManager.EnqueueSend(confirmation, ThreadExitRules.PromptText);

            Confirmation pending = null;

            if (!sent)
                player.ConfirmationManager.TryGetPending(ConfirmationType.Yes_No, out pending);

            if (ThreadExitRules.ClassifySend(sent, pending) == ExitPromptSend.BlockedByOtherPrompt)
                player.Session?.Network.EnqueueSend(new GameMessageSystemChat(ThreadExitRules.AnswerOpenConfirmationMessage, ChatMessageType.Broadcast));

            return true;
        }
    }
}
