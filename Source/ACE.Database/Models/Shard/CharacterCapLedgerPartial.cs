using System;

using Microsoft.EntityFrameworkCore;

namespace ACE.Database.Models.Shard
{
    // Class Ability Point (CAP) audit ledger, round 1: the `character_cap_ledger` and
    // `character_cap_audit` tables created by
    // Database/Updates/Shard/2026-09-10-00-Add-Character-Cap-Ledger.sql.
    //
    // Hand-written partials rather than Scaffold-DbContext output, following
    // CharacterSpeedRunPartial.cs. This is the established shape for a FORK-custom shard table; the
    // scaffolding flow described in CLAUDE.md applies to ACE's own tables.

    /// <summary>
    /// One CAP mutation. Append-only: nothing in gameplay updates or deletes a row. Carries the
    /// before/after state (available/total/owned-cost after the mutation) needed to reconstruct
    /// <see cref="ACE.Server.ClassAbilities.CapLedger.Unexplained"/> without replaying every prior
    /// row. No FK to `character` - deliberate, see the migration header; CharacterName is a
    /// snapshot taken at write time for exactly that reason.
    /// </summary>
    public partial class CharacterCapLedger
    {
        public uint Id { get; set; }

        public uint CharacterId { get; set; }

        /// <summary>Snapshot at write time - survives a later character rename or delete.</summary>
        public string CharacterName { get; set; }

        /// <summary>
        /// UTC. Always written explicitly; the column's CURRENT_TIMESTAMP default is only a
        /// backstop and stamps the database server's LOCAL time, so a row that falls back to it is
        /// off by the local UTC offset.
        /// </summary>
        public DateTime Ts { get; set; }

        /// <summary>
        /// The lowercase snake code from <see cref="ACE.Server.ClassAbilities.CapLedgerReason.ToCode"/>.
        /// A varchar rather than an int - see the migration header for why this deliberately
        /// diverges from AccountVaultLog.Action.
        /// </summary>
        public string Reason { get; set; }

        /// <summary>Groups the N rows one full respec emits. Null for every non-batched reason.</summary>
        public string BatchId { get; set; }

        /// <summary>Signed change to AvailableClassAbilityPoints.</summary>
        public int DeltaAvailable { get; set; }

        /// <summary>Signed change to TotalClassAbilityPointsEarned.</summary>
        public int DeltaTotal { get; set; }

        public int AvailableAfter { get; set; }

        public int TotalAfter { get; set; }

        /// <summary>Cumulative cost of all ranks owned after this mutation.</summary>
        public int OwnedCostAfter { get; set; }

        /// <summary>Ability Name, for ability-scoped reasons. Null otherwise.</summary>
        public string Ability { get; set; }

        /// <summary>Rank of <see cref="Ability"/> after this mutation. Null otherwise.</summary>
        public int? RankAfter { get; set; }

        public string Detail { get; set; }
    }

    /// <summary>
    /// One upserted row per CAP-holding character, summarizing whether that character's ledger is
    /// currently balanced. <see cref="Unexplained"/> nonzero flags a shortfall (or surplus) an
    /// incident sweep should investigate. No FK to `character` - deliberate, see the migration
    /// header.
    /// </summary>
    public partial class CharacterCapAudit
    {
        public uint CharacterId { get; set; }

        /// <summary>Snapshot at last-checked time - survives a later character rename or delete.</summary>
        public string CharacterName { get; set; }

        public int TotalEarned { get; set; }

        public int Available { get; set; }

        public int OwnedCost { get; set; }

        public int SinkSpend { get; set; }

        /// <summary>
        /// TotalEarned - Available - OwnedCost - SinkSpend (<see cref="ACE.Server.ClassAbilities.CapLedger.Unexplained"/>).
        /// Nonzero flags a shortfall.
        /// </summary>
        public int Unexplained { get; set; }

        public int OrphanRows { get; set; }

        public int RankDivergences { get; set; }

        public DateTime? FirstDetectedAt { get; set; }

        public DateTime LastCheckedAt { get; set; }
    }

    public partial class ShardDbContext
    {
        public virtual DbSet<CharacterCapLedger> CharacterCapLedger { get; set; }

        public virtual DbSet<CharacterCapAudit> CharacterCapAudit { get; set; }

        internal static void ConfigureCharacterCapLedger(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<CharacterCapLedger>(entity =>
            {
                entity.ToTable("character_cap_ledger");

                entity.HasKey(e => e.Id);

                entity.HasIndex(e => new { e.CharacterId, e.Id }, "character_cap_ledger_character_idx");

                entity.HasIndex(e => e.Ts, "character_cap_ledger_ts_idx");

                entity.HasIndex(e => e.Reason, "character_cap_ledger_reason_idx");

                // AUTO_INCREMENT in SQL.
                entity.Property(e => e.Id)
                    .HasColumnName("id")
                    .ValueGeneratedOnAdd();

                entity.Property(e => e.CharacterId).HasColumnName("character_Id");

                entity.Property(e => e.CharacterName)
                    .IsRequired()
                    .HasColumnName("character_Name")
                    .HasMaxLength(255);

                // The server always writes Ts explicitly as DateTime.UtcNow, which can never equal
                // DateTime.MinValue, so the explicit value always round-trips; HasDefaultValueSql
                // here only ever helps the rare case where a caller leaves it unset (matching the
                // reasoning in CharacterSpeedRunPartial.cs for CompletedAt).
                entity.Property(e => e.Ts)
                    .HasColumnType("datetime")
                    .HasDefaultValueSql("CURRENT_TIMESTAMP")
                    .HasColumnName("ts");

                entity.Property(e => e.Reason)
                    .IsRequired()
                    .HasColumnName("reason")
                    .HasMaxLength(32);

                entity.Property(e => e.BatchId)
                    .HasColumnName("batch_Id")
                    .HasMaxLength(32);

                entity.Property(e => e.DeltaAvailable).HasColumnName("delta_Available");

                entity.Property(e => e.DeltaTotal).HasColumnName("delta_Total");

                entity.Property(e => e.AvailableAfter).HasColumnName("available_After");

                entity.Property(e => e.TotalAfter).HasColumnName("total_After");

                entity.Property(e => e.OwnedCostAfter).HasColumnName("owned_Cost_After");

                entity.Property(e => e.Ability)
                    .HasColumnName("ability")
                    .HasMaxLength(64);

                entity.Property(e => e.RankAfter).HasColumnName("rank_After");

                entity.Property(e => e.Detail)
                    .HasColumnName("detail")
                    .HasMaxLength(255);
            });
        }

        internal static void ConfigureCharacterCapAudit(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<CharacterCapAudit>(entity =>
            {
                entity.ToTable("character_cap_audit");

                entity.HasKey(e => e.CharacterId);

                // Content-authored key (upserted by the incident sweep, not AUTO_INCREMENT) - the
                // opposite of CharacterCapLedger.Id.
                entity.Property(e => e.CharacterId)
                    .HasColumnName("character_Id")
                    .ValueGeneratedNever();

                entity.Property(e => e.CharacterName)
                    .IsRequired()
                    .HasColumnName("character_Name")
                    .HasMaxLength(255);

                entity.Property(e => e.TotalEarned).HasColumnName("total_Earned");

                entity.Property(e => e.Available).HasColumnName("available");

                entity.Property(e => e.OwnedCost).HasColumnName("owned_Cost");

                entity.Property(e => e.SinkSpend).HasColumnName("sink_Spend");

                entity.Property(e => e.Unexplained).HasColumnName("unexplained");

                entity.Property(e => e.OrphanRows).HasColumnName("orphan_Rows");

                entity.Property(e => e.RankDivergences).HasColumnName("rank_Divergences");

                entity.Property(e => e.FirstDetectedAt)
                    .HasColumnType("datetime")
                    .HasColumnName("first_Detected_At");

                entity.Property(e => e.LastCheckedAt)
                    .HasColumnType("datetime")
                    .HasColumnName("last_Checked_At");
            });
        }
    }
}
