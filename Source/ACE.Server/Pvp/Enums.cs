namespace ACE.Server.Pvp
{
    /// <summary>
    /// PvP arena match lifecycle. AwaitingAccept -> Staging -> Countdown -> Live -> Resolving -> Closed.
    /// Canceled is reachable from AwaitingAccept and Staging, and from Countdown and Live by an admin cancel only
    /// (Docs/Pvp/DESIGN.md "Match states").
    /// </summary>
    public enum PvpMatchState
    {
        AwaitingAccept,
        Staging,
        Countdown,
        Live,
        Resolving,
        Closed,
        Canceled
    }

    /// <summary>
    /// How a participant left a match before it otherwise concluded for them. Died is a normal
    /// elimination; the three Forfeit* values are the ways design's H3/commands sections identify a
    /// dodge (Logout via LogOut, Left via leaving the match instance/recall, Command via /arena leave).
    /// </summary>
    public enum ParticipantExit
    {
        Died,
        ForfeitLogout,
        ForfeitLeft,
        ForfeitCommand
    }

    /// <summary>
    /// Why a match ended, carried on <see cref="MatchOutcome"/>. Timeout is the mode's time limit being
    /// reached (Docs/Pvp/DESIGN.md "Modes" table's Timeout column and the code-review request for a
    /// "TimeLimit" reason - this fork already had an equivalent value, so no duplicate was added).
    /// </summary>
    public enum EndReason
    {
        Elimination,
        Timeout,
        Canceled,
        AllForfeited,

        /// <summary>An objective mode's team reached its score target. Appended last: EndReason is persisted by name (ToString), never by ordinal, but appending keeps every existing value stable regardless.</summary>
        Score
    }

    /// <summary>How a mode treats a queued premade group. Design: "Premade 2v2 duos are allowed."</summary>
    public enum TeamFellowshipPolicy
    {
        /// <summary>The mode does not look at fellowship membership at all (e.g. FFA, 1v1).</summary>
        Ignored,

        /// <summary>A premade group already fellowshipped together may queue and land on the same team.</summary>
        PremadeAllowed,

        /// <summary>
        /// Battlegrounds (Docs/Pvp/BATTLEGROUNDS.md "Team fellowships"): premades may queue as with PremadeAllowed, AND
        /// each team is put into a server-built fellowship at Countdown (pvp_bg_team_fellowship), released on exit with
        /// the player's outside fellowship restored where possible (pvp_bg_team_fellowship_restore). Appended last.
        /// </summary>
        TeamFellowship
    }

    /// <summary>
    /// The two shapes <see cref="PvpIntent"/> carries off a landblock/logout thread onto the world-thread
    /// tick queue (Docs/Pvp/DESIGN.md "Threading: Inputs").
    /// </summary>
    public enum PvpIntentKind
    {
        Death,
        Forfeit,

        /// <summary>
        /// PvP Template Facets: the template apply at match entry failed (or the binding carried no template), so the
        /// participant was never bound and never teleported. A no-fault removal; ExitReason on the intent is unused.
        /// </summary>
        EntryFailed,

        /// <summary>
        /// PvP Template Facets: the per-tick equipped-item backstop moved <see cref="PvpIntent.Count"/> personal
        /// item(s) off a templated participant (a gate miss). Noted on the match for staff; ExitReason is unused.
        /// </summary>
        BackstopFired,

        /// <summary>
        /// Attack/Defend: a defender crystal died. <see cref="PvpIntent.Count"/> carries the crystal's plan index and <see cref="PvpIntent.KillerId"/> the guid of the player
        /// who landed the blow (0 when unknown); CharacterId is 0 and ExitReason is unused. Appended last, so every existing value stays stable.
        /// </summary>
        ObjectiveDestroyed,

        /// <summary>
        /// Battlegrounds: a protected player's spawn protection ended early because they attacked or cast a harmful spell
        /// (Docs/Pvp/BATTLEGROUNDS.md "Spawn protection"). CharacterId is the player; ExitReason is unused. Appended last.
        /// </summary>
        SpawnProtectionEnded
    }

    /// <summary>Outcome of <see cref="PvpArenaGate"/> - the pure PvP damage-permission decision.</summary>
    public enum PvpGateDecision
    {
        /// <summary>Neither side is in a live match; the retail PK rules apply instead (H6).</summary>
        NotApplicable,
        Allow,
        Refuse,

        /// <summary>The defender is inside a battleground spawn-protection window (a refusal with its own notice). Appended last.</summary>
        RefuseProtected
    }

    /// <summary>Every reason <see cref="PvpAdmission"/> can refuse a queue-join attempt, plus None (accepted).</summary>
    public enum PvpAdmissionRefusal
    {
        None,
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

        /// <summary>PvP Template Facets: a character on the template account (pvp_template_account) never fights.</summary>
        TemplateAccount,

        /// <summary>PvP Template Facets: the character still carries a template restore record (inert until staff restore it).</summary>
        TemplateLocked
    }
}
