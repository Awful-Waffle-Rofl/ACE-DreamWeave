using System;

using Microsoft.EntityFrameworkCore;

namespace ACE.Database.Models.Shard
{
    // Thread puzzle-gate IP ledger: hand-written partial, following RewardClaimPartial.cs.
    // Table created by Database/Updates/Shard/2026-10-06-00-Add-Thread-Puzzle-Ip-Ledger.sql.

    /// <summary>What one thread_puzzle_ip_ledger row records. The values are the stored tinyint.</summary>
    public enum ThreadPuzzleIpLedgerKind : byte
    {
        Fail = 1,
        Lockout = 2
    }

    /// <summary>One fail, or one applied lockout, against one policy key.</summary>
    public partial class ThreadPuzzleIpLedgerRow
    {
        public ulong Id { get; set; }

        /// <summary>The policy key (IPv4, IPv6 /64 prefix, or acct:&lt;id&gt;). Compared ordinally.</summary>
        public string IpKey { get; set; }

        public ThreadPuzzleIpLedgerKind Kind { get; set; }

        /// <summary>UTC. When the fail happened, or when the lockout was applied.</summary>
        public DateTime AtUtc { get; set; }

        /// <summary>UTC lockout end. NULL on fail rows.</summary>
        public DateTime? UntilUtc { get; set; }

        /// <summary>Audit only.</summary>
        public uint? AccountId { get; set; }

        /// <summary>Audit only.</summary>
        public uint? CharacterId { get; set; }

        /// <summary>Audit only: the run instance id, which recycles across restarts.</summary>
        public uint? RunId { get; set; }

        /// <summary>Audit only: the per-gem-use GUID ("N" format, 32 chars).</summary>
        public string RunStartGroup { get; set; }

        /// <summary>Audit only: the raw remote address.</summary>
        public string IpAddress { get; set; }
    }

    public partial class ShardDbContext
    {
        public virtual DbSet<ThreadPuzzleIpLedgerRow> ThreadPuzzleIpLedger { get; set; }

        internal static void ConfigureThreadPuzzleIpLedger(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<ThreadPuzzleIpLedgerRow>(entity =>
            {
                entity.ToTable("thread_puzzle_ip_ledger");
                entity.HasKey(e => e.Id).HasName("PRIMARY");
                entity.HasIndex(e => new { e.IpKey, e.Kind, e.AtUtc }, "thread_puzzle_ip_ledger_key_kind_at_idx");
                entity.Property(e => e.Id).HasColumnName("id").ValueGeneratedOnAdd();
                entity.Property(e => e.IpKey).IsRequired().HasColumnName("ip_Key").HasMaxLength(45);
                entity.Property(e => e.Kind).HasColumnName("kind").HasConversion<byte>();
                entity.Property(e => e.AtUtc).HasColumnName("at_Utc").HasColumnType("datetime");
                entity.Property(e => e.UntilUtc).HasColumnName("until_Utc").HasColumnType("datetime");
                entity.Property(e => e.AccountId).HasColumnName("account_Id");
                entity.Property(e => e.CharacterId).HasColumnName("character_Id");
                entity.Property(e => e.RunId).HasColumnName("run_Id");
                entity.Property(e => e.RunStartGroup).HasColumnName("run_Start_Group").HasMaxLength(32).IsFixedLength();
                entity.Property(e => e.IpAddress).HasColumnName("ip_Address").HasMaxLength(45);
            });
        }
    }
}
