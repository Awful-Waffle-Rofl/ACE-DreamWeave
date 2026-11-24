using System;
using System.Collections.Concurrent;

using ACE.Entity;
using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Server.Entity.Actions;
using ACE.Server.Managers;
using ACE.Server.Network.GameMessages.Messages;
using ACE.Server.WorldObjects;

namespace ACE.Server.Avatars.Gauntlet;

/// <summary>
/// What a Gauntlet Gate needs from the run it belongs to. Task 7's GauntletRun implements it (composing a
/// <see cref="GauntletArenaHold"/> for admission) and registers its gate object through <see cref="GauntletGate.Register"/>
/// when it places the gate; it unregisters when the run closes.
/// </summary>
public interface IGauntletGateTarget
{
    /// <summary>The run's state.</summary>
    GauntletState State { get; }

    /// <summary>True when the player may enter now: a member of the Avatar's bound allegiance while the run is in an admitting state. Delegates to the run's <see cref="GauntletArenaHold.Admits"/>.</summary>
    bool Admits(Player player);

    /// <summary>The next arrival point in rotation (in the arena's instance), advancing the rotation. Null when the arena is not loaded.</summary>
    Position NextEntryPoint();

    /// <summary>Seconds left on the player's death hold (Task 9 fills this in), or 0 when the player is not held.</summary>
    double DeathHoldSecondsLeft(Player player);
}

/// <summary>
/// The Gauntlet Gate: a Generic object carrying PropertyBool.AllegianceGauntletGate, dispatched from GenericObject.ActOnUse.
/// A use finds its run by the gate's guid in <see cref="Register"/>'s registry, then checks admission, refuses a player still
/// under a death hold with the time left, stamps EphemeralRealmExitTo with the player's position (the Pinnacle model) and
/// teleports the player to the next entry point.
/// </summary>
public static class GauntletGate
{
    public const string DormantLine = "The gate is dormant.";
    public const string ClosedLine = "The Allegiance Gauntlet is not open right now.";
    public const string NotMemberLine = "Only a member of this Avatar's allegiance may pass the gate.";
    public const string InstanceLine = "You cannot enter the Allegiance Gauntlet from inside an instance.";
    public const string NoEntryLine = "The arena is not ready yet. Try again in a moment.";

    private static readonly ConcurrentDictionary<uint, IGauntletGateTarget> targets = new ConcurrentDictionary<uint, IGauntletGateTarget>();

    /// <summary>Test seam: the teleport. Defaults to a world-thread-safe teleport.</summary>
    internal static Action<Player, Position> Teleporter = DefaultTeleport;

    /// <summary>Test seam: every line sent to a player by a gate.</summary>
    internal static Action<Player, string> Told;

    private static void DefaultTeleport(Player player, Position to)
        => WorldManager.ThreadSafeTeleport(player, to, new ActionEventDelegate(() => player.SendWeenieError(WeenieError.ITeleported)));

    internal static void ResetSeams()
    {
        Teleporter = DefaultTeleport;
        Told = null;
        targets.Clear();
    }

    /// <summary>Binds a placed gate (its guid) to its run. Replaces any earlier binding for that guid.</summary>
    public static void Register(uint gateGuid, IGauntletGateTarget target)
    {
        if (target == null)
            throw new ArgumentNullException(nameof(target));

        targets[gateGuid] = target;
    }

    /// <summary>Removes a gate's binding (the run closed). A later use of that gate answers "dormant".</summary>
    public static void Unregister(uint gateGuid) => targets.TryRemove(gateGuid, out _);

    internal static bool TryGetTarget(uint gateGuid, out IGauntletGateTarget target) => targets.TryGetValue(gateGuid, out target);

    /// <summary>Sends a gate line (the gate's channel and its test seam) from outside the gate.</summary>
    internal static void TellLine(Player player, string text)
    {
        if (player != null)
            Tell(player, text);
    }

    private static void Tell(Player player, string text)
    {
        Told?.Invoke(player, text);
        player.Session?.Network.EnqueueSend(new GameMessageSystemChat(text, ChatMessageType.Broadcast));
    }

    /// <summary>
    /// GenericObject.ActOnUse: true when <paramref name="gate"/> is a Gauntlet Gate (the use is handled here, admitted or
    /// refused). False after one property read for every other object.
    /// </summary>
    public static bool TryHandleUse(WorldObject gate, Player player)
    {
        if (gate == null || player == null || gate.GetProperty(PropertyBool.AllegianceGauntletGate) != true)
            return false;

        if (!targets.TryGetValue(gate.Guid.Full, out var target))
        {
            Tell(player, DormantLine);
            return true;
        }

        try
        {
            Enter(target, player);
        }
        catch (Exception ex)
        {
            log4net.LogManager.GetLogger(typeof(GauntletGate)).Error($"[GAUNTLET] Gate use by {player.Name} threw", ex);
            Tell(player, ClosedLine);
        }

        return true;
    }

    /// <summary>
    /// Takes <paramref name="player"/> into <paramref name="target"/>'s arena, or refuses with a line: admission, the death
    /// hold, not already in an instance, an entry point ready. On entry the EphemeralRealmExitTo stamp is written first. The
    /// gate's use and <c>/avatar gauntlet enter</c> both come here, so entry never depends on the gate object.
    /// </summary>
    internal static void Enter(IGauntletGateTarget target, Player player)
    {
        if (target == null || player == null)
        {
            if (player != null)
                Tell(player, ClosedLine);
            return;
        }

        if (!target.Admits(player))
        {
            Tell(player, GauntletArenaHold.AdmitsIn(target.State) ? NotMemberLine : ClosedLine);
            return;
        }

        var heldFor = target.DeathHoldSecondsLeft(player);
        if (heldFor > 0)
        {
            Tell(player, $"You have fallen in the arena. You may return in {(int)Math.Ceiling(heldFor)} seconds.");
            return;
        }

        var from = player.Location;
        if (from == null || ACE.Server.Realms.EphemeralInstanceGroup.IsEphemeralInstance(from.Instance))
        {
            Tell(player, InstanceLine);
            return;
        }

        var entry = target.NextEntryPoint();
        if (entry == null)
        {
            Tell(player, NoEntryLine);
            return;
        }

        // The Pinnacle model: the stamp is where the click-only exit and the end of the run send the player back to.
        player.SetPosition(PositionType.EphemeralRealmExitTo, new Position(from));
        Teleporter(player, entry);
    }
}
