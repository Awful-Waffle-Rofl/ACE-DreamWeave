using System.Collections.Generic;
using System.Linq;

using ACE.Entity.Enum;
using ACE.Server.ClassAbilities;
using ACE.Server.ClassAbilities.Abilities;
using ACE.Server.Entity;
using ACE.Server.WorldObjects;

namespace ACE.Server.Frays
{
    /// <summary>
    /// The Blood Mage's AoE boons, which lift the Blood Mage outside the usual rules to bring it up to
    /// power. A Blood Mage fought one creature at a time and earned a third of the others' XP, which Frays
    /// pays by health cleared, most of it in packs. Both work only while camping (they live on the camp's mods).
    ///
    ///  - Crimson Splash: a drain also drains 1, 2 or 3 nearby creatures (Crimson Harvest's re-entry, so each is a
    ///    full drain), and a Bloodstone Bolt or Martyr's Hecatomb also strikes them at blood_splash_damage.
    ///  - Hecatomb's Reach: a Martyr's Hecatomb or Curse of Raven Fury also strikes every creature near its target,
    ///    at hecatomb_reach_damage.
    ///
    /// The extra hits are Spell AOE children (SpawnClassAbilityAoeChild): they never splash again, never echo, never
    /// fire item procs.
    /// </summary>
    public static class FraysBloodBoons
    {
        /// <summary>Crimson Splash's extra creatures for this caster (0 without the boon or outside a camp).</summary>
        public static int SplashTargets(Player caster) => caster?.FraysMods?.BloodSplash ?? 0;

        /// <summary>Martyr's Hecatomb and Curse of Raven Fury: life projectiles that hit health.</summary>
        public static bool IsHecatomb(Spell spell) =>
            spell != null && spell.School == MagicSchool.LifeMagic && spell.MetaSpellType == SpellType.LifeProjectile
            && (spell.DamageType & DamageType.Health) != 0;

        /// <summary>From the spell-hit dispatch: a caster's own landed projectile (never a splash child).</summary>
        public static void OnSpellHit(Player caster, Creature target, SpellProjectile projectile)
        {
            var m = caster?.FraysMods;
            var spell = projectile?.Spell;

            if (m == null || spell == null || target == null || target is Player || projectile.IsClassAbilitySpawned)
                return;

            int count;
            double mult;

            if (m.HecatombReach && IsHecatomb(spell))
            {
                count = int.MaxValue;
                mult = FraysSettings.Number(FraysSettings.HecatombReachDamage);
            }
            else if (m.BloodSplash > 0 && (IsHecatomb(spell) || BloodChargeMath.IsLifeHealthBolt(spell.School, spell.MetaSpellType, spell.DamageType)))
            {
                count = m.BloodSplash;
                mult = FraysSettings.Number(FraysSettings.BloodSplashDamage);
            }
            else
                return;

            if (mult <= 0)
                return;

            foreach (var neighbor in Neighbors(caster, target, count))
                projectile.SpawnClassAbilityAoeChild(caster, target, neighbor, (float)mult);
        }

        /// <summary>
        /// Up to <paramref name="max"/> hostile creatures near <paramref name="primary"/>, nearest first, within
        /// <paramref name="radius"/> (blood_boon_radius when null). The same filters as Spell AOE and Crimson Harvest.
        /// </summary>
        public static List<Creature> Neighbors(Player caster, Creature primary, int max, double? radius = null)
        {
            var list = new List<Creature>();

            if (caster == null || primary == null || max <= 0)
                return list;

            var r = radius ?? FraysSettings.Number(FraysSettings.BloodBoonRadius);

            if (r <= 0 || caster.PhysicsObj == null || primary.Location == null)
                return list;

            var candidates = new List<Creature>();
            var distances = new List<double>();

            foreach (var obj in caster.PhysicsObj.ObjMaint.GetVisibleObjectsValuesWhere(o => o.WeenieObj.WorldObject != null))
            {
                if (obj.WeenieObj.WorldObject is not Creature c || c == primary || c == caster)
                    continue;

                if (c.IsDead || c.Teleporting || c.Location == null || c is Player || c is CombatPet)
                    continue;

                if (Player.IsObjectiveProbeRefused(caster, c))
                    continue;

                if (!caster.CanDamage(c) || caster.CheckPKStatusVsTarget(c, null) != null)
                    continue;

                candidates.Add(c);
                distances.Add(primary.Location.DistanceTo(c.Location));
            }

            foreach (var i in CrimsonHarvestMath.SelectSecondaryTargets(distances, r, max))
                list.Add(candidates[i]);

            return list;
        }
    }
}
