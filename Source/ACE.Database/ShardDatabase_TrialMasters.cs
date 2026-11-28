using System.Collections.Generic;
using System.Linq;

using Microsoft.EntityFrameworkCore;

using ACE.Database.Models.Shard;

namespace ACE.Database
{
    public partial class ShardDatabase
    {
        /// <summary>
        /// Every biota_properties_string row of <paramref name="type"/> whose object id is within [<paramref name="minId"/>,
        /// <paramref name="maxId"/>], as object id to value. One plain synchronous read on its own short-lived context:
        /// untracked, no biota is materialized, and nothing enters the biota cache (ShardDatabaseWithCaching does not
        /// override it). Balance Trials reads the master stamps (PropertyString.TrialArchetypeStamp) of the parked masters
        /// with it, which PlayerManager never loads.
        /// </summary>
        public Dictionary<uint, string> GetBiotaStringPropertyInRange(ushort type, uint minId, uint maxId)
        {
            using (var context = new ShardDbContext())
            {
                return context.BiotaPropertiesString
                    .AsNoTracking()
                    .Where(r => r.Type == type && r.ObjectId >= minId && r.ObjectId <= maxId)
                    .Select(r => new { r.ObjectId, r.Value })
                    .ToList()
                    .ToDictionary(r => r.ObjectId, r => r.Value);
            }
        }

        /// <summary>
        /// The ids of every character row within [<paramref name="minId"/>, <paramref name="maxId"/>], deleted or not (parked
        /// Balance Trials masters are IsDeleted). One plain synchronous read on its own short-lived context: only the id column
        /// is selected, nothing is tracked and nothing enters CharacterContexts.
        /// </summary>
        public HashSet<uint> GetCharacterIdsInRange(uint minId, uint maxId)
        {
            using (var context = new ShardDbContext())
            {
                return context.Character
                    .AsNoTracking()
                    .Where(r => r.Id >= minId && r.Id <= maxId)
                    .Select(r => r.Id)
                    .ToHashSet();
            }
        }
    }
}
