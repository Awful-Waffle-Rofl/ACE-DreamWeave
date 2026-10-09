using System;
using System.Collections.Generic;

using ACE.Database;
using ACE.Entity;
using ACE.Server.Pvp.Templates;

namespace ACE.Server.Pvp
{
    /// <summary>Where a participant is, relative to their match instance, as the coordinator's tick sees it.</summary>
    public enum PvpPresence
    {
        /// <summary>Not logged in.</summary>
        Offline,

        /// <summary>Online and standing inside the match instance.</summary>
        InInstance,

        /// <summary>Online, not teleporting, and somewhere other than the match instance.</summary>
        Elsewhere,

        /// <summary>Online and mid-teleport: undecided, checked again next tick.</summary>
        InTransit
    }

    /// <summary>
    /// Everything the queue-join check needs from a live player, snapshotted by the gateway so the coordinator never
    /// reads a Player. IpKey is the session endpoint address; FellowshipMemberIds is empty when not in a fellowship.
    /// OnPkFacet is still reported but no longer gates admission (owner ruling 2026-10-03, Docs/Pvp/TEMPLATES.md).
    /// PvP Template Facets: PreferredTemplateKey is the remembered /arena template choice (PropertyString 9023, null when
    /// none), IsTemplateAccount is true for a character on pvp_template_account, and IsPvpTemplated is true while the
    /// character still carries a template restore record.
    /// </summary>
    public sealed record PvpPlayerFacts(
        uint CharacterId,
        string Name,
        int Level,
        string IpKey,
        bool InInstance,
        bool InRespite,
        bool PkTimerActive,
        bool IsOlthoi,
        bool IsMule,
        bool IsDead,
        bool IsTeleporting,
        bool OnPkFacet,
        IReadOnlyList<uint> FellowshipMemberIds,
        uint MonarchId = 0,
        string PreferredTemplateKey = null,
        bool IsTemplateAccount = false,
        bool IsPvpTemplated = false);

    /// <summary>
    /// The coordinator's only door to live players. Every method is called on the WORLD thread. The live
    /// implementation (<see cref="LivePvpPlayerGateway"/>) queues each change on the player's own action queue; the
    /// tests use a recording fake. No method may throw into the coordinator for an offline player: an offline
    /// player is a normal state here (a logout forfeit), and the call is a logged no-op.
    ///
    /// <para/>
    /// ONE DELIBERATE EXCEPTION to "queues each change on the player's own action queue": the battleground team
    /// fellowship seam (Battlegrounds.IPvpTeamFellowshipGateway, which the live gateway also implements) runs
    /// SYNCHRONOUSLY on the world thread. A fellowship spans several players with separate queues, and the client's own
    /// fellowship actions are dispatched on the world thread, so that is the one place a whole team can be changed in a
    /// single consistent step (Docs/Pvp/BATTLEGROUNDS.md "Team fellowships").
    /// </summary>
    public interface IPvpPlayerGateway
    {
        /// <summary>A snapshot of an online player, or null when they are offline.</summary>
        PvpPlayerFacts GetFacts(uint characterId);

        bool IsOnline(uint characterId);

        /// <summary>A copy of where the player stands now, or null when offline. Becomes their stamped exit.</summary>
        Position GetCurrentPosition(uint characterId);

        PvpPresence GetPresence(uint characterId, uint matchInstance);

        /// <summary>
        /// Places a participant. PvP Template Facets (Docs/Pvp/TEMPLATES.md "Lifecycle"): the template on
        /// <paramref name="binding"/> is applied on the player's queue FIRST; only a successful apply goes on to
        /// EnterPvpMatch(binding, exitTo) and the teleport to <paramref name="spawn"/>. A failed apply (or a binding with
        /// no template) is never bound and never teleported: it is reported back as a PvpIntentKind.EntryFailed intent.
        /// </summary>
        void EnterAndTeleport(uint characterId, PvpPlayerBinding binding, Position exitTo, Position spawn);

        /// <summary>Publishes a state change (ReplacePvpBinding). Retried on the player's queue if EnterPvpMatch has not run yet.</summary>
        void PublishBinding(uint characterId, PvpPlayerBinding binding);

        /// <summary>ExitPvpMatch(context): idempotent. For an offline player this is a no-op; the 9075 login restore covers them.</summary>
        void ExitMatch(uint characterId, string context);

        /// <summary>Teleports the player to <paramref name="exitTo"/> ONLY if they are still inside <paramref name="matchInstance"/>.</summary>
        void ReturnToExit(uint characterId, Position exitTo, uint matchInstance);

        /// <summary>A chat line. Dropped for an offline player.</summary>
        void Send(uint characterId, string text);

        /// <summary>Queues the accept popup. False when it could not be queued (offline, or another Yes/No is pending).</summary>
        bool SendAcceptPrompt(uint characterId, Guid matchId, string text);

        /// <summary>Closes this match's accept popup if it is still open. Safe to call when there is none.</summary>
        void AbortAcceptPrompt(uint characterId, Guid matchId);

        /// <summary>
        /// Pays <paramref name="amount"/> (positive) arena Blood for one resolved match (Docs/Pvp/DESIGN.md "Rewards").
        /// Called at most once per participant per match. The daily-cap check and increment happen at GRANT time
        /// against the live character (the live gateway: on the player's own action chain; offline: on the stored
        /// character), never in the coordinator. Blood that does not fit is kept as owed and delivered at next login.
        /// </summary>
        void GrantArenaBlood(uint characterId, int amount, PvpBloodGrant grant);

        /// <summary>
        /// PvP Template Facets: the pack-room precheck for <paramref name="template"/> (the player's worn set is stripped
        /// into the main pack and the kit's pack items are issued there). Null when there is room or the player is
        /// offline (the offline handling owns that case), else the player-facing refusal line.
        /// </summary>
        string CheckTemplateRoom(uint characterId, PvpTemplateDefinition template);

        /// <summary>
        /// PvP Template Facets (TEMPLATES.md "Backstop"): runs Player.PvpTemplateRunEquippedBackstop on the player's
        /// own queue. When it moves anything or leaves personal items worn (survivors), the live gateway reports a
        /// PvpIntentKind.BackstopFired intent for <paramref name="matchId"/> carrying both counts; the coordinator
        /// ejects on survivors. A no-op for an offline player.
        /// </summary>
        void RunTemplateBackstop(uint characterId, Guid matchId);
    }

    /// <summary>
    /// The match-space seam, keyed by character id so the coordinator never handles a Player. The live
    /// implementation (<see cref="LivePvpMatchSpaces"/>) resolves the online players and delegates to
    /// <see cref="EphemeralMatchSpaceProvider"/>; see that class for threading and readiness.
    /// </summary>
    public interface IPvpMatchSpaces
    {
        /// <summary>Never throws; a refusal comes back as a failed allocation with nothing left allocated.</summary>
        MatchSpaceAllocation Allocate(ArenaMap map, IReadOnlyList<uint> characterIds);

        /// <summary>Non-blocking.</summary>
        MatchSpaceReadiness GetReadiness(MatchSpace space);

        /// <summary>Queues destruction once empty. Does not evict. Idempotent.</summary>
        void Release(MatchSpace space);
    }

    /// <summary>
    /// The database seam. Both callbacks may run on ANY thread (the live one runs them on a thread-pool thread
    /// under the shard database worker). The coordinator's callbacks only enqueue a plain result record; the
    /// world-thread tick is the only place a result is acted on.
    /// </summary>
    public interface IPvpResultSink
    {
        /// <summary>The boot read. The callback gets a list (empty when the table is missing) or NULL when the read failed.</summary>
        void LoadRatings(Action<List<PvpRatingRecord>> callback);

        /// <summary>One resolved match in one write. Never resubmitted on Failed or Ambiguous.</summary>
        void SaveMatchResult(PvpMatchRecord match, IReadOnlyList<PvpMatchParticipantRecord> participants, IReadOnlyList<PvpRatingRecord> ratingUpserts, Action<PvpMatchSaveResult, uint> callback);

        /// <summary>PvP Template Facets: every pvp_template row. The callback gets (rows, status); rows is null on Failed.</summary>
        void LoadTemplates(Action<List<PvpTemplateRecord>, PvpTemplateStoreStatus> callback);

        /// <summary>
        /// PvP Template Facets: stamps each participant's template onto the participant rows of the already-saved
        /// match <paramref name="dbMatchId"/> (pvp_match.id). Queued only after that match's save came back Saved.
        /// </summary>
        void SaveParticipantTemplates(uint dbMatchId, IReadOnlyList<PvpMatchParticipantTemplateRecord> stamps, Action<PvpTemplateStoreStatus> callback);

        /// <summary>
        /// PvP Template Facets: the boot read behind /top's template column - the template each character fought their
        /// most recent stamped match on, per ladder. The callback gets (rows, status); rows is null on Failed.
        /// </summary>
        void LoadLatestParticipantTemplates(Action<List<PvpLatestParticipantTemplateRecord>, PvpTemplateStoreStatus> callback);
    }

    /// <summary>
    /// The Arena Crier's only door to the /lfg channel (Docs/Pvp/DESIGN.md "Arena Crier"). The live implementation
    /// (<see cref="LivePvpArenaCrierAnnouncer"/>) is the MarketAdvertiser pattern applied to TurbineChatChannel.LFG;
    /// tests use a recording fake so <see cref="PvpArenaCrier"/> itself never needs a live player or chat session.
    /// </summary>
    public interface IPvpArenaCrierAnnouncer
    {
        /// <summary>Sends every line, in order, as its own GameMessageTurbineChat, with <paramref name="senderName"/> as the speaker.</summary>
        void Announce(IReadOnlyList<string> lines, string senderName);
    }
}
