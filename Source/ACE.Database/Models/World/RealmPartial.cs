using Microsoft.EntityFrameworkCore;

namespace ACE.Database.Models.World
{
    // ACRealms port Phase 2: the `realm` table created by
    // Database/Updates/World/2026-07-10-00-Add-Realm-Table.sql.
    // Defined here (with the DbSet and mapping in partial classes) so the
    // scaffolded files stay untouched; a future Scaffold-DbContext run against a
    // database containing the table will generate the equivalent entity.
    public partial class Realm
    {
        public ushort Id { get; set; }
        public ushort Type { get; set; }
        public string Name { get; set; }
        public ushort? ParentRealmId { get; set; }
        public ushort? PropertyCountRandomized { get; set; }
    }

    public partial class WorldDbContext
    {
        public virtual DbSet<Realm> Realm { get; set; }

        partial void OnModelCreatingPartial(ModelBuilder modelBuilder)
        {
            ConfigureRealmContent(modelBuilder);

            modelBuilder.Entity<Realm>(entity =>
            {
                entity.ToTable("realm");

                entity.HasKey(e => e.Id);

                entity.Property(e => e.Id)
                    .HasColumnName("id")
                    .ValueGeneratedNever();

                entity.Property(e => e.Type).HasColumnName("type");

                entity.Property(e => e.Name)
                    .IsRequired()
                    .HasColumnName("name")
                    .HasColumnType("text");

                entity.Property(e => e.ParentRealmId).HasColumnName("parent_realm_id");

                entity.Property(e => e.PropertyCountRandomized).HasColumnName("property_count_randomized");
            });
        }
    }
}
