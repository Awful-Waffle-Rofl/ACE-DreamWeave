using ACE.Entity.Enum;
using ACE.Server.MonsterEffects;
using ACE.Server.WorldObjects;

namespace ACE.Server.Frays
{
    /// <summary>
    /// Names a hit on a fire creature by where it came from (melee, missile, spells by school, procs, DoT ticks, pets),
    /// for the camp log's damage split (FraysSessions.OnSourcedHit).
    /// </summary>
    public static class FraysHitKinds
    {
        /// <summary>What a Creature.TakeDamage hit was: a pet, a DoT tick, the camper's own strike, or a proc.</summary>
        public static string KindOf(WorldObject source, IncomingDamageOrigin origin, DamageType damageType)
        {
            if (source == null)
                return "DoT ticks";

            if (source is Pet || source is CombatPet)
                return "pets";

            if (source is Player player)
            {
                if (origin == IncomingDamageOrigin.DirectHit)
                    return player.CombatMode == CombatMode.Missile ? "missile" : player.CombatMode == CombatMode.Magic ? "spells" : "melee";

                // a proc riding a strike: the Poison Weapon proc is Base damage, an Acid Proc tick acid
                return damageType == DamageType.Base ? "procs: poison" : $"procs: {DamageName(damageType)}";
            }

            return "other";
        }

        /// <summary>What a SpellProjectile hit was: its school, and whether it was a proc, an echo or a splash.</summary>
        public static string SpellKind(SpellProjectile p)
        {
            if (p.ProjectileSource is Pet || p.ProjectileSource is CombatPet)
                return "pets";

            if (p.ProjectileSource is Player && p.Spell != null)
            {
                var school = p.Spell.School == MagicSchool.WarMagic ? "war" : p.Spell.School == MagicSchool.VoidMagic ? "void" : p.Spell.School == MagicSchool.LifeMagic ? "life" : p.Spell.School.ToString().ToLowerInvariant();
                var how = p.IsEchoCopy ? " (Echo Cast)" : p.IsClassAbilitySpawned ? " (Spell AOE and other copies)" : p.IsClassAbilityProc || p.FromProc ? " (procs)" : "";
                return school + how;
            }

            return "other";
        }

        private static string DamageName(DamageType t) => t switch
        {
            DamageType.Slash => "slashing",
            DamageType.Pierce => "piercing",
            DamageType.Bludgeon => "bludgeoning",
            DamageType.Electric => "lightning",
            _ => t.ToString().ToLowerInvariant(),
        };
    }
}
