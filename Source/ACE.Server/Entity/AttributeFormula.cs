using System;

using ACE.Common.Extensions;
using ACE.DatLoader.Entity;
using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Server.WorldObjects;

namespace ACE.Server.Entity
{
    /// <summary>
    /// Calculates the amount to add to a creature's current skills and vitals,
    /// based on their primary attribute current values
    /// </summary>
    public static class AttributeFormula
    {
        /// <summary>
        /// Returns the amount to add to a creature's current skill,
        /// based on their primary attribute current values
        /// </summary>
        public static uint GetFormula(Creature creature, Skill skill, bool current = true)
        {
            if (!GameTables.SkillTable.SkillBaseHash.TryGetValue((uint)skill, out SkillBase skillBase))
                return 0;

            return GetFormula(creature, skillBase.Formula, current);
        }

        /// <summary>
        /// Returns the amount to add to a creature's current vital,
        /// based on their primary attribute current values
        /// </summary>
        public static uint GetFormula(Creature creature, PropertyAttribute2nd vital, bool current = true)
        {
            var vitalTable = GameTables.SecondaryAttributeTable;

            switch (vital)
            {
                case PropertyAttribute2nd.MaxHealth:
                    return GetFormula(creature, vitalTable.MaxHealth.Formula, current);
                case PropertyAttribute2nd.MaxStamina:
                    return GetFormula(creature, vitalTable.MaxStamina.Formula, current);
                case PropertyAttribute2nd.MaxMana:
                    return GetFormula(creature, vitalTable.MaxMana.Formula, current);
                default:
                    return 0;
            }
        }

        /// <summary>
        /// Applies a SkillFormula from the portal.dat,
        /// using the primary attributes for a creature
        /// </summary>
        public static uint GetFormula(Creature creature, DatLoader.Entity.SkillFormula formula, bool current = true)
        {
            return Compute(formula, attr => current ? creature.Attributes[attr].Current : creature.Attributes[attr].Base);
        }

        /// <summary>
        /// The pure arithmetic core of <see cref="GetFormula(Creature, DatLoader.Entity.SkillFormula, bool)"/>,
        /// taking attribute values through a delegate rather than a live Creature. This is what lets
        /// DungeonBandStandard's effective-defense-skill median replicate this formula exactly from an
        /// authored attribute dictionary (DungeonStatProfile.Attributes) without constructing a Creature -
        /// ACE.Server.Tests cannot (see DungeonCreatureNormalizer.cs's "NOT COVERED HERE" note for the same
        /// constraint on other Threads normalization helpers).
        ///
        /// Same short-circuit as the original: a formula with X == 0 returns 0 without ever invoking
        /// <paramref name="attributeOf"/>, so a caller building that delegate from a possibly-empty
        /// attribute set never has to special-case an unused formula.
        /// </summary>
        public static uint Compute(DatLoader.Entity.SkillFormula formula, Func<PropertyAttribute, uint> attributeOf)
        {
            if (formula == null || formula.X == 0)
                return 0;

            var attr1 = (PropertyAttribute)formula.Attr1;
            var attr2 = (PropertyAttribute)formula.Attr2;
            var divisor = formula.Z;

            var total = attributeOf(attr1);
            if (attr2 != PropertyAttribute.Undef)
                total += attributeOf(attr2);

            if (divisor != 1)
                total = (uint)((float)total / divisor).Round();

            return total;
        }
    }
}
