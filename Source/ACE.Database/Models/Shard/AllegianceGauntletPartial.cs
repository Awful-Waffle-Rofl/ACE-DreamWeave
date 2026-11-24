using System;

using Microsoft.EntityFrameworkCore;

namespace ACE.Database.Models.Shard
{
    // Allegiance Gauntlet: hand-written partial, following AllegianceAvatarPartial.cs.
    // Tables created by Database/Updates/Shard/2026-11-21-02-Add-Allegiance-Gauntlet.sql.

    /// <summary>One Avatar's Gauntlet best score and cooldown. One row per Avatar that has ever committed a run.</summary>
    public partial class AllegianceGauntletRecord
    {
        /// <summary>The Avatar's avatar_Id. Not generated: the record is keyed by the Avatar.</summary>
        public uint AvatarId { get; set; }

        /// <summary>Best score, in hundredths.</summary>
        public uint BestCenti { get; set; }

        public ushort BestParticipants { get; set; }

        public uint? BestRunId { get; set; }

        public DateTime? BestAt { get; set; }

        /// <summary>The cooldown: the commit time of the last run. Written at commit.</summary>
        public DateTime? LastRunAt { get; set; }

        public uint RunCount { get; set; }

        public DateTime UpdatedAt { get; set; }
    }

    /// <summary>One Gauntlet run. An open run has EndedAt NULL.</summary>
    public partial class AllegianceGauntletRun
    {
        /// <summary>AUTO_INCREMENT key.</summary>
        public uint RunId { get; set; }

        public uint AvatarId { get; set; }

        /// <summary>The monarch at start, a snapshot.</summary>
        public uint? MonarchGuid { get; set; }

        public string SiteId { get; set; }

        public DateTime StartedAt { get; set; }

        /// <summary>When wave 1 began (the commit). NULL while staging.</summary>
        public DateTime? ActiveAt { get; set; }

        /// <summary>NULL while the run is open.</summary>
        public DateTime? EndedAt { get; set; }

        /// <summary>0 TimeLimit, 1 Aborted, 2 Restart, 3 InstanceLost, 4 Admin. NULL while open.</summary>
        public byte? EndReason { get; set; }

        public ushort WavesCleared { get; set; }

        /// <summary>Score, in hundredths.</summary>
        public uint ScoreCenti { get; set; }

        public ushort Participants { get; set; }

        public ushort Eligible { get; set; }

        public ushort PeakHeadcount { get; set; }

        public ulong FavorPrice { get; set; }

        /// <summary>1 once the Favor price was committed (wave 1), else 0.</summary>
        public byte FavorCommitted { get; set; }

        public ushort CratesClaimed { get; set; }

        /// <summary>Always 1 for new runs. Rows written before exempt runs were ranked may hold 0 (an exempt contact made them unranked).</summary>
        public byte Ranked { get; set; }
    }

    /// <summary>One wave of one Gauntlet run.</summary>
    public partial class AllegianceGauntletWave
    {
        public uint RunId { get; set; }

        public ushort Wave { get; set; }

        public uint Level { get; set; }

        public ushort Headcount { get; set; }

        public ushort Planned { get; set; }

        public ushort Killed { get; set; }

        public float Seconds { get; set; }

        /// <summary>0 Cleared, 1 TimedOut.</summary>
        public byte Outcome { get; set; }

        /// <summary>The fraction of the wave cleared, in hundredths (0-100).</summary>
        public byte FractionCenti { get; set; }

        public ushort PlayerDeaths { get; set; }

        public ushort FighterDeaths { get; set; }

        public string Family { get; set; }
    }

    /// <summary>One account's Gauntlet crate claim on one UTC day: the daily account cap.</summary>
    public partial class AllegianceGauntletClaim
    {
        public uint AccountId { get; set; }

        /// <summary>SQL date, the UTC day; only the date part is meaningful.</summary>
        public DateTime ClaimDate { get; set; }

        public uint RunId { get; set; }

        public uint CharacterId { get; set; }

        /// <summary>Per-attempt token; recognizes a retried insert of this attempt's own row.</summary>
        public string ClaimToken { get; set; }

        public DateTime ClaimedAt { get; set; }
    }

    public partial class ShardDbContext
    {
        public virtual DbSet<AllegianceGauntletRecord> AllegianceGauntletRecord { get; set; }

        public virtual DbSet<AllegianceGauntletRun> AllegianceGauntletRun { get; set; }

        public virtual DbSet<AllegianceGauntletWave> AllegianceGauntletWave { get; set; }

        public virtual DbSet<AllegianceGauntletClaim> AllegianceGauntletClaim { get; set; }

        internal static void ConfigureAllegianceGauntlet(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<AllegianceGauntletRecord>(entity =>
            {
                entity.ToTable("allegiance_gauntlet_record");
                entity.HasKey(e => e.AvatarId).HasName("PRIMARY");

                entity.Property(e => e.AvatarId).HasColumnName("avatar_Id").ValueGeneratedNever();
                entity.Property(e => e.BestCenti).HasColumnName("best_Centi");
                entity.Property(e => e.BestParticipants).HasColumnName("best_Participants");
                entity.Property(e => e.BestRunId).HasColumnName("best_Run_Id");
                entity.Property(e => e.BestAt).HasColumnName("best_At").HasColumnType("datetime");
                entity.Property(e => e.LastRunAt).HasColumnName("last_Run_At").HasColumnType("datetime");
                entity.Property(e => e.RunCount).HasColumnName("run_Count");
                entity.Property(e => e.UpdatedAt).HasColumnName("updated_At").HasColumnType("datetime").HasDefaultValueSql("CURRENT_TIMESTAMP");
            });

            modelBuilder.Entity<AllegianceGauntletRun>(entity =>
            {
                entity.ToTable("allegiance_gauntlet_run");
                entity.HasKey(e => e.RunId).HasName("PRIMARY");
                entity.HasIndex(e => new { e.AvatarId, e.StartedAt }, "allegiance_gauntlet_run_avatar_idx");

                // AUTO_INCREMENT in SQL.
                entity.Property(e => e.RunId).HasColumnName("run_Id").ValueGeneratedOnAdd();
                entity.Property(e => e.AvatarId).HasColumnName("avatar_Id");
                entity.Property(e => e.MonarchGuid).HasColumnName("monarch_Guid");
                entity.Property(e => e.SiteId).IsRequired().HasColumnName("site_Id").HasMaxLength(64);
                entity.Property(e => e.StartedAt).HasColumnName("started_At").HasColumnType("datetime");
                entity.Property(e => e.ActiveAt).HasColumnName("active_At").HasColumnType("datetime");
                entity.Property(e => e.EndedAt).HasColumnName("ended_At").HasColumnType("datetime");
                entity.Property(e => e.EndReason).HasColumnName("end_Reason");
                entity.Property(e => e.WavesCleared).HasColumnName("waves_Cleared");
                entity.Property(e => e.ScoreCenti).HasColumnName("score_Centi");
                entity.Property(e => e.Participants).HasColumnName("participants");
                entity.Property(e => e.Eligible).HasColumnName("eligible");
                entity.Property(e => e.PeakHeadcount).HasColumnName("peak_Headcount");
                entity.Property(e => e.FavorPrice).HasColumnName("favor_Price");
                entity.Property(e => e.FavorCommitted).HasColumnName("favor_Committed");
                entity.Property(e => e.CratesClaimed).HasColumnName("crates_Claimed");
                entity.Property(e => e.Ranked).HasColumnName("ranked");
            });

            modelBuilder.Entity<AllegianceGauntletWave>(entity =>
            {
                entity.ToTable("allegiance_gauntlet_wave");
                entity.HasKey(e => new { e.RunId, e.Wave }).HasName("PRIMARY");

                entity.Property(e => e.RunId).HasColumnName("run_Id").ValueGeneratedNever();
                entity.Property(e => e.Wave).HasColumnName("wave").ValueGeneratedNever();
                entity.Property(e => e.Level).HasColumnName("level");
                entity.Property(e => e.Headcount).HasColumnName("headcount");
                entity.Property(e => e.Planned).HasColumnName("planned");
                entity.Property(e => e.Killed).HasColumnName("killed");
                entity.Property(e => e.Seconds).HasColumnName("seconds");
                entity.Property(e => e.Outcome).HasColumnName("outcome");
                entity.Property(e => e.FractionCenti).HasColumnName("fraction_Centi");
                entity.Property(e => e.PlayerDeaths).HasColumnName("player_Deaths");
                entity.Property(e => e.FighterDeaths).HasColumnName("fighter_Deaths");
                entity.Property(e => e.Family).HasColumnName("family").HasMaxLength(64);
            });

            modelBuilder.Entity<AllegianceGauntletClaim>(entity =>
            {
                entity.ToTable("allegiance_gauntlet_claim");
                entity.HasKey(e => new { e.AccountId, e.ClaimDate }).HasName("PRIMARY");

                entity.Property(e => e.AccountId).HasColumnName("account_Id").ValueGeneratedNever();
                entity.Property(e => e.ClaimDate).HasColumnName("claim_Date").HasColumnType("date").ValueGeneratedNever();
                entity.Property(e => e.RunId).HasColumnName("run_Id");
                entity.Property(e => e.CharacterId).HasColumnName("character_Id");
                entity.Property(e => e.ClaimToken).IsRequired().HasColumnName("claim_Token").HasMaxLength(32).IsFixedLength();
                entity.Property(e => e.ClaimedAt).HasColumnName("claimed_At").HasColumnType("datetime").HasDefaultValueSql("CURRENT_TIMESTAMP");
            });
        }
    }
}
