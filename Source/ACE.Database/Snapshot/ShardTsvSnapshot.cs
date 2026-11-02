using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;

using ACE.Database.Models.Shard;
using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;

namespace ACE.Database.Snapshot
{
    /// <summary>
    /// One character's persisted state, read back from a directory of TSV files rather than from the shard
    /// database. Every object here is an EF <see cref="ACE.Database.Models.Shard"/> entity - the DATABASE shape,
    /// with navigation collections such as Biota.BiotaPropertiesInt - and NOT the runtime
    /// ACE.Entity.Models.Biota. The two are not interchangeable (see the project CLAUDE.md, "Two different Biota
    /// types"); convert with ACE.Database.Adapter.BiotaConverter.ConvertToEntityBiota where a runtime biota is
    /// wanted, exactly as the live load path does.
    ///
    /// The possession split is the one ShardDatabase.GetPossessedBiotasInParallel makes (ShardDatabase.cs,
    /// GetPossessionsCore): Inventory is every biota whose PropertyInstanceId.Container points at the character,
    /// followed by one further level - the Container-linked children of whichever of those are
    /// WeenieType.Container - and WieldedItems is every biota whose PropertyInstanceId.Wielder points at the
    /// character. Containers nested inside side packs are not descended, matching that loader.
    ///
    /// The objects are mutable and are handed to a Player by reference (the Character in particular is held by
    /// the Player for its lifetime), so hydrate at most one Player per loaded snapshot and reload for another.
    /// </summary>
    public sealed class ShardSnapshot
    {
        public string SourceDirectory { get; init; }

        public Character Character { get; init; }

        /// <summary>The character's own biota (Biota.Id == Character.Id).</summary>
        public Biota CharacterBiota { get; init; }

        /// <summary>Main-pack items, then the contents of side packs, each level ordered by id.</summary>
        public IReadOnlyList<Biota> Inventory { get; init; }

        /// <summary>Wielded items, ordered by id.</summary>
        public IReadOnlyList<Biota> WieldedItems { get; init; }

        /// <summary>Every biota row the directory held, whether or not the possession split reached it.</summary>
        public IReadOnlyDictionary<uint, Biota> Biotas { get; init; }

        /// <summary>Data rows read per table (file name without extension), for reporting.</summary>
        public IReadOnlyDictionary<string, int> RowCounts { get; init; }
    }

    /// <summary>
    /// Reads a directory of headered TSV files - one per shard table, named "&lt;table&gt;.tsv" - into EF shard
    /// entities. Pure file IO: no database connection is opened, read or written.
    ///
    /// FILE FORMAT. Exactly what the mysql command-line client prints in batch mode (-B, without --raw): a header
    /// line of COLUMN names, then one tab-separated line per row, with NULL printed as the bare word NULL and the
    /// characters backslash, tab, newline and NUL escaped as \\, \t, \n and \0. A table with no rows may be an
    /// empty file (mysql prints no header for an empty result). Two column encodings are NOT what a bare SELECT *
    /// gives and must be requested in the export query, because the batch client prints them as raw bytes: a BIT
    /// column must be selected as "col+0 AS col" (it then reads 0 or 1), and a BLOB column as "HEX(col) AS col"
    /// (it is then hex-decoded here; every byte[] property is read as hex).
    ///
    /// One known ambiguity of that format: a string column whose value is literally the four letters NULL reads
    /// back as null. No column this reader is pointed at is expected to carry that value.
    ///
    /// COLUMN MAPPING comes from the ShardDbContext model metadata, not from the C# property names - the
    /// scaffolded column names differ ("object_Id" for ObjectId, "s_a_c" for SAC, "level_From_P_P" for
    /// LevelFromPP), and the model is the one place that pairs them. Strict by design: an unknown table, an
    /// unknown column, a row with the wrong field count or a value that does not parse throws, naming the file
    /// and line, rather than loading a quietly partial character.
    /// </summary>
    public static class ShardTsvSnapshotReader
    {
        public const string NullToken = "NULL";

        private static readonly Lazy<IModel> model = new Lazy<IModel>(BuildModel);

        /// <summary>
        /// The ShardDbContext model, built from an options object that names a server version explicitly so no
        /// connection is ever opened - UseMySql with a ServerVersion does not connect, and OnConfiguring skips its
        /// own Config.js-based setup because the options are already configured.
        /// </summary>
        private static IModel BuildModel()
        {
            var options = new DbContextOptionsBuilder<ShardDbContext>()
                .UseMySql("server=localhost;database=ace_shard", new MySqlServerVersion(new Version(8, 0, 0)))
                .Options;

            using (var context = new ShardDbContext(options))
                return context.Model;
        }

        /// <summary>
        /// Loads the snapshot in <paramref name="directory"/>. When <paramref name="characterId"/> is null the
        /// character file must hold exactly one row, and that is the character; otherwise the named id is used.
        /// </summary>
        public static ShardSnapshot Load(string directory, uint? characterId = null)
        {
            if (string.IsNullOrWhiteSpace(directory))
                throw new ArgumentException("A snapshot directory is required.", nameof(directory));

            if (!Directory.Exists(directory))
                throw new DirectoryNotFoundException($"Snapshot directory not found: {directory}");

            var entityTypesByTable = model.Value.GetEntityTypes()
                .Where(e => e.GetTableName() != null && !e.IsOwned())
                .GroupBy(e => e.GetTableName(), StringComparer.Ordinal)
                .ToDictionary(g => g.Key, g => g.Single(), StringComparer.Ordinal);

            var rowsByType = new Dictionary<IEntityType, List<object>>();
            var rowCounts = new Dictionary<string, int>(StringComparer.Ordinal);

            foreach (var path in Directory.GetFiles(directory, "*.tsv").OrderBy(p => p, StringComparer.Ordinal))
            {
                var table = Path.GetFileNameWithoutExtension(path);

                if (!entityTypesByTable.TryGetValue(table, out var entityType))
                    throw new InvalidDataException($"{path}: no ShardDbContext entity maps to table '{table}'.");

                var rows = ReadTable(path, entityType);

                rowsByType[entityType] = rows;
                rowCounts[table] = rows.Count;
            }

            WireNavigations(rowsByType);

            var characters = RowsOf<Character>(rowsByType);
            var biotas = RowsOf<Biota>(rowsByType).ToDictionary(b => b.Id);

            Character character;

            if (characterId == null)
            {
                if (characters.Count != 1)
                    throw new InvalidDataException($"{directory}: expected exactly one character row when no character id is given, found {characters.Count}.");

                character = characters[0];
            }
            else
            {
                character = characters.SingleOrDefault(c => c.Id == characterId.Value)
                    ?? throw new InvalidDataException($"{directory}: no character row with id 0x{characterId.Value:X8}.");
            }

            if (!biotas.TryGetValue(character.Id, out var characterBiota))
                throw new InvalidDataException($"{directory}: no biota row for character 0x{character.Id:X8}.");

            SplitPossessions(character.Id, biotas, out var inventory, out var wielded);

            return new ShardSnapshot
            {
                SourceDirectory = directory,
                Character = character,
                CharacterBiota = characterBiota,
                Inventory = inventory,
                WieldedItems = wielded,
                Biotas = biotas,
                RowCounts = rowCounts,
            };
        }

        /// <summary>
        /// Mirrors ShardDatabase.GetPossessionsCore's id selection over the in-memory rows: one link level for
        /// Container and Wielder, plus one nested Container level under WeenieType.Container children.
        /// </summary>
        private static void SplitPossessions(uint characterId, IReadOnlyDictionary<uint, Biota> biotas, out List<Biota> inventory, out List<Biota> wielded)
        {
            var containerType = (ushort)PropertyInstanceId.Container;
            var wielderType = (ushort)PropertyInstanceId.Wielder;

            List<Biota> LinkedTo(ICollection<uint> parents, ushort linkType) => biotas.Values
                .Where(b => b.BiotaPropertiesIID.Any(r => r.Type == linkType && parents.Contains(r.Value)))
                .OrderBy(b => b.Id)
                .ToList();

            var topLevel = LinkedTo(new HashSet<uint> { characterId }, containerType);

            var sidePackIds = new HashSet<uint>(topLevel.Where(b => b.WeenieType == (int)WeenieType.Container).Select(b => b.Id));

            var items = new List<Biota>(topLevel);
            var seen = new HashSet<uint>(topLevel.Select(b => b.Id));

            if (sidePackIds.Count > 0)
                items.AddRange(LinkedTo(sidePackIds, containerType).Where(b => seen.Add(b.Id)));

            inventory = items;

            wielded = LinkedTo(new HashSet<uint> { characterId }, wielderType);
        }

        private static List<T> RowsOf<T>(Dictionary<IEntityType, List<object>> rowsByType)
        {
            return rowsByType.Where(kvp => kvp.Key.ClrType == typeof(T)).SelectMany(kvp => kvp.Value).Cast<T>().ToList();
        }

        /// <summary>
        /// Attaches every dependent row to its principal's navigation collection (Biota.BiotaPropertiesInt,
        /// BiotaPropertiesEmote.BiotaPropertiesEmoteAction, Character.CharacterPropertiesQuestRegistry, ...), using
        /// the model's foreign keys. A dependent whose principal is not in the snapshot stays unattached, which is
        /// what a query scoped to these principals would return too.
        /// </summary>
        private static void WireNavigations(Dictionary<IEntityType, List<object>> rowsByType)
        {
            var principalIndex = new Dictionary<IEntityType, Dictionary<object, object>>();

            Dictionary<object, object> IndexOf(IEntityType principalType, IKey key)
            {
                if (principalIndex.TryGetValue(principalType, out var index))
                    return index;

                index = new Dictionary<object, object>();

                if (key.Properties.Count == 1 && rowsByType.TryGetValue(principalType, out var principals))
                {
                    var keyProperty = key.Properties[0].PropertyInfo;

                    foreach (var principal in principals)
                        index[keyProperty.GetValue(principal)] = principal;
                }

                principalIndex[principalType] = index;
                return index;
            }

            foreach (var (dependentType, dependents) in rowsByType)
            {
                foreach (var foreignKey in dependentType.GetForeignKeys())
                {
                    var navigation = foreignKey.PrincipalToDependent;

                    if (navigation?.PropertyInfo == null || foreignKey.Properties.Count != 1)
                        continue;

                    var index = IndexOf(foreignKey.PrincipalEntityType, foreignKey.PrincipalKey);

                    if (index.Count == 0)
                        continue;

                    var fkProperty = foreignKey.Properties[0].PropertyInfo;

                    foreach (var dependent in dependents)
                    {
                        var fkValue = fkProperty.GetValue(dependent);

                        if (fkValue == null || !index.TryGetValue(fkValue, out var principal))
                            continue;

                        if (navigation.IsCollection)
                        {
                            var collection = navigation.PropertyInfo.GetValue(principal);

                            if (collection is IList list)
                                list.Add(dependent);
                            else
                                collection.GetType().GetMethod("Add").Invoke(collection, new[] { dependent });
                        }
                        else
                        {
                            navigation.PropertyInfo.SetValue(principal, dependent);
                        }
                    }
                }
            }
        }

        private static List<object> ReadTable(string path, IEntityType entityType)
        {
            var rows = new List<object>();

            var lines = File.ReadAllLines(path, new UTF8Encoding(false));

            if (lines.Length == 0 || (lines.Length == 1 && lines[0].Length == 0))
                return rows;

            var storeObject = StoreObjectIdentifier.Table(entityType.GetTableName(), entityType.GetSchema());

            var columns = new Dictionary<string, IProperty>(StringComparer.Ordinal);

            foreach (var property in entityType.GetProperties())
            {
                var column = property.GetColumnName(storeObject);

                if (column != null)
                    columns[column] = property;
            }

            var header = lines[0].Split('\t');
            var mapped = new IProperty[header.Length];

            for (var i = 0; i < header.Length; i++)
            {
                if (!columns.TryGetValue(header[i], out mapped[i]))
                    throw new InvalidDataException($"{path}: column '{header[i]}' is not mapped by the {entityType.ClrType.Name} entity.");
            }

            for (var lineNumber = 1; lineNumber < lines.Length; lineNumber++)
            {
                var line = lines[lineNumber];

                // a trailing newline at end of file is not a row
                if (line.Length == 0 && lineNumber == lines.Length - 1)
                    continue;

                var cells = line.Split('\t');

                if (cells.Length != header.Length)
                    throw new InvalidDataException($"{path}:{lineNumber + 1}: expected {header.Length} fields, found {cells.Length}.");

                var row = Activator.CreateInstance(entityType.ClrType);

                for (var i = 0; i < cells.Length; i++)
                {
                    var property = mapped[i];

                    object value;

                    try
                    {
                        value = ParseCell(cells[i], property.ClrType);
                    }
                    catch (Exception ex) when (ex is FormatException || ex is OverflowException || ex is InvalidDataException)
                    {
                        throw new InvalidDataException($"{path}:{lineNumber + 1}: column '{header[i]}' value '{cells[i]}' does not parse as {property.ClrType.Name}: {ex.Message}", ex);
                    }

                    property.PropertyInfo.SetValue(row, value);
                }

                rows.Add(row);
            }

            return rows;
        }

        /// <summary>
        /// Parses one batch-format cell into the CLR type of the mapped property. Internal so the test assembly can
        /// pin the format rules.
        /// </summary>
        internal static object ParseCell(string cell, Type clrType)
        {
            var underlying = Nullable.GetUnderlyingType(clrType);
            var isNullable = underlying != null || !clrType.IsValueType;
            var target = underlying ?? clrType;

            if (cell == NullToken)
            {
                if (!isNullable)
                    throw new InvalidDataException("NULL in a non-nullable column");

                return null;
            }

            if (target == typeof(string))
                return Unescape(cell);

            if (target == typeof(byte[]))
                return Convert.FromHexString(cell);

            if (target == typeof(bool))
            {
                if (cell == "0") return false;
                if (cell == "1") return true;

                return bool.Parse(cell);
            }

            if (target == typeof(float))
                return float.Parse(cell, NumberStyles.Float, CultureInfo.InvariantCulture);

            if (target == typeof(double))
                return double.Parse(cell, NumberStyles.Float, CultureInfo.InvariantCulture);

            if (target == typeof(decimal))
                return decimal.Parse(cell, NumberStyles.Float, CultureInfo.InvariantCulture);

            if (target == typeof(DateTime))
                return DateTime.SpecifyKind(DateTime.Parse(cell, CultureInfo.InvariantCulture, DateTimeStyles.None), DateTimeKind.Unspecified);

            if (target.IsPrimitive)
            {
                // integral columns: parse wide, then convert with an overflow check - never round a fraction away
                var wide = decimal.Parse(cell, NumberStyles.Integer, CultureInfo.InvariantCulture);

                return Convert.ChangeType(wide, target, CultureInfo.InvariantCulture);
            }

            throw new InvalidDataException($"no parser for {target.Name}");
        }

        /// <summary>Reverses the batch client's escaping of backslash, tab, newline, carriage return and NUL.</summary>
        internal static string Unescape(string cell)
        {
            if (cell.IndexOf('\\') < 0)
                return cell;

            var sb = new StringBuilder(cell.Length);

            for (var i = 0; i < cell.Length; i++)
            {
                var c = cell[i];

                if (c != '\\' || i == cell.Length - 1)
                {
                    sb.Append(c);
                    continue;
                }

                var next = cell[++i];

                switch (next)
                {
                    case 't': sb.Append('\t'); break;
                    case 'n': sb.Append('\n'); break;
                    case 'r': sb.Append('\r'); break;
                    case '0': sb.Append((char)0); break;
                    case '\\': sb.Append('\\'); break;
                    default: sb.Append('\\').Append(next); break;
                }
            }

            return sb.ToString();
        }
    }
}
