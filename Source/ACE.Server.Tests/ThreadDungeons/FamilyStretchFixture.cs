using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Server.ThreadDungeons;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ACE.Server.Tests.ThreadDungeons
{
    /// <summary>
    /// Shared inputs for the Thread family stretch tests: the shipped roster, a level-driven synthetic stat profile,
    /// and a canonical text form of a band standard so extrapolated standards can be pinned as strings.
    /// </summary>
    internal static class FamilyStretchFixture
    {
        public static DungeonGemSpec Spec(int level, int seed = 1, string family = "any")
            => new DungeonGemSpec("filos_doom", level, 6, family, seed, null, 0, 0);

        public static DungeonPopulationLimits Limits() => new DungeonPopulationLimits(60, 3.0, 3.0, 8, 0.5);

        /// <summary>Verbatim copy of ThreadsRunCeilingTests.LevelProfile.</summary>
        public static DungeonStatProfile LevelProfile(uint wcid)
        {
            var level = ShippedRosterFixture.LevelOf(wcid);

            if (level <= 0)
                return DungeonStatProfile.Empty;

            var l = (uint)level;
            var attrs = new Dictionary<PropertyAttribute, uint>
            {
                [PropertyAttribute.Strength] = l, [PropertyAttribute.Endurance] = l, [PropertyAttribute.Quickness] = l,
                [PropertyAttribute.Coordination] = l, [PropertyAttribute.Focus] = l, [PropertyAttribute.Self] = l,
            };

            return new DungeonStatProfile(level, 0,
                new Dictionary<Skill, uint> { [Skill.MeleeDefense] = 2 * l, [Skill.MissileDefense] = 2 * l, [Skill.HeavyWeapons] = 2 * l },
                l, 2 * l, 0, 0.0, attrs);
        }

        public static DungeonSpawnPlan Build(int level, int seed, DungeonPopulationLimits limits, string family = "any",
            Func<uint, bool> fits = null, ThreadPlanMemo memo = null)
        {
            var tables = ShippedRosterFixture.Tables;
            Assert.IsNotNull(tables, "Content/events/axes/species not found from the test output directory");

            Func<uint, int> levelOf = ShippedRosterFixture.LevelOf;
            Func<uint, uint> healthOf = ShippedRosterFixture.HealthOf;
            Func<uint, DungeonStatProfile> profileOf = LevelProfile;

            if (memo != null)
            {
                levelOf = memo.LevelOf;
                healthOf = memo.HealthOf;
                profileOf = memo.ProfileOf;
            }

            return DungeonPopulationBuilder.Build(Spec(level, seed, family), ShippedRosterFixture.Dungeon(30), ThreadDungeonStoreTests.LoadShipped(),
                tables, levelOf, healthOf, limits, new Random(seed), profileOf, fits, memo);
        }

        /// <summary>Canonical text of MaxBodyDamage, MaxBaseArmor and every attribute / effective-skill / skill median, sorted by key.</summary>
        public static string StandardText(DungeonBandStandard s)
        {
            var sb = new StringBuilder();
            sb.Append("dmg=").Append(s.MaxBodyDamage).Append("|armor=").Append(s.MaxBaseArmor);
            sb.Append("|attr=").Append(string.Join(",", s.AttributeMedians.OrderBy(k => (int)k.Key).Select(k => ((int)k.Key).ToString(CultureInfo.InvariantCulture) + ":" + k.Value.ToString(CultureInfo.InvariantCulture))));
            sb.Append("|eff=").Append(string.Join(",", s.EffectiveSkillMedians.OrderBy(k => (int)k.Key).Select(k => ((int)k.Key).ToString(CultureInfo.InvariantCulture) + ":" + k.Value.ToString(CultureInfo.InvariantCulture))));
            sb.Append("|skill=").Append(string.Join(",", s.SkillMedians.OrderBy(k => (int)k.Key).Select(k => ((int)k.Key).ToString(CultureInfo.InvariantCulture) + ":" + k.Value.ToString(CultureInfo.InvariantCulture))));
            return sb.ToString();
        }
    }
}
