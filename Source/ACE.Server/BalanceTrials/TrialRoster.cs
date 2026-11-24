using System.Collections.Generic;
using System.Linq;

using ACE.Server.Managers;

namespace ACE.Server.BalanceTrials
{
    /// <summary>
    /// The frozen guid and name table for Balance Trials characters (Docs/BalanceTrials/DESIGN.md).
    ///
    /// Masters and reps live in the player guid range BELOW the tester roster's floor, so they never collide with
    /// a TesterRoster guid, and the player allocator (which only moves up) never reaches them. Item guids come from
    /// a dynamic-range band disjoint from the roster's item band.
    ///
    /// Changing a value here after trial characters exist re-points or renames live ones. Treat this table as frozen.
    /// </summary>
    public static class TrialRoster
    {
        /// <summary>Base of the trial band, below TesterRoster.SlotBase.</summary>
        public const uint SlotBase = 0x59000000;

        /// <summary>Number of master slots (one per archetype slot).</summary>
        public const int MasterCount = 32;

        /// <summary>Number of rep characters, one per account trial-01..trial-08.</summary>
        public const int RepCount = 8;

        /// <summary>Guid distance between one slot's item band and the next.</summary>
        public const uint ItemBandStride = 0x00100000;

        /// <summary>Default base of the trial item band (dynamic range, clear of the roster's 0xF0000000 band).</summary>
        public const uint DefaultItemBandBase = 0xE8000000;

        private const uint RepBase = SlotBase + 0x100;

        private static readonly string[] RepWords = { "Anvil", "Bastion", "Cairn", "Dirge", "Ember", "Flint", "Gale", "Hearth" };

        /// <summary>One rep character: a guid, its display name and the account it logs in on.</summary>
        public readonly record struct RepSlot(uint Guid, string Name, string AccountName);

        /// <summary>
        /// Last guid of the trial item band for a given base: one stride per master. Reps get no item band (they are
        /// filled by DoCopyChar with dynamic guids), so the DELETE, Gate A allowlist and preflight all use this span.
        /// </summary>
        public static uint ItemBandMax(uint bandBase) => bandBase + (uint)MasterCount * ItemBandStride - 1;

        public static uint MasterSlot(int slot) => SlotBase + (uint)slot;

        public static string MasterName(int slot) => $"Trialmaster {slot}";

        public static IReadOnlyList<RepSlot> RepSlots { get; } = Enumerable.Range(0, RepCount)
            .Select(i => new RepSlot(RepBase + (uint)i, $"Trial {RepWords[i]}", $"trial-{i + 1:00}"))
            .ToArray();

        /// <summary>The master slots as a <see cref="SeedLayout"/>, parked as "Trialmaster {slot}".</summary>
        public static SeedLayout Layout { get; } = new SeedLayout(
            "trial",
            Enumerable.Range(0, MasterCount).Select(MasterSlot).ToArray(),
            ItemBandStride,
            MasterName);
    }
}
