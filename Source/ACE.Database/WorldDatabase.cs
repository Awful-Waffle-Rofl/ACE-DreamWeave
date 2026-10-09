using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage;

using log4net;

using ACE.Database.Entity;
using ACE.Database.Extensions;
using ACE.Database.Models.World;
using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;

namespace ACE.Database
{
    public class WorldDatabase
    {
        private static readonly ILog log = LogManager.GetLogger(System.Reflection.MethodBase.GetCurrentMethod().DeclaringType);

        public bool Exists(bool retryUntilFound)
        {
            var config = Common.ConfigManager.Config.MySql.World;

            for (; ; )
            {
                using (var context = new WorldDbContext())
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


        // =====================================
        // Weenie
        // =====================================

        /// <summary>
        /// The weenie types whose creature tables (attribute, attribute_2nd, body_part, skill) are read. Real data has
        /// non-creature weenies carrying rows in those tables (34 with skill rows, 12 with attribute/attribute_2nd/
        /// body_part rows on a retail ace_world, per the 2026-09-21 review), and the per-id reader has never loaded
        /// them, so every reader must gate on exactly this.
        /// </summary>
        public static bool IsCreatureWeenieType(WeenieType weenieType)
        {
            return weenieType == WeenieType.Creature || weenieType == WeenieType.Cow ||
                   weenieType == WeenieType.Sentinel || weenieType == WeenieType.Admin ||
                   weenieType == WeenieType.Vendor ||
                   weenieType == WeenieType.CombatPet || weenieType == WeenieType.Pet;
        }

        /// <summary>
        /// The weenie types whose book tables (book, book_page_data) are read. Same gating rule as
        /// <see cref="IsCreatureWeenieType"/>: non-book weenies with book rows exist and are not loaded.
        /// </summary>
        public static bool IsBookWeenieType(WeenieType weenieType)
        {
            return weenieType == WeenieType.Book;
        }

        /// <summary>
        /// This will populate all sub collections except the following: LandblockInstances, PointsOfInterest
        /// <para />
        /// With weenie_bulk_load on (the default) this is <see cref="GetWeeniesCore"/> with a single-element list, so
        /// the per-id and bulk paths share one reader. With it off, it is <see cref="GetWeenieLegacy"/>, unchanged.
        /// </summary>
        public virtual Weenie GetWeenie(WorldDbContext context, uint weenieClassId)
        {
            if (!WeenieBulkLoadSettings.Enabled)
                return GetWeenieLegacy(context, weenieClassId);

            return GetWeeniesCore(context, new[] { weenieClassId }).TryGetValue(weenieClassId, out var weenie) ? weenie : null;
        }

        /// <summary>
        /// The original per-id reader, kept as it was before the bulk loader (its creature and book conditions now call
        /// the shared <see cref="IsCreatureWeenieType"/>/<see cref="IsBookWeenieType"/>, same logic): one query per table, with
        /// the tracking behaviour of whatever context it is handed. weenie_bulk_load=false routes every per-id read
        /// here, and it is the REFERENCE the fidelity test and the bulk self-check compare against.
        /// NON-VIRTUAL on purpose: WorldDatabaseWithEntityCache overrides GetWeenie(context, id) to write its cache,
        /// so a reference read must never go through the virtual (the GetBiotaCore trap, CLAUDE.md).
        /// </summary>
        public Weenie GetWeenieLegacy(WorldDbContext context, uint weenieClassId)
        {
            var weenie = context.Weenie.FirstOrDefault(r => r.ClassId == weenieClassId);

            if (weenie == null)
                return null;

            // Base properties for every weenie (ACBaseQualities)
            weenie.WeeniePropertiesBool = context.WeeniePropertiesBool.Where(r => r.ObjectId == weenie.ClassId).ToList();
            weenie.WeeniePropertiesDID = context.WeeniePropertiesDID.Where(r => r.ObjectId == weenie.ClassId).ToList();
            weenie.WeeniePropertiesFloat = context.WeeniePropertiesFloat.Where(r => r.ObjectId == weenie.ClassId).ToList();
            weenie.WeeniePropertiesIID = context.WeeniePropertiesIID.Where(r => r.ObjectId == weenie.ClassId).ToList();
            weenie.WeeniePropertiesInt = context.WeeniePropertiesInt.Where(r => r.ObjectId == weenie.ClassId).ToList();
            weenie.WeeniePropertiesInt64 = context.WeeniePropertiesInt64.Where(r => r.ObjectId == weenie.ClassId).ToList();
            weenie.WeeniePropertiesPosition = context.WeeniePropertiesPosition.Where(r => r.ObjectId == weenie.ClassId).ToList();
            weenie.WeeniePropertiesString = context.WeeniePropertiesString.Where(r => r.ObjectId == weenie.ClassId).ToList();

            var weenieType = (WeenieType)weenie.Type;

            bool isCreature = IsCreatureWeenieType(weenieType);

            //.Include(r => r.LandblockInstances)   // When we grab a weenie, we don't need to also know everywhere it exists in the world
            //.Include(r => r.PointsOfInterest)     // I think these are just foreign keys for the POI table

            weenie.WeeniePropertiesAnimPart = context.WeeniePropertiesAnimPart.Where(r => r.ObjectId == weenie.ClassId).ToList();

            if (isCreature)
            {
                weenie.WeeniePropertiesAttribute = context.WeeniePropertiesAttribute.Where(r => r.ObjectId == weenie.ClassId).ToList();
                weenie.WeeniePropertiesAttribute2nd = context.WeeniePropertiesAttribute2nd.Where(r => r.ObjectId == weenie.ClassId).ToList();

                weenie.WeeniePropertiesBodyPart = context.WeeniePropertiesBodyPart.Where(r => r.ObjectId == weenie.ClassId).ToList();
            }

            if (IsBookWeenieType(weenieType))
            {
                weenie.WeeniePropertiesBook = context.WeeniePropertiesBook.FirstOrDefault(r => r.ObjectId == weenie.ClassId);
                weenie.WeeniePropertiesBookPageData = context.WeeniePropertiesBookPageData.Where(r => r.ObjectId == weenie.ClassId).ToList();
            }

            weenie.WeeniePropertiesCreateList = context.WeeniePropertiesCreateList.Where(r => r.ObjectId == weenie.ClassId).ToList();
            weenie.WeeniePropertiesEmote = context.WeeniePropertiesEmote.Include(r => r.WeeniePropertiesEmoteAction).Where(r => r.ObjectId == weenie.ClassId).ToList();
            weenie.WeeniePropertiesEventFilter = context.WeeniePropertiesEventFilter.Where(r => r.ObjectId == weenie.ClassId).ToList();

            weenie.WeeniePropertiesGenerator = context.WeeniePropertiesGenerator.Where(r => r.ObjectId == weenie.ClassId).ToList();
            weenie.WeeniePropertiesPalette = context.WeeniePropertiesPalette.Where(r => r.ObjectId == weenie.ClassId).ToList();

            if (isCreature)
            {
                weenie.WeeniePropertiesSkill = context.WeeniePropertiesSkill.Where(r => r.ObjectId == weenie.ClassId).ToList();
            }

            weenie.WeeniePropertiesSpellBook = context.WeeniePropertiesSpellBook.Where(r => r.ObjectId == weenie.ClassId).OrderBy(i => i.Id).ToList();

            weenie.WeeniePropertiesTextureMap = context.WeeniePropertiesTextureMap.Where(r => r.ObjectId == weenie.ClassId).ToList();

            return weenie;
        }

        /// <summary>
        /// Default ids per IN-list for <see cref="GetWeeniesCore"/>. 250, not QueryableExtensions' 1000: a 1000-weenie
        /// chunk puts several per-table row lists past the 85 KB large-object-heap threshold (an int table runs ~30
        /// rows per weenie), and LOH allocations are what DATAS collects in the pauses that already show up as tick
        /// spikes (ACE.Server.csproj GarbageCollectionAdaptationMode=1).
        /// </summary>
        public const int DefaultWeenieChunkSize = 250;

        /// <summary>
        /// Loads many weenies with ONE query per table per chunk instead of one query per table per weenie, and
        /// returns exactly what <see cref="GetWeenieLegacy"/> would return for each id, keyed by id (missing ids are
        /// simply absent). Modelled on ShardDatabase.GetBiotasCore/PopulateBiotaCollections.
        /// <para />
        /// Fidelity rules, each enforced by the exhaustive BulkMatchesPerId_AllWeenies test:
        /// - Row order. The legacy reader has no ORDER BY (except spell_book), so its list order is the order MySQL's
        ///   ref access on each table's object_Id index returns: the unique key's columns for most tables, and the
        ///   primary key id for create_list, emote and generator (secondary index entries carry the PK). A multi-id IN
        ///   is free to pick another plan, so every query here states that order explicitly, prefixed by object_Id
        ///   and tie-broken by id.
        /// - Gating. Creature tables are read only for <see cref="IsCreatureWeenieType"/> weenies and book tables only
        ///   for <see cref="IsBookWeenieType"/> weenies - the IN-list itself is restricted, so a non-creature's stray
        ///   skill rows are neither fetched nor assigned, and its collection keeps the entity's empty default, as in
        ///   the legacy reader.
        /// - Every collection the legacy reader assigns is assigned here (EveryWorldCollectionAssigned).
        /// <para />
        /// NoTracking on every query regardless of the context's own setting, and NON-VIRTUAL: this never writes a
        /// cache, so it is safe for reference reads and for a shared bulk context.
        /// </summary>
        public Dictionary<uint, Weenie> GetWeeniesCore(WorldDbContext context, IReadOnlyCollection<uint> ids, int chunkSize = DefaultWeenieChunkSize)
        {
            if (chunkSize < 1)
                throw new ArgumentOutOfRangeException(nameof(chunkSize));

            var distinctIds = ids.Distinct().ToArray();

            if (distinctIds.Length == 0)
                return new Dictionary<uint, Weenie>();

            var weenies = QueryableExtensions.QueryChunked(distinctIds, chunk => context.Weenie.AsNoTracking().Where(r => chunk.Contains(r.ClassId)), chunkSize);

            if (weenies.Count == 0)
                return new Dictionary<uint, Weenie>();

            var allIds = weenies.Select(w => w.ClassId).ToArray();
            var creatureIds = weenies.Where(w => IsCreatureWeenieType((WeenieType)w.Type)).Select(w => w.ClassId).ToArray();
            var bookIds = weenies.Where(w => IsBookWeenieType((WeenieType)w.Type)).Select(w => w.ClassId).ToArray();

            // Base properties for every weenie (ACBaseQualities)
            var bools = LoadGrouped(allIds, chunkSize, chunk => context.WeeniePropertiesBool.AsNoTracking().Where(r => chunk.Contains(r.ObjectId)).OrderBy(r => r.ObjectId).ThenBy(r => r.Type).ThenBy(r => r.Id), r => r.ObjectId);
            var dids = LoadGrouped(allIds, chunkSize, chunk => context.WeeniePropertiesDID.AsNoTracking().Where(r => chunk.Contains(r.ObjectId)).OrderBy(r => r.ObjectId).ThenBy(r => r.Type).ThenBy(r => r.Id), r => r.ObjectId);
            var floats = LoadGrouped(allIds, chunkSize, chunk => context.WeeniePropertiesFloat.AsNoTracking().Where(r => chunk.Contains(r.ObjectId)).OrderBy(r => r.ObjectId).ThenBy(r => r.Type).ThenBy(r => r.Id), r => r.ObjectId);
            var iids = LoadGrouped(allIds, chunkSize, chunk => context.WeeniePropertiesIID.AsNoTracking().Where(r => chunk.Contains(r.ObjectId)).OrderBy(r => r.ObjectId).ThenBy(r => r.Type).ThenBy(r => r.Id), r => r.ObjectId);
            var ints = LoadGrouped(allIds, chunkSize, chunk => IntRowsQuery(context, chunk), r => r.ObjectId);
            var int64s = LoadGrouped(allIds, chunkSize, chunk => context.WeeniePropertiesInt64.AsNoTracking().Where(r => chunk.Contains(r.ObjectId)).OrderBy(r => r.ObjectId).ThenBy(r => r.Type).ThenBy(r => r.Id), r => r.ObjectId);
            var positions = LoadGrouped(allIds, chunkSize, chunk => context.WeeniePropertiesPosition.AsNoTracking().Where(r => chunk.Contains(r.ObjectId)).OrderBy(r => r.ObjectId).ThenBy(r => r.PositionType).ThenBy(r => r.Id), r => r.ObjectId);
            var strings = LoadGrouped(allIds, chunkSize, chunk => context.WeeniePropertiesString.AsNoTracking().Where(r => chunk.Contains(r.ObjectId)).OrderBy(r => r.ObjectId).ThenBy(r => r.Type).ThenBy(r => r.Id), r => r.ObjectId);

            var animParts = LoadGrouped(allIds, chunkSize, chunk => context.WeeniePropertiesAnimPart.AsNoTracking().Where(r => chunk.Contains(r.ObjectId)).OrderBy(r => r.ObjectId).ThenBy(r => r.Index).ThenBy(r => r.Id), r => r.ObjectId);

            ILookup<uint, WeeniePropertiesAttribute> attributes = null;
            ILookup<uint, WeeniePropertiesAttribute2nd> attribute2nds = null;
            ILookup<uint, WeeniePropertiesBodyPart> bodyParts = null;
            ILookup<uint, WeeniePropertiesSkill> skills = null;

            if (creatureIds.Length > 0)
            {
                attributes = LoadGrouped(creatureIds, chunkSize, chunk => context.WeeniePropertiesAttribute.AsNoTracking().Where(r => chunk.Contains(r.ObjectId)).OrderBy(r => r.ObjectId).ThenBy(r => r.Type).ThenBy(r => r.Id), r => r.ObjectId);
                attribute2nds = LoadGrouped(creatureIds, chunkSize, chunk => context.WeeniePropertiesAttribute2nd.AsNoTracking().Where(r => chunk.Contains(r.ObjectId)).OrderBy(r => r.ObjectId).ThenBy(r => r.Type).ThenBy(r => r.Id), r => r.ObjectId);
                bodyParts = LoadGrouped(creatureIds, chunkSize, chunk => context.WeeniePropertiesBodyPart.AsNoTracking().Where(r => chunk.Contains(r.ObjectId)).OrderBy(r => r.ObjectId).ThenBy(r => r.Key).ThenBy(r => r.Id), r => r.ObjectId);
            }

            ILookup<uint, WeeniePropertiesBook> books = null;
            ILookup<uint, WeeniePropertiesBookPageData> bookPages = null;

            if (bookIds.Length > 0)
            {
                books = LoadGrouped(bookIds, chunkSize, chunk => context.WeeniePropertiesBook.AsNoTracking().Where(r => chunk.Contains(r.ObjectId)).OrderBy(r => r.ObjectId).ThenBy(r => r.Id), r => r.ObjectId);
                bookPages = LoadGrouped(bookIds, chunkSize, chunk => context.WeeniePropertiesBookPageData.AsNoTracking().Where(r => chunk.Contains(r.ObjectId)).OrderBy(r => r.ObjectId).ThenBy(r => r.PageId).ThenBy(r => r.Id), r => r.ObjectId);
            }

            var createLists = LoadGrouped(allIds, chunkSize, chunk => context.WeeniePropertiesCreateList.AsNoTracking().Where(r => chunk.Contains(r.ObjectId)).OrderBy(r => r.ObjectId).ThenBy(r => r.Id), r => r.ObjectId);
            var emotes = LoadGrouped(allIds, chunkSize, chunk => EmoteRowsQuery(context, chunk), r => r.ObjectId);
            var eventFilters = LoadGrouped(allIds, chunkSize, chunk => context.WeeniePropertiesEventFilter.AsNoTracking().Where(r => chunk.Contains(r.ObjectId)).OrderBy(r => r.ObjectId).ThenBy(r => r.Event).ThenBy(r => r.Id), r => r.ObjectId);

            var generators = LoadGrouped(allIds, chunkSize, chunk => context.WeeniePropertiesGenerator.AsNoTracking().Where(r => chunk.Contains(r.ObjectId)).OrderBy(r => r.ObjectId).ThenBy(r => r.Id), r => r.ObjectId);
            var palettes = LoadGrouped(allIds, chunkSize, chunk => context.WeeniePropertiesPalette.AsNoTracking().Where(r => chunk.Contains(r.ObjectId)).OrderBy(r => r.ObjectId).ThenBy(r => r.SubPaletteId).ThenBy(r => r.Offset).ThenBy(r => r.Length).ThenBy(r => r.Id), r => r.ObjectId);

            if (creatureIds.Length > 0)
                skills = LoadGrouped(creatureIds, chunkSize, chunk => context.WeeniePropertiesSkill.AsNoTracking().Where(r => chunk.Contains(r.ObjectId)).OrderBy(r => r.ObjectId).ThenBy(r => r.Type).ThenBy(r => r.Id), r => r.ObjectId);

            // The legacy reader's one explicit ORDER BY is spell_book's id.
            var spellBooks = LoadGrouped(allIds, chunkSize, chunk => context.WeeniePropertiesSpellBook.AsNoTracking().Where(r => chunk.Contains(r.ObjectId)).OrderBy(r => r.ObjectId).ThenBy(r => r.Id), r => r.ObjectId);

            var textureMaps = LoadGrouped(allIds, chunkSize, chunk => context.WeeniePropertiesTextureMap.AsNoTracking().Where(r => chunk.Contains(r.ObjectId)).OrderBy(r => r.ObjectId).ThenBy(r => r.Index).ThenBy(r => r.OldId).ThenBy(r => r.Id), r => r.ObjectId);

            var result = new Dictionary<uint, Weenie>(weenies.Count);

            foreach (var weenie in weenies)
            {
                var id = weenie.ClassId;
                var weenieType = (WeenieType)weenie.Type;

                weenie.WeeniePropertiesBool = bools[id].ToList();
                weenie.WeeniePropertiesDID = dids[id].ToList();
                weenie.WeeniePropertiesFloat = floats[id].ToList();
                weenie.WeeniePropertiesIID = iids[id].ToList();
                weenie.WeeniePropertiesInt = ints[id].ToList();
                weenie.WeeniePropertiesInt64 = int64s[id].ToList();
                weenie.WeeniePropertiesPosition = positions[id].ToList();
                weenie.WeeniePropertiesString = strings[id].ToList();

                weenie.WeeniePropertiesAnimPart = animParts[id].ToList();

                if (IsCreatureWeenieType(weenieType))
                {
                    weenie.WeeniePropertiesAttribute = attributes[id].ToList();
                    weenie.WeeniePropertiesAttribute2nd = attribute2nds[id].ToList();

                    weenie.WeeniePropertiesBodyPart = bodyParts[id].ToList();
                }

                if (IsBookWeenieType(weenieType))
                {
                    weenie.WeeniePropertiesBook = books[id].FirstOrDefault();
                    weenie.WeeniePropertiesBookPageData = bookPages[id].ToList();
                }

                weenie.WeeniePropertiesCreateList = createLists[id].ToList();
                weenie.WeeniePropertiesEmote = emotes[id].ToList();
                weenie.WeeniePropertiesEventFilter = eventFilters[id].ToList();

                weenie.WeeniePropertiesGenerator = generators[id].ToList();
                weenie.WeeniePropertiesPalette = palettes[id].ToList();

                if (IsCreatureWeenieType(weenieType))
                    weenie.WeeniePropertiesSkill = skills[id].ToList();

                weenie.WeeniePropertiesSpellBook = spellBooks[id].ToList();

                weenie.WeeniePropertiesTextureMap = textureMaps[id].ToList();

                result[id] = weenie;
            }

            return result;
        }

        /// <summary>
        /// The names of every Weenie navigation <see cref="GetWeeniesCore"/> assigns. EveryWorldCollectionAssigned
        /// holds this equal to the scaffolded model's navigations, so a table added by a future re-scaffold fails a
        /// test instead of silently loading as an empty list.
        /// </summary>
        public static readonly IReadOnlyList<string> CoreAssignedNavigations = new[]
        {
            nameof(Weenie.WeeniePropertiesBool), nameof(Weenie.WeeniePropertiesDID), nameof(Weenie.WeeniePropertiesFloat),
            nameof(Weenie.WeeniePropertiesIID), nameof(Weenie.WeeniePropertiesInt), nameof(Weenie.WeeniePropertiesInt64),
            nameof(Weenie.WeeniePropertiesPosition), nameof(Weenie.WeeniePropertiesString), nameof(Weenie.WeeniePropertiesAnimPart),
            nameof(Weenie.WeeniePropertiesAttribute), nameof(Weenie.WeeniePropertiesAttribute2nd), nameof(Weenie.WeeniePropertiesBodyPart),
            nameof(Weenie.WeeniePropertiesBook), nameof(Weenie.WeeniePropertiesBookPageData), nameof(Weenie.WeeniePropertiesCreateList),
            nameof(Weenie.WeeniePropertiesEmote), nameof(Weenie.WeeniePropertiesEventFilter), nameof(Weenie.WeeniePropertiesGenerator),
            nameof(Weenie.WeeniePropertiesPalette), nameof(Weenie.WeeniePropertiesSkill), nameof(Weenie.WeeniePropertiesSpellBook),
            nameof(Weenie.WeeniePropertiesTextureMap),
        };

        /// <summary>
        /// The int table's chunk query, named so a test can pin the SQL EF/Pomelo generates for an id-list
        /// Contains (literal IN versus a JSON_TABLE parameter). Every table but emote has this shape.
        /// </summary>
        internal static IQueryable<WeeniePropertiesInt> IntRowsQuery(WorldDbContext context, uint[] chunk)
        {
            return context.WeeniePropertiesInt.AsNoTracking().Where(r => chunk.Contains(r.ObjectId)).OrderBy(r => r.ObjectId).ThenBy(r => r.Type).ThenBy(r => r.Id);
        }

        /// <summary>The emote table's chunk query (the one with an Include), named for the same SQL pin.</summary>
        internal static IQueryable<WeeniePropertiesEmote> EmoteRowsQuery(WorldDbContext context, uint[] chunk)
        {
            return context.WeeniePropertiesEmote.AsNoTracking().Include(r => r.WeeniePropertiesEmoteAction).Where(r => chunk.Contains(r.ObjectId)).OrderBy(r => r.ObjectId).ThenBy(r => r.Id);
        }

        private static ILookup<uint, T> LoadGrouped<T>(uint[] ids, int chunkSize, Func<uint[], IQueryable<T>> queryPerChunk, Func<T, uint> objectId)
        {
            return QueryableExtensions.QueryChunked(ids, chunk => queryPerChunk(chunk), chunkSize).ToLookup(objectId);
        }

        /// <summary>
        /// This will populate all sub collections except the following: LandblockInstances, PointsOfInterest
        /// </summary>
        public virtual List<Weenie> GetAllWeenies()
        {
            using (var context = new WorldDbContext())
            {
                context.Weenie.Load();

                // Base properties for every weenie (ACBaseQualities)
                context.WeeniePropertiesBool.Load();
                context.WeeniePropertiesDID.Load();
                context.WeeniePropertiesFloat.Load();
                context.WeeniePropertiesIID.Load();
                context.WeeniePropertiesInt.Load();
                context.WeeniePropertiesInt64.Load();
                context.WeeniePropertiesPosition.Load();
                context.WeeniePropertiesString.Load();

                context.WeeniePropertiesAnimPart.Load();

                //if (isCreature)
                {
                    context.WeeniePropertiesAttribute.Load();
                    context.WeeniePropertiesAttribute2nd.Load();

                    context.WeeniePropertiesBodyPart.Load();
                }

                //if (weenieType == WeenieType.Book)
                {
                    context.WeeniePropertiesBook.Load();
                    context.WeeniePropertiesBookPageData.Load();
                }

                context.WeeniePropertiesCreateList.Load();
                context.WeeniePropertiesEmote.Load();
                context.WeeniePropertiesEmoteAction.Load();
                context.WeeniePropertiesEventFilter.Load();

                context.WeeniePropertiesGenerator.Load();
                context.WeeniePropertiesPalette.Load();

                //if (isCreature)
                {
                    context.WeeniePropertiesSkill.Load();
                }

                context.WeeniePropertiesSpellBook.Load();

                context.WeeniePropertiesTextureMap.Load();

                return context.Weenie.ToList();
            }
        }

        /// <summary>
        /// This will populate all sub collections except the following: LandblockInstances, PointsOfInterest
        /// </summary>
        public Weenie GetWeenie(uint weenieClassId)
        {
            using (var context = new WorldDbContext())
            {
                context.ChangeTracker.QueryTrackingBehavior = QueryTrackingBehavior.NoTracking;

                return GetWeenie(context, weenieClassId);
            }
        }

        /// <summary>
        /// This will populate all sub collections except the following: LandblockInstances, PointsOfInterest
        /// </summary>
        public Weenie GetWeenie(WorldDbContext context, string weenieClassName)
        {
            var result = context.Weenie
                .FirstOrDefault(r => r.ClassName == weenieClassName);

            if (result != null)
                return GetWeenie(context, result.ClassId);

            return null;
        }

        /// <summary>
        /// This will populate all sub collections except the following: LandblockInstances, PointsOfInterest
        /// </summary>
        public Weenie GetWeenie(string weenieClassName)
        {
            using (var context = new WorldDbContext())
            {
                context.ChangeTracker.QueryTrackingBehavior = QueryTrackingBehavior.NoTracking;

                return GetWeenie(context, weenieClassName);
            }
        }


        public Dictionary<uint, string> GetAllWeenieNames(WorldDbContext context)
        {
            return context.Weenie
                .Include(r => r.WeeniePropertiesString)
                .ToDictionary(r => r.ClassId, r => r.WeeniePropertiesString.FirstOrDefault(p => p.Type == (int)PropertyString.Name)?.Value ?? "");
        }

        public Dictionary<uint, string> GetAllWeenieNames()
        {
            using (var context = new WorldDbContext())
            {
                context.ChangeTracker.QueryTrackingBehavior = QueryTrackingBehavior.NoTracking;

                return GetAllWeenieNames(context);
            }
        }

        /// <summary>
        /// One indexed read of weenie_properties_bool; used by World Events to find WorldEventCreature-flagged
        /// weenies without caching every weenie.
        /// </summary>
        public virtual List<uint> GetWeenieClassIdsWithBool(WorldDbContext context, ushort propertyType, bool value)
        {
            return context.WeeniePropertiesBool
                .Where(r => r.Type == propertyType && r.Value == value)
                .Select(r => r.ObjectId)
                .Distinct()
                .OrderBy(id => id)
                .ToList();
        }

        /// <summary>
        /// One indexed read of weenie_properties_bool; used by World Events to find WorldEventCreature-flagged
        /// weenies without caching every weenie.
        /// </summary>
        public List<uint> GetWeenieClassIdsWithBool(ushort propertyType, bool value)
        {
            using (var context = new WorldDbContext())
            {
                context.ChangeTracker.QueryTrackingBehavior = QueryTrackingBehavior.NoTracking;

                return GetWeenieClassIdsWithBool(context, propertyType, value);
            }
        }

        /// <summary>
        /// One read of weenie_properties_create_list: the distinct weenie_Class_Id of every Shop-destination
        /// (DestinationType.Shop) row whose OWNING weenie is WeenieType.Vendor and is not a Mule Vendor
        /// (PropertyBool.PersonalVendor true). That is exactly the set Vendor.LoadInventory creates by wcid the
        /// first time a vendor is opened. Used by the server's boot-time vendor stock warm-up; this method only
        /// COLLECTS ids and caches nothing, so a future bulk-by-ids weenie loader can replace the per-id cache
        /// fill without touching it. Ascending, zeros excluded.
        /// </summary>
        public virtual List<uint> GetVendorShopCreateListWcids(WorldDbContext context)
        {
            return VendorShopCreateListWcidsQuery(context).ToList();
        }

        /// <summary>
        /// The query behind <see cref="GetVendorShopCreateListWcids(WorldDbContext)"/>, unexecuted, so its SQL shape
        /// can be rendered (ToQueryString) and pinned by a test without a database.
        /// </summary>
        public static IQueryable<uint> VendorShopCreateListWcidsQuery(WorldDbContext context)
        {
            var shop = (sbyte)DestinationType.Shop;
            var vendor = (int)WeenieType.Vendor;
            var personalVendor = (ushort)PropertyBool.PersonalVendor;

            return context.WeeniePropertiesCreateList
                .Where(r => r.DestinationType == shop
                            && r.WeenieClassId != 0
                            && r.Object.Type == vendor
                            && !r.Object.WeeniePropertiesBool.Any(b => b.Type == personalVendor && b.Value))
                .Select(r => r.WeenieClassId)
                .Distinct()
                .OrderBy(id => id);
        }

        /// <inheritdoc cref="GetVendorShopCreateListWcids(WorldDbContext)"/>
        public List<uint> GetVendorShopCreateListWcids()
        {
            using (var context = new WorldDbContext())
            {
                context.ChangeTracker.QueryTrackingBehavior = QueryTrackingBehavior.NoTracking;

                return GetVendorShopCreateListWcids(context);
            }
        }

        public Dictionary<uint, string> GetAllWeenieClassNames(WorldDbContext context)
        {
            return context.Weenie
                .ToDictionary(r => r.ClassId, r => r.ClassName);
        }

        public Dictionary<uint, string> GetAllWeenieClassNames()
        {
            using (var context = new WorldDbContext())
            {
                context.ChangeTracker.QueryTrackingBehavior = QueryTrackingBehavior.NoTracking;

                return GetAllWeenieClassNames(context);
            }
        }


        public List<HouseListResults> GetHousesAll()
        {
            using (var context = new WorldDbContext())
            {
                var query = from weenie in context.Weenie
                            join winst in context.LandblockInstance on weenie.ClassId equals winst.WeenieClassId
                            where weenie.Type == (int)WeenieType.SlumLord
                            select new HouseListResults(weenie, winst);

                return query.ToList();
            }
        }



        // =====================================
        // CookBook
        // =====================================

        public virtual CookBook GetCookbook(WorldDbContext context, uint sourceWeenieClassId, uint targetWeenieClassId)
        {
            var result = context.CookBook
                .Include(r => r.Recipe)
                .Include(r => r.Recipe.RecipeMod)
                    .ThenInclude(r => r.RecipeModsBool)
                .Include(r => r.Recipe.RecipeMod)
                    .ThenInclude(r => r.RecipeModsDID)
                .Include(r => r.Recipe.RecipeMod)
                    .ThenInclude(r => r.RecipeModsFloat)
                .Include(r => r.Recipe.RecipeMod)
                    .ThenInclude(r => r.RecipeModsIID)
                .Include(r => r.Recipe.RecipeMod)
                    .ThenInclude(r => r.RecipeModsInt)
                .Include(r => r.Recipe.RecipeMod)
                    .ThenInclude(r => r.RecipeModsString)
                .Include(r => r.Recipe.RecipeRequirementsBool)
                .Include(r => r.Recipe.RecipeRequirementsDID)
                .Include(r => r.Recipe.RecipeRequirementsFloat)
                .Include(r => r.Recipe.RecipeRequirementsIID)
                .Include(r => r.Recipe.RecipeRequirementsInt)
                .Include(r => r.Recipe.RecipeRequirementsString)
                .FirstOrDefault(r => r.SourceWCID == sourceWeenieClassId && r.TargetWCID == targetWeenieClassId);

            return result;
        }

        public virtual List<CookBook> GetAllCookbooks()
        {
            using (var context = new WorldDbContext())
            {
                context.CookBook.Load();

                context.Recipe.Load();

                context.RecipeMod.Load();
                context.RecipeModsBool.Load();
                context.RecipeModsDID.Load();
                context.RecipeModsFloat.Load();
                context.RecipeModsIID.Load();
                context.RecipeModsInt.Load();
                context.RecipeModsString.Load();

                context.RecipeRequirementsBool.Load();
                context.RecipeRequirementsDID.Load();
                context.RecipeRequirementsFloat.Load();
                context.RecipeRequirementsIID.Load();
                context.RecipeRequirementsInt.Load();
                context.RecipeRequirementsString.Load();

                return context.CookBook.ToList();
            }
        }

        public CookBook GetCookbook(uint sourceWeenieClassId, uint targetWeenieClassId)
        {
            using (var context = new WorldDbContext())
            {
                context.ChangeTracker.QueryTrackingBehavior = QueryTrackingBehavior.NoTracking;

                return GetCookbook(context, sourceWeenieClassId, targetWeenieClassId);
            }
        }

        public List<CookBook> GetCookbooksByRecipeId(uint recipeId)
        {
            var results = new List<CookBook>();

            using (var context = new WorldDbContext())
            {
                context.ChangeTracker.QueryTrackingBehavior = QueryTrackingBehavior.NoTracking;

                var baseRecords = context.CookBook.Where(i => i.RecipeId == recipeId).ToList();

                foreach (var baseRecord in baseRecords)
                {
                    var cookbook = GetCookbook(context, baseRecord.SourceWCID, baseRecord.TargetWCID);

                    results.Add(cookbook);
                }
            }

            return results;
        }

        // =====================================
        // Recipe
        // =====================================

        public Recipe GetRecipe(uint recipeId)
        {
            using (var context = new WorldDbContext())
            {
                context.ChangeTracker.QueryTrackingBehavior = QueryTrackingBehavior.NoTracking;

                return GetRecipe(context, recipeId);
            }
        }

        public virtual Recipe GetRecipe(WorldDbContext context, uint recipeId)
        {
            var result = context.Recipe
                .Include(r => r.RecipeMod)
                    .ThenInclude(r => r.RecipeModsBool)
                .Include(r => r.RecipeMod)
                    .ThenInclude(r => r.RecipeModsDID)
                .Include(r => r.RecipeMod)
                    .ThenInclude(r => r.RecipeModsFloat)
                .Include(r => r.RecipeMod)
                    .ThenInclude(r => r.RecipeModsIID)
                .Include(r => r.RecipeMod)
                    .ThenInclude(r => r.RecipeModsInt)
                .Include(r => r.RecipeMod)
                    .ThenInclude(r => r.RecipeModsString)
                .Include(r => r.RecipeRequirementsBool)
                .Include(r => r.RecipeRequirementsDID)
                .Include(r => r.RecipeRequirementsFloat)
                .Include(r => r.RecipeRequirementsIID)
                .Include(r => r.RecipeRequirementsInt)
                .Include(r => r.RecipeRequirementsString)
                .FirstOrDefault(r => r.Id == recipeId);

            return result;
        }


        // =====================================
        // Encounter
        // =====================================


        // =====================================
        // Event
        // =====================================

        /// <summary>
        /// This takes under 1 second to complete.
        /// </summary>
        public virtual List<Event> GetAllEvents(WorldDbContext context)
        {
            return context.Event
                .ToList();
        }

        /// <summary>
        /// This takes under 1 second to complete.
        /// </summary>
        public List<Event> GetAllEvents()
        {
            using (var context = new WorldDbContext())
            {
                context.ChangeTracker.QueryTrackingBehavior = QueryTrackingBehavior.NoTracking;

                return GetAllEvents(context);
            }
        }


        // =====================================
        // HousePortal
        // =====================================


        // =====================================
        // LandblockInstance
        // =====================================

        public LandblockInstance GetLandblockInstanceByGuid(WorldDbContext context, uint guid)
        {
            return context.LandblockInstance
                .Include(r => r.LandblockInstanceLink)
                .FirstOrDefault(r => r.Guid == guid);
        }

        public LandblockInstance GetLandblockInstanceByGuid(uint guid)
        {
            using (var context = new WorldDbContext())
            {
                context.ChangeTracker.QueryTrackingBehavior = QueryTrackingBehavior.NoTracking;

                return GetLandblockInstanceByGuid(context, guid);
            }
        }

        public List<LandblockInstance> GetLandblockInstancesByWcid(WorldDbContext context, uint wcid)
        {
            return context.LandblockInstance
                .Include(r => r.LandblockInstanceLink)
                .Where(i => i.WeenieClassId == wcid)
                .ToList();
        }

        public List<LandblockInstance> GetLandblockInstancesByWcid(uint wcid)
        {
            using (var context = new WorldDbContext())
            {
                context.ChangeTracker.QueryTrackingBehavior = QueryTrackingBehavior.NoTracking;

                return GetLandblockInstancesByWcid(context, wcid);
            }
        }


        // =====================================
        // PointsOfInterest
        // =====================================


        // =====================================
        // Quest
        // =====================================


        // =====================================
        // Spell
        // =====================================

        /// <summary>
        /// Realms Phase 3: per-realm content override rows for a landblock, mapped into
        /// the base LandblockInstance shape. An empty list means the realm has no
        /// override for this landblock and base content should load.
        /// </summary>
        public virtual List<LandblockInstance> GetRealmInstancesByLandblock(ushort landblock, ushort realmId)
        {
            using (var context = new WorldDbContext())
            {
                var rows = context.LandblockInstanceRealm
                    .Include(r => r.LandblockInstanceLinkRealm)
                    .AsNoTracking()
                    .Where(r => r.RealmId == realmId && r.Landblock == landblock)
                    .ToList();

                return Adapter.RealmContentConverter.ConvertToLandblockInstances(rows);
            }
        }

        /// <summary>
        /// Realms Phase 4: every per-(realm, landblock) content rule. The table holds one
        /// row per stripped landblock, so it is loaded whole and cached - see
        /// WorldDatabaseWithEntityCache.CacheAllRealmLandblockRules.
        /// Returns an empty list when nothing is stripped anywhere.
        /// </summary>
        public virtual List<RealmLandblockRule> GetAllRealmLandblockRules()
        {
            using (var context = new WorldDbContext())
            {
                context.ChangeTracker.QueryTrackingBehavior = QueryTrackingBehavior.NoTracking;

                return context.RealmLandblockRule.ToList();
            }
        }

        /// <summary>
        /// ACRealms port Phase 2: the realm registry, loaded once at boot by RealmManager.
        /// Returns an empty list if the realm table has no rows (base world only).
        /// </summary>
        public virtual List<Realm> GetAllRealms()
        {
            using (var context = new WorldDbContext())
            {
                context.ChangeTracker.QueryTrackingBehavior = QueryTrackingBehavior.NoTracking;

                return context.Realm.ToList();
            }
        }

        /// <summary>
        /// Proving Grounds: Speed - the season registry, read at boot by SpeedSeasonManager.
        /// Returns an empty list if the speed_season table has no rows.
        /// </summary>
        public virtual List<SpeedSeason> GetAllSpeedSeasons()
        {
            using (var context = new WorldDbContext())
            {
                context.ChangeTracker.QueryTrackingBehavior = QueryTrackingBehavior.NoTracking;

                return context.SpeedSeason.ToList();
            }
        }

        public Dictionary<uint, string> GetAllSpellNames(WorldDbContext context)
        {
            return context.Spell
                .ToDictionary(r => r.Id, r => r.Name);
        }

        public Dictionary<uint, string> GetAllSpellNames()
        {
            using (var context = new WorldDbContext())
            {
                context.ChangeTracker.QueryTrackingBehavior = QueryTrackingBehavior.NoTracking;

                return GetAllSpellNames(context);
            }
        }


        // =====================================
        // TreasureDeath
        // =====================================

        public Dictionary<uint, TreasureDeath> GetAllTreasureDeath(WorldDbContext context)
        {
            return context.TreasureDeath
                .ToDictionary(r => r.TreasureType, r => r);
        }

        public Dictionary<uint, TreasureDeath> GetAllTreasureDeath()
        {
            using (var context = new WorldDbContext())
            {
                context.ChangeTracker.QueryTrackingBehavior = QueryTrackingBehavior.NoTracking;

                return GetAllTreasureDeath(context);
            }
        }


        // =====================================
        // TreasureMaterial
        // =====================================


        // =====================================
        // TreasureWielded
        // =====================================

        public Dictionary<uint, List<TreasureWielded>> GetAllTreasureWielded(WorldDbContext context)
        {
            var results = context.TreasureWielded;

            var treasure = new Dictionary<uint, List<TreasureWielded>>();

            foreach (var record in results)
            {
                if (!treasure.ContainsKey(record.TreasureType))
                    treasure.Add(record.TreasureType, new List<TreasureWielded>());

                treasure[record.TreasureType].Add(record);
            }

            return treasure;

        }

        public Dictionary<uint, List<TreasureWielded>> GetAllTreasureWielded()
        {
            using (var context = new WorldDbContext())
            {
                context.ChangeTracker.QueryTrackingBehavior = QueryTrackingBehavior.NoTracking;

                return GetAllTreasureWielded(context);
            }
        }


        // =====================================
        // Version
        // =====================================

        /// <summary>
        /// Get the version information stored in database
        /// </summary>
        public ACE.Database.Models.World.Version GetVersion(WorldDbContext context)
        {
            var version = context.Version
                .FirstOrDefault(r => r.Id == 1);

            return version;
        }

        /// <summary>
        /// Get the version information stored in database
        /// </summary>
        public ACE.Database.Models.World.Version GetVersion()
        {
            using (var context = new WorldDbContext())
            {
                context.ChangeTracker.QueryTrackingBehavior = QueryTrackingBehavior.NoTracking;

                return GetVersion(context);
            }
        }

        // =====================================
        // IsWorldDatabaseGuidRangeValid
        // =====================================

        public bool IsWorldDatabaseGuidRangeValid(WorldDbContext context)
        {
            return context.LandblockInstance.FirstOrDefault(i => i.Guid >= 0x80000000) == null;
        }

        public bool IsWorldDatabaseGuidRangeValid()
        {
            using (var context = new WorldDbContext())
            {
                context.ChangeTracker.QueryTrackingBehavior = QueryTrackingBehavior.NoTracking;

                return IsWorldDatabaseGuidRangeValid(context);
            }
        }
    }
}
