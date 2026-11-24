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
        /// that defender's crit-resist rating. Without one, the production attacker-side half,
        /// GetWeaponCriticalChancePreResist, with no target, so every attacker-side term (CriticalFrequency or the 10
        /// percent default, the Critical Strike imbue, crit rating, Focus, Killer Instinct) is read from production code
        /// rather than copied. A null target is safe against the PvP gate inside: PvpClassifier reads it as NOT PvP.
        /// </summary>
        public static float PhysicalCritChance(WorldObject weapon, Creature attacker, CreatureSkill attackSkill, Creature defender)
        {
            var critChance = defender != null
                ? WorldObject.GetWeaponCriticalChance(weapon, attacker, attackSkill, defender)
                : WorldObject.GetWeaponCriticalChancePreResist(weapon, attacker, attackSkill, null);

            return Math.Clamp(critChance, 0.0f, 1.0f);
        }

        /// <summary>
        /// Magic crit chance, GetWeaponMagicCritFrequency. With no wand that returns the flat 5 percent default plus a
        /// pet's Precision from its owner's Summoner focus, and reads no crit rating. With a wand and no defender, the attacker-side half
        /// (GetWeaponMagicCritFrequencyPreResist) with no target, as PhysicalCritChance.
        /// </summary>
        public static float MagicCritChance(WorldObject wand, Creature caster, CreatureSkill magicSkill, Creature defender)
        {
            var critChance = defender != null
                ? WorldObject.GetWeaponMagicCritFrequency(wand, caster, magicSkill, defender)
                : WorldObject.GetWeaponMagicCritFrequencyPreResist(wand, caster, magicSkill, null);

            return Math.Clamp(critChance, 0.0f, 1.0f);
        }

        /// <summary>
        /// AttackerSpec.CritDamageMod: DamageEvent's "1.0f + GetWeaponCritDamageMod(...)" times
        /// "1.0f + Execution" times a pet's "1.0f + Execution" from its owner's Summoner focus.
        /// GetWeaponCritDamageMod reads its target only through the PvP Crippling Blow lever.
        /// </summary>
        public static float PhysicalCritDamageMod(WorldObject weapon, Creature attacker, CreatureSkill attackSkill, Creature defender)
        {
            return (1.0f + WorldObject.GetWeaponCritDamageMod(weapon, attacker, attackSkill, defender))
                * (1.0f + (float)WeaponModCombat.ReadWeaponOnlyWieldedBy(weapon, WeaponModId.Execution, attacker))
                * (1.0f + (float)SummonerFocus.PetFocusModValue(attacker, defender, WeaponModId.Execution));
        }

        /// <summary>
        /// AttackerSpec.MagicCritDamageMod: SpellProjectile.CalculateDamage's war/void crit multiplier,
        /// GetWeaponCritDamageMod(...) times "1.0f + Execution" times a pet's "1.0f + Execution" from its owner's
        /// Summoner focus, with NO +1.0 on the first term.
        /// </summary>
        public static float MagicCritDamageMod(WorldObject wand, Creature caster, CreatureSkill magicSkill, Creature defender)
        {
            return WorldObject.GetWeaponCritDamageMod(wand, caster, magicSkill, defender)
                * (1.0f + (float)WeaponModCombat.ReadWeaponOnlyWieldedBy(wand, WeaponModId.Execution, caster))
                * (1.0f + (float)SummonerFocus.PetFocusModValue(caster, defender, WeaponModId.Execution));
        }

        /// <summary>
        /// AttackerSpec.ArmorRendingMod: DamageEvent's Math.Min(armorRendingMod, armorCleavingMod), the rending term from the
        /// same WorldObject.GetArmorRendingModFor DamageEvent reads (so a pet's focus Armor Rending is in it).
        /// </summary>
        public static float ArmorIgnoreMod(WorldObject weapon, Creature attacker, CreatureSkill attackSkill, Creature defender)
        {
            return Math.Min(WorldObject.GetArmorRendingModFor(weapon, attacker, attackSkill, defender), attacker.GetArmorCleavingMod(weapon));
        }

        [ThreadStatic]
        private static bool modeOnlyStance;

        /// <summary>
        /// Runs <paramref name="body"/> with EnterCreatureStance writing CombatMode ONLY (Creature.SetCombatModeForSimulator), on
        /// this thread. For a creature that must come out of the measurement untouched (a Balance Trials creature measured at
        /// spawn): the real SetCombatMode also sets CurrentMotionState to a combat stance (which every DoAttackStance trigger is
        /// gated on), moves LastWeaponSwap, and for a missile creature queues a reload ActionChain, none of which a caller can
        /// undo. The caller restores CombatMode itself. The spec terms read CombatMode, not the motion stance.
        /// </summary>
        internal static T WithModeOnlyStance<T>(Func<T> body)
        {
            var previous = modeOnlyStance;
            modeOnlyStance = true;

            try
            {
                return body();
            }
            finally
            {
                modeOnlyStance = previous;
            }
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
            if (modeOnlyStance)
            {
                creature.SetCombatModeForSimulator(combatMode);
                return;
            }

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
