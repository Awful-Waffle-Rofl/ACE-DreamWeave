using System;
using System.Collections.Generic;

using Microsoft.EntityFrameworkCore;

namespace ACE.Database.Models.Shard
{
    // Salvage Roller: the three fork tables created by
    // Database/Updates/Shard/2026-11-02-00-Add-Salvage-Roller.sql (Docs/SalvageRoller/DESIGN.md 5.2, 5.9).
    //
    // Hand-written partials rather than Scaffold-DbContext output, following AccountVaultPartial.cs - the
    // established shape for a FORK-custom shard table.
    //
    // The enums below are stored as tinyint columns. Their numeric values are part of the stored data
    // and are pinned by SalvageRollerSchemaTests: renumbering one silently rewrites the meaning of every
    // row already written.

    /// <summary>character_salvage_batch.state.</summary>
    public enum SalvageBatchState : byte
    {
        /// <summary>The rows exist and the items are moving into (or sit in) escrow; nothing has been rolled. Reconcile aborts it.</summary>
        Escrowing = 0,

        /// <summary>Rolled and committed: the payload holds every candidate. Reconcile finishes consuming Hammers.</summary>
        Pending = 1,

        /// <summary>The player's pick (chosen_Seq) is committed. Reconcile applies it and delivers the item.</summary>
        Taking = 2,
    }

    /// <summary>character_salvage_batch_item.role.</summary>
    public enum SalvageBatchItemRole : byte
    {
        Target = 0,
        Hammer = 1,
    }

    /// <summary>character_salvage_batch.action. 0 is never written.</summary>
    public enum SalvageBatchAction : byte
    {
        None = 0,
        TourmalineReroll = 1,
        AmethystSwap = 2,
        ObsidianConvert = 3,
        ObsidianReroll = 4,
        SerpentineReroll = 5,
        GreenJadeSwap = 6,
    }

    /// <summary>salvage_roller_audit.event. 0 is never written.</summary>
    public enum SalvageRollerAuditEvent : byte
    {
        None = 0,
        Sold = 1,
        Escrowed = 2,
        Aborted = 3,
        Rolled = 4,
        HammersConsumed = 5,
        Taken = 6,
        ReconcileAbort = 7,
        ReconcileForward = 8,
        DeliveryFailed = 9,
        CharacterPurged = 10,
        Error = 11,
    }

    /// <summary>
    /// A character's one open Salvage Roller batch. The UNIQUE key on <see cref="CharacterId"/> is what
    /// makes "one batch per character" a database fact rather than a check that can race.
    /// </summary>
    public partial class CharacterSalvageBatch
    {
        public ulong Id { get; set; }

        public uint CharacterId { get; set; }

        /// <summary>The account whose escrow store holds the items.</summary>
        public uint AccountId { get; set; }

        /// <summary>The target item. Its guid is preserved from sale to take.</summary>
        public uint ItemGuid { get; set; }

        public uint ItemWcid { get; set; }

        /// <summary><see cref="SalvageBatchAction"/>, stored as its byte value.</summary>
        public byte Action { get; set; }

        /// <summary><see cref="SalvageBatchState"/>, stored as its byte value.</summary>
        public byte State { get; set; }

        /// <summary>The picked candidate's sequence number. Set only on the move to Taking.</summary>
        public int? ChosenSeq { get; set; }

        public int RollCount { get; set; }

        /// <summary>UTC. Always written explicitly; the column default is a local-time backstop.</summary>
        public DateTime CreatedAt { get; set; }

        /// <summary>Versioned candidate JSON. NULL until the batch is committed to Pending.</summary>
        public string Payload { get; set; }

        /// <summary>
        /// The objects this batch holds in escrow. A navigation only so that a batch and its item rows are
        /// inserted in ONE SaveChanges (EF fixes up <see cref="CharacterSalvageBatchItem.BatchId"/> from the
        /// generated id). The table itself declares no foreign key.
        /// </summary>
        public virtual ICollection<CharacterSalvageBatchItem> Items { get; set; } = new List<CharacterSalvageBatchItem>();
    }

    /// <summary>
    /// One object a batch holds in escrow. Every guid in this table is exempt from the orphan purge.
    /// </summary>
    public partial class CharacterSalvageBatchItem
    {
        public uint Guid { get; set; }

        public ulong BatchId { get; set; }

        /// <summary><see cref="SalvageBatchItemRole"/>, stored as its byte value.</summary>
        public byte Role { get; set; }
    }

    /// <summary>
    /// One append-only Salvage Roller audit event. <see cref="Detail"/> is self-explanatory JSON: a refund
    /// is worked out from the row alone, with no lookup into any other table.
    /// </summary>
    public partial class SalvageRollerAudit
    {
        public ulong Id { get; set; }

        /// <summary>NULL for an event written before the batch row exists.</summary>
        public ulong? BatchId { get; set; }

        public uint CharacterId { get; set; }

        public uint AccountId { get; set; }

        /// <summary>Snapshot at event time.</summary>
        public string CharacterName { get; set; }

        /// <summary><see cref="SalvageRollerAuditEvent"/>, stored as its byte value.</summary>
        public byte Event { get; set; }

        public uint? ItemGuid { get; set; }

        /// <summary>UTC. Always written explicitly; the column default is a local-time backstop.</summary>
        public DateTime CreatedAt { get; set; }

        public string Detail { get; set; }
    }

    public partial class ShardDbContext
    {
        public virtual DbSet<CharacterSalvageBatch> CharacterSalvageBatch { get; set; }
        public virtual DbSet<CharacterSalvageBatchItem> CharacterSalvageBatchItem { get; set; }
        public virtual DbSet<SalvageRollerAudit> SalvageRollerAudit { get; set; }

        internal static void ConfigureCharacterSalvageBatch(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<CharacterSalvageBatch>(entity =>
            {
                entity.ToTable("character_salvage_batch");
                entity.HasKey(e => e.Id);
                entity.HasIndex(e => e.CharacterId, "character_salvage_batch_character_uidx").IsUnique();
                entity.HasIndex(e => e.AccountId, "character_salvage_batch_account_idx");

                entity.Property(e => e.Id).HasColumnName("id").ValueGeneratedOnAdd();
                entity.Property(e => e.CharacterId).HasColumnName("character_Id");
                entity.Property(e => e.AccountId).HasColumnName("account_Id");
                entity.Property(e => e.ItemGuid).HasColumnName("item_Guid");
                entity.Property(e => e.ItemWcid).HasColumnName("item_Wcid");
                entity.Property(e => e.Action).HasColumnName("action");
                entity.Property(e => e.State).HasColumnName("state");
                entity.Property(e => e.ChosenSeq).HasColumnName("chosen_Seq");
                entity.Property(e => e.RollCount).HasColumnName("roll_Count");
                entity.Property(e => e.CreatedAt)
                    .HasColumnType("datetime")
                    .HasDefaultValueSql("CURRENT_TIMESTAMP")
                    .HasColumnName("created_At");
                entity.Property(e => e.Payload)
                    .HasColumnName("payload")
                    .HasColumnType("mediumtext");

                // Model-only relationship: the table has no FOREIGN KEY, matching the other fork tables.
                // ClientCascade deletes TRACKED item rows with their batch; every delete path in
                // ShardDatabase_SalvageRoller.cs also removes them explicitly, so nothing depends on it.
                entity.HasMany(e => e.Items)
                    .WithOne()
                    .HasForeignKey(i => i.BatchId)
                    .OnDelete(DeleteBehavior.ClientCascade);
            });
        }

        internal static void ConfigureCharacterSalvageBatchItem(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<CharacterSalvageBatchItem>(entity =>
            {
                entity.ToTable("character_salvage_batch_item");
                entity.HasKey(e => e.Guid);
                entity.HasIndex(e => e.BatchId, "character_salvage_batch_item_batch_idx");

                entity.Property(e => e.Guid).HasColumnName("guid").ValueGeneratedNever();
                entity.Property(e => e.BatchId).HasColumnName("batch_Id");
                entity.Property(e => e.Role).HasColumnName("role");
            });
        }

        internal static void ConfigureSalvageRollerAudit(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<SalvageRollerAudit>(entity =>
            {
                entity.ToTable("salvage_roller_audit");
                entity.HasKey(e => e.Id);
                entity.HasIndex(e => e.CharacterId, "salvage_roller_audit_character_idx");
                entity.HasIndex(e => e.BatchId, "salvage_roller_audit_batch_idx");
                entity.HasIndex(e => e.ItemGuid, "salvage_roller_audit_item_idx");
                entity.HasIndex(e => e.CreatedAt, "salvage_roller_audit_time_idx");

                entity.Property(e => e.Id).HasColumnName("id").ValueGeneratedOnAdd();
                entity.Property(e => e.BatchId).HasColumnName("batch_Id");
                entity.Property(e => e.CharacterId).HasColumnName("character_Id");
                entity.Property(e => e.AccountId).HasColumnName("account_Id");
                entity.Property(e => e.CharacterName)
                    .IsRequired()
                    .HasColumnName("character_Name")
                    .HasMaxLength(255);
                entity.Property(e => e.Event).HasColumnName("event");
                entity.Property(e => e.ItemGuid).HasColumnName("item_Guid");
                entity.Property(e => e.CreatedAt)
                    .HasColumnType("datetime")
                    .HasDefaultValueSql("CURRENT_TIMESTAMP")
                    .HasColumnName("created_At");
                entity.Property(e => e.Detail)
                    .IsRequired()
                    .HasColumnName("detail")
                    .HasColumnType("mediumtext");
            });
        }
    }
}
