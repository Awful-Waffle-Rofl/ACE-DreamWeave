using System;
using System.Collections.Generic;

namespace ACE.Server.Entity
{
    /// <summary>teleport_flush_scope values. Anything else reads as <see cref="Legacy"/>.</summary>
    public enum TeleportFlushScope
    {
        Off = 0,
        Legacy = 1,
        Ledger = 2,
    }

    /// <summary>What Player.Teleport does to the client's known objects, as decided by <see cref="TeleportFlushPolicy.Decide"/>.</summary>
    [Flags]
    public enum TeleportFlushAction
    {
        None = 0,

        /// <summary>FlushKnownObjectsForInstanceChange, exactly as before teleport_flush_scope existed.</summary>
        LegacyFlush = 1,

        /// <summary>The same flush with the own-client deletes queued for paced release (collisions still go at once).</summary>
        DeferredFlush = 2,

        /// <summary>Server-side tracking and replayed-effect bookkeeping cleared on both sides, no client deletes.</summary>
        SilentPurge = 4,

        /// <summary>Every observer deletes the teleporter (the legacy observer half), alongside a silent purge.</summary>
        ObserverDeletes = 8,

        /// <summary>The client object ledger deletes what the client holds from other copies of the destination 3x3.</summary>
        LedgerFire = 16,
    }

    /// <summary>The three teleport flush settings, read once per teleport.</summary>
    public struct TeleportFlushSettings
    {
        public TeleportFlushScope Scope;
        public bool Pacing;
        public int Rate;

        public static TeleportFlushSettings Defaults => new TeleportFlushSettings
        {
            Scope = TeleportFlushPolicy.Normalize(TeleportFlushPolicy.DefaultScope),
            Pacing = TeleportFlushPolicy.DefaultPacing,
            Rate = TeleportFlushPolicy.NormalizeRate(TeleportFlushPolicy.DefaultRate),
        };
    }

    /// <summary>
    /// Pure decisions for the teleport flush switch (teleport_flush_scope, teleport_flush_delete_pacing,
    /// teleport_flush_delete_rate). No WorldObject references and no settings reads, so every arm is testable.
    /// </summary>
    public static class TeleportFlushPolicy
    {
        public const long DefaultScope = 1;
        public const bool DefaultPacing = false;
        public const long DefaultRate = 10;

        public static TeleportFlushScope Normalize(long scope)
        {
            switch (scope)
            {
                case 0: return TeleportFlushScope.Off;
                case 2: return TeleportFlushScope.Ledger;
                default: return TeleportFlushScope.Legacy;
            }
        }

        /// <summary>Deletes released per tick: 0 or less counts as 1.</summary>
        public static int NormalizeRate(long rate)
        {
            if (rate < 1)
                return 1;

            return rate > int.MaxValue ? int.MaxValue : (int)rate;
        }

        public static uint InstanceOf(ulong instancedLandblock) => (uint)(instancedLandblock >> 32);

        public static ushort LandblockOf(ulong instancedLandblock) => (ushort)(instancedLandblock >> 16);

        /// <param name="originInstancedLandblock">Position.InstancedLandblock captured before the relocate</param>
        /// <param name="destinationInstancedLandblock">the validated destination's Position.InstancedLandblock</param>
        public static TeleportFlushAction Decide(TeleportFlushScope scope, bool pacing, ulong originInstancedLandblock, ulong destinationInstancedLandblock)
        {
            // the legacy gate, Player.TeleportRequiresClientObjectFlush: any change of (instance, landblock)
            var landblockChange = originInstancedLandblock != destinationInstancedLandblock;
            var instanceChange = InstanceOf(originInstancedLandblock) != InstanceOf(destinationInstancedLandblock);

            switch (scope)
            {
                case TeleportFlushScope.Off:
                    return instanceChange ? TeleportFlushAction.SilentPurge : TeleportFlushAction.None;

                case TeleportFlushScope.Ledger:
                    // pacing is inert here: every scope-2 delete targets the destination 3x3, which goes up front
                    return (instanceChange ? TeleportFlushAction.SilentPurge | TeleportFlushAction.ObserverDeletes : TeleportFlushAction.None)
                        | TeleportFlushAction.LedgerFire;

                default:
                    if (!landblockChange)
                        return TeleportFlushAction.None;

                    return pacing ? TeleportFlushAction.DeferredFlush : TeleportFlushAction.LegacyFlush;
            }
        }

        /// <summary>
        /// Leftover broadcast links (ObjectMaint.CutResidualObservers) are cut on EVERY scope when the teleport changes
        /// INSTANCE: a link that survives into another instance leaks broadcasts across it, which is a correctness bug,
        /// not part of the client-cull optimisation the scopes trade off. A leftover link inside one instance is valid
        /// (the heartbeat self-heal and the destruction-queue expiry manage it), and cutting it under scope 0 or 2,
        /// which send no observer delete there, would leave a frozen copy on the observer's screen.
        /// </summary>
        public static bool CutsResidualObservers(ulong originInstancedLandblock, ulong destinationInstancedLandblock)
        {
            return InstanceOf(originInstancedLandblock) != InstanceOf(destinationInstancedLandblock);
        }

        /// <summary>
        /// Whether a cut leftover observer that still draws the teleporter is sent a DeleteObject: exactly when this
        /// scope's own flush deletes the teleporter from its observers (the legacy and paced flushes always do, and
        /// observer deletes are never paced; scope 2 does on an instance change; scope 0 never does, it only forgets).
        /// </summary>
        public static bool ResidualObserverDeletes(TeleportFlushScope scope, bool pacing, ulong originInstancedLandblock, ulong destinationInstancedLandblock)
        {
            const TeleportFlushAction observerDeleting = TeleportFlushAction.LegacyFlush | TeleportFlushAction.DeferredFlush | TeleportFlushAction.ObserverDeletes;

            return (Decide(scope, pacing, originInstancedLandblock, destinationInstancedLandblock) & observerDeleting) != 0;
        }

        /// <summary>
        /// The landblock and its 8 neighbours, each coordinate kept within 0..0xFE. The landblock itself is always
        /// first and always included.
        /// </summary>
        public static IReadOnlyList<ushort> Neighbourhood(ushort landblock)
        {
            var x = landblock >> 8;
            var y = landblock & 0xFF;

            var result = new List<ushort>(9) { landblock };

            for (var dx = -1; dx <= 1; dx++)
            {
                for (var dy = -1; dy <= 1; dy++)
                {
                    if (dx == 0 && dy == 0)
                        continue;

                    var nx = x + dx;
                    var ny = y + dy;

                    if (nx < 0 || nx > 0xFE || ny < 0 || ny > 0xFE)
                        continue;

                    result.Add((ushort)(nx << 8 | ny));
                }
            }

            return result;
        }

        /// <summary>
        /// True when an object filed under (<paramref name="landblock"/>, <paramref name="instance"/>) is another copy
        /// of something in the destination 3x3: same landblock id range, different instance. Its delete must reach the
        /// client before the destination's same-guid objects are created, so it is never paced.
        /// </summary>
        public static bool Collides(ushort landblock, uint instance, IReadOnlyList<ushort> destinationNeighbourhood, uint destinationInstance)
        {
            if (instance == destinationInstance)
                return false;

            for (var i = 0; i < destinationNeighbourhood.Count; i++)
            {
                if (destinationNeighbourhood[i] == landblock)
                    return true;
            }

            return false;
        }
    }
}
