using System;
using System.Collections.Concurrent;
using System.Collections.Generic;

using log4net;

using ACE.Server.WorldObjects;

namespace ACE.Server.Pvp
{
    /// <summary>
    /// The static facade over the one production <see cref="PvpMatchCoordinator"/> (Docs/Pvp/DESIGN.md
    /// "Threading"). Two roles:
    ///   - The player hooks report into it from any thread (<see cref="Report"/>): deaths from landblock threads
    ///     (Player.Die) and forfeits from the logout path (Player.LogOut). Both land on one ConcurrentQueue that
    ///     the coordinator's tick drains.
    ///   - PR D's commands call the thin API below. Every API method, and <see cref="Tick"/>, must be called on the
    ///     WORLD thread: commands and popup replies already arrive there (NetworkManager.InboundMessageQueue runs
    ///     inside the world loop). A caller on any other thread (the server console) must hop onto the world thread
    ///     first with WorldManager.EnqueueAction.
    ///
    /// <see cref="Initialize"/> runs at boot (Program.cs, after SpeedBoardManager); until it has, the API answers as
    /// if the arena were closed and the tick does nothing.
    /// </summary>
    public static class PvpMatchManager
    {
        private static readonly ILog log = LogManager.GetLogger(typeof(PvpMatchManager));

        private static readonly ConcurrentQueue<PvpIntent> intents = new ConcurrentQueue<PvpIntent>();

        /// <summary>The coordinator ticks at most this often; WorldManager calls <see cref="Tick"/> every heartbeat.</summary>
        public const double TickIntervalMilliseconds = 250;

        private static PvpMatchCoordinator coordinator;

        private static DateTime lastTickUtc = DateTime.MinValue;

        /// <summary>The production coordinator, or null before <see cref="Initialize"/>.</summary>
        public static PvpMatchCoordinator Coordinator => coordinator;

        /// <summary>
        /// Boot: builds the coordinator over the live gateway, spaces and shard sink, and queues the ratings read on
        /// the shard database worker. The result is published to the world thread by the first tick after it lands.
        /// </summary>
        public static void Initialize()
        {
            if (coordinator != null)
                return;

            coordinator = new PvpMatchCoordinator(
                new LivePvpPlayerGateway(),
                new LivePvpMatchSpaces(),
                new ShardPvpResultSink(),
                () => DateTime.UtcNow,
                () => PvpTunables.DialSource(),
                intents);

            coordinator.BeginLoadRatings();
            coordinator.BeginLoadTemplates();
            coordinator.BeginLoadLatestTemplates();

            log.Info("[PVP] arena coordinator initialized");
        }

        /// <summary>
        /// H1: called from WorldManager.UpdateGameWorld every heartbeat, right after the Thread Dungeon tick and inside
        /// its own try/catch there. Rate-limited here to <see cref="TickIntervalMilliseconds"/>.
        /// </summary>
        public static void Tick()
        {
            var c = coordinator;

            if (c == null)
                return;

            var now = DateTime.UtcNow;

            if ((now - lastTickUtc).TotalMilliseconds < TickIntervalMilliseconds)
                return;

            lastTickUtc = now;

            c.Tick();
        }

        // ---------------- intents (any thread) ----------------

        /// <summary>Thread-safe; callable from any thread. A null intent is ignored.</summary>
        public static void Report(PvpIntent intent)
        {
            if (intent == null)
                return;

            intents.Enqueue(intent);
        }

        /// <summary>Takes the oldest pending intent, if any. The production coordinator drains the same queue.</summary>
        public static bool TryDequeue(out PvpIntent intent)
        {
            return intents.TryDequeue(out intent);
        }

        /// <summary>How many intents are waiting. Diagnostics and tests only.</summary>
        public static int PendingCount => intents.Count;

        /// <summary>Test seam: empties the queue so one test class cannot leak intents into another.</summary>
        internal static void ClearForTests()
        {
            while (intents.TryDequeue(out _))
            {
            }
        }

        /// <summary>The forfeit a logout reports. Pure, so the shape is pinned by a test.</summary>
        public static PvpIntent LogoutForfeit(uint characterId, Guid matchId, DateTime utcNow)
        {
            return new PvpIntent(PvpIntentKind.Forfeit, characterId, matchId, ParticipantExit.ForfeitLogout, utcNow);
        }

        /// <summary>Battleground spawn protection ended early (the player attacked or cast a harmful spell). Pure, so the shape is pinned by a test.</summary>
        public static PvpIntent SpawnProtectionEnded(uint characterId, Guid matchId, DateTime utcNow)
        {
            return new PvpIntent(PvpIntentKind.SpawnProtectionEnded, characterId, matchId, ParticipantExit.Died, utcNow);
        }

        /// <summary>The death a Die() in a match reports. Pure, so the shape is pinned by a test.</summary>
        public static PvpIntent Death(uint characterId, Guid matchId, DateTime utcNow)
        {
            return new PvpIntent(PvpIntentKind.Death, characterId, matchId, ParticipantExit.Died, utcNow);
        }

        /// <summary>The death a Die() in a match reports, with the killing player's guid (0 when none).</summary>
        public static PvpIntent Death(uint characterId, Guid matchId, DateTime utcNow, uint killerId)
        {
            return new PvpIntent(PvpIntentKind.Death, characterId, matchId, ParticipantExit.Died, utcNow, killerId);
        }

        /// <summary>
        /// The death a Die() in a match reports, with the killing player's guid (0 when none) and a COPY of where the victim died, which
        /// Attack/Defend's kill chip and heal range check reads (Docs/Pvp/ATTACK-DEFEND.md "Kill chip and heal"). Null position = unknown.
        /// </summary>
        public static PvpIntent Death(uint characterId, Guid matchId, DateTime utcNow, uint killerId, ACE.Entity.Position deathPosition)
        {
            return new PvpIntent(PvpIntentKind.Death, characterId, matchId, ParticipantExit.Died, utcNow, killerId,
                DeathPosition: deathPosition == null ? null : new ACE.Entity.Position(deathPosition));
        }

        /// <summary>
        /// PvP Template Facets: the template apply at entry failed, so the participant was never bound or teleported.
        /// ExitReason is a placeholder the coordinator never reads for this kind.
        /// </summary>
        public static PvpIntent EntryFailed(uint characterId, Guid matchId, DateTime utcNow, bool playerCaused = false, bool retryable = false)
        {
            return new PvpIntent(PvpIntentKind.EntryFailed, characterId, matchId, ParticipantExit.ForfeitLeft, utcNow, PlayerCaused: playerCaused, Retryable: retryable);
        }

        /// <summary>PvP Template Facets: the equipped-item backstop moved <paramref name="moved"/> personal item(s) off a participant.</summary>
        public static PvpIntent BackstopFired(uint characterId, Guid matchId, DateTime utcNow, int moved, int survivors = 0)
        {
            return new PvpIntent(PvpIntentKind.BackstopFired, characterId, matchId, ParticipantExit.ForfeitLeft, utcNow, 0, moved, survivors);
        }

        /// <summary>Attack/Defend: the report a destroyed crystal sends. The crystal's plan index rides in Count, the killing player's guid in KillerId (0 when unknown).</summary>
        public static PvpIntent ObjectiveDestroyed(Guid matchId, int crystalIndex, uint killerGuid, DateTime utcNow)
        {
            return new PvpIntent(PvpIntentKind.ObjectiveDestroyed, 0, matchId, ParticipantExit.Died, utcNow, killerGuid, crystalIndex);
        }

        // ---------------- API for PR D (world thread) ----------------

        /// <summary>
        /// Queues the player. <paramref name="templateKey"/> null uses their remembered template (PvP Template Facets,
        /// arena modes only); <paramref name="group"/> is the battleground group join.
        /// </summary>
        public static PvpJoinResult Join(Player player, string modeKey, bool duo, string templateKey = null, bool group = false)
        {
            if (player == null)
                return new PvpJoinResult(PvpJoinRefusal.Offline, modeKey);

            return coordinator?.Join(player.Guid.Full, modeKey, duo, templateKey, group) ?? new PvpJoinResult(PvpJoinRefusal.ArenaDisabled, modeKey);
        }

        // ---------------- PvP Template Facets (world thread) ----------------

        /// <summary>Templates as the commands list them; empty before Initialize or while the catalog is not loaded.</summary>
        public static IReadOnlyList<PvpTemplateOffer> ListTemplates(bool offeredOnly) => coordinator?.ListTemplates(offeredOnly) ?? Array.Empty<PvpTemplateOffer>();

        public static bool TemplatesLoaded => coordinator?.TemplatesLoaded ?? false;

        public static bool IsTemplateOffered(string key, string modeKey) => coordinator?.IsTemplateOffered(key, modeKey) ?? false;

        public static bool IsTemplateOfferedAnywhere(string key) => coordinator?.IsTemplateOfferedAnywhere(key) ?? false;

        public static string TemplateLabel(string key) => coordinator?.TemplateLabel(key) ?? key;

        /// <summary>The template a character fought their latest match on, on a ladder, as a label; null when unknown.</summary>
        public static string LatestTemplateLabel(uint characterId, string ladder) => coordinator?.LatestTemplateLabel(characterId, ladder);

        /// <summary>Re-reads every pvp_template row (after an admin change). The catalog swaps in on a later tick.</summary>
        public static void ReloadTemplates() => coordinator?.BeginLoadTemplates();

        public static PvpLeaveResult Leave(Player player)
        {
            return player == null || coordinator == null ? new PvpLeaveResult(PvpLeaveOutcome.NotQueued) : coordinator.Leave(player.Guid.Full);
        }

        public static PvpAnswerResult Accept(Player player)
        {
            return player == null || coordinator == null ? new PvpAnswerResult(PvpAnswerOutcome.NoPendingMatch) : coordinator.Accept(player.Guid.Full);
        }

        public static PvpAnswerResult Decline(Player player)
        {
            return player == null || coordinator == null ? new PvpAnswerResult(PvpAnswerOutcome.NoPendingMatch) : coordinator.Decline(player.Guid.Full);
        }

        /// <summary>/arena leave in a match, after the leave confirmation (PR D).</summary>
        public static PvpForfeitResult Forfeit(Player player) => Forfeit(player, null);

        /// <summary>The leave popup's Yes: a no-op unless the player is still in <paramref name="expectedMatchId"/>.</summary>
        public static PvpForfeitResult Forfeit(Player player, Guid? expectedMatchId)
        {
            return player == null || coordinator == null ? new PvpForfeitResult(PvpForfeitOutcome.NotInMatch) : coordinator.Forfeit(player.Guid.Full, expectedMatchId);
        }

        public static PvpStatusResult Status(Player player)
        {
            return player == null || coordinator == null ? new PvpStatusResult(PvpStatusKind.Idle) : coordinator.Status(player.Guid.Full);
        }

        /// <summary>Null while ratings are loading or unavailable.</summary>
        public static PvpRatingView GetRating(uint characterId, string ladder) => coordinator?.GetRating(characterId, ladder);

        /// <summary>Null while ratings are loading or unavailable.</summary>
        public static IReadOnlyList<PvpRatingView> GetTop(string ladder, int count) => coordinator?.GetTop(ladder, count);

        /// <summary>Loading, Available or Unavailable, so PR D can tell "no record" from "no ratings at all".</summary>
        public static PvpRatingStoreState RatingsState => coordinator?.Ratings.State ?? PvpRatingStoreState.Loading;

        public static IReadOnlyList<PvpMatchSummary> ListMatches() => coordinator?.ListMatches() ?? Array.Empty<PvpMatchSummary>();

        public static PvpCancelResult Cancel(Guid matchId) => coordinator?.Cancel(matchId) ?? new PvpCancelResult(PvpCancelOutcome.NotFound);

        public static int ClearQueue(string modeKey) => coordinator?.ClearQueue(modeKey) ?? 0;

        /// <summary>The accept popup's reply (<see cref="Confirmation_PvpArenaAccept"/>), on the world thread.</summary>
        internal static void AnswerAcceptPopup(uint characterId, Guid matchId, bool accepted)
        {
            coordinator?.AnswerFromPopup(characterId, matchId, accepted);
        }
    }
}
