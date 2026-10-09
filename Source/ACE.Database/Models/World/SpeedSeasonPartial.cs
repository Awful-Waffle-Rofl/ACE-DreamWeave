using System;

using Microsoft.EntityFrameworkCore;

namespace ACE.Database.Models.World
{
    // Proving Grounds: Speed - the `speed_season` table created by
    // Database/Updates/World/2026-08-26-00-Add-Speed-Season.sql.
    // Defined in partial classes (the RealmPartial / SkyDecorPartial pattern) so the scaffolded
    // files stay untouched; a future Scaffold-DbContext run against a database containing the table
    // will generate the equivalent entity.

    /// <summary>
    /// One content-authored season of the Proving Grounds speed challenge. The active season is the
    /// row where StartsAt &lt;= now &lt; EndsAt; a gap means no active season, and overlapping rows
    /// are a content error resolved by latest StartsAt, with a warning logged at load. See the
    /// migration's header for the full column-by-column notes.
    /// </summary>
    public partial class SpeedSeason
    {
        /// <summary>Content-authored, cached on the player biota via PropertyInt.SpeedChallengeSeasonId.</summary>
        public int Id { get; set; }

        public string Name { get; set; }
        public string DungeonName { get; set; }

        public uint ObjCellId { get; set; }
        public float OriginX { get; set; }
        public float OriginY { get; set; }
        public float OriginZ { get; set; }
        public float AnglesW { get; set; }
        public float AnglesX { get; set; }
        public float AnglesY { get; set; }
        public float AnglesZ { get; set; }

        /// <summary>0 is the base retail world.</summary>
        public ushort RealmId { get; set; }

        /// <summary>The only goal object or boss wcid that may finish a run this season.</summary>
        public uint ObjectiveWcid { get; set; }

        /// <summary>Wcid of the object whose activation starts the clock; 0 means the clock starts on arrival.</summary>
        public uint StartWcid { get; set; }

        /// <summary>Recommended level floor, advisory only in v1 - v1 does not scale enemies.</summary>
        public int LevelFloor { get; set; }

        /// <summary>
        /// UTC. MySQL datetime carries no timezone; SpeedSeasonManager compares this against
        /// DateTime.UtcNow, the same clock ACE.Common's Time.GetUnixTime() uses. Season rows are
        /// authored in UTC.
        /// </summary>
        public DateTime StartsAt { get; set; }

        /// <summary>UTC, exclusive upper bound of the season window. See <see cref="StartsAt"/>.</summary>
        public DateTime EndsAt { get; set; }
    }

    public partial class WorldDbContext
    {
        public virtual DbSet<SpeedSeason> SpeedSeason { get; set; }

        internal static void ConfigureSpeedSeason(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<SpeedSeason>(entity =>
            {
                entity.ToTable("speed_season");

                entity.HasKey(e => e.Id);

                entity.HasIndex(e => new { e.StartsAt, e.EndsAt }, "speed_season_window_idx");

                entity.HasIndex(e => e.Name, "speed_season_name_uidx").IsUnique();

                // Content-authored, like Realm.Id - never auto-generated.
                entity.Property(e => e.Id)
                    .HasColumnName("id")
                    .ValueGeneratedNever();

                entity.Property(e => e.Name)
                    .IsRequired()
                    .HasColumnName("name")
                    .HasMaxLength(64);

                entity.Property(e => e.DungeonName)
                    .IsRequired()
                    .HasColumnName("dungeon_name")
                    .HasMaxLength(64);

                entity.Property(e => e.ObjCellId).HasColumnName("obj_cell_id");

                entity.Property(e => e.OriginX).HasColumnName("origin_x");
                entity.Property(e => e.OriginY).HasColumnName("origin_y");
                entity.Property(e => e.OriginZ).HasColumnName("origin_z");

                entity.Property(e => e.AnglesW).HasColumnName("angles_w");
                entity.Property(e => e.AnglesX).HasColumnName("angles_x");
                entity.Property(e => e.AnglesY).HasColumnName("angles_y");
                entity.Property(e => e.AnglesZ).HasColumnName("angles_z");

                entity.Property(e => e.RealmId).HasColumnName("realm_id");

                entity.Property(e => e.ObjectiveWcid).HasColumnName("objective_wcid");

                entity.Property(e => e.StartWcid).HasColumnName("start_wcid");

                entity.Property(e => e.LevelFloor).HasColumnName("level_floor");

                entity.Property(e => e.StartsAt)
                    .HasColumnType("datetime")
                    .HasColumnName("starts_at");

                entity.Property(e => e.EndsAt)
                    .HasColumnType("datetime")
                    .HasColumnName("ends_at");
            });
        }
    }
}
