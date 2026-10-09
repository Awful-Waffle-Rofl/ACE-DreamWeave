using System;
using System.Collections.Generic;
using System.Linq;

using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Entity.Models;
using ACE.Server.Entity;
using ACE.Server.WorldObjects;
using ACE.Server.WorldObjects.Entity;

namespace ACE.Server.ThreadDungeons
{
    /// <summary>
    /// Scales one reach-up STAMPED creature (DungeonSpawnPlanEntry.StampLevel) from its authored level to its
    /// stamp, on the stat curve (owner ruling 2026-10-08): every primary attribute, every attack and defense
    /// skill's EFFECTIVE value, its melee damage (body parts and any wielded weapon or ammunition) and its body
    /// armour are multiplied by DungeonStatCurve's ratio between the two levels. The same ratio for every
    /// creature at the same (authored, stamp) pair, which is what "preserving authored spread" means: creatures
    /// authored at one level keep their toughness order at their shared stamp, and across levels every creature
    /// keeps its position RELATIVE TO THE CURVE (value / Standard(level)), since the ratio is
    /// Standard(stamp) / Standard(authored). Equal raw values at two different authored levels need not stay in
    /// order on a defense axis - the lower one was relatively tougher for its level, and stays so
    /// (ThreadsRunCeilingTests.Stamp_scaling_preserves_authored_order).
    ///
    /// EFFECTIVE skill, not InitLevel. The attributes climb on their own slopes, so the skill's InitLevel is
    /// re-derived as target effective minus the attribute term over the RAISED attributes
    /// (<see cref="DungeonStatCurve.InitFor"/>). Without that the attribute climb would carry the effective
    /// defense past the softened rate above level 375, which is exactly what the softening exists to prevent.
    ///
    /// HEALTH IS NOT TOUCHED HERE: the run's health terms (HealthNormalizeRatio onto T(run level), the trash
    /// floor, the gem multiplier) already price every non-boss creature, stamped or not, and the spawner applies
    /// them from the AUTHORED max captured before this runs - so the Endurance scale does not leak into health.
    /// If the scaled Endurance's own health contribution would put the run's health target out of reach (the
    /// same guard the uplift's attribute raise takes), the ATTRIBUTE half is skipped for that creature and its
    /// skills are re-targeted against its unscaled attributes instead; the effective targets still land.
    ///
    /// Not for a boss: a normalized boss is SET to the curve standard at the run level by its own path, and a
    /// legacy (un-normalized) boss is deliberately left tougher than its pack as authored.
    ///
    /// The creature-facing glue is thin; the numbers are decided by the pure members below, which is where the
    /// unit tests are (ACE.Server.Tests cannot construct a live Creature).
    /// </summary>
    internal static class ReachUpStamp
    {
        /// <summary>What one stamp did, for the spawner's log line.</summary>
        internal readonly struct Result
        {
            public Result(int attributesScaled, int skillsRetargeted, int partsScaled, int weaponsScaled, bool attributesSkipped)
            {
                AttributesScaled = attributesScaled;
                SkillsRetargeted = skillsRetargeted;
                PartsScaled = partsScaled;
                WeaponsScaled = weaponsScaled;
                AttributesSkipped = attributesSkipped;
            }

            public int AttributesScaled { get; }
            public int SkillsRetargeted { get; }
            public int PartsScaled { get; }
            public int WeaponsScaled { get; }

            /// <summary>True when the attribute half was refused because the health target would become unreachable.</summary>
            public bool AttributesSkipped { get; }
        }

        /// <summary>
        /// Applies the stamp scaling to a live creature before EnterWorld. <paramref name="healthTarget"/> is the
        /// health the spawner is about to write (ThreadDungeonSpawner.NonBossHealthTarget over the AUTHORED max),
        /// used only for the Endurance reachability guard.
        /// </summary>
        internal static Result Apply(Creature creature, int authoredLevel, int stampLevel, DungeonStatCurve.Anchored curve, int healthTarget)
        {
            if (creature == null || curve == null || authoredLevel <= 0 || stampLevel <= authoredLevel)
                return default;

            double RatioOf(DungeonStatAxis axis) => curve.Ratio(axis, authoredLevel, stampLevel);

            // 1. Every usable attack and defense skill's effective value, measured on the AUTHORED attributes and
            //    UNCLAMPED (no defense ceiling has been written yet - the strip runs after this).
            var skills = new List<(Skill Skill, CreatureSkill Live, uint EffectiveBefore, DungeonStatAxis Axis)>();

            foreach (var skill in DungeonCombatNormalizer.AttackSkills.Concat(DungeonCombatNormalizer.DefenseSkills))
            {
                var axis = DungeonStatCurve.AxisOf(skill);
                var live = creature.GetCreatureSkill(skill, false);

                if (!axis.HasValue || live == null || !live.IsUsable)
                    continue;

                skills.Add((skill, live, AttributeFormula.GetFormula(creature, skill, false) + live.InitLevel + live.Ranks, axis.Value));
            }

            // 2. The attributes, unless the raised Endurance would make the health target unreachable.
            var attributeTargets = AttributeTargets(
                attribute => creature.Attributes.TryGetValue(attribute, out var a) && a != null ? a.Base : (uint?)null, RatioOf);

            var attributesSkipped = false;
            var attributesScaled = 0;

            if (attributeTargets.TryGetValue(PropertyAttribute.Endurance, out var endurance)
                && !ThreadDungeonSpawner.UpliftAttributeRaiseKeepsHealthReachable(healthTarget, endurance))
            {
                attributesSkipped = true;
            }
            else
            {
                foreach (var kvp in attributeTargets)
                {
                    var attribute = creature.Attributes[kvp.Key];

                    if (attribute.Base == kvp.Value)
                        continue;

                    attribute.StartingValue = kvp.Value;
                    attribute.Ranks = 0;
                    attributesScaled++;
                }

                if (attributesScaled > 0)
                {
                    creature.Health.Current = creature.Health.MaxValue;
                    creature.Stamina.Current = creature.Stamina.MaxValue;
                    creature.Mana.Current = creature.Mana.MaxValue;
                }
            }

            // 3. Each skill re-targeted onto its effective value carried up the curve, against the attribute term
            //    it NOW has.
            var skillsRetargeted = 0;

            foreach (var (skill, live, effectiveBefore, axis) in skills)
            {
                var fixedTerm = AttributeFormula.GetFormula(creature, skill, false) + live.Ranks;
                var init = SkillInitTarget(effectiveBefore, fixedTerm, RatioOf(axis));

                if (init == live.InitLevel)
                    continue;

                live.InitLevel = init;
                skillsRetargeted++;
            }

            // 4. Melee damage and body armour.
            var damageRatio = RatioOf(DungeonStatAxis.Damage);
            var partsScaled = ScaleBodyParts(creature.Biota, damageRatio, RatioOf(DungeonStatAxis.Armor));

            // 5. A wielded weapon or its ammunition replaces the body part's DVal entirely on the monster damage
            //    path (DungeonCombatNormalizer.WeaponDamageTarget's doc comment has the call sites), so it has to
            //    climb by the same ratio or a weapon-wielder would keep its authored damage. Same item set and
            //    the same launcher exclusion as DungeonCreatureNormalizer.NormalizeBossWeapons; per instance,
            //    since every wielded item has its own biota.
            var weaponsScaled = 0;

            foreach (var item in creature.EquippedObjects.Values.Concat(creature.Inventory.Values).Where(i => i != null).Distinct())
            {
                if (item is MissileLauncher)
                    continue;

                var damage = item.GetProperty(PropertyInt.Damage) ?? 0;
                var scaled = ScaleInt(damage, damageRatio);

                if (scaled == damage)
                    continue;

                item.SetProperty(PropertyInt.Damage, scaled);
                weaponsScaled++;
            }

            return new Result(attributesScaled, skillsRetargeted, partsScaled, weaponsScaled, attributesSkipped);
        }

        /// <summary>
        /// The six primary attributes' new bases (pure): every attribute the creature carries with a non-zero
        /// base, times its axis ratio. A zero base is "not authored" (the runtime wrappers exist for all six) and
        /// is never raised from nothing - BandUplift.RaiseAttributeFloors' rule.
        /// </summary>
        internal static Dictionary<PropertyAttribute, uint> AttributeTargets(Func<PropertyAttribute, uint?> baseOf, Func<DungeonStatAxis, double> ratioOf)
        {
            var targets = new Dictionary<PropertyAttribute, uint>();

            foreach (var attribute in BandUplift.PrimaryAttributes)
            {
                var current = baseOf(attribute);
                var axis = DungeonStatCurve.AxisOf(attribute);

                if (current == null || current.Value == 0 || !axis.HasValue)
                    continue;

                targets[attribute] = DungeonStatCurve.Scale(current.Value, ratioOf(axis.Value));
            }

            return targets;
        }

        /// <summary>
        /// One skill's new InitLevel (pure): the effective value it had at its authored level, times the curve
        /// ratio, minus the attribute term (plus Ranks) it has after the attribute half ran, floored at 0.
        /// </summary>
        internal static uint SkillInitTarget(uint effectiveBefore, uint fixedTermAfter, double ratio)
            => DungeonStatCurve.InitFor(DungeonStatCurve.Scale(effectiveBefore, ratio), fixedTermAfter);

        /// <summary>
        /// Scales every body part's DVal and BaseArmor by its ratio and returns how many parts changed (pure over
        /// the runtime biota). CLONES the collection and every part first, for the reason
        /// BandUplift.ApplyBodyPartUplift documents at length: PropertiesBodyPart is shared BY REFERENCE with the
        /// cached weenie, so an in-place write would re-tune every instance of that creature server-wide. Nothing
        /// is cloned when nothing would change. A zero stays zero (a creature authored with no bite gets none).
        /// </summary>
        internal static int ScaleBodyParts(Biota biota, double damageRatio, double armorRatio)
        {
            var parts = biota?.PropertiesBodyPart;

            if (parts == null || parts.Count == 0)
                return 0;

            var changed = 0;
            var fresh = new Dictionary<CombatBodyPart, PropertiesBodyPart>(parts.Count);

            foreach (var kvp in parts)
            {
                var part = kvp.Value?.Clone();

                if (part != null)
                {
                    var dval = ScaleInt(part.DVal, damageRatio);
                    var armor = ScaleInt(part.BaseArmor, armorRatio);

                    if (dval != part.DVal || armor != part.BaseArmor)
                        changed++;

                    part.DVal = dval;
                    part.BaseArmor = armor;
                }

                fresh[kvp.Key] = part;
            }

            if (changed > 0)
                biota.PropertiesBodyPart = fresh;

            return changed;
        }

        /// <summary><see cref="DungeonStatCurve.Scale"/> for a signed authored value: 0 or below is left as it is.</summary>
        internal static int ScaleInt(int value, double ratio)
            => value <= 0 ? value : (int)Math.Min(int.MaxValue, DungeonStatCurve.Scale((uint)value, ratio));
    }
}
