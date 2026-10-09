using System;

using Microsoft.EntityFrameworkCore;

namespace ACE.Database.Models.Shard
{
    // PvP Template Facets: the `pvp_template` and `pvp_template_history` tables created by
    // Database/Updates/Shard/2026-10-03-00-Add-Pvp-Templates.sql.
    //
    // Hand-written partials, following PvpArenaPartial.cs. These entities never leave ACE.Database:
    // ShardDatabase_PvpTemplates.cs maps them to and from the plain records in PvpTemplateRecords.cs.
    //
    // DELIBERATELY NOT MAPPED: the two columns the same migration adds to `pvp_match_participant`
    // (`template_Key`, `template_Version`). Mapping them on PvpMatchParticipant would put them in every
    // participant INSERT, so a shard whose migration has not run yet would fail to save ANY match.
    // ShardDatabase_PvpTemplates.SetPvpMatchParticipantTemplates writes them with a guarded UPDATE that
    // tolerates the missing columns instead.

    /// <summary>One template key: the current definition, its admin switches, and where it came from.</summary>
    public partial class PvpTemplate
    {
        public string TemplateKey { get; set; }

        public string DisplayName { get; set; }

        public uint SourceCharacterId { get; set; }

        public string SourceCharacterName { get; set; }

        public uint Version { get; set; }

        public bool Enabled { get; set; }

        /// <summary>Comma-separated mode keys, e.g. "arena_1v1,arena_ffa". Empty offers the template nowhere.</summary>
        public string Modes { get; set; }

        public string DefinitionJson { get; set; }

        /// <summary>UTC, always written explicitly.</summary>
        public DateTime SnapshotAt { get; set; }

        public string SnapshotBy { get; set; }
    }

    /// <summary>One snapshot of one key. Append-only.</summary>
    public partial class PvpTemplateHistory
    {
        public string TemplateKey { get; set; }

        public uint Version { get; set; }

        public string DefinitionJson { get; set; }

        /// <summary>UTC, always written explicitly.</summary>
        public DateTime SnapshotAt { get; set; }

        public string SnapshotBy { get; set; }
    }

    public partial class ShardDbContext
    {
        public virtual DbSet<PvpTemplate> PvpTemplate { get; set; }

        public virtual DbSet<PvpTemplateHistory> PvpTemplateHistory { get; set; }

        internal static void ConfigurePvpTemplate(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<PvpTemplate>(entity =>
            {
                entity.ToTable("pvp_template");

                entity.HasKey(e => e.TemplateKey);

                entity.Property(e => e.TemplateKey)
                    .IsRequired()
                    .HasColumnName("template_Key")
                    .HasMaxLength(32)
                    .HasCharSet("ascii")
                    .UseCollation("ascii_bin")
                    .ValueGeneratedNever();

                entity.Property(e => e.DisplayName)
                    .IsRequired()
                    .HasColumnName("display_Name")
                    .HasMaxLength(64);

                entity.Property(e => e.SourceCharacterId).HasColumnName("source_Character_Id");

                entity.Property(e => e.SourceCharacterName)
                    .IsRequired()
                    .HasColumnName("source_Character_Name")
                    .HasMaxLength(255);

                entity.Property(e => e.Version).HasColumnName("version");

                // The SQL DEFAULT b'0' is a backstop only, not told to EF, for the reason PvpMatch.Rated gives.
                entity.Property(e => e.Enabled)
                    .HasColumnName("enabled")
                    .HasColumnType("bit(1)");

                entity.Property(e => e.Modes)
                    .IsRequired()
                    .HasColumnName("modes")
                    .HasMaxLength(64)
                    .HasCharSet("ascii")
                    .UseCollation("ascii_bin");

                entity.Property(e => e.DefinitionJson)
                    .IsRequired()
                    .HasColumnName("definition_Json")
                    .HasColumnType("mediumtext");

                entity.Property(e => e.SnapshotAt)
                    .HasColumnType("datetime")
                    .HasColumnName("snapshot_At");

                entity.Property(e => e.SnapshotBy)
                    .IsRequired()
                    .HasColumnName("snapshot_By")
                    .HasMaxLength(255);
            });
        }

        internal static void ConfigurePvpTemplateHistory(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<PvpTemplateHistory>(entity =>
            {
                entity.ToTable("pvp_template_history");

                entity.HasKey(e => new { e.TemplateKey, e.Version });

                entity.Property(e => e.TemplateKey)
                    .IsRequired()
                    .HasColumnName("template_Key")
                    .HasMaxLength(32)
                    .HasCharSet("ascii")
                    .UseCollation("ascii_bin")
                    .ValueGeneratedNever();

                entity.Property(e => e.Version)
                    .HasColumnName("version")
                    .ValueGeneratedNever();

                entity.Property(e => e.DefinitionJson)
                    .IsRequired()
                    .HasColumnName("definition_Json")
                    .HasColumnType("mediumtext");

                entity.Property(e => e.SnapshotAt)
                    .HasColumnType("datetime")
                    .HasColumnName("snapshot_At");

                entity.Property(e => e.SnapshotBy)
                    .IsRequired()
                    .HasColumnName("snapshot_By")
                    .HasMaxLength(255);
            });
        }
    }
}
