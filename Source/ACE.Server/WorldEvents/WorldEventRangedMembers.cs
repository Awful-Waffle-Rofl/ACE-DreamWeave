using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;

using ACE.Database;
using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Entity.Models;
using ACE.Server.WorldEvents.Defs;
using ACE.Server.WorldObjects;

using log4net;

namespace ACE.Server.WorldEvents
{
    /// <summary>
    /// Hold ranged roles: which monsters really attack from a distance. A monster counts only with a ranged attack
    /// it actually uses (<see cref="HasRealRangedAttack"/>):
    ///   * offensive spells it casts often enough: a projectile spell or Harm, with a combined chance per attack
    ///     roll of at least <see cref="MinOffensiveCastChance"/> (a melee monster with a 3% bolt is not a caster);
    ///   * a wielded missile launcher AND matching ammunition (arrows for a bow, bolts for a crossbow);
    ///   * a wielded thrown weapon (its own ammo).
    /// The weenie check is one cached-weenie read per wcid, remembered for the life of the server. The spawner
    /// re-checks the constructed creature (<see cref="CreatureHasRealRangedAttack"/>) and gives a monster that
    /// fails another role. A family with no ranged member borrows them from a family of the same theme
    /// (<see cref="CompatiblePool"/>, themes in <see cref="WorldEventFamilyThemes"/>).
    /// </summary>
    public static class WorldEventRangedMembers
    {
        private static readonly ILog log = LogManager.GetLogger(typeof(WorldEventRangedMembers));

        /// <summary>
        /// The least combined chance per attack roll of an offensive spell for a monster to count as a caster.
        /// Real casters sit well above it (a 25% Harm, five bolts at 10%); melee monsters with an incidental bolt
        /// sit below (two bolts at 3% is 6%).
        /// </summary>
        public const double MinOffensiveCastChance = 0.15;

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
        /// The rule (D6 - pure): offensive spells cast often enough, a wielded thrown weapon, or a wielded launcher
        /// with ammunition that fits it. A launcher with no (or the wrong) ammo, spells that only buff and heal,
        /// or a rare incidental bolt is not a ranged attack.
        /// </summary>
        public static bool HasRealRangedAttack(bool launcherWielded, bool hasMatchingAmmo, bool thrownWielded, double offensiveCastChance)
        {
            return (double.IsFinite(offensiveCastChance) && offensiveCastChance >= MinOffensiveCastChance)
                || thrownWielded || (launcherWielded && hasMatchingAmmo);
        }

        /// <summary>
        /// One spellbook entry's chance per attack roll (D6 - pure), as the monster AI reads it (Monster_Magic):
        /// a value above 2 is 2 plus the chance (2.25 is 25%), anything else is a percentage (2.0 or less / 100).
        /// </summary>
        public static double SpellChance(float bookValue)
        {
            if (!float.IsFinite(bookValue) || bookValue <= 0)
                return 0;

            return Math.Clamp(bookValue > 2.0f ? bookValue - 2.0 : bookValue / 100.0, 0, 1);
        }

        /// <summary>
        /// The chance that one attack roll casts an offensive spell (D6 - pure): 1 minus the chance that every
        /// offensive entry in <paramref name="book"/> fails its own roll. Buffs and heals add nothing.
        /// </summary>
        public static double OffensiveCastChance(IEnumerable<KeyValuePair<int, float>> book, Func<uint, bool> isOffensive)
        {
            if (book == null || isOffensive == null)
                return 0;

            var none = 1.0;

            foreach (var entry in book)
            {
                if (entry.Key <= 0 || !isOffensive((uint)entry.Key))
                    continue;

                none *= 1.0 - SpellChance(entry.Value);
            }

            return 1.0 - none;
        }

        /// <summary>
        /// The offensive spell a held ranged monster casts next (D6 - pure): one of the offensive entries in
        /// <paramref name="book"/>, weighted by its own chance (each at least 1%), chosen by
        /// <paramref name="roll"/> in [0, 1). 0 when the book has none.
        /// </summary>
        public static int PickOffensiveSpell(IEnumerable<KeyValuePair<int, float>> book, Func<uint, bool> isOffensive, double roll)
        {
            if (book == null || isOffensive == null)
                return 0;

            var offensive = book.Where(e => e.Key > 0 && isOffensive((uint)e.Key))
                .Select(e => (Id: e.Key, Weight: Math.Max(0.01, SpellChance(e.Value))))
                .ToList();

            if (offensive.Count == 0)
                return 0;

            var total = offensive.Sum(e => e.Weight);
            var target = Math.Clamp(double.IsFinite(roll) ? roll : 0, 0, 0.999999) * total;

            foreach (var e in offensive)
            {
                if (target < e.Weight)
                    return e.Id;

                target -= e.Weight;
            }

            return offensive[offensive.Count - 1].Id;
        }

        /// <summary>
        /// Whether ammunition fits a launcher (D6 - pure), by their AmmoType flags: arrows for a bow, bolts for a
        /// crossbow, darts for an atlatl. Either one with no ammo type set fits, as the stock AI would fire it.
        /// </summary>
        public static bool AmmoFits(int? launcherAmmoType, int? ammoAmmoType)
        {
            var launcher = launcherAmmoType ?? 0;
            var ammo = ammoAmmoType ?? 0;

            return launcher == 0 || ammo == 0 || (launcher & ammo) != 0;
        }

        /// <summary>
        /// Whether one spell is offensive at range (D6 - pure, from its world-database row): it launches a projectile
        /// (a projectile wcid, or projectiles to fire), or it lowers health (a negative Boost on health: Harm).
        /// Buffs, heals, debuffs and every other spell are not.
        /// </summary>
        public static bool IsOffensiveSpell(uint? projectileWcid, int? numProjectiles, int? boost, int? vitalDamageType)
        {
            if ((projectileWcid ?? 0) != 0 || (numProjectiles ?? 0) > 0)
                return true;

            // A harmful health boost (Harm) is negative.
            return (boost ?? 0) < 0 && (vitalDamageType ?? 0) == (int)DamageType.Health;
        }

        /// <summary>
        /// The constructed creature (spawner, before EnterWorld): its equipped launcher with equipped ammunition
        /// that fits it, an equipped thrown weapon, and how often its own spellbook casts an offensive spell.
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
                var ammo = creature.GetEquippedAmmo();
                var ammoFits = launcher != null && ammo != null && AmmoFits((int?)launcher.AmmoType, (int?)ammo.AmmoType);

                var chance = OffensiveCastChance(creature.Biota?.PropertiesSpellBook?.ToList(), OffensiveSpellLookup);

                return HasRealRangedAttack(launcher != null, ammoFits, thrown, chance);
            }
            catch (Exception ex)
            {
                log.Warn($"[WORLDEVENT] ranged check for {creature.Name} ({creature.WeenieClassId}) failed; treated as not ranged", ex);
                return false;
            }
        }

        /// <summary>
        /// A caster (D6 - pure): its ranged attack is spells (offensive spells often enough) and it has no physical one (no
        /// launcher with fitting ammunition, no thrown weapon). World Events Hold tunes casters apart from archers.
        /// </summary>
        public static bool IsCasterAttack(bool launcherWithAmmo, bool thrownWielded, double offensiveCastChance) =>
            double.IsFinite(offensiveCastChance) && offensiveCastChance >= MinOffensiveCastChance && !launcherWithAmmo && !thrownWielded;

        /// <summary>The constructed creature is a caster (<see cref="IsCasterAttack"/>). False on any error.</summary>
        public static bool CreatureIsCaster(Creature creature)
        {
            if (creature == null)
                return false;

            try
            {
                var launcher = creature.GetEquippedMissileLauncher();
                var weapon = creature.GetEquippedMissileWeapon();
                var thrown = weapon != null && weapon.IsThrownWeapon;
                var ammo = creature.GetEquippedAmmo();
                var ammoFits = launcher != null && ammo != null && AmmoFits((int?)launcher.AmmoType, (int?)ammo.AmmoType);

                var chance = OffensiveCastChance(creature.Biota?.PropertiesSpellBook?.ToList(), OffensiveSpellLookup);

                return IsCasterAttack(ammoFits, thrown, chance);
            }
            catch (Exception ex)
            {
                log.Warn($"[WORLDEVENT] caster check for {creature.Name} ({creature.WeenieClassId}) failed; treated as not a caster", ex);
                return false;
            }
        }

        private static readonly ConcurrentDictionary<uint, bool> casterByWcid = new ConcurrentDictionary<uint, bool>();

        /// <summary>Test seam: wcid to "is a caster" (<see cref="IsCasterAttack"/> over its weenie).</summary>
        internal static Func<uint, bool> CasterLookup = LookUpCaster;

        /// <summary>True when the weenie <paramref name="wcid"/> is a caster. Cached.</summary>
        public static bool IsCasterWcid(uint wcid)
        {
            if (wcid == 0)
                return false;

            return casterByWcid.GetOrAdd(wcid, w =>
            {
                try
                {
                    return CasterLookup(w);
                }
                catch (Exception ex)
                {
                    log.Warn($"[WORLDEVENT] caster check for wcid {w} failed; treated as not a caster", ex);
                    return false;
                }
            });
        }

        /// <summary>Forgets the cached caster answers (tests swap <see cref="CasterLookup"/>).</summary>
        internal static void ClearCasterCache() => casterByWcid.Clear();

        private static bool LookUpCaster(uint wcid)
        {
            var weenie = DatabaseManager.World.GetCachedWeenie(wcid);

            if (weenie == null)
                return false;

            var launcherTypes = new List<int?>();
            var ammoTypes = new List<int?>();
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
                        launcherTypes.Add(itemWeenie.GetProperty(PropertyInt.AmmoType));
                    else if (itemWeenie.WeenieType == WeenieType.Ammunition)
                        ammoTypes.Add(itemWeenie.GetProperty(PropertyInt.AmmoType));
                    else if (itemWeenie.WeenieType == WeenieType.Missile && wielded)
                        thrown = true;
                }
            }

            var ammoFits = launcherTypes.Any(l => ammoTypes.Any(a => AmmoFits(l, a)));

            return IsCasterAttack(ammoFits, thrown, OffensiveCastChance(weenie.PropertiesSpellBook, OffensiveSpellLookup));
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

        /// <summary>How many of a borrowed family's ranged members a slot may draw from when none sits in the band: the nearest in level.</summary>
        public const int NearestBorrowed = 3;

        /// <summary>
        /// Ranged members borrowed from another family (D6 - pure apart from <see cref="IsRanged"/>), for a run whose
        /// own roster has none: the catalog families that share a theme with one of <paramref name="rosterFamilies"/>
        /// (<see cref="WorldEventFamilyThemes.Compatible"/>) and are not already in it. The family with the most ranged
        /// members inside <paramref name="band"/> wins (ties broken by <paramref name="rng"/>), and the slots draw from
        /// those members. With none in band anywhere, the <see cref="NearestBorrowed"/> ranged members nearest the
        /// band's centre across those families. Empty (and a null family) when no compatible family has any.
        /// </summary>
        public static List<uint> CompatiblePool(IEnumerable<string> rosterFamilies,
            IReadOnlyDictionary<string, IReadOnlyList<FamilyMember>> catalog, LevelBand band, out string borrowedFrom,
            Func<FamilyMember, bool> isRanged = null, Random rng = null)
        {
            borrowedFrom = null;
            isRanged ??= IsRanged;
            rng ??= new Random();

            var roster = (rosterFamilies ?? Enumerable.Empty<string>()).Where(f => !string.IsNullOrWhiteSpace(f)).ToList();

            if (catalog == null || roster.Count == 0)
                return new List<uint>();

            var candidates = new List<(string Id, List<FamilyMember> Ranged)>();

            foreach (var family in catalog.Keys.OrderBy(k => k, StringComparer.Ordinal))
            {
                if (roster.Contains(family, StringComparer.OrdinalIgnoreCase))
                    continue;

                if (!roster.Any(r => WorldEventFamilyThemes.Compatible(r, family)))
                    continue;

                var ranged = (catalog[family] ?? new List<FamilyMember>()).Where(m => m != null && m.Wcid != 0 && isRanged(m)).ToList();

                if (ranged.Count > 0)
                    candidates.Add((family, ranged));
            }

            if (candidates.Count == 0)
                return new List<uint>();

            var best = candidates.Max(c => c.Ranged.Count(m => band.Contains(m.Level)));

            if (best > 0)
            {
                var top = candidates.Where(c => c.Ranged.Count(m => band.Contains(m.Level)) == best).ToList();
                var pick = top[rng.Next(top.Count)];

                borrowedFrom = pick.Id;
                return pick.Ranged.Where(m => band.Contains(m.Level)).Select(m => m.Wcid).Distinct().ToList();
            }

            // Nobody in band: the few nearest the band's centre, whichever compatible family they come from.
            var nearest = candidates.SelectMany(c => c.Ranged.Select(m => (c.Id, Member: m)))
                .OrderBy(x => Math.Abs(x.Member.Level - band.Centre)).ThenBy(x => x.Member.Wcid)
                .Take(NearestBorrowed).ToList();

            borrowedFrom = string.Join("+", nearest.Select(x => x.Id).Distinct());
            return nearest.Select(x => x.Member.Wcid).Distinct().ToList();
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

            return WeenieHasRealRangedAttack(weenie, DatabaseManager.World.GetCachedWeenie, OffensiveSpellLookup);
        }

        /// <summary>
        /// <see cref="HasRealRangedAttack"/> for a weenie (pure over its arguments): a launcher wielded from its create
        /// list with fitting ammunition also in the list, a wielded thrown weapon, and its spellbook's offensive chance.
        /// </summary>
        public static bool WeenieHasRealRangedAttack(Weenie weenie, Func<uint, Weenie> itemLookup, Func<uint, bool> isOffensive)
        {
            if (weenie == null)
                return false;

            var launcherTypes = new List<int?>();
            var ammoTypes = new List<int?>();
            var thrown = false;

            if (weenie.PropertiesCreateList != null && itemLookup != null)
            {
                foreach (var item in weenie.PropertiesCreateList)
                {
                    if (item == null || item.WeenieClassId == 0)
                        continue;

                    var itemWeenie = itemLookup(item.WeenieClassId);

                    if (itemWeenie == null)
                        continue;

                    var wielded = (item.DestinationType & DestinationType.Wield) != 0;

                    if (itemWeenie.WeenieType == WeenieType.MissileLauncher && wielded)
                        launcherTypes.Add(itemWeenie.GetProperty(PropertyInt.AmmoType));
                    else if (itemWeenie.WeenieType == WeenieType.Ammunition)
                        ammoTypes.Add(itemWeenie.GetProperty(PropertyInt.AmmoType));
                    else if (itemWeenie.WeenieType == WeenieType.Missile && wielded)
                        thrown = true;
                }
            }

            var ammoFits = launcherTypes.Any(l => ammoTypes.Any(a => AmmoFits(l, a)));
            var chance = OffensiveCastChance(weenie.PropertiesSpellBook, isOffensive);

            return HasRealRangedAttack(launcherTypes.Count > 0, ammoFits, thrown, chance);
        }

        private static bool LookUpOffensiveSpell(uint spellId)
        {
            var spell = DatabaseManager.World.GetCachedSpell(spellId);

            return spell != null && IsOffensiveSpell(spell.Wcid, spell.NumProjectiles, spell.Boost, spell.DamageType);
        }
    }
}
