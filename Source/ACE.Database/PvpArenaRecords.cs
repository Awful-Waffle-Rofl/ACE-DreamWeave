using System;

namespace ACE.Database
{
    // PvP Arena (DreamWeave): the plain records at the ACE.Database boundary. ACE.Server's arena code
    // (ACE.Server.Pvp) reads and writes only these. The EF entities in
    // Models/Shard/PvpArenaPartial.cs never leave ACE.Database, and ShardDatabase_PvpArena.cs is the
    // only place the two shapes meet.
    //
    // String codes (ladder, mode, outcome, end reason, result, forfeit reason) are stored verbatim.
    // Their vocabulary belongs to the arena code, not to this layer. The column widths are the only
    // limit: ladder and mode 32, outcome and result 16, end reason and forfeit reason 32, map 64,
    // character name 255 (see Database/Updates/Shard/2026-09-25-02-Add-Pvp-Arena.sql).
    //
    // Every DateTime is UTC. Reads hand back DateTimeKind.Utc.

    /// <summary>
    /// The outcome of ShardDatabase.SavePvpMatchResult. Three values, because a lost commit
    /// acknowledgement makes a third state real (see that method's doc comment).
    /// </summary>
    public enum PvpMatchSaveResult
    {
        /// <summary>The match, its participants and every rating upsert were committed.</summary>
        Saved,

        /// <summary>Definitely nothing was written: refused up front, or the write failed outright.</summary>
        Failed,

        /// <summary>
        /// The outcome is UNKNOWN and the match may already be recorded. The save hit a duplicate key on
        /// a first-time rating insert, which is what an execution-strategy retry sees when an earlier
        /// attempt committed but its acknowledgement was lost.
        /// </summary>
        Ambiguous,
    }

    /// <summary>One character's standing on one ladder (`character_pvp_rating`).</summary>
    public class PvpRatingRecord
    {
        public uint CharacterId { get; set; }

        /// <summary>Ladder code, e.g. arena_1v1, arena_2v2, arena_ffa.</summary>
        public string Ladder { get; set; }

        /// <summary>Snapshot, written on every upsert.</summary>
        public string CharacterName { get; set; }

        /// <summary>
        /// Stored rating. Decay is the reader's job (design: worked out on read, written back only by
        /// the player's next match), so an upsert should carry the post-match value.
        /// </summary>
        public int Rating { get; set; }

        public int Games { get; set; }

        public int Wins { get; set; }

        public int Losses { get; set; }

        public int Draws { get; set; }

        public int Peak { get; set; }

        /// <summary>UTC. Left unset on an upsert, it takes the match's EndedAt.</summary>
        public DateTime LastMatchAt { get; set; }

        /// <summary>A full copy. Every member is a value type or an immutable string.</summary>
        internal PvpRatingRecord Clone() => (PvpRatingRecord)MemberwiseClone();
    }

    /// <summary>One resolved match (`pvp_match`). Append-only.</summary>
    public class PvpMatchRecord
    {
        /// <summary>
        /// Assigned by the database. Ignored on save. The new id is reported back through the save
        /// result instead.
        /// </summary>
        public uint Id { get; set; }

        public string Mode { get; set; }

        public string Ladder { get; set; }

        public string Map { get; set; }

        /// <summary>UTC. Null when the match never reached Live.</summary>
        public DateTime? StartedAt { get; set; }

        /// <summary>UTC. Required.</summary>
        public DateTime EndedAt { get; set; }

        public string Outcome { get; set; }

        public string EndReason { get; set; }

        public bool Rated { get; set; }

        /// <summary>A full copy. Every member is a value type or an immutable string.</summary>
        internal PvpMatchRecord Clone() => (PvpMatchRecord)MemberwiseClone();
    }

    /// <summary>One character's line in one match (`pvp_match_participant`). Append-only.</summary>
    public class PvpMatchParticipantRecord
    {
        /// <summary>
        /// Ignored on save: the participant is always attached to the match it is saved with.
        /// Populated on read.
        /// </summary>
        public uint MatchId { get; set; }

        public uint CharacterId { get; set; }

        /// <summary>Snapshot at match time.</summary>
        public string CharacterName { get; set; }

        public int Team { get; set; }

        /// <summary>1 = winner. FFA survivors share 1.</summary>
        public int Placement { get; set; }

        public string Result { get; set; }

        /// <summary>Null when the match was unrated for this participant.</summary>
        public int? RatingBefore { get; set; }

        /// <summary>Null when the match was unrated for this participant.</summary>
        public int? RatingAfter { get; set; }

        public int Kills { get; set; }

        public int Deaths { get; set; }

        /// <summary>Null unless this participant forfeited.</summary>
        public string ForfeitReason { get; set; }

        /// <summary>A full copy. Every member is a value type or an immutable string.</summary>
        internal PvpMatchParticipantRecord Clone() => (PvpMatchParticipantRecord)MemberwiseClone();
    }
}
