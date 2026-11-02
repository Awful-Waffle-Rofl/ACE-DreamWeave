using System;
using System.Collections.Generic;

using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Server.Factories.Tables;
using ACE.Server.WorldObjects;

namespace ACE.Server.KnotLoot
{
    /// <summary>
    /// The upgraded Knot cloak roll (Drift Matron Spines, wcid 1007602, from a won Matron Knot). Each copy rolls like a
    /// retail loot cloak (LootGenerationFactory.MutateCloak), but upgraded: a max level of 5-10 windowed by the Knot level,
    /// level-II surge rings, Cloaked in Skill at +40, damage reduction at 400, and a set bonus that keeps scaling to
    /// item level 10. Every upgrade is a property stamped on the item, so retail cloaks are untouched.
    /// WieldDifficulty, workmanship, material and the item XP curve stay as authored.
    /// </summary>
    public static class UpgradedCloak
    {
        /// <summary>
        /// Level-overlay icons by item max level. 1-5 are the retail overlays (LootGenerationFactory.IconOverlay_ItemMaxLevel);
        /// 6-9 continue the same numeral family in the portal dat (0x06006C39-0x06006C3C, verified by rendering). The dat
        /// has no 10 in that family, so level 10 carries no overlay.
        /// </summary>
        public static readonly IReadOnlyList<uint> IconOverlayByMaxLevel = new List<uint>
        {
            0x6006C34,  // 1
            0x6006C35,  // 2
            0x6006C36,  // 3
            0x6006C37,  // 4
            0x6006C38,  // 5
            0x6006C39,  // 6
            0x6006C3A,  // 7
            0x6006C3B,  // 8
            0x6006C3C,  // 9
        };

        /// <summary>Applies the upgraded roll with the live equipment-set roll (CloakChance.RollEquipmentSet).</summary>
        public static void Mutate(WorldObject item, int knotLevel, Func<double> next)
            => Mutate(item, knotLevel, next, CloakChance.RollEquipmentSet);

        /// <summary>
        /// Applies the upgraded roll. Draw order is pinned: one <paramref name="next"/> for the max level, then one for
        /// the proc; the set comes from <paramref name="rollSet"/>.
        /// </summary>
        public static void Mutate(WorldObject item, int knotLevel, Func<double> next, Func<EquipmentSet> rollSet)
        {
            if (item == null)
                return;

            var maxLevel = RollMaxLevel(knotLevel, next());

            item.ItemMaxLevel = maxLevel;
            item.IconOverlayId = IconOverlay(maxLevel);

            item.EquipmentSetId = rollSet();
            item.SetProperty(PropertyInt.SetBonusMaxLevel, KnotLootDefaults.UpgradedCloakSetBonusMaxLevel);

            ApplyProc(item, CloakChance.RollUpgradedProcSpell(next()));
        }

        /// <summary>Stamps one proc outcome; <see cref="SpellId.Undef"/> is the damage reduction proc.</summary>
        public static void ApplyProc(WorldObject item, SpellId proc)
        {
            if (proc != SpellId.Undef)
            {
                item.ProcSpell = (uint)proc;

                // Cloaked In Skill is the only self-targeted spell
                item.ProcSpellSelfTargeted = proc == SpellId.CloakAllSkill;

                item.CloakWeaveProc = 1;

                if (proc == SpellId.CloakAllSkill)
                    item.SetProperty(PropertyFloat.CloakProcStatModValue, KnotLootDefaults.UpgradedCloakCloakedInSkillValue);
                else
                    item.RemoveProperty(PropertyFloat.CloakProcStatModValue);

                item.RemoveProperty(PropertyInt.CloakDamageReductionAmount);
            }
            else
            {
                // Damage Reduction proc
                item.ProcSpell = null;
                item.ProcSpellSelfTargeted = false;
                item.CloakWeaveProc = 2;

                item.RemoveProperty(PropertyFloat.CloakProcStatModValue);
                item.SetProperty(PropertyInt.CloakDamageReductionAmount, KnotLootDefaults.UpgradedCloakDamageReduction);
            }
        }

        /// <summary>The level-overlay icon for a max level, or null when the dat has none (level 10, or out of range).</summary>
        public static uint? IconOverlay(int maxLevel)
        {
            if (maxLevel < 1 || maxLevel > IconOverlayByMaxLevel.Count)
                return null;

            return IconOverlayByMaxLevel[maxLevel - 1];
        }

        /// <summary>
        /// Pure: the inclusive max-level window for a Knot level. t = clamp((L - 200) / 200, 0, 1);
        /// lo = 5 + round-half-up(3t), hi = 6 + round-half-up(4t). Integer math only.
        /// </summary>
        public static (int Low, int High) MaxLevelWindow(int knotLevel)
        {
            const long low = KnotLootDefaults.UpgradedCloakLowLevel;
            const long high = KnotLootDefaults.UpgradedCloakHighLevel;

            var level = Math.Clamp((long)knotLevel, low, high);

            var lo = KnotLootDefaults.UpgradedCloakMinLevelBase + RoundHalfUpFraction(KnotLootDefaults.UpgradedCloakMinLevelSpan * (level - low), high - low);
            var hi = KnotLootDefaults.UpgradedCloakMaxLevelBase + RoundHalfUpFraction(KnotLootDefaults.UpgradedCloakMaxLevelSpan * (level - low), high - low);

            return (lo, hi);
        }

        /// <summary>Pure: lo + min(floor(u x (hi - lo + 1)), hi - lo) over <see cref="MaxLevelWindow"/>. NaN or negative u reads as 0.</summary>
        public static int RollMaxLevel(int knotLevel, double u)
        {
            var (lo, hi) = MaxLevelWindow(knotLevel);

            if (double.IsNaN(u) || u < 0)
                u = 0;

            var span = hi - lo;
            var step = (int)Math.Min(Math.Floor(u * (span + 1)), span);

            return lo + step;
        }

        /// <summary>round-half-up(numerator / denominator) for a non-negative numerator and positive denominator: floor((2n + d) / 2d).</summary>
        private static int RoundHalfUpFraction(long numerator, long denominator)
            => (int)((2 * numerator + denominator) / (2 * denominator));
    }
}
