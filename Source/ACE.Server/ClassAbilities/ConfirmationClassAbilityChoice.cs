using System;

using ACE.Entity;
using ACE.Entity.Enum;
using ACE.Server.Entity;

namespace ACE.Server.ClassAbilities
{
    /// <summary>
    /// A Yes/No confirmation that hands the response back to a callback (unlike Confirmation_Custom, which
    /// only fires on Yes), letting a caller advance/react on No as well as Yes. A timeout ends the
    /// interaction quietly (the callback is never invoked).
    ///
    /// Originally private to ClassAbilityTrainer's own trainer/exchanger option-chain prompts
    /// (<see cref="ClassAbilityTrainer"/>). Pulled out to its own file and made internal so
    /// Vendor.cs's class-ability instant-learn buy flow (Vendor.BuyItems_ValidateTransaction) can reuse
    /// the identical Yes/No/timeout shape - it needs the No branch (to resync the vendor panel) that
    /// Confirmation_Custom does not offer.
    /// </summary>
    internal class Confirmation_ClassAbilityChoice : Confirmation
    {
        private readonly Action<bool> onResponse;

        public Confirmation_ClassAbilityChoice(ObjectGuid playerGuid, Action<bool> onResponse)
            : base(playerGuid, ConfirmationType.Yes_No)
        {
            this.onResponse = onResponse;
        }

        public override void ProcessConfirmation(bool response, bool timeout = false)
        {
            if (Player == null || timeout)
                return;

            onResponse(response);
        }
    }
}
