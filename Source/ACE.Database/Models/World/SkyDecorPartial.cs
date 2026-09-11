using System;

using Microsoft.EntityFrameworkCore;

namespace ACE.Database.Models.World
{
    // WaffleACE fork: sky decor regions - the `sky_decor_region` table created by
    // Database/Updates/World/2026-08-20-01-Add-Sky-Decor-Region.sql.
    // Defined in partial classes (the RealmPartial / RealmContentPartial pattern) so the scaffolded
    // files stay untouched; a future Scaffold-DbContext run against a database containing the table
    // will generate the equivalent entity.

    /// <summary>
    /// One rectangle of landblocks whose outdoor sky is dressed with server-generated spinning decor.
    /// Read-only content: the spawner never writes here, and nothing it spawns is persisted anywhere.
    /// See the migration's header for what each column means.
    /// </summary>
    public partial class SkyDecorRegion
    {
        public uint Id { get; set; }
        public string Name { get; set; }
        public bool Enabled { get; set; }

        /// <summary>0 = the retail world.</summary>
        public ushort RealmId { get; set; }

        public byte LbXMin { get; set; }
        public byte LbXMax { get; set; }
        public byte LbYMin { get; set; }
        public byte LbYMax { get; set; }

        public int Seed { get; set; }
        public int Version { get; set; }

        /// <summary>Clouds per landblock; the fractional part is the chance of one extra.</summary>
        public float Density { get; set; }

        /// <summary>CSV of `wcid` or `wcid:weight`.</summary>
        public string Palette { get; set; }

        public float ScaleMin { get; set; }
        public float ScaleMax { get; set; }

        /// <summary>Disc CENTRE height above the terrain, metres.</summary>
        public float HeightMin { get; set; }
        public float HeightMax { get; set; }

        public float TiltMaxDeg { get; set; }

        public float SpinMin { get; set; }
        public float SpinMax { get; set; }

        /// <summary>Metres per scale unit from the object origin to the disc along local +Z.</summary>
        public float PartOffset { get; set; }

        public bool OverWater { get; set; }

        /// <summary>Chance in [0, 1] that a planned cloud also gets a partner disc. 0 = single discs.</summary>
        public float PairChance { get; set; }

        /// <summary>Partner ObjScale = primary ObjScale x this.</summary>
        public float PairScaleRatio { get; set; }

        /// <summary>Metres between the two disc planes, partner on the ground side.</summary>
        public float PairGap { get; set; }

        /// <summary>Partner MotionSpeed = primary MotionSpeed x this.</summary>
        public float PairSpeedRatio { get; set; }

        /// <summary>
        /// Required XY centre distance between two clouds as a multiple of the sum of their primary disc
        /// radii. 1.0 = may touch, never overlap. 0 = culling off.
        /// </summary>
        public float MinSeparation { get; set; }

        /// <summary>Per-cloud opacity range. 0 = opaque (current behaviour, and the default), 1 = fully
        /// invisible - the same PropertyFloat.Translucency direction every other translucent object in
        /// the world already uses.</summary>
        public float TranslucencyMin { get; set; }
        public float TranslucencyMax { get; set; }

        /// <summary>'inverted' (disc below the origin) or 'mast' (disc above a near-ground origin).</summary>
        public string Rig { get; set; }

        public DateTime LastModified { get; set; }

        /// <summary>
        /// True when this region covers the given landblock id in the given realm. The landblock id is
        /// (x &lt;&lt; 8) | y and the rectangle is inclusive on both ends.
        /// </summary>
        public bool Covers(ushort landblockId, ushort realmId)
        {
            if (realmId != RealmId)
                return false;

            var x = (byte)(landblockId >> 8);
            var y = (byte)(landblockId & 0xFF);

            return x >= LbXMin && x <= LbXMax && y >= LbYMin && y <= LbYMax;
        }

        /// <summary>
        /// A field-for-field copy. Used by the runtime-override layer (ACE.Server's SkyDecorOverrides),
        /// which never mutates a cached row in place: it publishes a modified COPY, so a landblock thread
        /// mid-rebuild always reads one internally consistent set of values rather than a half-applied
        /// edit. MemberwiseClone rather than a hand-written field list precisely so a column added above
        /// is copied without anyone having to remember to add it here too.
        /// </summary>
        public SkyDecorRegion Clone()
        {
            return (SkyDecorRegion)MemberwiseClone();
        }
    }

    public partial class WorldDbContext
    {
        public virtual DbSet<SkyDecorRegion> SkyDecorRegion { get; set; }

        internal static void ConfigureSkyDecor(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<SkyDecorRegion>(entity =>
            {
                entity.ToTable("sky_decor_region");

                entity.HasKey(e => e.Id);

                entity.HasIndex(e => e.Name, "sky_decor_region_name_uidx").IsUnique();

                entity.HasIndex(e => new { e.RealmId, e.Enabled }, "sky_decor_region_realm_idx");

                entity.Property(e => e.Id).HasColumnName("id");

                entity.Property(e => e.Name)
                    .IsRequired()
                    .HasColumnName("name")
                    .HasMaxLength(64);

                // bool maps to bit(1) with no HasColumnType - the same shape landblock_instance's
                // is_Link_Child uses (WorldDbContext.cs:298). Deliberately NO HasDefaultValueSql: EF then
                // treats a written `false` as "unset" and lets the column default win on insert, which
                // would silently make `enabled = false` unwritable.
                entity.Property(e => e.Enabled).HasColumnName("enabled");

                entity.Property(e => e.RealmId).HasColumnName("realm_id");

                entity.Property(e => e.LbXMin).HasColumnName("lb_x_min");
                entity.Property(e => e.LbXMax).HasColumnName("lb_x_max");
                entity.Property(e => e.LbYMin).HasColumnName("lb_y_min");
                entity.Property(e => e.LbYMax).HasColumnName("lb_y_max");

                entity.Property(e => e.Seed).HasColumnName("seed");
                entity.Property(e => e.Version).HasColumnName("version");

                entity.Property(e => e.Density).HasColumnName("density");

                entity.Property(e => e.Palette)
                    .IsRequired()
                    .HasColumnName("palette")
                    .HasMaxLength(255);

                entity.Property(e => e.ScaleMin).HasColumnName("scale_min");
                entity.Property(e => e.ScaleMax).HasColumnName("scale_max");

                entity.Property(e => e.HeightMin).HasColumnName("height_min");
                entity.Property(e => e.HeightMax).HasColumnName("height_max");

                entity.Property(e => e.TiltMaxDeg).HasColumnName("tilt_max_deg");

                entity.Property(e => e.SpinMin).HasColumnName("spin_min");
                entity.Property(e => e.SpinMax).HasColumnName("spin_max");

                entity.Property(e => e.PartOffset).HasColumnName("part_offset");

                entity.Property(e => e.OverWater).HasColumnName("over_water");

                entity.Property(e => e.PairChance).HasColumnName("pair_chance");
                entity.Property(e => e.PairScaleRatio).HasColumnName("pair_scale_ratio");
                entity.Property(e => e.PairGap).HasColumnName("pair_gap");
                entity.Property(e => e.PairSpeedRatio).HasColumnName("pair_speed_ratio");

                entity.Property(e => e.MinSeparation).HasColumnName("min_separation");

                entity.Property(e => e.TranslucencyMin).HasColumnName("translucency_min");
                entity.Property(e => e.TranslucencyMax).HasColumnName("translucency_max");

                entity.Property(e => e.Rig)
                    .IsRequired()
                    .HasColumnName("rig")
                    .HasMaxLength(16);

                entity.Property(e => e.LastModified)
                    .ValueGeneratedOnAddOrUpdate()
                    .HasDefaultValueSql("CURRENT_TIMESTAMP")
                    .HasColumnType("datetime")
                    .HasColumnName("last_modified");
            });
        }
    }
}
