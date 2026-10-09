using System;
using System.Collections.Generic;

using Microsoft.EntityFrameworkCore;

namespace ACE.Database.Models.Shard
{
    // PvP Arena (DreamWeave): the `character_pvp_rating`, `pvp_match` and `pvp_match_participant`
    // tables created by Database/Updates/Shard/2026-09-25-02-Add-Pvp-Arena.sql.
    //
    // Hand-written partials rather than Scaffold-DbContext output, following
    // CharacterSpeedRunPartial.cs and CharacterCapLedgerPartial.cs. This is the established shape for
    // a FORK-custom shard table; the scaffolding flow described in CLAUDE.md applies to ACE's own
    // tables.
    //
    // These entities never leave ACE.Database. ShardDatabase_PvpArena.cs maps them to and from the
    // plain records in PvpArenaRecords.cs, which is all ACE.Server's arena code ever sees.

    /// <summary>
    /// One character's standing on one ladder. Upserted with absolute values when a rated match
    /// resolves. No FK to `character` - deliberate, see the migration header.
    /// </summary>
    public partial class CharacterPvpRating
    {
        public uint CharacterId { get; set; }

        /// <summary>Ladder code, e.g. arena_1v1.</summary>
        public string Ladder { get; set; }

        /// <summary>Snapshot at last write - survives a later character rename or delete.</summary>
        public string CharacterName { get; set; }

        /// <summary>Stored rating. Decay is applied when read, not here.</summary>
        public int Rating { get; set; }

        public int Games { get; set; }

        public int Wins { get; set; }

        public int Losses { get; set; }

        public int Draws { get; set; }

        public int Peak { get; set; }

        /// <summary>UTC, always written explicitly.</summary>
        public DateTime LastMatchAt { get; set; }
    }

    /// <summary>
    /// One resolved arena match. Append-only: nothing in gameplay updates or deletes a row.
    /// </summary>
    public partial class PvpMatch
    {
        public uint Id { get; set; }

        public string Mode { get; set; }

        public string Ladder { get; set; }

        public string Map { get; set; }

        /// <summary>UTC. Null when the match never reached Live.</summary>
        public DateTime? StartedAt { get; set; }

        /// <summary>UTC, always written explicitly.</summary>
        public DateTime EndedAt { get; set; }

        public string Outcome { get; set; }

        public string EndReason { get; set; }

        public bool Rated { get; set; }

        /// <summary>
        /// EF navigation only (no database FK). It exists so the participant rows pick up this
        /// match's AUTO_INCREMENT id inside the same SaveChanges that inserts the match.
        /// </summary>
        public virtual ICollection<PvpMatchParticipant> Participants { get; set; } = new List<PvpMatchParticipant>();
    }

    /// <summary>
    /// One character's line in one match. Append-only. No FK to `character` - deliberate, see the
    /// migration header.
    /// </summary>
    public partial class PvpMatchParticipant
    {
        public uint MatchId { get; set; }

        public uint CharacterId { get; set; }

        /// <summary>Snapshot at match time - survives a later character rename or delete.</summary>
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
    }

    public partial class ShardDbContext
    {
        public virtual DbSet<CharacterPvpRating> CharacterPvpRating { get; set; }

        public virtual DbSet<PvpMatch> PvpMatch { get; set; }

        public virtual DbSet<PvpMatchParticipant> PvpMatchParticipant { get; set; }

        internal static void ConfigureCharacterPvpRating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<CharacterPvpRating>(entity =>
            {
                entity.ToTable("character_pvp_rating");

                entity.HasKey(e => new { e.CharacterId, e.Ladder });

                entity.HasIndex(e => new { e.Ladder, e.Rating }, "character_pvp_rating_ladder_idx");

                // Natural key supplied by the server (upserted), not AUTO_INCREMENT.
                entity.Property(e => e.CharacterId)
                    .HasColumnName("character_Id")
                    .ValueGeneratedNever();

                entity.Property(e => e.Ladder)
                    .IsRequired()
                    .HasColumnName("ladder")
                    .HasMaxLength(32)
                    .HasCharSet("ascii")
                    .UseCollation("ascii_bin");

                entity.Property(e => e.CharacterName)
                    .IsRequired()
                    .HasColumnName("character_Name")
                    .HasMaxLength(255);

                entity.Property(e => e.Rating).HasColumnName("rating");

                entity.Property(e => e.Games).HasColumnName("games");

                entity.Property(e => e.Wins).HasColumnName("wins");

                entity.Property(e => e.Losses).HasColumnName("losses");

                entity.Property(e => e.Draws).HasColumnName("draws");

                entity.Property(e => e.Peak).HasColumnName("peak");

                // No HasDefaultValueSql, and no DEFAULT in the SQL: a missing value must fail the
                // insert rather than take the database server's local time.
                entity.Property(e => e.LastMatchAt)
                    .HasColumnType("datetime")
                    .HasColumnName("last_Match_At");
            });
        }

        internal static void ConfigurePvpMatch(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<PvpMatch>(entity =>
            {
                entity.ToTable("pvp_match");

                entity.HasKey(e => e.Id);

                entity.HasIndex(e => e.EndedAt, "pvp_match_ended_idx");

                // AUTO_INCREMENT in SQL.
                entity.Property(e => e.Id)
                    .HasColumnName("id")
                    .ValueGeneratedOnAdd();

                entity.Property(e => e.Mode)
                    .IsRequired()
                    .HasColumnName("mode")
                    .HasMaxLength(32)
                    .HasCharSet("ascii")
                    .UseCollation("ascii_bin");

                entity.Property(e => e.Ladder)
                    .IsRequired()
                    .HasColumnName("ladder")
                    .HasMaxLength(32)
                    .HasCharSet("ascii")
                    .UseCollation("ascii_bin");

                entity.Property(e => e.Map)
                    .IsRequired()
                    .HasColumnName("map")
                    .HasMaxLength(64);

                entity.Property(e => e.StartedAt)
                    .HasColumnType("datetime")
                    .HasColumnName("started_At");

                entity.Property(e => e.EndedAt)
                    .HasColumnType("datetime")
                    .HasColumnName("ended_At");

                entity.Property(e => e.Outcome)
                    .IsRequired()
                    .HasColumnName("outcome")
                    .HasMaxLength(16)
                    .HasCharSet("ascii")
                    .UseCollation("ascii_bin");

                entity.Property(e => e.EndReason)
                    .IsRequired()
                    .HasColumnName("end_Reason")
                    .HasMaxLength(32)
                    .HasCharSet("ascii")
                    .UseCollation("ascii_bin");

                // The SQL column carries DEFAULT b'0' as a backstop only. EF is deliberately NOT told
                // about it: a bool store default makes EF omit an explicit false from the INSERT, and
                // the server always writes this value.
                entity.Property(e => e.Rated)
                    .HasColumnName("rated")
                    .HasColumnType("bit(1)");

                // No database FK exists (see the migration header). This relationship lives in the EF
                // model only, so one SaveChanges inserts the match first and then its participants
                // with the generated id filled in.
                entity.HasMany(e => e.Participants)
                    .WithOne()
                    .HasForeignKey(p => p.MatchId);
            });
        }

        internal static void ConfigurePvpMatchParticipant(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<PvpMatchParticipant>(entity =>
            {
                entity.ToTable("pvp_match_participant");

                entity.HasKey(e => new { e.MatchId, e.CharacterId });

                entity.HasIndex(e => new { e.CharacterId, e.MatchId }, "pvp_match_participant_character_idx");

                entity.Property(e => e.MatchId)
                    .HasColumnName("match_Id")
                    .ValueGeneratedNever();

                entity.Property(e => e.CharacterId)
                    .HasColumnName("character_Id")
                    .ValueGeneratedNever();

                entity.Property(e => e.CharacterName)
                    .IsRequired()
                    .HasColumnName("character_Name")
                    .HasMaxLength(255);

                entity.Property(e => e.Team).HasColumnName("team");

                entity.Property(e => e.Placement).HasColumnName("placement");

                entity.Property(e => e.Result)
                    .IsRequired()
                    .HasColumnName("result")
                    .HasMaxLength(16)
                    .HasCharSet("ascii")
                    .UseCollation("ascii_bin");

                entity.Property(e => e.RatingBefore).HasColumnName("rating_Before");

                entity.Property(e => e.RatingAfter).HasColumnName("rating_After");

                entity.Property(e => e.Kills).HasColumnName("kills");

                entity.Property(e => e.Deaths).HasColumnName("deaths");

                entity.Property(e => e.ForfeitReason)
                    .HasColumnName("forfeit_Reason")
                    .HasMaxLength(32)
                    .HasCharSet("ascii")
                    .UseCollation("ascii_bin");
            });
        }
    }
}
