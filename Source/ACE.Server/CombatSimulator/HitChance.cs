using System.Collections.Generic;

using ACE.Entity.Enum;
using ACE.Server.WorldObjects;

namespace ACE.Server.CombatSimulator
{
    /// <summary>
    /// Hit chance needs no sampling. The engine's evade roll compares a single deterministic
    /// skill-check value against a random draw, so the probability of landing is that value
    /// itself. Reproduced here in closed form rather than measured.
    /// </summary>
    public static class HitChance
    {
        public static float Calculate(uint attackSkill, uint defenseSkill)
        {
            return (float)SkillCheck.GetSkillChance(attackSkill, defenseSkill);
        }

        public static float For(AttackerSpec spec, DefenderProfile profile)
        {
            // fails soft on an absent table as well as an absent key, matching every lookup in
            // MitigationMath: a profile is a decomposition, so a table that is not there says
            // that term does not apply
            if (profile.DefenseSkills == null || !profile.DefenseSkills.TryGetValue(spec.CombatType, out var defenseSkill))
                defenseSkill = 0;

            return Calculate(spec.AttackSkill, defenseSkill);
        }
    }
}
