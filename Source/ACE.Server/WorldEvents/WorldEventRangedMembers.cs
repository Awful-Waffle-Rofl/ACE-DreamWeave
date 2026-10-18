using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;

using ACE.Database;
using ACE.Entity.Enum;
using ACE.Server.WorldEvents.Defs;

using log4net;

namespace ACE.Server.WorldEvents
{
    /// <summary>
    /// Hold ranged waves: which family members attack from a distance. A member is ranged when it casts
    /// (FamilyMember.Caster, a non-empty spellbook, already known from the catalog) or when its weenie wields
    /// a missile launcher. The weenie check is one cached-weenie read per wcid, remembered for the life of
    /// the server.
    /// </summary>
    public static class WorldEventRangedMembers
    {
        private static readonly ILog log = LogManager.GetLogger(typeof(WorldEventRangedMembers));

        private static readonly ConcurrentDictionary<uint, bool> wieldsMissile = new ConcurrentDictionary<uint, bool>();

        /// <summary>Test seam: replaces the weenie lookup (wcid to "wields a missile launcher").</summary>
        internal static Func<uint, bool> MissileLookup = LookUpWieldsMissile;

        public static bool IsRanged(FamilyMember member)
        {
            if (member == null || member.Wcid == 0)
                return false;

            return member.Caster || wieldsMissile.GetOrAdd(member.Wcid, w => SafeLookup(w));
        }

        /// <summary>
        /// The ranged members a ranged wave draws from (D6 - pure apart from <see cref="IsRanged"/>): those inside
        /// <paramref name="band"/>, else every ranged member of the roster (a family with only out-of-band
        /// archers still gets a ranged wave). Empty when the family has none at all.
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
                return MissileLookup(wcid);
            }
            catch (Exception ex)
            {
                log.Warn($"[WORLDEVENT] ranged check for wcid {wcid} failed; treated as not ranged", ex);
                return false;
            }
        }

        private static bool LookUpWieldsMissile(uint wcid)
        {
            var weenie = DatabaseManager.World.GetCachedWeenie(wcid);

            if (weenie?.PropertiesCreateList == null)
                return false;

            foreach (var item in weenie.PropertiesCreateList)
            {
                if (item == null || (item.DestinationType & DestinationType.Wield) == 0)
                    continue;

                var itemWeenie = DatabaseManager.World.GetCachedWeenie(item.WeenieClassId);

                if (itemWeenie != null && itemWeenie.WeenieType == WeenieType.MissileLauncher)
                    return true;
            }

            return false;
        }
    }
}
