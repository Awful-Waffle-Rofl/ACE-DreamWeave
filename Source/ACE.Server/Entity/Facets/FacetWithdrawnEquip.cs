using System;

using ACE.Entity.Enum;
using ACE.Server.WorldObjects;

namespace ACE.Server.Entity.Facets
{
    /// <summary>
    /// The facet restore's equip-first step for an object that has just come out of the account vault:
    /// the step Player_Facets.WithdrawAndRestoreFromVault hands to VaultPackDelivery.WithdrawToPack as
    /// its tryDeliverFirst, so it runs after a successful withdraw and before any pack delivery.
    ///
    /// Static over its four collaborators rather than an instance method on Player so it can be driven
    /// without a live Player: ACE.Server.Tests cannot construct one, and an uninitialized Player has a
    /// null facetSpellActivationWaiver set (field initializers skipped), while reflecting into a
    /// Player-declared field can run Player's static initializer, which reaches the world database (see
    /// MuleSummonTests' class remarks). Production passes the Player's own members, so the behaviour is
    /// exactly the inline block this replaced.
    /// </summary>
    internal static class FacetWithdrawnEquip
    {
        /// <summary>
        /// Checks wield requirements, then - only when they pass - equips the object, with the facet
        /// spell-activation waiver held around the equip when the remembered entry was captured with its
        /// spells active and the caller is waiving buffed requirements.
        ///
        /// <paramref name="wieldError"/> is the wield check's answer whatever happens after it, because
        /// the caller's report wording depends on it when the object ends up in the pack instead.
        ///
        /// The waiver is keyed on the object's CURRENT guid (a ledger withdrawal rebuilds the object under
        /// a NEW guid, so resolution.Guid would be wrong), and its End runs in a finally: the equip can
        /// throw, and a leaked waiver would apply to a later, unrelated equip of the same guid. An
        /// exception from the equip propagates after the finally, as it did inline.
        ///
        /// Returns true when the object is now worn; false means nothing has been said to the client about
        /// it (TryEquipWithdrawnForFacet's contract) and the caller should deliver it to the pack.
        /// </summary>
        internal static bool TryEquip(
            WorldObject item,
            FacetGearResolution resolution,
            bool waiveBuffedRequirements,
            Func<WorldObject, bool, WeenieError> checkWieldRequirements,
            Action<uint> beginSpellActivationWaiver,
            Action<uint> endSpellActivationWaiver,
            Func<WorldObject, EquipMask, bool> equipWithdrawn,
            out WeenieError wieldError)
        {
            // BEFORE any delivery, because the answer picks which delivery is correct.
            // CheckWieldRequirements reads only item properties and this character's own skills,
            // attributes, vitals and level, so it does not care that the object is currently parented to
            // nothing; it was already being called on an item the caller had not yet equipped.
            wieldError = checkWieldRequirements(item, waiveBuffedRequirements);

            // Waived only when the remembered entry was captured with its spells active - see
            // Player.facetSpellActivationWaiver's remarks.
            var waiveSpells = waiveBuffedRequirements && resolution.SpellsActive;

            if (waiveSpells)
                beginSpellActivationWaiver(item.Guid.Full);

            try
            {
                return wieldError == WeenieError.None && equipWithdrawn(item, (EquipMask)resolution.Slot);
            }
            finally
            {
                if (waiveSpells)
                    endSpellActivationWaiver(item.Guid.Full);
            }
        }
    }
}
