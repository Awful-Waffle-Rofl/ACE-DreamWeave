using System.Collections.Generic;
using System.Linq;

namespace ACE.Server.Managers
{
    /// <summary>
    /// The fixed guid and name table for prod character seeding (Docs/ProdCharSeed/TECH-DESIGN.md).
    ///
    /// These values are reused by every refresh rather than allocated. That is the whole point: a
    /// fixed table means the player guid allocator's floor moves once and never ratchets, and slot
    /// names stay stable even when the prod top-10 reshuffles.
    ///
    /// Changing a guid or a name here after a roster exists does not create new characters, it
    /// re-points or renames live ones. Treat this table as frozen.
    /// </summary>
    public static class TesterRoster
    {
        /// <summary>
        /// Base of the reserved band. Arbitrary; being fixed and documented is the point. Sits well
        /// clear of anything a stage shard reaches organically, with roughly 100 million player
        /// guids of headroom above it.
        /// </summary>
        public const uint SlotBase = 0x5A000000;

        /// <summary>Number of prod characters copied, and therefore tester slots per account.</summary>
        public const int RankCount = 10;

        /// <summary>
        /// Guid distance between one rank's reserved item band and the next, used by prodcharseed's
        /// item-band bounds (Source/ACE.Content.Tools/Commands/ProdCharSeedCommand.cs) and independently
        /// re-derived by prodcharseedverify's Gate A (Source/ACE.Content.Tools/Commands/ProdCharSeedVerifyCommand.cs)
        /// as the upper bound of its guid allowlist. Lives here, alongside RankCount and SlotBase, rather
        /// than on either command, so both sides read the same fixed roster constant instead of one
        /// importing the other's declaration - see ProdCharSeedVerifyCommand's Gate A comment for why that
        /// independence matters.
        /// </summary>
        public const uint ItemBandStride = 0x00100000;

        /// <summary>Guid distance between one account's slot block and the next.</summary>
        private const uint AccountStride = 0x10;

        /// <summary>First tester slot guid. Source slots occupy SlotBase through SlotBase + 9.</summary>
        private const uint TesterBase = SlotBase + 0x100;

        private static readonly string[] SlotWords =
        {
            "Kestrel", "Lantern", "Quarry", "Cinder", "Thistle",
            "Harrow", "Beacon", "Marrow", "Tallow", "Vellum"
        };

        private static readonly string[] AccountWords = { "Alpha", "Bravo", "Charlie", "Delta", "Echo" };

        /// <summary>
        /// Account names on the auth database, one per account word, lower-cased because
        /// AccountCommands.HandleAccountCreate lower-cases on creation.
        /// </summary>
        private static readonly string[] AccountNames =
        {
            "roster-alpha", "roster-bravo", "roster-charlie", "roster-delta", "roster-echo"
        };

        /// <summary>
        /// One tester character slot. Rank is the prod level ranking, 0 being the highest-level
        /// character, and indexes into SourceSlots.
        /// </summary>
        public readonly record struct TesterSlot(uint Guid, string Name, string AccountName, int Rank, int AccountIndex);

        /// <summary>
        /// The ten guids holding the prod characters as extracted. These are staging only: they are
        /// parked at AccountId = 0 and flagged deleted, so they never appear in a character list.
        /// </summary>
        public static IReadOnlyList<uint> SourceSlots { get; } =
            Enumerable.Range(0, RankCount).Select(i => SlotBase + (uint)i).ToArray();

        /// <summary>The fifty guids holding the copies testers actually play.</summary>
        public static IReadOnlyList<TesterSlot> TesterSlots { get; } = BuildTesterSlots();

        public static uint SourceSlotForRank(int rank) => SourceSlots[rank];

        private static TesterSlot[] BuildTesterSlots()
        {
            var slots = new List<TesterSlot>(AccountWords.Length * RankCount);

            for (var account = 0; account < AccountWords.Length; account++)
            {
                for (var rank = 0; rank < RankCount; rank++)
                {
                    var guid = TesterBase + ((uint)account * AccountStride) + (uint)rank;
                    var name = $"{SlotWords[rank]} {AccountWords[account]}";

                    slots.Add(new TesterSlot(guid, name, AccountNames[account], rank, account));
                }
            }

            return slots.ToArray();
        }
    }
}
