using ACE.Common;
using ACE.Entity;
using ACE.Entity.Enum;
using ACE.Server.Entity;
using ACE.Server.Managers;

namespace ACE.Server.Frays
{
    /// <summary>The yes/no popup a player gets when they use an unlit Hollow Campfire: Yes lights it.</summary>
    public class Confirmation_FraysLight : Confirmation
    {
        public FraysFire Fire { get; }

        public Confirmation_FraysLight(ObjectGuid playerGuid, FraysFire fire)
            : base(playerGuid, ConfirmationType.Yes_No)
        {
            Fire = fire;
        }

        public override void ProcessConfirmation(bool response, bool timeout = false)
        {
            if (!response || timeout)
                return;

            var player = PlayerManager.GetOnlinePlayer(PlayerGuid);

            if (player == null || Fire == null)
                return;

            var fire = Fire;

            // Light it on the fire's own landblock thread, like every other change to a fire.
            fire.Landblock.EnqueueAction(new ACE.Server.Entity.Actions.ActionEventDelegate(() =>
                FraysManager.Tell(player, fire.TryLight(player, Time.GetUnixTime()))));
        }
    }
}
