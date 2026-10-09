namespace ACE.Server.Pvp
{
    /// <summary>
    /// Which death penalties are waived for an in-match death (Docs/Pvp/DESIGN.md's Rulings and H5):
    /// "No vitae and no item loss on any in-match death", no respite, and enchantments are kept or purged
    /// per pvp_arena_death_keeps_enchantments.
    /// </summary>
    public sealed record PvpDeathWaiver(bool NoVitae, bool NoItemLoss, bool NoRespite, bool KeepsEnchantments)
    {
        public static PvpDeathWaiver ForInMatchDeath(bool deathKeepsEnchantments)
        {
            return new PvpDeathWaiver(NoVitae: true, NoItemLoss: true, NoRespite: true, KeepsEnchantments: deathKeepsEnchantments);
        }
    }
}
