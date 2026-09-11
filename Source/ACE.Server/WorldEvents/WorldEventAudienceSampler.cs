using System;
using System.Collections.Generic;
using System.Reflection;

using ACE.Entity;
using ACE.Entity.Enum;
using ACE.Server.Managers;

using log4net;

namespace ACE.Server.WorldEvents
{
    /// <summary>
    /// The one non-pure piece of the audience estimate: it walks the online player list. Isolated here so
    /// <see cref="WorldEventRosterSelector.EstimateFromLevels"/> stays pure and so a unit test can inject a
    /// fixed estimate instead (D6 - no test may stand up a live Player).
    ///
    /// Runs on the world thread, from WorldEvent.Stage.
    /// </summary>
    public static class WorldEventAudienceSampler
    {
        private static readonly ILog log = LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);

        /// <summary>
        /// Whether one online character counts toward the SCALING audience (D6 - pure, so it is unit
        /// testable without a live Player). Everything from <see cref="AccessLevel.Sentinel"/> up is staff
        /// and is excluded: an admin or a developer standing in a run to watch it must not make the run
        /// harder for the players who are actually fighting. <see cref="AccessLevel.Player"/> and
        /// <see cref="AccessLevel.Advocate"/> do count - an advocate is a player with a support flag, not
        /// staff. A null level (no session on the character) counts as a player, which is the safe reading:
        /// the exclusion is for known staff, never for anything merely unknown.
        ///
        /// Deliberately NOT keyed off <c>Player.IsAdmin</c> and friends: those are set only when
        /// <c>Server.Accounts.OverrideCharacterPermissions</c> is on, and only for exactly
        /// <see cref="AccessLevel.Admin"/> (Player.cs, the OverrideCharacterPermissions block), so a
        /// developer or sentinel would slip through and the whole rule would depend on a config toggle.
        /// It is also independent of cloak / admin-vision state on purpose - an exclusion a toggle can undo
        /// is not an exclusion.
        ///
        /// This is a SCALING rule only. It has no bearing on participation credit, reward radius or the
        /// reward ledger, none of which read this.
        /// </summary>
        public static bool CountsTowardAudience(AccessLevel? level)
        {
            return level == null || level.Value < AccessLevel.Sentinel;
        }

        /// <summary>
        /// Levels of every online player within <paramref name="radius"/> of the anchor, in the SAME
        /// landblock instance (a realm copy of a landblock is a different place with the same geometry, so
        /// a raw distance comparison across instances would count players who are not there).
        /// </summary>
        public static AudienceEstimate Sample(Position anchor, float radius)
        {
            if (anchor == null || radius <= 0)
                return new AudienceEstimate(0, 0, 0);

            var levels = new List<int>();

            try
            {
                foreach (var player in PlayerManager.GetAllOnline())
                {
                    var loc = player?.Location;

                    if (loc == null || loc.Instance != anchor.Instance)
                        continue;

                    if (loc.DistanceTo(anchor) > radius)
                        continue;

                    if (!CountsTowardAudience(player.Session?.AccessLevel))
                        continue;

                    levels.Add(player.Level ?? 1);
                }
            }
            catch (Exception ex)
            {
                log.Error("[WORLDEVENT] audience sample failed; treating the audience as empty", ex);
                return new AudienceEstimate(0, 0, 0);
            }

            return WorldEventRosterSelector.EstimateFromLevels(levels);
        }

        /// <summary>
        /// Count of online, living, non-staff players within <paramref name="radius"/> of the anchor, in the
        /// SAME landblock instance - same walk as <see cref="Sample"/>, plus a dead-player exclusion (the
        /// same guard <see cref="WorldEventParticipation.AliveNear"/> uses): a field of corpses is a wipe,
        /// not a presence. Staff exclusion is preserved for the same reason it exists in <see cref="Sample"/>
        /// - an admin watching a run must not be able to hold it open.
        ///
        /// This is a PRESENCE rule, used to decide whether an abandon/wipe verdict should fire, and that is
        /// exactly why its failure mode differs from <see cref="Sample"/>'s: <see cref="Sample"/> failing to
        /// zero is safe because a zero audience only ever makes a run EASIER (fewer/weaker waves), but a
        /// presence count failing to zero would make a run that is actually still being played look
        /// abandoned. So on exception this returns <see cref="int.MaxValue"/> instead of 0 - a broken scan
        /// must hold the run open, never end it.
        /// </summary>
        public static int CountAliveNear(Position anchor, float radius)
        {
            if (anchor == null || radius <= 0)
                return 0;

            var count = 0;

            try
            {
                foreach (var player in PlayerManager.GetAllOnline())
                {
                    var loc = player?.Location;

                    if (loc == null || loc.Instance != anchor.Instance)
                        continue;

                    if (loc.DistanceTo(anchor) > radius)
                        continue;

                    if (!CountsTowardAudience(player.Session?.AccessLevel))
                        continue;

                    if (player.IsDead)
                        continue;

                    count++;
                }
            }
            catch (Exception ex)
            {
                log.Error("[WORLDEVENT] presence scan failed; holding the run open rather than reporting empty", ex);
                return int.MaxValue;
            }

            return count;
        }

        /// <summary>
        /// The highest Health.MaxValue among the same players <see cref="Sample"/> would have counted -
        /// same anchor, same radius, same landblock instance, same staff exclusion (TECH-DESIGN 2.16). This
        /// is the "top player" the boss's damage is sized against, and it is deliberately MAX rather than
        /// median or mean: the requirement is that the toughest player present survives hitsToKill ordinary
        /// hits, and everyone softer than that is expected to die faster.
        ///
        /// Health.MaxValue rather than level, because the target is a number of HITS to kill and hits
        /// remove health, not levels. It also picks up buffs, augmentations and gear the level does not.
        ///
        /// 0 means "nobody countable is in range", which the caller treats as "keep the last known value",
        /// never as "size the boss for a zero-health player". Kept a separate walk from <see cref="Sample"/>
        /// rather than folded into <see cref="AudienceEstimate"/>: the two run on completely different
        /// cadences (this one only once the damage controller already has a full sample window), and
        /// widening the estimate struct would have made every wave pick pay for a field only the boss
        /// damage decision reads.
        /// </summary>
        public static uint SampleTopMaxHealth(Position anchor, float radius)
        {
            if (anchor == null || radius <= 0)
                return 0;

            var top = 0u;

            try
            {
                foreach (var player in PlayerManager.GetAllOnline())
                {
                    var loc = player?.Location;

                    if (loc == null || loc.Instance != anchor.Instance)
                        continue;

                    if (loc.DistanceTo(anchor) > radius)
                        continue;

                    if (!CountsTowardAudience(player.Session?.AccessLevel))
                        continue;

                    var max = player.Health?.MaxValue ?? 0;

                    if (max > top)
                        top = max;
                }
            }
            catch (Exception ex)
            {
                log.Error("[WORLDEVENT] top max health sample failed; treating the audience as empty", ex);
                return 0;
            }

            return top;
        }
    }
}
