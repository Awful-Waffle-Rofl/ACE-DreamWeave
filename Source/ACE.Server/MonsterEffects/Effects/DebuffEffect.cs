using System;
using System.Collections.Generic;

using ACE.Common;
using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Server.ClassAbilities.Abilities;
using ACE.Server.Entity;
using ACE.Server.WorldObjects;

namespace ACE.Server.MonsterEffects.Effects
{
    /// <summary>
    /// The token a "what=" arg names. Matched by <see cref="MonsterEffectSpec.GetEnum{T}"/> case-insensitively.
    /// Undef is not authorable - it is the sentinel <see cref="DebuffEffect.Validate"/> rejects a missing or
    /// unrecognised what= against.
    /// </summary>
    public enum MonsterEffectDebuffWhat
    {
        Undef,
        Imperil,
        Vuln,
        AttackSkills,
    }

    /// <summary>
    /// Phase 4: a landed hit, a landed spell hit, or an avoided attack has a chance to lay one of three
    /// debuff shapes on the other party - a body-armor Imperil, an element-matched resistance Vulnerability
    /// (element taken from the triggering hit's own damage type, the same "match the swing" rule
    /// RangeRampEffect and RangeRampEffect's Spellblade cousin use), or a flat all-attack-skills debuff in
    /// PocketSandAbility's shape.
    ///
    /// EVERY VARIANT WRITES THROUGH EnchantmentManager.AddClassAbilityDebuff, never CreateEnchantment/a real
    /// spell cast: no resist check runs (matching BreakArmorAbility's "unresistable Imperil" precedent, for
    /// a different reason - AddClassAbilityDebuff never rolls one), and no client dat lookup is needed
    /// (matching PocketSand's identity-borrowing, not BreakArmor's live Spell construction). That second
    /// property is load-bearing here, not incidental: per invariant 3 (every magnitude comes from spec
    /// only), mag= IS the applied StatModValue directly - not a spell-level index into a real spell's own
    /// dat-authored magnitude - so nothing here ever needs to construct a live Spell. The SpellId/
    /// SpellCategory used per variant below are fixed, dat-free identities (real enum members, chosen to
    /// match the mechanic's real retail shape for correct interop with Malediction's shape-based amplifier
    /// and with normal same-category stacking) borrowed purely for the enchantment's client-facing icon and
    /// stacking group - never read back for their own magnitude.
    ///
    /// on=avoid NEEDS A GRANTER ON THE SAME MONSTER - the pocket-sand shape is "the monster dodges, and the
    /// dodge blinds you", so something has to make it dodge. IMonsterAvoidance is dispatched from exactly one
    /// site, Creature.RollMonsterEffectAvoidance, which pools every carried effect's GetAvoidChance and rolls
    /// ONCE. This effect is not an avoidance GRANTER - it applies a debuff, it does not avoid attacks - so
    /// its GetAvoidChance is a pure 0.0, exactly as the design says it must, and a weenie authoring only
    /// "debuff ... on=avoid" pools zero and never reaches this hook. Author it alongside a real granter
    /// (avoid) and every pooled win reaches it: the dispatch site notifies EVERY carried handler rather than
    /// crediting one weighted winner, so a reactor is never asked to buy its notification with avoidance
    /// chance it does not supply. Verified by MonsterEffectAvoidIntegrationTests.
    /// </summary>
    public sealed class DebuffEffect : IMonsterEffect, IMonsterOutgoingHit, IMonsterSpellHit, IMonsterAvoidance
    {
        public string Kind => "debuff";

        public bool Validate(MonsterEffectSpec spec, out string error)
        {
            var what = spec.GetEnum("what", MonsterEffectDebuffWhat.Undef);
            if (what == MonsterEffectDebuffWhat.Undef)
            {
                error = "debuff requires a valid what= (imperil|vuln|attackskills)";
                return false;
            }

            if (spec.GetDouble("mag", 0.0) <= 0.0)
            {
                error = "debuff requires mag= > 0";
                return false;
            }

            if (spec.GetDouble("secs", 0.0) <= 0.0)
            {
                error = "debuff requires secs= > 0";
                return false;
            }

            var chance = spec.GetDouble("chance", 1.0);
            if (chance <= 0.0 || chance > 1.0)
            {
                error = "debuff chance= must be in (0, 1]";
                return false;
            }

            error = null;
            return true;
        }

        public void OnOutgoingHit(Creature attacker, Creature defender, DamageEvent damageEvent, MonsterEffectSpec spec, ref MonsterEffectState state)
        {
            if (damageEvent.Damage <= 0.0f)
                return;

            var trigger = spec.GetFlags("on", MonsterEffectTrigger.Hit);
            if (!trigger.HasFlag(MonsterEffectTrigger.Hit))
                return;

            Apply(attacker, defender, damageEvent.DamageType, spec);
        }

        public void OnSpellHit(Creature caster, Creature target, SpellProjectile projectile, ref float damage, MonsterEffectSpec spec, ref MonsterEffectState state)
        {
            var trigger = spec.GetFlags("on", MonsterEffectTrigger.Hit);
            if (!trigger.HasFlag(MonsterEffectTrigger.SpellHit))
                return;

            Apply(caster, target, projectile.Spell.DamageType, spec);
        }

        /// <summary>Never contributes avoidance chance - see the class doc comment for why on=avoid needs a granter.</summary>
        public double GetAvoidChance(Creature defender, Creature attacker, CombatType combatType, MonsterEffectSpec spec, ref MonsterEffectState state) => 0.0;

        public void OnAvoided(Creature defender, Creature attacker, MonsterEffectSpec spec, ref MonsterEffectState state)
        {
            var trigger = spec.GetFlags("on", MonsterEffectTrigger.Hit);
            if (!trigger.HasFlag(MonsterEffectTrigger.Avoid))
                return;

            // no landed-hit damage type is available here - vuln has no element to match, so a record
            // authored what=vuln on=avoid is accepted but applies nothing (ApplyVuln below returns early
            // for DamageType.Undef, same as a damage type with no single-element vuln).
            Apply(defender, attacker, DamageType.Undef, spec);
        }

        private static void Apply(Creature source, Creature target, DamageType damageType, MonsterEffectSpec spec)
        {
            if (target == null || target.IsDead)
                return;

            // source is always the monster carrying this record (every Apply call site passes it first), so
            // the proc chance is scaled by ITS ramp axis=procchance and capped there - see
            // Creature.ScaleMonsterEffectProcChance
            var clamped = source.ScaleMonsterEffectProcChance(spec.GetDouble("chance", 1.0));
            if (clamped <= 0.0)
                return;

            if (ThreadSafeRandom.Next(0.0f, 1.0f) > clamped)
                return;

            var mag = spec.GetDouble("mag", 0.0);
            if (mag <= 0.0)
                return;

            var secs = spec.GetDouble("secs", 0.0);
            if (secs <= 0.0)
                return;

            // CLAMPED PER SHAPE, because mag= is not one unit. imperil and attackskills spend it as POINTS
            // removed; vuln adds it to a resist MULTIPLIER, where the same number means something three
            // orders of magnitude different. A single cap loose enough for armor points would wave a
            // "what=vuln mag=50" typo (an easy slip for 0.5) straight through as a 51x multiplier for the
            // whole authored duration, which is why there are two tunables rather than one.
            switch (spec.GetEnum("what", MonsterEffectDebuffWhat.Undef))
            {
                case MonsterEffectDebuffWhat.Imperil:
                    ApplyImperil(source, target, Math.Min(mag, MonsterEffectCaps.DebuffCap), secs);
                    break;

                case MonsterEffectDebuffWhat.Vuln:
                    ApplyVuln(source, target, damageType, Math.Min(mag, MonsterEffectCaps.DebuffVulnCap), secs);
                    break;

                case MonsterEffectDebuffWhat.AttackSkills:
                    ApplyAttackSkills(source, target, Math.Min(mag, MonsterEffectCaps.DebuffCap), secs);
                    break;
            }
        }

        /// <summary>
        /// Identity only, cosmetic - a fixed mid-ladder Imperil rung for the enchantment's icon/name. The
        /// applied magnitude is mag=, never this spell's own dat-authored value.
        /// </summary>
        private const SpellId ImperilIdentity = SpellId.ImperilOther4;

        private const uint DebuffPowerLevel = 1;

        private static void ApplyImperil(Creature source, Creature target, double mag, double secs)
        {
            target.EnchantmentManager.AddClassAbilityDebuff(
                (uint)ImperilIdentity, DebuffPowerLevel, source, SpellCategory.ArmorValueLowering,
                EnchantmentTypeFlags.BodyArmorValue | EnchantmentTypeFlags.Additive, 0, -(float)mag, secs);
        }

        /// <summary>
        /// Per-element resist property and real Vulnerability category, matching the shape
        /// MaledictionAbility.AffectsSpell checks for a real Vulnerability spell (Float|Multiplicative on
        /// one of PropertyFloat.ResistSlash..ResistElectric, value above 1.0).
        /// </summary>
        private static readonly Dictionary<DamageType, (PropertyFloat Resist, SpellCategory Category)> VulnByElement = new Dictionary<DamageType, (PropertyFloat, SpellCategory)>
        {
            [DamageType.Slash] = (PropertyFloat.ResistSlash, SpellCategory.SlashVulnerability),
            [DamageType.Pierce] = (PropertyFloat.ResistPierce, SpellCategory.PierceVulnerability),
            [DamageType.Bludgeon] = (PropertyFloat.ResistBludgeon, SpellCategory.BludgeonVulnerability),
            [DamageType.Fire] = (PropertyFloat.ResistFire, SpellCategory.FireVulnerability),
            [DamageType.Cold] = (PropertyFloat.ResistCold, SpellCategory.ColdVulnerability),
            [DamageType.Acid] = (PropertyFloat.ResistAcid, SpellCategory.AcidVulnerability),
            [DamageType.Electric] = (PropertyFloat.ResistElectric, SpellCategory.ElectricVulnerability),
        };

        /// <summary>Identity only, cosmetic - a fixed mid-ladder rung, matching ImperilIdentity's own choice.</summary>
        private const uint VulnIdentityLevel = 4;

        private static void ApplyVuln(Creature source, Creature target, DamageType damageType, double mag, double secs)
        {
            // no single-element vuln for this damage type (a combo type, Undef from on=avoid, or an exotic
            // type like Health/Nether) - apply nothing rather than guess
            if (!VulnByElement.TryGetValue(damageType, out var mapped))
                return;

            var vulnId = ElementalRendAbility.GetVulnerabilitySpell(damageType, VulnIdentityLevel);
            if (vulnId == null)
                return;

            target.EnchantmentManager.AddClassAbilityDebuff(
                (uint)vulnId.Value, DebuffPowerLevel, source, mapped.Category,
                EnchantmentTypeFlags.Float | EnchantmentTypeFlags.Multiplicative, (uint)mapped.Resist, (float)(1.0 + mag), secs);
        }

        private static void ApplyAttackSkills(Creature source, Creature target, double mag, double secs)
        {
            target.EnchantmentManager.AddClassAbilityDebuff(
                (uint)SpellId.DF_Specialized_AttackDebuff, DebuffPowerLevel, source, SpellCategory.AttackModLowering,
                EnchantmentTypeFlags.Skill | EnchantmentTypeFlags.Additive | EnchantmentTypeFlags.AttackSkills, 0, -(float)mag, secs);
        }
    }
}
