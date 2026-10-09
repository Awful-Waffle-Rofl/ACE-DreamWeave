using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text;

using ACE.Entity.Enum;
using ACE.Server.WorldEvents;
using ACE.Server.WorldEvents.Defs;
using ACE.Server.WorldObjects;

using log4net;

namespace ACE.Server.MlDigsite
{
    /// <summary>
    /// How a map-event creature is keyed for level-spread scaling (RoZ round 19). The World Event split,
    /// kept: trash is keyed to ONE randomly sampled present player, objectives to the WEAKEST present
    /// player, and a Boss is an objective that also takes the power-sum health curve.
    /// </summary>
    public enum MlMapEventRoleClass
    {
        /// <summary>Digsite Wave and Add roles: keyed to one sampled player; offence, damage, health and XP scale.</summary>
        Trash,

        /// <summary>Digsite MiniBoss, Checkpoint and Priority roles: keyed to the weakest player, defences only.</summary>
        Objective,

        /// <summary>The Boss Rush boss and Aun Relaria: an objective plus the power-sum health curve.</summary>
        Boss,
    }

    /// <summary>
    /// One present player, reduced to the numbers level-spread scaling needs. Built by
    /// MlDigsiteAudience.ProfileOf from the live Player; a 0 attack means that family is not trained.
    /// </summary>
    public readonly struct MlMapEventProfile
    {
        public readonly int Level;
        public readonly uint MaxHealth;

        /// <summary>Best trained melee weapon skill (Current), 0 when none is trained.</summary>
        public readonly uint MeleeAttack;

        /// <summary>Best trained missile weapon skill (Current), 0 when none is trained.</summary>
        public readonly uint MissileAttack;

        /// <summary>Best trained War/Void/Life/Creature skill (Current), 0 when none is trained.</summary>
        public readonly uint MagicAttack;

        /// <summary>Current MeleeDefense.</summary>
        public readonly uint MeleeDefense;

        /// <summary>Current MissileDefense.</summary>
        public readonly uint MissileDefense;

        public MlMapEventProfile(int level, uint maxHealth, uint meleeAttack, uint missileAttack, uint magicAttack,
            uint meleeDefense, uint missileDefense)
        {
            Level = level;
            MaxHealth = maxHealth;
            MeleeAttack = meleeAttack;
            MissileAttack = missileAttack;
            MagicAttack = magicAttack;
            MeleeDefense = meleeDefense;
            MissileDefense = missileDefense;
        }

        public override string ToString() =>
            $"L{Level}/hp{MaxHealth}/atk{MeleeAttack},{MissileAttack},{MagicAttack}/def{MeleeDefense},{MissileDefense}";
    }

    /// <summary>
    /// Every map-event tunable, resolved once by MlMapEventTunables.Read and handed to the pure code below
    /// as a value, so <see cref="MlMapEventScaling"/> never touches PropertyManager (whose reads throw under
    /// the test harness).
    /// </summary>
    public readonly struct MlMapEventSettings
    {
        public readonly bool Enabled;
        public readonly double MinHitChance;
        public readonly double MinSpellLandChance;
        public readonly double TrashMaxHitChance;
        public readonly long TrashReferenceMaxHealth;
        public readonly double TrashDamageFloor;
        public readonly double TrashHealthExponent;
        public readonly double XpLevelExponent;
        public readonly double BossPowerPerPlayer;
        public readonly double BossHealthCap;
        public readonly long BossBaseHealth;
        public readonly bool BossDamageScalingEnabled;
        public readonly double BossHitsToKill;

        public MlMapEventSettings(bool enabled, double minHitChance, double minSpellLandChance, double trashMaxHitChance,
            long trashReferenceMaxHealth, double trashDamageFloor, double trashHealthExponent, double xpLevelExponent,
            double bossPowerPerPlayer, double bossHealthCap, long bossBaseHealth, bool bossDamageScalingEnabled,
            double bossHitsToKill)
        {
            Enabled = enabled;
            MinHitChance = minHitChance;
            MinSpellLandChance = minSpellLandChance;
            TrashMaxHitChance = trashMaxHitChance;
            TrashReferenceMaxHealth = trashReferenceMaxHealth;
            TrashDamageFloor = trashDamageFloor;
            TrashHealthExponent = trashHealthExponent;
            XpLevelExponent = xpLevelExponent;
            BossPowerPerPlayer = bossPowerPerPlayer;
            BossHealthCap = bossHealthCap;
            BossBaseHealth = bossBaseHealth;
            BossDamageScalingEnabled = bossDamageScalingEnabled;
            BossHitsToKill = bossHitsToKill;
        }

        /// <summary>The shipped defaults - identical to the PropertyManager rows (owner ruling 2026-09-25).</summary>
        public static MlMapEventSettings Defaults => new MlMapEventSettings(true, 0.60, 0.70, 0.75, 500, 0.25, 1.0, 2.0,
            0.15, 3.0, 0, true, 3.0);
    }

    /// <summary>The outcome of one defence-floor solve, for the caller's write and the log line.</summary>
    public readonly struct MlMapEventDefenseResult
    {
        public readonly uint NewInitLevel;
        public readonly bool Changed;

        /// <summary>True when the wanted reduction exceeded InitLevel, so InitLevel stopped at 0 short of the target.</summary>
        public readonly bool Clamped;

        public MlMapEventDefenseResult(uint newInitLevel, bool changed, bool clamped)
        {
            NewInitLevel = newInitLevel;
            Changed = changed;
            Clamped = clamped;
        }
    }

    /// <summary>What <see cref="MlMapEventScaling.ApplySpreadScaling"/> did, for the caller (the boss ratchet's base) and tests.</summary>
    public sealed class MlMapEventSpreadResult
    {
        public bool Applied;
        public int Players;
        public MlMapEventProfile Key;

        public bool BossHealthApplied;

        /// <summary>The boss's StartingValue after any base-health rebase and BEFORE the power multiplier.</summary>
        public uint BossBaseStartingValue;

        /// <summary>The boss's MaxValue after any base-health rebase and BEFORE the power multiplier.</summary>
        public uint BossBaseMax;

        public double BossHealthMult = 1.0;
        public double PowerSum;

        public readonly List<string> ClampedDefenses = new List<string>();
    }

    /// <summary>
    /// RoZ round 19: level-spread scaling for Marae Lassel MAP EVENTS - the digsite encounters and the Aun
    /// Relaria treasure-map boss - so every level band present can kill what a map dug up. NOT ordinary island
    /// wildlife, NOT the Bluespire ladder / WaveEncounter runner, NOT World Events.
    ///
    /// THE ENGINE CONTEST this solves against, verified in source: a melee/missile swing lands with
    /// SkillCheck.GetSkillChance(attack, defense) = 1 - 1/(1 + e^(0.03 * (attack - defense)))
    /// (Source/ACE.Server/WorldObjects/SkillCheck.cs:7-12, called at Entity/DamageEvent.cs:530 with the
    /// defender's GetEffectiveDefenseSkill), and a spell lands against the same 0.03 contest on the target's
    /// GetEffectiveMagicDefense (WorldObject_Magic.cs:108-116, MagicDefenseCheck). Inverting it, chance &gt;= p
    /// exactly when defense &lt;= attack - logit(p)/0.03, which is <see cref="DefenseFloor"/>.
    ///
    /// Pure in the D6 sense everywhere except <see cref="ApplySpreadScaling"/> and
    /// <see cref="LowerDefenses"/>, which touch only the creature handed to them: no PropertyManager, no
    /// clock, no global RNG. Tunables arrive as <see cref="MlMapEventSettings"/>.
    /// </summary>
    public static class MlMapEventScaling
    {
        private static readonly ILog log = LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);

        /// <summary>The skill-contest factor SkillCheck.GetSkillChance uses by default, for both melee/missile hits and spell resists.</summary>
        public const double ContestFactor = 0.03;

        /// <summary>The melee weapon skills, retired and current. A creature or player may carry either generation.</summary>
        public static readonly Skill[] MeleeAttackSkills =
        {
            Skill.HeavyWeapons, Skill.LightWeapons, Skill.FinesseWeapons, Skill.TwoHandedCombat,
            Skill.Axe, Skill.Dagger, Skill.Mace, Skill.Spear, Skill.Staff, Skill.Sword, Skill.UnarmedCombat,
        };

        /// <summary>The missile weapon skills, retired and current.</summary>
        public static readonly Skill[] MissileAttackSkills =
        {
            Skill.MissileWeapons, Skill.Bow, Skill.Crossbow, Skill.ThrownWeapon,
        };

        /// <summary>The four offensive schools whose Current the spell resist check reads (WorldObject_Magic.TryResistSpell).</summary>
        public static readonly Skill[] MagicAttackSkills =
        {
            Skill.WarMagic, Skill.VoidMagic, Skill.LifeMagic, Skill.CreatureEnchantment,
        };

        // ---- the contest ---------------------------------------------------------------------------------

        /// <summary>ln(p / (1 - p)), with p clamped into (0.001, 0.999) so neither end is infinite.</summary>
        public static double Logit(double p)
        {
            if (double.IsNaN(p))
                p = 0.5;

            p = Math.Clamp(p, 0.001, 0.999);

            return Math.Log(p / (1.0 - p));
        }

        /// <summary>
        /// The effective defense to lower to so an attacker of <paramref name="attackSkill"/> reaches at least
        /// <paramref name="minChance"/>: min(effectiveDefense, floor(attack - logit(p)/0.03)), never below 0.
        ///
        /// NEVER RAISES: the result is at most <paramref name="effectiveDefense"/>. An attack of 0 means the
        /// keyed player does not train that family at all, and returns the defense untouched - there is no
        /// attacker to size for, and lowering a defense to 0 on behalf of a skill nobody uses would only hand
        /// every OTHER player a free win.
        /// </summary>
        public static uint DefenseFloor(uint effectiveDefense, uint attackSkill, double minChance)
        {
            if (attackSkill == 0)
                return effectiveDefense;

            var wanted = Math.Floor(attackSkill - Logit(minChance) / ContestFactor);

            if (wanted < 0)
                wanted = 0;

            return wanted >= effectiveDefense ? effectiveDefense : (uint)wanted;
        }

        /// <summary>The largest effective attack that lands on <paramref name="defense"/> no more often than <paramref name="maxChance"/>.</summary>
        public static uint MaxAttackFor(uint defense, double maxChance)
        {
            var wanted = Math.Floor(defense + Logit(maxChance) / ContestFactor);

            if (wanted < 0)
                return 0;

            return wanted > uint.MaxValue ? uint.MaxValue : (uint)wanted;
        }

        /// <summary>
        /// Trash offence cap: min(effectiveAttack, <see cref="MaxAttackFor"/>(playerDefense, maxChance)). Never raises.
        /// </summary>
        public static uint TrashAttackCap(uint effectiveAttack, uint playerDefense, double maxChance)
        {
            var cap = MaxAttackFor(playerDefense, maxChance);

            return effectiveAttack <= cap ? effectiveAttack : cap;
        }

        /// <summary>
        /// Converts a wanted EFFECTIVE skill into a new InitLevel. The engine builds a creature's effective
        /// defense as round(skill * mult + imbues) (Creature_Combat.GetEffectiveDefenseSkill, stance 1.0 for a
        /// non-Player; Creature_Magic.GetEffectiveMagicDefense), and the skill itself as attribute part +
        /// InitLevel + Ranks (CreatureSkill.cs), so InitLevel moves the skill one-for-one.
        ///
        /// The wanted skill is the highest whose rounded effective value is at or under
        /// <paramref name="targetEffective"/>, so the result never lands above it. InitLevel is a uint and stops at 0: when the wanted
        /// reduction is larger than InitLevel the result is 0 with <see cref="MlMapEventDefenseResult.Clamped"/>
        /// set, which the caller logs - the defense then sits at the attribute part alone, still above target.
        ///
        /// Never raises: a target at or above what the skill already produces is no change.
        /// </summary>
        public static MlMapEventDefenseResult InitLevelFor(uint initLevel, uint skill, double mult, int imbues, uint targetEffective)
        {
            if (!double.IsFinite(mult) || mult <= 0)
                mult = 1.0;

            var effective = Math.Round(skill * mult + imbues, MidpointRounding.AwayFromZero);

            if (targetEffective >= effective)
                return new MlMapEventDefenseResult(initLevel, false, false);

            // The highest skill whose ROUNDED effective value is still at or under the target: start from the
            // unrounded solve plus the half-point rounding allows, and step down past any rounding overshoot.
            var wantedSkill = Math.Floor((targetEffective - (double)imbues + 0.5) / mult);

            while (wantedSkill > 0 && Math.Round(wantedSkill * mult + imbues, MidpointRounding.AwayFromZero) > targetEffective)
                wantedSkill--;

            if (wantedSkill < 0)
                wantedSkill = 0;

            if (wantedSkill >= skill)
                return new MlMapEventDefenseResult(initLevel, false, false);

            var delta = skill - (uint)wantedSkill;

            if (delta > initLevel)
                return new MlMapEventDefenseResult(0, initLevel != 0, true);

            return new MlMapEventDefenseResult(initLevel - delta, true, false);
        }

        // ---- trash ---------------------------------------------------------------------------------------

        /// <summary>
        /// The DamageRating a trash creature keeps after sizing to the keyed player's health:
        /// mult = clamp(keyedMaxHealth / reference, floor, 1), composed onto the existing rating through the
        /// engine's own rating curve (WorldEventBossDamageController.RatingToMult / MultToRating), so a negative
        /// result means what the engine will make of it. Never raises; an unreadable health or reference is no change.
        /// </summary>
        public static int TrashDamageRating(int currentRating, uint keyedMaxHealth, long referenceMaxHealth, double damageFloor)
        {
            if (keyedMaxHealth == 0 || referenceMaxHealth <= 0)
                return currentRating;

            var mult = keyedMaxHealth / (double)referenceMaxHealth;

            if (!double.IsFinite(damageFloor) || damageFloor <= 0)
                damageFloor = 0.01;

            mult = Math.Clamp(mult, Math.Min(damageFloor, 1.0), 1.0);

            if (mult >= 1.0)
                return currentRating;

            var composed = WorldEventBossDamageController.MultToRating(WorldEventBossDamageController.RatingToMult(currentRating) * mult);

            return composed < currentRating ? composed : currentRating;
        }

        /// <summary>(keyedLevel / authoredLevel)^exponent, capped at 1. A level-less creature or player is no change.</summary>
        public static double LevelRatioScale(int keyedLevel, int authoredLevel, double exponent)
        {
            if (authoredLevel <= 0 || keyedLevel <= 0 || keyedLevel >= authoredLevel)
                return 1.0;

            if (!double.IsFinite(exponent) || exponent < 0)
                exponent = 1.0;

            var scale = Math.Pow(keyedLevel / (double)authoredLevel, exponent);

            return double.IsFinite(scale) ? Math.Clamp(scale, 0.0, 1.0) : 1.0;
        }

        /// <summary>The trash health multiplier: <see cref="LevelRatioScale"/> at ml_mapevent_trash_health_exponent.</summary>
        public static double TrashHealthScale(int keyedLevel, int authoredLevel, double exponent)
            => LevelRatioScale(keyedLevel, authoredLevel, exponent);

        /// <summary>
        /// The trash XP/luminance multiplier: <see cref="LevelRatioScale"/> at ml_mapevent_xp_level_exponent. A
        /// low character sized-for trash must not farm an XpOverride authored for a level-275 kill
        /// (Creature_Death.OnDeath_GrantXP pays XpOverride * damage share with no level check).
        /// </summary>
        public static double XpScale(int keyedLevel, int authoredLevel, double exponent)
            => LevelRatioScale(keyedLevel, authoredLevel, exponent);

        /// <summary>round(value * scale) for a positive value and a scale below 1; everything else is unchanged.</summary>
        public static long ScaleAward(long value, double scale)
        {
            if (value <= 0 || !double.IsFinite(scale) || scale >= 1.0)
                return value;

            return (long)Math.Round(value * Math.Max(0.0, scale), MidpointRounding.AwayFromZero);
        }

        // ---- keying --------------------------------------------------------------------------------------

        /// <summary>
        /// A composite of the WEAKEST present player on every axis: the lowest level and max health, and for each
        /// attack family the lowest value among the players who train it at all (a pure mage is not the weakest
        /// melee attacker - they are no melee attacker). This is "killable by every band present": each defense is
        /// floored for the weakest player who actually attacks it.
        /// </summary>
        public static MlMapEventProfile Weakest(IReadOnlyList<MlMapEventProfile> profiles)
        {
            if (profiles == null || profiles.Count == 0)
                return default;

            var level = int.MaxValue;
            var health = uint.MaxValue;
            uint melee = 0, missile = 0, magic = 0;
            var meleeDef = uint.MaxValue;
            var missileDef = uint.MaxValue;

            foreach (var p in profiles)
            {
                level = Math.Min(level, p.Level);
                health = Math.Min(health, p.MaxHealth);
                melee = MinTrained(melee, p.MeleeAttack);
                missile = MinTrained(missile, p.MissileAttack);
                magic = MinTrained(magic, p.MagicAttack);
                meleeDef = Math.Min(meleeDef, p.MeleeDefense);
                missileDef = Math.Min(missileDef, p.MissileDefense);
            }

            return new MlMapEventProfile(level, health, melee, missile, magic, meleeDef, missileDef);
        }

        private static uint MinTrained(uint current, uint candidate)
        {
            if (candidate == 0)
                return current;

            return current == 0 || candidate < current ? candidate : current;
        }

        /// <summary>The TOUGHEST present player's max health - what the measured boss damage controller sizes a hit against.</summary>
        public static uint ToughestMaxHealth(IReadOnlyList<MlMapEventProfile> profiles)
        {
            uint top = 0;

            if (profiles == null)
                return top;

            foreach (var p in profiles)
                top = Math.Max(top, p.MaxHealth);

            return top;
        }

        /// <summary>
        /// The profile a creature of <paramref name="roleClass"/> is sized against, or null when nobody is present.
        /// Trash: ONE player sampled uniformly from <paramref name="rng"/> (no draw at all for a single player, so a
        /// solo run consumes nothing). Objective and Boss: <see cref="Weakest"/>.
        ///
        /// <paramref name="rng"/> must NOT be the digsite spawner's own seeded stream - see MlDigsiteSpawner.TrySpawn.
        /// </summary>
        public static MlMapEventProfile? KeyFor(MlMapEventRoleClass roleClass, IReadOnlyList<MlMapEventProfile> profiles, Random rng)
        {
            if (profiles == null || profiles.Count == 0)
                return null;

            if (roleClass != MlMapEventRoleClass.Trash)
                return Weakest(profiles);

            if (profiles.Count == 1 || rng == null)
                return profiles[0];

            return profiles[rng.Next(profiles.Count)];
        }

        /// <summary>Digsite role -> role class. Wave and Add are trash; Boss is Boss; the rest are objectives.</summary>
        public static MlMapEventRoleClass RoleClassFor(MlDigsiteRole role)
        {
            switch (role)
            {
                case MlDigsiteRole.Wave:
                case MlDigsiteRole.Add:
                    return MlMapEventRoleClass.Trash;

                case MlDigsiteRole.Boss:
                    return MlMapEventRoleClass.Boss;

                default:
                    return MlMapEventRoleClass.Objective;
            }
        }

        // ---- boss health ---------------------------------------------------------------------------------

        /// <summary>sum((level/275)^2) over the present players - WorldEventRosterSelector.PowerSum, the World Event curve.</summary>
        public static double PowerSum(IReadOnlyList<MlMapEventProfile> profiles)
        {
            if (profiles == null || profiles.Count == 0)
                return 0;

            var levels = new List<int>(profiles.Count);

            foreach (var p in profiles)
                levels.Add(p.Level);

            return WorldEventRosterSelector.PowerSum(levels);
        }

        /// <summary>BossDef.ResolveHealthMult(perPlayer, cap, powerSum): clamp(1 + perPlayer * powerSum, 1, cap).</summary>
        public static double BossHealthMult(double perPlayer, double cap, double powerSum)
            => BossDef.ResolveHealthMult(perPlayer, cap, powerSum);

        /// <summary>
        /// The boss health write: an optional base (<paramref name="baseHealth"/>, 0 = keep the authored health
        /// as the floor) through WorldEventSpawner.RebasedStartingValue - which CAN go below the authored value,
        /// unlike ScaledStartingValue - then the power multiplier (always &gt;= 1) through
        /// WorldEventSpawner.ScaledStartingValue. Returns the pre-multiplier pair too, which is what the reap's
        /// ratchet re-scales from.
        /// </summary>
        public static uint BossHealthStartingValue(uint startingValue, uint maxValue, long baseHealth, double mult,
            out uint baseStartingValue, out uint baseMax)
        {
            var fixedPart = maxValue > startingValue ? maxValue - startingValue : 0;

            baseStartingValue = baseHealth > 0
                ? WorldEventSpawner.RebasedStartingValue(startingValue, maxValue, (uint)Math.Min(baseHealth, uint.MaxValue))
                : startingValue;

            baseMax = fixedPart + baseStartingValue;

            return WorldEventSpawner.ScaledStartingValue(baseStartingValue, baseMax, mult);
        }

        // ---- the creature writes -------------------------------------------------------------------------

        /// <summary>
        /// The melee-defense weapon modifier this creature will have IN COMBAT. The engine's own
        /// WorldObject.GetWeaponMeleeDefenseModifier returns 1.0 for any creature in NonCombat mode
        /// (WorldObject_Weapon.cs:143-147) - which is every creature at spawn - so solving against it would
        /// under-state the defense the fight actually rolls against by the weapon's WeaponDefense bonus. This
        /// mirrors the in-combat branch (WorldObject_Weapon.cs:149-186): the better of main hand and dual-wield
        /// off hand, each WeaponDefense (with the engine's imbue-stacking correction) plus weapon and wielder
        /// defense enchantments.
        ///
        /// Missile and magic defense need no counterpart: for a non-Player both engine helpers look the weapon
        /// up through `wielder as Player`, find none, and return 1.0 in every mode (WorldObject_Weapon.cs:191-236).
        /// </summary>
        public static double CombatMeleeDefenseMod(Creature creature)
        {
            var main = creature.GetEquippedWeapon(true) ?? creature.GetEquippedWand();
            var off = creature.GetDualWieldWeapon();

            var mod = MeleeDefenseModOf(creature, main);

            return off == null ? mod : Math.Max(mod, MeleeDefenseModOf(creature, off));
        }

        private static double MeleeDefenseModOf(Creature wielder, WorldObject weapon)
        {
            if (weapon == null)
                return 1.0;

            var baseWepDef = weapon.WeaponDefense ?? 1.0;

            if (weapon.WeaponDefense > 0 && weapon.WeaponDefense < 1 && ((weapon.GetProperty(ACE.Entity.Enum.Properties.PropertyInt.ImbueStackingBits) ?? 0) & 4) != 0)
                baseWepDef += 1;

            var mod = baseWepDef + weapon.EnchantmentManager.GetDefenseMod();

            if (weapon.IsEnchantable)
                mod += wielder.EnchantmentManager.GetDefenseMod();

            return mod;
        }

        /// <summary>
        /// The attack weapon modifier this creature will have IN COMBAT - the counterpart of
        /// <see cref="CombatMeleeDefenseMod"/> for WorldObject.GetWeaponOffenseModifier, which likewise returns
        /// 1.0 out of combat (WorldObject_Weapon.cs:241-286). A ranged weapon gets no offence bonus.
        /// </summary>
        public static double CombatOffenseMod(Creature creature)
        {
            var main = creature.GetEquippedWeapon(true) ?? creature.GetEquippedWand();
            var off = creature.GetDualWieldWeapon();

            var mod = OffenseModOf(creature, main);

            return off == null ? mod : Math.Max(mod, OffenseModOf(creature, off));
        }

        private static double OffenseModOf(Creature wielder, WorldObject weapon)
        {
            if (weapon == null || weapon.IsRanged)
                return 1.0;

            var mod = (weapon.WeaponOffense ?? 1.0) + weapon.EnchantmentManager.GetAttackMod();

            if (weapon.IsEnchantable)
                mod += wielder.EnchantmentManager.GetAttackMod();

            return mod;
        }

        /// <summary>
        /// Lowers (never raises) <paramref name="creature"/>'s melee, missile and magic defenses so
        /// <paramref name="key"/>'s best trained attack in each family reaches the minimum chance. Melee defense
        /// is floored against the key's melee attack, missile defense against its missile attack (a missile swing
        /// rolls against MissileDefense, Creature_Combat.GetEffectiveDefenseSkill), magic defense against its best
        /// offensive school. Works on the skill's Base (attribute part + InitLevel + Ranks, no enchantments), so a
        /// debuff a player has already landed still stacks on top afterwards.
        ///
        /// Shared by the spawn path and the reap re-check, so the two cannot solve differently. Returns a short
        /// summary for the log, and the families that hit the InitLevel-0 clamp.
        /// </summary>
        public static string LowerDefenses(Creature creature, MlMapEventProfile key, double minHitChance, double minSpellLandChance,
            List<string> clamped)
        {
            var sb = new StringBuilder();

            LowerOne(creature, Skill.MeleeDefense, key.MeleeAttack, minHitChance,
                CombatMeleeDefenseMod(creature) * creature.GetBurdenMod(),
                creature.GetDefenseImbues(ImbuedEffectType.MeleeDefense), "melee", sb, clamped);

            LowerOne(creature, Skill.MissileDefense, key.MissileAttack, minHitChance,
                WorldObject.GetWeaponMissileDefenseModifier(creature) * creature.GetBurdenMod(),
                creature.GetDefenseImbues(ImbuedEffectType.MissileDefense), "missile", sb, clamped);

            LowerOne(creature, Skill.MagicDefense, key.MagicAttack, minSpellLandChance,
                WorldObject.GetWeaponMagicDefenseModifier(creature),
                creature.GetDefenseImbues(ImbuedEffectType.MagicDefense), "magic", sb, clamped);

            return sb.ToString();
        }

        private static void LowerOne(Creature creature, Skill defenseSkill, uint attack, double chance, double mult, int imbues,
            string label, StringBuilder sb, List<string> clamped)
        {
            if (attack == 0)
                return;

            var skill = creature.GetCreatureSkill(defenseSkill, false);

            if (skill == null)
                return;

            var baseSkill = skill.Base;

            if (!double.IsFinite(mult) || mult <= 0)
                mult = 1.0;

            var effective = (uint)Math.Round(baseSkill * mult + imbues, MidpointRounding.AwayFromZero);

            var target = DefenseFloor(effective, attack, chance);

            var result = InitLevelFor(skill.InitLevel, baseSkill, mult, imbues, target);

            if (!result.Changed && !result.Clamped)
                return;

            skill.InitLevel = result.NewInitLevel;

            var after = (uint)Math.Round(skill.Base * mult + imbues, MidpointRounding.AwayFromZero);

            sb.Append($" {label}={effective}->{after}(target {target})");

            if (result.Clamped)
                clamped?.Add(label);
        }

        /// <summary>
        /// Caps every attack skill the trash creature carries so it lands on the keyed player's matching defense
        /// no more often than <paramref name="maxChance"/>: melee skills against MeleeDefense, missile skills
        /// against MissileDefense, solved on the effective attack (skill * in-combat weapon offence mod,
        /// Creature_Combat.GetEffectiveAttackSkill, <see cref="CombatOffenseMod"/>). Monster spellcasting is not capped - a spell's landing is the
        /// PLAYER's magic defense roll, which the profile does not carry, and a bolt must still hit its target.
        /// Returns how many skills were lowered.
        /// </summary>
        public static int CapTrashAttack(Creature creature, MlMapEventProfile key, double maxChance)
        {
            var offenseMod = CombatOffenseMod(creature);

            if (!double.IsFinite(offenseMod) || offenseMod <= 0)
                offenseMod = 1.0;

            var lowered = 0;

            foreach (var s in MeleeAttackSkills)
                lowered += CapOne(creature, s, key.MeleeDefense, maxChance, offenseMod) ? 1 : 0;

            foreach (var s in MissileAttackSkills)
                lowered += CapOne(creature, s, key.MissileDefense, maxChance, offenseMod) ? 1 : 0;

            return lowered;
        }

        private static bool CapOne(Creature creature, Skill attackSkill, uint playerDefense, double maxChance, double offenseMod)
        {
            var skill = creature.GetCreatureSkill(attackSkill, false);

            if (skill == null || skill.InitLevel == 0)
                return false;

            var baseSkill = skill.Base;
            var effective = (uint)Math.Round(baseSkill * offenseMod, MidpointRounding.AwayFromZero);
            var cap = TrashAttackCap(effective, playerDefense, maxChance);

            if (cap >= effective)
                return false;

            var result = InitLevelFor(skill.InitLevel, baseSkill, offenseMod, 0, cap);

            if (!result.Changed)
                return false;

            skill.InitLevel = result.NewInitLevel;

            return true;
        }

        /// <summary>
        /// THE one entry point both map-event spawn paths call (MlDigsiteSpawner.TrySpawn and
        /// MlRelariaSpawner.PrepareBoss), after Location/tether and BEFORE EnterWorld - and on the digsite path
        /// BEFORE ApplySpawnScaling, so any headcount raise multiplies the downscaled result rather than being
        /// undone by it. The M1 magic-defense scale must already have run, so the floor below measures what M1 left.
        ///
        /// Nobody present, or the kill switch off, changes nothing: the creature fights as authored.
        /// All-high groups also change nothing on the defense/offence/health/XP axes, because every floor is
        /// min(authored, solved) and every scale is capped at 1.
        ///
        /// Writes exactly one "[ML_DIGSITE] spread" line, same format on both paths.
        /// </summary>
        public static MlMapEventSpreadResult ApplySpreadScaling(Creature creature, MlMapEventRoleClass roleClass,
            IReadOnlyList<MlMapEventProfile> profiles, Random rng, MlMapEventSettings settings, string context)
        {
            var result = new MlMapEventSpreadResult { Players = profiles?.Count ?? 0 };

            if (creature == null)
                return result;

            if (!settings.Enabled)
            {
                log.Debug($"[ML_DIGSITE] spread {context} role={roleClass} wcid={creature.WeenieClassId} skipped: ml_mapevent_spread_scaling_enabled is off");
                return result;
            }

            var key = KeyFor(roleClass, profiles, rng);

            if (key == null)
            {
                log.Info($"[ML_DIGSITE] spread {context} role={roleClass} wcid={creature.WeenieClassId} players=0 unchanged (nobody present)");
                return result;
            }

            result.Applied = true;
            result.Key = key.Value;

            var sb = new StringBuilder();

            sb.Append(LowerDefenses(creature, key.Value, settings.MinHitChance, settings.MinSpellLandChance, result.ClampedDefenses));

            var authoredLevel = creature.Level ?? 0;

            if (roleClass == MlMapEventRoleClass.Trash)
            {
                var capped = CapTrashAttack(creature, key.Value, settings.TrashMaxHitChance);

                if (capped > 0)
                    sb.Append($" atkcap={capped}");

                var ratingBefore = creature.DamageRating ?? 0;
                var ratingAfter = TrashDamageRating(ratingBefore, key.Value.MaxHealth, settings.TrashReferenceMaxHealth, settings.TrashDamageFloor);

                if (ratingAfter != ratingBefore)
                {
                    creature.DamageRating = ratingAfter;
                    sb.Append($" dr={ratingBefore}->{ratingAfter}");
                }

                var healthScale = TrashHealthScale(key.Value.Level, authoredLevel, settings.TrashHealthExponent);

                if (healthScale < 1.0)
                {
                    var before = creature.Health.MaxValue;
                    var scaled = MlDigsiteBossMechanicRules.ScaledAddHealth(creature.Health.StartingValue, before, healthScale);

                    if (scaled != creature.Health.StartingValue)
                    {
                        creature.Health.StartingValue = scaled;
                        creature.Health.Current = creature.Health.MaxValue;
                        sb.Append($" hp={before}->{creature.Health.MaxValue}");
                    }
                }

                var xpScale = XpScale(key.Value.Level, authoredLevel, settings.XpLevelExponent);

                if (xpScale < 1.0)
                {
                    var xp = creature.XpOverride;

                    if (xp.HasValue && xp.Value > 0)
                    {
                        var scaledXp = (int)Math.Min(int.MaxValue, ScaleAward(xp.Value, xpScale));
                        creature.XpOverride = scaledXp;
                        sb.Append($" xp={xp.Value}->{scaledXp}");
                    }

                    var lum = creature.LuminanceAward;

                    if (lum.HasValue && lum.Value > 0)
                    {
                        var scaledLum = (int)Math.Min(int.MaxValue, ScaleAward(lum.Value, xpScale));
                        creature.LuminanceAward = scaledLum;
                        sb.Append($" lum={lum.Value}->{scaledLum}");
                    }
                }
            }

            if (roleClass == MlMapEventRoleClass.Boss)
            {
                result.PowerSum = PowerSum(profiles);
                result.BossHealthMult = BossHealthMult(settings.BossPowerPerPlayer, settings.BossHealthCap, result.PowerSum);

                var before = creature.Health.MaxValue;

                var sv = BossHealthStartingValue(creature.Health.StartingValue, before, settings.BossBaseHealth,
                    result.BossHealthMult, out var baseSv, out var baseMax);

                result.BossBaseStartingValue = baseSv;
                result.BossBaseMax = baseMax;
                result.BossHealthApplied = true;

                if (sv != creature.Health.StartingValue)
                {
                    creature.Health.StartingValue = sv;
                    creature.Health.Current = creature.Health.MaxValue;
                }

                sb.Append($" powerSum={result.PowerSum:F3} bossMult={result.BossHealthMult:F2} hp={before}->{creature.Health.MaxValue}");
            }

            if (result.ClampedDefenses.Count > 0)
                sb.Append($" clamped={string.Join(",", result.ClampedDefenses)}");

            var changes = sb.Length > 0 ? sb.ToString() : " unchanged";

            var line = $"[ML_DIGSITE] spread {context} role={roleClass} wcid={creature.WeenieClassId} level={authoredLevel} players={result.Players} key={key.Value}{changes}";

            if (result.ClampedDefenses.Count > 0)
                log.Warn(line + " (a defense floor stopped at InitLevel 0 short of its target)");
            else
                log.Info(line);

            return result;
        }
    }
}
