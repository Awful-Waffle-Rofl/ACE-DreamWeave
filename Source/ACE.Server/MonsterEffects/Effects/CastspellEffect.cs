using ACE.Common;
using ACE.Entity.Enum;
using ACE.Server.Entity;
using ACE.Server.WorldObjects;

namespace ACE.Server.MonsterEffects.Effects
{
    /// <summary>
    /// Phase 5: a landed hit or a landed spell hit has a chance to cast an authored spell at the target -
    /// the monster-side equivalent of SpellbladeAbility/RunebladeAbility's cast-on-strike procs, but general
    /// rather than fixed to a war-spell family: spell= names any real SpellId by NAME (matched by
    /// MonsterEffectSpec.GetEnum the same way type=fire is on FlatDamageEffect/DotEffect), and the cast
    /// itself goes through WorldObject.TryCastSpell - a Creature needs no new plumbing to cast, since
    /// TryCastSpell/CreateSpellProjectiles/CreateEnchantment are already general WorldObject members, not
    /// Player-only. Spellblade/Runeblade are NOT the template for the cast call itself: both route through
    /// Player.CastClassAbilityProc, a Player-only latch that stamps Cascade visibility, which this handler
    /// must not touch (it is defined on Player, not WorldObject, and nothing here needs Cascade).
    ///
    /// tryResist stays TRUE (the default) - this is a normal cast, unlike DebuffEffect's hand-made
    /// enchantment entries, so it is resisted exactly like a retail monster spell would be.
    /// </summary>
    public sealed class CastspellEffect : IMonsterEffect, IMonsterOutgoingHit, IMonsterSpellHit
    {
        public string Kind => "castspell";

        public bool Validate(MonsterEffectSpec spec, out string error)
        {
            if (spec.GetEnum("spell", SpellId.Undef) == SpellId.Undef)
            {
                error = "castspell requires a valid spell= (SpellId name)";
                return false;
            }

            var chance = spec.GetDouble("chance", 0.0);
            if (chance <= 0.0 || chance > 1.0)
            {
                error = "castspell chance= must be in (0, 1]";
                return false;
            }

            error = null;
            return true;
        }

        public void OnOutgoingHit(Creature attacker, Creature defender, DamageEvent damageEvent, MonsterEffectSpec spec, ref MonsterEffectState state)
        {
            if (damageEvent.Damage <= 0.0f)
                return;

            if (!TryResolveCast(attacker, spec, MonsterEffectTrigger.Hit, out var spellId))
                return;

            if (defender == null || defender.IsDead)
                return;

            Cast(attacker, defender, spellId);
        }

        public void OnSpellHit(Creature caster, Creature target, SpellProjectile projectile, ref float damage, MonsterEffectSpec spec, ref MonsterEffectState state)
        {
            if (!TryResolveCast(caster, spec, MonsterEffectTrigger.SpellHit, out var spellId))
                return;

            if (target == null || target.IsDead)
                return;

            Cast(caster, target, spellId);
        }

        /// <summary>
        /// The roll-and-select half: true when the given trigger is authored in on= AND the (capped) chance
        /// rolls a hit, with the authored spell id out. Pure and dat-free - split out from the actual cast
        /// on purpose, because constructing a live Spell needs the client dat (DatManager.PortalDat), which
        /// this test assembly never loads. See MonsterEffectCastspellTests for where that limit is hit.
        /// </summary>
        internal static bool TryResolveCast(Creature caster, MonsterEffectSpec spec, MonsterEffectTrigger firedAs, out SpellId spellId)
        {
            spellId = SpellId.Undef;

            var trigger = spec.GetFlags("on", MonsterEffectTrigger.Hit);
            if (!trigger.HasFlag(firedAs))
                return false;

            var chance = spec.GetDouble("chance", 0.0);
            if (chance <= 0.0)
                return false;

            // scaled by the casting monster's ramp axis=procchance, then capped - see
            // Creature.ScaleMonsterEffectProcChance
            var clamped = caster.ScaleMonsterEffectProcChance(chance);
            if (clamped <= 0.0)
                return false;

            if (ThreadSafeRandom.Next(0.0f, 1.0f) > clamped)
                return false;

            var configured = spec.GetEnum("spell", SpellId.Undef);
            if (configured == SpellId.Undef)
                return false;

            spellId = configured;
            return true;
        }

        private static void Cast(Creature caster, Creature target, SpellId spellId)
        {
            var spell = new Spell(spellId);
            if (spell.NotFound)
                return;

            caster.TryCastSpell(spell, target);
        }
    }
}
