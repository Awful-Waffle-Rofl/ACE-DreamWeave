using ACE.Server.WorldObjects;

namespace ACE.Server.Entity.BetaGift
{
    /// <summary>
    /// The add-to-pack step of granting the beta gift, pulled out of CharacterHandler so it is
    /// unit-testable on its own: Player cannot be constructed in ACE.Server.Tests (its constructor makes
    /// a live DatabaseManager.Authentication round-trip - see MonsterEffectWardTests.cs's note on "new
    /// Player("), but Player IS a Container, and a bare Container (no DB) exercises the same
    /// Container.TryAddToInventory call CharacterHandler makes on a real one.
    /// </summary>
    public static class BetaGiftGrant
    {
        /// <summary>
        /// Adds an already-created gift item to a character's pack. Does not create the item and does not
        /// check eligibility - callers gate with BetaGiftRules.IsEligible and create the item via
        /// WorldObjectFactory first, the same order PlayerFactory follows for ordinary starter gear.
        /// </summary>
        public static bool TryAddGiftToPack(Container pack, WorldObject giftItem)
        {
            if (pack == null || giftItem == null)
                return false;

            return pack.TryAddToInventory(giftItem);
        }
    }
}
