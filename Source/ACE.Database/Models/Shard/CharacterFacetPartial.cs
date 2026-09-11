using System;

using Microsoft.EntityFrameworkCore;

namespace ACE.Database.Models.Shard
{
    // Player Facets: the `character_facet` table. It takes THREE migrations to reach its current
    // shape, and no single file creates it under this name - 2026-09-05-00-Add-Character-Loadout.sql
    // creates it as `character_loadout`, 2026-09-06-01-Rename-Character-Loadout-To-Character-Facet.sql
    // renames it, and 2026-09-06-02-Add-Character-Facet-Attrs.sql adds `attrs_Json`. The first two are
    // merged and possibly applied, so neither may be edited; that is why the rename is a follow-on
    // file rather than a correction to the original.
    // Defined in partial classes (the CharacterSpeedRun pattern) so the scaffolded files stay
    // untouched; a future Scaffold-DbContext run against a database containing the table will
    // generate the equivalent entity.

    /// <summary>
    /// One stored build for one character in one slot. Slot 1 is the character's base build. A slot
    /// that has never been visited has no row - the fresh build is generated on first switch, so the
    /// ABSENCE of a row is meaningful and must never be backfilled. No FK to `character`, matching
    /// CharacterSpeedRun. See the migration's header and Docs/Facets/DESIGN.md for the full notes.
    /// </summary>
    public partial class CharacterFacet
    {
        public uint CharacterId { get; set; }

        public byte Slot { get; set; }

        /// <summary>Player-set display label, or null for unnamed.</summary>
        public string Name { get; set; }

        /// <summary>Per-skill SAC / ranks / PP / InitLevel, serialized by FacetSnapshot.</summary>
        public string SkillsJson { get; set; }

        /// <summary>Class ability id to rank, serialized by FacetSnapshot.</summary>
        public string AbilitiesJson { get; set; }

        /// <summary>
        /// The remembered worn set. Each entry carries both guid and wcid, because the account vault
        /// destroys the biota of any pristine item it collapses to a ledger row and rebuilds it with a
        /// new guid on withdraw - a guid-only set rots silently.
        /// </summary>
        public string EquipJson { get; set; }

        /// <summary>
        /// The six primary attributes' InitLevel (CreatureAttribute.StartingValue - the redistributable
        /// half, the one AttributeTransferDevice moves), keyed by attribute NAME and serialized by
        /// FacetSnapshot.SerializeAttributes. Added by
        /// Database/Updates/Shard/2026-09-06-02-Add-Character-Facet-Attrs.sql.
        ///
        /// NULLABLE, unlike the other three JSON columns, and that is load-bearing rather than an
        /// oversight: NULL means "this row predates per-facet attributes", a state the apply path has to
        /// tell apart from a real stored arrangement. A NULL row keeps the character's LIVE arrangement
        /// instead of applying anything, so a slot stored before this column existed upgrades in place
        /// the first time it is switched away from. There is no honest non-null default - an empty
        /// string or "{}" would read as a real arrangement summing to zero, which the conservation
        /// check in FacetAttributes.Reconcile would then have to refuse.
        ///
        /// The XP-bought half of an attribute (CPSpent / LevelFromCP) is NOT stored here and stays
        /// global, along with the vital records, augmentations, enlightenment, spellbook and level.
        /// </summary>
        public string AttrsJson { get; set; }

        /// <summary>
        /// UTC. Always written explicitly as DateTime.UtcNow; the column's CURRENT_TIMESTAMP default is
        /// only a backstop and stamps the database server's LOCAL time.
        /// </summary>
        public DateTime UpdatedAt { get; set; }
    }

    public partial class ShardDbContext
    {
        public virtual DbSet<CharacterFacet> CharacterFacet { get; set; }

        internal static void ConfigureCharacterFacet(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<CharacterFacet>(entity =>
            {
                entity.ToTable("character_facet");

                entity.HasKey(e => new { e.CharacterId, e.Slot });

                entity.Property(e => e.CharacterId)
                    .HasColumnName("character_Id")
                    .ValueGeneratedNever();

                entity.Property(e => e.Slot)
                    .HasColumnName("slot")
                    .ValueGeneratedNever();

                entity.Property(e => e.Name)
                    .HasColumnName("name")
                    .HasMaxLength(32);

                entity.Property(e => e.SkillsJson)
                    .IsRequired()
                    .HasColumnName("skills_Json");

                entity.Property(e => e.AbilitiesJson)
                    .IsRequired()
                    .HasColumnName("abilities_Json");

                entity.Property(e => e.EquipJson)
                    .IsRequired()
                    .HasColumnName("equip_Json");

                // NO .IsRequired() here, deliberately - see AttrsJson's remarks. NULL is the marker for
                // a row written before per-facet attributes existed, and marking the property required
                // would make EF reject reading one back.
                entity.Property(e => e.AttrsJson)
                    .HasColumnName("attrs_Json");

                entity.Property(e => e.UpdatedAt)
                    .HasColumnType("datetime")
                    .HasDefaultValueSql("CURRENT_TIMESTAMP")
                    .HasColumnName("updated_At");
            });
        }
    }
}
