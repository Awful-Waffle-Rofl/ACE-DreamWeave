using System;
using System.Collections.Generic;
using System.Linq;

using ACE.Entity.Models;
using ACE.Server.Entity;
using ACE.Server.WorldObjects;

namespace ACE.Server.ThreadDungeons
{
    /// <summary>
    /// The band-standard uplift: normalizes one creature that sits BELOW a band's natural low edge up to the
    /// standard that band carries (<see cref="DungeonBandStandard"/>). Extracted from
    /// ThreadDungeonSpawner.ApplyUplift so World Events can apply the SAME writes; Threads keeps a thin wrapper
    /// that adds its log line and any renaming. Everything here is a function of (creature, level, standard) -
    /// no run, no plan, no logging, no tunables.
    ///
    /// Four axes, each of them upward-only:
    ///   * LEVEL - a floor at the uplift level;
    ///   * SKILLS - a per-skill floor at the band's median InitLevel, for every skill the creature ALREADY
    ///     carries (never adds a skill: GetCreatureSkill's default overload would create one Untrained);
    ///   * BODY-PART DAMAGE and ARMOUR - a RATIO against the creature's own maximum, applied to every part, so
    ///     the creature's authored part profile keeps its shape.
    /// HEALTH is deliberately absent: callers floor health to BandMedianHealth themselves, before their
    /// multiplier. SPELLS (DungeonSpellTier.Raise) are a Threads-only axis scaled by the caller.
    /// </summary>
    public static class BandUplift
    {
        /// <summary>
        /// Applies the uplift to a live creature before EnterWorld. Returns how many skills were raised and how
        /// many body parts were scaled (both 0 when the standard has nothing to measure, in which case only the
        /// level floor is applied).
        /// </summary>
        public static (int SkillsRaised, int PartsScaled) Apply(Creature creature, int upliftLevel, DungeonBandStandard standard)
        {
            // Thin glue over the pure cores below (LevelFloor, RaiseSkillFloors, ApplyBodyPartUplift).
            var raisedLevel = LevelFloor(creature.Level, upliftLevel);

            if (raisedLevel.HasValue)
                creature.Level = raisedLevel.Value;

            if (standard == null || standard.IsEmpty)
            {
                // The band had nothing to measure - the level floor above still stands (it needs no sample);
                // the rest is skipped rather than guessed at.
                return (0, 0);
            }

            // add: false is load-bearing - never create a skill the creature was not authored with.
            var skillsRaised = RaiseSkillFloors(standard.SkillMedians,
                skill => creature.GetCreatureSkill(skill, false)?.InitLevel,
                (skill, value) => creature.GetCreatureSkill(skill, false).InitLevel = value);

            var partsScaled = ApplyBodyPartUplift(creature.Biota, standard.MaxBodyDamage, standard.MaxBaseArmor);

            return (skillsRaised, partsScaled);
        }

        // ---- Threads-only attribute uplift (dynamic_dungeons_uplift_attributes) -------------------------------
        //
        // World Events share Apply above and must not change, so everything below is a SEPARATE entry point that
        // only ThreadDungeonSpawner calls. Two steps because the spawner's ORDER matters (see the call sites):
        // RaiseAttributes runs BEFORE the health block (Endurance feeds max health), TopUpEffectiveSkills runs
        // AFTER Apply (it measures against the creature's final InitLevels).

        /// <summary>The six primary attributes, in the order Creature builds its wrappers.</summary>
        internal static readonly ACE.Entity.Enum.Properties.PropertyAttribute[] PrimaryAttributes =
        {
            ACE.Entity.Enum.Properties.PropertyAttribute.Strength, ACE.Entity.Enum.Properties.PropertyAttribute.Endurance,
            ACE.Entity.Enum.Properties.PropertyAttribute.Coordination, ACE.Entity.Enum.Properties.PropertyAttribute.Quickness,
            ACE.Entity.Enum.Properties.PropertyAttribute.Focus, ACE.Entity.Enum.Properties.PropertyAttribute.Self,
        };

        /// <summary>
        /// Raises a live creature's primary attributes to the band standard's per-attribute medians and returns
        /// how many were raised. UPWARD ONLY, per attribute, and a no-op for an empty standard. Called before the
        /// spawner's health block; refills all three vitals when anything was raised, because the vitals' Current
        /// values were set from the AUTHORED attributes at construction (Creature.cs:215-217).
        ///
        /// Per instance: PropertiesAttribute is cloned per biota (ACE.Entity/Adapter/WeenieConverter.cs:117-122),
        /// the same fact NormalizeBossAttributes relies on. The write mirrors it too: StartingValue takes the
        /// median and Ranks goes to 0, so CreatureAttribute.Base (Ranks + StartingValue) equals the median.
        /// </summary>
        internal static int RaiseAttributes(Creature creature, DungeonBandStandard standard)
        {
            if (creature == null || standard == null || standard.IsEmpty)
                return 0;

            var raised = RaiseAttributeFloors(standard.AttributeMedians,
                attribute => creature.Attributes.TryGetValue(attribute, out var a) && a != null ? a.Base : (uint?)null,
                (attribute, value) =>
                {
                    var a = creature.Attributes[attribute];
                    a.StartingValue = value;
                    a.Ranks = 0;
                });

            if (raised > 0)
            {
                creature.Health.Current = creature.Health.MaxValue;
                creature.Stamina.Current = creature.Stamina.MaxValue;
                creature.Mana.Current = creature.Mana.MaxValue;
            }

            return raised;
        }

        /// <summary>
        /// The Endurance base after the uplift's raise (pure): the median when it exceeds a carried (non-zero)
        /// current base, else the current base unchanged. Mirrors <see cref="RaiseAttributeFloors"/>.
        /// </summary>
        internal static uint RaisedEndurance(uint currentBase, uint median)
            => currentBase != 0 && median > currentBase ? median : currentBase;

        /// <summary>
        /// The per-attribute floors (pure over delegates): for every band median above zero, an attribute the
        /// creature ALREADY carries (<paramref name="baseOf"/> returns a value above zero - the runtime wrappers
        /// exist for all six attributes with a zero placeholder row when the weenie authored none, and a zero
        /// Base is therefore "not authored", never raised from nothing) whose Base is below the median is set to
        /// the median. Never lowers, never adds. Returns how many were raised.
        /// </summary>
        internal static int RaiseAttributeFloors(IReadOnlyDictionary<ACE.Entity.Enum.Properties.PropertyAttribute, uint> medians,
            Func<ACE.Entity.Enum.Properties.PropertyAttribute, uint?> baseOf, Action<ACE.Entity.Enum.Properties.PropertyAttribute, uint> setBase)
        {
            var raised = 0;

            foreach (var attribute in PrimaryAttributes)
            {
                if (!medians.TryGetValue(attribute, out var median) || median == 0)
                    continue;

                var current = baseOf(attribute);

                if (current == null || current.Value == 0 || median <= current.Value)
                    continue;

                setBase(attribute, median);
                raised++;
            }

            return raised;
        }

        /// <summary>
        /// The InitLevel an effective-skill top-up writes (pure): the larger of the creature's current InitLevel
        /// and <c>effectiveMedian - fixedTerm</c>, so that attribute term + InitLevel lands on the band's
        /// EFFECTIVE median. <paramref name="fixedTerm"/> is everything in the skill's Base that is not InitLevel:
        /// the attribute-formula term over the RAISED attributes, plus Ranks. A non-zero <paramref name="cap"/>
        /// (the #1173 defense ceiling) clamps the target so the effective skill never exceeds it. Never returns
        /// less than <paramref name="currentInit"/>.
        /// </summary>
        internal static uint EffectiveSkillFloor(uint effectiveMedian, uint fixedTerm, uint currentInit, uint cap)
        {
            var target = effectiveMedian > fixedTerm ? effectiveMedian - fixedTerm : 0u;

            if (cap > 0)
            {
                var capInit = cap > fixedTerm ? cap - fixedTerm : 0u;
                target = Math.Min(target, capInit);
            }

            return Math.Max(currentInit, target);
        }

        /// <summary>
        /// The effective median a top-up aims at for one skill (pure): the standard's own for that skill, or -
        /// for an ATTACK skill with none - the highest effective attack median (the precedent
        /// DungeonCombatNormalizer.AttackSkillTarget sets for a boss wielding a school the band does not use).
        /// Defense skills never borrow: the three guard three different categories. 0 means "no target".
        /// </summary>
        internal static uint EffectiveTargetFor(DungeonBandStandard standard, ACE.Entity.Enum.Skill skill)
        {
            var median = standard.EffectiveSkillMedianFor(skill);

            if (median != 0 || Array.IndexOf(DungeonCombatNormalizer.AttackSkills, skill) < 0)
                return median;

            var best = 0u;

            foreach (var attack in DungeonCombatNormalizer.AttackSkills)
                best = Math.Max(best, standard.EffectiveSkillMedianFor(attack));

            return best;
        }

        /// <summary>
        /// The effective-skill top-up (pure over delegates): for every attack and defense skill the creature
        /// ALREADY carries and can use (<paramref name="initOf"/> returns non-null), raises InitLevel to
        /// <see cref="EffectiveSkillFloor"/> against the standard's effective median. Never adds a skill, never
        /// lowers one. <paramref name="capOf"/> supplies the defense ceiling per skill (0 = none; only consulted
        /// for defense skills). Returns how many skills were raised.
        /// </summary>
        internal static int TopUpEffectiveSkills(DungeonBandStandard standard, Func<ACE.Entity.Enum.Skill, uint?> initOf,
            Func<ACE.Entity.Enum.Skill, uint> fixedTermOf, Func<ACE.Entity.Enum.Skill, uint> capOf, Action<ACE.Entity.Enum.Skill, uint> setInit)
        {
            if (standard == null || standard.IsEmpty)
                return 0;

            var raised = 0;

            foreach (var skill in DungeonCombatNormalizer.AttackSkills.Concat(DungeonCombatNormalizer.DefenseSkills))
            {
                var target = EffectiveTargetFor(standard, skill);

                if (target == 0)
                    continue;

                var init = initOf(skill);

                if (init == null)
                    continue;

                var isDefense = Array.IndexOf(DungeonCombatNormalizer.DefenseSkills, skill) >= 0;
                var next = EffectiveSkillFloor(target, fixedTermOf(skill), init.Value, isDefense ? capOf(skill) : 0u);

                if (next <= init.Value)
                    continue;

                setInit(skill, next);
                raised++;
            }

            return raised;
        }

        /// <summary>
        /// Creature glue for <see cref="TopUpEffectiveSkills"/> plus the defense-ceiling re-check (E), run AFTER
        /// <see cref="Apply"/>. Returns (skills raised, ceilings set).
        ///
        /// The fixed term reads attribute BASES through AttributeFormula.GetFormula(creature, skill, current:
        /// false) - the same lookup CreatureSkill.Base uses - and only for a skill that IsUsable, exactly as
        /// CreatureSkill.Base does; an unusable (Untrained, non-usable) skill is skipped, since its InitLevel
        /// does not carry the attribute term the standard's effective median measured. Ranks ride in the term.
        ///
        /// <paramref name="defenseCapOffset"/> is the plan's dynamic_dungeons_defense_skill_cap_offset and
        /// <paramref name="applyCap"/> mirrors CombatTraitGuard's own gate (the strip is on): without it there
        /// is no ceiling anywhere in the run and none is introduced here. The ceiling is a runtime clamp
        /// (Creature.DefenseSkillCeilings), never an InitLevel rewrite, and is set wherever the creature's
        /// UNCLAMPED base now exceeds the cap - which closes the gap that the strip-time check never saw an
        /// uplift-raised defense skill.
        /// </summary>
        internal static (int SkillsToppedUp, int CeilingsSet) TopUpEffectiveSkillsAndCeil(Creature creature, DungeonBandStandard standard,
            double defenseCapOffset, bool applyCap)
        {
            if (creature == null || standard == null || standard.IsEmpty)
                return (0, 0);

            uint CapOf(ACE.Entity.Enum.Skill skill)
                => applyCap ? DungeonCombatNormalizer.DefenseSkillCap(standard, skill, defenseCapOffset) : 0u;

            var topped = TopUpEffectiveSkills(standard,
                skill =>
                {
                    var s = creature.GetCreatureSkill(skill, false);
                    return s != null && s.IsUsable ? s.InitLevel : (uint?)null;
                },
                skill =>
                {
                    var s = creature.GetCreatureSkill(skill, false);
                    return s == null ? 0u : AttributeFormula.GetFormula(creature, skill, false) + s.Ranks;
                },
                CapOf,
                (skill, value) => creature.GetCreatureSkill(skill, false).InitLevel = value);

            var toSet = CeilingsToSet(standard, defenseCapOffset, applyCap,
                skill =>
                {
                    var s = creature.GetCreatureSkill(skill, false);

                    // UNCLAMPED base: CreatureSkill.Base already applies any ceiling, which would hide an overshoot.
                    return s != null && s.IsUsable ? AttributeFormula.GetFormula(creature, skill, false) + s.InitLevel + s.Ranks : (uint?)null;
                },
                creature.GetDefenseSkillCeiling);

            foreach (var (skill, cap) in toSet)
                (creature.DefenseSkillCeilings ??= new Dictionary<ACE.Entity.Enum.Skill, uint>())[skill] = cap;

            return (topped, toSet.Count);
        }

        /// <summary>
        /// The defense-ceiling decision (pure over delegates): for each defense skill, the (skill, cap) pairs a
        /// creature needs a ceiling written for. A skill qualifies when <paramref name="applyCap"/> is on, the
        /// cap is non-zero (DefenseSkillCap returns 0 for an empty standard, no effective median, or a negative
        /// offset), <paramref name="rawOf"/> returns a value (null = absent or unusable) STRICTLY above the cap,
        /// and the creature does not already carry a ceiling equal to the cap (re-setting it is a no-op that
        /// must not be counted).
        /// </summary>
        internal static List<(ACE.Entity.Enum.Skill Skill, uint Cap)> CeilingsToSet(DungeonBandStandard standard, double defenseCapOffset, bool applyCap,
            Func<ACE.Entity.Enum.Skill, uint?> rawOf, Func<ACE.Entity.Enum.Skill, uint?> existingCeilingOf)
        {
            var result = new List<(ACE.Entity.Enum.Skill, uint)>();

            if (!applyCap || standard == null || standard.IsEmpty)
                return result;

            foreach (var skill in DungeonCombatNormalizer.DefenseSkills)
            {
                var cap = DungeonCombatNormalizer.DefenseSkillCap(standard, skill, defenseCapOffset);

                if (cap == 0)
                    continue;

                var raw = rawOf(skill);

                if (raw == null || raw.Value <= cap)
                    continue;

                if (existingCeilingOf(skill) == cap)
                    continue;

                result.Add((skill, cap));
            }

            return result;
        }

        /// <summary>
        /// The level write (pure): <paramref name="upliftLevel"/> when it exceeds the creature's current level
        /// (null counts as 0), else null for "leave it". Upward-only, so a creature authored above the band is
        /// never lowered.
        /// </summary>
        internal static int? LevelFloor(int? currentLevel, int upliftLevel)
            => upliftLevel > (currentLevel ?? 0) ? upliftLevel : (int?)null;

        /// <summary>
        /// The per-skill InitLevel floors (pure over delegates): for every band median above zero, a skill the
        /// creature ALREADY carries (<paramref name="initOf"/> returns non-null) and whose InitLevel is below the
        /// median is set to the median. Never adds a skill and never lowers one. Returns how many were raised.
        /// </summary>
        internal static int RaiseSkillFloors(IReadOnlyDictionary<ACE.Entity.Enum.Skill, uint> medians,
            Func<ACE.Entity.Enum.Skill, uint?> initOf, Action<ACE.Entity.Enum.Skill, uint> setInit)
        {
            var raised = 0;

            foreach (var kvp in medians)
            {
                if (kvp.Value == 0)
                    continue;

                var init = initOf(kvp.Key);

                if (init == null || kvp.Value <= init.Value)
                    continue;

                setInit(kvp.Key, kvp.Value);
                raised++;
            }

            return raised;
        }

        /// <summary>
        /// Scales a creature's body-part DVal and BaseArmor toward the band standard's own maxima, and
        /// returns how many parts were rewritten.
        ///
        /// CLONES THE WHOLE COLLECTION BEFORE TOUCHING IT, and this is not optional. PropertiesBodyPart is
        /// shared BY REFERENCE with the CACHED weenie (WorldObject.cs passes
        /// referenceWeenieCollectionsForCommonProperties: true and ACE.Entity/Adapter/WeenieConverter.cs then
        /// does a bare `result.PropertiesBodyPart = weenie.PropertiesBodyPart`). Mutating a part in place would
        /// raise the damage and armour of EVERY instance of that creature server-wide, on retail landblocks
        /// included, until the world cache was invalidated. Both the dictionary AND each PropertiesBodyPart
        /// value have to be copied: a fresh dictionary holding the same value objects would still write
        /// straight through into the cached weenie.
        ///
        /// Nothing caches the old references. Every combat read goes through Biota.PropertiesBodyPart at the
        /// moment it is needed, so replacing the collection before EnterWorld is complete and per-instance.
        ///
        /// Takes the biota rather than the Creature so the clone-not-mutate rule is unit-testable. The
        /// parameter is the RUNTIME biota (ACE.Entity.Models.Biota), never the EF entity of the same name.
        /// </summary>
        internal static int ApplyBodyPartUplift(ACE.Entity.Models.Biota biota, uint standardMaxDamage, uint standardMaxArmor)
        {
            var parts = biota?.PropertiesBodyPart;

            if (parts == null || parts.Count == 0)
                return 0;

            var ownMaxDamage = 0;
            var ownMaxArmor = 0;

            foreach (var kvp in parts)
            {
                if (kvp.Value == null)
                    continue;

                if (kvp.Value.DVal > ownMaxDamage) ownMaxDamage = kvp.Value.DVal;
                if (kvp.Value.BaseArmor > ownMaxArmor) ownMaxArmor = kvp.Value.BaseArmor;
            }

            var damageRatio = UpliftRatio(ownMaxDamage, standardMaxDamage);
            var armorRatio = UpliftRatio(ownMaxArmor, standardMaxArmor);

            // Already at or above the standard on both axes: touch nothing, and in particular do NOT clone,
            // so an uplift that changes nothing also allocates nothing.
            if (damageRatio <= 1.0 && armorRatio <= 1.0)
                return 0;

            var fresh = new Dictionary<ACE.Entity.Enum.CombatBodyPart, PropertiesBodyPart>(parts.Count);
            var scaled = 0;

            foreach (var kvp in parts)
            {
                var part = kvp.Value?.Clone();

                if (part != null)
                {
                    part.DVal = ScaleBodyValue(part.DVal, damageRatio);
                    part.BaseArmor = ScaleBodyValue(part.BaseArmor, armorRatio);
                    scaled++;
                }

                fresh[kvp.Key] = part;
            }

            biota.PropertiesBodyPart = fresh;

            return scaled;
        }

        /// <summary>
        /// The upward-only ratio between a creature's own maximum on some axis and the band standard's. 1.0 -
        /// a no-op - whenever the standard has no data (0), whenever the creature has none (0 or less), and
        /// whenever the creature already matches or beats the standard. A zero own-maximum stays zero on
        /// purpose: a ratio cannot lift a zero, and inventing a bite the creature never had would be content,
        /// not normalization.
        /// </summary>
        internal static double UpliftRatio(int ownMax, uint standardMax)
        {
            if (standardMax == 0 || ownMax <= 0)
                return 1.0;

            var ratio = standardMax / (double)ownMax;

            return ratio > 1.0 ? ratio : 1.0;
        }

        /// <summary>
        /// One body-part value scaled by <paramref name="ratio"/>. The lower clamp is the ORIGINAL value, not
        /// zero: that makes "no uplift axis ever lowers a value" true by construction.
        /// </summary>
        internal static int ScaleBodyValue(int value, double ratio)
        {
            if (value <= 0 || double.IsNaN(ratio) || ratio <= 1.0)
                return value;

            return (int)Math.Clamp(Math.Round(value * ratio), value, int.MaxValue);
        }
    }
}
