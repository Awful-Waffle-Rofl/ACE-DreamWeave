using System;
using System.Collections.Generic;

namespace ACE.Server.Pvp
{
    /// <summary>
    /// Why a queue join was refused. The admission refusals mirror <see cref="PvpAdmissionRefusal"/>; the rest are
    /// the coordinator's own checks. PR D maps each to a PvpArenaText line.
    /// </summary>
    public enum PvpJoinRefusal
    {
        None,
        UnknownMode,
        ArenaDisabled,
        ModeDisabled,
        AlreadyInInstance,
        NotOnPkFacet,
        InRespite,
        ActivePkTimer,
        IsOlthoi,
        IsMule,
        IsDead,
        IsTeleporting,
        AlreadyQueued,
        AlreadyInMatch,
        BelowMinLevel,
        Denylisted,
        DeclineLockout,
        DuoNotSupportedForMode,
        DuoNeedsPair,
        DuoPartnerIneligible,
        Offline,

        // ---- PvP Template Facets (Docs/Pvp/TEMPLATES.md): every arena and battleground match is templated ----

        /// <summary>The character is on the template account (pvp_template_account).</summary>
        TemplateAccount,

        /// <summary>The character still carries a template restore record; staff must run /pvptemplate restore.</summary>
        TemplateLocked,

        /// <summary>pvp_template_enabled is off. Templates are mandatory, so the arena and battlegrounds are closed.</summary>
        TemplatesDisabled,

        /// <summary>The template catalog has not been read yet (or the read failed): nothing can be validated.</summary>
        TemplatesUnavailable,

        /// <summary>No key was given and no preference is remembered: the command lists the templates instead.</summary>
        NoTemplateChosen,

        /// <summary>The chosen key is not an enabled template offered for this mode. <see cref="PvpJoinResult.TemplateKey"/> names it.</summary>
        TemplateNotOffered,

        /// <summary>Not enough pack room for the stripped gear plus the kit. <see cref="PvpJoinResult.Detail"/> carries the line.</summary>
        TemplateNoRoom,

        // ---- battlegrounds group join (appended) ----

        /// <summary>A group join outside the battleground room.</summary>
        GroupNotSupportedForMode,

        /// <summary>A group join needs a fellowship of 2 to pvp_bg_max_premade_size members that includes the caller.</summary>
        GroupNeedsFellowship,

        /// <summary>A member of the group failed admission; <see cref="PvpJoinResult.PartnerName"/> names them.</summary>
        GroupMemberIneligible
    }

    /// <summary>
    /// Result of <see cref="PvpMatchCoordinator.Join"/>. On success <see cref="WaitingCount"/> is the players now in
    /// that queue and <see cref="PartnerName"/> is set for a duo. On refusal the fields each line needs are set:
    /// ModeKey (mode disabled, already queued), MinLevel (too low), LockoutSecondsRemaining, PartnerName (partner
    /// ineligible), TemplateKey (the template queued on, or the one refused), Detail (a ready-made refusal line, e.g. the
    /// pack-room shortfall), TemplateLabel (on success, the queued template's display name).
    /// </summary>
    public sealed record PvpJoinResult(
        PvpJoinRefusal Refusal,
        string ModeKey,
        int WaitingCount = 0,
        string PartnerName = null,
        int LockoutSecondsRemaining = 0,
        int MinLevel = 0,
        string TemplateKey = null,
        string Detail = null,
        string TemplateLabel = null,
        int GroupMax = 0)
    {
        public bool Joined => Refusal == PvpJoinRefusal.None;
    }

    public enum PvpLeaveOutcome
    {
        /// <summary>Not queued and not in a match.</summary>
        NotQueued,

        /// <summary>Left the queue (a duo leaves together; <see cref="PvpLeaveResult.PartnerName"/> names the partner).</summary>
        LeftQueue,

        /// <summary>Was being asked to accept a match: treated as a decline.</summary>
        DeclinedMatch,

        /// <summary>In a match: PR D asks the leave confirmation, then calls <see cref="PvpMatchCoordinator.Forfeit"/>.</summary>
        InMatchNeedsConfirm
    }

    /// <param name="MatchId">Set for <see cref="PvpLeaveOutcome.InMatchNeedsConfirm"/>: the match the leave popup is about.</param>
    public sealed record PvpLeaveResult(PvpLeaveOutcome Outcome, string ModeKey = null, string PartnerName = null, Guid? MatchId = null);

    public enum PvpAnswerOutcome
    {
        /// <summary>No match is waiting on this player's answer.</summary>
        NoPendingMatch,

        /// <summary>This player already answered this match.</summary>
        AlreadyAnswered,

        Accepted,

        Declined
    }

    public sealed record PvpAnswerResult(PvpAnswerOutcome Outcome, Guid MatchId = default);

    public enum PvpForfeitOutcome
    {
        NotInMatch,

        /// <summary>Forfeited a match in progress: exited and returned now.</summary>
        Forfeited,

        /// <summary>The match had not placed anyone yet (accept or staging): handled as a decline.</summary>
        Declined,

        /// <summary>Already out of the match (eliminated or forfeited earlier), or it is already resolving.</summary>
        AlreadyOut
    }

    public sealed record PvpForfeitResult(PvpForfeitOutcome Outcome);

    public enum PvpStatusKind
    {
        Idle,
        Queued,
        AwaitingAccept,
        InMatch
    }

    /// <summary>
    /// Result of <see cref="PvpMatchCoordinator.Status"/>. Queued: ModeKey, Waited, WaitingCount. InMatch or
    /// AwaitingAccept: ModeKey, MapKey, MapName (the player-facing name, "Arena I", for the "on {map}" line), State
    /// and Remaining (the time limit left once Live, else null). Overtime is true once a Live match has gone to
    /// overtime, and Remaining is then the time left in overtime. For a battleground (objective) match Scores is each
    /// team's score keyed by TeamIndex and ScoreTarget the target it plays to; both are null / 0 for an arena match.
    /// StatusLine is the objective mode's own suffix for the in-match line (IObjectiveModeHandler.StatusLine); null means
    /// none was supplied, and the command then falls back to the King of the Hill score suffix built from Scores.
    /// </summary>
    public sealed record PvpStatusResult(
        PvpStatusKind Kind,
        string ModeKey = null,
        TimeSpan Waited = default,
        int WaitingCount = 0,
        string MapKey = null,
        PvpMatchState? State = null,
        TimeSpan? Remaining = null,
        string MapName = null,
        bool Overtime = false,
        IReadOnlyDictionary<int, int> Scores = null,
        int ScoreTarget = 0,
        string StatusLine = null);

    /// <summary>One active match, for the admin list.</summary>
    public sealed record PvpMatchSummary(
        Guid MatchId,
        string ModeKey,
        PvpMatchState State,
        string MapKey,
        string MapName,
        uint? Instance,
        IReadOnlyList<string> ParticipantNames,
        TimeSpan Age);

    public enum PvpCancelOutcome
    {
        NotFound,
        Canceled
    }

    public sealed record PvpCancelResult(PvpCancelOutcome Outcome, PvpMatchState? State = null);

    /// <summary>One template as the commands list it: key, display name, version, whether it is enabled, and its modes.</summary>
    public sealed record PvpTemplateOffer(string Key, string DisplayName, uint Version, bool Enabled, IReadOnlyList<string> Modes, bool Parsed);
}
