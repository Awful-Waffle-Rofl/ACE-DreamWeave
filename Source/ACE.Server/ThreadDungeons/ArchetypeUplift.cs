using System;
using System.Collections.Generic;
using System.Linq;

using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Server.Entity;
using ACE.Server.WorldObjects;
using ACE.Server.WorldObjects.Entity;

namespace ACE.Server.ThreadDungeons
{
    /// <summary>
    /// The Threads ARCHETYPE uplift (design steps 8-9, orchestrator-approved 2026-10-23; dynamic_dungeons_archetype_uplift):
    /// for an adaptive-band uplifted NON-boss creature, every axis is brought up against the standard of creatures of
    /// ITS OWN archetype (<see cref="DungeonArchetypeStandards"/>), not the cross-archetype band standard, so an uplifted
    /// caster lands on what casters at the run level carry and an uplifted tank on what tanks carry.
    ///
    /// THE ONE RULE, per axis, upward only (orchestrator rulings 2026-10-23, the band cap included) - with ONE exception,
    /// the ATTACK-SKILL CEILING (see the attack-skill axis below), which may LOWER an authored attack skill:
    /// <code>
    ///   target = max(own, min(max(own x k, rho x S_A(L)), cap x rho x S_A(L)))        k = rho x S_A(L) / S_A(l)
    /// </code>
    /// with cap = dynamic_dungeons_uplift_archetype_cap (1.25) on the OFFENSE axes, and cap = dynamic_dungeons_uplift_defense_cap
    /// (1.0) on the DEFENSIVE axes (health, body armour, the three defense skills, DamageResistRating, Endurance and Quickness;
    /// owner ruling 2026-10-23: "defensive axes aiming at the archetype median - these are very sensitive, and misses are
    /// very frustrating"). At the shipped 1.0 a defensive target is exactly max(own, rho_def x S_A(L)): the own x k term
    /// cannot lift it above the median. An offensive creature strong for its own level keeps that shape inside
    /// [standard, 1.25 x standard]; a value already above its cap is never lowered by the cap.
    /// where own is the creature's authored value, S_A(L) its source's standard at the run level, S_A(l) the same
    /// source's standard at the creature's own authored level, and rho the run's offense or defense ratio
    /// (dynamic_dungeons_uplift_offense_ratio / _defense_ratio). own x k keeps a creature that was above its own level's
    /// standard that far above the run level's; rho x S_A(L) brings everyone else up to it. A standard with no data (0)
    /// drops its term; a rho of 0 leaves the axis authored.
    ///
    /// THE RATIO SPLIT. Offense (rho_off): attack skills, melee/missile damage (body parts, weapons, ammunition), spell
    /// output, DamageRating, CritRating, CritDamageRating, and the attributes Strength, Coordination, Focus and Self.
    /// Defense (rho_def): defense skills, body armour, health, DamageResistRating, and the attributes Endurance and
    /// Quickness. The attribute split is a choice (the design left it open): Strength and Coordination drive the melee
    /// and missile attack formulas and the damage attribute mod, Focus and Self the magic attack formula, while Endurance
    /// is health and Quickness the defense formulas. Coordination and Self also feed defense formulas, but the effective
    /// defense skills are re-targeted on their own axis afterwards (InitLevel = target - attribute term), so the split only
    /// decides how much of each skill's target the attributes supply.
    ///
    /// AXES, and what each one writes:
    ///   * attack skills - CARRIED only (never adds a school the creature was not authored with), EFFECTIVE value
    ///     (attribute term + InitLevel + Ranks), written as InitLevel = target - attribute term over the raised attributes
    ///     (ReachUpStamp's InitFor approach). A skill with no median of its own borrows the highest attack median
    ///     (BandUplift.EffectiveTargetFor). On top of the band cap, every attack-skill target is held under the
    ///     ATTACK-SKILL CEILING (ArchetypeUpliftInfo.AttackSkillCeiling, orchestrator ruling 2026-10-23): the P75 of the
    ///     natural band's pooled effective attack skill at L, an absolute value (no rho) - attack skill is the steepest lever
    ///     (the hit-chance sigmoid), so neither the k term nor a ratio-transferred standard may invent attack skill natural
    ///     creatures at L do not carry. It is the ONLY axis allowed to go down (round-4 ruling): an authored attack skill
    ///     above the ceiling is lowered to it (InitLevel floors at 0, so a raised attribute term alone above the ceiling
    ///     still stands), and the spawner logs every lowering;
    ///   * defense skills - melee, missile and magic defense, ADDED when missing (a monster without the row still defends
    ///     with the untrained attribute term, CreatureSkill.IsUsable for a MinLevel-1 skill, so the row is added Untrained
    ///     and promoted to Trained only if the skill table says it is not usable untrained), each capped by
    ///     DungeonCombatNormalizer.DefenseSkillCap against the SOURCE standard at L (the #1173 offset) when the trait strip
    ///     is on. MAGIC DEFENSE reads the CROSS-ARCHETYPE standards at both ends (owner default: the retail curve as it is,
    ///     no archetype factor);
    ///   * damage - ONE target from the creature's LIVE own damage (code review 2026-10-23: the best of its body-part DVal
    ///     and every non-launcher source it carries - melee or thrown weapon Damage, ammunition Damage x the launcher's
    ///     DamageMod - since wielded treasure is rolled per instance) against the source's effective-damage standard; then
    ///     each source is scaled onto that target by its OWN ratio: the body parts by target / best DVal (cloned -
    ///     PropertiesBodyPart is shared with the cached weenie), each weapon or ammunition item by target / its effective
    ///     damage. Each is DIVIDED by the effect of the DamageRating the spell-output top-up added (DamageRatingOffset, the
    ///     engine's (100 + total) / 100 rating mod), so melee and missile land on their own target, and none goes below
    ///     its authored damage;
    ///   * armour - one multiplier from the best body-part BaseArmor, applied to every part in the same clone;
    ///   * spells - the book is raised to the source's spell tier (DungeonSpellTier.Raise, before the AoE strip, at the
    ///     spawner), THEN DamageRating is raised until the creature's spell output (best war/void projectile mean x
    ///     (1 + DR/100)) reaches its target - which covers untierable spells and books already at tier 8. Above the pivot
    ///     the output standard is FLAT at the pivot's (natural stamped picks never get spell scaling);
    ///   * ratings - DamageRating, DamageResistRating, CritRating and CritDamageRating floored to the source's standard
    ///     (a mean including zeros) BEFORE the gem adds its own additive ratings;
    ///   * health - the rule itself: the floor is the FULL archetype median rho_def x H_A(L) (replacing
    ///     dynamic_dungeons_trash_health_floor_ratio for this creature), then the cap, then the gem's multiplier; H on the
    ///     health curve (ArchetypeUpliftContext.HealthOnCurve). It replaces the run's normalize-then-floor for this creature;
    ///   * attributes - every authored (non-zero) attribute by the rule above, before the health block (Endurance feeds
    ///     max health), skipped for the creature when the raised Endurance would put the health target out of reach;
    ///   * every other carried skill (Run, Deception, ...) - floored to the source's InitLevel median (BandUplift's rule);
    ///   * level - floored at the uplift level.
    ///
    /// BandUplift.Apply is untouched (World Events). The pure cores below decide every number; the creature glue is thin.
    /// </summary>
    internal static class ArchetypeUplift
    {
        // ---- the rule (pure) -----------------------------------------------------------------------------------

        /// <summary>
        /// target = min(max(own, own x rho x S(L) / S(l), rho x S(L)), cap x rho x S(L)), and never below own (upward
        /// only still wins for a creature already above the cap). A non-positive or garbled own reads as 0; S(L) of 0 (no
        /// data) leaves own; S(l) of 0 drops the ratio term; a rho that is not a positive finite number leaves own; a cap
        /// that is not a positive finite number applies no cap (orchestrator ruling 2026-10-23,
        /// dynamic_dungeons_uplift_archetype_cap: the archetype's shape is kept inside [median, cap x median]).
        /// </summary>
        internal static double AxisTarget(double own, double runStandard, double ownStandard, double rho, double cap = 0.0)
        {
            if (double.IsNaN(own) || double.IsInfinity(own) || own < 0)
                own = 0;

            if (double.IsNaN(rho) || double.IsInfinity(rho) || rho <= 0)
                return own;

            if (double.IsNaN(runStandard) || double.IsInfinity(runStandard) || runStandard <= 0)
                return own;

            var target = Math.Max(own, rho * runStandard);

            if (ownStandard > 0 && !double.IsInfinity(ownStandard))
                target = Math.Max(target, own * rho * runStandard / ownStandard);

            if (cap > 0 && !double.IsInfinity(cap) && !double.IsNaN(cap))
                target = Math.Min(target, cap * rho * runStandard);

            return Math.Max(own, target);
        }

        /// <summary>target / own when the target is above a positive own, else 1.0 (a zero is never lifted by a ratio).</summary>
        internal static double Multiplier(double own, double target)
            => own > 0 && target > own ? target / own : 1.0;

        /// <summary>
        /// A rating's floored value (pure): the rule over max(0, own), rounded half away from zero, never below own. When
        /// the rule raises nothing (no standard, a rho of 0, or a value already at its target) the AUTHORED value is
        /// returned as it is, so an authored negative rating is not lifted to 0 by an axis that has no data.
        /// </summary>
        internal static int RatingTarget(int own, double runStandard, double ownStandard, double rho, double cap = 0.0)
        {
            var floor = Math.Max(0, own);
            var target = AxisTarget(floor, runStandard, ownStandard, rho, cap);

            if (!(target > floor))
                return own;

            return Math.Max(own, (int)Math.Clamp(Math.Round(target, MidpointRounding.AwayFromZero), int.MinValue, int.MaxValue));
        }

        /// <summary>
        /// The DamageRating that lifts a caster's spell output to its target (pure): own output = <paramref name="ownMean"/>
        /// x (1 + max(0, DR) / 100); the target is the rule over the output standards; the returned rating is the smallest
        /// that reaches the target, never below <paramref name="currentRating"/>. No war/void projectile (mean 0) changes
        /// nothing.
        /// </summary>
        internal static int SpellDamageRating(double ownMean, int currentRating, double runOutput, double ownOutput, double rho, double cap = 0.0)
        {
            if (!(ownMean > 0) || double.IsInfinity(ownMean))
                return currentRating;

            var ownOut = ownMean * (1.0 + Math.Max(0, currentRating) / 100.0);
            var target = AxisTarget(ownOut, runOutput, ownOutput, rho, cap);

            if (target <= ownOut)
                return currentRating;

            var needed = Math.Ceiling(100.0 * (target / ownMean - 1.0) - 1e-9);

            return Math.Max(currentRating, (int)Math.Clamp(needed, 0, int.MaxValue));
        }

        /// <summary>
        /// The health BASE (pure, before the gem's multiplier), orchestrator ruling 2026-10-23: the floor is the FULL
        /// archetype median rho_def x H(L) (it replaces dynamic_dungeons_trash_health_floor_ratio for an archetype-uplifted
        /// creature), so the base is the axis rule itself - max(authored, authored x rho_def x H(L) / H(l), rho_def x H(L)),
        /// then the cap, never below authored.
        /// </summary>
        internal static double HealthBase(uint authored, double runHealth, double ownHealth, double defenseRatio, double cap)
            => AxisTarget(authored, runHealth, ownHealth, defenseRatio, cap);

        /// <summary>The health WRITE target (pure): the base times the gem's multiplier, rounded, at least 1.</summary>
        internal static int HealthTarget(uint authored, ArchetypeUpliftInfo info, double hpMult)
        {
            var baseHealth = HealthBase(authored, info.Run.Health, info.Own.Health, info.DefenseRatio, info.DefenseCapRatio);
            var mult = double.IsNaN(hpMult) || double.IsInfinity(hpMult) || hpMult <= 0 ? 1.0 : hpMult;

            return (int)Math.Clamp(Math.Round(baseHealth * mult), 1, int.MaxValue);
        }

        /// <summary>The ratio an attribute rides (see the class comment's split).</summary>
        internal static double AttributeRatio(PropertyAttribute attribute, ArchetypeUpliftInfo info)
            => attribute == PropertyAttribute.Endurance || attribute == PropertyAttribute.Quickness ? info.DefenseRatio : info.OffenseRatio;

        /// <summary>The cap an attribute rides: the DEFENSE cap for Endurance and Quickness, the offense (band) cap otherwise.</summary>
        internal static double AttributeCap(PropertyAttribute attribute, ArchetypeUpliftInfo info)
            => attribute == PropertyAttribute.Endurance || attribute == PropertyAttribute.Quickness ? info.DefenseCapRatio : info.CapRatio;

        /// <summary>True for melee and missile defense, which read the SOURCE standard; magic defense reads the cross-archetype one.</summary>
        internal static bool UsesArchetypeDefense(Skill skill) => skill == Skill.MeleeDefense || skill == Skill.MissileDefense;

        /// <summary>The run-level and own-level EFFECTIVE standards one attack or defense skill is measured against (pure).</summary>
        internal static (double Run, double Own) SkillStandards(ArchetypeUpliftInfo info, Skill skill)
        {
            if (Array.IndexOf(DungeonCombatNormalizer.DefenseSkills, skill) >= 0)
            {
                return UsesArchetypeDefense(skill)
                    ? (info.Run.Core.EffectiveDefenseMedianFor(skill), info.Own.Core.EffectiveDefenseMedianFor(skill))
                    : (info.AllRun.EffectiveDefenseMedianFor(skill), info.AllOwn.EffectiveDefenseMedianFor(skill));
            }

            return (BandUplift.EffectiveTargetFor(info.Run.Core, skill), BandUplift.EffectiveTargetFor(info.Own.Core, skill));
        }

        /// <summary>
        /// One skill's EFFECTIVE target (pure): the rule over the skill's standards at the right ratio, and for a defense
        /// skill clamped to <paramref name="cap"/> (0 = no cap) but never below its own effective value.
        /// </summary>
        internal static uint SkillTarget(ArchetypeUpliftInfo info, Skill skill, uint ownEffective, uint cap)
        {
            var isDefense = Array.IndexOf(DungeonCombatNormalizer.DefenseSkills, skill) >= 0;
            var rounded = UnceiledSkillTarget(info, skill, ownEffective, isDefense);

            if (isDefense && cap > 0)
                rounded = Math.Max(ownEffective, Math.Min(rounded, cap));

            // The attack-skill ceiling (orchestrator rulings 2026-10-23): an absolute P75 of natural attack skill at L, on
            // top of the band cap, so neither the k term nor a ratio-transferred standard invents attack skill above what
            // natural creatures at the run level carry. It is the ONE axis allowed to go down (round 4): an authored
            // attack skill above the ceiling is LOWERED to it (this path only ever runs for an uplifted non-boss entry).
            if (!isDefense && CeilingApplies(info))
                return Math.Min(rounded, info.AttackSkillCeiling);

            return Math.Max(ownEffective, rounded);
        }

        /// <summary>
        /// Whether the attack-skill ceiling is in force (pure): a ceiling exists AND the offense ratio is a positive finite
        /// number - rho_off 0 is the documented "leave offense authored", which must hold for the one axis that can lower
        /// (code review 2026-10-23).
        /// </summary>
        internal static bool CeilingApplies(ArchetypeUpliftInfo info)
            => info.AttackSkillCeiling > 0 && info.OffenseRatio > 0 && !double.IsNaN(info.OffenseRatio) && !double.IsInfinity(info.OffenseRatio);

        /// <summary>The rule's skill target before any defense cap or the attack-skill ceiling (pure, rounded).</summary>
        private static uint UnceiledSkillTarget(ArchetypeUpliftInfo info, Skill skill, uint ownEffective, bool isDefense)
        {
            var (run, own) = SkillStandards(info, skill);
            var target = AxisTarget(ownEffective, run, own, isDefense ? info.DefenseRatio : info.OffenseRatio, isDefense ? info.DefenseCapRatio : info.CapRatio);

            return Math.Max(ownEffective, (uint)Math.Clamp(Math.Round(target, MidpointRounding.AwayFromZero), 0, uint.MaxValue));
        }

        /// <summary>True when the attack-skill ceiling, not the rule, decides an ATTACK skill's target (pure; for the log).</summary>
        internal static bool AttackCeilingBinds(ArchetypeUpliftInfo info, Skill skill, uint ownEffective)
            => CeilingApplies(info)
               && Array.IndexOf(DungeonCombatNormalizer.DefenseSkills, skill) < 0
               && UnceiledSkillTarget(info, skill, ownEffective, false) > info.AttackSkillCeiling;

        /// <summary>True when the attack-skill ceiling LOWERS an authored attack skill (pure; the creature was authored above it).</summary>
        internal static bool AttackCeilingLowers(ArchetypeUpliftInfo info, Skill skill, uint ownEffective)
            => CeilingApplies(info)
               && Array.IndexOf(DungeonCombatNormalizer.DefenseSkills, skill) < 0
               && ownEffective > info.AttackSkillCeiling;

        /// <summary>The defense cap one defense skill is clamped to (pure): DefenseSkillCap against the skill's run-level standard, or 0.</summary>
        internal static uint DefenseCapFor(ArchetypeUpliftInfo info, Skill skill, double capOffset, bool applyCap)
            => applyCap ? DungeonCombatNormalizer.DefenseSkillCap(UsesArchetypeDefense(skill) ? info.Run.Core : info.AllRun, skill, capOffset) : 0u;

        /// <summary>
        /// Whether a defense ceiling must be (re)written after the uplift (pure), and at what: when the cap is on and
        /// non-zero, and the creature's UNCLAMPED effective value now exceeds it, or an existing ceiling (the trait strip's,
        /// cut against the cross-archetype standard) sits below the value this uplift just wrote. Null = leave it.
        /// </summary>
        internal static uint? CeilingFor(uint cap, uint rawAfter, uint? existing, bool raised)
        {
            if (cap == 0)
                return null;

            if (rawAfter > cap && existing != cap)
                return cap;

            if (raised && existing.HasValue && existing.Value < rawAfter)
                return cap;

            return null;
        }

        // ---- creature glue ---------------------------------------------------------------------------------------

        /// <summary>What the uplift measured on the AUTHORED creature, before any attribute was raised.</summary>
        internal sealed class Session
        {
            public ArchetypeUpliftInfo Info;
            public Dictionary<Skill, uint> OwnEffective = new Dictionary<Skill, uint>();
            public bool AttributesSkipped;
            public int AttributesRaised;
            public uint AuthoredHealthMax;
            public int HealthTarget;

            /// <summary>
            /// The DamageRating <see cref="FloorRatings"/> added ABOVE the rating axis's own floor purely for spell-output
            /// parity. <see cref="Apply"/> divides physical damage by its effect so melee and missile stay on their own
            /// targets (orchestrator ruling 2026-10-23: no double dip).
            /// </summary>
            public int DamageRatingAddedForSpells;
        }

        // ---- the DamageRating offset (pure) ---------------------------------------------------------------------

        /// <summary>
        /// The engine's damage-rating modifier for a TOTAL rating (Creature.GetPositiveRatingMod, Creature_Rating.cs:34):
        /// (100 + r) / 100 for r >= 0, 100 / (100 - r) below. A monster's total is its DamageRating property (the gem's
        /// additive bonus included) plus enchantments; for a monster attacker DamageEvent combines it additively with
        /// neutral recklessness, sneak and heritage mods (DamageEvent.cs:394-399), so this ratio is the whole effect.
        /// </summary>
        internal static double RatingModOf(int rating) => rating >= 0 ? (100.0 + rating) / 100.0 : 100.0 / (100.0 - rating);

        /// <summary>
        /// The factor physical damage is multiplied by so that <paramref name="added"/> extra DamageRating (on a creature
        /// whose total is now <paramref name="totalAfter"/>) leaves physical damage where it would have been without it:
        /// mod(total - added) / mod(total). 1 when nothing was added.
        /// </summary>
        internal static double DamageRatingOffset(int totalAfter, int added)
            => added <= 0 ? 1.0 : RatingModOf(totalAfter - added) / RatingModOf(totalAfter);

        /// <summary>The physical (body part and weapon) multiplier: the axis multiplier times the offset, never below 1 (authored is the floor).</summary>
        internal static double PhysicalMultiplier(double axisMultiplier, double damageRatingOffset)
            => Math.Max(1.0, axisMultiplier * damageRatingOffset);

        /// <summary>The effective value the engine gives a defense skill the creature carries no row for: the attribute term when usable untrained, else 0.</summary>
        private static uint UntrainedEffective(Creature creature, Skill skill)
            => GameTables.SkillTable.SkillBaseHash.TryGetValue((uint)skill, out var record) && record?.MinLevel == 1
                ? AttributeFormula.GetFormula(creature, skill, false)
                : 0u;

        /// <summary>
        /// Opens the uplift for one creature: captures every attack and defense skill's AUTHORED effective value (unclamped,
        /// before the attribute raise) and the authored max health, and computes the health target the Endurance guard
        /// checks against. Call before <see cref="RaiseAttributes"/>.
        /// </summary>
        internal static Session Begin(Creature creature, ArchetypeUpliftInfo info, double hpMult)
        {
            var session = new Session { Info = info, AuthoredHealthMax = creature.Health.MaxValue };

            foreach (var skill in DungeonCombatNormalizer.AttackSkills)
            {
                var live = creature.GetCreatureSkill(skill, false);

                if (live != null && live.IsUsable)
                    session.OwnEffective[skill] = AttributeFormula.GetFormula(creature, skill, false) + live.InitLevel + live.Ranks;
            }

            foreach (var skill in DungeonCombatNormalizer.DefenseSkills)
            {
                var live = creature.GetCreatureSkill(skill, false);

                session.OwnEffective[skill] = live != null
                    ? (live.IsUsable ? AttributeFormula.GetFormula(creature, skill, false) : 0u) + live.InitLevel + live.Ranks
                    : UntrainedEffective(creature, skill);
            }

            session.HealthTarget = HealthTarget(session.AuthoredHealthMax, info, hpMult);

            return session;
        }

        /// <summary>
        /// The attribute targets (pure over delegates): every attribute carried with a non-zero base, by the rule at its
        /// ratio against the source's attribute medians. Only targets above the current base are returned.
        /// </summary>
        internal static Dictionary<PropertyAttribute, uint> AttributeTargets(ArchetypeUpliftInfo info, Func<PropertyAttribute, uint?> baseOf)
        {
            var targets = new Dictionary<PropertyAttribute, uint>();

            foreach (var attribute in BandUplift.PrimaryAttributes)
            {
                var current = baseOf(attribute);

                if (current == null || current.Value == 0)
                    continue;

                var target = AxisTarget(current.Value, info.Run.Core.AttributeMedianFor(attribute), info.Own.Core.AttributeMedianFor(attribute),
                    AttributeRatio(attribute, info), AttributeCap(attribute, info));
                var rounded = (uint)Math.Clamp(Math.Round(target, MidpointRounding.AwayFromZero), 0, uint.MaxValue);

                if (rounded > current.Value)
                    targets[attribute] = rounded;
            }

            return targets;
        }

        /// <summary>
        /// Raises the attributes (before the spawner's health block). Skipped outright when the raised Endurance's own
        /// health term would put <see cref="Session.HealthTarget"/> out of reach (ThreadDungeonSpawner's shared guard).
        /// Refills the vitals when anything moved.
        /// </summary>
        internal static int RaiseAttributes(Creature creature, Session session)
        {
            var targets = AttributeTargets(session.Info,
                attribute => creature.Attributes.TryGetValue(attribute, out var a) && a != null ? a.Base : (uint?)null);

            if (targets.TryGetValue(PropertyAttribute.Endurance, out var endurance)
                && !ThreadDungeonSpawner.UpliftAttributeRaiseKeepsHealthReachable(session.HealthTarget, endurance))
            {
                session.AttributesSkipped = true;
                return 0;
            }

            foreach (var kvp in targets)
            {
                var attribute = creature.Attributes[kvp.Key];
                attribute.StartingValue = kvp.Value;
                attribute.Ranks = 0;
            }

            if (targets.Count > 0)
            {
                creature.Health.Current = creature.Health.MaxValue;
                creature.Stamina.Current = creature.Stamina.MaxValue;
                creature.Mana.Current = creature.Mana.MaxValue;
            }

            session.AttributesRaised = targets.Count;

            return targets.Count;
        }

        /// <summary>What the rating floor did, for the log line.</summary>
        internal readonly struct RatingResult
        {
            public RatingResult(int drFrom, int drTo, int drSpell, int drrFrom, int drrTo, int crFrom, int crTo, int cdrFrom, int cdrTo, double spellMean,
                int drRating = 0)
            {
                DrFrom = drFrom; DrTo = drTo; DrSpell = drSpell; DrrFrom = drrFrom; DrrTo = drrTo;
                CrFrom = crFrom; CrTo = crTo; CdrFrom = cdrFrom; CdrTo = cdrTo; SpellMean = spellMean; DrRating = drRating;
            }

            public int DrFrom { get; }
            public int DrTo { get; }

            /// <summary>The DamageRating the rating axis alone would floor to (before the spell-output top-up).</summary>
            public int DrRating { get; }

            /// <summary>The DamageRating the spell-output axis alone would need.</summary>
            public int DrSpell { get; }
            public int DrrFrom { get; }
            public int DrrTo { get; }
            public int CrFrom { get; }
            public int CrTo { get; }
            public int CdrFrom { get; }
            public int CdrTo { get; }
            public double SpellMean { get; }
        }

        /// <summary>The best war/void projectile mean in a FINAL spell book (pure over the mean delegate).</summary>
        internal static double BestProjectileMean(IEnumerable<int> spellIds, Func<int, double> meanOf)
        {
            if (spellIds == null || meanOf == null)
                return 0.0;

            var best = 0.0;

            foreach (var id in spellIds)
                best = Math.Max(best, meanOf(id));

            return best;
        }

        /// <summary>
        /// The DamageRating floor (pure, <see cref="FloorRatings"/>'s own rule, shared with the damage ceiling's prediction):
        /// the rating axis's floor <c>DrRating</c>, what the spell-output axis needs for a final book whose best war/void
        /// projectile mean is <paramref name="spellMean"/> (<c>DrSpell</c>), the written value <c>DrTo</c> (the larger), and
        /// <c>Added</c>, how much of it sits above max(authored, rating floor) purely for spell parity - the amount
        /// <see cref="DamageRatingOffset"/> later divides physical damage by.
        /// </summary>
        internal static (int DrTo, int DrRating, int DrSpell, int Added) DamageRatingFloor(ArchetypeUpliftInfo info, int authoredRating, double spellMean)
        {
            var drSpell = SpellDamageRating(spellMean, authoredRating, info.Run.SpellOutput, info.Own.SpellOutput, info.OffenseRatio, info.CapRatio);
            var drRating = RatingTarget(authoredRating, info.Run.DamageRating, info.Own.DamageRating, info.OffenseRatio, info.CapRatio);
            var drTo = Math.Max(drRating, drSpell);

            return (drTo, drRating, drSpell, Math.Max(0, drTo - Math.Max(authoredRating, drRating)));
        }

        /// <summary>
        /// Floors the four ratings to the source standard, DamageRating also to what the spell-output axis needs, and
        /// records on the session how much DamageRating the spell top-up added above the rating floor. Call BEFORE the gem
        /// adds its own ratings, and after the AoE strip (the spell output is read off the FINAL book).
        /// </summary>
        internal static RatingResult FloorRatings(Creature creature, Session session, Func<int, double> projectileMeanOf)
        {
            var info = session.Info;
            var cap = info.CapRatio;

            var dr = creature.DamageRating ?? 0;
            var drr = creature.DamageResistRating ?? 0;
            var cr = creature.CritRating ?? 0;
            var cdr = creature.CritDamageRating ?? 0;

            var mean = BestProjectileMean(creature.Biota.PropertiesSpellBook?.Keys, projectileMeanOf);
            var (drTo, drRating, drSpell, added) = DamageRatingFloor(info, dr, mean);
            var drrTo = RatingTarget(drr, info.Run.DamageResistRating, info.Own.DamageResistRating, info.DefenseRatio, info.DefenseCapRatio);
            var crTo = RatingTarget(cr, info.Run.CritRating, info.Own.CritRating, info.OffenseRatio, cap);
            var cdrTo = RatingTarget(cdr, info.Run.CritDamageRating, info.Own.CritDamageRating, info.OffenseRatio, cap);

            session.DamageRatingAddedForSpells = added;

            if (drTo != dr) creature.DamageRating = drTo;
            if (drrTo != drr) creature.DamageResistRating = drrTo;
            if (crTo != cr) creature.CritRating = crTo;
            if (cdrTo != cdr) creature.CritDamageRating = cdrTo;

            return new RatingResult(dr, drTo, drSpell, drr, drrTo, cr, crTo, cdr, cdrTo, mean, drRating);
        }

        /// <summary>What <see cref="Apply"/> did, for the log line.</summary>
        internal sealed class Result
        {
            public int SkillsRetargeted;
            public int DefenseRowsAdded;
            public int CeilingsSet;
            public int OtherSkillsFloored;
            public int PartsScaled;
            public int WeaponsScaled;
            /// <summary>The damage AXIS multiplier (effective max damage onto its target), before the DamageRating offset.</summary>
            public double DamageMultiplier = 1.0;

            /// <summary>
            /// The DamageRating offset (DamageRatingOffset) and the multiplier written to the BODY PARTS (each weapon and
            /// ammunition item gets its own, onto the same <see cref="DamageTarget"/>).
            /// </summary>
            public double DamageRatingOffset = 1.0;
            public double PhysicalMultiplier = 1.0;

            /// <summary>The live own damage the rule read (best of body DVal and weapon/ammo sources) and the target it gave.</summary>
            public double OwnLiveDamage;
            public double DamageTarget;

            public double ArmorMultiplier = 1.0;
            public readonly Dictionary<Skill, (uint From, uint To)> Skills = new Dictionary<Skill, (uint, uint)>();

            /// <summary>The attack skills whose target the attack-skill ceiling held down (ArchetypeUpliftInfo.AttackSkillCeiling).</summary>
            public readonly List<Skill> AttackCeilingBound = new List<Skill>();

            /// <summary>The subset of <see cref="AttackCeilingBound"/> authored ABOVE the ceiling, and so lowered to it.</summary>
            public readonly List<Skill> AttackCeilingLowered = new List<Skill>();
        }

        /// <summary>
        /// The skill, damage, armour and level half (the spawner's uplift site, after the ratings): level floor, attack and
        /// defense skills re-targeted on the raised attributes, missing defense rows added, defense ceilings re-checked,
        /// every other carried skill floored, body parts and weapons scaled.
        /// </summary>
        internal static Result Apply(Creature creature, Session session, int upliftLevel, double capOffset, bool applyCap)
        {
            var info = session.Info;
            var result = new Result();

            var raisedLevel = BandUplift.LevelFloor(creature.Level, upliftLevel);

            if (raisedLevel.HasValue)
                creature.Level = raisedLevel.Value;

            // Attack skills: carried and usable only (the session captured exactly those).
            foreach (var skill in DungeonCombatNormalizer.AttackSkills)
            {
                if (!session.OwnEffective.TryGetValue(skill, out var own))
                    continue;

                var live = creature.GetCreatureSkill(skill, false);

                if (live == null)
                    continue;

                var target = SkillTarget(info, skill, own, 0);

                if (AttackCeilingBinds(info, skill, own))
                    result.AttackCeilingBound.Add(skill);

                if (AttackCeilingLowers(info, skill, own))
                    result.AttackCeilingLowered.Add(skill);

                var init = DungeonStatCurve.InitFor(target, AttributeFormula.GetFormula(creature, skill, false) + live.Ranks);

                if (init != live.InitLevel)
                {
                    live.InitLevel = init;
                    result.SkillsRetargeted++;
                }

                result.Skills[skill] = (own, target);
            }

            // Defense skills: every one, added when missing.
            foreach (var skill in DungeonCombatNormalizer.DefenseSkills)
            {
                var own = session.OwnEffective.TryGetValue(skill, out var o) ? o : 0u;
                var cap = DefenseCapFor(info, skill, capOffset, applyCap);
                var target = SkillTarget(info, skill, own, cap);
                var live = creature.GetCreatureSkill(skill, false);
                var raised = false;

                if (target > own)
                {
                    if (live == null)
                    {
                        live = creature.GetCreatureSkill(skill, true);
                        result.DefenseRowsAdded++;

                        // A defense skill that is not usable untrained would drop its attribute term; train it so the
                        // written InitLevel lands on the effective target.
                        if (!live.IsUsable)
                            live.AdvancementClass = SkillAdvancementClass.Trained;
                    }

                    var term = live.IsUsable ? AttributeFormula.GetFormula(creature, skill, false) : 0u;
                    var init = DungeonStatCurve.InitFor(target, term + live.Ranks);

                    if (init != live.InitLevel)
                    {
                        live.InitLevel = init;
                        result.SkillsRetargeted++;
                    }

                    raised = true;
                }

                result.Skills[skill] = (own, target);

                if (live == null)
                    continue;

                // UNCLAMPED: CreatureSkill.Base applies any ceiling, which would hide an overshoot.
                var rawAfter = (live.IsUsable ? AttributeFormula.GetFormula(creature, skill, false) : 0u) + live.InitLevel + live.Ranks;
                var ceiling = CeilingFor(cap, rawAfter, creature.GetDefenseSkillCeiling(skill), raised);

                if (ceiling.HasValue)
                {
                    (creature.DefenseSkillCeilings ??= new Dictionary<Skill, uint>())[skill] = ceiling.Value;
                    result.CeilingsSet++;
                }
            }

            // Every other carried skill (Run, Deception, ...): BandUplift's InitLevel floor against the source.
            var otherMedians = info.Run.Core.SkillMedians
                .Where(kvp => Array.IndexOf(DungeonCombatNormalizer.AttackSkills, kvp.Key) < 0 && Array.IndexOf(DungeonCombatNormalizer.DefenseSkills, kvp.Key) < 0)
                .ToDictionary(kvp => kvp.Key, kvp => kvp.Value);

            result.OtherSkillsFloored = BandUplift.RaiseSkillFloors(otherMedians,
                skill => creature.GetCreatureSkill(skill, false)?.InitLevel,
                (skill, value) => creature.GetCreatureSkill(skill, false).InitLevel = value);

            // Damage and armour. Armour: one multiplier from the best body-part BaseArmor.
            var ownArmor = 0;
            var bodyMax = 0;

            if (creature.Biota.PropertiesBodyPart != null)
                foreach (var part in creature.Biota.PropertiesBodyPart.Values)
                {
                    if (part == null)
                        continue;

                    ownArmor = Math.Max(ownArmor, part.BaseArmor);
                    bodyMax = Math.Max(bodyMax, part.DVal);
                }

            result.ArmorMultiplier = Multiplier(ownArmor,
                AxisTarget(ownArmor, info.Run.Core.MaxBaseArmor, info.Own.Core.MaxBaseArmor, info.DefenseRatio, info.DefenseCapRatio));

            // Damage (code review 2026-10-23): own damage is read off the LIVE prepared instance - the best of its body-part
            // DVal and every damage source it FIGHTS with (an equipped melee or thrown weapon's Damage, equipped ammunition
            // Damage x an equipped launcher's DamageMod), since its wielded treasure is rolled per instance and the weenie
            // facts never see it. A weapon in the PACK (treasure, or one EquipInventoryItems can swap in mid-fight) never
            // sets own - a loot drop must not inflate k - but it is still scaled onto the same target by its own ratio, so a
            // swap lands on it too. The rule gives ONE target from own; then every source is scaled onto the target by its
            // OWN ratio. Each is divided by the DamageRating the spell top-up added (read off the FINAL total, the gem's
            // additive bonus included) and never goes below its authored value.
            var sources = DamageSourcesOf(creature);

            result.OwnLiveDamage = LiveOwnDamage(bodyMax, sources.Where(s => s.SetsOwn).Select(s => s.Effective));
            result.DamageTarget = AxisTarget(result.OwnLiveDamage, info.Run.EffectiveDamage, info.Own.EffectiveDamage, info.OffenseRatio, info.CapRatio);
            result.DamageMultiplier = Multiplier(result.OwnLiveDamage, result.DamageTarget);

            result.DamageRatingOffset = DamageRatingOffset(creature.DamageRating ?? 0, session.DamageRatingAddedForSpells);
            result.PhysicalMultiplier = SourceMultiplier(bodyMax, result.DamageTarget, result.DamageRatingOffset);

            if (result.PhysicalMultiplier > 1.0 || result.ArmorMultiplier > 1.0)
                result.PartsScaled = ReachUpStamp.ScaleBodyParts(creature.Biota, result.PhysicalMultiplier, result.ArmorMultiplier);

            foreach (var source in sources)
            {
                var multiplier = SourceMultiplier(source.Effective, result.DamageTarget, result.DamageRatingOffset);
                var scaled = ReachUpStamp.ScaleInt(source.Damage, multiplier);

                if (scaled == source.Damage)
                    continue;

                source.Item.SetProperty(PropertyInt.Damage, scaled);
                result.WeaponsScaled++;
            }

            return result;
        }

        /// <summary>
        /// Every non-launcher item the creature carries (equipped or in its pack) with a positive Damage, and its effective
        /// damage: ammunition is multiplied by the launcher's DamageMod (the equipped launcher, else the first one carried),
        /// everything else is its own Damage. The damage sources <see cref="Apply"/> scales. <c>SetsOwn</c> marks the ones
        /// the creature fights with right now - an equipped non-ammunition item, or equipped ammunition with a launcher
        /// equipped - which are the only ones own damage is read from.
        /// </summary>
        internal static List<(WorldObject Item, int Damage, double Effective, bool SetsOwn)> DamageSourcesOf(Creature creature)
        {
            var equipped = creature.EquippedObjects.Values.Where(i => i != null).Distinct().ToList();
            var items = equipped.Concat(creature.Inventory.Values.Where(i => i != null)).Distinct().ToList();
            var equippedLauncher = creature.GetEquippedMissileLauncher();
            var launcher = equippedLauncher ?? items.FirstOrDefault(i => i is MissileLauncher);
            var launcherMod = LauncherDamageMod(launcher?.GetProperty(PropertyFloat.DamageMod));

            return items
                .Where(i => !(i is MissileLauncher))
                .Select(i => (Item: i, Damage: i.GetProperty(PropertyInt.Damage) ?? 0))
                .Where(s => s.Damage > 0)
                .Select(s => (s.Item, s.Damage, Effective: s.Damage * (s.Item is Ammunition ? launcherMod : 1.0),
                    SetsOwn: equipped.Contains(s.Item) && (!(s.Item is Ammunition) || equippedLauncher != null)))
                .ToList();
        }

        /// <summary>
        /// The creature's live effective damage right now: the best of its body-part DVal and the damage sources it fights
        /// with (<see cref="DamageSourcesOf"/>, SetsOwn only). Read before <see cref="Apply"/> it is the own value the rule
        /// uses; read after, it is what the creature ended up with.
        /// </summary>
        internal static double LiveEffectiveDamageOf(Creature creature)
        {
            var bodyMax = creature.Biota.PropertiesBodyPart?.Values.Where(p => p != null).Select(p => p.DVal).DefaultIfEmpty(0).Max() ?? 0;
            return LiveOwnDamage(bodyMax, DamageSourcesOf(creature).Where(s => s.SetsOwn).Select(s => s.Effective));
        }

        /// <summary>A launcher's DamageMod as the damage formula reads it: a missing or non-positive value is 1.</summary>
        internal static double LauncherDamageMod(double? damageMod)
            => damageMod.HasValue && damageMod.Value > 0 && !double.IsNaN(damageMod.Value) && !double.IsInfinity(damageMod.Value) ? damageMod.Value : 1.0;

        /// <summary>The creature's live own damage (pure): the best of its body-part DVal and its effective weapon/ammo sources.</summary>
        internal static double LiveOwnDamage(int bodyMax, IEnumerable<double> sourceEffective)
            => Math.Max(Math.Max(0, bodyMax), (sourceEffective ?? Enumerable.Empty<double>()).Where(v => !double.IsNaN(v)).DefaultIfEmpty(0).Max());

        /// <summary>
        /// One damage source's multiplier onto the damage target (pure): target / its own effective value, times the
        /// DamageRating offset, never below 1 (authored is the floor) and 1 for a source with no damage.
        /// </summary>
        internal static double SourceMultiplier(double sourceEffective, double target, double damageRatingOffset)
            => sourceEffective > 0 ? PhysicalMultiplier(Multiplier(sourceEffective, target), damageRatingOffset) : 1.0;

        // ---- the resist floor (dynamic_dungeons_uplift_resist_floor, 2026-10-27) ----------------------------------------

        private static bool Usable(double v) => !double.IsNaN(v) && !double.IsInfinity(v) && v > 0;

        /// <summary>
        /// One element's ResistX target (pure): an authored weakness (own above 1.0) is left as authored; otherwise the
        /// tougher of own and the archetype median, min(own, median), so the floor never raises a resist (never softens).
        /// A median of 0 or not finite (no data) leaves own.
        /// </summary>
        internal static double ResistTarget(double own, double median)
        {
            if (double.IsNaN(own) || double.IsInfinity(own) || !Usable(median) || own > 1.0)
                return own;

            return Math.Min(own, median);
        }

        /// <summary>
        /// One element's ArmorModVsX target (pure): max(own, median) when the creature has body armour to multiply (its
        /// FINAL best BaseArmor above 0), so the floor never lowers an armour mod; own otherwise, and for a median of 0 or
        /// not finite (no data).
        /// </summary>
        internal static double ArmorModTarget(double own, double median, int finalBaseArmor)
        {
            if (double.IsNaN(own) || double.IsInfinity(own) || finalBaseArmor <= 0 || !Usable(median))
                return own;

            return Math.Max(own, median);
        }

        /// <summary>
        /// The resist-floor writes for one archetype-uplifted creature (pure over <paramref name="get"/>, which reads the
        /// creature's current float property; absent reads 1.0), in a fixed order: the eight ResistX against
        /// info.Run.ResistMedians, then the eight ArmorModVsX against info.Run.ArmorModMedians. Identity (no writes) when
        /// info is null or rho_def is not a positive finite number (the documented "leave defense authored").
        ///
        /// When <paramref name="guard"/> is set (the trait strip is on):
        /// - a physical ArmorModVs (slash, pierce, bludgeon) is raised no higher than max(ceiling, own), so the floor can
        ///   never lift the group's smallest member above the ceiling (DungeonCombatNormalizer.ArmorModWrites' invariant);
        /// - when the floor wrote anything, the category-immunity guards (CategoryResistWrites and ArmorModWrites, with
        ///   the plan's own dials) are re-run over the floored values, so a floor that left a whole group immune is put
        ///   back inside the owner's invariant.
        /// Every final value is then clamped to never soften against the creature's PRE-floor value: a resist never ends
        /// above its own, an armour mod never below its own. So a guard write can only take back (part of) what the floor
        /// wrote - it can never raise an authored immunity the floor did not touch, nor scale down an authored armour mod.
        /// </summary>
        internal static List<(PropertyFloat Property, double From, double To)> ResistFloorWrites(ArchetypeUpliftInfo info, Func<PropertyFloat, double?> get,
            int finalBaseArmor, bool guard, double categoryResistFloor, double armorModCeiling)
        {
            var result = new List<(PropertyFloat, double, double)>();

            if (info?.Run == null || get == null || !Usable(info.DefenseRatio))
                return result;

            var writes = new Dictionary<PropertyFloat, double>();
            var order = new List<PropertyFloat>();

            double Own(PropertyFloat p) => get(p) ?? 1.0;
            double? Current(PropertyFloat p) => writes.TryGetValue(p, out var w) ? w : get(p);

            void Write(PropertyFloat p, double v)
            {
                if (!writes.ContainsKey(p))
                    order.Add(p);

                writes[p] = v;
            }

            var resistMedians = info.Run.ResistMedians;

            if (resistMedians != null && resistMedians.Count == ArchetypeFacts.ResistProperties.Length)
            {
                for (var i = 0; i < resistMedians.Count; i++)
                {
                    var p = ArchetypeFacts.ResistProperties[i];
                    var own = Own(p);
                    var target = ResistTarget(own, resistMedians[i]);

                    if (target < own)
                        Write(p, target);
                }
            }

            var armorMedians = info.Run.ArmorModMedians;

            if (armorMedians != null && armorMedians.Count == ArchetypeFacts.ArmorModProperties.Length)
            {
                for (var i = 0; i < armorMedians.Count; i++)
                {
                    var p = ArchetypeFacts.ArmorModProperties[i];
                    var own = Own(p);
                    var target = ArmorModTarget(own, armorMedians[i], finalBaseArmor);

                    if (guard && Usable(armorModCeiling) && Array.IndexOf(DungeonCombatNormalizer.PhysicalArmorMods, p) >= 0)
                        target = Math.Min(target, Math.Max(armorModCeiling, own));

                    if (target > own)
                        Write(p, target);
                }
            }

            if (writes.Count > 0 && guard)
            {
                foreach (var (p, v) in DungeonCombatNormalizer.CategoryResistWrites(Current, categoryResistFloor))
                    Write(p, v);

                foreach (var (p, v) in DungeonCombatNormalizer.ArmorModWrites(Current, armorModCeiling))
                    Write(p, v);
            }

            var resistSet = new HashSet<PropertyFloat>(ArchetypeFacts.ResistProperties);

            foreach (var p in order)
            {
                var from = Own(p);

                // never soften against the pre-floor value: resists never rise, armour mods never fall
                var to = resistSet.Contains(p) ? Math.Min(writes[p], from) : Math.Max(writes[p], from);

                if (Math.Abs(to - from) > 1e-12)
                    result.Add((p, from, to));
            }

            return result;
        }

        /// <summary>
        /// Applies the resist floor to one archetype-uplifted creature (the spawner's glue, right after <see cref="Apply"/>
        /// and before the weak point): the writes of <see cref="ResistFloorWrites"/> against the creature's FINAL best
        /// body-part BaseArmor, with the guards re-run when the plan strips combat traits. Returns what it wrote.
        /// </summary>
        internal static List<(PropertyFloat Property, double From, double To)> FloorResists(Creature creature, Session session, DungeonSpawnPlan plan)
        {
            var finalArmor = creature.Biota.PropertiesBodyPart?.Values.Where(p => p != null).Select(p => p.BaseArmor).DefaultIfEmpty(0).Max() ?? 0;

            var writes = ResistFloorWrites(session?.Info, p => creature.GetProperty(p), finalArmor,
                plan.StripCombatTraits, plan.CategoryResistFloor, plan.CategoryArmorModCeiling);

            foreach (var (property, _, to) in writes)
                creature.SetProperty(property, to);

            return writes;
        }

        /// <summary>The per-creature diagnostic (pure): archetype, source, per-axis multipliers and the rating floors.</summary>
        internal static string LogLine(uint wcid, int upliftLevel, int authoredLevel, Session session, Result result, RatingResult ratings, int spellsRaised, int healthWritten,
            IReadOnlyList<(PropertyFloat Property, double From, double To)> resistWrites = null)
        {
            var info = session.Info;

            string K(uint from, uint to) => from > 0 ? $"x{to / (double)from:0.###}" : (to > 0 ? $"+{to}" : "x1");

            var skills = string.Join(" ", result.Skills.OrderBy(k => k.Key).Where(k => k.Value.To != k.Value.From || k.Value.From > 0)
                .Select(k => $"{k.Key}:{k.Value.From}->{k.Value.To}({K(k.Value.From, k.Value.To)})"));

            var hpK = session.AuthoredHealthMax > 0 ? healthWritten / (double)session.AuthoredHealthMax : 0;

            return $"wcid {wcid} archetype-uplifted to level {upliftLevel} (authored {authoredLevel}) as {info.Archetype}" +
                   (info.Source != info.Archetype ? $" against {(info.Source == CreatureArchetype.Unclassified ? "All" : info.Source.ToString())}" : "") +
                   $" ({info.SourceDescription}, off x{info.OffenseRatio:0.##} def x{info.DefenseRatio:0.##} cap off x{info.CapRatio:0.##} def x{info.DefenseCapRatio:0.##}): " +
                   $"dmg own(live) {result.OwnLiveDamage:0.#} -> {result.DamageTarget:0.#} (x{result.DamageMultiplier:0.###}) dr-offset x{result.DamageRatingOffset:0.###} -> body x{result.PhysicalMultiplier:0.###} " +
                   $"arm x{result.ArmorMultiplier:0.###} hp {session.AuthoredHealthMax}->{healthWritten} (x{hpK:0.###}); " +
                   $"skills {skills}; " +
                   (result.AttackCeilingBound.Count > 0
                       ? $"attack-skill ceiling {info.AttackSkillCeiling} (P75 of natural at L) BOUND on {string.Join(",", result.AttackCeilingBound)}" +
                         (result.AttackCeilingLowered.Count > 0 ? $", LOWERED authored {string.Join(",", result.AttackCeilingLowered)}" : "") + "; "
                       : $"attack-skill ceiling {info.AttackSkillCeiling} not binding; ") +
                   $"{result.DefenseRowsAdded} defense row(s) added, {result.CeilingsSet} ceiling(s), {result.OtherSkillsFloored} other skill(s) floored, " +
                   $"{result.PartsScaled} part(s), {result.WeaponsScaled} weapon(s); attributes {(session.AttributesSkipped ? "skipped (health target unreachable)" : session.AttributesRaised + " raised")}; " +
                   $"spells {spellsRaised} raised to tier {info.Run.Core.SpellTier}, mean {ratings.SpellMean:0.#} dr-for-spells {ratings.DrSpell}; " +
                   $"dr {ratings.DrFrom}->{ratings.DrTo} drr {ratings.DrrFrom}->{ratings.DrrTo} cr {ratings.CrFrom}->{ratings.CrTo} cdr {ratings.CdrFrom}->{ratings.CdrTo} (before the gem's)" +
                   (resistWrites == null ? "" : resistWrites.Count == 0 ? "; resist floor: no change"
                       : $"; resist floor: {string.Join(" ", resistWrites.Select(w => $"{w.Property}:{w.From:0.###}->{w.To:0.###}"))}");
        }
    }
}
