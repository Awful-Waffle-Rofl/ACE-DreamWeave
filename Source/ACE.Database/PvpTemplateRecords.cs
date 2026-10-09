using System;

namespace ACE.Database
{
    // PvP Template Facets: the plain records at the ACE.Database boundary. ACE.Server reads and writes
    // only these. The EF entities in Models/Shard/PvpTemplatePartial.cs never leave ACE.Database, and
    // ShardDatabase_PvpTemplates.cs is the only place the two shapes meet.
    //
    // definition_Json is opaque here: its schema belongs to ACE.Server.Pvp.Templates.PvpTemplateJson.
    // Every DateTime is UTC. Reads hand back DateTimeKind.Utc.

    /// <summary>
    /// The outcome of a template store call. Several values, because "the tables do not exist yet" is an
    /// expected boot state (the migration is config-gated) and must never be confused with a failure or
    /// with "no such key".
    /// </summary>
    public enum PvpTemplateStoreStatus
    {
        /// <summary>The call did what it was asked.</summary>
        Ok,

        /// <summary>The key does not exist (a read or an update of a missing row). Nothing was written.</summary>
        NotFound,

        /// <summary>The template tables do not exist (MySQL 1146). Template features refuse; nothing threw.</summary>
        TablesMissing,

        /// <summary>The call failed for any other reason. Nothing was written.</summary>
        Failed,

        /// <summary>
        /// A snapshot write hit a duplicate key (MySQL 1062). The likely cause is an execution-strategy retry
        /// after an attempt that DID commit but lost its acknowledgement, so the snapshot may well be saved.
        /// Never resubmit blindly: re-read the key and look at its version.
        /// </summary>
        Ambiguous,
    }

    /// <summary>One `pvp_template` row.</summary>
    public class PvpTemplateRecord
    {
        public string TemplateKey { get; set; }

        public string DisplayName { get; set; }

        public uint SourceCharacterId { get; set; }

        public string SourceCharacterName { get; set; }

        /// <summary>Bumped by every snapshot of this key. Assigned by the store on a snapshot write.</summary>
        public uint Version { get; set; }

        public bool Enabled { get; set; }

        /// <summary>Comma-separated mode keys. Empty offers the template nowhere.</summary>
        public string Modes { get; set; }

        public string DefinitionJson { get; set; }

        /// <summary>UTC.</summary>
        public DateTime SnapshotAt { get; set; }

        public string SnapshotBy { get; set; }

        /// <summary>A full copy. Every member is a value type or an immutable string.</summary>
        public PvpTemplateRecord Clone() => (PvpTemplateRecord)MemberwiseClone();
    }

    /// <summary>
    /// One participant's template stamp, written onto `pvp_match_participant` after the match row is saved
    /// (see ShardDatabase_PvpTemplates.SetPvpMatchParticipantTemplates for why it is a separate write).
    /// </summary>
    public class PvpMatchParticipantTemplateRecord
    {
        public uint CharacterId { get; set; }

        public string TemplateKey { get; set; }

        public uint TemplateVersion { get; set; }

        public PvpMatchParticipantTemplateRecord Clone() => (PvpMatchParticipantTemplateRecord)MemberwiseClone();
    }

    /// <summary>
    /// The template one character fought their most recent stamped match on, for one ladder: the boot read behind
    /// /top's template column (ShardDatabase_PvpTemplates.GetLatestPvpParticipantTemplates).
    /// </summary>
    public class PvpLatestParticipantTemplateRecord
    {
        public uint CharacterId { get; set; }

        public string Ladder { get; set; }

        public string TemplateKey { get; set; }

        public uint TemplateVersion { get; set; }
    }
}
