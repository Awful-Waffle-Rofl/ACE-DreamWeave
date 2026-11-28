using System;

using Microsoft.EntityFrameworkCore;

namespace ACE.Database.Models.Shard
{
    // Allegiance Avatar: hand-written partial, following RewardClaimPartial.cs.
    // Tables created by Database/Updates/Shard/2026-11-20-01-Add-Allegiance-Avatar.sql; look_Json added by
    // 2026-11-21-01-Add-Allegiance-Avatar-Look.sql; level_Xp by 2026-11-23-01-Add-Allegiance-Avatar-Level-Xp.sql;
    // blessing_Bought_At, last_Event_At and events_On_Day by 2026-11-25-02-Add-Allegiance-Avatar-Daily-Purchases.sql.

    /// <summary>One Allegiance Avatar. One row per Avatar ever founded; rows are never deleted.</summary>
    public partial class AllegianceAvatar
    {
        /// <summary>AUTO_INCREMENT key.</summary>
        public uint AvatarId { get; set; }

        /// <summary>Bound monarch character guid. UNIQUE; NULL once the Avatar is absorbed or retired.</summary>
        public uint? MonarchGuid { get; set; }

        /// <summary>0 Bound, 1 Absorbed, 2 Retired.</summary>
        public byte State { get; set; }

        public ulong LifetimeXp { get; set; }

        /// <summary>The Avatar's level pool (2026-11-23-01-Add-Allegiance-Avatar-Level-Xp.sql): a share of every XP grant members receive.</summary>
        public ulong LevelXp { get; set; }

        public ulong Favor { get; set; }

        public uint? HeirGuid { get; set; }

        // Seat: the Keeper's own position at /avatar seat time. All NULL while unseated.
        public uint? SeatHouseGuid { get; set; }
        public uint? SeatCell { get; set; }
        public uint? SeatInstance { get; set; }
        public float? SeatX { get; set; }
        public float? SeatY { get; set; }
        public float? SeatZ { get; set; }
        public float? SeatQw { get; set; }
        public float? SeatQx { get; set; }
        public float? SeatQy { get; set; }
        public float? SeatQz { get; set; }
        public DateTime? SeatedAt { get; set; }

        /// <summary>The avatar_Id this Avatar was absorbed into, when State is Absorbed.</summary>
        public uint? AbsorbedIntoId { get; set; }

        public DateTime? LastContributionAt { get; set; }

        /// <summary>SQL date; only the date part is meaningful.</summary>
        public DateTime? LastDecayDay { get; set; }

        /// <summary>
        /// The Avatar's look as one versioned JSON document (2026-11-21-01-Add-Allegiance-Avatar-Look.sql).
        /// NULL means the default look. Stored and read back verbatim; nothing in this layer parses it.
        /// </summary>
        public string LookJson { get; set; }

        /// <summary>UTC instant the Allegiance Blessing was last bought (2026-11-25-02). NULL = never.</summary>
        public DateTime? BlessingBoughtAt { get; set; }

        /// <summary>UTC instant a private World Event last reached Active (2026-11-25-02). NULL = never.</summary>
        public DateTime? LastEventAt { get; set; }

        /// <summary>Private World Events that reached Active on the Avatar day of <see cref="LastEventAt"/> (2026-11-25-02).</summary>
        public byte EventsOnDay { get; set; }

        public DateTime CreatedAt { get; set; }

        public DateTime UpdatedAt { get; set; }
    }

    /// <summary>One account's XP contribution to one Avatar on one day. Keyed by account, never by character.</summary>
    public partial class AllegianceAvatarContribution
    {
        public uint AvatarId { get; set; }

        public uint AccountId { get; set; }

        /// <summary>SQL date; only the date part is meaningful.</summary>
        public DateTime Day { get; set; }

        /// <summary>XP the account earned toward the Avatar that day, before the daily curve.</summary>
        public ulong RawXp { get; set; }

        /// <summary>XP that actually reached the Avatar that day, after the daily curve.</summary>
        public ulong Credited { get; set; }

        /// <summary>Informational only: the character that last contributed for this account that day.</summary>
        public uint LastCharacterId { get; set; }
    }

    public partial class ShardDbContext
    {
        public virtual DbSet<AllegianceAvatar> AllegianceAvatar { get; set; }

        public virtual DbSet<AllegianceAvatarContribution> AllegianceAvatarContribution { get; set; }

        internal static void ConfigureAllegianceAvatar(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<AllegianceAvatar>(entity =>
            {
                entity.ToTable("allegiance_avatar");
                entity.HasKey(e => e.AvatarId).HasName("PRIMARY");
                entity.HasIndex(e => e.MonarchGuid, "allegiance_avatar_monarch_uidx").IsUnique();

                // AUTO_INCREMENT in SQL.
                entity.Property(e => e.AvatarId).HasColumnName("avatar_Id").ValueGeneratedOnAdd();
                entity.Property(e => e.MonarchGuid).HasColumnName("monarch_Guid");
                entity.Property(e => e.State).HasColumnName("state");
                entity.Property(e => e.LifetimeXp).HasColumnName("lifetime_Xp");
                entity.Property(e => e.LevelXp).HasColumnName("level_Xp");
                entity.Property(e => e.Favor).HasColumnName("favor");
                entity.Property(e => e.HeirGuid).HasColumnName("heir_Guid");
                entity.Property(e => e.SeatHouseGuid).HasColumnName("seat_House_Guid");
                entity.Property(e => e.SeatCell).HasColumnName("seat_Cell");
                entity.Property(e => e.SeatInstance).HasColumnName("seat_Instance");
                entity.Property(e => e.SeatX).HasColumnName("seat_X");
                entity.Property(e => e.SeatY).HasColumnName("seat_Y");
                entity.Property(e => e.SeatZ).HasColumnName("seat_Z");
                entity.Property(e => e.SeatQw).HasColumnName("seat_Qw");
                entity.Property(e => e.SeatQx).HasColumnName("seat_Qx");
                entity.Property(e => e.SeatQy).HasColumnName("seat_Qy");
                entity.Property(e => e.SeatQz).HasColumnName("seat_Qz");
                entity.Property(e => e.SeatedAt).HasColumnName("seated_At").HasColumnType("datetime");
                entity.Property(e => e.AbsorbedIntoId).HasColumnName("absorbed_Into_Id");
                entity.Property(e => e.LastContributionAt).HasColumnName("last_Contribution_At").HasColumnType("datetime");
                entity.Property(e => e.LastDecayDay).HasColumnName("last_Decay_Day").HasColumnType("date");
                entity.Property(e => e.LookJson).HasColumnName("look_Json").HasColumnType("mediumtext");
                entity.Property(e => e.BlessingBoughtAt).HasColumnName("blessing_Bought_At").HasColumnType("datetime");
                entity.Property(e => e.LastEventAt).HasColumnName("last_Event_At").HasColumnType("datetime");
                entity.Property(e => e.EventsOnDay).HasColumnName("events_On_Day");
                entity.Property(e => e.CreatedAt).HasColumnName("created_At").HasColumnType("datetime").HasDefaultValueSql("CURRENT_TIMESTAMP");
                entity.Property(e => e.UpdatedAt).HasColumnName("updated_At").HasColumnType("datetime").HasDefaultValueSql("CURRENT_TIMESTAMP");
            });

            modelBuilder.Entity<AllegianceAvatarContribution>(entity =>
            {
                entity.ToTable("allegiance_avatar_contribution");
                entity.HasKey(e => new { e.AvatarId, e.AccountId, e.Day }).HasName("PRIMARY");
                entity.HasIndex(e => new { e.AvatarId, e.Day }, "allegiance_avatar_contribution_day_idx");

                entity.Property(e => e.AvatarId).HasColumnName("avatar_Id").ValueGeneratedNever();
                entity.Property(e => e.AccountId).HasColumnName("account_Id").ValueGeneratedNever();
                entity.Property(e => e.Day).HasColumnName("day").HasColumnType("date").ValueGeneratedNever();
                entity.Property(e => e.RawXp).HasColumnName("raw_Xp");
                entity.Property(e => e.Credited).HasColumnName("credited");
                entity.Property(e => e.LastCharacterId).HasColumnName("last_Character_Id");
            });
        }
    }
}
