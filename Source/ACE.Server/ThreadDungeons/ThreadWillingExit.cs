using System;
using System.Collections.Concurrent;

using ACE.Server.WorldObjects;

namespace ACE.Server.ThreadDungeons
{
    /// <summary>
    /// A solo owner's WILLING exit from their cleared Thread (owner ruling 2026-10-10). A solo gem survives the clear,
    /// a death, a logout and an admin teleport; it is retired only when its owner leaves the cleared copy of their own
    /// accord - any recall (commands and spells), the exit portal or any other portal, a portal gem, /exitinstance.
    /// Those are exactly the voluntary exits that already route through <see cref="ThreadExitGuard.TryHoldExit"/>.
    ///
    /// Two halves, because the guard is not the moment the player leaves:
    ///   * ARM. TryHoldExit, on its not-held return path, arms a marker for a solo owner: the instance they are
    ///     standing in and the time. Any run state is armed, so a run that clears during a recall animation still
    ///     counts once the teleport lands.
    ///   * CONSUME. Player.Teleport takes the marker on every cross-instance teleport and, when
    ///     <see cref="ShouldRetire"/> holds, retires the owner gem (ThreadDungeonManager.ConsumeRetiredOwnerGem).
    ///
    /// The gem is not destroyed inside the guard because a recall can still abort after it (a house or lifestone
    /// recall refuses with "You have moved too far" at the end of its animation) or be overtaken by a death. A
    /// death clears the marker (Player.Die), so the death's own teleport, or a recall teleport landing after the
    /// death, never retires the gem. A marker older than <see cref="MaxArmAge"/> is ignored, which bounds how long an
    /// aborted recall's marker can linger; a forced cross-instance teleport inside that window (an admin teleport just
    /// after an aborted recall) is the accepted residue. The same residue applies to portals: a portal arms in
    /// Portal.CheckUseRequirements (the guard call there), but Portal.ActOnUse can still refuse the use afterwards,
    /// so a forced cross-instance move within MaxArmAge of such a refusal also retires the gem. Accepted, like the
    /// aborted recall.
    ///
    /// Threading: one ConcurrentDictionary keyed by player guid, the same atomic pattern as ThreadExitGuard's bypass.
    /// Arm is a single indexer write, every consume is a single TryRemove, so one marker is taken at most once.
    /// </summary>
    public static class ThreadWillingExit
    {
        /// <summary>
        /// How long an armed marker stays valid. Above the longest guarded exit's delay between the guard and its
        /// teleport: the recall animations (house, lifestone, marketplace, allegiance hometown, mansion, PK arenas)
        /// are read from the motion table at run time and are a matter of seconds; a portal's and a spell's teleport
        /// follows the guard within the same use. 60 seconds clears all of them with a wide margin.
        /// </summary>
        internal static readonly TimeSpan MaxArmAge = TimeSpan.FromSeconds(60);

        private static readonly ConcurrentDictionary<uint, (uint Instance, DateTime ArmedUtc)> armed = new ConcurrentDictionary<uint, (uint, DateTime)>();

        /// <summary>
        /// Arms the marker when <paramref name="playerGuid"/> is the owner of SOLO <paramref name="run"/>. Called by
        /// ThreadExitGuard.TryHoldExit on its not-held path only. <paramref name="standingInstance"/> is where the player
        /// is standing now, which the consume compares with the instance the teleport leaves.
        /// </summary>
        internal static void ArmIfSoloOwner(ThreadDungeonRun run, uint playerGuid, uint standingInstance, DateTime nowUtc)
        {
            if (!ShouldArm(run != null, run?.IsGroup ?? false, run?.OwnerGuid ?? 0, playerGuid))
                return;

            armed[playerGuid] = (standingInstance, nowUtc);
        }

        /// <summary>The arm half's rule, pure: only a solo run's owner is armed.</summary>
        internal static bool ShouldArm(bool runFound, bool isGroup, uint ownerGuid, uint playerGuid)
            => runFound && !isGroup && ownerGuid == playerGuid;

        /// <summary>Drops any marker. Called from Player.Die: a death is never a willing exit.</summary>
        internal static void Disarm(uint playerGuid) => armed.TryRemove(playerGuid, out _);

        /// <summary>Test seam: is a marker present (consumes nothing).</summary>
        internal static bool IsArmed(uint playerGuid) => armed.ContainsKey(playerGuid);

        /// <summary>
        /// Takes the marker (always removing it) and answers whether this teleport retires the owner gem. Pure apart
        /// from the removal; <paramref name="runAt"/> resolves the run at an instance (ThreadDungeonManager.GetRun in
        /// production). Returns the run to retire, or null.
        /// </summary>
        internal static ThreadDungeonRun TakeForTeleport(uint playerGuid, uint fromInstance, uint toInstance, DateTime nowUtc, Func<uint, ThreadDungeonRun> runAt)
        {
            if (fromInstance == toInstance)
                return null;

            if (!armed.TryRemove(playerGuid, out var mark))
                return null;

            var run = runAt?.Invoke(fromInstance);

            return ShouldRetire(true, mark.Instance, mark.ArmedUtc, nowUtc, fromInstance, toInstance,
                run != null, run?.IsGroup ?? false, run?.State ?? ThreadDungeonRunState.Ended, run?.OwnerGuid == playerGuid)
                ? run
                : null;
        }

        /// <summary>
        /// The consume half's rule, pure. The owner gem is retired when ALL of these hold:
        ///   * a marker was armed (<paramref name="armedFound"/>), for the instance being left, no older than
        ///     <see cref="MaxArmAge"/>;
        ///   * the teleport actually changes instance;
        ///   * the run at the instance being left is a SOLO run in the Cleared state, owned by the player.
        /// An Active run keeps its gem (an exit before the clear spends nothing extra); a group run's gem is never
        /// retired here (it dies at run end, ruling R29).
        /// </summary>
        internal static bool ShouldRetire(bool armedFound, uint armedInstance, DateTime armedUtc, DateTime nowUtc,
            uint fromInstance, uint toInstance, bool runFound, bool isGroup, ThreadDungeonRunState state, bool isOwner)
        {
            if (!armedFound || fromInstance == toInstance || armedInstance != fromInstance)
                return false;

            var age = nowUtc - armedUtc;
            if (age < TimeSpan.Zero || age > MaxArmAge)
                return false;

            return runFound && !isGroup && state == ThreadDungeonRunState.Cleared && isOwner;
        }

        /// <summary>
        /// The Player.Teleport hook. Runs in the teleporting player's own context, after the fog-colour re-queue and
        /// before the teleport starts. Only a cross-instance teleport touches the marker.
        /// </summary>
        public static void OnTeleport(Player player, uint fromInstance, uint toInstance)
        {
            if (player == null || fromInstance == toInstance)
                return;

            var run = TakeForTeleport(player.Guid.Full, fromInstance, toInstance, DateTime.UtcNow, ThreadDungeonManager.GetRun);

            if (run != null)
                ThreadDungeonManager.ConsumeRetiredOwnerGem(player, run);
        }
    }
}
