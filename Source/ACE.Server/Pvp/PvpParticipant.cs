namespace ACE.Server.Pvp
{
    /// <summary>
    /// One character's seat in a <see cref="PvpMatch"/>. Rating and IP key are snapshotted at match
    /// formation time; ExitReason is set exactly once, by <see cref="IWinCondition.OnParticipantOut"/>,
    /// when the participant is eliminated (dies or forfeits) and never respawns (design: "Single
    /// elimination and no respawn in every arena mode").
    /// </summary>
    public sealed class PvpParticipant
    {
        public uint CharacterId { get; }

        /// <summary>Rating snapshotted at match formation, used by the rating model at resolve time.</summary>
        public int RatingAtStart { get; }

        /// <summary>
        /// Opaque same-IP grouping key (null if unknown/not applicable). Two participants sharing a
        /// non-null key are excluded from each other's pairwise rating comparison (FFA) and from being
        /// matched together in 1v1/2v2 when pvp_arena_block_same_ip is on.
        /// </summary>
        public string IpKey { get; }

        /// <summary>Null while the participant is still active in the match.</summary>
        public ParticipantExit? ExitReason { get; set; }

        public PvpParticipant(uint characterId, int ratingAtStart, string ipKey = null)
        {
            CharacterId = characterId;
            RatingAtStart = ratingAtStart;
            IpKey = ipKey;
        }
    }
}
