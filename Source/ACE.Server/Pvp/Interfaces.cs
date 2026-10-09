using System;
using System.Collections.Generic;

using ACE.Server.WorldObjects;

namespace ACE.Server.Pvp
{
    /// <summary>Read-only view of a match, handed to rule checks that must never mutate state.</summary>
    public interface IMatchView
    {
        Guid MatchId { get; }

        string ModeKey { get; }

        IReadOnlyList<PvpTeam> Teams { get; }

        PvpMatchState State { get; }

        DateTime? LiveSinceUtc { get; }
    }

    /// <summary>
    /// The mutation surface a tick handler gets. Kept as small as <see cref="IMatchView"/> plus a state
    /// setter for PR A - the coordinator (PR C) is what actually drives players, teleports and timers, and
    /// it does so through queued player actions, never through this interface.
    /// </summary>
    public interface IMatchContext : IMatchView
    {
        void SetState(PvpMatchState state);
    }

    /// <summary>Pure matchmaker: turns the waiting queue into a proposal, or null if none forms yet.</summary>
    public interface IMatchmaker
    {
        MatchProposal TryForm(IReadOnlyList<QueueEntrant> waiting, MatchmakingContext ctx);
    }

    /// <summary>
    /// The single win-condition shape every arena mode shares (Docs/Pvp/DESIGN.md "Must stay identical").
    /// </summary>
    public interface IWinCondition
    {
        void OnParticipantOut(IMatchView m, PvpParticipant p, ParticipantExit how);

        /// <summary>Returns null while the match continues, or the finished MatchOutcome.</summary>
        MatchOutcome Evaluate(IMatchView m, DateTime utcNow);
    }

    /// <summary>A mode's own per-tick behaviour hook (battlegrounds' extension point; arena modes use the no-op).</summary>
    public interface IMatchTickHandler
    {
        void OnLive(IMatchContext m);

        void OnTick(IMatchContext m, DateTime utcNow);
    }

    /// <summary>Decides what happens to a participant on death. Every v1 arena mode uses <see cref="NoRespawnPolicy"/>.</summary>
    public interface IRespawnPolicy
    {
        DeathDisposition OnDeath(IMatchView m, PvpParticipant p);
    }

    /// <summary>
    /// Allocates and releases the physical space a match plays in. Left free of live-server types where
    /// possible; Player is unavoidable in Allocate's signature, so it appears here only. The v1 implementation
    /// is EphemeralMatchSpaceProvider; its doc comment is the authority on threading and readiness.
    /// </summary>
    public interface IMatchSpaceProvider
    {
        /// <summary>
        /// Creates the space and admits every player in <paramref name="admitted"/>. Never throws for a refusal:
        /// a failure comes back as <see cref="MatchSpaceAllocation.Failure"/>, with nothing left allocated.
        /// </summary>
        MatchSpaceAllocation Allocate(ArenaMap map, IReadOnlyList<Player> admitted);

        /// <summary>Non-blocking readiness poll for an allocated space.</summary>
        MatchSpaceReadiness GetReadiness(MatchSpace s);

        /// <summary>Hands the space to the destruction path once it is empty. Idempotent; a null space is a no-op.</summary>
        void Release(MatchSpace s);
    }

    /// <summary>Computes rating deltas for a finished, rated match.</summary>
    public interface IRatingModel
    {
        IReadOnlyDictionary<uint, int> Deltas(IReadOnlyList<RatedTeam> teams, MatchOutcome o);
    }
}
