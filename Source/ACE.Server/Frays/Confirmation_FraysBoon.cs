using ACE.Entity;
using ACE.Entity.Enum;
using ACE.Server.Entity;

namespace ACE.Server.Frays
{
    /// <summary>
    /// The Ember Boon offer window, which goes away on /fp 1, 2 or 3. A yes/no box,
    /// because the server can close one (ConfirmationManager.TryCancel) and cannot close a plain popup. Either button
    /// only closes it; the pick is /fp. The fire closes it when the boon is taken, by pick, plan or vote.
    /// </summary>
    public class Confirmation_FraysBoon : Confirmation
    {
        public Confirmation_FraysBoon(ObjectGuid playerGuid) : base(playerGuid, ConfirmationType.Yes_No)
        {
        }

        public override void ProcessConfirmation(bool response, bool timeout = false)
        {
        }
    }
}
