namespace ACE.Server.SalvageRoller
{
    /// <summary>
    /// What one Salvage Roller batch does to its target (Docs/SalvageRoller/DESIGN.md 5.2).
    ///
    /// THE VALUES ARE THE SCHEMA'S: they are stored as character_salvage_batch.action and must stay equal, member
    /// for member, to ACE.Database.Models.Shard.SalvageBatchAction (SalvageRollerEligibilityTests pins both directions).
    /// Cast between the two; never renumber either. 0 is never written.
    /// </summary>
    public enum SalvageRollerAction : byte
    {
        None = 0,

        /// <summary>Tourmaline on a weapon: every roll rerolls the whole special set of a fresh copy.</summary>
        TourmalineReroll = 1,

        /// <summary>Amethyst on a weapon, CHAIN: candidate k is the weapon after k swaps in a row.</summary>
        AmethystSwap = 2,

        /// <summary>Obsidian on an item with natural gear ratings: every roll converts a fresh copy.</summary>
        ObsidianConvert = 3,

        /// <summary>Obsidian on an already-converted item: every roll rerolls a fresh copy's mods.</summary>
        ObsidianReroll = 4,

        /// <summary>Serpentine: every roll rerolls a fresh copy's item enchantments, same levels.</summary>
        SerpentineReroll = 5,

        /// <summary>Green Jade on an aspected ring, bracelet or necklace, CHAIN: candidate k is the piece after k add-or-swap steps in a row.</summary>
        GreenJadeSwap = 6,
    }
}
