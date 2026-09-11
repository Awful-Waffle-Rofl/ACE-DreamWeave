using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage;

using log4net;

using ACE.Common;
using ACE.Common.Extensions;
using ACE.Database.Entity;
using ACE.Database.Extensions;
using ACE.Database.Models.Shard;
using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;

namespace ACE.Database
{
    // partial so the Mule Vendor vault DAO can live in its own file
    // (ShardDatabase_AccountVault.cs) instead of growing this one further.
    public partial class ShardDatabase
    {
        private static readonly ILog log = LogManager.GetLogger(System.Reflection.MethodBase.GetCurrentMethod().DeclaringType);

        public bool Exists(bool retryUntilFound)
        {
            var config = Common.ConfigManager.Config.MySql.Shard;

            for (; ; )
            {
                using (var context = new ShardDbContext())
                {
                    if (((RelationalDatabaseCreator)context.Database.GetService<IDatabaseCreator>()).Exists())
                    {
                        log.InfoFormat("[DATABASE] Successfully connected to {0} database on {1}:{2}.", config.Database, config.Host, config.Port);
                        return true;
                    }
                }

                log.Error($"[DATABASE] Attempting to reconnect to {config.Database} database on {config.Host}:{config.Port} in 5 seconds...");

                if (retryUntilFound)
                    Thread.Sleep(5000);
                else
                    return false;
            }
        }


        /// <summary>
        /// Will return uint.MaxValue if no records were found within the range provided.
        /// </summary>
        public uint GetMaxGuidFoundInRange(uint min, uint max)
        {
            using (var context = new ShardDbContext())
            {
                var result = context.Biota
                    .AsNoTracking()
                    .Where(r => r.Id >= min && r.Id <= max)
                    .OrderByDescending(r => r.Id)
                    .FirstOrDefault();

                if (result == null)
                    return uint.MaxValue;

                return result.Id;
            }
        }

        /// <summary>
        /// This will return available id's, in the form of sequence gaps starting from min.<para />
        /// If a gap is just 1 value wide, then both start and end will be the same number.
        /// </summary>
        public List<(uint start, uint end)> GetSequenceGaps(uint min, uint limitAvailableIDsReturned)
        {
            // References:
            // https://stackoverflow.com/questions/4340793/how-to-find-gaps-in-sequential-numbering-in-mysql/29736658#29736658
            // https://stackoverflow.com/questions/50402015/how-to-execute-sqlquery-with-entity-framework-core-2-1

            // This query is ugly, but very fast.
            var sql = "SET @available_ids=0, @rownum=0;"                                                + Environment.NewLine +
                      "SELECT"                                                                          + Environment.NewLine +
                      " z.gap_starts_at, z.gap_ends_at_not_inclusive, @available_ids:=@available_ids+(z.gap_ends_at_not_inclusive - z.gap_starts_at) as running_total_available_ids" + Environment.NewLine +
                      "FROM ("                                                                          + Environment.NewLine +
                      " SELECT"                                                                         + Environment.NewLine +
                      "  @rownum:=@rownum+1 AS gap_starts_at,"                                          + Environment.NewLine +
                      "  @available_ids:=0,"                                                            + Environment.NewLine +
                      "  IF(@rownum=id, 0, @rownum:=id) AS gap_ends_at_not_inclusive"                   + Environment.NewLine +
                      " FROM"                                                                           + Environment.NewLine +
                      "  (SELECT @rownum:=(SELECT MIN(id)-1 FROM biota WHERE id > " + min + ")) AS a"   + Environment.NewLine +
                      "  JOIN biota"                                                                    + Environment.NewLine +
                      "  WHERE id > " + min                                                             + Environment.NewLine +
                      "  ORDER BY id"                                                                   + Environment.NewLine +
                      " ) AS z" + Environment.NewLine;
            if (limitAvailableIDsReturned != uint.MaxValue)
                sql += "WHERE z.gap_ends_at_not_inclusive!=0 AND @available_ids<" + limitAvailableIDsReturned + "; ";
            else
                sql += "WHERE z.gap_ends_at_not_inclusive!=0;";

            using (var context = new ShardDbContext())
            {
                context.Database.SetCommandTimeout(TimeSpan.FromMinutes(5));

                var connection = context.Database.GetDbConnection();
                connection.Open();
                var command = connection.CreateCommand();
                command.CommandText = sql;
                var reader = command.ExecuteReader();

                var gaps = new List<(uint start, uint end)>();

                while (reader.Read())
                {
                    var gap_starts_at               = reader.GetFieldValue<long>(0);
                    var gap_ends_at_not_inclusive   = reader.GetFieldValue<decimal>(1);
                    //var running_total_available_ids = reader.GetFieldValue<double>(2);

                    gaps.Add(((uint)gap_starts_at, (uint)gap_ends_at_not_inclusive - 1));
                }

                return gaps;
            }
        }


        public int GetBiotaCount()
        {
            using (var context = new ShardDbContext())
                return context.Biota.Count();
        }

        public int GetEstimatedBiotaCount(string dbName)
        {
            // https://mariadb.com/kb/en/incredibly-slow-count-on-mariadb-mysql/

            var sql = $"SELECT TABLE_ROWS FROM information_schema.tables" + Environment.NewLine +
                      $"WHERE TABLE_SCHEMA = '{dbName}'" + Environment.NewLine +
                      $"AND TABLE_NAME = 'biota';";

            using (var context = new ShardDbContext())
            {
                var connection = context.Database.GetDbConnection();
                connection.Open();
                var command = connection.CreateCommand();
                command.CommandText = sql;
                var reader = command.ExecuteReader();

                var biotaEstimatedCount = 0;

                while (reader.Read())
                {
                    biotaEstimatedCount = reader.GetFieldValue<int>(0);
                }

                return biotaEstimatedCount;
            }    
        }

        [Flags]
        public enum PopulatedCollectionFlags
        {
            BiotaPropertiesAnimPart             = 0x1,
            BiotaPropertiesAttribute            = 0x2,
            BiotaPropertiesAttribute2nd         = 0x4,
            BiotaPropertiesBodyPart             = 0x8,
            BiotaPropertiesBook                 = 0x10,
            BiotaPropertiesBookPageData         = 0x20,
            BiotaPropertiesBool                 = 0x40,
            BiotaPropertiesCreateList           = 0x80,
            BiotaPropertiesDID                  = 0x100,
            BiotaPropertiesEmote                = 0x200,
            BiotaPropertiesEnchantmentRegistry  = 0x400,
            BiotaPropertiesEventFilter          = 0x800,
            BiotaPropertiesFloat                = 0x1000,
            BiotaPropertiesGenerator            = 0x2000,
            BiotaPropertiesIID                  = 0x4000,
            BiotaPropertiesInt                  = 0x8000,
            BiotaPropertiesInt64                = 0x10000,
            BiotaPropertiesPalette              = 0x20000,
            BiotaPropertiesPosition             = 0x40000,
            BiotaPropertiesSkill                = 0x80000,
            BiotaPropertiesSpellBook            = 0x100000,
            BiotaPropertiesString               = 0x200000,
            BiotaPropertiesTextureMap           = 0x400000,
            HousePermission                     = 0x800000,
            BiotaPropertiesAllegiance           = 0x1000000,
        }

        public static void SetBiotaPopulatedCollections(Biota biota)
        {
            PopulatedCollectionFlags populatedCollectionFlags = 0;

            if (biota.BiotaPropertiesAnimPart != null && biota.BiotaPropertiesAnimPart.Count > 0) populatedCollectionFlags |= PopulatedCollectionFlags.BiotaPropertiesAnimPart;
            if (biota.BiotaPropertiesAttribute != null && biota.BiotaPropertiesAttribute.Count > 0) populatedCollectionFlags |= PopulatedCollectionFlags.BiotaPropertiesAttribute;
            if (biota.BiotaPropertiesAttribute2nd != null && biota.BiotaPropertiesAttribute2nd.Count > 0) populatedCollectionFlags |= PopulatedCollectionFlags.BiotaPropertiesAttribute2nd;
            if (biota.BiotaPropertiesBodyPart != null && biota.BiotaPropertiesBodyPart.Count > 0) populatedCollectionFlags |= PopulatedCollectionFlags.BiotaPropertiesBodyPart;
            if (biota.BiotaPropertiesBook != null) populatedCollectionFlags |= PopulatedCollectionFlags.BiotaPropertiesBook;
            if (biota.BiotaPropertiesBookPageData != null && biota.BiotaPropertiesBookPageData.Count > 0) populatedCollectionFlags |= PopulatedCollectionFlags.BiotaPropertiesBookPageData;
            if (biota.BiotaPropertiesBool != null && biota.BiotaPropertiesBool.Count > 0) populatedCollectionFlags |= PopulatedCollectionFlags.BiotaPropertiesBool;
            if (biota.BiotaPropertiesCreateList != null && biota.BiotaPropertiesCreateList.Count > 0) populatedCollectionFlags |= PopulatedCollectionFlags.BiotaPropertiesCreateList;
            if (biota.BiotaPropertiesDID != null && biota.BiotaPropertiesDID.Count > 0) populatedCollectionFlags |= PopulatedCollectionFlags.BiotaPropertiesDID;
            if (biota.BiotaPropertiesEmote != null && biota.BiotaPropertiesEmote.Count > 0) populatedCollectionFlags |= PopulatedCollectionFlags.BiotaPropertiesEmote;
            if (biota.BiotaPropertiesEnchantmentRegistry != null && biota.BiotaPropertiesEnchantmentRegistry.Count > 0) populatedCollectionFlags |= PopulatedCollectionFlags.BiotaPropertiesEnchantmentRegistry;
            if (biota.BiotaPropertiesEventFilter != null && biota.BiotaPropertiesEventFilter.Count > 0) populatedCollectionFlags |= PopulatedCollectionFlags.BiotaPropertiesEventFilter;
            if (biota.BiotaPropertiesFloat != null && biota.BiotaPropertiesFloat.Count > 0) populatedCollectionFlags |= PopulatedCollectionFlags.BiotaPropertiesFloat;
            if (biota.BiotaPropertiesGenerator != null && biota.BiotaPropertiesGenerator.Count > 0) populatedCollectionFlags |= PopulatedCollectionFlags.BiotaPropertiesGenerator;
            if (biota.BiotaPropertiesIID != null && biota.BiotaPropertiesIID.Count > 0) populatedCollectionFlags |= PopulatedCollectionFlags.BiotaPropertiesIID;
            if (biota.BiotaPropertiesInt != null && biota.BiotaPropertiesInt.Count > 0) populatedCollectionFlags |= PopulatedCollectionFlags.BiotaPropertiesInt;
            if (biota.BiotaPropertiesInt64 != null && biota.BiotaPropertiesInt64.Count > 0) populatedCollectionFlags |= PopulatedCollectionFlags.BiotaPropertiesInt64;
            if (biota.BiotaPropertiesPalette != null && biota.BiotaPropertiesPalette.Count > 0) populatedCollectionFlags |= PopulatedCollectionFlags.BiotaPropertiesPalette;
            if (biota.BiotaPropertiesPosition != null && biota.BiotaPropertiesPosition.Count > 0) populatedCollectionFlags |= PopulatedCollectionFlags.BiotaPropertiesPosition;
            if (biota.BiotaPropertiesSkill != null && biota.BiotaPropertiesSkill.Count > 0) populatedCollectionFlags |= PopulatedCollectionFlags.BiotaPropertiesSkill;
            if (biota.BiotaPropertiesSpellBook != null && biota.BiotaPropertiesSpellBook.Count > 0) populatedCollectionFlags |= PopulatedCollectionFlags.BiotaPropertiesSpellBook;
            if (biota.BiotaPropertiesString != null && biota.BiotaPropertiesString.Count > 0) populatedCollectionFlags |= PopulatedCollectionFlags.BiotaPropertiesString;
            if (biota.BiotaPropertiesTextureMap != null && biota.BiotaPropertiesTextureMap.Count > 0) populatedCollectionFlags |= PopulatedCollectionFlags.BiotaPropertiesTextureMap;
            if (biota.HousePermission != null && biota.HousePermission.Count > 0) populatedCollectionFlags |= PopulatedCollectionFlags.HousePermission;
            if (biota.BiotaPropertiesAllegiance != null && biota.BiotaPropertiesAllegiance.Count > 0) populatedCollectionFlags |= PopulatedCollectionFlags.BiotaPropertiesAllegiance;

            biota.PopulatedCollectionFlags = (uint)populatedCollectionFlags;
        }

        public virtual Biota GetBiota(ShardDbContext context, uint id, bool doNotAddToCache = false)
        {
            return GetBiotaCore(context, id);
        }

        /// <summary>
        /// Non-virtual core of GetBiota, so callers that must not risk re-entering ShardDatabaseWithCaching's
        /// cache-check logic against the wrong context (e.g. batch staging) can call this directly instead of
        /// relying on every call site remembering to `base.`-qualify the virtual GetBiota.
        /// </summary>
        protected Biota GetBiotaCore(ShardDbContext context, uint id)
        {
            var biota = context.Biota
                .FirstOrDefault(r => r.Id == id);

            if (biota == null)
                return null;

            PopulateBiotaCollections(context, new[] { biota });

            return biota;
        }

        /// <summary>
        /// The many-ids counterpart of GetBiotaCore: one chunked query for the parent rows, then ONE
        /// PopulateBiotaCollections pass over the whole set, instead of GetBiotaCore's per-object call with a
        /// single-element array. Returned keyed by id, with ids that have no row simply absent.
        /// TRACKING IS LOAD-BEARING HERE. Like GetBiotaCore, this deliberately leaves the context's default
        /// tracking behavior alone rather than using AsNoTracking: the entities it returns are handed straight to
        /// StageBiota, and BiotaUpdater mutates them and calls context.BiotaPropertiesX.Remove(...) on their
        /// children. Untracked entities would let SaveChanges() commit an incomplete update, or none at all, with
        /// no error anywhere - so any caller that wants a read-only bulk load must not reuse this.
        /// </summary>
        protected Dictionary<uint, Biota> GetBiotasCore(ShardDbContext context, IReadOnlyCollection<uint> ids)
        {
            if (ids.Count == 0)
                return new Dictionary<uint, Biota>();

            var biotas = QueryableExtensions.QueryChunked(ids, chunk => context.Biota.Where(r => chunk.Contains(r.Id)));

            PopulateBiotaCollections(context, biotas);

            return biotas.ToDictionary(b => b.Id);
        }

        /// <summary>
        /// Batch-assembles the ~24 property-table collections for many biotas at once: one query per property
        /// table actually present anywhere in the batch (chunked by id, since MySQL has practical IN-clause
        /// limits), instead of one query per property table PER OBJECT. GetBiotaCore uses this with a
        /// single-element list; the bulk loaders (GetStaticObjectsByLandblock, GetDynamicObjectsByLandblock,
        /// GetBiotasByType, GetBiotasByWcid) use it with however many objects they loaded, so this flag-gating
        /// logic lives in exactly one place rather than duplicated between the single-object and batch paths.
        /// </summary>
        protected void PopulateBiotaCollections(ShardDbContext context, IList<Biota> biotas)
        {
            if (biotas.Count == 0)
                return;

            var ids = biotas.Select(b => b.Id).ToArray();

            PopulatedCollectionFlags aggregateFlags = 0;
            foreach (var b in biotas)
                aggregateFlags |= (PopulatedCollectionFlags)b.PopulatedCollectionFlags;

            if (aggregateFlags.HasFlag(PopulatedCollectionFlags.BiotaPropertiesAnimPart))
            {
                var grouped = QueryableExtensions.QueryChunked(ids, chunk => context.BiotaPropertiesAnimPart.Where(r => chunk.Contains(r.ObjectId))).ToLookup(r => r.ObjectId);
                foreach (var b in biotas)
                    if (((PopulatedCollectionFlags)b.PopulatedCollectionFlags).HasFlag(PopulatedCollectionFlags.BiotaPropertiesAnimPart))
                        b.BiotaPropertiesAnimPart = grouped[b.Id].ToList();
            }

            if (aggregateFlags.HasFlag(PopulatedCollectionFlags.BiotaPropertiesAttribute))
            {
                var grouped = QueryableExtensions.QueryChunked(ids, chunk => context.BiotaPropertiesAttribute.Where(r => chunk.Contains(r.ObjectId))).ToLookup(r => r.ObjectId);
                foreach (var b in biotas)
                    if (((PopulatedCollectionFlags)b.PopulatedCollectionFlags).HasFlag(PopulatedCollectionFlags.BiotaPropertiesAttribute))
                        b.BiotaPropertiesAttribute = grouped[b.Id].ToList();
            }

            if (aggregateFlags.HasFlag(PopulatedCollectionFlags.BiotaPropertiesAttribute2nd))
            {
                var grouped = QueryableExtensions.QueryChunked(ids, chunk => context.BiotaPropertiesAttribute2nd.Where(r => chunk.Contains(r.ObjectId))).ToLookup(r => r.ObjectId);
                foreach (var b in biotas)
                    if (((PopulatedCollectionFlags)b.PopulatedCollectionFlags).HasFlag(PopulatedCollectionFlags.BiotaPropertiesAttribute2nd))
                        b.BiotaPropertiesAttribute2nd = grouped[b.Id].ToList();
            }

            if (aggregateFlags.HasFlag(PopulatedCollectionFlags.BiotaPropertiesBodyPart))
            {
                var grouped = QueryableExtensions.QueryChunked(ids, chunk => context.BiotaPropertiesBodyPart.Where(r => chunk.Contains(r.ObjectId))).ToLookup(r => r.ObjectId);
                foreach (var b in biotas)
                    if (((PopulatedCollectionFlags)b.PopulatedCollectionFlags).HasFlag(PopulatedCollectionFlags.BiotaPropertiesBodyPart))
                        b.BiotaPropertiesBodyPart = grouped[b.Id].ToList();
            }

            if (aggregateFlags.HasFlag(PopulatedCollectionFlags.BiotaPropertiesBook))
            {
                // Singular (PK is ObjectId), not a list.
                var byId = QueryableExtensions.QueryChunked(ids, chunk => context.BiotaPropertiesBook.Where(r => chunk.Contains(r.ObjectId))).ToDictionary(r => r.ObjectId);
                foreach (var b in biotas)
                    if (((PopulatedCollectionFlags)b.PopulatedCollectionFlags).HasFlag(PopulatedCollectionFlags.BiotaPropertiesBook))
                        b.BiotaPropertiesBook = byId.TryGetValue(b.Id, out var book) ? book : null;
            }

            if (aggregateFlags.HasFlag(PopulatedCollectionFlags.BiotaPropertiesBookPageData))
            {
                var grouped = QueryableExtensions.QueryChunked(ids, chunk => context.BiotaPropertiesBookPageData.Where(r => chunk.Contains(r.ObjectId))).ToLookup(r => r.ObjectId);
                foreach (var b in biotas)
                    if (((PopulatedCollectionFlags)b.PopulatedCollectionFlags).HasFlag(PopulatedCollectionFlags.BiotaPropertiesBookPageData))
                        b.BiotaPropertiesBookPageData = grouped[b.Id].ToList();
            }

            if (aggregateFlags.HasFlag(PopulatedCollectionFlags.BiotaPropertiesBool))
            {
                var grouped = QueryableExtensions.QueryChunked(ids, chunk => context.BiotaPropertiesBool.Where(r => chunk.Contains(r.ObjectId))).ToLookup(r => r.ObjectId);
                foreach (var b in biotas)
                    if (((PopulatedCollectionFlags)b.PopulatedCollectionFlags).HasFlag(PopulatedCollectionFlags.BiotaPropertiesBool))
                        b.BiotaPropertiesBool = grouped[b.Id].ToList();
            }

            if (aggregateFlags.HasFlag(PopulatedCollectionFlags.BiotaPropertiesCreateList))
            {
                var grouped = QueryableExtensions.QueryChunked(ids, chunk => context.BiotaPropertiesCreateList.Where(r => chunk.Contains(r.ObjectId))).ToLookup(r => r.ObjectId);
                foreach (var b in biotas)
                    if (((PopulatedCollectionFlags)b.PopulatedCollectionFlags).HasFlag(PopulatedCollectionFlags.BiotaPropertiesCreateList))
                        b.BiotaPropertiesCreateList = grouped[b.Id].ToList();
            }

            if (aggregateFlags.HasFlag(PopulatedCollectionFlags.BiotaPropertiesDID))
            {
                var grouped = QueryableExtensions.QueryChunked(ids, chunk => context.BiotaPropertiesDID.Where(r => chunk.Contains(r.ObjectId))).ToLookup(r => r.ObjectId);
                foreach (var b in biotas)
                    if (((PopulatedCollectionFlags)b.PopulatedCollectionFlags).HasFlag(PopulatedCollectionFlags.BiotaPropertiesDID))
                        b.BiotaPropertiesDID = grouped[b.Id].ToList();
            }

            if (aggregateFlags.HasFlag(PopulatedCollectionFlags.BiotaPropertiesEmote))
            {
                // Nested one-to-many under a one-to-many - preserved exactly as the original single-object query, just scoped to the chunked id set.
                var grouped = QueryableExtensions.QueryChunked(ids, chunk => context.BiotaPropertiesEmote.Include(r => r.BiotaPropertiesEmoteAction).Where(r => chunk.Contains(r.ObjectId))).ToLookup(r => r.ObjectId);
                foreach (var b in biotas)
                    if (((PopulatedCollectionFlags)b.PopulatedCollectionFlags).HasFlag(PopulatedCollectionFlags.BiotaPropertiesEmote))
                        b.BiotaPropertiesEmote = grouped[b.Id].ToList();
            }

            if (aggregateFlags.HasFlag(PopulatedCollectionFlags.BiotaPropertiesEnchantmentRegistry))
            {
                var grouped = QueryableExtensions.QueryChunked(ids, chunk => context.BiotaPropertiesEnchantmentRegistry.Where(r => chunk.Contains(r.ObjectId))).ToLookup(r => r.ObjectId);
                foreach (var b in biotas)
                    if (((PopulatedCollectionFlags)b.PopulatedCollectionFlags).HasFlag(PopulatedCollectionFlags.BiotaPropertiesEnchantmentRegistry))
                        b.BiotaPropertiesEnchantmentRegistry = grouped[b.Id].ToList();
            }

            if (aggregateFlags.HasFlag(PopulatedCollectionFlags.BiotaPropertiesEventFilter))
            {
                var grouped = QueryableExtensions.QueryChunked(ids, chunk => context.BiotaPropertiesEventFilter.Where(r => chunk.Contains(r.ObjectId))).ToLookup(r => r.ObjectId);
                foreach (var b in biotas)
                    if (((PopulatedCollectionFlags)b.PopulatedCollectionFlags).HasFlag(PopulatedCollectionFlags.BiotaPropertiesEventFilter))
                        b.BiotaPropertiesEventFilter = grouped[b.Id].ToList();
            }

            if (aggregateFlags.HasFlag(PopulatedCollectionFlags.BiotaPropertiesFloat))
            {
                var grouped = QueryableExtensions.QueryChunked(ids, chunk => context.BiotaPropertiesFloat.Where(r => chunk.Contains(r.ObjectId))).ToLookup(r => r.ObjectId);
                foreach (var b in biotas)
                    if (((PopulatedCollectionFlags)b.PopulatedCollectionFlags).HasFlag(PopulatedCollectionFlags.BiotaPropertiesFloat))
                        b.BiotaPropertiesFloat = grouped[b.Id].ToList();
            }

            if (aggregateFlags.HasFlag(PopulatedCollectionFlags.BiotaPropertiesGenerator))
            {
                var grouped = QueryableExtensions.QueryChunked(ids, chunk => context.BiotaPropertiesGenerator.Where(r => chunk.Contains(r.ObjectId))).ToLookup(r => r.ObjectId);
                foreach (var b in biotas)
                    if (((PopulatedCollectionFlags)b.PopulatedCollectionFlags).HasFlag(PopulatedCollectionFlags.BiotaPropertiesGenerator))
                        b.BiotaPropertiesGenerator = grouped[b.Id].ToList();
            }

            if (aggregateFlags.HasFlag(PopulatedCollectionFlags.BiotaPropertiesIID))
            {
                var grouped = QueryableExtensions.QueryChunked(ids, chunk => context.BiotaPropertiesIID.Where(r => chunk.Contains(r.ObjectId))).ToLookup(r => r.ObjectId);
                foreach (var b in biotas)
                    if (((PopulatedCollectionFlags)b.PopulatedCollectionFlags).HasFlag(PopulatedCollectionFlags.BiotaPropertiesIID))
                        b.BiotaPropertiesIID = grouped[b.Id].ToList();
            }

            if (aggregateFlags.HasFlag(PopulatedCollectionFlags.BiotaPropertiesInt))
            {
                var grouped = QueryableExtensions.QueryChunked(ids, chunk => context.BiotaPropertiesInt.Where(r => chunk.Contains(r.ObjectId))).ToLookup(r => r.ObjectId);
                foreach (var b in biotas)
                    if (((PopulatedCollectionFlags)b.PopulatedCollectionFlags).HasFlag(PopulatedCollectionFlags.BiotaPropertiesInt))
                        b.BiotaPropertiesInt = grouped[b.Id].ToList();
            }

            if (aggregateFlags.HasFlag(PopulatedCollectionFlags.BiotaPropertiesInt64))
            {
                var grouped = QueryableExtensions.QueryChunked(ids, chunk => context.BiotaPropertiesInt64.Where(r => chunk.Contains(r.ObjectId))).ToLookup(r => r.ObjectId);
                foreach (var b in biotas)
                    if (((PopulatedCollectionFlags)b.PopulatedCollectionFlags).HasFlag(PopulatedCollectionFlags.BiotaPropertiesInt64))
                        b.BiotaPropertiesInt64 = grouped[b.Id].ToList();
            }

            if (aggregateFlags.HasFlag(PopulatedCollectionFlags.BiotaPropertiesPalette))
            {
                var grouped = QueryableExtensions.QueryChunked(ids, chunk => context.BiotaPropertiesPalette.Where(r => chunk.Contains(r.ObjectId))).ToLookup(r => r.ObjectId);
                foreach (var b in biotas)
                    if (((PopulatedCollectionFlags)b.PopulatedCollectionFlags).HasFlag(PopulatedCollectionFlags.BiotaPropertiesPalette))
                        b.BiotaPropertiesPalette = grouped[b.Id].ToList();
            }

            if (aggregateFlags.HasFlag(PopulatedCollectionFlags.BiotaPropertiesPosition))
            {
                var grouped = QueryableExtensions.QueryChunked(ids, chunk => context.BiotaPropertiesPosition.Where(r => chunk.Contains(r.ObjectId))).ToLookup(r => r.ObjectId);
                foreach (var b in biotas)
                    if (((PopulatedCollectionFlags)b.PopulatedCollectionFlags).HasFlag(PopulatedCollectionFlags.BiotaPropertiesPosition))
                        b.BiotaPropertiesPosition = grouped[b.Id].ToList();
            }

            if (aggregateFlags.HasFlag(PopulatedCollectionFlags.BiotaPropertiesSkill))
            {
                var grouped = QueryableExtensions.QueryChunked(ids, chunk => context.BiotaPropertiesSkill.Where(r => chunk.Contains(r.ObjectId))).ToLookup(r => r.ObjectId);
                foreach (var b in biotas)
                    if (((PopulatedCollectionFlags)b.PopulatedCollectionFlags).HasFlag(PopulatedCollectionFlags.BiotaPropertiesSkill))
                        b.BiotaPropertiesSkill = grouped[b.Id].ToList();
            }

            if (aggregateFlags.HasFlag(PopulatedCollectionFlags.BiotaPropertiesSpellBook))
            {
                var grouped = QueryableExtensions.QueryChunked(ids, chunk => context.BiotaPropertiesSpellBook.Where(r => chunk.Contains(r.ObjectId))).ToLookup(r => r.ObjectId);
                foreach (var b in biotas)
                    if (((PopulatedCollectionFlags)b.PopulatedCollectionFlags).HasFlag(PopulatedCollectionFlags.BiotaPropertiesSpellBook))
                        b.BiotaPropertiesSpellBook = grouped[b.Id].ToList();
            }

            if (aggregateFlags.HasFlag(PopulatedCollectionFlags.BiotaPropertiesString))
            {
                var grouped = QueryableExtensions.QueryChunked(ids, chunk => context.BiotaPropertiesString.Where(r => chunk.Contains(r.ObjectId))).ToLookup(r => r.ObjectId);
                foreach (var b in biotas)
                    if (((PopulatedCollectionFlags)b.PopulatedCollectionFlags).HasFlag(PopulatedCollectionFlags.BiotaPropertiesString))
                        b.BiotaPropertiesString = grouped[b.Id].ToList();
            }

            if (aggregateFlags.HasFlag(PopulatedCollectionFlags.BiotaPropertiesTextureMap))
            {
                var grouped = QueryableExtensions.QueryChunked(ids, chunk => context.BiotaPropertiesTextureMap.Where(r => chunk.Contains(r.ObjectId))).ToLookup(r => r.ObjectId);
                foreach (var b in biotas)
                    if (((PopulatedCollectionFlags)b.PopulatedCollectionFlags).HasFlag(PopulatedCollectionFlags.BiotaPropertiesTextureMap))
                        b.BiotaPropertiesTextureMap = grouped[b.Id].ToList();
            }

            if (aggregateFlags.HasFlag(PopulatedCollectionFlags.HousePermission))
            {
                // Keyed by HouseId, not ObjectId.
                var grouped = QueryableExtensions.QueryChunked(ids, chunk => context.HousePermission.Where(r => chunk.Contains(r.HouseId))).ToLookup(r => r.HouseId);
                foreach (var b in biotas)
                    if (((PopulatedCollectionFlags)b.PopulatedCollectionFlags).HasFlag(PopulatedCollectionFlags.HousePermission))
                        b.HousePermission = grouped[b.Id].ToList();
            }

            if (aggregateFlags.HasFlag(PopulatedCollectionFlags.BiotaPropertiesAllegiance))
            {
                // Keyed by AllegianceId, not ObjectId.
                var grouped = QueryableExtensions.QueryChunked(ids, chunk => context.BiotaPropertiesAllegiance.Where(r => chunk.Contains(r.AllegianceId))).ToLookup(r => r.AllegianceId);
                foreach (var b in biotas)
                    if (((PopulatedCollectionFlags)b.PopulatedCollectionFlags).HasFlag(PopulatedCollectionFlags.BiotaPropertiesAllegiance))
                        b.BiotaPropertiesAllegiance = grouped[b.Id].ToList();
            }
        }

        public virtual Biota GetBiota(uint id, bool doNotAddToCache = false)
        {
            using (var context = new ShardDbContext())
                return GetBiota(context, id, doNotAddToCache);
        }

        public List<Biota> GetBiotasByWcid(uint wcid)
        {
            using (var context = new ShardDbContext())
            {
                context.ChangeTracker.QueryTrackingBehavior = QueryTrackingBehavior.NoTracking;

                var biotas = context.Biota.Where(r => r.WeenieClassId == wcid).ToList();

                PopulateBiotaCollections(context, biotas);

                return biotas;
            }
        }

        public List<Biota> GetBiotasByType(WeenieType type)
        {
            using (var context = new ShardDbContext())
            {
                context.ChangeTracker.QueryTrackingBehavior = QueryTrackingBehavior.NoTracking;

                var iType = (int)type;

                var biotas = context.Biota.Where(r => r.WeenieType == iType).ToList();

                PopulateBiotaCollections(context, biotas);

                return biotas;
            }
        }

        /// <summary>
        /// Commits a context with ACE's existing retry-once-then-fail convention. Shared by the single-item
        /// commit path (DoSaveBiota) and the batch commit path (SaveBiotaBatch) so both fail the same way.
        /// </summary>
        protected bool CommitContext(ShardDbContext context, string label)
        {
            Exception firstException = null;
            retry:

            try
            {
                context.SaveChanges();

                if (firstException != null)
                    log.InfoFormat("[DATABASE] {0} retry succeeded after initial exception of: {1}", label, firstException.GetFullMessage());

                return true;
            }
            catch (Exception ex)
            {
                if (firstException == null)
                {
                    firstException = ex;
                    goto retry;
                }

                // Character name might be in use or some other fault
                log.Error($"[DATABASE] {label} failed first attempt with exception: {firstException.GetFullMessage()}");
                log.Error($"[DATABASE] {label} failed second attempt with exception: {ex.GetFullMessage()}");
                return false;
            }
        }

        protected bool DoSaveBiota(ShardDbContext context, Biota biota)
        {
            SetBiotaPopulatedCollections(biota);

            return CommitContext(context, $"DoSaveBiota 0x{biota.Id:X8}:{biota.GetProperty(PropertyString.Name)}");
        }

        /// <summary>
        /// Adds-or-updates a single biota's changes into the given context, without committing. Looks the existing
        /// row up itself, one object at a time - the single-item SaveBiota path. The batch path pre-loads every id
        /// in one pass instead and calls the overload below, so this lookup is the only difference between them.
        /// </summary>
        protected Biota StageBiota(ShardDbContext context, ACE.Entity.Models.Biota biota, ReaderWriterLockSlim rwLock)
        {
            return StageBiota(context, biota, rwLock, GetBiotaCore(context, biota.Id));
        }

        /// <summary>
        /// Adds-or-updates a single biota's changes into the given context against an ALREADY-LOADED existing
        /// entity, null meaning there is no such row yet and this is an insert. This is the shared core of both
        /// staging paths, so the insert-versus-update decision and the lock discipline around it exist once.
        /// existingBiota must have been loaded on THIS context and still be tracked by it (see GetBiotasCore).
        /// </summary>
        protected Biota StageBiota(ShardDbContext context, ACE.Entity.Models.Biota biota, ReaderWriterLockSlim rwLock, Biota existingBiota)
        {
            rwLock.EnterReadLock();
            try
            {
                if (existingBiota == null)
                {
                    existingBiota = ACE.Database.Adapter.BiotaConverter.ConvertFromEntityBiota(biota);

                    context.Biota.Add(existingBiota);
                }
                else
                {
                    ACE.Database.Adapter.BiotaUpdater.UpdateDatabaseBiota(context, biota, existingBiota);
                }
            }
            finally
            {
                rwLock.ExitReadLock();
            }

            return existingBiota;
        }

        public virtual bool SaveBiota(ACE.Entity.Models.Biota biota, ReaderWriterLockSlim rwLock, bool doNotAddToCache = false)
        {
            using (var context = new ShardDbContext())
            {
                var existingBiota = StageBiota(context, biota, rwLock);

                return DoSaveBiota(context, existingBiota);
            }
        }

        /// <summary>
        /// Cap on how many biotas one SaveBiotaBatch may stage into a single context and commit as one
        /// transaction. It bounds two separate things with the same number: how long one batch can monopolize
        /// SerializedShardDatabase's single worker thread, and how large a transaction SaveBiotasInParallel opens
        /// when a caller hands it a very large set (the landblock checkpoint being the worst case).
        /// </summary>
        public const int MaxSaveBiotaBatchSize = 100;

        /// <summary>
        /// Stages many biotas into ONE shared context and commits ONCE, amortizing the per-round-trip cost of
        /// SaveChanges() across the whole batch instead of paying it per item. If the shared commit fails twice
        /// (ACE's existing retry-once convention), falls back to the unchanged per-item SaveBiota path so one
        /// genuinely-bad item in the batch can't spuriously fail the other items.
        /// The existing rows for the whole batch are read in ONE pass up front. Staging item by item through the
        /// lookup-based StageBiota would call GetBiotaCore per item, and GetBiotaCore runs PopulateBiotaCollections
        /// over a single-element array - which is the exact per-object property-table fan-out that
        /// PopulateBiotaCollections exists to avoid, re-introduced once per batch member.
        /// </summary>
        public virtual List<bool> SaveBiotaBatch(IList<(ACE.Entity.Models.Biota biota, ReaderWriterLockSlim rwLock)> items, bool doNotAddToCache = false)
        {
            SaveBatchStats.RecordBatch(items.Count, 0, items.Count);

            using (var context = new ShardDbContext())
            {
                var existingBiotas = GetBiotasCore(context, items.Select(i => i.biota.Id).ToList());

                foreach (var item in items)
                {
                    // Absent from the dictionary means no row exists yet, which is exactly the null StageBiota
                    // reads as "insert" - the same answer GetBiotaCore gave per item, just resolved in bulk.
                    existingBiotas.TryGetValue(item.biota.Id, out var existingBiota);

                    var staged = StageBiota(context, item.biota, item.rwLock, existingBiota);
                    SetBiotaPopulatedCollections(staged);
                }

                if (CommitContext(context, $"SaveBiotaBatch of {items.Count} item(s)"))
                    return items.Select(_ => true).ToList();

                log.Warn($"[DATABASE] SaveBiotaBatch of {items.Count} item(s) failed twice as a batch; falling back to per-item saves.");
            }

            return items.Select(item => SaveBiota(item.biota, item.rwLock, doNotAddToCache)).ToList();
        }

        /// <summary>
        /// Observability hook: how many of these biotas are currently held in the in-memory biota cache.
        /// The base database has no cache and always reports zero; ShardDatabaseWithCaching overrides it.
        /// Used only by SaveBatchStats - it must never influence what gets written.
        /// </summary>
        protected virtual int CountCachedBiotas(IList<(ACE.Entity.Models.Biota biota, ReaderWriterLockSlim rwLock)> items)
        {
            return 0;
        }

        /// <summary>
        /// Name kept for its existing callers, but this no longer fans out over the database thread pool: N
        /// parallel contexts each committing its own transaction is the cost being removed, and the batch path
        /// additionally collapses the per-item existence reads into one query per property table per chunk.
        /// Chunked rather than committed as one transaction because this method bypasses the queue drain's cap
        /// entirely - Landblock's checkpoint save can hand it thousands of biotas, and one unbounded transaction
        /// over all of them would hold locks far too long. Many bounded commits, roughly 100x fewer than before.
        /// </summary>
        public bool SaveBiotasInParallel(IEnumerable<(ACE.Entity.Models.Biota biota, ReaderWriterLockSlim rwLock)> biotas, bool doNotAddToCache = false)
        {
            // Materialized once so the instrumentation below cannot double-enumerate a lazy sequence, and so
            // the count it reports is exactly the set the chunk loop then iterates.
            var items = biotas as IList<(ACE.Entity.Models.Biota biota, ReaderWriterLockSlim rwLock)> ?? biotas.ToList();

            SaveBatchStats.RecordParallelCall(items.Count, CountCachedBiotas(items));

            var result = true;

            foreach (var chunk in items.Chunk(MaxSaveBiotaBatchSize))
            {
                if (!SaveBiotaBatch(chunk, doNotAddToCache).All(r => r))
                    result = false;
            }

            return result;
        }

        /// <summary>
        /// Deletes the given biotas with one set-based DELETE ... WHERE id IN (...) per chunk, all inside ONE
        /// transaction, and applies ACE's existing retry-once-then-fail convention to the whole thing. This is the
        /// delete-side counterpart of CommitContext, which cannot be reused directly because it is SaveChanges-shaped
        /// and a set-based delete stages nothing into the change tracker.
        /// The context is created inside the retry rather than outside it: unlike SaveBiotaBatch, there is no staged
        /// state to preserve across attempts, so a second attempt is better off with a clean context and connection.
        /// </summary>
        private bool ExecuteBiotaDelete(IReadOnlyCollection<uint> ids, string label)
        {
            Exception firstException = null;
            retry:

            try
            {
                using (var context = new ShardDbContext())
                {
                    // ShardDbContext configures EnableRetryOnFailure, and MySqlRetryingExecutionStrategy REFUSES a
                    // user-initiated transaction outright ("does not support user-initiated transactions") unless
                    // the whole transaction is handed to it as one retriable unit. SaveBiotaBatch never hits this
                    // because a bare SaveChanges() is already its own implicit transaction; the moment a batch
                    // needs an explicit one, it has to go through CreateExecutionStrategy.
                    // Re-running the delegate on a strategy retry is safe: deleting an id that is already gone is
                    // a no-op, so every statement in here is idempotent.
                    var strategy = context.Database.CreateExecutionStrategy();

                    strategy.Execute(() =>
                    {
                        // One explicit transaction across every chunk, so a multi-chunk delete is as atomic as the
                        // single SaveChanges() that SaveBiotaBatch commits - never half-applied.
                        using (var transaction = context.Database.BeginTransaction())
                        {
                            QueryableExtensions.ExecuteChunked(ids, chunk => context.Biota.Where(r => chunk.Contains(r.Id)).ExecuteDelete());

                            transaction.Commit();
                        }
                    });
                }

                if (firstException != null)
                    log.InfoFormat("[DATABASE] {0} retry succeeded after initial exception of: {1}", label, firstException.GetFullMessage());

                return true;
            }
            catch (Exception ex)
            {
                if (firstException == null)
                {
                    firstException = ex;
                    goto retry;
                }

                log.Error($"[DATABASE] {label} failed first attempt with exception: {firstException.GetFullMessage()}");
                log.Error($"[DATABASE] {label} failed second attempt with exception: {ex.GetFullMessage()}");
                return false;
            }
        }

        public virtual bool RemoveBiota(uint id)
        {
            // No SELECT first. The old load-then-Remove pair existed only to hand EF an entity to mark deleted;
            // the loaded row was never read for anything else, and a missing row returned true then and still does.
            // All 25 child tables have ON DELETE CASCADE foreign keys to biota(id), so InnoDB removes their rows
            // either way - EF only ever issued the parent DELETE here too.
            return ExecuteBiotaDelete(new[] { id }, $"RemoveBiota 0x{id:X8}");
        }

        /// <summary>
        /// Deletes many biotas as ONE transaction of set-based DELETEs, instead of a SELECT + DELETE + commit per id.
        /// If that fails twice (ACE's existing retry-once convention), falls back to the unchanged per-item
        /// RemoveBiota path so one genuinely-bad id can't fail the other ids in the batch.
        /// Every id in a batch that commits reports true, regardless of how many rows were actually affected: a
        /// biota that was already gone is a successful removal on the single-item path, and the batch path must not
        /// disagree with it. Affected-row counts cannot be attributed back to individual ids anyway.
        /// </summary>
        public virtual List<bool> RemoveBiotaBatch(IList<uint> ids)
        {
            // Same-id duplicates are deliberately NOT filtered out the way SaveBiotaBatch's caller filters them.
            // Staging one entity into a context twice throws; naming one id twice in a DELETE ... IN (...) is
            // harmless and idempotent.
            var idList = ids as IReadOnlyCollection<uint> ?? ids.ToList();

            if (ExecuteBiotaDelete(idList, $"RemoveBiotaBatch of {ids.Count} id(s)"))
                return ids.Select(_ => true).ToList();

            log.Warn($"[DATABASE] RemoveBiotaBatch of {ids.Count} id(s) failed twice as a batch; falling back to per-item removes.");

            return ids.Select(id => RemoveBiota(id)).ToList();
        }

        /// <summary>
        /// Name kept for its existing callers, but this no longer fans out over the database thread pool: a set of
        /// deletes is cheaper as one batched transaction than as N parallel connections each doing its own commit,
        /// and RemoveBiotaBatch is virtual so ShardDatabaseWithCaching still gets its cache eviction.
        /// </summary>
        public bool RemoveBiotasInParallel(IEnumerable<uint> ids)
        {
            var idList = ids as IList<uint> ?? ids.ToList();

            if (idList.Count == 0)
                return true;

            return RemoveBiotaBatch(idList).All(r => r);
        }

        /// <summary>
        /// Permanently deletes a character row. Nine child tables cascade from character(id) through
        /// real ON DELETE CASCADE foreign keys, so InnoDB removes them and EF issues only the parent
        /// delete: the eight character_properties_* tables (contract_registry, fill_comp_book,
        /// friend_list, quest_registry, shortcut_bar, spell_bar, squelch, title_book), plus
        /// biota_properties_allegiance via FK_allegiance_character_Id - whose name suggests it belongs
        /// to the biota family when its cascading parent is actually character.
        ///
        /// This is NOT the normal deletion path. In-game character deletion is a soft delete that
        /// sets is_Deleted and delete_Time and leaves the row in place. This method is for callers
        /// that own the guid outright and are reusing it, such as tester roster seeding.
        ///
        /// Note that character has no foreign key to biota, so this does not remove the player's
        /// biota. Callers wanting both must also call RemoveBiota.
        /// </summary>
        public virtual bool RemoveCharacter(uint id)
        {
            using (var context = new ShardDbContext())
            {
                try
                {
                    var strategy = context.Database.CreateExecutionStrategy();

                    strategy.Execute(() =>
                    {
                        using (var transaction = context.Database.BeginTransaction())
                        {
                            context.Character.Where(r => r.Id == id).ExecuteDelete();
                            transaction.Commit();
                        }
                    });

                    return true;
                }
                catch (Exception ex)
                {
                    log.Error($"[DATABASE] RemoveCharacter 0x{id:X8} failed: {ex.GetFullMessage()}");
                    return false;
                }
            }
        }


        public PossessedBiotas GetPossessedBiotasInParallel(uint id)
        {
            var inventory = GetInventoryInParallel(id, true);

            var wieldedItems = GetWieldedItemsInParallel(id);

            return new PossessedBiotas(inventory, wieldedItems);
        }

        /// <summary>
        /// Bulk-loads a possession set - the objects linked to parentId by a single PropertyInstanceId - the same
        /// way the landblock bulk loaders do: one NoTracking context, one query for the child ids, one chunked
        /// query for the biota rows, and one PopulateBiotaCollections pass that fetches each property table once
        /// for the whole set.
        ///
        /// This replaced a Parallel.ForEach over the virtual GetBiota(id), which opened its own context per item
        /// and issued 1 + K queries for it (K = that object's populated property-table count). Measured on the
        /// loadtest shard with a 303-object possession chain, that shape issued 2,727 SELECTs and took ~407ms;
        /// this shape issues a fixed handful regardless of item count.
        ///
        /// Nesting is deliberately breadth-first and exactly one level deep, matching the behavior of the code it
        /// replaces: direct children are loaded, then in ONE further id query the children of whichever of those
        /// are WeenieType.Container. Containers nested inside those packs are not descended, as before.
        ///
        /// Note this does NOT populate ShardDatabaseWithCaching's in-memory biota cache, matching the four other
        /// bulk loaders. Anything loaded here is NoTracking, and an untracked entity in that cache would make
        /// BiotaUpdater.UpdateDatabaseBiota commit a silent partial write on the next save of that object.
        /// </summary>
        private List<Biota> GetPossessionsCore(ShardDbContext context, uint parentId, PropertyInstanceId linkType, bool includedNestedItems)
        {
            var biotas = LoadBiotaRows(context, GetLinkedObjectIds(context, new[] { parentId }, linkType));

            if (includedNestedItems)
            {
                var containerIds = biotas
                    .Where(b => b.WeenieType == (int)WeenieType.Container)
                    .Select(b => b.Id)
                    .ToArray();

                if (containerIds.Length > 0)
                    biotas.AddRange(LoadBiotaRows(context, GetLinkedObjectIds(context, containerIds, PropertyInstanceId.Container)));
            }

            PopulateBiotaCollections(context, biotas);

            return biotas;
        }

        /// <summary>
        /// The ids of every object whose given PropertyInstanceId points at one of parentIds. One query per chunk
        /// of parents, rather than one per parent.
        /// </summary>
        private static uint[] GetLinkedObjectIds(ShardDbContext context, IReadOnlyCollection<uint> parentIds, PropertyInstanceId linkType)
        {
            var type = (ushort)linkType;

            return QueryableExtensions
                .QueryChunked(parentIds, chunk => context.BiotaPropertiesIID.Where(r => r.Type == type && chunk.Contains(r.Value)).Select(r => r.ObjectId))
                .Distinct()
                .ToArray();
        }

        /// <summary>
        /// The biota rows for the given ids, without their property collections - callers finish the job with a
        /// single PopulateBiotaCollections pass over everything they gathered.
        /// </summary>
        private static List<Biota> LoadBiotaRows(ShardDbContext context, IReadOnlyCollection<uint> ids)
        {
            if (ids.Count == 0)
                return new List<Biota>();

            return QueryableExtensions.QueryChunked(ids, chunk => context.Biota.Where(b => chunk.Contains(b.Id)));
        }

        /// <summary>
        /// Kept the historical "InParallel" name because it is part of this class's public surface and is called
        /// from ACE.Server; the work is no longer parallel, it is batched, which is strictly cheaper here.
        /// </summary>
        public List<Biota> GetInventoryInParallel(uint parentId, bool includedNestedItems)
        {
            using (var context = new ShardDbContext())
            {
                context.ChangeTracker.QueryTrackingBehavior = QueryTrackingBehavior.NoTracking;

                return GetPossessionsCore(context, parentId, PropertyInstanceId.Container, includedNestedItems);
            }
        }

        /// <summary>
        /// See GetInventoryInParallel for why this keeps its name. Wielded items are never nested.
        /// </summary>
        public List<Biota> GetWieldedItemsInParallel(uint parentId)
        {
            using (var context = new ShardDbContext())
            {
                context.ChangeTracker.QueryTrackingBehavior = QueryTrackingBehavior.NoTracking;

                return GetPossessionsCore(context, parentId, PropertyInstanceId.Wielder, includedNestedItems: false);
            }
        }

        public List<Biota> GetStaticObjectsByLandblock(ushort landblockId)
        {
            var staticLandblockId = (uint)(0x70000 | landblockId);

            var min = staticLandblockId << 12;
            var max = min | 0xFFF;

            using (var context = new ShardDbContext())
            {
                context.ChangeTracker.QueryTrackingBehavior = QueryTrackingBehavior.NoTracking;

                var staticObjects = context.Biota.Where(b => b.Id >= min && b.Id <= max).ToList();

                PopulateBiotaCollections(context, staticObjects);

                return staticObjects;
            }
        }

        public List<Biota> GetDynamicObjectsByLandblock(ushort landblockId, uint instance)
        {
            var min = (uint)(landblockId << 16);
            var max = min | 0xFFFF;

            using (var context = new ShardDbContext())
            {
                context.ChangeTracker.QueryTrackingBehavior = QueryTrackingBehavior.NoTracking;

                var positionResults = context.BiotaPropertiesPosition
                    .Where(p => p.PositionType == 1 && p.ObjCellId >= min && p.ObjCellId <= max && p.ObjectId >= 0x80000000 && (p.Instance ?? 0) == instance)
                    .ToList();

                var ids = positionResults.Select(p => p.ObjectId).Distinct().ToArray();

                var biotas = QueryableExtensions.QueryChunked(ids, chunk => context.Biota.Where(b => chunk.Contains(b.Id))).ToList();

                PopulateBiotaCollections(context, biotas);

                var dynamics = new List<Biota>();

                foreach (var biota in biotas)
                {
                    // Filter out objects that are in a container
                    if (biota.BiotaPropertiesIID.FirstOrDefault(r => r.Type == 2 && r.Value != 0) != null)
                        continue;

                    // Filter out wielded objects
                    if (biota.BiotaPropertiesIID.FirstOrDefault(r => r.Type == 3 && r.Value != 0) != null)
                        continue;

                    dynamics.Add(biota);
                }

                return dynamics;
            }
        }

        public List<Biota> GetHousesOwned()
        {
            using (var context = new ShardDbContext())
            {
                context.ChangeTracker.QueryTrackingBehavior = QueryTrackingBehavior.NoTracking;

                var query = from biota in context.Biota
                            join iid in context.BiotaPropertiesIID on biota.Id equals iid.ObjectId
                            where biota.WeenieType == (int)WeenieType.SlumLord && iid.Type == (ushort)PropertyInstanceId.HouseOwner
                            select biota;

                var results = query.ToList();

                return results;
            }
        }


        public bool IsCharacterNameAvailable(string name)
        {
            using (var context = new ShardDbContext())
            {
                var result = context.Character
                    .AsNoTracking()
                    .Where(r => !r.IsDeleted)
                    .Where(r => !(r.DeleteTime > 0))
                    .FirstOrDefault(r => r.Name == name);

                return result == null;
            }
        }

        private static readonly ConditionalWeakTable<Character, ShardDbContext> CharacterContexts = new ConditionalWeakTable<Character, ShardDbContext>();

        public List<Character> GetCharacters(uint accountId, bool includeDeleted)
        {
            return GetCharacterList(accountId, includeDeleted);
        }

        public Character GetCharacter(uint characterId)
        {
            return GetCharacterList(0, true, characterId).FirstOrDefault();
        }

        private static List<Character> GetCharacterList(uint accountID, bool includeDeleted, uint characterID = 0)
        {
            var context = new ShardDbContext();

            IQueryable<Character> query;

            if (accountID > 0)
                query = context.Character.Where(r => r.AccountId == accountID && (includeDeleted || !r.IsDeleted));
            else
                query = context.Character.Where(r => r.Id == characterID && (includeDeleted || !r.IsDeleted));

            var results = query.ToList();

            // Resolve the CharacterContexts cache once per character, up front. The eight Include(...).Load()
            // calls below are bound to `query` - which is the ACCOUNT-WIDE predicate, never re-scoped to an
            // individual character - so they load the same rows for every character on the account no matter
            // where they run. Running them inside the per-character loop therefore re-issued all eight once per
            // uncached character: 1 + 8M statements for an M-character account, where 1 + 8 does the same work.
            var existingChars = new Character[results.Count];
            var needsPropertyLoad = false;

            for (int i = 0; i < results.Count; i++)
            {
                var id = results[i].Id;

                existingChars[i] = CharacterContexts.FirstOrDefault(r => r.Key.Id == id).Key;

                if (existingChars[i] == null)
                    needsPropertyLoad = true;
            }

            // Runs if ANY character on the account is uncached, not just the first one - each Load() covers the
            // whole result set, so one pass serves however many of them were missing.
            if (needsPropertyLoad)
            {
                query.Include(r => r.CharacterPropertiesContractRegistry).Load();
                query.Include(r => r.CharacterPropertiesFillCompBook).Load();
                query.Include(r => r.CharacterPropertiesFriendList).Load();
                query.Include(r => r.CharacterPropertiesQuestRegistry).Load();
                query.Include(r => r.CharacterPropertiesShortcutBar).Load();
                query.Include(r => r.CharacterPropertiesSpellBar).Load();
                query.Include(r => r.CharacterPropertiesSquelch).Load();
                query.Include(r => r.CharacterPropertiesTitleBook).Load();
            }

            for (int i = 0; i < results.Count; i++)
            {
                // Do we have a reference to this Character already?
                if (existingChars[i] != null)
                    results[i] = existingChars[i];
                else
                    CharacterContexts.Add(results[i], context);
            }

            return results;
        }

        public Character GetCharacterStubByName(string name) // When searching by name, only non-deleted characters matter
        {
            var context = new ShardDbContext();

            var result = context.Character
                .FirstOrDefault(r => r.Name == name && !r.IsDeleted);

            return result;
        }

        /// <summary>
        /// One character row, or null. Fix round 2, F5: the context is DISPOSED, unlike
        /// <see cref="GetCharacterStubByName"/> and <see cref="GetCharacter"/> above it, which
        /// deliberately keep theirs alive - GetCharacter registers its context in
        /// <see cref="CharacterContexts"/> so SaveCharacter can reuse it, and this method does not.
        /// Leaking one connection per call mattered once /mule &lt;name&gt; turned out to reach here for
        /// every valid name typed, with no throttle in front of it.
        ///
        /// Disposing is safe because every caller reads SCALAR columns off the returned entity and
        /// none of them saves it or walks a navigation property: OfflinePlayer's constructor takes
        /// AccountId, and its IsDeleted / IsPendingDeletion take IsDeleted and DeleteTime.
        /// AccountVaultManager.TryResolveCharacter takes AccountId. Those are all materialized by the
        /// query itself, so nothing needs the context afterwards.
        /// </summary>
        public Character GetCharacterStubByGuid(uint guid)
        {
            using (var context = new ShardDbContext())
            {
                return context.Character
                    .FirstOrDefault(r => r.Id == guid);
            }
        }

        public bool SaveCharacter(Character character, ReaderWriterLockSlim rwLock)
        {
            if (CharacterContexts.TryGetValue(character, out var cachedContext))
            {
                rwLock.EnterReadLock();
                try
                {
                    Exception firstException = null;
                    retry:

                    try
                    {
                        cachedContext.SaveChanges();

                        if (firstException != null)
                            log.InfoFormat("[DATABASE] SaveCharacter-1 0x{0:X8}:{1} retry succeeded after initial exception of: {2}", character.Id, character.Name, firstException.GetFullMessage());

                        return true;
                    }
                    catch (Exception ex)
                    {
                        if (firstException == null)
                        {
                            firstException = ex;
                            goto retry;
                        }

                        // Character name might be in use or some other fault
                        log.Error($"[DATABASE] SaveCharacter-1 0x{character.Id:X8}:{character.Name} failed first attempt with exception: {firstException.GetFullMessage()}");
                        log.Error($"[DATABASE] SaveCharacter-1 0x{character.Id:X8}:{character.Name} failed second attempt with exception: {ex.GetFullMessage()}");
                        return false;
                    }
                }
                finally
                {
                    rwLock.ExitReadLock();
                }
            }

            var context = new ShardDbContext();

            CharacterContexts.Add(character, context);

            rwLock.EnterReadLock();
            try
            {
                context.Character.Add(character);

                Exception firstException = null;
                retry:

                try
                {
                    context.SaveChanges();

                    if (firstException != null)
                        log.InfoFormat("[DATABASE] SaveCharacter-2 0x{0:X8}:{1} retry succeeded after initial exception of: {2}", character.Id, character.Name, firstException.GetFullMessage());

                    return true;
                }
                catch (Exception ex)
                {
                    if (firstException == null)
                    {
                        firstException = ex;
                        goto retry;
                    }

                    // Character name might be in use or some other fault
                    log.Error($"[DATABASE] SaveCharacter-2 0x{character.Id:X8}:{character.Name} failed first attempt with exception: {firstException.GetFullMessage()}");
                    log.Error($"[DATABASE] SaveCharacter-2 0x{character.Id:X8}:{character.Name} failed second attempt with exception: {ex.GetFullMessage()}");
                    return false;
                }
            }
            finally
            {
                rwLock.ExitReadLock();
            }
        }


        public bool AddCharacterInParallel(ACE.Entity.Models.Biota biota, ReaderWriterLockSlim biotaLock, IEnumerable<(ACE.Entity.Models.Biota biota, ReaderWriterLockSlim rwLock)> possessions, Character character, ReaderWriterLockSlim characterLock)
        {
            if (!SaveBiota(biota, biotaLock))
                return false; // Biota save failed which mean Character fails.

            if (!SaveBiotasInParallel(possessions))
                return false;

            if (!SaveCharacter(character, characterLock))
                return false;

            return true;
        }


        /// <summary>
        /// This will get all player biotas that are backed by characters that are not deleted.
        /// </summary>
        public List<ACE.Entity.Models.Biota> GetAllPlayerBiotasInParallel()
        {
            var biotas = new ConcurrentBag<ACE.Entity.Models.Biota>();

            using (var context = new ShardDbContext())
            {
                var results = context.Character
                    .Where(r => !r.IsDeleted)
                    .AsNoTracking()
                    .ToList();

                Parallel.ForEach(results, result =>
                {
                    var biota = GetBiota(result.Id, true);

                    if (biota != null)
                    {
                        var convertedBiota = ACE.Database.Adapter.BiotaConverter.ConvertToEntityBiota(biota);

                        biotas.Add(convertedBiota);
                    }
                    else
                        log.Error($"ShardDatabase.GetAllPlayerBiotasInParallel() - couldn't find biota for character 0x{result.Id:X8}");
                });
            }

            return biotas.ToList();
        }

        public uint? GetAllegianceID(uint monarchID)
        {
            using (var context = new ShardDbContext())
            {
                var query = from biota in context.Biota
                            join iid in context.BiotaPropertiesIID on biota.Id equals iid.ObjectId
                            where biota.WeenieType == (int)WeenieType.Allegiance && iid.Type == (int)PropertyInstanceId.Monarch && iid.Value == monarchID
                            select biota.Id;

                return query.FirstOrDefault();
            }
        }

        public bool RenameCharacter(Character character, string newName, ReaderWriterLockSlim rwLock)
        {
            if (CharacterContexts.TryGetValue(character, out var cachedContext))
            {
                rwLock.EnterReadLock();
                try
                {
                    Exception firstException = null;
                retry:

                    try
                    {
                        character.Name = newName;
                        cachedContext.SaveChanges();

                        if (firstException != null)
                            log.InfoFormat("[DATABASE] RenameCharacter 0x{0:X8}:{1} retry succeeded after initial exception of: {2}", character.Id, character.Name, firstException.GetFullMessage());

                        return true;
                    }
                    catch (Exception ex)
                    {
                        if (firstException == null)
                        {
                            firstException = ex;
                            goto retry;
                        }

                        // Character name might be in use or some other fault
                        log.Error($"[DATABASE] RenameCharacter 0x{character.Id:X8}:{character.Name} failed first attempt with exception: {firstException.GetFullMessage()}");
                        log.Error($"[DATABASE] RenameCharacter 0x{character.Id:X8}:{character.Name} failed second attempt with exception: {ex.GetFullMessage()}");
                        return false;
                    }
                }
                finally
                {
                    rwLock.ExitReadLock();
                }
            }

            character.Name = newName;

            var context = new ShardDbContext();

            CharacterContexts.Add(character, context);

            rwLock.EnterReadLock();
            try
            {
                context.Character.Add(character);

                Exception firstException = null;
            retry:

                try
                {
                    context.SaveChanges();

                    if (firstException != null)
                        log.InfoFormat("[DATABASE] RenameCharacter 0x{0:X8}:{1} retry succeeded after initial exception of: {2}", character.Id, character.Name, firstException.GetFullMessage());

                    return true;
                }
                catch (Exception ex)
                {
                    if (firstException == null)
                    {
                        firstException = ex;
                        goto retry;
                    }

                    // Character name might be in use or some other fault
                    log.Error($"[DATABASE] RenameCharacter 0x{character.Id:X8}:{character.Name} failed first attempt with exception: {firstException.GetFullMessage()}");
                    log.Error($"[DATABASE] RenameCharacter 0x{character.Id:X8}:{character.Name} failed second attempt with exception: {ex.GetFullMessage()}");
                    return false;
                }
            }
            finally
            {
                rwLock.ExitReadLock();
            }
        }

        // ---------------------------------------------------------------------------------------
        // Proving Grounds: Speed - `character_speed_run`
        //
        // Plain (non-virtual) methods, and deliberately NOT mirrored on ShardDatabaseWithCaching:
        // this table has nothing to do with the biota cache.
        // ---------------------------------------------------------------------------------------

        /// <summary>
        /// Appends one completed run to `character_speed_run`, the RECORD OF TRUTH for the speed
        /// board (DESIGN section 3.5). Append-only: rows are never updated and never deleted by
        /// gameplay.
        /// <para/>
        /// No explicit transaction. A bare SaveChanges() is its own implicit transaction and is
        /// therefore unaffected by the EnableRetryOnFailure resiliency wrapper ShardDbContext turns
        /// on in OnConfiguring; a BeginTransaction() here would throw outright, because
        /// MySqlRetryingExecutionStrategy refuses a user-initiated transaction.
        /// </summary>
        /// <returns>
        /// false when the insert failed. The caller logs that loudly: the in-memory board cache is
        /// updated synchronously on the world thread ahead of this write, so a failure here leaves
        /// the cache ahead of the table until the next boot rebuilds it from the table.
        /// </returns>
        public bool AddSpeedRun(CharacterSpeedRun row)
        {
            if (row == null)
            {
                log.Error("[DATABASE] AddSpeedRun called with a null row.");
                return false;
            }

            try
            {
                using (var context = new ShardDbContext())
                {
                    context.CharacterSpeedRun.Add(row);
                    context.SaveChanges();
                }

                return true;
            }
            catch (Exception ex)
            {
                log.Error($"[DATABASE] AddSpeedRun failed for character 0x{row.CharacterId:X8}:{row.CharacterName}, season {row.SeasonId}, centiseconds {row.Centiseconds}, with exception: {ex.GetFullMessage()}");
                return false;
            }
        }

        /// <summary>
        /// Every recorded speed run, every season. Read once at boot by SpeedBoardManager to build
        /// the in-memory board cache; never on a hot path.
        /// </summary>
        public List<CharacterSpeedRun> GetAllSpeedRuns()
        {
            using (var context = new ShardDbContext())
            {
                context.ChangeTracker.QueryTrackingBehavior = QueryTrackingBehavior.NoTracking;

                return context.CharacterSpeedRun.ToList();
            }
        }

        /// <summary>
        /// Every stored facet row for one character. A slot with no row has never been visited; the
        /// fresh build is generated on first switch, so a missing row is meaningful and must not be
        /// backfilled here.
        /// </summary>
        public List<CharacterFacet> GetCharacterFacets(uint characterId)
        {
            using (var context = new ShardDbContext())
            {
                context.ChangeTracker.QueryTrackingBehavior = QueryTrackingBehavior.NoTracking;

                return context.CharacterFacet.Where(r => r.CharacterId == characterId).ToList();
            }
        }

        /// <summary>
        /// Inserts or replaces one facet row. A bare SaveChanges is its own implicit transaction and
        /// is therefore unaffected by the EnableRetryOnFailure resiliency wrapper ShardDbContext turns
        /// on in OnConfiguring; a BeginTransaction() here would throw outright, because
        /// MySqlRetryingExecutionStrategy refuses a user-initiated transaction.
        /// </summary>
        /// <returns>
        /// false when the write failed. The caller logs that loudly: the live Player has already been
        /// mutated by the time this runs, so a failure leaves memory ahead of the table until the next
        /// successful save.
        /// </returns>
        public bool SaveCharacterFacet(CharacterFacet row)
        {
            if (row == null)
            {
                log.Error("[DATABASE] SaveCharacterFacet called with a null row.");
                return false;
            }

            try
            {
                using (var context = new ShardDbContext())
                {
                    var existing = context.CharacterFacet
                        .FirstOrDefault(r => r.CharacterId == row.CharacterId && r.Slot == row.Slot);

                    if (existing == null)
                    {
                        context.CharacterFacet.Add(row);
                    }
                    else
                    {
                        existing.Name = row.Name;
                        existing.SkillsJson = row.SkillsJson;
                        existing.AbilitiesJson = row.AbilitiesJson;
                        existing.EquipJson = row.EquipJson;

                        // EVERY column has to be copied here, not just the ones that look interesting.
                        // This branch is field-by-field, so a column added to CharacterFacet and omitted
                        // from this list persists on the INSERT path and then silently never updates
                        // again - the row keeps whatever value it was first created with, forever, with
                        // no error anywhere. ACE.Server.Tests has no database and never reaches
                        // SaveChanges, so nothing in the suite can catch that; the only guard is this
                        // comment plus CharacterFacetSchemaTests' source-shape assertion that the
                        // assignment exists at all.
                        existing.AttrsJson = row.AttrsJson;

                        existing.UpdatedAt = row.UpdatedAt;
                    }

                    context.SaveChanges();
                }

                return true;
            }
            catch (Exception ex)
            {
                log.Error($"[DATABASE] SaveCharacterFacet failed for character 0x{row.CharacterId:X8} slot {row.Slot}, with exception: {ex.GetFullMessage()}");
                return false;
            }
        }

        /// <summary>
        /// Every recorded run for one season. Used to rebuild a single season of the board cache
        /// after admin tooling has written the table directly.
        /// </summary>
        public List<CharacterSpeedRun> GetSpeedRunsBySeason(int seasonId)
        {
            using (var context = new ShardDbContext())
            {
                context.ChangeTracker.QueryTrackingBehavior = QueryTrackingBehavior.NoTracking;

                return context.CharacterSpeedRun.Where(r => r.SeasonId == seasonId).ToList();
            }
        }

        /// <summary>
        /// Deletes every recorded run for one season and returns how many rows went. The ONLY delete
        /// path on this table - gameplay never deletes - and it exists solely for the
        /// /resetspeedboard admin command.
        /// <para/>
        /// ExecuteDelete issues a single DELETE statement, which is its own implicit transaction and
        /// is therefore unaffected by the EnableRetryOnFailure resiliency wrapper ShardDbContext turns
        /// on in OnConfiguring. A BeginTransaction() here would throw outright, because
        /// MySqlRetryingExecutionStrategy refuses a user-initiated transaction.
        /// <para/>
        /// The caller must refresh SpeedBoardManager's cache for the season AFTER this returns: the
        /// table is the record of truth and the cache is derived from it (DESIGN section 3.5).
        /// </summary>
        public int DeleteSpeedRunsBySeason(int seasonId)
        {
            using (var context = new ShardDbContext())
            {
                return context.CharacterSpeedRun.Where(r => r.SeasonId == seasonId).ExecuteDelete();
            }
        }
    }
}
