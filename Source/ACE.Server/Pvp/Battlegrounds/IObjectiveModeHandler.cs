using System.Collections.Generic;

namespace ACE.Server.Pvp.Battlegrounds
{
    /// <summary>
    /// The mode-specific text and tick behaviour the coordinator reads from an objective mode's tick handler, so it never casts to
    /// one concrete handler (Docs/Pvp/ATTACK-DEFEND.md "Mode seam"). <see cref="KothTickHandler"/> returns its existing strings
    /// unchanged; <see cref="AttackDefendTickHandler"/> names its sides Attackers and Defenders. Pure: every method is a function of its arguments.
    /// </summary>
    public interface IObjectiveModeHandler : IMatchTickHandler
    {
        /// <summary>The player-facing name of a team in this mode ("West", or "Attackers").</summary>
        string TeamName(int team);

        /// <summary>
        /// The line announced to every participant when the match ends, or null when the mode has none for this outcome.
        /// <paramref name="scores"/> is the final <c>ScoreBoard</c>; a missing team scores 0.
        /// </summary>
        string ResultLine(MatchOutcome outcome, IReadOnlyDictionary<int, int> scores);

        /// <summary>
        /// The suffix appended to the /arena status in-match line, or empty when the mode adds none. <paramref name="scoreTarget"/> is the
        /// target the match plays to (the score for King of the Hill, the crystal count for Attack/Defend); 0 means unknown, which adds nothing.
        /// </summary>
        string StatusLine(IReadOnlyDictionary<int, int> scores, int scoreTarget);

        /// <summary>The line sent once to each member of <paramref name="team"/> when the match goes Live, or null when the mode sends none.</summary>
        string RoleLine(int team);

        /// <summary>
        /// The line sent to a player whose respawn is confirmed (Attack/Defend sequential mode: which crystal is vulnerable), or null when
        /// the mode has none to send.
        /// </summary>
        string VulnerableLine();

        /// <summary>
        /// Binds the formed match, once, before anything reads the handler: the match's resolved <paramref name="layout"/> and, for a
        /// crystal mode, its <paramref name="plan"/> (null for every other mode). The mode definition builds the handler before either
        /// is known. Each mode takes what it uses and ignores the rest.
        /// </summary>
        void BindMatch(BattlegroundLayout layout, AttackDefendPlan plan);

        /// <summary>
        /// The zone the cosmetic marker ring outlines, read right after <see cref="BindMatch"/>; null when the mode places no ring.
        /// </summary>
        KothZone? MarkerZone { get; }

        /// <summary>
        /// An objective of the match was destroyed (an Attack/Defend crystal). True only when the mode counted it: the first report of a
        /// valid index. A mode with no destructible objectives returns false.
        /// </summary>
        bool OnObjectiveDestroyed(IObjectiveMatchContext ctx, int index);

        /// <summary>
        /// True while the objective at <paramref name="index"/> is a planned one the mode has not yet counted as destroyed (Attack/Defend's
        /// kill chip and heal skip a fallen crystal with it). False for an index outside the plan and for a mode with no destructible objectives.
        /// </summary>
        bool IsObjectiveStanding(int index);
    }

    /// <summary>
    /// What an Attack/Defend tick handler may read and ask of its match, on top of the objective-mode surface it inherits
    /// (Docs/Pvp/ATTACK-DEFEND.md "Alerts"). A separate interface so <see cref="IBattlegroundMatchContext"/> and its test fake stay untouched.
    /// Every method is called on the world thread and none blocks.
    /// </summary>
    public interface IObjectiveMatchContext : IBattlegroundMatchContext
    {
        /// <summary>
        /// The health of every crystal still standing, polled cosmetically from the placed objects (destruction itself is pushed as an
        /// intent, never read from here). A destroyed or unplaced crystal is absent.
        /// </summary>
        IReadOnlyList<CrystalHealth> SampleCrystals();

        /// <summary>Sends a chat line to every seat of one team (the defenders' under-attack alert).</summary>
        void AnnounceToTeam(int teamIndex, string text);
    }
}
