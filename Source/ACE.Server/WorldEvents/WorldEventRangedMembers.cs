using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;

using ACE.Database;
using ACE.Entity.Enum;
using ACE.Server.WorldEvents.Defs;
using ACE.Server.WorldObjects;

using log4net;

namespace ACE.Server.WorldEvents
{
    /// <summary>
    /// Hold ranged roles: which family members really attack from a distance. A member counts only with a REAL
    /// ranged attack (<see cref="HasRealRangedAttack"/>):
    ///   * an offensive spell in its spellbook: a projectile spell, or a spell that harms health (Harm, drains);
    ///     a book of buffs and heals alone does not count;
    ///   * a wielded missile launcher AND ammunition for it in its create list;
    ///   * a wielded thrown weapon (its own ammo).
    /// The weenie check is one cached-weenie read per wcid, remembered for the life of the server. The spawner
    /// re-checks the constructed creature (<see cref="CreatureHasRealRangedAttack"/>) and gives a monster that
    /// fails another role.
    /// </summary>
    public static class WorldEventRangedMembers
    {
        private static readonly ILog log = LogManager.GetLogger(typeof(WorldEventRangedMembers));

        private static readonly ConcurrentDictionary<uint, bool> rangedByWcid = new ConcurrentDictionary<uint, bool>();

        /// <summary>Test seam: replaces the weenie lookup (wcid to "has a real ranged attack").</summary>
        internal static Func<uint, bool> RangedLookup = LookUpRealRangedAttack;

        /// <summary>Test seam: replaces the spell lookup (spell id to "is an offensive spell").</summary>
        internal static Func<uint, bool> OffensiveSpellLookup = LookUpOffensiveSpell;

        /// <summary>Forgets the cached answers (tests swap the lookups).</summary>
        internal static void ClearCache() => rangedByWcid.Clear();

        public static bool IsRanged(FamilyMember member)
        {
            if (member == null || member.Wcid == 0)
                return false;

            return rangedByWcid.GetOrAdd(member.Wcid, w => SafeLookup(w));
        }

        /// <summary>
        /// The rule (D6 - pure): an offensive spell, a wielded thrown weapon, or a wielded launcher with ammunition.
        /// A launcher with no ammo, or spells that only buff and heal, is not a ranged attack.
        /// </summary>
        public static bool HasRealRangedAttack(bool launcherWielded, bool hasAmmo, bool thrownWielded, int offensiveSpells)
        {
            return offensiveSpells > 0 || thrownWielded || (launcherWielded && hasAmmo);
        }

        /// <summary>
        /// Whether one spell is offensive at range (D6 - pure, from its world-database row): it launches a projectile
        /// (a projectile wcid, or projectiles to fire), or it lowers health (a negative Boost on health: Harm,
        /// drains). Buffs, heals and every other spell are not.
        /// </summary>
        public static bool IsOffensiveSpell(uint? projectileWcid, int? numProjectiles, int? boost, int? vitalDamageType)
        {
            if ((projectileWcid ?? 0) != 0 || (numProjectiles ?? 0) > 0)
                return true;

            // A harmful health boost (Harm) is negative.
            return (boost ?? 0) < 0 && (vitalDamageType ?? 0) == (int)DamageType.Health;
        }

        /// <summary>
        /// The constructed creature (spawner, before EnterWorld): its equipped launcher and ammo, an equipped thrown
        /// weapon, and the offensive spells in its own spellbook.
        /// </summary>
        public static bool CreatureHasRealRangedAttack(Creature creature)
        {
            if (creature == null)
                return false;

            try
            {
                var launcher = creature.GetEquippedMissileLauncher();
                var weapon = creature.GetEquippedMissileWeapon();
                var thrown = weapon != null && weapon.IsThrownWeapon;
                var ammo = creature.GetEquippedAmmo() != null
                    || creature.Inventory.Values.Any(i => i != null && i.WeenieType == WeenieType.Ammunition);

                var offensive = 0;
                var book = creature.Biota?.PropertiesSpellBook;

                if (book != null)
                {
                    foreach (var spellId in book.Keys.ToList())
                    {
                        if (OffensiveSpellLookup((uint)spellId))
                            offensive++;
                    }
                }

                return HasRealRangedAttack(launcher != null, ammo, thrown, offensive);
            }
            catch (Exception ex)
            {
                log.Warn($"[WORLDEVENT] ranged check for {creature.Name} ({creature.WeenieClassId}) failed; treated as not ranged", ex);
                return false;
            }
        }

        /// <summary>
        /// The ranged members a ranged slot draws from (D6 - pure apart from <see cref="IsRanged"/>): those inside
        /// <paramref name="band"/>, else every ranged member of the roster (a family with only out-of-band
        /// archers still gets ranged). Empty when the family has none at all.
        /// </summary>
        public static List<uint> Pool(IEnumerable<FamilyMember> members, LevelBand band, Func<FamilyMember, bool> isRanged = null)
        {
            isRanged ??= IsRanged;

            var ranged = (members ?? Enumerable.Empty<FamilyMember>()).Where(m => m != null && isRanged(m)).ToList();

            var inBand = ranged.Where(m => band.Contains(m.Level)).Select(m => m.Wcid).Distinct().ToList();

            return inBand.Count > 0 ? inBand : ranged.Select(m => m.Wcid).Distinct().ToList();
        }

        private static bool SafeLookup(uint wcid)
        {
            try
            {
                return RangedLookup(wcid);
            }
            catch (Exception ex)
            {
                log.Warn($"[WORLDEVENT] ranged check for wcid {wcid} failed; treated as not ranged", ex);
                return false;
            }
        }

        private static bool LookUpRealRangedAttack(uint wcid)
        {
            var weenie = DatabaseManager.World.GetCachedWeenie(wcid);

            if (weenie == null)
                return false;

            var launcher = false;
            var ammo = false;
            var thrown = false;

            if (weenie.PropertiesCreateList != null)
            {
                foreach (var item in weenie.PropertiesCreateList)
                {
                    if (item == null || item.WeenieClassId == 0)
                        continue;

                    var itemWeenie = DatabaseManager.World.GetCachedWeenie(item.WeenieClassId);

                    if (itemWeenie == null)
                        continue;

                    var wielded = (item.DestinationType & DestinationType.Wield) != 0;

                    if (itemWeenie.WeenieType == WeenieType.MissileLauncher && wielded)
                        launcher = true;
                    else if (itemWeenie.WeenieType == WeenieType.Ammunition)
                        ammo = true;
                    else if (itemWeenie.WeenieType == WeenieType.Missile && wielded)
                        thrown = true;
                }
            }

            var offensive = 0;

            if (weenie.PropertiesSpellBook != null)
            {
                foreach (var spellId in weenie.PropertiesSpellBook.Keys)
                {
                    if (OffensiveSpellLookup((uint)spellId))
                        offensive++;
                }
            }

            return HasRealRangedAttack(launcher, ammo, thrown, offensive);
        }

        private static bool LookUpOffensiveSpell(uint spellId)
        {
            var spell = DatabaseManager.World.GetCachedSpell(spellId);

            return spell != null && IsOffensiveSpell(spell.Wcid, spell.NumProjectiles, spell.Boost, spell.DamageType);
        }
    }
}
