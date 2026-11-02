using System;

using log4net;

using ACE.Entity.Enum;
using ACE.Server.Entity;
using ACE.Server.WeaponMods;
using ACE.Server.WorldObjects;
using ACE.Server.WorldObjects.Entity;

namespace ACE.Server.CombatSimulator
{
    /// <summary>
    /// The attacker-side terms every AttackerSpec builder shares - CreatureAttackerSpecs (monster melee, missile,
    /// spell) and PlayerAttackerSpecs (player melee, missile, spell). They live in ONE place so the builders cannot
    /// drift apart on the invariants the matchup table depends on:
    ///
    /// - PreMitigationMod is attributeMod * powerMod * slayerMod * damageRatingMod (times elementalMod on the magic
    ///   path only), the engine's "BaseDamage * AttributeMod * PowerMod * SlayerMod * DamageRatingMod" at
    ///   Entity\DamageEvent.cs (DoCalculateDamage, "damage before mitigation").
    /// - damageRatingMod is Creature.AdditiveCombine over the attacker's positive damage rating and the
    ///   recklessness, sneak attack and heritage terms, with recklessness and sneak attack at their neutral 1.0
    ///   (both read the defender or live positions) and heritage read from production code (always 1.0 for a
    ///   non-player: Creature.GetHeritageBonus returns false).
    /// - Physical crits read CritDamageMod (+1.0 base folded in); magic crits read MagicCritDamageMod (no +1.0).
    ///   See the AttackerSpec field comments.
    /// - Crit chance is clamped to [0, 1], the probability the engine's roll actually realises.
    ///
    /// Every term is read from the production function the engine calls; only the composition is written here.
    /// </summary>
    public static class AttackerTerms
    {
        private static readonly ILog log = LogManager.GetLogger(System.Reflection.MethodBase.GetCurrentMethod().DeclaringType);

        /// <summary>
        /// The shared PreMitigationMod composition. <paramref name="elementalMod"/> is the caster elemental damage
        /// modifier and belongs to the MAGIC path only (SpellProjectile.CalculateDamage multiplies the whole sum by
        /// it, so folding it here is exact); physical callers leave it at 1.0.
        /// </summary>
        public static float ComposePreMitigationMod(float attributeMod, float powerMod, float slayerMod, float damageRatingMod, float elementalMod = 1.0f)
        {
            return attributeMod * powerMod * slayerMod * damageRatingMod * elementalMod;
        }

        /// <summary>
        /// The non-crit physical damage rating mod, DamageEvent.DoCalculateDamage's
        /// "AdditiveCombine(DamageRatingBaseMod, RecklessnessMod, SneakAttackMod, HeritageMod)" with recklessness
        /// and sneak attack neutral. The crit branch's recomposition (crit damage rating in, recklessness out) is
        /// NOT modelled: AttackerSpec carries one PreMitigationMod for both branches.
        /// </summary>
        public static float PhysicalDamageRatingMod(Creature attacker, WorldObject weapon)
        {
            var heritageMod = attacker.GetHeritageBonus(weapon) ? 1.05f : 1.0f;

            return Creature.AdditiveCombine(Creature.GetPositiveRatingMod(attacker.GetDamageRating()), 1.0f, 1.0f, heritageMod);
        }

        /// <summary>
        /// The non-crit magic damage rating mod, SpellProjectile.DamageTarget's
        /// "AdditiveCombine(GetPositiveRatingMod(damageRating), heritageMod, sneakAttackMod)" with sneak attack
        /// neutral. Heritage there is read against the caster's equipped wand. Same crit-recomposition caveat as the
        /// physical path.
        /// </summary>
        public static float MagicDamageRatingMod(Creature caster, WorldObject wand)
        {
            var heritageMod = caster is Player && caster.GetHeritageBonus(wand) ? 1.05f : 1.0f;

            return Creature.AdditiveCombine(Creature.GetPositiveRatingMod(caster.GetDamageRating()), heritageMod, 1.0f);
        }

        /// <summary>
        /// Physical crit chance. With a defender, the production GetWeaponCriticalChance against it, which applies
        /// that defender's crit-resist rating. Without one, the attacker is passed as its own target and that single
        /// defender-side multiply is divided straight back out, so every attacker-side term (CriticalFrequency or
        /// the 10 percent default, the Critical Strike imbue, crit rating, Focus, Killer Instinct) is still read from
        /// production code rather than copied. The self-target is safe against the PvP gates inside: PvpClassifier
        /// treats a player acting on itself as NOT PvP.
        /// </summary>
        public static float PhysicalCritChance(WorldObject weapon, Creature attacker, CreatureSkill attackSkill, Creature defender)
        {
            float critChance;

            if (defender != null)
            {
                critChance = WorldObject.GetWeaponCriticalChance(weapon, attacker, attackSkill, defender);
            }
            else
            {
                // GetNegativeRatingMod is strictly positive when allowBug is false (Creature_Rating.cs), so the
                // divisor cannot be zero; the guard is belt and braces
                var selfCritResistMod = Creature.GetNegativeRatingMod(attacker.GetCritResistRating());

                critChance = WorldObject.GetWeaponCriticalChance(weapon, attacker, attackSkill, attacker);

                if (selfCritResistMod > 0.0f)
                    critChance /= selfCritResistMod;
            }

            return Math.Clamp(critChance, 0.0f, 1.0f);
        }

        /// <summary>
        /// Magic crit chance, GetWeaponMagicCritFrequency. With no wand that returns the flat 5 percent default and
        /// reads nothing else - no crit rating, no target. With a wand, the same self-target divide-out as
        /// PhysicalCritChance when no defender is given.
        /// </summary>
        public static float MagicCritChance(WorldObject wand, Creature caster, CreatureSkill magicSkill, Creature defender)
        {
            float critChance;

            if (defender != null || wand == null)
            {
                critChance = WorldObject.GetWeaponMagicCritFrequency(wand, caster, magicSkill, defender ?? caster);
            }
            else
            {
                var selfCritResistMod = Creature.GetNegativeRatingMod(caster.GetCritResistRating());

                critChance = WorldObject.GetWeaponMagicCritFrequency(wand, caster, magicSkill, caster);

                if (selfCritResistMod > 0.0f)
                    critChance /= selfCritResistMod;
            }

            return Math.Clamp(critChance, 0.0f, 1.0f);
        }

        /// <summary>
        /// AttackerSpec.CritDamageMod: DamageEvent's "1.0f + GetWeaponCritDamageMod(...)" times
        /// "1.0f + Execution". GetWeaponCritDamageMod reads its target only through the PvP Crippling Blow lever.
        /// </summary>
        public static float PhysicalCritDamageMod(WorldObject weapon, Creature attacker, CreatureSkill attackSkill, Creature defender)
        {
            return (1.0f + WorldObject.GetWeaponCritDamageMod(weapon, attacker, attackSkill, defender))
                * (1.0f + (float)WeaponModCombat.ReadWeaponOnlyWieldedBy(weapon, WeaponModId.Execution, attacker));
        }

        /// <summary>
        /// AttackerSpec.MagicCritDamageMod: SpellProjectile.CalculateDamage's war/void crit multiplier,
        /// GetWeaponCritDamageMod(...) times "1.0f + Execution", with NO +1.0 on the first term.
        /// </summary>
        public static float MagicCritDamageMod(WorldObject wand, Creature caster, CreatureSkill magicSkill, Creature defender)
        {
            return WorldObject.GetWeaponCritDamageMod(wand, caster, magicSkill, defender)
                * (1.0f + (float)WeaponModCombat.ReadWeaponOnlyWieldedBy(wand, WeaponModId.Execution, caster));
        }

        /// <summary>
        /// AttackerSpec.ArmorRendingMod: DamageEvent's Math.Min(armorRendingMod, armorCleavingMod).
        /// </summary>
        public static float ArmorIgnoreMod(WorldObject weapon, Creature attacker, CreatureSkill attackSkill)
        {
            var armorRendingMod = 1.0f;

            if (weapon != null && weapon.HasImbuedEffect(ImbuedEffectType.ArmorRending))
                armorRendingMod = WorldObject.GetArmorRendingMod(attackSkill);

            return Math.Min(armorRendingMod, attacker.GetArmorCleavingMod(weapon));
        }

        /// <summary>
        /// Puts an attacker into the stance its attack is made from, BEFORE any skill is read. Load-bearing:
        /// GetWeaponOffenseModifier returns 1.0 while CombatMode is NonCombat (WorldObject_Weapon.cs), a
        /// factory-built creature and a hydrated Player both start NonCombat, and Player.GetCurrentAttackSkill reads
        /// the magic skill only in CombatMode.Magic.
        ///
        /// A non-player creature goes through the real SetCombatMode - the call the engine makes before a monster's
        /// first swing (Monster_Combat.cs, DoAttackStance), and the one @simhit has always used - guarded, because
        /// the creature is usually one that never entered the world; on a throw it is measured out of stance and the
        /// failure is logged. It is NOT restored afterwards. A Player has no Session to send the switch to, so it
        /// gets Creature.SetCombatModeForSimulator, and the caller restores its previous mode (see
        /// SimulatorStance.With).
        /// </summary>
        internal static void EnterCreatureStance(Creature creature, CombatMode combatMode)
        {
            try
            {
                creature.SetCombatMode(combatMode);
            }
            catch (Exception ex)
            {
                log.Warn($"CombatSimulator could not put wcid {creature.WeenieClassId} into {combatMode} combat mode; measuring it out of stance.", ex);
            }
        }
    }

    /// <summary>
    /// Runs a body with an offline Creature (in practice a hydrated Player) in a given CombatMode, and restores the
    /// previous mode afterwards whatever the body does. Uses Creature.SetCombatModeForSimulator, which writes the
    /// mode and nothing else, so it is for objects outside the world only.
    /// </summary>
    public static class SimulatorStance
    {
        public static T With<T>(Creature creature, CombatMode combatMode, Func<T> body)
        {
            if (creature == null)
                throw new ArgumentNullException(nameof(creature));

            if (body == null)
                throw new ArgumentNullException(nameof(body));

            var previous = creature.CombatMode;

            creature.SetCombatModeForSimulator(combatMode);

            try
            {
                return body();
            }
            finally
            {
                creature.SetCombatModeForSimulator(previous);
            }
        }
    }
}
