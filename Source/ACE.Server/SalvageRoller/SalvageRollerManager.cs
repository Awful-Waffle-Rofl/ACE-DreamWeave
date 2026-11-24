using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;

using log4net;

using ACE.Database;
using ACE.Database.Models.Shard;
using ACE.Entity.Enum;
using ACE.Server.Entity.AccountVault;
using ACE.Server.Entity.Actions;
using ACE.Server.Managers;
using ACE.Server.Network.GameMessages.Messages;
using ACE.Server.WorldObjects;

namespace ACE.Server.SalvageRoller
{
    /// <summary>
    /// The Salvage Roller service (Docs/SalvageRoller/DESIGN.md 5.1): batch rows, the sell pipeline, the take, and
    /// reconcile. Static and stateless between calls; every escrow move runs on the account store's queue.
    ///
    /// The shared station's Use and /salvageroller are built against the two public seams below,
    /// <see cref="OnStationOpened"/> and <see cref="DescribeStatus"/>.
    /// </summary>
    public static partial class SalvageRollerManager
    {
        /// <summary>The dedicated Salvage Roller logger (DESIGN 5.9). ERROR lines carry <see cref="RefundPrefix"/>.</summary>
        internal static readonly ILog Log = LogManager.GetLogger("SalvageRoller");

        /// <summary>The fixed prefix on every ERROR line, so a refund investigation can grep for it.</summary>
        public const string RefundPrefix = "[SALVAGE ROLLER REFUND]";

        /// <summary>Sent when the station opens with no batch to show.</summary>
        public const string HowToMessage = "Sell one item and full Hammers of one material to the Salvage Roller. Every charge is one roll; you then take exactly one result, and the original is not returned.";

        public const string StatusNoBatchMessage = "You have no batch at the Salvage Roller.";

        public const string StatusReadFailedMessage = "The Salvage Roller cannot read your batch right now. Try again in a moment.";

        /// <summary>
        /// Called by the station's Use once the player's view exists. Runs reconcile for the player's batch first,
        /// then fills the player's view from a Pending batch, or leaves it empty and sends <see cref="HowToMessage"/>.
        /// A batch that still needs attention after reconcile shows nothing.
        /// </summary>
        public static void OnStationOpened(Player player, SalvageRollerVendor station)
        {
            if (player == null || station == null)
                return;

            OpenStation(player, station);
        }

        /// <summary>One chat line describing the player's batch, for /salvageroller status (step 8). Never null.</summary>
        public static string DescribeStatus(Player player)
        {
            if (player == null)
                return StatusNoBatchMessage;

            if (!DatabaseManager.Shard.BaseDatabase.TryGetSalvageBatchForCharacter(player.Guid.Full, out var batch))
                return StatusReadFailedMessage;

            return DescribeStatus(batch);
        }

        /// <summary>The status line for a batch row, or for none. Pure.</summary>
        internal static string DescribeStatus(CharacterSalvageBatch batch)
        {
            if (batch == null)
                return StatusNoBatchMessage;

            var action = (SalvageRollerAction)batch.Action;

            switch ((SalvageBatchState)batch.State)
            {
                case SalvageBatchState.Pending:
                    return $"Your Salvage Roller batch ({ActionName(action)}, {batch.RollCount} rolls) is waiting for you to take one result. Use the Salvage Roller to choose.";

                case SalvageBatchState.Taking:
                    return $"Your Salvage Roller batch ({ActionName(action)}) has result #{batch.ChosenSeq} chosen and is being delivered. Use the Salvage Roller to finish it.";

                default:
                    return $"Your Salvage Roller batch ({ActionName(action)}, {batch.RollCount} rolls) has not been rolled. Use the Salvage Roller to cancel it.";
            }
        }

        internal static string ActionName(SalvageRollerAction action)
        {
            switch (action)
            {
                case SalvageRollerAction.TourmalineReroll: return "tourmaline reroll";
                case SalvageRollerAction.AmethystSwap: return "amethyst swaps";
                case SalvageRollerAction.ObsidianConvert: return "obsidian conversion";
                case SalvageRollerAction.ObsidianReroll: return "obsidian reroll";
                case SalvageRollerAction.SerpentineReroll: return "serpentine reroll";
                case SalvageRollerAction.GreenJadeSwap: return "green jade swaps";
                default: return "unknown";
            }
        }

        /// <summary>Sends <paramref name="text"/> to the player's chat, when they have a session.</summary>
        internal static void Chat(Player player, string text)
        {
            if (player?.Session == null || string.IsNullOrEmpty(text))
                return;

            player.Session.Network.EnqueueSend(new GameMessageSystemChat(text, ChatMessageType.Broadcast));
        }

        /// <summary>How long a deferred open waits between two looks at a still-loading escrow, in seconds.</summary>
        internal const double EscrowWaitIntervalSeconds = 0.5;

        /// <summary>
        /// How many deferred looks an open makes before it gives up and says why (20 x 0.5 s, about ten seconds). The
        /// last look never defers again, so it ends with exactly the line an open without the wait would have sent.
        /// </summary>
        internal const int EscrowWaitAttempts = 20;

        /// <summary>
        /// Character guid to the generation of the open whose deferred fill is still waiting. A newer open replaces it
        /// (or removes it), so an older wait finds a different value and stands down rather than filling twice.
        /// </summary>
        private static readonly ConcurrentDictionary<uint, long> waitingOpens = new ConcurrentDictionary<uint, long>();

        private static long nextOpenGeneration;

        /// <summary>Whether a deferred open is waiting for this character. For the tests.</summary>
        internal static bool IsOpenWaitingForEscrow(uint characterId) => waitingOpens.ContainsKey(characterId);

        /// <summary>The outside steps a deferred open needs. Production builds them in <see cref="OpenStation(Player, SalvageRollerVendor)"/>.</summary>
        internal sealed class OpenSteps
        {
            /// <summary>Runs the action once, after the delay in seconds, on the player's own thread. Never blocks.</summary>
            public Action<double, Action> Schedule;

            /// <summary>True while the player is online and their panel on this station is still the one they opened.</summary>
            public Func<bool> StillViewing;

            /// <summary>Re-sends the player's panel, so rows filled late reach the client.</summary>
            public Action Resend;
        }

        /// <summary>What one fill of the opener's view came to.</summary>
        internal enum OpenFill { Done, WaitForEscrow }

        /// <summary>
        /// The production open: the real host, a delayed ActionChain on the player for the wait, and the station's panel
        /// for the re-send.
        /// </summary>
        private static void OpenStation(Player player, SalvageRollerVendor station)
        {
            var host = new PlayerSalvageRollerHost(player);

            OpenStation(host, station, new OpenSteps
            {
                Schedule = (delay, action) =>
                {
                    var chain = new ActionChain();
                    chain.AddDelaySeconds(delay);
                    chain.AddAction(player, action);
                    chain.EnqueueChain();
                },
                StillViewing = () => player.Session != null
                    && ReferenceEquals(PlayerManager.GetOnlinePlayer(player.Guid.Full), player)
                    && ReferenceEquals(player.CurrentSalvageRollerStation, station)
                    // the last panel the client was sent is still this station: re-sending over another vendor or chest
                    // the player opened during the wait would replace it
                    && player.LastOpenedContainerId == station.Guid,
                Resend = () => station.ApproachVendor(player, VendorType.Open),
            });
        }

        /// <summary>
        /// The body of <see cref="OnStationOpened"/>. First it touches the account's escrow, on EVERY Use, so a cold store
        /// starts its load now: reading it is what starts the load, and the escrow containers' inventories arrive
        /// asynchronously, so the first sale of a session would otherwise be the call that starts it and be refused.
        ///
        /// Then reconcile (DESIGN 5.6) and the rows of a Pending batch, or an empty view and a line saying why. When the
        /// player has a batch and the only thing in the way is an escrow that is still loading, the fill is deferred
        /// instead of telling them to come back: it is retried every <see cref="EscrowWaitIntervalSeconds"/> on the
        /// player's own thread, up to <see cref="EscrowWaitAttempts"/> times, and the panel is re-sent once it has run.
        /// A degraded escrow is never waited for. Every failure path leaves the player's view empty, which is always safe.
        /// </summary>
        internal static void OpenStation(ISalvageRollerHost host, SalvageRollerVendor station, OpenSteps steps)
        {
            WarmEscrow(host);

            // any older wait for this character stands down: this open is the one the player is looking at
            waitingOpens.TryRemove(host.CharacterId, out _);

            if (FillOpenedView(host, station, mayWait: true) != OpenFill.WaitForEscrow)
                return;

            var generation = Interlocked.Increment(ref nextOpenGeneration);
            waitingOpens[host.CharacterId] = generation;

            WaitForEscrow(host, station, steps, generation, 1);
        }

        /// <summary>Reads the escrow's readiness, which on a cold store is what starts its load. Never throws.</summary>
        private static void WarmEscrow(ISalvageRollerHost host)
        {
            try
            {
                host.GetStore()?.IsEscrowReady(out _);
            }
            catch (Exception ex)
            {
                // WARN, not ERROR: nothing has moved, and the fill below reads the escrow again and reports any failure.
                Log.Warn($"[SALVAGE ROLLER] {host.CharacterName} (0x{host.CharacterId:X8}): warming the escrow at the station threw: {ex}");
            }
        }

        /// <summary>
        /// True when the escrow is not ready ONLY because it is still loading: a store exists, its index has been read
        /// cleanly (IsIndexLoaded and LastLoadFailed answer from state and never start a load), it is not ready, and the
        /// reason is not the degraded one. A failed index read reports the same reason as a load in progress, but it is
        /// not waited for: every look would repeat the synchronous backend reads, and a retry seconds later rarely heals.
        /// </summary>
        private static bool EscrowStillLoading(ISalvageRollerHost host)
        {
            var store = host.GetStore();

            return store != null
                && store.IsIndexLoaded
                && !store.LastLoadFailed
                && !store.IsEscrowReady(out var reason)
                && reason != AccountVaultStore.EscrowDegradedMessage;
        }

        private static void WaitForEscrow(ISalvageRollerHost host, SalvageRollerVendor station, OpenSteps steps, long generation, int attempt)
        {
            steps.Schedule(EscrowWaitIntervalSeconds, () =>
            {
                if (!waitingOpens.TryGetValue(host.CharacterId, out var current) || current != generation)
                    return;

                // Walked off, logged out, opened another station, or the station is gone: nothing to show, and the batch
                // is durable, so the next Use picks it up. Nothing is told; the player is no longer looking.
                if (station.IsDestroyed || !station.HasView(host.CharacterId) || !steps.StillViewing())
                {
                    waitingOpens.TryRemove(new KeyValuePair<uint, long>(host.CharacterId, generation));
                    return;
                }

                OpenFill fill;

                try
                {
                    fill = FillOpenedView(host, station, mayWait: attempt < EscrowWaitAttempts);
                }
                catch (Exception ex)
                {
                    waitingOpens.TryRemove(new KeyValuePair<uint, long>(host.CharacterId, generation));
                    Log.Error($"{RefundPrefix} a deferred open threw for {host.CharacterName} (0x{host.CharacterId:X8}): {ex}");
                    host.Tell(NeedsAttentionMessage);
                    return;
                }

                if (fill == OpenFill.WaitForEscrow)
                {
                    WaitForEscrow(host, station, steps, generation, attempt + 1);
                    return;
                }

                waitingOpens.TryRemove(new KeyValuePair<uint, long>(host.CharacterId, generation));

                steps.Resend();
            });
        }

        /// <summary>
        /// One fill of the opener's view: reconcile, then the rows or a line saying why. With <paramref name="mayWait"/>,
        /// a failure caused only by a still-loading escrow tells nothing and returns <see cref="OpenFill.WaitForEscrow"/>;
        /// without it, every failure tells its line exactly as it always has.
        /// </summary>
        private static OpenFill FillOpenedView(ISalvageRollerHost host, SalvageRollerVendor station, bool mayWait)
        {
            station.ClearRows(host.CharacterId);

            ReconcileResult result;

            try
            {
                result = Reconcile(host);
            }
            catch (Exception ex)
            {
                Log.Error($"{RefundPrefix} reconcile threw for {host.CharacterName} (0x{host.CharacterId:X8}): {ex}");
                host.Tell(NeedsAttentionMessage);
                return OpenFill.Done;
            }

            switch (result.Kind)
            {
                case ReconcileKind.NoBatch:
                    host.Chat(HowToMessage);
                    return OpenFill.Done;

                case ReconcileKind.Finished:
                    if (result.Message != null)
                        host.Chat(result.Message);
                    host.Chat(HowToMessage);
                    return OpenFill.Done;

                case ReconcileKind.Pending:
                {
                    var store = host.GetStore();
                    var target = store != null && store.IsEscrowReady(out _) ? store.FindEscrowed(result.Batch.ItemGuid) : null;

                    if (target == null && mayWait && EscrowStillLoading(host))
                        return OpenFill.WaitForEscrow;

                    if (target == null || ShowRows(station, host.CharacterId, result.Batch, target) == 0)
                        host.Tell(NeedsAttentionMessage);

                    return OpenFill.Done;
                }

                // NotNow covers a failed batch read (Batch null), an escrow refusal, and an abort that could not finish.
                // EscrowStillLoading is the guard that picks out the escrow case; the Batch check only skips the read
                // failure, which says nothing about the escrow.
                case ReconcileKind.NotNow when mayWait && result.Batch != null && EscrowStillLoading(host):
                    return OpenFill.WaitForEscrow;

                default:
                    host.Tell(result.Message ?? NeedsAttentionMessage);
                    return OpenFill.Done;
            }
        }
    }
}
