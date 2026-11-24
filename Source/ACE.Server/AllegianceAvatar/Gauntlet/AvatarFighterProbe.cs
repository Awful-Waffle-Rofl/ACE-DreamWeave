using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

using log4net;

using ACE.Server.Managers;
using ACE.Server.WorldObjects;

namespace ACE.Server.Avatars.Gauntlet;

/// <summary>
/// The fighter visibility probe (stage report 2026-11-23: one member never saw the fighter while an admin standing beside
/// them did). Logs one line when a fighter is placed, naming the players its placement create went to, and one line each
/// time a client is sent a create or a delete for it. A stage rerun then tells a create that was never sent (no create line
/// for that player: a known-objects gap) from one that was sent and not drawn (a create line, but nothing on screen: a
/// client draw failure). Gated by allegiance_avatar_fighter_probe_log.
///
/// Every entry point is reached only after a type test against <see cref="GauntletAvatarFighter"/>, so nothing here runs
/// for any other object. Nothing here throws into its caller.
/// </summary>
internal static class AvatarFighterProbe
{
    private static readonly ILog log = LogManager.GetLogger(System.Reflection.MethodBase.GetCurrentMethod().DeclaringType);

    internal const string Tag = "[AVATAR_FIGHTER_PROBE]";

    /// <summary>Test seam: whether the probe logs (allegiance_avatar_fighter_probe_log).</summary>
    internal static Func<bool> Enabled = EnabledFromPropertyManager;

    /// <summary>Test seam: receives each line instead of the log.</summary>
    internal static Action<string> Sink;

    private static bool EnabledFromPropertyManager() => PropertyManager.GetBool("allegiance_avatar_fighter_probe_log").Item;

    internal static void ResetSeams()
    {
        Enabled = EnabledFromPropertyManager;
        Sink = null;
    }

    /// <summary>The fighter entered the world (its landblock thread). <paramref name="context"/> names the caller, e.g. "gauntlet run 12".</summary>
    public static void OnPlaced(GauntletAvatarFighter fighter, string context)
    {
        Guarded(() =>
        {
            var known = fighter.PhysicsObj?.ObjMaint?.GetKnownPlayersValuesAsPlayer()?.Select(p => p.Name).ToList() ?? new List<string>();
            return PlacedLine(context, fighter.Guid.Full, fighter.Location?.Instance ?? 0, fighter.Location?.Cell ?? 0,
                fighter.PhysicsObj != null ? (uint)fighter.PhysicsObj.State : 0u, fighter.ObjScale, known);
        });
    }

    /// <summary>
    /// Player.TrackObject reached a fighter (any thread the caller is on). <paramref name="blocked"/> names the gate that
    /// stopped the send (null when the create went out); <paramref name="delayed"/> is TrackObject's post-teleport flag.
    /// </summary>
    public static void OnClientCreate(Player viewer, GauntletAvatarFighter fighter, bool delayed, string blocked)
    {
        Guarded(() => ClientLine(blocked == null ? "create sent" : "create skipped (" + blocked + ")", viewer.Name, viewer.Guid.Full,
            viewer.Adminvision, viewer.Location?.Instance ?? 0, viewer.Location?.Cell ?? 0, fighter.Guid.Full, fighter.Location?.Instance ?? 0,
            Distance(viewer, fighter), delayed));
    }

    /// <summary>Player.RemoveTrackedObject reached a fighter: the client is told to delete it (or to pick it up).</summary>
    public static void OnClientDelete(Player viewer, GauntletAvatarFighter fighter, bool fromPickup)
    {
        Guarded(() => ClientLine(fromPickup ? "pickup sent" : "delete sent", viewer.Name, viewer.Guid.Full, viewer.Adminvision,
            viewer.Location?.Instance ?? 0, viewer.Location?.Cell ?? 0, fighter.Guid.Full, fighter.Location?.Instance ?? 0,
            Distance(viewer, fighter), false));
    }

    // ---- lines (pure) ---------------------------------------------------------------------------------------------------

    internal static string PlacedLine(string context, uint guid, uint instance, uint cell, uint physicsState, float? scale, IReadOnlyCollection<string> knownPlayers)
    {
        var names = knownPlayers == null || knownPlayers.Count == 0 ? "none" : string.Join(", ", knownPlayers);

        return string.Format(CultureInfo.InvariantCulture,
            "{0} placed 0x{1:X8} ({2}) instance 0x{3:X8} cell 0x{4:X8} physics 0x{5:X8} scale {6}; placement create went to {7} player(s): {8}",
            Tag, guid, context ?? "?", instance, cell, physicsState, scale.HasValue ? scale.Value.ToString("0.###", CultureInfo.InvariantCulture) : "unset",
            knownPlayers?.Count ?? 0, names);
    }

    internal static string ClientLine(string what, string viewer, uint viewerGuid, bool adminvision, uint viewerInstance, uint viewerCell,
        uint fighterGuid, uint fighterInstance, double? distance, bool delayed)
    {
        return string.Format(CultureInfo.InvariantCulture,
            "{0} {1}: 0x{2:X8} to {3} (0x{4:X8}) adminvision {5}, viewer instance 0x{6:X8} cell 0x{7:X8}, fighter instance 0x{8:X8}, distance {9}{10}",
            Tag, what, fighterGuid, viewer ?? "?", viewerGuid, adminvision ? "on" : "off", viewerInstance, viewerCell, fighterInstance,
            distance.HasValue ? distance.Value.ToString("0.0", CultureInfo.InvariantCulture) + " m" : "unknown",
            delayed ? ", post-teleport delay" : "");
    }

    private static double? Distance(Player viewer, GauntletAvatarFighter fighter)
    {
        if (viewer.Location == null || fighter.Location == null)
            return null;

        return viewer.Location.Instance == fighter.Location.Instance ? viewer.Location.DistanceTo(fighter.Location) : null;
    }

    private static void Guarded(Func<string> line)
    {
        try
        {
            if (!Enabled())
                return;

            var text = line();

            if (Sink != null)
                Sink(text);
            else
                log.Info(text);
        }
        catch (Exception ex)
        {
            log.Debug($"{Tag} probe line threw", ex);
        }
    }
}
