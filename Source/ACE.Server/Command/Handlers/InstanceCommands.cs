using ACE.Entity;
using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Server.Managers;
using ACE.Server.Network;
using ACE.Server.Realms;

namespace ACE.Server.Command.Handlers
{
    /// <summary>
    /// ACRealms port Phase 2: commands for moving between realms and ephemeral instances.
    /// </summary>
    public static class InstanceCommands
    {
        [CommandHandler("telerealm", AccessLevel.Developer, CommandHandlerFlag.RequiresWorld, 1, "Teleports you to your current coordinates in another realm's default instance.", "<realm id>")]
        public static void HandleTeleRealm(Session session, params string[] parameters)
        {
            if (!ushort.TryParse(parameters[0], out var realmId) || RealmManager.GetRealm(realmId) == null)
            {
                CommandHandlerHelper.WriteOutputInfo(session, $"Unknown realm: {parameters[0]}");
                return;
            }

            var realm = RealmManager.GetRealm(realmId);
            var dest = new Position(session.Player.Location, realm.DefaultInstanceID);

            CommandHandlerHelper.WriteOutputInfo(session, $"Teleporting to realm {realm}...");
            session.Player.Teleport(dest);
        }

        [CommandHandler("enter-instance", AccessLevel.Developer, CommandHandlerFlag.RequiresWorld, "Creates a fresh ephemeral instance of the current dungeon landblock and teleports you into it.")]
        public static void HandleEnterInstance(Session session, params string[] parameters)
        {
            var player = session.Player;
            var currentLandblock = player.CurrentLandblock;

            if (currentLandblock == null || !currentLandblock.IsDungeon)
            {
                CommandHandlerHelper.WriteOutputInfo(session, "Ephemeral instances are only supported for dungeon landblocks.");
                return;
            }

            if (player.Location.IsEphemeralRealm)
            {
                CommandHandlerHelper.WriteOutputInfo(session, "You are already inside an ephemeral instance. Use /exitinstance first.");
                return;
            }

            var landblock = RealmManager.GetNewEphemeralLandblock(currentLandblock.Id, player);
            if (landblock == null)
            {
                CommandHandlerHelper.WriteOutputInfo(session, "Failed to create the ephemeral instance.");
                return;
            }

            // stamp where /exitinstance (or an expired-instance login) returns to
            player.SetPosition(PositionType.EphemeralRealmExitTo, new Position(player.Location));

            CommandHandlerHelper.WriteOutputInfo(session, $"Entering ephemeral instance 0x{landblock.Instance:X8} of landblock 0x{landblock.Id.Landblock:X4}...");
            player.Teleport(new Position(player.Location, landblock.Instance));
        }

        [CommandHandler("exitinstance", AccessLevel.Player, CommandHandlerFlag.RequiresWorld, "Leaves the current ephemeral instance.")]
        public static void HandleExitInstance(Session session, params string[] parameters)
        {
            var player = session.Player;

            if (!player.Location.IsEphemeralRealm)
            {
                CommandHandlerHelper.WriteOutputInfo(session, "You are not inside an instance.");
                return;
            }

            var exitTo = player.GetPosition(PositionType.EphemeralRealmExitTo)
                ?? (player.Sanctuary ?? player.Location).AsInstancedPosition(player, PlayerInstanceSelectMode.HomeRealmDefault);

            // Threads pooled loot (spec section 7): leaving a Cleared run with loot still pooled asks first.
            if (ACE.Server.ThreadDungeons.ThreadExitGuard.TryHoldExit(player, () => HandleExitInstance(session, parameters)))
                return;

            player.SetPosition(PositionType.EphemeralRealmExitTo, null);
            player.Teleport(new Position(exitTo));
        }

        [CommandHandler("portal-instancing", AccessLevel.Developer, CommandHandlerFlag.RequiresWorld, 1, "Toggles per-use ephemeral instancing on the last appraised portal.", "<on|off>")]
        public static void HandlePortalInstancing(Session session, params string[] parameters)
        {
            var player = session.Player;

            if (player.CurrentAppraisalTarget == null)
            {
                CommandHandlerHelper.WriteOutputInfo(session, "Appraise (examine) a portal first, then use this command.");
                return;
            }

            var target = player.CurrentLandblock?.GetObject(new ACE.Entity.ObjectGuid(player.CurrentAppraisalTarget.Value));

            if (!(target is WorldObjects.Portal portal))
            {
                CommandHandlerHelper.WriteOutputInfo(session, $"Last appraised object is not a portal.");
                return;
            }

            var enable = parameters[0].Equals("on", System.StringComparison.OrdinalIgnoreCase);

            if (enable)
                portal.SetProperty(ACE.Entity.Enum.Properties.PropertyInt.PortalInstancing, 1);
            else
                portal.RemoveProperty(ACE.Entity.Enum.Properties.PropertyInt.PortalInstancing);

            CommandHandlerHelper.WriteOutputInfo(session, $"{portal.Name} (0x{portal.Guid}) per-use instancing: {(enable ? "ON - each use creates a fresh ephemeral copy of its destination dungeon" : "off")}");
        }

        [CommandHandler("portal-realm", AccessLevel.Developer, CommandHandlerFlag.RequiresWorld, 1, "Routes the last appraised portal's users into a realm's default instance.", "<realm id|off>")]
        public static void HandlePortalRealm(Session session, params string[] parameters)
        {
            var player = session.Player;

            if (player.CurrentAppraisalTarget == null)
            {
                CommandHandlerHelper.WriteOutputInfo(session, "Appraise (examine) a portal first, then use this command.");
                return;
            }

            var target = player.CurrentLandblock?.GetObject(new ACE.Entity.ObjectGuid(player.CurrentAppraisalTarget.Value));

            if (!(target is WorldObjects.Portal portal))
            {
                CommandHandlerHelper.WriteOutputInfo(session, "Last appraised object is not a portal.");
                return;
            }

            if (parameters[0].Equals("off", System.StringComparison.OrdinalIgnoreCase))
            {
                portal.RemoveProperty(ACE.Entity.Enum.Properties.PropertyInt.PortalRealm);
                CommandHandlerHelper.WriteOutputInfo(session, $"{portal.Name} (0x{portal.Guid}) realm routing: off");
                return;
            }

            if (!ushort.TryParse(parameters[0], out var realmId) || RealmManager.GetRealm(realmId) == null)
            {
                CommandHandlerHelper.WriteOutputInfo(session, $"Unknown realm: {parameters[0]}");
                return;
            }

            portal.SetProperty(ACE.Entity.Enum.Properties.PropertyInt.PortalRealm, realmId);
            CommandHandlerHelper.WriteOutputInfo(session, $"{portal.Name} (0x{portal.Guid}) now routes users into {RealmManager.GetRealm(realmId)}");
        }

        [CommandHandler("exitinst", AccessLevel.Player, CommandHandlerFlag.RequiresWorld, "Leaves the current ephemeral instance.")]
        public static void HandleExitInst(Session session, params string[] parameters) => HandleExitInstance(session, parameters);

        [CommandHandler("leaveinstance", AccessLevel.Player, CommandHandlerFlag.RequiresWorld, "Leaves the current ephemeral instance.")]
        public static void HandleLeaveInstance(Session session, params string[] parameters) => HandleExitInstance(session, parameters);
    }
}
