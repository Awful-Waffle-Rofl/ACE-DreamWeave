using System;
using System.Collections.Generic;

using ACE.Database.Models.World;
using ACE.Server.Entity;
using ACE.Server.WorldObjects;

namespace ACE.Server.ThreadDungeons
{
    /// <summary>
    /// One banked kill (Docs/Threads/POOLED-LOOT-CACHE-DESIGN.md section 3). Holds what is needed to roll,
    /// later, exactly what that creature's corpse would have held: its own death-treasure profile (the
    /// spawner's DeathTreasureOverride, the same object stamped as run.LootProfile), its salvage affinities,
    /// and a rare that had to be rolled at kill time because the rule needs the killer. The rare is a live
    /// WorldObject held OUT of world until it lands in a cache or the run ends.
    /// </summary>
    public sealed class ThreadLootLedgerEntry
    {
        /// <summary>A solo-shaped entry: one roll, no named rare recipient.</summary>
        public ThreadLootLedgerEntry(bool isBoss, bool killerIsOlthoiPlayer, TreasureDeath profile,
            IReadOnlyList<(int MaterialId, uint BaseWcid, double Chance)> salvageAffinities, WorldObject heldRare, string heldRareFinderName)
            : this(isBoss, killerIsOlthoiPlayer, profile, salvageAffinities, heldRare, heldRareFinderName, 1, 0)
        {
        }

        /// <param name="rolls">How many times the entry's DeathTreasure and salvage-affinity rolls run when it materialises. Below 1 reads as 1.</param>
        /// <param name="rareRecipientGuid">The roster member a group run rolled the held rare for; 0 for the solo rule.</param>
        public ThreadLootLedgerEntry(bool isBoss, bool killerIsOlthoiPlayer, TreasureDeath profile,
            IReadOnlyList<(int MaterialId, uint BaseWcid, double Chance)> salvageAffinities, WorldObject heldRare, string heldRareFinderName,
            int rolls, uint rareRecipientGuid)
        {
            // GenerateTreasure_Olthoi's "no normal loot" rule: an Olthoi kill materialises nothing, and the
            // corpse rare rule already makes an Olthoi killer ineligible, so a rare here is a caller bug.
            if (killerIsOlthoiPlayer && heldRare != null)
                throw new ArgumentException("an Olthoi-killer entry materialises nothing and cannot hold a rare", nameof(heldRare));

            IsBoss = isBoss;
            KillerIsOlthoiPlayer = killerIsOlthoiPlayer;
            Profile = profile;
            SalvageAffinities = salvageAffinities;
            HeldRare = heldRare;
            HeldRareFinderName = heldRareFinderName;
            Rolls = Math.Max(1, rolls);
            RareRecipientGuid = rareRecipientGuid;
            RollsRemaining = Rolls;
        }

        /// <summary>
        /// Rolls of this entry not yet handed out. Starts at <see cref="Rolls"/>. Only ThreadDungeonRun's ledger
        /// claim writes it, under the run's stateLock, while the entry is still at the head of the ledger: a fat group
        /// entry (up to ThreadLootPool.MaxRollsPerKill rolls) is consumed a slice at a time across delivery steps, so
        /// no step has to materialise it whole (Docs/Threads/POOLED-LOOT-CACHE-DESIGN.md section 4).
        /// </summary>
        internal int RollsRemaining { get; set; }

        /// <summary>
        /// A claimable piece of this entry: <paramref name="rolls"/> of its rolls, the same profile, affinities and
        /// killer flag. Only the FINAL slice (the one that empties the entry) carries the held rare and its recipient,
        /// so the rare is handed out exactly once and still lands last, after every roll, as on the corpse. A slice is
        /// a new object; the ledger keeps the original until its last roll is claimed.
        /// </summary>
        internal ThreadLootLedgerEntry Slice(int rolls, bool final)
            => new ThreadLootLedgerEntry(IsBoss, KillerIsOlthoiPlayer, Profile, SalvageAffinities,
                final ? HeldRare : null, final ? HeldRareFinderName : null, rolls, final ? RareRecipientGuid : 0);

        public bool IsBoss { get; }
        public bool KillerIsOlthoiPlayer { get; }
        public TreasureDeath Profile { get; }
        public IReadOnlyList<(int MaterialId, uint BaseWcid, double Chance)> SalvageAffinities { get; }
        public WorldObject HeldRare { get; }

        /// <summary>The name the "has discovered" broadcast uses: the killer's name with a leading '+' trimmed, as Corpse.TryGenerateRare stores it.</summary>
        public string HeldRareFinderName { get; }

        /// <summary>
        /// How many times the kill's DeathTreasure and salvage-affinity rolls run when the entry materialises (Group
        /// Threads ruling R17). Always at least 1, and exactly 1 for every solo entry. The held rare is never multiplied.
        /// </summary>
        public int Rolls { get; }

        /// <summary>
        /// The roster member a group run rolled the held rare FOR (ruling R18), so delivery can put the rare in that
        /// member's pile. 0 is the solo rule: no named recipient. Always 0 when the entry holds no rare.
        /// </summary>
        public uint RareRecipientGuid { get; }

        /// <param name="canGenerateRare">Creature.ResolveCanGenerateRare for this kill.</param>
        /// <param name="rollRare">Invoked once, only for an eligible kill with a killer. Returns null for no rare.</param>
        public static ThreadLootLedgerEntry FromKill(bool isBoss, DamageHistoryInfo killer, bool canGenerateRare, TreasureDeath profile,
            IReadOnlyList<(int MaterialId, uint BaseWcid, double Chance)> salvageAffinities, Func<DamageHistoryInfo, WorldObject> rollRare)
            => FromKill(isBoss, killer, canGenerateRare, profile, salvageAffinities, rollRare, 1, null, 0);

        /// <param name="canGenerateRare">Creature.ResolveCanGenerateRare for this kill. Eligibility is always the KILLER's.</param>
        /// <param name="rollRare">Invoked once, only for an eligible kill with a killer, with <paramref name="rareFor"/> when given, else the killer. Returns null for no rare.</param>
        /// <param name="rolls">The entry's roll count (<see cref="ThreadLootPool.RollCount"/>); 1 for a solo run.</param>
        /// <param name="rareFor">
        /// The player the rare is rolled for, when a group run picked a recipient other than the killer's own roll;
        /// null keeps the solo rule (roll for the killer). The finder name follows the same player.
        /// </param>
        /// <param name="rareRecipientGuid">Stored only when a rare was actually rolled; 0 otherwise.</param>
        public static ThreadLootLedgerEntry FromKill(bool isBoss, DamageHistoryInfo killer, bool canGenerateRare, TreasureDeath profile,
            IReadOnlyList<(int MaterialId, uint BaseWcid, double Chance)> salvageAffinities, Func<DamageHistoryInfo, WorldObject> rollRare,
            int rolls, DamageHistoryInfo rareFor, uint rareRecipientGuid)
        {
            var olthoi = killer != null && killer.IsOlthoiPlayer;

            WorldObject rare = null;
            string finder = null;

            if (!olthoi && canGenerateRare && killer != null && rollRare != null)
            {
                var rolledFor = rareFor ?? killer;

                rare = rollRare(rolledFor);

                if (rare != null)
                    finder = rolledFor.Name?.TrimStart('+');
            }

            return new ThreadLootLedgerEntry(isBoss, olthoi, profile, salvageAffinities, rare, finder, rolls, rare != null ? rareRecipientGuid : 0);
        }
    }
}
