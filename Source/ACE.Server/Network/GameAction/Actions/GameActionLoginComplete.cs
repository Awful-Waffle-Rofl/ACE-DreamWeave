
namespace ACE.Server.Network.GameAction.Actions
{
    public static class GameActionLoginComplete
    {
        /// <summary>
        /// This is called when the client player exits portal space
        /// It includes initial login, as well as portaling / teleporting
        /// </summary>
        [GameAction(GameActionType.LoginComplete)]
        public static void Handle(ClientMessage message, Session session)
        {
            // the same entry point the server-side fallback uses (Player_TeleportArrival): runs OnTeleportComplete once per
            // window, then the once-only FirstEnterWorldDone block that used to live here
            session.Player.CompleteArrival(ACE.Server.Entity.TeleportCompletionSource.Client);
        }
    }
}
