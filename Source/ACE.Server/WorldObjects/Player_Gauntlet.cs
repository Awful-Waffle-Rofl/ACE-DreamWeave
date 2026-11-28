using System;
using System.Threading;

using ACE.Entity;
using ACE.Entity.Enum;
using ACE.Server.Avatars.Gauntlet;
using ACE.Server.Entity.Actions;
using ACE.Server.Managers;
using ACE.Server.Network.GameMessages.Messages;
using ACE.Server.Pvp;

namespace ACE.Server.WorldObjects
{
    /// <summary>
    /// Allegiance Gauntlet death hold, the player half (Task 9; the registry and the rules are in
    /// AllegianceAvatar/Gauntlet/GauntletDeathHold.cs). Owner ruling: a member who dies inside a live arena copy is held for
    /// allegiance_gauntlet_death_hold_seconds (60) with no vitae and no item loss; no corpse and keeping every buff are
    /// orchestrator defaults. The death is latched ONCE in Die() at the moment of death, beside the other latches, because
    /// every site that reads it (the vitae, purge, corpse, item-loss and Olthoi gates and the death teleport) runs later,
    /// inside the delayed dieChain, by which time the run may have closed. They read <see cref="GauntletDeathInProgress"/>,
    /// never a live run lookup. The latch is cleared at the end of the death sequence with its siblings.
    ///
    /// The hold itself outlives the death sequence: it is the registry entry, keyed by guid and checked at 1 Hz from
    /// Player_Tick, so a relog while penned is held by the new Player object and an expired hold releases at its first check.
    /// </summary>
    partial class Player
    {
        /// <summary>Latched in Die(): this death happened inside a live Gauntlet arena copy (GauntletManager.ForInstance).</summary>
        private bool gauntletDeathInProgress;

        /// <summary>The one latched read every Gauntlet death-penalty site uses, never a live run lookup.</summary>
        public bool GauntletDeathInProgress => gauntletDeathInProgress;

        /// <summary>The next 1 Hz death hold check (unix seconds). 0 at login, so the first tick checks at once.</summary>
        private double nextGauntletHoldCheckTime;

        /// <summary>
        /// When the arrival shield ends (unix seconds), 0 when none is running. Not persisted. Set and read by the release
        /// follow-up and the 1 Hz check, and cleared by SetLifestoneProtection, LifestoneProtectionDispel and the Gauntlet
        /// death latch. Those are expected to run on the player's own thread, but that is not enforced here, so the field is a
        /// plain double accessed through Volatile: a reader never sees a torn value. It is not a lock: a clear racing a set
        /// can still lose to it.
        /// </summary>
        private double gauntletArrivalShieldUntil;

        /// <summary>The first hold check of this Player object (the login) has looked at a carried-over shield.</summary>
        private bool gauntletLoginShieldChecked;

        /// <summary>Player_Death's SetLifestoneProtection and LifestoneProtectionDispel: an ordinary protection, or none, replaces the shield.</summary>
        internal void ClearGauntletArrivalShieldStamp() => Volatile.Write(ref gauntletArrivalShieldUntil, 0);

        /// <summary>
        /// Die(), at the moment of death: true when the player is inside a live Gauntlet copy, in which case the death is
        /// counted on the run and the hold has begun. Never throws: a fault reads as an ordinary death.
        /// </summary>
        private bool LatchGauntletDeath()
        {
            // A destination left from an earlier death must not steer this one.
            gauntletCachesPending = null;
            gauntletCachesPendingArrived = false;

            try
            {
                var run = LiveGauntletDeathHooks.Instance.LatchDeath(this, Guid.Full, Location?.Instance ?? 0);

                if (run == null)
                    return false;

                // After the fight ended the death goes to the caches (no hold), so the line says that, not "return to the fight".
                var line = run.FightOver
                    ? "You have fallen in the Allegiance Gauntlet, but the fight is over. You keep your items and your enchantments, and you are brought to the caches shortly."
                    : $"You have fallen in the Allegiance Gauntlet. You keep your items and your enchantments, and you return to the fight in {Math.Max(0L, run.DeathHoldSeconds)} seconds.";

                // A death inside the shield's seconds: the death arrival's own protection must not be cut by the old expiry.
                ClearGauntletArrivalShieldStamp();

                Session?.Network.EnqueueSend(new GameMessageSystemChat(line, ChatMessageType.Broadcast));

                return true;
            }
            catch (Exception ex)
            {
                log.Error($"[GAUNTLET] the death latch threw for {Name}", ex);
                return false;
            }
        }

        /// <summary>ThreadSafeTeleportOnDeath: the pen of the run this player is held in, or null (no hold, or the run ended).</summary>
        private Position GauntletDeathPen()
        {
            try
            {
                return LiveGauntletDeathHooks.Instance.PenFor(Guid.Full);
            }
            catch (Exception ex)
            {
                log.Error($"[GAUNTLET] the death pen lookup threw for {Name}", ex);
                return null;
            }
        }

        /// <summary>
        /// Player_Tick, the player's own landblock thread, at most once a second. Returns after one volatile read while nobody
        /// on the server is held. Skipped while dead, dying, teleporting or before the login has completed; the next second
        /// decides then.
        /// </summary>
        private void GauntletHoldTick(double currentUnixTime)
        {
            if (nextGauntletHoldCheckTime > currentUnixTime)
                return;

            nextGauntletHoldCheckTime = currentUnixTime + 1.0;

            // A shield carried over a relog: its stamp is not persisted, but the protection is. Only once the login has
            // finished and no teleport is running, so the fresh 5 s does not run out while the client is still loading.
            if (!gauntletLoginShieldChecked && FirstEnterWorldDone && !Teleporting)
                GauntletLoginShieldCheck(currentUnixTime, InGauntletForShield(), CachedHeartbeatInterval);

            // The arrival shield's own expiry, at this 1 Hz check: 5 to 6 s, whatever the heartbeat.
            GauntletArrivalShieldExpiry(currentUnixTime);

            var hooks = LiveGauntletDeathHooks.Instance;

            if (hooks.Holds.Count == 0)
                return;

            try
            {
                var busy = IsDead || IsInDeathProcess || Teleporting || !FirstEnterWorldDone;
                hooks.TickPlayer(this, Guid.Full, Location, busy);
            }
            catch (Exception ex)
            {
                log.Error($"[GAUNTLET] the death hold check threw for {Name}", ex);
            }
        }

        /// <summary>A held player walked into the fight circle: back to the pen, told how long is left.</summary>
        internal void GauntletSnapBackToPen(Position pen, int secondsLeft)
        {
            Session?.Network.EnqueueSend(new GameMessageSystemChat(
                $"You have fallen and cannot rejoin the fight yet. You return in {secondsLeft} seconds.", ChatMessageType.Broadcast));

            WorldManager.ThreadSafeTeleport(this, pen);
        }

        /// <summary>
        /// The hold is over (the Battlegrounds respawn steps, Docs/Pvp/BATTLEGROUNDS.md "Respawn" 6): full health, stamina
        /// and mana, then the teleport to <paramref name="entry"/> (the run's next entry point; null leaves the player where
        /// they stand), and the arrival shield once there. The shield is set in the teleport's follow-up, as the death
        /// arrival's protection is (Player_Death, ThreadSafeTeleportOnDeath), because Player.Teleport dispels lifestone
        /// protection. The player's own queue.
        /// </summary>
        internal void ReleaseFromGauntletHold(Position entry)
        {
            // Full vitals. Player.UpdateVital sends each change to the client itself.
            PvpPlayerRules.RestoreRespawnVitals(v => Vitals[v].MaxValue, (v, max) => UpdateVital(Vitals[v], max));

            DamageHistory.Reset();

            Session?.Network.EnqueueSend(new GameMessageSystemChat(LiveGauntletDeathHooks.ReleaseLine, ChatMessageType.Broadcast));

            if (entry != null)
            {
                WorldManager.ThreadSafeTeleport(this, entry, new ActionEventDelegate(() =>
                {
                    SetGauntletArrivalShield(ACE.Common.Time.GetUnixTime());
                    SendWeenieError(WeenieError.ITeleported);
                }));
            }
            else
            {
                SetGauntletArrivalShield(ACE.Common.Time.GetUnixTime());
            }
        }

        /// <summary>
        /// Where a player who was still in the death animation or the pen-arrival chain when the fight ended goes once that chain
        /// is done (null: none). Read by ThreadSafeTeleportOnDeath, which sends them there instead of the pen, and by the arrival
        /// chain's last step, which then restores their vitals (the chain's own 75% write would otherwise stand).
        /// </summary>
        private Position gauntletCachesPending;

        /// <summary>True once ThreadSafeTeleportOnDeath has already sent the player to <see cref="gauntletCachesPending"/>.</summary>
        private bool gauntletCachesPendingArrived;

        /// <summary>The death latch, after the fight ended: records where the death chain sends this player (the caches landing).</summary>
        internal void SetGauntletCachesPending(Position caches) => gauntletCachesPending = caches;

        /// <summary>Test seam: the destination waiting for the death chain, or null.</summary>
        internal Position GauntletCachesPending => gauntletCachesPending;

        /// <summary>ThreadSafeTeleportOnDeath: the pending cache destination (kept, so the chain's last step still releases), or null.</summary>
        internal Position PeekGauntletCachesPending()
        {
            var at = gauntletCachesPending;

            if (at == null)
                return null;

            // The run may have closed since the destination was recorded: its copy is going, so the ordinary exit branch runs.
            if (!GauntletCachesPendingStillValid(at))
            {
                gauntletCachesPending = null;
                gauntletCachesPendingArrived = false;
                return null;
            }

            gauntletCachesPendingArrived = true;
            return new Position(at);
        }

        /// <summary>True while the run whose copy <paramref name="at"/> is in still exists and has not closed.</summary>
        private static bool GauntletCachesPendingStillValid(Position at)
        {
            try
            {
                var run = LiveGauntletDeathHooks.RunForInstance(at.Instance);
                return run != null && !run.IsTerminal;
            }
            catch (Exception ex)
            {
                log.Error("[GAUNTLET] the pending cache destination's run lookup threw", ex);
                return false;
            }
        }

        /// <summary>The death chain's last step: a still-pending cache release is carried out now (vitals, shield; no second teleport when already there).</summary>
        private void CompleteGauntletCachesPending()
        {
            var at = gauntletCachesPending;

            if (at == null)
                return;

            var arrived = gauntletCachesPendingArrived;
            gauntletCachesPending = null;
            gauntletCachesPendingArrived = false;

            if (!GauntletCachesPendingStillValid(at))
                return;   // the run closed mid-chain: no false "brought to the caches" line

            ReleaseFromGauntletHoldAtCaches(at, teleport: !arrived);
        }

        /// <summary>
        /// The run stopped fighting (Rewarding began) and this player was still held: the same release as
        /// <see cref="ReleaseFromGauntletHold"/> (full vitals, damage history reset, the arrival shield once there), but with the
        /// end-of-fight line and the teleport to <paramref name="caches"/>, the cache area. The player's own queue. A player still
        /// dying (the death animation, or the 3 s pen-arrival chain) only has the destination recorded: restoring health now would
        /// read as alive in the middle of the death chain, which would then send them out of the arena, and its arrival write
        /// would overwrite the vitals. The chain finishes the release (<see cref="CompleteGauntletCachesPending"/>).
        /// </summary>
        internal void ReleaseFromGauntletHoldAtCaches(Position caches, bool teleport = true)
        {
            if (IsInDeathProcess || IsDead)
            {
                gauntletCachesPending = caches;
                return;
            }

            PvpPlayerRules.RestoreRespawnVitals(v => Vitals[v].MaxValue, (v, max) => UpdateVital(Vitals[v], max));

            DamageHistory.Reset();

            Session?.Network.EnqueueSend(new GameMessageSystemChat(GauntletCachesReleaseLine, ChatMessageType.Broadcast));

            if (caches != null && teleport)
            {
                WorldManager.ThreadSafeTeleport(this, caches, new ActionEventDelegate(() =>
                {
                    SetGauntletArrivalShield(ACE.Common.Time.GetUnixTime());
                    SendWeenieError(WeenieError.ITeleported);
                }));
            }
            else
            {
                SetGauntletArrivalShield(ACE.Common.Time.GetUnixTime());
            }
        }

        internal const string GauntletCachesReleaseLine = "The fight is over. You are brought to the caches.";

        /// <summary>Seconds of lifestone protection a released player arrives with.</summary>
        public const double GauntletArrivalShieldSeconds = 5.0;

        /// <summary>
        /// The lifestone protection a released player arrives with, so a member landing beside a portal is not killed on
        /// arrival but cannot stand protected for long. The heartbeat cannot time it: LifestoneProtectionTick steps by the
        /// heartbeat interval (5 s by default, WorldObject_Tick.InitializeHeartbeats), so a 5 s remainder could end on the
        /// next heartbeat, anywhere from 0 to 5 s later. The protection is set as a full one (timestamp 0) and ended by its
        /// own stamp at the 1 Hz hold check (<see cref="GauntletArrivalShieldExpiry"/>): 5 to 6 s. Attacking ends it early,
        /// as it does any lifestone protection. Split out so a test can pin it on a seeded Player.
        /// </summary>
        internal void SetGauntletArrivalShield(double nowUnix)
        {
            UnderLifestoneProtection = true;
            LifestoneProtectionTimestamp = 0;
            Volatile.Write(ref gauntletArrivalShieldUntil, nowUnix + GauntletArrivalShieldSeconds);
        }

        /// <summary>True when the player stands in a live Gauntlet copy or holds a Gauntlet death hold. Never throws.</summary>
        private bool InGauntletForShield()
        {
            try
            {
                var instance = Location?.Instance ?? 0;

                return (instance != 0 && LiveGauntletDeathHooks.RunForInstance(instance) != null)
                    || LiveGauntletDeathHooks.Instance.Holds.RunOf(Guid.Full) != null;
            }
            catch (Exception ex)
            {
                log.Error($"[GAUNTLET] the login shield lookup threw for {Name}", ex);
                return false;
            }
        }

        /// <summary>The heartbeat a seeded or unticked Player reports as 0; WorldObject_Tick.InitializeHeartbeats' default.</summary>
        internal const double GauntletDefaultHeartbeatSeconds = 5.0;

        /// <summary>
        /// The most a shield's LifestoneProtectionTimestamp can have reached before a relog: it starts at 0 and the heartbeat
        /// (LifestoneProtectionTick) adds one interval per beat, and the shield lives at most 6 s (5 s plus the 1 Hz check), so
        /// it sees at most two beats. A timestamp above this is an ordinary protection already well under way.
        /// </summary>
        internal static double GauntletRelogShieldTimestampBound(double heartbeatSeconds)
            => GauntletArrivalShieldSeconds + 2 * (heartbeatSeconds > 0 ? heartbeatSeconds : GauntletDefaultHeartbeatSeconds);

        /// <summary>
        /// The first hold check after login: a protection that is still young (timestamp at most
        /// <see cref="GauntletRelogShieldTimestampBound"/>) carried in by a player in a Gauntlet copy or hold is the arrival
        /// shield of a relog mid-shield, whose stamp was lost, so it gets a fresh 5 s instead of the rest of a minute. Once per
        /// Player object. The player's own thread.
        /// </summary>
        internal void GauntletLoginShieldCheck(double nowUnix, bool inGauntlet, double heartbeatSeconds)
        {
            if (gauntletLoginShieldChecked)
                return;

            gauntletLoginShieldChecked = true;

            var ts = LifestoneProtectionTimestamp;

            if (inGauntlet && UnderLifestoneProtection && ts is double t && t >= 0 && t <= GauntletRelogShieldTimestampBound(heartbeatSeconds)
                && Volatile.Read(ref gauntletArrivalShieldUntil) == 0)
                Volatile.Write(ref gauntletArrivalShieldUntil, nowUnix + GauntletArrivalShieldSeconds);
        }

        /// <summary>Ends the arrival shield once its seconds are up. True when it ended one. The player's own thread.</summary>
        internal bool GauntletArrivalShieldExpiry(double nowUnix)
        {
            var until = Volatile.Read(ref gauntletArrivalShieldUntil);

            if (until == 0)
                return false;

            // Already ended (an attack dispelled it): drop the stamp, so it can never end a later protection.
            if (!UnderLifestoneProtection)
            {
                Volatile.Write(ref gauntletArrivalShieldUntil, 0);
                return false;
            }

            if (nowUnix < until)
                return false;

            Volatile.Write(ref gauntletArrivalShieldUntil, 0);

            UnderLifestoneProtection = false;
            LifestoneProtectionTimestamp = null;

            Session?.Network.EnqueueSend(new GameMessageSystemChat("You're no longer protected by the Lifestone's magic!", ChatMessageType.Magic));
            return true;
        }
    }
}
