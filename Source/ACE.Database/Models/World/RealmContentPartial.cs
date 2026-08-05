using System;
using System.Collections.Generic;

using Microsoft.EntityFrameworkCore;

namespace ACE.Database.Models.World
{
    // Realms Phase 3: per-realm content overrides - the landblock_instance_realm /
    // landblock_instance_link_realm tables created by
    // Database/Updates/World/2026-07-10-01-Add-Realm-Content-Tables.sql.
    // Defined in partial classes so the scaffolded files stay untouched.

    /// <summary>
    /// A per-realm weenie instance. When a realm has any rows for a landblock,
    /// they replace the base landblock_instance rows for landblocks loaded in
    /// that realm's instances.
    /// </summary>
    public partial class LandblockInstanceRealm
    {
        public ushort RealmId { get; set; }
        public uint Guid { get; set; }
        public int? Landblock { get; set; }
        public uint WeenieClassId { get; set; }
        public uint ObjCellId { get; set; }
        public float OriginX { get; set; }
        public float OriginY { get; set; }
        public float OriginZ { get; set; }
        public float AnglesW { get; set; }
        public float AnglesX { get; set; }
        public float AnglesY { get; set; }
        public float AnglesZ { get; set; }
        public bool IsLinkChild { get; set; }
        public DateTime LastModified { get; set; }

        public virtual ICollection<LandblockInstanceLinkRealm> LandblockInstanceLinkRealm { get; set; } = new List<LandblockInstanceLinkRealm>();
    }

    public partial class LandblockInstanceLinkRealm
    {
        public ushort RealmId { get; set; }
        public uint ParentGuid { get; set; }
        public uint ChildGuid { get; set; }
        public DateTime LastModified { get; set; }

        public virtual LandblockInstanceRealm Parent { get; set; }
    }

    public partial class WorldDbContext
    {
        public virtual DbSet<LandblockInstanceRealm> LandblockInstanceRealm { get; set; }
        public virtual DbSet<LandblockInstanceLinkRealm> LandblockInstanceLinkRealm { get; set; }

        internal static void ConfigureRealmContent(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<LandblockInstanceRealm>(entity =>
            {
                entity.ToTable("landblock_instance_realm");

                entity.HasKey(e => new { e.RealmId, e.Guid });

                entity.HasIndex(e => new { e.RealmId, e.Landblock }, "realm_instance_landblock_idx");

                entity.Property(e => e.RealmId).HasColumnName("realm_id");
                entity.Property(e => e.Guid).HasColumnName("guid");
                entity.Property(e => e.Landblock)
                    .HasComputedColumnSql("`obj_Cell_Id` >> 16", false)
                    .HasColumnName("landblock");
                entity.Property(e => e.WeenieClassId).HasColumnName("weenie_Class_Id");
                entity.Property(e => e.ObjCellId).HasColumnName("obj_Cell_Id");
                entity.Property(e => e.OriginX).HasColumnName("origin_X");
                entity.Property(e => e.OriginY).HasColumnName("origin_Y");
                entity.Property(e => e.OriginZ).HasColumnName("origin_Z");
                entity.Property(e => e.AnglesW).HasColumnName("angles_W");
                entity.Property(e => e.AnglesX).HasColumnName("angles_X");
                entity.Property(e => e.AnglesY).HasColumnName("angles_Y");
                entity.Property(e => e.AnglesZ).HasColumnName("angles_Z");
                entity.Property(e => e.IsLinkChild).HasColumnName("is_Link_Child");
                entity.Property(e => e.LastModified)
                    .ValueGeneratedOnAddOrUpdate()
                    .HasDefaultValueSql("CURRENT_TIMESTAMP")
                    .HasColumnType("datetime")
                    .HasColumnName("last_Modified");
            });

            modelBuilder.Entity<LandblockInstanceLinkRealm>(entity =>
            {
                entity.ToTable("landblock_instance_link_realm");

                entity.HasKey(e => new { e.RealmId, e.ParentGuid, e.ChildGuid });

                entity.HasIndex(e => new { e.RealmId, e.ChildGuid }, "realm_child_idx");

                entity.Property(e => e.RealmId).HasColumnName("realm_id");
                entity.Property(e => e.ParentGuid).HasColumnName("parent_GUID");
                entity.Property(e => e.ChildGuid).HasColumnName("child_GUID");
                entity.Property(e => e.LastModified)
                    .ValueGeneratedOnAddOrUpdate()
                    .HasDefaultValueSql("CURRENT_TIMESTAMP")
                    .HasColumnType("datetime")
                    .HasColumnName("last_Modified");

                entity.HasOne(d => d.Parent).WithMany(p => p.LandblockInstanceLinkRealm)
                    .HasForeignKey(d => new { d.RealmId, d.ParentGuid })
                    .HasConstraintName("realm_instance_link");
            });
        }
    }
}
