using System;
using System.Reflection;

using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Server.Network.GameMessages.Messages;
using ACE.Server.WorldObjects;

using log4net;

namespace ACE.Server.MlDigsite
{
    /// <summary>
    /// The use hook for the Boss Rush interrupt object - the drum a player runs to and sounds to cancel the
    /// boss's announced big hit.
    ///
    /// ONE LINE IN GenericObject.ActOnUse, in the same opt-in shape as the three hooks already there (the
    /// class-ability trainer pedestal, the Weave Cache and the Tinkerer's Inspiration pedestal). The cost this
    /// imposes on every other Generic object on the server is ONE dictionary miss: the first thing
    /// <see cref="TryHandleUse"/> does is read PropertyInt.MlDigsiteEncounterId and return false when it is
    /// absent, which it is for every object that is not something a digsite encounter placed.
    ///
    /// NO NEW FORK PROPERTY ID WAS NEEDED. The hook keys on MlDigsiteEncounterId, which MlDigsiteProps already
    /// stamps on every prop for the persistence exclusion and the orphan sweep, so nothing had to be reserved
    /// in Source/property-registry.tsv.
    ///
    /// IDENTITY IS CHECKED THREE WAYS before anything happens: the stamp names a live encounter, that
    /// encounter still holds this guid (MlDigsiteEncounter.IsHeld), and the driver's open window is pointed at
    /// THIS object. The third is what stops a drum left over from a previous window - or one a player somehow
    /// reached from another encounter - cancelling a hit it has nothing to do with.
    /// </summary>
    public static class MlDigsiteInterruptObject
    {
        private static readonly ILog log = LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);

        /// <summary>
        /// Handles a use of a digsite-placed object. Returns TRUE for anything this encounter placed, whether
        /// or not the use did something - a digsite prop must never fall through to ordinary Generic use
        /// behaviour, and a player who ran thirty paces deserves to be told why nothing happened.
        ///
        /// Runs on the landblock thread that handled the use. It records and schedules; it applies nothing.
        /// </summary>
        public static bool TryHandleUse(WorldObject wo, Player player)
        {
            if (wo == null || player == null)
                return false;

            // THE HOT PATH for every Generic object in the world: one property read, then out.
            var stamp = wo.GetProperty(PropertyInt.MlDigsiteEncounterId);

            if (stamp == null)
                return false;

            try
            {
                var encounter = MlDigsiteManager.FindLiveEncounter((uint)stamp.Value);

                if (encounter == null || !encounter.IsHeld(wo.Guid.Full))
                {
                    // A prop whose encounter has already ended. It is on its way out through the cleanup, and
                    // whatever it was for is over.
                    LogRejected(wo, player, stamp.Value, encounter == null ? "encounter-not-live" : "not-held-by-encounter");
                    Tell(player, "The drum is dead under your hand. Whatever it was for is finished.");
                    return true;
                }

                var pending = encounter.BossMechanics?.InterruptObject;

                if (pending == null || pending.Guid.Full != wo.Guid.Full)
                {
                    LogRejected(wo, player, stamp.Value, pending == null ? "no-open-window" : $"stale-drum (open window is 0x{pending.Guid.Full:X8})");
                    Tell(player, "The drum is quiet. There is nothing to answer right now.");
                    return true;
                }

                if (!MlDigsiteBossMechanics.TryInterrupt(encounter, player))
                {
                    LogRejected(wo, player, stamp.Value, "window-already-closed");
                    Tell(player, "The drum is quiet. There is nothing to answer right now.");
                }

                return true;
            }
            catch (Exception ex)
            {
                // A use handler must never throw into the landblock tick, and a failure here must cost the
                // interrupt, never the player's session.
                log.Error($"[ML_DIGSITE] interrupt use threw for 0x{wo.Guid.Full:X8} by {player.Name}", ex);
                return true;
            }
        }

        /// <summary>
        /// Round 17: every use that reaches this hook and does NOT cancel the hit is logged with its reason, so
        /// "somebody tried and it refused them" is distinguishable in the log from "nobody tried" - a stage
        /// window expired unanswered and the log could not say which. A successful use is already logged by
        /// InterruptObjectMechanic.TryUse ("interrupt answered by").
        ///
        /// LIMIT, stated so it is not over-read: this only sees a use the SERVER accepted and dispatched to
        /// GenericObject.ActOnUse. A double-click the client refuses locally (the ItemUseable trap in the
        /// weenie header) or a move-to that never completes never reaches here, so it cannot be logged here.
        /// </summary>
        private static void LogRejected(WorldObject wo, Player player, int stamp, string reason)
            => log.Info($"[ML_DIGSITE] digsite={stamp} interrupt use REJECTED for {player.Name} on 0x{wo.Guid.Full:X8} (wcid {wo.WeenieClassId}): {reason}");

        private static void Tell(Player player, string line)
            => player.Session?.Network.EnqueueSend(new GameMessageSystemChat(line, ChatMessageType.Broadcast));
    }
}
