using ACE.DatLoader.Entity;
using ACE.DatLoader.FileTypes;
using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Server.Entity;

namespace ACE.Server.Tests
{
    /// <summary>
    /// Installs synthetic skill/vital formula tables into GameTables, taking the place of the
    /// portal dat injection the server does at boot. The retired-skill formulas come from the
    /// same production AddRetiredSkills call the server uses; the rest are the documented
    /// retail formulas. Idempotent - safe to call from any fixture that builds creatures.
    /// </summary>
    internal static class TestGameTables
    {
        private static bool initialized;

        public static void EnsureInitialized()
        {
            if (initialized)
                return;

            var skillTable = new SkillTable();

            // production data: UnarmedCombat = (Str+Coord)/3, Bow = Coord/2, etc.
            skillTable.AddRetiredSkills();

            // retail defense formulas
            skillTable.SkillBaseHash[(uint)Skill.MeleeDefense] = new SkillBase(new SkillFormula(PropertyAttribute.Quickness, PropertyAttribute.Coordination, 3));
            skillTable.SkillBaseHash[(uint)Skill.MissileDefense] = new SkillBase(new SkillFormula(PropertyAttribute.Coordination, PropertyAttribute.Quickness, 5));

            // retail vital formulas: health = endurance/2, stamina = endurance, mana = self
            var vitalTable = new SecondaryAttributeTable();
            SetFormula(vitalTable.MaxHealth, PropertyAttribute.Endurance, 2);
            SetFormula(vitalTable.MaxStamina, PropertyAttribute.Endurance, 1);
            SetFormula(vitalTable.MaxMana, PropertyAttribute.Self, 1);

            GameTables.Initialize(skillTable, vitalTable);

            initialized = true;
        }

        private static void SetFormula(Attribute2ndBase vital, PropertyAttribute attribute, uint divisor)
        {
            vital.Formula.X = 1;    // X == 0 marks a formula as inert
            vital.Formula.Attr1 = (uint)attribute;
            vital.Formula.Z = divisor;
        }
    }
}
