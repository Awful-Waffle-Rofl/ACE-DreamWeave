using System.Collections.Generic;

using ACE.Entity.Enum;
using ACE.Server.Entity;
using ACE.Server.Network.GameMessages.Messages;
using ACE.Server.ThreadDungeons;

namespace ACE.Server.WorldObjects
{
    /// <summary>
    /// Open World Threads WP3b, R12: a player standing past the circle cannot damage an outdoor run's creature. Hooked
    /// into <see cref="CheckPKStatusVsTarget"/>, the one gate every player melee hit, missile hit, harmful spell (cast
    /// time for a non-projectile spell, impact for a projectile) and class-ability strike already asks, so a single check
    /// covers every damage path that targets a creature.
    /// </summary>
    public partial class Player
    {
        /// <summary>
        /// DateTime.UtcNow.Ticks stamped where <c>Teleporting = false</c> is set (Player_Location, OnTeleportComplete), i.e. at
        /// ARRIVAL. LastTeleportTime is stamped at teleport START, so it cannot serve the outdoor perimeter's arrival grace
        /// (Open World Threads R19). Snap-backs are teleports, so they stamp it too. 0 = never arrived.
        /// </summary>
        public long LastArrivalTicks;

        /// <summary>Timers.RunningTime of the last R12 refusal line this player was sent (rate limit, runtime only).</summary>
        private double lastOutdoorStrikeNoticeAt = double.NegativeInfinity;

        /// <summary>
        /// True (with <paramref name="result"/> set) when <paramref name="targetCreature"/> is an outdoor run creature and this
        /// player stands past its circle: the hit is refused, with one throttled chat line. The result is the silent refusal
        /// sentinel (PvpArenaHookSettings.SpawnProtectedRefusal), whose contract every caller already honours: "refused, and
        /// the refusing gate has already said what it needed to; report nothing more". False for every other target.
        /// </summary>
        internal bool TryOutdoorPerimeterDamageGate(Creature targetCreature, out List<WeenieErrorWithString> result)
        {
            result = null;

            var perimeter = targetCreature?.P_OutdoorPerimeter;
            if (perimeter == null || !perimeter.ShouldRefuseDamage(true, Location))
                return false;

            NotifyOutdoorStrikeRefused();

            result = ACE.Server.Pvp.PvpArenaHookSettings.SpawnProtectedRefusal;
            return true;
        }

        /// <summary>
        /// R12's chat line, at most once per OutdoorCreaturePerimeter.RefusedStrikeNoticeIntervalS for this player, whichever
        /// path refused the damage (the attack gate above, a proc, a DoT tick, a projectile or a pet).
        /// </summary>
        internal void NotifyOutdoorStrikeRefused()
        {
            var now = Timers.RunningTime;
            if (Session == null || !OutdoorCreaturePerimeter.RefusedStrikeNoticeDue(now, lastOutdoorStrikeNoticeAt))
                return;

            lastOutdoorStrikeNoticeAt = now;
            Session.Network.EnqueueSend(new GameMessageSystemChat(OutdoorCreaturePerimeter.RefusedStrikeText, ChatMessageType.Broadcast));
        }
    }
}
