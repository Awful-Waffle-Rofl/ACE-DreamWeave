using System;

namespace ACE.Server.Pvp
{
    /// <summary>
    /// A cross-thread event pushed onto the coordinator's ConcurrentQueue&lt;PvpIntent&gt; (Docs/Pvp/DESIGN.md
    /// "Threading: Inputs") - a death from a landblock thread, or a forfeit from Player.LogOut or a command.
    /// Pure data; the coordinator (PR C) is what drains and acts on it.
    /// </summary>
    /// <remarks>
    /// KillerId is the guid of the player who landed the killing blow, for a Death; 0 when unknown or not a player.
    /// Count is the number of items a BackstopFired intent reports moved, and Survivors the number of personal items
    /// still worn after it; both 0 for every other kind. ExitReason is only read for Death and Forfeit.
    /// PlayerCaused and Retryable are only read for EntryFailed: the player caused the failure (locked out), and the
    /// failure was only a busy player (retried for a short window first).
    /// DeathPosition is a copy of where the victim stood when a Death was reported (Attack/Defend's kill chip and heal range check,
    /// Docs/Pvp/ATTACK-DEFEND.md "Kill chip and heal"); null for every other kind, and for a death reported without one.
    /// </remarks>
    public sealed record PvpIntent(PvpIntentKind Kind, uint CharacterId, Guid MatchId, ParticipantExit ExitReason, DateTime OccurredAtUtc, uint KillerId = 0, int Count = 0, int Survivors = 0, bool PlayerCaused = false, bool Retryable = false, ACE.Entity.Position DeathPosition = null);
}
