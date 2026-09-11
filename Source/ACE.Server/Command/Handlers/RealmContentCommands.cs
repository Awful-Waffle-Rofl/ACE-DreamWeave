using System;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;

using Microsoft.EntityFrameworkCore;

using ACE.Database;
using ACE.Database.Models.World;
using ACE.Entity;
using ACE.Entity.Enum;
using ACE.Server.Factories;
using ACE.Server.Managers;
using ACE.Server.Network;
using ACE.Server.Network.GameMessages.Messages;
using ACE.Server.WorldObjects;

namespace ACE.Server.Command.Handlers
{
    /// <summary>
    /// Realms Phase 3: in-game authoring of per-realm content overrides.
    /// Stand in a realm instance (telerealm N), place content, done - rows go to the
    /// realm content tables, never the base landblock_instance table.
    /// </summary>
    public static class RealmContentCommands
    {
        [CommandHandler("realm-createinst", AccessLevel.Developer, CommandHandlerFlag.RequiresWorld, 1, "Spawns a new wcid or classname as a realm content instance at your location (you must be in a realm instance).", "<wcid or classname>\n\nTo create a parent/child link (e.g. generator children): /realm-createinst -p <parent guid> -c <wcid or classname>\nTo use the last appraised object as parent: /realm-createinst -p -c <wcid or classname>")]
        public static void HandleRealmCreateInst(Session session, params string[] parameters)
        {
            var player = session.Player;
            var loc = new Position(player.Location);

            Position.ParseInstanceID(loc.Instance, out _, out var realmId, out _);

            if (realmId == 0)
            {
                CommandHandlerHelper.WriteOutputInfo(session, "You are in the base world. Use telerealm <id> first - realm content is authored from inside the realm.");
                return;
            }

            var param = parameters[0];
            uint? parentGuid = null;

            var landblock = player.CurrentLandblock.Id.Landblock;
            var firstStaticGuid = 0x70000000 | (uint)landblock << 12;
            var maxStaticGuid = firstStaticGuid | 0xFFF;

            if (parameters.Length > 1)
            {
                var allParams = string.Join(" ", parameters);
                var match = Regex.Match(allParams, @"-p ([\S]+) -c ([\S]+)", RegexOptions.IgnoreCase);

                if (match.Success)
                {
                    var parentGuidStr = match.Groups[1].Value;
                    param = match.Groups[2].Value;

                    if (parentGuidStr.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
                        parentGuidStr = parentGuidStr.Substring(2);

                    if (!uint.TryParse(parentGuidStr, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var _parentGuid))
                    {
                        CommandHandlerHelper.WriteOutputInfo(session, $"Couldn't parse parent guid {match.Groups[1].Value}");
                        return;
                    }

                    parentGuid = _parentGuid <= 0xFFF ? firstStaticGuid | _parentGuid : _parentGuid;
                }
                else if (parameters[1].StartsWith("-c", StringComparison.OrdinalIgnoreCase))
                {
                    var parent = CommandHandlerHelper.GetLastAppraisedObject(session);

                    if (parent == null)
                    {
                        CommandHandlerHelper.WriteOutputInfo(session, "Couldn't find parent object");
                        return;
                    }

                    parentGuid = parent.Guid.Full;
                }
            }

            var weenie = uint.TryParse(param, out var wcid) ? DatabaseManager.World.GetWeenie(wcid) : DatabaseManager.World.GetWeenie(param);

            if (weenie == null)
            {
                CommandHandlerHelper.WriteOutputInfo(session, $"Couldn't find weenie {param}");
                return;
            }

            using (var ctx = new WorldDbContext())
            {
                // realm content allocates guids top-down from the landblock static band
                // (base content allocates bottom-up), across all realms, skipping base
                // guids - a realm row must never share persisted state with a base row
                var usedGuids = ctx.LandblockInstanceRealm
                    .Where(i => i.Landblock == landblock)
                    .Select(i => i.Guid)
                    .ToHashSet();

                foreach (var baseInstance in DatabaseManager.World.GetCachedInstancesByLandblock(landblock))
                    usedGuids.Add(baseInstance.Guid);

                var nextGuid = maxStaticGuid;
                while (nextGuid >= firstStaticGuid && usedGuids.Contains(nextGuid))
                    nextGuid--;

                if (nextGuid < firstStaticGuid)
                {
                    CommandHandlerHelper.WriteOutputInfo(session, $"Landblock {landblock:X4} has no free static guids left");
                    return;
                }

                LandblockInstanceRealm parentRow = null;
                WorldObject parentObj = null;

                if (parentGuid != null)
                {
                    parentRow = ctx.LandblockInstanceRealm.Include(i => i.LandblockInstanceLinkRealm).FirstOrDefault(i => i.RealmId == realmId && i.Guid == parentGuid);
                    parentObj = player.CurrentLandblock.GetObject(parentGuid.Value);

                    if (parentRow == null || parentObj == null)
                    {
                        CommandHandlerHelper.WriteOutputInfo(session, $"Couldn't find realm content instance for parent guid 0x{parentGuid:X8} in realm {realmId} (parents must be realm content, not base content)");
                        return;
                    }
                }

                var entityWeenie = Database.Adapter.WeenieConverter.ConvertToEntityWeenie(weenie);
                var wo = WorldObjectFactory.CreateWorldObject(entityWeenie, new ObjectGuid(nextGuid));

                if (wo == null)
                {
                    CommandHandlerHelper.WriteOutputInfo(session, $"Failed to create new object for {weenie.ClassId} - {weenie.ClassName}");
                    return;
                }

                var isLinkChild = parentRow != null;

                if (!wo.Stuck && !isLinkChild)
                {
                    CommandHandlerHelper.WriteOutputInfo(session, $"{weenie.ClassId} - {weenie.ClassName} is missing PropertyBool.Stuck, cannot spawn as an instance unless it is a child object");
                    return;
                }

                // spawn as ethereal temporarily, to spawn directly on player position
                wo.Ethereal = true;
                wo.Location = new Position(loc);
                wo.Location.PositionZ += 0.05f;

                CommandHandlerHelper.WriteOutputInfo(session, $"Creating new realm {realmId} content instance {(isLinkChild ? "child object " : "")}@ {loc.ToLOCString()}\n{wo.WeenieClassId} - {wo.Name} (0x{nextGuid:X8})");

                if (!wo.EnterWorld())
                {
                    CommandHandlerHelper.WriteOutputInfo(session, "Failed to spawn new object at this location");
                    return;
                }

                var row = new LandblockInstanceRealm
                {
                    RealmId = realmId,
                    Guid = nextGuid,
                    WeenieClassId = wo.WeenieClassId,
                    ObjCellId = wo.Location.Cell,
                    OriginX = wo.Location.PositionX,
                    OriginY = wo.Location.PositionY,
                    OriginZ = wo.Location.PositionZ,
                    AnglesW = wo.Location.RotationW,
                    AnglesX = wo.Location.RotationX,
                    AnglesY = wo.Location.RotationY,
                    AnglesZ = wo.Location.RotationZ,
                    IsLinkChild = isLinkChild,
                };

                ctx.LandblockInstanceRealm.Add(row);

                if (isLinkChild)
                {
                    ctx.LandblockInstanceLinkRealm.Add(new LandblockInstanceLinkRealm
                    {
                        RealmId = realmId,
                        ParentGuid = parentGuid.Value,
                        ChildGuid = nextGuid,
                    });

                    parentObj.SetLinkProperties(wo);
                    parentObj.ChildLinks.Add(wo);
                    wo.ParentLink = parentObj;
                }

                ctx.SaveChanges();
            }

            DatabaseManager.World.ClearCachedInstancesByLandblock(landblock);
        }

        [CommandHandler("reload-realms", AccessLevel.Developer, CommandHandlerFlag.None, "Reloads the realm registry and the realm landblock rules from the world database without a restart. Additive: new realms appear, existing realms pick up name/parent changes; removing a realm still requires a restart.")]
        public static void HandleReloadRealms(Session session, params string[] parameters)
        {
            var (added, updated, missing) = RealmManager.Reload();

            // reloads the strip manifest and drops the per-realm instance cache, whose
            // entries were resolved against the rules being replaced
            var rules = DatabaseManager.World.CacheAllRealmLandblockRules();

            var msg = $"Realm registry reloaded: {added} added, {updated} updated, {RealmManager.GetAllRealms().Count - 1} realm(s) total, {rules} landblock rule(s) cached.";

            if (missing.Count > 0)
                msg += $"\nWarning: realm id(s) {string.Join(", ", missing)} are no longer in the database but stay loaded - removing a realm requires a restart.";

            CommandHandlerHelper.WriteOutputInfo(session, msg);
        }

        [CommandHandler("realm-removeinst", AccessLevel.Developer, CommandHandlerFlag.RequiresWorld, "Removes the last appraised object from the current realm's content instances (including its link children).")]
        public static void HandleRealmRemoveInst(Session session, params string[] parameters)
        {
            var player = session.Player;

            Position.ParseInstanceID(player.Location.Instance, out _, out var realmId, out _);

            if (realmId == 0)
            {
                CommandHandlerHelper.WriteOutputInfo(session, "You are in the base world - this command only removes realm content.");
                return;
            }

            var target = CommandHandlerHelper.GetLastAppraisedObject(session);

            if (target == null)
            {
                CommandHandlerHelper.WriteOutputInfo(session, "Appraise (examine) the object to remove first.");
                return;
            }

            var landblock = player.CurrentLandblock.Id.Landblock;

            using (var ctx = new WorldDbContext())
            {
                var row = ctx.LandblockInstanceRealm.Include(i => i.LandblockInstanceLinkRealm).FirstOrDefault(i => i.RealmId == realmId && i.Guid == target.Guid.Full);

                if (row == null)
                {
                    CommandHandlerHelper.WriteOutputInfo(session, $"0x{target.Guid} - {target.Name} is not a realm {realmId} content instance.");
                    return;
                }

                // remove link children with the row (base removeinst semantics)
                var childGuids = row.LandblockInstanceLinkRealm.Select(l => l.ChildGuid).ToList();

                foreach (var childGuid in childGuids)
                {
                    var childRow = ctx.LandblockInstanceRealm.FirstOrDefault(i => i.RealmId == realmId && i.Guid == childGuid);
                    if (childRow != null)
                        ctx.LandblockInstanceRealm.Remove(childRow);

                    player.CurrentLandblock.GetObject(childGuid)?.Destroy();
                }

                ctx.LandblockInstanceRealm.Remove(row);   // links cascade

                ctx.SaveChanges();

                CommandHandlerHelper.WriteOutputInfo(session, $"Removed realm {realmId} content instance 0x{target.Guid} - {target.Name}{(childGuids.Count > 0 ? $" and {childGuids.Count} link child(ren)" : "")}");
            }

            target.Destroy();

            DatabaseManager.World.ClearCachedInstancesByLandblock(landblock);
        }
    }
}
