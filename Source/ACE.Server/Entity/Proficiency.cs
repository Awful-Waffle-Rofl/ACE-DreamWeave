using System;
using log4net;
using ACE.Common;
using ACE.Entity.Enum;
using ACE.Server.WorldObjects;
using ACE.Server.WorldObjects.Entity;

namespace ACE.Server.Entity
{
    public class Proficiency
    {
        private static readonly ILog log = LogManager.GetLogger(System.Reflection.MethodBase.GetCurrentMethod().DeclaringType);

        public static TimeSpan FullTime = TimeSpan.FromMinutes(15);

        public static void OnSuccessUse(Player player, CreatureSkill skill, uint difficulty)
        {
            //Console.WriteLine($"Proficiency.OnSuccessUse({player.Name}, {skill.Skill}, targetDiff: {difficulty})");

            // TODO: this formula still probably needs some work to match up with retail truly...

            // possible todo: does this only apply to players?
            // ie., can monsters still level up from skill usage, or killing players?
            // it was possible on release, but i think they might have removed that feature?

            if (player.IsOlthoiPlayer)
                return;

            // ensure skill is at least trained
            if (skill.AdvancementClass < SkillAdvancementClass.Trained)
                return;

            var last_difficulty = skill.PropertiesSkill.ResistanceAtLastCheck;
            var last_used_time = skill.PropertiesSkill.LastUsedTime;

            var currentTime = Time.GetUnixTime();

            var timeDiff = currentTime - last_used_time;

            if (timeDiff < 0)
            {
                // can happen if server clock is rewound back in time
                log.Warn($"Proficiency.OnSuccessUse({player.Name}, {skill.Skill}, {difficulty}) - timeDiff: {timeDiff}");
                skill.PropertiesSkill.LastUsedTime = currentTime;       // update to prevent log spam
                return;
            }

            var difficulty_check = difficulty > last_difficulty;
            var time_check = timeDiff >= FullTime.TotalSeconds;

            if (difficulty_check || time_check)
            {
                // todo: not independent variables?
                // always scale if timeDiff < FullTime?
                var timeScale = 1.0f;
                if (!time_check)
                {
                    // 10 mins elapsed from 15 min FullTime:
                    // 0.66f timeScale
                    timeScale = (float)(timeDiff / FullTime.TotalSeconds);

                    // any rng involved?
                }

                skill.PropertiesSkill.ResistanceAtLastCheck = difficulty;
                skill.PropertiesSkill.LastUsedTime = currentTime;

                player.ChangesDetected = true;

                if (player.IsMaxLevel) return;

                var pp = (uint)Math.Round(difficulty * timeScale);
                var totalXPGranted = (long)Math.Round(pp * 1.1f);   // give additional 10% of proficiency XP to unassigned XP

                if (totalXPGranted > 10000)
                {
                    log.Warn($"Proficiency.OnSuccessUse({player.Name}, {skill.Skill}, {difficulty}) - totalXPGranted: {totalXPGranted:N0}");
                }

                // Clamp against the character's OWN ceiling (the synthesized chart's hard ceiling), not the
                // retail chart's level-275 cap. This used to read Player.GetMaxLevel(), which is still the
                // retail dat chart's last index; once levels were uncapped a character could carry more total
                // XP than level 275 requires, and GetRemainingXP then returned a NEGATIVE remainder that was
                // assigned straight into totalXPGranted and handed to GrantXP. GrantXP has no negative guard
                // (only EarnXP does, and proficiency does not route through it), so UpdateXpAndLevel
                // subtracted it from BOTH TotalExperience and AvailableExperience - snapping the character's
                // total back to the level-275 total on every successful skill check.
                var maxLevel = (uint)player.GetPlayerMaxLevel();
                var remainingXP = player.GetRemainingXP(maxLevel) ?? 0;

                var clamped = ClampToRemaining(totalXPGranted, remainingXP);

                if (clamped <= 0)
                    return;

                if (clamped != totalXPGranted)
                {
                    // checks and balances:
                    // total xp = pp * 1.1
                    // pp = total xp / 1.1

                    totalXPGranted = clamped;
                    pp = (uint)Math.Round(totalXPGranted / 1.1f);
                }

                // if skill is maxed out, but player is below MaxLevel,
                // not sure if retail granted 0%, 10%, or 110% of the pp to TotalExperience here
                // since pp is such a miniscule system at the higher levels,
                // going to just naturally add it to TotalXP for now..

                pp = Math.Min(pp, skill.ExperienceLeft);

                //Console.WriteLine($"Earned {pp} PP ({skill.Skill})");

                // send CP to player as unassigned XP
                player.GrantXP(totalXPGranted, XpType.Proficiency, ShareType.None);

                // send PP to player as skill XP, which gets spent from the CP sent
                if (pp > 0)
                {
                    player.HandleActionRaiseSkill(skill.Skill, pp);
                }
            }
        }

        /// <summary>
        /// Clamps a proficiency XP grant to the room the character still has below the XP chart's hard
        /// ceiling. Never returns a negative value: a character already at or past the ceiling gets 0, which
        /// the caller treats as "grant nothing". Pure, so the boundary is testable without a live Player.
        /// </summary>
        internal static long ClampToRemaining(long totalXPGranted, long remainingXP)
        {
            if (totalXPGranted <= 0 || remainingXP <= 0)
                return 0;

            return Math.Min(totalXPGranted, remainingXP);
        }

        public static void OnSuccessUse(Player player, CreatureSkill skill, int difficulty)
        {
            if (difficulty < 0)
            {
                log.Error($"Proficiency.OnSuccessUse({player.Name}, {skill.Skill}, {difficulty}) - difficulty cannot be negative");
                return;
            }
            OnSuccessUse(player, skill, (uint)difficulty);
        }
    }
}
