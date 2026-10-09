namespace ACE.Server.Pvp.Battlegrounds
{
    /// <summary>
    /// One seat in a battleground match as the objective logic sees it: the participant, their team, and whether
    /// they are dead or waiting in the pen.
    /// </summary>
    public readonly record struct BattlegroundSeat(PvpParticipant Participant, int TeamIndex, bool Respawning);

    /// <summary>
    /// The single shared definition of "active" and "counts on the zone" (Docs/Pvp/BATTLEGROUNDS.md "Invariants for
    /// the step 2 fan-out"). Every battleground type uses these; none re-derives them.
    /// </summary>
    public static class BattlegroundSeats
    {
        /// <summary>Active means the participant has not exited the match: <c>ExitReason == null</c>. A respawning seat is still active.</summary>
        public static bool IsActive(PvpParticipant seat)
        {
            return seat != null && seat.ExitReason == null;
        }

        /// <summary>Active, as <see cref="IsActive(PvpParticipant)"/>, for a <see cref="BattlegroundSeat"/>.</summary>
        public static bool IsActive(BattlegroundSeat seat)
        {
            return IsActive(seat.Participant);
        }

        /// <summary>Counts on the zone means active and not respawning.</summary>
        public static bool CountsOnZone(BattlegroundSeat seat)
        {
            return IsActive(seat) && !seat.Respawning;
        }
    }
}