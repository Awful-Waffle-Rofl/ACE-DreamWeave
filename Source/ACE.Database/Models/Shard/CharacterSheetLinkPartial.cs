using System;

using Microsoft.EntityFrameworkCore;

namespace ACE.Database.Models.Shard
{
    // Character Sheet: hand-written partial, following MarketPartial.cs.
    // Table created by Database/Updates/Shard/2026-09-15-00-Add-Character-Sheet-Link.sql.

    /// <summary>One opt-in public link. Row present = sheet public; no row = hidden; new slug = rotated.</summary>
    public partial class CharacterSheetLink
    {
        public uint CharacterId { get; set; }

        /// <summary>10 chars base62, random, never derived from the guid. UNIQUE.</summary>
        public string Slug { get; set; }

        /// <summary>UTC; set by the DAO on every insert and every rotation.</summary>
        public DateTime CreatedAt { get; set; }
    }

    public partial class ShardDbContext
    {
        public virtual DbSet<CharacterSheetLink> CharacterSheetLink { get; set; }

        internal static void ConfigureCharacterSheetLink(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<CharacterSheetLink>(entity =>
            {
                entity.ToTable("character_sheet_link");
                entity.HasKey(e => e.CharacterId);
                entity.HasIndex(e => e.Slug, "character_sheet_link_slug_uidx").IsUnique();
                entity.Property(e => e.CharacterId).HasColumnName("character_Id").ValueGeneratedNever();
                entity.Property(e => e.Slug).IsRequired().HasColumnName("slug").HasMaxLength(16);
                entity.Property(e => e.CreatedAt).HasColumnName("created_At").HasColumnType("datetime");
            });
        }
    }
}
