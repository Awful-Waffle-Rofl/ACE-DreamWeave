using System.Collections.Generic;
using System.Linq;

using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Server.WorldObjects;

namespace ACE.Server.ThreadDungeons
{
    /// <summary>
    /// Applies DungeonCombatNormalizer's pure decisions to a live Creature, before EnterWorld. Split from
    /// ThreadDungeonSpawner only to keep TryPlaceOnce readable; every NUMBER written here is decided by
    /// DungeonCombatNormalizer, which is where the unit tests are.
    ///
    /// PER-INSTANCE SAFETY, verified 2026-09-13 against ACE.Entity/Adapter/WeenieConverter.cs: PropertiesBool,
    /// PropertiesFloat and PropertiesInt are copied into fresh dictionaries per biota (WeenieConverter.cs:22-30),
    /// and PropertiesSkill is rebuilt per biota (WeenieConverter.cs:150) - the same precedent ApplyUplift's skill
    /// floors already rely on. PropertiesBodyPart is the one that is SHARED, and SetBossBodyParts clones it.
    /// </summary>
    internal static class DungeonCreatureNormalizer
    {
        /// <summary>
        /// The six authored ratings zeroed on a NORMALIZED boss before the plan's boss rating floors are added
        /// (ids verified 2026-09-13 in PropertyInt.cs: 307, 308, 313, 314, 315, 316). The spawner's rating writes
        /// are additive on top of whatever the creature carries, so without this an authored DamageRating 60
        /// boss would keep its 60 on top of the plan's floor.
        /// </summary>
        public static readonly PropertyInt[] BossRatingIds =
        {
            PropertyInt.DamageRating, PropertyInt.DamageResistRating, PropertyInt.CritRating, PropertyInt.CritDamageRating,
            PropertyInt.CritResistRating, PropertyInt.CritDamageResistRating,
        };

        /// <summary>
        /// The trait strip and category-immunity guard for one run creature (dynamic_dungeons_strip_combat_traits).
        /// Returns a short description of every change, for the spawner's log line; empty when nothing changed.
        /// Covers the creature's OWN properties, every item it has equipped (weapons, shields, ammo, armor),
        /// AND every item still sitting in its Inventory - several of these flags are read from the attacker's
        /// weapon as well as the attacker itself (see DungeonCombatNormalizer.BypassBools/BypassFloats), so a
        /// strip that only touched the creature leaves a wielded weapon's copy fully live. See
        /// StripItemCombatTraits for the per-item half and its per-instance-safety citation.
        ///
        /// Inventory is covered too because a Threads creature can equip a NEW item from Inventory mid-fight
        /// with no strip in between: Monster_Missile.SwitchToMeleeAttack (Monster_Missile.cs:222-256) destroys
        /// the ranged weapon/ammo and calls EquipInventoryItems(true) (Monster_Inventory.cs:323-345), which
        /// pulls the melee backup straight out of Inventory via SelectWieldedWeapons
        /// (Monster_Inventory.cs:201-260, sourced from Monster_Inventory.cs:169-188). Monster_Tick.cs:135 does
        /// the same thing when a missile creature runs out of ammo. Both only ever RE-EQUIP an item that
        /// already exists in Inventory - neither creates a fresh instance from the weenie - so stripping
        /// Inventory once here at spawn covers both paths; nothing needs to run again at the switch site.
        /// Safe from a loot standpoint: ThreadDungeonSpawner.StripCorpseTransfer clears DestinationType on
        /// both creature.Inventory.Values and creature.EquippedObjects.Values
        /// (ThreadDungeonSpawner.cs:1079-1080), so nothing sitting in Inventory can reach the corpse either
        /// way, before or after this change.
        ///
        /// Order inside matters only once: the defense-skill cap reads plan.BandStandard, which the builder
        /// computes whenever this guard is on and the plan has a non-boss entry. The BOSS is exempt from the cap -
        /// with normalization on its defense is SET to the band standard afterwards, and with normalization off
        /// the owner's boss is allowed to be tougher than its pack.
        /// </summary>
        /// <param name="capStandard">
        /// The standard the defense ceiling is measured against, when it is not the run's own: a reach-up STAMPED
        /// creature is capped against the stat curve at its stamp level (2026-10-08). Null - every other creature,
        /// and every World Events caller of CombatTraitGuard - reads plan.BandStandard exactly as before.
        /// </param>
        internal static List<string> StripCombatTraits(Creature creature, DungeonSpawnPlan plan, bool isBoss, DungeonBandStandard capStandard = null)
        {
            var dials = new CombatGuardDials(plan.CategoryResistFloor, plan.CategoryArmorModCeiling, plan.DefenseSkillCapOffset);

            return CombatTraitGuard.Apply(creature, dials, capStandard ?? plan.BandStandard, isBoss);
        }

        /// <summary>
        /// Per-item half of the bool/float/int bypass strip above, applied to one equipped item rather than the
        /// creature itself. Split out so it is unit-testable without a live Creature (ACE.Server.Tests cannot
        /// construct one - Creature's constructor runs SetEphemeralValues, which needs a live PropertyManager
        /// and DatabaseManager.World; see ThreadDungeonSpawnerLootStripTests.cs's "NOT COVERED HERE" note for
        /// the same constraint on StripCorpseTransfer). A GenericObject/MeleeWeapon item has no such
        /// dependency and can be built directly.
        ///
        /// Necessary at all because combat does not read these bypass flags from the creature alone: several
        /// are read as (weapon?.Flag ?? false) || creature.Flag, or MAX/MIN of the two - see
        /// DungeonCombatNormalizer.BypassBools/BypassFloats for the exact call sites. A strip that only
        /// touched the creature's own properties left a wielded weapon's copy fully live (the production
        /// incident this fixes: boss wcid 24496 General Garsh wielded axe wcid 24567 carrying
        /// IgnoreMagicResist/IgnoreMagicArmor on the WEAPON's own row).
        ///
        /// Per instance: each equipped item is its own WorldObject, converted from its own weenie the same way
        /// the creature was. PropertiesBool, PropertiesFloat and PropertiesInt are copied into fresh
        /// dictionaries per biota (ACE.Entity/Adapter/WeenieConverter.cs:22, :26, :30 - the same precedent the
        /// class remarks above cite for the creature), so removing a property here never reaches the cached
        /// weapon weenie or any other creature's copy of the same wcid.
        /// </summary>
        internal static List<string> StripItemCombatTraits(WorldObject item)
        {
            var changes = new List<string>();

            if (item == null)
                return changes;

            foreach (var p in DungeonCombatNormalizer.BypassBools)
            {
                if (item.GetProperty(p) == true)
                {
                    item.RemoveProperty(p);
                    changes.Add(p.ToString());
                }
            }

            foreach (var p in DungeonCombatNormalizer.BypassFloats)
            {
                if (item.GetProperty(p).HasValue)
                {
                    item.RemoveProperty(p);
                    changes.Add(p.ToString());
                }
            }

            foreach (var p in DungeonCombatNormalizer.BypassInts)
            {
                if (item.GetProperty(p).HasValue)
                {
                    item.RemoveProperty(p);
                    changes.Add(p.ToString());
                }
            }

            return changes;
        }

        /// <summary>
        /// Boss normalization's stat half (dynamic_dungeons_boss_normalize): attack and defense skills, body-part
        /// damage and armour, and health regen are SET to the band standard. Called AFTER any adaptive-band uplift,
        /// so the two-way SET is the value that stands. Returns (skills changed, body parts changed, regen set).
        ///
        /// Skills are set on InitLevel, the authored base DungeonBandStandard's medians are taken over - the same
        /// axis ApplyUplift floors. A skill's Current still adds its attribute formula for a usable skill
        /// (CreatureSkill.cs:170-179), so a boss authored with very high attributes keeps that part; that is the
        /// uplift's existing behaviour too, and attributes are not normalized here.
        ///
        /// HealthRate is written to BOTH the property and Health.RegenRate, because CreatureVital caches the rate
        /// once at construction (CreatureVital.cs:37-38) and the creature already exists by now.
        /// </summary>
        internal static (int SkillsSet, int PartsChanged, bool RegenSet, int WeaponsChanged) NormalizeBoss(Creature creature, DungeonSpawnPlan plan)
        {
            var standard = plan.BandStandard;

            if (standard == null || standard.IsEmpty)
                return (0, 0, false, 0);

            var skillsSet = 0;

            foreach (var skill in DungeonCombatNormalizer.AttackSkills)
                skillsSet += SetSkill(creature, skill, DungeonCombatNormalizer.AttackSkillTarget(standard, skill, plan.BossOffenseBandRatio));

            foreach (var skill in DungeonCombatNormalizer.DefenseSkills)
                skillsSet += SetSkill(creature, skill, DungeonCombatNormalizer.DefenseSkillTarget(standard, skill, plan.BossDefenseBandRatio));

            var parts = DungeonCombatNormalizer.SetBossBodyParts(creature.Biota, standard.MaxBodyDamage, standard.MaxBaseArmor,
                plan.BossOffenseBandRatio, plan.BossDefenseBandRatio);

            var regenSet = false;

            if (standard.HealthRate > 0)
            {
                creature.HealthRate = standard.HealthRate;
                creature.Health.RegenRate = standard.HealthRate;
                regenSet = true;
            }

            return (skillsSet, parts, regenSet, NormalizeBossWeapons(creature, plan));
        }

        /// <summary>
        /// SETS a normalized boss's six primary attributes to the band medians x the matching ratio
        /// (DungeonCombatNormalizer.AttributeTarget) and returns how many changed. StartingValue takes the median and
        /// Ranks goes to 0, so CreatureAttribute.Base (Ranks + StartingValue, CreatureAttribute.cs:102-106) equals the
        /// basis DungeonStatProfile.Attributes measured the band on (InitLevel + LevelFromCP).
        ///
        /// Per instance: PropertiesAttribute is CLONED per biota (ACE.Entity/Adapter/WeenieConverter.cs:117-122),
        /// and every Creature carries all six CreatureAttribute wrappers (Creature.cs:185-190).
        ///
        /// Called BEFORE the spawner's health block (see the call site). The three vitals' Current values were set
        /// from the AUTHORED attributes at construction (Creature.cs:215-217), so they are refilled to the new
        /// maxima here; the boss health block then re-targets Health on top.
        /// </summary>
        internal static int NormalizeBossAttributes(Creature creature, DungeonSpawnPlan plan)
        {
            var standard = plan.BandStandard;

            if (standard == null || standard.IsEmpty)
                return 0;

            var set = 0;

            foreach (var attribute in DungeonCombatNormalizer.OffenseAttributes.Concat(DungeonCombatNormalizer.DefenseAttributes))
            {
                var target = DungeonCombatNormalizer.AttributeTarget(standard, attribute, plan.BossOffenseBandRatio, plan.BossDefenseBandRatio);

                if (target == 0 || !creature.Attributes.TryGetValue(attribute, out var creatureAttribute) || creatureAttribute == null)
                    continue;

                if (creatureAttribute.StartingValue == target && creatureAttribute.Ranks == 0)
                    continue;

                creatureAttribute.StartingValue = target;
                creatureAttribute.Ranks = 0;
                set++;
            }

            if (set > 0)
            {
                creature.Health.Current = creature.Health.MaxValue;
                creature.Stamina.Current = creature.Stamina.MaxValue;
                creature.Mana.Current = creature.Mana.MaxValue;
            }

            return set;
        }

        /// <summary>
        /// SETS the damage of every weapon and ammunition item a normalized boss carries (equipped or in its
        /// inventory, since Monster_Missile/Monster_Tick call EquipInventoryItems to swap weapons mid-fight) so its
        /// effective maximum lands on the band standard's body-part damage x the offense ratio. Returns how many
        /// items changed. The arithmetic and why it is exact are on DungeonCombatNormalizer.WeaponDamageTarget.
        ///
        /// Scaled rather than stripped: the non-player damage max is linear in Damage, so scaling reproduces the band
        /// figure exactly while the boss keeps the weapon's damage type, animations and appearance; stripping would
        /// change how the boss fights (a weaponless creature falls back to body parts it may not author).
        ///
        /// Per instance: each wielded item is its own WorldObject with its own biota, whose PropertiesInt and
        /// PropertiesFloat are fresh dictionary copies (ACE.Entity/Adapter/WeenieConverter.cs:26, :30), so the
        /// write never reaches the cached weapon weenie or any other creature's copy.
        ///
        /// A MissileLauncher is skipped: on the non-player path it contributes only its DamageMod to the ammunition's
        /// damage (WorldObject.GetDamageMod, WorldObject.cs:864-874); its own Damage is read only for a PLAYER's shot
        /// (DamageEvent.cs:473-474). Ammunition uses the launcher's DamageMod; everything else its own.
        /// </summary>
        internal static int NormalizeBossWeapons(Creature creature, DungeonSpawnPlan plan)
        {
            var standard = plan.BandStandard;

            if (standard == null || standard.IsEmpty || standard.MaxBodyDamage == 0)
                return 0;

            var items = creature.EquippedObjects.Values.Concat(creature.Inventory.Values).Where(i => i != null).Distinct().ToList();
            var launcher = items.FirstOrDefault(i => i is MissileLauncher);
            var changed = 0;

            foreach (var item in items)
            {
                if (item is MissileLauncher)
                    continue;

                var damage = item.GetProperty(PropertyInt.Damage) ?? 0;

                if (damage <= 0)
                    continue;

                var modSource = item.WeenieType == WeenieType.Ammunition ? launcher : item;
                var damageMod = modSource?.GetProperty(PropertyFloat.DamageMod) ?? 1.0;
                var target = DungeonCombatNormalizer.WeaponDamageTarget(damage, damageMod, standard.MaxBodyDamage, plan.BossOffenseBandRatio);

                if (target == damage)
                    continue;

                item.SetProperty(PropertyInt.Damage, target);
                changed++;
            }

            return changed;
        }

        /// <summary>Removes the boss's authored <see cref="BossRatingIds"/>, returning how many it carried.</summary>
        internal static int ZeroBossRatings(Creature creature)
        {
            var removed = 0;

            foreach (var id in BossRatingIds)
            {
                if (creature.GetProperty(id).HasValue)
                {
                    creature.RemoveProperty(id);
                    removed++;
                }
            }

            return removed;
        }

        private static int SetSkill(Creature creature, Skill skill, uint target)
        {
            if (target == 0)
                return 0;

            // add: false - a boss is never handed a school or weapon skill it was not authored with.
            var creatureSkill = creature.GetCreatureSkill(skill, false);

            if (creatureSkill == null || creatureSkill.InitLevel == target)
                return 0;

            creatureSkill.InitLevel = target;
            return 1;
        }
    }
}
