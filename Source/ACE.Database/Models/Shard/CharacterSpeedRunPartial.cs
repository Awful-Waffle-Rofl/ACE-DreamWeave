using System;

using Microsoft.EntityFrameworkCore;

namespace ACE.Database.Models.Shard
{
    // Proving Grounds: Speed - the `character_speed_run` table created by
    // Database/Updates/Shard/2026-08-26-00-Add-Character-Speed-Run.sql.
    // Defined in partial classes (the BiotaPropertiesPosition instance-column pattern, mirroring the
    // world-side SkyDecorPartial / RealmPartial style since this is the first fork-custom table on
    // the shard side) so the scaffolded files stay untouched; a future Scaffold-DbContext run against
    // a database containing the table will generate the equivalent entity.

    /// <summary>
    /// One completed Proving Grounds speed run. This table is the RECORD OF TRUTH for the speed
    /// board - unlike the other three Proving Grounds arenas, whose scores live on the character
    /// biota. Rows are append-only: never updated and never deleted by gameplay. No FK to
    /// `character` - deliberate, so a row survives the character being deleted; CharacterName is a
    /// snapshot taken at completion for exactly that reason. See the migration's header for the full
    /// column-by-column notes.
    /// </summary>
    public partial class CharacterSpeedRun
    {
        public uint Id { get; set; }

        public uint CharacterId { get; set; }

        /// <summary>Snapshot at completion time - survives a later character rename or delete.</summary>
        public string CharacterName { get; set; }

        public int SeasonId { get; set; }

        /// <summary>Elapsed run time in hundredths of a second. LOWER IS BETTER.</summary>
        public long Centiseconds { get; set; }

        public int CharacterLevel { get; set; }

        /// <summary>
        /// UTC. Always written explicitly as DateTime.UtcNow; the column's CURRENT_TIMESTAMP default is
        /// only a backstop and stamps the database server's LOCAL time, so a row that falls back to it
        /// is off by the local UTC offset.
        /// </summary>
        public DateTime CompletedAt { get; set; }
    }

    public partial class ShardDbContext
    {
        public virtual DbSet<CharacterSpeedRun> CharacterSpeedRun { get; set; }

        internal static void ConfigureCharacterSpeedRun(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<CharacterSpeedRun>(entity =>
            {
                entity.ToTable("character_speed_run");

                entity.HasKey(e => e.Id);

                entity.HasIndex(e => new { e.SeasonId, e.Centiseconds }, "character_speed_run_season_idx");

                entity.HasIndex(e => e.CharacterId, "character_speed_run_character_idx");

                // AUTO_INCREMENT in SQL - the opposite of SpeedSeason.Id / Realm.Id, which are
                // content-authored and use ValueGeneratedNever().
                entity.Property(e => e.Id)
                    .HasColumnName("id")
                    .ValueGeneratedOnAdd();

                entity.Property(e => e.CharacterId).HasColumnName("character_Id");

                entity.Property(e => e.CharacterName)
                    .IsRequired()
                    .HasColumnName("character_Name")
                    .HasMaxLength(255);

                entity.Property(e => e.SeasonId).HasColumnName("season_Id");

                entity.Property(e => e.Centiseconds).HasColumnName("centiseconds");

                entity.Property(e => e.CharacterLevel).HasColumnName("character_Level");

                // Unlike SkyDecorRegion.Enabled (a bool whose CLR default, false, is also a valid
                // value a caller might genuinely intend - see that file's comment on why it deliberately
                // omits HasDefaultValueSql), CompletedAt is a DateTime whose CLR default is
                // DateTime.MinValue (0001-01-01), a value the server never legitimately writes. EF only
                // lets a column's SQL default win over an explicit value when the property's current
                // value equals the CLR default for its type; the server always writes CompletedAt as a
                // real "now" timestamp, which can never equal DateTime.MinValue, so the explicit value
                // always round-trips and HasDefaultValueSql here only ever helps the rare case where a
                // caller leaves it unset (letting MySQL stamp CURRENT_TIMESTAMP instead of writing 0001).
                entity.Property(e => e.CompletedAt)
                    .HasColumnType("datetime")
                    .HasDefaultValueSql("CURRENT_TIMESTAMP")
                    .HasColumnName("completed_At");
            });
        }
    }
}
