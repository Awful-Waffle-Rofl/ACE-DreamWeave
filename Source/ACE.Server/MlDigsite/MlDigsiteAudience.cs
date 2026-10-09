using System;
using System.Collections.Generic;
using System.Reflection;

using ACE.Entity;
using ACE.Entity.Enum;
using ACE.Server.Managers;
using ACE.Server.WorldObjects;

using log4net;

namespace ACE.Server.MlDigsite
{
    /// <summary>
    /// Who is at a digsite. THE single presence predicate for the whole system (invariant 5): the same scan
    /// decides how big a wave is, who hears a band announcement, whether a group has wiped, and whose
    /// presence the reap stamps for group rewards. It does NOT by itself decide who is paid: a player must
    /// also have dealt damage credit (MlDigsiteRules.SelectEligible), so a passer-by standing in the radius
    /// makes the fight bigger but is not paid for it.
    ///
    /// A digsite sits on a SHARED, PERSISTENT outdoor landblock in the live world, so the instance test is
    /// not optional: a realm copy of the same landblock is a different place with the same geometry, and a
    /// raw distance comparison across instances would count players who are not there. This mirrors
    /// WorldEventAudienceSampler's walk, but NOT its staff rule: see <see cref="CountsAtDigsite"/>.
    ///
    /// The two failure modes are deliberately ASYMMETRIC, for the reason WorldEventAudienceSampler
    /// documents: a scan that fails to empty only ever makes an encounter EASIER and pay LESS, but a
    /// presence scan that fails to empty would end a fight people are still having. So
    /// <see cref="Participants"/> degrades to nobody and <see cref="AnyPresent"/> degrades to "yes".
    /// </summary>
    public static class MlDigsiteAudience
    {
        private static readonly ILog log = LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);

        /// <summary>
        /// Online, alive players - staff included - in the same landblock instance as the anchor and within
        /// <paramref name="radius"/> metres of it. See <see cref="CountsAtDigsite"/> for why staff count here
        /// when they do not count toward a world event.
        ///
        /// On exception this returns an EMPTY list. That is the safe direction here and only here - an empty
        /// audience scales a wave down and skips one presence stamp, both of which are recoverable; see the
        /// class remarks.
        /// </summary>
        public static List<Player> Participants(Position anchor, float radius)
        {
            var found = new List<Player>();

            if (anchor == null || radius <= 0)
                return found;

            try
            {
                foreach (var player in PlayerManager.GetAllOnline())
                {
                    if (!Counts(player, anchor, radius))
                        continue;

                    found.Add(player);
                }
            }
            catch (Exception ex)
            {
                log.Error("[ML_DIGSITE] participant scan failed; treating the digsite as empty", ex);
                return new List<Player>();
            }

            return found;
        }

        /// <summary>
        /// How many players the difficulty curve should size against. Same scan as
        /// <see cref="Participants"/> (it is <see cref="Sample"/>'s count, and Sample walks the same
        /// <see cref="Counts"/> predicate), so a wave is never sized against a different set of people from the
        /// one that gets paid for clearing it.
        /// </summary>
        public static int Count(Position anchor, float radius) => Sample(anchor, radius).Count;

        /// <summary>
        /// RoZ round 19 level-spread scaling: one <see cref="MlMapEventProfile"/> per player the presence rule
        /// counts at <paramref name="anchor"/> - the SAME <see cref="Counts"/> predicate every other scan here
        /// uses, so the people a creature is sized for are exactly the people who make the fight bigger and get
        /// paid for it. Staff count here too (see <see cref="CountsAtDigsite"/>), noted rather than changed.
        ///
        /// Degrades to EMPTY on exception, the same safe direction as <see cref="Participants"/>: an empty
        /// sample leaves a creature exactly as authored.
        /// </summary>
        public static List<MlMapEventProfile> Sample(Position anchor, float radius)
        {
            var found = new List<MlMapEventProfile>();

            if (anchor == null || radius <= 0)
                return found;

            try
            {
                foreach (var player in PlayerManager.GetAllOnline())
                {
                    if (!Counts(player, anchor, radius))
                        continue;

                    found.Add(ProfileOf(player));
                }
            }
            catch (Exception ex)
            {
                log.Error("[ML_DIGSITE] profile scan failed; treating the digsite as empty for spread scaling", ex);
                return new List<MlMapEventProfile>();
            }

            return found;
        }

        /// <summary>
        /// The numbers level-spread scaling reads off one player. Every skill read uses
        /// GetCreatureSkill(skill, add: false) - a missing skill row is simply 0, and this scan must never ADD a
        /// row to a player's biota. Attacks count only when trained (a 0 means "does not use this family").
        /// Defenses are the skill's Current, not the stance-dependent effective value, so the reading does not
        /// swing with whether the player happens to be in combat mode at the moment of the scan.
        /// </summary>
        public static MlMapEventProfile ProfileOf(Player player)
        {
            return new MlMapEventProfile(
                player.Level ?? 1,
                player.Health?.MaxValue ?? 0,
                BestTrained(player, MlMapEventScaling.MeleeAttackSkills),
                BestTrained(player, MlMapEventScaling.MissileAttackSkills),
                BestTrained(player, MlMapEventScaling.MagicAttackSkills),
                player.GetCreatureSkill(Skill.MeleeDefense, false)?.Current ?? 0,
                player.GetCreatureSkill(Skill.MissileDefense, false)?.Current ?? 0);
        }

        private static uint BestTrained(Player player, Skill[] skills)
        {
            uint best = 0;

            foreach (var s in skills)
            {
                var skill = player.GetCreatureSkill(s, false);

                if (skill == null || skill.AdvancementClass < SkillAdvancementClass.Trained)
                    continue;

                best = Math.Max(best, skill.Current);
            }

            return best;
        }

        /// <summary>
        /// Whether anyone is still at the digsite, for the reap's abandon test.
        ///
        /// Returns TRUE on exception, the opposite of <see cref="Participants"/>. A broken scan must hold an
        /// encounter open rather than collapse one that is still being fought; the TTL is the backstop that
        /// ends it either way, so failing this direction costs at most one encounter's lifetime and never
        /// costs a group their fight.
        /// </summary>
        public static bool AnyPresent(Position anchor, float radius)
        {
            if (anchor == null || radius <= 0)
                return false;

            try
            {
                foreach (var player in PlayerManager.GetAllOnline())
                {
                    if (Counts(player, anchor, radius))
                        return true;
                }
            }
            catch (Exception ex)
            {
                log.Error("[ML_DIGSITE] presence scan failed; holding the encounter open rather than reporting it empty", ex);
                return true;
            }

            return false;
        }

        /// <summary>
        /// The per-player half of every scan above, kept in one place so presence, scaling, announcement and
        /// payout can never disagree about who is at a digsite.
        /// </summary>
        private static bool Counts(Player player, Position anchor, float radius)
        {
            var loc = player?.Location;

            if (loc == null || loc.Instance != anchor.Instance)
                return false;

            if (loc.DistanceTo(anchor) > radius)
                return false;

            return CountsAtDigsite(player.Session?.AccessLevel, player.IsDead);
        }

        /// <summary>
        /// The pure per-player rule every digsite scan applies once position and instance have matched.
        ///
        /// STAFF COUNT HERE (round 15 owner ruling), which is deliberately the opposite of
        /// WorldEventAudienceSampler.CountsTowardAudience. A digsite is opened by one map holder and is
        /// played by whoever dug it up; a staff character (the playtest account is staff) running a digsite
        /// alone was invisible to this scan, so the presence test saw nobody, the encounter ended as a wipe,
        /// and none of its chat reached the player running it. World events keep their exclusion - that rule
        /// is about a spectating admin inflating a shared public fight, which a digsite is not - so this is a
        /// digsite-only predicate rather than a change to the shared sampler.
        ///
        /// <paramref name="level"/> is accepted and deliberately ignored, so the one place that would have
        /// to change to reintroduce a staff rule is this method, and so the tests can pin that every access
        /// level counts.
        ///
        /// A field of corpses is a wipe, not a presence - and a dead player has not earned the payout the
        /// survivors are about to take, either.
        /// </summary>
        public static bool CountsAtDigsite(AccessLevel? level, bool isDead)
        {
            return !isDead;
        }
    }
}
